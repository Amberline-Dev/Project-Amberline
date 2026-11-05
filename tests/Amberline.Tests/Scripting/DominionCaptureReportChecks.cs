using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Replication;

namespace Amberline.Tests;

/// <summary>
/// Batch 687: the community's capture point reports of 4 November 2026, on the two-seat map 8 match of <see cref="DominionGuardianChecks"/>
/// (point C).
/// </summary>
public static partial class DominionGuardianChecks
{
    /// <summary>The shipped OdinTower tree's AcquisitionRange and AttackRadius (OdinTowerBehaviorTrees.xml:32-41).</summary>
    private const double TowerRange = 600.0;

    /// <summary>
    /// Report "Shrines on Dominion have way too big range". The guardian acquires and keeps its target within the shipped OdinTower tree's
    /// AcquisitionRange and AttackRadius, 600 centre to centre (LEVELS/Map8/Scripts/OdinTowerBehaviorTrees.xml:32-41, the only shipped tower
    /// AI written for these units), not within its record's AcquisitionRange 950 and its attack spell's CastRange 1000 (batch 662).
    /// </summary>
    [Check("dominion.guardian-reaches-the-odin-tower-range-only", TimeoutSeconds = 300)]
    public static void TheGuardianReachesTheOdinTowerRangeOnly()
    {
        using var match = StartMatch();
        Capture(match, match.Order);
        var (runtime, guardian, chaos) = (match.Runtime, match.Guardian, match.Chaos);
        match.Order.Position = match.OrderHome;
        chaos.Position = Offset(guardian, -(TowerRange + 100.0));
        var idle = CastAt(guardian);
        Advance(runtime, 2.0);
        TestAssert.True(CastAt(guardian) == idle,
            $"a Chaos champion {TowerRange + 100.0:F0} away, past the tower range {TowerRange:F0}, draws no shot");
        chaos.Position = Offset(guardian, -(TowerRange - 50.0));
        var health = chaos.Health;
        TestAssert.True(Until(runtime, () => chaos.Health < health - 50.0, 1.5),
            $"a Chaos champion {TowerRange - 50.0:F0} away is hit ({health:F0} to {chaos.Health:F0})");
        chaos.Position = Offset(guardian, -(TowerRange + 100.0));
        Advance(runtime, 0.5);
        var shot = CastAt(guardian);
        Advance(runtime, 2.0);
        TestAssert.True(CastAt(guardian) == shot, "the guardian stops firing once its target leaves the tower range");
    }

    /// <summary>
    /// Report "a shrine that lost progress to a failed enemy capture lets its own team into a rejected capture". The stock client refuses a use
    /// of an allied guardian (IsAllowedToUse, the client: its own team needs Useable*AllyCanUse, which OdinNeutralGuardian.inibin does not
    /// set), so the right-click falls through to its attack branch, which sends ATTACKTO (order 3) on an ally whose CanBeAttackedBy
    /// (obj_AI_Minion) passes: health over max health at or below 0.0, which the guardian's wire health 0
    /// (batch 503) always is. The server took every order 3 on a guardian as the capture, and the shipped channel then ended itself at its first
    /// update (OdinCaptureChannel ChannelingUpdateStats: MyTeam equals TargetTeam), with the 3 s use cooldown. The server now refuses a capture
    /// The client's use rule refuses, on every start path.
    /// </summary>
    [Check("dominion.own-team-cannot-channel-its-damaged-point", TimeoutSeconds = 300)]
    public static void TheOwnTeamCannotChannelItsDamagedPoint()
    {
        using var match = StartMatch();
        var (runtime, order, chaos) = (match.Runtime, match.Order, match.Chaos);
        Capture(match, order);
        var guardian = match.Guardian;
        order.Position = match.OrderHome;
        chaos.Position = Offset(guardian, -300.0);
        TestAssert.True(runtime.Spells.BeginCaptureChannel(chaos, guardian), "Chaos channels the Order-owned point");
        Advance(runtime, 4.0);
        chaos.Position = match.ChaosHome;
        Advance(runtime, 0.5);
        var progress = (double)match.Quests.Captures[PointIndex].Variable("CaptureProgress")!;
        TestAssert.True(Owner(match) == Team.Order && progress < 95.0,
            $"the failed neutralise left Order's point C with lost progress (owner {Owner(match)}, progress {progress:F1})");
        order.Position = Offset(guardian, 300.0);
        Order(runtime, order, 3, guardian.Position, guardian.NetId);
        Advance(runtime, 0.5);
        TestAssert.True(runtime.Spells.CaptureChannelTarget(order) is null && !order.Buffs.Any(buff => buff.Name == Channel) &&
                        !runtime.Spells.UseSlotCoolingDown(order),
            $"the Order champion's ATTACKTO on its own guardian starts no capture channel (buffs {string.Join(",", order.Buffs.Select(b => b.Name))})");
    }

