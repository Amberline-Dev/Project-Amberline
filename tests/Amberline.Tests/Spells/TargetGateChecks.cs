using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Batch 286: the client's shared target gate (TargetHelper code, so the server ran it too) and the
/// per-class type test it calls. A pet that clones a hero and copies its items answers the minion type test
/// with <c>AffectHeroes</c>: the pet spawn routine points
/// the pet's item source at the hero it clones, and every shipped <c>BBCloneUnitPet</c> keeps <c>CopyItems</c> at its default
/// true. Team rule: a unit on the attacker's own team needs <c>AffectFriends</c>, and also
/// <c>AffectNeutral</c> when that team is neutral.
/// </summary>
public static class TargetGateChecks
{
    private static HashSet<string> Set(params string[] names) => new(names, StringComparer.Ordinal);

    private static Entity Unit(uint netId, EntityKind kind, UnitType type, Team team) =>
        new(netId, kind, type, team, new Position2(1000, 1000)) { Name = kind.ToString(), Alive = true, Visible = true };

    [Check("spells.target-gate-hero-clones-are-champions")]
    public static void HeroClonesAreChampionsForSpellFlags()
    {
        var caster = Unit(1, EntityKind.Hero, UnitType.Hero, Team.Order);
        var heroes = Set("AffectEnemies", "AffectHeroes");
        var minions = Set("AffectEnemies", "AffectMinions");
        // Shaco's Hallucinate, Yorick's revenant, and LeBlanc's image, Wukong's decoy and Mordekaiser's ghost (ClonePet).
        foreach (var kind in new[] { EntityKind.ShacoClone, EntityKind.YorickRevenant, EntityKind.ClonePet })
        {
            var clone = Unit(10 + (uint)kind, kind, UnitType.Monster, Team.Chaos);
            TestAssert.True(BbSpellTargeting.Admits(caster, clone, heroes), $"{kind}: a champion-only area strikes the clone");
            TestAssert.False(BbSpellTargeting.Admits(caster, clone, minions), $"{kind}: a minion-only area misses the clone");
            TestAssert.Equal(null, BbSpellTargeting.RejectTarget(caster, clone, heroes), $"{kind}: a champion-only cast takes the clone");
            TestAssert.Equal("cannot target that unit", BbSpellTargeting.RejectTarget(caster, clone, minions),
                $"{kind}: a minion-only cast refuses the clone");
        }
        // Tibbers clones nobody (no CloneNetId), so he stays a minion.
        var tibbers = Unit(40, EntityKind.Pet, UnitType.Monster, Team.Chaos);
        TestAssert.True(BbSpellTargeting.Admits(caster, tibbers, minions), "Tibbers is a minion");
        TestAssert.False(BbSpellTargeting.Admits(caster, tibbers, heroes), "Tibbers is not a champion");
    }

    [Check("spells.target-gate-neutral-campmate-needs-friends-and-neutral")]
    public static void NeutralCampmateNeedsFriendsAndNeutral()
    {
        var worm = Unit(1, EntityKind.Neutral, UnitType.Monster, Team.Neutral);
        var campmate = Unit(2, EntityKind.Neutral, UnitType.Monster, Team.Neutral);
        TestAssert.False(BbSpellTargeting.Admits(worm, campmate, Set("AffectNeutral", "AffectMinions")),
            "a neutral attacker's AffectNeutral alone does not take a campmate (same team: AffectFriends first)");
        TestAssert.False(BbSpellTargeting.Admits(worm, campmate, Set("AffectFriends", "AffectMinions")),
            "nor AffectFriends alone (the team is neutral)");
        TestAssert.True(BbSpellTargeting.Admits(worm, campmate, Set("AffectFriends", "AffectNeutral", "AffectMinions")),
            "both flags take it");
        TestAssert.Equal("cannot target an ally", BbSpellTargeting.RejectTarget(worm, campmate, Set("AffectNeutral", "AffectMinions")),
            "the unit-target form is the same rule");
        // Another team's attacker reaches a neutral unit with AffectNeutral only.
        var hero = Unit(3, EntityKind.Hero, UnitType.Hero, Team.Order);
        TestAssert.True(BbSpellTargeting.Admits(hero, campmate, Set("AffectNeutral", "AffectMinions")), "a hero's AffectNeutral takes a monster");
        TestAssert.False(BbSpellTargeting.Admits(hero, campmate, Set("AffectEnemies", "AffectMinions")), "AffectEnemies does not");
    }

