using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Stats;

namespace Amberline.Tests;

/// <summary>
/// Batch 902 (player report on 1.0.0.132, 19 November 2026: "siege commander mastery doesnt work - check all masteries"). The 1.0.0.132
/// tree (DATA/Talents/TalentTree.xml, ids 211-463) only; the 1.0.0.126 tree has no Siege Commander. Each check records every link of the
/// shipped chain and fails on the first that breaks, so the message is the verdict. Pages are legal 132 pages (row thresholds 0/4/8/12/16/20
/// per group; 232←222, 233←223, 241←231, 332←322, 334←314, 423←413, 442←432 at the prerequisite's maximum rank).
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    private static readonly Position2 B902Mid = new(7000, 7000);

    private static MasteryPage B902Page(Dictionary<int, int> ranks) => MasteryPage.Create(new MasteryCatalog(), ranks);

    private static void B902Advance(Match match, double seconds)
    {
        for (var step = 0; step < (int)Math.Round(seconds * 30.0); step++) match.Runtime.Tick(1.0 / 30.0);
    }

    private static Entity B902EnemyTurret(Match match) => match.Runtime.World.Entities.Values
        .Where(unit => unit.Alive && unit.Team == Team.Chaos && unit.UnitType == UnitType.Turret && !unit.Protected &&
                       !TurretRules.IsShrineTurret(unit))
        .OrderBy(unit => double.Hypot(unit.Position.X - B902Mid.X, unit.Position.Z - B902Mid.Z)).First();

    /// <summary>
    /// Siege Commander (341, "Reduces the Armor of nearby enemy turrets by 10 (does not stack)"). Talents/341.luaobj UpdateSelfBuffActions
    /// writes <c>AvatarVars.MasterySeigeCommander = true</c>; the hero's own <c>APBonusDamageToTowers</c> (every champion's CharOnActivate)
    /// runs <c>BuffOnUpdateActions</c> <c>BBExecutePeriodically</c> 2 s: <c>BBForEachUnitInTargetArea</c> 900 around the Owner,
    /// <c>AffectEnemies AffectTurrets</c>, adds <c>MasterySiegeCommanderDebuff</c> (BUFF_REPLACE_EXISTING, BUFF_Internal, 2 s) whose
    /// <c>BuffOnUpdateStats</c> is <c>BBIncStat IncFlatArmorMod -10</c>. Links: (a) the flag on the table the champion router reads, (b) the
    /// debuff on the turret, (c) the turret's armour 10 lower once applied, (d) a physical hit on the turret is mitigated by that armour,
    /// (e) the turret block a client receives carries it, (f) a second wearer does not stack it, (g) the armour comes back once the wearers
    /// leave. Before batch 886 (release 132 0.1.7 and earlier) the scan found no turret, so (b) failed.
    /// <para>
    /// Batch 906 (strict since then): a buff set during a step was also aged by that step's buff timer, so the 2 s debuff ended one step
    /// before the 2 s periodic re-added it (the turret read 200 for one step in sixty). Now a timer set in a step starts counting at the
    /// next step (<c>BuffLifecycle.TickBuffs</c>), and the armour is sampled on every step after the first add: no step may lack the debuff.
    /// </para>
    /// </summary>
    [Check("masteries-live-b902-p132-siege-commander-takes-ten-armour-off-enemy-turrets-in-900")]
    public static void B902SiegeCommander()
    {
        var page = B902Page(new() { [311] = 1, [313] = 3, [314] = 2, [322] = 4, [333] = 1, [334] = 1, [341] = 1 });
        using var match = new Match("Annie", Team.Order, ally: true, allyChampion: "Annie", masteries: page);
        var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1); var ally = match.Hero(2);
        enemy.Position = new Position2(3000, 11500);
        ally.Position = new Position2(1500, 1500);
        var turret = B902EnemyTurret(match);
        var baseArmor = turret.Armor;
        // 850 from the turret's centre toward the map's middle: inside the 900 scan, outside the turret's own reach.
        var dx = B902Mid.X - turret.Position.X; var dz = B902Mid.Z - turret.Position.Z; var length = double.Hypot(dx, dz);
        Position2 At(double distance) => new(turret.Position.X + dx / length * distance, turret.Position.Z + dz / length * distance);
        hero.Position = At(850.0);
        hero.MaxHealth = hero.Health = 100000.0;

        var failures = new List<string>();
        var flag = runtime.Spells.LoadoutHooks?.AvatarVars(hero)?["MasterySeigeCommander"];
        if (flag is not true) failures.Add($"(a) AvatarVars.MasterySeigeCommander = {flag ?? "nil"}");
        if (!hero.Buffs.Any(buff => buff.Name == "APBonusDamageToTowers")) failures.Add("the hero carries no APBonusDamageToTowers");

        bool Debuffed() => turret.Buffs.Any(buff => buff.Name == "MasterySiegeCommanderDebuff");
        double? firstAt = null; var lapse = 0; var longestLapse = 0; var wrongArmor = new List<string>();
        for (var step = 0; step < 6 * 30; step++)
        {
            runtime.Tick(1.0 / 30.0);
            if (Debuffed()) firstAt ??= step / 30.0;
            if (firstAt is null) continue;
            lapse = Debuffed() ? 0 : lapse + 1;
            longestLapse = Math.Max(longestLapse, lapse);
            if (Math.Abs(turret.Armor - (baseArmor - 10.0)) > 1e-6 && wrongArmor.Count < 4)
                wrongArmor.Add($"t={step / 30.0:F2} armour {turret.Armor:F2}");
        }
        if (firstAt is null) failures.Add($"(b) {turret.Name} never held MasterySiegeCommanderDebuff in 6 s (hero 850 away)");
        if (longestLapse > 0) failures.Add($"(b) the debuff lapsed for {longestLapse} steps in a row after it first landed at {firstAt:F2} s");
        if (wrongArmor.Count > 0)
            failures.Add($"(c) armour should be {baseArmor - 10.0:F2} (base {baseArmor:F2}) on every step after the first add: " + string.Join(", ", wrongArmor));

        if (firstAt is not null)
        {
            // Step past a renewal step (see the remarks) so the hit is measured with the debuff held.
            while (!Debuffed()) runtime.Tick(1.0 / 30.0);
            var before = turret.Health;
            TestAssert.True(runtime.Combat.ApplyHit(hero.NetId, turret.NetId, 100.0, DamageType.Physical, DamageSourceKind.Spell));
            var dealt = before - turret.Health;
            var expected = 100.0 * CombatRules.ResistanceMultiplier(baseArmor - 10.0);
            if (Math.Abs(dealt - expected) > 1e-6) failures.Add($"(d) a 100 physical hit dealt {dealt:F4}, {expected:F4} at armour {baseArmor - 10.0:F2}");

            var armorBytes = BitConverter.GetBytes((float)(baseArmor - 10.0));
            var replicated = match.Captures[0].Packets.Any(packet =>
            {
                var span = packet.Plaintext.Span;
                return span.Length == 83 && span[0] == 0xCD && span.IndexOf(BitConverter.GetBytes(turret.NetId)) >= 0 &&
                       span.IndexOf(armorBytes) >= 0;
            });
            if (!replicated) failures.Add($"(e) no 83-byte 0xCD turret block for {turret.Name} carried armour {baseArmor - 10.0:F2}");

            // (h) A siege: an enemy lane minion within 1000 lifts the turret's backdoor armour (Spells/TurretAssistManager.luaobj adds
            // TurretBackdoorBonus, whose OnBuffActivate is IncPermanentFlatArmorMod -150, a change of the turret's own baseline). The
            // debuff keeps its -10 across it: 200 - 150 - 10.
            var annie = runtime.Dependencies.ChampionDispatcher.FindModule("Annie") as BbChampionModule;
            var backdoor = annie?.Buffs?.Add(new BbBuffRequest(turret, turret, "TurretBackdoorBonus", (int)BbBuffAddType.BUFF_RENEW_EXISTING,
                (int)BbBuffType.BUFF_Internal, 1, 30.0, 0.0, false, new BbTable()));
            var sieged = new List<string>();
            for (var step = 0; step < 3 * 30; step++)
            {
                runtime.Tick(1.0 / 30.0);
                if (Debuffed() && Math.Abs(turret.Armor - (baseArmor - 160.0)) > 1e-6 && sieged.Count < 4) sieged.Add($"{turret.Armor:F2}");
            }
            if (backdoor is null) failures.Add("(h) TurretBackdoorBonus was not placed");
            else
            {
                if (sieged.Count > 0) failures.Add($"(h) with the backdoor armour lifted the turret read {string.Join(", ", sieged)} ({baseArmor - 160.0:F2} wanted)");
                runtime.SpellBuffs.Remove(turret, backdoor.Slot);
            }
            while (!Debuffed()) runtime.Tick(1.0 / 30.0);
            if (Math.Abs(turret.Armor - (baseArmor - 10.0)) > 1e-6) failures.Add($"(h) after the siege the turret read {turret.Armor:F2}");

            ally.Masteries = page;
            runtime.HeroStats.Refresh(ally);
            runtime.Spells.LoadoutHooks?.OwnerFor(ally);
            ally.Position = At(800.0);
            ally.MaxHealth = ally.Health = 100000.0;
            var stacked = new List<string>();
            for (var step = 0; step < 5 * 30; step++)
            {
                runtime.Tick(1.0 / 30.0);
                if (turret.Armor < baseArmor - 10.0 - 1e-6 && stacked.Count < 4) stacked.Add($"{turret.Armor:F2}");
            }
            if (stacked.Count > 0) failures.Add("(f) two wearers: armour " + string.Join(", ", stacked));

            hero.Position = new Position2(1500, 1500);
            ally.Position = new Position2(1600, 1500);
            B902Advance(match, 5.0);
            if (Debuffed() || Math.Abs(turret.Armor - baseArmor) > 1e-6)
                failures.Add($"(g) 5 s after both left: debuff={Debuffed()} armour {turret.Armor:F2}, base {baseArmor:F2}");
        }
        TestAssert.True(failures.Count == 0, $"Siege Commander on {turret.Name}: " + string.Join("; ", failures));
    }

    /// <summary>
    /// The talents whose whole effect is a <c>BBIncStat</c>/<c>BBInc*PAR*</c>/<c>BBIncMaxHealth</c> in <c>UpdateSelfBuffStats</c>
    /// (Talents/&lt;id&gt;.luaobj): each one's own number reaches the hero's stats (<c>HeroStatCalculator</c>, the production refresh's
    /// calculator), measured as the difference between a legal page with the talent and the same page without it, at champion level 18.
    /// </summary>
    [Check("masteries-b902-p132-stat-talents-reach-the-hero")]
    public static void B902StatTalentsReachTheHero()
    {
        var data = GameData.Default;
        var items = new ItemCatalog(data.Paths);
        var empty = new InventorySlot?[] { null, null, null, null, null, null };
        HeroStats Stats(string champion, Dictionary<int, int> ranks) => HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput(champion, 18, empty, masteries: ranks.Count == 0 ? MasteryPage.Empty : B902Page(ranks)));
        // Fillers: four points in each earlier row of the group, none of them the measured stat's.
        var o1 = new Dictionary<int, int> { [211] = 1, [214] = 2, [212] = 1 };
        var o2 = new Dictionary<int, int>(o1) { [222] = 4 };
        var o3 = new Dictionary<int, int>(o2) { [234] = 3, [232] = 1 };
        var d1 = new Dictionary<int, int> { [311] = 1, [314] = 2, [312] = 1 };
        var d2 = new Dictionary<int, int>(d1) { [323] = 3, [311] = 1 };
        d2[322] = 1;
        var d3 = new Dictionary<int, int>(d2) { [331] = 2, [333] = 2 };
        var d4 = new Dictionary<int, int>(d3) { [341] = 1, [342] = 3 };
        var d5 = new Dictionary<int, int>(d4) { [352] = 3, [353] = 1 };
        var u1 = new Dictionary<int, int> { [411] = 1, [412] = 3 };
        var u1m = new Dictionary<int, int> { [411] = 1, [413] = 3 };
        var u2 = new Dictionary<int, int>(u1) { [424] = 1, [422] = 3 };
        var u3 = new Dictionary<int, int>(u2) { [434] = 1, [432] = 3 };
        var u4 = new Dictionary<int, int>(u3) { [443] = 4 };
        var cases = new (string Name, Dictionary<int, int> Base, int Id, int Rank, string Champion, Func<HeroStats, double> Read, double Expected)[]
        {
            ("212 Brute Force", new() { [211] = 1 }, 212, 3, "Annie", s => s.AttackDamage, 3.0),
            ("213 Mental Force", new() { [211] = 1 }, 213, 4, "Annie", s => s.AbilityPower, 4.0),
            ("222 Alacrity", o1, 222, 4, "Annie", s => s.AttackSpeedMod, double.NaN),
            ("223 Sorcery", o1, 223, 4, "Annie", s => s.CooldownMod, -0.04),
            ("231 Deadliness", o2, 231, 4, "Annie", s => s.CritChance, 0.04),
            ("232 Weapon Expertise", o2, 232, 1, "Annie", s => s.ArmorPenPercent, 0.10),
            ("233 Arcane Knowledge", new(o2) { [223] = 4 }, 233, 1, "Annie", s => s.MagicPenPercent, 0.10),
            ("241 Lethality", new(o3) { [231] = 4 }, 241, 1, "Annie", s => s.CritMultiplier, 0.10),
            ("242 Vampirism", o3, 242, 3, "Annie", s => s.LifeSteal, 0.03),
            ("243 Blast", o3, 243, 4, "Annie", s => s.AbilityPower, 18.0),
            ("252 Sunder", new(o3) { [242] = 3, [243] = 1 }, 252, 3, "Annie", s => s.ArmorPenFlat, 6.0),
            ("312 Resistance", new() { [311] = 1 }, 312, 3, "Annie", s => s.SpellBlock, 6.0),
            ("313 Hardiness", new() { [311] = 1 }, 313, 3, "Annie", s => s.Armor, 6.0),
            ("322 Durability", d1, 322, 4, "Annie", s => s.MaxHp, 108.0),
            ("323 Vigor", d1, 323, 3, "Annie", s => s.HpRegen, 0.6),
            ("332 Veteran's Scars", new(d2) { [322] = 4 }, 332, 1, "Annie", s => s.MaxHp, 30.0),
            ("343 Enlightenment", d3, 343, 3, "Annie", s => s.CooldownMod, -0.0015 * 3 * 18),
            ("362 Juggernaut", d5, 362, 1, "Annie", s => s.MaxHp / 1.0, double.NaN),
            ("413 Expanded Mind (mana)", new() { [411] = 1 }, 413, 3, "Annie", s => s.MaxMp, 12.0 * 18),
            ("413 Expanded Mind (energy)", new() { [411] = 1 }, 413, 3, "LeeSin", s => s.EnergyPool, 10.0),
            ("412 Good Hands", new() { [411] = 1 }, 412, 3, "Annie", s => s.TimeDeadPercent, 0.10),
            ("422 Swiftness", u1, 422, 4, "Annie", s => s.MoveSpeed, double.NaN),
            ("423 Meditation", u1m, 423, 3, "Annie", s => s.MpRegen, 0.6),
            ("432 Greed", u2, 432, 4, "Annie", s => s.GoldPer10Mod, 2.0),
            ("433 Transmutation", u2, 433, 3, "Annie", s => s.SpellVamp, 0.03),
            ("443 Awareness", u3, 443, 4, "Annie", s => s.ExperiencePercent, 0.05),
            ("453 Intelligence", u4, 453, 3, "Annie", s => s.CooldownMod, -0.06),
        };
        var failures = new List<string>();
        foreach (var (name, basis, id, rank, champion, read, expected) in cases)
        {
            HeroStats without, with;
            try
            {
                without = Stats(champion, basis);
                with = Stats(champion, new Dictionary<int, int>(basis) { [id] = rank });
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: page refused ({ex.Message})");
                continue;
            }
            var delta = read(with) - read(without);
            // Juggernaut: 3 % of the maximum health; Swiftness: 2 % of the speed (below the 415 soft cap).
            var want = id == 362 ? read(without) * 0.03 : id == 422 ? read(without) * 0.02 : expected;
            // Alacrity: the attack-speed composition is the calculator's own (audit A3-R); only its direction is asserted here.
            if (id == 222) { if (delta <= 0.0) failures.Add($"{name}: {delta:F6}, more wanted"); continue; }
            if (Math.Abs(delta - want) > 1e-6) failures.Add($"{name}: {delta:F6}, want {want:F6}");
        }
        TestAssert.True(failures.Count == 0, "stat talents: " + string.Join("; ", failures));
    }

    /// <summary>
    /// The talents that only set an <c>AvatarVars</c> flag for the hero's <c>APBonusDamageToTowers</c> (132), each through that buff's own hook:
    /// Demolitionist (224, BuffOnHitUnit +10 on a turret, its flag written by a 3 s periodic), Bladed Armor (334, BuffOnBeingHit 6 true damage
    /// back to a minion or monster attacker, none to a hero or turret), Initiator (342, BuffOnUpdateStats +3 % movement above 70 % health),
    /// Juggernaut (362, BuffOnAllowAdd: an enemy's stun lasts 10 % less), Mercenary (353, BuffOnKill +24 gold for a champion) and Sage (444,
    /// BuffOnKill +40 experience for a champion).
    /// </summary>
    [Check("masteries-live-b902-p132-apbonus-talents-act-through-the-heros-tower-buff")]
    public static void B902ApBonusTalents()
    {
        var failures = new List<string>();

        // Demolitionist.
        {
            using var match = new Match("Annie", Team.Order, masteries: B902Page(new() { [211] = 1, [214] = 2, [212] = 1, [224] = 1 }));
            var hero = match.Hero(0); match.Hero(1).Position = new Position2(3000, 11500);
            var turret = B902EnemyTurret(match);
            turret.MaxHealth = turret.Health = 100000.0;
            hero.Position = B902Mid;
            double Hit()
            {
                var before = turret.Health;
                match.Runtime.Combat.ApplyHit(hero.NetId, turret.NetId, 100.0, DamageType.Physical, DamageSourceKind.Basic);
                return before - turret.Health;
            }
            B902Advance(match, 0.5);
            var early = Hit();
            B902Advance(match, 3.0);
            var late = Hit();
            var want = 10.0 * CombatRules.ResistanceMultiplier(turret.Armor);
            if (Math.Abs(late - early - want) > 1e-6) failures.Add($"224 Demolitionist: turret hit {early:F4} -> {late:F4}, +{want:F4} wanted after 3 s");
        }

        // Bladed Armor, Initiator, Juggernaut.
        {
            var page = B902Page(new() { [311] = 1, [314] = 2, [313] = 1, [322] = 4, [334] = 1, [333] = 3, [342] = 3, [341] = 1, [352] = 3, [353] = 1, [362] = 1 });
            using var match = new Match("Annie", Team.Order, enemyChampion: "Ashe", masteries: page);
            var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
            hero.Position = B902Mid; enemy.Position = B902Mid + new Position2(300, 0);
            var minion = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
                EntityKind.Minion, UnitType.Minion, Team.Chaos, hero.Position) { Health = 1000.0, MaxHealth = 1000.0 });
            var minionBefore = minion.Health;
            runtime.Combat.ApplyHit(minion.NetId, hero.NetId, 10.0, DamageType.Physical, DamageSourceKind.Basic);
            if (Math.Abs(minionBefore - minion.Health - 6.0) > 1e-6) failures.Add($"334 Bladed Armor: the minion took {minionBefore - minion.Health:F4}, 6 wanted");
            var enemyBefore = enemy.Health;
            runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, 10.0, DamageType.Physical, DamageSourceKind.Basic);
            if (enemy.Health != enemyBefore) failures.Add($"334 Bladed Armor: a hero attacker took {enemyBefore - enemy.Health:F4}, none wanted");

            // The fixture's 5000 health is not the hero's own pool: the stat refresh takes it back to Annie's, so the fractions are set after it.
            runtime.HeroStats.Refresh(hero);
            hero.Health = hero.MaxHealth;
            B902Advance(match, 1.0);
            var fast = hero.Speed;
            runtime.HeroStats.Refresh(hero);
            hero.Health = hero.MaxHealth * 0.5;
            B902Advance(match, 1.0);
            var slow = hero.Speed;
            if (Math.Abs(fast / slow - 1.03) > 1e-6) failures.Add($"342 Initiator: speed {fast:F3} above 70 %, {slow:F3} at 50 % (x1.03 wanted)");

            var ashe = runtime.Dependencies.ChampionDispatcher.FindModule("Ashe") as BbChampionModule;
            var stun = ashe?.Buffs?.Add(new BbBuffRequest(hero, enemy, "Stun", (int)BbBuffAddType.BUFF_REPLACE_EXISTING,
                (int)BbBuffType.BUFF_Stun, 1, 1.0, 0.0, true, new BbTable()));
            if (stun is null) failures.Add("362 Juggernaut: the enemy's stun was not placed");
            else if (Math.Abs(stun.ExpiresAt - 0.9) > 1e-6) failures.Add($"362 Juggernaut: an enemy's 1 s stun holds {stun.ExpiresAt:F4} s, 0.9 wanted");
        }

        // Mercenary and Sage: a champion kill, and an assist (the wearer hits, the ally lands the kill), against the same without the page.
        (double Gold, double Exp) Kill(MasteryPage? page, bool assist)
        {
            using var match = new Match("Annie", Team.Order, ally: true, allyChampion: "Annie", masteries: page);
            var hero = match.Hero(0); var enemy = match.Hero(1); var ally = match.Hero(2);
            hero.Position = B902Mid; enemy.Position = B902Mid + new Position2(200, 0); ally.Position = B902Mid + new Position2(0, 200);
            B902Advance(match, 0.5);
            var gold = hero.Gold; var exp = hero.Experience;
            if (assist) match.Runtime.Combat.ApplyHit(hero.NetId, enemy.NetId, 10.0, DamageType.True, DamageSourceKind.Spell);
            match.Runtime.Combat.ApplyHit((assist ? ally : hero).NetId, enemy.NetId, 1.0e6, DamageType.True, DamageSourceKind.Spell);
            B902Advance(match, 0.5);
            if (enemy.Alive) throw new InvalidOperationException("the enemy survived");
            return (hero.Gold - gold, hero.Experience - exp);
        }
        var mercenaryPage = B902Page(new() { [311] = 1, [313] = 3, [322] = 4, [331] = 2, [333] = 2, [341] = 1, [342] = 3, [353] = 3 });
        var sagePage = B902Page(new() { [411] = 1, [412] = 3, [422] = 4, [432] = 4, [444] = 1 });
        foreach (var assist in new[] { false, true })
        {
            var what = assist ? "assist" : "kill";
            var bare = Kill(null, assist);
            var mercenary = Kill(mercenaryPage, assist);
            if (Math.Abs(mercenary.Gold - bare.Gold - 24.0) > 1e-6) failures.Add($"353 Mercenary: {what} gold {bare.Gold:F2} -> {mercenary.Gold:F2}, +24 wanted");
            var sage = Kill(sagePage, assist);
            if (Math.Abs(sage.Exp - bare.Exp - 40.0) > 1e-6) failures.Add($"444 Sage: {what} experience {bare.Exp:F2} -> {sage.Exp:F2}, +40 wanted");
        }

        TestAssert.True(failures.Count == 0, "APBonusDamageToTowers talents: " + string.Join("; ", failures));
    }

    /// <summary>
    /// The two character hooks that read the hit's own unit and source: Executioner (262, CharOnPreDealDamage: a non-true hit on an AI
    /// non-turret at 40 % health or less is x1.06) and Evasion (333, CharOnPreDamage: a non-true <c>DAMAGESOURCE_SPELLAOE</c> hit is
    /// 1 - 0.01 x rank). Rows: Executioner on a champion at 39 % and 41 %, on a minion at 39 %, on a turret at 39 %, true damage at 39 %;
    /// Evasion at rank 3 on an area spell against a single-target spell, and on true area damage.
    /// </summary>
    [Check("masteries-live-b902-p132-executioner-and-evasion-read-the-real-hit")]
    public static void B902ExecutionerAndEvasion()
    {
        var failures = new List<string>();
        {
            var offense = B902Page(new()
                { [211] = 1, [214] = 2, [212] = 1, [223] = 4, [234] = 3, [233] = 1, [243] = 4, [253] = 4, [262] = 1 });
            using var match = new Match("Annie", Team.Order, masteries: offense);
            var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
            hero.Position = B902Mid; enemy.Position = B902Mid + new Position2(300, 0);
            enemy.MaxHealth = 10000.0;
            double OnHero(double fraction, DamageType type)
            {
                enemy.Health = enemy.MaxHealth * fraction;
                var before = enemy.Health;
                runtime.Combat.ApplyHit(hero.NetId, enemy.NetId, 100.0, type, DamageSourceKind.Spell);
                return before - enemy.Health;
            }
            var low = OnHero(0.39, DamageType.Physical); var high = OnHero(0.41, DamageType.Physical);
            if (Math.Abs(low / high - 1.06) > 1e-6) failures.Add($"262 Executioner on a champion: 39 % {low:F4}, 41 % {high:F4} (x1.06 wanted)");
            var trueLow = OnHero(0.39, DamageType.True); var trueHigh = OnHero(0.41, DamageType.True);
            if (Math.Abs(trueLow - trueHigh) > 1e-6) failures.Add($"262 Executioner on true damage: {trueLow:F4} / {trueHigh:F4} (no change wanted)");

            double OnUnit(Entity unit, double fraction)
            {
                unit.MaxHealth = 10000.0; unit.Health = unit.MaxHealth * fraction;
                var before = unit.Health;
                runtime.Combat.ApplyHit(hero.NetId, unit.NetId, 100.0, DamageType.Magic, DamageSourceKind.Spell);
                return before - unit.Health;
            }
            var minion = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
                EntityKind.Minion, UnitType.Minion, Team.Chaos, hero.Position) { Health = 1000.0, MaxHealth = 1000.0, SpellBlock = 0.0 });
            var mLow = OnUnit(minion, 0.39); var mHigh = OnUnit(minion, 0.41);
            if (Math.Abs(mLow / mHigh - 1.06) > 1e-6) failures.Add($"262 Executioner on a minion: 39 % {mLow:F4}, 41 % {mHigh:F4} (x1.06 wanted)");
            var turret = B902EnemyTurret(match);
            var tLow = OnUnit(turret, 0.39); var tHigh = OnUnit(turret, 0.41);
            if (Math.Abs(tLow - tHigh) > 1e-6) failures.Add($"262 Executioner on a turret: 39 % {tLow:F4}, 41 % {tHigh:F4} (no change wanted)");
        }
        {
            using var match = new Match("Annie", Team.Order,
                masteries: B902Page(new() { [311] = 1, [313] = 3, [322] = 4, [333] = 3 }));
            var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
            hero.Position = B902Mid; enemy.Position = B902Mid + new Position2(300, 0);
            hero.MaxHealth = 100000.0;
            double Taken(DamageType type, DamageSourceKind kind)
            {
                hero.Health = hero.MaxHealth;
                runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, 100.0, type, kind);
                return hero.MaxHealth - hero.Health;
            }
            var area = Taken(DamageType.Magic, DamageSourceKind.SpellAoe); var single = Taken(DamageType.Magic, DamageSourceKind.Spell);
            if (Math.Abs(area / single - 0.97) > 1e-6) failures.Add($"333 Evasion: area {area:F4}, single target {single:F4} (x0.97 wanted)");
            var trueArea = Taken(DamageType.True, DamageSourceKind.SpellAoe);
            if (Math.Abs(trueArea - 100.0) > 1e-6) failures.Add($"333 Evasion on true area damage: {trueArea:F4} (100 wanted)");
        }
        TestAssert.True(failures.Count == 0, "hit-reading talents: " + string.Join("; ", failures));
    }

    /// <summary>
    /// Wealth (442, "Increases starting gold by 20" per rank): UpdateSelfBuffActions adds MasteryHoardBuff BUFF_RENEW_EXISTING 25000 s with
    /// <c>GoldAmount</c> {20, 40}, and the buff's own OnBuffActivate is <c>BBIncGold</c>. The gold lands once: not again on a poll, a level-up
    /// or a death and respawn (PersistsThroughDeath). Measured against an ally without the page. The same page holds Improved Recall (414),
    /// whose MasteryImprovedRecallBuff is live since batch 906 (its slot swap: masteries-live-b906-p132-improved-recall-swaps-in-the-seven-second-recall).
    /// </summary>
    [Check("masteries-live-b902-p132-wealth-grants-its-gold-once")]
    public static void B902Wealth()
    {
        var failures = new List<string>();
        var page = B902Page(new() { [411] = 1, [412] = 2, [414] = 1, [422] = 4, [432] = 4, [442] = 2 });
        using var match = new Match("Annie", Team.Order, ally: true, allyChampion: "Annie", masteries: page);
        var runtime = match.Runtime; var hero = match.Hero(0); var ally = match.Hero(2); var enemy = match.Hero(1);
        enemy.Position = new Position2(3000, 11500);
        B902Advance(match, 1.0);
        var lead = hero.Gold - ally.Gold;
        if (Math.Abs(lead - 40.0) > 1e-6) failures.Add($"442 Wealth: {lead:F2} gold over a hero without it after 1 s (40 wanted)");
        hero.Level = 2;
        runtime.HeroStats.Refresh(hero);
        runtime.Spells.LoadoutHooks?.OwnerFor(hero);
        B902Advance(match, 1.0);
        runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, 1.0e6, DamageType.True, DamageSourceKind.Spell);
        B902Advance(match, 20.0);
        var after = hero.Gold - ally.Gold;
        if (!hero.Alive) failures.Add("the hero did not respawn in 20 s");
        if (Math.Abs(after - 40.0) > 1e-6) failures.Add($"442 Wealth: {after:F2} after a level-up, a death and a respawn (40 wanted)");

        // The buff's template is a copy of Fortify's (BuffName FortifyBuff, Summoner_fortify.dds, BUFF_Internal, no torch): seat 0's client
        // gets one add of MasteryHoardBuff and no add under Fortify's name; the polls renew the held slot in place (no second add, no
        // flicker), batch 157's rule for the utility marker (items-live-utility-marker-renews-on-every-poll-without-flicker).
        var hash = Amberline.Network.Protocol.SpellPackets.BuffNameHash("MasteryHoardBuff");
        var fortify = Amberline.Network.Protocol.SpellPackets.BuffNameHash("FortifyBuff");
        uint? AddOf(PlaintextPacket packet) => packet.Plaintext.Span is var bytes && bytes.Length >= 13 &&
            bytes[0] == Amberline.Network.Protocol.SpellPackets.BuffAddOpcode &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..5]) == hero.NetId
                ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[9..13]) : null;
        var seen = match.SpawnBursts[0].Concat(match.Captures[0].Packets).ToArray();
        var hoardAdds = seen.Count(packet => AddOf(packet) == hash);
        var fortifyAdds = seen.Count(packet => AddOf(packet) == fortify);
        if (!hero.Buffs.Any(buff => buff.Name == "MasteryHoardBuff")) failures.Add("442 Wealth: no MasteryHoardBuff on the hero");
        if (hoardAdds > 1 || fortifyAdds > 0) failures.Add($"442 Wealth: {hoardAdds} adds of MasteryHoardBuff and {fortifyAdds} of FortifyBuff reached seat 0");
        // Batch 906: 414 Improved Recall's buff is a live add (BbLoadoutHooks.IsLiveAdd); the recall follows its slot swap.
        if (!hero.Buffs.Any(buff => buff.Name == "MasteryImprovedRecallBuff"))
            failures.Add("414 Improved Recall: no MasteryImprovedRecallBuff on the hero");
        TestAssert.True(failures.Count == 0, "Wealth: " + string.Join("; ", failures));
    }

    /// <summary>
    /// Scout (424, "Increases vision range of wards by 5%"): Talents/424.luaobj writes <c>AvatarVars.Scout = true</c>; the ward's
    /// Spells/SightWard.luaobj placement adds MasteryScoutBuff to the ward under that flag, and the buff's OnBuffActivate is
    /// <c>BBIncPermanentStat IncPermanentFlatBubbleRadiusMod 55</c>. The ward's sight carries the 55 (VisibilityRules.BubbleRadiusMods); a
    /// ward placed without the talent carries none.
    /// </summary>
    [Check("masteries-live-b902-p132-scout-wards-see-55-further")]
    public static void B902Scout()
    {
        double WardFlat(MasteryPage? page)
        {
            using var match = new Match("Morgana", Team.Order, masteries: page);
            var hero = match.Hero(0);
            hero.Inventory = [new InventorySlot(2044), null, null, null, null, null];
            B902Advance(match, 0.5);
            TestAssert.True(match.TryCastGround(0, 4, new Position2(7100, 7000)), "the sight ward is placed");
            B902Advance(match, 0.5);
            var ward = match.Runtime.World.Entities.Values.Single(unit => unit.IsWard);
            return Amberline.Network.Replication.VisibilityRules.BubbleRadiusMods(ward).Flat;
        }
        var bare = WardFlat(null);
        var scout = WardFlat(B902Page(new() { [411] = 1, [412] = 3, [424] = 1 }));
        TestAssert.True(Math.Abs(scout - bare - 55.0) < 1e-6, $"Scout: the ward's flat sight {bare:F2} -> {scout:F2}, +55 wanted");
    }
}
