using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Stats;

namespace Amberline.Network.Sessions;

public readonly record struct SessionRosterCounts
{
    public SessionRosterCounts(int order, int chaos)
    {
        if (order < 0 || chaos < 0)
            throw new ArgumentOutOfRangeException(nameof(order), "Roster counts cannot be negative.");
        Order = order;
        Chaos = chaos;
    }

    public int Order { get; }
    public int Chaos { get; }

    public int For(Team team) => team switch
    {
        Team.Order => Order,
        Team.Chaos => Chaos,
        _ => 0,
    };
}

public sealed record SessionParticipantDefinition(
    ulong PlayerId,
    uint ClientId,
    string Name,
    string ChampionName,
    Team Team,
    string SummonerSpell1 = SummonerLoadout.DefaultFirst,
    string SummonerSpell2 = SummonerLoadout.DefaultSecond,
    int SkinId = 0,
    int SummonerLevel = 30,
    RunePage? Runes = null,
    MasteryPage? Masteries = null,
    bool IsBot = false,
    bool CanCheat = true)
{
    private static readonly Lazy<RuneCatalog> DefaultRunes = new(() =>
        new RuneCatalog(GameData.Default.Paths));
    private static readonly Lazy<MasteryCatalog> DefaultMasteries = new(() =>
        new MasteryCatalog(GameData.Default.Paths));

    public void Validate()
    {
        if (PlayerId == 0 || PlayerId == ulong.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(PlayerId));
        if (ClientId > 0xFF)
            throw new ArgumentOutOfRangeException(nameof(ClientId));
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(ChampionName);
        if (Team is not (Team.Order or Team.Chaos))
            throw new ArgumentOutOfRangeException(nameof(Team));
        SummonerLoadout.ValidatePair(SummonerSpell1, SummonerSpell2);
        if (SkinId < 0) throw new ArgumentOutOfRangeException(nameof(SkinId));
        if (SummonerLevel is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(SummonerLevel), "Summoner level must be 1..30.");
        var runes = Runes ?? RunePage.Empty;
        // A bot page is the shipped Bot*Runes list, not bound by the lobby page rules (RunePage.CreateBot). The page itself says so
        // (IsBotList), because the headless match seats its bots as ordinary participants and still holds Shen's 18 marks.
        if (!runes.IsEmpty && !IsBot && !runes.IsBotList)
            RunePage.Create(DefaultRunes.Value, runes.Red, runes.Blue, runes.Yellow, runes.Black,
                SummonerLevel);
        var masteries = Masteries ?? MasteryPage.Empty;
        if (masteries.TotalPoints > 0)
            MasteryPage.Create(DefaultMasteries.Value, masteries.Allocations, SummonerLevel);
    }
}

/// <summary>A human seat the start barrier stopped waiting for; <paramref name="ConnectionId"/> is its peer when attached.</summary>
internal readonly record struct LoadingExpiry(ulong PlayerId, long? ConnectionId, bool Late);

/// <summary>One fixed seat in a duel; connection identity is transport-owned.</summary>
public sealed class SessionParticipant
{
    /// <summary>DIAGNOSTICS: whether this seat may use the playtest cheat commands and the cheat packet (see SessionParticipantDefinition.CanCheat).</summary>
    public bool CanCheat { get; }

    public SessionParticipant(SessionParticipantDefinition definition)
    {
        definition.Validate();
        PlayerId = definition.PlayerId;
        ClientId = definition.ClientId;
        Name = definition.Name;
        ChampionName = definition.ChampionName;
        SkinId = definition.SkinId;
        Team = definition.Team;
        IsBot = definition.IsBot;
        CanCheat = definition.CanCheat;
        var loadout = SummonerLoadout.ValidatePair(definition.SummonerSpell1, definition.SummonerSpell2);
        SummonerSpell1 = loadout.First;
        SummonerSpell2 = loadout.Second;
        SummonerLevel = definition.SummonerLevel;
        Runes = definition.Runes ?? RunePage.Empty;
        Masteries = definition.Masteries ?? MasteryPage.Empty;
    }

    public ulong PlayerId { get; }
    public uint ClientId { get; }
    public string Name { get; }
    public string ChampionName { get; }
    public int SkinId { get; }
    public Team Team { get; }
    /// <summary>Server-owned seat with no ENet peer or client authentication.</summary>
    public bool IsBot { get; }
    public string SummonerSpell1 { get; }
    public string SummonerSpell2 { get; }
    public int SummonerLevel { get; }
    public RunePage Runes { get; }
    public MasteryPage Masteries { get; }
    public long? ConnectionId { get; internal set; }
    public Entity? Hero { get; internal set; }
    /// <summary>Original team-local roster slot, including bots; retained on reconnect.</summary>
    public byte SpawnPositionIndex { get; internal set; }
    public bool Spawned { get; internal set; }
    public bool Ready { get; internal set; }
    /// <summary>The current native process confirmed world/UI initialization.</summary>
    public bool Loaded { get; internal set; }
    public bool ShopOpen { get; internal set; }
    /// <summary>
    /// Armed by the seat's version handshake (its value is that handshake's time). The hero spawn burst follows once the client has sent
    /// its own 0xC6 (<see cref="Loaded"/>); nothing else paces it (the fixed 3 s delay was deleted on 18 October 2026).
    /// </summary>
    public double? SpawnAt { get; internal set; }
    public double? ReadyAt { get; internal set; }
    /// <summary>Per-peer knowledge is reset on each authenticated connection.</summary>
    public ReplicationView View { get; } = new();
}

