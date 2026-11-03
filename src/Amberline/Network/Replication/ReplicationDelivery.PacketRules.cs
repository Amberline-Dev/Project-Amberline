using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Neutral;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Spells;
using System.Buffers.Binary;

namespace Amberline.Network.Replication;

public sealed partial class ReplicationDelivery
{
    private void RefreshLeeFollowDashes(IReadOnlyList<ReplicationObserver> active)
    {
        var live = new HashSet<(ReplicationObserver Observer, uint NetId)>();
        var movingLee = World.Entities.Values.Where(actor => actor.ForcedMove is
            { Effect: "BlindMonkQTwoDash", Completed: false, DashPresentation: true,
                FollowNetId: not null }).ToArray();
        foreach (var observer in active)
        foreach (var actor in movingLee)
        {
            if (!observer.View.Visible.Contains(actor.NetId) ||
                actor.ForcedMove is not { Effect: "BlindMonkQTwoDash", Completed: false,
                    DashPresentation: true, FollowNetId: uint targetId } move)
                continue;
            var key = (observer, actor.NetId);
            live.Add(key);
            var targetVisible = observer.View.Visible.Contains(targetId);
            var had = _leeFollowDashes.TryGetValue(key, out var previous) &&
                ReferenceEquals(previous.Move, move);
            var sent = _leeDashSent.TryGetValue(key, out var sentFollow);
            if (sent ? sentFollow != targetVisible : !had ||
                previous.TargetVisible != targetVisible ||
                !targetVisible && (previous.Position != actor.Position ||
                    previous.Destination != move.Destination))
                SendAll(observer, Services.BuildWaypointReanchor(actor, observer));
            _leeFollowDashes[key] = (move, actor.Position, move.Destination, targetVisible);
        }
        foreach (var key in _leeFollowDashes.Keys.Where(key => !live.Contains(key)).ToArray())
            _leeFollowDashes.Remove(key);
    }

    // The units whose creation is a 0x3C (ProductionMatchComposition.BuildCreation): Tibbers (BBSpawnPet) and the clones of a unit
    // (BBCloneUnitPet). Every other spawned unit is a 0x80 the server sends itself, created at a client's first sight.
    private static bool IsSpawnedAsPet(Entity entity) =>
        entity.PetState is { Kind: CloneKind.Tibbers } or { CloneSourceNetId: not 0 };

    /// <summary>
    /// Reconstruction (batch 419, batch 418 evidence): a bound effect whose unit is dead when Flush puts it on the wire is sent as its unbound variant
    /// at the unit's point, as a NoRender placemarker's effect is (batch 397), when <see cref="SendsUnboundWhenDead"/> holds for that unit. Flush sends
    /// the step's lifecycle first, so a create the script ran before the unit's death (the Xerath R cube's hit effect, whose self-damage follows the
    /// create) reaches the clients after the death. A unit that is not in the world (its corpse is gone) keeps the bound packet, as before. A
    /// non-persistent effect is reported once, so it ends with its unit's corpse (SpellSystem.EndEffectWithUnitCorpse).
    /// </summary>
    private EffectCreateReplicationMessage OnWire(EffectCreateReplicationMessage message)
    {
        if (message.BindNetId == 0 || message.UnboundPacket is null ||
            World.Get(message.BindNetId) is not { Alive: false } dead || !SendsUnboundWhenDead(dead))
            return message;
        if (!message.Persistent && message.EffectNetId != 0) UnboundEffectAtDeadUnit?.Invoke(message.EffectNetId, dead.NetId);
        return message with
        {
            BindNetId = 0, Packet = message.UnboundPacket, EnemyPacket = message.UnboundEnemyPacket, LivePacket = null,
            UnboundPacket = null, UnboundEnemyPacket = null, DeadBindNetId = dead.NetId,
        };
    }

