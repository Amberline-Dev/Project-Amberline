using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Network.Replication;

public sealed partial class ReplicationDelivery
{
    // Reconstruction: an attack or cast start the dying unit queued earlier in the step of its death is not sent. Since batch 692 Flush sends
    // the death in its place in the step (it was sent ahead of the step, where such a start played on the corpse); the start is still retired,
    // so no swing or cast begins on the client in the burst that kills its unit (the stock client's death routine, the client, would only
    // finish it again).
    private void DropPreDeathStarts()
    {
        // Retire earlier hero gestures and NPC attack/cast starts of a unit that dies later in the step.
        var laterDeaths = new HashSet<uint>();
        var laterNpcDeaths = new HashSet<uint>();
        for (var index = _pending.Count - 1; index >= 0; index--)
        {
            if (_pending[index] is LifecycleReplicationMessage death)
            {
                // Builders have stable opcodes and no side effects. Respawn
                // also uses this queue; only actual deaths retire earlier starts.
                var bytes = death.BuildPacket(death.VictimNetId).Plaintext.Span;
                if (bytes.Length == 0) continue;
                if (bytes[0] == 0xA6)
                    laterNpcDeaths.Add(death.VictimNetId);
                else if ((bytes[0] is 0x61 or 0x1E) &&
                         World.Get(death.VictimNetId)?.UnitType == UnitType.Hero)
                    laterDeaths.Add(death.VictimNetId);
            }
            else if (_pending[index] is SpellCastReplicationMessage cast &&
                     laterNpcDeaths.Contains(cast.CasterNetId))
                _pending.RemoveAt(index);
            else if (_pending[index] is PacketReplicationMessage packet &&
                     (IsPreDeathGesture(packet.Packet, laterDeaths) ||
                      IsPreDeathNpcStart(packet.Packet, laterNpcDeaths)))
                _pending.RemoveAt(index);
        }
    }

    private static bool IsPreDeathNpcStart(PlaintextPacket packet, IReadOnlySet<uint> laterDeaths)
    {
        var bytes = packet.Plaintext.Span;
        if (bytes.Length == 0 || bytes[0] is not (0x0D or SpellPackets.CastSpellAnswerOpcode))
            return false;
        // Hero is the protocol's caster/attacker role even for NPCs. Targets
        // and launched missile/impact messages must survive (playtest B4 #2).
        return packet.EntityReferences.Any(reference =>
            reference.Role == LeaguePacketEntityRole.Hero && laterDeaths.Contains(reference.NetId));
    }

    private bool IsPreDeathGesture(PlaintextPacket packet, IReadOnlySet<uint> laterDeaths)
    {
        var bytes = packet.Plaintext.Span;
        if (bytes.Length < 14 || bytes[0] != 0xB8 || bytes[^1] != 0 ||
            bytes[13..^1].SequenceEqual("Death"u8)) return false;
        return packet.EntityReferences.Any(reference =>
            reference.Role == LeaguePacketEntityRole.Hero &&
            laterDeaths.Contains(reference.NetId) &&
            World.Get(reference.NetId)?.UnitType == UnitType.Hero);
    }

    private void EmitDeathRecap(
        ReplicationObserver observer,
        uint victimNetId,
        DeathRecapSnapshot snapshot)
    {
        if (!observer.Ready || !observer.DeathRecapReady || observer.HeroNetId != victimNetId ||
            snapshot.VictimNetId != victimNetId ||
            observer.View.DeathRecapGenerations.GetValueOrDefault(victimNetId) >= snapshot.Generation)
            return;
        var victim = World.Get(victimNetId);
        var killer = World.Get(snapshot.KillerNetId);
        // The native handler resolves the killer before entering recap. A
        // zero killer makes the client skip the builder; substituting another
        // actor would falsely attribute the death, so it is rejected too.
        if (victim is null || victim.Alive || !observer.View.Known.Contains(victimNetId) ||
            killer is null || !observer.View.Known.Contains(killer.NetId))
            return;
        var records = snapshot.Records
            .Where(record => record.VictimNetId == victimNetId &&
                record.AttackerNetId != 0 && record.Origin.IsSafe)
            .Select(record =>
            {
                var parent = record.Origin.ParentCasterNetId;
                var attackerUnavailable = World.Get(record.AttackerNetId) is null ||
                    !observer.View.Known.Contains(record.AttackerNetId);
                var canProject = record.ProjectToParentWhenAttackerUnavailable &&
                    record.Origin.EventSource == DeathRecapEventSource.Spell &&
                    parent != 0 && record.AttackerNetId != parent && attackerUnavailable &&
                    World.Get(parent) is not null && observer.View.Known.Contains(parent);
                return canProject ? record with { AttackerNetId = parent } : record;
            })
            .Where(record => World.Get(record.AttackerNetId) is not null &&
                observer.View.Known.Contains(record.AttackerNetId) &&
                (record.Origin.ParentCasterNetId == 0 ||
                 (World.Get(record.Origin.ParentCasterNetId) is not null &&
                  observer.View.Known.Contains(record.Origin.ParentCasterNetId))))
            .ToArray();
        if (records.Length == 0) return;
        Send(observer, CombatPackets.DeathRecap(snapshot, records));
        observer.View.DeathRecapGenerations[victimNetId] = snapshot.Generation;
    }

    /// <summary>
    /// Batch 419 follow-up (orchestrator; evidence: the Xerath R cube, batch 418): the dead units whose bound effects go unbound are the script- or
    /// pet-spawned ones, that is units with an owner (OwnerNetId != 0: cubes, Tibbers, clones, boxes, traps) and not a hero. A champion keeps its
    /// visible corpse on the client, and a lane minion, a jungle monster or a structure plays its own death animation, so a lethal impact bound to
    /// them stays bound. The lane, jungle and structure spawns never set an owner.
    /// </summary>
    internal static bool SendsUnboundWhenDead(Entity unit) => unit.UnitType is not UnitType.Hero && unit.OwnerNetId != 0;
}
