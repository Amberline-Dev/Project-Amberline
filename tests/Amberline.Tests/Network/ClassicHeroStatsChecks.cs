using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation;

namespace Amberline.Tests;

public static class ClassicHeroStatsChecks
{
    [Check("protocol.classic-hero-stats-schema")]
    public static void ClassicSchemaIsShippedAndOrdinal()
    {
        var schema = ClassicHeroStatsSchema.Load(GameDataPaths.Default);
        TestAssert.Equal(ClassicHeroStatsSchema.FieldCount, schema.Definitions.Count);
        TestAssert.Equal(schema.Definitions.OrderBy(definition => definition.Name, StringComparer.Ordinal).ToArray(),
            schema.Definitions.ToArray());
        TestAssert.Equal(ClassicHeroStatKind.LongLong, schema.Definitions.Single(definition => definition.Name == "ID").Kind);
        TestAssert.Equal(ClassicHeroStatKind.String, schema.Definitions.Single(definition => definition.Name == "NAME").Kind);
        TestAssert.Equal(ClassicHeroStatKind.String, schema.Definitions.Single(definition => definition.Name == "WIN").Kind);
        TestAssert.Equal(ClassicHeroStatKind.Int, schema.Definitions.Single(definition => definition.Name == "TOTAL_HEAL").Kind);
        TestAssert.Equal(ClassicHeroStatKind.Float, schema.Definitions.Single(definition => definition.Name == "TOTAL_DAMAGE_DEALT").Kind);
    }

