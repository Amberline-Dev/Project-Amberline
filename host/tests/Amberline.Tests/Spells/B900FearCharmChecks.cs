using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 900 (owner, 19 November 2026): "Fear is affecting units, but those affected behave weirdly. Barely moving at all if they do for the
/// duration" and "Charming a target with Ahri E makes them teleport back to the location they were charmed at sometimes". Each check follows a
/// controlled unit on the server and on one client's copy of it (the waypoint lists that client receives, walked at the unit's speed), so a
/// route the server does not walk, or a walk the client is never told about, shows as a snap.
/// docs/maintenance/2026-11-19-batch-900-fear-charm.md (132/).
/// </summary>
public static class B900FearCharmChecks
{
    // A lane minion under Terrify: every TimerFeared wander (minionLib OnFearBegin, then each second) reaches the client when it starts.
    // Red before: ProductionMinionServices held each wander's route until the wander's own Fear move ended, so the client saw the minion stand.
    [Check("minions.b900-a-feared-lane-minion-walks-each-wander-on-its-client")]
    public static void FearedLaneMinionWalksEachWanderOnItsClient()
    {
        using var match = new Match("FiddleSticks");
        match.Runtime.Waves!.AutomaticSpawningEnabled = false;
        var minion = LaneMinion(match, match.Hero.Position + new Position2(300, 0));
        var wire = Observe(match, Team.Order, match.Hero);
        match.Advance(0.2);
        match.Learn(5);
        match.Refill();
        TestAssert.True(match.Cast(0, minion), "Terrify is cast at the lane minion");
        var run = Follow(match, wire, minion, "Fear", 4.0);
        Expect(run, "lane minion under Terrify", minPath: 150.0, minRoutes: 3);
    }

    // A bot champion under Terrify wanders as Hero.luaobj walks a player's champion (BotController.StepControl). Red before: a bot seat runs
    // Bot.luaobj, which has no fear hook, so the bot stood for the whole fear.
    [Check("spells.b900-a-feared-bot-champion-wanders-like-a-player")]
    public static void FearedBotChampionWandersLikeAPlayer()
    {
        using var match = new Match("FiddleSticks", foeChampion: "Annie", foeIsBot: true);
        match.Foe.Position = match.Hero.Position + new Position2(300, 0);
        var wire = Observe(match, Team.Order, match.Hero);
        match.Advance(0.2);
        match.Learn(5);
        match.Refill();
        TestAssert.True(match.Cast(0, match.Foe), "Terrify is cast at the bot");
        var run = Follow(match, wire, match.Foe, "Fear", 4.0);
        Expect(run, "bot champion under Terrify", minPath: 150.0, minRoutes: 3);
    }

    // A bot champion under Rammus's Puncturing Taunt closes on Rammus, as Hero.luaobj's OnTauntBegin does for a player, and keeps him as its
    // target for the whole taunt while its own task's orders wait (BotController.StepControl, IsActionLocked). The bot has fought on its own
    // for 6 s first (the ally Annie beside it). Red before: Bot.luaobj has no taunt hook and the bot went on with its own target.
    [Check("spells.b900-a-taunted-bot-champion-closes-on-its-taunter")]
    public static void TauntedBotChampionClosesOnItsTaunter()
    {
        using var match = new Match("Rammus", foeChampion: "Annie", foeIsBot: true);
        match.Advance(6.0);
        var bot = match.Foe;
        match.Hero.Position = bot.Position + new Position2(-250, 0);
        match.Hero.Waypoints.Clear();
        match.Learn(5);
        match.Refill();
        TestAssert.True(match.Cast(2, bot), "Puncturing Taunt is cast at the bot");
        double? on = null, off = null, lostAt = null;
        for (var elapsed = 0.0; elapsed < 4.0 && off is null; elapsed += Step)
        {
            match.Advance(Step);
            var taunted = bot.Buffs.Any(buff => buff.Type == Amberline.Core.Buffs.BuffType.Taunt);
            if (taunted && on is null) on = match.Now;
            if (!taunted && on is not null) off = match.Now;
            if (taunted && match.Now - on > 0.1 && bot.AttackTarget != match.Hero.NetId) lostAt ??= match.Now - on;
        }
        TestAssert.True(on is not null && (off ?? match.Now) - on > 0.5, "the bot was taunted");
        TestAssert.True(lostAt is null, $"the taunted bot's target was not Rammus {lostAt:0.###} s into the taunt (target {bot.AttackTarget})");
    }

    /// <summary>What <see cref="Follow"/> saw of one control on one unit.</summary>
    internal sealed record ControlRun(double Seconds, double ServerPath, double FirstRouteDelay, int Routes, int StopsInside, double MaxSnap, double EndGap, Position2 Start, Position2 End);

