using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// Which units a script-driven spell or area scan may affect. The <c>SPELL_Affect*</c> flags are the client's own
/// <c>SpellData*Flags</c> bits (<see cref="BbSpellFlags"/>), so a spell script's <c>Flags='...'</c>
/// string and the spell inibin's number name the same set; both reach this class as a set of flag names.
/// </summary>
internal static class BbSpellTargeting
{
    private const string Prefix = "SPELL_";

    /// <summary>The raw <c>SpellData*Flags</c> of a spell.</summary>
    public static BbSpellFlags Flags(SpellData spell) =>
        spell.Raw.TryGetValue(InibinReader.KeyHash("SpellData*Flags"), out var value) && value.IsNumeric
            ? (BbSpellFlags)(uint)(long)value.AsDouble() : 0;

    /// <summary>
    /// Batch 575: <c>AffectWards</c> (0x4000000). The client's flag-name table pairs the name with that bit (the name string, the
    /// pair the name pair in the table), but the Lua registration of the <c>SPELL_*</c> globals
    /// leaves it out, so it is not a <see cref="BbSpellFlags"/> member: only a spell inibin's number carries it (Jax's Leap Strike, Lee Sin's
    /// Safeguard, Teleport, the attack spells), and its text form (<c>... | AffectWards</c>) is stored beside it.
    /// </summary>
    internal const uint AffectWardsBit = 0x4000000;

