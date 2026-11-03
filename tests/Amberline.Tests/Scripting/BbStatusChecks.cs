using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// The status bit of a unit (phase 3, wave 5 engine bundle E02, 19 October 2026): <c>BBSetStatus</c> outside a stats hook, from a spell's hook, a buff's
/// other hooks or on another unit, and <c>SetGhostProof</c>. The checks drive <see cref="BbBuffRuntime.WriteStatus"/> the way the host does for the
/// shipped sites (Leona's Zenith Blade, Renekton's unlock buff, Trundle's pillar, Jarvan's Cataclysm). Written, not run.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    private static (Match Match, BbBuffRuntime Buffs) StatusMatch()
    {
        var match = new Match("Shaco", Team.Order);
        var module = (BbChampionModule)match.Runtime.Dependencies.ChampionDispatcher.FindModule("Shaco")!;
        return (match, module.Buffs!);
    }

    [Check("scripting.bb-status-from-a-spell-hook-is-held-until-a-script-clears-it")]
    public static void StatusFromASpellHookIsHeldByTheUnitUntilCleared()
    {
        var (match, buffs) = StatusMatch();
        using (match)
        {
            var hero = match.Hero(0); var other = match.Hero(1);
            var slotsBefore = hero.Buffs.Count;
            // SelfExecute of Leona's Zenith Blade: CanAttack and CanMove off on the caster; its missile's SpellOnMissileEnd gives them back.
            buffs.WriteStatus(hero, BbSetStatus.SetCanMove, false, null, "SelfExecute");
            buffs.WriteStatus(hero, BbSetStatus.SetCanAttack, false, null, "SelfExecute");
            TestAssert.False(CharacterState.Of(hero).CanMove, "the unit cannot move");
            TestAssert.False(CharacterState.Of(hero).CanAttack, "or attack");
            TestAssert.True(CharacterState.Of(hero).CanCast, "and can still cast");
            TestAssert.True(CharacterState.Of(other).CanMove, "another unit is not touched");
            // A write of the rest value releases the bit, whichever hook writes it.
            buffs.WriteStatus(hero, BbSetStatus.SetCanMove, true, null, "SpellOnMissileEnd");
            buffs.WriteStatus(hero, BbSetStatus.SetCanAttack, true, null, "SpellOnMissileEnd");
            TestAssert.True(CharacterState.Of(hero).CanMove && CharacterState.Of(hero).CanAttack, "the missile's end gives both back");
            TestAssert.Equal(slotsBefore, hero.Buffs.Count, "and the statuses took no buff slot");
            // A write on a unit the script has just spawned (Maokai's sapling, Talon's test cube, Trundle's pillar) is held by that unit.
            buffs.WriteStatus(other, BbSetStatus.SetTargetable, false, null, "TargetExecute");
            TestAssert.True(other.AnyBuffOrStatus(buff => buff.Payload.Untargetable), "the other unit holds the status the hook wrote on it");
        }
    }

    [Check("scripting.bb-status-takes-no-buff-slot")]
    public static void StatusTakesNoBuffSlot()
    {
        // Batch 201: a status a script sets with no buff to hold it is a bit of the unit's state (retail: CanAttack and CanMove are
        // bits of the CharacterState word), not a buff. A full buff table still takes the write, and the status counts for no slot.
        var (match, buffs) = StatusMatch();
        using (match)
        {
            var hero = match.Hero(0);
            for (var index = 0; hero.Buffs.Count < 64; index++)
                TestAssert.True(match.Runtime.Spells.Buffs.Add(hero, $"StatusSlotProbe{index}", BuffType.Internal, 60.0, hero.NetId) is not null);
            TestAssert.Equal(64, hero.Buffs.Count, "the table is full");
            buffs.WriteStatus(hero, BbSetStatus.SetCanAttack, false, null, "SelfExecute");
            TestAssert.False(CharacterState.Of(hero).CanAttack, "a full table still takes the status");
            TestAssert.Equal(64, hero.Buffs.Count, "and the status takes no slot");
            buffs.WriteStatus(hero, BbSetStatus.SetCanAttack, true, null, "SelfExecute");
            TestAssert.True(CharacterState.Of(hero).CanAttack, "the rest write releases it");
            TestAssert.Equal(64, hero.Buffs.Count, "and still no slot is taken or freed");
        }
    }

    [Check("scripting.bb-status-carrier-ends-with-the-unit")]
    public static void StatusCarrierEndsWithTheUnit()
    {
        var (match, buffs) = StatusMatch();
        using (match)
        {
            var hero = match.Hero(0); var enemy = match.Hero(1);
            buffs.WriteStatus(enemy, BbSetStatus.SetCanCast, false, null, "SelfExecute");
            TestAssert.False(CharacterState.Of(enemy).CanCast, "Talon's Shadow Assault: the caster cannot cast");
            match.Runtime.Combat.Kill(enemy, hero.NetId);
            TestAssert.True(CharacterState.Of(enemy).CanCast, "death releases a status no buff holds");
            // A corpse takes no new status.
            buffs.WriteStatus(enemy, BbSetStatus.SetCanMove, false, null, "SelfExecute");
            TestAssert.True(CharacterState.Of(enemy).CanMove, "a dead unit holds none");
        }
    }

    [Check("scripting.bb-status-rest-write-releases-the-hold-of-a-buff")]
    public static void StatusRestWriteReleasesTheHoldOfABuff()
    {
        var (match, buffs) = StatusMatch();
        using (match)
        {
            var hero = match.Hero(0);
            match.Runtime.Spells.TickLateChampions(0);
            var holder = buffs.Find(hero, "Backstab") ?? throw new InvalidOperationException("Shaco's passive buff is a script buff");
            // Jarvan's Cataclysm: OnBuffActivate holds CanCast off, BuffOnUpdateActions writes it back on while the buff runs.
            buffs.WriteStatus(hero, BbSetStatus.SetCanCast, false, holder, "OnBuffActivate");
            // The activation is followed by the sample of the buff's stats in production (BbBuffRuntime.WriteStatus leaves it to the caller).
            // That sample is the 0.25 s stats grid of batch 150 part 2a now, not the every-update poll this wait was written against, so the
            // step has to cross a grid sample for the hold to reach the status: 0.3 s does that wherever the grid currently stands.
            match.Runtime.Tick(0.3);
            TestAssert.False(CharacterState.Of(hero).CanCast, "the buff holds the status");
            buffs.WriteStatus(hero, BbSetStatus.SetCanCast, true, holder, "BuffOnUpdateActions");
            TestAssert.True(CharacterState.Of(hero).CanCast, "its own later write releases it while the buff lives");
            // Another script's write of the rest value clears the one bit too (Renekton's unlock buff against the dash that left CanAttack off).
            buffs.WriteStatus(hero, BbSetStatus.SetCanAttack, false, holder, "BuffOnSpellCast");
            TestAssert.False(CharacterState.Of(hero).CanAttack, "any hook of the buff may hold the status");
            buffs.WriteStatus(hero, BbSetStatus.SetCanAttack, true, null, "OnBuffDeactivate");
            TestAssert.True(CharacterState.Of(hero).CanAttack, "a rest write from elsewhere releases the buff's hold");
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "Backstab"), "and the buff itself stays");
        }
    }

    // Batch 320: the client's SetRooted calls SetCanMove and SetCanAttack with !rooted; SetNetted
    // calls SetCanMove alone. Root.luaobj (the buff BBApplyRoot adds: Rammus's Powerball) writes SetRooted, so a root holds the attack too.
    [Check("scripting.bb-status-set-rooted-clears-move-and-attack")]
    public static void StatusSetRootedClearsMoveAndAttack()
    {
        var (match, buffs) = StatusMatch();
        using (match)
        {
            var hero = match.Hero(0);
            buffs.WriteStatus(hero, BbSetStatus.SetRooted, true, null, "OnBuffActivate");
            var state = CharacterState.Of(hero);
            TestAssert.False(state.CanMove, "a rooted unit cannot move");
            TestAssert.False(state.CanAttack, "nor attack (SetRooted clears bit 0 as well as bit 2)");
            TestAssert.True(state.CanCast, "and can still cast");
            TestAssert.Equal(0u, CharacterState.ActionWord(hero) & 0b101u, "the action word the client gets has bits 0 and 2 clear");
            buffs.WriteStatus(hero, BbSetStatus.SetRooted, false, null, "OnBuffDeactivate");
            TestAssert.True(CharacterState.Of(hero).CanMove && CharacterState.Of(hero).CanAttack, "SetRooted(false) gives both back");
        }
    }

    private sealed class StopCapture : Amberline.Network.Replication.IReplicationTransport
    {
        public readonly List<Amberline.Network.Protocol.PlaintextPacket> Packets = [];
        public void Send(Amberline.Network.Protocol.PlaintextPacket packet) => Packets.Add(packet);

        public int Routes(uint netId) => Packets.Count(packet => packet.Plaintext.Length >= 5 &&
            packet.Plaintext.Span[0] == Amberline.Network.Protocol.VisionPackets.WaypointListOpcode &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == netId);
    }

    // Batch 320 item 0: a script root sends its stop route when it starts, not again for every later status the unit gains while it
    // stands rooted (the client tells its listener only when a CanAttack/CanCast/CanMove bit changes).
    [Check("scripting.bb-status-script-root-sends-one-stop-not-one-per-status")]
    public static void StatusScriptRootSendsOneStop()
    {
        var (match, buffs) = StatusMatch();
        using (match)
        {
            var hero = match.Hero(0);
            var capture = new StopCapture();
            var participant = match.Runtime.Session.Participants.First();
            match.Runtime.Delivery.AddObserver(new Amberline.Network.Replication.ReplicationObserver((ulong)participant.ClientId + 1000,
                participant.Team, participant.Hero!.NetId, capture, ready: true, view: participant.View));
            match.Runtime.Tick(0.05);
            capture.Packets.Clear();
            buffs.WriteStatus(hero, BbSetStatus.SetCanMove, false, null, "SelfExecute");
            match.Runtime.Tick(0.05);
            var atStart = capture.Routes(hero.NetId);
            TestAssert.True(atStart >= 1, "the root sends its stop when it starts; sent: " +
                string.Join(" ", capture.Packets.Select(packet => $"0x{packet.Plaintext.Span[0]:X2}")));
            foreach (var status in new[] { BbSetStatus.SetGhosted, BbSetStatus.SetMagicImmune, BbSetStatus.SetSilenced })
            {
                buffs.WriteStatus(hero, status, true, null, "SelfExecute");
                match.Runtime.Tick(0.05);
            }
            TestAssert.Equal(atStart, capture.Routes(hero.NetId), "no further stop while the root stands");
        }
    }

    [Check("scripting.bb-status-ghost-proof-is-a-payload-flag")]
    public static void StatusGhostProofIsAPayloadFlagTheMovementRulesRead()
    {
        var (match, buffs) = StatusMatch();
        using (match)
        {
            var hero = match.Hero(0);
            TestAssert.False(hero.GhostProof, "a fresh unit is not ghost-proof");
            buffs.WriteStatus(hero, BbSetStatus.SetGhostProof, true, null, "SelfExecute");
            TestAssert.True(hero.GhostProof, "Entity.GhostProof reads the buff the status rides on");
            TestAssert.True((CharacterState.ActionWord(hero) & (1u << 11)) != 0, "bit 11 of the action word");
            buffs.WriteStatus(hero, BbSetStatus.SetGhostProof, false, null, "SelfExecute");
            TestAssert.False(hero.GhostProof, "a write of false releases it");
            TestAssert.True((CharacterState.ActionWord(hero) & (1u << 11)) == 0, "and the bit");
            hero.GhostProof = true;
            TestAssert.True(hero.GhostProof, "the stored flag the movement checks set directly still works");
        }
    }
}
