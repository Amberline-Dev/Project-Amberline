using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Neutral;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Batch 159 step 2: a non-champion unit runs its own shipped character script from
/// <see cref="BbUnitCharacterModule"/>, bound to one unit at a time the way a champion's binds to a champion. The
/// first check pins a turret against the shipped <c>Characters/OrderTurretNormal/Scripts/CharScriptOrderTurretNormal.
/// luaobj</c> and the five spells its <c>CharOnActivate</c> adds; the second drives a hand-built character through the
/// same router with the production combat hooks; the third pins what the module refuses. The checks run in a production
/// <see cref="BbM3.Match"/>, whose spell system already carries the module
/// (<c>ProductionMatchComposition</c> installs it the way it installs the item and summoner ones).
/// </summary>
public static class BbUnitCharacterChecks
{
    private static BbBlock Take(double amount) =>
        BbW3Rig.Block("BBMath", "Src1Var", "DamageAmount", "Src1Value", 0, "Src2Value", amount, "DestVar", "DamageAmount",
            "MathOp", BbTest.Ref("MO_SUBTRACT"));

    private static BbBlock Scale(double factor) =>
        BbW3Rig.Block("BBMath", "Src1Var", "DamageAmount", "Src1Value", 0, "Src2Value", factor, "DestVar", "DamageAmount",
            "MathOp", BbTest.Ref("MO_MULTIPLY"));

    private static BbScript UnitProbe() => BbW3Rig.Character("W159UnitChar",
        new BbEventTable("CharOnActivate", [BbW3Rig.KeepChar("ActOwner", "Owner")]),
        new BbEventTable("CharOnHitUnit", [BbW3Rig.KeepChar("HitTarget", "Target"), BbW3Rig.KeepChar("HitRead", "DamageAmount"),
            Take(10.0), BbW3Rig.KeepChar("HitLeft", "DamageAmount")]),
        new BbEventTable("CharOnBeingHit", [BbW3Rig.KeepChar("BeingAttacker", "Attacker"), Take(5.0)]),
        new BbEventTable("CharOnPreDamage", [BbW3Rig.KeepChar("PreAmount", "DamageAmount"), Scale(0.5)]));

    private static double Hit(BbM3.Match match, Entity attacker, Entity target, double amount, DamageType type = DamageType.Physical,
        DamageSourceKind kind = DamageSourceKind.Basic)
    {
        var before = target.Health;
        match.Runtime.Combat.ApplyHit(attacker.NetId, target.NetId, amount, type, kind);
        return before - target.Health;
    }

    private static void NoFailures(BbUnitCharacterModule module) =>
        TestAssert.Equal(0, module.ScriptFailures.Count,
            "no script failure: " + string.Join("; ", module.ScriptFailures.Select(f => $"{f.Script}.{f.Hook} {f.Message}")));

