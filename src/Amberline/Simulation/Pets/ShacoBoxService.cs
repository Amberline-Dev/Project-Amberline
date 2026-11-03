using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Spells;

namespace Amberline.Simulation.Pets;

/// <summary>
/// The unit behind Shaco's Jack in the Box and the walk of a feared unit. Phase 3 (W33): Shaco runs from his shipped scripts, and the box is
/// their <c>BBSpawnMinion Skin="ShacoBox" AiScript="turret.lua"</c>; the box's own <c>JackInTheBox</c> buff script does everything else (the
/// fade, the wake, the fear, the shots through its spell slot), so this service only creates the unit. The hand-written box AI is gone.
/// <para>
/// <see cref="Tick"/> keeps the one piece that is not Shaco's: the wander of a unit that carries a <c>Fear</c> buff with a fear origin (the
/// shared one-second walk of Pet.lua / minionLib's OnFearBegin, on a first-collision endpoint). Lane minions and camp members walk through their
/// own services, so it moves heroes and pets that <c>BBApplyFear</c> (the box's wake, later Fiddlesticks' and Nocturne's fears) feared.
/// </para>
/// </summary>
public sealed class ShacoBoxService
{
    private readonly WorldState _world;
    private readonly CombatService _combat;
    private readonly ISpellEffectSink _effects;
    private readonly IRandomSource _random;
    private readonly NavigationGrid _navigation;
    private readonly MovementSystem _movement;
    private readonly GameData _data;

    /// <summary>The fade id the stock script's second push (1.0 over 0.1 s) takes; each native push gets its own id.</summary>
    internal const short WakeFadeId = 21315;

    public ShacoBoxService(WorldState world, MovementSystem movement, CombatService combat,
        ISpellEffectSink effects, IRandomSource random, NavigationGrid navigation, GameData? data = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _movement = movement ?? throw new ArgumentNullException(nameof(movement));
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _data = data ?? GameData.Default;
    }

    /// <summary>
    /// The unit alone, for the BuildingBlocks <c>BBSpawnMinion Skin="ShacoBox"</c> (<c>JackInTheBoxInternal.OnBuffDeactivate</c>): the
    /// record's stats, registration, the spawn packets and the stats. The script adds the <c>JackInTheBox</c> buff (its fade, poof, wake and
    /// shots) itself; the unit is <see cref="EntityKind.ShacoBox"/> (700 sight, the stealth and shield rules).
    /// </summary>
    public Entity SpawnForScript(Entity owner, Position2 position, double now) => CreateUnit(owner, position, now);

    private Entity CreateUnit(Entity owner, Position2 position, double now)
    {
        var data = _data.Champion("ShacoBox");
        position = _navigation.ClosestSummonPlacement(position, data.GameplayRadius);
        var box = new Entity(_world.Allocate(UnitType.Monster), EntityKind.ShacoBox,
            UnitType.Monster, owner.Team, position)
        {
            Name = "ShacoBox",
            // The Python Entity constructor leaves champion_name at its
            // default for the synthetic box; the model name is carried by
            // the spawn packet/name field.
            ChampionName = "Annie",
            OwnerNetId = owner.NetId,
            Health = data.BaseHp,
            MaxHealth = data.BaseHp,
            Armor = data.Armor,
            SpellBlock = data.SpellBlock,
            Speed = 0.0,
            Radius = data.GameplayRadius,
            CombatRadius = data.CombatRadius,
            SelectionRadius = data.SelectionRadius,
            // ShacoBox.inibin Data*AttackRange (600); no script reads it, the box never attacks.
            AttackRange = data.AttackRange,
            ExpiresAt = now + 90.0,
            // ShacoBox CharacterData: 10 gold, 0 XP to its killer.
            GoldReward = data.GoldGivenOnDeath,
            ExperienceReward = data.ExpGivenOnDeath,
            Created = false,
            // The source entity remains locally hidden; EnterVisibility is a
            // packet sent to the peer and does not mutate this field.
            Visible = false,
            NodeId = 0x40,
        };
        _world.Register(box);
        _effects.SendMinionSpawn(box, "Jack In The Box", "ShacoBox", ignoreCollision: false);
        box.Created = true;
        _effects.SendInitialVisibility(box);
        _effects.SendStats(box);
        return box;
    }

    /// <summary>One pulse of the fear walk (see the class summary).</summary>
    public void Tick(double now, double dt) => TickFears(now);

    private void TickFears(double now)
    {
        foreach (var target in _world.Entities.Values.Where(entity => entity.Alive).ToArray())
        {
            if (target.UnitType == UnitType.Minion && target.MinionType is not null &&
                target.MinionState is MinionAiState.Feared or MinionAiState.Halted) continue;
            // TimerFeared of a camp member belongs to NeutralAiService (0.5 s, leash-point wander).
            if (target.Kind == EntityKind.Neutral) continue;
            // Hero.luaobj's own TimerFeared (1.0 s, re-armed by its OnFearBegin) wanders a
            // champion, so this shared pulse must not walk one as well. A spawned unit that only
            // looks like a champion keeps the pet script's wander below.
            if (target.UnitType == UnitType.Hero && target.PetState is null) continue;
            // Aggro.luaobj's own TimerFeared (1.0 s, re-armed by its OnFearBegin) wanders a
            // Nimbus clone, so this shared pulse must not walk one as well. A unit whose
            // Idle.luaobj answers OnFearBegin with nothing keeps the wander below.
            if (target.AiScript == SpawnedUnitAi.Aggro) continue;
            var fear = target.Buffs.FirstOrDefault(buff => buff.Name == "Fear" &&
                buff.Payload.FearOrigin is not null);
            if (fear is null || now < fear.Payload.NextWander) continue;
            if (BuffLifecycle.BlocksFearLocomotion(target)) continue;
            // A knockback or dash still owns its completed state until its
            // spell callback retires it (notably Condemn's wall hit). Keep
            // Fear pending until that owner releases the attached segment.
            if (target.ForcedMove is { } attachedMove && attachedMove.Effect != "Fear") continue;
            fear.Payload.NextWander = now + 1.0;
            var origin = fear.Payload.FearOrigin!.Value;
            var angle = _random.Uniform(0.0, Math.PI * 2.0);
            // Pet.lua FEAR_WANDER_DISTANCE is 500; YorickPHPet.lua (the ghouls) sets 400.
            var radius = _random.Uniform(0.0, target.Kind == EntityKind.YorickGhoul ? 400.0 : 500.0);
            var destination = new Position2(origin.X + Math.Cos(angle) * radius,
                origin.Z + Math.Sin(angle) * radius);
            // Publish the collision-safe endpoint actually followed by the
            // server. A truncated segment gets a new destination at the shared
            // fear timer's next one-second pulse, as in the shipped minionLib.
            var route = _movement.StartFearWander(target, destination, Math.Min(Math.Max(0.0, fear.ExpiresAt), 1.0),
                fear.CasterNetId);
            _effects.SendWaypoints(target, route, _world.Clock.SyncId());
        }
    }
}
