using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Bots;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 5 (25 October 2026, batch 243): Malphite plays from his shipped scripts in a production <c>MatchRuntime</c>; no hand-written module ever
/// existed. Expected numbers are the shipped spell inibins (rank one unless a check says otherwise) and the script constants (Granite Shield's 10 percent
/// of maximum health, the six seconds of Brutal Strikes, the four seconds of Seismic Shard, the 400 radius of the stun and the armor-scaled Ground Slam).
/// The shield is the script's own: it is read from the health a hit leaves (the native only sends the displayed shield). The scenario <c>S-malphite</c>
/// shows the same facts on a headless match. The Chaos foe stands 300 units east, the far enemy out of reach.
/// </summary>
public static class BbMalphiteChecks
{
    // The "every native is bound" check leaves the shared system buffs out, as the Lee Sin, Janna and Kayle checks do: they are the shipped system scripts.
    private static readonly string[] SystemBuffs =
    [
        "APBonusDamageToTowers", "ChampionChampionDelta", "PositiveChampionDelta", "NegativeChampionDelta",
        "DisconnectTimer", "DisconnectTarget", "FortifyCheck", "FortifyBuff",
    ];

    private static void Near(double expected, double actual, double tolerance, string message) =>
        TestAssert.True(Math.Abs(actual - expected) <= tolerance, $"{message}: expected {expected:0.###} within {tolerance:0.###}, got {actual:0.###}");

    // The rig gives every unit 5000 health and leaves the stat block of the victims unrefreshed (their first refresh would clamp the health at
    // once): refresh each unit's stats and start it at full.
    private static void Full(Match match)
    {
        foreach (var unit in new[] { match.Hero, match.Ally, match.Foe, match.FarFoe })
        {
            match.Runtime.HeroStats.Refresh(unit, push: false);
            unit.Health = unit.MaxHealth;
        }
    }

    // A physical or magic hit of the foe on Malphite: the health it takes at once (no time passes, so no regeneration in between).
    private static double Hit(Match match, double amount, DamageType type = DamageType.Physical)
    {
        var before = match.Hero.Health;
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, match.Hero.NetId, amount, type, DamageSourceKind.Spell);
        var loss = before - match.Hero.Health;
        match.Hero.Health = match.Hero.MaxHealth;
        return loss;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Closure and registration
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-malphite-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Malphite");
        var hero = new Entity { ChampionName = "Malphite" };
        TestAssert.Equal(["SeismicShard", "Obduracy", "Landslide", "UFSlash"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Malphite.inibin (Data*Spell2 is Obduracy, Brutal Strikes; Data*Spell3 is Landslide, Ground Slam)");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "ranks");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Malphite");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(24, module.ScriptNames.Count, "the Malphite closure is 24 scripts (audit bb-coverage)");
        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => !SystemBuffs.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native a Malphite hook runs is bound: " + string.Join(", ", unbound));
    }

    [Check("scripting.bb-malphite-production-runs-the-shipped-scripts")]
    public static void ProductionRegistersTheScriptModule()
    {
        using var match = new BbM3.Match("Malphite");
        TestAssert.True(match.Runtime.Dependencies.ChampionDispatcher.FindModule("Malphite") is BbChampionModule,
            "production registers BbChampionModule(\"Malphite\") and no hand-written module");
    }