    [Check("scripting.bb-unit-character-a-turret-runs-the-shipped-charscript-bound-to-it")]
    public static void TurretRunsItsShippedCharacterScript()
    {
        using var match = new BbM3.Match("Ryze");
        var module = match.Runtime.Spells.UnitScripts!;
        // A lane turret of the map's own character: OrderTurretNormal is what CreateLevelProps makes of the outer and
        // inner rows, and its CharScript is the file the pin below reads its behaviour from.
        var turret = BbM3.Enemy(match, "OrderTurretNormal", new Position2(match.Hero.Position.X, match.Hero.Position.Z + 400),
            UnitType.Turret, Team.Order);
        turret.MaxHealth = turret.Health = 2000.0;
        turret.AttackDamage = 100.0;
        turret.Armor = turret.SpellBlock = 20.0;
        TestAssert.True(module.BindUnit(turret, "OrderTurretNormal"), "the shipped character script binds");
        TestAssert.Equal("OrderTurretNormal", module.CharacterOf(turret), "the module keeps the character it bound");
        NoFailures(module);
        // CharOnActivate's own BBSetDodgePiercing (the hook is not a stats sample, so it writes the entity field at once).
        TestAssert.True(turret.DodgePiercing, "the character script pierces dodge");
        // CharOnActivate's five BBSpellBuffAdd blocks, in the order the script adds them. TurretBonusHealth is a
        // one-shot: its TickRate 0 add runs its own sample at the add and ends itself inside that same run, so it
        // is already gone and its work is on the unit.
        foreach (var name in new[] { "TurretBonus", "TurretChampionDelta", "TurretAssistManager", "TurretDamageManager" })
            TestAssert.True(BbM3.Buff(turret, name) is not null, $"CharOnActivate added {name}: {BbM3.Names(turret)}");
        // TurretAssistManager's OnBuffActivate adds PersonalTurretAssistBonus on top of its own permanents
        // (Spells/TurretAssistManager.luaobj).
        TestAssert.True(BbM3.Buff(turret, "PersonalTurretAssistBonus") is not null,
            $"TurretAssistManager's activation added its personal bonus: {BbM3.Names(turret)}");
        // TurretBonusHealth's one-shot (Spells/TurretBonusHealth.luaobj, run at the add): min(5, enemy champions) *
        // the add's NextBuffVars.BonusHealth of 250. The match has two Chaos heroes, so the turret gained 500.
        TestAssert.Near(2500.0, turret.MaxHealth, 1e-6, "TurretBonusHealth: 2000 + 2 champions * 250, at the add");
        TestAssert.True(BbM3.Buff(turret, "TurretBonusHealth") is null, "and it is a one-shot, gone in that sample");
        // TurretAssistManager's OnBuffActivate: +150 flat armor and spell block (BBIncPermanentStat IncPermanentFlat*Mod
        // 150), and PersonalTurretAssistBonus's OnBuffActivate adds +25 more of each (Spells/PersonalTurretAssistBonus.
        // luaobj) — 20 + 150 + 25. The old hand-written path stopped at 170 and kept the +25 only as a timed bonus.
        TestAssert.Near(195.0, turret.Armor, 1e-6, "TurretAssistManager 150 + PersonalTurretAssistBonus 25 on the 20");
        TestAssert.Near(195.0, turret.SpellBlock, 1e-6, "the same for the spell block");
        match.Advance(0.5);
        NoFailures(module);
        TestAssert.Near(2500.0, turret.MaxHealth, 1e-6, "the one-shot health holds");
        // The character's own UpdateSelfBuffStats sample (BBIncStat IncPercentArmorPenetrationMod 0.2), which the
        // non-hero stat write-back copies onto the entity field every unit's mitigation reads.
        TestAssert.Near(0.2, turret.ArmorPenetrationPercent, 1e-6, "UpdateSelfBuffStats' armor penetration lands on the unit");
        TestAssert.True(module.Bound.Any(pair => ReferenceEquals(pair.Unit, turret)), "the poll owns the unit");
    }

