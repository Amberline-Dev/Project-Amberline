using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Batch 660 (community reports of 3 November 2026: "turrets attack too fast", "Dominion minions attack WAY TOO FAST"): the swings a unit
/// makes per second in the production runtime, against the period its own character record gives through the stock client's delay helper
/// (docs/gameplay/patch126-attack-speed.md: clamp(1.6 x (1 + Data*AttackDelayOffsetPercent) / multiplier, 0.4, 5)). The expectations are
/// computed here from the shipped inibins, not from the production tables. A swing is the step its attack timer restarts; the 30 Hz step
/// rounds each interval up to whole steps.
/// </summary>
public static class AttackCadenceChecks
{
    private const double Step = BbM3.Step;

    private static double RecordPeriod(string character, double multiplier = 1.0)
    {
        var raw = GameData.Default.Champion(character).Raw;
        var offset = raw.TryGetValue(InibinReader.KeyHash("Data*AttackDelayOffsetPercent"), out var value) && value.IsNumeric
            ? value.AsClientNumber() : 0.0;
        return Math.Clamp(1.6 * (1.0 + offset) / multiplier, 0.4, 5.0);
    }

    private static double RecordCastFraction(string character)
    {
        var raw = GameData.Default.Champion(character).Raw;
        var offset = raw.TryGetValue(InibinReader.KeyHash("Data*AttackDelayCastOffsetPercent"), out var value) && value.IsNumeric
            ? value.AsClientNumber() : 0.0;
        return Math.Max(0.0, 0.3 + offset);
    }

    /// <summary>The mean interval between the swings <paramref name="attacker"/> starts while <paramref name="swing"/> is asked every step.</summary>
    private static double MeanInterval(BbM3.Match match, Entity attacker, double seconds, Func<bool>? swing = null)
    {
        var starts = new List<double>();
        var cooldown = attacker.AttackCooldown;
        for (var tick = 0; tick < (int)Math.Round(seconds / Step); tick++)
        {
            swing?.Invoke();
            match.Advance(Step);
            if (attacker.AttackCooldown > cooldown + 1e-9) starts.Add(match.Now);
            cooldown = attacker.AttackCooldown;
        }
        TestAssert.True(starts.Count >= 3, $"{attacker.Name} swings at least three times in {seconds} s (it swung {starts.Count} times)");
        return (starts[^1] - starts[0]) / (starts.Count - 1);
    }

    private static void AssertCadence(string label, double period, double interval)
    {
        // The step rounds a period up to whole steps: the measured interval is the period or at most one step more.
        TestAssert.True(interval >= period - 1e-6 && interval <= period + Step + 1e-6,
            $"{label}: one swing every {interval:0.0000} s, the record's period {period:0.0000} s ({1.0 / period:0.000} attacks per second)");
    }

    /// <summary>
    /// The Crystal Scar's wave units (SmallWave.json: Odin_*_Minion_Caster and Odin*Superminion) spawn through the encounter path
    /// (EncounterService, BbNativeHandlers.SpawnEncounterUnit), which gave them no attack timing: period 0 and windup 0, so they swung on
    /// every 30 Hz step (the report's "insta kill"). The stock CharacterData loader gives every character its record's timing, so a wave
    /// unit swings at its record's period and lands at its cast fraction of it.
    /// </summary>
    [Check("combat.dominion-wave-units-swing-at-their-record-period")]
    public static void DominionWaveUnitsSwingAtTheirRecordPeriod()
    {
        using var match = new BbM3.Match("Ryze");
        var unitScripts = new BbUnitCharacterModule(match.Runtime.Spells.Data);
        var hooks = (MatchGameplayHooks)match.Runtime.Dependencies.ChampionHooks;
        ((BbHookFanout)hooks.ScriptHooks!).Add(unitScripts.Bind(match.Runtime.Spells, _ => { }));
        foreach (var (skin, team) in new[]
                 {
                     ("Odin_Blue_Minion_Caster", Team.Order), ("Odin_Red_Minion_Caster", Team.Chaos),
                     ("OdinBlueSuperminion", Team.Order), ("OdinRedSuperminion", Team.Chaos),
                 })
        {
            var minion = unitScripts.SpawnEncounterUnit(skin, new Position2(7000, 7300), team, UnitType.Minion)!;
            var period = RecordPeriod(skin);
            TestAssert.Near(period, minion.AttackPeriod, 1e-9, skin + " period from its record");
            TestAssert.Near(period * RecordCastFraction(skin), minion.AttackWindup, 1e-9, skin + " windup from its record");
            var target = match.Runtime.World.Register(new Entity(match.Runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion,
                team == Team.Order ? Team.Chaos : Team.Order, new Position2(7060, 7300)) { Health = 1e7, MaxHealth = 1e7, Name = "Dummy" });
            AssertCadence(skin, period, MeanInterval(match, minion, 10.0, () => match.Runtime.Combat.QueueBasicAttack(minion, target)));
            minion.Alive = false;
            target.Alive = false;
        }
    }

    /// <summary>
    /// A Summoner's Rift outer turret, run by its shipped Turret.luaobj on a champion in its range: one swing per its record's 1.2 s
    /// (AttackDelayOffsetPercent -0.25) over 1.3, the +0.3 IncPermanentPercentAttackSpeedMod of PersonalTurretAssistBonus (added once,
    /// by TurretAssistManager from the turret's CharOnActivate): 0.923 s, 1.083 attacks per second. A lane melee minion swings at its
    /// record's 0.8 s. Both are what the shipped data gives; the check pins that the live cadence adds nothing to it.
    /// </summary>
    [Check("combat.summoners-rift-turret-and-minion-swing-at-their-record-period")]
    public static void SummonersRiftTurretAndMinionSwingAtTheirRecordPeriod()
    {
        using var match = new BbM3.Match("Ryze");
        var turret = match.Runtime.World.Entities.Values
            .Where(unit => unit.Team == Team.Chaos && unit.UnitType == UnitType.Turret &&
                           MapDefinitions.TurretTiers.TryGetValue(unit.Name, out var tier) && tier == TurretTier.Outer)
            .OrderBy(unit => unit.Name, StringComparer.Ordinal).First();
        var character = MapDefinitions.TurretCharacter(TurretTier.Outer, Team.Chaos);
        var hero = match.Hero;
        hero.MaxHealth = hero.Health = 1e7;
        // Toward the map's centre, inside the 750 attack range; the other heroes stay out of the way.
        var dx = 7000.0 - turret.Position.X;
        var dz = 7000.0 - turret.Position.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        hero.Position = new Position2(turret.Position.X + dx / length * 450.0, turret.Position.Z + dz / length * 450.0);
        foreach (var other in new[] { match.Ally, match.Foe, match.FarFoe })
            other.Position = other.Team == Team.Order ? new Position2(400, 400) : new Position2(13900, 14000);
        AssertCadence(turret.Name, RecordPeriod(character, 1.3), MeanInterval(match, turret, 12.0));

        var minion = match.Runtime.Waves!.SpawnFixture(Team.Order, MinionType.Melee, 0, "C", 0.0, 0.0);
        minion.Position = new Position2(7000, 7000);
        minion.Waypoints.Clear();
        var dummy = match.Runtime.World.Register(new Entity(match.Runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion,
            Team.Chaos, new Position2(7060, 7000)) { Health = 1e7, MaxHealth = 1e7, Name = "Dummy" });
        AssertCadence(minion.Name, RecordPeriod(minion.Name),
            MeanInterval(match, minion, 8.0, () => match.Runtime.Combat.QueueBasicAttack(minion, dummy)));
    }
}
