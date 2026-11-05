using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;

namespace Amberline.Network.Sessions;

/// <summary>
/// Fixed gameplay seats over an authenticated native connection. A peer
/// identity is the ENet connection generation, never an address or player ID.
/// </summary>
public sealed partial class DuelSession
{
    public const int SeatCount = 2;
    // Reconstruction: the retail server's inactivity rules are in neither the client nor the shipped data; only the
    // notices it could send are (the localized chat line, the AFK warning box 0xC0 and ENet disconnect data 6).
    // Conservative emulator policy: five combat-clock minutes of inactivity
    // for a connected, ready human seat. Pause time and disconnected seats do
    // not advance this timer; surrender keeps its configured roster denominator.
    // Owner decision, 25 Sept 2026 (playtest #1): retail-like notices at 3 and
    // 4 minutes, then the threshold also disconnects the seat (ENet data 6).
    private const double AfkThresholdSeconds = 5 * 60;
    private const double AfkChatWarningSeconds = 3 * 60;
    private const double AfkWarningBoxSeconds = 4 * 60;
    // Reconstruction: wall seconds after a reconnect's clock sync before the turret protection state is
    // resent. The client's map Lua re-protects the lane turrets on the first frame after the sync; any
    // later time wins, and the cost of waiting is only how long the returning client sees them protected.
    private const double ReconnectProtectionDelaySeconds = 5.0;

    private readonly SessionParticipant[] _participants;
    private readonly SessionSpawnService _spawn;
    private readonly LegacyBlowfishCipher _cipher;
    private readonly int _mapId;
    /// <summary>The SynchVersion and result mode (<see cref="SessionDependencies.Mode"/>, batch 669).</summary>
    private readonly string _mode;
    private readonly ISessionMovementService _movement;
    private readonly ISessionCombatService _combat;
    private readonly ISessionEffectService _effects;
    private readonly ISessionShopService? _shop;
    private readonly ISessionCheatService? _cheats;
    private readonly ISessionChatService? _chat;
    private readonly Func<double> _combatClock;
    private readonly Func<bool> _combatGameOver;
    private readonly Func<bool> _combatEndGameSent;
    // EndOfGame.luaobj's CloseShop: the shop is shut for the rest of the match once the ceremony
    // asks for it, independently of the match being over.
    private readonly Func<bool> _combatShopClosed;
    // DIAGNOSTICS: the two single-seat diagnostic modes of the Python server (--visibility-baseline / DEV_VISIBILITY_BASELINE and
    // --minion-fidelity): a probe minion without waves, or a protected hero that walks a fixed route at the start. Retail has neither;
    // the launcher never turns them on (LauncherServer sets DEV_VISIBILITY_BASELINE=0).
    private readonly bool _visibilityBaseline;
    private readonly bool _minionFidelity;
    private readonly bool _solo;
    // Map1's normal navigation metadata is supplied by the production
    // composition when available.  The bounded fallback keeps direct session
    // fixtures safe without accepting arbitrary world coordinates.
    private readonly NavigationBounds _mapBounds;
    private readonly PingRateLimiter _pingRate;
    private readonly HashSet<ulong> _reconnectPending = new();
    private readonly HashSet<ulong> _reconnectedPlayers = new();
    private readonly HashSet<ulong> _reconnectMapReadySent = new();
    private readonly HashSet<ulong> _mapReadySent = new();
    private readonly HashSet<ulong> _afkCancelPending = new();
    private readonly List<long> _afkKicks = new();
    // Seats the start barrier no longer waits for after the loading limit (ExpireLoadingTimeout).
    private readonly HashSet<ulong> _loadingTimedOut = new();
    private readonly List<LoadingExpiry> _loadingExpiries = new();
    private double? _firstHumanConnectionAt;
    private readonly double _readySettleSeconds;
    private double? _clockAt;
    private double? _protectAt;
    // Reconstruction: per-seat structure-protection resend after a reconnect (see BuildReconnectReadyPackets); retail's reconnect resend is not recovered.
    private readonly Dictionary<ulong, double> _reconnectProtectAt = new();
    // Seats whose channel-zero registry entry was dropped on the other clients (DuelSession.Presence.cs),
    // with the player ids of the clients that received the removal.
    private readonly Dictionary<ulong, HashSet<ulong>> _registryDroppedBy = new();
    private double _startClock;
    private double? _hudOrigin;
    private readonly List<(byte EventId, double HudTime)> _announcements = new();
    private readonly Dictionary<ulong, PlaintextPacket> _terminalClassicStats = new();
    private bool _nexusIdleParticlesSent;
    private bool _terminalStatsPending;
    private double? _terminalClock;

    /// <summary>Production delivery hook for owner-only terminal snapshots.</summary>
    internal Action<ulong, PlaintextPacket>? PrivatePacketSink { get; set; }

    internal Func<Entity, int, bool>? ChampionCastTargetsUnit { get; set; }
    internal Func<Entity, bool, int, bool>? OtherCastTargetsUnit { get; set; }

    /// <summary>
    /// DIAGNOSTICS: tool-only. A headless match drives every connected seat from the server's bot
    /// scripts, so no seat has a client that can be idle; retail AFK latching and its notices
    /// would only add false warnings and a false <c>WasAfk</c> to the result.
    /// </summary>
    internal bool AfkTrackingDisabled { get; set; }

    /// <summary>
    /// Reconstruction: how long the start barrier waits for the human seats, counted from the first human
    /// connection, before the match starts without every seat that has not finished loading. The 126 string
    /// table carries <c>game_announcement_OnLoadingTimeout</c> ("was disconnected due to loading time out!") and
    /// <c>game_messagebox_text_loadtimeout</c> ("Your loading time has exceeded the server timeout limit"), so the
    /// retail server had such a limit and dropped the slow player; no value is shipped (Game.cfg, the Lua and the
    /// AIR sources hold none) and the executable never reads either key, so the limit is the owner's pick
    /// (18 October 2026). The host's 30-minute abort of a game that never starts stays the outer bound.
    /// </summary>
    public const double DefaultLoadingTimeoutSeconds = 180.0;

    internal double LoadingTimeoutSeconds { get; set; } = DefaultLoadingTimeoutSeconds;

    /// <summary>Fresh team sight for targeted input between replication flushes.</summary>
    internal Func<Entity, Entity, bool>? CurrentTargetVisible { get; set; }

