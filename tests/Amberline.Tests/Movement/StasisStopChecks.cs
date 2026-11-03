using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Simulation;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Batch 158, the owner's playtest finding: "the character sometimes moves a little bit when Zhonya's takes effect". In stasis the
    /// hero must stop exactly where it stands, at once, and keep its facing. This covers every root of a hold a CanMove-removing status
    /// causes, not the item alone: a status that lands on the <b>add</b> of its buff (Zhonya's stasis, a stun, a root) is held by
    /// <c>BuffLifecycle.AddBuff</c>, and a status the script writes into a <b>live</b> buff (a <c>BuffOnUpdateStats</c>/<c>OnBuffActivate</c>
    /// resample — stasis refreshed on an already-held buff, Teleport's channel, any <c>BBSetStatus</c> that removes CanMove) is held by
    /// <c>BuffLifecycle.ReplaceScriptPayload</c>. Before that second root, a status that removed CanMove without rooting stopped only the
    /// hero's swing and sent no stop at all, so the client kept marching the route it had been given and the character walked on a little
    /// after the status landed.
    ///
    /// The stop must therefore leave in the same tick, carry that tick's server position (never a stale or predicted one) and retire the
    /// route. A standing unit's client-visible position is the two-unit wire anchor of its server position: every
    /// <c>WaypointList</c> point passes the waypoint position setter, which compresses it to the scale-2 grid of
    /// <c>NavigationHelper</c>. The client's server-path follower hard-seats the actor on its stored point zero. Its research note,
    /// docs/research/2026-10-16-minion-teleport-investigation.md, says that every server position correction is a client snap.
    /// The anchor of where the hero stood must not move, and the hero itself
    /// must not move either.
    ///
    /// Facing travels in the stop packet, so the hold itself may never turn the hero. A root removes CanMove only (never CanAttack), so
    /// idle acquisition can still pick an enemy in reach and that swing turns the hero like any attack; that turn, and nothing else, is
    /// what this check allows after the activation.
    /// </summary>
    [Check("movement.stasis-holds-client-position")]
    public static void StasisAndControlHoldsKeepTheClientVisiblePosition()
    {
        foreach (var hold in new[] { "zhonya", "stun", "root", "resample-root", "resample-stun" })
        {
            using var match = new Match("Annie", Team.Order, ally: true, autoAcquire: false);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            BuffState? live = null;
            if (hold == "zhonya")
            {
                hero.Inventory = [new InventorySlot(3157), null, null, null, null, null];
                runtime.Tick(0.05);
            }
            else if (hold.StartsWith("resample", StringComparison.Ordinal))
            {
                // A live script buff first, added with a payload that locks nothing: an add that removes nothing stops nothing, so
                // the hold below can only be the resample of that same live buff (BbBuffRuntime.WriteStatus → HeldStatuses →
                // Resample → BuffLifecycle.ReplaceScriptPayload), the path a shipped CanMove-removing status takes when it lands on a
                // buff the unit already holds. Without that root no stop leaves at all and the client marches on.
                live = runtime.Buffs.AddBuff(hero, "TestScriptHold", BuffType.Internal, 5.0, 0, new BuffPayload());
                TestAssert.True(live is not null, $"{hold}: the live script buff takes a slot");
            }

            // Walk first: the visible nudge only exists while the hero is mid-march between two wire anchors.
            match.Move(0, hero.Position + new Position2(600, 0));
            match.Clear();
            for (var step = 0; step < 6; step++) runtime.Tick(0.05);
            var before = hero.Position;
            var visible = runtime.Movement.ClientAnchor(before);
            var facing = hero.Facing;
            TestAssert.True(before != visible,
                $"{hold}: the hero is between wire anchors mid-march, the case the client snap moves");

            match.Clear();
            switch (hold)
            {
                case "zhonya":
                    match.Cast(0, 4, hero);
                    break;
                case "stun":
                    runtime.Buffs.AddBuff(hero, "TestStun", BuffType.Stun, 2.0, 0,
                        new BuffPayload { ActionDisabled = true });
                    break;
                case "root":
                    runtime.Buffs.AddBuff(hero, "TestRoot", BuffType.Snare, 2.0, 0,
                        new BuffPayload { MovementDisabled = true });
                    break;
                case "resample-root":
                    runtime.Buffs.ReplaceScriptPayload(hero, live!, new BuffPayload { MovementDisabled = true });
                    break;
                default:
                    runtime.Buffs.ReplaceScriptPayload(hero, live!, new BuffPayload { ActionDisabled = true });
                    break;
            }
            runtime.Delivery.Flush();

            var held = hero.Position;
            TestAssert.Equal(before, held, $"{hold}: the hero stops exactly where it stands, at once");
            TestAssert.Equal(visible, runtime.Movement.ClientAnchor(held),
                $"{hold}: the hero's client-visible position does not change through the activation");
            TestAssert.True(hero.Waypoints.Count == 0, $"{hold}: the hold retires the route in the same tick");
            TestAssert.Equal(facing, hero.Facing, $"{hold}: the hero keeps its facing");

            // Without this stop the client keeps marching the route it was last given and the character walks on a
            // little after the status landed (the owner's finding). The stop carries the server position of the same
            // tick, never a stale or predicted one, and the anchor the client seats its actor on is the hero's own.
            var points = HeroRoutePoints(match, hero);
            TestAssert.True(points.Count > 0,
                $"{hold}: the hold sends its stop in the same tick, or the client keeps marching the echoed route");
            foreach (var point in points)
            {
                TestAssert.Equal(held, point, $"{hold}: the stop carries the server position of the activation tick");
                TestAssert.Equal(visible, runtime.Movement.ClientAnchor(point),
                    $"{hold}: the stop seats the client's actor on the anchor the hero already stood on");
            }

            // A click refused during the hold re-anchors at the same point (or sends nothing); no queued order and no later
            // packet may move it. This is the same rule for every hold: the order is refused by the locked-action check, never
            // committed and then frozen, or the client marches the new route while the server stands.
            match.Clear();
            match.Move(0, hero.Position + new Position2(400, 0));
            runtime.Delivery.Flush();
            foreach (var point in HeroRoutePoints(match, hero))
            {
                // Batch 279: the refusal's one-point path is installed at once (the client's path follower): the hero is seated on the anchor the
                // client already drew, and the path carries it.
                TestAssert.Equal(visible, point, $"{hold}: an order refused in stasis re-anchors at the hero");
                TestAssert.Equal(visible, runtime.Movement.ClientAnchor(point),
                    $"{hold}: an order refused in stasis moves nothing the client draws");
            }

            // Through the whole hold the hero and the drawn hero stay where the status found them. The hold itself never turns the
            // hero: a root is not a silence (it removes CanMove only), so idle acquisition may still pick an enemy in reach and the
            // swing turns the hero toward it like any attack — the one legal facing change while held.
            for (var step = 0; step < 30; step++)
            {
                runtime.Tick(0.05);
                // Restated in batch 254: the stop is a one-point path, and the path follower seats the actor on
                // its anchor; the server's hero is seated there at its next frame, the point the client already draws.
                TestAssert.True(hero.Position == held || hero.Position == visible,
                    $"{hold}: the hero does not move during the hold, other than onto the anchor it is drawn on ({hero.Position})");
                TestAssert.Equal(visible, runtime.Movement.ClientAnchor(hero.Position),
                    $"{hold}: the hero's client-visible position does not change through the activation");
                if (hero.Facing == facing) continue;
                var swungAt = hero.AttackTarget is uint targetId ? runtime.World.Get(targetId) : null;
                TestAssert.True(swungAt is not null, $"{hold}: the hold itself never turns the hero");
                facing = Geometry.FaceTowards(hero.Position, swungAt!.Position, facing);
                TestAssert.Equal(facing, hero.Facing, $"{hold}: the hero turns only toward the target it swings at");
            }
            foreach (var point in HeroRoutePoints(match, hero))
                TestAssert.True(point == held || point == visible, $"{hold}: no later packet re-anchors the hero elsewhere ({point})");
        }
    }

    /// <summary>Every first point of the hero's own waypoint packets captured since the last <c>Clear</c>.</summary>
    private static List<Position2> HeroRoutePoints(Match match, Entity hero)
    {
        var points = new List<Position2>();
        foreach (var packet in match.Captures[0].Packets)
        {
            var span = packet.Plaintext.Span;
            if (span[0] != VisionPackets.WaypointListOpcode || span.Length < 13) continue;
            if (BinaryPrimitives.ReadUInt32LittleEndian(span[1..]) != hero.NetId) continue;
            points.Add(new Position2(
                BinaryPrimitives.ReadSingleLittleEndian(span[9..]),
                BinaryPrimitives.ReadSingleLittleEndian(span[13..])));
        }
        return points;
    }
}