    [Check("scripting.bb-unit-character-a-bound-units-charscript-runs-the-combat-hooks")]
    public static void BoundUnitRunsTheCombatHooks()
    {
        using var match = new BbM3.Match("Ryze");
        var module = match.Runtime.Spells.UnitScripts!;
        var turret = BbM3.Enemy(match, "W159Turret", new Position2(match.Foe.Position.X + 150, match.Foe.Position.Z),
            UnitType.Turret, Team.Order);
        turret.Armor = turret.SpellBlock = 0.0;
        turret.MaxHealth = turret.Health = 100000.0;
        var probe = UnitProbe();
        TestAssert.True(module.BindUnit(turret, "W159UnitChar", probe), "the probe character binds through the module");
        TestAssert.True(ReferenceEquals(module.CharVars(turret)["ActOwner"], turret), "CharOnActivate ran with the unit as Owner");
        match.Foe.Armor = match.Foe.SpellBlock = 0.0;
        match.Foe.MaxHealth = match.Foe.Health = 100000.0;
        // The unit is the attacker: CharOnHitUnit of its character script runs before mitigation and may change the hit.
        TestAssert.Near(90.0, Hit(match, turret, match.Foe, 100.0), 1e-6, "CharOnHitUnit takes ten off the swing");
        // The unit is the target of a landed basic attack: CharOnBeingHit before the raw damage is kept, then
        // CharOnPreDamage after resistance and the shields.
        TestAssert.Near(47.5, Hit(match, match.Foe, turret, 100.0), 1e-6, "CharOnBeingHit takes five and CharOnPreDamage halves the rest");
        // A spell hit is not a basic attack: no being-hit hook, only the pre-damage one.
        TestAssert.Near(50.0, Hit(match, match.Foe, turret, 100.0, DamageType.Magic, DamageSourceKind.Spell), 1e-6,
            "a spell hit reaches CharOnPreDamage alone");
        NoFailures(module);
        var vars = module.CharVars(turret);
        TestAssert.True(ReferenceEquals(vars["HitTarget"], match.Foe), "CharOnHitUnit saw the unit it struck as Target");
        TestAssert.Near(90.0, (double)vars["HitLeft"]!, 1e-6, "and the amount it left in DamageAmount");
        TestAssert.True(ReferenceEquals(vars["BeingAttacker"], match.Foe), "CharOnBeingHit saw the unit that struck it");
        TestAssert.Near(100.0, (double)vars["PreAmount"]!, 1e-6,
            "CharOnPreDamage read the damage entering the hook (the spell hit's 100 at 0 resistances, before its own half)");
        // The unit is gone: its character script binding and its per-unit tables go with it (MatchGameplayHooks.OnDeath).
        match.Runtime.Combat.Kill(turret, match.Foe.NetId);
        TestAssert.True(module.CharacterOf(turret) is null, "a dead unit is unbound");
        TestAssert.True(module.Bound.All(pair => !ReferenceEquals(pair.Unit, turret)), "and the poll no longer owns it");
    }