    /// <summary>
    /// Batch 575 (owner question 1 of 1 November 2026): the minion type test refuses a ward unless the flags carry <c>AffectWards</c>
    /// (0x4000000). The shipped flags decide: Two-Shiv Poison has no bit, Leap Strike
    /// and Safeguard have it. Red before the batch: the shared gate took a ward with any minion flag.
    /// </summary>
    [Check("spells.target-gate-shaco-two-shiv-refuses-a-ward-leap-strike-and-safeguard-take-one")]
    public static void AWardNeedsTheAffectWardsBit()
    {
        HashSet<string> Shipped(string spell) => BbSpellTargeting.Names(BbSpellTargeting.Flags(GameData.Default.Spell(spell)));
        var caster = Unit(1, EntityKind.Hero, UnitType.Hero, Team.Order);
        var enemyWard = Unit(2, EntityKind.Ward, UnitType.Monster, Team.Chaos);
        var ownWard = Unit(3, EntityKind.Ward, UnitType.Monster, Team.Order);
        enemyWard.IsWard = ownWard.IsWard = true;
        var minion = Unit(4, EntityKind.Minion, UnitType.Minion, Team.Chaos);
        var twoShiv = Shipped("TwoShivPoison");
        TestAssert.False(twoShiv.Contains("AffectWards"), $"TwoShivPoison.inibin flags [{string.Join(' ', twoShiv.Order())}]");
        TestAssert.Equal("cannot target that unit", BbSpellTargeting.RejectTarget(caster, enemyWard, twoShiv), "Two-Shiv Poison refuses an enemy ward");
        TestAssert.Equal(null, BbSpellTargeting.RejectTarget(caster, minion, twoShiv), "and takes an enemy minion");
        TestAssert.Equal(null, BbSpellTargeting.RejectTarget(caster, enemyWard, Shipped("LeapStrike")), "Leap Strike takes an enemy ward");
        TestAssert.Equal(null, BbSpellTargeting.RejectTarget(caster, ownWard, Shipped("BlindMonkWOne")), "Safeguard takes an allied ward");
        TestAssert.False(BbSpellTargeting.Admits(caster, enemyWard, Shipped("LeapStrike")), "an area scan still picks no ward");
    }

