using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// Batch 689 (community report, 4 November 2026: "Using Katarina E on a target that kills it returns katarina to previous position").
    /// ShadowStep.luaobj's TargetExecute teleports her past the target (BBTeleportToPosition) before its BBApplyDamage, and orders AI_ATTACKTO
    /// only after it. The server keeps her at the landing whether the blink kills or not, from a stand, a walk or an attack order, and her
    /// client gets the teleport (0x64) to it. This pins the server half; the report's return is not reproduced here (see the batch record).
    /// </summary>
    [Check("scripting.bb-katarina-shunpo-kill-keeps-her-at-the-landing")]
    public static void ShunpoKillKeepsKatarinaAtTheLanding()
    {
        foreach (var prior in new[] { "stand", "walk", "attack" })
        foreach (var health in new[] { 10.0, 5000.0 })
        {
            using var match = new Match("Katarina", Team.Order);
            var kat = match.Hero(0);
            var foe = match.Hero(1);
            kat.Position = new Position2(7000, 7000);
            foe.Position = new Position2(7400, 7000);
            foe.Health = health;
            match.Runtime.Tick(0.5);
            if (prior == "walk") match.Move(0, new Position2(7000, 6000));
            else if (prior == "attack") match.Attack(0, foe);
            match.Runtime.Tick(0.3);
            var start = kat.Position;
            match.Runtime.Delivery.Flush(); match.Clear();
            match.Cast(0, 2, foe);
            match.Runtime.Tick(1.0 / 30.0);
            match.Runtime.Delivery.Flush();
            var landing = kat.Position;
            var label = $"{prior}, foe health {health}";
            TestAssert.True(Math.Abs(landing.X - start.X) > 350.0, $"{label}: Shunpo blinks her past the foe ({start.X:0} -> {landing.X:0})");
            TestAssert.Equal(health < 100.0, !foe.Alive, $"{label}: the blink's damage kills only the low foe");
            TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == VisionPackets.TeleportOpcode),
                $"{label}: her client gets the teleport");
            for (var step = 0; step < 45; step++) match.Runtime.Tick(1.0 / 30.0);
            TestAssert.True(Math.Abs(kat.Position.X - landing.X) < 1.0 && Math.Abs(kat.Position.Z - landing.Z) < 1.0,
                $"{label}: she stays at the landing ({kat.Position.X:0}, {kat.Position.Z:0})");
        }
    }
}
