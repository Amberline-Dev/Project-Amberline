using System.Buffers.Binary;
using System.Text.Json;
using Amberline.Core;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 421 (HUD findings 113, 116, 118, 125, 132): driven wire checks of what the observer's client gets, read from the shipped scripts. Each
/// check drives the case (Trundle W, Teemo E and R, Veigar Q, Wukong's passive) and reads the packets of an observer on the caster's team. None
/// changes production behaviour: the wire already matches the shipped scripts, so these guard it (the record names the classification).
/// </summary>
public static class BbB421HudChecks
{
    private static JsonElement Json(PlaintextPacket packet) =>
        JsonSerializer.SerializeToElement(WirePacketDecoder.Decode(packet.Plaintext.Span, packet.Channel).Fields);

    private static byte Op(PlaintextPacket packet) => packet.Plaintext.Span[0];

    private static uint Hash(string name) => SpellPackets.BuffNameHash(name);

    // A buff add (0xBF) on one unit, by its name hash.
    private static List<(double At, JsonElement Add)> Adds(WireCapture wire, Entity unit, string name) =>
        wire.Packets.Where(entry => Op(entry.Packet) == 0xBF).Select(entry => (entry.At, Json(entry.Packet)))
            .Where(entry => entry.Item2.GetProperty("target_netid").GetUInt32() == unit.NetId &&
                entry.Item2.GetProperty("buff_name_hash").GetUInt32() == Hash(name)).ToList();

    // The effect ids a 0x8C create names with one effect name, with the time the observer got the create.
    private static List<(double At, uint Id)> Created(WireCapture wire, string effect)
    {
        var created = new List<(double At, uint Id)>();
        foreach (var entry in wire.Packets)
        {
            if (Op(entry.Packet) != 0x8C) continue;
            var json = Json(entry.Packet);
            if (!json.TryGetProperty("groups", out var groups)) continue;
            foreach (var group in groups.EnumerateArray())
            {
                if (group.GetProperty("effect_name_hash").GetUInt32() != Hash(effect)) continue;
                foreach (var item in group.GetProperty("effects").EnumerateArray())
                    created.Add((entry.At, item.GetProperty("net_assigned_id").GetUInt32()));
            }
        }
        return created;
    }

    // The effect ids a 0x3D kill names (the net id at offset 5), with the time the observer got it.
    private static List<(double At, uint Id)> Killed(WireCapture wire) =>
        wire.Packets.Where(entry => Op(entry.Packet) == 0x3D)
            .Select(entry => (entry.At, BinaryPrimitives.ReadUInt32LittleEndian(entry.Packet.Plaintext.Span[5..]))).ToList();

    // Batch 421, finding 118 (Trundle W circle). The sweep reported a kill 0.13 s after the create, but that kill belongs to Trundle's Q effect
    // (its 0x3D lands in the W window). The circle itself lives exactly the placemarker buff's eight seconds (TrundleDesecrate, Duration 8.0,
    // BBSpellEffectRemove on its deactivation), so the observer sees one create and one kill eight seconds later.
    [Check("scripting.bb-trundle-desecrate-circle-lives-eight-seconds-on-the-wire")]
    public static void TrundleDesecrateCircleLivesEightSeconds()
    {
        using var match = new Match("Trundle");
        match.Learn(1);
        match.Refill();
        match.Advance(0.3);
        var wire = Observe(match, Team.Order, match.Hero);
        TestAssert.True(match.Cast(1, at: new Position2(match.Hero.Position.X + 150.0, match.Hero.Position.Z)), "W is cast 150 units away");
        match.Advance(9.0);
        var created = Created(wire, "trundledesecrate_green");
        TestAssert.Equal(1, created.Count, "one create of the circle reaches the observer (sent unbound at the placemarker)");
        var kills = Killed(wire).Where(kill => kill.Id == created[0].Id).ToList();
        TestAssert.Equal(1, kills.Count, "the circle is ended once, by a kill of its own id");
        TestAssert.Near(8.0, kills[0].At - created[0].At, 0.1, "the circle lives the placemarker buff's eight seconds");
    }