    private bool CanRouteEffectCreate(
        EffectCreateReplicationMessage message,
        ReplicationObserver observer,
        TeamVisibilityState teamState,
        IReadOnlySet<uint> routeVisible)
    {
        if (message.OwnerHeroNetId != 0 && observer.HeroNetId != message.OwnerHeroNetId)
            return false;
        if (message.AlliedTeam is Team allies && message.EnemyPacket is null && observer.Team != allies)
            return false;
        var dependencies = message.Packet.EntityReferences.Where(reference =>
            reference.Role != LeaguePacketEntityRole.Create &&
            reference.NetId != 0).ToArray();
        // Removal from the server registry does not make an unknown native
        // binding safe. This also covers traps triggered before first sight.
        if (dependencies.Any(reference => !observer.View.Known.Contains(reference.NetId)))
            return false;
        // Batch 419: the unbound variant of a bound create keeps the bound create's audience, the observers that saw its dead unit before this flush.
        if (message.DeadBindNetId != 0 && routeVisible.Contains(message.DeadBindNetId))
            return true;
        var actors = dependencies.Where(reference => IsCurrentActor(reference.NetId)).ToArray();
        if (actors.Length > 0)
            return actors.Any(reference => routeVisible.Contains(reference.NetId));
        // Batch 473 (Reconstruction): an effect on no current unit reaches its own fog-of-war team (BBSpellEffectCreate's FOWTeam /
        // FOWTeamOverrideVar, else the placemarker cube it was bound to) wherever it stands, as a unit reaches its own team
        // (VisibilityRules.RevealsEntity); other teams need sight of its point. The 0x8C carries no team and the client's
        // BBSpellEffectCreate parser is a stub, so the rule is read from the scripts, which name the
        // caster's team there (Rumble's Equalizer, Akali's smoke, Morgana's soil, Trundle's Contaminate). Before batch 473 a
        // ServerOnly cube's own 1350 sight hid the gap: Rumble's own client lost the far end of its carpet.
        return message.FowTeam == observer.Team ||
               message.CreationVisibleTeams?.Contains(observer.Team) == true ||
               message.Position is Position2 position &&
               VisibilityRules.ProvidersReveal(Environment, teamState.Providers, position);
    }

    private bool IsCurrentActor(uint netId) =>
        World.Get(netId) is not null || Balls.Any(ball => ball.NetId == netId) ||
        _queuedTemplateIds.Contains(netId);

    private static bool CanRoute(
        PlaintextPacket packet,
        ReplicationView view,
        IReadOnlySet<uint> routeVisible,
        bool global)
    {
        var required = packet.EntityReferences.Where(reference =>
            reference.NetId != 0 && reference.Role != LeaguePacketEntityRole.Create).ToArray();
        // A buff packet's caster is the one reference the client can do without: Send maps an unknown one to 0
        // (ReplicationView.ForClient). Everything else the packet names must have been created for the client.
        if (required.Any(reference => reference.Role != LeaguePacketEntityRole.Caster && !view.KnowsUnit(reference.NetId)))
            return false;
        // A client retains buff particles and animation locks on known actors
        // in fog. Their cleanup must reach it after visibility has fallen;
        // these packets carry no hidden position.
        // A slot rename (0x35, no position either) reaches every client that holds the unit, in sight or not (batch 325). Retail's BBSetSpell
        // (the send routine) sends it through the unit's NetVisibilityObject (SendVariableSizeInternal) with TryBroadcast (the broadcast
        // sender; the map-view and local-view senders are the ones the shield, level-up and heal packets use).
        // The client applies it with no visibility test (the packet handler, then the change-spell routine), and the
        // reveal's 0xC2 (the visible-enter handler) carries no spellbook, so a rename missed in fog was never repaired.
        // Batch 329: what TryBroadcast means. The client holds only its own compile of the visibility object, where the three senders are
        // the same stub (the send-event routine); the server's filters are not shipped. The policy is in the type: each PKT_ derives from
        // DefaultPacketMixin<opcode, Policy>, and the template picks the sender from it. PolicyMapView (TrySendMapView) holds the
        // packets that are vision-gated and repaired by the reveal (waypoints, attacks, cast answers, buff adds and removes, the item answers whose
        // result the 0xC2's inventory diff repeats); PolicyBroadcast holds the unit's state no reveal repeats. So a PolicyBroadcast packet the
        // unit's NetVisibilityObject sends reaches every client that holds the unit, in sight or not:
        // 0x1A ChangeSlotSpellType (BBSetTargetingType; handler SetSpellTargetting, no visibility test).
        // 0x99 ChangePARColorOverride (BBSetPARColorOverride, BBClearPARColorOverride), 0x9E
        // ChangeCharacterVoice (BBSetVoiceOverride, BBResetVoiceOverride) and 0xBA SetFadeOutPush
        // (BBPushCharacterFade), whose pop 0x34 already went this way: their handlers write the colours, the
        // voice string and the fade stack with no visibility test. Every push of a layer goes, not only the full-opacity push that ends one
        // (VayneTumbleFade, Deceive), which until now was the one push let through to repair a viewer that lost sight during a stealth.
        // 0x53 FaceDirection (FacePosition, BBFaceDirection): a direction, no position; the handler only
        // turns the unit (the face-vector and SetTargetFacing routines).
        return global || packet.Plaintext.Span[0] is 0x7F or 0x2B or 0x34 or 0x35 or 0x1A or 0x99 or 0x9E or 0xBA or 0x53 ||
            IsOwnerClientOnly(packet) || required.Any(reference => routeVisible.Contains(reference.NetId));
    }

