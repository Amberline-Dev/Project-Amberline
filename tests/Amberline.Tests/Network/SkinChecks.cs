using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("skins.shipped-catalog")]
    public static void ShippedSkinCatalog()
    {
        var counts = new Dictionary<string, int>
        {
            ["Annie"] = 6, ["Orianna"] = 2, ["LeeSin"] = 2, ["Nidalee"] = 4,
            ["Yorick"] = 2, ["Shaco"] = 4, ["Ezreal"] = 4, ["Vayne"] = 3,
            ["Janna"] = 2, ["Blitzcrank"] = 4,
        };
        foreach (var (champion, count) in counts)
        {
            var skins = GameData.Default.Skins(champion);
            TestAssert.Equal(Enumerable.Range(0, count + 1).ToArray(), skins.Select(skin => skin.Id).ToArray());
            TestAssert.True(skins.All(skin => !skin.DisplayName.StartsWith("Skin ", StringComparison.Ordinal)),
                $"{champion} has shipped English labels for every variant");
        }
        TestAssert.Equal("Reverse Annie", GameData.Default.Skins("Annie")[6].DisplayName);
        foreach (var (model, count) in new[] { ("AnnieTibbers", 6), ("OriannaBall", 2), ("Nidalee_Cougar", 4) })
        {
            var data = GameData.Default;
            var raw = data.Champion(model).Raw;
            for (var id = 1; id <= count; id++)
            foreach (var field in new[] { "SimpleSkin", "Skeleton", "Texture" })
                TestAssert.True(File.Exists(data.Paths.Data($"Characters/{model}/{raw[InibinReader.KeyHash($"MeshSkin{id}*{field}")].AsString()}")),
                    $"{model} variant {id} has its {field} asset");
        }
    }

    [Check("skins.pet-and-form-replication")]
    public static void PetAndFormSkinReplication()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var champion in new[] { "Annie", "Orianna", "Shaco", "Yorick", "Nidalee" })
        {
            var skin = GameData.Default.Skins(champion)[^1].Id;
            using var match = new Match(champion, team, ally: champion == "Yorick", skinId: skin);
            var owner = match.Hero(0);
            var source = champion == "Yorick" ? match.Hero(2) : owner;
            if (champion == "Yorick") source.SkinId = 1;
            var expectedSkin = source.SkinId;
            match.Cast(0, champion == "Orianna" ? 0 : 3,
                champion is "Annie" or "Orianna" ? match.Hero(1) : source);
            match.Runtime.Tick(0.6);
            uint objectId;
            byte creationOpcode;
            if (champion == "Orianna")
            {
                // The ball is the unit the Q script spawns where the missile ends (BBSpawnMinion OriannaBall), announced through the ball projection.
                objectId = match.Runtime.World.Entities.Values.Single(unit => unit.ChampionName == "OriannaBall" && unit.Alive).NetId;
                creationOpcode = 0x80;
            }
            else if (champion == "Nidalee")
            {
                TestAssert.True(owner.Buffs.Any(buff => buff.Name == "AspectOfTheCougar"));
                TestAssert.Equal(skin, owner.SkinId, "transformation keeps the base variant");
                TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x9F));
                objectId = owner.NetId;
                creationOpcode = 0x4F;
            }
            else
            {
                var pet = match.Runtime.World.Entities.Values.Single(entity => entity.OwnerNetId == owner.NetId &&
                    entity.Kind is EntityKind.Pet or EntityKind.ShacoClone or EntityKind.YorickRevenant);
                TestAssert.Equal(expectedSkin, pet.SkinId, "pet inherits the source champion's variant");
                objectId = pet.NetId;
                // Champion sweep ANN-02, 27 Sept 2026: Tibbers is also a native pet.
                creationOpcode = 0x3C;
            }
            if (champion != "Nidalee") AssertCreationSkin(match.Captures[0].Packets);
            // A fresh observer exercises reconstruction instead of reusing the action-time packet.
            var fresh = new Capture();
            match.Runtime.Delivery.AddObserver(new ReplicationObserver(99, team, owner.NetId, fresh, ready: true));
            match.Runtime.Delivery.Flush();
            AssertCreationSkin(fresh.Packets);

            void AssertCreationSkin(IEnumerable<PlaintextPacket> packets)
            {
                var creation = packets.Single(packet => Opcode(packet) == creationOpcode &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..9]) == objectId);
                var offset = creationOpcode == 0x3C ? 102 : creationOpcode == 0x4F ? 19 : 22;
                TestAssert.Equal(expectedSkin, BinaryPrimitives.ReadInt32LittleEndian(creation.Plaintext.Span.Slice(offset, 4)),
                    $"{team}/{champion}: variant survives creation and observer reconstruction");
            }
        }
    }

    // Batch 730 (owner, 9 November 2026: "Astronaut Teemo still uses the mushroom model of the base skin for his R"). The Noxious Trap is the
    // shipped BantamTrap's BBSpawnMinion Skin='TeemoMushroom' with no variant argument, and TeemoMushroom.inibin has MeshSkin4..6 (the astronaut
    // satellite trap, the easter bunny trap and the super trap) for Teemo's skins 4..6. The trap wears its owner's variant on the 0x80 creation.
    // Reconstruction: the native's rule is not recovered; the Orianna ball, the pets and the clones already copy the owner's variant.
    [Check("skins.spawned-unit-owner-variant")]
    public static void SpawnedUnitWearsItsOwnersVariant()
    {
        using var match = new BbM3.Match("Teemo");
        var teemo = match.Hero;
        teemo.SkinId = 4;
        match.Learn(1);
        match.Module.OnRankUp(match.Runtime.Spells, teemo, 3, 1);
        match.Refill();
        var own = new BbM3.WireCapture(() => match.Now);
        match.Runtime.Delivery.AddObserver(new ReplicationObserver(1, Team.Order, teemo.NetId, own, ready: true));
        TestAssert.True(match.Cast(3, at: match.Foe.Position), "Noxious Trap is accepted");
        match.Advance(match.CastFrameOf(3) + 0.5);
        match.Runtime.Delivery.Flush();
        var trap = match.Runtime.World.Entities.Values.Single(unit => unit.Alive && unit.ChampionName == "TeemoMushroom");
        TestAssert.Equal(4, trap.SkinId, "Astronaut Teemo's Noxious Trap wears variant 4 (TeemoMushroom MeshSkin4, the astronaut trap)");
        var creation = own.Packets.Select(entry => entry.Packet).Single(packet => Opcode(packet) == 0x80 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..9]) == trap.NetId);
        TestAssert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(creation.Plaintext.Span.Slice(22, 4)),
            "the trap's 0x80 creation carries the owner's variant");
        var fresh = new Capture();
        match.Runtime.Delivery.AddObserver(new ReplicationObserver(99, Team.Order, teemo.NetId, fresh, ready: true));
        match.Runtime.Delivery.Flush();
        var reconstructed = fresh.Packets.Single(packet => Opcode(packet) == 0x80 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..9]) == trap.NetId);
        TestAssert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(reconstructed.Plaintext.Span.Slice(22, 4)),
            "observer reconstruction keeps the trap's variant");
    }
}