    [Check("protocol.classic-hero-stats-encoder")]
    public static void ClassicEncoderWritesEveryTypedFieldAndRejectsMalformedMaps()
    {
        var schema = ClassicHeroStatsSchema.Load(GameDataPaths.Default);
        var values = new Dictionary<string, ClassicHeroStatValue>(StringComparer.Ordinal);
        foreach (var (definition, index) in schema.Definitions.Select((definition, index) => (definition, index)))
        {
            values[definition.Name] = definition.Kind switch
            {
                ClassicHeroStatKind.Int => ClassicHeroStatValue.FromInt(index + 1),
                ClassicHeroStatKind.Float => ClassicHeroStatValue.FromFloat(index + 0.5f),
                ClassicHeroStatKind.String => ClassicHeroStatValue.FromString(definition.Name),
                ClassicHeroStatKind.LongLong => ClassicHeroStatValue.FromLongLong(0x01020304L),
                _ => throw new InvalidDataException(),
            };
        }

        var packet = ClassicHeroStatsPackets.HeroStats(0x10203040, schema, values);
        var bytes = packet.Plaintext.Span;
        TestAssert.Equal((byte)0x4B, bytes[0]);
        TestAssert.Equal(0x10203040u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..5]));
        TestAssert.Equal((uint)(bytes.Length - 5), BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..9]));
        TestAssert.Equal((byte)3, packet.Channel);
        TestAssert.Equal(LeaguePacketReliability.Reliable, packet.Reliability);
        TestAssert.Equal(new LeaguePacketEntityReference(LeaguePacketEntityRole.Hero, 0x10203040),
            packet.EntityReferences.Single());

        var cursor = 9;
        foreach (var (definition, index) in schema.Definitions.Select((definition, index) => (definition, index)))
        {
            switch (definition.Kind)
            {
                case ClassicHeroStatKind.Int:
                    TestAssert.Equal(index + 1, BinaryPrimitives.ReadInt32LittleEndian(bytes[cursor..]));
                    cursor += 4;
                    break;
                case ClassicHeroStatKind.Float:
                    TestAssert.Near(index + 0.5, BitConverter.ToSingle(bytes[cursor..(cursor + 4)]), 1e-6);
                    cursor += 4;
                    break;
                case ClassicHeroStatKind.String:
                    var length = BinaryPrimitives.ReadInt16LittleEndian(bytes[cursor..]);
                    cursor += 2;
                    TestAssert.Equal(definition.Name.Length, length);
                    TestAssert.Equal(definition.Name,
                        Encoding.ASCII.GetString(bytes[cursor..(cursor + length)]));
                    cursor += length;
                    break;
                case ClassicHeroStatKind.LongLong:
                    TestAssert.Equal(0x01020304,
                        BinaryPrimitives.ReadInt32LittleEndian(bytes[cursor..]));
                    cursor += 4;
                    break;
            }
        }
        TestAssert.Equal(bytes.Length, cursor);

        var missing = new Dictionary<string, ClassicHeroStatValue>(values, StringComparer.Ordinal);
        missing.Remove(schema.Definitions[0].Name);
        TestAssert.Throws<ArgumentException>(() => ClassicHeroStatsPackets.HeroStats(1, schema, missing));

        var extra = new Dictionary<string, ClassicHeroStatValue>(values, StringComparer.Ordinal)
        {
            ["UNKNOWN"] = ClassicHeroStatValue.FromInt(1),
        };
        TestAssert.Throws<ArgumentException>(() => ClassicHeroStatsPackets.HeroStats(1, schema, extra));

        var wrongKind = new Dictionary<string, ClassicHeroStatValue>(values, StringComparer.Ordinal)
        {
            ["ID"] = ClassicHeroStatValue.FromInt(1),
        };
        TestAssert.Throws<ArgumentException>(() => ClassicHeroStatsPackets.HeroStats(1, schema, wrongKind));

        var tooLong = new Dictionary<string, ClassicHeroStatValue>(values, StringComparer.Ordinal)
        {
            ["NAME"] = ClassicHeroStatValue.FromString(new string('x', 255)),
        };
        TestAssert.Throws<ArgumentOutOfRangeException>(() => ClassicHeroStatsPackets.HeroStats(1, schema, tooLong));

        var nul = new Dictionary<string, ClassicHeroStatValue>(values, StringComparer.Ordinal)
        {
            ["NAME"] = ClassicHeroStatValue.FromString("bad\0name"),
        };
        TestAssert.Throws<ArgumentException>(() => ClassicHeroStatsPackets.HeroStats(1, schema, nul));

        foreach (var overflowingId in new[] { (long)int.MaxValue + 1, (long)int.MinValue - 1 })
        {
            var overflowing = new Dictionary<string, ClassicHeroStatValue>(values, StringComparer.Ordinal)
            {
                ["ID"] = ClassicHeroStatValue.FromLongLong(overflowingId),
            };
            TestAssert.Throws<OverflowException>(() => ClassicHeroStatsPackets.HeroStats(1, schema, overflowing));
        }
    }

    [Check("protocol.classic-hero-stats-values")]
    public static void ClassicSnapshotDecodesSupportedValuesAndDefaults()
    {
        var schema = ClassicHeroStatsSchema.Load(GameDataPaths.Default);
        var hero = new Entity(0x10203040, EntityKind.Hero, UnitType.Hero,
            Team.Chaos, new Position2(100.0, 200.0))
        {
            Name = "Second",
            SkinId = 3,
            Level = 7,
            Experience = 123.5,
            ChampionKills = 2,
            ChampionDeaths = 3,
            ChampionAssists = 4,
            LaneMinionsKilled = 5,
            OtherMinionsKilled = 6,
            NeutralMinionsKilled = 7,
            TurretsKilled = 8,
            InhibitorsKilled = 9,
            NexusesKilled = 10,
        };
        hero.Inventory = [new InventorySlot(1001), null, new InventorySlot(2003), null, null, null];

        var packet = ClassicHeroStatsPackets.HeroStats(hero.NetId, schema,
            ClassicHeroStatsSnapshot.Values(hero, schema, now: 4.0, playerId: 123));
        var decoded = Decode(packet.Plaintext.Span, schema);
        TestAssert.Equal(200, (int)decoded["TEAM"]);
        TestAssert.Equal(3, (int)decoded["SKIN"],
            "owner snapshots carry the roster skin policy");
        TestAssert.Equal(2, (int)decoded["CHAMPIONS_KILLED"]);
        TestAssert.Equal(11, (int)decoded["MINIONS_KILLED"]);
        TestAssert.Equal(1001, (int)decoded["ITEM0"]);
        TestAssert.Equal(0, (int)decoded["ITEM1"]);
        TestAssert.Equal(2003, (int)decoded["ITEM2"]);
        TestAssert.Equal(0, (int)decoded["SUPER_MONSTER_KILLED"]);
        TestAssert.Equal(0, (int)decoded["WAS_AFK"]);
        TestAssert.Equal(123L, (long)decoded["ID"]);
        TestAssert.Equal(string.Empty, (string)decoded["WIN"]);
        TestAssert.Equal(0.0, (double)decoded["TIME_OF_FROM_LAST_DISCONNECT"]);
        TestAssert.Equal("Second", (string)decoded["NAME"]);
        TestAssert.Near(123.5, (double)decoded["EXP"], 1e-6);
    }

    [Check("protocol.classic-hero-stats-policy-fields")]
    public static void ClassicSnapshotEncodesActivityObjectiveDisconnectAndTerminalPolicies()
    {
        var schema = ClassicHeroStatsSchema.Load(GameDataPaths.Default);
        var hero = new Entity(0x10203041, EntityKind.Hero, UnitType.Hero,
            Team.Order, new Position2(100.0, 200.0))
        {
            SkinId = 7,
        };
        hero.MatchStatistics.StartMatch(0.0);
        hero.MatchStatistics.UpdateAfk(300.0, 300.0);
        hero.MatchStatistics.RecordSuperMonsterKill();
        hero.MatchStatistics.RecordDisconnect(4.0);

        var packet = ClassicHeroStatsPackets.HeroStats(hero.NetId, schema,
            ClassicHeroStatsSnapshot.Values(hero, schema, now: 10.0, playerId: 123, win: "Win"));
        var decoded = Decode(packet.Plaintext.Span, schema);
        TestAssert.Equal(7, (int)decoded["SKIN"]);
        TestAssert.Equal(123L, (long)decoded["ID"]);
        TestAssert.Equal(1, (int)decoded["WAS_AFK"]);
        TestAssert.Equal(1, (int)decoded["SUPER_MONSTER_KILLED"]);
        TestAssert.Equal("Win", (string)decoded["WIN"]);
        TestAssert.Near(6.0, (double)decoded["TIME_OF_FROM_LAST_DISCONNECT"], 1e-6);

        var oversized = ClassicHeroStatsSnapshot.Values(hero, schema, now: 10.0,
            playerId: (ulong)int.MaxValue + 1, win: "Loss");
        TestAssert.Equal(0L, oversized["ID"].LongLong,
            "IDs outside the signed Classic holder remain on the outer roster identity");
        TestAssert.Equal("Loss", oversized["WIN"].Text ?? string.Empty);
    }

    [Check("protocol.classic-hero-stats-public-projection")]
    public static void PublicProjectionKeepsForeignCollectionPrivateFieldsAtNativeDefaults()
    {
        var schema = ClassicHeroStatsSchema.Load(GameDataPaths.Default);
        var hero = new Entity(0x10203040, EntityKind.Hero, UnitType.Hero,
            Team.Order, new Position2(100.0, 200.0))
        {
            ChampionKills = 3,
            ChampionDeaths = 2,
            ChampionAssists = 7,
            LaneMinionsKilled = 11,
            OtherMinionsKilled = 4,
            NeutralMinionsKilled = 19,
            Gold = 9999.0,
        };

        var packet = ClassicHeroStatsPackets.HeroStats(hero.NetId, schema,
            ClassicHeroStatsSnapshot.PublicValues(hero, schema));
        var decoded = Decode(packet.Plaintext.Span, schema);
        TestAssert.Equal(3, (int)decoded["CHAMPIONS_KILLED"]);
        TestAssert.Equal(2, (int)decoded["NUM_DEATHS"]);
        TestAssert.Equal(7, (int)decoded["ASSISTS"]);
        TestAssert.Equal(15, (int)decoded["MINIONS_KILLED"]);
        TestAssert.Equal(0, (int)decoded["NEUTRAL_MINIONS_KILLED"],
            "neutral kills remain on the separate absolute 0xCC path");

        var publicFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "CHAMPIONS_KILLED", "NUM_DEATHS", "ASSISTS", "MINIONS_KILLED",
        };
        foreach (var definition in schema.Definitions)
        {
            if (publicFields.Contains(definition.Name)) continue;
            var actual = decoded[definition.Name];
            switch (definition.Kind)
            {
                case ClassicHeroStatKind.Int:
                    TestAssert.Equal(0, (int)actual, "foreign public projection " + definition.Name);
                    break;
                case ClassicHeroStatKind.Float:
                    TestAssert.Equal(0.0, (double)actual, "foreign public projection " + definition.Name);
                    break;
                case ClassicHeroStatKind.String:
                    TestAssert.Equal(string.Empty, (string)actual,
                        "foreign public projection " + definition.Name);
                    break;
                case ClassicHeroStatKind.LongLong:
                    TestAssert.Equal(0L, (long)actual, "foreign public projection " + definition.Name);
                    break;
            }
        }
    }

    /// <summary>
    /// Batch 462: a 0x4B read the way the client reads it, independently of the server's schema. The client's collection is every
    /// <c>HeroStatList.txt</c> name that <c>Data\Globals\&lt;mode&gt;_Stats</c> types under <c>HeroStats*&lt;name&gt;</c>,
    /// held in a name-ordered map; the client's field reader takes four bytes for INT, FLOAT and LONGLONG and a 16-bit length
    /// plus the bytes for STRING, then logs "malformed buffer" unless it consumed exactly the declared count.
    /// </summary>
    internal static Dictionary<string, object> DecodeClient(ReadOnlySpan<byte> bytes, string mode)
    {
        var paths = GameDataPaths.Default;
        var types = InibinReader.Load(paths, $"Globals/{mode}_Stats.inibin", raw: true);
        var collection = File.ReadAllLines(paths.Data("Globals/HeroStatList.txt"))
            .Select(line => line.Trim())
            .Where(name => name.Length != 0 && !name.StartsWith('#') &&
                           types.TryGetValue(InibinReader.KeyHash("HeroStats*" + name), out _))
            .Order(StringComparer.Ordinal)
            .Select(name => (Name: name, Type: types[InibinReader.KeyHash("HeroStats*" + name)].AsString()))
            .ToArray();
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        var cursor = 9;
        foreach (var (name, type) in collection)
        {
            if (type == "STRING")
            {
                var length = BinaryPrimitives.ReadInt16LittleEndian(bytes[cursor..]);
                values[name] = Encoding.ASCII.GetString(bytes[(cursor + 2)..(cursor + 2 + length)]);
                cursor += 2 + length;
                continue;
            }
            values[name] = type == "FLOAT"
                ? (double)BitConverter.ToSingle(bytes[cursor..(cursor + 4)])
                : (object)BinaryPrimitives.ReadInt32LittleEndian(bytes[cursor..]);
            cursor += 4;
        }
        TestAssert.Equal(BinaryPrimitives.ReadInt32LittleEndian(bytes[5..9]), cursor - 5,
            $"the {mode} collection consumes the 0x4B count exactly (the client's malformed-buffer test)");
        return values;
    }

    internal static Dictionary<string, object> Decode(ReadOnlySpan<byte> bytes,
        ClassicHeroStatsSchema schema)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        var cursor = 9;
        foreach (var definition in schema.Definitions)
        {
            switch (definition.Kind)
            {
                case ClassicHeroStatKind.Int:
                    values[definition.Name] = BinaryPrimitives.ReadInt32LittleEndian(bytes[cursor..]);
                    cursor += 4;
                    break;
                case ClassicHeroStatKind.Float:
                    values[definition.Name] = (double)BitConverter.ToSingle(bytes[cursor..(cursor + 4)]);
                    cursor += 4;
                    break;
                case ClassicHeroStatKind.String:
                    var length = BinaryPrimitives.ReadInt16LittleEndian(bytes[cursor..]);
                    cursor += 2;
                    values[definition.Name] = Encoding.ASCII.GetString(bytes[cursor..(cursor + length)]);
                    cursor += length;
                    break;
                case ClassicHeroStatKind.LongLong:
                    values[definition.Name] = (long)BinaryPrimitives.ReadInt32LittleEndian(bytes[cursor..]);
                    cursor += 4;
                    break;
                default:
                    throw new InvalidDataException();
            }
        }
        TestAssert.Equal(bytes.Length, cursor);
        return values;
    }
}