    [Check("scripting.bb-malphite-bot-loadout-is-his-shipped-bot-data")]
    public static void BotLoadoutIsTheShippedOne()
    {
        // The retail bot (game_bot_Malphite) reads his MalphiteBot*.inibin loadout; the bot drives the same module (its casts go through the spell
        // dispatch), and his shipped Scripts folder holds no TaskCastSpell, so the cast template is the reconstructed one (NOT RETAIL, batch 160).
        TestAssert.True(BotLoadoutReader.Read(GameData.Default.Paths, "Malphite", 1) is not null, "MalphiteBot*.inibin loads for Malphite");
        TestAssert.True(BotController.SupportedChampions.Contains("Malphite"), "Malphite is a bot champion of the catalog");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Kit
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-malphite-granite-shield-absorbs-a-tenth-of-his-health-and-recharges-after-ten-quiet-seconds")]
    public static void GraniteShieldBreaksAndRecharges()
    {
        using var match = new BbM3.Match("Malphite");
        Full(match);
        match.Advance(1.0);
        TestAssert.True(Buff(match.Hero, "MalphiteShield") is not null, "the passive is on from the start");
        TestAssert.True(Buff(match.Hero, "MalphiteShieldEffect") is not null, "the shield is up from the start");
        var whole = Hit(match, 400.0);
        var down = Hit(match, 400.0);
        var absorbed = down - whole;
        var tenth = 0.1 * match.Hero.MaxHealth;
        // Batch 252 (open item 1 of batch 243): the armor acts before MalphiteShieldEffect's BuffOnPreDamage, which takes ShieldHealth (0.1 x
        // GetMaxHealth) off the DamageAmount it sees, so with no talent page (this harness) the health difference is the shield exactly. The
        // scenario's 139.3 of 145.1 is its talent page: the masteries' CharOnPreDamage multiplier (Talents/125, x 0.96; 145.1 x 0.96 = 139.3) runs after the buffs' stage
        // (MatchGameplayHooks.ModifyDamageAfterMitigationBeforeShields), so it scales what the shield let through, not the shield.
        Near(tenth, absorbed, 0.01, $"the shield absorbs 10 percent of the maximum health ({tenth:0.##}); absorbed {absorbed:0.##}");
        match.Advance(9.5);
        var stillDown = Hit(match, 400.0);
        TestAssert.True(Math.Abs(stillDown - down) < 1.0, $"nine and a half seconds after the last hit the shield is still down: {stillDown:0.#} vs {down:0.#}");
        match.Advance(10.5);
        var recharged = Hit(match, 400.0);
        TestAssert.True(Math.Abs(recharged - whole) < 1.0, $"ten and a half quiet seconds later the shield is whole again: {recharged:0.#} vs {whole:0.#}");
    }

    [Check("scripting.bb-malphite-granite-shield-is-gone-when-he-dies-and-back-at-the-respawn")]
    public static void GraniteShieldSurvivesTheRespawn()
    {
        using var match = new BbM3.Match("Malphite");
        match.Advance(1.0);
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        match.Runtime.Combat.ScheduleRespawn(match.Hero);
        TestAssert.True(Buff(match.Hero, "MalphiteShieldEffect") is null, "the shield goes with his death");
        match.Advance(0.5);
        TestAssert.False(match.Hero.Alive, "Malphite is dead");
        // Batch 336 (restated from batch 243, which read the engine of the time): CharScriptMalphite's UpdateSelfBuffActions adds
        // MalphiteShieldEffect whenever MalphiteShieldBeenHit is off and has no dead test, and a character script's poll runs on the corpse
        // with its adds landing (Corki, Malzahar, Mordekaiser, Urgot, Vladimir and Cassiopeia add their death particle under CO_IS_DEAD; eleven
        // others, Blitzcrank to Zilean, guard their poll's adds with CO_IS_NOT_DEAD). So the shield is back on the corpse at the next poll.
        TestAssert.True(Buff(match.Hero, "MalphiteShieldEffect") is not null, "his own poll puts the shield back on the corpse");
        TestAssert.True(Buff(match.Hero, "MalphiteShield") is not null, "the passive stays on the corpse");
        match.Advance(90.0);
        TestAssert.True(match.Hero.Alive, "Malphite is back after the respawn timer");
        TestAssert.True(Buff(match.Hero, "MalphiteShieldEffect") is not null, "the shield is up again at the respawn");
        Near(match.Hero.MaxHealth, match.Hero.Health, 0.5, "the respawn heals him to full");
    }

    [Check("scripting.bb-malphite-seismic-shard-hits-for-70-and-steals-fourteen-percent-of-movement-speed-for-four-seconds")]
    public static void SeismicShardStealsMovementSpeed()
    {
        using var match = new BbM3.Match("Malphite");
        match.Learn(1);
        match.Refill();
        Full(match);
        var mana = match.Hero.Mana;
        var foeHealth = match.Foe.Health;
        var foeSpeed = match.Foe.Speed;
        var ownSpeed = match.Hero.Speed;
        TestAssert.True(match.Cast(0, match.Foe), "Seismic Shard is cast at a foe 300 away");
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press.
        match.Advance(match.CastFrameOf(0));
        Near(70.0, mana - match.Hero.Mana, 0.5, "rank one costs 70 mana (the cast frame takes it)");
        Near(8.0, match.Hero.SpellCooldowns[0], Step + 1e-6, "rank one cooldown is 8 seconds (no cooldown reduction here)");
        match.Advance(1.0 - match.CastFrameOf(0));
        var damage = foeHealth - match.Foe.Health;
        Near(70.0 * 100.0 / (100.0 + match.Foe.SpellBlock), damage, 0.05 * 70.0, "the hit is 70 magic damage less the foe's resistance");
        var stolen = foeSpeed - match.Foe.Speed;
        Near(0.14 * foeSpeed, stolen, 1.0, "the foe loses 14 percent of its movement speed (rank one)");
        Near(stolen, match.Hero.Speed - ownSpeed, 1.0, "Malphite gains the same amount");
        match.Advance(2.5);
        TestAssert.True(match.Foe.Speed < foeSpeed - 0.5 * stolen, "still slowed at 3.5 seconds (four seconds from the hit)");
        match.Advance(2.0);
        Near(foeSpeed, match.Foe.Speed, 0.5, "the foe's movement speed is back after four seconds");
        Near(ownSpeed, match.Hero.Speed, 0.5, "Malphite's movement speed is back");
    }

    /// <summary>
    /// Batch 331: SeismicShard's TargetExecute deals its hit as DAMAGESOURCE_DEFAULT. The client's BBApplyDamage body
    /// hands a DEFAULT, SPELL or ATTACK hit to the health apply-damage routine with the running cast when there is one (
    /// the damage-effect constructor: DEFAULT becomes ATTACK for an auto attack's cast, SPELL for any other), so a spell's own execute
    /// hook deals a spell hit, not an uncategorised one, and the call's SpellDamageRatio 0.6 counts (70 + 0.6 AP at rank one).
    /// </summary>
    [Check("scripting.bb-malphite-seismic-shard-default-source-is-a-spell-hit")]
    public static void SeismicShardDefaultSourceIsASpellHit()
    {
        using var match = new BbM3.Match("Malphite");
        match.Learn(1);
        match.GiveAbilityPower();
        match.Refill();
        Full(match);
        TestAssert.True(match.Cast(0, match.Foe), "Seismic Shard is cast at a foe 300 away");
        match.Advance(1.0);
        var hit = HitsOn(match, "Foe").Single();
        TestAssert.Equal("Spell", hit.Kind, $"the shard's DEFAULT hit is a spell hit (log {string.Join(" | ", match.Log.Hits)})");
        Near(70.0 + 0.6 * match.Hero.AbilityPower, hit.Raw, 0.5, "the hit is 70 plus 0.6 of his ability power before resistance");
    }

    [Check("scripting.bb-malphite-brutal-strikes-adds-twenty-percent-attack-damage-and-armor-for-six-seconds")]
    public static void BrutalStrikesBuffsForSixSeconds()
    {
        using var match = new BbM3.Match("Malphite");
        match.Learn(1);
        match.Refill();
        Full(match);
        var damage = match.Hero.AttackDamage;
        var armor = match.Hero.Armor;
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(1, match.Hero), "Brutal Strikes is cast on himself");
        Near(50.0, mana - match.Hero.Mana, 0.5, "rank one costs 50 mana (Brutal Strikes)");
        match.Advance(0.3);
        Near(1.2 * damage, match.Hero.AttackDamage, 0.01 * damage, "attack damage +20 percent");
        Near(1.2 * armor, match.Hero.Armor, 0.01 * armor, "armor +20 percent");
        match.Advance(5.5);
        Near(1.2 * damage, match.Hero.AttackDamage, 0.01 * damage, "still +20 percent at 5.8 seconds");
        match.Advance(1.0);
        Near(damage, match.Hero.AttackDamage, 0.01 * damage, "attack damage back after six seconds");
        Near(armor, match.Hero.Armor, 0.01 * armor, "armor back after six seconds");
    }

