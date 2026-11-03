using System.Buffers.Binary;
using System.Text.Json;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Spells.Bb;
using Amberline.Server;

namespace Amberline.Tests;

/// <summary>
/// The original champion acceptance scenario checks validation at the
/// authenticated session boundary.  Each gate is rebuilt in a fresh
/// production composition so a rejected request cannot inherit state from a
/// previous champion or team.
/// </summary>
public static class ChampionAcceptanceChecks
{
    private static readonly Team[] Teams = [Team.Order, Team.Chaos];


    private static void RunGate(string champion, JsonElement matrixCase,
        Team actorTeam, ValidationGate gate)
    {
        var orderChampion = actorTeam == Team.Order ? champion : "Annie";
        var chaosChampion = actorTeam == Team.Chaos ? champion : "Annie";
        var runtime = MatchRuntime.CreateProduction(
            NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion, chaosChampion, readySettleSeconds: 0.0);
        try
        {
            runtime.World.Clock.SetVirtual(0.0);
            runtime.Session.CreateWorld();
            TestAssert.True(runtime.Session.Authenticate(1, runtime.Session.BuildKeyCheck(1)) is not null,
                champion + " order authentication");
            TestAssert.True(runtime.Session.Authenticate(2, runtime.Session.BuildKeyCheck(2)) is not null,
                champion + " chaos authentication");
            runtime.Session.Spawn(1);
            runtime.Session.Spawn(2);
            TestAssert.True(runtime.Session.MarkReady(1), champion + " order ready");
            TestAssert.True(runtime.Session.MarkReady(2), champion + " chaos ready");
            TestAssert.True(runtime.Session.Start(), champion + " start");

            var actorParticipant = runtime.Session.Participants.Single(
                participant => participant.Team == actorTeam);
            var targetParticipant = runtime.Session.Participants.Single(
                participant => participant.Team != actorTeam);
            var actor = actorParticipant.Hero
                ?? throw new InvalidOperationException("acceptance actor missing");
            var target = targetParticipant.Hero
                ?? throw new InvalidOperationException("acceptance target missing");
            // DuelFixture places the caster and dummy close enough for the
            // authenticated target-visibility check.  Reproduce that input
            // state on the production entities before the observer pass.
            actor.Position = new Position2(7000.0, 7000.0);
            // A row may place its dummy inside a short cast range (Cho'Gath's Feast, range 100); the default is the 250-unit dummy.
            var targetDistance = matrixCase.TryGetProperty("target_distance", out var distance) ? distance.GetDouble() : 250.0;
            target.Position = new Position2(7000.0 + targetDistance, 7000.0);
            if (gate == ValidationGate.Dead)
            {
                // The dead caster cannot provide team sight. Keep this gate
                // about spell rejection by supplying a live allied observer.
                runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
                    EntityKind.Minion, UnitType.Minion, actorTeam, target.Position)
                {
                    Name = "AcceptanceVisionMinion",
                    Created = true,
                    Visible = true,
                    Health = 100.0,
                    MaxHealth = 100.0,
                    Speed = 1.0,
                    AttackRange = 125.0,
                });
            }
            var actorCapture = new CaptureTransport();
            var targetCapture = new CaptureTransport();

            runtime.Delivery.AddObserver(new ReplicationObserver(
                checked((ulong)actorParticipant.ConnectionId!.Value), actorTeam,
                actor.NetId, actorCapture, ready: true, view: actorParticipant.View));
            runtime.Delivery.AddObserver(new ReplicationObserver(
                checked((ulong)targetParticipant.ConnectionId!.Value), targetParticipant.Team,
                target.NetId, targetCapture, ready: true, view: targetParticipant.View));
            runtime.Delivery.Flush();
            actorCapture.Clear();
            targetCapture.Clear();

