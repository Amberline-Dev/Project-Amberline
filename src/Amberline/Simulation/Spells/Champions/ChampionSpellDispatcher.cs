using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Spells;

namespace Amberline.Simulation.Spells.Champions;

/// <summary>One explicit module per supported champion.</summary>
public interface IChampionSpellModule
{
    string ChampionName { get; }
    bool Handles(Entity hero);

    /// <summary>The champion script's CanCast gate for a slot (finding 32). A dispatcher without scripts keeps every slot open.</summary>
    bool ScriptCastGateOpen(Entity hero, int slot) => true;
    string SpellName(Entity hero, int slot);
    int MaxRank(Entity hero, int slot);
    int RankGate(Entity hero, int slot, int rank);
    /// <summary>
    /// The normal child spell an instacast slot spell casts (the parent script's <c>BBSpellCast</c> with <c>FireWithoutCasting</c>
    /// false: an extra-slot spell with its own cast time), or null. The client runs the parent at once and creates no SpellCaster
    /// for it, but the child's cast packet does create one, with the child's windup. A press of the slot therefore still winds up
    /// and is held like a non-instant spell while another windup runs (<see cref="SpellSystem.CreatesSpellCaster"/>).
    /// </summary>
    string? CasterChildSpell(Entity hero, int slot) => null;
    /// <summary>
    /// The reach the module's spell script asks for this cast (<c>AdjustCastInfo</c>), or null for the spell's own. The module's cast gate
    /// applies it; <see cref="SpellSystem"/> walks the hero to it.
    /// </summary>
    double? CastRange(Entity hero, int slot) => null;
    /// <summary><paramref name="end"/> is the request's end point (a cone's aim), null when the caller has none (a usability probe).</summary>
    SpellValidation Validate(SpellSystem system, Entity hero, int slot,
        uint targetNetId, Position3 position, double now, Position3? end = null);
    bool Execute(SpellSystem system, Entity hero, SpellCastRequest request, double now);
    void Tick(SpellSystem system, Entity hero, double dt);
    void TickSharedProjectilePhase(SpellSystem system, double dt) { }
    ChampionTickPhase TickPhase => ChampionTickPhase.Late;
    bool OwnsCooldown(Entity hero, int slot);
    void OnRankUp(SpellSystem system, Entity hero, int slot, int rank);
    void OnHeroDeath(SpellSystem system, Entity hero);
    int RetireTargetedFlights(SpellSystem system, Entity hero, uint targetNetId, TargetedFlightRetirement reason);
    ChampionDeathPhase DeathPhase => ChampionDeathPhase.BeforeControllerBuffs;
    void OnUnitDeath(SpellSystem system, Entity unit) { }
    void OnRecipientDeathCleanup(SpellSystem system, Entity unit) { }
    void OnBuffRemoved(SpellSystem system, Entity unit, BuffState buff) { }
}

/// <summary>
/// Match-owned composition of champion modules. Registration is explicit and
/// duplicate champion names are rejected at construction time.
/// </summary>
public sealed class ChampionSpellDispatcher : IChampionSpellDispatcher
{
    private readonly IReadOnlyList<IChampionSpellModule> _modules;

    public ChampionSpellDispatcher(IEnumerable<IChampionSpellModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var list = modules.ToArray();
        if (list.Length == 0) throw new ArgumentException("At least one champion module is required.", nameof(modules));
        if (list.GroupBy(module => module.ChampionName, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() != 1))
            throw new ArgumentException("Champion modules must have unique names.", nameof(modules));
        _modules = list;
    }

    public static ChampionSpellDispatcher CreateDefault(
        Func<Entity, Entity, bool> canSeeTarget,
        IEnumerable<IChampionSpellModule>? additionalModules = null)
    {
        var modules = new List<IChampionSpellModule>();
        if (additionalModules is not null) modules.AddRange(additionalModules);
        return new ChampionSpellDispatcher(modules);
    }