    /// <summary>
    /// Report "capturing by hidden characters like Evelynn is broken". OdinCaptureChannel sets CastingBreaksStealth true, and the stealth buffs
    /// end themselves on a cast that says so (ShadowWalk BuffOnSpellCast: SpellVars.CastingBreaksStealth -> WillRemove). The use channel raised
    /// no cast hook, so Evelynn stayed stealthed and the point's tree skipped her ("Doesn't count if stealthed",
    /// OdinScriptBehaviorTrees.xml:148): her channel ran and the progress never moved.
    /// </summary>
    [Check("dominion.stealthed-capturer-is-revealed-and-moves-the-point", TimeoutSeconds = 300)]
    public static void TheStealthedCapturerIsRevealedAndMovesThePoint()
    {
        using var match = StartMatch(chaosChampion: "Evelynn");
        var (runtime, evelynn, guardian) = (match.Runtime, match.Chaos, match.Guardian);
        evelynn.Position = Offset(guardian, -300.0);
        evelynn.SpellRanks[1] = 1;
        evelynn.Mana = evelynn.MaxMana;
        var at = new Position3(evelynn.Position.X, 0.0, evelynn.Position.Z);
        TestAssert.True(runtime.Spells.HandleCastRequest(evelynn, new SpellCastRequest(1, false, at, at, 0), runtime.Combat.CurrentTime),
            "Evelynn casts Shadow Walk");
        TestAssert.True(Until(runtime, () => VisibilityRules.IsStealthed(evelynn), 2.0), "Shadow Walk stealths her");
        TestAssert.True(runtime.Spells.BeginCaptureChannel(evelynn, guardian), "Evelynn channels the neutral point");
        TestAssert.True(Until(runtime, () => !VisibilityRules.IsStealthed(evelynn), 0.5),
            $"the capture (CastingBreaksStealth) ends her stealth (buffs {string.Join(",", evelynn.Buffs.Select(b => b.Name))})");
        Advance(runtime, 3.0);
        var progress = (double)match.Quests.Captures[PointIndex].Variable("CaptureProgress")!;
        TestAssert.True(runtime.Spells.CaptureChannelTarget(evelynn) == guardian && progress < -5.0,
            $"her channel moves point C toward Chaos as a visible capturer's does (progress {progress:F1})");
    }

