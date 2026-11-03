using System.Reflection;
using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Batch 221 (25 October 2026): the spell-engine natives as retail. The circle query is the client's arithmetic (
/// the XZ distance squared minus the linear gameplay radius, float32, against the squared radius); the cone's candidates use the same circle.
/// </summary>
public static class BbSpellNativeChecks
{
    [Check("scripting.bb-circle-broad-phase-squared-distance-minus-radius")]
    public static void CircleAdmitsBySquaredDistanceMinusLinearRadius()
    {
        using var match = new BbM3.Match("Sivir");
        var flags = new HashSet<string> { "AffectEnemies", "AffectHeroes" };
        var centre = new Position2(7000.0, 7000.0);
        bool Admitted(double range, double distance, double unitRadius)
        {
            match.Foe.Position = new Position2(centre.X + distance, centre.Z);
            match.Foe.CombatRadius = unitRadius;
            return BbSpellTargeting.Area(match.Runtime.Spells, match.Hero, centre, range, flags).Contains(match.Foe);
        }
        // 100^2 + 65 = 10065: the centre may stand sqrt(10065) = 100.324 away, not only 100.
        TestAssert.True(Admitted(100.0, 100.30, 65.0), "a centre 0.30 past the range is in: the client subtracts the radius from the squared distance");
        TestAssert.True(!Admitted(100.0, 100.35, 65.0), "a centre 0.35 past it is out");
        // No radius: the boundary is the range itself, as before.
        TestAssert.True(Admitted(100.0, 100.0, 0.0), "a unit with no radius is in on the boundary");
        TestAssert.True(!Admitted(100.0, 100.01, 0.0), "and out just past it");
        // 625^2 + 65 = 390690: sqrt = 625.052, the boundary of the 625-unit areas.
        TestAssert.True(Admitted(625.0, 625.04, 65.0) && !Admitted(625.0, 625.07, 65.0), "the same at the 625 range");
        // 10^2 + 65 = 165: a small area reaches a centre 12.84 away, not 10.
        TestAssert.True(Admitted(10.0, 12.80, 65.0) && !Admitted(10.0, 12.90, 65.0), "a 10 range admits a centre up to 12.84 away");
        // A zero range admits only a unit whose centre lies inside its own radius (the excess clamps at zero).
        TestAssert.True(Admitted(0.0, 5.0, 65.0), "zero range still admits a unit inside its own radius");
        TestAssert.True(!Admitted(0.0, 8.2, 65.0), "and not one outside it (sqrt(65) = 8.06)");
    }

    [Check("scripting.bb-cone-candidates-use-the-client-circle")]
    public static void ConeCandidatesUseTheClientsCircle()
    {
        // Annie's W is the 625-unit cone: the candidate circle is the one of the areas, so the cone reaches the same 625.052.
        using var match = new BbM3.Match("Annie");
        var hero = match.Hero;
        var spell = match.Runtime.Spells.SpellDataOf(hero, 1);
        var origin = hero.Position;
        var direction = new Position2(1.0, 0.0);
        bool InCone(double distance)
        {
            match.Foe.Position = new Position2(origin.X + distance, origin.Z);
            match.Foe.CombatRadius = 65.0;
            return Cone.Units(match.Runtime.Spells, spell, origin, direction, hero).Contains(match.Foe);
        }
        TestAssert.True(InCone(625.04), "a centre 0.04 past the cone distance is a candidate (the client's circle, 625.052)");
        TestAssert.True(!InCone(625.07), "and 0.07 past it is not");
    }

