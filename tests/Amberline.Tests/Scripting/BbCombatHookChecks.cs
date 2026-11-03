using Amberline.Core;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine, wave 3 (W31b, E07): <c>BuffOnBeingHit</c> / <c>CharOnBeingHit</c> (a basic attack lands on the holder), <c>CharOnPreDamage</c>,
/// <c>BuffOnDealDamage</c>, <c>CharOnMiss</c> / <c>CharOnDodge</c> and <c>CharOnNearbyDeath</c>. The probe scripts keep what the hooks read and change
/// <c>DamageAmount</c>; the events come from the production producers (<c>CombatService.ApplyHit</c>, <c>QueueBasicAttack</c>, <c>Kill</c>). The shipped users
/// are Tantrum, Defensive Ball Curl, Paragon of Demacia, Silent Killer, Pantheon's Aegis, Sion's Feel No Pain, Olaf and Malphite, Sona's Power Chord debuff,
/// Cho'Gath's Vorpal Spikes, Jax's Counter Strike, Alistar's Trample and Trundle's Decimate. Written, not run.
/// </summary>
public static class BbCombatHookChecks
{
    private static BbBlock Take(double amount) =>
        BbW3Rig.Block("BBMath", "Src1Var", "DamageAmount", "Src1Value", 0, "Src2Value", amount, "DestVar", "DamageAmount", "MathOp", BbTest.Ref("MO_SUBTRACT"));

    private static BbBlock Scale(double factor) =>
        BbW3Rig.Block("BBMath", "Src1Var", "DamageAmount", "Src1Value", 0, "Src2Value", factor, "DestVar", "DamageAmount", "MathOp", BbTest.Ref("MO_MULTIPLY"));

    private static BbScript BeingHitProbe() => BbW3Rig.Buff("W3BeingHitProbe",
        new BbEventTable("BuffOnBeingHit", [BbW3Rig.Keep("HitOwner", "Owner"), BbW3Rig.Keep("HitAttacker", "Attacker"), BbW3Rig.Keep("HitTarget", "Target"),
            BbW3Rig.Keep("HitResult", "HitResult"), BbW3Rig.Keep("HitAmount", "DamageAmount"), BbW3Rig.Count("Hits"), Take(10.0)]));

    private static BbScript CharacterProbe() => BbW3Rig.Character("W3CombatChar",
        new BbEventTable("CharOnBeingHit", [BbW3Rig.KeepChar("CharHitAttacker", "Attacker"), BbW3Rig.KeepChar("CharHitResult", "HitResult"), Take(5.0)]),
        new BbEventTable("CharOnPreDamage", [BbW3Rig.KeepChar("PreAttacker", "Attacker"), BbW3Rig.KeepChar("PreAmount", "DamageAmount"), Scale(0.5)]),
        new BbEventTable("CharOnMiss", [BbW3Rig.KeepChar("MissOwner", "Owner"), BbW3Rig.KeepChar("MissTarget", "Target"), BbW3Rig.KeepChar("MissResult", "HitResult")]),
        new BbEventTable("CharOnDodge", [BbW3Rig.KeepChar("DodgeOwner", "Owner"), BbW3Rig.KeepChar("DodgeAttacker", "Attacker")]),
        new BbEventTable("CharOnNearbyDeath", [BbW3Rig.KeepChar("NearOwner", "Owner"), BbW3Rig.KeepChar("NearTarget", "Target"), BbW3Rig.KeepChar("NearKiller", "Attacker")]));

    private static BbScript DealProbe() => BbW3Rig.Buff("W3DealProbe",
        new BbEventTable("BuffOnDealDamage", [BbW3Rig.Keep("DealOwner", "Owner"), BbW3Rig.Keep("DealTarget", "Target"), BbW3Rig.Keep("DealAmount", "DamageAmount"),
            BbTest.Block("BBIf", ["Src1Var", "DamageType", "Value2", BbTest.Ref("TRUE_DAMAGE"), "CompareOp", BbTest.Ref("CO_NOT_EQUAL")], Scale(0.8))]));

    private static double Hit(BbW3Rig rig, Entity attacker, Entity target, double amount, DamageType type = DamageType.Physical,
        DamageSourceKind kind = DamageSourceKind.Basic)
    {
        var before = target.Health;
        rig.Match.Runtime.Combat.ApplyHit(attacker.NetId, target.NetId, amount, type, kind);
        return before - target.Health;
    }

    private static void Plain(BbW3Rig rig)
    {
        foreach (var unit in new[] { rig.Match.Hero, rig.Match.Ally, rig.Match.Foe })
        {
            unit.Armor = 0.0;
            unit.SpellBlock = 0.0;
            unit.MaxHealth = unit.Health = 100000.0;
        }
    }