            var mode = matrixCase.GetProperty("mode").GetString() ?? "point";
            // "ally": an allied champion beside the caster. Stand United (AffectFriends | AffectHeroes | NotAffectSelf) accepts no other target.
            if (mode == "ally")
                target = SpawnAlly(runtime, actor, actorTeam);
            // "minion": an enemy minion where the dummy stands. Consume's flags (AffectEnemies, AffectMinions, AffectNeutral) take a minion or a
            // monster and never a hero, so its champion row names a minion.
            if (mode == "minion")
            {
                target = SpawnMinion(runtime, actor, targetParticipant.Team);
                runtime.Delivery.Flush(); // the actor's view must see the minion before the request
                actorCapture.Clear();
                targetCapture.Clear();
            }
            var targeted = mode is "enemy" or "ally" or "minion";
            var targetNetId = targeted ? target.NetId : mode == "self" ? actor.NetId : 0u;
            if (mode is "enemy" or "minion" && !actorParticipant.View.Visible.Contains(target.NetId))
                throw new InvalidOperationException("enemy target was not visible at request boundary");
            var slot = matrixCase.GetProperty("slot").GetInt32();
            var point = targeted || mode is "point" or "shaco_q" ? target.Position : actor.Position;
            actor.SpellRanks = [1, 1, 1, 1];
            // Corki's Missile Barrage is an ammo spell: its charges are added when the slot is first trained (CharOnLevelUpSpell, two charges, E29),
            // which the rank array does not do. A row that says "trained_first" runs the champion's rank-up hook the way SpellSystem does.
            if (matrixCase.TryGetProperty("trained_first", out var trained) && trained.GetBoolean())
            {
                var trainedSlot = matrixCase.GetProperty("slot").GetInt32();
                for (var tick = 0; tick < 30; tick++) runtime.Tick(1.0 / 30.0); // the charge timer is set on the hero's first ticks (ChargeCooldown)
                actor.Level = 6;
                actor.SpellRanks[trainedSlot] = 0;
                TestAssert.True(runtime.Spells.RankUp(actor, trainedSlot), "the first training of the slot is accepted at level six");
                runtime.Delivery.Flush();
            }
            actor.Mana = matrixCase.GetProperty("cost").GetDouble() == 0 ? 0 : actor.MaxMana;
            // Kassadin's E is sealed until Force Pulse is charged (CharScriptKassadin seals it, ForcePulseCanCast opens it): the first-cast row is the charged state.
            if (matrixCase.TryGetProperty("sealed_until_ready", out var charged) && charged.GetBoolean())
                actor.SpellSeals.Set(false, slot, false);
            // Akali's Shadow Dance is castable only with two charges (its CanCast): a row with "ammo" starts the ammo buff with that many stacks,
            // added the way the shipped scripts add it (BUFF_STACKS_AND_RENEWS of BUFF_AmmoStack, the timer long).
            if (matrixCase.TryGetProperty("ammo", out var ammo))
                for (var charge = 0; charge < ammo.GetProperty("stacks").GetInt32(); charge++)
                    ((BbChampionModule)runtime.Dependencies.ChampionDispatcher.FindModule(champion)!).Buffs!.Add(new BbBuffRequest(actor, actor, ammo.GetProperty("buff").GetString()!,
                        (int)BbBuffAddType.BUFF_STACKS_AND_RENEWS, (int)BuffType.AmmoStack, 4, 25000.0, 0.0, false, new BbTable(), 1, true));
            // A row whose first cast needs a mark on the target (Kennen's Electrical Surge: CanCast wants a marked unit within 800) starts the target with it.
            if (matrixCase.TryGetProperty("marked_target", out var marked))
                ((BbChampionModule)runtime.Dependencies.ChampionDispatcher.FindModule(champion)!).Buffs!.Add(new BbBuffRequest(target, actor, marked.GetString()!,
                    (int)BbBuffAddType.BUFF_STACKS_AND_RENEWS, (int)BuffType.CombatDehancer, 5, 8.0, 0.0, false, new BbTable(), 1, true));
            var position = new Position3(point.X, 0, point.Z);
            // A row whose spell copies an earlier cast (LeBlanc's Mimic: its CanCast wants a prior Q, W or E, CharOnSpellCast records it) casts
            // "after_slot" at the same target first; its packets and mana are set aside so the gates see only the copy.
            if (matrixCase.TryGetProperty("after_slot", out var after))
            {
                var mana = actor.Mana;
                TestAssert.True(runtime.Spells.HandleCastRequest(actor, new SpellCastRequest(after.GetInt32(), false, position, position, targetNetId),
                    runtime.Spells.CurrentTime), "the copied cast is accepted");
                runtime.Delivery.Flush();
                actorCapture.Clear();
                targetCapture.Clear();
                actor.Mana = mana;
            }
            TestAssert.True(runtime.Spells.CanCast(actor, slot, targetNetId, position, 0).Allowed,
                "ungated fixture cast must be valid before testing its rejection");
            var request = EncodeCast(actor.NetId,
                matrixCase.GetProperty("slot").GetInt32(), point,
                targetNetId);

            ApplyGateState(runtime, actor, slot, gate);
            var expectedFailure = gate switch
            {
                ValidationGate.Rank => SpellCastFailure.Rank,
                ValidationGate.Cooldown => SpellCastFailure.Cooldown,
                ValidationGate.Mana => SpellCastFailure.Mana,
                ValidationGate.Dead => SpellCastFailure.Dead,
                _ => SpellCastFailure.Disabled,
            };
            TestAssert.Equal(expectedFailure,
                runtime.Spells.CanCast(actor, slot, targetNetId, position, 0).Failure,
                "FW-04: the selected slot fails for the intended gate");
            if (gate == ValidationGate.Dead && mode == "enemy")
                TestAssert.True(actorParticipant.View.Visible.Contains(target.NetId) &&
                                runtime.Session.CurrentTargetVisible?.Invoke(actor, target) == true,
                    "dead-caster gate retains independent allied sight of the target");
            var manaBefore = actor.Mana;
            var cooldownsBefore = actor.SpellCooldowns.ToArray();
            var rankBefore = actor.SpellRanks.ToArray();
            actorCapture.Clear();
            targetCapture.Clear();
            var callbackCalled = false;
            var accepted = false;
            var direct = runtime.Session.Receive(actorParticipant.ConnectionId!.Value, 1,
                runtime.Cipher.Encrypt(request), context =>
                {
                    callbackCalled = true;
                    accepted = runtime.Dependencies.Gameplay(context);
                    return accepted;
                });
            runtime.Delivery.Flush();