    public DuelSession(
        SessionDependencies dependencies,
        WorldState? world = null,
        GameData? gameData = null,
        string orderChampion = "Annie",
        string chaosChampion = "Orianna",
        SessionRosterCounts? originalRosterCounts = null,
        LegacyBlowfishCipher? cipher = null,
        double readySettleSeconds = 2.0,
        bool solo = false,
        bool visibilityBaseline = false,
        bool minionFidelity = false,
        Func<double>? combatClock = null,
        Func<bool>? combatGameOver = null,
        IReadOnlyList<SessionParticipantDefinition>? roster = null,
        ulong soloPlayerId = 1,
        NavigationBounds? mapBounds = null,
        PingRateLimiter? pingRateLimiter = null,
        Func<bool>? combatEndGameSent = null,
        Func<bool>? combatShopClosed = null)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        World = world ?? new WorldState();
        GameData = gameData ?? GameData.Default;
        _cipher = cipher ?? LegacyBlowfish.CreateDefault();
        if (visibilityBaseline && minionFidelity)
            throw new ArgumentException("Visibility baseline and minion fidelity are mutually exclusive.");
        if (!solo && (visibilityBaseline || minionFidelity))
            throw new ArgumentException("Visibility modes require a solo session.");
        _visibilityBaseline = visibilityBaseline;
        _minionFidelity = minionFidelity;
        _solo = solo;
        _mapBounds = mapBounds ?? new NavigationBounds(-1024.0, 16384.0, -1024.0, 16384.0);
        if (!double.IsFinite(_mapBounds.MinX) || !double.IsFinite(_mapBounds.MaxX) ||
            !double.IsFinite(_mapBounds.MinZ) || !double.IsFinite(_mapBounds.MaxZ) ||
            _mapBounds.MinX > _mapBounds.MaxX || _mapBounds.MinZ > _mapBounds.MaxZ)
            throw new ArgumentOutOfRangeException(nameof(mapBounds), "Map bounds must be finite and ordered.");
        _pingRate = pingRateLimiter ?? new PingRateLimiter();
        _mode = dependencies.Mode ?? GameMaps.ModeOf(dependencies.MapId);
        // Batch 674: the hero-stats file is the client's mode's (GameMaps.ClientModeOf: the Basic Tutorial's client runs CLASSIC).
        _spawn = new SessionSpawnService(World, GameData, dependencies.HeroStats, dependencies.SyncIds,
            visibilityBaseline, minionFidelity, GameMaps.ClientModeOf(dependencies.MapId, _mode));
        _mapId = dependencies.MapId;
        _movement = dependencies.Movement;
        _combat = dependencies.Combat;
        _effects = dependencies.Effects;
        _shop = dependencies.Shop;
        _cheats = dependencies.Cheats;
        _chat = dependencies.Chat;
        _combatClock = combatClock ?? (() => World.Clock.Now());
        _combatGameOver = combatGameOver ?? (() => false);
        _combatEndGameSent = combatEndGameSent ?? (() => false);
        _combatShopClosed = combatShopClosed ?? (() => false);
        if (!double.IsFinite(readySettleSeconds) || readySettleSeconds < 0.0)
            throw new ArgumentOutOfRangeException(nameof(readySettleSeconds));
        _readySettleSeconds = readySettleSeconds;
        if (roster is not null)
        {
            var definitions = roster.ToArray();
            if (definitions.Length is < 1 or > 10 || solo && definitions.Length != 1)
                throw new ArgumentException("A roster needs 1–10 seats; solo needs exactly one.", nameof(roster));
            if (!definitions.Any(definition => !definition.IsBot))
                throw new ArgumentException("A production roster needs at least one human player.", nameof(roster));
            if (definitions.Select(definition => definition.PlayerId).Distinct().Count() != definitions.Length ||
                definitions.Select(definition => definition.ClientId).Distinct().Count() != definitions.Length)
                throw new ArgumentException("Roster player and client IDs must be unique.", nameof(roster));
            _participants = definitions.Select(definition => new SessionParticipant(definition)).ToArray();
        }
        else
        {
            if (soloPlayerId == 0 || soloPlayerId == ulong.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(soloPlayerId));
            _participants = solo
                ? [new SessionParticipant(new(soloPlayerId, 0, "Player1", orderChampion, Team.Order))]
                :
                [
                    new SessionParticipant(new(1, 0, "Player1", orderChampion, Team.Order)),
                    new SessionParticipant(new(2, 1, "Player2", chaosChampion, Team.Chaos)),
                ];
        }
        OriginalRosterCounts = originalRosterCounts ?? new SessionRosterCounts(
            _participants.Count(participant => participant.Team == Team.Order),
            _participants.Count(participant => participant.Team == Team.Chaos));
    }

    public WorldState World { get; }
    public GameData GameData { get; }
    public SessionRosterCounts OriginalRosterCounts { get; }
    public IReadOnlyList<SessionParticipant> Participants => _participants;
    public bool WorldCreated { get; private set; }
    public bool Started { get; private set; }
    public MatchResult? Result { get; private set; }

    internal MatchResult? RecordResult(Team winner, bool surrendered)
    {
        if (!Started || Result is not null) return null;
        var now = _combatClock();
        // The combat end callback can run before the next network scheduler
        // pass. Latch a connected human's terminal inactivity in the result
        // snapshot without changing the post-game transport state.
        UpdateConnectedAfk(allowGameOver: true);
        var result = MatchResult.Capture(_participants, winner,
            Math.Max(0.0, now - _startClock), surrendered, now, LeaverRules.Load(GameData.Paths), _mapId, World, _mode);
        _terminalClock = _startClock + result.DurationSeconds;
        _terminalClassicStats.Clear();
        foreach (var participant in _participants)
        {
            if (participant.Hero is not { } hero) continue;
            var win = result.Winner == participant.Team.ToString() ? "Win" : "Loss";
            // Freeze every roster owner's row at result time, including seats
            // that are already detached. Reconnect and terminal delivery must
            // not recalculate disconnect/activity fields from later clocks.
            _terminalClassicStats[participant.PlayerId] =
                _spawn.BuildClassicHeroStats(hero, _terminalClock.Value,
                    participant.PlayerId, win);
        }
        _terminalStatsPending = true;
        if (PrivatePacketSink is not null)
        {
            foreach (var participant in _participants)
            {
                if (participant.ConnectionId is null || !participant.Spawned ||
                    !participant.Loaded || !participant.Ready || participant.Hero is null)
                    continue;
                PrivatePacketSink(participant.PlayerId,
                    _terminalClassicStats[participant.PlayerId]);
            }
            _terminalStatsPending = false;
        }
        return Result = result;
    }
    /// <summary>
    /// Reconstruction: every human seat connected, spawned and ready (the seat's 0xC6, then the spawn burst), each settled for
    /// <c>readySettleSeconds</c> (2 s by default) after its readiness before the fixed map phase and StartGame go out.
    /// The settle time is the Python server's figure; the client gives no reason for it beyond a margin after its
    /// first main-loop pass; the readiness notes are in docs/network/native-world-readiness.md, and bots are ready from world creation.
    /// </summary>
    public bool CanStart => !Started && WorldCreated &&
                            _participants.All(participant => _loadingTimedOut.Contains(participant.PlayerId) ||
                                (participant.IsBot || participant.ConnectionId.HasValue) &&
                                participant.Spawned && participant.Ready &&
                                participant.ReadyAt is double readyAt &&
                                World.Clock.Monotonic() + 1e-9 >= readyAt + _readySettleSeconds);
    public IReadOnlyList<Entity> MapEntities { get; private set; } = Array.Empty<Entity>();
    public double? ClockAt => _clockAt;
    public double? ProtectAt => _protectAt;
    public double? HudOrigin => _hudOrigin;
    /// <summary>Current combat-clock time relative to the sent HUD baseline.</summary>
    public double? HudTime => _hudOrigin is double origin
        ? Math.Max(0.0, _combatClock() - origin) : null;
    // DIAGNOSTICS: Markers need the StartGame origin even before delayed HUD clock sync.
    internal double? PlaytestHudTime => Started ? Math.Max(0.0, _combatClock() - _startClock) : null;
    public IReadOnlyList<(byte EventId, double HudTime)> Announcements => _announcements;
    public bool TurretRebindDone { get; private set; }
    public NavigationBounds MapBounds => _mapBounds;

    // NOT RETAIL: retail sent bots in SynchVersion with IsBot=1, and the client
    // drew their loading cards itself (no summoner spells; name from
    // game_bot_<skin>, which only the 18 retail bot champions have). Bots are
    // listed as ordinary roster players so every bot card shows a proper name,
    // splash and spells; their rows come from HandlePingLoadInfo.
    public IReadOnlyList<LeagueLoadingPlayer> Roster => _participants
        .Select(participant => new LeagueLoadingPlayer(
            participant.PlayerId, participant.Name, participant.ChampionName,
            TeamMapping.ToNative(participant.Team), participant.SummonerSpell1,
            participant.SummonerSpell2, participant.SkinId))
        .ToArray();

    public Action<Entity>? HeroCreated { get; set; }

    /// <summary>
    /// Binds a map unit to its shipped character script as the unit is created (batch 159 step 2); the spawner's own
    /// seam, so <see cref="CreateMapEntities"/> of one world can reach the match's unit module.
    /// </summary>
    public Action<Entity, string>? BindUnitCharacter
    {
        get => _spawn.BindUnitCharacter;
        set => _spawn.BindUnitCharacter = value;
    }

    /// <summary>Batch 460: the ground height the spawn burst's start camera uses (<see cref="SessionSpawnService.GroundHeight"/>).</summary>
    public Func<Position2, double> GroundHeight
    {
        get => _spawn.GroundHeight;
        set => _spawn.GroundHeight = value;
    }

    public SessionWorldSnapshot CreateWorld()
    {
        if (WorldCreated)
            throw new InvalidOperationException("Session world has already been created.");
        var snapshot = _spawn.CreateWorld(_participants, OriginalRosterCounts);
        foreach (var hero in snapshot.Heroes) HeroCreated?.Invoke(hero);
        MapEntities = snapshot.MapEntities;
        // A bot is a server-owned seat. It shares the normal authoritative
        // hero/loadout construction but has no peer to send loading or ready
        // markers, so its barrier state is completed here.
        var botReadyAt = World.Clock.Monotonic();
        foreach (var bot in _participants.Where(participant => participant.IsBot))
        {
            bot.Spawned = true;
            bot.Loaded = true;
            bot.Ready = true;
            bot.ReadyAt = botReadyAt;
        }
        WorldCreated = true;
        return snapshot;
    }

    public SessionParticipant? Authenticate(long connectionId, ReadOnlySpan<byte> keyCheck)
    {
        if (connectionId <= 0)
            return null;
        if (!SessionKeyCheck.TryReadPlayerId(keyCheck, _cipher, out var playerId))
            return null;

        var participant = _participants.FirstOrDefault(item => item.PlayerId == playerId);
        if (participant is null || participant.IsBot)
            return null;
        if (Started)
            return null; // reconnect snapshots are intentionally a separate path
        if (participant.ConnectionId is long existing)
            return existing == connectionId ? participant : null;

        if (_participants.Any(item => item.ConnectionId == connectionId))
            return null;
        participant.ConnectionId = connectionId;
        _firstHumanConnectionAt ??= World.Clock.Monotonic();
        participant.Spawned = false;
        participant.Ready = false;
        participant.Loaded = false;
        participant.ShopOpen = false;
        participant.SpawnAt = null;
        participant.ReadyAt = null;
        participant.View.Reset();
        return participant;
    }

    /// <summary>
    /// Returns whether a cryptographically valid key may replace the currently
    /// attached generation for this seat. The network layer uses this before
    /// detaching the old generation so a later session rejection cannot evict
    /// a valid seat.
    /// </summary>
    internal bool CanReplaceConnection(long connectionId, ulong playerId)
    {
        if (connectionId <= 0 || playerId == 0 || playerId == ulong.MaxValue ||
            _participants.Any(item => item.ConnectionId == connectionId))
            return false;
        var participant = _participants.FirstOrDefault(item => item.PlayerId == playerId);
        if (participant is null || participant.IsBot || participant.ConnectionId is null)
            return false;
        return !Started || participant.Hero is not null;
    }

    /// <summary>
    /// Authenticates a new transport generation for an already-created seat.
    /// This is deliberately separate from the pre-start authentication path:
    /// a started match may only reattach a detached, known hero.
    /// </summary>
    public SessionParticipant? AuthenticateReconnect(long connectionId,
        ReadOnlySpan<byte> keyCheck)
    {
        if (connectionId <= 0 || !Started ||
            !SessionKeyCheck.TryReadPlayerId(keyCheck, _cipher, out var playerId))
            return null;

        var participant = _participants.FirstOrDefault(item => item.PlayerId == playerId);
        if (participant is null || participant.IsBot || participant.Hero is null ||
            participant.ConnectionId is not null ||
            _participants.Any(item => item.ConnectionId == connectionId))
            return null;

        participant.ConnectionId = connectionId;
        // A restarted stock client must consume the normal loading/spawn
        // sequence before it can receive world replication.  The hero stays
        // authoritative, but this transport generation starts gated.
        participant.Spawned = false;
        participant.Ready = false;
        participant.Loaded = false;
        participant.ShopOpen = false;
        participant.SpawnAt = null;
        participant.ReadyAt = null;
        participant.View.Reset();
        _reconnectPending.Add(participant.PlayerId);
        _reconnectedPlayers.Add(participant.PlayerId);
        if (Result is null)
        {
            var reconnectAt = _combatClock();
            participant.Hero.MatchStatistics.RecordReconnect(reconnectAt);
            // Reauthentication is valid human activity for the connected
            // seat. It clears the current AFK state while preserving WasAfk.
            participant.Hero.MatchStatistics.RecordActivity(reconnectAt);
            _combat.NotifyHeroConnection(participant.Hero, connected: true);
        }
        return participant;
    }

    /// <summary>Returns reconnect seats whose observers need a post-flush state burst.</summary>
    internal IReadOnlyList<SessionParticipant> ConsumeReconnects()
    {
        if (_reconnectPending.Count == 0)
            return Array.Empty<SessionParticipant>();
        var pending = _participants
            .Where(participant => _reconnectPending.Contains(participant.PlayerId) &&
                _reconnectMapReadySent.Contains(participant.PlayerId) &&
                participant.Spawned && participant.Loaded && participant.Ready)
            .ToArray();
        foreach (var participant in pending)
            _reconnectPending.Remove(participant.PlayerId);
        return pending;
    }

    internal bool IsReconnectParticipant(ulong playerId) =>
        _reconnectedPlayers.Contains(playerId);

    public SessionParticipant? Participant(long connectionId) =>
        _participants.FirstOrDefault(participant => participant.ConnectionId == connectionId);

    public IReadOnlyList<PlaintextPacket> Spawn(long connectionId)
        => SpawnInternal(connectionId, reconnectBaseline: false);

    private IReadOnlyList<PlaintextPacket> SpawnInternal(long connectionId,
        bool reconnectBaseline)
    {
        var participant = RequireParticipant(connectionId);
        if (participant.Spawned)
            throw new InvalidOperationException("The participant has already spawned.");
        if (!WorldCreated)
            CreateWorld();
        var packets = _spawn.BuildHeroSpawnBurst(participant, _participants,
            revealNonRecipient: !reconnectBaseline,
            // StartGame constructs the local HUD. Its own hero must already
            // have a model/health snapshot, as in the initial loading path.
            revealRecipient: true, includeChampionSpells: !reconnectBaseline,
            includeClassicHeroStats: !reconnectBaseline);
        foreach (var member in _participants)
        {
            if (member.Hero is not { } hero) continue;
            participant.View.Known.Add(hero.NetId);
            if (member == participant && !reconnectBaseline)
                participant.View.Visible.Add(hero.NetId);
            else participant.View.Visible.Remove(hero.NetId);
        }
        participant.Spawned = true;
        return packets;
    }

    private IReadOnlyList<PlaintextPacket> SpawnReconnect(long connectionId) =>
        SpawnInternal(connectionId, reconnectBaseline: true);

    public bool MarkReady(long connectionId)
    {
        var participant = RequireParticipant(connectionId);
        if (!participant.Spawned || participant.Ready)
            return false;
        participant.Ready = true;
        participant.ReadyAt ??= World.Clock.Monotonic();
        return true;
    }

    public IReadOnlyList<PlaintextPacket> BuildMapReadyPackets(long connectionId)
    {
        var participant = RequireParticipant(connectionId);
        if (!CanStart)
            throw new InvalidOperationException("Both fixed seats must be spawned and ready first.");
        if (!_mapReadySent.Add(participant.PlayerId))
            throw new InvalidOperationException("Map-ready packets were already built for this seat.");
        return _spawn.BuildMapReadyBurst(participant, MapEntities);
    }

    public IReadOnlyList<PlaintextPacket> BuildLoadingBurst(long connectionId)
    {
        var participant = RequireParticipant(connectionId);
        return LoadingPackets.LoadingBurst(participant.PlayerId, participant.Name,
            participant.ChampionName, Roster);
    }

    /// <summary>Resolves an actor only when this connection owns that hero.</summary>
    public Entity? ControlledHero(long connectionId, uint actorNetId)
    {
        var participant = Participant(connectionId);
        if (participant is null || !Started || !participant.Spawned)
            return null;
        return participant.Hero is { } hero && hero.NetId == actorNetId ? hero : null;
    }

    /// <summary>Resolves the hero or one of that hero's commandable pets.</summary>
    public Entity? ControlledActor(long connectionId, uint actorNetId)
    {
        var participant = Participant(connectionId);
        if (participant is null || !Started || !participant.Spawned ||
            participant.Hero is not { } hero)
            return null;
        if (hero.NetId == actorNetId) return hero;
        var actor = World.Get(actorNetId);
        return actor is not null && actor.Alive && actor.OwnerNetId == hero.NetId &&
            actor.Kind is EntityKind.Pet or EntityKind.ShacoClone or EntityKind.YorickRevenant or EntityKind.ClonePet
            ? actor : null;
    }

    /// <summary>
    /// Enforces the native gameplay channel, actor ownership, and per-peer
    /// visibility before handing a payload to a typed command dispatcher.
    /// </summary>
    public bool DispatchGameplay(
        long connectionId,
        byte channel,
        ReadOnlySpan<byte> payload,
        Func<SessionGameplayContext, bool> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        LastGameplayAccepted = false;
        if (channel != ClientGameplayChannel || payload.Length == 0)
            return false;
        var participant = Participant(connectionId);
        var isStatsRefresh = payload.Length == 5 && payload[0] == 0x59;
        if (participant is null || !Started || _combatGameOver() ||
            !participant.Spawned || !participant.Ready || participant.Hero is not { } hero ||
            (!isStatsRefresh && Paused) ||
            (isStatsRefresh && !participant.Loaded))
            return false;

        var actorNetId = payload.Length >= 5
            ? BinaryPrimitives.ReadUInt32LittleEndian(payload[1..5])
            : 0u;
        var actor = ControlledActor(connectionId, actorNetId);
        if (actor is null || (payload[0] != 0x75 && actor.NetId != hero.NetId))
            return false;
        // The client mirrors ordinary hero orders onto its owned pet with
        // the same order value. Explicit pet control has distinct hard
        // order values (5/6/9/11). Pet.lua OnOrder acts on a mirrored hold,
        // move, attack or attack-move (1/2/3/7); its stop (10) is a no-op.
        if (payload[0] == 0x75 && actor.NetId != hero.NetId &&
            (payload.Length < 22 || payload[5] is not (1 or 2 or 3 or 5 or 6 or 7 or 9 or 11)))
            return false;

        uint? targetId = null;
        if (payload[0] == 0x75 && payload.Length >= 22)
            targetId = BinaryPrimitives.ReadUInt32LittleEndian(payload[18..22]);
        else if (payload[0] == 0xA2 && payload.Length >= 34)
            targetId = BinaryPrimitives.ReadUInt32LittleEndian(payload[30..34]);
        // Batch 470: UseObject (0x3F, 9 bytes: the actor, then the used unit; the client's use-object routine).
        else if (payload[0] == UseObjectOpcode && payload.Length >= 9)
            targetId = BinaryPrimitives.ReadUInt32LittleEndian(payload[5..9]);

        Entity? target = null;
        if (targetId is uint selected && selected != 0)
        {
            target = World.Get(selected);
            // Self/ground casts can carry an incidental stale mouse-over ID.
            // The owning spell/item services decide whether this cast uses
            // that ID; attacks and true unit-target casts still require sight.
            var incidentalCastTarget = payload[0] == 0xA2 && payload.Length >= 34 &&
                ((payload[5] & 1) == 0 && (payload[5] >> 1) < 4
                    ? ChampionCastTargetsUnit?.Invoke(hero, payload[5] >> 1) == false
                    : OtherCastTargetsUnit?.Invoke(hero, (payload[5] & 1) != 0,
                        payload[5] >> 1) == false);
            if (target is not null && WorldState.IsEnemy(hero, target) &&
                !incidentalCastTarget &&
                (!participant.View.Visible.Contains(selected) ||
                 CurrentTargetVisible?.Invoke(hero, target) == false))
                return false;
        }

        // The stock client periodically asks the server to republish the
        // authoritative hero record (StatsUpdateReq, 0x59).  It is a
        // five-byte actor-only request and follows the same ownership gate as
        // the other hero commands.
        if (payload[0] is not (0x59 or 0x75 or 0x3E or 0xA2 or UseObjectOpcode or EmotePackets.ClientOpcode))
            return false;

        LastGameplayAccepted = handler(new SessionGameplayContext(
            participant, hero, channel, payload.ToArray(), targetId, target, actor));
        if (LastGameplayAccepted)
            MarkActivity(participant);
        return LastGameplayAccepted;
    }

    /// <summary>Result of the most recent gameplay dispatch on this session.</summary>
    public bool LastGameplayAccepted { get; private set; }

    /// <summary>
    /// Batch 563: the Basic Tutorial's client acknowledgements, OnTutorialPopupClosed (0xD5) and TutorialAudioEventFinished
    /// (0x05) with their payload. Both carry a zero net id, so they bypass the hero-command gate; set on map 3 only.
    /// </summary>
    public Action<byte[]>? TutorialAcknowledgement { get; set; }

    public bool DispatchGameplay(
        SessionInput input,
        Func<SessionGameplayContext, bool> handler)
    {
        if (input.Channel != ClientGameplayChannel || input.PlaintextPayload.Length == 0)
            return false;
        return DispatchGameplay(input.ConnectionId, input.Channel,
            input.PlaintextPayload.Span, handler);
    }

    public bool DispatchShop(SessionInput input)
    {
        if (input.Channel != ClientGameplayChannel || input.PlaintextPayload.Length == 0)
            return false;
        var participant = Participant(input.ConnectionId);
        var accepted = Started && !Paused && !_combatGameOver() && !_combatShopClosed() &&
            participant is { Spawned: true, Ready: true } && participant.Hero is { } hero &&
            _shop is not null && _shop.Dispatch(participant, hero, input.PlaintextPayload);
        if (accepted && participant is not null)
            MarkActivity(participant);
        return accepted;
    }

    public bool DispatchCheat(SessionInput input)
    {
        if (input.Channel != ClientGameplayChannel || input.PlaintextPayload.Length == 0)
            return false;
        var participant = Participant(input.ConnectionId);
        return Started && !Paused && !_combatGameOver() && participant is { Spawned: true, Ready: true } && participant.Hero is { } hero && _cheats is not null &&
            _cheats.Dispatch(participant, hero, input.PlaintextPayload);
    }

    private const byte ClientGameplayChannel = 1;
    private const byte ClientClockChannel = 2;
    /// <summary>UseObject: the stock client's right-click on a useable unit (batch 470).</summary>
    internal const byte UseObjectOpcode = 0x3F;

    public bool Start()
    {
        if (!CanStart)
            return false;
        _startClock = _combatClock();
        foreach (var participant in _participants)
        {
            participant.Hero!.MatchStatistics.StartMatch(_startClock);
            // A seat the barrier stopped waiting for starts the match detached; AuthenticateReconnect
            // closes this span when it returns (RecordReconnect).
            if (_loadingTimedOut.Contains(participant.PlayerId))
                participant.Hero.MatchStatistics.RecordDisconnect(_startClock);
        }
        Started = true;
        return true;
    }

    /// <summary>Detach a peer while preserving its authoritative hero.</summary>
    public bool Disconnect(long connectionId)
    {
        var participant = Participant(connectionId);
        if (participant is null)
            return false;
        if (participant.Hero is { } hero)
        {
            if (Started && Result is null)
            {
                // A detach can arrive between scheduler polls. Evaluate the
                // connected idle span before excluding this seat so AFK
                // history does not depend on poll ordering.
                UpdateConnectedAfk();
                hero.MatchStatistics.RecordDisconnect(_combatClock());
                _combat.NotifyHeroConnection(hero, connected: false);
            }
            _combat.CancelPendingActions(hero);
            hero.AttackMoveGoal = null;
            hero.HeroPursuit = null;
            hero.HeroAutoAcquire = false;
            hero.HeroHold = false;
            hero.Target = null;
            hero.Waypoints.Clear();
        }
        participant.ConnectionId = null;
        participant.Ready = false;
        participant.Loaded = false;
        participant.ReadyAt = null;
        participant.Spawned = false;
        participant.SpawnAt = null;
        participant.ShopOpen = false;
        _reconnectPending.Remove(participant.PlayerId);
        _reconnectedPlayers.Remove(participant.PlayerId);
        _reconnectMapReadySent.Remove(participant.PlayerId);
        _reconnectProtectAt.Remove(participant.PlayerId);
        _afkCancelPending.Remove(participant.PlayerId);
        _afkKicks.Remove(connectionId);
        return true;
    }

    /// <summary>Immediate stop/re-anchor packets for the detached hero.</summary>
    public IReadOnlyList<PlaintextPacket> DisconnectPackets(long connectionId)
    {
        var participant = Participant(connectionId);
        if (participant?.Hero is not { } hero)
        {
            Disconnect(connectionId);
            return Array.Empty<PlaintextPacket>();
        }
        // The detach drops the hero's unfinished swing (Disconnect does it again, harmlessly); the stop that tells the clients
        // goes out first, so it has to say so.
        _combat.CancelPendingActions(hero);
        var packets = new[]
        {
            _effects.BuildStopAttack(hero),
            // The stop's one-point path is installed on receipt (the client's path-follow routine): seated on its anchor at once.
            _effects.BuildWaypoints(hero, _movement.InstallStop(hero),
                checked((uint)World.Clock.SyncId())),
        };
        Disconnect(connectionId);
        return packets;
    }

    /// <summary>
    /// Handles one native receive after ENet framing. Decryption is performed
    /// exactly once at this boundary: channel 1 and chat channel 5 use the
    /// legacy ECB stream rule, while short packets and all other channels stay
    /// byte-for-byte unchanged.
    /// </summary>
    public IReadOnlyList<SessionOutboundPacket> Receive(
        long connectionId,
        byte channel,
        ReadOnlySpan<byte> wirePayload,
        Func<SessionGameplayContext, bool>? gameplayHandler = null)
    {
        if (connectionId <= 0 || wirePayload.Length == 0)
            return Array.Empty<SessionOutboundPacket>();

        var wire = wirePayload.ToArray();
        // The client Blowfish-encrypts channels 1 and 5 and its channel-2 clock reply (49 bytes: whole blocks, then the tail clear).
        var encrypted = channel is 1 or 2 or 5 && wire.Length >= LegacyBlowfishCipher.BlockSize;
        var plaintext = encrypted ? _cipher.Decrypt(wire) : wire.ToArray();
        var input = new SessionInput(connectionId, channel, wire, plaintext, encrypted);
        var output = new List<SessionOutboundPacket>();
        var now = World.Clock.Monotonic();

        if (channel == 0)
        {
            var reconnecting = Started;
            var participant = reconnecting
                ? AuthenticateReconnect(connectionId, plaintext)
                : Authenticate(connectionId, plaintext);
            if (participant is null) return output;
            // A restarted process needs the ordinary loading handshake. 0x10
            // is a reconnect notification for an already-loaded game UI; its
            // stock handler dereferences the HUD before this client has one.
            AddInitialLoading(output, participant);
            return output;
        }

        var participantForInput = Participant(connectionId);
        if (participantForInput is null)
            return output;

        if (channel == LeagueProtocolConstants.LoadingChannel)
        {
            output.AddRange(ToParticipant(participantForInput,
                BuildLoadingBurst(connectionId)));
            return output;
        }

        if (channel == 5)
        {
            // GameOver is latched at Nexus/surrender time, but the stock
            // ceremony keeps chat open until the EndGame packet at 8.5s.
            // The receive boundary closes the whole channel once that native
            // terminal transition has been sent.
            if (_combatEndGameSent()) return output;
            if (_chat is not null)
            {
                var chatPackets = _chat.Dispatch(participantForInput, plaintext, _participants,
                    allowCommands: !Paused);
                output.AddRange(chatPackets);
                if (chatPackets.Count != 0)
                    MarkActivity(participantForInput);
            }
            return output;
        }

        if (channel == ClientClockChannel)
        {
            HandleClockSync(output, participantForInput, plaintext);
            return output;
        }

        if (channel != ClientGameplayChannel)
            return output;

        var opcode = plaintext[0];
        switch (opcode)
        {
            case 197: // SynchVersion
            case 199:
                if (!Started)
                {
                    output.AddRange(ToParticipant(participantForInput,
                        [LoadingPackets.SynchVersionS2C(players: Roster, mapId: _mapId, mode: _mode),
                         LoadingPackets.WorldSendGameNumber()]));
                    if (!participantForInput.Spawned && participantForInput.SpawnAt is null)
                        participantForInput.SpawnAt = now;
                }
                else if (_reconnectedPlayers.Contains(participantForInput.PlayerId))
                {
                    // A reconnecting stock client repeats the version/world
                    // handshake after its key check. The match is already
                    // running, so answer it and arm the existing-hero spawn
                    // baseline without rebuilding any world state.
                    output.AddRange(ToParticipant(participantForInput,
                        [LoadingPackets.SynchVersionS2C(players: Roster, mapId: _mapId, mode: _mode),
                         LoadingPackets.WorldSendGameNumber()]));
                    if (!participantForInput.Spawned && participantForInput.SpawnAt is null)
                        participantForInput.SpawnAt = now;
                }
                break;
            case 25: // PingLoadInfo
                HandlePingLoadInfo(output, participantForInput, plaintext);
                break;
            case TutorialPackets.PopupClosedClientOpcode:
            case TutorialPackets.AudioEventFinishedClientOpcode:
            case TutorialPackets.CameraClientOpcode:
            case DominionPackets.QuestEventClientOpcode:
                // Batch 563: the Basic Tutorial's acknowledgements (TutorialPackets); the hook is set on map 3 only. Batch 580: and the
                // camera report its RegisterOnCameraNearUnitCallback watches. Batch 674: and Battle Training's quest events (map 1 in TUTORIAL).
                if (Started) TutorialAcknowledgement?.Invoke(plaintext.ToArray());
                break;
            case PingPackets.ClientOpcode:
                if (Started && HandleMapPing(output, participantForInput, plaintext, now))
                    MarkActivity(participantForInput);
                break;
            case SurrenderPackets.ClientOpcode:
                HandleSurrender(output, participantForInput, plaintext);
                break;
            case PausePackets.PauseOpcode:
            case PausePackets.ResumeOpcode:
                HandlePause(output, participantForInput, plaintext);
                break;
            case 0xC6: // stock world/UI initialization completed (CharSelected)
                // The client sends it once, when it has loaded ("Start Main Loop"), and needs nothing from the
                // spawn burst first. The burst waits for it (RunSchedulers), and the seat becomes ready on the pass after the
                // burst (MarkReady still needs Spawned). It is recorded at any time, also before the handshake.
                if (plaintext.Length == 5 && plaintext[1] == 0 && plaintext[2] == 0 &&
                    plaintext[3] == 0 && plaintext[4] == 0)
                    participantForInput.Loaded = true;
                break;
            case 0x55: // later loading marker, not native world readiness
            case 0xA4: // reconnect announcement, not native world readiness
                break;
            case 23:
                // On channel 1 opcode 0x17 is always the client's
                // QueryStatusReq.  Shop swap uses its own typed dispatch
                // boundary and must not consume this loading poll after the
                // match has started.
                output.Add(new SessionOutboundPacket(connectionId, LoadingPackets.QueryStatusAns()));
                break;
            default:
                if (Started)
                {
                    if (opcode is 0x60 or 0x87 or 0x09 or 0x23)
                    {
                        // Batch 691b: while the level script holds INPUT_SHOP (bit 16) the shop does not open, buy or sell (an inventory swap
                        // is not the shop's). The stock client's lock closes the shop and unbinds evtOpenShop, so it sends none
                        // of them (Reconstruction: the server's own check is not in the shipped files).
                        if (opcode is not 0x23 && (ScriptInputLocks?.Invoke() & 16u) is > 0u) break;
                        // Batch 580: the shop's opening is also the tutorial's RegisterOnShopOpenedCallback (map 3 only).
                        if (opcode == TutorialPackets.ShopOpenedClientOpcode) TutorialAcknowledgement?.Invoke(plaintext.ToArray());
                        DispatchShop(input);
                    }
                    else if (opcode == 0xB6)
                        DispatchCheat(input);
                    else if (gameplayHandler is not null)
                        DispatchGameplay(input, gameplayHandler);
                }
                break;
        }

        return output;
    }

    /// <summary>
    /// The client answers every 0xC9 with a 49-byte 0x08 on channel 2 (SynchSimTime, the client's packet handler for 0xC9): its client
    /// id at +5, the server time it received echoed at +9, its clock right after setting it at +13, and a 32-byte digest of its
    /// own code. The retail server answered with 0x79 (<see cref="SpawnPackets.SyncSimTimeFinal"/>): the client time and the
    /// round trip. The digest is an anti-tamper value this server does not check.
    /// <para>
    /// The round trip is the HUD time now minus the echoed server time. The client's 0x79 case folds +9 into its
    /// average latency (0.8 old + 0.1 new, the two averaging constants), which the next 0xC9 adds to the server time it sets its clock to,
    /// so the average settles at half its input, the one-way latency. Its own check, the overlay's "last convergence dt", is that
    /// average minus half of its clock now minus the echoed <c>time_LastClient</c>: zero exactly when +9 is the round trip
    /// the client itself sees. The 0x08 echoes the server time for this alone, so the input is the full round trip measured from it
    /// (batch 315). The answer changes the clock only at the next 0xC9, which this server sends at the start, on a reconnect and on a
    /// resume.
    /// </para>
    /// </summary>
    private void HandleClockSync(List<SessionOutboundPacket> output, SessionParticipant participant, byte[] plaintext)
    {
        if (!Started || plaintext.Length != 49 || plaintext[0] != 0x08 || participant.ConnectionId is not long connectionId)
            return;
        var lastServer = BitConverter.ToSingle(plaintext, 9);
        var lastClient = BitConverter.ToSingle(plaintext, 13);
        if (!float.IsFinite(lastServer) || !float.IsFinite(lastClient)) return;
        var hudNow = Math.Max(0.0, _combatClock() - (_hudOrigin ?? _startClock));
        var roundTrip = Math.Clamp(hudNow - lastServer, 0.0, 5.0);
        output.Add(new SessionOutboundPacket(connectionId, SpawnPackets.SyncSimTimeFinal(
            lastClient, ProtocolEncoding.Float32(roundTrip, nameof(roundTrip)))));
    }

    private void MarkActivity(SessionParticipant participant)
    {
        if (!Started || Result is not null || _combatGameOver() || participant.IsBot || participant.ConnectionId is null ||
            !participant.Spawned || !participant.Ready || participant.Hero is not { } hero)
            return;
        var now = _combatClock();
        if (double.IsFinite(now))
        {
            // Polling is normally the boundary that latches inactivity. An
            // accepted command can arrive first, so evaluate the old idle
            // interval before replacing its activity timestamp.
            hero.MatchStatistics.UpdateAfk(now, AfkThresholdSeconds);
            // A warned (not yet kicked) player gets the stock cancellation
            // line on the next scheduler pass.
            if (hero.MatchStatistics.AfkNoticeStage is 1 or 2)
                _afkCancelPending.Add(participant.PlayerId);
            hero.MatchStatistics.RecordActivity(now);
        }
    }

    /// <summary>
    /// Latches AFK for connected, ready human seats. With <paramref name="notices"/>
    /// (scheduler pass only) it also emits each idle episode's owner-only
    /// warnings once and queues the kick for the network layer.
    /// </summary>
    private void UpdateConnectedAfk(List<SessionOutboundPacket>? notices = null,
        bool allowGameOver = false)
    {
        if (!Started || AfkTrackingDisabled || (!allowGameOver && _combatGameOver())) return;
        var now = _combatClock();
        if (!double.IsFinite(now)) return;
        foreach (var participant in _participants)
        {
            if (participant.IsBot || participant.ConnectionId is not long connectionId ||
                !participant.Spawned || !participant.Ready || participant.Hero is not { } hero)
                continue;
            var statistics = hero.MatchStatistics;
            statistics.UpdateAfk(now, AfkThresholdSeconds);
            if (notices is null) continue;
            if (_afkCancelPending.Remove(participant.PlayerId))
                notices.Add(new SessionOutboundPacket(connectionId,
                    AfkPackets.LocalizedChatText(AfkPackets.KickCancelledKey)));
            var idle = now - statistics.LastActivityAt;
            if (idle >= AfkChatWarningSeconds && statistics.TryAdvanceAfkNotice(1))
                notices.Add(new SessionOutboundPacket(connectionId,
                    AfkPackets.LocalizedChatText(AfkPackets.MoveOrBeKickedKey)));
            if (idle >= AfkWarningBoxSeconds && statistics.TryAdvanceAfkNotice(2))
                notices.Add(new SessionOutboundPacket(connectionId, AfkPackets.OpenWarningMessage()));
            if (idle >= AfkThresholdSeconds && statistics.TryAdvanceAfkNotice(3))
                _afkKicks.Add(connectionId);
        }
    }

    /// <summary>
    /// Owner decision 18 October 2026 (option C): once <see cref="LoadingTimeoutSeconds"/> have passed since the
    /// first human connection and at least one human seat has finished loading, the start barrier stops waiting
    /// for every human seat that has not (connected and spawned and ready). Such a seat is detached like any
    /// lost connection (the network layer disconnects an attached peer), keeps its hero and returns later through
    /// <see cref="AuthenticateReconnect"/>. Nothing happens while no human seat has finished: a start with nobody
    /// present would only run bots, and the host's 30-minute abort remains the bound.
    /// </summary>
    private void ExpireLoadingTimeout(double now)
    {
        if (Started || !WorldCreated || _firstHumanConnectionAt is not double first ||
            now - first + 1e-9 < LoadingTimeoutSeconds)
            return;
        var humans = _participants.Where(participant => !participant.IsBot).ToArray();
        static bool Finished(SessionParticipant seat) => seat.ConnectionId.HasValue && seat.Spawned && seat.Ready;
        if (!humans.Any(Finished)) return;
        foreach (var seat in humans.Where(seat => !Finished(seat) && !_loadingTimedOut.Contains(seat.PlayerId)))
        {
            _loadingTimedOut.Add(seat.PlayerId);
            _loadingExpiries.Add(new LoadingExpiry(seat.PlayerId, seat.ConnectionId, Late: false));
        }
    }

    /// <summary>Seats the loading limit dropped since the last call; the network disconnects the attached ones.</summary>
    internal IReadOnlyList<LoadingExpiry> ConsumeLoadingExpiries()
    {
        if (_loadingExpiries.Count == 0) return Array.Empty<LoadingExpiry>();
        var expiries = _loadingExpiries.ToArray();
        _loadingExpiries.Clear();
        return expiries;
    }

    /// <summary>
    /// Connections whose seat crossed the AFK kick threshold since the last
    /// call. The network disconnects each with <see cref="AfkPackets.KickDisconnectData"/>;
    /// the ordinary detach/reconnect/leaver handling then applies.
    /// </summary>
    internal IReadOnlyList<long> ConsumeAfkKicks()
    {
        if (_afkKicks.Count == 0) return Array.Empty<long>();
        var kicks = _afkKicks.ToArray();
        _afkKicks.Clear();
        return kicks;
    }

    private bool HandleMapPing(
        List<SessionOutboundPacket> output,
        SessionParticipant participant,
        ReadOnlySpan<byte> plaintext,
        double now)
    {
        if (participant.Hero is not { } hero || !participant.Spawned ||
            !PingPackets.TryReadRequest(plaintext, out var request) ||
            (request.ActorNetId != 0 && request.ActorNetId != hero.NetId) ||
            !PingPackets.IsMapBounded(request.Position, _mapBounds))
            return false;

        if (request.TargetNetId != 0)
        {
            var target = World.Get(request.TargetNetId);
            // A sender may target a friendly object by identity, but an enemy
            // target must already be visible to this seat.  Rejecting here
            // keeps a forged hidden target from becoming a team-wide leak.
            if (target is null ||
                target.Team != hero.Team && !participant.View.Visible.Contains(target.NetId))
                return false;
        }

        if (!_pingRate.TryTake(participant.PlayerId, now))
            return false;

        foreach (var recipient in _participants)
        {
            if (recipient.Team != participant.Team ||
                recipient.ConnectionId is not long connectionId ||
                !recipient.Spawned || !recipient.Ready || recipient.Hero is null)
                continue;

            // A target reference is meaningful only to a recipient that has
            // the entity in its current visibility set.  Keep the ping itself
            // useful to teammates while stripping a stale/unseen target ID.
            var targetNetId = request.TargetNetId != 0 &&
                recipient.View.Visible.Contains(request.TargetNetId)
                ? request.TargetNetId : 0u;
            var packet = PingPackets.MapPingNotification(
                hero.NetId,
                request.Position,
                targetNetId,
                request.Category);
            output.Add(new SessionOutboundPacket(connectionId, packet));
        }
        return true;
    }

    /// <summary>Runs delayed spawn/start/HUD/skin/protection work at one clock point.</summary>
    public IReadOnlyList<SessionOutboundPacket> RunSchedulers()
    {
        var now = World.Clock.Monotonic();
        var output = new List<SessionOutboundPacket>();
        UpdatePause(output, now);
        UpdateConnectedAfk(output);
        ExpireLoadingTimeout(now);

        if (_terminalStatsPending && Result is not null)
        {
            foreach (var participant in _participants)
            {
                if (participant.ConnectionId is not long connectionId ||
                    !participant.Spawned || !participant.Loaded || !participant.Ready ||
                    participant.Hero is null)
                    continue;
                output.Add(new SessionOutboundPacket(connectionId,
                    _terminalClassicStats[participant.PlayerId]));
            }
            _terminalStatsPending = false;
        }

        if (Started)
            ExpireSurrenderVotes(output, _combatClock());

        foreach (var participant in _participants)
        {
            if (participant.ConnectionId is not long connectionId)
                continue;
            // The stock client dispatches network packets from the loading screen's own update calls, so a CreateHero
            // that arrives while the map is still loading is handled there. Creating a hero loads its spell scripts through the
            // global Lua state, which the world load builds only after the room.nvr load (InitLuaForGameWorld);
            // before that the Lua script loader reads a null state and the client faults (read of address 0).
            // A burst on a fixed timer after the handshake crashed every launch whose map load ran longer than the timer.
            // The client sends 0xC6 ("Start Main Loop") after the world load, so the burst follows it, which is also the
            // order the readiness notes record for retail, in docs/network/native-world-readiness.md. The heroes then take about
            // a second to create, inside the settle time before the map phase and StartGame.
            var spawnedThisPass = false;
            if (participant.SpawnAt is not null && participant.Loaded)
            {
                var packets = _reconnectedPlayers.Contains(participant.PlayerId)
                    ? SpawnReconnect(connectionId)
                    : Spawn(connectionId);
                output.AddRange(ToParticipant(participant, packets));
                participant.SpawnAt = null;
                spawnedThisPass = true;
            }
            // The seat is made ready on the pass after its burst, so the client receives the burst before anything that
            // depends on it (the reconnect state, the map phase), as it did while the burst preceded the 0xC6.
            if (!spawnedThisPass && participant.Spawned && participant.Loaded && !participant.Ready)
            {
                if (MarkReady(connectionId) &&
                    _reconnectedPlayers.Contains(participant.PlayerId))
                    output.AddRange(ToParticipant(participant,
                        BuildReconnectReadyPackets(connectionId)));
            }
        }

        if (CanStart && _mapReadySent.Count == 0)
        {
            var mapBursts = new Dictionary<SessionParticipant, IReadOnlyList<PlaintextPacket>>();
            foreach (var participant in _participants)
            {
                if (participant.ConnectionId is not long connectionId ||
                    _loadingTimedOut.Contains(participant.PlayerId)) continue;
                foreach (var entity in MapEntities)
                {
                    participant.View.Known.Add(entity.NetId);
                }
                // Build every recipient's fixed map phase first. The source
                // replication counter has consumed the two map sends for
                // each unit before Delivery.flush starts the observer pass.
                mapBursts[participant] = BuildMapReadyPackets(connectionId);
            }
            foreach (var participant in _participants)
            {
                if (participant.ConnectionId is not long connectionId ||
                    !mapBursts.TryGetValue(participant, out var mapBurst))
                    continue;
                output.AddRange(ToParticipant(participant, mapBurst));
            }
            if (!Start())
                throw new InvalidOperationException("Session start barrier changed while building map-ready packets.");
            // A timed-out seat that attached after the limit is still loading: it is detached like the ones
            // the limit dropped, and its client returns through the reconnect path.
            foreach (var late in _participants.Where(item => _loadingTimedOut.Contains(item.PlayerId) &&
                         item.ConnectionId.HasValue))
                if (!_loadingExpiries.Any(queued => queued.PlayerId == late.PlayerId && queued.ConnectionId == late.ConnectionId))
                    _loadingExpiries.Add(new LoadingExpiry(late.PlayerId, late.ConnectionId, Late: true));
            TurretRebindDone = true;
            // Reconstruction: the first 0xC9 follows the start by 1 s so it lands after the client's first game-loop pass
            // (the HUD zero is whenever that packet lands; see SyncMissionStartTime).
            _clockAt = now + 1.0;
            // Map1 LevelScript.luaobj instructions 0082-0086: InitTimer('AllowDamageOnBuildings', 10.0, false) is the
            // retail server's own 10-second timer for the building protection (CombatService.EngageProtection).
            _protectAt = now + 10.0;
        }

        if (_clockAt is double clockAt && now + 1e-9 >= clockAt)
        {
            var hudTime = Math.Max(0.0, _combatClock() - _startClock);
            var clock = SpawnPackets.GameClock(ProtocolEncoding.Float32(hudTime, nameof(hudTime)));
            foreach (var participant in _participants)
            {
                if (participant.Spawned && participant.Ready && participant.ConnectionId is long connectionId)
                    output.Add(new SessionOutboundPacket(connectionId, clock));
            }
            // Synchronization is delayed for client readiness. It must not
            // discard simulation time already elapsed since StartGame.
            _hudOrigin = _startClock;
            _announcements.Clear();
            // Batch 455: Dominion (map 8) raises its own start messages from the shipped trees (Announcement_OnStartGame in
            // CapturePointManager and OdinOpeningCeremony: 79 and 80 with the Map8 texts), and no shipped Dominion tree raises 78.
            // The classic schedule below therefore runs on the other maps only.
            if (_mapId != GameMaps.CrystalScar)
            {
                // Reconstruction: when the retail server raised OnStartGameMessage1/2 (79 at HUD 30, 80 at HUD 60) is not in the
                // shipped files (Announcements_CLASSIC.ini holds the texts and sounds only); 80 reads "thirty seconds until minions
                // spawn", so it sits 30 s before the first wave.
                _announcements.Add((79, 30.0));
                _announcements.Add((80, 60.0));
                // Reconstruction: nothing in the shipped files raises world event 78 OnMinionsSpawn (no
                // client code path passes id 78 to the event dispatcher, and the Map1 LevelScript calls
                // no announcement), so the server owns it. Event 80 says "thirty seconds until minions
                // spawn" at HUD 60 and the LevelScript's INITIAL_TIME_TO_SPAWN is 90, the first wave.
                _announcements.Add((78, Amberline.Simulation.Minions.MinionDefinitions.FirstWaveSeconds));
            }
            _nexusIdleParticlesSent = false;
            _clockAt = null;
            if (_minionFidelity && !_visibilityBaseline)
            {
                var hero = _participants[0].Hero;
                if (hero is not null && _participants[0] is { Spawned: true, Ready: true, ConnectionId: not null })
                {
                    var route = _movement.SetMoveOrder(hero,
                        new Position2(7601.74, 6449.17));
                    output.Add(new SessionOutboundPacket(
                        _participants[0].ConnectionId!.Value,
                        _effects.BuildWaypoints(hero,
                            route.Count == 0 ? new[] { hero.Position } : route,
                            checked((uint)World.Clock.SyncId()))));
                }
            }
        }

        if (_hudOrigin is double hudOrigin)
        {
            var hudNow = _combatClock() - hudOrigin;
            // The waves stop at game over, so their announcement stops with them.
            if (_combatGameOver()) _announcements.Clear();
            while (_announcements.Count != 0 && hudNow + 1e-9 >= _announcements[0].HudTime)
            {
                var (eventId, _) = _announcements[0];
                _announcements.RemoveAt(0);
                // The client's @MapNumber@ picks the start text and voice line per map (Announcements_CLASSIC.ini:529 and :537,
                // game_announcement_OnStartMessage1Map@MapNumber@), so the packet carries the match's map id. Summoner's Rift (1) and
                // the Harrowing rift (2) read the same welcome text (fontconfig_en_US.txt:681-682); Twisted Treeline (4) reads
                // "Welcome to The Twisted Treeline!" (:683). Event 78 has no map key, so its text does not depend on the number.
                var packet = CombatPackets.OnEventWorld(eventId, 0, 0, mapNumber: _mapId);
                foreach (var participant in _participants)
                {
                    if (participant.Spawned && participant.Ready && participant.ConnectionId is long connectionId)
                        output.Add(new SessionOutboundPacket(connectionId, packet));
                }
            }

            // Reconstruction: the client starts with the nexus intro particles and switches
            // them once at HUD 1:38. The 98 s is the Python server's figure; no shipped file
            // or client code names the time, so it is kept as observed. This follows the
            // combat/HUD clock, so speed changes and deterministic ticks cross the same boundary.
            if (!_nexusIdleParticlesSent && !_combatGameOver() && hudNow + 1e-9 >= 98.0)
            {
                var packet = SpawnPackets.SwitchNexusesToIdleParticles();
                foreach (var participant in _participants)
                {
                    if (participant.Spawned && participant.Ready && participant.ConnectionId is long connectionId)
                        output.Add(new SessionOutboundPacket(connectionId, packet));
                }
                _nexusIdleParticlesSent = true;
            }
        }

        if (!Paused && _protectAt is double protectAt && now + 1e-9 >= protectAt)
        {
            var packets = _combat.EngageProtection(World);
            foreach (var participant in _participants)
            {
                if (!participant.Spawned || !participant.Ready ||
                    participant.ConnectionId is not long connectionId) continue;
                foreach (var packet in packets)
                {
                    if (packet.EntityReferences.Count != 0 &&
                        !packet.EntityReferences.Any(reference =>
                            participant.View.Visible.Contains(reference.NetId)))
                        continue;
                    output.Add(new SessionOutboundPacket(connectionId, packet));
                }
            }
            _protectAt = null;
        }

        if (!Paused && _reconnectProtectAt.Count != 0)
            ResendReconnectProtection(output, now);

        return output;
    }

    /// <summary>
    /// Reconstruction: the current protection state of every living lane turret, to the one returning seat; retail's reconnect resend is not recovered.
    /// See <see cref="BuildReconnectReadyPackets"/>.
    /// </summary>
    private void ResendReconnectProtection(List<SessionOutboundPacket> output, double now)
    {
        foreach (var (playerId, dueAt) in _reconnectProtectAt.ToArray())
        {
            if (now + 1e-9 < dueAt) continue;
            _reconnectProtectAt.Remove(playerId);
            if (Result is not null ||
                _participants.FirstOrDefault(seat => seat.PlayerId == playerId) is not
                { Spawned: true, Ready: true, ConnectionId: long connectionId })
                continue;
            foreach (var turret in MapEntities.Where(entity => entity.UnitType == UnitType.Turret &&
                         entity.Alive && !TurretRules.IsShrineTurret(entity)))
                output.Add(new SessionOutboundPacket(connectionId,
                    ProtocolCombatTransport.StructureProtectionPacket(turret, _spawn.SyncIds.Next(turret.NetId))));
        }
    }

    /// <summary>
    /// Completes the restarted client's staged loading boundary without
    /// entering the match-start barrier or creating any world entities.
    /// </summary>
    internal IReadOnlyList<PlaintextPacket> BuildReconnectReadyPackets(
        long connectionId)
    {
        var participant = RequireParticipant(connectionId);
        if (!Started || !_reconnectedPlayers.Contains(participant.PlayerId) ||
            !participant.Spawned || !participant.Loaded || !participant.Ready ||
            !_reconnectMapReadySent.Add(participant.PlayerId))
            return Array.Empty<PlaintextPacket>();

        // Initial start seeds map IDs before its direct burst. Reconnect must
        // do the same so the first normal delivery flush deduplicates live
        // allied structures without changing Visible or fogged actors.
        foreach (var entity in MapEntities)
            participant.View.Known.Add(entity.NetId);
        var packets = _spawn.BuildMapReadyBurst(participant, MapEntities).ToList();
        if (_nexusIdleParticlesSent)
            packets.Add(SpawnPackets.SwitchNexusesToIdleParticles());
        foreach (var structure in MapEntities)
        {
            if (structure.Kind == EntityKind.Turret && !structure.Alive)
            {
                // Reconstruction: ForceDead (0x1E), not the NPC death (0xA6), restores a destroyed turret. Every
                // lane tower is a client-built obj_AI_Turret that 0xA5 only binds a net ID to, so it must be
                // bound and then killed or it stands intact. A 0xA6 runs the unit's death routine, which notifies
                // EVENT_ON_DIE and the turret event manager's death handler raises the OnTurretDie announcement, so a state
                // restore showed a "turret destroyed" banner (playtest of 15 October 2026). DoForceDead sets
                // the same bDead flag and plays the death animation without any event. The turret's update
                // then deactivates the dead unit once 1.2 s of game time have passed since its construction,
                // and the scene obj_Turret, which draws the "DestroyedTower" skin when its child is inactive,
                // shows the rubble. DoDeath's death audio, particles and client nav-stamp removal are skipped;
                // the server keeps a dead turret's stamp too. Retail's restore is not recovered (this is the Reconstruction).
                packets.Add(CombatPackets.ForceDead(structure.NetId));
                packets.Add(CombatPackets.TurretProtectionFlags(structure.NetId,
                    CombatPackets.SFlagNontargetableAll, _spawn.SyncIds.Next(structure.NetId),
                    invulnerable: 1u, targetable: 0u));
            }
            else if (structure.Kind == EntityKind.Inhibitor && !structure.Alive)
            {
                var remaining = Math.Max(0.0, structure.RespawnAt - _combatClock());
                packets.Add(CombatPackets.DampenerState(structure.NetId,
                    checked((float)Math.Clamp(remaining, 0.0, float.MaxValue)), dead: true));
            }
        }
        var currentClock = _combatClock();
        if (!double.IsFinite(currentClock)) currentClock = 0.0;
        currentClock = Math.Clamp(currentClock, 0.0, float.MaxValue);
        // A reconnect must continue the original HUD timeline. The normal
        // start scheduler owns the origin; do not move that origin when a
        // fresh transport joins an already-running match.
        var hudTime = Math.Max(0.0, currentClock - (_hudOrigin ?? _startClock));
        // The stock HUD shows SlaveClock.t minus an origin that the client latches to its own t on its
        // first game-loop iteration. The 0xC9 below shares a burst with
        // StartGame and is processed before that iteration, so the restarted client latched
        // t = hudTime and its HUD restarted at 0:00 (playtest of 15 October 2026, 25 minutes in).
        // 0xCA, SyncMissionStartTime, sets the origin explicitly; zero is the origin in the clock
        // domain of that 0xC9.
        packets.Add(SpawnPackets.SyncMissionStartTime(0.0f));
        packets.Add(SpawnPackets.GameClock(
            ProtocolEncoding.Float32(Math.Clamp(hudTime, 0.0, float.MaxValue), nameof(hudTime))));
        // Reconstruction: retail's reconnect burst is not recovered. That same 0xC9 jumps the client's clock from 0, and the map script's timers
        // advance by clock deltas (the map script's update), so every timer elapses at once, including
        // the 10 s AllowDamageOnBuildings timer. Its Lua re-protects each lane turret except the
        // front one on the first frame after the sync and overwrites the protection state sent with
        // this burst. Resend that state once this frame has certainly run (ResendReconnectProtection).
        _reconnectProtectAt[participant.PlayerId] = World.Clock.Monotonic() + ReconnectProtectionDelaySeconds;
        return packets;
    }

    /// <summary>Late reconnect state, sent after visibility, equipment and spell snapshots.</summary>
    public IReadOnlyList<PlaintextPacket> BuildReconnectMatchState(long connectionId)
    {
        var seat = Participant(connectionId);
        if (seat is not { Spawned: true, Loaded: true, Ready: true } ||
            !_reconnectMapReadySent.Contains(seat.PlayerId)) return Array.Empty<PlaintextPacket>();
        if (Result is { } result)
        {
            var terminal = new List<PlaintextPacket>();
            if (seat.Hero is not null)
                terminal.Add(_terminalClassicStats[seat.PlayerId]);
            terminal.Add(CombatPackets.EndGame(result.Winner == nameof(Team.Order), result.Surrendered));
            return terminal;
        }
        var packets = new List<PlaintextPacket>();
        foreach (var member in _participants)
        {
            if (member.Hero is not { } hero) continue;
            if (member != seat && !seat.View.Known.Contains(hero.NetId)) continue;
            packets.Add(member == seat
                ? _spawn.BuildClassicHeroStats(hero, ClassicHeroStatsNow(), member.PlayerId)
                : _spawn.BuildPublicClassicHeroStats(hero));
        }
        AddReconnectSurrender(packets, seat);
        AddReconnectPause(packets);
        // Batch 691b: the returning client's tutorial manager is new (its input-locking flags 0), and the script's later
        // ToggleInputLockingFlag (0xA3) is an XOR, so each flag the level script holds is set again, one 0x89 per flag (the client's
        // callback switches on one flag). Reconstruction: retail's reconnect burst is not recovered.
        if (ScriptInputLocks?.Invoke() is uint locks)
            for (var flag = 1u; flag <= 128u; flag <<= 1)
                if ((locks & flag) != 0) packets.Add(CombatPackets.SetInputLockingFlag(flag, true));
        return packets;
    }

    /// <summary>Batch 691b: the input-locking flags the map's level script holds on the clients (LevelScriptHost.InputLocks).</summary>
    public Func<uint>? ScriptInputLocks { get; set; }

    /// <summary>
    /// Builds the actor-owned Classic snapshot plus narrow public projections
    /// for every other known roster hero.  The request remains local and
    /// authenticated; foreign rows never receive private collection fields.
    /// </summary>
    internal IReadOnlyList<PlaintextPacket> BuildClassicHeroStatsRefreshBurst(
        long connectionId, ReadOnlySpan<byte> payload)
    {
        var participant = ValidClassicHeroStatsRefresh(connectionId, payload);
        if (participant?.Hero is not { } owner)
            return Array.Empty<PlaintextPacket>();

        var packets = new List<PlaintextPacket>(_participants.Length);
        foreach (var member in _participants)
        {
            if (member.Hero is not { } hero) continue;
            if (member != participant && !participant.View.Known.Contains(hero.NetId)) continue;
            packets.Add(member == participant
                ? _spawn.BuildClassicHeroStats(owner, ClassicHeroStatsNow(), member.PlayerId)
                : _spawn.BuildPublicClassicHeroStats(hero));
        }
        return packets;
    }

    private SessionParticipant? ValidClassicHeroStatsRefresh(
        long connectionId, ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 5 || payload[0] != 0x59 || !Started ||
            _combatGameOver()) return null;
        var participant = Participant(connectionId);
        if (participant is not { Spawned: true, Loaded: true, Ready: true, Hero: { } hero } ||
            !participant.View.Known.Contains(hero.NetId) ||
            BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]) != hero.NetId)
            return null;
        return participant;
    }

    private double ClassicHeroStatsNow()
    {
        if (!Started)
            return 0.0;
        var now = _terminalClock ?? _combatClock();
        if (!double.IsFinite(now))
            return Math.Max(0.0, _startClock);
        // MatchStatistics stores the combat-clock origin at Start. The
        // combat clock is simulation-owned and remains frozen while paused;
        // never let a bad/backward source clock add time.
        return Math.Max(_startClock, now);
    }

    private void AddInitialLoading(List<SessionOutboundPacket> output, SessionParticipant recipient)
    {
        if (recipient.ConnectionId is not long connectionId) return;
        foreach (var member in _participants)
            output.Add(new SessionOutboundPacket(connectionId,
                LoadingPackets.RegistryAdd(member.ClientId, member.PlayerId)));
        // No 0x78 SetLocalClientId: the live client never reads it on channel 0; the registry add above sets its id.
        output.AddRange(ToParticipant(recipient, BuildLoadingBurst(connectionId)));
        // The deployed solo harness emits PingLoadInfo before QueryStatusAns;
        // the ordinary two-seat loading path retains its historical query
        // then ping order.  Keep the distinction at the session boundary so
        // replay mode does not perturb the native duel loading contract.
        if (_solo)
        {
            output.Add(new SessionOutboundPacket(connectionId,
                LoadingPackets.PingLoadInfo(recipient.ClientId, recipient.PlayerId)));
            output.Add(new SessionOutboundPacket(connectionId, LoadingPackets.QueryStatusAns()));
        }
        else
        {
            output.Add(new SessionOutboundPacket(connectionId, LoadingPackets.QueryStatusAns()));
            output.Add(new SessionOutboundPacket(connectionId,
                LoadingPackets.PingLoadInfo(recipient.ClientId, recipient.PlayerId)));
        }
    }

    private void HandlePingLoadInfo(List<SessionOutboundPacket> output,
        SessionParticipant source, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 30) return;
        var percentage = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(payload[17..21]));
        var eta = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(payload[21..25]));
        var count = BinaryPrimitives.ReadInt16LittleEndian(payload[25..27]);
        var ping = BinaryPrimitives.ReadUInt16LittleEndian(payload[27..29]);
        var ready = (payload[29] & 1) != 0;
        if (!float.IsFinite(percentage)) return;
        percentage = Math.Clamp(percentage, 0.0f, 100.0f);
        foreach (var participant in _participants)
        {
            if (participant.ConnectionId is not long connectionId) continue;
            output.Add(new SessionOutboundPacket(connectionId,
                LoadingPackets.PingLoadInfo(source.ClientId, source.PlayerId,
                    percentage, float.IsFinite(eta) ? eta : 0.0f, count, ping, ready)));
        }

        // NOT RETAIL (bots as roster players, see Roster). Bots are already
        // server-loaded and have no client to report it, so
        // the server publishes their completed rows with every report of a
        // loading client, to that client only. The stock loading screen
        // (the player connection update, reached from the 0x9D handler)
        // builds a card's splash and summoner spells from a row received after
        // the screen exists, and draws "Not Connected" for a seat without one.
        // Rows sent at authentication arrive before the screen and are dropped.
        if (!source.Loaded && source.ConnectionId is long reporter)
            foreach (var bot in _participants.Where(participant => participant.IsBot))
                output.Add(new SessionOutboundPacket(reporter,
                    LoadingPackets.PingLoadInfo(bot.ClientId, bot.PlayerId)));
    }

    private static IEnumerable<SessionOutboundPacket> ToParticipant(
        SessionParticipant participant, IReadOnlyList<PlaintextPacket> packets)
    {
        if (participant.ConnectionId is not long connectionId)
            yield break;
        foreach (var packet in packets)
            yield return new SessionOutboundPacket(connectionId, packet);
    }

    public byte[] BuildKeyCheck(ulong playerId) => SessionKeyCheck.Build(playerId, _cipher);

    private SessionParticipant RequireParticipant(long connectionId) =>
        Participant(connectionId) ?? throw new InvalidOperationException("Connection is not authenticated.");
}
