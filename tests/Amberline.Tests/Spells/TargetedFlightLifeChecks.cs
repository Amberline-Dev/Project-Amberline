using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Data;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-homing-flights-retire-at-recipient-death")]
    public static void HomingChampionAndBoxFlightsDoNotFollowRevive()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var (champion, slot) in new[]
                 {
                     ("Annie", 0), ("Shaco", 2), ("Janna", 1),
                     ("Ezreal", 2), ("Vayne", 2),
                 })
        {
            using var match = new Match(champion, team);
            var caster = match.Hero(0);
            var recipient = match.Hero(1);
            recipient.Position = caster.Position + new Position2(400, 0);
            match.Runtime.Delivery.Flush();
            if (champion == "Ezreal") match.CastGround(0, caster.Position, slot);
            else match.Cast(0, slot, recipient);
            for (var frame = 0; frame < 30 && !FlightLaunched(match, champion, recipient.NetId); frame++)
                match.Runtime.Tick(1.0 / 30.0);
            TestAssert.True(FlightLaunched(match, champion, recipient.NetId),
                $"{team}/{champion}: a real target-bound missile has launched");
            var oldPoint = recipient.Position;
            match.Clear();
            TestAssert.True(match.Runtime.Combat.Kill(recipient, caster.NetId));
            match.Runtime.Delivery.Flush();
            // Retail rule (SpellSystem.DestroyTargetedProjectiles; the client's homing missile follows the target object, dead or
            // alive): the missile in the air is not destroyed by its target's death, it flies on to the corpse.
            TestAssert.True(FlightExists(match, champion, recipient.NetId),
                $"{team}/{champion}: the flight is still in the air after its target died");
            foreach (var capture in match.Captures)
                TestAssert.False(capture.Packets.Any(packet => Opcode(packet) == 0x5D),
                    $"{team}/{champion}: no missile destroy goes out at the target's death");
            // The same NetId lives again (Summoner Revive): the old flight belongs to the old life and goes with it.
            Revive(match, seat: 1, recipient);
            TestAssert.False(FlightExists(match, champion, recipient.NetId),
                $"{team}/{champion}: the respawn retires the old-life flight");
            recipient.Position = oldPoint;
            var damage = recipient.MatchStatistics.TotalDamageTaken;
            match.Runtime.Tick(.8);
            TestAssert.Equal(damage, recipient.MatchStatistics.TotalDamageTaken,
                $"{team}/{champion}: same-NetId Revive is not struck by an old flight");
        }

        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Shaco", team);
            var caster = match.Hero(0);
            var recipient = match.Hero(1);
            recipient.Position = caster.Position + new Position2(350, 0);
            match.CastGround(0, caster.Position + new Position2(100, 0), slot: 1);
            for (var frame = 0; frame < 150 &&
                 !match.Runtime.Spells.SharedProjectiles.Any(projectile =>
                     projectile.Snapshot.SpellName == "ShacoBoxSpell" &&
                     projectile.Snapshot.TargetNetId == recipient.NetId &&
                     projectile.Snapshot.Launched); frame++)
                match.Runtime.Tick(1.0 / 30.0);
            TestAssert.True(match.Runtime.Spells.SharedProjectiles.Any(projectile =>
                    projectile.Snapshot.SpellName == "ShacoBoxSpell" &&
                    projectile.Snapshot.TargetNetId == recipient.NetId &&
                    projectile.Snapshot.Launched),
                $"{team}: a live Jack in the Box launched a targeted shot");
            var oldPoint = recipient.Position;
            TestAssert.True(match.Runtime.Combat.Kill(recipient, caster.NetId));
            TestAssert.True(match.Runtime.Spells.SharedProjectiles.Any(projectile =>
                    projectile.Snapshot.TargetNetId == recipient.NetId),
                $"{team}: the launched box shot flies on to the corpse (retail rule)");
            var box = match.Runtime.World.Entities.Values.Single(entity =>
                entity.Kind == EntityKind.ShacoBox && entity.Alive);
            TestAssert.True(match.Runtime.Combat.Kill(box, 0),
                $"{team}: no new box shot can obscure the old launched-shot control");
            Revive(match, seat: 1, recipient);
            TestAssert.False(match.Runtime.Spells.SharedProjectiles.Any(projectile =>
                    projectile.Snapshot.TargetNetId == recipient.NetId),
                $"{team}: the respawn retires the old box shot");
            recipient.Position = oldPoint;
            var damage = recipient.MatchStatistics.TotalDamageTaken;
            match.Runtime.Tick(.8);
            TestAssert.Equal(damage, recipient.MatchStatistics.TotalDamageTaken,
                $"{team}: the old box shot cannot strike the revived recipient");
        }
    }

    /// <summary>
    /// The retail rule for a target that dies under a launched missile (SpellSystem.DestroyTargetedProjectiles): the client's homing
    /// missile (the missile's update routine) needs only the target object, so the missile flies on to the corpse
    /// and ends there. Every hand-written homing missile does the same and hits nothing: no destroy at the death, no damage, no buff.
    /// </summary>
    [Check("spells-live-homing-flights-fly-on-to-the-corpse-without-a-hit")]
    public static void HomingFlightsEndAtTheCorpseWithoutAHit()
    {
        foreach (var (champion, slot) in new[] { ("Annie", 0), ("Shaco", 2), ("Janna", 1), ("Ezreal", 2), ("Vayne", 2), ("Shaco", 1) })
        {
            using var match = new Match(champion, Team.Order);
            var caster = match.Hero(0);
            var recipient = match.Hero(1);
            recipient.Position = caster.Position + new Position2(champion == "Shaco" && slot == 1 ? 350 : 450, 0);
            match.Runtime.Delivery.Flush();
            if (champion == "Ezreal") match.CastGround(0, caster.Position, slot);
            else if (champion == "Shaco" && slot == 1) match.CastGround(0, caster.Position + new Position2(100, 0), slot);
            else match.Cast(0, slot, recipient);
            bool Launched() => champion == "Shaco" && slot == 1
                ? match.Runtime.Spells.SharedProjectiles.Any(projectile => projectile.Snapshot.SpellName == "ShacoBoxSpell" &&
                      projectile.Snapshot.TargetNetId == recipient.NetId && projectile.Snapshot.Launched)
                : FlightLaunched(match, champion, recipient.NetId);
            bool InAir() => champion == "Shaco" && slot == 1
                ? match.Runtime.Spells.SharedProjectiles.Any(projectile => projectile.Snapshot.TargetNetId == recipient.NetId)
                : FlightExists(match, champion, recipient.NetId);
            for (var frame = 0; frame < 150 && !Launched(); frame++) match.Runtime.Tick(1.0 / 30.0);
            TestAssert.True(Launched(), $"{champion}/{slot}: a target-bound missile has launched");
            var corpseAt = recipient.Position;
            match.Clear();
            TestAssert.True(match.Runtime.Combat.Kill(recipient, caster.NetId));
            var taken = recipient.MatchStatistics.TotalDamageTaken;
            var buffs = recipient.Buffs.Count;
            match.Runtime.Delivery.Flush();
            TestAssert.True(InAir(), $"{champion}/{slot}: the missile flies on after its target died");
            foreach (var capture in match.Captures)
                TestAssert.False(capture.Packets.Any(packet => Opcode(packet) == 0x5D),
                    $"{champion}/{slot}: no destroy goes out at the death");
            for (var frame = 0; frame < 90 && InAir(); frame++) match.Runtime.Tick(1.0 / 30.0);
            TestAssert.False(InAir(), $"{champion}/{slot}: the missile ends at the corpse");
            TestAssert.True(!recipient.Alive && recipient.Position == corpseAt, $"{champion}/{slot}: the corpse stayed where it fell");
            TestAssert.Equal(taken, recipient.MatchStatistics.TotalDamageTaken, $"{champion}/{slot}: a corpse takes no hit");
            TestAssert.Equal(buffs, recipient.Buffs.Count, $"{champion}/{slot}: a corpse gets no buff from the missile");
        }
    }

    [Check("spells-live-homing-flight-death-during-shared-phase-and-yorick-life")]
    public static void TargetDeathRetiresLaterSharedShotsAndTemporaryLifeShots()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team, ally: true,
                enemyChampion: "Yorick", allyChampion: "Annie");
            var target = match.Hero(1);
            match.Cast(1, 3, target);
            match.Runtime.Tick(1.0);
            TestAssert.True(target.Buffs.Any(buff => buff.Name == "YorickReviveAllySelf"),
                $"{team}: real Yorick R has marked its owner");
            target.Health = 1.0;
            match.Cast(0, 0, target);
            match.Cast(2, 0, target);
            TestAssert.Equal(2, match.Runtime.Spells.SharedProjectiles.Count(projectile =>
                projectile.Snapshot.TargetNetId == target.NetId),
                $"{team}: two launched Disintegrates enter the same ordered shared phase");
            for (var frame = 0; frame < 30 && target.YorickRevival is null; frame++)
                match.Runtime.Tick(1.0 / 30.0);
            TestAssert.True(target.YorickRevival is not null,
                $"{team}: the first projectile caused an actual Yorick death transition");
            TestAssert.False(match.Runtime.Spells.SharedProjectiles.Any(projectile =>
                    projectile.Snapshot.TargetNetId == target.NetId),
                $"{team}: the later processing-array projectile was retired on that death");

            match.Runtime.Tick(1.1);
            var secondLife = target.YorickRevival ??
                throw new InvalidOperationException("Yorick temporary life was lost");
            TestAssert.True(target.Alive && target.Buffs.Any(buff => buff.Name == "YorickRAZombie"),
                $"{team}: the recipient reached its real temporary life");
            // Yorick R locks the victim's summoners for four seconds. Let that
            // real lock expire inside the ten-second temporary life before
            // testing an immediate Revive after its final death.
            for (var frame = 0; frame < 150 && target.SummonerCooldowns[0] > 0.0; frame++)
                match.Runtime.Tick(1.0 / 30.0);
            TestAssert.True(ReferenceEquals(secondLife, target.YorickRevival) && target.Alive &&
                            target.SummonerCooldowns[0] == 0.0,
                $"{team}: the real summoner lock expires during Yorick's temporary life");
            var caster = match.Hero(0);
            caster.SpellCooldowns[0] = 0.0;
            match.Cast(0, 0, target);
            TestAssert.True(match.Runtime.Spells.SharedProjectiles.Any(projectile =>
                projectile.Snapshot.TargetNetId == target.NetId));
            // A kill of the unit in its zombie life ends the life (MatchGameplayHooks.EndZombieLife), as the script's own ForceDead does.
            match.Runtime.Combat.Kill(target, caster.NetId);
            TestAssert.True(!target.Alive && target.YorickRevival is null,
                $"{team}: the end of the zombie life ended the temporary life");
            TestAssert.False(match.Runtime.Spells.SharedProjectiles.Any(projectile =>
                    projectile.Snapshot.TargetNetId == target.NetId),
                $"{team}: the finalizer retired the targeted temporary-life flight");
            Revive(match, seat: 1, target);
            target.Position = caster.Position + new Position2(200, 0);
            var damage = target.MatchStatistics.TotalDamageTaken;
            match.Runtime.Tick(.8);
            TestAssert.Equal(damage, target.MatchStatistics.TotalDamageTaken,
                $"{team}: ordinary Revive is not struck by temporary-life Q");

            using var earlier = new Match("Annie", team, ally: true,
                allyChampion: "Annie");
            var lethalTarget = earlier.Hero(1);
            lethalTarget.SpellBlock = 0.0;
            earlier.Cast(0, 0, lethalTarget);
            earlier.Cast(2, 0, lethalTarget);
            var acceptedShots = earlier.Runtime.Spells.SharedProjectiles
                .Where(projectile => projectile.Snapshot.TargetNetId == lethalTarget.NetId)
                .Select(projectile => projectile.Snapshot).ToArray();
            TestAssert.Equal(2, acceptedShots.Length,
                $"{team}: both real Annie Q casts retain their accepted damage snapshots");
            // 19 October 2026 (batch 86): Annie's Q is her shipped script, so the queued shot carries no damage figure (Disintegrate.TargetExecute
            // computes it on arrival); the thresholds are the script's damage of each caster: the rank's base damage plus 0.7 of her ability power,
            // unmitigated at zero spell block.
            var disintegrate = GameData.Default.Spell("Disintegrate", 5);
            double DisintegrateDamage(Entity annie) =>
                disintegrate.EffectNumber(1, annie.SpellRanks[0]) + disintegrate.Coefficient * annie.AbilityPower;
            var firstDamage = DisintegrateDamage(earlier.Hero(0));
            var secondDamage = DisintegrateDamage(earlier.Hero(2));
            lethalTarget.Health = firstDamage + secondDamage / 2.0;
            TestAssert.True(firstDamage > 0.0 && secondDamage > 0.0 &&
                            firstDamage < lethalTarget.Health &&
                            lethalTarget.Health <= firstDamage + secondDamage &&
                            lethalTarget.SpellBlock == 0.0,
                $"{team}: first Q cannot kill; second Q crosses the unmitigated health threshold");
            var firstMissile = acceptedShots[0].MissileNetId;
            TestAssert.True(firstMissile != 0, $"{team}: the first Q owns a real missile ID");
            var lateView = earlier.Runtime.Session.Participants[0].View;
            // Model an observer whose late first-sight refresh owns this ID,
            // so its native impact retirement is visible as one 0x5D.
            lateView.Effects.Add(firstMissile);
            lateView.LateMissiles.Add(firstMissile);
            earlier.Clear();
            const double step = 1.0 / 30.0;
            for (var frame = 0; frame < 30 &&
                 earlier.Runtime.Spells.SharedProjectiles.All(projectile =>
                     projectile.Snapshot.CastRemaining > step + 1e-9); frame++)
                earlier.Runtime.Tick(1.0 / 30.0);
            TestAssert.True(lethalTarget.Alive &&
                            earlier.Runtime.Spells.SharedProjectiles.Count(projectile =>
                                projectile.Snapshot.TargetNetId == lethalTarget.NetId) == 2 &&
                            earlier.Runtime.Spells.SharedProjectiles.All(projectile =>
                                projectile.Snapshot.CastRemaining <= step + 1e-9),
                $"{team}: both Q impacts are queued for the next shared phase");
            // The script's missile flies to the target and Disintegrate.TargetExecute deals the damage on arrival (the hand-written Q resolved
            // inside the shared phase after its cast time), so the impacts land some steps later, the first one nonlethal.
            for (var frame = 0; frame < 90 && lethalTarget.Alive; frame++) earlier.Runtime.Tick(step);
            TestAssert.False(lethalTarget.Alive,
                $"{team}: first Q was nonlethal and the second ended the target life");
            TestAssert.Equal(1, earlier.Captures[0].Packets.Count(packet =>
                Opcode(packet) == 0x5D &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == firstMissile),
                $"{team}: an earlier completed flight has one late-view destroy");

            // Delivery deduplicates terminal packets by effect ID, so the
            // wire count above alone cannot reveal an extra internal destroy
            // attempt. Probe the shared queue immediately after one real Q
            // completes in its processing snapshot, with no other flight to
            // this target to confound the retirement count.
            using var completed = new Match("Annie", team);
            var survivor = completed.Hero(1);
            completed.Cast(0, 0, survivor);
            for (var frame = 0; frame < 30 &&
                 completed.Runtime.Spells.SharedProjectiles.Single().Snapshot.CastRemaining > step + 1e-9;
                 frame++)
                completed.Runtime.Tick(step);
            TestAssert.True(completed.Runtime.Spells.SharedProjectiles.Single().Snapshot.CastRemaining <=
                            step + 1e-9,
                $"{team}: the standalone Q is ready to finish in one shared phase");
            // Annie's Q is a script flight now: the shared entry ends at the launch and the damage lands when the missile arrives. The retirement
            // probe therefore runs once the flight has landed: a completed flight is not retired a second time by the target-bound helper.
            for (var frame = 0; frame < 90 && survivor.MatchStatistics.TotalDamageTaken <= 0.0; frame++)
                completed.Runtime.Tick(step);
            TestAssert.True(survivor.Alive && survivor.MatchStatistics.TotalDamageTaken > 0,
                $"{team}: the first current-life Q completed before the retirement probe");
            var probe = new TargetRetirementProbe(survivor.NetId);
            completed.Runtime.Spells.EnqueueSharedProjectile(probe);
            completed.Runtime.Tick(step);
            TestAssert.Equal(0, probe.RetiredAgain,
                $"{team}: a completed flight cannot be destroyed twice");
        }
    }

    [Check("spells-live-lee-q2-follow-stops-when-target-life-ends")]
    public static void LeeFollowDashDoesNotAcquireRevivedTargetLife()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team);
            var lee = match.Hero(0);
            var target = match.Hero(1);
            target.Position = lee.Position + new Position2(370, 0);
            match.CastGround(0, target.Position);
            match.Runtime.Tick(.6);
            TestAssert.True(target.Buffs.Any(buff => buff.Name.StartsWith("BlindMonkQOne")),
                $"{team}: real Sonic Wave marked the target");
            match.Cast(0, 0, target);
            var dash = lee.ForcedMove is { Effect: "BlindMonkQTwoDash" } started ? started :
                throw new InvalidOperationException("Q2 did not start its follow dash");
            TestAssert.True(!dash.Completed && dash.FollowNetId == target.NetId);
            var oldPoint = target.Position;
            TestAssert.True(match.Runtime.Combat.Kill(target, lee.NetId));
            // 19 October 2026: BBMoveToUnit follows by a per-frame test of its target (MovementSystem.AdvanceForcedMove), no longer by a death
            // hook of the hand-written module: the movement step that sees the corpse ends the dash. A respawn takes seconds, never the same frame.
            match.Runtime.Movement.Tick(.01);
            TestAssert.True(dash.Completed && !dash.Succeeded,
                $"{team}: target death stops Q2 before same-NetId Revive");
            Revive(match, seat: 1, target);
            target.Position = oldPoint;
            var damage = target.MatchStatistics.TotalDamageTaken;
            var before = lee.Position;
            match.Runtime.Tick(.1);
            TestAssert.True(lee.ForcedMove is null && lee.Position == before,
                $"{team}: Q2 owner and movement cleanup do not chase the new life");
            TestAssert.Equal(damage, target.MatchStatistics.TotalDamageTaken,
                $"{team}: Q2 cannot hit a revived target");
        }
    }

    [Check("items-live-targeted-flight-stays-on-recipient-life")]
    public static void TargetedItemMissilesDoNotFollowSameNetIdRevive()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var item in new[] { 3128, 3146 })
        {
            using var match = new Match("Annie", team);
            var caster = match.Hero(0);
            var recipient = match.Hero(1);
            caster.Inventory = [new InventorySlot(item), null, null, null, null, null];
            recipient.Position = caster.Position + new Position2(400, 0);
            match.Cast(0, 4, recipient);
            var oldPoint = recipient.Position;
            TestAssert.True(match.Runtime.Combat.Kill(recipient, caster.NetId));
            Revive(match, seat: 1, recipient);
            recipient.Position = oldPoint;
            var damage = recipient.MatchStatistics.TotalDamageTaken;
            match.Runtime.Tick(.5);
            TestAssert.Equal(damage, recipient.MatchStatistics.TotalDamageTaken,
                $"{team}/{item}: the launched item missile cannot hit new-life Revive");
        }
    }

    [Check("spells-live-targeted-death-retirement-preserves-launched-and-ground-controls")]
    public static void TargetDeathDoesNotRetireCasterDeathOrGroundAreaWork()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using (var launched = new Match("Annie", team))
            {
                var caster = launched.Hero(0);
                var recipient = launched.Hero(1);
                recipient.Position = caster.Position + new Position2(500, 0);
                launched.Cast(0, 0, recipient);
                for (var frame = 0; frame < 30 && !launched.Runtime.Spells.SharedProjectiles.Any(
                         projectile => projectile.Snapshot.TargetNetId == recipient.NetId &&
                                       projectile.Snapshot.Launched); frame++)
                    launched.Runtime.Tick(1.0 / 30.0);
                TestAssert.True(launched.Runtime.Spells.SharedProjectiles.Any(projectile =>
                    projectile.Snapshot.TargetNetId == recipient.NetId &&
                    projectile.Snapshot.Launched), $"{team}: Disintegrate launched before caster death");
                var before = recipient.MatchStatistics.TotalDamageTaken;
                TestAssert.True(launched.Runtime.Combat.Kill(caster, recipient.NetId));
                launched.Runtime.Tick(.8);
                TestAssert.True(recipient.MatchStatistics.TotalDamageTaken > before,
                    $"{team}: launched Q still hits the same living target after caster death");
            }

            using (var ground = new Match("Ezreal", team))
            {
                var caster = ground.Hero(0);
                var recipient = ground.Hero(1);
                recipient.Position = caster.Position + new Position2(800, 0);
                ground.CastGround(0, recipient.Position, slot: 0);
                for (var frame = 0; frame < 30 &&
                     !EzrealFlights.InFlight(ground.Runtime.Spells, caster).Any(missile =>
                         missile.Slot == 0 && missile.TargetNetId == 0); frame++)
                    ground.Runtime.Tick(1.0 / 30.0);
                var groundQ = EzrealFlights.InFlight(ground.Runtime.Spells, caster).SingleOrDefault(missile =>
                    missile.Slot == 0 && missile.TargetNetId == 0);
                TestAssert.True(groundQ.MissileNetId != 0 && recipient.Alive,
                    $"{team}: real ground-aimed Mystic Shot is in flight before recipient death");
                TestAssert.True(ground.Runtime.Combat.Kill(recipient, caster.NetId));
                TestAssert.True(EzrealFlights.InFlight(ground.Runtime.Spells, caster).Any(missile => missile.MissileNetId == groundQ.MissileNetId),
                    $"{team}: target death does not cancel an untargeted missile");
            }

            using var ga = new Match("Annie", team);
            var gaCaster = ga.Hero(0);
            var gaRecipient = ga.Hero(1);
            gaRecipient.Inventory = [new InventorySlot(3026), null, null, null, null, null];
            // The copy binds to its slot on the next item poll (BbItemModule.SyncInventory), the way a real buy does;
            // Items/3026.luaobj's UpdateSelfBuffActions then keeps the WillRevive marker up.
            ga.Runtime.Tick(0.01);
            ga.Runtime.HeroStats.Refresh(gaRecipient);
            ga.Cast(0, 0, gaRecipient);
            var deaths = gaRecipient.ChampionDeaths;
            TestAssert.True(ga.Runtime.Combat.ApplyHit(gaCaster.NetId,
                gaRecipient.NetId, 100000.0, DamageType.Magic, DamageSourceKind.SpellAoe));
            TestAssert.True(gaRecipient.Alive && gaRecipient.ChampionDeaths == deaths &&
                            ga.Runtime.Spells.SharedProjectiles.Any(projectile =>
                                projectile.Snapshot.TargetNetId == gaRecipient.NetId),
                $"{team}: Guardian Angel keeps its same-life targeted flight");
        }
    }

    private static bool FlightExists(Match match, string champion, uint targetNetId)
    {
        var hero = match.Hero(0);
        return champion switch
        {
            "Annie" or "Shaco" or "Janna" or "Vayne" or "Ezreal" => match.Runtime.Spells.SharedProjectiles.Any(projectile =>
                projectile.Snapshot.TargetNetId == targetNetId),
            _ => false,
        };
    }

    private static bool FlightLaunched(Match match, string champion, uint targetNetId)
    {
        var hero = match.Hero(0);
        return champion switch
        {
            // 19 October 2026 (batch 106): Two-Shiv Poison is the script's 0.25 s windup child; its entry waits in the queue (not launched) until the
            // cast time ends, so "launched" reads the same flag for Shaco as for every other flight.
            "Annie" or "Janna" or "Vayne" or "Ezreal" or "Shaco" => match.Runtime.Spells.SharedProjectiles.Any(projectile =>
                projectile.Snapshot.TargetNetId == targetNetId && projectile.Snapshot.Launched),
            _ => false,
        };
    }

    private sealed class TargetRetirementProbe(uint targetNetId) : ISharedSpellProjectile
    {
        public int RetiredAgain { get; private set; } = -1;
        public SharedProjectileSnapshot Snapshot => new("RetirementProbe", 0, 0, 0,
            0.0, true, Position2.Zero, 0.0);
        public bool Tick(SpellSystem spells, double dt)
        {
            RetiredAgain = spells.DestroyTargetedProjectiles(targetNetId);
            return false;
        }
    }
}