    /// <summary>
    /// Batch 256: the cone's four planes (the two sides at the half angle, a front plane through the origin and a far plane at
    /// the cone distance) admit a unit while every plane's distance plus <c>0.5 * max(BBox.Size.x, BBox.Size.z) + 0.001</c> is positive.
    /// The box is the unit's bound-box recalculation: SkinScale times the mean of the skin's X and Z vertex extents, times
    /// the model scale. So a unit whose centre is outside a side plane, or behind the caster, by less than that half width is hit.
    /// </summary>
    [Check("scripting.bb-cone-planes-widen-by-the-units-bounding-box")]
    public static void ConePlanesWidenByTheUnitsBoundingBox()
    {
        // The Nexus' mesh gives its attack radius from the same extents ((586.1222 + 531.6111) / 4, SkinScale 1).
        TestAssert.Near(279.4333, GameData.Default.SkinHalfWidth("OrderNexus"), 1e-3, "the Nexus skin's half width is its mesh radius");
        using var match = new BbM3.Match("Annie");
        var hero = match.Hero;
        var foe = match.Foe;
        var spell = match.Runtime.Spells.SpellDataOf(hero, 1);
        var halfAngle = spell.Raw.TryGetValue(0x8a05f8b9, out var raw) && raw.IsNumeric ? raw.AsDouble() : 24.76;
        var widen = GameData.Default.SkinHalfWidth(foe.ChampionName, foe.SkinId) + 0.001;
        Console.WriteLine($"cone: foe {foe.ChampionName} skin {foe.SkinId} widen {widen:0.000}, half angle {halfAngle}");
        TestAssert.True(widen > 5.0, $"a champion's half width is tens of units ({widen})");
        var origin = hero.Position;
        var direction = new Position2(1.0, 0.0);
        var h = halfAngle * Math.PI / 180.0;
        bool At(Position2 offset)
        {
            foe.Position = new Position2(origin.X + offset.X, origin.Z + offset.Z);
            return Cone.Units(match.Runtime.Spells, spell, origin, direction, hero).Contains(foe);
        }
        // 300 along one side edge, then out across it (the edge's outward normal).
        Position2 Side(double outward) => new(300.0 * Math.Cos(h) - outward * Math.Sin(h), 300.0 * Math.Sin(h) + outward * Math.Cos(h));
        TestAssert.True(At(Side(widen - 0.5)), "a centre outside the side plane by less than the half width is hit");
        TestAssert.True(!At(Side(widen + 0.5)), "and one further out is not");
        TestAssert.True(At(new Position2(-(widen - 0.5), 0.0)), "a centre behind the caster by less than the half width is hit (front plane)");
        TestAssert.True(!At(new Position2(-(widen + 0.5), 0.0)), "and one further behind is not");
    }