    [Check("spells.target-gate-always-self-takes-the-attacker-only-inside-the-shape")]
    public static void AlwaysSelfTakesTheAttackerOnlyInsideTheShape()
    {
        // Batch 292 (G5): the area queries hand the gate only the units of their shape (the multigrid query: AlwaysSelf only
        // re-opens the attacker's own team grid; the full-list fallback tests the same circle), and the gate's targetable test
        // (step 1) runs before its AlwaysSelf answer (step 2). Before batch 292 the attacker was added from anywhere, untargetable or not.
        using var match = new BbM3.Match("Sivir");
        var hero = match.Hero;
        var flags = Set("AffectEnemies", "AffectHeroes", "AlwaysSelf");
        var far = new Position2(hero.Position.X + 2000.0, hero.Position.Z);
        bool Picked(Position2 centre) => BbSpellTargeting.Area(match.Runtime.Spells, hero, centre, 300.0, flags).Contains(hero);
        TestAssert.True(Picked(hero.Position), "AlwaysSelf takes the attacker inside the circle, with no AffectFriends");
        TestAssert.False(Picked(far), "and not when it stands outside it");
        TestAssert.False(BbSpellTargeting.Rectangle(match.Runtime.Spells, hero, far, new Position2(1.0, 0.0), 100.0, 100.0, flags).Contains(hero),
            "nor outside a rectangle");
        hero.NotTargetableToAlly = true;
        TestAssert.False(Picked(hero.Position), "an attacker its own team may not target fails the targetable test first");
        flags.Add("AffectUntargetable");
        TestAssert.True(Picked(hero.Position), "unless the flags carry AffectUntargetable");
        // Batch 314: after the gate, the query's filter refuses a unit whose bDead byte is set
        // unless the flags carry AffectDead (0x80000), so a dead attacker is not its own AlwaysSelf victim.
        hero.NotTargetableToAlly = false;
        flags.Remove("AffectUntargetable");
        hero.Alive = false;
        TestAssert.False(Picked(hero.Position), "a dead attacker is not taken");
        flags.Add("AffectDead");
        TestAssert.True(Picked(hero.Position), "unless the flags carry AffectDead");
        hero.Alive = true;
    }
}

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Batch 575 (owner question 1): Shaco's Two-Shiv Poison reaches no enemy ward. The thrown E is refused by the spell's flags (no
    /// <c>AffectWards</c>), and the passive poison of a basic attack (<c>Backstab</c>'s <c>BuffOnHitUnit</c> adds <c>TwoShivPoison</c>, a
    /// <c>BUFF_Slow</c>) is refused by the ward's own <c>BuffOnAllowAdd</c> (SightWard.luaobj: a hostile Slow returns false). The enemy
    /// minion is the control for both.
    /// </summary>
    [Check("spells-live-shaco-two-shiv-neither-cast-nor-poison-reaches-an-enemy-ward")]
    public static void ShacoTwoShivPoisonReachesNoEnemyWard()
    {
        using var match = new BbM3.Match("Shaco", foeChampion: "Annie");
        match.Learn(1);
        match.Refill();
        var hero = match.Hero;
        Entity Add(EntityKind kind, UnitType type, string name, Position2 offset)
        {
            var unit = new Entity(match.Runtime.World.Allocate(type), kind, type, Team.Chaos, hero.Position + offset)
            {
                Name = name, Alive = true, Visible = true, Created = true, MaxHealth = 500, Health = 500,
                IsWard = kind == EntityKind.Ward, MinionType = kind == EntityKind.Minion ? MinionType.Melee : null,
            };
            match.Runtime.World.Register(unit);
            foreach (var seat in match.Runtime.Session.Participants)
            {
                seat.View.Known.Add(unit.NetId);
                seat.View.Visible.Add(unit.NetId);
            }
            return unit;
        }
        var ward = Add(EntityKind.Ward, UnitType.Monster, "SightWard", new Position2(200, 0));
        var minion = Add(EntityKind.Minion, UnitType.Minion, "Red_Minion_Basic", new Position2(0, 200));
        TestAssert.False(match.CanCast(2, ward).Allowed, "E at the enemy ward is refused: " + match.CanCast(2, ward).Reason);
        TestAssert.True(match.CanCast(2, minion).Allowed, "E at the enemy minion is allowed: " + match.CanCast(2, minion).Reason);
        // E is learned and ready, so every landed swing carries the poison (Backstab's BuffOnHitUnit).
        match.Module.Router!.RunHitUnit(hero, ward, 100.0);
        match.Module.Router!.RunHitUnit(hero, minion, 100.0);
        match.Advance(0.1);
        TestAssert.False(ward.Buffs.Any(buff => buff.Name == "TwoShivPoison"), "the ward refuses the hostile slow");
        TestAssert.True(minion.Buffs.Any(buff => buff.Name == "TwoShivPoison"), "the minion is poisoned");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    /// <summary>
    /// Batch 286: the unit-target summoners read their own shipped <c>SpellData*Flags</c> through the shared gate, where a hero
    /// clone is a champion. Smite (no AffectHeroes) refuses Shaco's clone; Exhaust (AffectHeroes) takes it. The enemy lane minion
    /// is the control: Smite takes it, Exhaust does not.
    /// </summary>
    [Check("spells-live-target-gate-summoners-take-a-clone-as-a-champion")]
    public static void SummonersTakeAHeroCloneAsAChampion()
    {
        var smite = BbSpellTargeting.Names(BbSpellTargeting.Flags(GameData.Default.Spell("SummonerSmite")));
        var exhaust = BbSpellTargeting.Names(BbSpellTargeting.Flags(GameData.Default.Spell("SummonerExhaust")));
        TestAssert.True(!smite.Contains("AffectHeroes") && exhaust.Contains("AffectHeroes"),
            $"shipped flags: Smite [{string.Join(' ', smite.Order())}], Exhaust [{string.Join(' ', exhaust.Order())}]");
        foreach (var (spell, kind, expected) in new[]
                 {
                     ("SummonerSmite", EntityKind.ShacoClone, false), ("SummonerSmite", EntityKind.Minion, true),
                     ("SummonerExhaust", EntityKind.ShacoClone, true), ("SummonerExhaust", EntityKind.Minion, false),
                 })
        {
            using var match = new Match("Annie", Team.Order);
            var hero = match.Hero(0);
            hero.SummonerSpells[0] = spell;
            hero.SummonerCooldowns[0] = 0.0;
            var type = kind == EntityKind.Minion ? UnitType.Minion : UnitType.Monster;
            var unit = new Entity(match.Runtime.World.Allocate(type), kind, type, Team.Chaos, hero.Position + new Position2(200, 0))
            {
                Name = kind == EntityKind.Minion ? "Red_Minion_Basic" : "Shaco", Alive = true, Visible = true,
                MaxHealth = 3000, Health = 3000, MinionType = kind == EntityKind.Minion ? MinionType.Melee : null,
            };
            match.Runtime.World.Register(unit);
            // The client names only a unit it was told about.
            foreach (var seat in match.Runtime.Session.Participants)
            {
                seat.View.Known.Add(unit.NetId);
                seat.View.Visible.Add(unit.NetId);
            }
            var accepted = match.TryCast(0, 0, unit, summoner: true);
            TestAssert.Equal(expected, accepted, $"{spell} at a {kind}: cast taken (cooldown {hero.SummonerCooldowns[0]}, " +
                $"health {unit.Health}, buffs [{string.Join(",", unit.Buffs.Select(buff => buff.Name))}])");
        }
    }
}
