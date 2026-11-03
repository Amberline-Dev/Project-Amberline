using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using System.IO;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Items;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Spells;

namespace Amberline.Simulation.Bots;

/// <summary>
/// Server-owned controller for the patch-126 bot seats: the engine half of the shipped
/// <c>Bot.luaobj</c> task arbiter and its <c>Task*.luaobj</c> modules. Those scripts own the
/// decision — which task is active, where the bot walks, what it swings at, when it shops — and
/// run on their own timers; this controller answers their natives, mirrors what they decided and
/// keeps the movement, combat and cast plumbing alive. It uses the existing authoritative
/// services rather than emulating a client.
///
/// The scripts call about 45 native helpers that live in the game server, not in
/// the stock client executable, so their bodies cannot be recovered here. Each
/// place where a helper had to be reconstructed is marked "Reconstruction" and
/// listed in docs/research/patch126-bot-scripts.md. Server-side additions that
/// change what the scripts would do are marked "NOT RETAIL".
/// </summary>
public sealed partial class BotController
{
    // NOT RETAIL: retail 126 has bots only for the 18 champions whose game_bot_
    // name the client ships (ChampionInfo.RetailBot); of the playable ones Annie,
    // Ryze, Sivir, Soraka and Taric are retail bots. This is the bounded
    // generic-controller roster: every playable champion of ChampionCatalog, by
    // owner decision of 22 October 2026: a champion whose bot data the shipped
    // files do not cover is still a bot, driven by the shipped Bot/Task scripts,
    // with its missing per-champion data reconstructed (see
    // NativeReconstructModuleData). Champions outside the catalog's playable list
    // stay server-owned but receive no guessed AI.
    public static readonly IReadOnlySet<string> SupportedChampions = new HashSet<string>(
        ChampionCatalog.PlayableNames, StringComparer.OrdinalIgnoreCase);

    private const double Epsilon = 1e-9;

    // Reconstruction: sqrt(GetMaxTravelDistSquared). The scripts use it as the
    // farthest a bot will travel to push, defend or regroup and as the scale of
    // their distance terms. The longest trip on the map, base to base, keeps a
    // respawned bot from finding every lane "too far" and idling.
    internal static double MaxTravelDistance => Distance(Fountain(Team.Order), Fountain(Team.Chaos));
    private static double MaxTravelDistanceSquared => MaxTravelDistance * MaxTravelDistance;

    // Reconstruction: order throttles. KillMinion and KillHero re-issue SetStateAndMove / SetStateAndCloseToTarget on
    // every 250 ms Tick and retarget to the lowest-health minion each time; the game server's natives took
    // them as they came (not recovered: a reading of the natives, not a trace). Here an order is a path plan plus a WaypointGroup (the stock client stops and drops
    // its avoidance offset at each one) and a retarget interrupts the wind-up, so an identical destination
    // (within SameGoalTolerance) is not ordered again inside MoveRepeatSeconds, and a goal within SameGoalTolerance of the route's end is dropped with no time limit (IssueMove); a live attack is retargeted
    // at most once per AttackRetargetSeconds. A8 audit, seeded 60-minute matches (seeds 21 to 24, ten bots):
    // without the retarget throttle the bots made 5 % fewer last hits and 4.5 % less damage; without the move
    // throttle no gameplay figure moved (one of the four matches hit the NPC solver's NaN, fixed by batch 57), so
    // that one is kept for the client's sake only.
    private const double MoveRepeatSeconds = 1.0;
    private const double AttackRetargetSeconds = 1.0;
    private const double SameGoalTolerance = 120.0;

