using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Items;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Simulation.Neutral;


/// <summary>
/// Every jungle monster's AI runs from <c>DATA/Scripts/Leashed.luaobj</c>: this service owns the
/// camp members' spawn bookkeeping and their baseline regeneration, and it is the neutral side of
/// the Lua AI host — one script instance per camp
/// member, stepped here, with the engine events (damage, call for help, taunt, fear) raised into
/// the script's callbacks. The leash, the return, the reset and its regen, the aggro and the call
/// for help between camp members are the script's decisions; <see cref="NeutralLuaAiEngine"/> is
/// the world half those decisions name (movement, orders, combat, camp state).
/// Movement, combat, buff expiry and wire output remain injected match services.
/// The Baron (Worm) rotates, recoups and grows from its character script CharScriptWorm (batch 174
/// part 2), so none of that is here: this service only keeps the Worm out of the baseline regeneration.
/// </summary>
public sealed class NeutralAiService
{
    private readonly WorldState _world;
    private readonly JungleScheduler _scheduler;
    private readonly MovementSystem _movement;
    private readonly CombatService _combat;
    private readonly INeutralPacketSink _wire;
    private readonly ISpellEffectSink _effects;
    private readonly ISpellBuffService _spellBuffs;
    private readonly GameData _gameData;
    private readonly IRandomSource _random;
    private readonly NeutralLuaAiEngine _jungle;
    private readonly LuaAiService _host;
    // Last baseline-regeneration HP replication per monster (throttled like minion regeneration).
    private readonly Dictionary<uint, double> _lastRegenSend = new();

    public NeutralAiService(
        WorldState world,
        JungleScheduler scheduler,
        MovementSystem movement,
        CombatService combat,
        INeutralPacketSink wire,
        ISpellEffectSink effects,
        ISpellBuffService spellBuffs,
        GameData? gameData = null,
        IRandomSource? random = null,
        Func<Entity, Entity, bool>? targetVisible = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _movement = movement ?? throw new ArgumentNullException(nameof(movement));
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _wire = wire ?? throw new ArgumentNullException(nameof(wire));
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
        _spellBuffs = spellBuffs ?? throw new ArgumentNullException(nameof(spellBuffs));
        _gameData = gameData ?? GameData.Default;
        _random = random ?? new SystemRandomSource();
        _jungle = new NeutralLuaAiEngine(world, movement, combat, wire, _random, targetVisible);
        _host = new LuaAiService(world, _jungle);
    }

    /// <summary>Where a Leashed.luaobj failure goes. The composition root points this at the session log.</summary>
    public Action<AiScriptFailure>? AiScriptError
    {
        get => _host.OnError;
        set
        {
            _host.OnError = value;
            _jungle.OnScriptError = value is null ? null : (_, message) =>
                value(new AiScriptFailure("Leashed", "Error", message));
        }
    }

    /// <summary>The jungle half of the natives, for the checks and the differential oracle.</summary>
    public NeutralLuaAiEngine Engine => _jungle;

    /// <summary>The Lua AI host every camp member's Leashed.luaobj instance lives in.</summary>
    public LuaAiService Host => _host;

