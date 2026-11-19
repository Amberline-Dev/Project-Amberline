using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Batch 906 (both lines): a timer of a whole number of 30 Hz steps holds its buff for exactly that many steps. The buff timer
    /// (<c>BuffLifecycle.TickBuffs</c>) counts a buff down by each step from the step it was added in, so the count reaches zero on the last
    /// step of its life; whether that step still held it depended on the rounding of the repeated 1/30 subtraction: a positive leftover kept
    /// it (0.1 to 1.1 s, 2.9 s and up), a zero or negative one dropped it a step early (1.2 to 2.8 s: 2.0 s held 59 steps, so a 2 s debuff
    /// re-added every 2 s, Siege Commander's, was missing for one step in sixty). Now the step's pass leaves a timer set in that step whole,
    /// and a count that ends within rounding of zero ends there. The buffs here are added inside a step, before the buff pass (a timed action
    /// of the spell system, <c>SpellSystem.RunAt</c>, as a cast frame or a hit adds them), and held for D x 30 steps counting the add step.
    /// </summary>
    [Check("buffs-live-b906-whole-step-timers-hold-their-whole-duration")]
    public static void B906WholeStepTimersHoldTheirWholeDuration()
    {
        using var match = new Match("Annie", Team.Order);
        var runtime = match.Runtime; var hero = match.Hero(0);
        match.Hero(1).Position = new Position2(3000, 11500);
        var durations = new[] { 0.5, 1.0, 1.5, 2.0, 2.5, 3.0, 5.0 };
        var held = durations.ToDictionary(duration => duration, _ => 0);
        var added = 0;
        runtime.Spells.RunAt(0.0, () =>
        {
            foreach (var duration in durations)
                if (runtime.Buffs.AddBuff(hero, $"B906Timer{duration * 10:0}", BuffType.Internal, duration, hero.NetId) is not null) added++;
        });
        for (var step = 0; step < 6 * 30; step++)
        {
            runtime.Tick(1.0 / 30.0);
            foreach (var duration in durations)
                if (hero.Buffs.Any(buff => buff.Name == $"B906Timer{duration * 10:0}")) held[duration]++;
        }
        TestAssert.Equal(durations.Length, added, "every timer buff was added in the first step");
        var wrong = durations.Where(duration => held[duration] != (int)Math.Round(duration * 30.0))
            .Select(duration => $"{duration} s held {held[duration]} steps ({Math.Round(duration * 30.0)} wanted)").ToList();
        TestAssert.True(wrong.Count == 0, "whole-step timers: " + string.Join("; ", wrong));
    }

    /// <summary>
    /// Batch 906 (both lines): Sona's Aria of Perseverance (W) puts SonaAriaofPerseveranceAura on her (CharScriptSona's self-update renews it
    /// while the song's marker stays), whose <c>BuffOnUpdateActions</c> runs <c>BBExecutePeriodically</c> 0.25 (ExecuteImmediately) and renews
    /// <c>SonaAriaofPerseveranceAuraB</c> (BUFF_RENEW_EXISTING, 0.25 s, +7 armour and magic resist at rank 1) on every allied champion within
    /// 1000. A 0.25 s period is 7.5 steps, so the renewals come 8 and 7 steps apart; the 0.25 s buff held 7 steps, and the ally lost the armour
    /// for one step on every 8-step gap (red run: "step 7: no aura, armour 20.50" against 27.50 around it). Links: the ally holds the aura on
    /// every step for 4 s after the first add (its armour is the aura's <c>BuffOnUpdateStats</c>; the W cast's own buffs move the total, so
    /// the step-to-step armour is reported, not compared).
    /// </summary>
    [Check("buffs-live-b906-sona-aria-aura-holds-the-allys-armour-on-every-step")]
    public static void B906SonaAriaHoldsEveryStep()
    {
        const double step = 1.0 / 30.0;
        using var match = new Match("Sona", Team.Order, ally: true, allyChampion: "Annie");
        var runtime = match.Runtime; var sona = match.Hero(0); var ally = match.Hero(2);
        match.Hero(1).Position = new Position2(3000, 11500);
        ally.Position = new Position2(7300, 7000);
        runtime.Tick(step);
        sona.SpellCooldowns[1] = 0.0;
        match.Cast(0, 1, sona);
        bool Aura() => ally.Buffs.Any(buff => buff.Name == "SonaAriaofPerseveranceAuraB");
        int? first = null;
        var missing = new List<string>();
        var previousArmor = ally.Armor;
        for (var index = 0; index < 5 * 30; index++)
        {
            runtime.Tick(step);
            if (Aura()) first ??= index;
            if (first is not null && index - first < 4 * 30 && !Aura() && missing.Count < 6)
                missing.Add($"step {index - first}: no aura, armour {ally.Armor:F2} (the step before {previousArmor:F2})");
            previousArmor = ally.Armor;
        }
        TestAssert.True(first is not null, "the ally never received SonaAriaofPerseveranceAuraB in 5 s");
        TestAssert.True(missing.Count == 0, "the aura lapsed after its first add: " + string.Join(", ", missing));
    }

    /// <summary>
    /// Batch 906 (both lines; a pin that did not change from the base): a buff hook re-adds a 1 s buff every second onto a unit whose buff pass
    /// comes before the adder's in the step. Spells/MordekaiserCreepingDeathSelf.luaobj (the same on both lines) runs <c>BBExecutePeriodically</c>
    /// 1 s and adds <c>MordekaiserCreepingDeathDebuff</c> (BUFF_REPLACE_EXISTING, 1 s) to the enemies within 350. The debuff is set after the
    /// victim's pass, so the next pass counts it; its 30th count ends at a positive leftover and the re-add in that step replaces a live debuff.
    /// A form of the batch that rounded every count to zero ended it in the victim's pass first, so every period ran a deactivation and a
    /// fresh activation (shown red on that build). Links: the victim holds the debuff on every step for 4 s, and each debuff the re-add
    /// replaces still had time left.
    /// </summary>
    [Check("buffs-live-b906-hook-re-add-onto-an-earlier-unit-replaces-a-live-buff")]
    public static void B906HookReAddOntoAnEarlierUnitReplacesALiveBuff()
    {
        const double step = 1.0 / 30.0;
        using var match = new Match("Annie", Team.Order);
        var runtime = match.Runtime; var victim = match.Hero(0); var adder = match.Hero(1);
        TestAssert.True(runtime.World.Entities.Values.ToList().IndexOf(victim) < runtime.World.Entities.Values.ToList().IndexOf(adder),
            "the victim's buff pass comes before the adder's");
        adder.Position = new Position2(victim.Position.X + 200.0, victim.Position.Z);
        victim.MaxHealth = victim.Health = 100000.0;
        var module = runtime.Dependencies.ChampionDispatcher.FindModule(adder.ChampionName) as Amberline.Simulation.Spells.Bb.BbChampionModule;
        var vars = new Amberline.Scripting.BuildingBlocks.Runtime.BbTable();
        vars["DamagePerTick"] = 1.0;
        TestAssert.True(module?.Buffs?.Add(new Amberline.Simulation.Spells.Bb.BbBuffRequest(adder, adder, "MordekaiserCreepingDeathSelf",
            (int)Amberline.Scripting.BuildingBlocks.Runtime.BbBuffAddType.BUFF_REPLACE_EXISTING,
            (int)Amberline.Scripting.BuildingBlocks.Runtime.BbBuffType.BUFF_Internal, 1, 30.0, 0.0, false, vars)) is not null,
            "MordekaiserCreepingDeathSelf is placed on the adder");
        BuffState? Debuff() => victim.Buffs.FirstOrDefault(buff => buff.Name == "MordekaiserCreepingDeathDebuff");
        int? first = null;
        BuffState? previous = null;
        var problems = new List<string>();
        for (var index = 0; index < 6 * 30; index++)
        {
            runtime.Tick(step);
            var now = Debuff();
            if (now is not null) first ??= index;
            if (first is null || index - first >= 4 * 30) { previous = now; continue; }
            if (now is null && problems.Count < 6) problems.Add($"step {index - first}: no debuff");
            if (previous is not null && now is not null && !ReferenceEquals(previous, now) && previous.ExpiresAt <= 0.0 && problems.Count < 6)
                problems.Add($"step {index - first}: the re-add replaced a debuff that had run out ({previous.ExpiresAt:G3} left)");
            previous = now;
        }
        TestAssert.True(first is not null, "the victim never received MordekaiserCreepingDeathDebuff in 6 s");
        TestAssert.True(problems.Count == 0, "creeping death re-add: " + string.Join("; ", problems));
    }

    /// <summary>
    /// Batch 906 (both lines): a buff's own periodic due inside the last step of its life runs. MordekaiserCreepingDeathSelf (the same on
    /// both lines) is placed for 1.02 s inside a step (a timed action of the spell system, before the buff pass); its
    /// <c>BBExecutePeriodically</c> 1 s (not immediate) is due at 1.0 s, inside the life. Before the batch the add step also counted the timer,
    /// so the buff's end read 0.987 s (<c>LifeEndsAt</c>) and the due was refused as past the end: no debuff ever. Now the end reads 1.02 s and
    /// the victim takes one debuff at 1.0 s, and none after. A due exactly at the end stays refused (Ignite's pins).
    /// </summary>
    [Check("buffs-live-b906-a-periodic-due-in-the-last-step-of-a-buff-runs")]
    public static void B906PeriodicDueInTheLastStepRuns()
    {
        const double step = 1.0 / 30.0;
        using var match = new Match("Annie", Team.Order);
        var runtime = match.Runtime; var victim = match.Hero(0); var adder = match.Hero(1);
        adder.Position = new Position2(victim.Position.X + 200.0, victim.Position.Z);
        victim.MaxHealth = victim.Health = 100000.0;
        var module = runtime.Dependencies.ChampionDispatcher.FindModule(adder.ChampionName) as Amberline.Simulation.Spells.Bb.BbChampionModule;
        var vars = new Amberline.Scripting.BuildingBlocks.Runtime.BbTable();
        vars["DamagePerTick"] = 1.0;
        object? placed = null;
        runtime.Spells.RunAt(0.0, () => placed = module?.Buffs?.Add(new Amberline.Simulation.Spells.Bb.BbBuffRequest(adder, adder,
            "MordekaiserCreepingDeathSelf", (int)Amberline.Scripting.BuildingBlocks.Runtime.BbBuffAddType.BUFF_REPLACE_EXISTING,
            (int)Amberline.Scripting.BuildingBlocks.Runtime.BbBuffType.BUFF_Internal, 1, 1.02, 0.0, false, vars)));
        var debuffs = new List<double>();
        BuffState? previous = null;
        for (var index = 1; index <= 2 * 30; index++)
        {
            runtime.Tick(step);
            var now = victim.Buffs.FirstOrDefault(buff => buff.Name == "MordekaiserCreepingDeathDebuff");
            if (now is not null && !ReferenceEquals(now, previous)) debuffs.Add(index * step);
            previous = now;
        }
        TestAssert.True(placed is not null, "MordekaiserCreepingDeathSelf is placed on the adder inside the first step");
        TestAssert.True(debuffs.Count == 1 && Math.Abs(debuffs[0] - 31.0 * step) < 1e-6,
            $"one debuff at 1.0 s after the add step wanted; got [{string.Join(", ", debuffs.Select(at => at.ToString("F3")))}] s");
    }

    /// <summary>
    /// Batch 906 (both lines; a pin that did not change): Items/3105.luaobj (Aegis of the Legion, the same on both lines) runs
    /// <c>BBExecutePeriodically</c> 1 s on its holder's self-update line and renews <c>AegisoftheLegionAuraFriend</c> (BUFF_RENEW_EXISTING, 1 s:
    /// +12 armour, +15 magic resist, +8 attack damage) on every allied champion within 1200. A 1 s timer already held through its last step
    /// before the batch (a positive rounding leftover, see <c>buffs-live-b906-whole-step-timers-hold-their-whole-duration</c>), so the renewal
    /// always met a live aura. Links: (a) the ally's armour is the aura's on every step after the first add, (b) out of range right after a
    /// renewal, the aura lives 1.0 s from that renewal: 30 steps counting the renewal's own.
    /// </summary>
    [Check("items-live-b906-aegis-friend-aura-holds-the-allys-armour-on-every-step")]
    public static void B906AegisFriendAuraHoldsEveryStep()
    {
        const double step = 1.0 / 30.0;
        const string aura = "AegisoftheLegionAuraFriend";
        var failures = new List<string>();
        using var match = new Match("Annie", Team.Order, ally: true, allyChampion: "Annie");
        var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1); var ally = match.Hero(2);
        enemy.Position = new Position2(3000, 11500);
        ally.Position = new Position2(7300, 7000);
        runtime.Tick(step);
        var baseArmor = ally.Armor;
        hero.Inventory = [new InventorySlot(3105), null, null, null, null, null];
        BuffState? Friend() => ally.Buffs.FirstOrDefault(buff => buff.Name == aura);

        int? first = null;
        var missing = new List<string>();
        for (var index = 0; index < 8 * 30; index++)
        {
            runtime.Tick(step);
            if (Friend() is not null) first ??= index;
            if (first is null) continue;
            if ((Friend() is null || Math.Abs(ally.Armor - (baseArmor + 12.0)) > 1e-6) && missing.Count < 6)
                missing.Add($"step {index - first}: {(Friend() is null ? "no aura" : "aura")}, armour {ally.Armor:F2}");
        }
        if (first is null) failures.Add("(a) the ally never received AegisoftheLegionAuraFriend in 8 s");
        else if (missing.Count > 0) failures.Add($"(a) armour {baseArmor + 12.0:F2} wanted on every step after the first add: " + string.Join(", ", missing));

        // (b) Wait for a renewal (a new instance or a timer that went up), then take the ally out of range and count the steps the aura stays.
        var previous = Friend();
        var previousTimer = previous?.ExpiresAt ?? 0.0;
        var renewed = false;
        for (var index = 0; index < 60 && !renewed; index++)
        {
            runtime.Tick(step);
            var now = Friend();
            renewed = now is not null && (!ReferenceEquals(now, previous) || now.ExpiresAt > previousTimer + 1e-9);
            previous = now;
            previousTimer = now?.ExpiresAt ?? 0.0;
        }
        if (!renewed) failures.Add("(b) no renewal of the aura seen in 2 s");
        else
        {
            ally.Position = new Position2(7300, 9500);
            var held = 1;
            while (Friend() is not null && held < 90)
            {
                runtime.Tick(step);
                if (Friend() is not null) held++;
            }
            if (held != 30) failures.Add($"(b) out of range after a renewal the aura stayed {held} steps counting the renewal's (30 wanted: its 1.0 s)");
        }
        TestAssert.True(failures.Count == 0, "Aegis friend aura: " + string.Join("; ", failures));
    }
}