    private readonly WorldState _world;
    private readonly MovementSystem _movement;
    private readonly CombatService _combat;
    private readonly SpellSystem _spells;
    private readonly ShopService _shop;
    private readonly ItemCatalog _catalog;
    private readonly HeroAttackOrderSystem _heroOrders;
    private readonly WaveScheduler _waves;
    private readonly ISpellEffectSink _effects;
    private readonly Func<Entity, Entity, bool> _visible;
    private readonly IReadOnlyList<SessionParticipant> _participants;
    private readonly IRandomSource _random;
    private readonly GameData _data;
    private readonly Dictionary<string, CastMode> _castMode = new(StringComparer.OrdinalIgnoreCase);
    // DIAGNOSTICS: retail scripts run only on bot seats; headless matches run them on every seat.
    private readonly bool _controlsAllSeats;
    private readonly HashSet<ulong> _scriptedSeats = new();
    private readonly Dictionary<ulong, BotState> _states = new();
    // The same states by hero id: the script natives arrive with the unit, not the seat.
    private readonly Dictionary<uint, BotState> _stateByHero = new();

    public BotController(
        WorldState world,
        MovementSystem movement,
        CombatService combat,
        SpellSystem spells,
        ShopService shop,
        ItemCatalog catalog,
        HeroAttackOrderSystem heroOrders,
        WaveScheduler waves,
        ISpellEffectSink effects,
        IReadOnlyList<SessionParticipant> participants,
        Func<Entity, Entity, bool> visible,
        IRandomSource? random = null,
        bool controlsAllSeats = false,
        GameData? data = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _movement = movement ?? throw new ArgumentNullException(nameof(movement));
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _spells = spells ?? throw new ArgumentNullException(nameof(spells));
        _shop = shop ?? throw new ArgumentNullException(nameof(shop));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _heroOrders = heroOrders ?? throw new ArgumentNullException(nameof(heroOrders));
        _waves = waves ?? throw new ArgumentNullException(nameof(waves));
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
        _participants = participants ?? throw new ArgumentNullException(nameof(participants));
        _visible = visible ?? throw new ArgumentNullException(nameof(visible));
        _random = random ?? new SystemRandomSource();
        _controlsAllSeats = controlsAllSeats;
        _data = data ?? GameData.Default;
        // The shipped AI runs on the match's own Lua host: one Bot.luaobj instance per bot seat,
        // its natives answered by the plumbing above (BotController.Natives.cs).
        Engine = new BotLuaAiEngine(this);
        Host = new LuaAiService(_world, Engine, new AiScriptCatalog(_data.Paths));
        // Batch 167: the family a champion's AI belongs to is its seat's, not the unit's. Both
        // hosts ask this controller before claiming a hero, so a bot seat's hero runs Bot.luaobj
        // here and never a Hero.luaobj on the hero host, and a human player's hero the other way
        // round. A harness that runs one family alone leaves the answer unset and each host drives
        // the heroes it is handed.
        Host.IsBotHero = DrivesHero;
        heroOrders.Host.IsBotHero = DrivesHero;
    }

    /// <summary>
    /// Whether this controller's scripts drive a hero: the seat is one of the bot's, its champion
    /// is in the roster and no tool has released it (<see cref="SetSeatScripted"/>) — the same test
    /// <see cref="Tick"/> applies before it steps a seat, so this is the set of heroes the bot
    /// family owns, asked race-free from the participants rather than from the states the first
    /// update has not built yet.
    /// </summary>
    internal bool DrivesHero(Entity hero) =>
        _participants.Any(participant => participant.Hero is { } seat &&
            seat.NetId == hero.NetId && Controls(participant) &&
            SupportedChampions.Contains(participant.ChampionName));

    /// <summary>
    /// DIAGNOSTICS: tool-only seam. Retail runs the bot scripts only for bot seats. A headless
    /// match (<c>Amberline.Tools match</c>) seats ten connected non-bot participants so each has
    /// the same per-peer replication a human client gets, and the scripts drive all of them.
    /// </summary>
    internal bool Controls(SessionParticipant participant) =>
        (_controlsAllSeats || participant.IsBot) && !_scriptedSeats.Contains(participant.PlayerId);

