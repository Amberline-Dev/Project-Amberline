using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Host;
using Amberline.Network.Sessions;
using Amberline.Server;
using Amberline.Simulation;

namespace Amberline.Tests;

public static class CheatCommandChecks
{
    [Check("server.cheat-command-list")]
    public static void CheatsCommandListsEverySupportedSlashCommandPrivately()
    {
        using var runtime = NewRuntime();
        var expected = "Cheats: /cheats /gold [n] /level [n] /cooldowns [on|off] /god [on|off] /mana [on|off] /revive /win /speed [n] /dummy [team]";
        TestAssert.True(Encoding.ASCII.GetByteCount(expected) <= ChatPackets.MaxTextBytes);
        TestAssert.Equal(expected, ReadChatText(SendChat(runtime, "/cheats").Plaintext.Span));
        TestAssert.Equal("Use /cheats without arguments.",
            ReadChatText(SendChat(runtime, "/cheats extra").Plaintext.Span));
        TestAssert.Equal("Unknown command.",
            ReadChatText(SendChat(runtime, "/not-a-cheat").Plaintext.Span));
    }

    [Check("server.cheat-revive-command")]
    public static void ReviveCommandUsesTheNormalRespawnLifecycle()
    {
        var runtime = NewRuntime();
        var hero = runtime.Session.Participant(1)!.Hero!;
        var position = hero.Position;
        var health = hero.Health;

        var livingReply = SendChat(runtime, "/revive");
        TestAssert.Equal("Already alive.", ReadChatText(livingReply.Plaintext.Span),
            "reviving a living hero is a safe no-op");
        TestAssert.Equal(position, hero.Position, "living /revive does not move the hero");
        TestAssert.Equal(health, hero.Health, "living /revive does not change health");

        TestAssert.True(runtime.Combat.Kill(hero), "the test hero dies through CombatService");
        runtime.Combat.ScheduleRespawn(hero);
        var beforeDeaths = hero.ChampionDeaths;
        var beforeKills = hero.ChampionKills;
        var revivedReply = SendChat(runtime, "/revive");
        TestAssert.Equal("Revived.", ReadChatText(revivedReply.Plaintext.Span),
            "a dead hero receives the revive response");
        TestAssert.True(hero.Alive, "/revive restores the dead hero");
        TestAssert.Equal(0.0, hero.RespawnAt, "/revive clears the respawn timer");
        TestAssert.Equal(hero.MaxHealth, hero.Health, "/revive restores full health");
        TestAssert.Equal(beforeDeaths, hero.ChampionDeaths, "/revive does not add a death");
        TestAssert.Equal(beforeKills, hero.ChampionKills, "/revive does not add a kill");
    }

    [Check("server.cheat-host-seat-only")]
    public static void CheatCommandsAndPacketsWorkOnlyForASeatWhoseRosterAllowsThem()
    {
        TestAssert.True(new SessionParticipantDefinition(1, 1, "Solo", "Annie", Team.Order).CanCheat,
            "a roster without the flag keeps every seat able to cheat (tools and developer matches)");
        var roster = new[]
        {
            // A seat played on this PC may cheat; a seat that came through a remote tunnel may not (its roster says CanCheat false).
            new SessionParticipantDefinition(1, 1, "Local", "Annie", Team.Order, CanCheat: true),
            new SessionParticipantDefinition(2, 2, "Tunnel", "Ashe", Team.Chaos, CanCheat: false),
        };
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default, solo: false,
            readySettleSeconds: 0.0, roster: roster);
        runtime.World.Clock.SetVirtual(0.0);
        foreach (var seat in new long[] { 1, 2 })
        {
            TestAssert.True(runtime.Session.Authenticate(seat, runtime.Session.BuildKeyCheck((ulong)seat)) is not null);
            runtime.Session.Spawn(seat);
            TestAssert.True(runtime.Session.MarkReady(seat));
        }
        TestAssert.True(runtime.Session.Start());
        var host = runtime.Session.Participant(1)!.Hero!;
        var guest = runtime.Session.Participant(2)!.Hero!;
        var hostGold = host.Gold;
        var guestGold = guest.Gold;

        // The guest's chat cheats get the answer an unknown command gets, so they reveal nothing about the cheat list.
        TestAssert.Equal("Unknown command.", ReadChatText(SendChatFrom(runtime, 2, "/gold 100").Plaintext.Span));
        TestAssert.Equal("Unknown command.", ReadChatText(SendChatFrom(runtime, 2, "/cheats").Plaintext.Span));
        TestAssert.Equal(guestGold, guest.Gold, "the refused chat cheat changes nothing");
        TestAssert.True(ReadChatText(SendChatFrom(runtime, 1, "/gold 100").Plaintext.Span) != "Unknown command.",
            "the host seat's chat cheat is answered");
        TestAssert.True(host.Gold > hostGold, "the host seat's /gold grants gold");

