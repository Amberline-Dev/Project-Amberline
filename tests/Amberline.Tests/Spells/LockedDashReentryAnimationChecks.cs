using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-locked-dash-animation-late-vision-and-reconnect")]
    public static void LockedDashGestureFollowsActiveMoveIntoFreshViews()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using (var leeW = HiddenCaster("LeeSin", team, ally: true))
            {
                var hero = leeW.Hero(0);
                var ally = leeW.Hero(2);
                ally.Position = hero.Position + new Position2(600, 0);
                leeW.Cast(0, 1, ally);
                TestAssert.True(hero.ForcedMove is { Effect: "BlindMonkWOneDash" });
                AssertLateDash(leeW, "Spell2", loop: true);
            }

            using (var leeQ = HiddenCaster("LeeSin", team))
            {
                var hero = leeQ.Hero(0);
                var neutral = leeQ.Runtime.World.Register(new Entity(
                    leeQ.Runtime.World.Allocate(UnitType.Monster), EntityKind.Neutral,
                    UnitType.Monster, Team.Neutral, hero.Position + new Position2(370, 0))
                { Name = "DashTarget", Health = 5000, MaxHealth = 5000 });
                leeQ.CastGround(0, neutral.Position);
                leeQ.Runtime.Tick(.6);
                TestAssert.True(neutral.Buffs.Any(buff => buff.Name.StartsWith("BlindMonkQOne")),
                    "real Sonic Wave marks the neutral before Q2");
                leeQ.Cast(0, 0, neutral);
                TestAssert.True(hero.ForcedMove is { Effect: "BlindMonkQTwoDash" });
                AssertLateDash(leeQ, "Spell1b", loop: true);
            }

            using (var nidalee = HiddenCaster("Nidalee", team))
            {
                var hero = nidalee.Hero(0);
                nidalee.Cast(0, 3, hero);
                hero.SpellCooldowns[1] = 0.0;
                nidalee.Cast(0, 1, hero);
                AssertLateDash(nidalee, "Spell2", loop: false);
            }

            using (var vayne = HiddenCaster("Vayne", team))
            {
                var hero = vayne.Hero(0);
                vayne.CastGround(0, hero.Position + new Position2(300, 0));
                AssertLateDash(vayne, "Spell1", loop: false, reconnect: true);
            }
        }
    }

    [Check("spells-live-locked-dash-animation-same-flush-no-duplicate")]
    public static void FirstSightOnCastFrameDoesNotDoubleStartTumbleClip()
    {
        using var match = HiddenCaster("Vayne", Team.Order);
        var hero = match.Hero(0);
        match.Hero(1).Position = hero.Position + new Position2(200, 0);
        // No visibility flush between moving the observer and accepting Q:
        // the new-sight snapshot and the original B8 share one delivery frame.
        match.CastGround(0, hero.Position + new Position2(300, 0));
        AssertAnimation(match, 1, hero, "Spell1", loop: false, 1);
    }

    private static Match HiddenCaster(string champion, Team team, bool ally = false)
    {
        var match = new Match(champion, team, ally: ally);
        match.Hero(1).Position = new Position2(12000, 12000);
        match.Runtime.Delivery.Flush();
        TestAssert.False(match.Runtime.Session.Participants[1].View.Visible.Contains(match.Hero(0).NetId),
            "the opposite team cannot receive the original caster gesture");
        match.Clear();
        return match;
    }

    private static void AssertLateDash(Match match, string clip, bool loop, bool reconnect = false)
    {
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        var view = match.Runtime.Session.Participants[1].View;
        TestAssert.False(view.Visible.Contains(hero.NetId), "actor stayed hidden through the original cast");
        match.Clear();
        enemy.Position = hero.Position + new Position2(200, 0);
        match.Runtime.Delivery.Flush();
        AssertAnimation(match, 1, hero, clip, loop, 1);
        // Batch 291: since batch 276 the remaining dash is the reveal's 0xC2 movement data type 1 (AnnouncesDash).
        TestAssert.True(match.Captures[1].Packets.Any(packet => AnnouncesDash(packet, hero.NetId)),
            "late sight receives the remaining dash together with its locked clip");
        match.Clear();
        match.Runtime.Delivery.Flush();
        AssertAnimation(match, 1, hero, clip, loop, 0);

        if (reconnect)
        {
            TestAssert.True(match.Runtime.Session.Disconnect(2));
            var seat = match.Runtime.Session.AuthenticateReconnect(102,
                match.Runtime.Session.BuildKeyCheck(2));
            TestAssert.True(seat is not null);
            match.Runtime.Session.Spawn(102);
            TestAssert.True(match.Runtime.Session.MarkReady(102));
            seat!.Loaded = true;
            match.Clear();
            match.Runtime.Delivery.Flush();
            AssertAnimation(match, 1, hero, clip, loop, 1);
        }

        match.Runtime.Tick(1.0);
        TestAssert.True(hero.ForcedMove is null || hero.ForcedMove.Completed,
            "the locked movement has completed before its next reveal");
        enemy.Position = new Position2(12000, 12000);
        match.Runtime.Delivery.Flush();
        match.Clear();
        enemy.Position = hero.Position + new Position2(200, 0);
        match.Runtime.Delivery.Flush();
        AssertAnimation(match, 1, hero, clip, loop, 0);
    }

    private static void AssertAnimation(Match match, int viewer, Entity actor,
        string clip, bool loop, int expectedCount)
    {
        var expected = SpellPackets.PlayAnimation(actor.NetId, clip,
            loop: loop, lockAnimation: true, blend: false).Plaintext.ToArray();
        var starts = match.Captures[viewer].Packets.Where(packet =>
            packet.Plaintext.Span.Length >= 5 && packet.Plaintext.Span[0] == 0xB8 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == actor.NetId).ToArray();
        TestAssert.Equal(expectedCount, starts.Length,
            $"{clip} locked gesture count in observer's delivery frame");
        foreach (var start in starts)
            TestAssert.True(start.Plaintext.Span.SequenceEqual(expected),
                $"{clip} keeps its shipped B8 flags and natural playback rate");
    }
}
