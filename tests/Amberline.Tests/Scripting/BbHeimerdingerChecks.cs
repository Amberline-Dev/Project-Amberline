using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 9 (26 October 2026, batch 339): Heimerdinger plays from his shipped scripts in a production <c>MatchRuntime</c>. Expected numbers are
/// the rank-one literals of Heimerdinger.inibin and his four spell inibins (cost, cooldown, the effects), the shipped tooltips of fontconfig_en_US and
/// the shipped scripts' constants (the turret's 260 health and 15 health per owner level, the stockpile's 25 seconds, the 10 percent passive, the
/// 1000 detonation, the rocket and grenade damage). The scenario <c>S-heimerdinger</c> shows the same facts on a headless match. Ability power is zero in
/// every arena, so each damage is its base amount after the foe's resistance.
/// </summary>
public static class BbHeimerdingerChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    /// <summary>
    /// A match with Heimerdinger at level six and rank one of every spell, learnt through <c>RankUp</c> as the game does it: the first rank of Q
    /// grants the stockpile's part (CharOnActivateBuildingBlocks) and the ultimate's rank starts the passive (UPGRADE!!!). Every foe has 30 magic resistance.
    /// </summary>
    private static Match Arena()
    {
        var match = new Match("Heimerdinger");
        match.Hero.Level = 6;
        match.Hero.SkillPoints = 6;
        match.Hero.SpellRanks = [0, 0, 0, 0];
        for (var slot = 0; slot < 4; slot++)
            TestAssert.True(match.Runtime.Spells.RankUp(match.Hero, slot), $"slot {slot} is learnt at level six");
        match.Foe.SpellBlock = 30.0;
        match.FarFoe.SpellBlock = 30.0;
        match.Refill();
        return match;
    }

    /// <summary>The turrets the hero's scripts placed: the H-28G turrets are Minion.lua units owned by him.</summary>
    private static List<Entity> Turrets(Match match) =>
        match.Runtime.World.Entities.Values.Where(unit => unit.AiScript == "Minion.lua" && unit.OwnerNetId == match.Hero.NetId && unit.Alive).ToList();

    private static int Stock(Entity hero) => hero.Buffs.FirstOrDefault(buff => buff.Name == "HeimerdingerTurretReady") is { } ready ? ready.Stacks : 0;

    /// <summary>A point beside the hero on the foe's side, 150 units from him (inside the 250 cast range of the turret).</summary>
    private static Position2 TurretSpot(Match match) => new(match.Hero.Position.X + 150.0, match.Hero.Position.Z);

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-heimerdinger-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Heimerdinger");
        foreach (var name in new[] { "CharScriptHeimerdinger", "H28GEvolutionTurret", "HextechMicroRockets", "CH1ConcussionGrenade", "UPGRADE!!!",
                     "TechmaturgicalRepairBots", "HeimerdingerTurretReady", "HeimerdingerTurretTimer", "UpgradeBuff", "ExplosiveCartridges", "UrAniumRounds" })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Heimerdinger");
        TestAssert.Equal(66, closure.ScriptNames.Count(), "the closure is 66 scripts (the character, the four spells and their turrets, missiles and dummies, the passive, the upgrade and the system buffs)");

        var hero = new Entity { ChampionName = "Heimerdinger" };
        TestAssert.Equal(["H28GEvolutionTurret", "HextechMicroRockets", "CH1ConcussionGrenade", "UPGRADE!!!"], Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(),
            "spell names come from Heimerdinger.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "three spells of five ranks, the ultimate of three");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(), "UPGRADE!!! is learnt at 6, 11, 16 (the stock table)");

        var info = ChampionCatalog.ByName("Heimerdinger")!;
        TestAssert.True(info is { Playable: true, Role: "mid", DefaultIp: 3150, RetailBot: false }, "playable as a mid for 3150 IP, not a retail bot");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Heimerdinger"), "listed once");
    }

    // ------------------------------------------------------------------------------------------------------------
    // H-28G Evolution Turret (Q): the stockpile, the turret, the replacement
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-heimerdinger-h28g-costs-70-mana-and-needs-a-stockpiled-part")]
    public static void TurretCostsManaAndNeedsAPart()
    {
        using var match = Arena();
        TestAssert.Equal(1, Stock(match.Hero), "the first rank grants one part of the stockpile (CharOnActivateBuildingBlocks)");
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(0, at: TurretSpot(match)), "Q casts with a part in the stockpile");
        // Batch 346 (batch 340's rule): the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), CastFrameOf after the press.
        match.Advance(match.CastFrameOf(0));
        TestAssert.Near(70.0, mana - match.Hero.Mana, 1.0, "70 mana at rank one (the cast frame takes it)");
        match.Advance(0.3 - match.CastFrameOf(0)); // the part is taken when the cast lands (the turret's script), a quarter of a second after the press
        TestAssert.Equal(0, Stock(match.Hero), "the part is spent");
        TestAssert.False(match.Cast(0, at: TurretSpot(match)), "a second cast with an empty stockpile is refused (CanCastBuildingBlocks)");
        TestAssert.Equal(1, Turrets(match).Count, "one turret stands");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-heimerdinger-h28g-turret-is-immobile-with-260-health-and-15-per-level")]
    public static void TurretStatsFollowTheOwnerLevel()
    {
        using var match = Arena();
        match.Cast(0, at: TurretSpot(match));
        match.Advance(0.3);
        var turret = Turrets(match).Single();
        TestAssert.Equal("HeimerTYellow", turret.StatCharacterName, "the first turret is the yellow skin (HeimerTYellow.inibin)");
        TestAssert.Near(260.0 + 15.0 * match.Hero.Level, turret.MaxHealth, 0.5, "260 health plus 15 per level of its owner (BonusHealthPreLevel4)");
        TestAssert.Near(0.0, turret.Speed, 1e-9, "no movement speed");
        TestAssert.False(CharacterState.Of(turret).CanMove, "it cannot move (SetCanMove off)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-heimerdinger-turret-attacks-the-foe-in-reach")]
    public static void TurretAttacksTheFoeInReach()
    {
        using var match = Arena();
        match.Cast(0, at: TurretSpot(match));
        match.Advance(0.3);
        var turret = Turrets(match).Single();
        match.Foe.Position = new Position2(turret.Position.X + 350.0, turret.Position.Z);
        var health = match.Foe.Health;
        match.Advance(3.0);
        TestAssert.True(health - match.Foe.Health > 15.0, $"the turret hits the foe 350 away within three seconds (lost {health - match.Foe.Health})");
        TestAssert.True(turret.Alive, "the turret stands");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// The turret runs its shipped AI, <c>Minion.luaobj</c> with the <c>minionLib.luaobj</c> it loads, on the spawned-unit Lua host (batch 339):
    /// OnInit writes AI_IDLE and arms TimerMoveForward (0 s) and TimerFindEnemies (0.25 s); TimerMoveForward's FindTargetOrMove takes the enemy
    /// FindTargetInAcR returns (AI_ATTACKMOVE_ATTACKING) or, with none, the forward navigation a unit with no lane answers by standing
    /// (AI_ATTACKMOVESTATE). An enemy that walks out of its reach is a target it cannot close on (no movement speed): the blocked path
    /// (minionLib's OnPathToTargetBlocked: AddToIgnore, FindTargetOrMove) and TimerFindEnemies' empty scan (TurnOffAutoAttack TARGET_LOST) drop it.
    /// </summary>
    [Check("scripting.bb-heimerdinger-turret-runs-the-shipped-minion-script")]
    public static void TurretRunsTheShippedMinionScript()
    {
        using var match = Arena();
        var errors = new List<string>();
        match.Runtime.Tibbers.Host.OnError = failure => errors.Add($"{failure.Script}.{failure.Hook}: {failure.Message}");
        match.Foe.Position = new Position2(match.Hero.Position.X, match.Hero.Position.Z + 2000.0);
        match.Cast(0, at: TurretSpot(match));
        match.Advance(0.6);
        var turret = Turrets(match).Single();
        TestAssert.True(match.Runtime.Tibbers.Host.ExistingInstanceFor(turret) is not null, "the spawned-unit Lua host drives the turret");
        TestAssert.Equal(MinionAiState.March, turret.MinionState, "nothing in reach: FindTargetOrMove's forward navigation (AI_ATTACKMOVESTATE)");
        TestAssert.Equal((uint?)null, turret.AttackTarget, "and no target");

        match.Foe.Position = new Position2(turret.Position.X + 350.0, turret.Position.Z);
        var health = match.Foe.Health;
        match.Advance(1.5);
        TestAssert.Equal(MinionAiState.Engage, turret.MinionState, "TimerFindEnemies engaged the foe in reach (AI_ATTACKMOVE_ATTACKING)");
        TestAssert.Equal(match.Foe.NetId, turret.AttackTarget, "the foe is its target");
        TestAssert.True(health - match.Foe.Health > 15.0, $"and it hits her (lost {health - match.Foe.Health})");

        // Out of its reach (525) but in sight and well inside MAX_ENGAGE_DISTANCE (2500): only the blocked path lets it go.
        match.Foe.Position = new Position2(turret.Position.X, turret.Position.Z + 800.0);
        match.Advance(0.1);
        TestAssert.True(match.Runtime.Spells.CanSeeTarget(turret, match.Foe), "the foe stands in his team's sight");
        match.Advance(1.0);
        TestAssert.Equal((uint?)null, turret.AttackTarget, "the foe out of its reach is dropped");
        TestAssert.Equal(MinionAiState.March, turret.MinionState, "back to AI_ATTACKMOVESTATE");
        TestAssert.True(turret.Position.X > 0.0 && turret.Waypoints.Count == 0, "it never walks");
        TestAssert.Equal(0, errors.Count, "Minion.luaobj ran with no failure: " + string.Join(" | ", errors));
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// Batch 348, pinned (passes before and after): patch V1.0.0.120 (22 June 2011, wiki) "Fixed a bug where his turrets would not assist him if
    /// damage from an enemy champion was absorbed by shields". The assist against a champion is his own script's: TechmaturgicalIcon's
    /// BuffOnPreDamage, which runs before his shields, taunts every turret within 450 of a hero attacker (H28GEvolutionTurretSpell1). The foe's
    /// attack lands wholly on a shield of Heimerdinger's and the turret turns to her all the same.
    /// </summary>
    [Check("scripting.bb-heimerdinger-turret-assists-him-through-a-shield")]
    public static void TurretAssistsHimThroughAShield()
    {
        using var match = Arena();
        match.Foe.Position = new Position2(match.Hero.Position.X, match.Hero.Position.Z + 2000.0);
        match.Cast(0, at: TurretSpot(match));
        match.Advance(0.6);
        var turret = Turrets(match).Single();
        var dummy = Enemy(match, "Dummy", new Position2(turret.Position.X + 300.0, turret.Position.Z));
        match.Advance(1.0);
        TestAssert.Equal(dummy.NetId, turret.AttackTarget, "the turret fights the dummy minion in its reach");

        var shield = new BuffState("TestShield", new BuffPayload { DamageShield = 500.0 }, expiresAt: match.Now + 60.0);
        match.Hero.Buffs.Add(shield);
        var health = match.Hero.Health;
        match.Foe.Position = new Position2(turret.Position.X, turret.Position.Z + 400.0);
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, match.Hero.NetId, 10.0, DamageType.Physical, DamageSourceKind.Basic);
        TestAssert.Near(health, match.Hero.Health, 1e-9, "the shield took the whole hit");
        TestAssert.True(shield.Payload.DamageShield < 500.0, "the shield paid for it");
        match.Advance(0.25);
        TestAssert.Equal(match.Foe.NetId, turret.AttackTarget, "the turret assists him all the same");
    }

    [Check("scripting.bb-heimerdinger-stockpile-returns-after-its-timer")]
    public static void StockpileReturnsAfterTheTimer()
    {
        using var match = Arena();
        match.Cast(0, at: TurretSpot(match));
        match.Advance(15.0);
        TestAssert.Equal(0, Stock(match.Hero), "the part is still in the timer after fifteen seconds");
        match.Advance(11.0);
        TestAssert.Equal(1, Stock(match.Hero), "the part is back after its timer (25 seconds, less the passive when UPGRADE!!! is learnt)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-heimerdinger-turrets-detonate-with-their-owner")]
    public static void TurretsDieWithHeimerdinger()
    {
        using var match = Arena();
        match.Cast(0, at: TurretSpot(match));
        match.Advance(0.3);
        var turret = Turrets(match).Single();
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        TestAssert.False(match.Hero.Alive, "Heimerdinger dies");
        TestAssert.False(turret.Alive, "his turret detonates with him (HeimerdingerTurretDetonation, 1000 damage to each of his turrets)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Hextech Micro-Rockets (W), CH-1 Concussion Grenade (E), UPGRADE!!! (R), the passive
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-heimerdinger-micro-rockets-cost-65-mana-and-hit-the-foe")]
    public static void MicroRocketsCostAndHit()
    {
        using var match = Arena();
        var mana = match.Hero.Mana;
        var health = match.Foe.Health;
        TestAssert.True(match.Cast(1), "W casts on himself (TTYPE_Self)");
        TestAssert.Near(65.0, mana - match.Hero.Mana, 1.5, "65 mana at rank one");
        match.Advance(1.5);
        var lost = health - match.Foe.Health;
        TestAssert.True(lost > 55.0 && lost < 70.0, $"the foe takes one rocket's 85 magic damage after 30 resistance (about 65; lost {lost})");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// HextechMicroRockets' SelfExecuteBuildingBlocks: <c>BBForNClosestUnitsInTargetArea</c> (centre Heimerdinger, Range 1000, enemies, neutral,
    /// minions and heroes, MaximumUnitsToPick 3) and, for each unit he can see (<c>BBCanSeeTarget</c>), one HextechMicroRocketsMissile
    /// (ExtraSlots 1). One rocket per unit picked, never two on one. The production probe placed its second enemy 950 east of Heimerdinger, at
    /// (7950, 7000): a wall cell of Map1, where his team cannot see it, so <c>BBCanSeeTarget</c> sends it no rocket (the script's rule). On open
    /// ground beside the foe both enemies take one rocket each.
    /// </summary>
    [Check("scripting.bb-heimerdinger-micro-rockets-hit-each-of-the-closest-enemies-once")]
    public static void MicroRocketsHitEachEnemy()
    {
        using var match = Arena();
        int Rockets(string unit) => match.Log.Damage.Count(line => line.Contains($"Heimerdinger->{unit} 85 Magic"));
        string Trace() => $"damage [{string.Join("; ", match.Log.Damage)}]";
        match.Foe.Position = new Position2(match.Hero.Position.X + 900.0, match.Hero.Position.Z);
        match.FarFoe.Position = new Position2(match.Hero.Position.X + 950.0, match.Hero.Position.Z);
        match.Advance(0.1);
        TestAssert.False(match.Runtime.Spells.CanSeeTarget(match.Hero, match.FarFoe), "the enemy in the wall at (7950, 7000) is out of his team's sight");
        TestAssert.True(match.Cast(1), "W casts on himself");
        match.Advance(2.0);
        TestAssert.Equal(1, Rockets("Foe"), $"one rocket on the foe; {Trace()}");
        TestAssert.Equal(0, Rockets("FarFoe"), $"none on the enemy he cannot see (BBCanSeeTarget); {Trace()}");

        match.Refill();
        match.Foe.Position = new Position2(match.Hero.Position.X + 300.0, match.Hero.Position.Z);
        match.FarFoe.Position = new Position2(match.Hero.Position.X + 350.0, match.Hero.Position.Z + 60.0);
        match.Advance(0.1);
        TestAssert.True(match.Cast(1), "W casts again");
        match.Advance(1.5);
        TestAssert.Equal(2, Rockets("Foe"), $"one more rocket on the foe; {Trace()}");
        TestAssert.Equal(1, Rockets("FarFoe"), $"one rocket on the second enemy on open ground; {Trace()}");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-heimerdinger-upgrade-heals-the-turrets-and-slows-their-shots")]
    public static void UpgradeHealsTurretsAndSlows()
    {
        using var match = Arena();
        match.Cast(0, at: TurretSpot(match));
        match.Advance(0.3);
        var turret = Turrets(match).Single();
        turret.Health = turret.MaxHealth * 0.5;
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(3, at: match.Hero.Position), "UPGRADE!!! casts at rank one");
        // Batch 346 (batch 340's rule): the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), CastFrameOf after the press.
        match.Advance(match.CastFrameOf(3));
        TestAssert.Near(90.0, mana - match.Hero.Mana, 1.5, "90 mana at rank one (the cast frame takes it)");
        match.Advance(0.5 - match.CastFrameOf(3));
        TestAssert.Near(turret.MaxHealth, turret.Health, 0.5, "every active turret is healed to its maximum");
        TestAssert.True(turret.Buffs.Any(buff => buff.Name == "UpgradeSlow"), "the turret fires movement-slowing shots (UpgradeSlow)");
        TestAssert.True(match.Hero.Buffs.Any(buff => buff.Name == "UpgradeBuff"), "Heimerdinger carries the upgrade (UpgradeBuff)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-heimerdinger-passive-cuts-cooldowns-by-ten-percent")]
    public static void PassiveCutsCooldowns()
    {
        using var match = Arena();
        match.Advance(0.5);
        var rCooldownBefore = match.Hero.SpellCooldowns[3];
        TestAssert.True(match.Cast(3, at: match.Hero.Position), "UPGRADE!!! casts");
        // Batch 346 (batch 340's rule): the cooldown starts at the cast frame (DoCast_ClientAndServer -> ApplyCostsOfSpell), CastFrameOf after the press.
        match.Advance(match.CastFrameOf(3));
        TestAssert.Near(108.0, match.Hero.SpellCooldowns[3], 0.6, "120 seconds less the passive's 10 percent at rank one (its own cooldown counts from the cast)");
        match.Advance(1.0 - match.CastFrameOf(3));
        TestAssert.True(match.Cast(1, at: match.Hero.Position), "W casts");
        match.Advance(match.CastFrameOf(1));
        TestAssert.Near(9.0, match.Hero.SpellCooldowns[1], 0.6, "W's 10 seconds less 10 percent");
        TestAssert.True(rCooldownBefore == 0.0, "UPGRADE!!! was ready before the cast");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-heimerdinger-concussion-grenade-costs-80-mana")]
    public static void ConcussionGrenadeCosts()
    {
        using var match = Arena();
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(2, at: match.Foe.Position), "E casts at the foe's spot");
        // Batch 346 (batch 340's rule): the cast frame pays and starts the cooldown (DoCast_ClientAndServer -> ApplyCostsOfSpell), CastFrameOf after the press.
        match.Advance(match.CastFrameOf(2));
        TestAssert.Near(80.0, mana - match.Hero.Mana, 1.5, "80 mana at rank one (the cast frame takes it)");
        TestAssert.True(match.Hero.SpellCooldowns[2] is > 11.6 and < 13.1, $"a 13 second cooldown at rank one, less the passive's 10 percent when it is learnt ({match.Hero.SpellCooldowns[2]})");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// CH1ConcussionGrenade spawns the placemarker <c>k</c> (TestCubeRender, idle.lua) on the target point and fires CH1ConcussionGrenadeSpell
    /// (ExtraSlots 0, FireWithoutCasting) from Heimerdinger at it; the missile's TargetExecuteBuildingBlocks hits every enemy within 250 of the
    /// placemarker for 80 magic damage at rank one (DamageByLevel 80/135/190/245/300), blinds them (BlindingDart, DurationByLevel 1/1.5/2/2.5/3) and
    /// stuns those within 125 for 1.5 seconds. The foe stands on the point; the missile flies 300 units at 750 a second (0.4 s).
    /// </summary>
    [Check("scripting.bb-heimerdinger-concussion-grenade-damages-blinds-and-stuns-the-foe")]
    public static void ConcussionGrenadeHits() => GrenadeHitsTheFoeAt(300.0, 0.9);

    /// <summary>
    /// The same grenade near its 925 cast range (the production finding: the placemarker appeared at the foe and expired with no hit). The
    /// missile flies 900 units at 750 a second, 1.2 seconds, and the placemarker's ExpirationTimer (Duration 1.0) kills it at 1.0 s, before the
    /// missile lands: the shipped design (also UPGRADE!!!'s CH1ConcussionGrenadeUpgrade, 1000 a second over its 1000 range) needs the hit to
    /// run at the expired placemarker.
    /// </summary>
    [Check("scripting.bb-heimerdinger-concussion-grenade-hits-near-its-full-range")]
    public static void ConcussionGrenadeHitsAtRange() => GrenadeHitsTheFoeAt(900.0, 1.6);

    private static void GrenadeHitsTheFoeAt(double distance, double wait)
    {
        using var match = Arena();
        match.Foe.Position = new Position2(match.Hero.Position.X + distance, match.Hero.Position.Z);
        var health = match.Foe.Health;
        TestAssert.True(match.Cast(2, at: match.Foe.Position), "E casts at the foe's spot");
        match.Advance(wait);
        var trace = $"damage [{string.Join("; ", match.Log.Damage)}] buffs [{string.Join("; ", match.Log.Buffs)}] effects [{string.Join("; ", match.Log.Effects)}]";
        var lost = health - match.Foe.Health;
        TestAssert.Near(80.0 * 100.0 / 130.0, lost, 1.0, $"80 magic damage after 30 resistance (lost {lost}); {trace}");
        TestAssert.True(match.Foe.Buffs.Any(buff => buff.Name == "BlindingDart"), $"the foe is blinded (BlindingDart); {trace}");
        TestAssert.True(match.Foe.Buffs.Any(buff => buff.Type == BuffType.Stun), $"the foe on the point is stunned; {trace}");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    [Check("scripting.bb-heimerdinger-repair-bots-aura-heals-the-nearby-ally")]
    public static void RepairBotsAuraHealsTheNearbyAlly()
    {
        using var match = Arena();
        match.Ally.Position = new Position2(match.Hero.Position.X + 90.0, match.Hero.Position.Z);
        match.Advance(0.5);
        match.Ally.Health = match.Ally.MaxHealth - 200.0;
        var beside = match.Ally.Health;
        match.Advance(5.0);
        var withAura = match.Ally.Health - beside;
        TestAssert.True(match.Ally.Buffs.Any(buff => buff.Name == "TechmaturgicalRepairBots"), "the ally beside him carries the aura's buff");
        match.Ally.Position = new Position2(9000.0, match.Hero.Position.Z);
        match.Advance(0.5);
        match.Ally.Health = match.Ally.MaxHealth - 200.0;
        var away = match.Ally.Health;
        match.Advance(5.0);
        var withoutAura = match.Ally.Health - away;
        TestAssert.True(withAura - withoutAura > 5.0, $"the aura adds health regeneration on top of the ally's own ({withAura} with it, {withoutAura} without)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }
}
