using Amberline.Core;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// The wave 9 engine bundle (W26c, 19 October 2026): the clone pets and the AI-controlled spawns the shipped scripts of LeBlanc, Wukong, Mordekaiser,
/// Malzahar and Heimerdinger need. Driven with the shipped call blocks on the production <c>MatchRuntime</c> of the M3 harness (Soraka's match supplies the
/// caster and the services), as <c>BbSpawnChecks</c> does; the scenario <c>S-engine-w9</c> shows the same units live. Written, not run (owner rule: no check
/// runs during the overnight phase 3).
/// </summary>
public static class BbEngineW9Checks
{
    private static BbBlock Block(string function, params (string Key, BbValue Value)[] fields) =>
        new(function, fields.Select(field => new BbField(field.Key, field.Value)).ToArray(), []);

    private static BbValue Text(string value) => new BbValue.Text(value);

    private static BbBlock Shipped(string script, string hook, string function, string? skin = null) =>
        BbM3.Blocks(BbM3.Script(script), hook).First(block => block.Function == function && (skin is null || block.Text("Skin") == skin));

    private static void Run(BbM3.Match match, Entity caster, IReadOnlyList<BbBlock> blocks, Entity? target = null, int rank = 1, Position2? at = null)
    {
        var script = new BbScript("W26cSpawn", "Spells/W26cSpawn.luaobj", [], [new BbEventTable("SelfExecute", blocks)], []);
        var point = at ?? caster.Position;
        var position = new Position3(point.X, 0, point.Z);
        match.Module.Router!.RunSelfExecute(script, caster, 0, rank, 0.0, position, position, target);
    }

    private static void NoFailure(BbM3.Match match, string what) =>
        TestAssert.Equal(0, match.Module.ScriptFailures.Count,
            what + ": " + string.Join(" | ", match.Module.ScriptFailures.Select(failure => $"{failure.Script}/{failure.Hook}/{failure.BlockPath}: {failure.Message}")));

    private static List<Entity> NewUnits(BbM3.Match match, Action run)
    {
        var before = match.Runtime.World.Entities.Keys.ToHashSet();
        run();
        return match.Runtime.World.Entities.Values.Where(unit => !before.Contains(unit.NetId)).ToList();
    }

    // ------------------------------------------------------------------------------------------------------------
    // Clone pets
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-engine-w9-the-clone-families-name-their-controller-buffs")]
    public static void TheCloneFamiliesAreKeyedByTheCallsBuffAndNameTheirControllers()
    {
        TestAssert.Equal(CloneKind.ShacoClone, BbNativeHandlers.CloneKindOf("Hallucinate"), "Shaco's");
        TestAssert.Equal(CloneKind.YorickRevenant, BbNativeHandlers.CloneKindOf("YorickRARevive"), "Yorick's");
        TestAssert.Equal(CloneKind.LeblancImage, BbNativeHandlers.CloneKindOf("LeblancMI"), "LeBlanc's");
        TestAssert.True(BbNativeHandlers.CloneKindOf("NoSuchClone") is null, "any other buff name is no family");
        TestAssert.Equal("HallucinateFull", CloneFamilies.ControllerBuff(CloneKind.ShacoClone), "Shaco's controller");
        TestAssert.Equal("YorickRAPetBuff2", CloneFamilies.ControllerBuff(CloneKind.YorickRevenant), "Yorick's controller");
        TestAssert.Equal("LeblancMIFull", CloneFamilies.ControllerBuff(CloneKind.LeblancImage), "LeBlanc's controller (the buff her script adds right after the clone)");
        TestAssert.True(BbM3.Blocks(BbM3.Script("LeblancMIApplicator"), "OnBuffDeactivate").Any(block =>
                block.Function == "BBSpellBuffAdd" && block.Text("BuffName") == "LeblancMIFull"), "LeblancMIApplicator adds LeblancMIFull to the clone");
    }

