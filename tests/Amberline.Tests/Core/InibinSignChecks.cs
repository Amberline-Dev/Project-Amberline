using System.Numerics;
using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests;

/// <summary>
/// The stock client reads the inibin 16-bit integer segment sign-extended
/// (the client's raw integer lookup, a (int)(short) cast); 56 shipped values
/// have the top bit set (0x8000 or above). <see cref="InibinValue.AsDouble"/> is that client decode; only
/// <see cref="InibinValue.UInt16"/> keeps the raw unsigned bytes for the source-backed corpus digests.
/// </summary>
public static class InibinSignChecks
{
    [Check("data.inibin.client-signed-16bit-segment")]
    public static void SixteenBitSegmentReadsSignedThroughTheClientPath()
    {
        // Synthetic file: segment 3 (16-bit) holds the probes, segment 4 (byte) must keep its unsigned decode.
        var probes = new (ushort Stored, int Client)[]
            { (0xFFFF, -1), (0xFF99, -103), (0x8000, -32768), (0x7FFF, 32767), (1, 1), (0, 0) };
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.Latin1, leaveOpen: true))
        {
            writer.Write((byte)2);
            writer.Write((ushort)0);                                   // no string block
            writer.Write((ushort)((1 << 3) | (1 << 4)));
            writer.Write((ushort)probes.Length);
            for (var i = 0; i < probes.Length; i++) writer.Write((uint)(100 + i));
            foreach (var probe in probes) writer.Write(probe.Stored);
            writer.Write((ushort)1);
            writer.Write(200u);
            writer.Write((byte)0xFF);
        }
        var data = InibinReader.Parse(stream.ToArray());
        for (var i = 0; i < probes.Length; i++)
        {
            var value = data[(uint)(100 + i)];
            TestAssert.Equal(InibinValueKind.UInt16, value.Kind);
            TestAssert.Equal(probes[i].Stored, value.UInt16, "the raw property keeps the stored bytes");
            TestAssert.Equal((double)probes[i].Client, value.AsDouble(), $"{probes[i].Stored:X4} AsDouble");
            TestAssert.Equal((double)probes[i].Client, value.AsClientNumber(), $"{probes[i].Stored:X4} AsClientNumber");
            TestAssert.Equal(new BigInteger(probes[i].Client), value.AsInteger(), $"{probes[i].Stored:X4} AsInteger");
            TestAssert.Equal(probes[i].Stored.ToString(), value.ToString(), "ToString stays the raw unsigned form");
        }
        TestAssert.Equal(255.0, data[200].AsDouble(), "the byte segment is unsigned in the client getter");

        // Every shipped file: the 56 values at or above 0x8000 all read negative, raw minus 65536.
        var perFile = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        foreach (var path in Directory.EnumerateFiles(GameDataPaths.Default.Root, "*.inibin", SearchOption.AllDirectories))
        {
            foreach (var value in InibinReader.Load(path).Values)
            {
                if (value.Kind != InibinValueKind.UInt16 || value.UInt16 < 0x8000) continue;
                total++;
                var name = Path.GetFileName(path);
                perFile[name] = perFile.GetValueOrDefault(name) + 1;
                TestAssert.Equal(value.UInt16 - 65536.0, value.AsDouble(), name);
                TestAssert.True(value.AsDouble() < 0, name);
            }
        }
        TestAssert.Equal(56, total, "shipped 16-bit values at or above 0x8000");
        TestAssert.Equal(14, perFile["HeroSpawnOffsets.inibin"], "facing degrees");
        TestAssert.Equal(3, perFile["GeneralCharacterData.inibin"]);
        TestAssert.Equal(8, perFile["Janna.inibin"]);
        TestAssert.Equal(1, perFile["ChaosTurretShrine.inibin"]);
    }

    [Check("data.gamedata.client-signed-character-numbers")]
    public static void CharacterNumbersAreTheClientSignedValues()
    {
        var data = GameData.Default;
        // Data*BaseStaticMPRegen is the stored 65535 on six non-hero characters; the client reads -1. That is
        // retail's data: a negative regen on a unit whose mana the server never regenerates (CombatService only
        // adds mana for positive regen), so the value is a faithful stat, not a behaviour change.
        foreach (var name in new[] { "MaokaiSproutling", "OdinMinionGraveyardPortal", "OdinMinionSpawnPortal",
            "OdinQuestIndicator", "OriannaBall", "TeemoMushroom" })
        {
            var champion = data.Champion(name);
            TestAssert.Equal(-1.0, champion.MpRegen, name + " MpRegen");
            TestAssert.Equal(-1.0 + champion.MpRegenPerLevel, data.StatsAtLevel(champion, 1).MpRegen, name + " level 1");
        }
        TestAssert.Near(1.38, data.Champion("Janna").MpRegen, 1e-6, "float-segment regen is unchanged");

        // Data*AttackDelayOffsetPercent -1: raw period 1.6 * (1 - 1) = 0 (clamped to 0.4 s by every period helper).
        foreach (var name in new[] { "OrderTurretShrine", "ChaosTurretShrine", "OdinOrderTurretShrine",
            "OdinChaosTurretShrine", "Red_Minion_MechRange" })
            TestAssert.Equal(-1.0, data.Champion(name).Raw[InibinReader.KeyHash("Data*AttackDelayOffsetPercent")].AsDouble(),
                name + " AttackDelayOffsetPercent");
    }

    [Check("neutral.client-signed-attack-delay-offset")]
    public static void NeutralPeriodUsesTheSignedDelayOffset()
    {
        // Red_Minion_MechRange stores AttackDelayOffsetPercent as 65535. The unsigned decode gave
        // 1.6 * 65536 -> clamped to the 5 s maximum; the client's -1 gives 0 -> the 0.4 s minimum.
        var stats = NeutralStatsCalculator.Calculate(GameData.Default, "Red_Minion_MechRange");
        TestAssert.Near(0.4, stats.AttackPeriod, 1e-12, "attack period");
        var castOffset = GameData.Default.Champion("Red_Minion_MechRange").Raw[
            InibinReader.KeyHash("Data*AttackDelayCastOffsetPercent")].AsDouble();
        TestAssert.Near(0.4 * Math.Max(0.0, 0.3 + castOffset), stats.Windup, 1e-12, "windup");
    }

    [Check("session.hero-spawn-table-matches-client-decode")]
    public static void HeroSpawnTableMatchesTheClientDecodeOfHeroSpawnOffsets()
    {
        // The client's spawn-facing getter reads "Facing<slot+1>" of section
        // Order<size>/Chaos<size> through the float getter with default 0.0; a facing stored in the 16-bit
        // segment (14 of them) is therefore the signed degrees. Decoded unsigned they would all be rotated by
        // 65536 mod 360 = 16 degrees. The position vectors need no sign, but Order1*Pos1 is a byte vector, which
        // the client reads divided by ten (the client's raw float3 lookup, 0.1f):
        // (100,0,100) raw is the client's (10,0,10), the lone Order hero's spot on the fountain centre.
        var offsets = InibinReader.LoadData("Characters/HeroSpawnOffsets.inibin");
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var anchor = team == Team.Order ? new Position2(25.902751922607422, 263.5519104003906) : new Position2(13933.7890625, 14170.09375);
            for (var size = 1; size <= 5; size++)
            for (var slot = 0; slot < size; slot++)
            {
                var section = $"{team}{size}";
                var position = offsets[InibinReader.KeyHash($"{section}*Pos{slot + 1}")].AsClientVector();
                var facing = offsets.TryGetValue(InibinReader.KeyHash($"{section}*Facing{slot + 1}"), out var value)
                    ? value.Kind == InibinValueKind.Boolean ? (value.Boolean ? 1.0 : 0.0) : value.AsDouble()
                    : 0.0;
                var (spawn, direction) = MapDefinitions.InitialHeroSpawn(team, size, slot);
                var label = $"{section} slot {slot + 1}";
                TestAssert.Near(anchor.X + position[0], spawn.X, 1e-9, label + " x");
                TestAssert.Near(anchor.Z + position[2], spawn.Z, 1e-9, label + " z");
                var degrees = Math.Atan2(-direction.Z, direction.X) * 180.0 / Math.PI;
                var delta = (degrees - facing) % 360.0;
                TestAssert.Near(0.0, delta > 180 ? delta - 360 : delta < -180 ? delta + 360 : delta, 1e-9, label + " facing");
            }
        }
    }
}
