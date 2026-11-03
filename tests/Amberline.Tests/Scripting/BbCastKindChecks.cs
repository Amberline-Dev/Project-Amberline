using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbSlotChecks;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine, cast kinds (19 October 2026, second chunk): every <c>ttype_e</c> from 0 to 10 runs, <c>TargetOrLocation</c> is a unit or a ground
/// cast by the request, chain and spell-slot <c>BBSpellCast</c> children, the summoner spellbook swap of <c>BBSetSpell</c>, the character references of
/// the closure, and the line-missile rules the spell data holds. The probe harness of <see cref="BbSlotChecks"/> runs one-hook scripts through the
/// production router of a Soraka or Ryze match; the expected values come from the client's targeting classes and the shipped data.
/// </summary>
public static class BbCastKindChecks
{
    private static BbBlock SlotChild(string caster, int slot, double slotType, string? target) => B("BBSpellCast", ("CasterVar", new BbValue.Text(caster)),
        ("TargetVar", new BbValue.Text(target ?? "Nothing")), F("SlotNumber", slot), F("SlotType", slotType), F("OverrideForceLevel", 1),
        F("OverrideCoolDownCheck", true), F("FireWithoutCasting", true));

    // ------------------------------------------------------------------------------------------------------------
    // Targeting kinds
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-every-targeting-kind-from-self-to-offset-location-runs")]
    public static void EveryTtypeIsRunByTheModule()
    {
        using var probe = new Probe("Soraka");
        var match = probe.Match;
        // The client's factory builds a SpellTargeting for ttype_e 0-10: Self, Target, Area, Cone, SelfAOE, TargetOrLocation, Location, Direction (a
        // Location object), DragDirection (SpellTargetingDragLine), OffsetAOE, OffsetLocation (SpellTargetingOffsetLine). Soraka's W is overridden to each.
        for (var type = 0; type <= 10; type++)
        {
            probe.Run(null, SetTargeting(1, type));
            match.Refill();
            var target = type == 1 ? match.Ally : null;
            var verdict = match.CanCast(1, target);
            TestAssert.True(verdict.Allowed, $"ttype {type} is castable: {verdict.Reason}");
            var casts = match.Log.Casts.Count;
            TestAssert.True(match.Cast(1, target), $"ttype {type} is accepted");
            match.Advance(1.0);
            TestAssert.Equal(casts + 1, match.Log.Casts.Count, $"ttype {type} announces exactly one cast");
        }
        TestAssert.Equal(0, probe.Failures.Count, "no script failed");
    }

