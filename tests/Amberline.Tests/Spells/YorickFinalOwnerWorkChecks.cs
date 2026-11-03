using Amberline.Core;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-yorick-final-life-retires-owner-work")]
    public static void YorickSecondDeathRetiresUnfinishedEzrealCast()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Yorick", team, ally: true,
                allyChampion: "Ezreal");
            var runtime = match.Runtime;
            var ezreal = match.Hero(2);
            var enemy = match.Hero(1);
            enemy.Position = new Position2(9800, 7000);
            match.Cast(0, 3, ezreal);
            runtime.Tick(1.0);
            TestAssert.True(ezreal.Buffs.Any(buff => buff.Name == "YorickReviveAllySelf"),
                $"{team}: real Yorick R marked Ezreal");
            TestAssert.True(runtime.Combat.Kill(ezreal, enemy.NetId));
            runtime.Tick(1.1);
            var secondLife = ezreal.YorickRevival ??
                throw new InvalidOperationException("Yorick did not create a second life");
            TestAssert.True(ezreal.Alive,
                $"{team}: Ezreal can cast in the temporary life");
            for (var frame = 0; frame < 150 && ezreal.SummonerCooldowns[0] > 0.0; frame++)
                runtime.Tick(1.0 / 30.0);
            TestAssert.True(ReferenceEquals(secondLife, ezreal.YorickRevival) && ezreal.Alive &&
                            ezreal.SummonerCooldowns[0] == 0.0,
                $"{team}: Yorick's four-second summoner lock expires before finalization");

            // A launched Q aimed into open distance belongs to its original
            // recipient-independent flight and must survive caster death.
            match.CastGround(2, enemy.Position, slot: 0);
            runtime.Tick(.3);
            var spells = runtime.Spells;
            var launchedQ = EzrealFlights.InFlight(spells, ezreal).Single(missile => missile.Slot == 0);
            TestAssert.False(EzrealFlights.WindingUp(spells, ezreal),
                $"{team}: Q launched before the temporary life ended");

            match.CastGround(2, enemy.Position, slot: 3);
            var unfinishedR = EzrealFlights.Of(spells, ezreal).Single(item => item.Slot == 3 && !item.Launched);
            TestAssert.True(unfinishedR.CastWindupRemaining > 1.0 / 30.0,
                $"{team}: R is still unlaunched at finalization");
            var oldMissileId = EzrealFlights.MissileId(unfinishedR);
            // A kill of the unit in its zombie life ends the life (MatchGameplayHooks.EndZombieLife), as the script's own ForceDead does.
            runtime.Combat.Kill(ezreal, enemy.NetId);
            TestAssert.True(!ezreal.Alive && ezreal.YorickRevival is null,
                $"{team}: the kill finalized the temporary life");
            TestAssert.False(EzrealFlights.WindingUp(spells, ezreal),
                $"{team}: the death hook retired the unfinished R cast");
            TestAssert.True(EzrealFlights.InFlight(spells, ezreal).Any(missile => missile.MissileNetId == launchedQ.MissileNetId),
                $"{team}: a previously launched Q to another recipient remains in flight");
            TestAssert.False(EzrealFlights.InFlight(spells, ezreal).Any(missile => EzrealFlights.MissileId(missile) == oldMissileId),
                $"{team}: unlaunched R was never inserted into the flight list");

            Revive(match, seat: 2, ezreal);
            runtime.Tick(1.1);
            TestAssert.False(EzrealFlights.InFlight(spells, ezreal).Any(missile => EzrealFlights.MissileId(missile) == oldMissileId),
                $"{team}: old temporary-life R cannot launch after ordinary Revive");
            ezreal.SpellCooldowns[3] = 0.0;
            ezreal.Mana = ezreal.MaxMana;
            match.CastGround(2, enemy.Position, slot: 3);
            var newMissileId = EzrealFlights.Of(spells, ezreal).Where(item => item.Slot == 3).Select(EzrealFlights.MissileId).FirstOrDefault();
            TestAssert.True(newMissileId != 0 && newMissileId != oldMissileId,
                $"{team}: ordinary new life may begin a fresh R cast");
            runtime.Tick(1.1);
            TestAssert.True(EzrealFlights.InFlight(spells, ezreal).Any(missile => missile.MissileNetId == newMissileId),
                $"{team}: the new-life R launches through its own cast delay");
        }
    }
}
