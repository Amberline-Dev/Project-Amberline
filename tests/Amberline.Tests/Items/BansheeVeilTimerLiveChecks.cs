using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("items-live-banshee-timer-expiry-restores-script-veil")]
    public static void BansheeVeilReturnsAsItsScriptInstanceWhenTheTimerEnds()
    {
        using var match = new Match("Annie", Team.Order);
        var hero = BuyBansheesVeil(match);
        bool Scripted(BuffState state) =>
            match.Runtime.Spells.ItemScripts?.Buffs?.InstanceOf(state) is not null ||
            (match.Runtime.Dependencies.ChampionDispatcher.FindModule(hero.ChampionName) as
                Amberline.Simulation.Spells.Bb.BbChampionModule)?.Buffs?.InstanceOf(state) is not null;
        TestAssert.True(Scripted(hero.Buffs.Single(buff => buff.Name == "BansheesVeil")),
            "control: the item script's own veil is a script instance");
        TestAssert.True(match.Runtime.Combat.ConsumeSpellShield(match.Hero(1), hero, force: true), "a spell pops the veil");
        // BuffOnUpdateStats swaps the popped veil for its timer at the next 0.25 s poll (BbBuffRuntime), not at the pop.
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "BansheesVeil"), "the popped veil stays until the script's poll");
        TickUntilGone(match, hero, "BansheesVeil", 0.30);
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "BansheesVeil"), "the popped veil is gone at the poll");
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "BansheesVeilTimer"), "the poll adds the 45 s timer");
        for (var second = 0; second < 60 && hero.Buffs.Any(buff => buff.Name == "BansheesVeilTimer"); second++)
            match.Runtime.Tick(1.0);
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "BansheesVeilTimer"), "the 45 s timer ended");
        // The shipped item script re-adds the veil from its own two-second periodic update once no timer is left (Items/3102.luaobj
        // UpdateSelfBuffActions), and the re-add is the script's instance, not a payload-less copy. The timer's end falls between two
        // one-second ticks, so the re-add is timed from the first tick that sees the timer gone (the periodic's phase is the script's).
        var waited = 0.0;
        while (!hero.Buffs.Any(buff => buff.Name == "BansheesVeil") && waited < 2.1)
        {
            match.Runtime.Tick(0.05);
            waited += 0.05;
        }
        var veil = hero.Buffs.FirstOrDefault(buff => buff.Name == "BansheesVeil");
        TestAssert.True(veil is not null, "the item script's periodic re-adds the veil within its two-second period");
        TestAssert.True(Scripted(veil!), "the re-added veil is the shipped script's instance, not a payload-less copy");
    }

    // One blocked spell pops the veil once, through the shipped BansheesVeil.luaobj: BuffOnBeingSpellHit sets WillRemove and the
    // veil stays on the unit until BuffOnUpdateStats swaps it for the 45 s timer at its next 0.25 s poll.
    [Check("items-live-banshee-spell-pop-waits-for-the-script-poll")]
    public static void BansheeVeilSpellPopIsTheScriptsAndItsTimerComesAtThePoll()
    {
        using var match = new Match("Annie", Team.Order);
        var hero = BuyBansheesVeil(match);
        var enemy = match.Hero(1);
        TestAssert.True(match.Runtime.Combat.ConsumeSpellShield(enemy, hero), "a hostile spell pops the veil");
        TestAssert.False(match.Runtime.Combat.ConsumeSpellShield(enemy, hero),
            "a second spell inside the same window does not pop the veil again");
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "BansheesVeil"), "the popped veil is still on the unit");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "BansheesVeilTimer"), "no timer before the script's poll");
        TickUntilGone(match, hero, "BansheesVeil", 0.30);
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "BansheesVeil"), "the veil leaves at the 0.25 s poll");
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "BansheesVeilTimer"), "the poll adds the 45 s timer");
    }

    // Buys Banshee's Veil at the fountain and ticks until the shipped item script has raised the veil.
    private static Entity BuyBansheesVeil(Match match)
    {
        var hero = match.Hero(0);
        hero.Position = MapDefinitions.StructureVisionPositions["Fountain_T1"];
        hero.Inventory = Inventory.Empty();
        hero.Gold = 50_000.0;
        bool Shop(byte[] packet) => match.Runtime.Session.DispatchShop(
            new SessionInput(1, 1, match.Runtime.Cipher.Encrypt(packet), packet, true));
        var buy = new byte[9];
        buy[0] = 0x87;
        BinaryPrimitives.WriteUInt32LittleEndian(buy.AsSpan(1), hero.NetId);
        BinaryPrimitives.WriteUInt32LittleEndian(buy.AsSpan(5), 3102u);
        TestAssert.True(Shop([0x60, 0, 0, 0, 0]), "fountain opens shop");
        TestAssert.True(Shop(buy), "Banshee's Veil bought");
        match.Runtime.Tick(2.5);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "BansheesVeil"), "the item script raises the veil");
        return hero;
    }

    // Ticks in 0.05 s steps until the named buff is gone from the hero, and fails if that takes longer than the limit.
    private static void TickUntilGone(Match match, Entity hero, string name, double limitSeconds)
    {
        var elapsed = 0.0;
        while (hero.Buffs.Any(buff => buff.Name == name) && elapsed < limitSeconds)
        {
            match.Runtime.Tick(0.05);
            elapsed += 0.05;
        }
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == name), $"{name} is gone within {limitSeconds:0.00} s");
    }
}