    [Check("scripting.bb-target-or-location-is-a-unit-cast-or-a-ground-cast-by-the-request")]
    public static void TargetOrLocationFollowsTheRequest()
    {
        using var probe = new Probe("Soraka");
        var match = probe.Match;
        probe.Run(null, SetTargeting(1, 5));
        // the target-or-location targeter keeps the clicked unit only when SpellData's valid-target check admits it, and always keeps the cursor.
        match.Refill();
        TestAssert.True(match.CanCast(1, match.Ally).Allowed, "an ally the spell's flags admit");
        var before = match.Log.Casts.Count;
        TestAssert.True(match.Cast(1, match.Ally), "the unit cast is accepted");
        TestAssert.Equal([match.Ally.NetId], match.Log.Casts[^1].Targets.Select(target => target.NetId).ToArray(), "and names its unit");
        match.Advance(1.0);
        match.Refill();
        TestAssert.True(match.CanCast(1, null).Allowed, "no unit: a ground cast at the position");
        TestAssert.True(match.Cast(1, null), "accepted");
        TestAssert.Equal(0, match.Log.Casts[^1].Targets.Count, "with no target in the announcement");
        match.Advance(1.0);
        match.Refill();
        TestAssert.True(match.CanCast(1, match.Foe).Allowed, "an enemy the flags refuse is no unit target: the cursor point is cast at");
        TestAssert.True(match.Cast(1, match.Foe), "accepted");
        TestAssert.Equal(0, match.Log.Casts[^1].Targets.Count, "as a ground cast");
        TestAssert.Equal(before + 3, match.Log.Casts.Count, "three casts in all");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Child casts
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-spell-cast-child-of-cast-type-2-sends-the-chain-and-hits-in-order")]
    public static void AChainChildAnnouncesOneUnitAndListsTheHops()
    {
        using var probe = new Probe("Ryze");
        var match = probe.Match;
        TestAssert.Equal(2, BbChampionModule.CastType(GameData.Default.Spell("SpellFlux", 5)), "Spell Flux is CastType 2");
        probe.Run(null, SetSpell("SpellFlux", 0, slotType: 2));
        var casts = match.Log.Casts.Count;
        var health = match.Foe.Health;
        probe.Run(match.Foe, Child(0, "Target"));
        TestAssert.Equal(casts + 1, match.Log.Casts.Count, "announced once");
        var cast = match.Log.Casts[^1];
        TestAssert.True(cast.Slot == 42 && cast.MissileNetId != 0u, "in extra slot 0 with a missile");
        TestAssert.Equal([match.Foe.NetId], cast.Targets.Select(target => target.NetId).ToArray(), "naming the first unit only (the client asserts one cast target)");
        match.Advance(0.5);
        TestAssert.True(match.Log.ChainSyncs.Count >= 1, "the hop list follows in the chain-sync packet");
        TestAssert.Equal(cast.MissileNetId, match.Log.ChainSyncs[0].Missile, "in the missile's name");
        TestAssert.Equal(match.Foe.NetId, match.Log.ChainSyncs[0].Targets[0], "the first hop is the unit the cast named");
        match.Advance(3.0);
        TestAssert.True(match.Foe.Health < health, "and the child's TargetExecute hit it");
        TestAssert.Equal(0, probe.Failures.Count, "no script failed");
    }

    [Check("scripting.bb-spell-cast-child-of-a-unit-spell-slot-is-announced-in-that-slot")]
    public static void AMinionCastsTheSpellInItsOwnSlot()
    {
        using var probe = new Probe("Soraka");
        var match = probe.Match;
        var dummy = BbM3.Enemy(match, "TestCube", new Position2(7300, 7100), UnitType.Minion, Team.Order);
        dummy.ActiveSpellNames.Add("Starcall");
        var casts = match.Log.Casts.Count;
        probe.Run(dummy, SlotChild("Target", 0, slotType: 0, target: null));
        TestAssert.Equal(casts + 1, match.Log.Casts.Count, "the minion's cast is announced");
        var cast = match.Log.Casts[^1];
        TestAssert.Equal(dummy.NetId, cast.CasterNetId, "by the minion");
        TestAssert.Equal(SpellSystem.ProtocolHash("Starcall"), cast.SpellHash, "with the spell its slot holds");
        TestAssert.Equal((byte)0, cast.Slot, "in spell slot 0, not an extra slot");
        // Inventory slots are only the disconnect trigger (CharOnDisconnect and DisconnectTimer cast slot 6). Batch 184: that cast is a no-op
        // now, not an unbound failure, and it announces no spell (it used to throw "not bound" before the disconnect hooks ran).
        var failures = probe.Failures.Count;
        var castsBefore = match.Log.Casts.Count;
        probe.Run(dummy, SlotChild("Target", 6, slotType: 1, target: null));
        TestAssert.Equal(failures, probe.Failures.Count, "an inventory slot cast is not a script failure");
        TestAssert.Equal(castsBefore, match.Log.Casts.Count, "and it announces no spell");
    }

    // ------------------------------------------------------------------------------------------------------------
    // The summoner spellbook, the closure, the line missile
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-set-spell-on-the-summoner-spellbook-swaps-teleport")]
    public static void TheSummonerBookIsTheHeroesTwoLoadoutSlots()
    {
        using var probe = new Probe("Soraka");
        var hero = probe.Hero;
        var original = hero.SummonerSpells[0];
        hero.SummonerCooldowns[0] = 30.0;
        probe.Run(null, SetSpell("TeleportCancel", 0, book: 1));
        TestAssert.Equal("TeleportCancel", hero.SummonerSpells[0], "the pseudo name the summoner code recognises");
        TestAssert.Near(0.0, hero.SummonerCooldowns[0], 1e-9, "SetSpellData resets the slot's cooldown");
        TestAssert.Equal([$"SendSpellName({hero.NetId:X8},TeleportCancel,0,True)"], probe.Calls.ToArray(), "0x35 with the summoner bit");
        probe.Run(null, SetSpell("summonerteleport", 0, book: 1));
        TestAssert.Equal("SummonerTeleport", hero.SummonerSpells[0], "a loadout spell is stored in its canonical spelling");
        TestAssert.Equal("summonerteleport", probe.Calls[^1].Split(',')[1], "and sent as the script wrote it");
        TestAssert.Equal(0, hero.ActiveSpellNames.Count(name => name.Length != 0), "the champion book is untouched");
        hero.SummonerSpells[0] = original;
        TestAssert.True(probe.Fails(null, B("BBSetTargetingType", Owner, F("SlotType", 0), F("SlotNumber", 0), F("SlotBook", 1), F("TargetType", 6)))
            .Contains("not bound", StringComparison.Ordinal), "BBSetTargetingType on that book stays unbound");
    }

    [Check("scripting.bb-closure-adds-the-spells-of-a-pushed-or-spawned-character")]
    public static void CharacterReferencesJoinTheClosure()
    {
        var resolver = new BbScriptResolver(GameData.Default.Paths);
        var nidalee = resolver.Resolve("Nidalee").ScriptNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var spell in new[] { "Takedown", "Pounce", "Swipe", "AspectOfTheCougar", "NidaleeTakedownAttack" })
            TestAssert.True(nidalee.Contains(spell), $"{spell} (Nidalee_Cougar's, pushed with OverrideSpells by AspectOfTheCougar) is in her closure");
        TestAssert.True(resolver.Resolve("Shaco").ScriptNames.Contains("ShacoBoxSpell", StringComparer.OrdinalIgnoreCase), "Shaco's closure holds the spell of the ShacoBox it spawns");
        TestAssert.Equal(16, resolver.Resolve("Soraka").ScriptNames.Count(), "a champion that spawns and pushes nothing keeps its closure");
        TestAssert.Equal(0, resolver.Resolve("Nidalee").Diagnostics.Count, "and the closure stays clean");
        TestAssert.Equal(0, resolver.Resolve("Nidalee").Dangling.Count, "with nothing dangling");
    }

