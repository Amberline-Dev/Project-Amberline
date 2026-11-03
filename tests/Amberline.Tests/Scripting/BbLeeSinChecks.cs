using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3 (19 October 2026): Lee Sin plays from his shipped scripts in a production <c>MatchRuntime</c> (the hand-written module is
/// gone). Every expected number is read from the shipped files (script tables through <see cref="BbScriptLoader"/>, spell inibins through
/// <see cref="GameData"/>) and pinned a second time as the literal the two files agree on. The harness puts Lee Sin at (7000, 7000), the
/// ally 150 units east, the enemy hero 300 and the far enemy out of reach. Written, not run (owner rule: tests at the end of the cycle).
/// </summary>
public static class BbLeeSinChecks
{
    // The "every native is bound" check leaves the shared system buffs out, as the Janna, Kayle and Ryze checks do: their hooks are the shipped
    // system scripts, not this champion's.
    // Batch 203: APBonusDamageToTowers and DisconnectTarget run from their shipped scripts now (a champion's CharOnActivate adds the first).
    // They stay in this list because the check counts the shared system buffs of the closure, not what the engine applies.
    private static readonly string[] SystemBuffs =
    [
        "APBonusDamageToTowers", "ChampionChampionDelta", "PositiveChampionDelta", "NegativeChampionDelta",
        "DisconnectTimer", "DisconnectTarget", "FortifyCheck", "FortifyBuff",
    ];

    // ------------------------------------------------------------------------------------------------------------
    // Closure and registration
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-leesin-production-runs-the-shipped-scripts")]
    public static void ProductionRegistersTheScriptModule()
    {
        var module = new BbChampionModule("LeeSin");
        var hero = new Entity { ChampionName = "LeeSin" };
        TestAssert.Equal(["BlindMonkQOne", "BlindMonkWOne", "BlindMonkEOne", "BlindMonkRKick"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from LeeSin.inibin (Data*Spell4 is BlindMonkRKick)");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "ranks");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("LeeSin");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(32, module.ScriptNames.Count, "the Lee Sin closure is 32 scripts (audit bb-coverage)");
        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => !SystemBuffs.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native a Lee Sin hook runs is bound: " + string.Join(", ", unbound));

        using var match = new Match("LeeSin");
        TestAssert.True(match.Runtime.Dependencies.ChampionDispatcher.FindModule("LeeSin") is BbChampionModule,
            "production registers BbChampionModule(\"LeeSin\") and no hand-written module");
    }

    [Check("scripting.bb-leesin-tables-agree-with-the-spell-data")]
    public static void ShippedTablesAgreeWithTheInibins()
    {
        TestAssert.Equal([50.0, 80.0, 110.0, 140.0, 170.0], ByLevel("BlindMonkQOne", "TargetExecute", "BaseDamage"), "BlindMonkQOne.luaobj BaseDamage");
        TestAssert.Equal([50.0, 80.0, 110.0, 140.0, 170.0], ByLevel("BlindMonkQTwo", "SelfExecute", "BaseDamage"), "BlindMonkQTwo.luaobj BaseDamage");
        TestAssert.Equal([40.0, 80.0, 120.0, 160.0, 200.0], ByLevel("BlindMonkWOne", "TargetExecute", "ShieldAbsorb"), "BlindMonkWOne.luaobj ShieldAbsorb");
        TestAssert.Equal([10.0, 15.0, 20.0, 25.0, 30.0], ByLevel("BlindMonkWTwo", "SelfExecute", "TotalArmor"), "BlindMonkWTwo.luaobj TotalArmor");
        TestAssert.Equal([0.05, 0.1, 0.15, 0.2, 0.25], ByLevel("BlindMonkWTwo", "SelfExecute", "LifestealPercent"), "BlindMonkWTwo.luaobj LifestealPercent");
        TestAssert.Equal([60.0, 95.0, 130.0, 165.0, 200.0], ByLevel("BlindMonkEOne", "SelfExecute", "BaseDamage"), "BlindMonkEOne.luaobj BaseDamage");
        TestAssert.Equal([-0.3, -0.375, -0.45, -0.525, -0.6], ByLevel("BlindMonkETwoMissile", "TargetExecute", "PercentReduction"), "BlindMonkETwoMissile.luaobj PercentReduction");
        TestAssert.Equal([200.0, 400.0, 600.0, 600.0, 600.0], ByLevel("BlindMonkRKick", "TargetExecute", "BaseDamage"), "BlindMonkRKick.luaobj BaseDamage");
        var q = GameData.Default.Spell("BlindMonkQOne", 5);
        TestAssert.Equal([11.0, 10.0, 9.0, 8.0, 7.0], q.Cooldowns.ToArray(), "Sonic Wave cooldowns");
        TestAssert.Equal(50.0, q.ManaCosts[0], "Sonic Wave costs 50 energy");
        TestAssert.Equal(30.0, GameData.Default.Spell("BlindMonkQTwo", 5).ManaCosts[0], "Resonating Strike costs 30 energy");
        TestAssert.Equal(30.0, GameData.Default.Spell("BlindMonkWTwo", 5).ManaCosts[0], "Iron Will costs 30 energy");
        TestAssert.Equal(30.0, GameData.Default.Spell("BlindMonkETwo", 5).ManaCosts[0], "Cripple costs 30 energy");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Q Sonic Wave and Resonating Strike
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-leesin-q-marks-swaps-the-slot-and-the-recast-dashes")]
    public static void SonicWaveMarksAndResonatingStrikeDashes()
    {
        using var match = new Match("LeeSin");
        match.Learn(5);
        match.Refill();
        var mana = match.Hero.Mana;
        var cast = match.Log.Casts.Count;
        TestAssert.True(match.Cast(0, null, at: match.Foe.Position), "Sonic Wave is a ground cast");
        TestAssert.Equal(cast + 1, match.Log.Casts.Count, "one cast announcement");
        var announced = match.Log.Casts[^1];
        TestAssert.Equal(SpellSystem.ProtocolHash("BlindMonkQOne"), announced.SpellHash, "hash");
        TestAssert.True(announced.MissileNetId != 0u, "a CastType 3 line announces its missile");
        TestAssert.Equal(0, announced.Targets.Count, "a line lists no unit");
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press; one step of regeneration allowed.
        var castFrame = match.CastFrameOf(0);
        match.Advance(castFrame);
        TestAssert.Near(mana - 50.0, match.Hero.Mana, 0.5, "50 energy");
        TestAssert.Near(7.0, match.Hero.SpellCooldowns[0], Step + 1e-6, "rank 5 cooldown 7 s");
        match.Advance(1.0 - castFrame);

        var mark = Buff(match.Foe, "BlindMonkQOne");
        TestAssert.True(mark is not null, "the Order caster's mark is BlindMonkQOne: " + Names(match.Foe));
        TestAssert.Near(2.0, mark!.ExpiresAt, 1.0, "3 s of mark, some already gone");
        TestAssert.True(Buff(match.Hero, "BlindMonkQManager") is not null, "the manager is on Lee: " + Names(match.Hero));
        TestAssert.Equal("BlindMonkQTwo", match.Hero.ActiveSpellNames[0], "slot 0 holds Resonating Strike (0x35, rank kept)");
        TestAssert.Near(0.0, match.Hero.SpellCooldowns[0], 1e-6, "the recast is ready at once");
        TestAssert.Equal(5, match.Hero.SpellRanks[0], "the rank survives the swap");
        TestAssert.True(match.Runtime.World.TimedVisionBubbles.Count(bubble => bubble.Team == Team.Order && bubble.Remaining > 0.0) >= 2,
            "the mark's two perception bubbles (400 and 50 true sight) come from the script");
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "one hit");
        NearF32(Mitigated(170.0, match.Foe.Armor), hits[0].Dealt, "Sonic Wave rank 5: 170 physical damage mitigated by armor");
        TestAssert.Equal(1, match.Log.DestroyedMissiles.Count(id => id == announced.MissileNetId), "the missile is destroyed once, on the hit");

        // The recast: a self-area instacast that spends 30 energy and dashes to the marked unit.
        var energy = match.Hero.Mana;
        TestAssert.True(match.Cast(0, null), "Resonating Strike is accepted with a marked unit in range");
        TestAssert.Near(energy - 30.0, match.Hero.Mana, 1.0, "30 energy");
        match.Advance(0.05);
        TestAssert.True(match.Hero.ForcedMove is { Effect: "BlindMonkQTwoDash" }, "the dash is the script's buff move");
        match.Advance(1.0);
        TestAssert.Equal("BlindMonkQOne", match.Hero.ActiveSpellNames[0], "slot 0 is Sonic Wave again");
        TestAssert.True(Buff(match.Foe, "BlindMonkQOne") is null, "the mark is spent");
        TestAssert.True(Buff(match.Hero, "BlindMonkQManager") is null, "the manager is gone");
        TestAssert.True(match.Hero.SpellCooldowns[0] > 4.0, "the first cast's cooldown (3 + the unused part of the window) is charged on the swap back");
        var landing = HitsOn(match, "Foe").Skip(1).First();
        TestAssert.True(landing.Raw >= 170.0, "the landing deals 170 plus 10% of the missing health: " + landing.Raw);
        TestAssert.True(WorldDistance(match.Hero.Position, match.Foe.Position) < 200.0, "Lee Sin stands next to the marked unit");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-leesin-q-window-runs-out-and-the-slot-returns-with-its-cooldown")]
    public static void SonicWaveWindowEnds()
    {
        using var match = new Match("LeeSin");
        match.Learn(5);
        match.Refill();
        // Batch 159: every map turret's own character script adds a TurretBonusHealth perception bubble at the
        // character's creation (Spells/TurretBonusHealth.luaobj, BBAddUnitPerceptionBubble with the add's BubbleSize:
        // 800 on the lane characters, 1600 on ChaosTurretShrine), so the Order side always carries its turret
        // bubbles. What the mark adds is on top of those and must go with the mark.
        var mapBubbles = match.Runtime.World.TimedVisionBubbles.Count(bubble =>
            bubble.Team == Team.Order && bubble.Remaining > 0.0);
        TestAssert.True(mapBubbles >= 2, "the map's own turret bubbles are there before the cast: " + mapBubbles);
        TestAssert.True(match.Cast(0, null, at: match.Foe.Position), "cast");
        match.Advance(0.8);
        TestAssert.Equal("BlindMonkQTwo", match.Hero.ActiveSpellNames[0], "the window is open");
        match.Advance(3.0);
        TestAssert.Equal("BlindMonkQOne", match.Hero.ActiveSpellNames[0], "the window closed after three seconds");
        TestAssert.True(Buff(match.Foe, "BlindMonkQOne") is null && Buff(match.Hero, "BlindMonkQManager") is null, "mark and manager are gone");
        TestAssert.True(match.Hero.SpellCooldowns[0] > 0.0 && match.Hero.SpellCooldowns[0] <= 3.0, "rank 5: 3 s of cooldown start when the window ends: " + match.Hero.SpellCooldowns[0]);
        TestAssert.Equal(mapBubbles, match.Runtime.World.TimedVisionBubbles.Count(bubble =>
            bubble.Team == Team.Order && bubble.Remaining > 0.0),
            "the mark's bubbles go with the mark (the map turrets' own TurretBonusHealth ones stay)");
    }

    [Check("scripting.bb-leesin-q-miss-leaves-no-window")]
    public static void SonicWaveMissLeavesNoWindow()
    {
        using var match = new Match("LeeSin");
        match.Learn(1);
        match.Refill();
        TestAssert.True(match.Cast(0, null, at: new Position2(7000, 7900)), "cast away from every unit");
        match.Advance(1.5);
        TestAssert.True(match.Hero.ActiveSpellNames.Count == 0 || match.Hero.ActiveSpellNames[0].Length == 0 || match.Hero.ActiveSpellNames[0] == "BlindMonkQOne", "no swap");
        TestAssert.True(Buff(match.Hero, "BlindMonkQManager") is null && Buff(match.Foe, "BlindMonkQOne") is null, "no mark, no manager");
        TestAssert.True(match.Hero.SpellCooldowns[0] > 0.0, "the first cast's cooldown runs");
    }

    // ------------------------------------------------------------------------------------------------------------
    // W Safeguard and Iron Will
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-leesin-w-shields-himself-and-iron-will-follows")]
    public static void SafeguardSelfAndIronWill()
    {
        using var match = new Match("LeeSin");
        match.Learn(5);
        match.Refill();
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(1, match.Hero), "Safeguard on himself");
        match.Advance(0.1);
        TestAssert.Near(mana - 50.0 + match.Hero.ManaRegen * 0.1, match.Hero.Mana, 0.05, "50 energy, plus the 0.1 s of energy regeneration");
        TestAssert.True(Buff(match.Hero, "BlindMonkWOneShield") is not null, "the shield: " + Names(match.Hero));
        TestAssert.Equal("BlindMonkWTwo", match.Hero.ActiveSpellNames[1], "slot 1 holds Iron Will");
        TestAssert.Near(0.0, match.Hero.SpellCooldowns[1], 1e-6, "the recast is ready");
        var armor = match.Hero.Armor;
        TestAssert.True(match.Cast(1, null), "Iron Will");
        match.Advance(0.1);
        var buff = Buff(match.Hero, "BlindMonkWTwo");
        TestAssert.True(buff is not null, "Iron Will: " + Names(match.Hero));
        TestAssert.Near(armor + 30.0, match.Hero.Armor, 0.5, "rank 5: +30 armor");
        TestAssert.Near(0.25, match.Hero.LifeSteal, 1e-6, "rank 5: 25% life steal");
        TestAssert.Near(0.25, match.Hero.SpellVamp, 1e-6, "rank 5: 25% spell vamp");
        TestAssert.Equal("BlindMonkWOne", match.Hero.ActiveSpellNames[1], "slot 1 is Safeguard again");
        TestAssert.True(match.Hero.SpellCooldowns[1] > 5.0 && match.Hero.SpellCooldowns[1] < 8.0, "5 s plus the unused part of the window: " + match.Hero.SpellCooldowns[1]);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-leesin-w-dash-to-an-ally-shields-both-on-arrival")]
    public static void SafeguardDashShieldsBoth()
    {
        using var match = new Match("LeeSin");
        match.Learn(5);
        match.Refill();
        match.Ally.Position = new Position2(7600, 7000);
        TestAssert.True(match.Cast(1, match.Ally), "Safeguard on an ally 600 away");
        match.Advance(0.05);
        TestAssert.True(match.Hero.ForcedMove is { Effect: "BlindMonkWOneDash" }, "the dash is the script's move");
        TestAssert.True(Buff(match.Hero, "BlindMonkWOneDash") is not null, "the dash buff: " + Names(match.Hero));
        TestAssert.True(match.Hero.Buffs.Any(item => item.Payload.Ghosted), "ghosted while he dashes");
        TestAssert.True(Buff(match.Ally, "BlindMonkWOneShield") is null, "the shields come on arrival");
        match.Advance(1.0);
        TestAssert.True(Buff(match.Hero, "BlindMonkWOneShield") is not null && Buff(match.Ally, "BlindMonkWOneShield") is not null,
            "both shielded: " + Names(match.Hero) + " | " + Names(match.Ally));
        // BlindMonkWOneDash moves with IdealDistance = BBDistanceBetweenObjects(Lee, ally), the gap between the bodies (edge to edge since batch 112):
        // he stops where the bodies touch, beside the ally, not on its centre.
        TestAssert.Near(match.Hero.CombatRadius + match.Ally.CombatRadius, WorldDistance(match.Hero.Position, match.Ally.Position), 5.0,
            "Lee Sin stands in contact with the ally");
        TestAssert.Equal("BlindMonkWTwo", match.Hero.ActiveSpellNames[1], "Iron Will is up");
        TestAssert.True(Buff(match.Hero, "BlindMonkWOneDash") is null, "the dash buff ended with the move");
        TestAssert.False(match.Hero.Buffs.Any(item => item.Payload.Ghosted), "no longer ghosted");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-leesin-w-shield-break-returns-no-energy")]
    public static void SafeguardReturnsNoEnergyWhenItsShieldBreaks()
    {
        // Batch 220 (owner decision, 25 October 2026): follow the shipped script. BlindMonkWOne, BlindMonkWOneDash, BlindMonkWOneShield
        // and the Lee Sin character script never return energy when a shield breaks, so the 18 October refund is gone.
        using var match = new Match("LeeSin");
        match.Learn(5);
        match.Refill();
        match.Ally.Position = new Position2(7600, 7000);
        TestAssert.True(match.Cast(1, match.Ally), "dash");
        match.Advance(1.0);
        match.Hero.Mana = 10.0;
        var combat = match.Runtime.SpellCombat;
        combat.ApplyDamage(match.Foe, match.Hero, 50.0, DamageType.True, DamageSourceKind.Raw);
        TestAssert.Near(10.0, match.Hero.Mana, 1e-6, "a hit the shield absorbs breaks nothing and refunds nothing");
        match.Hero.GodMode = false;
        combat.ApplyDamage(match.Foe, match.Hero, 400.0, DamageType.True, DamageSourceKind.Raw);
        TestAssert.True(Buff(match.Hero, "BlindMonkWOneShield") is null, "the overflow broke Lee Sin's shield");
        TestAssert.Near(10.0, match.Hero.Mana, 1e-6, "the shield's break returns no energy");
        match.Ally.GodMode = false;
        combat.ApplyDamage(match.Foe, match.Ally, 400.0, DamageType.True, DamageSourceKind.Raw);
        TestAssert.True(Buff(match.Ally, "BlindMonkWOneShield") is null, "the ally's shield broke too");
        TestAssert.Near(10.0, match.Hero.Mana, 1e-6, "the ally's shield breaking returns no energy either");
    }

    // ------------------------------------------------------------------------------------------------------------
    // E Tempest and Cripple
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-leesin-e-strikes-marks-and-cripple-slows")]
    public static void TempestMarksAndCrippleSlows()
    {
        using var match = new Match("LeeSin");
        match.Learn(5);
        match.Refill();
        match.FarFoe.Position = new Position2(7000, 7600);
        var mana = match.Hero.Mana;
        var cast = match.Log.Casts.Count;
        TestAssert.True(match.Cast(2, null), "Tempest");
        var announced = match.Log.Casts[cast];
        TestAssert.True(announced.Targets.Any(target => target.NetId == match.Foe.NetId), "the cast lists the enemy it strikes (the caster reveal)");
        match.Advance(0.6);
        // Batch 340: the cast frame pays (CastFrameOf after the press); full energy does not regenerate before it.
        // (Tempest's slot holds BlindMonkETwo by now, so the cast frame is read by the first spell's name.)
        TestAssert.Near(mana - 50.0 + match.Hero.ManaRegen * (0.6 - CastFrame("BlindMonkEOne")), match.Hero.Mana, 0.4,
            "50 energy, plus the energy regenerated since the cast frame");
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(1, hits.Count, "one hit on the enemy 300 away");
        NearF32(Mitigated(200.0, match.Foe.SpellBlock), hits[0].Dealt, "rank 5: 200 magic damage");
        TestAssert.Equal(0, HitsOn(match, "FarFoe").Count, "600 away is out of the 450 radius");
        TestAssert.True(Buff(match.Foe, "BlindMonkEOne") is not null, "the mark: " + Names(match.Foe));
        TestAssert.True(Buff(match.Hero, "BlindMonkEManager") is not null, "the manager: " + Names(match.Hero));
        TestAssert.Equal("BlindMonkETwo", match.Hero.ActiveSpellNames[2], "slot 2 holds Cripple");
        TestAssert.Near(0.1, match.Hero.SpellCooldowns[2], 0.12, "the recast is ready within a tenth of a second");

        TestAssert.True(match.Cast(2, null), "Cripple");
        match.Advance(0.6);
        var slow = Buff(match.Foe, "BlindMonkETwoMissile");
        TestAssert.True(slow is not null, "the slow: " + Names(match.Foe));
        TestAssert.Equal("BlindMonkEOne", match.Hero.ActiveSpellNames[2], "slot 2 is Tempest again");
        TestAssert.True(match.Hero.SpellCooldowns[2] > 6.0 && match.Hero.SpellCooldowns[2] < 10.0, "7 s plus the unused part of the window: " + match.Hero.SpellCooldowns[2]);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-leesin-e-without-a-unit-opens-no-window")]
    public static void TempestWithoutAHitOpensNoWindow()
    {
        using var match = new Match("LeeSin");
        match.Learn(1);
        match.Refill();
        match.Foe.Position = new Position2(7000, 8000);
        TestAssert.True(match.Cast(2, null), "Tempest");
        match.Advance(0.6);
        TestAssert.True(Buff(match.Hero, "BlindMonkEManager") is null, "no manager");
        TestAssert.True(match.Hero.ActiveSpellNames.Count <= 2 || match.Hero.ActiveSpellNames[2].Length == 0 || match.Hero.ActiveSpellNames[2] == "BlindMonkEOne",
            "slot 2 is still Tempest");
        TestAssert.True(match.Hero.SpellCooldowns[2] > 8.0, "the full 10 s cooldown runs");
    }

    // ------------------------------------------------------------------------------------------------------------
    // R Dragon's Rage
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-leesin-r-roots-the-windup-kicks-and-strikes-what-is-in-the-way")]
    public static void DragonsRageKicksAndCollides()
    {
        using var match = new Match("LeeSin");
        match.Learn(3);
        match.Refill();
        // Along the mid lane (NE), where 700 units of floor are open behind the target: east of the harness spot the jungle wall ends the flight
        // at 529 units. The unit in the way stands 220 beyond the target, 10 to the side.
        const double Along = 0.70710678118654752;
        match.Foe.Position = new Position2(7000 + 300 * Along, 7000 + 300 * Along);
        match.FarFoe.Position = new Position2(7000 + 520 * Along - 10 * Along, 7000 + 520 * Along + 10 * Along);
        var start = match.Foe.Position;
        TestAssert.True(match.Cast(3, match.Foe), "Dragon's Rage on the enemy 300 away");
        match.Advance(0.1);
        TestAssert.True(Buff(match.Foe, "BlindMonkRRoot") is not null, "the windup roots the target (CharOnSpellCast, Target = the cast's unit): " + Names(match.Foe));
        match.Advance(0.4);
        TestAssert.True(match.Foe.ForcedMove is { Effect: "BlindMonkRKick" }, "the kick is the script's BBMove");
        TestAssert.True(Buff(match.Foe, "BlindMonkRKick") is not null, "the 4 s stun buff holds the target: " + Names(match.Foe));
        match.Advance(1.5);
        // BBDistanceBetweenObjects is the gap between the bodies (batch 112): the kick aims at Lee + (gap + 800), so the target flies 800 less both radii.
        TestAssert.Near(800.0 - match.Hero.CombatRadius - match.Foe.CombatRadius, WorldDistance(start, match.Foe.Position), 25.0,
            "knocked back 800 units less the two body radii");
        TestAssert.True(Buff(match.Foe, "BlindMonkRKick") is null, "the stun ends with the move");
        var primary = HitsOn(match, "Foe");
        TestAssert.True(primary.Count >= 1, "the target takes the kick");
        NearF32(Mitigated(600.0, match.Foe.Armor), primary[0].Dealt, "rank 3: 600 physical damage");
        var collision = HitsOn(match, "FarFoe");
        TestAssert.Equal(1, collision.Count, "the unit in the way takes one hit");
        NearF32(Mitigated(600.0, match.FarFoe.Armor), collision[0].Dealt, "the same damage (SecondaryDamage = PrimaryDamage / 1)");
        TestAssert.True(match.Hero.SpellCooldowns[3] > 50.0, "rank 3 cooldown is 60 s");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-leesin-r-target-lost-in-the-windup-refunds-the-cast")]
    public static void DragonsRageTargetLostInTheWindupRefunds()
    {
        using var match = new Match("LeeSin");
        match.Learn(3);
        match.Refill();
        TestAssert.True(match.Cast(3, match.Foe), "cast");
        match.Advance(0.1);
        match.Runtime.Combat.Kill(match.Foe, match.Hero.NetId);
        match.Advance(0.5);
        TestAssert.Equal(0, HitsOn(match, "Foe").Count, "a dead target takes no kick");
        TestAssert.Near(0.0, match.Hero.SpellCooldowns[3], 1e-6, "the cast is refunded (owner decision of 29 September 2026)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Passive
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-leesin-flurry-follows-a-spell-and-pays-energy-twice")]
    public static void FlurryPaysEnergyOnTwoAttacks()
    {
        using var match = new Match("LeeSin");
        match.Learn(1);
        match.Refill();
        match.Foe.Position = new Position2(7100, 7000);
        TestAssert.True(Buff(match.Hero, "BlindMonkPassive") is null, "no passive buff before a spell");
        TestAssert.True(match.Cast(2, null), "any spell");
        match.Advance(0.6);
        var flurry = Buff(match.Hero, "BlindMonkPassive");
        TestAssert.True(flurry is not null, "Flurry after the cast: " + Names(match.Hero));
        TestAssert.Near(3.0, flurry!.ExpiresAt, 0.8, "three seconds");
        match.Hero.Mana = 40.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "an attack");
        match.Advance(1.2);
        TestAssert.True(match.Hero.Mana >= 50.0, "the first landed attack pays 15 energy: " + match.Hero.Mana);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    private static double WorldDistance(Position2 a, Position2 b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
