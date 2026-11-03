using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine, wave 2 bundle (W27b, 19 October 2026): the natives and rules of the ten small engine items of the new-champion wave plan.
/// The probe scripts are hand-built block lists run on a production <c>MatchRuntime</c> whose Sivir module hosts them; every parameter name is one
/// the shipped scripts write. Written 19 October 2026 and compiled, not run (owner rule).
/// </summary>
public static class BbW27bChecks
{
    private static BbScript Probe(string name, params BbBlock[] blocks) =>
        new(name, "Spells/" + name + ".luaobj", [], [new BbEventTable("TargetExecute", blocks)], []);

    private static void Run(BbM3.Match match, BbScript script, Position3? end = null)
    {
        var at = new Position3(0, 0, 0);
        match.Module.Router!.RunTargetExecute(script, match.Hero, 0, 1, 0.0, at, end ?? at, match.Foe);
    }

    private static void NoFailures(BbM3.Match match) =>
        TestAssert.Equal(0, match.Module.ScriptFailures.Count,
            "no script failure: " + string.Join("; ", match.Module.ScriptFailures.Select(f => $"{f.Script}.{f.Hook} {f.Message}")));

    private static object? CharVar(BbM3.Match match, string name) => match.Module.CharVars(match.Hero)[name];

    [Check("protocol.w27b-wave2-packet-layouts")]
    public static void TheWaveTwoPacketsMatchTheClientsWriters()
    {
        // ColorRemapFX: flag at +5, fade time at +6, team at +10, colour at +14 (blue, green, red, alpha), max weight at +18; global.
        var remap = SpellPackets.ColorRemapFx(true, 1.0f, 100, ParColorOverride.Pack(r: 0, g: 0, b: 75, a: 0), 0.3f);
        var bytes = remap.Plaintext.Span;
        TestAssert.Equal(22, bytes.Length, "ColorRemapFX is 22 bytes");
        TestAssert.Equal((byte)0xE4, bytes[0]);
        TestAssert.Equal(1, bytes[5], "isFadingIn");
        TestAssert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(bytes[6..]), "fade time");
        TestAssert.Equal(100, BinaryPrimitives.ReadInt32LittleEndian(bytes[10..]), "team id");
        TestAssert.Equal((byte)75, bytes[14], "blue is the first colour byte");
        TestAssert.Equal(0.3f, BinaryPrimitives.ReadSingleLittleEndian(bytes[18..]), "max weight");
        TestAssert.Equal(0, remap.EntityReferences.Count, "the tint names no unit");

        // ChangeCharacterVoice: reset byte at +5, the 64-byte suffix at +6.
        var voice = SpellPackets.ChangeCharacterVoice(0x40000001, "Ult").Plaintext.Span;
        TestAssert.Equal(70, voice.Length, "ChangeCharacterVoice is 70 bytes");
        TestAssert.Equal((byte)0x9E, voice[0]);
        TestAssert.Equal(0, voice[5], "reset is false");
        TestAssert.Equal((byte)'U', voice[6], "the suffix starts at +6");
        TestAssert.Equal(0, voice[9], "and is NUL-terminated");
        TestAssert.Equal(1, SpellPackets.ChangeCharacterVoice(0x40000001, null).Plaintext.Span[5], "a null suffix is the reset");

        // ChangePARColorOverride: unit at +5, enabling byte at +9, bar colour at +10, fade colour at +14.
        var par = SpellPackets.ChangeParColorOverride(0x40000001, new ParColorOverride(
            ParColorOverride.Pack(255, 0, 0, 255), ParColorOverride.Pack(175, 0, 0, 255))).Plaintext.Span;
        TestAssert.Equal(18, par.Length, "ChangePARColorOverride is 18 bytes");
        TestAssert.Equal((byte)0x99, par[0]);
        TestAssert.Equal(0x40000001u, BinaryPrimitives.ReadUInt32LittleEndian(par[5..]), "the unit field");
        TestAssert.Equal(1, par[9], "enabling");
        TestAssert.Equal((byte)255, par[12], "red is the third colour byte");
        TestAssert.Equal((byte)255, par[13], "alpha is the fourth");
        TestAssert.Equal((byte)175, par[16], "the fade colour follows");
        TestAssert.Equal(0, SpellPackets.ChangeParColorOverride(0x40000001, null).Plaintext.Span[9], "the clear disables");

