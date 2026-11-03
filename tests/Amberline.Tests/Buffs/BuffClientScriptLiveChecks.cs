using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

/// <summary>
/// Batch 357: the name a buff's add carries must be one the stock client can build a buff from, a Spells or Items script of that exact
/// name (the client builds from Data\Spells\%s.lua, its find-or-create routine looks it up by name, and the 0xBF handler
/// drops an add whose name it cannot build). Part 3 checks the lifetime of the adds that carry a duration by level.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    [Check("buffs.wire-name.sivir-e-and-zilean-r-add-a-client-script-name")]
    public static void SivirSpellShieldAndZileanChronoShiftAddAClientScriptName()
    {
        using (var sivir = new Match("Sivir", Team.Order))
        {
            sivir.Cast(0, 2, sivir.Hero(0));
            sivir.Runtime.Tick(0.25);
            AssertHolderAddNamesAClientScript(sivir, sivir.Hero(0).NetId, "SpellShield", "Spell Shield", "Sivir E");
        }
        using (var zilean = new Match("Zilean", Team.Order, ally: true))
        {
            zilean.Cast(0, 3, zilean.Hero(2));
            zilean.Runtime.Tick(0.25);
            AssertHolderAddNamesAClientScript(zilean, zilean.Hero(2).NetId, "ChronoShift", "Chrono Shift", "Zilean R");
        }
    }

    [Check("buffs.wire-name.every-shipped-buff-script-sends-a-client-script-name")]
    public static void EveryShippedBuffScriptSendsAClientScriptName()
    {
        var data = GameData.Default;
        var named = 0;
        var broken = new List<string>();
        foreach (var directory in new[] { "Spells", "Items" })
            foreach (var file in Directory.EnumerateFiles(GameDataPaths.Default.Data(directory), "*.luaobj").OrderBy(f => f, StringComparer.Ordinal))
            {
                var binding = Path.GetFileNameWithoutExtension(file);
                if (LuaObjectReader.Load(file).Globals.GetValueOrDefault("BuffName") is LuaValue.String { Value.Length: > 0 })
                {
                    named++;
                    // Batch 357: binding stem. The wire name is the binding, and that stem has a client script (the add's own).
                    if (data.ClientBuffName(binding) != binding || !data.HasClientBuff(binding)) broken.Add(binding);
                }
            }
        TestAssert.True(named > 0, "the shipped buff scripts name a BuffName");
        TestAssert.Equal(0, broken.Count,
            $"{named} buff scripts name a BuffName; {broken.Count} do not send their binding stem with a client script: {string.Join(", ", broken.Take(8))}");
    }

    /// <summary>
    /// Part 2 (batch 357): the Nunu passive. CharScriptNunu's CharOnHitUnitBuildingBlocks adds Visionary (BBIfNotHasBuff, BuffName Visions),
    /// and Visions has no client script, so the rule must send the binding the client builds from. The live hit is not driven here (open).
    /// </summary>
    [Check("buffs.wire-name.nunu-passive-add-names-a-client-script")]
    public static void NunuPassiveAddNamesAClientScript()
    {
        var data = GameData.Default;
        TestAssert.Equal("Visionary", data.ClientBuffName("Visionary"), "the Nunu passive adds the binding stem Visionary (Batch 357: binding stem; its BuffName Visions is not sent)");
        TestAssert.True(data.HasClientBuff("Visionary"), "the client builds the passive from Spells/Visionary.luaobj");
    }

    [Check("buffs.duration.morgana-q-root-holds-the-target-for-its-duration")]
    public static void MorganaDarkBindingRootHoldsTheTargetForItsDuration()
    {
        using var match = new Match("Morgana", Team.Order);
        var morgana = match.Hero(0);
        var enemy = match.Hero(1);
        morgana.Position = new Position2(7000, 7000);
        enemy.Position = new Position2(7400, 7000);
        match.Runtime.Tick(1.0);
        TestAssert.True(match.TryCastGround(0, 0, enemy.Position), "Dark Binding is accepted");
        match.Runtime.Tick(0.5);
        var net = enemy.Buffs.FirstOrDefault(buff => buff.Type == BuffType.Net);
        TestAssert.True(net is not null, "the target is netted by Dark Binding");
        TestAssert.True(net!.ExpiresAt >= 1.0, $"the net lasts its duration ({net.ExpiresAt} s left at rank 1)");
        var start = enemy.Position;
        match.Move(1, new Position2(start.X - 400, start.Z));
        match.Runtime.Tick(0.5);
        TestAssert.Near(start.X, enemy.Position.X, 1.0, "the rooted target does not move while the net lasts");
    }

    [Check("buffs.duration.morgana-e-black-shield-lasts-its-duration")]
    public static void MorganaBlackShieldLastsItsDuration()
    {
        using var match = new Match("Morgana", Team.Order);
        var morgana = match.Hero(0);
        match.Cast(0, 2, morgana);
        match.Runtime.Tick(0.25);
        var shield = morgana.Buffs.FirstOrDefault(buff => buff.Type == BuffType.SpellImmunity);
        TestAssert.True(shield is not null, "Black Shield is on Morgana");
        TestAssert.True(shield!.ExpiresAt >= 1.0, $"Black Shield lasts its duration ({shield.ExpiresAt} s left at rank 1)");
    }

    [Check("buffs.duration.zilean-e-time-warp-lasts-its-duration")]
    public static void ZileanTimeWarpLastsItsDuration()
    {
        using var match = new Match("Zilean", Team.Order, ally: true);
        var ally = match.Hero(2);
        match.Cast(0, 2, ally);
        match.Runtime.Tick(0.25);
        var warp = ally.Buffs.FirstOrDefault(buff => buff.Type == BuffType.Haste);
        TestAssert.True(warp is not null, "Time Warp hastes the ally");
        TestAssert.True(warp!.ExpiresAt >= 1.0, $"Time Warp lasts its duration ({warp.ExpiresAt} s left at rank 1)");
    }

    private static void AssertHolderAddNamesAClientScript(Match match, uint holder, string binding, string buffName, string label)
    {
        var adds = match.Captures.SelectMany(capture => capture.Packets)
            .Where(packet => packet.Plaintext.Length >= 13 && packet.Plaintext.Span[0] == SpellPackets.BuffAddOpcode
                && BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == holder)
            .ToList();
        TestAssert.True(adds.Count >= 1, $"{label}: the holder receives a buff add");
        var hash = BinaryPrimitives.ReadUInt32LittleEndian(adds[0].Plaintext.Span[9..]);
        var sent = hash == SpellPackets.BuffNameHash(binding) ? binding
            : hash == SpellPackets.BuffNameHash(buffName) ? buffName
            : "(a name outside the shipped set)";
        TestAssert.True(GameData.Default.HasClientBuff(sent),
            $"{label}: the add names {sent}, which the client cannot build (no DATA/Spells or DATA/Items script of that name)");
    }
}