    [Check("scripting.bb-combat-being-hit-runs-for-a-landed-basic-attack-before-mitigation-and-the-scripts-change-the-hit")]
    public static void BeingHitChangesTheHit()
    {
        using var rig = new BbW3Rig(CharacterProbe(), BeingHitProbe());
        Plain(rig);
        var hero = rig.Match.Hero;
        var foe = rig.Match.Foe;
        var probe = rig.Give(hero, BeingHitProbe());
        var taken = Hit(rig, foe, hero, 100.0);
        rig.NoFailures();
        // The same character script halves what it is about to take (CharOnPreDamage runs for every hit, after the being-hit hooks and the armor).
        TestAssert.Near(42.5, taken, 1e-6, "100 less the character script's 5 and the buff's 10 (no armor), halved by its pre-damage hook");
        TestAssert.True(ReferenceEquals(probe.InstanceVars["HitOwner"], hero) && ReferenceEquals(probe.InstanceVars["HitAttacker"], foe) &&
                        ReferenceEquals(probe.InstanceVars["HitTarget"], hero), "Owner and Target are the victim, Attacker the unit that hit");
        TestAssert.Equal((double)BbHitResult.HIT_Normal, (double)probe.InstanceVars["HitResult"]!, "HitResult of a plain hit");
        TestAssert.Equal(95.0, (double)probe.InstanceVars["HitAmount"]!, "the buff reads the hit after the character script");
        TestAssert.True(ReferenceEquals(rig.CharVars(hero)["CharHitAttacker"], foe), "CharOnBeingHit ran with the same attacker");
        TestAssert.Near(50.0, Hit(rig, foe, hero, 100.0, DamageType.Magic, DamageSourceKind.Spell), 1e-6, "a spell hit is not a basic attack: no BeingHit (only the pre-damage halving)");
        TestAssert.Equal(1.0, (double)probe.InstanceVars["Hits"]!, "and the buff counted one hit");
    }

    [Check("scripting.bb-combat-pre-damage-runs-for-the-hero-after-its-buffs-and-changes-the-damage")]
    public static void CharPreDamage()
    {
        using var rig = new BbW3Rig(CharacterProbe());
        Plain(rig);
        var hero = rig.Match.Hero;
        var taken = Hit(rig, rig.Match.Foe, hero, 100.0, DamageType.Magic, DamageSourceKind.Spell);
        rig.NoFailures();
        TestAssert.Near(50.0, taken, 1e-6, "the character script halves the damage about to be taken");
        TestAssert.Equal(100.0, (double)rig.CharVars(hero)["PreAmount"]!, "it read the damage after mitigation");
        TestAssert.True(ReferenceEquals(rig.CharVars(hero)["PreAttacker"], rig.Match.Foe), "Attacker is the damage source");
    }

    [Check("scripting.bb-combat-deal-damage-changes-the-damage-the-holder-deals-for-any-unit-and-spares-true-damage")]
    public static void DealDamageChangesTheDamageDealt()
    {
        using var rig = new BbW3Rig(null, DealProbe());
        Plain(rig);
        var hero = rig.Match.Hero;
        var foe = rig.Match.Foe;
        var probe = rig.Give(hero, DealProbe());
        var minion = BbM3.Enemy(rig.Match, "W3Marked", new Position2(hero.Position.X + 150, hero.Position.Z), team: hero.Team);
        var marked = rig.Give(minion, DealProbe());
        TestAssert.Near(80.0, Hit(rig, hero, foe, 100.0, DamageType.Magic, DamageSourceKind.Spell), 1e-6, "a fifth less non-true damage");
        TestAssert.Near(100.0, Hit(rig, hero, foe, 100.0, DamageType.True, DamageSourceKind.Spell), 1e-6, "true damage is whole");
        TestAssert.Near(80.0, Hit(rig, minion, foe, 100.0), 1e-6, "a marked minion deals less too");
        rig.NoFailures();
        TestAssert.True(ReferenceEquals(probe.InstanceVars["DealOwner"], hero) && ReferenceEquals(probe.InstanceVars["DealTarget"], foe), "Owner is the dealer, Target the victim");
        TestAssert.True(ReferenceEquals(marked.InstanceVars["DealOwner"], minion), "the minion's own buff ran for its hit");
    }