    public bool Handles(Entity hero) => Find(hero) is not null;
    public string SpellName(Entity hero, int slot) => FindRequired(hero).SpellName(hero, slot);
    public double? CastRange(Entity hero, int slot) => Find(hero)?.CastRange(hero, slot);
    public int MaxRank(Entity hero, int slot) => FindRequired(hero).MaxRank(hero, slot);
    public int RankGate(Entity hero, int slot, int rank) => FindRequired(hero).RankGate(hero, slot, rank);
    public SpellValidation Validate(SpellSystem system, Entity hero, int slot,
        uint targetNetId, Position3 position, double now, Position3? end = null) =>
        FindRequired(hero).Validate(system, hero, slot, targetNetId, position, now, end);
    public bool ScriptCastGateOpen(Entity hero, int slot) => Find(hero)?.ScriptCastGateOpen(hero, slot) ?? true;
    public bool Execute(SpellSystem system, Entity hero, SpellCastRequest request, double now) =>
        FindRequired(hero).Execute(system, hero, request, now);
    public void Tick(SpellSystem system, double dt)
    {
        TickLate(system, dt);
    }
    public void TickHero(SpellSystem system, Entity hero, double dt) => Find(hero)?.Tick(system, hero, dt);
    public void TickLate(SpellSystem system, double dt)
    {
        foreach (var hero in system.World.Entities.Values
                     .Where(entity => entity.UnitType == UnitType.Hero &&
                                      TickPhase(entity) == ChampionTickPhase.Late)
                     .ToArray())
            Find(hero)?.Tick(system, hero, dt);
    }
    public ChampionTickPhase TickPhase(Entity hero) => Find(hero)?.TickPhase ?? ChampionTickPhase.Late;
    public void OnRankUp(SpellSystem system, Entity hero, int slot, int rank) =>
        FindRequired(hero).OnRankUp(system, hero, slot, rank);
    public bool OwnsCooldown(Entity hero, int slot) => Find(hero)?.OwnsCooldown(hero, slot) ?? false;

    /// <summary>The champions that have a registered module (compared with the playable set of <c>ChampionCatalog</c>).</summary>
    public IEnumerable<string> RegisteredChampionNames => _modules.Select(module => module.ChampionName);

    public IChampionSpellModule? FindModule(string championName) => _modules.FirstOrDefault(module =>
        string.Equals(module.ChampionName, championName, StringComparison.OrdinalIgnoreCase));
    private IChampionSpellModule? Find(Entity hero) => FindModule(hero.ChampionName);
    public void TickSharedProjectilePhase(SpellSystem system, double dt)
    {
        foreach (var module in _modules) module.TickSharedProjectilePhase(system, dt);
    }
    public void OnUnitDeath(SpellSystem system, Entity unit)
    {
        foreach (var module in _modules) module.OnUnitDeath(system, unit);
    }
    public void OnHeroDeath(SpellSystem system, Entity unit, ChampionDeathPhase phase)
    {
        var module = Find(unit);
        // Existing death handlers require the canonical character name, including pets.
        if (module is not null && unit.ChampionName == module.ChampionName && module.DeathPhase == phase)
            module.OnHeroDeath(system, unit);
    }
    public void OnRecipientDeathCleanup(SpellSystem system, Entity unit)
    {
        foreach (var module in _modules) module.OnRecipientDeathCleanup(system, unit);
    }
    public int RetireTargetedFlights(SpellSystem system, Entity hero, uint targetNetId, TargetedFlightRetirement reason)
    {
        var removed = 0;
        foreach (var module in _modules)
            removed += module.RetireTargetedFlights(system, hero, targetNetId, reason);
        return removed;
    }
    public void OnBuffRemoved(SpellSystem system, Entity unit, BuffState buff)
    {
        foreach (var module in _modules) module.OnBuffRemoved(system, unit, buff);
    }
    private IChampionSpellModule FindRequired(Entity hero) => Find(hero) ??
        throw new InvalidOperationException($"No champion module registered for {hero.ChampionName}.");
}

internal static class ChampionSpellSupport
{
    public static bool CanAddControl(Entity caster, Entity target, string name) =>
        target.UnitType == UnitType.Hero && caster.Team != target.Team &&
        Inventory.HasItem(target.Inventory, 3117)
            ? BuffLifecycle.CanAddNamedBuffs(target, name, "BootsOfMobilityDebuff")
            : BuffLifecycle.CanAddNamedBuffs(target, name);

    // Late observers need the live missile, not a replay of its launch cast.
    // Line missiles retain their origin separately from the owner's current
    // position; homing missiles also sample the target's current destination.
    public static void RefreshMissile(SpellSystem system, Entity hero, SpellCastInfo? info,
        Position2 origin, Position2 position, Position2 end, double speed,
        bool homing = false, Position2? casterPosition = null,
        MissileCasterProjection? casterProjection = null, double launchHeight = 0.0)
    {
        if (info is null || info.MissileNetId == 0) return;
        var start3 = Ground(system, origin);
        var current3 = Ground(system, position);
        var end3 = Ground(system, end);
        var length = Distance(origin, end);
        var life = length <= 1e-9 ? 0.0f : (float)Math.Clamp(Distance(origin, position) / length, 0.0, 1.0);
        var direction = Direction(homing ? position : origin, end);
        // Batch 481b: a launch height (a unit's missile bone, see BbSpellProjectile.HeightAt) is the start's height, and the current
        // position is still that height over the ground point. Zero leaves every value as it was.
        var height = launchHeight * (1.0 - life);
        if (launchHeight != 0.0) start3 = new Position3(start3.X, start3.Y + launchHeight, start3.Z);
        if (height != 0.0) current3 = new Position3(current3.X, current3.Y + height, current3.Z);
        var velocity = new Position3(end3.X - current3.X, height == 0.0 ? 0.0 : -height, end3.Z - current3.Z);
        if (casterProjection is { } projection)
            system.Effects.RefreshProjectedMissile(projection, info, current3,
                direction, velocity, start3, end3, start3, start3,
                checked((float)speed), life);
        else
            system.Effects.RefreshMissile(hero, info, current3, direction, velocity,
                start3, end3, Ground(system, hero.Position), Ground(system, casterPosition ?? origin),
                checked((float)speed), life);
    }