    /// <summary>
    /// DIAGNOSTICS: tool-only seam (the scenario director, Amberline.Tools). Retail has no per-seat switch. A scripted seat is
    /// released from the bot scripts: <see cref="Controls"/> is false for it, so it issues no order, spends no skill point
    /// and counts for no other bot's lane spread, and whoever scripts it drives its hero through the ordinary client
    /// dispatcher. The set is empty in every shipped composition, where the call is never made, except the Basic Tutorial's: its ally
    /// leaves the bot script when the TutorialScript sends it at the enemy turret (batch 580, LevelScriptHost.DirectTutorialBots).
    /// </summary>
    public void SetSeatScripted(ulong playerId, bool scripted)
    {
        if (scripted) _scriptedSeats.Add(playerId);
        else _scriptedSeats.Remove(playerId);
    }

    /// <summary>Whether <see cref="SetSeatScripted"/> released this seat from the bot scripts.</summary>
    public bool IsSeatScripted(ulong playerId) => _scriptedSeats.Contains(playerId);

    /// <summary>How many seats <see cref="SetSeatScripted"/> has released; zero unless a tool asked.</summary>
    public int ScriptedSeatCount => _scriptedSeats.Count;

    public int ControlledBotCount =>
        _participants.Count(participant => Controls(participant) &&
            SupportedChampions.Contains(participant.ChampionName));

    /// <summary>Per-seat measurements for the headless match report (diagnostics only; null before the first update).</summary>
    public BotMeasure? MeasureOf(ulong playerId) =>
        _states.TryGetValue(playerId, out var state) ? state.Measure : null;

    /// <summary>Orders (move, attack, stop, cast, buy) this seat's controller has issued so far.</summary>
    public int OrdersIssued(ulong playerId) =>
        _states.TryGetValue(playerId, out var state) ? state.OrdersIssued : 0;

    /// <summary>Runs the Bot.luaobj cadence: init after 1 s, then a 250 ms task update.</summary>
    public void Tick(double dt)
    {
        if (!double.IsFinite(dt) || dt < 0.0)
            throw new ArgumentOutOfRangeException(nameof(dt));

        // A finished match (win or surrender) stops every bot order.
        if (_combat.GameOver) return;

        var now = _combat.CurrentTime;
        if (Quests is not null) RefreshDominion();
        for (var index = 0; index < _participants.Count; index++)
        {
            var participant = _participants[index];
            if (!Controls(participant) || participant.Hero is not { } hero ||
                !SupportedChampions.Contains(participant.ChampionName))
                continue;

            if (!_states.TryGetValue(participant.PlayerId, out var state))
            {
                state = new BotState { NextUpdateAt = now + BotScript.FirstUpdateSeconds };
                _states.Add(participant.PlayerId, state);
                _stateByHero[hero.NetId] = state;
            }

            if (!hero.Alive)
            {
                // CombatService owns respawn timing and the Entity remains the
                // same seat owner. Drop stale order/task state while dead.
                // Reconstruction: the scripts have no death handling (Bot.txt). Their Lua state (activeTask, the anti-kite latch,
                // BUY_INDEX) would survive a death, and the engine clears the unit's orders. Here OnDeath clears only the C# mirror of
                // the task; MirrorTasks re-reads the script's activeTask on the next step, so BeginTask does not run again and the bot
                // resumes its task after the respawn. Whether it should resume is the owner's call.
                if (state.WasAlive)
                {
                    ClearOrders(hero);
                    state.OnDeath();
                }
                continue;
            }

            if (!state.WasAlive)
            {
                // Respawn restores the same Entity at its fountain. Discard
                // any pre-death movement endpoint before restarting the timer.
                ClearOrders(hero);
                state.WasAlive = true;
                state.NextUpdateAt = now + BotScript.UpdateIntervalSeconds;
                continue;
            }

            // The shipped AI decides: its own Bot.luaobj instance per seat and its own timers.
            // The engine's 250 ms pass below only measures the seat and keeps the movement
            // plumbing alive.
            StepScript(hero, state, now, dt);
            if (now + Epsilon < state.NextUpdateAt) continue;
            var scheduled = state.NextUpdateAt + BotScript.UpdateIntervalSeconds;
            state.NextUpdateAt = scheduled > now + Epsilon
                ? scheduled : now + BotScript.UpdateIntervalSeconds;
            Update(hero, state, now);
        }
    }

