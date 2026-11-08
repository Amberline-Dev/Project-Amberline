using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation.Combat;
using Amberline.Network.Protocol;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-leesin-b6-safeguard-overflow-particle")]
    public static void SafeguardOverflowCreatesItsBoundBreakParticle()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var ending in new[] { "exact", "overflow", "zero-overflow", "lethal", "expiry" })
        {
            using var match = new Match("LeeSin", team, ally: true);
            var runtime = match.Runtime; var lee = match.Hero(0); var enemy = match.Hero(1);
            match.Cast(0, 1, lee);
            var shield = lee.Buffs.Single(buff => buff.Name == "BlindMonkWOneShield");
            // 19 October 2026: the script keeps ShieldAbsorb in the buff's instance variables, not in the payload: the amount is the shipped formula.
            var amount = SafeguardShield(lee);
            match.Clear();
            if (ending == "expiry") runtime.SpellBuffs.Tick(lee, 5.1);
            else
            {
                if (ending == "lethal") lee.Health = 1;
                runtime.Combat.ApplyHit(enemy.NetId, lee.NetId,
                    amount + (ending is "overflow" or "lethal" ? 10 : 0), DamageType.True, DamageSourceKind.Spell);
                if (ending == "zero-overflow")
                {
                    runtime.Delivery.Flush();
                    TestAssert.True(lee.Buffs.Contains(shield));
                    TestAssert.False(match.Captures.SelectMany(capture => capture.Packets)
                        .Any(packet => IsNamedVisual(packet, "blindMonk_W_shield_block")), "exact absorption has no break particle");
                    match.Clear();
                    runtime.Combat.ApplyHit(enemy.NetId, lee.NetId, 1, DamageType.True, DamageSourceKind.Spell);
                }
            }
            runtime.Delivery.Flush();
            var overflow = ending is "overflow" or "zero-overflow" or "lethal";
            TestAssert.Equal(ending == "exact", lee.Buffs.Contains(shield));
            TestAssert.Equal(ending != "lethal", lee.Alive);
            foreach (var capture in match.Captures)
            {
                // BlindMonkWOneShield BuffOnPreDamage's strict-overflow Else.
                var effects = capture.Packets.Where(packet => Opcode(packet) == 0x8C &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == 0x0A1FDF3Bu).ToArray();
                TestAssert.Equal(overflow ? 1 : 0, effects.Length, ending);
                foreach (var effect in effects)
                {
                    var bytes = effect.Plaintext.Span;
                    TestAssert.Equal(lee.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]));
                    TestAssert.Equal(lee.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[29..]));
                    TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
                    TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]));
                }
            }
        }
    }

    [Check("spells-janna-b5-q-release-and-impact-rank")]
    public static void JannaQSamplesReleaseCooldownAndImpactRankSeparately()
    {
        // HowlingGale.OnBuffDeactivate reads the Q rank when the charge ends (GetSlotSpellLevel) for its cooldown {14,13,12,11,10}: a rank
        // learned before the automatic release counts, one learned after it does not.
        foreach (var rankAfterRelease in new[] { false, true })
        {
            using var match = new Match("Janna", Team.Order);
            var hero = match.Hero(0); var target = match.Hero(1);
            hero.Level = 6; hero.SkillPoints = 1;
            match.Runtime.HeroStats.Refresh(hero);
            hero.Mana = hero.MaxMana;
            target.Position = hero.Position + new Position2(900, 0);
            match.CastGround(0, target.Position);
            if (!rankAfterRelease) TestAssert.True(match.Runtime.Spells.RankUp(hero, 0));
            Advance(match, 3.05);
            TestAssert.False(JannaCharging(hero), "the full charge released by itself");
            TestAssert.Near(rankAfterRelease ? 14 : 13, hero.SpellCooldowns[0], .1,
                "JAN-01: automatic release reads the current Q rank");
            if (rankAfterRelease) TestAssert.True(match.Runtime.Spells.RankUp(hero, 0));
        }
    }

    [Check("spells-janna-b5-q-manual-release-spellblade")]
    public static void JannaManualQReleaseRearmsSpellbladeWithoutMana()
    {
        foreach (var automatic in new[] { false, true })
        foreach (var item in new[] { 3057, 3078, 3100 })
        {
            using var match = new Match("Janna", Team.Order);
            var runtime = match.Runtime; var hero = match.Hero(0); var target = match.Hero(1);
            hero.Inventory = [new Amberline.Simulation.InventorySlot(item), null, null, null, null, null];
            runtime.HeroStats.Refresh(hero); hero.Mana = hero.MaxMana;
            // Batch 147: the spellblade arm is the item's own ItemOnSpellCast, so its copy has to reach the
            // inventory slot first (BbItemModule.SyncInventory, one spell update) before the cast below.
            runtime.Tick(0.01);
            var buffName = item == 3100 ? "LichBane" : "Sheen";
            match.CastGround(0, target.Position);
            TestAssert.True(hero.Buffs.Any(b => b.Name == buffName));
            hero.AttackWindup = 0; hero.MissileSpeed = 0;
            TestAssert.True(runtime.Combat.QueueBasicAttack(hero, target,
                new Amberline.Simulation.Combat.AttackRolls(.99, .99, .99)));
            runtime.Combat.CombatTick(.01, regenerate: false);
            TestAssert.False(hero.Buffs.Any(b => b.Name == buffName), "first proc consumed during charge");
            TestAssert.True(hero.Buffs.Any(b => b.Name == "SheenDelay"));
            runtime.Tick(1.5);
            TestAssert.False(hero.Buffs.Any(b => b.Name == "SheenDelay"));
            var mana = hero.Mana;
            if (automatic) runtime.Tick(1.6);
            else match.CastGround(0, target.Position);
            TestAssert.Equal(!automatic, hero.Buffs.Any(b => b.Name == buffName),
                "JAN-02: only a manual release runs the champion-cast item hook");
            TestAssert.True(hero.Mana >= mana - 1e-9, "release never charges mana again");
        }
    }

    [Check("spells-janna-b5-w-effect-data")]
    public static void JannaZephyrReadsPassiveAndSlowEffectRows()
    {
        // SowTheWind: the passive's movement speed {0.08,0.10,0.12,0.14,0.16} (BuffOnUpdateStats) and the slow's multiplier
        // {-0.24,-0.30,-0.36,-0.42,-0.48} (TargetExecute) are the script's tables, picked by the W rank.
        foreach (var rank in new[] { 1, 5 })
        {
            using var match = new Match("Janna", Team.Order);
            var runtime = match.Runtime; var hero = match.Hero(0); var target = match.Hero(1);
            hero.SpellRanks[1] = rank; hero.Mana = hero.MaxMana;
            Advance(match, .1);
            TestAssert.Near(rank == 1 ? .08 : .16,
                hero.Buffs.Single(b => b.Name == "SowTheWind").Payload.MoveSpeedPercent, 1e-6,
                "JAN-05: the ready passive's speed follows the W rank");
            match.Cast(0, 1, target);
            Advance(match, 1.0);
            TestAssert.Near(rank == 1 ? -.24 : -.48,
                target.Buffs.Single(b => b.Name == "Slow").Payload.MoveSpeedMult, 1e-6,
                "the slow multiplier follows the W rank");
        }
    }

    [Check("spells-janna-b5-q-stealthed-collision-visibility")]
    public static void JannaQHitsStealthedHeroesAndVisibleStealthedNonHeroes()
    {
        foreach (var detected in new[] { false, true })
        {
            using var match = new Match("Janna", Team.Order);
            var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
            var minion = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
                EntityKind.Minion, UnitType.Minion, enemy.Team, hero.Position + new Position2(300, 0))
                { Health = 1000, MaxHealth = 1000, Radius = 30 });
            foreach (var unit in new[] { enemy, minion })
                runtime.SpellBuffs.Add(unit, "RegressionStealth", BuffType.Invisibility, 10,
                    unit.NetId, new BuffPayload { Stealthed = true });
            if (detected)
                runtime.World.TimedVisionBubbles.Add(new TimedVisionBubble(
                    runtime.World.Allocate(UnitType.Missile), hero.Team, hero.Position, 1000, 10,
                    RevealStealth: true));
            TestAssert.Equal(detected, runtime.Spells.CanSeeTarget(hero, minion));
            var hp = enemy.Health;
            match.CastGround(0, hero.Position + new Position2(600, 0));
            match.CastGround(0, hero.Position + new Position2(600, 0));
            Advance(match, 1.0);
            TestAssert.True(enemy.Health < hp, "stealthed heroes remain hittable without detection");
            TestAssert.Equal(detected, minion.Health < 1000,
                "HowlingGaleSpell uses BBCanSeeTarget for stealthed non-heroes");
        }
    }

    [Check("spells-janna-b5-zephyr-lethal-impact-effect")]
    public static void JannaLethalZephyrKeepsImpactWithoutSlow()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Janna", team);
            var hero = match.Hero(0); var target = match.Hero(1);
            target.Health = 1;
            match.Cast(0, 1, target);
            var missile = match.Runtime.Spells.SharedProjectiles.Select(projectile => projectile.Snapshot)
                .Single(projectile => projectile.CasterNetId == hero.NetId).MissileNetId;
            match.Clear();
            SpellStep(match, 1); match.Runtime.Delivery.Flush();
            TestAssert.False(target.Alive);
            foreach (var capture in match.Captures)
            {
                TestAssert.True(HasEffect(capture, "SowTheWind_tar"), "lethal impact retains its transient particle");
                // 19 October 2026: Zephyr is a script missile. A cast missile retires implicitly on its impact for the viewers who saw the cast
                // (module-layout.md, RetireCastMissile: only a late viewer gets 0x5D), so the lethal impact sends none; the hand-written module did.
                TestAssert.Equal(0, capture.Packets.Count(p => Opcode(p) == 0x5D &&
                    BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[1..]) == missile));
            }
        }
    }

    [Check("spells-leesin-b5-cripple-spell-shield")]
    public static void LeeCrippleConsumesLateSpellShield()
    {
        using var match = new Match("LeeSin", Team.Order, ally: true);
        var lee = match.Hero(0); var target = match.Hero(1);
        target.Position = lee.Position + new Position2(400, 0);
        match.Cast(0, 2, lee);
        SpellStep(match, .4);
        TestAssert.True(target.Buffs.Any(buff => buff.Name == "BlindMonkEOne"));
        match.Runtime.Combat.CombatTick(2, regenerate: false);
        match.Cast(0, 2, lee);
        // 19 October 2026: the Cripple missile is BlindMonkETwoMissile, a child cast of BlindMonkETwo's script in the shared projectile queue.
        var missile = LeeFlights(match, lee, "BlindMonkETwoMissile").Single();
        TestAssert.Equal(target.NetId, missile.TargetNetId, "the Cripple missile homes on the marked unit");
        match.Runtime.SpellBuffs.Add(target, "BansheesVeil", BuffType.Aura, 10,
            target.NetId, canMitigateDuration: false);
        match.Runtime.Delivery.Flush(); match.Clear();
        SpellStep(match, .5);
        match.Runtime.Delivery.Flush();
        TestAssert.False(target.Buffs.Any(buff => buff.Name == "BansheesVeil"));
        TestAssert.False(target.Buffs.Any(buff => buff.Name == "BlindMonkETwoMissile"),
            "LEE-02: blocked Cripple adds neither slow nor its assist marker");
        TestAssert.Equal(0, LeeFlights(match, lee).Length, "the missile has arrived and left the queue");
        match.Runtime.Combat.CombatTick(14, regenerate: false);
        match.Runtime.Combat.Kill(target, match.Hero(2).NetId);
        TestAssert.Equal(0, lee.ChampionAssists, "blocked E2 does not extend E1 assist credit");
        // 19 October 2026: BlindMonkETwoMissile has no BBDestroyMissile (the client ends a homing missile on arrival), so the blocked missile, like a
        // landed one, sends no DestroyClientMissile; the hand-written module retired it by hand (batch 5).
        foreach (var capture in match.Captures)
            TestAssert.Equal(0, capture.Packets.Count(packet => Opcode(packet) == 0x5D &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == missile.MissileNetId),
                "the blocked homing missile ends on arrival: no DestroyClientMissile, as for a landed one");
    }

    [Check("spells-leesin-a6-tempest-breaks-spell-shield-and-damages")]
    public static void LeeTempestBreaksSpellShieldAndStillDamages()
    {
        // BlindMonkEOne.luaobj: BBBreakSpellShields precedes BBApplyDamage in the ForEachUnit block.
        using var match = new Match("LeeSin", Team.Order, ally: true);
        var lee = match.Hero(0); var target = match.Hero(1);
        target.Position = lee.Position + new Position2(300, 0);
        match.Runtime.SpellBuffs.Add(target, "BansheesVeil", BuffType.Aura, 10,
            target.NetId, canMitigateDuration: false);
        var hp = target.Health;
        match.Cast(0, 2, lee);
        SpellStep(match, .4);
        // Batch 56 (811c362): the popped veil's BuffOnPreDamage zeroes the shockwave's damage and its BuffOnAllowAdd refuses the
        // mark (BansheesVeil.luaobj, WillRemove), so Tempest spends the shield and does nothing else to that unit.
        TestAssert.False(target.Buffs.Any(buff => buff.Name == "BansheesVeil"), "Tempest breaks the shield");
        TestAssert.Near(hp, target.Health, 0, "and the popped veil zeroes the shockwave");
        TestAssert.False(target.Buffs.Any(buff => buff.Name == "BlindMonkEOne"), "and refuses the mark");
    }

    [Check("spells-leesin-b5-safeguard-overflow-only")]
    public static void LeeSafeguardRetainsExactDepletion()
    {
        foreach (var expire in new[] { false, true })
        {
            using var match = new Match("LeeSin", Team.Order);
            var lee = match.Hero(0); var enemy = match.Hero(1);
            match.Cast(0, 1, lee);
            var shield = lee.Buffs.Single(buff => buff.Name == "BlindMonkWOneShield");
            var hp = lee.Health;
            // 19 October 2026: the shield amount is the script's ShieldAbsorb (instance variable), the shipped formula here.
            match.Runtime.Combat.ApplyHit(enemy.NetId, lee.NetId, SafeguardShield(lee),
                DamageType.True, DamageSourceKind.Spell);
            TestAssert.True(lee.Buffs.Contains(shield), "LEE-06: exact absorption retains Safeguard");
            TestAssert.Near(hp, lee.Health, 0);
            if (expire) match.Runtime.SpellBuffs.Tick(lee, 5.1);
            else match.Runtime.Combat.ApplyHit(enemy.NetId, lee.NetId, 1, DamageType.True, DamageSourceKind.Spell);
            TestAssert.False(lee.Buffs.Contains(shield), "zero shield ends on overflow or natural expiry");
            TestAssert.Near(hp - (expire ? 0 : 1), lee.Health, .001);
        }
    }

    [Check("spells-leesin-d1-safeguard-break-refunds-half-its-energy")]
    public static void LeeSafeguardBreakRefundsHalfItsEnergy()
    {
        // Owner decision, 25 October 2026 (batch 220): a broken shield returns no energy, the shipped scripts' rule. The 18 October
        // tooltip refund (non-retail) is retired, so every ending keeps the energy it had. The name is kept for the bundle's list.
        foreach (var ending in new[] { "exact", "overflow", "overflow-near-cap", "expiry" })
        {
            using var match = new Match("LeeSin", Team.Order);
            var lee = match.Hero(0); var enemy = match.Hero(1);
            match.Cast(0, 1, lee);
            var shield = lee.Buffs.Single(buff => buff.Name == "BlindMonkWOneShield");
            var amount = SafeguardShield(lee);
            lee.Mana = ending == "overflow-near-cap" ? lee.MaxMana - 10 : 100;
            if (ending == "expiry") match.Runtime.SpellBuffs.Tick(lee, 5.1);
            else match.Runtime.Combat.ApplyHit(enemy.NetId, lee.NetId, amount + (ending == "exact" ? 0 : 10),
                DamageType.True, DamageSourceKind.Spell);
            var expected = ending switch
            {
                "overflow-near-cap" => lee.MaxMana - 10,
                _ => 100.0,
            };
            TestAssert.Near(expected, lee.Mana, 1e-9, ending);
        }
        // An ally cast places two shields (Lee Sin's and the ally's); breaking either or both, in either order, returns no energy.
        foreach (var allyFirst in new[] { false, true })
        {
            using var match = new Match("LeeSin", Team.Order, ally: true);
            var lee = match.Hero(0); var enemy = match.Hero(1); var ally = match.Hero(2);
            ally.Position = lee.Position + new Position2(600, 0);
            match.Cast(0, 1, ally);
            match.Runtime.Tick(.8);
            var leeShield = lee.Buffs.Single(buff => buff.Name == "BlindMonkWOneShield");
            var allyShield = ally.Buffs.Single(buff => buff.Name == "BlindMonkWOneShield");
            lee.Mana = 100;
            var (first, second) = allyFirst ? (ally, lee) : (lee, ally);
            match.Runtime.Combat.ApplyHit(enemy.NetId, first.NetId, SafeguardShield(lee) + 10,
                DamageType.True, DamageSourceKind.Spell);
            TestAssert.Near(100, lee.Mana, 1e-9, "the first broken shield returns no energy");
            match.Runtime.Combat.ApplyHit(enemy.NetId, second.NetId, SafeguardShield(lee) + 10,
                DamageType.True, DamageSourceKind.Spell);
            TestAssert.False(lee.Buffs.Contains(leeShield) || ally.Buffs.Contains(allyShield), "both shields broke");
            TestAssert.Near(100, lee.Mana, 1e-9, "the second shield of the same cast returns nothing either");
        }
    }

    [Check("spells-yorick-b5-q-cancels-windup-keeps-order")]
    public static void YorickQReplacesAnInProgressSwing()
    {
        using var match = new Match("Yorick", Team.Order);
        var hero = match.Hero(0); var enemy = match.Hero(1);
        var combat = match.Runtime.Combat;
        enemy.Position = hero.Position + new Position2(100, 0);
        hero.AttackWindup = 1;
        hero.AttackTarget = enemy.NetId;
        TestAssert.True(combat.QueueBasicAttack(hero, enemy));
        combat.CombatTick(.2, regenerate: false);
        var oldHit = combat.PendingHits.Single(hit => hit.SourceNetId == hero.NetId);
        var hp = enemy.Health;
        match.Cast(0, 0, hero);
        TestAssert.False(combat.PendingHits.Contains(oldHit), "YOR-01 cancels the old positive windup");
        TestAssert.Equal<uint?>(enemy.NetId, hero.AttackTarget, "Q preserves the attack order");
        TestAssert.Near(0, hero.AttackCooldown, 0, "Q resets the attack timer");
        TestAssert.Near(hp, enemy.Health, 0, "cancelled swing does not deal damage");
        TestAssert.True(combat.QueueBasicAttack(hero, enemy));
        hero.AttackTarget = null; // Isolate the replacement from subsequent ordinary swings.
        combat.CombatTick(1.01, regenerate: false);
        combat.CombatTick(.01, regenerate: false); // Travel/impact follows the windup-crossing tick.
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "YorickSpectral"));
        TestAssert.Equal(1, match.Runtime.World.Entities.Values.Count(unit =>
            unit.Kind == EntityKind.YorickGhoul && unit.Buffs.Any(buff => buff.Name == "YorickSpectralLogic")),
            "exactly one empowered replacement spawns its ghoul");
        TestAssert.True(enemy.Health < hp);
    }

    [Check("spells-yorick-b5-q-hud-permission-lifecycle")]
    public static void YorickQHudSealsUntilConsumptionOrExpiry()
    {
        foreach (var consume in new[] { false, true })
        {
            using var match = new Match("Yorick", Team.Order);
            var hero = match.Hero(0); var enemy = match.Hero(1);
            match.Cast(0, 0, hero);
            AssertYorickQPermission(match, sealedSlot: true);
            match.Runtime.Session.Participants[0].View.Reset();
            match.Clear(); match.Runtime.Delivery.Flush();
            AssertYorickQPermission(match, sealedSlot: true);
            match.Clear();
            if (consume)
                match.Runtime.Combat.ApplyHit(hero.NetId, enemy.NetId, hero.AttackDamage,
                    sourceKind: DamageSourceKind.Basic);
            else match.Runtime.Buffs.TickBuffs(hero, 10.01);
            match.Runtime.Delivery.Flush();
            AssertYorickQPermission(match, sealedSlot: false);
        }
    }

    private static void AssertYorickQPermission(Match match, bool sealedSlot)
    {
        var hero = match.Hero(0);
        var packet = match.Captures[0].Packets.Last(packet => Opcode(packet) == 0xCC &&
            packet.Plaintext.Length >= 35 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[11..]) == hero.NetId);
        var bits = BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[27..]);
        TestAssert.Equal(sealedSlot ? 0u : 1u, bits & 1u, "YOR-08 replicated slot-zero permission");
    }

    [Check("spells-yorick-b5-unholy-symbiosis-display")]
    public static void YorickInstallsAndRestoresThePassiveDisplay()
    {
        using var match = new Match("Yorick", Team.Order);
        var hero = match.Hero(0);
        SpellStep(match, .01);
        var buff = hero.Buffs.Single(buff => buff.Name == "YorickUnholySymbiosis");
        TestAssert.Equal(BuffType.CombatEnhancer, buff.Type);
        TestAssert.Equal(hero.NetId, buff.CasterNetId);
        TestAssert.Near(25000, buff.ExpiresAt, .1, "YOR-09 shipped display duration");
        var attack = hero.AttackDamage;
        hero.AbilityPower += 100;
        SpellStep(match, .1);
        TestAssert.Near(attack, hero.AttackDamage, 0, "display adds no AP-derived gameplay bonus");
        match.Runtime.Session.Participants[0].View.Reset();
        match.Clear(); match.Runtime.Delivery.Flush();
        TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == SpellPackets.BuffAddOpcode &&
            packet.Plaintext.Span.IndexOf(BitConverter.GetBytes(SpellPackets.BuffNameHash("YorickUnholySymbiosis"))) >= 0),
            "reconnect restores the visible passive buff");
        // 19 October 2026 (batch 109): there is no maintenance tick any more. CharScriptYorick.CharOnActivate adds the buff once; it says
        // PersistsThroughDeath and NonDispellable, so the instance Yorick was given stays through his death, and exactly one of it.
        match.Runtime.Combat.Kill(hero, match.Hero(1).NetId);
        SpellStep(match, .1);
        TestAssert.False(hero.Alive, "Yorick is dead");
        TestAssert.Equal(1, hero.Buffs.Count(buff => buff.Name == "YorickUnholySymbiosis"),
            "the display buff persists through death, one instance");
    }

    [Check("spells-b4-ezreal-we-current-impact-rank")]
    public static void EzrealWeReadCurrentRankAtImpact()
    {
        foreach (var slot in new[] { 1, 2 })
        foreach (var allied in slot == 1 ? new[] { false, true } : new[] { false })
        {
            using var match = new Match("Ezreal", Team.Order, ally: allied);
            var hero = match.Hero(0); var target = match.Hero(allied ? 2 : 1);
            match.Hero(1).Position = hero.Position + new Position2(700, 0);
            target.Position = hero.Position + new Position2(600, 0);
            hero.Level = 6; hero.SkillPoints = 1;
            match.Runtime.HeroStats.Refresh(hero);
            match.Runtime.HeroStats.Refresh(target);
            target.Health = target.MaxHealth;
            TestAssert.Near(0, hero.AbilityPower, 0);
            match.CastGround(0, slot == 2 ? hero.Position : target.Position, slot);
            SpellStep(match, .25);
            var missile = EzrealFlights.InFlight(match.Runtime.Spells, hero).Single();
            TestAssert.Equal(1, missile.Rank, "launch metadata retains rank one");
            TestAssert.True(match.Runtime.Spells.RankUp(hero, slot), "rank up while missile is in flight");
            var hp = target.Health;
            SpellStep(match, .5);
            if (allied)
            {
                TestAssert.Near(.25, target.Buffs.Single(buff => buff.Name == "EzrealEssenceFlux").Payload.AttackSpeedMod,
                    1e-9, "allied W uses rank-two attack speed");
                TestAssert.Near(hp, target.Health, 0);
            }
            else
            {
                TestAssert.Near(slot == 1 ? 130 : 125, (hp - target.Health) * (1 + target.SpellBlock / 100),
                    .001, "impact reads rank-two base damage at zero AP");
                if (slot == 1)
                    TestAssert.Near(-.25, target.Buffs.Single(buff => buff.Name == "EzrealEssenceFluxMissile").Payload.AttackSpeedMult,
                        1e-9, "enemy W uses rank-two attack-speed reduction");
            }
        }
    }

    [Check("spells-leesin-r-existing-yorick-ghost-kick")]
    public static void LeeSinKickPreservesExistingYorickGhost()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team);
            var runtime = match.Runtime;
            var lee = match.Hero(0);
            var ghost = match.Hero(1);
            ghost.MaxHealth = ghost.Health = 10000.0;
            var revival = new YorickRevivalState(runtime.Combat.CurrentTime + 10.0);
            ghost.YorickRevival = revival;

            match.Cast(0, 3, ghost);
            runtime.Tick(.3);

            TestAssert.True(ghost.Alive, "a nonlethal R keeps the existing ghost alive");
            TestAssert.True(ReferenceEquals(revival, ghost.YorickRevival),
                "a nonlethal R keeps the same Yorick ghost identity");
            TestAssert.True(ghost.ForcedMove is { Completed: false },
                "a nonlethal R still kicks an already active Yorick ghost");
            TestAssert.True(ghost.Buffs.Any(buff => buff.Name == "BlindMonkRKick"),
                "the existing ghost receives the normal kick control");
        }
    }

    [Check("spells-yorick-owner-death-with-clone-kick-revives")]
    public static void YorickOwnerDeathWithCloneKickRevives()
    {
        // 19 October 2026: Lee Sin's R is his script's, run by a module that binds to the match when its first Lee Sin hero is created, so
        // the opposing hero is created as Lee Sin (renaming another champion's hero to "LeeSin" would leave the module unbound).
        using var match = new Match("Yorick", Team.Order, enemyChampion: "LeeSin");
        var runtime = match.Runtime;
        var owner = match.Hero(0);
        var lee = match.Hero(1);
        lee.SpellRanks[3] = 3;

        match.Cast(0, 3, owner);
        runtime.Tick(.3);
        var clone = runtime.World.Entities.Values.Single(unit =>
            unit.Kind == EntityKind.YorickRevenant);
        // Keep the clone inside Lee Sin's real R cast range while leaving it
        // behind the owner, so the clone kick is active before the owner hit.
        clone.Position = owner.Position + new Position2(-100.0, 0.0);

        // Put the clone in an active kick when the owner's later lethal R is resolved. Lee Sin's R is flagged AffectEnemies | AffectHeroes
        // (BlindMonkRKick.inibin), so the script path no longer lets a real cast pick a revenant (a minion-class pet) as its target;
        // the kick is installed the way the owner's below is.
        runtime.Movement.StartForcedMove(clone.NetId,
            new ForcedMoveState(clone.Position + new Position2(0, 400), 1000.0, 1.0, lee.NetId, "BlindMonkRKick"), cancelOrder: true);
        runtime.Tick(.1);
        TestAssert.True(clone.ForcedMove is { Completed: false },
            "the revenant is already in a Lee Sin kick");

        var ownerMove = new ForcedMoveState(owner.Position + new Position2(0, 400),
            1000.0, 1.0, lee.NetId, "BlindMonkRKick");
        runtime.Movement.StartForcedMove(owner.NetId, ownerMove, cancelOrder: true);
        owner.Health = 1.0;
        lee.SpellCooldowns[3] = 0.0;
        match.Cast(1, 3, owner);
        runtime.Tick(.3);

        TestAssert.True(owner.Alive && owner.YorickRevival is not null,
            "the lethal Lee R begins Yorick's temporary life");
        // Batch 58: the owner's death itself no longer ends a revenant (it lives out its own 10 s). Here the owner is also the
        // revived ally (a self cast), so the revival consumes his YorickRARemovePet marker, and that script's
        // OnBuffDeactivate (remaining duration > 0.5) deals 9999 true damage to the pet holding YorickRAPetBuff2
        // (YorickRARemovePet.luaobj): the revenant ends by its own marker rule, kicked or not.
        TestAssert.False(clone.Alive,
            "the revival's consumed marker ends the actively kicked revenant");
        TestAssert.True(owner.ForcedMove is null,
            "revival does not inherit the old forced move");
    }

    [Check("spells-leesin-secondary-kick-yorick-revival-boundary")]
    public static void LeeSinSecondaryKickDoesNotReapplyMoveAfterYorickRevival()
    {
        // 19 October 2026: Yorick's R is his script's, run by a module that binds to the match when its first Yorick hero is created, so the opposing
        // hero is created as Yorick (renaming another champion's hero would leave the module unbound and refuse the cast).
        using var match = new Match("LeeSin", Team.Order, enemyChampion: "Yorick", extraEnemy: true);
        var runtime = match.Runtime;
        var lee = match.Hero(0);
        var owner = match.Hero(1);
        var victim = match.Hero(2);
        owner.Level = 6;
        owner.SpellRanks[3] = 3;
        runtime.HeroStats.Refresh(owner);
        owner.Health = 1.0;

        // 19 October 2026: Lee Sin's R takes enemy heroes only (BlindMonkRKick SpellData*Flags), so it can no longer be cast on the revenant, a
        // minion-type pet; the hand-written module kicked it. A third hero is the primary victim instead: standing 100 units ahead of Lee Sin,
        // on the line to the owner, its kick sweeps across the owner (and the revenant that starts on him) as a secondary collision,
        // exercising the lethal ApplyDamage -> BeginZombie path.
        match.Cast(1, 3, owner);
        runtime.Tick(0.3);
        var clone = runtime.World.Entities.Values.Single(unit =>
            unit.Kind == EntityKind.YorickRevenant && unit.Alive);
        clone.MaxHealth = clone.Health = 10000.0;
        victim.Position = lee.Position + new Position2(100.0, 0.0);
        match.Cast(0, 3, victim);
        // Batch 175: BlindMonkRKick's BuffOnUpdateActions (the sweep that lands the secondary collision) ran on the bare hook's first
        // line, one ActionsUpdateInterval after the buff's add — 0.25 s of cast windup plus 0.25 s of line. The old 0.3 s pin was the
        // per-step phase of batch 168. Batch 720: the first run is the kicked unit's next 0.25 s pass, 0 to 0.25 s after the add, still
        // inside the 0.6 s.
        runtime.Tick(0.6);

        TestAssert.True(owner.Alive && owner.YorickRevival is not null,
            "a lethal secondary collision begins Yorick's temporary life");
        TestAssert.False(clone.Alive,
            "the revival's consumed marker retires the revenant (self cast)");
        // 19 October 2026: the two asserts that followed (no kick movement and no BlindMonkRDamage on the revived owner) pinned the hand-written
        // kick, which skipped the secondary stun for a unit its own damage had just revived. The script path runs BlindMonkRKick's
        // BuffOnUpdateActions in order: BBApplyDamage (the lethal hit, Yorick's revival clears the owner's controls and forced move), then
        // BBSpellBuffAdd BlindMonkRDamage on that unit; whether the revived unit takes that stun is the engine's buff rule, not a Lee Sin rule.
    }

    [Check("spells-leesin-self-recasts-preserve-facing")]
    public static void LeeSinSelfRecastsPreserveFacing()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var slot in new[] { 1, 2 })
        {
            using var match = new Match("LeeSin", team);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            match.Cast(0, slot, slot == 1 ? hero : enemy);
            match.Runtime.Tick(.5);
            hero.Facing = new Position2(-1, 0);
            hero.Mana = 200;
            match.Clear();
            match.Cast(0, slot, enemy);
            TestAssert.Equal(new Position2(-1, 0), hero.Facing);
            foreach (var capture in match.Captures)
            {
                // 19 October 2026: a script Self cast (BlindMonkWTwo, BlindMonkETwo) lists no target (BbChampionModule.CastSelf); its start and end are the
                // caster's own position. The cast of the spell itself is found by its hash: it is announced before the child missile E2's script casts
                // (batch 134; it was the last 0xBD while CastSelf ran the script first).
                var spellHash = ProtocolEncoding.HashString(slot == 1 ? "BlindMonkWTwo" : "BlindMonkETwo");
                var packet = capture.Packets.Single(p => Opcode(p) == 0xBD &&
                    Decode(p).GetProperty("cast_info").GetProperty("spell_hash").GetUInt32() == spellHash).Plaintext.Span;
                TestAssert.Near(hero.Position.X, BinaryPrimitives.ReadSingleLittleEndian(packet[32..]), .001);
                TestAssert.Near(hero.Position.Z, BinaryPrimitives.ReadSingleLittleEndian(packet[40..]), .001);
                if (slot != 2) continue;
                // The parent is announced first, the child missile its script casts follows (every cast kind sends its 0xBD before its script runs).
                // Batch 582: the Instacast Cripple missile is announced by its 0x41 alone (no cast answer of its own).
                var casts = capture.Packets.Where(p => Opcode(p) is 0xBD or 0x41)
                    .Select(p => Decode(p).GetProperty("cast_info").GetProperty("spell_hash").GetUInt32()).ToList();
                var cripple = ProtocolEncoding.HashString("BlindMonkETwoMissile");
                TestAssert.True(casts.IndexOf(spellHash) >= 0 && casts.IndexOf(spellHash) < casts.IndexOf(cripple),
                    "E2 is announced before the Cripple missile it casts");
                TestAssert.False(capture.Packets.Any(p => Opcode(p) == 0xBD && Decode(p).GetProperty("cast_info").GetProperty("spell_hash").GetUInt32() == cripple),
                    "and the Instacast missile has no cast answer of its own");
            }
            if (slot == 1) TestAssert.True(hero.Buffs.Any(b => b.Name == "BlindMonkWTwo"));
            else TestAssert.Equal(enemy.NetId, LeeFlights(match, hero, "BlindMonkETwoMissile").Single().TargetNetId);
        }
    }

    [Check("spells-leesin-cripple-impact-lifecycle")]
    public static void LeeSinCrippleUsesImpactRankAndSurvivesCasterDeath()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var casterDies in new[] { false, true })
        foreach (var impactRank in new[] { 1, 5 })
        {
            using var match = new Match("LeeSin", team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = new(7300, 7000);
            match.Cast(0, 2, enemy);
            runtime.Tick(.5);
            TestAssert.True(enemy.Buffs.Any(b => b.Name == "BlindMonkEOne"));
            enemy.Position = new(7600, 7000);
            hero.Mana = 200;
            match.Cast(0, 2, enemy);
            var missile = LeeFlights(match, hero, "BlindMonkETwoMissile").Single();
            // TargetExecute reads E's current slot level, including a rank
            // learned while the already-launched missile is travelling.
            hero.SpellRanks[2] = impactRank;
            if (casterDies)
                TestAssert.True(runtime.Combat.ApplyHit(enemy.NetId, hero.NetId,
                    hero.Health + 1, DamageType.True, DamageSourceKind.Spell));
            runtime.Delivery.Flush();
            match.Clear();
            runtime.Tick(.001);
            TestAssert.True(LeeFlights(match, hero, "BlindMonkETwoMissile").Any(flight => flight.MissileNetId == missile.MissileNetId),
                "a launched Cripple remains in flight after caster death");
            runtime.Tick(.5);
            var slow = enemy.Buffs.Single(b => b.Name == "BlindMonkETwoMissile");
            var reduction = impactRank == 1 ? -.3 : -.6;
            AssertReduction(1);
            TestAssert.False(hero.Buffs.Any(b => b.Name == "BlindMonkEManager"), "E2 closed the recast window");
            foreach (var capture in match.Captures)
            {
                TestAssert.False(capture.Packets.Any(p => Opcode(p) == 0x68), "Cripple applies control without a second damage hit");
                // 19 October 2026: BlindMonkETwoMissile has no BBDestroyMissile: the client ends the homing missile on arrival, no DestroyClientMissile.
                TestAssert.Equal(0, capture.Packets.Count(p => Opcode(p) == 0x5D &&
                    BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[1..]) == missile.MissileNetId));
            }
            // 19 October 2026: the decay is the script's own (BlindMonkETwoMissile.BuffOnUpdateStats: 0.75, 0.5, 0.25 of the initial value at 1, 2
            // and 3 s of the buff's age); the age is read from the buff's remaining time (BuffState.ExpiresAt counts down from the duration) instead of
            // the hand-written payload's CrippleAge clock.
            double Age() => 4.0 - slow.ExpiresAt;
            for (var second = 1; second <= 3; second++)
            {
                runtime.Tick(second + .1 - Age());
                AssertReduction(1 - .25 * second);
            }
            runtime.Tick(4.1 - Age());
            TestAssert.False(enemy.Buffs.Contains(slow), "the four-second slow expires normally with a dead caster");

            void AssertReduction(double factor)
            {
                TestAssert.Near(reduction * factor, slow.Payload.MoveSpeedMult, .00001,
                    $"{team}, dead={casterDies}, rank={impactRank}: native slow step {factor}; actual {slow.Payload.MoveSpeedMult}");
                TestAssert.Near(reduction * factor, slow.Payload.AttackSpeedMult, .00001);
            }
        }
    }

    [Check("spells-janna-w-death-launch-boundary")]
    public static void JannaZephyrDeathDistinguishesCastFromFlight()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var launched in new[] { false, true })
        {
            using var match = new Match("Janna", team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = new(7500, 7000);
            enemy.Health = 400;
            enemy.SpellBlock = 0;
            hero.AbilityPower = hero.HealthRegen = enemy.HealthRegen = 0;
            match.Cast(0, 1, enemy);
            var missileId = runtime.Spells.SharedProjectiles.Select(projectile => projectile.Snapshot)
                .Single(projectile => projectile.CasterNetId == hero.NetId).MissileNetId;
            if (launched) runtime.Tick(GameData.Default.Spell("SowTheWind", 5).CastTime + .01);
            TestAssert.Near(400, enemy.Health, .001, "the bolt has not hit before death");
            // OnDeath retires an unlaunched bolt immediately. Keep that
            // packet and the subsequent flight in one exactly-once window.
            match.Clear();
            TestAssert.True(runtime.Combat.ApplyHit(enemy.NetId, hero.NetId,
                hero.Health + 1, DamageType.True, DamageSourceKind.Spell));
            runtime.Delivery.Flush();
            runtime.Tick(1);
            TestAssert.False(hero.Buffs.Any(b => b.Name == "SowTheWind"), "Zephyr cannot remain on the dead caster");
            foreach (var capture in match.Captures)
            {
                var hits = capture.Packets.Where(p => Opcode(p) == 0x68 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[1..]) == hero.NetId &&
                    BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[6..]) == enemy.NetId).ToArray();
                TestAssert.Equal(launched ? 1 : 0, hits.Length, "only a launched bolt hits after death");
                if (launched) TestAssert.Near(60, BinaryPrimitives.ReadSingleLittleEndian(hits[0].Plaintext.Span[14..]), .001);
                // 19 October 2026: the script's cast missile retires on its own, never with a 0x5D for the viewers who saw the cast: a launched
                // bolt ends at its impact, an unlaunched one is dropped with its caster's death (the client stops the cast with the caster).
                TestAssert.Equal(0, capture.Packets.Count(p => Opcode(p) == 0x5D &&
                    BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[1..]) == missileId));
            }
            TestAssert.Equal(launched, enemy.Buffs.Any(b => b.Name == "Slow"));
            if (!launched) TestAssert.Near(400, enemy.Health, .001);
            hero.SpellCooldowns[1] = 0;
            runtime.Tick(.1);
            TestAssert.False(hero.Buffs.Any(b => b.Name == "SowTheWind"), "a ready cooldown cannot recreate the passive on a corpse");
        }
    }

    [Check("spells-launched-control-missiles-after-death")]
    public static void LaunchedControlMissilesSurviveCasterDeath()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var champion in new[] { "LeeSin", "Janna", "Blitzcrank" })
        {
            using var match = new Match(champion, team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = new(7600, 7000);
            enemy.Health = 400;
            enemy.Armor = enemy.SpellBlock = 0;
            hero.AbilityPower = hero.HealthRegen = enemy.HealthRegen = 0;
            match.Cast(0, 0, enemy);
            if (champion == "Janna") match.Cast(0, 0, enemy);
            else if (champion == "LeeSin")
            {
                // 19 October 2026: the line leaves one step after the windup ends (BbLineMissile starts in the queue, and a line that never
                // left is cancelled by its caster's death), so the windup is stepped until the queue reports the line launched.
                for (var step = 0; step < 30 && !LeeFlights(match, hero, "BlindMonkQOne").Any(flight => flight.Launched); step++)
                    runtime.Tick(1.0 / 30);
            }
            else runtime.Tick(.25);
            var missileId = champion switch
            {
                "LeeSin" => LeeFlights(match, hero, "BlindMonkQOne").Single(flight => flight.Launched).MissileNetId,
                "Janna" => JannaWaveMissile(match),
                // Rocket Grab is a script line missile (BbLineMissile): its live missile is in the shared projectile queue.
                _ => runtime.Spells.SharedProjectiles.Select(p => p.Snapshot)
                    .Single(p => p.CasterNetId == hero.NetId && p.Launched && p.MissileNetId != 0).MissileNetId,
            };
            hero.Mana = 0; // Mana Barrier cannot absorb the lethal test hit.
            TestAssert.True(runtime.Combat.ApplyHit(enemy.NetId, hero.NetId,
                hero.Health + 1, DamageType.True, DamageSourceKind.Spell));
            TestAssert.False(hero.Alive);
            runtime.Delivery.Flush();
            match.Clear();
            runtime.Tick(.001);
            foreach (var capture in match.Captures)
                TestAssert.False(capture.Packets.Any(p => Opcode(p) == 0x5D), $"{champion}: no retirement on caster death");
            runtime.Tick(.6);
            foreach (var capture in match.Captures)
            {
                var hit = capture.Packets.Single(p => Opcode(p) == 0x68).Plaintext.Span;
                TestAssert.Near(champion == "LeeSin" ? 50 : 60,
                    BinaryPrimitives.ReadSingleLittleEndian(hit[14..]), .01, $"{champion}: posthumous rank-one damage");
            }
            if (champion == "LeeSin")
            {
                TestAssert.True(enemy.Buffs.Any(b => b.Name == (team == Team.Order ? "BlindMonkQOne" : "BlindMonkQOneChaos")));
            }
            else
                // Rocket Grab (batch 100): the hit's crowd control is BBApplyStun's global Stun (0.6 s); the RocketGrab2 buff is the pull and has ended with its
                // move by now (BuffOnMoveEnd), so what the launched spell still holds is the stun.
                TestAssert.True(enemy.Buffs.Any(b => b.Name == (champion == "Janna" ? "Move" : "Stun")), "the launched spell retains its crowd control");
            runtime.Tick(4);
            TestAssert.True(enemy.ForcedMove is null, "crowd control movement finishes without a living caster");
            TestAssert.False(enemy.Buffs.Any(b => b.Name is "Move" or "RocketGrab2" or "BlindMonkQOne" or "BlindMonkQOneChaos"));
            foreach (var capture in match.Captures)
                TestAssert.Equal(1, capture.Packets.Count(p => Opcode(p) == 0x5D &&
                    BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[1..]) == missileId), "one normal missile retirement");
        }
    }

    [Check("spells-ezreal-launched-missiles-after-death")]
    public static void EzrealLaunchedMissilesSurviveCasterDeath()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var slot in Enumerable.Range(0, 4))
        {
            using var match = new Match("Ezreal", team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = new(7600, 7000);
            // W refreshes the recipient's stats; keep health below Annie's
            // shipped maximum instead of the helper's synthetic 5000 HP.
            enemy.Health = 400;
            hero.HealthRegen = enemy.HealthRegen = 0;
            enemy.Armor = enemy.SpellBlock = 0;
            hero.AbilityPower = 0;
            var damage = slot switch { 0 => 35 + hero.AttackDamage, 1 => 80, 2 => 75, _ => 350 };
            match.Cast(0, slot, slot == 2 ? hero : enemy);
            // The cast frame (the child's .251 s, the other casts' windups) and one step more: a ground line's first step is its launch.
            runtime.Tick(slot switch { 0 => .251, 3 => 1.0, _ => .25 } + .034);
            var missile = EzrealFlights.InFlight(runtime.Spells, hero).Single();
            var initial = missile.Position;
            TestAssert.Near(400, enemy.Health, .001, "launch has not yet reached the target");
            TestAssert.True(runtime.Combat.ApplyHit(enemy.NetId, hero.NetId,
                hero.Health + 1, DamageType.True, DamageSourceKind.Spell));
            var cooldown = hero.SpellCooldowns[0];
            runtime.Delivery.Flush();
            match.Clear();
            runtime.Tick(.001);
            var moved = EzrealFlights.InFlight(runtime.Spells, hero).Where(item => item.MissileNetId == missile.MissileNetId).ToArray();
            TestAssert.True(moved.Length == 1, "death preserves a launched missile");
            TestAssert.True(moved[0].Position.X > initial.X, "the missile continues moving");
            TestAssert.False(match.Captures[0].Packets.Any(p => Opcode(p) == 0x5D), "no premature missile retirement");
            runtime.Tick(.5);
            TestAssert.True(enemy.Health < 400, "the launched missile damages its recipient");
            foreach (var capture in match.Captures)
            {
                var hit = capture.Packets.Single(p => Opcode(p) == 0x68).Plaintext.Span;
                TestAssert.Near(damage, BinaryPrimitives.ReadSingleLittleEndian(hit[14..]), .01,
                    $"{team} slot {slot}: the impact carries the shipped damage before later regeneration");
            }
            TestAssert.False(hero.Alive, "damage does not revive the caster");
            TestAssert.False(hero.Buffs.Any(b => b.Name == "CantAttack"), "death releases the cast lock");
            if (slot == 0)
                TestAssert.Near(Math.Max(0, cooldown - .501 - 1), hero.SpellCooldowns[0], .001, "posthumous Q retains its one-second cooldown reduction");
            // R's shipped range extends well beyond Map 1. Let the actual
            // remaining flight complete, including that off-map segment.
            runtime.Tick(slot == 3 ? 12.0 : 2.0);
            TestAssert.Equal(0, EzrealFlights.InFlight(runtime.Spells, hero).Length, $"{team} slot {slot}: flight still ends normally");
            // 19 October 2026 (batch 103, row 9): the lines (Q, W, R) end with one DestroyMissile for everyone. Arcane Shift's bolt is a homing
            // missile the client made from the child's cast: it retires it on arrival, so the cast's viewers get no 0x5D (RetireCastMissile).
            foreach (var capture in match.Captures)
                TestAssert.Equal(slot == 2 ? 0 : 1, capture.Packets.Count(p => Opcode(p) == 0x5D &&
                    BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[1..]) == missile.MissileNetId), "one retirement per observer");
        }
    }

    [Check("spells-ezreal-unlaunched-casts-on-death")]
    public static void EzrealDeathStillCancelsUnlaunchedCasts()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var slot in Enumerable.Range(0, 4))
        {
            using var match = new Match("Ezreal", team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = new(7600, 7000);
            hero.HealthRegen = enemy.HealthRegen = 0;
            var origin = hero.Position;
            match.Cast(0, slot, enemy);
            TestAssert.True(EzrealFlights.WindingUp(runtime.Spells, hero));
            TestAssert.True(runtime.Combat.ApplyHit(enemy.NetId, hero.NetId,
                hero.Health + 1, DamageType.True, DamageSourceKind.Spell));
            runtime.Delivery.Flush();
            match.Clear();
            runtime.Tick(1.2);
            TestAssert.False(EzrealFlights.WindingUp(runtime.Spells, hero));
            TestAssert.Equal(0, EzrealFlights.InFlight(runtime.Spells, hero).Length);
            TestAssert.Near(5000, enemy.Health, .001);
            // Batch 279: the cast's stop seats Ezreal on his lattice anchor at once; the cancelled blink moves him no further.
            TestAssert.Equal(runtime.Movement.ClientAnchor(origin), hero.Position, "an unfinished Arcane Shift cannot teleport a corpse");
            TestAssert.False(hero.Buffs.Any(b => b.Name == "CantAttack"));
            foreach (var capture in match.Captures)
                TestAssert.False(capture.Packets.Any(p => Opcode(p) == 0x41), "no missile launch after cast cancellation");
        }
    }

    [Check("spells-janna-e-shield-effect")]
    public static void JannaShieldEffectFollowsTheShieldLifetime()
    {
        // EyeOfTheStorm creates EyeoftheStorm_buf on its holder in OnBuffActivate and removes it in OnBuffDeactivate: the effect follows the
        // shield's lifetime, whether the shield is replaced, broken or runs out, and JannaEoTSBuff keeps its own five seconds.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var turretTarget in new[] { false, true })
        {
            using var match = new Match("Janna", team, ally: true);
            var runtime = match.Runtime;
            var target = turretTarget ? runtime.World.Entities.Values.Single(e =>
                e.Name == (team == Team.Order ? "Turret_T1_C_05" : "Turret_T2_C_05")) : match.Hero(2);
            target.Position = new(7100, 7000);
            runtime.Delivery.Flush();
            match.Clear();
            var first = CastAndCheckEffect();
            match.Clear();
            runtime.Buffs.SyncUnitBuffs(target);
            runtime.Delivery.Flush();
            foreach (var capture in match.Captures)
                TestAssert.False(capture.Packets.Any(p => Opcode(p) == 0x8C), "buff replay does not duplicate an active effect");
            match.Hero(0).SpellCooldowns[2] = 0;
            match.Hero(0).Mana = match.Hero(0).MaxMana;
            match.Clear();
            var second = CastAndCheckEffect();
            TestAssert.True(first != second);
            AssertRetired(first);
            match.Clear();
            TestAssert.True(runtime.Combat.ApplyHit(match.Hero(1).NetId, target.NetId, 81, DamageType.True, DamageSourceKind.Spell));
            runtime.Delivery.Flush();
            AssertRetired(second);
            TestAssert.True(target.Buffs.Any(b => b.Name == "JannaEoTSBuff"), "shield retirement leaves the independent AD buff");
            match.Hero(0).SpellCooldowns[2] = 0;
            match.Hero(0).Mana = match.Hero(0).MaxMana;
            match.Clear();
            var third = CastAndCheckEffect();
            match.Clear();
            runtime.Buffs.TickBuffs(target, 5.001);
            runtime.Delivery.Flush();
            AssertRetired(third);
            match.Clear();
            runtime.Buffs.TickBuffs(target, 0.1);
            runtime.Delivery.Flush();
            foreach (var capture in match.Captures)
                TestAssert.False(capture.Packets.Any(p => Opcode(p) == 0x3D), "expiry retires the effect only once");

            uint CastAndCheckEffect()
            {
                match.Cast(0, 2, target);
                uint id = 0;
                foreach (var capture in match.Captures)
                {
                    var raw = capture.Packets.Single(p => Opcode(p) == 0x8C &&
                        BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[6..]) == SpellPackets.BuffNameHash("EyeoftheStorm_buf")).Plaintext.Span;
                    id = BinaryPrimitives.ReadUInt32LittleEndian(raw[25..]);
                    TestAssert.True(id != 0, "the shield owns its shipped persistent effect");
                    TestAssert.Equal(target.NetId, BinaryPrimitives.ReadUInt32LittleEndian(raw[21..]));
                    TestAssert.Equal(target.NetId, BinaryPrimitives.ReadUInt32LittleEndian(raw[29..]));
                }
                return id;
            }
            void AssertRetired(uint id)
            {
                foreach (var capture in match.Captures)
                    TestAssert.Equal(1, capture.Packets.Count(p => Opcode(p) == 0x3D &&
                        BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[5..]) == id));
            }
        }
    }

    [Check("spells-janna-e-turret-stats")]
    public static void JannaTurretDamageBonusReplicatesOnAddAndExpiry()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Janna", team);
            var runtime = match.Runtime;
            var turret = runtime.World.Entities.Values.Single(e =>
                e.Name == (team == Team.Order ? "Turret_T1_C_05" : "Turret_T2_C_05"));
            turret.Position = new(7100, 7000);
            var baseDamage = turret.AttackDamage;
            runtime.Delivery.Flush();
            match.Clear();
            match.Cast(0, 2, turret);
            AssertDamage(baseDamage + 14);
            match.Clear();
            TestAssert.True(runtime.Combat.ApplyHit(match.Hero(1).NetId, turret.NetId, 81, DamageType.True, DamageSourceKind.Spell));
            TestAssert.Near(baseDamage + 14, turret.AttackDamage, 1e-8);
            runtime.Buffs.TickBuffs(turret, 5.001);
            // Batch 365 (wire grace): the expiry's remove, and the stat it carries, go out at the second EndTick after the expiry.
            runtime.Buffs.EndTick();
            runtime.Buffs.EndTick();
            runtime.Delivery.Flush();
            AssertDamage(baseDamage);

            void AssertDamage(double expected)
            {
                foreach (var capture in match.Captures)
                {
                    var stats = capture.Packets.Where(p => Opcode(p) == 0xCC && p.Plaintext.Span[10] == 0x0A &&
                        BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[11..]) == turret.NetId).ToArray();
                    TestAssert.True(stats.Length > 0, "temporary turret stat changes reach each visible peer");
                    var raw = stats[^1].Plaintext.Span;
                    TestAssert.Near(expected, BinaryPrimitives.ReadSingleLittleEndian(raw[19..]), 1e-5);
                    TestAssert.Near(1.3, BinaryPrimitives.ReadSingleLittleEndian(raw[31..]), 1e-6);
                }
            }
        }
    }

    [Check("spells-janna-e-lifecycle")]
    public static void JannaShieldAndDamageBuffHaveSeparateNativeLifecycles()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Janna", team, ally: true);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            var target = match.Hero(2);
            var baseDamage = target.AttackDamage;
            // Batch 357 (restates batch 181): the shield's add and removal carry its binding EyeOfTheStorm (0x02370B4D); Eye Of The Storm has no script.
            var shieldHash = SpellPackets.BuffNameHash("EyeOfTheStorm");
            const uint damageHash = 0x063CDC16; // JannaEoTSBuff: its own binding name, unchanged
            for (var slot = 0; slot < 2; slot++)
                runtime.Buffs.AddBuff(target, $"TooltipSlotOccupant{slot}",
                    BuffType.Internal, 30, target.NetId);
            bool BuffPacket(PlaintextPacket packet, byte opcode, uint hash) =>
                Opcode(packet) == opcode && BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == target.NetId &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[(opcode == 0xBF ? 9 : 6)..]) == hash;
            match.Clear();
            match.Cast(0, 2, target);
            foreach (var capture in match.Captures)
            foreach (var hash in new[] { shieldHash, damageHash })
            {
                var packet = capture.Packets.Single(p => BuffPacket(p, 0xBF, hash)).Plaintext.Span;
                TestAssert.Equal((byte)2, packet[6]);
                // Batch 365 (wire grace): a renewal-type add carries one step of margin on its wire duration.
                TestAssert.Equal((float)(5 + Amberline.Simulation.Spells.Bb.BbBuffRuntime.WireGraceStep), BinaryPrimitives.ReadSingleLittleEndian(packet[17..]));
                TestAssert.Equal(hero.NetId, BinaryPrimitives.ReadUInt32LittleEndian(packet[21..]));
            }
            var eye = target.Buffs.Single(buff => buff.Name == "EyeOfTheStorm");
            TestAssert.True(eye.Slot >= 2 && eye.Slot < SpellPackets.MaxBuffTooltipSlots,
                "the tooltip selector must be an allocated native buff slot");
            TestAssert.Throws<ArgumentOutOfRangeException>(() =>
                SpellPackets.BuffTooltipVariables(target.NetId, SpellPackets.MaxBuffTooltipSlots, 80, 0, 0));
            foreach (var capture in match.Captures)
                AssertEyeTooltip(capture);

            // A late/fog reveal rebuilds the buff snapshot. It must carry
            // the same target-local Index 1 value after AddBuff.
            foreach (var observer in runtime.Delivery.Observers)
                observer.View.Visible.Remove(target.NetId);
            match.Clear();
            runtime.Delivery.Flush();
            foreach (var capture in match.Captures)
                AssertEyeTooltip(capture);

            match.Clear();
            var health = target.Health;
            TestAssert.True(runtime.Combat.ApplyHit(enemy.NetId, target.NetId, 80, DamageType.True, DamageSourceKind.Spell));
            runtime.Delivery.Flush();
            TestAssert.Near(health, target.Health, 1e-8);
            TestAssert.True(target.Buffs.Any(b => b.Name == "EyeOfTheStorm"),
                "the shipped greater-or-equal branch retains a shield exhausted exactly");
            foreach (var capture in match.Captures)
                TestAssert.False(capture.Packets.Any(p => BuffPacket(p, 0x7F, shieldHash) || BuffPacket(p, 0x7F, damageHash)));
            match.Clear();
            TestAssert.True(runtime.Combat.ApplyHit(enemy.NetId, target.NetId, 1, DamageType.True, DamageSourceKind.Spell));
            runtime.Delivery.Flush();
            TestAssert.Near(health - 1, target.Health, 1e-8);
            TestAssert.Near(baseDamage + 14, target.AttackDamage, 1e-8);
            foreach (var capture in match.Captures)
            {
                TestAssert.Equal(1, capture.Packets.Count(p => BuffPacket(p, 0x7F, shieldHash)));
                TestAssert.False(capture.Packets.Any(p => BuffPacket(p, 0x7F, damageHash)));
            }
            runtime.Buffs.TickBuffs(target, 1);
            hero.SpellRanks[2] = 5;
            hero.SpellCooldowns[2] = 0;
            hero.Mana = hero.MaxMana;
            match.Clear();
            match.Cast(0, 2, target);
            TestAssert.Equal(1, target.Buffs.Count(b => b.Name == "JannaEoTSBuff"));
            TestAssert.Near(baseDamage + 50, target.AttackDamage, 1e-8, "replacement changes rank without stacking AD");
            var rankFiveHealth = target.Health;
            TestAssert.True(runtime.Combat.ApplyHit(enemy.NetId, target.NetId, 240, DamageType.True, DamageSourceKind.Spell));
            TestAssert.Near(rankFiveHealth, target.Health, 1e-4, "the replacement carries the rank five shield (240 at no ability power)");
            TestAssert.True(runtime.Combat.Kill(hero, enemy.NetId));
            runtime.Buffs.TickBuffs(target, 4.999);
            TestAssert.Near(baseDamage + 50, target.AttackDamage, 1e-8, "caster death does not remove the ally's buff");
            runtime.Delivery.Flush();
            match.Clear();
            runtime.Buffs.TickBuffs(target, 0.0011);
            // Batch 365 (wire grace): both expiries' removes are held through one step (BuffLifecycle.EndTick); the second call sends them.
            runtime.Buffs.EndTick();
            runtime.Buffs.EndTick();
            runtime.Delivery.Flush();
            TestAssert.Near(baseDamage, target.AttackDamage, 1e-8);
            foreach (var capture in match.Captures)
            foreach (var hash in new[] { shieldHash, damageHash })
                TestAssert.Equal(1, capture.Packets.Count(p => BuffPacket(p, 0x7F, hash)), "each expiry reaches every visible peer");

            void AssertEyeTooltip(Capture capture)
            {
                // Champion sweep TT-01, 27 Sept 2026: Lee Sin also publishes
                // spellbook tooltips; select the shield's allocated buff slot.
                var packet = capture.Packets.Single(p => Opcode(p) == 0x83 &&
                    p.Plaintext.Length >= 24 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[7..]) == target.NetId &&
                    p.Plaintext.Span[11] == eye.Slot);
                var bytes = packet.Plaintext.Span;
                TestAssert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(bytes[5..]));
                TestAssert.Equal(target.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[7..]));
                TestAssert.True(target.NetId != hero.NetId, "tooltip must target the shielded ally");
                TestAssert.Equal((byte)eye.Slot, bytes[11]);
                TestAssert.Near(80.0, BinaryPrimitives.ReadSingleLittleEndian(bytes[12..]), 1e-5,
                    "Eye of the Storm writes the shield to native tooltip Index 1");
                TestAssert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(bytes[16..]));
                TestAssert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(bytes[20..]));
            }
        }
    }

    // The migration recordings intentionally retain their old delayed actions.
    // These cases use explicit timing/effect assertions on both production peers.
    // 19 October 2026: the LeeSin cases (Q1/Q2, W1/W2, E2) left with the hand-written module; Lee Sin is a script champion now.
    internal static bool HasRevisedTiming(string champion, int slot, string? mode) => champion switch
    {
        "Nidalee" => slot == 3 || mode == "cougar",
        "Yorick" => true,
        "Vayne" => slot is 0 or 3,
        "Blitzcrank" => slot is 1 or 2,
        _ => false,
    };

    internal static void VerifyRevisedTiming(string champion, int slot, int rank, string? mode, Team team)
    {
        using var match = new Match(champion, team, ally: true);
        var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
        hero.SpellRanks[slot] = rank;
        hero.HealthRegen = enemy.HealthRegen = hero.ManaRegen = 0;
        hero.AttackCooldown = 100;
        hero.Facing = new Position2(1, 0);
        enemy.Position = new Position2(7600, 7000);
        match.Hero(2).Position = new Position2(7500, 7250);
        switch (champion)
        {
            case "Nidalee":
                hero.SpellRanks[3] = slot == 3 ? rank : 3;
                var armor = hero.Armor;
                match.Cast(0, 3, hero); AssertDuration(match, 0);
                TestAssert.True(hero.Buffs.Any(buff => buff.Name == "AspectOfTheCougar"), "R transforms on receipt");
                TestAssert.Near(armor + 10 * hero.SpellRanks[3], hero.Armor, .001, "cougar rank armor immediately applied");
                if (slot != 3)
                {
                    runtime.Tick(.6); match.Clear(); match.Cast(0, slot, enemy);
                    AssertDuration(match, slot == 2 ? .25 : 0);
                    if (slot == 0)
                    {
                        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "Takedown"), "cougar Q arms immediately");
                        TestAssert.Near(0, hero.AttackCooldown, 0, "cougar Q resets attack");
                    }
                    else if (slot == 1) AssertDashAndLanding(match);
                    else
                    {
                        TestAssert.True(hero.ChampionState!.BlocksActions, "Swipe retains its cast");
                        runtime.Tick(.26);
                        TestAssert.False(hero.ChampionState!.BlocksActions, "Swipe releases cast lock");
                    }
                }
                break;
            case "Yorick" when slot == 3:
                match.Cast(0, 3, hero); AssertDuration(match, .25);
                runtime.Tick(.3);
                var revenant = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.YorickRevenant);
                var controller = revenant.Buffs.Single(buff => buff.Name == "YorickRAPetBuff2");
                TestAssert.True(controller.Payload.Ghosted, "R clone uses shipped SetGhosted state");
                foreach (var capture in match.Captures)
                    TestAssert.True(capture.Packets.Any(packet => IsGhostedStats(packet, revenant.NetId)),
                        "R collision state reaches both peers");
                if (mode == "owner-dies")
                {
                    runtime.Combat.Kill(hero, enemy.NetId);
                    var respawn = hero.RespawnAt;
                    TestAssert.True(hero.YorickRevival is not null, "R death begins temporary revival");
                    runtime.Tick(1.1);
                    TestAssert.True(hero.YorickRevival is not null, "temporary life continues after the one-second delay");
                    runtime.Tick(respawn - runtime.Combat.CurrentTime + .1);
                    TestAssert.True(hero.Alive && hero.YorickRevival is null && hero.RespawnAt == 0,
                        "R owner still completes normal respawn");
                }
                else
                {
                    runtime.Tick(10.1);
                    TestAssert.False(revenant.Alive, "R clone expires after ten seconds");
                }
                break;
            case "Yorick":
                enemy.Position = new Position2(7300, 7000);
                var hp = enemy.Health;
                match.Cast(0, slot, slot == 0 ? hero : enemy); AssertDuration(match, slot == 2 ? .25 : 0);
                if (slot == 0)
                {
                    TestAssert.True(hero.Buffs.Any(buff => buff.Name == "YorickSpectral"), "Q arms immediately");
                    TestAssert.Near(0, hero.AttackCooldown, 0, "Q resets attack");
                    TestAssert.Near(0, hero.SpellCooldowns[0], 0, "Q cooldown waits for consumption");
                    TestAssert.False(hero.ChampionState!.BlocksActions, "Q never blocks empowered attack");
                    if (mode == "caster-dies")
                    {
                        runtime.Combat.Kill(hero, enemy.NetId); runtime.Tick(.3);
                        TestAssert.False(runtime.World.Entities.Values.Any(unit => unit.OwnerNetId == hero.NetId),
                            "death before the empowered attack produces no ghoul");
                        // Shipped YorickSpectral.luaobj explicitly sets
                        // PersistsThroughDeath=true (instructions 14-15).
                        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "YorickSpectral"), "armed Q survives death");
                    }
                    else
                    {
                        if (mode == "target-moves") enemy.Position = new Position2(8000, 8000);
                        runtime.Tick(10.1);
                        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "YorickSpectral"), "unconsumed Q expires");
                        TestAssert.True(hero.SpellCooldowns[0] > 0, "Q expiry starts cooldown");
                    }
                }
                else
                {
                    TestAssert.True(hero.ChampionState!.BlocksActions, "W/E use one quarter-second cast");
                    runtime.Tick(.1);
                    TestAssert.False(runtime.World.Entities.Values.Any(unit => unit.OwnerNetId == hero.NetId), "no early ghoul");
                    runtime.Tick(.16);
                    TestAssert.Equal(1, runtime.World.Entities.Values.Count(unit => unit.OwnerNetId == hero.NetId), "one ghoul by .26 seconds");
                    TestAssert.False(hero.ChampionState!.BlocksActions, "no second summon cast lock");
                    if (slot == 2) TestAssert.Near((25 + 30 * rank) * 100 / (100 + enemy.SpellBlock), hp - enemy.Health, .01, "E rank damage");
                    runtime.Tick(5.2);
                    TestAssert.False(runtime.World.Entities.Values.Any(unit => unit.OwnerNetId == hero.NetId && unit.Alive), "ghoul lifetime ends");
                }
                break;
            case "Vayne":
                if (slot == 3 || mode == "ultimate-tumble")
                {
                    hero.SpellRanks[3] = slot == 3 ? rank : 3;
                    match.Cast(0, 3, hero); AssertDuration(match, 0);
                    TestAssert.True(hero.Buffs.Any(buff => buff.Name == "VayneInquisition"), "R applies immediately");
                    TestAssert.False(hero.ChampionState!.BlocksActions, "R has no cast lock");
                    if (slot == 3) break;
                    match.Clear();
                }
                match.Cast(0, 0, mode == "zero-aim" ? hero : enemy); AssertDuration(match, 0);
                if (mode == "target-moves") enemy.Position += new Position2(0, 700);
                if (mode == "caster-dies")
                {
                    runtime.Combat.Kill(hero, enemy.NetId); runtime.Tick(.8);
                    TestAssert.True(hero.ForcedMove is null, "death cancels Tumble");
                }
                else AssertDashAndLanding(match);
                break;
            case "Blitzcrank":
                match.Cast(0, slot, hero); AssertDuration(match, 0);
                var name = slot == 1 ? "Overdrive" : "PowerFist";
                TestAssert.True(hero.Buffs.Any(buff => buff.Name == name), "W/E buff is immediate");
                TestAssert.False(hero.ChampionState!.BlocksActions, "W/E never leave a cast lock");
                if (slot == 2) TestAssert.Near(0, hero.AttackCooldown, 0, "E resets attack");
                runtime.Tick(slot == 1 ? 8.1 : 10.1);
                TestAssert.False(hero.Buffs.Any(buff => buff.Name == name), "buff expires from command time");
                break;
            default: throw new InvalidOperationException("Unreviewed timing case");
        }
    }

    private static void AssertDuration(Match match, double expected)
    {
        foreach (var capture in match.Captures)
        {
            var packet = capture.Packets.Last(packet => Opcode(packet) == 0xBD);
            var info = packet.Plaintext.Span[9..];
            TestAssert.Near(expected, BinaryPrimitives.ReadSingleLittleEndian(info[(0x30 + 17 * info[0x2F])..]), .00001,
                "native acknowledgement duration on every peer");
        }
    }

    private static void AssertDashAndLanding(Match match)
    {
        var hero = match.Hero(0); var runtime = match.Runtime;
        TestAssert.True(hero.ForcedMove is not null, "dash starts immediately");
        TestAssert.Equal(0, runtime.HeroStats.Snapshot(hero).ActionState & 5, "dash locks movement and attacks");
        for (var step = 0; hero.ForcedMove is not null && step < 25; step++) runtime.Tick(.1);
        TestAssert.True(hero.ForcedMove is null, "dash finishes");
        TestAssert.Equal(5, runtime.HeroStats.Snapshot(hero).ActionState & 5, "landing restores action permissions");
        TestAssert.True(runtime.Navigation.IsWalkable(hero.Position), "landing remains on walkable terrain");
    }
}