public sealed record SessionWorldSnapshot(
    IReadOnlyList<SessionParticipant> Participants,
    IReadOnlyList<Entity> Heroes,
    IReadOnlyList<Entity> MapEntities,
    SessionRosterCounts OriginalRosterCounts);

/// <summary>Required match-owned services; a session never invents gameplay state.</summary>
public sealed class SessionDependencies
{
    /// <summary>The map this match is played on (<see cref="GameMaps"/>): it picks the SynchVersion map id and the lane minion skin.</summary>
    public int MapId { get; init; } = GameMaps.Normal;
    /// <summary>
    /// Batch 669: the SynchVersion, hero-stats and result mode; null is the map's own (<see cref="GameMaps.ModeOf"/>). Battle Training names
    /// TUTORIAL on map 1 (<see cref="GameMaps.IsPlayedIn"/>).
    /// </summary>
    public string? Mode { get; init; }

    public SessionDependencies(
        HeroStatsService heroStats,
        ReplicationSyncCounter syncIds,
        ISessionMovementService movement,
        ISessionCombatService combat,
        ISessionEffectService effects,
        ISessionShopService? shop = null,
        ISessionCheatService? cheats = null,
        ISessionChatService? chat = null)
    {
        HeroStats = heroStats ?? throw new ArgumentNullException(nameof(heroStats));
        SyncIds = syncIds ?? throw new ArgumentNullException(nameof(syncIds));
        Movement = movement ?? throw new ArgumentNullException(nameof(movement));
        Combat = combat ?? throw new ArgumentNullException(nameof(combat));
        Effects = effects ?? throw new ArgumentNullException(nameof(effects));
        Shop = shop;
        Cheats = cheats;
        Chat = chat;
    }

    public HeroStatsService HeroStats { get; }
    public ReplicationSyncCounter SyncIds { get; }
    public ISessionMovementService Movement { get; }
    public ISessionCombatService Combat { get; }
    public ISessionEffectService Effects { get; }
    public ISessionShopService? Shop { get; }
    public ISessionCheatService? Cheats { get; }
    public ISessionChatService? Chat { get; }
}

/// <summary>Authoritative hooks used when a match owns movement/combat state.</summary>
public interface ISessionMovementService
{
    IReadOnlyList<Position2> SetMoveOrder(Entity unit, Position2 destination);
    /// <summary>A stop's one-point path, installed as the client installs it on receipt: the unit seated on its anchor.</summary>
    IReadOnlyList<Position2> InstallStop(Entity unit);
}

public interface ISessionCombatService
{
    bool Surrender(Team team);
    void CancelPendingActions(Entity unit);
    /// <summary>A seat's hero dropped out of (false) or returned to (true) a live match; the combat side runs the disconnect hooks.</summary>
    void NotifyHeroConnection(Entity hero, bool connected) { }
    IReadOnlyList<PlaintextPacket> EngageProtection(WorldState world);
}

public interface ISessionEffectService
{
    PlaintextPacket BuildStopAttack(Entity unit);
    PlaintextPacket BuildWaypoints(Entity unit, IReadOnlyList<Position2> points, uint syncId);
}

public interface ISessionShopService
{
    bool Dispatch(SessionParticipant participant, Entity hero, ReadOnlyMemory<byte> plaintext);
}

public interface ISessionCheatService
{
    bool Dispatch(SessionParticipant participant, Entity hero, ReadOnlyMemory<byte> plaintext);
}

public interface ISessionChatService
{
    IReadOnlyList<SessionOutboundPacket> Dispatch(SessionParticipant participant,
        ReadOnlyMemory<byte> plaintext, IReadOnlyList<SessionParticipant> participants,
        bool allowCommands = true);
}

public readonly record struct SessionGameplayContext(
    SessionParticipant Participant,
    Entity Hero,
    byte Channel,
    ReadOnlyMemory<byte> Payload,
    uint? TargetNetId,
    Entity? Target,
    /// <summary>
    /// The actor named by the packet.  It is normally <see cref="Hero"/>,
    /// but is an owned Shaco clone or Yorick revenant for pet orders.
    /// </summary>
    Entity? Actor = null);

/// <summary>A packet produced by the session driver for one authenticated seat.</summary>
public readonly record struct SessionOutboundPacket(long ConnectionId, PlaintextPacket Packet);

/// <summary>Explicit input boundary after native transport framing.</summary>
public readonly record struct SessionInput(
    long ConnectionId,
    byte Channel,
    ReadOnlyMemory<byte> WirePayload,
    ReadOnlyMemory<byte> PlaintextPayload,
    bool WasEncrypted);