    /// <summary>
    /// Report "capturing one shrine as multiple people makes the channel bars disappear for some people, the progress bugs out". Pinned as the
    /// shipped scripts run it: two champions of one team each keep their own channel until the point is theirs, and the point moves by
    /// CapturePointMath's 0.85 a tick plus 0.175 per extra capturer (OdinScriptBehaviorTrees.xml:1565-1730); one capturer leaving takes only
    /// its own share. An enemy who joins cancels every channel on the point: OdinGuardianSuppression's stats hook clears itself while both teams
    /// hold a team stack, and its OnBuffDeactivate clears both team stacks, so each channel's ChannelingUpdateStats stops it (the wiki's Capture
    /// point page: "if an enemy champion initiates his or her own capture during the channel time, the channeling will be canceled").
    /// </summary>
    [Check("dominion.several-capturers-keep-their-channels-at-the-shipped-rate", TimeoutSeconds = 300)]
    public static void SeveralCapturersKeepTheirChannelsAtTheShippedRate()
    {
        using var match = StartMatch(secondChaos: true);
        var (runtime, chaos, guardian) = (match.Runtime, match.Chaos, match.Guardian);
        var second = runtime.Session.Participants.Select(seat => seat.Hero!).Single(hero => hero.Team == Team.Chaos && hero != chaos);
        chaos.Position = Offset(guardian, -300.0);
        second.Position = new Position2(guardian.Position.X - 300.0, guardian.Position.Z + 100.0);
        TestAssert.True(runtime.Spells.BeginCaptureChannel(chaos, guardian), "the first Chaos champion channels");
        TestAssert.True(runtime.Spells.BeginCaptureChannel(second, guardian), "the second Chaos champion channels");
        Advance(runtime, 2.5);
        double Progress() => (double)match.Quests.Captures[PointIndex].Variable("CaptureProgress")!;
        var start = Progress();
        Advance(runtime, 1.0);
        var two = start - Progress();
        TestAssert.True(runtime.Spells.CaptureChannelTarget(chaos) == guardian && runtime.Spells.CaptureChannelTarget(second) == guardian,
            "both channels go on together");
        chaos.Position = Offset(chaos, 50.0);
        Advance(runtime, 0.5);
        start = Progress();
        Advance(runtime, 1.0);
        var one = start - Progress();
        TestAssert.True(runtime.Spells.CaptureChannelTarget(chaos) is null && runtime.Spells.CaptureChannelTarget(second) == guardian,
            "the first capturer's move ends only its own channel");
        TestAssert.Near((0.85 + 0.175) / 0.85, two / one, 0.02, $"two capturers move the point 1.025/0.85 of one ({two:F2} and {one:F2} a second)");
        match.Order.Position = Offset(guardian, 300.0);
        TestAssert.True(runtime.Spells.BeginCaptureChannel(match.Order, guardian), "an Order champion joins");
        TestAssert.True(Until(runtime, () => runtime.Spells.CaptureChannelTarget(second) is null && runtime.Spells.CaptureChannelTarget(match.Order) is null, 1.0),
            "the contest cancels every channel on the point (OdinGuardianSuppression clears both team stacks)");
    }

    /// <summary>
    /// Report "Garrison is bugged? Should disappear after some time?". SummonerOdinGarrison's TargetExecute gives an allied guardian the
    /// SummonerOdinGarrison buff and an enemy one SummonerOdinGarrisonDebuff, each for 8 s (the wiki's Garrison page: "for 8 seconds"); each
    /// ends on time, with its 0x7F, and the Debuff's 80 % damage cut (BuffOnPreDealDamage x0.2) ends with it.
    /// </summary>
    [Check("dominion.garrison-on-a-point-ends-after-eight-seconds", TimeoutSeconds = 300)]
    public static void GarrisonOnAPointEndsAfterEightSeconds()
    {
        foreach (var enemy in new[] { false, true })
        {
            using var match = StartMatch();
            Capture(match, match.Order);
            var (runtime, guardian) = (match.Runtime, match.Guardian);
            var caster = enemy ? match.Chaos : match.Order;
            var buff = enemy ? "SummonerOdinGarrisonDebuff" : "SummonerOdinGarrison";
            caster.SummonerSpells[0] = "SummonerOdinGarrison";
            caster.SummonerCooldowns[0] = 0.0;
            caster.Position = Offset(guardian, enemy ? -1100.0 : 600.0);
            var at = new Position3(guardian.Position.X, 0.0, guardian.Position.Z);
            TestAssert.True(runtime.Spells.HandleCastRequest(caster, new SpellCastRequest(0, true, at, at, guardian.NetId), runtime.Combat.CurrentTime),
                $"{caster.Team} casts Garrison on Order's point");
            TestAssert.True(guardian.Buffs.Any(b => b.Name == buff), $"the guardian holds {buff}");
            Advance(runtime, 7.8);
            TestAssert.True(guardian.Buffs.Any(b => b.Name == buff), $"{buff} still holds at 7.8 s");
            Advance(runtime, 0.5);
            TestAssert.True(!guardian.Buffs.Any(b => b.Name == buff), $"{buff} ends at 8 s (buffs {string.Join(",", guardian.Buffs.Select(b => b.Name))})");
        }
    }

