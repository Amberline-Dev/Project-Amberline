using System.Buffers.Binary;
using System.Globalization;
using Amberline.Core;

namespace Amberline.Tests;

/// <summary>
/// Batch 728 (owner, 9 November 2026): "Using Kassadin R really close doesnt teleport", "Urgot R sometimes does not change the places of urgot
/// and target", "Gragas E sometimes does not dash". Each check drives the production composition through the session (the client's 0xA2 cast
/// request) and reads what the server did and what every client is told.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    // Where a client's actor stands after the 0x64 entries naming the unit, from where it stood: the stock client applies an entry only when the
    // actor is outside the 50-unit box of the entry's point (HandleWaypoint, teleport branch -: the client with 50.0 tests
    // |dx| <= 50 and |dz| <= 50; inside it neither the position nor the teleport id is taken).
    private static Position2 ClientTeleportLanding(Match match, int seat, Entity unit, Position2 actor, List<Position2> entries)
    {
        foreach (var packet in match.Captures[seat].Packets.Where(packet => Opcode(packet) == 0x64))
        {
            var fields = Decode(packet);
            if (!fields.TryGetProperty("teleport_netid", out var id) || id.GetUInt32() != unit.NetId) continue;
            foreach (var point in fields.GetProperty("waypoints").EnumerateArray())
            {
                var entry = new Position2(point[0].GetDouble(), point[1].GetDouble());
                entries.Add(entry);
                if (Math.Abs(entry.X - actor.X) > 50.0 || Math.Abs(entry.Z - actor.Z) > 50.0) actor = entry;
            }
        }
        return actor;
    }

    private static string Point(Position2 point) => string.Create(CultureInfo.InvariantCulture, $"({point.X:0.#}, {point.Z:0.#})");

    /// <summary>
    /// A script teleport shorter than the stock client's 50-unit box is sent through a staging point outside it (SpellSystem.ShortTeleportStaging).
    /// The staging point was tested against the landing, where the server had already put the unit, instead of the launch, where every client's
    /// actor still stands: a Riftwalk of 20 to 70 units toward about half the compass sent a staging point inside the launch's box, the client took
    /// neither entry, and Kassadin stayed where he was on every screen while the server had him at the landing (Flash the same). Riftwalk and
    /// Flash, 20 to 70 units in twelve directions: every entry the clients get takes their actor to the server's landing.
    /// </summary>
    [Check("movement.short-script-teleport-reaches-every-client")]
    public static void ShortScriptTeleportReachesEveryClient()
    {
        var failures = new List<string>();
        foreach (var (champion, slot, summoner) in new[] { ("Kassadin", 3, false), ("Annie", 0, true) })
        for (var degrees = 0; degrees < 360; degrees += 30)
        foreach (var distance in new[] { 20.0, 40.0, 55.0, 70.0 })
        {
            using var match = new Match(champion, Team.Order);
            var hero = match.Hero(0);
            hero.Level = 6;
            match.Runtime.HeroStats.Refresh(hero, push: false);
            hero.Position = match.Runtime.Movement.ClientAnchor(new Position2(7000, 7000));
            match.Hero(1).Position = new Position2(7000, 7600);
            match.Runtime.Tick(0.5);
            hero.Mana = hero.MaxMana;
            var launch = hero.Position;
            match.Runtime.Delivery.Flush(); match.Clear();
            var aim = new Position2(launch.X + Math.Cos(degrees * Math.PI / 180.0) * distance, launch.Z + Math.Sin(degrees * Math.PI / 180.0) * distance);
            // The client's ground cast (0xA2: the slot, the summoner bit, the point twice), as Batch699Checks sends it.
            var request = new byte[34]; request[0] = 0xA2; request[5] = (byte)((slot << 1) | (summoner ? 1 : 0));
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
            foreach (var offset in new[] { 6, 18 })
            {
                BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset), (float)aim.X);
                BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset + 8), (float)aim.Z);
            }
            var accepted = false;
            match.Runtime.Session.Receive(1, 1, match.Runtime.Cipher.Encrypt(request), context => accepted = match.Runtime.Dependencies.Gameplay(context));
            for (var step = 0; step < 30; step++) match.Runtime.Tick(1.0 / 30.0);
            match.Runtime.Delivery.Flush();
            var label = string.Create(CultureInfo.InvariantCulture, $"{champion} {degrees} deg {distance}");
            if (!accepted) { failures.Add(label + ": refused"); continue; }
            if (Gap(hero.Position, launch) < distance * 0.5) { failures.Add(label + $": the server moved {Gap(hero.Position, launch):0.#}"); continue; }
            for (var seat = 0; seat < 2; seat++)
            {
                var entries = new List<Position2>();
                var actor = ClientTeleportLanding(match, seat, hero, launch, entries);
                if (Gap(actor, hero.Position) > 1.0)
                    failures.Add(label + $", seat {seat}: the client's actor stays at {Point(actor)}, the server's at {Point(hero.Position)} " +
                                 $"(launch {Point(launch)}, entries {string.Join(" ", entries.Select(Point))})");
            }
        }
        TestAssert.Equal(0, failures.Count, string.Join(" | ", failures));
    }

    /// <summary>
    /// The stock client's own teleport body (BBTeleportToPosition) gives its nearest-walkable search the flag that
    /// turns the actor test off (byte 2): a unit standing on the landing does not move it. The server
    /// backed off from such a unit toward the caster in 10-unit steps and, finding no clear point, left the caster where he stood: Riftwalk onto
    /// an enemy 120 units away did not move Kassadin at all. He lands on the point (the 270 burst hits the enemy).
    /// </summary>
    [Check("spells-live-riftwalk-onto-an-adjacent-enemy-lands-on-it")]
    public static void RiftwalkOntoAnAdjacentEnemyLandsOnIt()
    {
        foreach (var gap in new[] { 120.0, 250.0 })
        {
            using var match = new Match("Kassadin", Team.Order);
            var kassadin = match.Hero(0);
            var foe = match.Hero(1);
            kassadin.Level = 6;
            match.Runtime.HeroStats.Refresh(kassadin, push: false);
            kassadin.Position = match.Runtime.Movement.ClientAnchor(new Position2(7000, 7000));
            var dir = KassadinOpenDirection(match, kassadin.Position);
            foe.Position = match.Runtime.Movement.ClientAnchor(new Position2(kassadin.Position.X + dir.X * gap, kassadin.Position.Z + dir.Z * gap));
            match.Runtime.Tick(0.5);
            kassadin.Mana = kassadin.MaxMana;
            var launch = kassadin.Position;
            var aim = foe.Position;
            var health = foe.Health;
            match.CastGround(0, aim, slot: 3);
            for (var step = 0; step < 30; step++) match.Runtime.Tick(1.0 / 30.0);
            TestAssert.True(Gap(kassadin.Position, aim) < 3.0,
                $"gap {gap}: Riftwalk onto the enemy lands on it (from {Point(launch)} to {Point(kassadin.Position)}, aim {Point(aim)})");
            TestAssert.True(foe.Health < health, $"gap {gap}: the burst hits the enemy");
        }
    }

    /// <summary>
    /// Hyper-Kinetic Position Reverser swaps the two by three teleports (UrgotSwapExecute.OnBuffActivate: Urgot to the target's point, the target
    /// to Urgot's, Urgot to the target's again). Each landing lay on the other champion's body, and the server backed off from it toward the
    /// unit's own start: within about 130 units nobody moved, up to about 250 only Urgot moved part of the way, and farther each ended about 50
    /// units short. With no actor test in the teleport (the client's own body, above) the two end on each other's points at every range.
    /// </summary>
    [Check("spells-live-urgot-position-reverser-swaps-places-at-every-range")]
    public static void UrgotPositionReverserSwapsPlacesAtEveryRange()
    {
        foreach (var gap in new[] { 90.0, 130.0, 200.0, 300.0, 600.0 })
        {
            using var match = new Match("Urgot", Team.Order);
            var urgot = match.Hero(0);
            var foe = match.Hero(1);
            urgot.Level = 6;
            match.Runtime.HeroStats.Refresh(urgot, push: false);
            urgot.Position = match.Runtime.Movement.ClientAnchor(new Position2(7000, 7000));
            var dir = KassadinOpenDirection(match, urgot.Position);
            foe.Position = match.Runtime.Movement.ClientAnchor(new Position2(urgot.Position.X + dir.X * gap, urgot.Position.Z + dir.Z * gap));
            match.Runtime.Tick(0.5);
            urgot.Mana = urgot.MaxMana;
            var urgotStart = urgot.Position;
            var foeStart = foe.Position;
            match.Cast(0, 3, foe);
            for (var step = 0; step < 45; step++) match.Runtime.Tick(1.0 / 30.0);
            TestAssert.True(Gap(urgot.Position, foeStart) < 3.0 && Gap(foe.Position, urgotStart) < 3.0,
                $"gap {gap}: the two swap places (Urgot {Point(urgotStart)} -> {Point(urgot.Position)}, target {Point(foeStart)} -> {Point(foe.Position)})");
        }
    }

    /// <summary>
    /// Body Slam stops at the first unit it collides with (GragasBodySlam.BuffOnCollision: BBStopMoveBlock). The contact test ran in the spell
    /// phase of the step, and heroes move at 20 Hz against the 30 Hz step: in a step with no hero frame the test met Gragas where he started, so a
    /// unit touching his side or back (a minion he was fighting) ended the dash before it moved, one cast in three. A contact counts while the dash
    /// carries him toward the body (BbScriptMoves.ReportCollisions): in every step phase he dashes away from a unit touching his side or back, and
    /// still stops at a unit touching his front.
    /// </summary>
    [Check("spells-live-gragas-body-slam-dashes-away-from-a-unit-touching-him")]
    public static void GragasBodySlamDashesAwayFromAUnitTouchingHim()
    {
        foreach (var phase in new[] { 0, 1, 2 })
        foreach (var angle in new[] { 0, 100, 135, 180 })
        {
            using var match = new Match("Gragas", Team.Order);
            var gragas = match.Hero(0);
            var foe = match.Hero(1);
            gragas.Position = match.Runtime.Movement.ClientAnchor(new Position2(7000, 7000));
            var dir = KassadinOpenDirection(match, gragas.Position);
            var heading = Math.Atan2(dir.Z, dir.X) + angle * Math.PI / 180.0;
            var contact = gragas.Radius + foe.Radius - 5.0;
            foe.Position = new Position2(gragas.Position.X + Math.Cos(heading) * contact, gragas.Position.Z + Math.Sin(heading) * contact);
            match.Runtime.Tick(0.5);
            for (var step = 0; step < phase; step++) match.Runtime.Tick(1.0 / 30.0);
            gragas.Mana = gragas.MaxMana;
            var start = gragas.Position;
            match.CastGround(0, new Position2(start.X + dir.X * 500.0, start.Z + dir.Z * 500.0), slot: 2);
            for (var step = 0; step < 36; step++) match.Runtime.Tick(1.0 / 30.0);
            var moved = Gap(gragas.Position, start);
            if (angle == 0)
            {
                TestAssert.True(moved < 60.0, $"phase {phase}: the dash stops at the unit touching his front (moved {moved:0.#})");
                TestAssert.True(foe.Buffs.Any(buff => buff.Name == "GragasBodySlamTargetSlow"), $"phase {phase}: and slows it");
            }
            else TestAssert.True(moved > 450.0, $"phase {phase}, a unit touching him at {angle} degrees: the dash runs its 500 (moved {moved:0.#})");
        }
    }
}
