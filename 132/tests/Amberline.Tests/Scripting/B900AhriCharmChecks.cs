using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Simulation.Buffs;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 900 (owner, 19 November 2026): "Charming a target with Ahri E makes them teleport back to the location they were charmed at
/// sometimes". docs/maintenance/2026-11-19-batch-900-fear-charm.md.
/// </summary>
public static class B900AhriCharmChecks
{
    // The shipped AhriSeduce (added as BUFF_Stun) writes only SetCanAttack, slows its holder and orders it AI_MOVETO Ahri. Red before: the stun
    // label held the champion on the server while its client walked the published route, and BBStopMove at the end put the client's copy back
    // 230 to 280 u (a player's click in between answered with a stop on the charm point instead); a bot did not walk at all.
    [Check("scripting.b900-ahri-charm-walks-the-champion-to-ahri-with-no-snap-back")]
    public static void CharmWalksTheChampionToAhriWithNoSnapBack()
    {
        Row("champion 600 away", bot: false, clicks: false);
        Row("champion clicking away every 0.25 s", bot: false, clicks: true);
        Row("bot champion 600 away", bot: true, clicks: false);

        static void Row(string name, bool bot, bool clicks)
        {
            using var match = new Match("Ahri", foeChampion: bot ? "Annie" : "Orianna", foeIsBot: bot);
            match.Hero.Facing = new Position2(1, 0);
            match.Foe.Position = match.Hero.Position + new Position2(600, 0);
            var wire = Observe(match, Team.Order, match.Hero);
            match.Advance(0.2);
            match.Learn(5);
            match.Refill();
            TestAssert.True(match.Cast(2, match.Foe), $"{name}: Charm is cast at the champion in front of Ahri");
            bool stunned = false, couldCast = false, couldAttack = false;
            var clickAt = 0.0;
            var run = B900FearCharmChecks.Follow(match, wire, match.Foe, "AhriSeduce", 3.5, () =>
            {
                if (Buff(match.Foe, "AhriSeduce") is not { } charm) return;
                stunned |= charm.DisablesActions;
                // The charm's taunt (BBApplyTaunt for the same TauntLength) is what keeps the champion from casting (SpellOwnerGate: taunted).
                couldCast |= match.Foe.Buffs.Any(buff => buff.Type == Amberline.Core.Buffs.BuffType.Taunt) &&
                             Amberline.Simulation.Spells.SpellOwnerGate.CanUse(match.Foe, null);
                couldAttack |= CharacterState.Of(match.Foe).CanAttack;
                if (!clicks || match.Now < clickAt) return;
                clickAt = match.Now + 0.25;
                // The champion's own move order (wire order 2) away from Ahri, as its player's client sends it.
                var packet = new byte[22];
                packet[0] = 0x76;
                packet[5] = 2;
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), match.Foe.NetId);
                var away = match.Foe.Position + new Position2(600, 300);
                BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(6), (float)away.X);
                BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(14), (float)away.Z);
                match.Runtime.Session.DispatchGameplay(3, 1, packet, match.Runtime.Dependencies.Gameplay);
            });
            TestAssert.False(stunned, $"{name}: the AhriSeduce row reads as a stun (its script never takes movement)");
            TestAssert.False(couldCast, $"{name}: the charmed champion could cast while its taunt lasted");
            TestAssert.False(couldAttack, $"{name}: the charmed champion could attack (AhriSeduce SetCanAttack false)");
            B900FearCharmChecks.Expect(run, name, minPath: 150.0, minRoutes: 1);
            var closer = B900FearCharmChecks.Distance(run.Start, match.Hero.Position) - B900FearCharmChecks.Distance(run.End, match.Hero.Position);
            TestAssert.True(closer >= 150.0, $"{name}: the charm took the champion only {closer:0} u closer to Ahri");
            TestAssert.Equal(0, match.Module.ScriptFailures.Count, $"{name}: script failures");
        }
    }
}