    public void Tick(double dt)
    {
        if (!double.IsFinite(dt) || dt < 0.0)
            throw new ArgumentOutOfRangeException(nameof(dt));
        // The engine's clock is the script timers' clock (InitTimer/ResetAndStartTimer and the
        // host's per-step pass have to agree), so it advances first and everything below reads it.
        _jungle.Tick(dt);
        var now = _jungle.Now;

        // Forget the regeneration throttle once the world's corpse lifecycle has removed the actor.
        foreach (var netId in _lastRegenSend.Keys.Where(id => _world.Get(id) is null).ToArray())
            _lastRegenSend.Remove(netId);

        foreach (var member in _scheduler.Members.Values.ToArray())
        {
            var monster = member.Entity;
            if (!ReferenceEquals(_world.Get(monster.NetId), monster)) continue;
            if (!monster.Alive) continue;

            // Baseline regeneration is independent of Leashed's reset timer. The Worm's regeneration is its
            // character script's recoup (CharScriptWorm -> WormRecouperateOn -> WormRecouperate1), not this tick.
            if (monster.Name != "Worm" && monster.Health < monster.MaxHealth)
            {
                var health = monster.Health;
                monster.Health = Math.Min(monster.MaxHealth, monster.Health +
                    monster.HealthRegen * dt * _combat.HealingReceivedMultiplier(monster));
                // Minion regeneration replicates once per CombatService.GoldSendInterval (the 30 Hz stream
                // flooded the HP channel, F6); the value that reaches full health is always sent.
                if (monster.Created && monster.Health != health &&
                    (monster.Health >= monster.MaxHealth ||
                     !_lastRegenSend.TryGetValue(monster.NetId, out var lastSend) ||
                     now - lastSend >= CombatService.GoldSendInterval))
                {
                    _lastRegenSend[monster.NetId] = now;
                    _wire.ReplicationHp(monster);
                }
            }

            if (NeutralDefinitions.PlayerLevelHealth.ContainsKey(monster.Name))
            {
                // Batch 159 part 2: this is the remembered hero level only. The health growth it used to buy is
                // Spells/HPByPlayerLevel.luaobj's own now — the buff the AncientGolem's, LizardElder's and Dragon's
                // character scripts keep on them rescans the same 9999-unit area every 10 s while the holder is at 99%
                // health or better (BuffOnUpdateStats' BBExecutePeriodically) and adds HPPerLevel x MaxPlayerLevel.
                // Applying the level here as well doubled it. The field stays because the replay corpora record it.
                monster.NeutralScalingElapsed += dt;
                // Preserve the source's integer check boundary. The fixture
                // clock intentionally lands one floating-point ulp before ten
                // seconds; an epsilon advances scaling a frame early.
                if (monster.NeutralScalingElapsed >= NeutralDefinitions.PlayerLevelCheckSeconds)
                {
                    var checks = (int)(monster.NeutralScalingElapsed /
                        NeutralDefinitions.PlayerLevelCheckSeconds);
                    monster.NeutralScalingElapsed -=
                        checks * NeutralDefinitions.PlayerLevelCheckSeconds;
                    _scheduler.RestampPlayerLevel(member);
                }
            }

            // The chase re-path stand-in rides the retired machine's free-running 500 ms decision
            // clock: the grid keeps its phase through a suppressed step (a stun or a displacement
            // takes the frames' action but not the edge), so the next re-path lands where the old
            // accumulator would have fired it. The edge is computed here but acted on only for the
            // steps the monster is free to move.
            var decisionEdge = _jungle.AdvancePursuitClock(monster, dt);

            // Leashed.luaobj's own timers (TimerRetreat on its 0.5 s grid, TimerAttack every step,
            // TimerRegen and the fear/taunt pulses) are the whole decision path from here; the
            // re-path stand-in below is the one piece of chase behaviour the script leaves to the
            // native it replaces. The script's frame runs for every live member, suppressed steps
            // included: a stun or a displacement disables the actions its natives name (SendMove and
            // CloseToTarget answer with nothing while one is up, as minion AI does), not the host's
            // clock. The script never consults the stun — TimerRetreat's leash and arrival tests and
            // TimerAttack's range tests are its whole body — so stalling the pass only slips its
            // grids: neutral-ai-corpus stunned has the reference hold TimerRetreat's f = 4 mod 5
            // phase straight through a 1.75 s stun (its accumulator keeps running) and leash on 89,
            // where a frozen pass re-bases the grid onto 87.
            _host.StepUnit(monster, now, dt);

            // A forced displacement owns this frame's movement and action state; the script's own
            // path requests must not compete with it. Leashed' own fear wander is the exception:
            // TimerFeared keeps re-picking its point while the segment is running.
            if (monster.ForcedMove is { Effect: not "Fear" }) continue;

            if (NeutralLuaAiEngine.IsStunned(monster) && !NeutralLuaAiEngine.IsFeared(monster))
            {
                _jungle.Stop(monster, forceWire: true, reason: "neutral-stunned");
                continue;
            }

            _jungle.TickPursuit(monster, decisionEdge);
        }
    }