    /// <summary>The shipped Garrison's PAR per stats sample (SummonerOdinGarrison BuffOnUpdateStats BBIncPAR Delta 800).</summary>
    private const double GarrisonPar = 800.0;

    /// <summary>
    /// Batch 694: Garrison on an allied point that lost progress restores it ("Quickly restores the neutralized portion", the wiki's Garrison
    /// page). The shipped buff's BuffOnUpdateStats adds 800 PAR to the guardian on every stats sample (0.25 s) while its team holds the point; the
    /// guardian's PAR is the capture meter, which the point's tree writes from its CaptureProgress (batch 512, one writer), so the 800 is a
    /// progress of 800 / MaxPAR (50000) x 100 = 1.6 toward the owner per sample, on top of the owner's own regen. Both teams' points.
    /// </summary>
    [Check("dominion.garrison-restores-its-points-lost-progress", TimeoutSeconds = 300)]
    public static void GarrisonRestoresItsPointsLostProgress()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = StartMatch();
            var (runtime, guardian) = (match.Runtime, match.Guardian);
            var (owner, enemy) = team == Team.Order ? (match.Order, match.Chaos) : (match.Chaos, match.Order);
            var (ownerHome, enemyHome) = team == Team.Order ? (match.OrderHome, match.ChaosHome) : (match.ChaosHome, match.OrderHome);
            var side = team == Team.Order ? 1.0 : -1.0;
            Capture(match, owner);
            owner.Position = ownerHome;
            enemy.Position = Offset(guardian, -side * 300.0);
            TestAssert.True(runtime.Spells.BeginCaptureChannel(enemy, guardian), $"{enemy.Team} channels {team}'s point");
            Advance(runtime, 6.0);
            enemy.Position = enemyHome;
            Advance(runtime, 0.5);
            // The owner's progress, 0 to 100 for either team (the tree's CaptureProgress runs -100 Chaos to +100 Order).
            double Held() => side * (double)match.Quests.Captures[PointIndex].Variable("CaptureProgress")!;
            var start = Held();
            TestAssert.True(Owner(match) == team && start < 80.0, $"the failed neutralise left {team}'s point C with lost progress ({start:F1})");
            Advance(runtime, 1.0);
            var regen = Held() - start;
            owner.SummonerSpells[0] = "SummonerOdinGarrison";
            owner.SummonerCooldowns[0] = 0.0;
            owner.Position = Offset(guardian, side * 600.0);
            var at = new Position3(guardian.Position.X, 0.0, guardian.Position.Z);
            TestAssert.True(runtime.Spells.HandleCastRequest(owner, new SpellCastRequest(0, true, at, at, guardian.NetId), runtime.Combat.CurrentTime),
                $"{team} casts Garrison on its point");
            TestAssert.True(guardian.Buffs.Any(b => b.Name == "SummonerOdinGarrison"), "the guardian holds SummonerOdinGarrison");
            var before = Held();
            Advance(runtime, 2.0);
            var restored = Held() - before - 2.0 * regen;
            var perSample = GarrisonPar / guardian.MaxMana * 100.0;
            TestAssert.Near(8.0 * perSample, restored, perSample + 0.1,
                $"Garrison restores {perSample:F1} a stats sample, eight in 2 s (restored {restored:F2} beyond the regen {regen:F2} a second)");
            TestAssert.Near(guardian.MaxMana * Held() / 100.0, guardian.Mana, 1.0,
                $"the guardian's pool is the tree's progress (PAR {guardian.Mana:F0}, progress {Held():F1})");
            TestAssert.True(Until(runtime, () => Held() >= 100.0 - 1e-6, 5.5),
                $"the point is whole again inside Garrison's 8 s (progress {Held():F1})");
            TestAssert.True(Owner(match) == team, $"the point stays {team}'s");
        }
    }

    /// <summary>
    /// Batch 694: Garrison on an allied point speeds its guardian's shots for the buff's 8 s ("increases its attack speed for 8 seconds", the
    /// wiki's Garrison page). The shipped buff's BuffOnUpdateStats adds IncPercentCooldownMod -1, held at the global floor
    /// gcd_PercentCooldownModMinimum -0.4 (LEVELS/Map8/Constants.var), so the guardian's Spell1 (OdinGuardianSpellAttackCast, Cooldown 1) cools
    /// down in 0.6 s, and in its Cooldown again once the buff has ended.
    /// </summary>
    [Check("dominion.garrison-speeds-the-guardians-shots-for-its-eight-seconds", TimeoutSeconds = 300)]
    public static void GarrisonSpeedsTheGuardiansShotsForItsEightSeconds()
    {
        using var match = StartMatch();
        Capture(match, match.Order);
        var (runtime, guardian, order) = (match.Runtime, match.Guardian, match.Order);
        match.Chaos.Position = match.ChaosHome;
        var spell = GameData.Default.Champion(GuardianName).SpellNames[0];
        var cooldown = GameData.Default.Spell(spell, 5).Cooldowns[0];
        runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion, Team.Chaos,
            Offset(guardian, -580.0)) { Health = 1e7, MaxHealth = 1e7, Name = "Dummy" });
        Advance(runtime, 2.0);
        order.SummonerSpells[0] = "SummonerOdinGarrison";
        order.SummonerCooldowns[0] = 0.0;
        order.Position = Offset(guardian, 600.0);
        var at = new Position3(guardian.Position.X, 0.0, guardian.Position.Z);
        TestAssert.True(runtime.Spells.HandleCastRequest(order, new SpellCastRequest(0, true, at, at, guardian.NetId), runtime.Combat.CurrentTime),
            "Order casts Garrison on its point");
        var casts = new List<(double At, bool Garrisoned)>();
        var ready = CastAt(guardian);
        for (var tick = 0; tick < (int)Math.Round(13.0 / Step); tick++)
        {
            runtime.Tick(Step);
            if (CastAt(guardian) > ready + 1e-9)
                casts.Add((runtime.World.Clock.Now(), guardian.Buffs.Any(b => b.Name == "SummonerOdinGarrison")));
            ready = CastAt(guardian);
        }
        List<double> Intervals(bool garrisoned) => casts.Zip(casts.Skip(1))
            .Where(pair => pair.First.Garrisoned == garrisoned && pair.Second.Garrisoned == garrisoned).Select(pair => pair.Second.At - pair.First.At).ToList();
        var during = Intervals(true);
        var after = Intervals(false);
        double Steps(double seconds) => Math.Ceiling(seconds / Step - 1e-6) * Step;
        TestAssert.True(during.Count >= 8 && after.Count >= 3, $"the guardian fires through and after Garrison ({during.Count} and {after.Count} intervals)");
        TestAssert.True(during.All(interval => Math.Abs(interval - Steps(cooldown * 0.6)) < 1e-6),
            $"one {spell} every {cooldown * 0.6:F2} s under Garrison (intervals {string.Join(", ", during.Select(v => v.ToString("F3")))})");
        TestAssert.True(after.All(interval => Math.Abs(interval - Steps(cooldown)) < 1e-6),
            $"one {spell} every {cooldown:F2} s once Garrison has ended (intervals {string.Join(", ", after.Select(v => v.ToString("F3")))})");
    }
}