    [Check("scripting.bb-unit-character-a-character-with-no-shipped-script-is-not-claimed")]
    public static void ACharacterWithoutAScriptIsNotClaimed()
    {
        using var match = new BbM3.Match("Ryze");
        var module = match.Runtime.Spells.UnitScripts!;
        // The one map turret with no CharScript<OrderTurretShrine>.luaobj: it keeps the hand-written path and this
        // module never claims it (batch 159 step 3 keeps the shrine's own rules, marked NOT RETAIL).
        var shrine = BbM3.Enemy(match, "OrderTurretShrine", new Position2(match.Hero.Position.X, match.Hero.Position.Z + 400),
            UnitType.Turret, Team.Order);
        TestAssert.False(module.HasScript("OrderTurretShrine"), "the shipped data holds no shrine character script");
        TestAssert.False(module.BindUnit(shrine, "OrderTurretShrine"), "so the module refuses the bind");
        TestAssert.True(module.CharacterOf(shrine) is null, "and claims no character for the unit");
        TestAssert.True(module.Bound.All(pair => !ReferenceEquals(pair.Unit, shrine)), "the module owns no shrine");
        // Batch 159 step 2 binds the map's own turrets as CreateMapEntities creates them (SessionSpawnService's
        // seam), so a production world is not an empty module any more. What this check pins is the exception: the
        // one CreateChildTurret row whose character has no shipped CharScript is the Order fountain's own.
        var mapTurrets = match.Runtime.World.Entities.Values.Where(unit =>
            unit.UnitType == UnitType.Turret && MapDefinitions.TurretTiers.ContainsKey(unit.Name)).ToArray();
        TestAssert.Equal(24, mapTurrets.Length, "the map's twenty-four CreateChildTurret rows");
        var unclaimed = mapTurrets.Where(unit => module.CharacterOf(unit) is null)
            .Select(unit => unit.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        TestAssert.Equal(1, unclaimed.Length, "one map turret is unclaimed: " + string.Join(", ", unclaimed));
        TestAssert.Equal("Turret_OrderTurretShrine", unclaimed[0], "it is the Order fountain's own row");
        // Every other turret character of the map has one.
        foreach (var character in new[] { "OrderTurretNormal", "OrderTurretNormal2", "OrderTurretDragon", "OrderTurretAngel",
            "ChaosTurretNormal", "ChaosTurretWorm", "ChaosTurretWorm2", "ChaosTurretGiant", "ChaosTurretShrine" })
            TestAssert.True(module.HasScript(character), $"the shipped data holds {character}'s character script");
    }

    [Check("scripting.bb-unit-character-a-camps-monster-grows-through-its-own-hp-by-player-level")]
    public static void MonsterGrowsThroughItsOwnHpByPlayerLevel()
    {
        // Spells/HPByPlayerLevel.luaobj is the shipped monster's player-level growth. Its OnBuffActivate takes the
        // highest hero level in 9999 units into MaxPlayerLevel (0016-0057), its BuffOnUpdateStats multiplies the add's
        // NextBuffVars.HPPerLevel by that level into BBIncStat IncFlatHPPoolMod (0138-0161) — the whole growth — and
        // the level scan repeats every 10 s while the monster is at 99% health or better (0060-0137). AncientGolem and
        // LizardElder add it with HPPerLevel 175 (CharScriptAncientGolem 0047-0073), the Dragon with 220.
        var world = new WorldState();
        var grid = NavigationGrid.LoadDefault(GameDataPaths.Default, NavigationGridSettings.Default);
        var characters = Amberline.Tests.Spells.UnitCharacterRig.Create(world, grid);
        var scheduler = new JungleScheduler(world, GameData.Default, navigation: grid);
        scheduler.BindUnitCharacter = (unit, character) => characters.Bind(unit, character);
        // A level-6 hero at every camp home, so the script's own 9999-unit scan sees the same level the engine's
        // NearbyHeroLevel does — and so the monster cannot grow twice, once from the spawn's own table and once from
        // the buff's payload.
        foreach (var camp in NeutralDefinitions.Camps.Values)
        {
            var home = camp.SpawnPoints[0];
            world.Register(new Entity(world.Allocate(UnitType.Hero), EntityKind.Hero, UnitType.Hero,
                Team.Order, new Position2(home.X + 250.0, home.Z))
            { Level = 6, Health = 1000.0, MaxHealth = 1000.0, Created = true });
        }
        scheduler.Start();
        scheduler.Tick(NeutralDefinitions.InitialSpawnSeconds);
        scheduler.Tick(900.0);
        characters.AssertNoFailures("the camp monsters' character scripts");
        var golems = scheduler.Members.Values.Where(member => member.Name == "AncientGolem").ToArray();
        TestAssert.True(golems.Length > 0, "the map spawns AncientGolems: " +
            string.Join(", ", scheduler.Members.Values.Select(member => member.Name).Distinct()));
        foreach (var golem in golems)
        {
            TestAssert.True(golem.Entity.Buffs.Any(buff => buff.Name == "HPByPlayerLevel"),
                "CharScriptAncientGolem's own UpdateSelfBuffActions keeps HPByPlayerLevel on the golem");
            TestAssert.Near(GameData.Default.Champion("AncientGolem").BaseHp + golem.Stats.HealthBonus + 175.0 * 6.0,
                golem.Entity.MaxHealth, 1e-6,
                "HPByPlayerLevel's 175 per hero level is the monster's whole player-level growth, applied once");
        }
    }

    [Check("scripting.bb-unit-character-every-camp-and-wave-character-resolves-to-its-shipped-script")]
    public static void EveryCampAndWaveCharacterResolvesToItsShippedScript()
    {
        using var match = new BbM3.Match("Ryze");
        var module = match.Runtime.Spells.UnitScripts!;
        // Every camp roster the map's NeutralMinionSpawn.luaobj lists: nine of the ten characters ship an eponymous
        // CharScript<name>.luaobj, and "wolf" — the small wolf's character name — ships its script in
        // Characters/wolf/Scripts/CharScriptYoungWolf.luaobj instead (there is no CharScriptwolf.luaobj). That is the
        // fallback BbScriptSet.SpawnedCharacter takes: a character whose own name finds nothing takes the folder's
        // single CharScript*.luaobj, and refuses it when another character owns that file by its own name (every
        // Characters/MonkeyKing* folder ships the champion's CharScriptMonkeyKing.luaobj, so the clone never runs it).
        foreach (var name in new[]
                 {
                     "AncientGolem", "LizardElder", "Dragon", "Golem", "GiantWolf", "wolf",
                     "Wraith", "LesserWraith", "YoungLizard",
                 })
            TestAssert.True(module.HasScript(name), $"the shipped data holds {name}'s character script");
        TestAssert.True(module.SpawnedCharacterName("wolf")?.EndsWith("CharScriptYoungWolf.luaobj",
                StringComparison.Ordinal) == true,
            "wolf resolves to Characters/wolf/Scripts/CharScriptYoungWolf.luaobj, not an eponymous file");
        TestAssert.False(module.HasScript("MonkeyKingClone"),
            "the MonkeyKing folders' shared CharScriptMonkeyKing.luaobj belongs to the champion and is not a fallback");
        // Batch 174 part 2: the Worm binds its shipped character script and the hand-written Worm logic is gone. Its statuses from a
        // stats sample and its unit child casts are the engine pieces part 1 added.
        TestAssert.True(module.HasScript("Worm"), "the Worm's character script ships");
        var worm = BbM3.Enemy(match, "Worm", match.Hero.Position, UnitType.Monster, Team.Neutral);
        TestAssert.True(module.BindUnit(worm, "Worm"), "the Worm binds its shipped character script");
        // The four wave characters: only the siege (MechCannon) and super (MechMelee) minions ship a CharScript; the
        // melee and caster characters carry a Bot.dat alone, so the module leaves them alone and they keep the
        // hand-written path (their wave AI is batch 155's Lua).
        foreach (var name in new[] { "Blue_Minion_MechCannon", "Blue_Minion_MechMelee",
                     "Red_Minion_MechCannon", "Red_Minion_MechMelee" })
            TestAssert.True(module.HasScript(name), $"the shipped data holds {name}'s character script");
        foreach (var name in new[] { "Blue_Minion_Basic", "Blue_Minion_Wizard", "Red_Minion_Basic", "Red_Minion_Wizard" })
            TestAssert.False(module.HasScript(name), $"{name} ships no character script");
    }

    [Check("scripting.bb-unit-character-a-camps-red-buff-burns-from-its-own-buff-script")]
    public static void RedBuffBurnsFromItsOwnBuffScript()
    {
        // Spells/BlessingoftheLizardElder.luaobj's own BuffOnHitUnitBuildingBlocks (0598) is the burn and the slow a
        // red-buffed hero deals: the HIT_Dodge/HIT_Miss guards, the CO_IS_TYPE_AI / CO_IS_TYPE_TURRET target pair, a
        // BBGetLevel into the 10..50 TickDamage-by-level table, the Burning add (BUFF_RENEW_EXISTING, BUFF_Damage,
        // 3.0 s, TickRate 1.0) and the Slow add (BUFF_STACKS_AND_OVERLAPS, MaxStack 100, -0.05/-0.10/-0.15 by
        // CO_IS_RANGED and by level). CharScriptLizardElder's UpdateSelfBuffActions puts that buff on the camp and the
        // buff's own BuffOnDeath transfers it, so the holder's burn is the shipped script's and the hand-written twin
        // in ItemPassiveService is gone. A level-6 ranged hero burns for 20 and slows for -0.05 (the table's sixth
        // entry, 0342-0363 and 0429-0448).
        using var match = new BbM3.Match("Ryze");
        var runtime = match.Runtime;
        var hero = match.Hero;
        var victim = match.Foe;
        hero.Level = 6;
        hero.AttackRange = 600.0;
        victim.MaxHealth = victim.Health = 100000.0;
        victim.Armor = victim.SpellBlock = 0.0;
        victim.Position = hero.Position;
        // The holder's instance is the one the camp's transfer makes: a script buff of this module's runtime, whose
        // BuffOnHitUnit the hit's own hook dispatch runs (BbHookRouter.WithHook). The add's name resolves the shipped
        // Spells/BlessingoftheLizardElder.luaobj.
        var before = victim.Health;
        var added = runtime.Spells.UnitScripts!.Buffs!.Add(new BbBuffRequest(hero, hero, "BlessingoftheLizardElder",
            (int)BbBuffAddType.BUFF_REPLACE_EXISTING, (int)Amberline.Core.Buffs.BuffType.CombatEnhancer, 1, 150.0, 0.0,
            false, new BbTable()));
        TestAssert.True(added is not null, "the hero holds the red buff as a live script instance");
        TestAssert.True(runtime.Combat.ApplyHit(hero.NetId, victim.NetId, 1.0, DamageType.Physical,
            DamageSourceKind.Basic), "the red-buffed swing lands");
        TestAssert.True(victim.Health <= before, "the swing resolves");
        var burning = victim.Buffs.FirstOrDefault(buff => buff.Name == "Burning");
        TestAssert.True(burning is not null,
            "BlessingoftheLizardElder.luaobj's BuffOnHitUnit adds Burning: " +
            string.Join(", ", victim.Buffs.Select(buff => buff.Name)));
        TestAssert.Near(20.0, burning!.Payload.DamagePerTick, 1e-9,
            "the burn is the script's own level-6 table entry");
        TestAssert.Near(3.0, burning.ExpiresAt, 1e-9, "Burning lasts the script's 3.0 s");
        var slow = victim.Buffs.Where(buff => buff.Name == "Slow").ToArray();
        TestAssert.Equal(1, slow.Length,
            "BUFF_STACKS_AND_OVERLAPS adds one slow instance per hit and no hand-written twin adds a second: " +
            string.Join(", ", victim.Buffs.Select(buff => buff.Name)));
        TestAssert.Near(-0.05, slow[0].Payload.MoveSpeedMult, 1e-9, "a ranged hero's level-6 slow is the script's -0.05");
        TestAssert.Near(3.0, slow[0].ExpiresAt, 1e-9, "Slow lasts the script's 3.0 s");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Batch 174 part 1: the three engine gaps batch 159 named for the Worm's character script
    // ------------------------------------------------------------------------------------------------------------

    private static BbBlock Status(string status, bool value) =>
        BbW3Rig.Block("BBSetStatus", ["TargetVar", "Owner", "SrcValue", value, "Status", BbTest.Ref(status)]);

    private static BbBlock Regen(string function, double delta, string parType) =>
        BbW3Rig.Block(function, ["TargetVar", "Owner", "Delta", delta, "PARType", BbTest.Ref(parType)]);

    [Check("scripting.bb-unit-character-a-status-set-from-a-character-stats-sample-lands-on-the-unit")]
    public static void AStatusFromACharacterStatsSampleLandsOnTheUnit()
    {
        // CharScriptWorm's UpdateSelfBuffStats writes SetCanAttack false and SetCanMove false (0039-0057) beside its two
        // buff adds. Batch 159 left them out (BbSpellHost.SetStatus, "phase 3"); batch 174 collects them with the rest of
        // the sample and writes them onto the unit when the hook ends, so the swing and movement gates see them.
        using var match = new BbM3.Match("Ryze");
        var module = match.Runtime.Spells.UnitScripts!;
        var probe = BbW3Rig.Character("W174SampleStatus",
            new BbEventTable("UpdateSelfBuffStats", [Status("SetCanAttack", false), Status("SetCanMove", false)]));
        var unit = BbM3.Enemy(match, "W174SampleUnit", new Position2(match.Hero.Position.X, match.Hero.Position.Z + 300),
            UnitType.Monster, Team.Neutral);
        unit.MaxHealth = unit.Health = 5000.0;
        TestAssert.True(module.BindUnit(unit, "W174SampleStatus", probe), "the probe character binds through the module");
        NoFailures(module);
        // The bind took the first sample before CharOnActivate; both bits are clear.
        TestAssert.False(CharacterState.Of(unit).CanAttack, "SetCanAttack false from the stats sample clears the attack bit");
        TestAssert.False(CharacterState.Of(unit).CanMove, "SetCanMove false clears the move bit");
        TestAssert.True(CharacterState.Of(unit).CanCast, "and nothing else");
        // The poll re-asserts them: another script's rest write clears the carrier, and the character's own next sample
        // puts the status back, the way retail's one bit per unit is rebuilt by the poll that writes it.
        // Batch 256: the next sample is the unit's next 0.25 s self-buff line (the hero's rule, BbSelfUpdate), not the next step; the
        // status natives write one bit with no per-update reset, so the bit stays released until that poll in retail too.
        module.Buffs!.WriteStatus(unit, BbSetStatus.SetCanAttack, true, null, "OnBuffDeactivate");
        TestAssert.True(CharacterState.Of(unit).CanAttack, "another script's rest write releases the status");
        match.Runtime.Spells.Tick(1.0 / 30.0);
        TestAssert.True(CharacterState.Of(unit).CanAttack, "the bind's sample started the line: the next step is not a poll");
        match.Runtime.Spells.Tick(0.25);
        TestAssert.False(CharacterState.Of(unit).CanAttack, "the character's own next sample puts it back");
        TestAssert.False(CharacterState.Of(unit).CanMove, "both bits");
        NoFailures(module);
    }

    [Check("scripting.bb-unit-character-charscriptworm-casts-its-own-spell-slot-through-a-child-cast")]
    public static void WormCastsItsOwnSpellSlotThroughAChildCast()
    {
        // CharScriptWorm's CharOnPreDamage casts the unit's spell slot 3 (BBSpellCast SlotNumber=3 SlotType=SpellSlots,
        // 0130-0148) when a hero within 950 units hits it; slot 4 of Worm.inibin is WormAttack (a CastType 1 missile,
        // 1300 speed) whose own TargetExecute deals the base attack damage. This is the child-cast gap batch 159 named:
        // a non-champion unit now runs its own slot cast through the module's ChildCaster.
        using var match = new BbM3.Match("Ryze");
        var module = match.Runtime.Spells.UnitScripts!;
        // The Worm's own shipped character script is what Part 2 binds; this part pins the engine piece by driving the
        // same call the script makes (CharScriptWorm 0131-0148: BBSpellCast caster Owner, slot 3 of SpellSlots, the
        // attacker as Target). Worm.inibin's Data*Spell4 is WormAttack, a CastType 1 missile at 1300 speed whose
        // TargetExecute adds WormAttack to the victim; the cast is bound by hand so the check does not wait for Part 2.
        var worm = BbM3.Enemy(match, "Worm", new Position2(match.Hero.Position.X + 400, match.Hero.Position.Z),
            UnitType.Monster, Team.Neutral);
        worm.MaxHealth = worm.Health = 20000.0;
        worm.AttackDamage = 600.0;
        var probe = BbW3Rig.Character("W174UnitCast",
            new BbEventTable("CharOnPreDamage",
            [
                BbW3Rig.Block("BBSpellCast", ["CasterVar", "Owner", "TargetVar", "Attacker", "PosVar", "Owner",
                    "EndPosVar", "Owner", "SlotNumber", 3.0, "SlotType", BbTest.Ref("SpellSlots"),
                    "OverrideForceLevel", 1.0, "OverrideCoolDownCheck", false, "FireWithoutCasting", false]),
                BbW3Rig.KeepChar("Ran", "DamageAmount"),
            ]));
        TestAssert.True(module.BindUnit(worm, "W174UnitCast", probe), "the probe character binds through the module");
        NoFailures(module);
        // The slot's name is the character record's own (Worm.inibin Data*Spell4 = WormAttack): the module resolves it
        // through BbSpellHost.ChildSpellName, so a unit needs no champion spellbook.
        TestAssert.Equal("WormAttack", module.SlotSpellName(worm, 3), "slot 4 of the Worm's record is WormAttack");
        // A monster, not the enemy hero: the hero stat pipeline rebuilds a hero's pool on the next tick, and the script's
        // TargetExecute applies its own hit before it adds the buff, which a corpse would refuse (BuffLifecycle).
        var victim = BbM3.Enemy(match, "W174Victim", worm.Position, UnitType.Minion, Team.Chaos);
        victim.MaxHealth = victim.Health = 5000.0;
        victim.Armor = victim.SpellBlock = 0.0;
        var buffsBefore = victim.Buffs.Count;
        // The pre-damage hook itself, through the gameplay hooks (the dispatch a real hit takes). The victim hits the Worm:
        // Attacker is the damage source, and that is the unit the shipped CharOnPreDamage casts at (0130-0148).
        match.Runtime.Combat.ApplyHit(victim.NetId, worm.NetId, 50.0, DamageType.Physical, DamageSourceKind.Basic);
        NoFailures(module);
        TestAssert.True(module.CharVars(worm)["Ran"] is not null, "the Worm's CharOnPreDamage ran");
        // The cast has a missile: it waits the child's own cast time, then flies at 1300 units a second and lands the script's
        // own hit. Step to the landing so the buff's end is the script's duration and not the test's choice of tick.
        for (var step = 0; step < 90 && BbM3.Buff(victim, "WormAttack") is null; step++) match.Advance(1.0 / 30.0);
        NoFailures(module);
        var wormAttack = BbM3.Buff(victim, "WormAttack");
        TestAssert.True(wormAttack is not null,
            "WormAttack's TargetExecute adds its CombatDehancer to the unit it lands on: victim=" + BbM3.Names(victim) +
            " worm=" + BbM3.Names(worm) + " hero=" + BbM3.Names(match.Hero) + " wormAlive=" + worm.Alive + " victimAlive=" + victim.Alive);
        // ExpiresAt counts down from the add (BuffLifecycle), and the tick that landed the missile already took its own step;
        // the add's duration is the script's 2.5 s (WormAttack 0091).
        TestAssert.True(wormAttack!.ExpiresAt <= 2.5 && wormAttack.ExpiresAt > 2.5 - BbM3.Step - 1e-9,
            $"the add carries the script's own 2.5 s, less the landing tick: {wormAttack.ExpiresAt:0.####}");
        TestAssert.True(victim.Buffs.Count > buffsBefore, "a buff arrived");
    }

    [Check("scripting.bb-inc-permanent-flat-par-regen-mod-adds-and-takes-back-the-script-s-flat-mana-regen")]
    public static void IncPermanentFlatParRegenModAddsAndTakesBackTheFlatManaRegen()
    {
        // Spells/ExaltedWithBaronNashor.luaobj's OnBuffActivate is BBIncPermanentFlatPARRegenMod PAR_MANA 3.0 and its
        // OnBuffDeactivate is the same call with -3.0 (0008-0015 and 0117-0124). The native is the permanent twin of
        // BBIncFlatPARRegenMod and lands in the hero's FlatMPRegenMod accumulator, the key the calculator reads. The probe
        // buff is the script's own pair of calls, at the script's arguments.
        var probe = BbW3Rig.Buff("W174ExaltedRegen",
            new BbEventTable("OnBuffActivate", [Regen("BBIncPermanentFlatPARRegenMod", 3.0, "PAR_MANA")]),
            new BbEventTable("OnBuffDeactivate", [Regen("BBIncPermanentFlatPARRegenMod", -3.0, "PAR_MANA")]));
        using var rig = new BbW3Rig(null, probe);
        var holder = rig.Match.Hero;
        holder.Mana = holder.MaxMana;
        rig.Match.Advance(0.1);
        var before = holder.ManaRegen;
        rig.Give(holder, probe);
        rig.NoFailures();
        TestAssert.Near(3.0, holder.PermanentStatMods.GetValueOrDefault("FlatMPRegenMod"), 1e-9,
            "the native adds the script's 3 to the FlatMPRegenMod ledger");
        TestAssert.Near(before + 3.0, holder.ManaRegen, 1e-6, "and the hero's regeneration rises by it");
        // The deactivation is the same native with -3.0; drive it the way the script writes it.
        rig.Module.Router!.RunBuffHook(rig.Module.Buffs!.Find(holder, probe.Name)!, "OnBuffDeactivate", holder);
        TestAssert.Near(0.0, holder.PermanentStatMods.GetValueOrDefault("FlatMPRegenMod"), 1e-9, "the -3 takes it back");
        TestAssert.Near(before, holder.ManaRegen, 1e-6, "and the regeneration is back where it was");
        rig.NoFailures();
    }
}