    /// <summary>
    /// The engine's half of the script's 250 ms step: what the seat measured, and the movement
    /// guards the scripts have no equivalent for. The decision itself is the script's — its own
    /// <c>TimerUpdate</c> ran it on its own timer a moment earlier (<see cref="StepScript"/>) —
    /// so the C# task machine that used to sit here is gone.
    /// </summary>
    private void Update(Entity hero, BotState state, double now)
    {
        SampleQueuedCast(hero, state);
        Sample(hero, state, now);
        DropLostTarget(hero, state);
        ReleaseHeldMove(hero, state);
    }

    /// <summary>Diagnostics only (<see cref="BotMeasure"/>): what the seat did since the previous update.</summary>
    private void Sample(Entity hero, BotState state, double now)
    {
        var measure = state.Measure;
        if (double.IsNaN(measure.LastSampleAt)) { measure.LastSampleAt = now; measure.LastPosition = hero.Position; return; }
        var elapsed = now - measure.LastSampleAt;
        measure.LastSampleAt = now;
        measure.AliveSeconds += elapsed;
        if (state.Active is { } active)
        {
            measure.TaskSeconds[(int)active.Kind] += elapsed;
            if (active.Kind == BotTaskKind.PushLane) measure.PushLaneSeconds[active.Lane] += elapsed;
        }
        else measure.NoTaskSeconds += elapsed;
        var atFountain = DistanceSquared(hero.Position, Fountain(hero.Team)) <=
            CombatService.FountainHealRadius * CombatService.FountainHealRadius;
        if (atFountain) measure.FountainSeconds += elapsed;
        var moving = IsMoving(hero);
        if (!moving && hero.AttackTarget is null && !atFountain)
        {
            measure.IdleSeconds += elapsed;
            measure.IdleRun += elapsed;
            measure.LongestIdleRun = Math.Max(measure.LongestIdleRun, measure.IdleRun);
        }
        else measure.IdleRun = 0.0;
        if (moving && measure.LastPosition is Position2 last && DistanceSquared(last, hero.Position) < 100.0)
        {
            measure.StuckSeconds += elapsed;
            measure.StuckRun += elapsed;
            measure.LongestStuckRun = Math.Max(measure.LongestStuckRun, measure.StuckRun);
        }
        else measure.StuckRun = 0.0;
        measure.LastPosition = hero.Position;
        if (state.ReducePriority) measure.AntiKiteSeconds += elapsed;
    }

    /// <summary>Script state of one bot seat, for checks and diagnostics.</summary>
    internal BotState? StateOf(ulong playerId) =>
        _states.TryGetValue(playerId, out var state) ? state : null;

    private static bool IsStructure(Entity entity) =>
        entity.Kind is EntityKind.Turret or EntityKind.Inhibitor or EntityKind.Nexus;