    [Check("scripting.bb-engine-w9-leblanc-image-is-a-clone-pet-of-the-owner")]
    public static void LeblancMirrorImageIsTheCloneServicesUnitOnTheOwnersTeam()
    {
        using var match = new BbM3.Match("Soraka");
        var hero = match.Hero;
        var blocks = BbM3.Blocks(BbM3.Script("LeblancMIApplicator"), "OnBuffDeactivate").ToList();
        var clones = NewUnits(match, () => Run(match, hero, [
            blocks.First(block => block.Function == "BBGetRandomPointInAreaUnit"), blocks.First(block => block.Function == "BBSetVarInTable"),
            blocks.First(block => block.Function == "BBCloneUnitPet")]));
        NoFailure(match, "the image");
        var image = clones.Single();
        TestAssert.True(image.Kind == EntityKind.ClonePet && image.PetState is { Kind: CloneKind.LeblancImage }, "a clone pet of LeBlanc's family");
        TestAssert.True(image.OwnerNetId == hero.NetId && image.PetState!.CloneSourceNetId == hero.NetId && image.Team == hero.Team, "his, a copy of him, on his team");
        TestAssert.Equal("LeblancMI", image.PetState!.SpawnBuffName);
        TestAssert.Near(8.0, image.PetState!.SpawnDuration, 1e-9, "Duration 8");
        TestAssert.True(image.PetState!.ShowMinimapIcon && image.Created, "ShowMinimapIcon = true, announced to the clients");
        TestAssert.Near(hero.AttackDamage, image.AttackDamage, 1e-9, "with the owner's attack damage (its swing is zeroed by LeblancMIFull, not here)");
        // Without its LeblancMIFull controller the clone service leaves the unit alone; it must not fault on it.
        match.Advance(1.0);
        TestAssert.True(image.Alive, "the image survives the service ticks");
        NoFailure(match, "after the ticks");
    }

    [Check("scripting.bb-engine-w9-wukong-decoy-is-a-controllerless-clone-pet")]
    public static void WukongDecoyIsTheCloneFamilyThatNeverActs()
    {
        TestAssert.Equal(CloneKind.MonkeyKingDecoy, BbNativeHandlers.CloneKindOf("MonkeyKingDecoyDummy"), "the decoy's buff name");
        TestAssert.True(CloneFamilies.ControllerBuff(CloneKind.MonkeyKingDecoy) is null, "it has no controller: its scripts stop it from moving and attacking");
        using var match = new BbM3.Match("Soraka");
        var hero = match.Hero;
        var blocks = BbM3.Blocks(BbM3.Script("MonkeyKingDecoy"), "SelfExecute").ToList();
        var clones = NewUnits(match, () => Run(match, hero, [
            blocks.First(block => block.Function == "BBGetUnitPosition"), blocks.First(block => block.Function == "BBCloneUnitPet")]));
        NoFailure(match, "the decoy");
        var decoy = clones.Single();
        TestAssert.True(decoy.Kind == EntityKind.ClonePet && decoy.PetState is { Kind: CloneKind.MonkeyKingDecoy }, "a clone pet of the decoy family");
        TestAssert.True(decoy.OwnerNetId == hero.NetId && decoy.Team == hero.Team, "his, on his team");
        TestAssert.Equal("MonkeyKingDecoyDummy", decoy.PetState!.SpawnBuffName);
        TestAssert.Near(0.0, decoy.PetState!.SpawnDuration, 1e-9, "Duration 0: its scripts end it");
        match.Advance(1.0);
        TestAssert.True(decoy.Alive && decoy.Waypoints.Count == 0, "the clone service leaves a controllerless clone alone");
        NoFailure(match, "after the ticks");
    }

    [Check("scripting.bb-engine-w9-set-not-targetable-to-team-hides-the-unit-from-one-side")]
    public static void SetNotTargetableToTeamIsTheUnitsOwnPerTeamFlag()
    {
        TestAssert.True(BbNativeHandlers.IsBound("BBSetNotTargetableToTeam"), "the native is bound");
        var call = BbM3.Blocks(BbM3.Script("MonkeyKingDecoyClone"), "OnBuffActivate").First(block => block.Function == "BBSetNotTargetableToTeam");
        TestAssert.True(call.Param("ToAlly") is BbValue.Bool { Value: true } && call.Param("ToEnemy") is BbValue.Bool { Value: false },
            "the decoy hides from its own team only");
        using var match = new BbM3.Match("Soraka");
        var hero = match.Hero;
        TestAssert.True(match.Runtime.Combat.IsTargetable(hero, match.Ally) && match.Runtime.Combat.IsTargetable(hero, match.Foe), "targetable to both before");
        Run(match, hero, [call]);
        NoFailure(match, "the native");
        TestAssert.False(match.Runtime.Combat.IsTargetable(hero, match.Ally), "not to his own team");
        TestAssert.True(match.Runtime.Combat.IsTargetable(hero, match.Foe), "still to the enemy");
    }

