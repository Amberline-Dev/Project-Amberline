using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-yorick-b5-r-mark-delay-zombie-presentation")]
    public static void YorickRRestoresTheScriptedPresentation()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var self in new[] { false, true })
        {
            using var match = new Match("Yorick", team, ally: true);
            var runtime = match.Runtime; var owner = match.Hero(0);
            var target = self ? owner : match.Hero(2); var enemy = match.Hero(1);
            match.Cast(0, 3, target); runtime.Tick(.3);
            TestAssert.True(target.Buffs.Any(buff => buff.Name == "YorickReviveAllySelf"), "the marker is on the target");
            // 19 October 2026 (batch 109): the green and red marker is the script's own pair (BBSpellEffectCreate with EffectNameForOtherTeam), one
            // effect id for every observer; the id is read from the wire, the BuffEffectService payload column is gone.
            YorickTeamEffectId(match, target, "yorick_ult_01_teamID");
            match.Clear();
            runtime.Combat.ApplyHit(enemy.NetId, target.NetId, 10000,
                DamageType.True, DamageSourceKind.Spell);
            runtime.Delivery.Flush();
            var death = SpellPackets.PlayAnimation(target.NetId, "Death", lockAnimation: true,
                blend: false, duration: 4).Plaintext.ToArray();
            foreach (var capture in match.Captures)
            {
                TestAssert.True(capture.Packets.Any(packet => packet.Plaintext.Span.SequenceEqual(death)),
                    "YOR-06 delay plays locked Death with ScaleTime 4");
                TestAssert.Equal(1, capture.Packets.Count(packet => Opcode(packet) == 0x8C &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) ==
                        SpellPackets.BuffNameHash("YorickRevenantDeathSound")),
                    "the already-dead revenant emits its death sound once");
            }
            match.Clear(); runtime.Tick(1.01);
            TestAssert.True(target.Buffs.Any(buff => buff.Name == "YorickRAZombie"), "the zombie buff is on the target");
            var zombieFx = YorickTeamEffectId(match, target, "yorick_ult_03_teamID");
            var unlock = SpellPackets.StopAnimation(target.NetId, blend: false).Plaintext.ToArray();
            var idle = SpellPackets.PlayAnimation(target.NetId, "idle1", lockAnimation: true,
                blend: false).Plaintext.ToArray();
            foreach (var capture in match.Captures)
            {
                var clips = capture.Packets.Where(packet => Opcode(packet) is 0xB8 or 0x2B &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == target.NetId).ToArray();
                TestAssert.Equal(3, clips.Length, "delay ends with unlock, idle1, unlock");
                TestAssert.True(clips[0].Plaintext.Span.SequenceEqual(unlock));
                TestAssert.True(clips[1].Plaintext.Span.SequenceEqual(idle));
                TestAssert.True(clips[2].Plaintext.Span.SequenceEqual(unlock));
            }
            match.Runtime.Session.Participants[1].View.Reset();
            match.Clear(); runtime.Delivery.Flush();
            var restored = match.Captures[1].Packets.Single(packet => Opcode(packet) == 0x8C &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[25..]) == zombieFx);
            TestAssert.Equal(SpellPackets.BuffNameHash("yorick_ult_03_teamID_red"),
                BinaryPrimitives.ReadUInt32LittleEndian(restored.Plaintext.Span[6..]));
        }
    }

    /// <summary>
    /// The team pair of a Yorick marker effect as every observer received it: one 0x8C per seat bound to <paramref name="target"/>, named
    /// <paramref name="stem"/> + <c>_green</c> for the target's team and <c>_red</c> for the other (the script's EffectNameForOtherTeam), all with one
    /// effect id. Returns that id.
    /// </summary>
    private static uint YorickTeamEffectId(Match match, Entity target, string stem)
    {
        var effectId = 0u;
        for (var seat = 0; seat < match.Captures.Count; seat++)
        {
            var name = stem + (match.Hero(seat).Team == target.Team ? "_green" : "_red");
            var packet = match.Captures[seat].Packets.Single(packet => Opcode(packet) == 0x8C &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == SpellPackets.BuffNameHash(name) &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[29..]) == target.NetId);
            var id = BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[25..]);
            TestAssert.True(id != 0, $"seat {seat}: {name} has an effect id");
            if (seat == 0) effectId = id;
            else TestAssert.Equal(effectId, id, $"seat {seat}: one effect id for both names of the pair");
        }
        return effectId;
    }

    [Check("spells-yorick-b5-persistent-spectral-and-ally-cross")]
    public static void YorickPersistentEffectsRestoreTheirExistingIdentities()
    {
        using var match = new Match("Yorick", Team.Order, ally: true);
        var runtime = match.Runtime; var owner = match.Hero(0); var enemy = match.Hero(1); var ally = match.Hero(2);
        match.Cast(0, 0, owner);
        runtime.Combat.ApplyHit(owner.NetId, enemy.NetId, owner.AttackDamage,
            sourceKind: DamageSourceKind.Basic);
        var ghoul = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.YorickGhoul);
        match.Cast(0, 3, ally); runtime.Tick(.3);
        // 19 October 2026 (batch 109): the scripts create the effects, so the ids are read from the wire, not from BuffPayload columns.
        // YorickSummonSpectral binds YorickPHSpectral to the ghoul and never removes it; the replay for a late observer is the persistent-effect
        // rule (BbNativeHandlers.SpellEffectCreate): an effect bound to a unit whose id the script removes later. YorickReviveAllySelf removes its
        // yorick_ult_02 with the marker, so that one is restored with its identity and retired once; the placeholder on a 4.5 s ghoul is not kept
        // (it would stay in the ledger forever, nothing destroys it) and a late observer sees the ghoul without it.
        uint Created(string name, Entity bound) => BinaryPrimitives.ReadUInt32LittleEndian(match.Captures[0].Packets.Single(packet => Opcode(packet) == 0x8C &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == SpellPackets.BuffNameHash(name) &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[29..]) == bound.NetId).Plaintext.Span[25..]);
        var spectral = Created("YorickPHSpectral", ghoul);
        var cross = Created("yorick_ult_02", ally);
        TestAssert.True(spectral != 0 && cross != 0);
        runtime.Session.Participants[0].View.Reset();
        match.Clear(); runtime.Delivery.Flush();
        foreach (var (id, actor, name) in new[] { (cross, ally, "yorick_ult_02") })
        {
            var packet = match.Captures[0].Packets.Single(packet => Opcode(packet) == 0x8C &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[25..]) == id);
            TestAssert.Equal(SpellPackets.BuffNameHash(name), BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]));
            TestAssert.Equal(actor.NetId, BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[29..]));
        }
        match.Clear();
        runtime.Combat.Kill(ghoul, enemy.NetId);
        var marker = ally.Buffs.Single(buff => buff.Name == "YorickReviveAllySelf");
        runtime.Buffs.RemoveBuff(ally, marker.Slot);
        runtime.Delivery.Flush();
        TestAssert.Equal(1, match.Captures[0].Packets.Count(packet => Opcode(packet) == 0x3D &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == cross),
            "YOR-07 restored effect retires exactly once");
    }

    [Check("spells-yorick-b5-lethal-impacts-keep-transient-effects")]
    public static void YorickLethalHitsKeepTheirScriptedImpactEffects()
    {
        foreach (var slot in new[] { 0, 1, 2 })
        {
            using var match = new Match("Yorick", Team.Order);
            var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
            enemy.Position = hero.Position + new Position2(100, 0);
            enemy.Health = 1; enemy.HealthRegen = 0;
            match.Cast(0, slot, slot == 0 ? hero : enemy);
            if (slot == 0) runtime.Combat.ApplyHit(hero.NetId, enemy.NetId, hero.AttackDamage,
                sourceKind: DamageSourceKind.Basic);
            else SpellStep(match, .26);
            runtime.Delivery.Flush();
            TestAssert.False(enemy.Alive, "impact is lethal");
            var name = slot switch { 0 => "yorick_spectralGhoul_attack_buf_tar",
                1 => "yorick_necroExplosion_unit_tar", _ => "yorick_ravenousGhoul_cas_tar" };
            TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x8C &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == SpellPackets.BuffNameHash(name) &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[29..]) == enemy.NetId),
                "shipped transient impact survives lethal damage");
            TestAssert.False(enemy.Buffs.Any(buff => buff.Name is "YorickSpectralPrimaryTarget" or
                "YorickRavenousPrimaryTarget" or "YorickDecayedSlow"), "persistent recipient state retires at death");
        }
    }
}
