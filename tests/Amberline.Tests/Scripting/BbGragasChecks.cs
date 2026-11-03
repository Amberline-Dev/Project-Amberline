using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 4 (26 October 2026, batch 265): Gragas plays from his shipped scripts. Expected numbers are the literals of the shipped scripts (the
/// condensed block trees of <c>DATA/Spells/Gragas*.luaobj</c>) and the spell inibins (the audit fixture's standalone decode). Each probe runs at rank one
/// and level one with no items; the foe has no spell block, so a magic hit reads its raw damage (the harness's foe is Orianna at 7300, moved per probe).
/// The scenario <c>S-Gragas</c> shows the same facts on a headless match. Written 26 October 2026.
/// </summary>
public static class BbGragasChecks
{
    [Check("scripting.bb-gragas-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Gragas");
        foreach (var name in new[]
                 {
                     "CharScriptGragas", "GragasBarrelRoll", "GragasBarrelRollBoom", "GragasBarrelRollToggle", "GragasBarrelRollRender",
                     "GragasDrunkenRage", "GragasDrunkenRageSelf", "GragasBodySlam", "GragasBodySlamTargetSlow", "GragasExplosiveCask",
                     "GragasExplosiveCaskBoom", "GragasExplosiveCaskDebuff", "GragasHappyHour", "GragasPassiveHeal",
                 })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Gragas");
        TestAssert.Equal(28, closure.ScriptNames.Count(), "the closure is 28 scripts (four spells and their helpers, the passive, the system buffs)");

        var hero = new Entity { ChampionName = "Gragas" };
        TestAssert.Equal(["GragasBarrelRoll", "GragasDrunkenRage", "GragasBodySlam", "GragasExplosiveCask"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Gragas.inibin's slots");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the cask of three");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "R is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Gragas")!;
        TestAssert.True(info is { Playable: true, Role: "jungle", DefaultIp: 3150, RetailBot: false }, "playable as a jungler for 3150 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Gragas"), "listed once");
    }

    /// <summary>
    /// The second press of Barrel Roll (the toggle spell) detonates the barrel at once: 85 magic damage (rank one, no ability power) to a foe 140 units from
    /// the landing, the attack-speed debuff on the foe, the cooldown left at eleven less the barrel's life, and Gragas survives his own blast (the blast
    /// centres on the barrel, and the five-thousand true damage that ends the barrel is the barrel's own, not his).
    /// </summary>
    [Check("scripting.bb-gragas-barrel-detonates-on-the-second-press")]
    public static void SecondPressDetonates()
    {
        var match = Setup();
        match.Foe.Position = new Position2(7640.0, 7000.0);
        match.Advance(0.8);
        TestAssert.True(match.Cast(0, at: new Position2(7500.0, 7000.0)), "Barrel Roll is cast at the landing point");
        match.Advance(1.2);
        var before = match.Foe.Health;
        TestAssert.True(match.Cast(0, at: new Position2(7500.0, 7000.0)), "the second press (the toggle spell) is accepted");
        match.Advance(0.6);
        TestAssert.True(match.Hero.Alive, $"Gragas survives his own barrel (health {match.Hero.Health:0.##})");
        // The hit log, not the health bar: a bot foe's maximum health can be re-read mid-test, which moves the bar by itself.
        var lost = HitsOn(match, "Foe").Sum(hit => hit.Raw);
        TestAssert.True(lost is >= 75.0 and <= 95.0, $"the blast deals 85 magic damage to a foe 140 units from the barrel (got {lost:0.##}; hits {string.Join(" | ", match.Log.Hits)})");
        TestAssert.True(Buff(match.Foe, "GragasExplosiveCaskDebuff") is not null, "the foe carries the attack-speed debuff");
        TestAssert.True(match.Hero.SpellCooldowns[0] is >= 9.0 and <= 10.5, $"the cooldown is eleven less the barrel's life (got {match.Hero.SpellCooldowns[0]:0.##})");
    }

    /// <summary>
    /// The barrel's explosion is drawn on the barrel: <c>GragasBarrelRollBoom</c> names the barrel as <c>PosVar='Attacker'</c>, so the 0x8C source is the
    /// barrel's position (500 ahead of Gragas), while the third position stays on Gragas, the create's target object (the client moves that object to it).
    /// </summary>
    [Check("scripting.bb-gragas-barrel-boom-is-drawn-on-the-barrel")]
    public static void BoomIsDrawnOnTheBarrel()
    {
        var match = Setup();
        match.Foe.Position = new Position2(7640.0, 7000.0);
        match.Advance(0.8);
        TestAssert.True(match.Cast(0, at: new Position2(7500.0, 7000.0)), "Barrel Roll is cast at the landing point");
        match.Advance(1.2);
        var caster = Observe(match, Team.Order, match.Hero);
        TestAssert.True(match.Cast(0, at: new Position2(7500.0, 7000.0)), "the second press detonates the barrel");
        match.Advance(0.6);
        match.Runtime.Delivery.Flush();
        var booms = caster.Packets.Select(item => item.Packet)
            .Where(packet => EffectNamed(packet, "gragas_barrelboom", "gragas_barrelboom_classy")).ToArray();
        TestAssert.True(booms.Length > 0, "the explosion reaches the caster's client");
        var (dx, dz) = EffectDelta(booms[0], 33, 49);
        TestAssert.Near(500.0, dx, 2.5, $"the explosion is drawn on the barrel, 500 ahead of Gragas (source minus Gragas, x; got {dx:0.#})");
        TestAssert.Near(0.0, dz, 2.5, "and on his line (z)");
    }

    /// <summary>The barrel's fuse is five seconds from its landing: the blast comes then, and the cooldown is eleven less the five seconds.</summary>
    [Check("scripting.bb-gragas-barrel-fuse-detonates-after-five-seconds")]
    public static void FuseDetonatesAfterFiveSeconds()
    {
        var match = Setup();
        match.Foe.Position = new Position2(7640.0, 7000.0);
        // The foe stands still: a bot foe walks off during the five seconds otherwise.
        match.Runtime.Movement.SetMoveOrder(match.Foe.NetId, match.Foe.Position);
        match.Advance(0.8);
        TestAssert.True(match.Cast(0, at: new Position2(7500.0, 7000.0)), "Barrel Roll is cast");
        // The barrel lands about half a second after the press (the missile's flight); the fuse runs five seconds from that landing.
        match.Advance(3.0);
        TestAssert.Near(0.0, HitsOn(match, "Foe").Sum(hit => hit.Raw), 0.5, "no blast before the fuse (the foe is untouched)");
        match.Advance(3.4);
        var lost = HitsOn(match, "Foe").Sum(hit => hit.Raw);
        TestAssert.True(lost is >= 75.0 and <= 95.0,
            $"the fuse sets the barrel off after five seconds (got {lost:0.##}; boom {Buff(match.Hero, "GragasBarrelRollBoom") is not null}; foe at {match.Foe.Position.X:0.##}; hits {string.Join(" | ", match.Log.Hits)})");
        TestAssert.True(match.Hero.SpellCooldowns[0] is >= 5.0 and <= 7.0, $"the cooldown is eleven less five (got {match.Hero.SpellCooldowns[0]:0.##})");
    }

    /// <summary>Drunken Rage's channel restores fifteen mana when it starts and fifteen when it ends (rank one: 30 in all), and then empowers him.</summary>
    [Check("scripting.bb-gragas-drunken-rage-restores-its-mana-on-the-channel-and-on-the-end")]
    public static void DrunkenRageRestoresMana()
    {
        var match = Setup();
        match.Hero.Mana = 0.0;
        match.Advance(0.8);
        TestAssert.True(match.Cast(1, match.Hero), "Drunken Rage is cast on Gragas");
        match.Advance(1.6);
        // Thirty from the two halves, plus the hero's own mana regeneration over the 2.4 seconds since the first press.
        TestAssert.True(match.Hero.Mana is >= 29.0 and <= 36.0, $"the channel and its end restore fifteen each at rank one (got {match.Hero.Mana:0.##})");
        TestAssert.True(Buff(match.Hero, "GragasDrunkenRageSelf") is not null, "the empowered buff follows a finished channel");
    }

    /// <summary>A channel that a move cancels still restores the mana (the deactivation of the drink buff gives its half again) and gives no empowerment.</summary>
    [Check("scripting.bb-gragas-drunken-rage-cancelled-by-a-move-keeps-the-mana")]
    public static void CancelledChannelKeepsTheMana()
    {
        var match = Setup();
        match.Hero.Mana = 0.0;
        match.Advance(0.8);
        TestAssert.True(match.Cast(1, match.Hero), "Drunken Rage is cast");
        match.Advance(0.4);
        match.Runtime.Movement.SetMoveOrder(match.Hero.NetId, new Position2(6500.0, 7000.0));
        match.Advance(1.6);
        TestAssert.True(match.Hero.Mana is >= 29.0 and <= 36.0, $"the cancelled channel still restores both halves (got {match.Hero.Mana:0.##})");
        TestAssert.True(Buff(match.Hero, "GragasDrunkenRageSelf") is null, "a cancelled channel does not empower him");
    }

    /// <summary>Body Slam stops at the first unit in its way (the foe 380 units out) and slows what it hits by 35 percent for 2.5 seconds.</summary>
    [Check("scripting.bb-gragas-body-slam-stops-at-the-first-unit-and-slows-it")]
    public static void BodySlamStopsAtTheFirstUnit()
    {
        var match = Setup();
        match.Foe.Position = new Position2(7380.0, 7000.0);
        match.Advance(0.8);
        var before = match.Foe.Health;
        TestAssert.True(match.Cast(2, at: new Position2(7600.0, 7000.0)), "Body Slam is cast toward a point 600 units out");
        match.Advance(1.2);
        var forward = match.Hero.Position.X - 7000.0;
        TestAssert.True(forward is >= 250.0 and <= 340.0, $"the dash stops at the first unit it hits (forward {forward:0.##})");
        TestAssert.True(before - match.Foe.Health > 20.0, "the foe takes the Body Slam damage");
        TestAssert.True(Buff(match.Foe, "GragasBodySlamTargetSlow") is not null, "the foe is slowed by Body Slam");
    }

    /// <summary>With no unit in the way Body Slam runs its full distance (the script's 600 cap).</summary>
    [Check("scripting.bb-gragas-body-slam-with-no-unit-runs-six-hundred-units")]
    public static void OpenDashRunsSixHundred()
    {
        var match = Setup();
        match.Foe.Position = new Position2(9000.0, 9000.0);
        match.Advance(0.8);
        TestAssert.True(match.Cast(2, at: new Position2(7600.0, 7000.0)), "Body Slam is cast");
        match.Advance(1.6);
        var forward = match.Hero.Position.X - 7000.0;
        TestAssert.True(forward is >= 560.0 and <= 610.0, $"the open dash runs about six hundred units (forward {forward:0.##})");
    }

    /// <summary>The Explosive Cask hits everything within its radius for 200 magic damage at rank one, and knocks the foe back from its centre.</summary>
    [Check("scripting.bb-gragas-explosive-cask-hits-and-knocks-the-foe-back-from-its-centre")]
    public static void CaskHitsAndKnocksBack()
    {
        var match = Setup();
        var centre = new Position2(7700.0, 7000.0);
        match.Foe.Position = new Position2(7760.0, 7000.0);
        // The foe stands still, so the distance it ends at is the knock-back and not a walk back to its lane.
        match.Runtime.Movement.SetMoveOrder(match.Foe.NetId, match.Foe.Position);
        match.Advance(0.8);
        var before = match.Foe.Health;
        TestAssert.True(match.Cast(3, at: centre), "Explosive Cask is cast at the centre");
        match.Advance(1.6);
        var lost = HitsOn(match, "Foe").Sum(hit => hit.Raw);
        TestAssert.True(lost is >= 190.0 and <= 210.0, $"the cask deals 200 magic damage at rank one (got {lost:0.##})");
        var dx = match.Foe.Position.X - centre.X;
        var dz = match.Foe.Position.Z - centre.Z;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        // From 60 units out to well beyond the blast (the amount depends on the victim: the scenario's Ezreal goes about 380 units out).
        TestAssert.True(distance >= 150.0, $"the foe is knocked back from the centre (distance {distance:0.##}, foe at {match.Foe.Position.X:0.##},{match.Foe.Position.Z:0.##})");
    }

    /// <summary>
    /// Batch 582 (owner finding 25, "two barrels, only sometimes"): the Boom (<c>GragasExplosiveCaskBoom</c>, Instacast, homing) is built on each client by
    /// its missile packet alone. Its cast answer built a second one whenever R's SpellCaster was still current on that client (the client's cast handler,
    /// then hands an extra-slot child straight to the cast-and-serve routine), and a player client keeps both (the duplicate check is spectator-only).
    /// </summary>
    [Check("scripting.bb-gragas-explosive-cask-boom-is-created-once-per-client")]
    public static void CaskBoomIsCreatedOncePerClient()
    {
        var match = Setup();
        var caster = Observe(match, Team.Order, match.Hero);
        var enemy = Observe(match, Team.Chaos, match.Foe, 9002);
        match.Advance(0.8);
        TestAssert.True(match.Cast(3, at: new Position2(7500.0, 7000.0)), "Explosive Cask is cast 500 ahead");
        match.Advance(1.6);
        match.Runtime.Delivery.Flush();
        var boom = SpellSystem.ProtocolHash("GragasExplosiveCaskBoom");
        foreach (var (name, capture) in new[] { ("caster", caster), ("enemy", enemy) })
        {
            var packets = capture.Packets.Select(item => item.Packet).ToArray();
            var created = packets.Where(packet => packet.Plaintext.Span[0] == 0x41 && MissileOf(packet)?.Spell == boom)
                .Select(packet => MissileOf(packet)!.Value.Missile).ToArray();
            TestAssert.Equal(1, created.Length, $"the {name}'s client gets the Boom's missile packet once");
            var answers = packets.Count(packet => packet.Plaintext.Span[0] == SpellPackets.CastSpellAnswerOpcode && MissileOf(packet)?.Missile == created[0]);
            TestAssert.Equal(0, answers, $"and no cast answer that builds the same missile again ({name})");
        }
    }

    /// <summary>A spell cast restores two percent of his maximum health over four seconds (Happy Hour: one percent on each of two ticks).</summary>
    [Check("scripting.bb-gragas-happy-hour-heals-two-percent-of-his-health-on-a-cast")]
    public static void HappyHourHealsOnACast()
    {
        var match = Setup();
        var heal = PassiveHeal(match, respawn: false);
        var expected = 0.02 * match.Hero.MaxHealth;
        TestAssert.True(Math.Abs(heal - expected) <= 1.0, $"two heals of one percent of his maximum health over four seconds (got {heal:0.##}, expected {expected:0.##})");
    }

    /// <summary>
    /// After a death the passive still heals: the heal is cast-driven (CharOnSpellCast adds GragasPassiveHeal), so the respawn needs no
    /// add of its own. OPEN (see the record): the Happy Hour aura icon is not added again at the respawn.
    /// </summary>
    [Check("scripting.bb-gragas-respawn-keeps-the-passive-heal")]
    public static void RespawnKeepsTheHeal()
    {
        var match = Setup();
        var heal = PassiveHeal(match, respawn: true);
        var expected = 0.02 * match.Hero.MaxHealth;
        TestAssert.True(Math.Abs(heal - expected) <= 1.0, $"the heal after a respawn is the same two percent (got {heal:0.##}, expected {expected:0.##})");
    }

    /// <summary>
    /// The heal of one spell cast by the passive, beyond the hero's own regeneration: the same hero (optionally killed and respawned first) and a control
    /// hero of the same kind are brought to half health, and only the first casts Drunken Rage. Both then run the same time.
    /// </summary>
    private static double PassiveHeal(Match match, bool respawn)
    {
        var control = Setup();
        foreach (var side in new[] { match, control })
        {
            if (respawn)
            {
                side.Runtime.Combat.Kill(side.Hero, side.Foe.NetId);
                side.Runtime.Combat.ScheduleRespawn(side.Hero);
                side.Advance(60.0);
            }
            side.Advance(0.5);
            side.Hero.Health = side.Hero.MaxHealth * 0.5;
        }
        var castStart = match.Hero.Health;
        var controlStart = control.Hero.Health;
        TestAssert.True(match.Cast(1, match.Hero), "Drunken Rage is cast (a spell cast starts the passive)");
        match.Advance(4.6);
        control.Advance(4.6);
        return (match.Hero.Health - castStart) - (control.Hero.Health - controlStart);
    }

    private static Match Setup()
    {
        var match = new Match("Gragas");
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7300, 7000);
        match.Foe.SpellBlock = 0.0;
        match.Hero.Facing = new Position2(1.0, 0.0);
        match.Refill();
        return match;
    }
}