    // These shipped buff assets explicitly call BBApplyAssistMarker. Keep
    // the list narrow so ordinary friendly stat buffs do not become assists.
    private static readonly HashSet<string> AssistMarkerBuffs =
    [
        "EyeOfTheStorm",
        "OrianaHaste", "OrianaQuickSlow", "OrianaRedactShield", "OrianaShred",
        "OrianaSlow", "OrianaStun", "PowerFistSlow",
        "Slow", "Stun", "Silence", "MoveAwayCollision",
        // The remaining global control scripts that call BBApplyAssistMarker on activation (Fear.luaobj, Taunt.luaobj,
        // Suppression.luaobj); Root, Sleep, Net, Charm and Blind do not.
        "Fear", "Taunt", "Suppression",
    ];

    public static double Distance(Position2 a, Position2 b)
    {
        var dx = a.X - b.X; var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    public static Position3 Ground(SpellSystem system, Position2 point) =>
        new(point.X, system.Movement.Navigation.HeightAt(point), point.Z);

    public static double RawNumber(SpellData data, string key, double fallback = 0.0) =>
        data.Raw.TryGetValue(InibinReader.KeyHash(key), out var value) && value.IsNumeric
            ? value.AsDouble() : fallback;

    // The native uses the unhalved LineWidth plus the target's resolved
    // gameplay radius. Circle queries and homing impact planes are separate.
    public static double LineWidth(SpellData data, double fallback) =>
        RawNumber(data, "SpellData*LineWidth", fallback);

    public static bool ScriptCollisionAllows(SpellSystem system, Entity caster, Entity unit) =>
        unit.Kind == EntityKind.Hero || !unit.AnyBuffOrStatus(buff => buff.Payload.Stealthed) ||
        system.CanSeeTarget(caster, unit);

    public static Position2 ClientPoint(SpellSystem system, Position2 point) =>
        Geometry.ClientWaypoints([point], new NavigationBounds(
            system.Movement.Navigation.MinX, system.Movement.Navigation.MaxX,
            system.Movement.Navigation.MinZ, system.Movement.Navigation.MaxZ))[0];

    public static Position3 Direction(Position2 from, Position2 to)
    {
        var dx = to.X - from.X; var dz = to.Z - from.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        return length <= 1e-9 ? new Position3(1.0, 0.0, 0.0) :
            new Position3(dx / length, 0.0, dz / length);
    }

    // The native requires the 0x04000000 ward opt-in in addition
    // to AffectMinions; these ordinary champion area effects do not set it.
    public static bool IsAreaEffectTarget(Entity entity) => entity.Alive && !entity.IsWard &&
        entity.UnitType is (UnitType.Hero or UnitType.Minion or UnitType.Monster);

    public static bool IsEnemyCombatTarget(Entity entity) => IsAreaEffectTarget(entity) && !entity.Protected &&
        !entity.AnyBuffOrStatus(buff => buff.Payload.Untargetable);

    public static ChampionCastContext Begin(
        SpellSystem system, Entity hero, SpellCastRequest request, double now,
        string spellName, bool interruptOrder = true) =>
        system.BeginChampionCast(hero, request.Slot, spellName,
            hero.SpellRanks[request.Slot], now, interruptOrder);

    // `mitigate` defaults to false here, unlike ISpellBuffService.Add (true): it must follow the script that adds the buff.
    // BBSpellBuffAdd carries its own CanMitigateDuration (false in every shipped champion script); the BBApplyStun/Silence/Fear
    // natives add the global buffs of Stun.luaobj/Silence.luaobj/Fear.luaobj as plain adds. The flag is carried, not read: tenacity is
    // Hardening.luaobj's BuffOnAllowAdd, which cuts every enemy control of its nine types (batch 296).
    public static BuffState? AddBuff(SpellSystem system, Entity target, string name,
        BuffType type, double duration, uint casterNetId, BuffPayload? payload = null,
        bool mitigate = false)
    {
        var buff = system.Buffs.Add(target, name, type, duration,
            casterNetId, payload, mitigate);
        if (buff is not null && AssistMarkerBuffs.Contains(name))
        {
            var source = system.World.Get(casterNetId);
            if (source is not null) system.Combat.RecordSupportAssist(source, target, 10.0);
        }
        return buff;
    }

    public static IEnumerable<Entity> CombatTargets(SpellSystem system, Entity hero) =>
        system.World.Entities.Values.Where(target =>
            IsEnemyCombatTarget(target) && WorldState.IsEnemy(hero, target));
}
