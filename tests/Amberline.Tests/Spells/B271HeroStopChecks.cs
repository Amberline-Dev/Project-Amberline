using System.Buffers.Binary;
using Amberline.Core;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Batch 271: a hero's stop is a one-point path, and the client installs it on receipt
    /// (the client's path install): the actor stopped (it zeroes the stored velocity) and seated on
    /// the path's point, its lattice anchor. The stop order (STOP, 10) and the hero AI's stop (Hero.luaobj's state
    /// changes, the attack's in-range stop) seated the walking hero only at its next frame's stop arrival (batch 263 open question 3).
    /// </summary>
    [Check("movement.hero-stop-seats-at-once")]
    public static void HeroStopSeatsAtOnce()
    {
        using var match = new Match("Annie", Team.Order);
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        var failures = new List<string>();

        Stop("stop order", () =>
        {
            var request = new byte[22]; request[0] = 0x75; request[5] = 10;
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(6), (float)hero.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(14), (float)hero.Position.Z);
            runtime.Session.Receive(1, 1, runtime.Cipher.Encrypt(request), runtime.Dependencies.Gameplay);
        });
        Stop("hero AI stop", () => runtime.Spells.HeroOrders!.StopMovement(hero));
        TestAssert.True(failures.Count == 0, string.Join(" | ", failures));

        void Stop(string name, Action act)
        {
            match.Move(0, new Position2(7000, 9000));
            runtime.Tick(.25);
            TestAssert.True(hero.Waypoints.Count > 0, $"fixture: the hero walks before the {name}");
            // Mid-walk, off the lattice and moving.
            hero.Position = new Position2(Math.Floor(hero.Position.X) + .7, Math.Floor(hero.Position.Z) + .3);
            hero.FrameVelocity = new Position2(0, 15);
            var anchor = runtime.Movement.ClientAnchor(hero.Position);
            TestAssert.True(anchor != hero.Position, $"fixture: the hero stands off the lattice before the {name}");
            act();
            if (hero.Waypoints.Count != 0) failures.Add($"{name}: the hero keeps its route");
            if (hero.Position != anchor) failures.Add($"{name}: the hero stands on {hero.Position}, not on the stop's point {anchor}");
            if (hero.FrameVelocity != Position2.Zero) failures.Add($"{name}: the stop's install did not stop it ({hero.FrameVelocity})");
        }
    }
}