    /// <summary>Counts the voice-override packets the natives ask the effect sink for (the sink is replaced for the check's duration).</summary>
    public class VoiceRecorder : DispatchProxy
    {
        public object Target { get; set; } = null!;
        public int VoiceCalls { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "SendVoiceOverride") VoiceCalls++;
            return targetMethod.Invoke(Target, args);
        }
    }

    [Check("scripting.bb-voice-override-sends-on-every-call")]
    public static void VoiceOverrideIsSentOnEveryCall()
    {
        // BBSetVoiceOverride sends ChangeCharacterVoice on every call; a call that repeats the suffix sends it again.
        using var match = new BbM3.Match("Sivir");
        var field = typeof(SpellSystem).GetField("_effects", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var proxy = DispatchProxy.Create<ISpellEffectSink, VoiceRecorder>();
        var recorder = (VoiceRecorder)(object)proxy;
        recorder.Target = field.GetValue(match.Runtime.Spells)!;
        field.SetValue(match.Runtime.Spells, proxy);
        var script = new BbScript("BatchVoice", "Spells/BatchVoice.luaobj", [],
            [new BbEventTable("TargetExecute", [BbTest.Block("BBSetVoiceOverride", ["OverrideSuffix", "Ult", "TargetVar", "Owner"])])], []);
        for (var call = 0; call < 2; call++)
        {
            var at = new Position3(0, 0, 0);
            match.Module.Router!.RunTargetExecute(script, match.Hero, 0, 1, 0.0, at, at, match.Foe);
        }
        TestAssert.Equal("Ult", match.Hero.VoiceOverride, "the suffix is set");
        TestAssert.Equal(2, recorder.VoiceCalls, "two calls, two packets: the second repeats the suffix and retail sends it again");
    }

    /// <summary>Counts the PAR colour packets the natives ask the effect sink for.</summary>
    public class ParColorRecorder : DispatchProxy
    {
        public object Target { get; set; } = null!;
        public int Calls { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "SendParColorOverride") Calls++;
            return targetMethod.Invoke(Target, args);
        }
    }

    [Check("scripting.bb-par-colour-override-sends-on-every-call")]
    public static void ParColourOverrideIsSentOnEveryCall()
    {
        // BBSetPARColorOverride and BBClearPARColorOverride send ChangePARColorOverride (0x99) on every call.
        // Renekton's Ruthless Predator sets and clears in one stats pass: two packets per applied sample. Before batch 241 the sample's net
        // result was sent only when it changed the colours (none here), and a repeated event call was skipped.
        static BbBlock Set() => BbTest.Block("BBSetPARColorOverride", ["SpellSlotOwnerVar", "Owner",
            "ColorR", 255, "ColorG", 0, "ColorB", 0, "ColorA", 255, "FadeR", 175, "FadeG", 0, "FadeB", 0, "FadeA", 255]);
        var buff = BbW3Rig.Buff("W241Par", new BbEventTable("BuffOnUpdateStats",
            [Set(), BbTest.Block("BBClearPARColorOverride", ["SpellSlotOwnerVar", "Owner"])]));
        using var rig = new BbW3Rig(null, buff);
        var field = typeof(SpellSystem).GetField("_effects", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var proxy = DispatchProxy.Create<ISpellEffectSink, ParColorRecorder>();
        var recorder = (ParColorRecorder)(object)proxy;
        recorder.Target = field.GetValue(rig.Match.Runtime.Spells)!;
        field.SetValue(rig.Match.Runtime.Spells, proxy);
        rig.Give(rig.Match.Hero, buff);
        rig.Match.Advance(0.6);
        rig.NoFailures();
        TestAssert.True(recorder.Calls >= 4 && recorder.Calls % 2 == 0, $"two packets per applied sample, a set and its clear: {recorder.Calls}");
        TestAssert.True(rig.Match.Hero.ParColorOverride is null, "the clear is the last word");

        var before = recorder.Calls;
        var script = new BbScript("W241ParEvent", "Spells/W241ParEvent.luaobj", [], [new BbEventTable("TargetExecute", [Set()])], []);
        for (var call = 0; call < 2; call++)
        {
            var at = new Position3(0, 0, 0);
            rig.Module.Router!.RunTargetExecute(script, rig.Match.Hero, 0, 1, 0.0, at, at, rig.Match.Foe);
        }
        TestAssert.Equal(2, recorder.Calls - before, "two event calls with the same colours, two packets");
    }

    [Check("scripting.bb-dodge-piercing-is-one-bit-only-a-script-clears")]
    public static void DodgePiercingIsOneBitOnlyAScriptClears()
    {
        // BBSetDodgePiercing sets bit 0x8000 of the unit's status word to Value; nothing but another call changes it. Before
        // batch 241 a hero's bit set from a buff hook lived in that buff's payload and went with it, and one set elsewhere was overwritten by
        // the next stat refresh.
        var buff = BbW3Rig.Buff("W241Dodge", new BbEventTable("OnBuffActivate",
            [BbTest.Block("BBSetDodgePiercing", ["TargetVar", "Owner", "Value", true])]));
        using var rig = new BbW3Rig(null, buff);
        var hero = rig.Match.Hero;
        var instance = rig.Give(hero, buff);
        rig.NoFailures();
        TestAssert.True(hero.DodgePiercing, "the activation sets the bit");
        rig.Module.Buffs!.RemoveAll(hero, "W241Dodge", null);
        rig.Match.Runtime.HeroStats.Refresh(hero);
        TestAssert.True(hero.DodgePiercing, "the buff ended without clearing it: the bit stays, a stat refresh included");
        var clear = new BbScript("W241DodgeClear", "Spells/W241DodgeClear.luaobj", [],
            [new BbEventTable("TargetExecute", [BbTest.Block("BBSetDodgePiercing", ["TargetVar", "Owner", "Value", false])])], []);
        var at = new Position3(0, 0, 0);
        rig.Module.Router!.RunTargetExecute(clear, hero, 0, 1, 0.0, at, at, rig.Match.Foe);
        rig.Match.Runtime.HeroStats.Refresh(hero);
        TestAssert.False(hero.DodgePiercing, "a script's clear is what takes it off");
    }
}
