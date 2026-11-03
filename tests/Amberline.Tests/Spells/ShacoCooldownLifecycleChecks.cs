using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-shaco-r-cooldown-after-clone-cleanup")]
    public static void ShacoRKeepsItsCastCooldownWhateverEndsTheClone()
    {
        // W33 (19 October 2026): R is cast from HallucinateFull.luaobj, which seals nothing and sets no cooldown of its own; the data cooldown
        // (100 s at rank 1) starts at the cast and runs on whether the clone dies, expires or is dispelled. The hand-written module sealed the slot
        // for the clone's life and republished the remaining timer on the unseal (a non-retail busy gate): both are gone.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var cleanup in new[] { "death", "expiry", "dispel" })
        {
            using var match = new Match("Shaco", team);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var enemy = match.Hero(1);
            match.Cast(0, 3, owner);
            runtime.Tick(0.6);
            var clone = runtime.World.Entities.Values.Single(unit =>
                unit.Kind == EntityKind.ShacoClone && unit.Alive);
            var running = owner.SpellCooldowns[3];
            TestAssert.True(running > 0.0 && running < 100.0, cleanup + ": R has a running authoritative cooldown");
            TestAssert.False(owner.SpellSeals.IsSealed(false, 3), cleanup + ": the clone's life does not seal R");

            match.Clear();
            if (cleanup == "death") runtime.Combat.Kill(clone, enemy.NetId);
            else if (cleanup == "expiry")
            {
                for (var tick = 0; tick < 190 && clone.Alive; tick++)
                    runtime.Tick(0.1);
            }
            else
                runtime.SpellBuffs.Remove(clone, clone.Buffs.Single(buff => buff.Name == "HallucinateFull").Slot);
            runtime.Delivery.Flush();

            TestAssert.False(clone.Alive, cleanup + ": cleanup ends the clone");
            var remaining = owner.SpellCooldowns[3];
            TestAssert.True(remaining > 0.0 && remaining <= running,
                cleanup + ": the cast's timer is neither reset nor cleared by the clone's end");
            var blocked = runtime.Spells.CanCast(owner, 3, owner.NetId,
                new Position3(owner.Position.X, 0.0, owner.Position.Z),
                runtime.Spells.CurrentTime);
            TestAssert.False(blocked.Allowed, cleanup + ": R remains unavailable");
            TestAssert.Equal(SpellCastFailure.Cooldown, blocked.Failure,
                cleanup + ": the timer owns the rejection");
        }

        foreach (var team in new[] { Team.Order, Team.Chaos })
        using (var match = new Match("Shaco", team))
        {
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var enemy = match.Hero(1);
            owner.CooldownsDisabled = true;
            match.Cast(0, 3, owner);
            runtime.Tick(0.6);
            var clone = runtime.World.Entities.Values.Single(unit =>
                unit.Kind == EntityKind.ShacoClone && unit.Alive);
            match.Clear();
            runtime.Combat.Kill(clone, enemy.NetId);
            runtime.Delivery.Flush();
            TestAssert.Equal(0.0, owner.SpellCooldowns[3],
                "disabled cooldown mode retains zero after clone death");
            var ready = runtime.Spells.CanCast(owner, 3, owner.NetId,
                new Position3(owner.Position.X, 0.0, owner.Position.Z),
                runtime.Spells.CurrentTime);
            TestAssert.True(ready.Allowed,
                "disabled cooldown mode permits R after the clone ends");
        }
    }
}