    // PolicySendToOwnerClientOnly<PolicyBroadcast>: CharSetCooldown 0x8A (SetSlotSpellCooldownTime),
    // ChangeSlotSpellIcon 0x36 (BBSetSlotSpellIcon) and ChangeSlotSpellOffsetTarget 0x37 (BBSetSpellOffsetTarget). The client's send
    // instantiations call the owner-client filter unconditionally before TryBroadcast: the packet
    // goes to the unit's own client, whoever sees the unit, and to no other. (CharSetCooldownBroadcast, a 0x8A for every client, is in
    // the packet types but nothing in the stock client sends it.)
    private static bool IsOwnerClientOnly(PlaintextPacket packet) =>
        packet.Plaintext.Length >= 5 && packet.Plaintext.Span[0] is 0x8A or 0x36 or 0x37;

    // The unit's client: a hero's player, or a pet's owner (the pet's spawn gives the pet its owner's NetVisibilityObject client id).
    // Reconstruction: a unit with no client (a lane minion, a camp monster) is filtered to nobody.
    private bool IsOwnerClient(ReplicationObserver observer, ReadOnlySpan<byte> bytes)
    {
        var unit = BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]);
        return observer.HeroNetId != 0 && (unit == observer.HeroNetId || World.Get(unit)?.OwnerNetId == observer.HeroNetId);
    }

    private void CreateProjectedCaster(ReplicationObserver observer,
        MissileCasterProjection caster, SpellCastInfo castInfo)
    {
        // Howling Gale's script casts from a TestCube placemarker. Keep this
        // object in the recipient's native object table only: it has no
        // World entity, terrain-vision provider, collision, AI or reward.
        SendAll(observer, TemporaryCasterPackets.Create(caster, castInfo, NextSyncId));
        observer.View.Known.Add(caster.NetId);
    }

    private static IReadOnlyList<SpellCastTarget> FilterSpellTargets(
        IReadOnlyList<SpellCastTarget> targets, IReadOnlySet<uint> known)
    {
        var hasUnknown = targets.Any(target => target.NetId != 0 && !known.Contains(target.NetId));
        return hasUnknown
            ? targets.Where(target => target.NetId != 0 && known.Contains(target.NetId)).ToArray()
            : targets;
    }

    private static void MarkCreated(ReplicationView view, PlaintextPacket packet)
    {
        foreach (var reference in packet.EntityReferences)
            if (reference.Role == LeaguePacketEntityRole.Create && reference.NetId != 0)
                view.Known.Add(reference.NetId);
    }

    private static void TrackMissile(ReplicationView view, uint missileNetId)
    {
        if (missileNetId != 0)
        {
            view.Effects.Add(missileNetId);
        }
    }

    // The movement data type of the reveal's 0xC2 (VisionPackets.MovementData*), 0 for a reveal without one (a structure's).
    private static byte RevealMovementType(IReadOnlyList<PlaintextPacket> reveal) =>
        reveal.FirstOrDefault(packet => packet.Plaintext.Span[0] == VisionPackets.EnterVisibilityOpcode) is { } enter &&
        enter.Plaintext.Length > VisionPackets.EnterVisibilityMovementTypeOffset
            ? enter.Plaintext.Span[VisionPackets.EnterVisibilityMovementTypeOffset] : (byte)0;
}