    /// <summary>The flag names (<c>AffectEnemies</c>, <c>NotAffectSelf</c> ...) a flags value sets.</summary>
    public static HashSet<string> Names(BbSpellFlags flags)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        // By member name, not by value: UNITSCAN_* repeat the values of SPELL_AffectEnemies/Friends/Buildings/Dead.
        foreach (var name in Enum.GetNames<BbSpellFlags>())
        {
            var flag = Enum.Parse<BbSpellFlags>(name);
            if (name.StartsWith(Prefix, StringComparison.Ordinal) && (uint)flag != 0 && (flags & flag) == flag)
                names.Add(name[Prefix.Length..]);
        }
        if (((uint)flags & AffectWardsBit) != 0) names.Add("AffectWards");
        return names;
    }

    /// <summary>
    /// Team and kind filter of an area scan, applied relative to the attacker (the native scan, shared by the
    /// ForEachUnitInTargetArea family). Wards need an opt-in the scan flags do not carry, so they are never picked.
    /// </summary>
    public static bool Admits(Entity? attacker, Entity unit, HashSet<string> flags)
    {
        // NotAffectSelf (SpellData*Flags): the caster is no victim of its own spell. Ezreal's Essence Flux wave passes through allied heroes and
        // carries the flag (EzrealEssenceFluxMissile: AffectEnemies | AffectFriends | AffectHeroes | NotAffectSelf); the area scan skipped it already.
        if (ReferenceEquals(unit, attacker) && flags.Contains("NotAffectSelf")) return false;
        if (!ChampionSpellSupport.IsAreaEffectTarget(unit) && !(flags.Contains("AffectDead") && unit.UnitType != UnitType.Missile)) return false;
        if (!Targetable(attacker, unit, flags)) return false;
        if (!unit.Alive && !flags.Contains("AffectDead")) return false;
        var team = attacker is null ? unit.Team : attacker.Team;
        // Reconstruction (batch 174 part 2): a neutral caster's scan that names no team (CharScriptWorm's SweepTimer: AffectMinions AffectHeroes
        // NotAffectSelf) reaches every team. The sweep's own hit still takes only enemy heroes (SweepingBlow: AffectEnemies), so the scan
        // only picks the closest unit the Sweep lands on.
        // Batch 286 (G1, left open for the owner): the client's shared gate disagrees. the shared gate (the other build's
        // copy is the same) takes a unit of team 100/200 only with AffectEnemies, so by it the scan finds nobody and SweepTimer falls back to
        // its second cast (the Wrath). The wiki's Baron Nashor history keeps the knockback after V1.0.0.105 (16 November 2010, "he can no
        // longer knock you out of his arena") with no removal before the rework, and SweepingBlow is the Baron's only knock-away (BBMoveAway
        // from the Baron; Propel lifts a unit at a point beside it), so the retail server's scan reached heroes in a way the client's rule
        // does not show.
        var neutralAnyTeam = attacker is { Team: Team.Neutral } && !flags.Contains("AffectEnemies") &&
            !flags.Contains("AffectFriends") && !flags.Contains("AffectNeutral");
        return (neutralAnyTeam || TeamOk(team, unit, flags)) && KindOk(unit, flags);
    }

    /// <summary>
    /// Step 1 of the shared target gate : a unit that is not targetable (slot 4) or not targetable to the attacker's
    /// team (slot 5) is refused unless the flags carry <c>AffectUntargetable</c>. The gate also lets <c>AffectUseable</c> (0x8000000)
    /// through when slot 7 answers true. That slot is the useable test (the attackable-unit check in the symbol
    /// list): a unit whose character file has a <c>Useable</c> section (<c>UseableComponent</c>, read from
    /// <c>ChangeCharacterData</c>). Only Dominion's OdinCenterRelic and OdinNeutralGuardian ship that section (batch 292). Batch 470:
    /// both stand on map 8 now, and a hero's use of either starts its <c>HeroUseSpell</c> channel aimed at it directly
    /// (<c>ProductionGameplayDispatcher.UseObject</c>), with no target gate. Batch 471: the rescue applies now. The only shipped scans that
    /// carry <c>AffectUseable</c> are the quest scripts' (CharScriptOdinQuestIndicator, CharScriptOdinQuestBuff, OdinQuestParticleRemover),
    /// which find the capture point's guardian (encounter Targetable 0) by its OdinGuardianBuff. Batch 502: a unit-target spell meets the
    /// same gate, the spell's valid-target test runs the gate with the spell's own <c>SpellData*Flags</c> (the flags word), so
    /// SummonerOdinGarrison (AffectUseable) takes the guardian through slot 7.
    /// </summary>
    internal static bool Targetable(Entity? attacker, Entity unit, HashSet<string> flags) =>
        flags.Contains("AffectUntargetable") ||
        flags.Contains("AffectUseable") && IsUseable(unit) ||
        !unit.Protected && !unit.AnyBuffOrStatus(buff => buff.Payload.Untargetable) && !unit.NotTargetableTo(attacker);

    /// <summary>Slot 7, the useable test: the unit's character file sets <c>Useable*IsUseable</c> (read as the client's generic flag getter).</summary>
    private static bool IsUseable(Entity unit) =>
        GameData.Default.OptionalChampion(unit.ChampionName)?.Raw is { } raw &&
        raw.TryGetValue(InibinReader.KeyHash("Useable*IsUseable"), out var value) &&
        (value.Kind == InibinValueKind.Boolean ? value.Boolean
            : value.Kind == InibinValueKind.String ? InibinValue.AtofPrefix(value.AsString()) != 0.0
            : value.IsNumeric && value.AsDouble() != 0.0);

    /// <summary>
    /// The gate as the area queries run it on each unit of their shape. Batch 292 (G5): the query hands the gate only the units of the
    /// shape. In the multigrid query, <c>AlwaysSelf</c> (0x40000) only re-opens the attacker's own team grid, and the
    /// full-list fallback tests the same circle. So an attacker outside the shape is never picked. Inside it, the gate's
    /// targetable test (step 1) comes first; then <c>AlwaysSelf</c> admits the attacker whatever its team, life and kind (step 2, before
    /// <c>NotAffectSelf</c>). The query's own filter then refuses a unit whose <c>bDead</c> byte (the dead byte, the game object
    /// layout) is set unless the flags carry <c>AffectDead</c> (0x80000), so a dead attacker is not taken
    /// (batch 314). Its last test, the untargetable test (rescued by <c>AffectUntargetable</c>), ends in a type test, which answers 1 for any obj_AI_Base
    /// (the base-AI flag) and 3 for obj_Barracks (odd, so true), so an attacker passes it. Reconstruction: the component check before that
    /// (the object and its component) is not traced; the attacker has passed the targetable test already.
    /// </summary>
    private static bool AreaGate(Entity? attacker, Entity unit, HashSet<string> flags) =>
        ReferenceEquals(unit, attacker) && flags.Contains("AlwaysSelf")
            ? Targetable(attacker, unit, flags) && (unit.Alive || flags.Contains("AffectDead"))
            : Admits(attacker, unit, flags);

    /// <summary>
    /// The team step of the client's shared target gate (team 300 is the literal 0x12c): a
    /// unit on the attacker's own team needs <c>AffectFriends</c>, and also <c>AffectNeutral</c> when that team is neutral (two
    /// monsters of a camp); a neutral unit of another team needs <c>AffectNeutral</c>; any other unit needs <c>AffectEnemies</c>.
    /// </summary>
    private static bool TeamOk(Team attackerTeam, Entity unit, HashSet<string> flags) => unit.Team == attackerTeam
        ? flags.Contains("AffectFriends") && (attackerTeam != Team.Neutral || flags.Contains("AffectNeutral"))
        : unit.Team == Team.Neutral ? flags.Contains("AffectNeutral") : flags.Contains("AffectEnemies");

    /// <summary>
    /// The flag a unit's own type test asks for, slot 0 of the target gate's interface (step 5 of the gate): turret
    /// <c>AffectTurrets</c> for a turret, <c>AffectHeroes</c> for a hero, <c>AffectMinions</c> for a minion,
    /// except that a minion whose item source is a hero answers <c>AffectHeroes</c> Only
    /// the pet spawn sets that source: a pet that clones a hero with <c>CopyItems</c>, which
    /// every shipped <c>BBCloneUnitPet</c> leaves at its default true (Hallucinate, LeblancMIApplicator, MonkeyKingDecoy,
    /// MordekaiserCOTGRevive, YorickRARevive ...). So Shaco's clone, LeBlanc's image, Wukong's decoy, Mordekaiser's ghost and
    /// Yorick's revenant, the three kinds <c>SharedCloneService</c> makes, are champions to every spell flag; Tibbers clones
    /// nobody and stays a minion. <c>CombatRules.ClassOf</c> (the damage table by object class) is a different rule.
    /// </summary>
    internal static string? KindFlag(Entity unit) =>
        unit.Kind is EntityKind.ShacoClone or EntityKind.YorickRevenant or EntityKind.ClonePet ? "AffectHeroes" : unit.UnitType switch
        {
            UnitType.Hero => "AffectHeroes",
            UnitType.Minion or UnitType.Monster => "AffectMinions",
            UnitType.Turret => "AffectTurrets",
            UnitType.Structure => "AffectBuildings",
            _ => null,
        };

    /// <summary>The type test: the unit's <see cref="KindFlag"/> is set, and a minion also meets <c>AffectBarrackOnly</c> and the ward
    /// clause (the minion test's later clauses; a hero clone has answered before them).</summary>
    private static bool KindOk(Entity unit, HashSet<string> flags) =>
        KindFlag(unit) is { } kind && flags.Contains(kind) &&
        (kind != "AffectMinions" || BarrackOnlyOk(unit, flags) && WardOk(unit, flags));

    /// <summary>
    /// Batch 575: the ward clause of the client's minion type test: a minion whose
    /// <c>IsWard</c> byte is set (the ward flag, read by its getter; the 0x80 spawn handler sets it,
    /// the ward setter) answers 0 unless the flags carry <c>AffectWards</c> (0x4000000). The valid-target test runs
    /// the same gate with the spell's own flags, so Two-Shiv Poison (enemies, neutral, minions, heroes) cannot be cast at a ward,
    /// while Leap Strike, Safeguard and Teleport can.
    /// </summary>
    private static bool WardOk(Entity unit, HashSet<string> flags) => !unit.IsWard || flags.Contains("AffectWards");

    /// <summary>
    /// <c>SPELL_AffectBarrackOnly</c> (0x200000): the second half of the client's minion target filter. The query
    /// reads the candidate's <c>barrackSpawn</c> pointer and refuses a minion
    /// that has none, so a pet, a ward or a monster is minion-shaped and passes <c>AffectMinions</c> while
    /// no barracks ever spawned it. The barracks' own is the lane minion the wave scheduler creates with a
    /// <c>MinionType</c> (<c>EntityKind.Minion</c> of <c>UnitType.Minion</c>). Spells/TurretAssistManager.luaobj is
    /// the shipped caller (<c>Flags='AffectEnemies AffectMinions AffectBarrackOnly '</c>).
    /// </summary>
    private static bool BarrackOnlyOk(Entity unit, HashSet<string> flags) =>
        !flags.Contains("AffectBarrackOnly") || unit.Kind == EntityKind.Minion && unit.UnitType == UnitType.Minion;

    /// <summary>
    /// Whether a unit-target spell may be cast on <paramref name="target"/>: alive, and its team and kind are among the
    /// spell's flags. The caster itself is a friend (no <c>NotAffectSelf</c> on any of Soraka's friendly spells). Unlike an
    /// area scan this keeps wards for a spell whose flags carry <c>AffectWards</c> (<see cref="WardOk"/>).
    /// </summary>
    public static string? RejectTarget(Entity caster, Entity target, HashSet<string> flags)
    {
        if (!target.Alive) return "target is not alive";
        if (ReferenceEquals(target, caster) && flags.Contains("NotAffectSelf")) return "cannot target itself";
        if (!TeamOk(caster.Team, target, flags)) return target.Team == caster.Team ? "cannot target an ally" : "cannot target an enemy";
        if (KindOk(target, flags)) return null;
        return target.UnitType is UnitType.Turret or UnitType.Structure ? "cannot target a building" : "cannot target that unit";
    }

    /// <summary>
    /// Units of a rectangle (<c>BBForEachUnitInTargetRectangle</c>): the client's box-intersection test
    /// (the typed client's version) with the unit's collision radius <c>r</c>. The box has the half length <paramref name="halfLength"/> along
    /// <paramref name="forward"/> (a unit vector in the XZ plane) and the half width <paramref name="halfWidth"/> across it, centred on
    /// <paramref name="centre"/>; with <c>along</c> and <c>across</c> the distances of the unit's centre beyond the box on each axis, the unit is
    /// in when neither exceeds <c>r</c> and, past both edges, the corner distance does not either: the box rounded by the unit's radius.
    /// Units that pass <see cref="AreaGate"/> only, in world order (the client's list holds at most 128).
    /// </summary>
    public static List<Entity> Rectangle(SpellSystem system, Entity? attacker, Position2 centre, Position2 forward, double halfWidth,
        double halfLength, HashSet<string> flags)
    {
        var picked = new List<Entity>();
        foreach (var unit in system.World.Entities.Values)
        {
            if (!InsideBox(unit, centre, forward, halfWidth, halfLength) || !AreaGate(attacker, unit, flags)) continue;
            picked.Add(unit);
            if (picked.Count == 128) break;
        }
        return picked;
    }

    private static bool InsideBox(Entity unit, Position2 centre, Position2 forward, double halfWidth, double halfLength)
    {
        var dx = unit.Position.X - centre.X;
        var dz = unit.Position.Z - centre.Z;
        var radius = unit.CombatRadius;
        var along = Math.Abs(forward.X * dx + forward.Z * dz) - halfLength;
        var across = Math.Abs(forward.Z * dx - forward.X * dz) - halfWidth;
        if (radius < along || radius < across) return false;
        return !(along > 0.0 && across > 0.0 && radius * radius < along * along + across * across);
    }

    /// <summary>Units of an area (the client's circle, <see cref="InCircle"/>) that pass <see cref="AreaGate"/>, in world order.</summary>
    public static List<Entity> Area(SpellSystem system, Entity? attacker, Position2 centre, double range, HashSet<string> flags) =>
        system.World.Entities.Values.Where(unit => InCircle(unit, centre, range) && AreaGate(attacker, unit, flags)).ToList();

    /// <summary>
    /// The client's circle broad phase: the circle query (called from the area scans, under the multigrid query).
    /// The distance step takes the XZ distance squared from the centre, subtracts the unit's linear gameplay radius (the radius getter,
    /// <see cref="Entity.CombatRadius"/>), stores that as a float32 and clamps it at zero; the unit is in when the value does not exceed the
    /// squared range. A centre up to <c>sqrt(range^2 + radius)</c> away is in, not <c>range</c> away (the quirk).
    /// </summary>
    internal static bool InCircle(Entity unit, Position2 centre, double range)
    {
        var dx = unit.Position.X - centre.X;
        var dz = unit.Position.Z - centre.Z;
        var excess = (float)(dx * dx + dz * dz - unit.CombatRadius);
        var rangeSquared = (double)(float)range * (float)range;
        return excess <= rangeSquared;
    }
}

