using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 308 (25 October 2026): Veigar plays from his shipped scripts in a production <c>MatchRuntime</c> (no hand-written module). Expected numbers are the
/// literals of the shipped scripts (CharScriptVeigar, VeigarBalefulStrike, VeigarDarkMatter, VeigarEventHorizon with its marker and prevent buffs,
/// VeigarPrimordialBurst, VeigarEquilibrium, read from the bytecode listings) and the inibins; the scenario <c>S-veigar</c> shows the same facts on a headless match.
/// Veigar and the foes stand at level six with 30 magic resist, so the level-dependent resistance of a higher-level foe does not move the figures.
/// Training goes through the rank-up path, so the character script's level-up hooks run the way the client's own casts do.
/// </summary>
public static class BbVeigarChecks
{
    private static readonly double[] BalefulDamage = [80.0, 125.0, 170.0, 215.0, 260.0];
    private static readonly double[] DarkMatterDamage = [120.0, 170.0, 220.0, 270.0, 320.0];
    private static readonly double[] RingStun = [1.5, 1.75, 2.0, 2.25, 2.5];

    /// <summary>A match with Veigar trained to level six, every slot at <paramref name="rank"/> and the foes 250 away with 30 magic resist.</summary>
    private static Match Arena(int rank = 1, string foe = "Ezreal")
    {
        var match = new Match("Veigar", foeChampion: foe);
        var experience = ProgressionRules.ExperienceThreshold(6) - match.Hero.Experience;
        if (experience > 0.0) match.Runtime.Combat.AwardHeroProgress(match.Hero, experience: experience);
        TestAssert.Equal(6, match.Hero.Level, "Veigar reaches level six (the experience of the seat helper)");
        for (var slot = 0; slot < 4; slot++)
        {
            match.Hero.SpellRanks[slot] = 0;
            TestAssert.True(match.Runtime.Spells.RankUp(match.Hero, slot), $"Veigar's slot {slot} is trained at level six");
        }
        match.Learn(rank);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7250, 7000);
        match.Foe.SpellBlock = 30.0;
        match.Ally.SpellBlock = 30.0;
        match.FarFoe.SpellBlock = 30.0;
        match.Refill();
        match.Advance(0.5);
        return match;
    }

    /// <summary>Two stat refreshes: an update hook of a buff adds its stat during the previous refresh (the Teemo and Zilean checks read the same way).</summary>
    private static void Refresh(Match match, Entity unit)
    {
        match.Runtime.HeroStats.Refresh(unit, push: false);
        match.Runtime.HeroStats.Refresh(unit, push: false);
    }

    /// <summary>Advances the match for <paramref name="seconds"/> and returns the largest health loss of one tick: an instant hit lands in one tick.</summary>
    private static double LargestTickLoss(Match match, Entity unit, double seconds)
    {
        var largest = 0.0;
        for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += Step)
        {
            var before = unit.Health;
            match.Advance(Step);
            largest = Math.Max(largest, before - unit.Health);
        }
        return largest;
    }

    /// <summary>Gives a unit a Needlessly Large Rod (80 ability power), the way the other ability power checks do.</summary>
    private static void GiveRod(Match match, Entity unit)
    {
        unit.Inventory = [new InventorySlot(1058), null, null, null, null, null];
        Refresh(match, unit);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-veigar-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Veigar");
        foreach (var name in new[] { "CharScriptVeigar", "VeigarBalefulStrike", "VeigarDarkMatter", "VeigarEventHorizon", "VeigarEventHorizonMarker",
                     "VeigarEventHorizonPrevent", "VeigarPrimordialBurst", "VeigarEquilibrium" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Veigar");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(16, closure.ScriptNames.Count(), "the closure is 16 scripts (audit bb-coverage: 253 blocks, 41 functions, nothing dangling)");

        var hero = new Entity { ChampionName = "Veigar" };
        TestAssert.Equal(["VeigarBalefulStrike", "VeigarDarkMatter", "VeigarEventHorizon", "VeigarPrimordialBurst"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Veigar.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "the ultimate is learnt at 6, 11, 16");

        var info = ChampionCatalog.ByName("Veigar")!;
        TestAssert.True(info is { Playable: true, Role: "mid", DefaultIp: 1350, Id: 45, RetailBot: false },
            "playable as a mid laner for 1350 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Veigar"), "listed once");

        // The tables the figures rest on, a second time as literals.
        var bolt = Blocks(Script("VeigarBalefulStrike"), "TargetExecute").First(item => item.Function == "BBApplyDamage");
        TestAssert.Equal(BalefulDamage, Numbers(bolt.Param("DamageByLevel")!), "Baleful Strike damage by rank (BBApplyDamage DamageByLevel)");
        var kill = Blocks(CharScript("Veigar"), "CharOnKillUnit").First(item => item.Text("DestVar") == "ChampionAPGain");
        TestAssert.Equal([1.0, 2.0, 3.0, 4.0, 5.0], Numbers(kill.Param("SrcValueByLevel")!), "a hero kill adds the Baleful Strike rank in ability power (CharOnKillUnit)");
        var area = Blocks(Script("VeigarDarkMatter"), "SelfExecute").First(item => item.Text("DestVar") == "DamageAmount");
        TestAssert.Equal(DarkMatterDamage, Numbers(area.Param("SrcValueByLevel")!), "Dark Matter damage by rank (SelfExecute, the buff's DamageAmount)");
        var ring = Blocks(Script("VeigarEventHorizon"), "SelfExecute").First(item => item.Text("DestVar") == "StunDuration");
        TestAssert.Equal(RingStun, Numbers(ring.Param("SrcValueByLevel")!), "Event Horizon's stun by rank (SelfExecute, StunDuration)");
        var burst = Blocks(Script("VeigarPrimordialBurst"), "TargetExecute").First(item => item.Text("DestVar") == "TotalDamage");
        TestAssert.Equal([250.0, 375.0, 500.0], Numbers(burst.Param("SrcValueByLevel")!), "Primordial Burst's base damage by rank (TargetExecute)");
        TestAssert.True(Blocks(Script("VeigarEquilibrium"), "BuffOnUpdateStats").Any(item => item.Function == "BBMath" &&
                item.Param("Src1Value") is BbValue.Number { Value: 0.75 }), "Equilibrium scales the missing fraction by 0.75 (BuffOnUpdateStats)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Equilibrium
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-veigar-equilibrium-regenerates-by-three-quarters-of-the-missing-mana")]
    public static void EquilibriumRegeneratesByTheMissingMana()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        TestAssert.True(Buff(hero, "VeigarEquilibrium") is not null, "Equilibrium is on from activation (CharOnActivate, a BUFF_Aura)");
        var regen = new List<double>();
        foreach (var fraction in new[] { 1.0, 0.5, 0.0 })
        {
            hero.Mana = hero.MaxMana * fraction;
            // The passive's percent is sampled by the stats sample of the ticks (BuffOnUpdateStats), not by a refresh: a second of ticks first.
            match.Advance(1.0);
            Refresh(match, hero);
            regen.Add(hero.ManaRegen);
        }
        TestAssert.True(regen[0] > 0.0, "a positive regeneration at full mana");
        // The tooltip: "increased by 0.75% for each 1% of mana missing" (BuffOnUpdateStats: BBIncPercentPARRegenMod, 0.75 x the missing fraction).
        // Full mana is the base the other two are read against (no missing mana, no bonus).
        // The sample reads the mana of the second of ticks (the regeneration itself refills it a little), so the check allows a per cent.
        TestAssert.Near(1.375, regen[1] / regen[0], 0.01, "half mana: 1 + 0.75 x 0.5");
        TestAssert.Near(1.75, regen[2] / regen[0], 0.01, "no mana: 1 + 0.75");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Baleful Strike
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-veigar-baleful-strike-damage-follows-the-script")]
    public static void BalefulStrikeDamage()
    {
        foreach (var rank in new[] { 1, 5 })
        {
            using var match = Arena(rank);
            var foe = match.Foe;
            var hp = foe.Health;
            TestAssert.True(match.Cast(0, foe), $"rank {rank}: the bolt is cast at the enemy");
            match.Advance(1.0);
            // The damage is read against the victim's regeneration over the second (Log.Hits does not carry spell damage).
            var dealt = hp - foe.Health + foe.HealthRegen;
            TestAssert.Near(Mitigated(BalefulDamage[rank - 1], 30.0), dealt, 1.5,
                $"rank {rank}: {BalefulDamage[rank - 1]} magic against 30 magic resist (no ability power at level six)");
        }
    }

    [Check("scripting.bb-veigar-baleful-strike-kill-adds-ability-power-permanently")]
    public static void BalefulStrikeKillAddsAbilityPower()
    {
        // (rank, a hero victim): a minion kill adds the Baleful Strike point at the buff's end; a hero kill adds the rank as well (CharOnKillUnit).
        foreach (var (rank, hero) in new[] { (1, false), (1, true), (3, true) })
        {
            using var match = Arena(rank);
            var victim = hero ? match.Foe : BbM3.Enemy(match, "Minion", new Position2(7300, 7000));
            victim.Health = 40.0;
            var before = match.Hero.AbilityPower;
            TestAssert.True(match.Cast(0, victim), $"rank {rank}: Baleful Strike is cast at the {(hero ? "hero" : "minion")}");
            match.Advance(2.0);
            Refresh(match, match.Hero);
            var gained = match.Hero.AbilityPower - before;
            var expected = (hero ? rank : 0) + 1.0;
            TestAssert.Near(expected, gained, 1e-6,
                $"rank {rank}, {(hero ? "a hero" : "a minion")}: ability power grows by {expected} (the buff's point, plus the rank for a hero)");
            // The buff's point is kept: the ability power stays after the buff has gone.
            match.Advance(5.0);
            Refresh(match, match.Hero);
            TestAssert.Near(expected, match.Hero.AbilityPower - before, 1e-6, "the gain is permanent");
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Dark Matter
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-veigar-dark-matter-lands-after-its-delay-on-its-area")]
    public static void DarkMatterLandsAfterItsDelayOnItsArea()
    {
        foreach (var rank in new[] { 1, 5 })
        {
            using var match = Arena(rank);
            var foe = match.Foe;
            TestAssert.True(match.Cast(1, at: foe.Position), $"rank {rank}: Dark Matter is cast at the enemy's feet");
            // The area lands 1.2 seconds after the cast's own quarter second: nothing in the first second, the whole hit in one tick after it.
            TestAssert.True(LargestTickLoss(match, foe, 1.0) < 0.5, $"rank {rank}: nothing lands in the first second");
            var dealt = LargestTickLoss(match, foe, 1.0);
            TestAssert.Near(Mitigated(DarkMatterDamage[rank - 1], 30.0), dealt, 0.5,
                $"rank {rank}: {DarkMatterDamage[rank - 1]} magic against 30 magic resist when the area lands");
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Event Horizon
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-veigar-event-horizon-stuns-the-ring-edge-and-not-its-centre")]
    public static void EventHorizonStunsAtItsEdge()
    {
        using var match = Arena(1);
        var edge = match.Foe;
        var centre = match.FarFoe;
        var point = new Position2(7500, 7000);
        // The ring's middle (350 from the centre) takes the foe; the enemy on the centre is inside the ring's inner gap and is not stunned.
        edge.Position = new Position2(7850, 7000);
        centre.Position = point;
        TestAssert.True(match.Cast(2, at: point), "Event Horizon is cast on the point");
        match.Advance(0.3);
        TestAssert.True(Buff(edge, "Stun") is not null, "the foe on the ring's edge is stunned on the cast");
        TestAssert.True(Buff(centre, "Stun") is null, "the enemy on the centre is not stunned");
        match.Advance(1.3);
        TestAssert.True(Buff(edge, "Stun") is not null, "the stun lasts StunDuration (1.5 seconds at rank one): still on at 1.6 seconds");
        match.Advance(0.6);
        TestAssert.True(Buff(edge, "Stun") is null, "the stun is over by 2.2 seconds");
    }

    [Check("scripting.bb-veigar-event-horizon-prevent-window-blocks-a-second-stun")]
    public static void EventHorizonPreventWindow()
    {
        using var match = Arena(1);
        var edge = match.Foe;
        var point = new Position2(7500, 7000);
        edge.Position = new Position2(7850, 7000);
        match.FarFoe.Position = new Position2(12000, 12000);
        TestAssert.True(match.Cast(2, at: point), "Event Horizon is cast on the point");
        match.Advance(0.8);
        TestAssert.True(Buff(edge, "VeigarEventHorizonPrevent") is not null, "the stun adds the prevent buff (3.5 seconds)");
        // The foe stays on the ring's edge. Without the prevent buff the marker would stun it again on the first tick after the stun ends (about 1.8 seconds).
        match.Advance(1.4);
        TestAssert.True(Buff(edge, "Stun") is null, "no second stun while the prevent buff runs (2.2 seconds)");
        match.Advance(0.8);
        TestAssert.True(Buff(edge, "Stun") is null, "and none by 3.0 seconds, when the ring itself ends");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Primordial Burst
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Batch 582 (owner finding 124, "post-hit VFX weird placement"): the hit flash (the inibin's HitEffectName <c>permission_mana_flare_tar</c> on
    /// HitBoneName <c>root</c>) is placed as the client places a hit effect itself: 0x8C flags 8 (no attachment), no bone, no target, at the bone's height.
    /// Bound to <c>root</c>, it took the joint's orientation: Ezreal's root is 86.3 above his feet (95 at his SkinScale 1.1) with its local up nearly
    /// level (Ezreal.skl).
    /// </summary>
    [Check("scripting.bb-veigar-primordial-burst-hit-flash-stands-upright-at-the-root")]
    public static void PrimordialBurstHitFlashStandsUprightAtTheRoot()
    {
        using var match = Arena(3);
        var caster = Observe(match, Team.Order, match.Hero);
        TestAssert.True(match.Cast(3, match.Foe), "Primordial Burst is cast at the enemy Ezreal");
        match.Advance(1.5);
        match.Runtime.Delivery.Flush();
        var flash = caster.Packets.Select(item => item.Packet).Where(packet => EffectNamed(packet, "permission_mana_flare_tar")).ToArray();
        TestAssert.Equal(1, flash.Length, "the hit flash reaches the caster's client once");
        var span = flash[0].Plaintext.Span;
        TestAssert.Equal(0x08, (int)BinaryPrimitives.ReadUInt16LittleEndian(span[10..]), "flags 8: placed in the world, not attached to the unit");
        TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(span[16..]), "no bone");
        TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(span[21..]), "no target object");
        TestAssert.Equal(match.Foe.NetId, BinaryPrimitives.ReadUInt32LittleEndian(span[29..]), "the hit unit as its bind, as the client's own create names it");
        var root = GameData.Default.JointHeight("Ezreal", 0, "root") ?? double.NaN;
        TestAssert.Near(86.298 * 1.1, root, 0.1, "Ezreal's root joint stands 86.3 above his feet in the bind pose, times his SkinScale 1.1");
        var height = BinaryPrimitives.ReadSingleLittleEndian(span[35..]) - match.Runtime.Movement.Navigation.HeightAt(match.Foe.Position);
        TestAssert.Near(root, height, 0.5, $"the flash stands at that height above the ground (got {height:0.##})");
    }

    [Check("scripting.bb-veigar-primordial-burst-adds-the-target-ability-power")]
    public static void PrimordialBurstScalesWithTheTarget()
    {
        // The same cast on the same foe, with and without a Needlessly Large Rod. The hit log carries the raw amount before mitigation.
        var raw = new List<double>();
        var mitigated = new List<double>();
        var foePower = new List<double>();
        foreach (var rod in new[] { false, true })
        {
            using var match = Arena(3);
            var foe = match.Foe;
            if (rod) GiveRod(match, foe);
            TestAssert.True(rod is false || foe.AbilityPower > 50, "the foe carries a Needlessly Large Rod");
            foePower.Add(foe.AbilityPower);
            var cooldownBefore = match.Hero.SpellCooldowns[3];
            TestAssert.True(match.Cast(3, foe), "Primordial Burst is cast at the enemy");
            match.Advance(1.5);
            // Batch 340: the cooldown starts at the cast frame (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press.
            if (!rod) TestAssert.Near(90.0, match.Hero.SpellCooldowns[3] + 1.5 - match.CastFrameOf(3) - cooldownBefore, 0.5,
                "rank three's cooldown is the inibin's 90 seconds (the script's AutoCooldownByLevel is not read)");
            var hit = HitsOn(match, "Foe").Last();
            raw.Add(hit.Raw);
            mitigated.Add(hit.Dealt);
        }
        // Rank three: 500, plus 0.8 of the target's ability power (TargetExecute: TargetAP x 0.8). Veigar has no ability power of his own at the cast.
        TestAssert.Near(500.0, raw[0], 1e-6, "500 magic with no ability power on the foe");
        TestAssert.Near(0.8 * foePower[1], raw[1] - raw[0], 1e-6, $"the foe's ability power ({foePower[1]}) adds 0.8 of itself to the damage");
        TestAssert.Near(Mitigated(raw[1], 30.0), mitigated[1], 0.05, "the hit is mitigated by the foe's 30 magic resist");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Death
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-veigar-death-keeps-the-passive-and-the-ability-power")]
    public static void DeathKeepsThePassiveAndTheAbilityPower()
    {
        using var match = Arena(1);
        var hero = match.Hero;
        var victim = match.Foe;
        victim.Health = 40.0;
        TestAssert.True(match.Cast(0, victim), "a Baleful Strike kill first, so there is ability power to keep");
        match.Advance(2.0);
        Refresh(match, hero);
        var power = hero.AbilityPower;
        TestAssert.True(power > 1.5, "the kill raised the ability power");
        match.Runtime.Combat.Kill(hero, match.Foe.NetId);
        // A direct Kill does not schedule the respawn (CombatService.ScheduleRespawn runs from the lethal-hit caller); the check plays that caller.
        match.Runtime.Combat.ScheduleRespawn(hero);
        match.Advance(0.5);
        TestAssert.True(!hero.Alive, "Veigar is dead");
        TestAssert.True(Buff(hero, "VeigarEquilibrium") is not null, "Equilibrium persists through death (PersistsThroughDeath)");
        match.Advance(120.0);
        TestAssert.True(hero.Alive, "Veigar respawns");
        Refresh(match, hero);
        TestAssert.Near(power, hero.AbilityPower, 1e-6, "the ability power is still there after the respawn");
        TestAssert.True(Buff(hero, "VeigarEquilibrium") is not null, "and so is Equilibrium");
    }
}
