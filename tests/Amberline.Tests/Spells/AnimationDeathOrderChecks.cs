using System.Buffers.Binary;
using System.Text;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("minions-live-same-flush-death-retires-attack-start")]
    public static void NpcDeathRetiresAttackStartWithoutRetiringLaunchedDamage()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var launched in new[] { false, true })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var enemy = match.Hero(1);
            var minion = DeathOrderMinion(match);
            runtime.Delivery.Flush();
            match.Clear();
            foreach (var observer in runtime.Delivery.Observers)
                TestAssert.True(observer.View.Known.Contains(minion.NetId) &&
                                observer.View.Visible.Contains(minion.NetId), "minion starts visible and known");

            TestAssert.True(runtime.Combat.QueueBasicAttack(minion, enemy, new AttackRolls(1, 1, 1)));
            if (launched) runtime.Combat.CombatTick(.3, regenerate: false);
            TestAssert.True(runtime.Combat.QueueBasicAttack(match.Hero(0), enemy, new AttackRolls(1, 1, 1)));
            TestAssert.True(runtime.Combat.ApplyHit(enemy.NetId, minion.NetId,
                20000, DamageType.True, DamageSourceKind.Spell));
            runtime.Delivery.Flush();

            TestAssert.True(!minion.Alive && ReferenceEquals(minion, runtime.World.Get(minion.NetId)),
                "playtest B4 #2: death retains the authoritative corpse");
            TestAssert.Equal(launched, runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == minion.NetId),
                "death cancels only the unlaunched damage work");
            foreach (var capture in match.Captures)
            {
                TestAssert.True(capture.Packets.Any(packet => IsActorPacket(packet, 0xA6, minion.NetId)),
                    "every known observer receives NPC death");
                TestAssert.False(capture.Packets.Any(packet => IsActorPacket(packet, 0x0D, minion.NetId)),
                    "earlier minion attack cannot start after death");
                TestAssert.True(capture.Packets.Any(packet => IsActorPacket(packet, 0x0D, match.Hero(0).NetId)),
                    "another actor's attack in the same flush survives");
            }

            runtime.Combat.CombatTick(1, regenerate: false);
            runtime.Delivery.Flush();
            foreach (var capture in match.Captures)
                TestAssert.Equal(launched,
                    capture.Packets.Any(packet => IsActorPacket(packet, 0x68, minion.NetId)),
                    "a shot already in flight still delivers its impact after source death");
            TestAssert.True(ReferenceEquals(minion, runtime.World.Get(minion.NetId)),
                "corpse remains during the normal eight-second retention window");
        }
    }

    [Check("minions-live-same-flush-death-retires-cast-start-only")]
    public static void NpcDeathRetiresTypedAndRawCastsButKeepsMissilesAndOtherCasters()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var runtime = match.Runtime;
            var minion = DeathOrderMinion(match);
            var enemy = match.Hero(1);
            runtime.Delivery.Flush();
            match.Clear();
            var start = new Position3(minion.Position.X, 0, minion.Position.Z);
            var end = new Position3(enemy.Position.X, 0, enemy.Position.Z);
            var info = new SpellCastInfo(1, minion.NetId, 1, minion.NetId,
                runtime.World.Allocate(UnitType.Missile), end, end,
                [new SpellCastTarget(enemy.NetId, end, 0)], 0, 0, 0, start);
            // Delivery boundary used by neutral spells and Shaco box shots.
            runtime.Delivery.EnqueueCast(new SpellCastReplicationMessage(minion.NetId, info));
            runtime.Delivery.Enqueue(SpellPackets.CastSpellAnswer(minion.NetId, info));
            var missile = new MissileReplicationMessage(minion.NetId, start, start,
                new Position3(1, 0, 0), new Position3(500, 0, 0), start, end, start, 500, 0, 0, info);
            runtime.Delivery.EnqueueMissile(missile);
            var otherInfo = info with { CasterNetId = enemy.NetId, SpellNetId = enemy.NetId,
                MissileNetId = 0, Targets = [new SpellCastTarget(minion.NetId, start, 0)] };
            runtime.Delivery.EnqueueCast(new SpellCastReplicationMessage(enemy.NetId, otherInfo));
            runtime.Delivery.Enqueue(SpellPackets.CastSpellAnswer(enemy.NetId, otherInfo));
            TestAssert.True(runtime.Combat.ApplyHit(enemy.NetId, minion.NetId,
                20000, DamageType.True, DamageSourceKind.Spell));
            runtime.Delivery.Flush();

            foreach (var capture in match.Captures)
            {
                TestAssert.True(capture.Packets.Any(packet => IsActorPacket(packet, 0xA6, minion.NetId)));
                TestAssert.False(capture.Packets.Any(packet =>
                    IsActorPacket(packet, SpellPackets.CastSpellAnswerOpcode, minion.NetId)),
                    "both typed and raw earlier NPC casts retire");
                TestAssert.Equal(2, capture.Packets.Count(packet =>
                    IsActorPacket(packet, SpellPackets.CastSpellAnswerOpcode, enemy.NetId)),
                    "targeting the dying NPC does not retire another caster's packets");
                TestAssert.True(capture.Packets.Any(packet => IsActorPacket(packet, 0x41, minion.NetId)),
                    "launched missile replication survives its caster's death");
            }
        }
    }

    private static Entity DeathOrderMinion(Match match) =>
        match.Runtime.World.Register(new Entity(match.Runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, match.Hero(0).Team, match.Hero(0).Position)
        {
            Name = "DeathOrderCaster", MinionType = MinionType.Caster,
            Health = 500, MaxHealth = 500, Radius = 35,
            AttackRange = 550, AttackDamage = 20, AttackPeriod = 1,
            AttackWindup = .25, MissileSpeed = 500,
        });

    [Check("spells-live-same-flush-death-retires-earlier-caster-gesture")]
    public static void DeathDoesNotReplayAnEarlierCasterGesture()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            // Janna E has an explicit B8 and no E-specific OnDeath stop.
            // Both the cast and lethal damage enter production before one flush.
            using (var match = new Match("Janna", team))
            {
                var caster = match.Hero(0);
                var enemy = match.Hero(1);
                TestAssert.True(match.Runtime.Session.Participants[1].View.Known.Contains(caster.NetId) &&
                                match.Runtime.Session.Participants[1].View.Visible.Contains(caster.NetId),
                    team + ": opposing observer starts with the caster visible and known");
                QueueCastWithoutFlush(match, 2, caster.Position, caster.NetId);
                TestAssert.True(match.Runtime.Combat.ApplyHit(enemy.NetId, caster.NetId,
                    20000, DamageType.True, DamageSourceKind.Spell));
                TestAssert.False(caster.Alive, team + ": self-shielded caster dies");
                match.Runtime.Delivery.Flush();
                foreach (var capture in match.Captures)
                {
                    var packets = capture.Packets;
                    TestAssert.True(packets.Any(packet => IsDeath(packet, caster.NetId)),
                        team + ": known observer receives the death lifecycle");
                    TestAssert.True(packets.Any(packet =>
                            packet.Plaintext.Span[0] == SpellPackets.CastSpellAnswerOpcode),
                        team + ": already accepted cast remains on the wire");
                    TestAssert.False(packets.Any(packet => IsGesture(packet, caster.NetId, "Spell3")),
                        team + ": pre-death Spell3 cannot start after the corpse packet");
                }
            }

            // A locked dash has an OnDeath 0x2B unlock. Keep that cleanup
            // while discarding only its earlier 0xB8 start.
            using (var match = new Match("Vayne", team))
            {
                var caster = match.Hero(0);
                var enemy = match.Hero(1);
                TestAssert.True(match.Runtime.Session.Participants[1].View.Known.Contains(caster.NetId) &&
                                match.Runtime.Session.Participants[1].View.Visible.Contains(caster.NetId),
                    team + ": opposing observer starts with Tumble visible and known");
                QueueCastWithoutFlush(match, 0, caster.Position + new Position2(150, 0));
                TestAssert.True(caster.ForcedMove is { Completed: false },
                    team + ": real Tumble entered forced movement");
                TestAssert.True(match.Runtime.Combat.ApplyHit(enemy.NetId, caster.NetId,
                    20000, DamageType.True, DamageSourceKind.Spell));
                TestAssert.False(caster.Alive, team + ": tumbling caster dies");
                match.Runtime.Delivery.Flush();
                foreach (var capture in match.Captures)
                {
                    var packets = capture.Packets;
                    var death = packets.FindIndex(packet => IsDeath(packet, caster.NetId));
                    var unlock = packets.FindIndex(packet => IsActorPacket(packet, 0x2B, caster.NetId));
                    TestAssert.True(death >= 0 && unlock > death,
                        team + ": death precedes the preserved animation unlock");
                    TestAssert.False(packets.Any(packet => IsGesture(packet, caster.NetId, "Spell1")),
                        team + ": dead Tumble does not restart its locked clip");
                }
            }
        }
    }

    [Check("spells-live-same-flush-animation-keeps-living-and-death-clips")]
    public static void AnimationRetirementPreservesOtherActorsAndGuardianDeath()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using (var match = new Match("Janna", team, ally: true))
            {
                var caster = match.Hero(0);
                var recipient = match.Hero(2);
                QueueCastWithoutFlush(match, 2, recipient.Position, recipient.NetId);
                TestAssert.True(match.Runtime.Combat.ApplyHit(match.Hero(1).NetId,
                    recipient.NetId, 20000, DamageType.True, DamageSourceKind.Spell));
                TestAssert.True(caster.Alive && !recipient.Alive,
                    team + ": only shield recipient dies");
                match.Runtime.Delivery.Flush();
                TestAssert.True(match.Captures[0].Packets.Any(packet =>
                        IsGesture(packet, caster.NetId, "Spell3")),
                    team + ": recipient death does not erase living caster gesture");
            }

            using (var match = new Match("Annie", team))
            {
                var owner = match.Hero(0);
                owner.Inventory = [new InventorySlot(3026), null, null, null, null, null];
                // The copy binds on the next item poll (BbItemModule.SyncInventory), the way a real buy does, and
                // Items/3026.luaobj's UpdateSelfBuffActions opens its revival state from that bind.
                match.Runtime.Tick(0.01);
                TestAssert.True(match.Runtime.Combat.ApplyHit(match.Hero(1).NetId,
                    owner.NetId, 20000, DamageType.True, DamageSourceKind.Spell));
                TestAssert.True(owner.Alive && owner.Buffs.Any(buff => buff.Name == "GuardianAngel"),
                    team + ": Guardian Angel retains its temporary living state");
                match.Runtime.Delivery.Flush();
                TestAssert.False(match.Captures[0].Packets.Any(packet => IsDeath(packet, owner.NetId)),
                    team + ": Guardian Angel did not enter corpse lifecycle");
                TestAssert.True(match.Captures[0].Packets.Any(packet =>
                        IsGesture(packet, owner.NetId, "Death")),
                    team + ": intentional Guardian Angel Death clip survives");
            }

            using (var match = new Match("Janna", team))
            {
                var hero = match.Hero(0);
                hero.CooldownsDisabled = true;
                hero.SummonerSpells[0] = "SummonerRevive";
                QueueCastWithoutFlush(match, 2, hero.Position, hero.NetId);
                TestAssert.True(match.Runtime.Combat.ApplyHit(match.Hero(1).NetId,
                    hero.NetId, 20000, DamageType.True, DamageSourceKind.Spell));
                TestAssert.False(hero.Alive, team + ": first life ends before delivery");
                match.Runtime.Combat.ScheduleRespawn(hero);
                QueueCastWithoutFlush(match, 0, hero.Position, hero.NetId, summoner: true);
                TestAssert.True(hero.Alive, team + ": actual dead-only Revive starts a new life");
                QueueCastWithoutFlush(match, 2, hero.Position, hero.NetId);
                match.Runtime.Delivery.Flush();
                var packets = match.Captures[0].Packets;
                var death = packets.FindIndex(packet => IsDeath(packet, hero.NetId));
                var respawn = packets.FindIndex(packet => IsActorPacket(packet, 0x4D, hero.NetId));
                TestAssert.True(death >= 0 && respawn > death,
                    team + ": real death and rapid Revive lifecycle retain order");
                TestAssert.Equal(1, packets.Count(packet => IsGesture(packet, hero.NetId, "Spell3")),
                    team + ": old-life gesture retires but the post-Revive cast remains");
            }
        }
    }

    private static void QueueCastWithoutFlush(Match match, int slot, Position2 point,
        uint targetNetId = 0, bool summoner = false)
    {
        var hero = match.Hero(0);
        var request = new byte[34];
        request[0] = 0xA2;
        request[5] = (byte)((slot << 1) | (summoner ? 1 : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
        foreach (var offset in new[] { 6, 18 })
        {
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset), (float)point.X);
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset + 8), (float)point.Z);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(30), targetNetId);
        var accepted = false;
        match.Runtime.Session.Receive(1, 1, match.Runtime.Cipher.Encrypt(request),
            context => accepted = match.Runtime.Dependencies.Gameplay(context));
        TestAssert.True(accepted, $"real slot {slot} cast accepted before delivery");
    }

    private static bool IsDeath(PlaintextPacket packet, uint actor) =>
        IsActorPacket(packet, 0x61, actor);

    private static bool IsActorPacket(PlaintextPacket packet, byte opcode, uint actor) =>
        packet.Plaintext.Span.Length >= 5 && packet.Plaintext.Span[0] == opcode &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == actor;

    private static bool IsGesture(PlaintextPacket packet, uint actor, string name)
    {
        var bytes = packet.Plaintext.Span;
        return bytes.Length >= 14 && bytes[0] == 0xB8 && bytes[^1] == 0 &&
               BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]) == actor &&
               Encoding.ASCII.GetString(bytes[13..^1]) == name;
    }
}
