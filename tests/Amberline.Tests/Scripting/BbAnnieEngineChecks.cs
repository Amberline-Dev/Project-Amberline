using System.Globalization;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;
using Amberline.Simulation.Pets;

namespace Amberline.Tests;

// Engine-level checks of the host, buff runtime and module around the Annie scenarios: what the module binds, the buff type mapping,
// the script buff hooks the Q/W/E scenarios do not reach (R's buff script on a stand-in pet), the R summon (BBSpawnPet, 19 October
// 2026), the spell shield rule and that production runs Annie from her scripts. Shares the harness of BbAnnieDifferentialChecks.
public static partial class BbAnnieDifferentialChecks
{
    [Check("scripting.bb-annie-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosure()
    {
        var module = new BbChampionModule("Annie");
        TestAssert.Equal(18, module.ScriptNames.Count, "the Annie closure is 18 scripts");
        foreach (var name in new[] { "CharScriptAnnie", "Disintegrate", "Incinerate", "MoltenShield", "InfernalGuardian", "Pyromania",
                     "Pyromania_particle", "Pyromania_marker", "InfernalGuardianBurning", "InfernalGuardianTimer" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var hero = new Entity { ChampionName = "Annie" };
        TestAssert.Equal(["Disintegrate", "Incinerate", "MoltenShield", "InfernalGuardian"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from the champion inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "ranks come from the spell inibins");
    }

    private enum ScriptBuffName
    {
        Internal, Aura, CombatEnchancer, CombatDehancer, Stun, Invisibility, Silence, Taunt, Polymorph, Slow, Snare, Damage, Heal,
        Haste, Invulnerability, Sleep, Fear, Net, Poison, Suppression, Blind,
    }

    [Check("scripting.bb-annie-buff-types-follow-the-client-registrations")]
    public static void ScriptBuffTypesMapToTheWireEnum()
    {
        // BbConstants holds the numbers the stock client registers; Core/Buffs/BuffType holds the packet type bytes, the same
        // numbers since the A4 audit (18 October 2026); the two lists agree name by name.
        var pairs = new (ScriptBuffName Script, BuffType Wire)[]
        {
            (ScriptBuffName.Internal, BuffType.Internal), (ScriptBuffName.Aura, BuffType.Aura),
            (ScriptBuffName.CombatEnchancer, BuffType.CombatEnhancer), (ScriptBuffName.CombatDehancer, BuffType.CombatDehancer),
            (ScriptBuffName.Stun, BuffType.Stun), (ScriptBuffName.Invisibility, BuffType.Invisibility),
            (ScriptBuffName.Silence, BuffType.Silence), (ScriptBuffName.Taunt, BuffType.Taunt),
            (ScriptBuffName.Polymorph, BuffType.Polymorph), (ScriptBuffName.Slow, BuffType.Slow), (ScriptBuffName.Snare, BuffType.Snare),
            (ScriptBuffName.Damage, BuffType.DamageOverTime), (ScriptBuffName.Heal, BuffType.Heal), (ScriptBuffName.Haste, BuffType.Haste),
            (ScriptBuffName.Invulnerability, BuffType.Invulnerability), (ScriptBuffName.Sleep, BuffType.Sleep),
            (ScriptBuffName.Fear, BuffType.Fear), (ScriptBuffName.Net, BuffType.Net), (ScriptBuffName.Poison, BuffType.Poison),
            (ScriptBuffName.Suppression, BuffType.Suppression), (ScriptBuffName.Blind, BuffType.Blind),
        };
        foreach (var (script, wire) in pairs)
        {
            TestAssert.True(BbConstants.TryGetValue("BUFF_" + script, out var number), $"BUFF_{script} is registered");
            TestAssert.Equal(wire, BbBuffRuntime.ServerBuffType(number), $"BUFF_{script}");
        }
    }

    [Check("scripting.bb-annie-production-runs-the-shipped-scripts")]
    public static void ProductionRunsAnnieFromHerScripts()
    {
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default, readySettleSeconds: 0);
        var dispatcher = (ChampionSpellDispatcher)runtime.Dependencies.ChampionDispatcher;
        // Phase 3 (19 October 2026): Annie was the pilot conversion; every other hand-written champion followed (batches 98 to 112, Orianna last),
        // so production registers one BbChampionModule per playable champion and no hand-written module is left.
        TestAssert.Equal(ChampionCatalog.PlayableNames.Order(StringComparer.Ordinal).ToArray(),
            dispatcher.RegisteredChampionNames.Where(name => dispatcher.FindModule(name) is BbChampionModule)
                .Order(StringComparer.Ordinal).ToArray(),
            "the script-driven champions of production are the playable roster");
        TestAssert.Equal(0, dispatcher.RegisteredChampionNames.Count(name => dispatcher.FindModule(name) is not BbChampionModule),
            "no hand-written module is left");
        TestAssert.True(((MatchGameplayHooks)runtime.Dependencies.ChampionHooks).ScriptHooks is null,
            "a match without a script-driven hero installs no sink: the gameplay hooks forward nothing to the script engine");
    }

    [Check("scripting.bb-annie-r-summons-tibbers-from-the-script")]
    public static void RSummonsTibbersAndBurstsFromTheScript()
    {
        // InfernalGuardian: TTYPE_Area, 0.25 s windup, SelfExecute at the cast frame (landing effect, BBSpawnPet, the timer and burning
        // buffs, the Pyromania check and stack), then TargetExecute per unit within 250.3 of the point (stun when armed, then 200/325/450
        // + 0.7 AP). Tibbers: base hit points 1200, damage 80, armor 30 and magic resist 25 plus the script's per-rank bonuses
        // (+0/400/800 hit points through BBIncMaxHealth, +0/25/50 damage in BBSpawnPet, +0/20/40 armor and magic resist in the buff).
        foreach (var rank in new[] { 1, 2, 3 })
        {
            using var run = Once($"R{rank}", r =>
            {
                r.GiveAbilityPower();
                r.Annie.SpellRanks[3] = rank;
                r.Refill();
                r.Foe.Health = r.Foe.MaxHealth = 100000;
                TestAssert.True(r.Cast(3, null, r.Foe.Position), $"R rank {rank} accepted");
                r.Advance(0.5);
            });
            var tibbers = run.Runtime.World.Entities.Values.Single(unit => unit.PetState?.Kind == CloneKind.Tibbers && unit.OwnerNetId == run.Annie.NetId);
            TestAssert.Near(new[] { 1200.0, 1600, 2000 }[rank - 1], tibbers.MaxHealth, 1e-6, "Tibbers hit points (Effect2)");
            TestAssert.Near(new[] { 80.0, 105, 130 }[rank - 1], tibbers.AttackDamage, 1e-6, "Tibbers attack damage (Effect3)");
            TestAssert.Near(new[] { 30.0, 50, 70 }[rank - 1], tibbers.Armor, 1e-6, "Tibbers armor");
            TestAssert.Near(new[] { 25.0, 45, 65 }[rank - 1], tibbers.SpellBlock, 1e-6, "Tibbers magic resist");
            TestAssert.Near(45.0, tibbers.ExpiresAt - tibbers.PetState!.BornAt, 1e-6, "Tibbers lives 45 seconds");
            TestAssert.True(tibbers.Buffs.Any(buff => buff.Name == "InfernalGuardianBurning"), "the script adds the burning buff to Tibbers");
            TestAssert.True(run.Annie.Buffs.Any(buff => buff.Name == "InfernalGuardianTimer"), "the script adds the timer buff to Annie");
            var burst = (new[] { 200.0, 325, 450 }[rank - 1] + (double)0.7f * run.Annie.AbilityPower).ToString("0.######", CultureInfo.InvariantCulture);
            TestAssert.True(run.Log.Damage.Any(line => line.Contains($"Annie->Foe {burst} Magic SpellAoe ", StringComparison.Ordinal)),
                $"the burst is DamageByLevel + 0.7 AP: {string.Join(" | ", run.Log.Damage)}");
            // The cast frame comes before the burst: the pet and his buffs are in place when the damage lands.
            var spawned = run.Log.Timeline.FindIndex(line => line.Contains("InfernalGuardianBurning", StringComparison.Ordinal));
            var damage = run.Log.Timeline.FindIndex(line => line.StartsWith("damage", StringComparison.Ordinal) && line.Contains("Annie->Foe", StringComparison.Ordinal));
            TestAssert.True(spawned >= 0 && spawned < damage, "SelfExecute (the pet and his buffs) runs before TargetExecute (the burst)");
        }
    }

    [Check("scripting.bb-annie-tibbers-aura-expiry-and-recast")]
    public static void TibbersAuraPulsesExpiresAndIsReplacedByARecast()
    {
        using var run = Once("Tibbers lifecycle", r =>
        {
            r.GiveAbilityPower();
            r.Annie.SpellRanks[3] = 1;
            r.Foe.Health = r.Foe.MaxHealth = 1000000;
            r.Refill();
            TestAssert.True(r.Cast(3, null, r.Foe.Position), "R accepted");
            r.Advance(2.0);
        });
        var first = run.Runtime.World.Entities.Values.Single(unit => unit.PetState?.Kind == CloneKind.Tibbers && unit.OwnerNetId == run.Annie.NetId);
        // InfernalGuardianBurning: 35 + 0.2 AP every second to every enemy within 350, the first pulse one second after the summon.
        var pulse = ((double)(float)(35.0 + 0.2 * run.Annie.AbilityPower)).ToString("0.######", CultureInfo.InvariantCulture);
        TestAssert.True(run.Log.Damage.Count(line => line.Contains($"Annie->Foe {pulse} Magic SpellAoe ", StringComparison.Ordinal)) >= 1,
            $"the aura pulsed once a second after the summon: {string.Join(" | ", run.Log.Damage)}");
        // A recast replaces Tibbers: the old one dies, the owner keeps one timer.
        run.Refill();
        TestAssert.True(run.Cast(3, null, run.Foe.Position), "R recast accepted");
        run.Advance(0.5);
        TestAssert.False(first.Alive, "the recast ends the first Tibbers");
        TestAssert.Equal(1, run.Runtime.World.Entities.Values.Count(unit => unit.PetState?.Kind == CloneKind.Tibbers && unit.OwnerNetId == run.Annie.NetId && unit.Alive),
            "one Tibbers at a time");
        TestAssert.Equal(1, run.Annie.Buffs.Count(buff => buff.Name == "InfernalGuardianTimer"), "the new cast's timer survives the old Tibbers' deactivation");
        // The 45 s timer ends him: the burning buff's deactivation deals 10000 true damage to its owner and removes the timer.
        run.Advance(46.0);
        TestAssert.False(run.Runtime.World.Entities.Values.Any(unit => unit.PetState?.Kind == CloneKind.Tibbers && unit.OwnerNetId == run.Annie.NetId && unit.Alive),
            "Tibbers is gone after 45 seconds");
        TestAssert.False(run.Annie.Buffs.Any(buff => buff.Name == "InfernalGuardianTimer"), "the timer leaves with him");
    }

    [Check("scripting.bb-annie-tibbers-aura-leaves-a-spell-shield-up")]
    public static void TibbersAuraTickDoesNotPopASpellShield()
    {
        // Owner decision, 18 October 2026 (evening): a damage-over-time tick follows the flags of the script that deals it.
        // InfernalGuardianBurning sets DoesntTriggerSpellCasts and no DoesntBreakShields, and the third branch of BuffOnBeingSpellHit in
        // SpellShield.luaobj and BansheesVeil.luaobj pops only a spell that does not set DoesntTriggerSpellCasts, so Tibbers' tick damages
        // the holder and leaves the veil up. (The hand-written aura passed bypassSpellShield for this; the script path gets it from
        // BbHookRouter.RunBuffHook, which runs BuffOnUpdateActions as the ambient spell.) The holder stands outside the burst (250.3)
        // and inside the aura (350) of the point.
        using var run = Once("aura and shield", r =>
        {
            r.GiveAbilityPower();
            r.Annie.SpellRanks[3] = 1;
            r.Foe.Health = r.Foe.MaxHealth = 1000000;
            r.Refill();
            var holder = Dummy(r, "Holder", r.Foe.Position + new Position2(300, 0));
            holder.Health = holder.MaxHealth = 100000;
            r.Runtime.Buffs.AddBuff(holder, "BansheesVeil", BuffType.Aura, 30.0, holder.NetId);
            TestAssert.True(r.Cast(3, null, r.Foe.Position), "R accepted");
            r.Advance(1.6);
        });
        var holder = run.Runtime.World.Entities.Values.Single(unit => unit.Name == "Holder");
        TestAssert.True(holder.Buffs.Any(buff => buff.Name == "BansheesVeil"), "the aura tick left the veil up");
        TestAssert.True(holder.Health < holder.MaxHealth, "the tick still damaged the holder");
        TestAssert.True(run.Log.Hits.Any(line => line.Contains("Annie->Holder", StringComparison.Ordinal) && line.Contains("SpellAoe", StringComparison.Ordinal)),
            "the aura hit is Annie's SpellAoe damage");
    }

    [Check("scripting.bb-annie-spell-shield-eats-the-stun")]
    public static void APoppedSpellShieldEatsTheStunAndTheDamage()
    {
        // Owner decision, 18 October 2026: a spell shield (Banshee's Veil here, Sivir's Spell Shield the same) blocks Annie's Q/W/R
        // stun together with the damage. On the script path the hit is raised where the engine hands the unit to TargetExecute
        // (BbHookRouter.RunInSpell), so the popped shield refuses BBApplyStun (BuffLifecycle) and zeroes the damage; the charge is
        // spent and the next Pyromania stack built either way.
        foreach (var key in new[] { 'Q', 'W', 'R' })
        {
            using var run = Once($"shield {key}", r =>
            {
                r.GiveAbilityPower();
                r.Annie.SpellRanks[0] = r.Annie.SpellRanks[1] = r.Annie.SpellRanks[3] = 1;
                r.Foe.Health = r.Foe.MaxHealth = 5000;
                r.Refill();
                r.Runtime.Buffs.AddBuff(r.Annie, "Pyromania_particle", BuffType.CombatEnhancer, 25000.0, r.Annie.NetId);
                r.Runtime.Buffs.AddBuff(r.Foe, "BansheesVeil", BuffType.Aura, 10.0, r.Foe.NetId);
                TestAssert.True(key switch
                {
                    'Q' => r.Cast(0, r.Foe),
                    'W' => r.Cast(1, null, r.Foe.Position, r.Annie.Position),
                    _ => r.Cast(3, null, r.Foe.Position),
                }, $"{key} accepted");
                r.Advance(0.5);
            });
            TestAssert.False(run.Foe.Buffs.Any(buff => buff.Name == "Stun"), $"{key}: the popped shield refuses the stun");
            TestAssert.Near(5000, run.Foe.Health, 1e-9, $"{key}: the popped shield zeroes the damage");
            TestAssert.False(run.Foe.Buffs.Any(buff => buff.Name == "BansheesVeil"), $"{key}: the veil popped");
            TestAssert.False(run.Annie.Buffs.Any(buff => buff.Name == "Pyromania_particle"), $"{key}: the charge is spent either way");
            TestAssert.Equal(1, run.Annie.Buffs.Where(buff => buff.Name == "Pyromania").Sum(buff => buff.Stacks), $"{key}: the next stack is built");
        }
    }

    [Check("scripting.bb-annie-burning-buff-hooks")]
    public static void InfernalGuardianBurningRunsItsHooksOnAPet()
    {
        // R's buff script on a stand-in pet (Tibbers' spawn stays hand-written): BuffOnUpdateStats with IncMaxHealth and the
        // resistance accessors, a one second BBExecutePeriodically in BuffOnUpdateActions with a ForEachUnitInTargetArea,
        // and OnBuffDeactivate (10000 true damage to the owner, which ends the pet when the buff expires).
        using var run = new Run();
        var world = run.Runtime.World;
        var pet = new Entity(world.Allocate(UnitType.Monster), EntityKind.Monster, UnitType.Monster, Team.Order, run.Annie.Position + new Position2(50, 0))
        {
            Name = "StandInPet", Health = 1000, MaxHealth = 1000, Armor = 10, SpellBlock = 10, Created = true, Visible = true,
            OwnerNetId = run.Annie.NetId, ExpiresAt = 1000.0,
        };
        world.Register(pet);
        run.Log.Name(pet, "Pet");
        run.Foe.Position = pet.Position + new Position2(200, 0);
        run.Foe.Health = run.Foe.MaxHealth = 5000;
        var vars = new BbTable();
        vars["FinalDamage"] = 50.0;
        vars["HealthAmount"] = 400.0;
        vars["ArmorAmount"] = 20.0;
        vars["MRAmount"] = 15.0;
        var request = new BbBuffRequest(pet, run.Annie, "InfernalGuardianBurning", (int)BbBuffAddType.BUFF_REPLACE_EXISTING,
            (int)BbBuffType.BUFF_CombatEnchancer, 1, 3.0, 0.0, false, vars);
        var state = run.Module.Buffs!.Add(request)!;
        TestAssert.Near(1400, pet.MaxHealth, 1e-9, "IncMaxHealth(HealthAmount, IncCurrentHealth)");
        TestAssert.Near(1400, pet.Health, 1e-9, "the current health grows with it");
        TestAssert.Near(30, pet.Armor, 1e-9, "IncFlatArmorMod");
        TestAssert.Near(25, pet.SpellBlock, 1e-9, "IncFlatSpellBlockMod");
        TestAssert.True(run.Log.Effects.Any(line => line.Contains("SunfireCapeAura_tar.troy bind=Pet", StringComparison.Ordinal)),
            "OnBuffActivate creates the aura effect bound to the owner (skin 0 branch)");

        run.Advance(2.5);
        if (Environment.GetEnvironmentVariable("BB_DIFF_TRACE") == "1")
        {
            foreach (var line in run.Log.Timeline) Console.WriteLine("  " + line);
            Console.WriteLine($"  pet buffs: {string.Join(",", pet.Buffs.Select(b => b.Name + "@" + b.ExpiresAt))} instances={run.Module.Buffs!.Instances.Count} time={run.Runtime.Spells.CurrentTime}");
        }
        var pulses = run.Log.Hits.Where(line => line.Contains("Annie->Foe", StringComparison.Ordinal)).ToArray();
        TestAssert.Equal(2, pulses.Length, "one pulse per second after the first full second: " + string.Join(" | ", pulses));
        TestAssert.True(pulses.All(line => line.Contains("raw=50 ", StringComparison.Ordinal) && line.Contains("SpellAoe", StringComparison.Ordinal)),
            "FinalDamage flat 50, DAMAGESOURCE_SPELLAOE, credited to the buff caster");
        var first = double.Parse(pulses[0][..pulses[0].IndexOf(' ')], CultureInfo.InvariantCulture);
        var second = double.Parse(pulses[1][..pulses[1].IndexOf(' ')], CultureInfo.InvariantCulture);
        // BBExecutePeriodically keeps overdue time (the next run is due at the previous due time + 1 s), so the spacing is
        // one second to within the 1/30 s step.
        TestAssert.Near(1.0, second - first, Step + 1e-6, "the period is one second");
        TestAssert.True(first > 1.0 && first < 1.1, $"first pulse right after the first full second: {first}");

        run.Foe.Position = pet.Position + new Position2(900, 0);
        run.Advance(0.6);
        TestAssert.False(pet.Alive, "OnBuffDeactivate deals 10000 true damage to the owner when the 3 s buff expires");
        TestAssert.False(pet.Buffs.Any(buff => ReferenceEquals(buff, state)), "the buff is gone");
        TestAssert.Equal(2, run.Log.Hits.Count(line => line.Contains("Annie->Foe", StringComparison.Ordinal)), "no pulse out of range");
    }

    private static Entity Dummy(Run run, string name, Position2 position)
    {
        var unit = new Entity(run.Runtime.World.Allocate(UnitType.Monster), EntityKind.Monster, UnitType.Monster, Team.Chaos, position)
        {
            Name = name, Health = 2000, MaxHealth = 2000, Armor = 20, SpellBlock = 20, Created = true, Visible = true,
        };
        run.Runtime.World.Register(unit);
        run.Log.Name(unit, name);
        return unit;
    }

    [Check("scripting.bb-annie-differential-geometry")]
    public static void ConeAndMovingTargetFollowTheScripts()
    {
        // W: units inside the 24.76 degree half angle and 625 range are hit in world order, the others are not (rear, wide, far).
        // Q: the missile tracks a target that walks away, and the damage keeps the rank of the cast when the rank changes in flight.
        using var script = Once("cone and flight", run =>
        {
            run.GiveAbilityPower();
            run.Annie.SpellRanks[0] = 2;
            run.Annie.SpellRanks[1] = 3;
            var origin = run.Annie.Position;
            Dummy(run, "NearCentre", origin + new Position2(250, 0));
            Dummy(run, "FarEdge", origin + new Position2(600, 100));
            Dummy(run, "TooWide", origin + new Position2(300, 250));
            Dummy(run, "TooFar", origin + new Position2(700, 0));
            Dummy(run, "Behind", origin + new Position2(-300, 0));
            run.Foe.Position = origin + new Position2(400, -60);
            TestAssert.True(Press(run, 'W'), "W cast request");
            run.Advance(0.6);
            TestAssert.True(run.Cast(0, run.Foe), "Q");
            run.Annie.SpellRanks[0] = 5;
            // The target walks away at 450 u/s while the missile flies.
            var steps = Enumerable.Range(1, 30).Select(i => (i * Step, (Action<Run>)(r => r.Foe.Position += new Position2(450 * Step, 0)))).ToArray();
            run.Timeline(1.2, steps);
        });
        {
            var victims = script.Log.Damage.Where(line => line.Contains("SpellAoe", StringComparison.Ordinal)).Count();
            TestAssert.Equal(3, victims, "NearCentre, FarEdge and the Foe stand inside the cone");
            // Batch 64: the script's SpellDamageRatio 0.7 reads as the float32 0.7f.
            var rank2 = (125.0 + (double)0.7f * script.Annie.AbilityPower).ToString("0.######", CultureInfo.InvariantCulture);
            TestAssert.Equal(1, script.Log.Damage.Count(line => line.Contains(" Spell ", StringComparison.Ordinal) && line.Contains($"Annie->Foe {rank2} ", StringComparison.Ordinal)),
                "Q keeps the rank of the cast (rank 2: 125 + 0.7 AP) although the rank rose in flight");
        }
    }
}
