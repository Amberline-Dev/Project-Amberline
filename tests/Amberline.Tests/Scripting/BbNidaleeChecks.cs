using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3 (19 October 2026): Nidalee plays from her shipped scripts in a production <c>MatchRuntime</c> (the hand-written module is gone). Expected numbers
/// come from the shipped files (script tables and spell inibins) and are pinned a second time as literals. Written, not run (owner rule: no check
/// runs during the overnight phase 3); the scenario <c>S-nidalee</c> shows the same facts on a headless match.
/// </summary>
public static class BbNidaleeChecks
{
    // Batch 203: APBonusDamageToTowers and DisconnectTarget run from their shipped scripts now (a champion's CharOnActivate adds the first).
    // They stay in this list because the check counts the shared system buffs of the closure, not what the engine applies.
    private static readonly string[] SystemBuffs =
    [
        "APBonusDamageToTowers", "ChampionChampionDelta", "PositiveChampionDelta", "NegativeChampionDelta",
        "DisconnectTimer", "DisconnectTarget", "FortifyCheck", "FortifyBuff",
    ];

    private static BuffState? Of(Entity unit, string name) => Buff(unit, name);

    private static string Failures(Match match) =>
        "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    private static double Between(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>A table the character script sets in one hook (<c>SrcValueByLevel</c> of the first block that sets the variable).</summary>
    private static double[] CharByLevel(string hook, string variable)
    {
        var block = CharScript("Nidalee").Event(hook)!.Blocks.SelectMany(Flatten)
            .First(item => item.Function == "BBSetVarInTable" && item.Text("DestVar") == variable);
        return Numbers(block.Param("SrcValueByLevel")!);
    }

    /// <summary>A point of brush the hero stands on (the passive tests the cell under her).</summary>
    private static Position2 FindBrush(Match match)
    {
        var grid = match.Runtime.Navigation;
        for (var z = 500.0; z < 14000.0; z += 150.0)
            for (var x = 500.0; x < 14000.0; x += 150.0)
            {
                var point = new Position2(x, z);
                if (grid.IsMovementWalkable(point) && grid.BrushIdAt(point, match.Hero.SelectionRadius) is not null) return point;
            }
        throw new InvalidOperationException("the map has no brush");
    }

    /// <summary>Learns the spells and runs the character script's level-up hook for the ultimate (it stores the cougar damage by its rank).</summary>
    private static void LearnWithTheUltimate(Match match, int rank)
    {
        match.Learn(rank);
        match.Module.OnRankUp(match.Runtime.Spells, match.Hero, 3, Math.Min(rank, 3));
    }