    /// <summary>
    /// A camp member was hit: the victim's <c>OnTakeDamage</c> and its camp-mates'
    /// <c>LeashedCallForHelp</c> are the script's two wake paths (they differ only in the
    /// retreating member's leash replacement). The Worm is not woken here: its engagement, its idle range and
    /// its damage are CharScriptWorm's CharOnPreDamage and ResistantSkin's BuffOnAllowAdd.
    /// </summary>
    public void OnTakeDamage(Entity monster, Entity attacker)
    {
        ArgumentNullException.ThrowIfNull(monster);
        ArgumentNullException.ThrowIfNull(attacker);
        if (monster.Kind != EntityKind.Neutral || !attacker.Alive ||
            !WorldState.IsEnemy(monster, attacker))
            return;

        // Reconstruction (batch 219): the Worm's wake on a hit is not in the shipped files. It wakes like the other camp members, so no
        // camp monster stays idle when hit (the fight-back check, neutral.every-member-fights-back-within-2s).

        // Leashed.luaobj groups by the live world's neutral/camp identity;
        // this callback is intentionally independent of the spawn scheduler
        // index so replayed or newly registered camp members also wake.
        foreach (var unit in _world.Entities.Values.Where(unit => unit.Kind == EntityKind.Neutral &&
                     unit.CampId == monster.CampId).ToArray())
        {
            if (!unit.Alive) continue;
            if (ReferenceEquals(unit, monster)) _host.OnTakeDamage(unit, attacker);
            else _host.OnCallForHelp(unit, attacker);
        }
    }

    /// <summary>
    /// Leashed.luaobj OnFearBegin: the camp member wanders around its leash point and goes INACTIVE.
    /// The wander itself is the script's (MakeWanderPoint + SetStateAndMove); this only records the
    /// anchor the fear owners all carry.
    /// </summary>
    public void OnFearBegin(Entity monster, BuffState fear)
    {
        ArgumentNullException.ThrowIfNull(monster);
        ArgumentNullException.ThrowIfNull(fear);
        if (!IsLiveNeutral(monster)) return;
        // Reconstruction: GetFearLeashPoint is native; the camp's home is its leash point. Its field has no
        // writer in the client but the constructors' zeroing (batch 310), so the server's rule is lost.
        fear.Payload.FearOrigin = _jungle.LeashPoint(monster);
        _host.OnFearBegin(monster);
    }

    /// <summary>Leashed.luaobj OnFearEnd: back to AI_ATTACK with roam state HOSTILE.</summary>
    public void OnFearEnd(Entity monster)
    {
        ArgumentNullException.ThrowIfNull(monster);
        if (!IsLiveNeutral(monster)) return;
        _host.OnFearEnd(monster);
    }

    /// <summary>Leashed.luaobj OnTauntBegin: the member closes on its taunter in AI_TAUNTED.</summary>
    public void OnTauntBegin(Entity monster)
    {
        ArgumentNullException.ThrowIfNull(monster);
        if (!IsLiveNeutral(monster)) return;
        _host.OnTauntBegin(monster);
    }

    /// <summary>Leashed.luaobj OnTauntEnd: another taunter, or back to AI_ATTACK.</summary>
    public void OnTauntEnd(Entity monster)
    {
        ArgumentNullException.ThrowIfNull(monster);
        if (!IsLiveNeutral(monster)) return;
        _host.OnTauntEnd(monster);
    }

    private bool IsLiveNeutral(Entity monster) => monster.Kind == EntityKind.Neutral && monster.Alive &&
        ReferenceEquals(_world.Get(monster.NetId), monster);
}
