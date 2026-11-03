using System.Reflection;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine, wave 8 (W35b, E14): the timer of an ammo buff (<c>BUFF_AmmoStack</c>) does not end the buff, it runs the script's
/// <c>BuffOnUpdateAmmo</c>, which adds the next charge and renews the timer. The buffs are the shipped scripts of Corki's Missile Barrage, Akali's Shadow Dance
/// and Karma's Mantra, attached as a second script module to the production Ryze match (they are not playable yet); the stack count is the charge count plus
/// one, the arithmetic the scripts do themselves. Written and run by the worker that made them.
/// </summary>
public static class BbAmmoChecks
{
    private static BbChampionModule Attach(BbM3.Match match, string champion)
    {
        var dispatcher = (ChampionSpellDispatcher)match.Runtime.Dependencies.ChampionDispatcher;
        var module = new BbChampionModule(champion);
        var field = typeof(ChampionSpellDispatcher).GetField("_modules", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(dispatcher, ((IReadOnlyList<IChampionSpellModule>)field.GetValue(dispatcher)!).Append(module).ToArray());
        var hooks = (MatchGameplayHooks)match.Runtime.Dependencies.ChampionHooks;
        ((BbHookFanout)hooks.ScriptHooks!).Add(module.Bind(match.Runtime.Spells));
        return module;
    }

    private static int Stacks(Entity unit, string name) => unit.Buffs.Where(buff => buff.Name == name).Sum(buff => buff.Stacks);

    private static BuffState? AddAmmo(BbChampionModule module, Entity hero, string name, int maxStack, double duration, int stacks) =>
        module.Buffs!.Add(new BbBuffRequest(hero, hero, name, (int)BbBuffAddType.BUFF_STACKS_AND_RENEWS, (int)BbBuffType.BUFF_AmmoStack, maxStack, duration, 0.0, false,
            new BbTable(), stacks));

    [Check("scripting.bb-ammo-the-timer-adds-a-charge-through-buff-on-update-ammo-until-the-buff-is-full-and-then-stops")]
    public static void TimerRechargesUntilFull()
    {
        // Champion, ammo buff, the character variable that holds its recharge time, MaxStack.
        foreach (var (champion, buff, variable, max) in new[]
                 {
                     ("Corki", "MissileBarrage", "ChargeCooldown", 8),
                     ("Akali", "AkaliShadowDance", "DanceTimerCooldown", 4),
                     ("Karma", "KarmaChakraCharge", "MantraTimerCooldown", 3),
                 })
        {
            using var match = new BbM3.Match("Ryze");
            var module = Attach(match, champion);
            var hero = match.Hero;
            var wire = BbM3.Observe(match, hero.Team, hero);
            module.CharVars(hero)[variable] = 5.0;
            var state = AddAmmo(module, hero, buff, max, 5.0, 1);
            TestAssert.True(state is not null, champion + ": the ammo buff was added");
            TestAssert.Equal(BuffType.AmmoStack, state!.Type, champion + ": the buff keeps its ammo type for the client's count rule");
            var counts = new List<int> { Stacks(hero, buff) };
            for (var charge = 1; charge <= max; charge++)
            {
                match.Advance(5.2);
                counts.Add(Stacks(hero, buff));
            }
            var expected = Enumerable.Range(1, max).Append(max).ToList();
            TestAssert.Equal(string.Join(",", expected), string.Join(",", counts), champion + ": one stack more per elapsed recharge time, then full");
            var full = hero.Buffs.Single(b => b.Name == buff);
            TestAssert.True(full.ExpiresAt > 1000.0, champion + ": the full buff waits (25000 seconds), no recharge runs");
            TestAssert.Equal(0, module.ScriptFailures.Count, champion + ": no script failure: " + string.Join("; ", module.ScriptFailures.Select(f => $"{f.Script}.{f.Hook} {f.Message}")));
            var adds = wire.Packets.Select(item => item.Packet.Plaintext.Span.ToArray()).Where(bytes => bytes.Length > 0 && bytes[0] == SpellPackets.BuffAddOpcode).ToArray();
            TestAssert.True(adds.Any(bytes => SpellPackets.TryReadBuffAdd(bytes, out var add) && add.BuffType == (byte)BuffType.AmmoStack),
                champion + ": the client was told the buff's type (25), the client counts stacks minus one for it");
            var updates = wire.Packets.Select(item => item.Packet.Plaintext.Span.ToArray()).Count(bytes => bytes.Length > 6 && bytes[0] == SpellPackets.BuffUpdateCountOpcode);
            TestAssert.True(updates >= max - 1, champion + ": every recharged charge went out as a count update");

            // Spending a charge restarts the recharge: BBSpellBuffRemove with a ResetDuration (the spells do it with the same character variable).
            module.Buffs!.Remove(hero, buff, hero, 5.0);
            TestAssert.Equal(max - 1, Stacks(hero, buff), champion + ": one charge spent");
            TestAssert.True(hero.Buffs.Single(b => b.Name == buff).ExpiresAt < 6.0, champion + ": the timer runs again");
            match.Advance(5.2);
            TestAssert.Equal(max, Stacks(hero, buff), champion + ": and the charge came back");
        }
    }

    [Check("scripting.bb-ammo-an-ammo-buff-whose-script-has-no-update-hook-ends-when-its-timer-runs-out")]
    public static void NoHookEndsLikeAnyBuff()
    {
        var plain = BbW3Rig.Buff("W8AmmoWithoutHook");
        using var rig = new BbW3Rig(null, plain);
        var hero = rig.Match.Hero;
        AddAmmo(rig.Module, hero, plain.Name, 3, 1.0, 2);
        TestAssert.Equal(2, Stacks(hero, plain.Name), "two stacks");
        rig.Match.Advance(1.2);
        TestAssert.Equal(0, Stacks(hero, plain.Name), "the buff ended with its timer");
        rig.NoFailures();
    }

    [Check("scripting.bb-ammo-a-hook-that-does-not-renew-the-timer-lets-the-buff-end")]
    public static void HookWithoutRenewalEnds()
    {
        var ticks = BbW3Rig.Buff("W8AmmoNoRenewal", new BbEventTable("BuffOnUpdateAmmo", [BbW3Rig.Count("Updates")]));
        using var rig = new BbW3Rig(null, ticks);
        var hero = rig.Match.Hero;
        var state = AddAmmo(rig.Module, hero, ticks.Name, 3, 1.0, 1)!;
        var instance = rig.Module.Buffs!.InstanceOf(state)!;
        rig.Match.Advance(1.2);
        TestAssert.Equal(1.0, (double)instance.InstanceVars["Updates"]!, "the hook ran once at the end of the timer");
        TestAssert.Equal(0, Stacks(hero, ticks.Name), "and, having renewed nothing, the buff ended");
        rig.NoFailures();
    }
}