        // LevelUpSpell: the slot as an int at +5.
        var level = SpellPackets.LevelUpSpell(0x40000001, 3).Plaintext.Span;
        TestAssert.Equal(9, level.Length, "LevelUpSpell is 9 bytes");
        TestAssert.Equal((byte)0x5E, level[0]);
        TestAssert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(level[5..]), "slot");
        foreach (var opcode in new byte[] { 0xE4, 0x9E, 0x99, 0x5E })
            TestAssert.True(WirePacketDecoder.Decode(opcode == 0xE4 ? remap.Plaintext.Span : opcode == 0x9E ? voice : opcode == 0x99 ? par : level, 3).Name.Length > 0,
                $"the diagnostics decoder names 0x{opcode:X2}");
    }

    [Check("data.w27b-inibin-string-reads-as-atoi")]
    public static void AnInibinStringReadsItsLeadingInteger()
    {
        // Twisted Fate's Destiny stores SpellData*TargettingType as "4CastRangeUseMapScaling=1": the client's int getter reads 4.
        TestAssert.Equal(4, InibinValue.AtoiPrefix("4CastRangeUseMapScaling=1"), "leading digits");
        TestAssert.Equal(-12, InibinValue.AtoiPrefix("  -12abc"), "white space and a sign");
        TestAssert.Equal(0, InibinValue.AtoiPrefix("abc"), "no digits");
    }

    [Check("scripting.bb-w27b-skip-next-auto-attack-sets-the-flag")]
    public static void SkipNextAutoAttackMarksTheTarget()
    {
        using var match = new BbM3.Match("Sivir");
        TestAssert.True(!match.Hero.SkipNextAutoAttack, "fresh unit");
        Run(match, Probe("W27bSkip", BbTest.Block("BBSkipNextAutoAttack", ["TargetVar", "Owner"])));
        NoFailures(match);
        TestAssert.True(match.Hero.SkipNextAutoAttack, "the six shipped callers set it in BuffOnPreAttack; QueueBasicAttack reads and clears it");
    }

    [Check("scripting.bb-w27b-multiplicative-cost-inc-round-trips")]
    public static void TheMultiplicativeCostIncrementIsASetAndReadsBack()
    {
        using var match = new BbM3.Match("Sivir");
        var slot = new object[] { "SpellSlotOwnerVar", "Owner", "SlotType", 0.0, "SpellSlot", 1 };
        Run(match, Probe("W27bMult",
            BbTest.Block("BBSetPARMultiplicativeCostInc", [.. slot, "Cost", -0.1]),
            BbTest.Block("BBGetPARMultiplicativeCostInc", [.. slot, "IncCostVar", "Read", "IncCostVarTable", "CharVars"]),
            BbTest.Block("BBSetPARMultiplicativeCostInc", [.. slot, "Cost", -0.5])));
        NoFailures(match);
        TestAssert.Near(-0.1, (double)CharVar(match, "Read")!, 1e-6, "the getter reads what the first set stored");
        TestAssert.Near(-0.5, match.Hero.SpellParCostMultipliers[1], 1e-6, "a second set replaces, it does not add");
        Run(match, Probe("W27bMultClear", BbTest.Block("BBSetPARMultiplicativeCostInc", [.. slot, "Cost", 0.0])));
        TestAssert.True(!match.Hero.SpellParCostMultipliers.ContainsKey(1), "0 puts the slot back to the default");
    }

    [Check("scripting.bb-w27b-inc-spell-level-raises-the-rank-without-a-skill-point")]
    public static void IncSpellLevelRaisesTheRankToSix()
    {
        using var match = new BbM3.Match("Sivir");
        match.Hero.SpellRanks[0] = 0;
        var points = match.Hero.SkillPoints;
        var block = BbTest.Block("BBIncSpellLevel", ["SpellSlotOwnerVar", "Owner", "SlotType", 0.0, "SpellSlot", 0]);
        Run(match, Probe("W27bLevel", block));
        NoFailures(match);
        TestAssert.Equal(1, match.Hero.SpellRanks[0], "one level");
        TestAssert.Equal(points, match.Hero.SkillPoints, "no skill point is spent");
        match.Hero.SpellRanks[0] = 6;
        Run(match, Probe("W27bLevelCap", block));
        TestAssert.Equal(6, match.Hero.SpellRanks[0], "the spell data's level-up never goes above 6");
    }

    [Check("scripting.bb-w27b-voice-and-bar-colour-overrides-are-unit-state")]
    public static void TheVoiceAndTheBarColourAreKeptOnTheUnit()
    {
        using var match = new BbM3.Match("Sivir");
        Run(match, Probe("W27bVoice", BbTest.Block("BBSetVoiceOverride", ["OverrideSuffix", "Ult", "TargetVar", "Owner"])));
        NoFailures(match);
        TestAssert.Equal("Ult", match.Hero.VoiceOverride, "the suffix is the unit's state, replayed to a late observer");
        Run(match, Probe("W27bVoiceReset", BbTest.Block("BBResetVoiceOverride", ["TargetVar", "Owner"])));
        TestAssert.True(match.Hero.VoiceOverride is null, "the reset clears it");

        // Renekton's Ruthless Predator sets a colour and clears it in one pass: only a call outside a stats hook changes the state at once.
        Run(match, Probe("W27bPar", BbTest.Block("BBSetPARColorOverride", ["SpellSlotOwnerVar", "Owner",
            "ColorR", 255, "ColorG", 0, "ColorB", 0, "ColorA", 255, "FadeR", 175, "FadeG", 0, "FadeB", 0, "FadeA", 255])));
        NoFailures(match);
        TestAssert.Equal(ParColorOverride.Pack(255, 0, 0, 255), match.Hero.ParColorOverride!.Value.Bar, "the bar colour");
        TestAssert.Equal(ParColorOverride.Pack(175, 0, 0, 255), match.Hero.ParColorOverride!.Value.Fade, "the fade colour");
        Run(match, Probe("W27bParClear", BbTest.Block("BBClearPARColorOverride", ["SpellSlotOwnerVar", "Owner"])));
        TestAssert.True(match.Hero.ParColorOverride is null, "the clear drops the override");
    }

    [Check("scripting.bb-w27b-drag-end-position-and-attribute-flag")]
    public static void TheDragEndPositionAndTheAttributeFlagAreRead()
    {
        using var match = new BbM3.Match("Sivir");
        Run(match, Probe("W27bDrag",
                BbTest.Block("BBGetCastSpellDragEndPos", ["DestVar", "End", "DestVarTable", "CharVars"]),
                BbTest.Block("BBTestUnitAttributeFlag", ["TargetVar", "Owner", "AttributeFlag", 0, "ResultVar", "Glasses", "ResultVarTable", "CharVars"])),
            end: new Position3(1200, 0, -300));
        NoFailures(match);
        var end = (BbTable)CharVar(match, "End")!;
        TestAssert.Near(1200.0, (double)end["x"]!, 1e-6, "the end of the drag, x");
        TestAssert.Near(-300.0, (double)end["z"]!, 1e-6, "and z");
        var expected = (GameData.Default.Champion("Sivir").ExtraAttributeFlags(match.Hero.SkinId) & 1) != 0;
        TestAssert.Equal(expected, (bool)CharVar(match, "Glasses")!, "HAS_SUNGLASSES is bit 0 of the skin's AttributeFlags");
    }
}