    /// <summary>
    /// A minion's lane as the bot scripts' lane id. Bot.luaobj builds its Push Lane tasks from a literal <c>for lane = 0, 2</c>
    /// (Bot.txt proto 0.4, TimerHackDelayedInit, 0076-0095) on every map, and TaskPushLane asks <c>GetMinions(GetTeam(), LaneID)</c> (TaskPushLane.txt proto 0.0
    /// 0000-0004). <c>Reconstruction:</c> 0 L, 1 C, 2 R; the server's lane numbering is unrecovered, the centre is 1 either way.
    /// Batch 559 (TT-6): a lane the match map does not have (Map4's C) is no lane, so <c>GetMinions</c> answers it an empty list,
    /// FindNearestFriendlyMinion nil and UpdatePriority 0 (proto 0.1 0046-0049, 0076), and that Push Lane task never wins: the
    /// same "nothing" Map4's own LevelScript gets from <c>GetTurret</c> for CENTER_LANE in its RIGHT_LANE..LEFT_LANE loop
    /// (LEVELS/Map4/LevelScript.txt proto 0.5 0000-0016).
    /// </summary>
    private static int LaneIndex(string? lane) =>
        lane is null || !MapDefinitions.Lanes.Contains(lane) ? -1 : lane switch
        {
            "L" => 0,
            "C" => 1,
            "R" => 2,
            _ => -1,
        };

    // Reconstruction: the sight and targetability test stands in for the sensors Bot.dat lists (EYES, EARS, PROBE), which are native and not recovered. The task scripts test no sight and no targetability of their targets (see
    // the TaskKillHero target choice; the method name KillHeroPriority this comment named is not in src).
    private bool IsVisibleTarget(Entity hero, Entity target) =>
        WorldState.IsEnemy(hero, target) && _combat.IsTargetable(target, hero) &&
        _visible(hero, target);

    private bool AreAllBarracksStarted(Team team)
    {
        // Reconstruction: GetAreAllBarracksStarted(team) is a server native. Every barracks of the team is registered
        // in the world, which is true from the start of the match; a bot still needs a friendly lane minion to push.
        var inhibitors = MapDefinitions.Inhibitors;
        for (var index = 0; index < inhibitors.Count; index++)
            if (inhibitors[index].Team == team &&
                _world.Get(inhibitors[index].NetId) is not { Kind: EntityKind.Inhibitor })
                return false;
        return true;
    }

    // Reconstruction: GetMinionDistanceToLane is a server native; here the distance to the team's lane route polyline.
    private static double MinionDistanceToLane(Entity minion, IReadOnlyList<Position2> route)
    {
        if (route.Count == 0) return double.PositiveInfinity;
        if (route.Count == 1) return Math.Sqrt(DistanceSquared(route[0], minion.Position));
        var best = double.PositiveInfinity;
        for (var index = 1; index < route.Count; index++)
            best = Math.Min(best, DistanceSquaredToSegment(minion.Position,
                route[index - 1], route[index]));
        return Math.Sqrt(best);
    }

    private static double DistanceSquaredToSegment(Position2 point, Position2 start, Position2 end)
    {
        var dx = end.X - start.X;
        var dz = end.Z - start.Z;
        var lengthSquared = dx * dx + dz * dz;
        if (lengthSquared <= Epsilon) return DistanceSquared(point, start);
        var t = Math.Clamp(((point.X - start.X) * dx + (point.Z - start.Z) * dz) /
            lengthSquared, 0.0, 1.0);
        var nearest = new Position2(start.X + t * dx, start.Z + t * dz);
        return DistanceSquared(point, nearest);
    }

    // Reconstruction: GetRegroupPos. TaskRetreat walks there and heals near it,
    // TaskBuyItem shops within 500 units of it, so it is the point where healing
    // and the shop coincide: the team's fountain.
    // Batch 460: the match map's spawn point (MapDefinitions.TeamSpawn: Summoner's Rift's, or the Crystal Scar's for the Dominion bots),
    // read per call: the static fields it replaced kept Summoner's Rift's Order point on every map.
    private static Position2 Fountain(Team team) => MapDefinitions.TeamSpawn(team == Team.Order ? Team.Order : Team.Chaos);

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt(DistanceSquared(first, second));

    private static double DistanceSquared(Position2 first, Position2 second) =>
        (first.X - second.X) * (first.X - second.X) +
        (first.Z - second.Z) * (first.Z - second.Z);
}