    // Batch 421, finding 113 (Teemo E: poison on the enemy). The E slot is sealed by the character script (BBSealSpellSlot), so the poison is the
    // aura's auto attack: the first hit adds ToxicShotParticle to the foe (BUFF type 22, visible, the shipped 5.1 s, icon Teemo_PoisonedDart and
    // Global_poison.troy on its head bone, both client-side from the add).
    [Check("scripting.bb-teemo-toxic-shot-poison-reaches-the-enemy-as-type-22")]
    public static void ToxicShotPoisonReachesTheEnemy()
    {
        using var match = new Match("Teemo");
        match.Learn(1);
        match.Refill();
        match.Advance(1.0);
        var wire = Observe(match, Team.Order, match.Hero);
        match.Advance(0.2);
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "Teemo's attack is queued");
        for (var step = 0; step < 60 && Buff(match.Foe, "ToxicShotParticle") is null; step++) match.Advance(0.05);
        var adds = Adds(wire, match.Foe, "ToxicShotParticle");
        TestAssert.Equal(1, adds.Count, "one poison add reaches the observer for the foe");
        TestAssert.Equal(22, adds[0].Add.GetProperty("buff_type").GetInt32(), "the poison is type 22 on the wire");
        TestAssert.Equal(0, adds[0].Add.GetProperty("is_hidden").GetInt32(), "the poison is visible");
        TestAssert.Near(5.1, adds[0].Add.GetProperty("duration").GetDouble(), 0.1, "the poison lasts the shipped 5.1 seconds");
    }

    // Batch 421, finding 132 (Wukong's passive HUD). MonkeyKingPassive is added from the character script as BUFF_Aura, visible, 25 s (permanent).
    // A late observer gets the add with the rest of the state, so the passive is on the wire from the join. The icon is the shipped
    // Cassiopeia_PetrifyingGaze.dds named by MonkeyKingPassive.inibin; the client draws it, so this is a native read, not a wire gap.
    [Check("scripting.bb-monkeyking-stone-skin-passive-reaches-the-observer-as-aura")]
    public static void StoneSkinPassiveReachesTheObserver()
    {
        using var match = new Match("MonkeyKing");
        match.Learn(1);
        match.Refill();
        var wire = Observe(match, Team.Order, match.Hero);
        match.Advance(0.3);
        TestAssert.True(Buff(match.Hero, "MonkeyKingPassive") is not null, "the passive is on the champion");
        var adds = Adds(wire, match.Hero, "MonkeyKingPassive");
        TestAssert.Equal(1, adds.Count, "the passive's add reaches the observer");
        TestAssert.Equal(1, adds[0].Add.GetProperty("buff_type").GetInt32(), "the passive is BUFF_Aura on the wire (the script's BuffType)");
        TestAssert.Equal(0, adds[0].Add.GetProperty("is_hidden").GetInt32(), "the passive is visible");
        TestAssert.Equal(1, adds[0].Add.GetProperty("count").GetInt32(), "one stack");
    }

    // Batch 421, finding 125 (Veigar Q's ability power). A last-hit kill with Baleful Strike on a hero adds the rank plus one ability power
    // (CharOnKillUnit, then the buff's point at its end). The stat block the observer gets for Veigar carries the new value in mFlatMagicDamageMod:
    // 1 at the kill, 2 when the buff ends. The HUD number is therefore on the wire; the shipped script declares no buff icon for the gain.
    [Check("scripting.bb-veigar-baleful-strike-kill-raises-ability-power-on-the-wire")]
    public static void BalefulStrikeKillRaisesAbilityPowerOnTheWire()
    {
        using var match = new Match("Veigar");
        match.Learn(1);
        match.Refill();
        match.Advance(0.3);
        var wire = Observe(match, Team.Order, match.Hero);
        match.Foe.Health = 40.0;
        TestAssert.True(match.Cast(0, match.Foe), "Baleful Strike is cast on the foe at 40 health");
        match.Advance(2.0);
        var values = new List<double>();
        foreach (var entry in wire.Packets)
        {
            if (Op(entry.Packet) != 0xCC) continue;
            var json = Json(entry.Packet);
            if (json.GetProperty("unit_netid").GetUInt32() != match.Hero.NetId) continue;
            if (json.GetProperty("fields").TryGetProperty("mFlatMagicDamageMod", out var value)) values.Add(value.GetDouble());
        }
        TestAssert.True(values.Contains(1.0), "the kill's point reaches the observer (mFlatMagicDamageMod 1)");
        TestAssert.Equal(2.0, values[^1], "the buff's point follows (mFlatMagicDamageMod 2, the last value the observer holds)");
        TestAssert.Equal(2.0, match.Hero.AbilityPower, "the server holds the same ability power");
    }

    // Batch 421, finding 116 (Teemo R stacks). The mushroom charges (TeemoMushrooms, BUFF_STACKS_AND_RENEWS) reach the observer as one add that
    // grows by 0x1F: training the trap adds one charge; a placement spends it (the buff's 0x7F removal at none left); the recharge counter
    // (MushroomCooldown, 35 s at rank one, times the cooldown modifier) returns one charge at a time. The count the observer holds is the
    // server's. Open: the retail number of starting charges at rank one (see the record).
    [Check("scripting.bb-teemo-noxious-trap-charges-reach-the-observer-by-the-recharge")]
    public static void NoxiousTrapChargesReachTheObserver()
    {
        using var match = new Match("Teemo");
        var experience = ProgressionRules.ExperienceThreshold(6) - match.Hero.Experience;
        if (experience > 0.0) match.Runtime.Combat.AwardHeroProgress(match.Hero, experience: experience);
        for (var slot = 0; slot < 4; slot++)
        {
            match.Hero.SpellRanks[slot] = 0;
            TestAssert.True(match.Runtime.Spells.RankUp(match.Hero, slot), $"Teemo's slot {slot} is trained at level six");
        }
        match.Learn(1);
        match.Refill();
        match.Foe.Position = new Position2(7900, 7000);
        var hero = match.Hero;
        var wire = Observe(match, Team.Order, hero);
        match.Advance(0.5);
        TestAssert.Equal(1, Buff(hero, "TeemoMushrooms")?.Stacks ?? 0, "training the trap leaves one charge");
        var training = Adds(wire, hero, "TeemoMushrooms");
        TestAssert.True(training.Count >= 1 && training[^1].Add.GetProperty("count").GetInt32() == 1, "the observer holds one charge");
        hero.SpellCooldowns[3] = 0.0;
        hero.Mana = hero.MaxMana;
        match.Advance(0.1);
        var start = match.Now;
        TestAssert.True(match.Cast(3, at: new Position2(7150, 7000)), "the trap is placed");
        match.Advance(0.5);
        TestAssert.True(Buff(hero, "TeemoMushrooms") is null, "the placement spends the last charge and the buff ends");
        var removed = wire.Packets.Where(entry => Op(entry.Packet) == 0x7F && entry.At >= start &&
            Json(entry.Packet).GetProperty("target_netid").GetUInt32() == hero.NetId &&
            Json(entry.Packet).GetProperty("buff_name_hash").GetUInt32() == Hash("TeemoMushrooms")).ToList();
        TestAssert.Equal(1, removed.Count, "the observer sees the charge buff removed");
        // The recharge returns one charge after the counter's 35 seconds (the cooldown modifier is zero in this arena).
        while (Buff(hero, "TeemoMushrooms") is null && match.Now - start < 80.0) match.Advance(0.1);
        TestAssert.Near(35.0 * (1.0 + hero.CooldownMod), match.Now - start, 1.0, "the first recharge takes MushroomCooldown (35 s at rank one)");
        var back = Adds(wire, hero, "TeemoMushrooms").Where(add => add.At >= start).ToList();
        TestAssert.True(back.Count >= 1 && back[^1].Add.GetProperty("count").GetInt32() == 1, "the observer gets the charge back at one");
        while (Buff(hero, "TeemoMushrooms")?.Stacks < 2 && match.Now - start < 120.0) match.Advance(0.1);
        var grown = wire.Packets.Where(entry => Op(entry.Packet) == 0x1F && entry.At >= start &&
            Json(entry.Packet).GetProperty("target_netid").GetUInt32() == hero.NetId &&
            Json(entry.Packet).GetProperty("count").GetInt32() == 2).ToList();
        TestAssert.True(grown.Count >= 1, "the second charge reaches the observer as a count of two (0x1F)");
        TestAssert.Equal(2, Buff(hero, "TeemoMushrooms")?.Stacks ?? 0, "the server holds two charges");
    }
}