    [Check("scripting.bb-line-missile-rules-come-from-the-shipped-data")]
    public static void EndsAtTargetPointIsSpellDataAndAFirstHitStopIsTheScripts()
    {
        int EndsAtTarget(string spell) => BbChampionModule.RawCount(GameData.Default.Spell(spell, 5), "SpellData*LineMissileEndsAtTargetPoint");
        foreach (var spell in new[] { "OrianaIzuna", "RumbleCarpetBombMissile", "GragasBarrelRollMissile", "LuxLightStrikeKugel", "OlafAxeThrow" })
            TestAssert.Equal(1, EndsAtTarget(spell), $"{spell} ends at its target point");
        foreach (var spell in new[] { "EzrealMysticShotMissile", "JavelinToss", "BlindMonkQOne", "SpiralBladeMissile", "RocketGrabMissile" })
            TestAssert.Equal(0, EndsAtTarget(spell), $"{spell} flies its whole range");
        // No spell data key says "stop at the first hit": the scripts that do call BBDestroyMissile from their TargetExecute.
        bool Destroys(string spell) => BbM3.Blocks(BbM3.Script(spell), "TargetExecute").Any(block => block.Function == "BBDestroyMissile");
        foreach (var spell in new[] { "JavelinToss", "BlindMonkQOne", "EzrealMysticShotMissile", "RocketGrabMissile", "LeblancSoulShackle" })
            TestAssert.True(Destroys(spell), $"{spell}'s TargetExecute destroys its missile");
        foreach (var spell in new[] { "OrianaIzuna", "EzrealTrueshotBarrage", "SpiralBladeMissile", "EzrealEssenceFluxMissile" })
            TestAssert.False(Destroys(spell), $"{spell}'s line goes on through its hits");
    }
}
