using System.Reflection;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbSlotChecks;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine, line rules (19 October 2026, third chunk): <c>LineMissileTrackUnits</c> and <c>LineMissileCollisionFromStartPoint</c> of
/// the slot-level line missile, and the cooldown rule of <c>BBSpellCast</c> with <c>OverrideCoolDownCheck=false</c>. The line checks start a
/// <see cref="BbLineMissile"/> by hand on the shipped spell data (no script-driven champion has a tracking line in its closure yet) with a probe
/// <c>TargetExecute</c> that deals 10 true damage, so a hit is a health change; the cooldown checks run <c>BBSpellCast</c> blocks through the probe
/// harness of <see cref="BbSlotChecks"/>. The expected values come from the client's <c>UpdateProjectile</c> and the shipped data.
/// </summary>
public static class BbLineRuleChecks
{
    private static readonly HashSet<string> Enemies = new(StringComparer.Ordinal) { "AffectEnemies", "AffectHeroes", "AffectMinions" };

    private static BbScript Hit() => new("LineProbe", "Spells/LineProbe.luaobj", [], [new BbEventTable("TargetExecute",
        [B("BBApplyDamage", F("AttackerVar", "Owner"), F("TargetVar", "Target"), F("Damage", 10), F("DamageType", (double)BbDamageType.TRUE_DAMAGE),
            F("SourceDamageType", (double)BbDamageSource.DAMAGESOURCE_INTERNALRAW), F("PercentOfAttack", 1))])], []);