    internal static void Expect(ControlRun run, string name, double minPath, int minRoutes, double maxSnap = 20.0)
    {
        TestAssert.True(run.Seconds > 0.5, $"{name}: the control was not applied ({run.Seconds:0.###} s)");
        TestAssert.True(run.ServerPath >= minPath, $"{name}: the server walked {run.ServerPath:0} u in {run.Seconds:0.##} s (at least {minPath:0} expected)");
        TestAssert.True(run.FirstRouteDelay <= Step + 1e-9, $"{name}: its first route reached the client {run.FirstRouteDelay:0.###} s after the control began (the same step expected)");
        TestAssert.True(run.Routes >= minRoutes, $"{name}: its client got {run.Routes} routes while it lasted (at least {minRoutes} expected)");
        TestAssert.True(run.MaxSnap <= maxSnap, $"{name}: its client's copy was put {run.MaxSnap:0} u off what it had been told (at most {maxSnap:0})");
        TestAssert.True(run.EndGap <= maxSnap, $"{name}: at the end the client's copy stands {run.EndGap:0} u from the server's unit");
        TestAssert.Equal(0, run.StopsInside, $"{name}: one-point stops between the first route and the end");
    }

    /// <summary>
    /// Steps the match up to <paramref name="seconds"/> while <paramref name="buff"/> is applied, then half a second past its end. The client's copy
    /// starts where the unit stands and walks each waypoint list it receives (seated on its first point, as the client's path install does) at the
    /// unit's current speed; the snap of a list is how far its first point is from where the copy stood.
    /// </summary>
    internal static ControlRun Follow(Match match, WireCapture wire, Entity unit, string buff, double seconds, Action? eachStep = null)
    {
        var copy = new ClientCopy(unit.Position);
        var seen = wire.Packets.Count;
        double? on = null, off = null;
        Position2 start = unit.Position, last = unit.Position, end = unit.Position;
        double path = 0, maxSnap = 0, endGap = double.PositiveInfinity;
        int routes = 0, stops = 0;
        var routed = false;
        double? firstRoute = null;
        for (var elapsed = 0.0; elapsed < seconds + 0.5 && (off is null || match.Now < off + 0.5); elapsed += Step)
        {
            eachStep?.Invoke();
            match.Advance(Step);
            var held = unit.Buffs.Any(item => item.Name == buff);
            if (held && on is null) { on = match.Now; start = unit.Position; }
            if (!held && on is not null && off is null) off = match.Now;
            var inside = on is not null && off is null;
            if (inside) path += Distance(last, unit.Position);
            last = unit.Position;
            copy.Advance(Step, unit.Speed);
            for (; seen < wire.Packets.Count; seen++)
            {
                if (WaypointsOf(wire.Packets[seen].Packet, unit.NetId) is not { } points) continue;
                var snap = copy.Install(points);
                if (on is null) continue;
                if (off is null || match.Now <= off + 1e-9) maxSnap = Math.Max(maxSnap, snap);
                if (!inside) continue;
                if (points.Length > 1) { routes++; routed = true; firstRoute ??= match.Now - on.Value; }
                else if (routed) stops++;
            }
            if (off == match.Now) (endGap, end) = (Distance(copy.Position, unit.Position), unit.Position);
        }
        return new ControlRun((off ?? match.Now) - (on ?? match.Now), path, firstRoute ?? double.PositiveInfinity, routes, stops, maxSnap, endGap, start, end);
    }

    /// <summary>The points of a waypoint list (VisionPackets.WaypointList: net id at 1, sync id at 5, float pairs from 9) for one unit.</summary>
    internal static Position2[]? WaypointsOf(PlaintextPacket packet, uint netId)
    {
        var span = packet.Plaintext.Span;
        if (span.Length < 17 || span[0] != VisionPackets.WaypointListOpcode || BinaryPrimitives.ReadUInt32LittleEndian(span[1..]) != netId) return null;
        var points = new Position2[(span.Length - 9) / 8];
        for (var index = 0; index < points.Length; index++)
            points[index] = new Position2(BinaryPrimitives.ReadSingleLittleEndian(span[(9 + index * 8)..]),
                BinaryPrimitives.ReadSingleLittleEndian(span[(13 + index * 8)..]));
        return points;
    }

    internal static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private sealed class ClientCopy(Position2 position)
    {
        private Position2[] _route = [];
        private int _next;
        public Position2 Position { get; private set; } = position;

        public double Install(Position2[] points)
        {
            var snap = Distance(Position, points[0]);
            Position = points[0];
            _route = points;
            _next = 1;
            return snap;
        }

        public void Advance(double dt, double speed)
        {
            var budget = Math.Max(0.0, speed) * dt;
            while (budget > 0 && _next < _route.Length)
            {
                var gap = Distance(Position, _route[_next]);
                if (gap <= budget) { Position = _route[_next++]; budget -= gap; continue; }
                Position = new Position2(Position.X + (_route[_next].X - Position.X) * budget / gap, Position.Z + (_route[_next].Z - Position.Z) * budget / gap);
                budget = 0;
            }
        }
    }
}
