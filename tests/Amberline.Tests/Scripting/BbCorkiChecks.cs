using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 8 (25 October 2026, batch 255): Corki plays from his shipped scripts in a production <c>MatchRuntime</c> (no hand-written module ever
/// existed). Expected numbers are the literals of the shipped scripts and spell inibins; the scenario <c>S-Corki</c> shows the same facts on a headless
/// match. Missile Barrage's charges come from the ammo buff of wave 8 (E14, <c>BuffOnUpdateAmmo</c>) and its first training (E29, two stacks); the
/// Valkyrie's trail and the Gatling Gun's sprays are the zone and the self buff the scripts add.
/// </summary>
public static class BbCorkiChecks
{
    private static readonly double[] BlastDamage = [70.0, 120.0, 170.0, 220.0, 270.0];

    private static int Stacks(Entity unit, string name) => unit.Buffs.Where(buff => buff.Name == name).Sum(buff => buff.Stacks);

    // The first training of Missile Barrage at level six, through the production rank-up (SpellSystem.RankUp): CharOnLevelUpSpell adds the
    // two stacks and the barrage counter, as a live training does.
    private static void Train(Match match)
    {
        match.Advance(1.0); // the charge timer (ChargeCooldown) is set on the hero's first ticks; a stack added before it has no duration
        match.Hero.Level = 6;
        for (var slot = 0; slot < 4; slot++) match.Hero.SpellRanks[slot] = 0;
        TestAssert.True(match.Runtime.Spells.RankUp(match.Hero, 3), "the first training of Missile Barrage is accepted at level six");
        match.Advance(0.1);
    }

    private static IEnumerable<Entity> Zones(Match match) =>
        match.Runtime.World.Entities.Values.Where(unit => Buff(unit, "DangerZone") is not null);

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-corki-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Corki");
        foreach (var name in new[] { "CharScriptCorki", "PhosphorusBomb", "CarpetBomb", "GGun", "MissileBarrage", "RapidReload", "DangerZone",
                     "DangerZoneTarget", "GatlingDebuff", "MissileBarrageMissile", "MissileBarrageMissile2" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Corki");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(25, closure.ScriptNames.Count(), "the closure is 25 scripts (the audit's count): his own, the zone and missile children, the system buffs");

        var hero = new Entity { ChampionName = "Corki" };
        TestAssert.Equal(["PhosphorusBomb", "CarpetBomb", "GGun", "MissileBarrage"], Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(),
            "spell names come from Corki.inibin (the Valkyrie is CarpetBomb)");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(), "Q is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Corki")!;
        TestAssert.True(info is { Playable: true, Role: "mid", DefaultIp: 3150, Id: 42, RetailBot: false }, "playable as a mid for 3150 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Corki"), "listed once");

        // The tables the figures rest on, a second time as literals.
        var blast = Blocks(Script("PhosphorusBomb"), "TargetExecute").First(block => block.Function == "BBApplyDamage");
        TestAssert.Equal(BlastDamage, Table(blast, "DamageByLevel"), "Phosphorus Bomb damage by rank");
        TestAssert.Equal([-1.0, -2.0, -3.0, -4.0, -5.0], ByLevel("GGSpray", "TargetExecute", "ArmorMod"), "each Gatling spray strips 1 to 5 armour per stack");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Phosphorus Bomb: an area with a four-second reveal
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-corki-phosphorus-bomb-hurts-the-area-only-and-reveals-for-four-seconds")]
    public static void PhosphorusBombHurtsTheAreaOnly()
    {
        using var match = new Match("Corki", foeChampion: "Orianna");
        match.Learn(5);
        match.Refill();
        var foe = match.Foe;
        var ally = match.Ally;
        var farFoe = match.FarFoe;
        var foeBefore = foe.Health;
        var allyBefore = ally.Health;
        var farBefore = farFoe.Health;
        TestAssert.True(match.Cast(0, at: foe.Position), "the bomb is cast at a point on the foe");
        match.Advance(0.6);
        // Learn(5) puts Q at rank five: the blast is 270 (DamageByLevel[4]) plus 0.5 of ability power, mitigated by the foe's spell block.
        var expected = Mitigated(BlastDamage[4] + 0.5 * match.Hero.AbilityPower, foe.SpellBlock);
        TestAssert.Near(expected, foeBefore - foe.Health, 1.5, "the foe in the area takes the rank-one blast (magic, the ratio of ability power)");
        TestAssert.Equal(allyBefore, ally.Health, "an ally in the area takes nothing");
        TestAssert.Equal(farBefore, farFoe.Health, "a unit outside the area takes nothing");
        TestAssert.True(Buff(match.Hero, "PhosphorusBomb") is not null, "the caster carries the bomb's buff for its four seconds");
        match.Advance(4.0);
        TestAssert.True(Buff(match.Hero, "PhosphorusBomb") is null, "the buff ends after four seconds");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Valkyrie: a dash that leaves a burning trail
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-corki-valkyrie-dashes-six-hundred-and-leaves-zones-for-2.5-seconds")]
    public static void ValkyrieDashLeavesZones()
    {
        using var match = new Match("Corki", foeChampion: "Orianna");
        match.Learn(5);
        match.Refill();
        var hero = match.Hero;
        var start = hero.Position;
        TestAssert.True(match.Cast(1, at: new Position2(start.X + 600.0, start.Z)), "the Valkyrie is cast 600 units along the x axis");
        match.Advance(0.3);
        TestAssert.True(Buff(hero, "CarpetBomb") is not null, "the dash buff runs while he moves");
        var peakZones = 0;
        for (var t = 0.0; t < 1.5; t += 0.1) { match.Advance(0.1); peakZones = Math.Max(peakZones, Zones(match).Count()); }
        var dx = hero.Position.X - start.X;
        var dz = hero.Position.Z - start.Z;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        TestAssert.Near(600.0, distance, 25.0, "the dash ends at the aimed point");
        TestAssert.True(peakZones >= 10, $"a zone drops along the path (peak {peakZones})");
        match.Advance(3.0);
        TestAssert.Equal(0, Zones(match).Count(), "every zone is gone after its 2.5 seconds");
    }

    // Batch 430: the trail follows the script's clock. SelfExecute sets TickAmount (15 at rank 1) and TickDuration = Duration / TickAmount into the
    // cast's InstanceVars; the self buff's BuffOnUpdateActions reads TickDuration back as its period. At the max range (800, the clamp of
    // SelfExecute) that is 15 zones, one impact create each on the wire, and 15 impact kills as each zone's own buff ends.
    [Check("scripting.bb-corki-valkyrie-max-range-drops-tickamount-zones")]
    public static void ValkyrieMaxRangeDropsTickAmountZones()
    {
        using var match = new Match("Corki", foeChampion: "Orianna");
        match.Learn(1);
        match.Refill();
        var hero = match.Hero;
        var wire = Observe(match, Team.Order, hero);
        var start = hero.Position;
        var zones = new HashSet<uint>();
        TestAssert.True(match.Cast(1, at: new Position2(start.X + 800.0, start.Z)), "the Valkyrie is cast 800 units along the x axis (rank 1)");
        for (var t = 0.0; t < 4.0; t += 0.1)
        {
            match.Advance(0.1);
            foreach (var zone in Zones(match)) zones.Add(zone.NetId);
        }
        var creates = wire.Packets.Count(packet => EffectNamed(packet.Packet, "corki_valkrie_impact_cas"));
        // Only the impacts' own kills count (a kill's effect netId, offset 5, matched to a create's effect netId, offset 25; the fire puffs have their own kills).
        var impacts = wire.Packets.Where(packet => EffectNamed(packet.Packet, "corki_valkrie_impact_cas"))
            .Select(packet => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.Packet.Plaintext.Span[25..])).ToHashSet();
        var kills = wire.Packets.Count(packet => packet.Packet.Plaintext.Span[0] == Amberline.Network.Protocol.FxPackets.KillOpcode &&
            impacts.Contains(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.Packet.Plaintext.Span[5..])));
        TestAssert.Equal(15, zones.Count, $"TickAmount 15 at rank 1: one zone per period of the script clock (saw {zones.Count})");
        TestAssert.Equal(15, creates, $"one impact create on the wire per zone (saw {creates})");
        TestAssert.Equal(15, kills, $"each zone's impact is killed with its buff (saw {kills} kills)");
        TestAssert.Equal(0, Zones(match).Count(), "every zone is gone after its 2.5 seconds");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Gatling Gun: a self buff whose sprays strip armour
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-corki-gatling-gun-is-a-timed-self-buff-of-sprays")]
    public static void GatlingGunIsATimedSelfBuff()
    {
        using var match = new Match("Corki", foeChampion: "Orianna");
        match.Learn(5);
        match.Refill();
        TestAssert.True(match.Cast(2, match.Hero), "the Gatling Gun is cast on himself");
        match.Advance(0.2);
        var buff = Buff(match.Hero, "GGun");
        TestAssert.True(buff is not null, "the cast adds the Gatling Gun buff (named by its script)");
        // Rank five (Learn(5)): DurationByLevel[4] is 5.5 seconds; the cast was 0.2 seconds ago.
        TestAssert.Near(5.5, buff!.ExpiresAt - match.Now + 0.2, 0.3, "the buff lasts DurationByLevel 5.5 seconds at rank five");
        match.Advance(5.5);
        TestAssert.True(Buff(match.Hero, "GGun") is null, "the buff ends with its duration");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Missile Barrage: ammo charges, the bigger missile every fourth cast
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-corki-missile-barrage-charges-and-every-fourth-missile-is-bigger")]
    public static void MissileBarrageChargesAndTheBigMissile()
    {
        using var match = new Match("Corki", foeChampion: "Orianna");
        Train(match);
        TestAssert.Equal(2, Stacks(match.Hero, "MissileBarrage"), "the first training gives two stacks: one charge (the HUD shows the stack count minus one)");
        TestAssert.True(Buff(match.Hero, "MBCheck2") is null, "no bigger missile is armed at the start");
        var damage = new List<double>();
        for (var cast = 1; cast <= 4; cast++)
        {
            for (var guard = 0; Stacks(match.Hero, "MissileBarrage") < 2 && guard < 400; guard++) match.Advance(0.5);
            match.Refill();
            TestAssert.True(match.Cast(3, at: match.Foe.Position), $"barrage {cast} is cast with a charge");
            var before = match.Foe.Health;
            match.Advance(1.5);
            damage.Add(before - match.Foe.Health);
            if (cast == 3) TestAssert.True(Buff(match.Hero, "MBCheck2") is not null, "the third barrage arms the bigger missile for the fourth");
        }
        // Casts one to three are the normal missile, cast four the bigger one: 1.5 times the same base, with the same resistance.
        TestAssert.Near(1.5, damage[3] / damage[0], 0.05, "the fourth missile is the bigger one (1.5 times the normal missile)");
        TestAssert.Near(damage[0], damage[1], 2.0, "the first and second missiles are the normal one");
        TestAssert.Near(damage[0], damage[2], 2.0, "the third missile is the normal one");
        TestAssert.True(Buff(match.Hero, "MBCheck2") is null, "the bigger missile is spent by the fourth cast");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Hextech Shrapnel Shells: ten percent true damage on minions and monsters
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-corki-shrapnel-shells-adds-ten-percent-true-damage-to-minions")]
    public static void ShrapnelShellsAddsTrueDamage()
    {
        var on = Blocks(Script("RapidReload"), "BuffOnHitUnit");
        var tenPercent = on.Where(block => block.Function == "BBMath").Select(block => block.Number("Src1Value")).Where(value => value.HasValue).ToArray();
        TestAssert.True(tenPercent.Any(value => Math.Abs(value!.Value - 0.1) < 1e-9), "the true damage is a tenth of the attack's damage");
        using var match = new Match("Corki", foeChampion: "Orianna");
        TestAssert.True(Buff(match.Hero, "RapidReload") is not null, "the passive is on from activation");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Death and respawn: the charges come back full
    // ------------------------------------------------------------------------------------------------------------

    // A hit of less than zero deals nothing: RapidReload's true damage is health minus one, which is -1 on a unit at zero health (seed 61 raised
    // ArgumentOutOfRange here: script.exception of RapidReload, BuffOnHitUnit).
    [Check("scripting.bb-corki-negative-true-damage-deals-nothing")]
    public static void NegativeTrueDamageDealsNothing()
    {
        using var match = new Match("Corki", foeChampion: "Orianna");
        var minion = Enemy(match, "CorkiNegativeHitMinion", match.Hero.Position);
        minion.Health = 500.0;
        match.Runtime.Combat.ApplyHit(match.Hero.NetId, minion.NetId, -1.0, DamageType.True);
        TestAssert.Equal(500.0, minion.Health, "a true hit of minus one leaves the health alone");
    }

    [Check("scripting.bb-corki-death-and-respawn-refill-the-barrage-to-eight-charges")]
    public static void RespawnRefillsTheCharges()
    {
        using var match = new Match("Corki", foeChampion: "Orianna");
        Train(match);
        TestAssert.Equal(2, Stacks(match.Hero, "MissileBarrage"), "two stacks before the death");
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        match.Runtime.Combat.ScheduleRespawn(match.Hero);
        for (var guard = 0; !match.Hero.Alive && guard < 4000; guard++) match.Advance(0.1);
        TestAssert.True(match.Hero.Alive, "the hero respawns");
        TestAssert.Equal(8, Stacks(match.Hero, "MissileBarrage"), "a respawn gives eight stacks (CharOnResurrect, E29)");
    }
}