/// <summary>
/// A chain missile spell's <c>ChainMissileParameters</c> (Spell Flux: five hits per rank, the caster may be hit, the same unit may be
/// hit again but not twice in a row) and the chain it flies. The client's obj_SpellChainMissile follows the target list of the cast
/// (the cast routine hands the list to the missile), so the server decides the hops and sends them.
/// </summary>
internal sealed record BbChainParameters(IReadOnlyList<double> MaximumHits, bool CanHitCaster, bool CanHitSameTarget,
    bool CanHitSameTargetConsecutively)
{
    private const string BounceRadiusKey = "SpellData*BounceRadius";

    /// <summary>The script's <c>ChainMissileParameters</c> table, or null when it has none.</summary>
    public static BbChainParameters? Read(BbScript script)
    {
        if (script.Global("ChainMissileParameters") is not BbValue.Table table) return null;
        var hits = table.Get("MaximumHits") is BbValue.Table list
            ? list.Items.OfType<BbValue.Number>().Select(number => number.Value).ToArray() : [1.0];
        bool Flag(string key) => table.Get(key) is BbValue.Number { Value: not 0.0 } or BbValue.Bool { Value: true };
        return new BbChainParameters(hits.Length == 0 ? [1.0] : hits, Flag("CanHitCaster"), Flag("CanHitSameTarget"),
            Flag("CanHitSameTargetConsecutively"));
    }

    public int Hits(int rank) => (int)MaximumHits[Math.Clamp(rank - 1, 0, MaximumHits.Count - 1)];

    /// <summary>How far a hop may reach from the unit the missile is on: the spell's own <c>BounceRadius</c>.</summary>
    public static double BounceRadius(SpellData spell) =>
        spell.Raw.TryGetValue(InibinReader.KeyHash(BounceRadiusKey), out var value) && value.IsNumeric ? value.AsDouble() : 0.0;

    /// <summary>
    /// Reconstruction (M3): the stock server's hop choice is not in the shipped client (the client only follows the list it is
    /// given), so the rule is the one the parameters name. From the unit the missile is on, the next hop is the nearest unit the
    /// spell's flags admit within BounceRadius that the parameters allow (the unit just hit is skipped unless
    /// CanHitSameTargetConsecutively, any earlier hop unless CanHitSameTarget); the caster is the last resort and only
    /// when CanHitCaster. The chain ends when nothing qualifies or MaximumHits is reached.
    /// </summary>
    public IReadOnlyList<Entity> Build(SpellSystem system, Entity caster, Entity first, SpellData spell, int rank, HashSet<string> flags)
    {
        var chain = new List<Entity> { first };
        var limit = Hits(rank);
        while (chain.Count < limit)
        {
            var next = NextHop(system, caster, chain[^1].Position, chain, spell, flags);
            if (next is null) break;
            chain.Add(next);
        }
        return chain;
    }

    /// <summary>
    /// The hop after the unit the missile is on at <paramref name="from"/> (the last of <paramref name="chain"/>): the nearest living unit the
    /// spell's flags admit within BounceRadius that the parameters allow, the caster as the last resort when CanHitCaster, or null when there
    /// is none, which ends the chain. Batch 177 part 4 (the owner, 24 October 2026: "E keeps bouncing off a dead target"): a bounce goes only to
    /// a living unit, so a dead one is never chosen here; a corpse a missile was already flying to is the caller's rule.
    /// </summary>
    public Entity? NextHop(SpellSystem system, Entity caster, Position2 from, IReadOnlyList<Entity> chain, SpellData spell, HashSet<string> flags)
    {
        var current = chain.Count > 0 ? chain[^1] : null;
        var radius = BounceRadius(spell);
        bool Allowed(Entity unit) => (CanHitSameTargetConsecutively || !ReferenceEquals(unit, current)) &&
                                     (CanHitSameTarget || !chain.Contains(unit));
        var next = BbSpellTargeting.Area(system, caster, from, radius, flags)
            .Where(unit => !ReferenceEquals(unit, caster) && unit.Alive && Allowed(unit))
            .OrderBy(unit => BbSpellHost.Distance(unit.Position, from)).ThenBy(unit => unit.NetId).FirstOrDefault();
        if (next is null && CanHitCaster && caster.Alive && Allowed(caster) && BbSpellHost.Distance(caster.Position, from) <= radius)
            next = caster;
        return next;
    }
}
