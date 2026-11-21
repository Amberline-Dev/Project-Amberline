using Amberline.Core;
using Amberline.Network.Protocol;
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

    // The units whose creation is a 0x3C (ProductionMatchComposition.BuildCreation): Tibbers and the other BBSpawnPet pets (Viktor's storm,
    // 1.0.0.132) and the clones of a unit (BBCloneUnitPet). Every other spawned unit is a 0x80 the server sends itself, created at a client's first sight.
    private static bool IsSpawnedAsPet(Entity entity) =>
        entity.PetState is { Kind: CloneKind.Tibbers or CloneKind.SpawnedPet } or { CloneSourceNetId: not 0 };

    /// <summary>
    /// Reconstruction (batch 419, batch 418 evidence): a bound effect whose unit is dead when Flush puts it on the wire is sent as its unbound variant
    /// at the unit's point, as a NoRender placemarker's effect is (batch 397), when <see cref="SendsUnboundWhenDead"/> holds for that unit. The test is
    /// the flush's, so a create the script ran before the unit's death in the same step (the Xerath R cube's hit effect, whose self-damage follows the
    /// create) goes unbound too, though since batch 692 it reaches the clients just before the death. A unit that is not in the world (its corpse is gone) keeps the bound packet, as before. A
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
        // Batch 847: the spectator counts as either team's ally (ReplicationObserver.Allied), so it gets each team's own effects.
        if (message.AlliedTeam is Team allies && message.EnemyPacket is null && !observer.Allied(allies))
            return false;
        var dependencies = message.Packet.EntityReferences.Where(reference =>
            reference.Role != LeaguePacketEntityRole.Create &&
            reference.NetId != 0).ToArray();
        // Batch 802 (owner report, 15 November 2026: the victim of a Noxious Trap sees no blast): an effect on no bind is drawn at its point,
        // and a team that sees the point is shown it whatever unit it is aimed at (below). Its target is the one reference the client can do
        // without: a client that never held it gets the effect with the target cleared (Route, FxPackets.WithoutTarget).
        var pointEffect = message.BindNetId == 0 && dependencies.All(reference => reference.Role == LeaguePacketEntityRole.Target);
        // Removal from the server registry does not make an unknown native binding safe.
        if (!pointEffect && dependencies.Any(reference => !observer.View.Known.Contains(reference.NetId)))
            return false;
        // Batch 419: the unbound variant of a bound create keeps the bound create's audience, the observers that saw its dead unit before this flush.
        if (message.DeadBindNetId != 0 && routeVisible.Contains(message.DeadBindNetId))
            return true;
        var actors = dependencies.Where(reference => IsCurrentActor(reference.NetId)).ToArray();
        // Batch 690 (community report, Zilean's Time Bomb on a carrier out of sight): an effect bound to a unit also reaches its own fog-of-war
        // team (FOWTeam / FOWTeamOverrideVar) when that team holds the unit but sees none of the units it names. The stock client draws such an
        // effect only while its bind or its target renders (the emitter's Draw), so it stays hidden until the
        // unit is seen, and its FX create routine moves a held target it cannot see to the packet's position, the case of a
        // create retail sent for a fogged unit. An effect whose bind or target the team sees keeps the sight rule (the client would draw it).
        // Reconstruction: which teams retail's server sent a bound effect to is not in the client; the script's own team is the one it names.
        if (actors.Any(reference => routeVisible.Contains(reference.NetId)) ||
            message.BindNetId != 0 && message.FowTeam is Team fow && observer.Allied(fow) && actors.Any(reference => reference.NetId == message.BindNetId))
            return true;
        // Batch 802: an effect on no bind aimed at a unit this team does not see is still shown to a team that sees its point. BantamTrapDetonate
        // creates ShroomMine.troy at the trap, aimed at the trap (BindObjectVar Nothing, PosVar and TargetObjectVar Owner), with Teemo's team as
        // its fog-of-war team and FOWVisibilityRadius 300: the effect's sight is its point's (retail's server tracked an effect's team visibility,
        // FxOnEnterTeamVisiblity / OnLeaveTeamVisiblity), not the stealthed trap's, so the victim standing on it saw the blast and heard
        // the sound the particle carries. Batch 931 (Reconstruction): the point's sight takes the script's FOWVisibilityRadius (VisibilityRules.
        // ProvidersRevealEffect: the centre or a point of that circle), so Graves's and Akali's smoke thrown into a wall reach the enemy standing in
        // it, whose team sees the floor around the centre and never the centre itself (batch 930's W finding). An effect aimed
        // at a unit with no own sight of its point keeps the old rule (batch 690: it does not reach its own team through the target's fog).
        if (actors.Length > 0)
            return pointEffect && message.Position is Position2 point &&
                   (message.CreationVisibleTeams?.Any(observer.Allied) == true ||
                    VisibilityRules.ProvidersRevealEffect(Environment, teamState.Providers, point, message.FowRadius));
        // Batch 473 (Reconstruction): an effect on no current unit reaches its own fog-of-war team (BBSpellEffectCreate's FOWTeam /
        // FOWTeamOverrideVar, else the placemarker cube it was bound to) wherever it stands, as a unit reaches its own team
        // (VisibilityRules.RevealsEntity); other teams need sight of its point. The 0x8C carries no team and the client's
        // BBSpellEffectCreate parser is a stub, so the rule is read from the scripts, which name the
        // caster's team there (Rumble's Equalizer, Akali's smoke, Morgana's soil, Trundle's Contaminate). Before batch 473 a
        // ServerOnly cube's own 1350 sight hid the gap: Rumble's own client lost the far end of its carpet.
        return message.FowTeam is Team fowTeam && observer.Allied(fowTeam) ||
               message.CreationVisibleTeams?.Any(observer.Allied) == true ||
               message.Position is Position2 position &&
               VisibilityRules.ProvidersRevealEffect(Environment, teamState.Providers, position, message.FowRadius);
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
        // Batch 737: 0xD7 ShowHealthBar (BBShowHealthBar) is DefaultPacketMixin<217, PolicyBroadcast> (217 is its AntiBot-build
        // opcode), and the client's BBShowHealthBar sends it through the unit's NetVisibilityObject (SendVariableSizeInternal). Its handler,
        // The client, stores show == false in the hide flag the champion bar reads, with no
        // visibility test, and nothing else writes that flag (the stock client's constructor zeroes it); the reveal's 0xC2 carries none. A
        // show routed by sight was lost for good on an enemy that saw a zombie life begin and not end (KogMawIcathianSurprise,
        // DeathDefiedBuff: Show=false at the start, Show=true at the end): that client drew the hero with no bar after his respawn.
        // (The opcodes are 132's: 126's 0x7F, 0x99, 0x9E, 0xBA, 0x53 and 0xD7 are one higher.)
        // Patch 132: 0xF4 UnitTargetableStatusChanged goes the same way. Its handler stores the two values
        // on the unit with no visibility test and the reveal's 0xC3 carries no targetability, so it is unit state no reveal repeats.
        // Reconstruction: its mixin policy is not in the 132 client's type names; 126 replicated the two values as 0xCC fields instead.
        return global || packet.Plaintext.Span[0] is 0x80 or 0x2B or 0x34 or 0x35 or 0x1A or 0x9A or 0x9F or 0xBB or 0x54 or 0xD8 or
                CombatPackets.UnitTargetableStatusOpcode ||
            IsOwnerClientOnly(packet) || required.Any(reference => routeVisible.Contains(reference.NetId));
    }

    // PolicySendToOwnerClientOnly<PolicyBroadcast>: CharSetCooldown 0x8A (SetSlotSpellCooldownTime),
    // ChangeSlotSpellIcon 0x36 (BBSetSlotSpellIcon) and ChangeSlotSpellOffsetTarget 0x37 (BBSetSpellOffsetTarget). The client's send
    // instantiations call the owner-client filter unconditionally before TryBroadcast: the packet
    // goes to the unit's own client, whoever sees the unit, and to no other. (CharSetCooldownBroadcast, a 0x8A for every client, is in
    // the packet types but nothing in the stock client sends it.)
    private static bool IsOwnerClientOnly(PlaintextPacket packet) =>
        packet.Plaintext.Length >= 5 && packet.Plaintext.Span[0] is 0x8B or 0x36 or 0x37;

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
        // terrain-vision provider, collision, AI or reward. Since the cube is
        // an announced world unit, this is its creation for a view that did
        // not hold it yet, and its own death retires it (batch 695,
        // BbLineMissile.Finish).
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