    [Check("scripting.bb-engine-w9-mordekaiser-ghost-is-a-family-with-the-slow-and-his-controller")]
    public static void MordekaiserGhostFamilyIsKeyedByTheCallsSpellingAndNamesItsController()
    {
        TestAssert.Equal(CloneKind.MordekaiserGhost, BbNativeHandlers.CloneKindOf("MordekaiserCotgPetSlow"), "the call's spelling");
        TestAssert.Equal(CloneKind.MordekaiserGhost, BbNativeHandlers.CloneKindOf("MordekaiserCOTGPetSlow"), "and the script file's, whatever the case");
        TestAssert.Equal("MordekaiserCOTGPetBuff2", CloneFamilies.ControllerBuff(CloneKind.MordekaiserGhost), "the controller buff his revive script adds right after");
        var call = BbM3.Blocks(BbM3.Script("MordekaiserCOTGRevive"), "BuffOnUpdateActions").First(block => block.Function == "BBCloneUnitPet");
        TestAssert.True(call.Text("Buff") == "MordekaiserCotgPetSlow" && call.Text("UnitToCloneVar") == "Attacker", "the shipped call clones the buff's caster, the fallen enemy");
        TestAssert.True(BbNativeHandlers.SpawnRefusal(call) is null, "and is accepted");
    }

    [Check("scripting.bb-engine-w9-a-clone-buff-with-buff-hooks-is-added-to-the-clone")]
    public static void TheNamedBuffOfACloneIsAddedWhenItsScriptHasBuffHooks()
    {
        // The slow is the speed cut of MordekaiserCOTGPetSlow.BuffOnUpdateStats; Hallucinate, YorickRARevive and LeblancMI are spell scripts with no buff hook.
        bool HasBuffHooks(string name) => BbM3.Script(name).Events.Any(table => table.Hook.StartsWith("OnBuff", StringComparison.Ordinal) ||
                                                                                 table.Hook.StartsWith("BuffOn", StringComparison.Ordinal));
        TestAssert.True(HasBuffHooks("MordekaiserCOTGPetSlow"), "the speed cut is a buff: the server adds it to the ghost");
        foreach (var spell in new[] { "Hallucinate", "YorickRARevive", "LeblancMI", "MonkeyKingDecoyDummy" })
            TestAssert.False(HasBuffHooks(spell), $"{spell} has no buff hook: nothing is added for it");
    }