            TestAssert.True(callbackCalled, "request did not reach gameplay dispatcher");
            TestAssert.False(accepted, "rejected gate was accepted");
            TestAssert.False(HasCastAnswer(direct) || HasCastAnswer(actorCapture.Packets) ||
                             HasCastAnswer(targetCapture.Packets),
                "rejected gate emitted CastSpellAns");
            TestAssert.Near(manaBefore, actor.Mana, 1e-9, "rejected gate changed mana");
            TestAssert.Equal(cooldownsBefore, actor.SpellCooldowns.ToArray(),
                "rejected gate changed cooldowns");
            TestAssert.Equal(rankBefore, actor.SpellRanks.ToArray(),
                "rejected gate changed spell ranks");
        }
        finally
        {
            runtime.World.Clock.UseReal();
        }
    }

    private static Entity SpawnAlly(MatchRuntime runtime, Entity actor, Team team)
    {
        var ally = new Entity(runtime.World.Allocate(UnitType.Hero), EntityKind.Hero, UnitType.Hero, team,
            new Position2(actor.Position.X + 150.0, actor.Position.Z))
        {
            Name = "AcceptanceAllyHero",
            Created = true,
            Visible = true,
            Health = 500.0,
            MaxHealth = 500.0,
            Speed = 325.0,
            AttackRange = 125.0,
        };
        runtime.World.Register(ally);
        return ally;
    }

    private static Entity SpawnMinion(MatchRuntime runtime, Entity actor, Team team)
    {
        var minion = new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion, team,
            new Position2(actor.Position.X + 100.0, actor.Position.Z)) // inside Consume's 125 cast range
        {
            Name = "AcceptanceMinion",
            Created = true,
            Visible = true,
            Health = 500.0,
            MaxHealth = 500.0,
            Speed = 325.0,
            AttackRange = 125.0,
        };
        runtime.World.Register(minion);
        return minion;
    }

    private static void ApplyGateState(MatchRuntime runtime, Entity actor, int slot, ValidationGate gate)
    {
        switch (gate)
        {
            case ValidationGate.Rank:
                actor.SpellRanks[slot] = 0;
                break;
            case ValidationGate.Cooldown:
                actor.SpellCooldowns[slot] = 10.0;
                break;
            case ValidationGate.Mana:
                actor.Mana = 0.0;
                break;
            case ValidationGate.Stun:
                runtime.Buffs.AddBuff(actor, "AcceptanceStun", BuffType.Stun, 10.0,
                    actor.NetId, canMitigateDuration: false);
                break;
            case ValidationGate.Dead:
                actor.Alive = false;
                actor.Health = 0.0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(gate));
        }
    }

    private static byte[] EncodeCast(uint actorNetId, int slot, Position2 point, uint targetNetId)
    {
        var payload = new byte[34];
        payload[0] = 0xA2;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1, 4), actorNetId);
        payload[5] = checked((byte)(slot << 1));
        WriteSingle(payload, 6, point.X);
        WriteSingle(payload, 10, 0.0);
        WriteSingle(payload, 14, point.Z);
        WriteSingle(payload, 18, point.X);
        WriteSingle(payload, 22, 0.0);
        WriteSingle(payload, 26, point.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(30, 4), targetNetId);
        return payload;
    }

    private static void WriteSingle(byte[] payload, int offset, double value) =>
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4),
            BitConverter.SingleToInt32Bits(checked((float)value)));

    private static bool HasCastAnswer(IEnumerable<SessionOutboundPacket> packets) =>
        packets.Any(packet => packet.Packet.Plaintext.Length > 0 &&
                              packet.Packet.Plaintext.Span[0] == 0xBD);

    private static bool HasCastAnswer(IEnumerable<PlaintextPacket> packets) =>
        packets.Any(packet => packet.Plaintext.Length > 0 && packet.Plaintext.Span[0] == 0xBD);

    private enum ValidationGate
    {
        Rank,
        Cooldown,
        Mana,
        Stun,
        Dead,
    }

    private sealed class CaptureTransport : IReplicationTransport
    {
        private readonly List<PlaintextPacket> _packets = new();
        public IReadOnlyList<PlaintextPacket> Packets => _packets;
        public void Send(PlaintextPacket packet) => _packets.Add(packet);
        public void Clear() => _packets.Clear();
    }
}