    /// <summary>Starts a line of <paramref name="data"/> from the hero toward <paramref name="aim"/>, optionally tracking a unit, and returns its missile id.</summary>
    private static uint Start(Probe probe, SpellData data, Position2 aim, Entity? tracked)
    {
        var match = probe.Match;
        var hero = probe.Hero;
        var host = (BbSpellHost)typeof(BbChampionModule).GetProperty("Host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(match.Module)!;
        var missile = match.Runtime.World.Allocate(UnitType.Missile);
        var ground = new Position3(hero.Position.X, 0, hero.Position.Z);
        var info = new SpellCastInfo(SpellSystem.ProtocolHash(data.Name), hero.NetId, 1, hero.NetId, missile, ground, ground, [], 0f, 0f, 42, ground);
        var line = new BbLineMissile(match.Module.Forget, host, Hit(), data.Name, hero, 0, 1, info, 0.0, aim, data, Enemies, tracked);
        match.Runtime.Spells.EnqueueSharedProjectile(line);
        return missile;
    }

    private static SpellData Without(SpellData data, string key)
    {
        var hash = InibinReader.KeyHash(key);
        return data with { Raw = new InibinData(data.Raw.Keys.Where(name => name != hash).ToDictionary(name => name, name => data.Raw[name])) };
    }

    // ------------------------------------------------------------------------------------------------------------
    // The data flags
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-line-missile-track-and-start-point-flags-come-from-the-shipped-spells")]
    public static void TheFlagsAreSpellData()
    {
        int Flag(string spell, string key) => BbChampionModule.RawCount(GameData.Default.Spell(spell, 5), key);
        foreach (var spell in new[] { "OrianaRedact", "TalonRakeMissileTwo", "TalonShadowAssaultMisTwo", "CaitlynAceintheHoleMissile" })
        {
            TestAssert.Equal(3, BbChampionModule.CastType(GameData.Default.Spell(spell, 5)), $"{spell} is a line missile");
            TestAssert.Equal(1, Flag(spell, "SpellData*LineMissileTrackUnits"), $"{spell} tracks its unit");
        }
        foreach (var spell in new[] { "SpiralBladeMissile", "EzrealMysticShotMissile", "JavelinToss", "NocturneDuskbringer" })
            TestAssert.Equal(0, Flag(spell, "SpellData*LineMissileTrackUnits"), $"{spell} flies straight");
        TestAssert.Equal(1, Flag("NocturneDuskbringer", "SpellData*LineMissileCollisionFromStartPoint"), "Duskbringer collides from its start point");
        foreach (var spell in new[] { "OrianaRedact", "EzrealMysticShotMissile", "SpiralBladeMissile" })
            TestAssert.Equal(0, Flag(spell, "SpellData*LineMissileCollisionFromStartPoint"), $"{spell} does not");
        TestAssert.True(BbLineMissile.Tracks(GameData.Default.Spell("OrianaRedact", 5)), "Tracks reads the flag");
    }

    // ------------------------------------------------------------------------------------------------------------
    // LineMissileTrackUnits
    // ------------------------------------------------------------------------------------------------------------

    // Batch 187: the client's tracking branch (the line missile's update routine) has no time or distance cap; its
    // timer is written only once the line is done. A slow line that needs 20 s to reach a unit it never catches
    // flies until it arrives; the 15 s chase cap this engine used to add ended it on the way.
    [Check("scripting.bb-tracking-line-has-no-flight-cap")]
    public static void ATrackingLineFliesPastFifteenSecondsUntilItArrives()
    {
        var slow = GameData.Default.Spell("OrianaRedact", 5) with { MissileSpeed = 100.0 };
        using var probe = new Probe("Soraka");
        var match = probe.Match;
        var foe = match.Foe;
        foe.Position = new Position2(probe.Hero.Position.X + 2000.0, probe.Hero.Position.Z);
        var health = foe.Health;
        var missile = Start(probe, slow, foe.Position, tracked: foe);
        match.Advance(16.0);
        TestAssert.Equal(1, match.Runtime.Spells.SharedProjectiles.Count, "still in flight at 16 s");
        TestAssert.Equal(health, foe.Health, "not there yet");
        match.Advance(6.0);
        TestAssert.True(foe.Health < health, "it reached its unit at about 20 s and struck it");
        TestAssert.True(match.Log.DestroyedMissiles.Contains(missile), "and ended on arrival");
        TestAssert.Equal(0, match.Runtime.Spells.SharedProjectiles.Count, "nothing is left in the queue");
    }

    [Check("scripting.bb-tracking-line-chases-its-unit-and-ends-on-reaching-it")]
    public static void ATrackingLineFollowsItsUnitAndEndsOnReachingIt()
    {
        var redact = GameData.Default.Spell("OrianaRedact", 5);
        // Control: the same line with no unit to track flies where it was aimed and meets what stands there.
        using (var probe = new Probe("Soraka"))
        {
            var match = probe.Match;
            match.Foe.Position = new Position2(7000, 7600);
            var bystander = BbM3.Enemy(match, "Bystander", new Position2(8000, 7000));
            Start(probe, redact, new Position2(9000, 7000), tracked: null);
            match.Advance(1.0);
            TestAssert.True(bystander.Health < 2000, "an untracked line hits what is on its way east");
            TestAssert.Equal(5000.0, Math.Round(match.Foe.Health), "and not the hero off to the north");
        }
        // The client's destination is the unit's live position, re-read every update: the line turns toward it.
        using (var probe = new Probe("Soraka"))
        {
            var match = probe.Match;
            var foe = match.Foe;
            foe.Position = new Position2(7000, 7600);
            var bystander = BbM3.Enemy(match, "Bystander", new Position2(8000, 7000));
            var missile = Start(probe, redact, new Position2(9000, 7000), tracked: foe);
            match.Advance(0.1);
            var snapshot = match.Runtime.Spells.SharedProjectiles.Single().Snapshot;
            TestAssert.Near(7000.0, snapshot.Position.X, 1.0, "the first step already heads for the unit, not for the aim");
            TestAssert.True(snapshot.Position.Z > 7100.0, "north of the launch point");
            // The unit runs: the line follows its new position.
            foe.Position = new Position2(7500, 7700);
            var health = foe.Health;
            match.Advance(1.0);
            TestAssert.True(foe.Health < health, "the line arrived and struck its unit");
            TestAssert.Near(10.0, health - foe.Health, 3.0, "once (the hit set)");
            TestAssert.True(match.Log.DestroyedMissiles.Contains(missile), "and ended there: the arrival test is against the unit, not the 1500 range");
            TestAssert.Equal(0, match.Runtime.Spells.SharedProjectiles.Count, "nothing is left in the queue");
            TestAssert.Equal(2000.0, bystander.Health, "the straight line east would have met the bystander; the turned one did not");
        }
        // A unit that is gone ends the line (the target lookup, then the destroy), and nothing is struck.
        using (var probe = new Probe("Soraka"))
        {
            var match = probe.Match;
            var pawn = BbM3.Enemy(match, "Pawn", new Position2(7000, 8800));
            var missile = Start(probe, redact, new Position2(9000, 7000), tracked: pawn);
            match.Advance(0.2);
            TestAssert.True(match.Runtime.World.Remove(pawn.NetId), "the unit leaves the world");
            match.Advance(0.1);
            TestAssert.True(match.Log.DestroyedMissiles.Contains(missile), "the line is destroyed");
            TestAssert.Equal(0, match.Runtime.Spells.SharedProjectiles.Count, "and leaves the queue");
            TestAssert.Equal(2000.0, pawn.Health, "with no hit");
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // LineMissileCollisionFromStartPoint
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-line-missile-collision-from-start-point-strikes-a-unit-behind-the-head")]
    public static void TheContactSweepStartsAtTheLaunchPoint()
    {
        var duskbringer = GameData.Default.Spell("NocturneDuskbringer", 5);
        foreach (var (data, label, expectHit) in new[]
                 {
                     (duskbringer, "with the flag", true),
                     (Without(duskbringer, "SpellData*LineMissileCollisionFromStartPoint"), "without it", false),
                 })
        {
            using var probe = new Probe("Soraka");
            var match = probe.Match;
            var foe = match.Foe;
            foe.Position = new Position2(7000, 9000);
            Start(probe, data, new Position2(8200, 7000), tracked: null);
            match.Advance(0.2);
            // The head is about 280 units out; a unit that steps onto the line 230 units behind it is beyond the contact radius of the last step.
            foe.Position = new Position2(7050, 7000);
            var health = foe.Health;
            match.Advance(0.1);
            TestAssert.Equal(expectHit, foe.Health < health - 5.0, $"a unit that walks into the line behind the head is struck {label}");
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // OverrideCoolDownCheck=false
    // ------------------------------------------------------------------------------------------------------------

    internal static BbBlock CheckedChild(string caster, int slot, double slotType, bool overrideCheck = false) => B("BBSpellCast",
        ("CasterVar", new BbValue.Text(caster)), ("TargetVar", new BbValue.Text("Nothing")), F("SlotNumber", slot), F("SlotType", slotType),
        F("OverrideForceLevel", 1), F("OverrideCoolDownCheck", overrideCheck), F("FireWithoutCasting", true));

    [Check("scripting.bb-spell-cast-child-with-the-cooldown-check-on-is-refused-while-the-slot-cools-down")]
    public static void ACheckedChildStartsAndHonoursItsCooldown()
    {
        using var probe = new Probe("Soraka");
        var match = probe.Match;
        var hero = probe.Hero;
        var system = match.Runtime.Spells;
        probe.Run(null, SetSpell("Starcall", 0, slotType: 2));
        var cooldown = GameData.Default.Spell("Starcall", 5).Cooldowns[0] * (1.0 + hero.CooldownMod);
        TestAssert.True(cooldown > 1.0, "Starcall has a cooldown to start");
        var mana = hero.Mana;
        var casts = match.Log.Casts.Count;
        probe.Run(null, CheckedChild("Owner", 0, 2));
        TestAssert.Equal(casts + 1, match.Log.Casts.Count, "the first cast goes ahead");
        TestAssert.True(hero.ScriptSlotReadyAt.TryGetValue(42, out var readyAt), "and starts the extra slot's cooldown");
        TestAssert.Near(system.CurrentTime + cooldown, readyAt, 1e-6, "the child's own per-rank cooldown with the hero's reduction");
        TestAssert.Near(mana, hero.Mana, 1e-6, "no mana is charged");
        probe.Run(null, CheckedChild("Owner", 0, 2));
        TestAssert.Equal(casts + 1, match.Log.Casts.Count, "a second cast while the slot cools down is refused");
        match.Advance(cooldown + 0.1);
        probe.Run(null, CheckedChild("Owner", 0, 2));
        TestAssert.Equal(casts + 2, match.Log.Casts.Count, "and goes ahead when it is over");
        // Cooldowns disabled: no cooldown is kept.
        hero.ScriptSlotReadyAt.Clear();
        hero.CooldownsDisabled = true;
        probe.Run(null, CheckedChild("Owner", 0, 2));
        probe.Run(null, CheckedChild("Owner", 0, 2));
        TestAssert.Equal(casts + 4, match.Log.Casts.Count, "with the cooldowns disabled every cast goes ahead");
        TestAssert.False(hero.ScriptSlotReadyAt.ContainsKey(42), "and none is kept");
        hero.CooldownsDisabled = false;
        // OverrideCoolDownCheck=true ignores the cooldown and starts none.
        hero.ScriptSlotReadyAt[42] = system.CurrentTime + 100.0;
        probe.Run(null, CheckedChild("Owner", 0, 2, overrideCheck: true));
        TestAssert.Equal(casts + 5, match.Log.Casts.Count, "an override cast ignores the cooldown");
        TestAssert.Near(system.CurrentTime + 100.0, hero.ScriptSlotReadyAt[42], 1e-6, "and leaves it alone");
        // NOT RETAIL: a child cast from a champion spell's hook is not kept busy past that spell's own remaining cooldown (the probe runs as slot 0).
        hero.ScriptSlotReadyAt.Clear();
        hero.SpellCooldowns[0] = 2.0;
        probe.Run(null, CheckedChild("Owner", 0, 2));
        TestAssert.Near(system.CurrentTime + 2.0, hero.ScriptSlotReadyAt[42], 1e-6, "the child's cooldown is capped by its parent's remaining one");
        TestAssert.Equal(0, probe.Failures.Count, "no script failed");
    }

    [Check("scripting.bb-spell-cast-child-of-a-minions-own-slot-keeps-its-cooldown-on-the-minion")]
    public static void AMinionsCheckedChildKeepsItsCooldownInTheScriptStore()
    {
        using var probe = new Probe("Soraka");
        var match = probe.Match;
        var dummy = BbM3.Enemy(match, "TestCube", new Position2(7300, 7100), UnitType.Minion, Team.Order);
        dummy.ActiveSpellNames.Add("Starcall");
        var cooldown = GameData.Default.Spell("Starcall", 5).Cooldowns[0];
        var casts = match.Log.Casts.Count;
        probe.Run(dummy, CheckedChild("Target", 0, 0));
        TestAssert.Equal(casts + 1, match.Log.Casts.Count, "the minion's cast goes ahead");
        TestAssert.True(dummy.ScriptSlotReadyAt.TryGetValue(0, out var readyAt), "and starts its slot's cooldown");
        TestAssert.Near(match.Runtime.Spells.CurrentTime + cooldown, readyAt, 1e-6, "on the minion, where nothing ticks the list of a hero");
        probe.Run(dummy, CheckedChild("Target", 0, 0));
        TestAssert.Equal(casts + 1, match.Log.Casts.Count, "a second cast is refused");
        TestAssert.Near(0.0, probe.Hero.SpellCooldowns[0], 1e-9, "the hero's own slot is untouched");
        TestAssert.Equal(0, probe.Failures.Count, "no script failed");
    }
}