    // ------------------------------------------------------------------------------------------------------------
    // AI-controlled spawns
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-engine-w9-malzahar-voidling-is-a-pet-ai-unit-that-takes-no-orders")]
    public static void VoidlingIsAMinionOnTheWireAndUncontrollablePetLuaForTheAi()
    {
        var blocks = BbM3.Blocks(BbM3.Script("AlZaharSummonVoidling"), "BuffOnSpellCast").ToList();
        var spawn = blocks.First(block => block.Function == "BBSpawnMinion");
        TestAssert.True(BbNativeHandlers.SpawnRefusal(spawn) is null, "the call is accepted: UncontrollablePet.lua has a controller since W26c");
        TestAssert.Equal("UncontrollablePet.lua", spawn.Text("AiScript"), "the shipped call");
        using var match = new BbM3.Match("Soraka");
        var hero = match.Hero;
        var units = NewUnits(match, () => Run(match, hero, [
            blocks.First(block => block.Function == "BBGetTeamID"), blocks.First(block => block.Function == "BBGetPointByUnitFacingOffset"), spawn]));
        NoFailure(match, "the voidling");
        var voidling = units.Single();
        TestAssert.True(voidling.Kind == EntityKind.Monster && voidling.AiScript == "UncontrollablePet.lua", "a minion on the wire that carries its AI script");
        TestAssert.True(voidling.PetState is { Kind: CloneKind.Voidling } && voidling.OwnerNetId == hero.NetId && voidling.Team == hero.Team, "Pet.lua's state, his, on his team");
        TestAssert.True(voidling.Created && double.IsPositiveInfinity(voidling.ExpiresAt), "announced; its life is its buff's, not a clock");
        TestAssert.True(voidling.AttackPeriod > 0.0 && voidling.AttackTimingBaseline is not null, "with the record's attack timing");
        // UncontrollablePet.luaobj's OnOrder (proto 0.2) is `function() return true end`: it
        // answers that it took the order and then does nothing with it, so the hard move to the
        // click is answered and leaves the voidling where its own scans keep it.
        TestAssert.True(match.Runtime.Pets.Command(hero, 6, hero.Position + new Position2(500, 0)),
            "the script answers the owner's order");
        TestAssert.True(voidling.Waypoints.Count == 0 && voidling.Target is null &&
            voidling.AttackTarget is null, "no order of the owner changes what it does");

        // It looks for enemies around its owner (Pet.lua's scan, FindTargetInAcRUsingGoldRedirectTarget read as the record's 800 around him).
        match.Foe.Position = hero.Position + new Position2(600, 0);
        match.Advance(2.0);
        TestAssert.Equal(match.Foe.NetId, voidling.AttackTarget, "it has taken the enemy 600 units from its owner");
        match.Foe.Position = hero.Position + new Position2(1500, 0);
        match.Advance(2.0);
        TestAssert.True(voidling.AttackTarget != match.Foe.NetId, "and drops it when it is beyond 800 of its owner");
        NoFailure(match, "after the ticks");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Heimerdinger's turrets (Minion.lua) and Wukong's Nimbus clones (Aggro.lua)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-engine-w9-heimerdinger-turret-stands-and-fights-by-minion-lua")]
    public static void HeimerdingerTurretIsAMinionUnitThatStandsAndTakesTheNearestEnemyInReach()
    {
        var spawn = Shipped("H28GEvolutionTurret", "SelfExecute", "BBSpawnMinion", "HeimerTYellow");
        TestAssert.True(BbNativeHandlers.SpawnRefusal(spawn) is null, "the call is accepted: Minion.lua has a controller since W26c");
        TestAssert.Equal("Minion.lua", spawn.Text("AiScript"), "the shipped call");
        using var match = new BbM3.Match("Soraka");
        var hero = match.Hero;
        var units = NewUnits(match, () => Run(match, hero, [
            Shipped("H28GEvolutionTurret", "SelfExecute", "BBGetTeamID"), Shipped("H28GEvolutionTurret", "SelfExecute", "BBGetCastSpellTargetPos"), spawn],
            at: hero.Position + new Position2(200, 0)));
        NoFailure(match, "the turret");
        var turret = units.Single();
        TestAssert.True(turret.Kind == EntityKind.Monster && turret.AiScript == "Minion.lua" && turret.PetState is null,
            "a minion on the wire that carries its AI script and no pet state");
        TestAssert.True(turret.OwnerNetId == hero.NetId && turret.Team == hero.Team && turret.Created, "his, on his team, announced");
        TestAssert.Equal("HeimerTYellow", turret.StatCharacterName, "in the yellow skin");
        TestAssert.True(turret.Speed <= 0.0 || turret.Waypoints.Count == 0, "it stands where it was put");
        var spot = turret.Position;

        // Minion.lua's scan (0.25 s): the enemy in reach is engaged, one beyond it is not chased (the unit cannot move), and a taunt names the target.
        match.Foe.Position = spot + new Position2(300, 0);
        match.Advance(1.0);
        TestAssert.Equal(match.Foe.NetId, turret.AttackTarget, "it has taken the enemy 300 units from it");
        TestAssert.True(WorldDistance(turret.Position, spot) < 1.0, "and has not moved");
        match.Foe.Position = spot + new Position2(1600, 0);
        match.Advance(1.0);
        TestAssert.True(turret.AttackTarget != match.Foe.NetId, "it drops the enemy that left");
        NoFailure(match, "after the ticks");
    }

    [Check("scripting.bb-engine-w9-wukong-nimbus-clone-runs-aggro-lua-and-is-script-driven")]
    public static void WukongNimbusCloneIsAnAggroUnitWhoseOwnBuffMakesItUntargetable()
    {
        var spawn = Shipped("MonkeyKingNimbus", "TargetExecute", "BBSpawnMinion", "MonkeyKingFlying");
        TestAssert.True(BbNativeHandlers.SpawnRefusal(spawn) is null, "the call is accepted: Aggro.lua has a controller since W26c");
        TestAssert.Equal("Aggro.lua", spawn.Text("AiScript"), "the shipped call");
        TestAssert.True(SpawnedUnitAi.Runs("aggro.lua") && SpawnedUnitAi.Canonical("aggro.lua") == "Aggro.lua" && SpawnedUnitAi.IsLaneLess("Aggro.lua"),
            "the script names are case-insensitive and canonical");
        TestAssert.False(SpawnedUnitAi.Runs("Pet.lua"), "Pet.lua is not a spawned unit's script");
        using var match = new BbM3.Match("Soraka");
        var hero = match.Hero;
        var units = NewUnits(match, () => Run(match, hero, [
            Shipped("MonkeyKingNimbus", "TargetExecute", "BBGetTeamID"), Shipped("MonkeyKingNimbus", "TargetExecute", "BBGetUnitPosition"), spawn],
            target: match.Foe));
        NoFailure(match, "the clone");
        var clone = units.Single();
        TestAssert.True(clone.Kind == EntityKind.Monster && clone.AiScript == "Aggro.lua" && clone.OwnerNetId == hero.NetId && clone.Team == hero.Team,
            "a minion of his team that carries its AI script");
        TestAssert.Equal("MonkeyKingFlying", clone.StatCharacterName, "in the flying skin");
        match.Foe.Position = clone.Position + new Position2(100, 0);
        match.Advance(0.3);
        // Script wins (batch 163): Aggro.luaobj's OnInit (proto 0.0) writes SetState(AI_IDLE) and arms
        // TimerFindEnemies, and that timer only acquires from AI_ATTACKMOVESTATE (proto 0.12).
        // Nothing walks a unit out of AI_IDLE but FindTargetOrMove (proto 0.14), and only the event hooks
        // call that — so the clone stands there and takes no target of its own. The hand-written Aggro
        // ladder of TibbersAiSystem (a scan every step) is gone with its old assertion.
        TestAssert.Equal(MinionAiState.Idle, clone.MinionState, "Aggro.luaobj's OnInit left it in AI_IDLE");
        TestAssert.Equal((uint?)null, clone.AttackTarget,
            "and its TimerFindEnemies has not taken the enemy beside it");
        NoFailure(match, "after the ticks");
    }

    /// <summary>
    /// Batch 348: a call for help reaches a spawned unit that runs a shipped AI script. The Nimbus clone (Aggro.lua) stands in AI_IDLE, which its
    /// <c>OnCallForHelp</c> (Aggro proto 0.3) answers like AI_ATTACKMOVESTATE and AI_ATTACKMOVE_ATTACKING: <c>SetStateAndCloseToTarget(
    /// AI_ATTACKMOVE_ATTACKING, aggressor)</c>. The foe's basic attack on the clone's hero, from inside the clone's reach, is that call; before the batch
    /// only lane minions and structures were called and the clone stood idle. A single-target spell calls nobody (the lane rule).
    /// </summary>
    [Check("scripting.bb-engine-w9-nimbus-clone-answers-a-call-for-help")]
    public static void NimbusCloneAnswersACallForHelp()
    {
        var spawn = Shipped("MonkeyKingNimbus", "TargetExecute", "BBSpawnMinion", "MonkeyKingFlying");
        using var match = new BbM3.Match("Soraka");
        var hero = match.Hero;
        match.Foe.Position = hero.Position + new Position2(0, 2000);
        var units = NewUnits(match, () => Run(match, hero, [
            Shipped("MonkeyKingNimbus", "TargetExecute", "BBGetTeamID"), Shipped("MonkeyKingNimbus", "TargetExecute", "BBGetUnitPosition"), spawn],
            target: match.Ally));
        var clone = units.Single();
        match.Advance(0.3);
        TestAssert.Equal(MinionAiState.Idle, clone.MinionState, "the clone stands in AI_IDLE");

        match.Foe.Position = clone.Position + new Position2(150, 0);
        match.Advance(0.1);
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, hero.NetId, 10.0, DamageType.Magic, DamageSourceKind.Spell);
        match.Advance(0.2);
        TestAssert.Equal((uint?)null, clone.AttackTarget, "a single-target spell calls nobody");

        TestAssert.True(match.Runtime.Combat.ApplyHit(match.Foe.NetId, hero.NetId, 10.0, DamageType.Physical, DamageSourceKind.Basic),
            "the foe's basic attack lands on the clone's hero");
        match.Advance(0.2);
        TestAssert.Equal((uint?)match.Foe.NetId, clone.AttackTarget, "the call for help turned the clone on the foe (Aggro OnCallForHelp)");
        TestAssert.Equal(MinionAiState.Engage, clone.MinionState, "in AI_ATTACKMOVE_ATTACKING");
        NoFailure(match, "after the call");
    }

    private static double WorldDistance(Position2 first, Position2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) + (first.Z - second.Z) * (first.Z - second.Z));
}
