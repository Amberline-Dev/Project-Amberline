using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.BehaviorTrees;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Bots;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Items;
using Amberline.Simulation.Neutral;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Stats;

namespace Amberline.Network.Sessions;

internal sealed class ProductionShopService : ISessionShopService
{
    private readonly ShopService _shop;
    public ProductionShopService(ShopService shop) => _shop = shop;
    public bool Dispatch(SessionParticipant participant, Entity hero, ReadOnlyMemory<byte> plaintext)
    {
        try
        {
            var payload = plaintext.Span;
            if (payload.Length == 0) return false;
            return payload[0] switch
            {
                0x60 => _shop.Open(hero, ItemsPackets.ParseShopOpen(payload)).Accepted,
                0x87 => _shop.Buy(hero, ItemsPackets.ParseBuyItem(payload)).Accepted,
                0x09 => _shop.Sell(hero, ItemsPackets.ParseRemoveItem(payload)).Accepted,
                0x23 => _shop.Swap(hero, ItemsPackets.ParseSwapItem(payload)).Accepted,
                _ => false,
            };
        }
        catch (Exception exception) when (exception is ArgumentException or
                                           InvalidOperationException or OverflowException)
        { return false; }
    }
}

/// <summary>
/// DIAGNOSTICS: the client's native cheat request (0xB6: experience or gold for the sender's own hero) is playtest tooling here.
/// The shipped GamePermanent.cfg [NetConfig] CheatsDisabled is 0 in the client's defaults, but a live retail server ran with
/// cheats off; this server leaves them on for every seat. The notes are in docs/network/duel-implementation.md.
/// </summary>
internal sealed class ProductionCheatService : ISessionCheatService
{
    private const byte AddExperience = 0;
    private const byte AddGold = 1;
    private readonly CombatService _combat;

    public ProductionCheatService(CombatService combat) =>
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));

    public bool Dispatch(SessionParticipant participant, Entity hero,
        ReadOnlyMemory<byte> plaintext)
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentNullException.ThrowIfNull(hero);
        // DIAGNOSTICS: the playtest cheat packet. A seat without CanCheat is refused silently, like any rejected request.
        if (!participant.CanCheat) return false;
        var payload = plaintext.Span;
        if (payload.Length != 16 || payload[0] != 0xB6 || !hero.Alive)
            return false;

        var target = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..12]);
        var amount = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(payload[12..16]));
        if (target != hero.NetId || payload[5] is not (AddExperience or AddGold) ||
            !float.IsFinite(amount) || amount <= 0.0f)
            return false;
        var current = payload[5] == AddGold ? hero.Gold : hero.Experience;
        if (!double.IsFinite(current) || current < 0.0 ||
            amount > float.MaxValue - current)
            return false;
        return _combat.AwardHeroProgress(hero,
            gold: payload[5] == AddGold ? amount : 0.0,
            experience: payload[5] == AddExperience ? amount : 0.0);
    }
}

/// <summary>
/// Relays chat on channel 5 (all and team). DIAGNOSTICS: the slash commands it answers privately (/cheats /gold /level /cooldowns
/// /god /mana /revive /win /speed /dummy, and .mark) are this server's playtest tools; retail has none of them. The stock client's
/// own slash commands (/pause, /resume, /surrender, /ff, /nosurrender, /noff) are sent as packets and never reach chat.
/// </summary>
internal sealed class ProductionChatService : ISessionChatService
{
    private const int MaxTextBytes = ChatPackets.MaxTextBytes;
    private readonly CombatService _combat;
    private readonly ProtocolSpellEffectSink _effects;
    private readonly ProductionSimulationSpeed _speed;
    private readonly WorldState _world;
    private readonly HeroStatsService _stats;
    private readonly NavigationGrid _navigation;
    private readonly GameData _data;
    private readonly PlaytestDiagnostics _playtest;

    public ProductionChatService(CombatService combat, ProtocolSpellEffectSink effects,
        ProductionSimulationSpeed speed, WorldState world, HeroStatsService stats,
        NavigationGrid navigation, GameData data, PlaytestDiagnostics playtest)
    {
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
        _speed = speed ?? throw new ArgumentNullException(nameof(speed));
        _world = world;
        _stats = stats;
        _navigation = navigation;
        _data = data;
        _playtest = playtest;
    }