    [Check("scripting.bb-combat-miss-and-dodge-hooks-run-for-the-avoided-swing-with-the-attacker-and-the-target")]
    public static void MissAndDodge()
    {
        using var rig = new BbW3Rig(CharacterProbe());
        Plain(rig);
        var hero = rig.Match.Hero;
        var foe = rig.Match.Foe;
        hero.Position = new Position2(foe.Position.X - 100, foe.Position.Z);
        // The hero swings and the foe dodges: CharOnMiss of the hero (its swing missed), with the unit it swung at; the foe is no Ryze, so no dodge hook.
        foe.Dodge = 0.5;
        rig.Match.Runtime.Combat.QueueBasicAttack(hero, foe, new AttackRolls(1.0, 0.0, 1.0));
        rig.Match.Advance(1.5);
        rig.NoFailures();
        TestAssert.True(ReferenceEquals(rig.CharVars(hero)["MissOwner"], hero) && ReferenceEquals(rig.CharVars(hero)["MissTarget"], foe), "CharOnMiss: Owner swung, Target was swung at");
        TestAssert.Equal((double)BbHitResult.HIT_Dodge, (double)rig.CharVars(hero)["MissResult"]!, "HitResult is HIT_Dodge for a dodged swing");
        TestAssert.True(rig.CharVars(hero)["DodgeOwner"] is null, "and no dodge hook for the hero, who dodged nothing");
        // The hero is swung at and dodges: CharOnDodge of the hero.
        hero.Dodge = 0.5;
        rig.Match.Runtime.Combat.QueueBasicAttack(foe, hero, new AttackRolls(1.0, 0.0, 1.0));
        rig.Match.Advance(1.5);
        rig.NoFailures();
        TestAssert.True(ReferenceEquals(rig.CharVars(hero)["DodgeOwner"], hero) && ReferenceEquals(rig.CharVars(hero)["DodgeAttacker"], foe),
            "CharOnDodge: Owner dodged, Attacker swung");
    }

    [Check("scripting.bb-combat-nearby-death-runs-for-the-heroes-within-the-experience-range-not-the-one-that-died")]
    public static void NearbyDeath()
    {
        using var rig = new BbW3Rig(CharacterProbe());
        var hero = rig.Match.Hero;
        var far = BbM3.Enemy(rig.Match, "W3Far", new Position2(hero.Position.X + 3000, hero.Position.Z));
        rig.Match.Runtime.Combat.Kill(far, rig.Match.Foe.NetId);
        TestAssert.True(rig.CharVars(hero)["NearOwner"] is null, "a death out of the experience range is not nearby");
        var near = BbM3.Enemy(rig.Match, "W3Near", new Position2(hero.Position.X + 300, hero.Position.Z));
        rig.Match.Runtime.Combat.Kill(near, rig.Match.Foe.NetId);
        rig.NoFailures();
        TestAssert.True(ReferenceEquals(rig.CharVars(hero)["NearOwner"], hero) && ReferenceEquals(rig.CharVars(hero)["NearTarget"], near), "Owner is the hero near, Target the unit that died");
        TestAssert.True(ReferenceEquals(rig.CharVars(hero)["NearKiller"], rig.Match.Foe), "Attacker is the unit that killed it");
    }

    [Check("scripting.bb-combat-the-hook-names-are-the-shipped-ones-of-the-wave-3-champions")]
    public static void TheShippedScriptsDefineTheHooks()
    {
        var resolver = new BbScriptResolver(Amberline.Data.GameData.Default.Paths);
        var found = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var champion in new[] { "Alistar", "Amumu", "Chogath", "Jax", "Malphite", "Olaf", "Poppy", "Rammus", "Sion", "Sona", "Trundle", "Twitch" })
            foreach (var script in resolver.Resolve(champion).Scripts)
                foreach (var table in script.Script.Events)
                    if (table.Hook is "BuffOnBeingHit" or "CharOnBeingHit" or "CharOnPreDamage" or "CharOnMiss" or "CharOnDodge" or "BuffOnDealDamage" or "CharOnNearbyDeath")
                    {
                        if (!found.TryGetValue(table.Hook, out var set)) found[table.Hook] = set = new SortedSet<string>(StringComparer.Ordinal);
                        set.Add(champion);
                    }
        TestAssert.True(found["CharOnNearbyDeath"].SetEquals(["Alistar", "Trundle"]), "CharOnNearbyDeath: " + string.Join(",", found["CharOnNearbyDeath"]));
        TestAssert.True(found["CharOnDodge"].SetEquals(["Jax"]), "CharOnDodge: Jax");
        TestAssert.True(found["CharOnMiss"].SetEquals(["Chogath"]), "CharOnMiss: Cho'Gath");
        TestAssert.True(found["BuffOnDealDamage"].Contains("Sona"), "BuffOnDealDamage: Sona's Power Chord debuff");
        TestAssert.True(found["BuffOnBeingHit"].IsSupersetOf(["Amumu", "Poppy", "Rammus", "Twitch"]), "BuffOnBeingHit: " + string.Join(",", found["BuffOnBeingHit"]));
        TestAssert.True(found["CharOnPreDamage"].IsSupersetOf(["Malphite", "Olaf"]), "CharOnPreDamage: Malphite and Olaf");
        TestAssert.True(found["CharOnBeingHit"].Contains("Sion"), "CharOnBeingHit: Sion");
    }
}