    private static void Transform(Match match)
    {
        match.Refill();
        TestAssert.True(match.Cast(3), "Aspect of the Cougar");
        match.Advance(0.5);
        TestAssert.True(Of(match.Hero, "AspectOfTheCougar") is not null, "the cougar form is on");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Closure and registration
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-nidalee-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndRunsEveryNativeOfItsHooks()
    {
        var module = new BbChampionModule("Nidalee");
        TestAssert.Equal(28, module.ScriptNames.Count, "the Nidalee closure is 28 scripts (the form's Takedown, Pounce and Swipe included)");
        foreach (var name in new[] { "CharScriptNidalee", "JavelinToss", "Bushwhack", "BushwhackDebuff", "PrimalSurge", "AspectOfTheCougar", "Prowl", "Takedown",
                     "Pounce", "Swipe", "NidaleeTakedownAttack" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var hero = new Entity { ChampionName = "Nidalee" };
        TestAssert.Equal(["JavelinToss", "Bushwhack", "PrimalSurge", "AspectOfTheCougar"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Nidalee.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "ranks");

        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Nidalee");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => !SystemBuffs.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native a Nidalee hook runs is bound: " + string.Join(", ", unbound));
        TestAssert.Equal("Nidalee_Spear", Script("Bushwhack").Event("SelfExecute")!.Blocks.SelectMany(Flatten)
            .First(item => item.Function == "BBSpawnMinion").Text("Skin"), "Bushwhack spawns the Noxious Trap");
    }

    [Check("scripting.bb-nidalee-production-runs-the-shipped-scripts")]
    public static void ProductionRunsNidaleeFromHerScripts()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default, readySettleSeconds: 0);
        var dispatcher = (ChampionSpellDispatcher)runtime.Dependencies.ChampionDispatcher;
        TestAssert.True(dispatcher.FindModule("Nidalee") is BbChampionModule, "Nidalee is a BbChampionModule in production (phase 3, 19 October 2026)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Passive: Prowl and the ally experience
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-nidalee-passive-prowl-speeds-her-by-15-percent-in-brush-and-changes-the-run-clip")]
    public static void ProwlIsAScriptBuffRenewedWhileSheStandsInBrush()
    {
        var prowl = Script("Prowl");
        TestAssert.Near(0.15, Blocks(prowl, "OnBuffActivate").First(item => item.Function == "BBIncStat").Number("Delta")!.Value, 1e-12,
            "Prowl.luaobj: +15 percent movement speed");
        TestAssert.Equal("Run2", Blocks(prowl, "OnBuffActivate").First(item => item.Function == "BBOverrideAnimation").Text("OverrideAnim"), "the run clip");
        using var match = new Match("Nidalee");
        match.Advance(0.3);
        var open = match.Hero.Speed;
        TestAssert.True(Of(match.Hero, "Prowl") is null, "no Prowl in the open");
        match.Hero.Position = FindBrush(match);
        match.Advance(1.0);
        TestAssert.True(Of(match.Hero, "Prowl") is { Type: BuffType.Aura }, "Prowl is an aura buff while she stands in brush (CharScriptNidalee.UpdateSelfBuffActions)");
        TestAssert.Near(open * 1.15, match.Hero.Speed, 0.5, "the +15 percent additive speed");
        TestAssert.True(match.Hero.ScriptAnimationOverrides.TryGetValue("run", out var clip) && clip == "Run2", "the run clip is Run2");
        match.Advance(2.0);
        TestAssert.True(Of(match.Hero, "Prowl") is { } renewed && renewed.ExpiresAt > 1.0, "the buff is renewed while she stays");
        match.Hero.Position = new Position2(7000, 7000);
        match.Advance(3.0);
        TestAssert.True(Of(match.Hero, "Prowl") is null && match.Hero.ScriptAnimationOverrides.Count == 0, "two seconds after she left the brush the buff and the clip are gone");
        TestAssert.Near(open, match.Hero.Speed, 0.5, "and the speed with them");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, Failures(match));
    }

    [Check("scripting.bb-nidalee-passive-gives-lower-level-allies-within-500-five-experience-every-five-seconds")]
    public static void AllyExperienceEveryFiveSeconds()
    {
        var script = CharScript("Nidalee");
        var periodic = Blocks(script, "UpdateSelfBuffActions").First(item => item.Function == "BBExecutePeriodically");
        TestAssert.Near(5.0, periodic.Number("TimeBetweenExecutions")!.Value, 1e-12, "every five seconds");
        TestAssert.Near(5.0, Blocks(script, "UpdateSelfBuffActions").First(item => item.Function == "BBIncExp").Number("Delta")!.Value, 1e-12, "five experience");
        using var match = new Match("Nidalee");
        match.Hero.Level = 6;
        match.Ally.Level = 2;
        match.Advance(0.5);
        var before = TotalExperience(match.Ally);
        match.Advance(5.0);
        var gained = TotalExperience(match.Ally) - before;
        TestAssert.True(gained is >= 5.0 and < 11.0, $"a lower-level ally within 500 gets 5 every 5 seconds: {gained}");
        match.Ally.Position = new Position2(9000, 7000);
        match.Advance(0.5);
        before = TotalExperience(match.Ally);
        match.Advance(6.0);
        TestAssert.Near(0.0, TotalExperience(match.Ally) - before, 1e-9, "an ally beyond 500 gets none");
        match.Ally.Position = new Position2(7150, 7000);
        match.Ally.Level = 6;
        before = TotalExperience(match.Ally);
        match.Advance(6.0);
        TestAssert.Near(0.0, TotalExperience(match.Ally) - before, 1e-9, "an ally of her own level gets none");

        // NOT RETAIL: owner decision 20 Oct 2026, "dead champions' passives stop" — while she is dead her passive gives no
        // experience. Her script has no CO_IS_NOT_DEAD of its own, so the shipped BBIncExp would pay a corpse's allies
        // (batch 108's Reconstruction polled a corpse's character hooks); BBIncExp refuses a dead owner. Her own poll keeps
        // running for the corpse — the engine stops only the ally-affecting passives. The same rule is what the retail
        // footage of Zilean's passive leaving his allies while he is dead shows
        // (bb-zilean-heightened-learning-gives-allied-heroes-eight-percent-experience), over there the script's own
        // Owner IS_NOT_DEAD test is what stops the renewal.
        match.Ally.Level = 2;
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        TestAssert.False(match.Hero.Alive, "Nidalee died");
        match.Advance(0.5);
        before = TotalExperience(match.Ally);
        match.Advance(6.0);
        TestAssert.Near(0.0, TotalExperience(match.Ally) - before, 1e-9, "a dead Nidalee's passive gives no experience");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, Failures(match));
    }

    private static double TotalExperience(Entity hero) => hero.Experience;

    // ------------------------------------------------------------------------------------------------------------
    // Q: Javelin Toss
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-nidalee-q-javelin-toss-deals-the-rank-plus-65-percent-ap-times-the-distance-factor-and-ends-at-the-first-unit")]
    public static void JavelinTossScalesWithDistanceAndEndsAtTheFirstHit()
    {
        TestAssert.Equal([55.0, 95.0, 140.0, 185.0, 230.0], ByLevel("JavelinToss", "SelfExecute", "BaseDamage"), "JavelinToss.luaobj: the base damage by rank");
        var self = Blocks(Script("JavelinToss"), "SelfExecute");
        TestAssert.Near(0.65, self.First(item => item.Function == "BBMath" && item.Text("DestVar") == "APDamage").Number("Src2Value")!.Value, 1e-12, "65 percent ability power");
        var target = Blocks(Script("JavelinToss"), "TargetExecute").ToList();
        TestAssert.Near(1000.0, target.First(item => item.Function == "BBMath" && item.Text("DestVar") == "Multiplicant").Number("Src2Value")!.Value, 1e-12, "one more per 1000 units");
        TestAssert.Near(2.5, target.First(item => item.Function == "BBMath" && (item.Param("MathOp") as BbValue.Reference)?.Name == "MO_MIN").Number("Src2Value")!.Value, 1e-12, "capped at 2.5");
        TestAssert.True(target.Any(item => item.Function == "BBDestroyMissile"), "the missile ends at the hit");

        using var match = new Match("Nidalee");
        match.Learn(3);
        match.GiveAbilityPower();
        match.Refill();
        match.Foe.Position = new Position2(7900, 7000);
        var behind = Enemy(match, "Behind", new Position2(8100, 7000), UnitType.Minion);
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(0, at: new Position2(8500, 7000)), "Javelin Toss");
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press.
        match.Advance(match.CastFrameOf(0));
        TestAssert.Near(mana - 70.0, match.Hero.Mana, 1.0, "70 mana at rank 3");
        TestAssert.Near(6.0, match.Hero.SpellCooldowns[0], 0.1, "six seconds");
        match.Advance(1.5 - match.CastFrameOf(0));
        var hit = HitsOn(match, "Foe").Single();
        // BBDistanceBetweenObjects is the gap between the two bodies (batch 112: the client helper subtracts both radii), 900 centre to centre here.
        var gap = Between(match.Hero.Position, match.Foe.Position) - match.Hero.CombatRadius - match.Foe.CombatRadius;
        var factor = Math.Min(2.5, 1.0 + gap / 1000.0);
        TestAssert.True(gap is > 700.0 and < 900.0, $"the gap is the 900 units less the two radii: {gap}");
        NearF32((140.0 + 0.65 * match.Hero.AbilityPower) * factor, hit.Raw, "rank 3, the distance factor 1 + gap / 1000");
        TestAssert.Equal(0, HitsOn(match, "Behind").Count, "the unit behind is not hit: the javelin ended at the first one");
        TestAssert.True(match.Log.EffectCalls.Any(call => call.Name.Equals("nidalee_javelinToss_tar.troy", StringComparison.OrdinalIgnoreCase) && call.Bind == match.Foe.NetId),
            "nidalee_javelinToss_tar is bound to the unit it hit");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // W: Bushwhack
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-nidalee-w-bushwhack-sets-a-trap-that-arms-at-0-9-seconds-and-springs-on-an-enemy-within-150")]
    public static void BushwhackTrapSpringsAndTheDebuffRunsItsPulses()
    {
        TestAssert.Equal([-0.2, -0.25, -0.3, -0.35, -0.4], ByLevel("BushwhackDebuff", "OnBuffActivate", "Debuff"), "BushwhackDebuff.luaobj: the armor and magic resistance reduction");
        TestAssert.Equal([20.0, 31.25, 42.5, 53.75, 65.0], ByLevel("BushwhackDebuff", "OnBuffActivate", "DamagePerTick"), "four pulses sum to the inibin's 80 to 260");
        var trapScript = Script("Bushwhack");
        TestAssert.Near(0.9, Blocks(trapScript, "BuffOnUpdateActions").First(item => item.Function == "BBExecutePeriodically").Number("TimeBetweenExecutions")!.Value, 1e-12, "armed after 0.9 s");
        TestAssert.Near(150.0, Blocks(trapScript, "BuffOnUpdateActions").First(item => item.Function == "BBForEachUnitInTargetArea").Number("Range")!.Value, 1e-12, "springs within 150");
        TestAssert.Near(240.0, Blocks(trapScript, "SelfExecute").First(item => item.Function == "BBSpellBuffAdd").Number("Duration")!.Value, 1e-12, "lives 240 seconds");

        using var match = new Match("Nidalee");
        match.Learn(3);
        match.GiveAbilityPower();
        match.Refill();
        match.Foe.Position = new Position2(7800, 7500);
        var armor = match.Foe.Armor;
        var resistance = match.Foe.SpellBlock;
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(1, at: new Position2(7600, 7000)), "Bushwhack");
        match.Advance(0.4);
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), CastFrameOf after the press.
        TestAssert.Near(mana - 90.0, match.Hero.Mana, 1.0, "90 mana at rank 3");
        TestAssert.Near(18.0, match.Hero.SpellCooldowns[1] + 0.4 - match.CastFrameOf(1), 0.1, "18 seconds");
        var trap = match.Runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.NidaleeTrap && unit.OwnerNetId == match.Hero.NetId);
        TestAssert.True(trap.Team == match.Hero.Team && trap.Alive, "her trap");
        TestAssert.Near(0.0, Between(trap.Position, new Position2(7600, 7000)), 5.0, "at the cast point");
        TestAssert.True(Of(trap, "Bushwhack") is { } buff && buff.ExpiresAt is > 239.0 and <= 240.0, "the trap holds the Bushwhack buff for 240 seconds");
        TestAssert.True(trap.Buffs.Any(item => item.Payload.Invulnerable) && trap.Buffs.Any(item => item.Payload.Untargetable), "invulnerable and untargetable");
        // The placement sight is gone (batch 221): Bushwhack's BBSpawnMinion passes VisibilitySize 0 (see the placement check above).
        TestAssert.False(match.Runtime.World.TimedVisionBubbles.Any(bubble => bubble.AnchorNetId == trap.NetId),
            "no sight is anchored to the trap once it is placed");

        match.Foe.Position = new Position2(7650, 7000);
        match.Advance(0.3);
        TestAssert.True(Of(match.Foe, "BushwhackDebuff") is null && trap.Alive, "before 0.9 s the trap is not armed");
        match.Advance(0.5);
        var debuff = Of(match.Foe, "BushwhackDebuff");
        TestAssert.True(debuff is not null && debuff.CasterNetId == match.Hero.NetId, "the armed trap puts her debuff on the enemy within 150");
        TestAssert.Near(12.0, debuff!.ExpiresAt, 0.6, "for twelve seconds");
        TestAssert.False(trap.Alive, "the trap is spent: its buff ends and its 4000-damage hit on itself kills it");
        NearF32(armor * 0.7, match.Foe.Armor, "rank 3: 30 percent less armor");
        NearF32(resistance * 0.7, match.Foe.SpellBlock, "and 30 percent less magic resistance");
        TestAssert.Equal(1, match.Runtime.World.TimedVisionBubbles.Count(bubble => bubble.AnchorNetId == match.Foe.NetId && bubble.Team == match.Hero.Team && bubble.Radius == 400.0),
            "the trapper's team sees the victim within 400 while the debuff lasts (BBAddUnitPerceptionBubble)");
        match.Advance(3.0);
        var pulses = HitsOn(match, "Foe").Where(item => item.Kind == "SpellPersist" || item.Raw is > 40.0 and < 70.0).ToList();
        TestAssert.Equal(4, pulses.Count, "four pulses");
        foreach (var pulse in pulses) NearF32(42.5 + 0.1 * match.Hero.AbilityPower, pulse.Raw, "each pulse: 42.5 + 10 percent of ability power at rank 3");
        TestAssert.True(Of(match.Foe, "BushwhackDebuff") is not null, "the debuff goes on for twelve seconds");
        match.Advance(10.0);
        TestAssert.True(Of(match.Foe, "BushwhackDebuff") is null, "and ends");
        TestAssert.Equal(0, match.Runtime.World.TimedVisionBubbles.Count(bubble => bubble.AnchorNetId == match.Foe.NetId && bubble.Team == match.Hero.Team),
            "with its sight (BBRemovePerceptionBubble)");
        NearF32(armor, match.Foe.Armor, "the armor is back");
        TestAssert.True(match.Hero.MatchStatistics.TotalDamageDealt < 1000.0,
            $"the trap's 4000-damage hit on itself is no damage dealt by its owner: {match.Hero.MatchStatistics.TotalDamageDealt}");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, Failures(match));
    }

    [Check("scripting.bb-nidalee-w-trap-marker-is-green-for-the-trapper-and-empty-for-the-others")]
    public static void TheTrapMarkerEffectIsATeamColourPair()
    {
        var create = Blocks(Script("Bushwhack"), "BuffOnUpdateActions").First(item =>
            item.Function == "BBSpellEffectCreate" && item.Text("EffectName") == "nidalee_trap_team_id_green.troy");
        TestAssert.Equal("empty.troy", create.Text("EffectNameForOtherTeam"), "Bushwhack.luaobj: the other team sees empty.troy");
        using var match = new Match("Nidalee");
        match.Learn(3);
        match.Refill();
        TestAssert.True(match.Cast(1, at: new Position2(7600, 7000)), "Bushwhack");
        match.Advance(1.5);
        var marker = match.Log.EffectCalls.Single(call => call.Name.Equals("nidalee_trap_team_id_green.troy", StringComparison.OrdinalIgnoreCase));
        TestAssert.Equal("empty.troy", marker.EnemyName, "the effect carries its enemy variant");
        var trap = match.Runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.NidaleeTrap);
        TestAssert.Equal(trap.NetId, marker.Bind, "bound to the trap");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, Failures(match));
    }

    [Check("scripting.bb-bushwhack-places-the-trap-without-a-sight-flash")]
    public static void BushwhackGivesThePlacementNoSight()
    {
        // The shipped Bushwhack passes VisibilitySize 0 and no sight spell (DATA/Spells/Bushwhack.luaobj, BBSpawnMinion table): the trap
        // is placed in the dark, with no timed vision bubble anchored to it.
        using var match = new Match("Nidalee");
        match.Learn(3);
        match.Refill();
        match.Foe.Position = new Position2(7900, 7000);
        TestAssert.True(match.Cast(1, at: new Position2(7600, 7000)), "Bushwhack");
        // Step until the trap appears: a flash lasts 0.25 s, so the first step that shows the trap still shows any sight its placement added.
        bool Placed() => match.Runtime.World.Entities.Values.Any(unit => unit.Kind == EntityKind.NidaleeTrap && unit.OwnerNetId == match.Hero.NetId);
        for (var step = 0; step < 60 && !Placed(); step++) match.Advance(0.05);
        TestAssert.True(Placed(), "the trap is placed");
        var trap = match.Runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.NidaleeTrap && unit.OwnerNetId == match.Hero.NetId);
        TestAssert.False(match.Runtime.World.TimedVisionBubbles.Any(bubble => bubble.AnchorNetId == trap.NetId),
            "no sight is anchored to the trap in its placement");
    }

    [Check("scripting.bb-nidalee-w-trap-is-stealthed-from-enemies-until-true-sight")]
    public static void TheTrapIsHiddenFromTheEnemyUntilTrueSightRevealsIt()
    {
        // NOT RETAIL, owner request of 19 October 2026: the shipped scripts add no stealth to Nidalee's Noxious Trap; BBSpawnMinion gives that trap (Skin
        // Nidalee_Spear) the state of the shared Stealth buff (BbNativeHandlers.TrapUnitName), as Teemo's mushroom and Shaco's box have it from their own
        // scripts. Caitlyn's trap is not stealthed (owner decision, 28 October 2026: BbCaitlynChecks).
        using var match = new Match("Nidalee");
        match.Learn(3);
        match.Refill();
        match.Foe.Position = new Position2(7900, 7000);
        TestAssert.True(match.Cast(1, at: new Position2(7600, 7000)), "Bushwhack");
        match.Advance(1.0);
        var trap = match.Runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.NidaleeTrap && unit.OwnerNetId == match.Hero.NetId);
        TestAssert.True(trap.Alive && trap.ScriptStatus is not null, "her trap holds the spawn's status bits (batch 241: no flags buff)");
        TestAssert.True(trap.ScriptStatus!.Payload.Stealthed, "its script status holds SetStealthed (GetStealthed)");
        TestAssert.True(VisibilityRules.IsStealthed(trap), "the sight rule reads it");
        TestAssert.False(match.Runtime.Spells.CanSeeTarget(match.Foe, trap), "an enemy 300 units away does not see it");
        TestAssert.True(match.Runtime.Spells.CanSeeTarget(match.Ally, trap) && match.Runtime.Spells.CanSeeTarget(match.Hero, trap), "her own team does");
        match.Runtime.World.AddTimedVisionBubble(match.Foe.Team, trap.Position, 300.0, 2.0, revealStealth: true);
        match.Advance(0.2);
        TestAssert.True(match.Runtime.Spells.CanSeeTarget(match.Foe, trap), "true sight (a bubble that reveals stealth) shows it to the enemy");
        match.Advance(2.5);
        TestAssert.False(match.Runtime.Spells.CanSeeTarget(match.Foe, trap), "and it is hidden again when the sight ends");
        TestAssert.True(Of(match.Foe, "BushwhackDebuff") is null && trap.Alive, "nobody stepped on it");
        match.Foe.Position = trap.Position;
        match.Advance(0.5);
        TestAssert.True(Of(match.Foe, "BushwhackDebuff") is not null && !trap.Alive, "stepping on it springs it as before");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, Failures(match));
    }

    [Check("scripting.bb-nidalee-w-trap-outlives-its-owner-and-ends-after-240-seconds")]
    public static void TheTrapStaysWhenItsOwnerDiesAndEndsAtTheBuffsEnd()
    {
        using var match = new Match("Nidalee");
        match.Learn(3);
        match.Refill();
        TestAssert.True(match.Cast(1, at: new Position2(7600, 7000)), "Bushwhack");
        match.Advance(1.0);
        var trap = match.Runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.NidaleeTrap);
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        match.Advance(2.0);
        TestAssert.True(trap.Alive, "a trap is not a pet: it stays when its owner dies");
        match.Advance(238.0);
        TestAssert.False(trap.Alive, "240 seconds after it was set the buff ends and the trap dies");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // E: Primal Surge
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-nidalee-e-primal-surge-heals-and-raises-the-allys-attack-speed-for-seven-seconds")]
    public static void PrimalSurgeHealsAndHastens()
    {
        TestAssert.Equal([0.2, 0.3, 0.4, 0.5, 0.6], ByLevel("PrimalSurge", "TargetExecute", "ASMod"), "PrimalSurge.luaobj: the attack speed by rank");
        TestAssert.Equal([50.0, 85.0, 120.0, 155.0, 190.0], ByLevel("PrimalSurge", "TargetExecute", "HealthToRestore"), "the heal by rank");
        using var match = new Match("Nidalee");
        match.Learn(3);
        match.GiveAbilityPower();
        match.Refill();
        match.Ally.Health = 100.0; // below the maximum: 100 + 176 stays under the 424 of a level-one Annie
                var speed = match.Ally.AttackSpeedMod;
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(2, match.Ally), "Primal Surge on the ally");
        match.Advance(0.5);
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press.
        TestAssert.Near(mana - 100.0, match.Hero.Mana, 1.0, "100 mana at rank 3");
        TestAssert.Near(100.0 + 120.0 + 0.7 * match.Hero.AbilityPower, match.Ally.Health, 1.0, "120 + 70 percent of ability power (and the half second of regeneration)");
        TestAssert.True(Of(match.Ally, "PrimalSurge") is { } buff && buff.ExpiresAt is > 6.0 and <= 7.0, "the buff lasts seven seconds");
        TestAssert.Near(speed + 0.4, match.Ally.AttackSpeedMod, 0.01, "+40 percent attack speed at rank 3");
        match.Advance(7.0);
        TestAssert.True(Of(match.Ally, "PrimalSurge") is null, "it ends");
        TestAssert.Near(speed, match.Ally.AttackSpeedMod, 0.01, "with its speed");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // R: the cougar form
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-nidalee-r-aspect-of-the-cougar-pushes-the-cougar-saves-the-human-cooldowns-and-gives-the-form-its-numbers")]
    public static void TheFormIsAPushedCharacterWithItsOwnSpells()
    {
        TestAssert.Equal([10.0, 20.0, 30.0], ByLevel("AspectOfTheCougar", "TargetExecute", "armorMod"), "AspectOfTheCougar.luaobj: armor and magic resistance by rank");
        TestAssert.Equal([40.0, 70.0, 100.0], CharByLevel("CharOnLevelUpSpell", "TakedownDamage"), "CharScriptNidalee: Takedown by the ultimate's rank");
        TestAssert.Equal([125.0, 175.0, 225.0], CharByLevel("CharOnLevelUpSpell", "PounceDamage"), "Pounce");
        TestAssert.Equal([150.0, 225.0, 300.0], CharByLevel("CharOnLevelUpSpell", "SwipeDamage"), "Swipe");
        using var match = new Match("Nidalee");
        LearnWithTheUltimate(match, 3);
        match.Refill();
        var human = (Speed: match.Hero.Speed, Range: match.Hero.AttackRange, Armor: match.Hero.Armor, Resistance: match.Hero.SpellBlock);
        match.Hero.SpellCooldowns[0] = 30.0;
        match.Hero.SpellCooldowns[1] = 40.0;
        match.Hero.SpellCooldowns[2] = 50.0;
        var entered = match.Now;
        TestAssert.True(match.Cast(3), "Aspect of the Cougar");
        TestAssert.Near(0.0, match.Hero.Mana - match.Hero.MaxMana, 1e-6, "no mana");
        match.Advance(0.5);
        TestAssert.True(Of(match.Hero, "AspectOfTheCougar") is { Type: BuffType.Internal }, "the form buff");
        TestAssert.True(match.Hero.ScriptCharacterData is { SkinName: "Nidalee_Cougar", OverrideSpells: true }, "the pushed character layer carries the form's spells");
        TestAssert.Equal(["Takedown", "Pounce", "Swipe", "AspectOfTheCougar"], match.Hero.ActiveSpellNames.Take(4).ToArray(), "the four spells of the form");
        var cougar = GameData.Default.StatsAtLevel(GameData.Default.Champion("Nidalee_Cougar"), match.Hero.Level);
        TestAssert.Near(cougar.MoveSpeed, match.Hero.Speed, 0.5, "the cougar's movement speed");
        TestAssert.Near(100.0, match.Hero.AttackRange, 1e-6, "melee range");
        TestAssert.Near(0.0, match.Hero.MissileSpeed, 1e-9, "the cougar's attack has no missile (Nidalee_CougarBasicAttack is CastType 0)");
        TestAssert.Near(human.Armor + 30.0, match.Hero.Armor - (cougar.Armor - GameData.Default.StatsAtLevel(GameData.Default.Champion("Nidalee"), match.Hero.Level).Armor), 0.5,
            "30 armor at rank 3");
        TestAssert.Equal(0.0, match.Runtime.HeroStats.Snapshot(match.Hero).ManaCosts.Take(3).Sum(), "the form's three spells cost nothing");
        TestAssert.True(match.Hero.SpellCooldowns[0] == 0.0 && match.Hero.SpellCooldowns[1] == 0.0 && match.Hero.SpellCooldowns[2] == 0.0,
            "the spellbook started again: the form's spells are ready");
        TestAssert.True(match.Hero.SpellCooldowns[3] is > 3.0 and <= 4.0, $"the ultimate is on its four seconds: {match.Hero.SpellCooldowns[3]}");

        match.Advance(4.0);
        TestAssert.True(match.Cast(3), "the second cast leaves the form");
        match.Advance(0.5);
        var lived = match.Now - entered;
        TestAssert.True(Of(match.Hero, "AspectOfTheCougar") is null && match.Hero.ScriptCharacterData is null, "the form is over");
        TestAssert.True(match.Hero.ActiveSpellNames.Take(4).All(string.IsNullOrEmpty), "the human spells are back");
        TestAssert.Near(human.Speed, match.Hero.Speed, 0.5, "her speed");
        TestAssert.Near(human.Range, match.Hero.AttackRange, 1e-6, "and her range");
        TestAssert.Near(1750.0, match.Hero.MissileSpeed, 1e-6, "and her javelin attack");
        TestAssert.Near(30.0 - lived, match.Hero.SpellCooldowns[0], 1.0, "the human cooldowns ran down while she was the cougar (LifeTime)");
        TestAssert.Near(40.0 - lived, match.Hero.SpellCooldowns[1], 1.0, "all three");
        TestAssert.Near(50.0 - lived, match.Hero.SpellCooldowns[2], 1.0, "of them");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, Failures(match));
    }

    [Check("scripting.bb-nidalee-r-the-form-stays-through-death")]
    public static void TheFormPersistsThroughDeath()
    {
        TestAssert.True(Script("AspectOfTheCougar").Global("PersistsThroughDeath") is BbValue.Bool { Value: true }, "AspectOfTheCougar.luaobj: PersistsThroughDeath");
        using var match = new Match("Nidalee");
        LearnWithTheUltimate(match, 3);
        Transform(match);
        match.Refill();
        TestAssert.True(match.Cast(0), "Takedown");
        match.Advance(0.5);
        TestAssert.True(Of(match.Hero, "Takedown") is not null, "armed");
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        match.Advance(0.3);
        TestAssert.True(Of(match.Hero, "AspectOfTheCougar") is not null && match.Hero.ScriptCharacterData is { SkinName: "Nidalee_Cougar" },
            "the form and its character layer outlive the death");
        TestAssert.True(Of(match.Hero, "Takedown") is null && match.Hero.ScriptAttackOverride is null, "the armed attack does not");
        TestAssert.False(match.Hero.SpellSeals.IsSealed(false, 0), "its deactivation opened the Q slot");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // The form's spells
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-nidalee-cougar-q-takedown-arms-an-attack-that-grows-with-the-targets-missing-health")]
    public static void TakedownIsAScriptAttackOverride()
    {
        var attack = Blocks(Script("NidaleeTakedownAttack"), "TargetExecute").ToList();
        TestAssert.Near(2.0, attack.First(item => item.Function == "BBMath" && item.Text("DestVar") == "bonusPercent" && item.Number("Src1Value") == 2.0).Number("Src1Value")!.Value, 1e-12,
            "NidaleeTakedownAttack.luaobj: twice the missing health fraction");
        TestAssert.True(attack.Any(item => item.Function == "BBApplyDamage" && item.Text("IgnoreDamageCrit") == null && item.Param("IgnoreDamageCrit") is BbValue.Bool { Value: true }),
            "the empowered hit ignores crits");
        using var match = new Match("Nidalee");
        LearnWithTheUltimate(match, 3);
        Transform(match);
        match.Refill();
        match.Foe.Position = new Position2(7100, 7000);
        match.Foe.Health = match.Foe.MaxHealth * 0.5;
        match.Foe.HealthRegen = 0.0; // the missing health the formula reads is the one at the hit
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Cast(0), "Takedown");
        match.Advance(0.3);
        TestAssert.True(Of(match.Hero, "Takedown") is { } buff && buff.ExpiresAt is > 9.0 and <= 10.0, "the buff lasts ten seconds");
        TestAssert.True(match.Hero.SpellSeals.IsSealed(false, 0), "the Q slot is sealed while it is armed");
        TestAssert.True(match.Hero.ScriptAttackOverride is { SpellName: "NidaleeTakedownAttack", Slot: 42, OriginSlot: 0 }, "her attack is the extra spell");
        TestAssert.True(match.Hero.DodgePiercing, "and it pierces dodge");
        TestAssert.False(match.Cast(0), "it cannot be armed twice");
        var bonus = 100.0;
        var attackDamage = match.Hero.AttackDamage;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "the empowered attack");
        match.Advance(1.2);
        var hit = HitsOn(match, "Foe").Single(item => item.Kind == "Basic");
        NearF32((attackDamage + bonus) * (1.0 + 2.0 * 0.5), hit.Raw, "rank 3: (attack damage + 100) x (1 + 2 x missing health)");
        TestAssert.True(Of(match.Hero, "Takedown") is null && match.Hero.ScriptAttackOverride is null && !match.Hero.SpellSeals.IsSealed(false, 0), "the hit consumes it");
        TestAssert.Near(5.0, match.Hero.SpellCooldowns[0], 1.0, "and the five-second cooldown starts");
        TestAssert.False(match.Hero.DodgePiercing, "dodge piercing ends with it");
        TestAssert.True(match.Log.EffectCalls.Any(call => call.Name.Equals("nidalee_cougar_takedown_tar.troy", StringComparison.OrdinalIgnoreCase) && call.Bind == match.Foe.NetId),
            "the hit effect is on the target");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, Failures(match));
    }

    /// <summary>
    /// Owner finding 31 (batch 178): "Cougar Q: she can't move for a moment after hitting an enemy." The empowered swing leaves as a cast of
    /// <c>NidaleeTakedownAttack</c> (the client plays an attack's own hit and sounds only from a cast flagged as one), and that inibin carries
    /// <c>CantCancelWhileChanneling</c> — but no <c>ChannelDuration</c>: the client's channel starts from its spell data's channel field, which it loads
    /// from the shipped script's global (<c>ExecuteCastFrame</c>), not from the spell data. So the swing is only a
    /// windup and a click inside it is accepted at the cast frame; the server must not hold her for the swing's <c>DesignerTotalTime</c>.
    /// </summary>
    [Check("scripting.bb-nidalee-cougar-q-takedown-leaves-her-free-to-move-at-the-swing-cast-frame")]
    public static void TakedownDoesNotHoldHerThroughTheSwing()
    {
        TestAssert.True(Script("NidaleeTakedownAttack").Global("ChannelDuration") is null,
            "NidaleeTakedownAttack.luaobj has no ChannelDuration: the swing is no channel on the client");
        using var match = new Match("Nidalee");
        LearnWithTheUltimate(match, 3);
        Transform(match);
        match.Refill();
        match.Hero.Waypoints.Clear();
        match.Hero.AttackTarget = null;
        match.Foe.Position = new Position2(7100, 7000);
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Cast(0), "Takedown");
        match.Advance(0.3);
        match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe);
        // The swing is a real cast (its own windup), so the hold the finding reports is the channel the sink used to start from the
        // packet's total time. Sample it: a run in which the swing never goes out would leave no windup at all and must not pass here.
        var peakWindup = 0.0;
        for (var step = 0; step < 30; step++)
        {
            match.Advance(1.0 / 30.0);
            peakWindup = Math.Max(peakWindup, match.Hero.CastWindupRemaining);
        }
        TestAssert.True(peakWindup > 0.05, $"the empowered swing leaves as a cast with its own windup ({peakWindup:0.000} s)");
        TestAssert.True(match.Hero.CastWindupRemaining <= 0.0,
            $"the empowered swing starts no client channel, so no order is held after it ({match.Hero.CastWindupRemaining:0.000} s)");
        var participant = match.Runtime.Session.Participants[0];
        var destination = match.Hero.Position + new Position2(0, 300);
        TestAssert.True(match.Runtime.Dependencies.Gameplay(new SessionGameplayContext(participant, match.Hero, 1,
            Amberline.Network.Protocol.ClientGameplayPackets.MoveOrder(match.Hero.NetId, 2, destination).Plaintext, 0u, null, match.Hero)),
            "the click is accepted");
        TestAssert.True(match.Hero.Waypoints.Count > 1, "and installs her route (she is not held)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, Failures(match));
    }

    [Check("scripting.bb-engine-an-attack-override-that-ignores-crits-is-rolled-without-a-critical-chance")]
    public static void TakedownNeverCrits()
    {
        using var match = new Match("Nidalee");
        LearnWithTheUltimate(match, 3);
        Transform(match);
        match.Refill();
        match.Foe.Position = new Position2(7100, 7000);
        match.Hero.CritChance = 1.0;
        var sure = new AttackRolls(0.0, 1.0, 1.0);
        TestAssert.True(match.Runtime.Combat.BasicAttackDamage(match.Hero, match.Foe, sure).Critical, "a plain attack with a certain critical chance crits");
        TestAssert.True(match.Cast(0), "Takedown");
        match.Advance(0.3);
        TestAssert.False(match.Runtime.Combat.BasicAttackDamage(match.Hero, match.Foe, sure).Critical,
            "the armed attack passes IgnoreDamageCrit, so the swing is rolled without a critical chance");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, Failures(match));
    }

    [Check("scripting.bb-nidalee-cougar-w-pounce-jumps-375-and-hurts-everything-within-225-on-landing")]
    public static void PounceLandsAndDamagesTheUnitsAround()
    {
        var pounce = Script("Pounce");
        TestAssert.Near(375.0, Blocks(pounce, "TargetExecute").First(item => item.Function == "BBGetPointByUnitFacingOffset").Number("Distance")!.Value, 1e-12, "375 along her facing");
        TestAssert.Near(225.0, Blocks(pounce, "BuffOnMoveSuccess").First(item => item.Function == "BBForEachUnitInTargetArea").Number("Range")!.Value, 1e-12, "225 around the landing");
        TestAssert.Near(900.0, Blocks(pounce, "OnBuffActivate").First(item => item.Function == "BBMove").Number("Speed")!.Value, 1e-12, "at 900");
        using var match = new Match("Nidalee");
        LearnWithTheUltimate(match, 3);
        match.GiveAbilityPower();
        Transform(match);
        match.Refill();
        match.Hero.Facing = new Position2(1.0, 0.0);
        match.Foe.Position = new Position2(7300, 7000);
        var start = match.Hero.Position;
        var far = Enemy(match, "Far", new Position2(7000, 8000), UnitType.Minion);
        TestAssert.True(match.Cast(1, at: new Position2(7300, 7000)), "Pounce");
        match.Advance(0.2);
        TestAssert.True(match.Hero.ForcedMove is not null && Of(match.Hero, "Pounce") is not null, "the jump is a forced move with its buff");
        match.Advance(0.8);
        TestAssert.True(Between(start, match.Hero.Position) is > 300.0 and <= 376.0, $"she jumped as far as the floor allows, at most 375: {Between(start, match.Hero.Position)}");
        TestAssert.True(match.Hero.ForcedMove is null && Of(match.Hero, "Pounce") is null, "the jump and its buff are over");
        var hit = HitsOn(match, "Foe").Single();
        NearF32(225.0 + 0.4 * match.Hero.AbilityPower, hit.Raw, "rank 3: 225 + 40 percent of ability power on landing");
        TestAssert.Equal(0, HitsOn(match, "Far").Count, "a unit 1000 away is not hit");
        TestAssert.Near(3.5 - 1.0, match.Hero.SpellCooldowns[1], 0.15, "the cooldown is 3.5 seconds, one of them spent in the jump");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, Failures(match));
    }

    [Check("scripting.bb-nidalee-cougar-e-swipe-hits-the-units-in-front-after-turning-to-the-aim")]
    public static void SwipeKeepsTheUnitsInFrontOfTheAim()
    {
        TestAssert.True(Blocks(Script("Swipe"), "TargetExecute").Any(item => item.Function == "BBIf" && item.Param("CompareOp") is BbValue.Reference { Name: "CO_IS_TARGET_IN_FRONT_OF_ME" }),
            "Swipe.luaobj keeps the units in front of the caster");
        using var match = new Match("Nidalee");
        LearnWithTheUltimate(match, 3);
        match.GiveAbilityPower();
        Transform(match);
        match.Refill();
        match.Hero.Facing = new Position2(1.0, 0.0);
        match.Foe.Position = new Position2(7200, 7000);
        var west = Enemy(match, "West", new Position2(6800, 7000), UnitType.Minion);
        TestAssert.True(match.Cast(2, at: new Position2(6000, 7000)), "Swipe aimed west");
        match.Advance(1.0);
        TestAssert.True(match.Hero.Facing.X < -0.9, "she turned to the aim");
        NearF32(300.0 + 0.4 * match.Hero.AbilityPower, HitsOn(match, "West").Single().Raw, "rank 3: 300 + 40 percent of ability power on the unit in front");
        TestAssert.Equal(0, HitsOn(match, "Foe").Count, "the unit behind her is not hit");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, Failures(match));
    }

    [Check("scripting.bb-nidalee-death-recap-names-the-spell-the-slot-holds-now")]
    public static void TheRecapNamesTheFormsSpells()
    {
        using var match = new Match("Nidalee");
        LearnWithTheUltimate(match, 3);
        match.Hero.SpellRanks[0] = match.Hero.SpellRanks[1] = match.Hero.SpellRanks[2] = 5;
        TestAssert.Equal(Amberline.Network.Protocol.ProtocolEncoding.HashString("Bushwhack"), DeathRecapOrigin.ChampionSpell(match.Hero, 1, 5).SourceScriptNameHash,
            "in human form slot W is Bushwhack");
        Transform(match);
        var pounce = DeathRecapOrigin.ChampionSpell(match.Hero, 1, 5);
        TestAssert.Equal(Amberline.Network.Protocol.ProtocolEncoding.HashString("Pounce"), pounce.SourceScriptNameHash, "as the cougar slot W is Pounce");
        TestAssert.Equal((byte)3, pounce.SourceSpellLevel, "the form's spells have three ranks: the slot's five is capped");
        TestAssert.Equal(Amberline.Network.Protocol.ProtocolEncoding.HashString("Takedown"), DeathRecapOrigin.ChampionSpell(match.Hero, 0, 2).SourceScriptNameHash, "Q is Takedown");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Engine rules this conversion added
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-engine-a-units-own-internalraw-hit-ignores-its-invulnerability-and-is-no-damage-dealt")]
    public static void AUnitsSelfKillGetsThroughItsInvulnerability()
    {
        using var match = new Match("Nidalee");
        match.Learn(3);
        match.Refill();
        var unit = Enemy(match, "Wall", new Position2(8000, 8000), UnitType.Minion, match.Hero.Team);
        match.Runtime.Buffs.AddBuff(unit, "TestInvulnerable", BuffType.Internal, 100.0, unit.NetId, new BuffPayload { Invulnerable = true });
        // Batch 227 (owner request, 25 October 2026) let another unit's InternalRaw hit through an invulnerable unit. Batch 230 narrowed that to the
        // fountain shrine (the shipped evidence is the shrine's): any other InternalRaw source is refused by invulnerability again, as before 227.
        // Reconstruction. The raw inputs are unchanged; only the expectation moved.
        var other = Enemy(match, "Wall", new Position2(8100, 8000), UnitType.Minion, match.Hero.Team);
        match.Runtime.Buffs.AddBuff(other, "TestInvulnerable", BuffType.Internal, 100.0, other.NetId, new BuffPayload { Invulnerable = true });
        TestAssert.False(match.Runtime.Combat.ApplyHit(match.Foe.NetId, other.NetId, 4000.0, DamageType.True, DamageSourceKind.InternalRaw), "another unit's InternalRaw hit is refused by the invulnerability");
        TestAssert.True(other.Alive, "so it lives");
        TestAssert.True(unit.Alive, "the self-kill unit lives until its own hit");
        match.Runtime.Combat.ApplyHit(unit.NetId, unit.NetId, 4000.0, DamageType.True, DamageSourceKind.InternalRaw);
        TestAssert.False(unit.Alive, "its own InternalRaw hit kills it");
        TestAssert.Near(0.0, match.Hero.MatchStatistics.TotalDamageDealt, 1e-9, "nobody dealt damage");
    }

    [Check("scripting.bb-engine-a-spell-buff-remove-without-a-name-removes-the-scripts-own-buff")]
    public static void SpellBuffRemoveDefaultsToTheScriptsName()
    {
        var toggle = Blocks(Script("AspectOfTheCougar"), "TargetExecute").First(item => item.Function == "BBSpellBuffRemove");
        TestAssert.True(toggle.Text("BuffName") is null, "AspectOfTheCougar.luaobj removes a buff it does not name");
        using var match = new Match("Nidalee");
        LearnWithTheUltimate(match, 3);
        Transform(match);
        match.Advance(4.0);
        TestAssert.True(match.Cast(3), "the second cast");
        match.Advance(0.5);
        TestAssert.True(Of(match.Hero, "AspectOfTheCougar") is null, "removed");
    }
}