    public IReadOnlyList<SessionOutboundPacket> Dispatch(SessionParticipant participant,
        ReadOnlyMemory<byte> plaintext, IReadOnlyList<SessionParticipant> participants,
        bool allowCommands = true)
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentNullException.ThrowIfNull(participants);
        if (!TryParse(plaintext.Span, out var teamChat, out var text))
            return Array.Empty<SessionOutboundPacket>();

        // DIAGNOSTICS: Private owner diagnostics; retail has no .mark command.
        // This is independent of cheat permission and remains usable while paused.
        var message = text[0] == (byte)'.' ? System.Text.Encoding.UTF8.GetString(text) : string.Empty;
        if (message.StartsWith(".mark", StringComparison.OrdinalIgnoreCase))
        {
            var hud = participant.Hero is { } hero ? _playtest.Mark(hero, message[5..].TrimStart()) : null;
            var stamp = hud is double seconds ? $"{(int)seconds / 60}:{(int)seconds % 60:00}" : "--:--";
            var reply = System.Text.Encoding.ASCII.GetBytes(participant.Hero is null
                ? "[mark] champion not spawned" : $"[mark {stamp}] noted");
            return participant.ConnectionId is long markerConnection
                ? [new SessionOutboundPacket(markerConnection,
                    ChatPackets.Envelope(participant.ClientId, teamChat, reply))]
                : Array.Empty<SessionOutboundPacket>();
        }

        if (text.Length > 0 && text[0] == (byte)'/')
        {
            if (text.Any(value => value > 0x7F))
                return participant.ConnectionId is long invalidConnectionId
                    ? [new SessionOutboundPacket(invalidConnectionId,
                        ChatPackets.Envelope(participant.ClientId, teamChat,
                            "Invalid command."u8))]
                    : Array.Empty<SessionOutboundPacket>();
            var reply = allowCommands ? HandleCommand(participant, text) : "The match is paused."u8.ToArray();
            return participant.ConnectionId is long connectionId
                ? [new SessionOutboundPacket(connectionId,
                    ChatPackets.Envelope(participant.ClientId, teamChat, reply))]
                : Array.Empty<SessionOutboundPacket>();
        }