        // The native cheat packet (0xB6, gold to the sender's own hero) follows the same rule.
        var request = new byte[16];
        request[0] = 0xB6;
        request[5] = 1;
        BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(12, 4), 100f);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8, 4), guest.NetId);
        TestAssert.False(runtime.Session.DispatchCheat(new SessionInput(2, 2, request, request, WasEncrypted: false)),
            "a seat without the flag is refused the cheat packet");
        TestAssert.Equal(guestGold, guest.Gold, "the refused cheat packet changes nothing");
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8, 4), host.NetId);
        TestAssert.True(runtime.Session.DispatchCheat(new SessionInput(1, 1, request, request, WasEncrypted: false)),
            "the host seat's cheat packet is accepted");
    }

    // Owner decision, 25 October 2026: a Host API game created on this PC gives its human seats the cheat commands; a game created
    // from elsewhere gives none. The roster is read from the game directory the manager wrote (the match server reads it at start).
    [Check("server.host-api-cheat-seats-follow-the-host-pc")]
    public static void HostApiCheatSeatsBelongToAGameCreatedOnThisPc() => HostApiCheatSeatsAsync().GetAwaiter().GetResult();

    private static async Task HostApiCheatSeatsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "league-host-api-cheat-" + Guid.NewGuid().ToString("N"));
        var serverExecutable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Amberline.Server", "release", "Amberline.Server.exe"));
        var players = new[] { new MatchPlayer("Human", "Annie", Team.Order), new MatchPlayer("Robot", "Annie", Team.Chaos, IsBot: true) };
        await using var manager = new GameManager(new HostSettings(GameData.Default.Paths.Root, serverExecutable,
            FirstGamePort: 47600 + Random.Shared.Next(400), MaxGames: 2, BindAddress: "127.0.0.1"), directory);
        var local = manager.Create(new HostGuest("air:local", "Local", ""), new CreateGameRequest(Guid.NewGuid(), players), local: true);
        var remote = manager.Create(new HostGuest("air:remote", "Remote", ""), new CreateGameRequest(Guid.NewGuid(), players), local: false);
        var localRoster = ReadRoster(directory, local.Id);
        var remoteRoster = ReadRoster(directory, remote.Id);
        TestAssert.True(localRoster[0].CanCheat, "a game created on this PC gives its human seat the cheat commands");
        TestAssert.False(localRoster[1].CanCheat, "a bot seat never gets the cheat commands");
        TestAssert.False(remoteRoster[0].CanCheat, "a game created from elsewhere gives no seat the cheat commands");
        TestAssert.False(GameManager.MayClaim(localRoster[0], local: false), "a seat that may cheat is claimed only from this PC");
        TestAssert.True(GameManager.MayClaim(remoteRoster[0], local: false), "a remote claim takes a seat without cheats");
        TestAssert.True(GameManager.MayClaim(localRoster[0], local: true), "this PC claims its own seat");
    }

    private static MatchPlayer[] ReadRoster(string directory, Guid gameId) =>
        JsonSerializer.Deserialize<MatchPlayer[]>(File.ReadAllText(Path.Combine(directory, gameId.ToString("N"), "roster.json")),
            MatchRoster.JsonOptions)!;


    private static uint HighestExpectedSync(JsonElement packets)
    {
        uint highest = 0;
        foreach (var row in packets.EnumerateArray())
        {
            var bytes = Convert.FromHexString(row.GetProperty("plaintext").GetString()!);
            if (bytes.Length >= 9 && bytes[0] == 0xCC)
                highest = Math.Max(highest, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5, 4)));
        }
        return highest;
    }

    private static MatchRuntime NewRuntime()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, orderChampion: "Annie", solo: true,
            readySettleSeconds: 0.0);
        runtime.World.Clock.SetVirtual(0.0);
        TestAssert.True(runtime.Session.Authenticate(1,
            runtime.Session.BuildKeyCheck(1)) is not null);
        runtime.Session.Spawn(1);
        TestAssert.True(runtime.Session.MarkReady(1));
        TestAssert.True(runtime.Session.Start());
        return runtime;
    }

    private static PlaintextPacket SendChatFrom(MatchRuntime runtime, long connection, string command)
    {
        var request = ChatPackets.Envelope((uint)connection, false, Encoding.ASCII.GetBytes(command));
        var output = runtime.Session.Receive(connection, ChatPackets.Channel, runtime.Cipher.Encrypt(request.Plaintext.Span));
        TestAssert.Equal(1, output.Count, $"chat command reply for {command}");
        return output[0].Packet;
    }

    private static PlaintextPacket SendChat(MatchRuntime runtime, string command)
    {
        var request = ChatPackets.Envelope(1, false, Encoding.ASCII.GetBytes(command));
        var output = runtime.Session.Receive(1, ChatPackets.Channel,
            runtime.Cipher.Encrypt(request.Plaintext.Span));
        TestAssert.Equal(1, output.Count, $"chat command reply for {command}");
        return output[0].Packet;
    }

    private static void ApplyInputSnapshot(Entity hero, JsonElement snapshot)
    {
        hero.Kind = EntityKindExtensions.FromSourceType(snapshot.GetProperty("etype").GetString());
        hero.UnitType = (UnitType)snapshot.GetProperty("unit_type").GetInt32();
        hero.Team = (Team)snapshot.GetProperty("team").GetInt32();
        hero.Name = snapshot.GetProperty("name").GetString() ?? string.Empty;
        hero.ChampionName = snapshot.GetProperty("champion_name").GetString() ?? "Annie";
        hero.Position = Point(snapshot.GetProperty("pos"));
        hero.Facing = Point(snapshot.GetProperty("facing"));
        hero.Health = snapshot.GetProperty("hp").GetDouble();
        hero.MaxHealth = snapshot.GetProperty("max_hp").GetDouble();
        hero.Mana = snapshot.GetProperty("mana").GetDouble();
        hero.MaxMana = snapshot.GetProperty("max_mana").GetDouble();
        hero.Armor = snapshot.GetProperty("armor").GetDouble();
        hero.SpellBlock = snapshot.GetProperty("spell_block").GetDouble();
        hero.AttackDamage = snapshot.GetProperty("atk_damage").GetDouble();
        hero.AbilityPower = snapshot.GetProperty("ability_power").GetDouble();
        hero.Speed = snapshot.GetProperty("speed").GetDouble();
        hero.Radius = snapshot.GetProperty("radius").GetDouble();
        hero.AttackRange = snapshot.GetProperty("atk_range").GetDouble();
        hero.AttackPeriod = snapshot.GetProperty("atk_period").GetDouble();
        hero.AttackWindup = snapshot.GetProperty("windup").GetDouble();
        var speed = snapshot.GetProperty("attack_speed_mod").GetDouble();
        if (hero.ChampionName == "Annie" && Math.Abs(hero.AttackPeriod - 1.6 / speed) < 1e-7)
        {
            hero.AttackPeriod = Math.Clamp(1.6 * (1 + 0.07999999821186066) / speed, 0.4, 5.0);
            hero.AttackWindup = hero.AttackPeriod * 0.195794753;
        }
        hero.AttackSpeedMod = snapshot.GetProperty("attack_speed_mod").GetDouble();
        hero.MissileSpeed = snapshot.GetProperty("missile_speed").GetDouble();
        hero.AttackCooldown = snapshot.GetProperty("attack_cd").GetDouble();
        hero.Alive = snapshot.GetProperty("alive").GetBoolean();
        hero.Created = snapshot.GetProperty("created").GetBoolean();
        hero.Visible = snapshot.GetProperty("visible").GetBoolean();
        hero.GoldReward = snapshot.GetProperty("gold_reward").GetDouble();
        hero.GlobalGoldReward = snapshot.GetProperty("global_gold_reward").GetDouble();
        hero.ExperienceReward = snapshot.GetProperty("xp_reward").GetDouble();
        hero.AcquisitionRange = snapshot.GetProperty("acquisition_range").GetDouble();
        hero.LifeSteal = snapshot.GetProperty("life_steal").GetDouble();
        hero.SpellVamp = snapshot.GetProperty("spell_vamp").GetDouble();
        hero.CritChance = snapshot.GetProperty("crit_chance").GetDouble();
        hero.CritMultiplier = snapshot.GetProperty("crit_multiplier").GetDouble();
        hero.ArmorPenetrationFlat = snapshot.GetProperty("armor_pen_flat").GetDouble();
        hero.ArmorPenetrationPercent = snapshot.GetProperty("armor_pen_percent").GetDouble();
        hero.MagicPenetrationFlat = snapshot.GetProperty("magic_pen_flat").GetDouble();
        hero.MagicPenetrationPercent = snapshot.GetProperty("magic_pen_percent").GetDouble();
        hero.Dodge = snapshot.GetProperty("dodge").GetDouble();
        hero.Hardness = snapshot.GetProperty("hardness").GetDouble();
        // Batch 149 step 4: `odyn_stored_damage` is a retired field (Entity.OdynStoredDamage is gone; the window
        // keeps what it prevented in the shipped Spells/OdynsVeil.luaobj's own CharVars.StoredDamage). Its archived
        // key stays in the fixture as data and is no longer applied.
        hero.CooldownMod = snapshot.GetProperty("cooldown_mod").GetDouble();
        hero.HealthRegen = snapshot.GetProperty("hp_regen").GetDouble();
        hero.ManaRegen = snapshot.GetProperty("mp_regen").GetDouble();
        hero.OwnerNetId = snapshot.GetProperty("owner_netid").GetUInt32();
        hero.ExpiresAt = snapshot.GetProperty("expires_at").GetDouble();
        hero.TeleportId = snapshot.GetProperty("teleport_id").GetByte();
        hero.Gold = snapshot.GetProperty("gold").GetDouble();
        hero.Experience = snapshot.GetProperty("exp").GetDouble();
        hero.Level = snapshot.GetProperty("level").GetInt32();
        hero.SkillPoints = snapshot.GetProperty("skill_points").GetInt32();
        hero.CooldownsDisabled = snapshot.GetProperty("cooldowns_disabled").GetBoolean();
        hero.GodMode = snapshot.GetProperty("god_mode").GetBoolean();
        CopyNumbers(snapshot.GetProperty("spell_cooldowns"), hero.SpellCooldowns);
        CopyNumbers(snapshot.GetProperty("summoner_cooldowns"), hero.SummonerCooldowns);
        CopyNumbers(snapshot.GetProperty("item_cooldowns"), hero.ItemCooldowns);
        hero.SpellRanks = snapshot.GetProperty("spell_ranks").EnumerateArray()
            .Select(value => value.GetInt32()).ToList();
        hero.Waypoints = snapshot.GetProperty("waypoints").EnumerateArray()
            .Select(Point).ToList();
        hero.Target = snapshot.GetProperty("target").ValueKind == JsonValueKind.Null
            ? null : Point(snapshot.GetProperty("target"));
        hero.AttackTarget = snapshot.GetProperty("attack_target").ValueKind == JsonValueKind.Null
            ? null : snapshot.GetProperty("attack_target").GetUInt32();
        hero.AnchorPath = snapshot.GetProperty("anchor_path").EnumerateArray()
            .Select(Point).ToList();
        hero.LastAnchorTime = snapshot.GetProperty("last_anchor_time").GetDouble();
        hero.ItemState = new HeroItemState();
        hero.Inventory = snapshot.GetProperty("inventory").EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.Null ? null :
                new InventorySlot(value.GetProperty("item_id").GetInt32(),
                    value.GetProperty("count").GetInt32(), value.GetProperty("charges").GetInt32()))
            .ToArray();
        hero.Buffs.Clear();
        foreach (var rawBuff in snapshot.GetProperty("buffs").EnumerateArray())
        {
            hero.Buffs.Add(new Amberline.Core.Buffs.BuffState(
                rawBuff.GetProperty("name").GetString()!,
                slot: rawBuff.GetProperty("slot").GetInt32(),
                type: (Amberline.Core.Buffs.BuffType)rawBuff.GetProperty("type").GetInt32(),
                expiresAt: rawBuff.GetProperty("expires_at").GetDouble(),
                casterNetId: rawBuff.GetProperty("caster").GetUInt32(),
                stacks: rawBuff.TryGetProperty("stacks", out var stacks) ? stacks.GetInt32() : 1,
                maxStack: rawBuff.TryGetProperty("max_stack", out var maxStack) &&
                          maxStack.ValueKind != JsonValueKind.Null ? maxStack.GetInt32() : null));
        }
    }

    private static void CopyNumbers(JsonElement values, List<double> destination)
    {
        destination.Clear();
        foreach (var value in values.EnumerateArray()) destination.Add(value.GetDouble());
    }

    private static void CompareSnapshot(Entity hero, JsonElement expected, JsonElement source)
    {
        if (FixtureRecorder.Enabled)
        {
            object? P(Position2? point) => point is { } p ? new[] { p.X, p.Z } : null;
            CorpusSnapshot.Set(expected, expected.EnumerateObject().ToDictionary(field => field.Name, field => field.Name switch
            {
                "etype" => (object?)hero.EntityType,
                "name" => (object?)(hero.Name),
                "champion_name" => (object?)(hero.ChampionName),
                "hp" => (object?)(hero.Health),
                "max_hp" => (object?)(hero.MaxHealth),
                "mana" => (object?)(hero.Mana),
                "max_mana" => (object?)(hero.MaxMana),
                "armor" => (object?)(hero.Armor),
                "spell_block" => (object?)(hero.SpellBlock),
                "atk_damage" => (object?)(hero.AttackDamage),
                "ability_power" => (object?)(hero.AbilityPower),
                "speed" => (object?)(hero.Speed),
                "radius" => (object?)(hero.Radius),
                "atk_range" => (object?)(hero.AttackRange),
                "atk_period" => (object?)(hero.AttackPeriod),
                "windup" => (object?)(hero.AttackWindup),
                "attack_speed_mod" => (object?)(hero.AttackSpeedMod),
                "missile_speed" => (object?)(hero.MissileSpeed),
                "attack_cd" => (object?)(hero.AttackCooldown),
                "alive" => (object?)(hero.Alive),
                "created" => (object?)(hero.Created),
                "visible" => (object?)(hero.Visible),
                "gold_reward" => (object?)(hero.GoldReward),
                "global_gold_reward" => (object?)(hero.GlobalGoldReward),
                "xp_reward" => (object?)(hero.ExperienceReward),
                "acquisition_range" => (object?)(hero.AcquisitionRange),
                "life_steal" => (object?)(hero.LifeSteal),
                "spell_vamp" => (object?)(hero.SpellVamp),
                "crit_chance" => (object?)(hero.CritChance),
                "crit_multiplier" => (object?)(hero.CritMultiplier),
                "armor_pen_flat" => (object?)(hero.ArmorPenetrationFlat),
                "armor_pen_percent" => (object?)(hero.ArmorPenetrationPercent),
                "magic_pen_flat" => (object?)(hero.MagicPenetrationFlat),
                "magic_pen_percent" => (object?)(hero.MagicPenetrationPercent),
                "dodge" => (object?)(hero.Dodge),
                "hardness" => (object?)(hero.Hardness),
                "odyn_stored_damage" => (object?)0.0, // retired field (batch 149 step 4); the window keeps its own CharVars.StoredDamage now
                "cooldown_mod" => (object?)(hero.CooldownMod),
                "hp_regen" => (object?)(hero.HealthRegen),
                "mp_regen" => (object?)(hero.ManaRegen),
                "owner_netid" => (object?)(hero.OwnerNetId),
                "expires_at" => (object?)(hero.ExpiresAt),
                "teleport_id" => (object?)(hero.TeleportId),
                "gold" => (object?)(hero.Gold),
                "exp" => (object?)(hero.Experience),
                "level" => (object?)(hero.Level),
                "skill_points" => (object?)(hero.SkillPoints),
                "cooldowns_disabled" => (object?)(hero.CooldownsDisabled),
                "god_mode" => (object?)(hero.GodMode),
                "last_anchor_time" => (object?)(hero.LastAnchorTime),
                "pos" => (object?)(P(hero.Position)),
                "facing" => (object?)(P(hero.Facing)),
                "target" => (object?)(P(hero.Target)),
                "attack_target" => (object?)(hero.AttackTarget),
                "waypoints" => (object?)(hero.Waypoints.Select(p => P(p)).ToArray()),
                "anchor_path" => (object?)(hero.AnchorPath.Select(p => P(p)).ToArray()),
                "spell_ranks" => (object?)(hero.SpellRanks),
                "spell_cooldowns" => (object?)(hero.SpellCooldowns),
                "summoner_cooldowns" => (object?)(hero.SummonerCooldowns),
                "item_cooldowns" => (object?)(hero.ItemCooldowns),
                "inventory" => (object?)(hero.Inventory.Select(slot => slot is null ? null : (object)new { item_id = slot.ItemId, count = slot.Count, charges = slot.Charges }).ToArray()),
                "buffs" => (object?)(hero.Buffs.Select(buff => new { name = buff.Name, slot = buff.Slot, type = (int)buff.Type, expires_at = buff.ExpiresAt, caster = buff.CasterNetId, stacks = buff.Stacks, max_stack = buff.MaxStack }).ToArray()),
                "pyro_stacks" => (object?)0,
                "unit_type" => (object?)((int)hero.UnitType),
                "team" => (object?)((int)hero.Team),
                "netid" => (object?)(hero.NetId),
                "respawn_at" => (object?)(hero.RespawnAt),
                "death_time" => (object?)(hero.DeathTime),
                "forced_move_grace" => (object?)(hero.ForcedMoveGrace),
                "pet_follow_since" => null, // retired field (the recorded pet follow delay); always null
                "pending_cast" => (object?)(hero.PendingCast is null ? null : throw new InvalidOperationException("Unexpected pending cheat cast")),
                "pending_summon" => (object?)null,
                "forced_move" => (object?)(hero.ForcedMove is null ? new Dictionary<string, object?>() : throw new InvalidOperationException("Unexpected cheat forced move")),
                "yorick_revival" => (object?)(hero.YorickRevival is null ? null : throw new InvalidOperationException("Unexpected cheat revival")),
                "item_counters" => (object?)(new Dictionary<string, object?>()),
                "blitzcrank_state" => (object?)(hero.ChampionName == "Annie" ? null : throw new InvalidOperationException("Unexpected cheat champion state")),
                "shaco_state" => (object?)(hero.ChampionName == "Annie" ? null : throw new InvalidOperationException("Unexpected cheat champion state")),
                "nidalee_state" => (object?)(hero.ChampionName == "Annie" ? null : throw new InvalidOperationException("Unexpected cheat champion state")),
                "vayne_state" => (object?)(hero.ChampionName == "Annie" ? null : throw new InvalidOperationException("Unexpected cheat champion state")),
                "yorick_state" => (object?)(hero.ChampionName == "Annie" ? null : throw new InvalidOperationException("Unexpected cheat champion state")),
                "orianna_ball" => (object?)(hero.ChampionName == "Annie" ? null : throw new InvalidOperationException("Unexpected cheat champion state")),
                // 19 October 2026: Orianna's passive target left the entity with her hand-written module (her scripts keep it
                // in buffs); the recorded column stays in the layouts as the constant 0 it always held - the same arm
                // SpellReplayRecording.Property carries for the spell corpora.
                "orianna_passive_target" => 0u,
                "janna_state" => (object?)(hero.ChampionName == "Annie" ? null : throw new InvalidOperationException("Unexpected cheat champion state")),
                "ezreal_state" => (object?)(hero.ChampionName == "Annie" ? null : throw new InvalidOperationException("Unexpected cheat champion state")),
                "leesin_state" => (object?)(hero.ChampionName == "Annie" ? null : throw new InvalidOperationException("Unexpected cheat champion state")),
                // 25 October 2026 (batch 193, record path only): Recall left the entity with batch 179 (SummonersRecall holds the
                // channel). Normal mode never compares these two columns (the list below), so they record as absent, as the summoner
                // corpus does (batch 191, SpellReplayRecording.EntitySnapshot).
                "recall_remaining" or "recall_fx_netid" => null,
                _ => throw new InvalidOperationException("Unknown cheat output: " + field.Name),
            }));
            return;
        }
        TestAssert.Equal(expected.GetProperty("netid").GetUInt32(), hero.NetId);
        TestAssert.Equal(expected.GetProperty("etype").GetString(), hero.EntityType);
        TestAssert.Equal(expected.GetProperty("unit_type").GetInt32(), (int)hero.UnitType);
        TestAssert.Equal(expected.GetProperty("team").GetInt32(), (int)hero.Team);
        TestAssert.Equal(expected.GetProperty("name").GetString(), hero.Name);
        TestAssert.Equal(expected.GetProperty("champion_name").GetString(), hero.ChampionName);
        ComparePoint(expected.GetProperty("pos"), hero.Position, "position");
        ComparePoint(expected.GetProperty("facing"), hero.Facing, "facing");
        foreach (var (name, actual) in new (string, double)[]
        {
            ("hp", hero.Health), ("max_hp", hero.MaxHealth), ("mana", hero.Mana),
            ("max_mana", hero.MaxMana), ("armor", hero.Armor),
            ("spell_block", hero.SpellBlock), ("atk_damage", hero.AttackDamage),
            ("ability_power", hero.AbilityPower), ("speed", hero.Speed),
            ("radius", hero.Radius), ("atk_range", hero.AttackRange),
            ("atk_period", hero.AttackPeriod), ("windup", hero.AttackWindup),
            ("attack_speed_mod", hero.AttackSpeedMod), ("missile_speed", hero.MissileSpeed),
            ("attack_cd", hero.AttackCooldown), ("expires_at", hero.ExpiresAt),
            ("gold", hero.Gold), ("exp", hero.Experience), ("respawn_at", hero.RespawnAt),
            ("cooldown_mod", hero.CooldownMod), ("hp_regen", hero.HealthRegen),
            ("mp_regen", hero.ManaRegen), ("gold_reward", hero.GoldReward),
            ("global_gold_reward", hero.GlobalGoldReward), ("xp_reward", hero.ExperienceReward),
            ("acquisition_range", hero.AcquisitionRange), ("life_steal", hero.LifeSteal),
            ("spell_vamp", hero.SpellVamp), ("crit_chance", hero.CritChance),
            ("crit_multiplier", hero.CritMultiplier), ("armor_pen_flat", hero.ArmorPenetrationFlat),
            ("armor_pen_percent", hero.ArmorPenetrationPercent),
            ("magic_pen_flat", hero.MagicPenetrationFlat),
            ("magic_pen_percent", hero.MagicPenetrationPercent), ("dodge", hero.Dodge),
            ("hardness", hero.Hardness),
            // Batch 149 step 4: `odyn_stored_damage` is a retired field (Entity.OdynStoredDamage is gone; the
            // shipped Spells/OdynsVeil.luaobj keeps the store in its own CharVars.StoredDamage), so its archived
            // key is superseded data here and is no longer asserted.
        })
        {
            var expectedValue = expected.GetProperty(name).GetDouble();
            TestAssert.Near(expectedValue, actual, 1e-5,
                $"{name} for {source.GetProperty("payload").GetString()}: " +
                $"expected {expectedValue:R}, actual {actual:R}");
        }
        TestAssert.Equal(expected.GetProperty("teleport_id").GetByte(), hero.TeleportId);
        TestAssert.Equal(expected.GetProperty("owner_netid").GetUInt32(), hero.OwnerNetId);
        TestAssert.Equal(expected.GetProperty("alive").GetBoolean(), hero.Alive);
        TestAssert.Equal(expected.GetProperty("created").GetBoolean(), hero.Created);
        TestAssert.Equal(expected.GetProperty("visible").GetBoolean(), hero.Visible);
        CompareNullablePoint(expected.GetProperty("target"), hero.Target, "target");
        CompareNullableUInt(expected.GetProperty("attack_target"), hero.AttackTarget, "attack target");
        ComparePoints(expected.GetProperty("waypoints"), hero.Waypoints, "waypoints");
        ComparePoints(expected.GetProperty("anchor_path"), hero.AnchorPath, "anchor path");
        TestAssert.Near(expected.GetProperty("last_anchor_time").GetDouble(), hero.LastAnchorTime, 1e-5);
        CompareList(expected.GetProperty("spell_ranks"), hero.SpellRanks.Select(x => (double)x), "spell ranks");
        CompareList(expected.GetProperty("spell_cooldowns"), hero.SpellCooldowns, "spell cooldowns");
        CompareList(expected.GetProperty("summoner_cooldowns"), hero.SummonerCooldowns, "summoner cooldowns");
        CompareList(expected.GetProperty("item_cooldowns"), hero.ItemCooldowns, "item cooldowns");
        CompareInventory(expected.GetProperty("inventory"), hero.Inventory);
        CompareBuffs(expected.GetProperty("buffs"), hero.Buffs);
        TestAssert.Near(expected.GetProperty("gold").GetDouble(), hero.Gold, 1e-5,
            $"gold after {source.GetProperty("operation").GetString()}");
        TestAssert.Near(expected.GetProperty("exp").GetDouble(), hero.Experience, 1e-5,
            "experience after command");
        TestAssert.Equal(expected.GetProperty("level").GetInt32(), hero.Level);
        TestAssert.Equal(expected.GetProperty("skill_points").GetInt32(), hero.SkillPoints);
        TestAssert.Equal(expected.GetProperty("cooldowns_disabled").GetBoolean(), hero.CooldownsDisabled);
        TestAssert.Equal(expected.GetProperty("god_mode").GetBoolean(), hero.GodMode);
    }

    private static void ComparePackets(JsonElement expected, IReadOnlyList<PlaintextPacket> actual,
        LegacyBlowfishCipher cipher, string label)
    {
        if (FixtureRecorder.Enabled)
        {
            CorpusSnapshot.Set(expected, actual.Select(packet => new
            {
                channel = packet.Channel, flags = (byte)packet.Reliability,
                plaintext = Convert.ToHexString(packet.Encrypt && packet.Plaintext.Length >= 8
                    ? cipher.Decrypt(packet.ToWireBytes(cipher)) : packet.Plaintext.ToArray()).ToLowerInvariant(),
                wire = Convert.ToHexString(packet.ToWireBytes(cipher)).ToLowerInvariant(),
            }).ToArray());
            return;
        }
        if (expected.GetArrayLength() != actual.Count)
            throw new InvalidOperationException(
                $"packet count for {label}: expected {expected.GetArrayLength()}, actual {actual.Count} " +
                $"[{string.Join(",", actual.Select(packet => Convert.ToHexString(packet.Plaintext.Span)))}]");
        for (var index = 0; index < actual.Count; index++)
        {
            var row = expected[index];
            var packet = actual[index];
            TestAssert.Equal(row.GetProperty("channel").GetByte(), packet.Channel, $"packet {index} channel");
            TestAssert.Equal((LeaguePacketReliability)row.GetProperty("flags").GetByte(), packet.Reliability,
                $"packet {index} reliability");
            var expectedPlaintext = row.GetProperty("plaintext").GetString()!;
            var actualPlaintext = Convert.ToHexString(packet.Plaintext.Span).ToLowerInvariant();
            var expectedWire = row.GetProperty("wire").GetString()!;
            var actualWire = Convert.ToHexString(packet.ToWireBytes(cipher)).ToLowerInvariant();
            if (!string.Equals(expectedWire, actualWire, StringComparison.OrdinalIgnoreCase))
            {
                var expectedDecoded = Convert.ToHexString(cipher.Decrypt(Convert.FromHexString(expectedWire))).ToLowerInvariant();
                var actualDecoded = Convert.ToHexString(cipher.Decrypt(Convert.FromHexString(actualWire))).ToLowerInvariant();
                throw new InvalidOperationException(
                    $"packet {index} wire for {label}: expected {expectedWire}, actual {actualWire}; " +
                    $"decoded expected {expectedDecoded}, actual {actualDecoded}");
            }
            var capturedPlaintext = packet.Encrypt && expectedWire.Length >= 16
                ? Convert.ToHexString(cipher.Decrypt(Convert.FromHexString(expectedWire))).ToLowerInvariant()
                : expectedWire;
            if (!string.Equals(expectedPlaintext, capturedPlaintext, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"packet {index} fixture plaintext does not match its wire for {label}: " +
                    $"expected {expectedPlaintext}, decrypted {capturedPlaintext}");
            if (packet.Encrypt && expectedPlaintext.Length >= actualPlaintext.Length)
            {
                var prefix = expectedPlaintext[..actualPlaintext.Length];
                var padding = expectedPlaintext[actualPlaintext.Length..];
                if (!string.Equals(prefix, actualPlaintext, StringComparison.OrdinalIgnoreCase) ||
                    padding.Any(character => character != '0'))
                    throw new InvalidOperationException(
                        $"packet {index} plaintext for {label}: expected captured prefix {prefix}, " +
                        $"actual {actualPlaintext}");
            }
            else if (!string.Equals(expectedPlaintext, actualPlaintext,
                         StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"packet {index} plaintext for {label}: expected {expectedPlaintext}, actual {actualPlaintext}");
        }
    }

    private static void CompareInventory(JsonElement expected, IReadOnlyList<InventorySlot?> actual)
    {
        TestAssert.Equal(expected.GetArrayLength(), actual.Count, "inventory count");
        for (var index = 0; index < actual.Count; index++)
        {
            var raw = expected[index];
            if (raw.ValueKind == JsonValueKind.Null)
            {
                TestAssert.True(actual[index] is null, $"inventory slot {index} must be empty");
                continue;
            }
            TestAssert.True(actual[index] is not null, $"inventory slot {index} missing");
            TestAssert.Equal(raw.GetProperty("item_id").GetInt32(), actual[index]!.ItemId);
            TestAssert.Equal(raw.GetProperty("count").GetInt32(), actual[index]!.Count);
            TestAssert.Equal(raw.GetProperty("charges").GetInt32(), actual[index]!.Charges);
        }
    }

    private static void CompareBuffs(JsonElement expected, IList<Amberline.Core.Buffs.BuffState> actual)
    {
        TestAssert.Equal(expected.GetArrayLength(), actual.Count, "buff count");
        for (var index = 0; index < actual.Count; index++)
        {
            var raw = expected[index];
            var buff = actual[index];
            TestAssert.Equal(raw.GetProperty("name").GetString(), buff.Name);
            TestAssert.Equal(raw.GetProperty("slot").GetInt32(), buff.Slot);
            TestAssert.Equal(raw.GetProperty("type").GetInt32(), (int)buff.Type);
            TestAssert.Near(raw.GetProperty("expires_at").GetDouble(), buff.ExpiresAt, 1e-5);
            TestAssert.Equal(raw.GetProperty("caster").GetUInt32(), buff.CasterNetId);
            if (raw.TryGetProperty("stacks", out var stacks))
                TestAssert.Equal(stacks.GetInt32(), buff.Stacks);
            var max = raw.TryGetProperty("max_stack", out var maxStack) &&
                      maxStack.ValueKind != JsonValueKind.Null ? maxStack.GetInt32() : (int?)null;
            TestAssert.Equal(max, buff.MaxStack);
        }
    }

    private static void CompareList(JsonElement expected, IEnumerable<double> actual, string label)
    {
        var values = actual.ToArray();
        TestAssert.Equal(expected.GetArrayLength(), values.Length, label + " length");
        for (var index = 0; index < values.Length; index++)
            TestAssert.Near(expected[index].GetDouble(), values[index], 1e-5, $"{label}[{index}]");
    }

    private static void ComparePoint(JsonElement expected, Position2 actual, string label)
    {
        TestAssert.Near(expected[0].GetDouble(), actual.X, 1e-5, label + " X");
        TestAssert.Near(expected[1].GetDouble(), actual.Z, 1e-5, label + " Z");
    }

    private static void ComparePoints(JsonElement expected, IReadOnlyList<Position2> actual, string label)
    {
        TestAssert.Equal(expected.GetArrayLength(), actual.Count, label + " count");
        for (var index = 0; index < actual.Count; index++) ComparePoint(expected[index], actual[index], $"{label}[{index}]");
    }

    private static void CompareNullablePoint(JsonElement expected, Position2? actual, string label)
    {
        if (expected.ValueKind == JsonValueKind.Null)
        {
            TestAssert.True(actual is null, label + " expected null");
            return;
        }
        TestAssert.True(actual is not null, label + " missing");
        ComparePoint(expected, actual!.Value, label);
    }

    private static void CompareNullableUInt(JsonElement expected, uint? actual, string label)
    {
        if (expected.ValueKind == JsonValueKind.Null)
            TestAssert.True(actual is null or 0, label + " expected null");
        else TestAssert.Equal(expected.GetUInt32(), actual ?? 0, label);
    }

    private static Position2 Point(JsonElement value) => new(value[0].GetDouble(), value[1].GetDouble());

    private sealed class CaptureReplicationTransport : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = new();
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }

    private static string ReadChatText(ReadOnlySpan<byte> payload)
    {
        var bytes = ReadChatTextBytes(payload);
        return Encoding.ASCII.GetString(bytes);
    }

    private static byte[] ReadChatTextBytes(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 13) throw new InvalidDataException("chat envelope is truncated");
        var length = BinaryPrimitives.ReadInt32LittleEndian(payload[8..12]);
        if (length < 0 || payload.Length < 13 + length)
            throw new InvalidDataException("chat envelope text length is invalid");
        return payload.Slice(12, length).ToArray();
    }

    private static string FixturePath(string file) => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", file);
}
