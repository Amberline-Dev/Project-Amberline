using Amberline.Simulation.Combat;
using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Minions;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("movement.hero-melee-range-boundary")]
    public static void MeleeWindupsSurviveTheCombatRangeTolerance()
    {
        foreach (var champion in new[] { "LeeSin", "Yorick" })
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match(champion, team);
            var hero = match.Hero(0); var enemy = match.Hero(1);
            // owner decision 27 Sept 2026: edge-to-edge gameplay radii
            enemy.Position = hero.Position + new Position2(CombatService.AttackReach(hero, enemy), 0);
            TestAssert.True(match.Runtime.Combat.CanAttack(hero, enemy));
            SendHeroOrder(match, 3, enemy.Position, enemy.NetId);
            TestAssert.Equal(0, hero.Waypoints.Count, "pursuit must use the same range as combat");
            var health = enemy.Health;
            match.Runtime.Tick(.8);
            TestAssert.True(enemy.Health < health, "the melee windup completes");
            foreach (var capture in match.Captures)
                TestAssert.True(capture.Packets.Any(p => Opcode(p) == 0x0D && Id(p.Plaintext.Span, 1) == hero.NetId),
                    "both clients receive an attack animation");
        }
    }

    [Check("neutral.live-markers-spawns-and-buff-visuals")]
    public static void JungleMarkersPrecedeSightAndBuffVisualsRestore()
    {
        using var match = new Match("LeeSin", Team.Order);
        match.Hero(0).Position = new(1100, 1100);
        match.Hero(1).Position = new(13900, 14000);
        match.Runtime.Tick(120);
        var monsters = match.Runtime.World.Entities.Values.Where(e => e.Kind == EntityKind.Neutral).ToArray();
        TestAssert.True(monsters.Length >= 25, "the initial ordinary camps spawned");
        foreach (var monster in monsters)
        {
            TestAssert.True(match.Runtime.Navigation.IsMovementWalkable(monster.Position),
                $"{monster.Name} camp {monster.CampId} member {monster.NeutralMember} is on walkable terrain at {monster.Position}");
            foreach (var capture in match.Captures)
                TestAssert.Equal(1, capture.Packets.Count(p => Opcode(p) == 0x66 && Id(p.Plaintext.Span, 5) == monster.NetId),
                    "camp identity is sent once before sight");
        }
        foreach (var monster in monsters.Where(e => e.Name is "AncientGolem" or "LizardElder"))
        {
            match.Clear();
            match.Hero(0).Position = monster.Position;
            match.Runtime.Delivery.Flush();
            var effect = monster.Buffs.Single(b => b.Name is "CrestoftheAncientGolem" or "BlessingoftheLizardElder").Payload.FxNetId;
            TestAssert.True(effect != 0);
            TestAssert.Equal(1, match.Captures[0].Packets.Count(p => Opcode(p) == 0x8C && Id(p.Plaintext.Span, 25) == effect),
                "first sight restores the pre-existing red/blue effect");
            match.Clear();
            match.Runtime.Delivery.Flush();
            TestAssert.False(match.Captures[0].Packets.Any(p => Opcode(p) == 0x8C && Id(p.Plaintext.Span, 25) == effect),
                "persistent effects are not duplicated on every flush");
        }
        foreach (var monster in monsters)
        {
            match.Clear();
            match.Hero(0).Position = monster.Position;
            match.Hero(1).Position = monster.Position;
            match.Runtime.Delivery.Flush();
            foreach (var observer in match.Runtime.Delivery.Observers)
                TestAssert.True(observer.View.Visible.Contains(monster.NetId),
                    $"camp {monster.CampId} member {monster.NeutralMember} reveals without attacking a sibling");
        }
    }

    [Check("spells-live-teleport-minion-effect")]
    public static void TeleportMinionEffectFollowsTheChannel()
    {
        using var match = new Match("LeeSin", Team.Order);
        var hero = match.Hero(0);
        hero.SummonerSpells[0] = "SummonerTeleport";
        var minion = match.Runtime.Waves!.SpawnFixture(Team.Order, MinionType.Melee,
            0, "L", match.Runtime.Combat.CurrentTime, 0);
        minion.Position = hero.Position + new Position2(100, 0);
        match.Runtime.Delivery.Flush();
        match.Clear();
        match.Cast(0, 0, minion, summoner: true);
        // The target effect is Teleport_Target's own BBSpellEffectCreate (BindObjectVar 'Nothing'), not a
        // server-owned buff visual, so the create packet is what carries its id.
        var create = match.Captures[0].Packets.Single(packet => Opcode(packet) == 0x8C &&
            Id(packet.Plaintext.Span, 6) == SpellPackets.BuffNameHash("Teleport_target"));
        var effect = Id(create.Plaintext.Span, 25);
        TestAssert.True(effect != 0);
        foreach (var capture in match.Captures)
        {
            var packet = capture.Packets.Single(p => Opcode(p) == 0x8C && Id(p.Plaintext.Span, 25) == effect);
            TestAssert.Equal(SpellPackets.BuffNameHash("Teleport_target"), Id(packet.Plaintext.Span, 6));
            TestAssert.Equal(0u, Id(packet.Plaintext.Span, 29), "the shipped target effect is grounded");
        }
        match.Clear();
        match.Runtime.Tick(4.1);
        TestAssert.False(minion.Buffs.Any(b => b.Name == "Teleport_Target"));
        foreach (var capture in match.Captures)
            TestAssert.True(capture.Packets.Any(p => Opcode(p) == 0x3D && Id(p.Plaintext.Span, 5) == effect));
    }

    [Check("spells-live-teleport-turret-client-owned-effect")]
    public static void TeleportTurretBuffOwnsItsShippedEffect()
    {
        using var match = new Match("LeeSin", Team.Order);
        var hero = match.Hero(0);
        hero.SummonerSpells[0] = "SummonerTeleport";
        var turret = match.Runtime.Session.MapEntities.Single(entity =>
            entity.Name == "Turret_T1_C_05");
        hero.Position = turret.Position + new Position2(200, 0);
        match.Hero(1).Position = turret.Position + new Position2(1000, 0);
        match.Runtime.Delivery.Flush();
        match.Clear();
        match.Cast(0, 0, turret, summoner: true);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "SummonerTeleport"),
            "caster keeps the Teleport channel");
        TestAssert.True(turret.Buffs.Any(buff => buff.Name == "Teleport_Turret"),
            "turret receives the shipped buff that owns AutoBuffActivateEffect");
        var turretBuffHash = SpellPackets.BuffNameHash("Teleport_Turret"); // Batch 357 (restates batch 181): the binding; its BuffName Teleport Target has no script
        var castFxHash = SpellPackets.BuffNameHash("Summoner_Cast");
        var channelFxHash = SpellPackets.BuffNameHash("Summoner_Teleport");
        foreach (var capture in match.Captures)
        {
            TestAssert.True(capture.Packets.Any(packet =>
                    Opcode(packet) == SpellPackets.BuffAddOpcode &&
                    Id(packet.Plaintext.Span, 1) == turret.NetId &&
                    Id(packet.Plaintext.Span, 9) == turretBuffHash),
                "the valid turret buff is delivered to both observers");
            var effects = capture.Packets.Where(packet => Opcode(packet) == FxPackets.CreateGroupOpcode)
                .ToArray();
            TestAssert.False(effects.Any(packet => Id(packet.Plaintext.Span, 6) == turretBuffHash),
                "a buff-script name must not be sent as a missing particle effect");
            TestAssert.True(effects.Any(packet => Id(packet.Plaintext.Span, 6) == castFxHash) &&
                            effects.Any(packet => Id(packet.Plaintext.Span, 6) == channelFxHash),
                "the two shipped caster particles remain delivered");
        }
        var casterFxIds = match.Captures[0].Packets
            .Where(packet => Opcode(packet) == FxPackets.CreateGroupOpcode &&
                (Id(packet.Plaintext.Span, 6) == castFxHash ||
                 Id(packet.Plaintext.Span, 6) == channelFxHash))
            .Select(packet => Id(packet.Plaintext.Span, 25)).ToArray();
        TestAssert.Equal(2, casterFxIds.Length,
            "both shipped caster particles are drawn");
        // SummonerTeleport.OnBuffDeactivate removes only its channel particle (EffectIDVar 'ak',
        // EffectIDVarTable InstanceVars). Summoner_Cast.troy is created with an EffectIDVar the script
        // never removes, so the server leaves that flash to the client's own particle lifetime.
        var channelFxIds = match.Captures[0].Packets
            .Where(packet => Opcode(packet) == FxPackets.CreateGroupOpcode &&
                Id(packet.Plaintext.Span, 6) == channelFxHash)
            .Select(packet => Id(packet.Plaintext.Span, 25)).ToArray();

        match.Clear();
        match.Runtime.Tick(4.1);
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "SummonerTeleport"),
            "completed channel removes its caster buff");
        TestAssert.False(turret.Buffs.Any(buff => buff.Name == "Teleport_Turret"),
            "completed channel removes the turret-owned visual buff");
        foreach (var capture in match.Captures)
        {
            TestAssert.True(capture.Packets.Any(packet =>
                    Opcode(packet) == 0x7F && Id(packet.Plaintext.Span, 1) == turret.NetId &&
                    Id(packet.Plaintext.Span, 6) == turretBuffHash),
                "the turret buff removal is delivered to both observers");
            foreach (var netId in channelFxIds)
                TestAssert.True(capture.Packets.Any(packet =>
                        Opcode(packet) == FxPackets.KillOpcode &&
                        Id(packet.Plaintext.Span, 5) == netId),
                    "completed Teleport retires its channel particle");
            foreach (var netId in casterFxIds.Except(channelFxIds))
                TestAssert.False(capture.Packets.Any(packet =>
                        Opcode(packet) == FxPackets.KillOpcode &&
                        Id(packet.Plaintext.Span, 5) == netId),
                    "the cast particle is fire-and-forget: the shipped script never removes it");
        }
    }

    [Check("spells-live-clairvoyance-eye-lifecycle")]
    public static void ClairvoyanceCreatesTeamEyesAndRetiresThem()
    {
        using var match = new Match("LeeSin", Team.Order);
        var hero = match.Hero(0); var enemy = match.Hero(1);
        // Batch 159 step 2: every map turret carries Spells/TurretBonusHealth.luaobj's own BBAddUnitPerceptionBubble
        // (BubbleSize 800 lane, 1000 nexus, 1600 ChaosTurretShrine) where DEV-06 stood in with the shrine's alone, so
        // "the eye this cast made" is the one bubble that is not one of the map's own.
        var mapBubbles = match.Runtime.World.TimedVisionBubbles.Select(bubble => bubble.BubbleId).ToHashSet();
        hero.SummonerSpells[0] = "SummonerClairvoyance";
        match.Cast(0, 0, enemy, summoner: true);
        // DEV-06, 27 Sept 2026 base-game sweep: shrine sight has an independent lifetime.
        TestAssert.Equal(1, match.Runtime.World.TimedVisionBubbles.Count(bubble => !mapBubbles.Contains(bubble.BubbleId)),
            "the cast adds its one eye bubble");
        // The eye is SummonerClairvoyance's own BBSpellEffectCreate (SelfExecute: EffectName
        // 'ClairvoyanceEyeLong_green.troy', EffectNameForOtherTeam 'ClairvoyanceEyeLong_red.troy', EffectIDVar
        // ParticleID, EffectID2Var ParticleID2), retired by the two BBSpellEffectRemove calls of its
        // OnBuffDeactivateBuildingBlocks. The bubble beside it is a separate BBAddPosPerceptionBubble whose client
        // parse carries no effect parameter (native-catalog), so it cannot hold the eye's net id: that link was the
        // hand-written CastClairvoyance's (EffectNetId: eye) and went with the script, which owns both halves. One
        // effect serves both team names here (SpellEffectCreate gives EffectIDVar and EffectID2Var the same id), so
        // each observer is told that one id under its own team's name and the pair of removes retires it once.
        uint eye = 0;
        for (var seat = 0; seat < 2; seat++)
        {
            var name = SpellPackets.BuffNameHash(seat == 0 ? "ClairvoyanceEyeLong_green" : "ClairvoyanceEyeLong_red");
            var packet = match.Captures[seat].Packets.Single(p =>
                Opcode(p) == FxPackets.CreateGroupOpcode && Id(p.Plaintext.Span, 6) == name);
            if (seat == 0) eye = Id(packet.Plaintext.Span, 25);
            else TestAssert.Equal(eye, Id(packet.Plaintext.Span, 25),
                "both team variants share the one eye effect id");
        }
        match.Clear();
        match.Runtime.Tick(6.1);
        TestAssert.Equal(0, match.Runtime.World.TimedVisionBubbles.Count(bubble => !mapBubbles.Contains(bubble.BubbleId)),
            "the buff's deactivation removes the eye bubble");
        foreach (var capture in match.Captures)
            TestAssert.True(capture.Packets.Any(p =>
                    Opcode(p) == FxPackets.KillOpcode && Id(p.Plaintext.Span, 5) == eye),
                "the buff's deactivation retires the eye for both observers");
    }

    private static uint Id(ReadOnlySpan<byte> packet, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(packet[offset..]);
}
