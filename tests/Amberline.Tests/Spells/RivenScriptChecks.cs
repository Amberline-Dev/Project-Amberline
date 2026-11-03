using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Diagnostics;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Riven runs from her shipped scripts (phase 3, new champions, 19 October 2026, W31c): the zero-size Shield pool, Runic Blade, Broken Wings (three strikes and
/// the recast window), Ki Burst, Valor and Blade of the Exile with Wind Slash. The numbers are the shipped scripts' and inibins' (rank 5 of Q, W and E, rank 3 of R,
/// level 18); the scenario <c>S-riven</c> reads the same figures with the wire.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    private static int RivenStacks(Entity hero, string name) => hero.Buffs.Where(buff => buff.Name == name).Sum(buff => Math.Max(1, buff.Stacks));

    private static void RivenLearn(Match match, Entity hero)
    {
        hero.SpellRanks[0] = 5;
        hero.SpellRanks[1] = 5;
        hero.SpellRanks[2] = 5;
        hero.SpellRanks[3] = 3;
        var required = ProgressionRules.ExperienceThreshold(18) - hero.Experience;
        if (required > 0.0) match.Runtime.Combat.AwardHeroProgress(hero, experience: required);
        match.Runtime.HeroStats.Refresh(hero);
    }

    // The foe's armor is what the stat pipeline gives it; the checks divide it out so the figures are the scripts' own.
    private static double RivenRaw(Entity foe, double lost) => lost * (100.0 + foe.Armor) / 100.0;

    private static string RivenSlot(Match match, Entity riven, int slot) =>
        ((BbChampionModule)match.Runtime.Dependencies.ChampionDispatcher.FindModule("Riven")!).SpellName(riven, slot);

    // One basic attack: the order goes in, the clock runs until the foe loses health, and the hero is told to stand (no second swing).
    private static double RivenSwing(Match match, Entity riven, Entity foe)
    {
        foe.Health = foe.MaxHealth;
        var before = foe.Health;
        match.Attack(0, foe);
        for (var step = 0; step < 90 && foe.Health >= before - 0.01; step++) match.Runtime.Tick(1.0 / 30.0);
        match.Runtime.Tick(0.1);
        riven.Target = null;
        riven.AttackTarget = null;
        riven.Waypoints.Clear();
        return before - foe.Health;
    }

    [Check("scripting.bb-riven-module-binds-the-shipped-closure")]
    public static void RivenClosureRunsEveryNativeOfItsHooks()
    {
        var module = new BbChampionModule("Riven");
        var needed = new[]
        {
            "CharScriptRiven", "RivenTriCleave", "RivenMartyr", "RivenFeint", "RivenFengShuiEngine", "RivenPassive", "RivenPassiveAABoost", "RivenLightsaberMissile",
            "RivenIzunaBlade", "RivenLightsaberMissileSide", "RivenTriCleaveCooldown", "RivenTriCleaveBuffer", "RivenTriCleaveBufferB", "RivenTriCleaveBuffered",
            "RivenTriCleaveDamage", "RivenTriCleaveDamageDebuff", "RivenTriCleaveDamageDebuff2", "RivenKnockback", "RivenMartyrDebuff", "RivenWindSlashReady", "RivenSword",
        };
        foreach (var name in needed)
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the Riven closure");
        var hero = new Entity { ChampionName = "Riven" };
        TestAssert.Equal(["RivenTriCleave", "RivenMartyr", "RivenFeint", "RivenFengShuiEngine"], Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(),
            "spell names come from Riven.inibin");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Riven");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => needed.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native a Riven hook runs is bound: " + string.Join(", ", unbound));
    }

    [Check("scripting.bb-riven-has-no-resource-pool-and-her-spells-cost-nothing")]
    public static void RivenHasAZeroSizeShieldPool()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Riven", team, enemyChampion: "Annie");
            var riven = match.Hero(0);
            RivenLearn(match, riven);
            TestAssert.Equal(0.0, riven.MaxMana, $"{team}: the Shield pool has no size (Riven.inibin MaxMp 0)");
            riven.Mana = 0.0;
            riven.SpellCooldowns[1] = 0.0;
            TestAssert.True(match.TryCast(0, 1, riven), $"{team}: Ki Burst casts with an empty pool");
            match.Runtime.Tick(0.1);
            match.Runtime.Tick(0.5);
            TestAssert.Equal(0.0, riven.Mana, $"{team}: and spends nothing");
        }
    }

    /// <summary>
    /// Batch 575 (owner question 4): Riven, Dr. Mundo and Katarina ship <c>PARType</c> Shield with no pool (BaseMP 0, Dr. Mundo -100000, no
    /// growth). The server replicates 0 / 0 at every level (the mana init clamps the maximum at 0; ProductionServices sends the entity's pool).
    /// The client's hero bar takes 0 as the PAR fraction when mMaxMP is 0 and always draws the
    /// bar: an empty strip, not a missing or a full one.
    /// </summary>
    [Check("scripting.bb-riven-drmundo-katarina-replicate-an-empty-shield-bar")]
    public static void ZeroPoolChampionsReplicateAnEmptyBar()
    {
        foreach (var champion in new[] { "Riven", "DrMundo", "Katarina" })
        {
            TestAssert.Equal(PrimaryResourceType.Shield, GameData.Default.Champion(champion).PrimaryResource, $"{champion}: PARType Shield");
            using var match = new Match(champion, Team.Order, enemyChampion: "Annie");
            var hero = match.Hero(0);
            TestAssert.Equal(0.0, hero.MaxMana, $"{champion}: no pool at level one");
            TestAssert.Equal(0.0, hero.Mana, $"{champion}: and nothing in it");
            var required = ProgressionRules.ExperienceThreshold(18) - hero.Experience;
            if (required > 0.0) match.Runtime.Combat.AwardHeroProgress(hero, experience: required);
            match.Runtime.HeroStats.Refresh(hero);
            TestAssert.Equal(18, hero.Level, $"{champion}: level 18");
            TestAssert.Equal(0.0, hero.MaxMana, $"{champion}: no pool at level 18");
            TestAssert.Equal(0.0, hero.Mana, $"{champion}: still empty");
        }
    }

    [Check("scripting.bb-riven-ki-burst-stuns-within-300-and-charges-the-runic-blade")]
    public static void RivenKiBurstHitsNearbyEnemiesAndStacksTheBlade()
    {
        using var match = new Match("Riven", Team.Order, enemyChampion: "Annie", extraEnemy: true, extraEnemyChampion: "Taric");
        var riven = match.Hero(0);
        var near = match.Hero(1);
        var far = match.Hero(2);
        RivenLearn(match, riven);
        near.Position = new Position2(riven.Position.X + 250.0, riven.Position.Z);
        far.Position = new Position2(riven.Position.X, riven.Position.Z + 340.0);
        // The first stat refresh clamps the harness's 5000 health to the champion's own: let it run, then fill the bars.
        match.Runtime.Tick(0.2);
        near.Health = near.MaxHealth;
        far.Health = far.MaxHealth;
        var nearHealth = near.Health;
        var farHealth = far.Health;
        match.Cast(0, 1, riven);
        match.Runtime.Tick(0.3);
        TestAssert.Near(210.0, RivenRaw(near, nearHealth - near.Health), 6.0, $"rank 5: 210 physical (no bonus attack damage) to the enemy at 250 (lost {nearHealth - near.Health:0.0}, armor {near.Armor:0.0})");
        TestAssert.True(near.Buffs.Any(buff => buff.Name == "Stun"), "and a stun");
        TestAssert.Near(farHealth, far.Health, 0.01, "the enemy at 340 is outside the 300");
        TestAssert.Equal(1, RivenStacks(riven, "RivenPassiveAABoost"), "the cast charges the Runic Blade");
        match.Runtime.Tick(0.9);
        TestAssert.False(near.Buffs.Any(buff => buff.Name == "Stun"), "the stun lasts 0.75 s");
        TestAssert.Near(7.0 - 1.2, riven.SpellCooldowns[1], 0.4, "rank 5 cooldown 7 s");
    }

    [Check("scripting.bb-riven-runic-blade-spends-a-stack-per-attack-and-restarts-the-window")]
    public static void RivenRunicBladeSpendsOneStackPerAttack()
    {
        using var match = new Match("Riven", Team.Order, enemyChampion: "Annie");
        var riven = match.Hero(0);
        var foe = match.Hero(1);
        RivenLearn(match, riven);
        foe.Position = new Position2(riven.Position.X + 110.0, riven.Position.Z);
        for (var cast = 0; cast < 3; cast++)
        {
            riven.SpellCooldowns[1] = 0.0;
            match.Cast(0, 1, riven);
            match.Runtime.Tick(0.4);
        }
        foe.Health = foe.MaxHealth;
        TestAssert.Equal(3, RivenStacks(riven, "RivenPassiveAABoost"), "three casts, three charges (the buff stops at three)");
        // Let the stun and the cast settle, then swing: one charge goes with each attack.
        match.Runtime.Tick(0.8);
        var seen = new List<int>();
        var extra = new List<double>();
        for (var swing = 0; swing < 3; swing++)
        {
            var lost = RivenSwing(match, riven, foe);
            seen.Add(RivenStacks(riven, "RivenPassiveAABoost"));
            extra.Add(RivenRaw(foe, lost) - riven.AttackDamage);
            if (swing == 0)
                TestAssert.True(riven.Buffs.Where(buff => buff.Name == "RivenPassiveAABoost").All(buff => buff.ExpiresAt > 3.0 && buff.ExpiresAt <= 5.0),
                    "the charges that stay get five seconds again (BBSpellBuffRemove with ResetDuration 5)");
            match.Runtime.Tick(0.6);
        }
        var plain = RivenSwing(match, riven, foe);
        TestAssert.Equal([2, 1, 0], seen, "one charge goes with each attack");
        TestAssert.Near(15.0, extra[0], 3.0, "level 18: 15 bonus physical damage per charge (+0.5 of bonus attack damage, none here)");
        TestAssert.Near(riven.AttackDamage, RivenRaw(foe, plain), 3.0, "and the attack after the last charge is a plain one");
    }

    [Check("scripting.bb-riven-valor-dashes-250-and-shields-180-against-both-damage-types")]
    public static void RivenValorDashesAndShields()
    {
        using var match = new Match("Riven", Team.Order, enemyChampion: "Annie");
        var riven = match.Hero(0);
        var foe = match.Hero(1);
        RivenLearn(match, riven);
        var start = riven.Position;
        match.Runtime.Tick(0.2);
        riven.Health = riven.MaxHealth;
        match.CastGround(0, new Position2(start.X - 800.0, start.Z), slot: 2);
        match.Runtime.Tick(0.7);
        riven.Armor = 0.0;
        riven.SpellBlock = 0.0;
        var moved = Math.Sqrt(Math.Pow(riven.Position.X - start.X, 2) + Math.Pow(riven.Position.Z - start.Z, 2));
        TestAssert.Near(250.0, moved, 6.0, "the dash is 250 units toward the cursor");
        TestAssert.True(riven.Buffs.Any(buff => buff.Name == "RivenFeint"), "the shield buff is on");
        var health = riven.Health;
        match.Runtime.Combat.ApplyHit(foe.NetId, riven.NetId, 100.0, DamageType.Physical, DamageSourceKind.Spell);
        TestAssert.Equal(health, riven.Health, "100 physical is absorbed whole (shield 180 at no bonus attack damage)");
        health = riven.Health;
        match.Runtime.Combat.ApplyHit(foe.NetId, riven.NetId, 150.0, DamageType.Magic, DamageSourceKind.Spell);
        TestAssert.Near(70.0, health - riven.Health, 0.5, $"150 magic damage: 80 left in the shield, 70 gets through (AD {riven.AttackDamage:0.0}, shield buff {riven.Buffs.Any(buff => buff.Name == "RivenFeint")})");
        TestAssert.False(riven.Buffs.Any(buff => buff.Name == "RivenFeint"), "an emptied shield ends the buff");
    }

    [Check("scripting.bb-riven-broken-wings-three-strikes-knock-up-the-third-and-keep-the-cooldown-of-the-first")]
    public static void RivenBrokenWingsThreeStrikes()
    {
        using var match = new Match("Riven", Team.Order, enemyChampion: "Annie");
        var riven = match.Hero(0);
        var foe = match.Hero(1);
        RivenLearn(match, riven);
        var direction = new Position2(1.0, 0.0);
        var stacks = new List<int>();
        var elapsed = 0.0;
        for (var strike = 1; strike <= 3; strike++)
        {
            foe.Position = new Position2(riven.Position.X + 200.0, riven.Position.Z);
            foe.Health = foe.MaxHealth;
            var before = foe.Health;
            var at = riven.Position;
            match.CastGround(0, new Position2(at.X + 600.0, at.Z), slot: 0);
            match.Runtime.Tick(0.6);
            var moved = Math.Sqrt(Math.Pow(riven.Position.X - at.X, 2) + Math.Pow(riven.Position.Z - at.Z, 2));
            // 240 less the 15 of MoveBackBy; the landing keeps clear of the foe's body that stands 200 ahead, so up to 15 units less.
            TestAssert.Near(220.0, moved, 12.0, $"strike {strike}: the dash is about 225 units");
            stacks.Add(RivenStacks(riven, "RivenTriCleave"));
            TestAssert.True(before - foe.Health > 50.0, $"strike {strike}: the foe in the path is hit");
            TestAssert.Equal(strike == 3, foe.Buffs.Any(buff => buff.Name == "RivenKnockback") || foe.Buffs.Any(buff => buff.Name == "RivenTriCleaveDamageDebuff2"),
                $"strike {strike}: only the third one knocks the foe back");
            // The next press waits out the slot's 0.25 s and the foe's debuff (one second).
            match.Runtime.Tick(0.8);
            elapsed += 1.4;
            if (strike < 3) riven.SpellCooldowns[0] = 0.0;
        }
        TestAssert.Equal([1, 2, 0], stacks, "the counter reads 1, 2 and then ends with the third strike");
        TestAssert.True(riven.SpellCooldowns[0] > 7.0 && riven.SpellCooldowns[0] < 12.9, $"after the third strike the Q holds what is left of the 13 s from the first press (it holds {riven.SpellCooldowns[0]:0.00} s after {elapsed:0.0} s)");
    }

    [Check("scripting.bb-move-a-move-a-spell-started-runs-the-movement-hooks-of-the-units-buffs")]
    public static void MoveStartedBySpellHookReachesTheUnitsBuffs()
    {
        using var match = new Match("Riven", Team.Order, enemyChampion: "Annie");
        var riven = match.Hero(0);
        RivenLearn(match, riven);
        var at = riven.Position;
        match.CastGround(0, new Position2(at.X + 600.0, at.Z), slot: 0);
        match.Runtime.Tick(0.15);
        TestAssert.True(riven.Buffs.Any(buff => buff.Name == "RivenTriCleaveDamage"), "the strike buff is on while the dash runs (RivenTriCleave.SelfExecute adds it, then moves)");
        match.Runtime.Tick(0.4);
        // BBMove is in the spell's SelfExecute, the hooks are the buff's: BuffOnMoveEnd of RivenTriCleaveDamage clears it when the dash is over, not after its own second.
        TestAssert.False(riven.Buffs.Any(buff => buff.Name == "RivenTriCleaveDamage"), "the dash's end reached the buff (BuffOnMoveEnd), which cleared itself 0.5 s before its second ran out");
        TestAssert.False(riven.Buffs.Any(buff => buff.Name == "RivenSword"), "and the sword effect buff that the same hook clears");
    }

    [Check("scripting.bb-riven-broken-wings-window-ends-after-3-75-seconds-and-the-slot-holds-the-rest-of-13")]
    public static void RivenBrokenWingsExpiryGivesTheRemainderOfThirteenSeconds()
    {
        using var match = new Match("Riven", Team.Order, enemyChampion: "Annie");
        var riven = match.Hero(0);
        RivenLearn(match, riven);
        var at = riven.Position;
        match.CastGround(0, new Position2(at.X + 600.0, at.Z), slot: 0);
        match.Runtime.Tick(3.0);
        TestAssert.Equal(1, RivenStacks(riven, "RivenTriCleave"), "the strike counter is still up at 3 s");
        match.Runtime.Tick(1.2);
        TestAssert.Equal(0, RivenStacks(riven, "RivenTriCleave"), "and gone at 4.2 s (3.75)");
        TestAssert.Near(13.0 - 4.2, riven.SpellCooldowns[0], 0.5, "the slot holds what is left of the 13 s");
    }

    [Check("scripting.bb-riven-broken-wings-press-inside-the-buffer-is-cast-when-it-ends")]
    public static void RivenBrokenWingsBufferedPress()
    {
        using var match = new Match("Riven", Team.Order, enemyChampion: "Annie");
        var riven = match.Hero(0);
        RivenLearn(match, riven);
        var at = riven.Position;
        var aim = new Position2(at.X + 900.0, at.Z);
        match.CastGround(0, aim, slot: 0);
        match.Runtime.Tick(0.1);
        TestAssert.Equal("RivenTriCleaveBuffer", RivenSlot(match, riven, 0), "the slot holds the buffer spell for 0.4 s after a strike");
        match.Runtime.Tick(0.2);
        TestAssert.True(match.TryCastGround(0, 0, aim), "a press inside the buffer is taken");
        match.Runtime.Tick(0.05);
        TestAssert.Equal(1, RivenStacks(riven, "RivenTriCleave"), "and does not strike yet");
        match.Runtime.Tick(0.4);
        TestAssert.Equal(2, RivenStacks(riven, "RivenTriCleave"), "the second strike follows when the buffer ends (RivenTriCleaveBuffered casts extra slot 4)");
    }

    // Whole simulation steps, as the server runs them: one long Tick hands the script move's hooks a single late step, after the buff they
    // run from may already have ended, which is not what a player sees.
    private static void RivenRun(Match match, double seconds)
    {
        for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += 1.0 / 30.0) match.Runtime.Tick(1.0 / 30.0);
    }

    /// <summary>The ground effects (<c>exile_Q_0N_detonate</c>) a capture received, by name: each strike draws one at its landing's focal point.</summary>
    private static List<string> RivenDetonates(Capture capture)
    {
        var names = new List<string>();
        foreach (var packet in capture.Packets)
        {
            var bytes = packet.Plaintext.Span;
            if (bytes.Length == 0 || bytes[0] != 0x8C) continue;
            foreach (var group in (WirePacketDecoder.Decode(bytes, packet.Channel).Fields["groups"] as object?[] ?? []).Cast<Dictionary<string, object?>>())
            {
                var hash = (uint)group["effect_name_hash"]!;
                for (var rank = 1; rank <= 3; rank++)
                    if (hash == SpellSystem.ProtocolHash($"exile_Q_0{rank}_detonate")) names.Add($"exile_Q_0{rank}_detonate");
            }
        }
        return names;
    }

    // Owner finding 16 (24 October 2026, batch 176 part 1): "Q near walls is still buggy, especially on the third Q. The ground VFX is sometimes
    // missing or dropped." The caster must see each strike's detonate, the third included, and the press must not lose its clip. The fixture is a
    // wall the wire sweep found (the third strike there reached a point where Riven died, see the record). Chaos towers reach this spot, so the caster
    // gets health a level-1 hero would not keep under them: a death removes her script buffs, and the detonate with them, which is not this finding.
    [Check("scripting.bb-riven-third-broken-wings-draws-each-detonate-on-the-casters-client-near-a-wall")]
    public static void RivenEveryStrikeDrawsItsDetonateNearAWall()
    {
        using var match = new Match("Riven", Team.Order, enemyChampion: "Annie");
        var riven = match.Hero(0);
        for (var slot = 0; slot < 4; slot++) riven.SpellRanks[slot] = slot == 3 ? 3 : 5;
        match.Runtime.HeroStats.Refresh(riven, push: false);
        riven.MaxHealth = riven.Health = 5000.0;
        var direction = new Position2(0.7071, 0.7071);
        riven.Position = match.Runtime.Movement.ClientAnchor(new Position2(8846.09698, 9064.8077));
        riven.Facing = direction;
        match.Hero(1).Position = new Position2(riven.Position.X - direction.X * 500.0, riven.Position.Z - direction.Z * 500.0);
        RivenRun(match, 0.2);
        for (var strike = 1; strike <= 3; strike++)
        {
            riven.SpellCooldowns[0] = 0.0;
            match.Clear();
            var from = riven.Position;
            match.CastGround(0, new Position2(from.X + direction.X * 900.0, from.Z + direction.Z * 900.0), slot: 0);
            RivenRun(match, 1.4);
            var detonates = RivenDetonates(match.Captures[0]);
            TestAssert.Equal(1, detonates.Count, $"strike {strike}: the caster's client gets one ground effect (it got {string.Join(", ", detonates)})");
            TestAssert.Equal($"exile_Q_0{strike}_detonate", detonates[0], $"strike {strike}: the effect is that strike's detonate");
            TestAssert.True(riven.Alive, $"strike {strike}: the caster is alive to see it");
            // The owner's other half: the clip is released once the strike has landed (its detonate is drawn first, then RivenTriCleaveUnlock's 0.1 s).
            TestAssert.False(riven.Buffs.Any(buff => buff.Name == "UnlockAnimation"), $"strike {strike}: the clip is released after the landing");
        }
    }

    // Owner finding 16, correction lead (24 October 2026, batch 176): a waypoint group the server sends the caster while her dash is in flight cuts
    // the dash and its clip (a plain 0xC1 clears the dash's override speed and replaces its path). See docs/champions/blitzcrank-playability.md. The
    // terminal stop of a completed dash comes at the first tick at or after the arrival, so after the flight (distance over speed). This pins that
    // order, and that she stands where the dash ended.
    [Check("scripting.bb-riven-broken-wings-sends-the-caster-no-waypoint-during-its-flight")]
    public static void RivenDashSendsNoWaypointDuringItsFlight()
    {
        using var match = new Match("Riven", Team.Order, enemyChampion: "Annie");
        var riven = match.Hero(0);
        RivenLearn(match, riven);
        var at = riven.Position;
        match.Clear();
        match.CastGround(0, new Position2(at.X + 900.0, at.Z), slot: 0);
        var destination = riven.ForcedMove?.Destination ?? throw new InvalidOperationException("the strike started no dash");
        // The first strike's Speed is 825 (RivenTriCleave.SelfExecute): the client's flight is the distance over that speed.
        var flight = Math.Sqrt(Math.Pow(destination.X - at.X, 2) + Math.Pow(destination.Z - at.Z, 2)) / 825.0;
        var firstGroupAt = double.PositiveInfinity;
        var seen = 0;
        for (var step = 0; step < 30; step++)
        {
            match.Runtime.Tick(1.0 / 30.0);
            var packets = match.Captures[0].Packets;
            for (; seen < packets.Count; seen++)
            {
                var bytes = packets[seen].Plaintext.Span;
                if (bytes.Length >= 5 && bytes[0] == 0xC1 && BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]) == riven.NetId)
                    firstGroupAt = Math.Min(firstGroupAt, (step + 1) / 30.0);
            }
        }
        TestAssert.True(firstGroupAt >= flight - 1e-9,
            $"the caster gets no waypoint group while her dash is in flight (the first came at {firstGroupAt:0.000} s, the flight ends at {flight:0.000} s)");
        TestAssert.Near(destination.X, riven.Position.X, 0.5, "she stands where the dash ended (X)");
        TestAssert.Near(destination.Z, riven.Position.Z, 0.5, "she stands where the dash ended (Z)");
    }


    /// <summary>
    /// Batch 583 (the record's known unknown, the <c>_ult</c> animations): RivenFengShuiEngine's OnBuffActivate overrides eleven clips
    /// (BBOverrideAnimation: Attack1 to Attack3, Crit, Idle1, Run, Spell1a to Spell1c, Spell2 and Spell3, each to its _ult clip, all in
    /// Riven's Animations.list) and its OnBuffDeactivate clears the same eleven (BBClearOverrideAnimation). The client keeps the map
    /// the 0x6E sends (a count, then the lower-cased original and the replacement): the last 0x6E of the cast holds all eleven, the
    /// last one after the 15 s holds none.
    /// </summary>
    [Check("scripting.bb-riven-blade-of-the-exile-sends-the-eleven-ult-clips-and-clears-them")]
    public static void RivenBladeOfTheExileOverridesItsClips()
    {
        using var match = new Match("Riven", Team.Order, enemyChampion: "Annie");
        var riven = match.Hero(0);
        RivenLearn(match, riven);
        var expected = BbM3.Script("RivenFengShuiEngine").Event("OnBuffActivate")!.Blocks.SelectMany(BbM3.Flatten)
            .Where(block => block.Function == "BBOverrideAnimation")
            .ToDictionary(block => block.Text("ToOverrideAnim")!.ToLowerInvariant(), block => block.Text("OverrideAnim")!);
        TestAssert.Equal(11, expected.Count, "the script overrides eleven clips");
        TestAssert.True(expected.All(pair => pair.Value.EndsWith("_ult", StringComparison.Ordinal)), "each to its _ult clip");
        byte LastCount() => match.Captures[0].Packets.Where(packet => packet.Plaintext.Span[0] == 0x6E &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == riven.NetId).Select(packet => packet.Plaintext.Span[5]).LastOrDefault((byte)0xFF);
        match.Clear();
        match.Cast(0, 3, riven);
        match.Runtime.Tick(0.8);
        TestAssert.Equal(expected.Count, riven.ScriptAnimationOverrides.Count, "the server's map holds the eleven");
        foreach (var (original, replacement) in expected)
            TestAssert.Equal(replacement, riven.ScriptAnimationOverrides.GetValueOrDefault(original), $"{original} plays {replacement}");
        TestAssert.Equal((byte)11, LastCount(), "the owner's client gets the whole map in a 0x6E");
        match.Clear();
        match.Runtime.Tick(15.0);
        TestAssert.False(riven.Buffs.Any(buff => buff.Name == "RivenFengShuiEngine"), "the buff ends after 15 s");
        TestAssert.Equal(0, riven.ScriptAnimationOverrides.Count, "the map is empty again");
        TestAssert.Equal((byte)0, LastCount(), "and the last 0x6E clears it on the client");
    }

    [Check("scripting.bb-riven-blade-of-the-exile-swaps-r-for-wind-slash-which-scales-with-missing-health")]
    public static void RivenBladeOfTheExileAndWindSlash()
    {
        // Ezreal as the second foe: Taric's aura would heal an Annie standing next to him between the hit and the reading.
        using var match = new Match("Riven", Team.Order, enemyChampion: "Annie", extraEnemy: true, extraEnemyChampion: "Ezreal");
        var riven = match.Hero(0);
        var full = match.Hero(1);
        var half = match.Hero(2);
        RivenLearn(match, riven);
        var ad = riven.AttackDamage;
        var range = riven.AttackRange;
        match.Cast(0, 3, riven);
        match.Runtime.Tick(0.8);
        TestAssert.True(riven.Buffs.Any(buff => buff.Name == "RivenFengShuiEngine"), "the ultimate buff is on");
        TestAssert.Equal("RivenIzunaBlade", RivenSlot(match, riven, 3), "the R slot holds Wind Slash");
        TestAssert.Near(ad * 1.2, riven.AttackDamage, 1.0, "+20 percent attack damage");
        TestAssert.Near(range + 75.0, riven.AttackRange, 0.5, "+75 attack range");
        // Wind Slash: two foes in the cone, one at full health and one at half; every hit stands at 160 (+0.6 bonus attack damage) times 1 to 3.
        full.Position = new Position2(riven.Position.X + 500.0, riven.Position.Z + 40.0);
        half.Position = new Position2(riven.Position.X + 500.0, riven.Position.Z - 40.0);
        match.Runtime.Tick(0.2);
        full.Health = full.MaxHealth;
        half.Health = half.MaxHealth * 0.5;
        var fullBefore = full.Health;
        var halfBefore = half.Health;
        riven.SpellCooldowns[3] = 0.0;
        match.CastDirected(0, 3, riven.Position, new Position2(riven.Position.X + 900.0, riven.Position.Z));
        match.Runtime.Tick(1.6);
        // No items: the bonus attack damage is the ultimate's 20 percent.
        var bonus = 0.6 * (riven.AttackDamage - ad);
        var fullDamage = RivenRaw(full, fullBefore - full.Health);
        var halfDamage = RivenRaw(half, halfBefore - half.Health);
        TestAssert.Near(160.0 + bonus, fullDamage, 8.0, $"a foe at full health takes 160 + 0.6 bonus attack damage (lost {fullBefore - full.Health:0.0}, armor {full.Armor:0.0}, bonus {bonus:0.0}, AD {riven.AttackDamage:0.0})");
        TestAssert.Near((160.0 + bonus) * (1.0 + 2.0 * (0.5 / 0.75)), halfDamage, 14.0, "a foe at half health takes 1 + 2 x (missing / 0.75) times that");
        TestAssert.True(riven.SpellSeals.IsSealed(false, 3), "Wind Slash spends the ultimate (R is sealed)");
        match.Runtime.Tick(14.0);
        TestAssert.False(riven.Buffs.Any(buff => buff.Name == "RivenFengShuiEngine"), "the buff ends after 15 s");
        TestAssert.Equal("RivenFengShuiEngine", RivenSlot(match, riven, 3), "the R slot has its own spell back");
        TestAssert.False(riven.SpellSeals.IsSealed(false, 3), "unsealed");
        TestAssert.True(riven.SpellCooldowns[3] > 40.0 && riven.SpellCooldowns[3] <= 45.0, "and cools down for the rank's 45 s from the end");
    }
}
