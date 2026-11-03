using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;
using Amberline.Tools.Diagnostics;

namespace Amberline.Tests;

/// <summary>DIAGNOSTICS: checks of the spell sweep (batch 360): the decoder layouts it reads, and a thin run of the sweep itself.</summary>
public static class SpellSweepChecks
{
    // Batch 360: each builder the sweep's rules read round-trips through WirePacketDecoder into the fields the decoder names, so a layout
    // that drifts from its writer fails here.
    [Check("tools.wire.batch-360-layouts")]
    public static void BuilderLayoutsDecode()
    {
        const uint unit = 0x40000001, other = 0x40000002;
        IReadOnlyDictionary<string, object?> Fields(PlaintextPacket packet) =>
            WirePacketDecoder.Decode(packet.Plaintext.Span, LeagueProtocolConstants.CombatChannel).Fields;

        var hero = Fields(CombatPackets.AiSetTarget(unit, other));
        TestAssert.Equal(unit, (uint)hero["unit_netid"]!, "AI_SetTarget: the unit");
        TestAssert.Equal(other, (uint)hero["target_netid"]!, "AI_SetTarget: the target");
        var heroTarget = Fields(CombatPackets.AiSetTargetHero(unit, other));
        TestAssert.Equal(other, (uint)heroTarget["target_netid"]!, "AI_SetTargetHero: the target");

        var bar = Fields(CombatPackets.ShowHealthBar(unit, show: false));
        TestAssert.Equal(unit, (uint)bar["netid"]!, "ShowHealthBar: the unit");
        TestAssert.Equal(false, (bool)bar["show"]!, "ShowHealthBar: hidden");
        TestAssert.Equal(true, (bool)Fields(CombatPackets.ShowHealthBar(unit, show: true))["show"]!, "ShowHealthBar: shown");

        var death = Fields(CombatPackets.BuildingDie(unit, other));
        TestAssert.Equal(other, (uint)death["killer_netid"]!, "BuildingDie: the killer");

        var level = Fields(SpellPackets.LevelUpSpell(unit, 2));
        TestAssert.Equal(2, (int)level["slot"]!, "LevelUpSpell: the slot");

        var stop = Fields(SpellPackets.StopAnimation(unit, blend: false));
        TestAssert.Equal(0x240u, (uint)stop["flags"]!, "StopAnimation: lock and no blending");
        TestAssert.True((bool)stop["lock"]!, "StopAnimation: the lock bit");

        var play = Fields(SpellPackets.PlayAnimation(unit, "Spell1", loop: true, lockAnimation: true, blend: false, duration: 1.5f));
        TestAssert.Equal("Spell1", (string)play["name"]!, "PlayAnimation: the NUL-terminated name at 13");
        TestAssert.Near(1.5, (double)play["duration"]!, 1e-6, "PlayAnimation: the duration at 9");
        TestAssert.True((bool)play["loop"]! && (bool)play["lock"]! && !(bool)play["blend"]!, "PlayAnimation: loop, lock and no blending");

        var voice = Fields(SpellPackets.ChangeCharacterVoice(unit, "Suffix"));
        TestAssert.Equal("Suffix", (string)voice["voice"]!, "ChangeCharacterVoice: the 64-byte override");
        TestAssert.False((bool)voice["reset"]!, "ChangeCharacterVoice: a suffix is not a reset");
        TestAssert.True((bool)Fields(SpellPackets.ChangeCharacterVoice(unit, null))["reset"]!, "ChangeCharacterVoice: no suffix resets");

        var renew = SpellPackets.RenewBuff(unit, 3, 4.0f, other, 1.0f);
        var renewFields = Fields(renew);
        TestAssert.Equal((byte)3, (byte)renewFields["slot"]!, "RenewBuff: the slot");
        TestAssert.Near(1.0, (double)renewFields["running_time"]!, 1e-6, "RenewBuff: the running time at 6");
        TestAssert.Near(4.0, (double)renewFields["duration"]!, 1e-6, "RenewBuff: the duration at 10");
        TestAssert.Equal(other, (uint)renewFields["caster_netid"]!, "RenewBuff: the caster at 14");
        TestAssert.Equal("BuffReplace", WirePacketDecoder.LogName(0x32, LeagueProtocolConstants.CombatChannel), "RenewBuff: catalog name");

        var color = Fields(SpellPackets.ColorRemapFx(fadeIn: true, fadeTime: 0.5f, teamId: 0, color: 0x11223344u, maxWeight: 1.0f));
        TestAssert.True((bool)color["fade_in"]!, "ColorRemapFX: the fade flag at 5");
        TestAssert.Equal(0x11223344u, (uint)color["color"]!, "ColorRemapFX: the colour at 14");
        TestAssert.Near(1.0, (double)color["max_weight"]!, 1e-6, "ColorRemapFX: the weight at 18");

        var short18 = Fields(CombatPackets.OnEventWorld(7, unit, other));
        TestAssert.Equal((byte)7, (byte)short18["event_id"]!, "OnEventWorld: the event");
        TestAssert.Equal(other, (uint)short18["other_netid"]!, "OnEventWorld: the other unit");
        TestAssert.Equal(null, short18["map_number"], "OnEventWorld: no map number in the 14-byte form");
        TestAssert.Equal(5, (int)Fields(CombatPackets.OnEventWorld(7, unit, other, mapNumber: 5))["map_number"]!, "OnEventWorld: the map number at 14");
    }

}