        var outputs = new List<SessionOutboundPacket>();
        foreach (var recipient in participants)
        {
            if (recipient.ConnectionId is not long connectionId ||
                (teamChat && recipient.Team != participant.Team))
                continue;
            outputs.Add(new SessionOutboundPacket(connectionId,
                ChatPackets.Envelope(participant.ClientId, teamChat, text)));
        }
        return outputs;
    }

    private byte[] HandleCommand(SessionParticipant participant, byte[] text)
    {
        if (_combat.GameOver) return "The match has ended."u8.ToArray();
        var hero = participant.Hero;
        var commandText = System.Text.Encoding.ASCII.GetString(text);
        var parts = commandText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2)
            return "Invalid command."u8.ToArray();
        // DIAGNOSTICS: every slash command here is a playtest cheat. A seat without CanCheat gets the answer an unknown command gets.
        if (!participant.CanCheat) return "Unknown command."u8.ToArray();
        var command = parts[0].ToLowerInvariant();
        var argument = parts.Length == 2 ? parts[1] : null;
        return command switch
        {
            "/cheats" => argument is null
                ? "Cheats: /cheats /gold [n] /level [n] /cooldowns [on|off] /god [on|off] /mana [on|off] /revive /win /speed [n] /dummy [team]"u8.ToArray()
                : "Use /cheats without arguments."u8.ToArray(),
            "/gold" => Gold(hero, argument),
            "/level" => Level(hero, argument),
            "/cooldowns" => Cooldowns(hero, argument),
            "/god" => God(hero, argument),
            "/mana" => Mana(hero, argument),
            "/revive" => Revive(hero, argument),
            "/win" => Win(hero, argument),
            "/speed" => Speed(argument),
            "/dummy" => Dummy(hero, argument),
            _ => "Unknown command."u8.ToArray(),
        };
    }

    private byte[] Dummy(Entity? hero, string? argument)
    {
        if (hero is null || !hero.Alive) return "A living champion is required."u8.ToArray();
        var team = argument?.ToLowerInvariant() switch
        {
            null => hero.Team == Team.Order ? Team.Chaos : Team.Order,
            "chaos" => Team.Chaos,
            "order" => Team.Order,
            _ => Team.Neutral,
        };
        if (team == Team.Neutral) return "Usage: /dummy [chaos|order]"u8.ToArray();
        if (_world.Allocator.Next(UnitType.Hero) >= NetIdAllocator.RangeFor(UnitType.Hero).Ceiling)
            return "No champion IDs remain for another dummy in this match."u8.ToArray();
        var position = _navigation.ProjectToMovementFloor(hero.Position +
            new Position2(hero.Facing.X * 350, hero.Facing.Z * 350));
        var champion = _data.Champion("Ezreal");
        var dummy = new Entity(_world.Allocate(UnitType.Hero), EntityKind.Hero, UnitType.Hero, team, position)
        {
            Name = "Training Dummy", ChampionName = "Ezreal", Level = 18,
            IsTrainingDummy = true, HeroAutoAcquire = false,
            Radius = champion.PathfindingRadius, SelectionRadius = champion.SelectionRadius,
            CombatRadius = champion.CombatRadius,
            SkillPoints = 0,
        };
        _stats.Refresh(dummy, push: false);
        dummy.Health = dummy.MaxHealth;
        dummy.Mana = dummy.MaxMana;
        _world.Register(dummy);
        return System.Text.Encoding.ASCII.GetBytes($"Level 18 Ezreal dummy spawned for {team}.");
    }

    private byte[] Gold(Entity? hero, string? argument)
    {
        if (hero is null || !hero.Alive) return "Invalid gold amount."u8.ToArray();
        var amount = 10000.0;
        if (argument is not null &&
            (!double.TryParse(argument, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out amount) ||
             !double.IsFinite(amount)))
            return "Invalid gold amount."u8.ToArray();
        if (amount <= 0.0 || !double.IsFinite(hero.Gold) || hero.Gold < 0.0 ||
            amount > float.MaxValue - hero.Gold || !_combat.AwardHeroProgress(hero, gold: amount))
            return "Invalid gold amount."u8.ToArray();
        return "Gold granted."u8.ToArray();
    }

    private byte[] Level(Entity? hero, string? argument)
    {
        if (hero is null || !hero.Alive) return "Cannot level now."u8.ToArray();
        var target = hero.Level + 1;
        if (argument is not null && !int.TryParse(argument,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out target))
            return "Invalid level."u8.ToArray();
        target = Math.Min(18, target);
        if (target < 1 || target <= hero.Level || !double.IsFinite(hero.Experience) ||
            hero.Experience < 0.0)
            return "Invalid level."u8.ToArray();
        var required = ProgressionRules.ExperienceThreshold(target) - hero.Experience;
        if (required <= 0.0 || required > float.MaxValue - hero.Experience ||
            !_combat.AwardHeroProgress(hero, experience: required))
            return "Invalid level."u8.ToArray();
        return "Level granted."u8.ToArray();
    }

    private byte[] Cooldowns(Entity? hero, string? argument)
    {
        if (hero is null) return "Cannot change cooldowns now."u8.ToArray();
        bool enabled;
        if (argument is null) enabled = !hero.CooldownsDisabled;
        else if (argument.Equals("on", StringComparison.OrdinalIgnoreCase)) enabled = true;
        else if (argument.Equals("off", StringComparison.OrdinalIgnoreCase)) enabled = false;
        else return "Use /cooldowns on or off."u8.ToArray();

        hero.CooldownsDisabled = enabled;
        if (enabled)
        {
            for (var index = 0; index < hero.SpellCooldowns.Count; index++)
            {
                hero.SpellCooldowns[index] = 0.0;
                _effects.SendCooldown(hero, index, 0.0);
            }
            for (var index = 0; index < hero.SummonerCooldowns.Count; index++)
            {
                hero.SummonerCooldowns[index] = 0.0;
                _effects.SendCooldown(hero, index, 0.0, summoner: true);
            }
            for (var index = 0; index < hero.ItemCooldowns.Count; index++)
            {
                hero.ItemCooldowns[index] = 0.0;
                _effects.SendCooldown(hero, index + 4, 0.0);
            }
        }
        return enabled ? "Cooldowns disabled."u8.ToArray() : "Cooldowns enabled."u8.ToArray();
    }

    private byte[] Mana(Entity? hero, string? argument)
    {
        if (hero is null) return "Cannot change resources now."u8.ToArray();
        bool enabled;
        if (argument is null) enabled = !hero.UnlimitedResource;
        else if (argument.Equals("on", StringComparison.OrdinalIgnoreCase)) enabled = true;
        else if (argument.Equals("off", StringComparison.OrdinalIgnoreCase)) enabled = false;
        else return "Use /mana on or off."u8.ToArray();
        hero.UnlimitedResource = enabled;
        if (enabled) hero.Mana = hero.MaxMana;
        _effects.SendStats(hero);
        return enabled ? "Unlimited mana/energy enabled."u8.ToArray() : "Unlimited mana/energy disabled."u8.ToArray();
    }

    private byte[] Win(Entity? hero, string? argument)
    {
        if (argument is not null) return "Use /win without arguments."u8.ToArray();
        if (hero is null || hero.Team is not (Team.Order or Team.Chaos) || _world is null)
            return "Cannot finish this match."u8.ToArray();
        var enemyTeam = hero.Team == Team.Order ? Team.Chaos : Team.Order;
        var nexus = _world.Entities.Values.FirstOrDefault(unit =>
            unit.Kind == EntityKind.Nexus && unit.Team == enemyTeam && unit.Alive);
        return nexus is not null && _combat.Kill(nexus, hero.NetId)
            ? "Victory triggered."u8.ToArray() : "Cannot finish this match."u8.ToArray();
    }

    private byte[] Revive(Entity? hero, string? argument)
    {
        if (argument is not null) return "Use /revive without arguments."u8.ToArray();
        if (hero is null) return "Cannot revive now."u8.ToArray();
        if (hero.Alive) return "Already alive."u8.ToArray();
        return _combat.RespawnImmediately(hero)
            ? "Revived."u8.ToArray()
            : "Cannot revive now."u8.ToArray();
    }

    private static byte[] God(Entity? hero, string? argument)
    {
        if (hero is null) return "Cannot change god mode now."u8.ToArray();
        if (argument is null) hero.GodMode = !hero.GodMode;
        else if (argument.Equals("on", StringComparison.OrdinalIgnoreCase)) hero.GodMode = true;
        else if (argument.Equals("off", StringComparison.OrdinalIgnoreCase)) hero.GodMode = false;
        else return "Use /god on or off."u8.ToArray();
        return System.Text.Encoding.ASCII.GetBytes(hero.GodMode ?
            "God mode enabled." : "God mode disabled.");
    }

    private byte[] Speed(string? argument)
    {
        if (argument is null)
            return System.Text.Encoding.ASCII.GetBytes($"Speed is {_speed.Multiplier:g}x.");
        if (!double.TryParse(argument, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) ||
            !_speed.TrySet(value))
            return "Speed must be a number from 0.1 to 10."u8.ToArray();
        _speed.ClockResyncPending = true;
        return System.Text.Encoding.ASCII.GetBytes($"Speed set to {value:g}x.");
    }

    private static bool TryParse(ReadOnlySpan<byte> payload, out bool teamChat,
        out byte[] text)
    {
        teamChat = false;
        text = Array.Empty<byte>();
        if (payload.Length < 13) return false;
        var team = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..8]);
        var length = BinaryPrimitives.ReadInt32LittleEndian(payload[8..12]);
        var messageBytes = 13 + length;
        if (team is not (0u or 1u) || length is < 1 or > MaxTextBytes ||
            payload.Length < messageBytes ||
            payload[12 + length] != 0 ||
            payload[messageBytes..].IndexOfAnyExcept((byte)0) >= 0)
            return false;
        var value = payload.Slice(12, length);
        if (value.IndexOf((byte)0) >= 0) return false;
        teamChat = team != 0;
        text = value.ToArray();
        return true;
    }

}