    [Check("scripting.bb-malphite-brutal-cleave-splashes-the-units-beside-his-melee-target")]
    public static void BrutalCleaveReachesTheUnitsBeside()
    {
        using var match = new BbM3.Match("Malphite");
        match.Learn(1);
        match.Refill();
        Full(match);
        match.Ally.Position = new Position2(6000, 6000);
        match.Foe.Position = new Position2(7140, 7000);
        match.FarFoe.Position = new Position2(7140, 7120);
        var foeHealth = match.Foe.Health;
        var sideHealth = match.FarFoe.Health;
        match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe);
        match.Advance(2.5);
        var plain = sideHealth - match.FarFoe.Health;
        TestAssert.True(match.Foe.Health < foeHealth, "the swing hits the melee target");
        TestAssert.True(plain > 0.0, "the cleave reaches the unit 120 units beside the target");
        match.Refill();
        match.Hero.Health = match.Hero.MaxHealth;
        match.Cast(1, match.Hero);
        match.Advance(0.3);
        match.Foe.Health = match.Foe.MaxHealth;
        match.FarFoe.Health = match.FarFoe.MaxHealth;
        match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe);
        match.Advance(2.5);
        var strikes = match.FarFoe.MaxHealth - match.FarFoe.Health;
        // The cleave is a share of the attack damage, and Brutal Strikes adds twenty percent to that damage: the splash grows with it.
        TestAssert.True(strikes / plain > 1.1 && strikes / plain < 1.3,
            $"the splash grows with his attack damage under Brutal Strikes (+20 percent): {strikes:0.#} vs {plain:0.#}");
    }

    [Check("scripting.bb-malphite-ground-slam-hits-the-area-and-slows-attack-speed-for-four-seconds")]
    public static void GroundSlamHitsTheAreaAndSlows()
    {
        using var match = new BbM3.Match("Malphite");
        match.Learn(1);
        match.Refill();
        Full(match);
        match.FarFoe.Position = new Position2(7000, 7700);
        var armor = match.Hero.Armor;
        var foeHealth = match.Foe.Health;
        var farHealth = match.FarFoe.Health;
        var foeSpeed = match.Foe.AttackSpeedMod;
        var farSpeed = match.FarFoe.AttackSpeedMod;
        TestAssert.True(match.Cast(2, match.Hero), "Ground Slam is cast on himself");
        // Batch 340: the cooldown starts at the cast frame, not the press.
        match.Advance(match.CastFrameOf(2));
        Near(8.0, match.Hero.SpellCooldowns[2], Step + 1e-6, "rank one cooldown is 8 seconds");
        match.Advance(1.0 - match.CastFrameOf(2));
        var damage = foeHealth - match.Foe.Health;
        var raw = 60.0 + 0.5 * armor;
        Near(raw * 100.0 / (100.0 + match.Foe.SpellBlock), damage, 0.12 * raw, "60 plus half his armor, less the foe's resistance");
        // Batch 252 (open item 2 of batch 243): the committed hit is plain resistance arithmetic (the health window above also holds a second of
        // regeneration). The scenario's 8 percent shortfall is its talent pages and runes, not the damage rules.
        var hit = HitsOn(match, "Foe").Single();
        Near(raw * 100.0 / (100.0 + match.Foe.SpellBlock), hit.Dealt, 1e-3, $"the hit is the plain arithmetic (raw {hit.Raw:0.###}, dealt {hit.Dealt:0.###})");
        Near(raw, hit.Raw, 1e-3, "the raw amount is 60 plus half his armor");
        TestAssert.True(match.FarFoe.Health == farHealth, "the unit 700 away (beyond the 400 area) is not hit");
        TestAssert.True(match.FarFoe.AttackSpeedMod == farSpeed, "and not slowed");
        Near(0.7 * foeSpeed, match.Foe.AttackSpeedMod, 0.02, "the foe's attack speed is reduced by 30 percent (rank one)");
        match.Advance(3.5);
        Near(foeSpeed, match.Foe.AttackSpeedMod, 0.01, "the slow is over four seconds after the landing");
    }

    [Check("scripting.bb-malphite-unstoppable-force-charges-to-the-point-and-stuns-the-enemy-it-lands-near")]
    public static void UnstoppableForceChargesAndStuns()
    {
        using var match = new BbM3.Match("Malphite");
        match.Learn(1);
        match.Refill();
        Full(match);
        var start = match.Hero.Position;
        var point = new Position2(start.X + 500.0, start.Z);
        var foeHealth = match.Foe.Health;
        TestAssert.True(match.Cast(3, at: point), "Unstoppable Force is cast at a point 500 away");
        Near(130.0, match.Hero.SpellCooldowns[3], 0.01, "rank one cooldown is 130 seconds");
        match.Advance(1.0);
        TestAssert.True(Math.Sqrt(Math.Pow(match.Hero.Position.X - point.X, 2) + Math.Pow(match.Hero.Position.Z - point.Z, 2)) < 15.0, "the charge ends at the point");
        var damage = foeHealth - match.Foe.Health;
        Near(200.0 * 100.0 / (100.0 + match.Foe.SpellBlock), damage, 0.12 * 200.0, "200 magic damage (rank one) less the foe's resistance");
        TestAssert.True(Buff(match.Foe, "UnstoppableForceStun") is not null, "the enemy it lands near is stunned");
        match.Advance(2.0);
        TestAssert.True(Buff(match.Foe, "UnstoppableForceStun") is null, "the stun is over one and a half seconds after the landing");
    }
}
