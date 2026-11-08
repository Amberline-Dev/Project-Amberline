using System.Buffers.Binary;
using System.Text;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-nidalee-b5-lethal-impact-effects")]
    public static void NidaleeLethalImpactsKeepTransientEffects()
    {
        foreach (var spell in new[] { "JavelinToss", "Takedown", "Swipe" })
        {
            using var match = new Match("Nidalee", Team.Order);
            var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
            enemy.Health = 1; enemy.Armor = enemy.SpellBlock = 0;
            if (spell != "JavelinToss") enemy.Position = hero.Position + new Position2(100, 0); // the cougar spells and the swing are close work
            if (spell != "JavelinToss") { RankUpTheUltimate(match, hero); match.Cast(0, 3, hero); }
            match.Clear();
            if (spell == "JavelinToss")
            {
                match.CastGround(0, enemy.Position); Advance(match, 1);
            }
            else if (spell == "Takedown")
            {
                // The empowered attack is the script's attack override: it rides on the swing, not on a bare ApplyHit.
                match.Cast(0, 0, hero);
                Advance(match, .3);
                hero.AttackCooldown = 0.0;
                TestAssert.True(runtime.Combat.QueueBasicAttack(hero, enemy), "the empowered attack");
                Advance(match, 1.2);
            }
            else
            {
                match.CastDirected(0, 2, hero.Position, enemy.Position); Advance(match, .5);
            }
            runtime.Delivery.Flush();
            TestAssert.False(enemy.Alive, spell + " kills its victim");
            var effect = spell switch
            {
                "JavelinToss" => "nidalee_javelinToss_tar",
                "Takedown" => "nidalee_cougar_takedown_tar",
                _ => "nidalee_cougar_swipe_tar",
            };
            TestAssert.True(HasEffect(match.Captures[0], effect), spell + " retains lethal impact FX");
        }
    }

    [Check("spells-nidalee-b5-takedown-forced-shield-break")]
    public static void NidaleeTakedownBreaksShieldButPlainAttackDoesNot()
    {
        using var match = new Match("Nidalee", Team.Order);
        var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
        runtime.SpellBuffs.Add(enemy, "BansheesVeil", BuffType.Aura, 10, enemy.NetId, new BuffPayload());
        runtime.Combat.ApplyHit(hero.NetId, enemy.NetId, hero.AttackDamage,
            DamageType.Physical, DamageSourceKind.Basic);
        TestAssert.True(enemy.Buffs.Any(buff => buff.Name == "BansheesVeil"), "ordinary attacks preserve spell shields");
        match.Cast(0, 3, hero); match.Cast(0, 0, hero);
        var health = enemy.Health;
        runtime.Combat.ApplyHit(hero.NetId, enemy.NetId, hero.AttackDamage,
            DamageType.Physical, DamageSourceKind.Basic);
        TestAssert.False(enemy.Buffs.Any(buff => buff.Name == "BansheesVeil"), "NID-03: Takedown explicitly breaks the shield");
        // Batch 56 (811c362): the popped veil's BuffOnPreDamage zeroes the attack that follows the break.
        TestAssert.Near(health, enemy.Health, 0, "forced break pops the veil, which zeroes the attack damage");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "Takedown"));
    }

    [Check("spells-nidalee-b5-ally-experience-cadence")]
    public static void NidaleeAllyExperienceUsesSharedProgression()
    {
        // 19 October 2026: the passive is CharScriptNidalee.UpdateSelfBuffActions (BBExecutePeriodically 5 s, ExecuteImmediately, BBIncExp 5 for each
        // lower-level allied champion within 500), polled by the script module's tick, not by the late champion phase this check used to drive.
        using var match = new Match("Nidalee", Team.Order, ally: true);
        var runtime = match.Runtime; var hero = match.Hero(0); var ally = match.Hero(2); var enemy = match.Hero(1);
        hero.Level = 3; ally.Position = hero.Position + new Position2(500, 0);
        Advance(match, .1);
        TestAssert.Near(5, ally.Experience, 0, "NID-04: immediate grant includes the 500-unit boundary");
        Advance(match, 4.8);
        TestAssert.Near(5, ally.Experience, 0);
        Advance(match, .3);
        TestAssert.Near(10, ally.Experience, 0, "five-second grant");
        ally.Level = hero.Level; Advance(match, 5);
        TestAssert.Near(10, ally.Experience, 0, "equal-level ally is excluded");
        ally.Level = 1; ally.Position = hero.Position + new Position2(501, 0);
        Advance(match, 5);
        TestAssert.Near(10, ally.Experience, 0, "out-of-range ally is excluded");
        TestAssert.Near(0, hero.Experience, 0, "self excluded");
        TestAssert.Near(0, enemy.Experience, 0, "enemy excluded");
        ally.Position = hero.Position; ally.Experience = 275;
        Advance(match, 5);
        TestAssert.Near(280, ally.Experience, 0);
        TestAssert.Equal(2, ally.Level, "shared progression performs level-up");
    }

    [Check("spells-shaco-b5-lethal-impact-effects")]
    public static void ShacoLethalImpactsKeepTransientEffects()
    {
        foreach (var attack in new[] { "backstab", "clone", "nova" })
        {
            using var match = new Match("Shaco", Team.Order);
            var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
            var source = attack == "backstab" ? hero : SpawnHallucinateClone(match, hero);
            hero.SpellRanks[2] = 0;
            source.SpellRanks[2] = 0;
            enemy.Position = source.Position + new Position2(100, 0);
            source.Facing = enemy.Facing = new Position2(1, 0);
            enemy.Health = 1;
            match.Clear();
            if (attack == "nova") runtime.Combat.Kill(source, enemy.NetId);
            else runtime.Combat.ApplyHit(source.NetId, enemy.NetId, 100,
                DamageType.Physical, DamageSourceKind.Basic);
            runtime.Delivery.Flush();
            TestAssert.False(enemy.Alive, attack + " kills");
            TestAssert.True(HasEffect(match.Captures[0], attack == "nova"
                ? "Hallucinate_nova" : "AbsoluteZero_tar"), attack + " retains its effect");
            TestAssert.False(enemy.Buffs.Any(buff => buff.Name == "TwoShivPoison"));
        }
    }

    // What ends Deceive on a cast is its own shipped BuffOnSpellCast, not a hand-written rule (batch 164): Spells/
    // Deceive.luaobj is `BBIf SpellVars.CastingBreaksStealth == true -> BBSpellBuffRemoveCurrent(Owner)`, then
    // `BBElseIf SpellVars.CastingBreaksStealth == false -> BBElseIf SpellVars.DoesntTriggerSpellCasts != true -> remove`.
    // So Recall, SummonerSmite, SummonerHeal and the other casts that say CastingBreaksStealth true take it; SummonerFlash
    // says CastingBreaksStealth false and DoesntTriggerSpellCasts true and keeps it, as does Hallucinate (no flag at all,
    // so neither branch runs). The item active keeps the one kept hand-written reader (BbItemModule.CastingBreaksStealth):
    // an item's own active raises ItemOnSpellCast of the copies and no BuffOnSpellCast, so the script never sees it.
    [Check("spells-shaco-b5-cast-breaks-deceive")]
    public static void ShacoCastStealthBoundaries()
    {
        foreach (var cast in new[] { "item", "smite", "heal", "recall", "flash", "ultimate" })
        {
            using var match = new Match("Shaco", Team.Order);
            var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
            hero.UnlimitedResource = true;
            match.CastGround(0, hero.Position);
            runtime.Buffs.TickBuffs(hero, .06);
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "Deceive"));
            runtime.Delivery.Flush();
            TestAssert.False(runtime.Session.Participants[1].View.Visible.Contains(hero.NetId));
            if (cast == "item")
            {
                hero.Inventory = [new Amberline.Simulation.InventorySlot(3128), null, null, null, null, null];
                match.Cast(0, 4, enemy);
            }
            else if (cast == "recall") match.CastGround(0, hero.Position, SpellSystem.RecallSlot);
            else if (cast == "ultimate") match.Cast(0, 3, hero);
            else
            {
                hero.SummonerSpells[0] = cast switch
                {
                    "smite" => "SummonerSmite", "heal" => "SummonerHeal", _ => "SummonerFlash",
                };
                var target = cast == "smite"
                    ? runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Monster),
                        EntityKind.Neutral, UnitType.Monster, Team.Neutral, enemy.Position)
                        { Health = 2000, MaxHealth = 2000, Created = true }) : hero;
                // Smite is unit-targeted: publish the newly registered monster before client input.
                runtime.Delivery.Flush();
                TestAssert.True(runtime.Session.Participants[0].View.Visible.Contains(target.NetId), cast);
                match.Cast(0, 0, target, summoner: true);
            }
            var retained = cast is "flash" or "ultimate";
            TestAssert.Equal(retained, hero.Buffs.Any(buff => buff.Name == "Deceive"), cast);
            TestAssert.Near(retained ? 0 : 11, hero.SpellCooldowns[0], 1e-6, cast);
            runtime.Delivery.Flush();
            TestAssert.Equal(!retained, runtime.Session.Participants[1].View.Visible.Contains(hero.NetId), cast);
        }
    }

    [Check("spells-shaco-b5-cancelled-deceive-critical-chance")]
    public static void ShacoCancelledQAttackKeepsOnlyCriticalDamage()
    {
        using var match = new Match("Shaco", Team.Order);
        var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
        match.CastGround(0, hero.Position);
        runtime.Buffs.TickBuffs(hero, .06);
        enemy.Position = hero.Position + new Position2(100, 0);
        TestAssert.True(runtime.Combat.QueueBasicAttack(hero, enemy,
            new Amberline.Simulation.Combat.AttackRolls(.5, 1, 1)));
        runtime.Combat.InterruptBasicAttack(hero);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "DeceiveCritBonus"));
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "Deceive"));
        TestAssert.Near(0, hero.CritChance, 1e-9);
        TestAssert.Near(-.6, hero.Buffs.Single(buff => buff.Name == "DeceiveCritBonus").Payload.CritDamage, 1e-9);
        var attack = runtime.Combat.BasicAttackDamage(hero, enemy,
            new Amberline.Simulation.Combat.AttackRolls(.5, 1, 1));
        TestAssert.False(attack.Critical, "cancelled Q does not guarantee the next attack");
    }

    [Check("spells-shaco-b5-backstab-aura-death-reconnect")]
    public static void ShacoBackstabAuraPersistsAndReplays()
    {
        using var match = new Match("Shaco", Team.Order);
        var runtime = match.Runtime; var hero = match.Hero(0);
        runtime.Spells.TickLateChampions(0);
        var aura = hero.Buffs.Single(buff => buff.Name == "Backstab");
        TestAssert.Equal(BuffType.Aura, aura.Type);
        TestAssert.Near(25000, aura.ExpiresAt, 1e-9);
        // Ordinary lethal damage schedules respawn; direct Kill is also used by non-respawning lifecycles.
        runtime.Combat.ApplyHit(match.Hero(1).NetId, hero.NetId, hero.Health * 2,
            DamageType.True, DamageSourceKind.Spell);
        TestAssert.False(hero.Alive);
        runtime.Spells.TickLateChampions(0);
        TestAssert.True(ReferenceEquals(aura, hero.Buffs.Single(buff => buff.Name == "Backstab")));
        TestAssert.True(runtime.Combat.RespawnImmediately(hero));
        int AuraAdds() => match.Captures[0].Packets.Count(packet => Opcode(packet) == SpellPackets.BuffAddOpcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[9..]) == SpellPackets.BuffNameHash("Backstab"));
        // The reconnect observer reuses this capture: count only the baseline it receives.
        // This harness drives both the session reconnect and a fresh observer baseline into
        // the same view before one flush, so the aura may be announced by each path.
        var before = AuraAdds();
        ReconnectShacoObserver(match);
        TestAssert.True(AuraAdds() - before >= 1, "the reconnect baseline carries the persistent aura");
        TestAssert.True(ReferenceEquals(aura, hero.Buffs.Single(buff => buff.Name == "Backstab")));
    }

    private static void ReconnectShacoObserver(Match match)
    {
        var runtime = match.Runtime; var hero = match.Hero(0);
        TestAssert.True(runtime.Session.Disconnect(1));
        TestAssert.True(runtime.Delivery.RemoveObserver(1));
        var participant = runtime.Session.AuthenticateReconnect(101, runtime.Session.BuildKeyCheck(1))
            ?? throw new InvalidOperationException("reconnect rejected");
        runtime.Session.Receive(101, 1, [0xC5]);
        runtime.Session.Receive(101, 1, [0xC6, 0, 0, 0, 0]);
        runtime.World.Clock.SetVirtual(participant.SpawnAt ?? throw new InvalidOperationException("no spawn time"));
        runtime.Session.RunSchedulers();
        runtime.Session.Receive(101, 1, [0xC6, 0, 0, 0, 0]);
        runtime.Session.RunSchedulers();
        TestAssert.True(participant.Ready);
        runtime.Delivery.AddObserver(new Amberline.Network.Replication.ReplicationObserver(
            participant.PlayerId, participant.Team, hero.NetId, match.Captures[0],
            ready: participant.Ready, view: participant.View, clientId: participant.ClientId));
        match.Clear(); runtime.Delivery.Flush();
    }

    [Check("spells-orianna-b5-q-landing-current-rank")]
    public static void OriannaLandingDamageReadsCurrentQRank()
    {
        using var match = new Match("Orianna", Team.Order);
        var hero = match.Hero(0); var target = match.Hero(1);
        hero.AbilityPower = 0; target.SpellBlock = 0;
        var landing = hero.Position + new Position2(600, 0);
        target.Position = landing + new Position2(0, 150);
        var health = target.Health;
        match.CastGround(0, landing);
        match.Runtime.Spells.Tick(.1);
        hero.SpellRanks[0] = 2;
        match.Runtime.Spells.Tick(.5);
        TestAssert.Near(100, health - target.Health, .001,
            "outside the full line corridor, inside the 175-unit landing burst");
    }

    private static void SpellStep(Match match, double dt)
    {
        match.Runtime.Spells.Tick(dt);
        match.Runtime.Spells.TickLateChampions(dt);
    }

    /// <summary>
    /// The ranks of these matches are written straight into the hero; in play the ultimate is trained through the rank-up, whose level-up hook stores the
    /// cougar damage by its rank (CharScriptNidalee.CharOnLevelUpSpell: Takedown, Pounce and Swipe). Run that hook once so the form's spells have their figures.
    /// </summary>
    private static void RankUpTheUltimate(Match match, Entity hero) =>
        ((ChampionSpellDispatcher)match.Runtime.Dependencies.ChampionDispatcher).FindModule(hero.ChampionName)!.OnRankUp(match.Runtime.Spells, hero, 3, hero.SpellRanks[3]);

    private static void Advance(Match match, double seconds)
    {
        for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += .01) match.Runtime.Tick(.01);
    }

    private static bool HasEffect(Capture capture, string name) => capture.Packets.Any(packet =>
        Opcode(packet) == FxPackets.CreateGroupOpcode &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == SpellPackets.BuffNameHash(name));

    [Check("spells-vayne-b5-fade-buff-type")]
    public static void VayneFadeKeepsStealthWithCombatEnhancerWireType()
    {
        using var match = new Match("Vayne", Team.Order);
        var runtime = match.Runtime; var hero = match.Hero(0);
        match.Cast(0, 3, hero);
        match.CastGround(0, hero.Position + new Position2(300, 0));
        var fade = hero.Buffs.Single(buff => buff.Name == "VayneTumbleFade");
        TestAssert.Equal(BuffType.CombatEnhancer, fade.Type);
        TestAssert.True(fade.Payload.Stealthed);
        TestAssert.False(runtime.Combat.IsTargetable(hero, match.Hero(1)));
        var capture = new Capture();
        var observer = new Amberline.Network.Replication.ReplicationObserver(90, hero.Team, hero.NetId, capture);
        observer.View.Known.UnionWith(runtime.Session.Participants.Select(p => p.Hero!.NetId));
        runtime.Delivery.AddObserver(observer);
        runtime.Delivery.Flush();
        foreach (var packets in new[] { match.Captures[0].Packets, capture.Packets })
        {
            var packet = packets.Single(packet => Opcode(packet) == SpellPackets.BuffAddOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[9..]) == SpellPackets.BuffNameHash("VayneTumbleFade")); // Batch 357 (restates batch 181): the binding; its BuffName VayneInquisitionStealth has no script
            TestAssert.Equal((byte)2, packet.Plaintext.Span[6], "VAY-05: initial and reconnect type 2");
        }
    }

    [Check("spells-ezreal-e-faces-bolt-target")]
    public static void ArcaneShiftFacesItsSelectedTargetBeforeBoltReplication()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Ezreal", team);
            var hero = match.Hero(0);
            match.Hero(1).Position = hero.Position + new Position2(100, 1);
            match.CastGround(0, hero.Position + new Position2(475, 0), 2);
            match.Clear();
            SpellStep(match, .25);
            match.Runtime.Delivery.Flush();
            TestAssert.Equal(new Position2(-1, 0), hero.Facing, "EZ-03: face west after blinking east");
            var expected = SpellPackets.FaceDirection(hero.NetId, new Position3(-1, 0, 0));
            // 19 October 2026: the script announces the homing bolt with the cast of EzrealArcaneShiftMissile (extra slot 43, FireWithoutCasting)
            // whose only target is the picked unit. Batch 177 part 2: that spell is Instacast (SpellData*Flags 115716, bit 2), so the cast answer
            // builds no missile on the client (its instacast handler returns before the cast-and-serve routine). The bolt is announced by its
            // missile packet after its cast, for every view the cast reached. The batch-103 premise ("the client starts its missile from that cast")
            // holds for a spell that is not Instacast only. Batch 582: the bolt's cast reaches every view as that missile packet alone (no cast answer,
            // which built a second bolt whenever a SpellCaster was current on the client; ProtocolSpellEffectSink.SendInstantMissileCast).
            var childHash = SpellSystem.ProtocolHash("EzrealArcaneShiftMissile");
            foreach (var capture in match.Captures)
            {
                var face = capture.Packets.FindIndex(packet =>
                    packet.Plaintext.Span.SequenceEqual(expected.Plaintext.Span));
                var bolt = capture.Packets.FindIndex(packet => Opcode(packet) == 0x41 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[100..]) == childHash);
                TestAssert.True(face >= 0 && bolt > face,
                    "both observers receive exact target-facing before the E bolt");
                var cast = capture.Packets[bolt].Plaintext.Span;
                TestAssert.Equal((byte)1, cast[98 + 47], "the bolt's cast lists one target");
                TestAssert.Equal(match.Hero(1).NetId, BinaryPrimitives.ReadUInt32LittleEndian(cast[(98 + 48)..]),
                    "the bolt's cast names the unit the hero turned to");
                var missileId = Decode(capture.Packets[bolt]).GetProperty("cast_info").GetProperty("missile_netid").GetUInt32();
                var announced = capture.Packets.Count(packet => Opcode(packet) == 0x41 &&
                    Decode(packet).GetProperty("cast_info").GetProperty("missile_netid").GetUInt32() == missileId);
                var answers = capture.Packets.Count(packet => Opcode(packet) == 0xBD &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[11..]) == childHash);
                TestAssert.True(announced == 1 && answers == 0, "the bolt's missile packet comes once, with no cast answer for it");
            }
        }
    }

    [Check("spells-ezreal-lethal-impact-effects")]
    public static void EzrealImpactsRemainVisibleOnLethalHits()
    {
        foreach (var (slot, effect) in new[] { (0, "Ezreal_mysticshot_tar"),
                     (1, "Ezreal_essenceflux_tar"), (3, "Ezreal_TrueShot_tar") })
        {
            using var match = new Match("Ezreal", Team.Order);
            var target = match.Hero(1);
            target.Health = 1;
            match.Cast(0, slot, target);
            SpellStep(match, 1.5);
            // Trueshot Barrage's line is made at the end of its one-second windup and takes its first step in the next one.
            SpellStep(match, .5);
            match.Runtime.Delivery.Flush();
            TestAssert.False(target.Alive, "impact kills its recipient");
            foreach (var capture in match.Captures)
                TestAssert.True(HasEffect(capture, effect), "lethal impact retains its transient effect");
            // 19 October 2026: EzrealEssenceFluxMissile.TargetExecute adds the debuff right after its damage block with no Alive test (the
            // hand-written module tested it), so a lethal W leaves the five-second debuff on the corpse, as every script debuff added after
            // a lethal hit. It runs out with its own timer: it must never outlive it (BuffLifecycle.TickBuffs ticks a corpse's buffs).
            match.Runtime.Tick(5.1);
            TestAssert.False(target.Buffs.Any(buff => buff.Name == "EzrealEssenceFluxMissile"),
                "lethal W cannot leave a persistent attack-speed debuff");
        }
    }

    [Check("spells-ezreal-q-stealthed-nonhero-collision")]
    public static void MysticShotRequiresSightForStealthedNonHeroes()
    {
        foreach (var stealthed in new[] { false, true })
        {
            using var match = new Match("Ezreal", Team.Order);
            var hero = match.Hero(0); var enemy = match.Hero(1);
            enemy.Position = hero.Position + new Position2(800, 0);
            var unit = match.Runtime.World.Register(new Entity(
                match.Runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
                UnitType.Minion, enemy.Team, hero.Position + new Position2(300, 0))
                { Health = 1000, MaxHealth = 1000, Radius = 30 });
            if (stealthed)
                match.Runtime.SpellBuffs.Add(unit, "RegressionStealth", BuffType.Invisibility,
                    10, unit.NetId, new BuffPayload { Stealthed = true });
            match.Cast(0, 0, enemy);
            SpellStep(match, 1);
            TestAssert.Equal(!stealthed, unit.Health < 1000, "Q skips an unseen stealthed non-hero");
            TestAssert.Equal(stealthed, enemy.Health < 5000, "skipped unit does not consume Q");
        }
    }

    [Check("spells-audit-b4-ezreal-arcane-stealth-and-vision")]
    public static void ArcaneShiftSelectsStealthedHeroAndAnchorsOrdinaryVision()
    {
        using var match = new Match("Ezreal", Team.Order, enemyChampion: "Shaco");
        var runtime = match.Runtime; var hero = match.Hero(0); var target = match.Hero(1);
        // Batch 159 step 2: every map turret carries Spells/TurretBonusHealth.luaobj's own BBAddUnitPerceptionBubble
        // (BubbleSize 800 lane, 1000 nexus, 1600 ChaosTurretShrine) where DEV-06 stood in with the shrine's alone, so
        // "the bubble this cast made" is every bubble that is not one of the map's own.
        var mapBubbles = runtime.World.TimedVisionBubbles.Select(bubble => bubble.BubbleId).ToHashSet();
        target.Position = hero.Position + new Position2(400, 0);
        runtime.SpellBuffs.Add(target, "RegressionStealth", BuffType.Invisibility, 10,
            target.NetId, new BuffPayload { Stealthed = true });
        var minion = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, target.Team, hero.Position + new Position2(600, 0))
            { Health = 1000, MaxHealth = 1000, Radius = 30 });
        TestAssert.False(runtime.Combat.IsTargetable(target, hero), "hero has no true sight");
        // The script runs at the cast frame, inside SpellSystem.Tick's shared projectile phase, and the same step then ages the timed vision
        // bubbles: a bubble made by the cast frame has lost the part of the step that follows it. A one-millisecond final step keeps that loss
        // at the millisecond (the hand-written module made the bubble in the late champion tick, after the aging).
        match.CastGround(0, hero.Position, 2); SpellStep(match, .249); SpellStep(match, .001);
        TestAssert.Equal(target.NetId, EzrealFlights.InFlight(runtime.Spells, hero).Single().TargetNetId,
            "nearer stealthed hero wins over visible minion");
        // DEV-06, 27 Sept 2026 base-game sweep: Arcane Shift excludes independent shrine sight.
        var bubble = runtime.World.TimedVisionBubbles.Single(value => !mapBubbles.Contains(value.BubbleId));
        TestAssert.Equal(target.NetId, bubble.AnchorNetId);
        TestAssert.Equal(hero.Team, bubble.Team);
        TestAssert.Near(100, bubble.Radius, 0);
        TestAssert.Near(1, bubble.Remaining, .0011, "BBAddUnitPerceptionBubble Duration 1");
        TestAssert.False(bubble.RevealStealth, "Arcane Shift grants ordinary vision only");
        TestAssert.False(runtime.Combat.IsTargetable(target, hero), "bubble does not reveal stealth");
        var hp = target.Health;
        SpellStep(match, .5);
        TestAssert.True(target.Health < hp, "bolt damages the stealthed hero");
        TestAssert.Near(1000, minion.Health, 0, "farther minion receives no bolt");
        SpellStep(match, .51);
        TestAssert.Equal(0, runtime.World.TimedVisionBubbles.Count(value => !mapBubbles.Contains(value.BubbleId)),
            "bubble expires after one second");
    }

    [Check("vision-b6-arcane-shift-live-anchor")]
    public static void ArcaneShiftSightFollowsItsLiveAnchor()
    {
        using var match = new Match("Ezreal", Team.Order);
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        var target = match.Hero(1);
        var mapBubbles = runtime.World.TimedVisionBubbles.Select(bubble => bubble.BubbleId).ToHashSet();
        target.Position = hero.Position + new Position2(400, 0);
        match.CastGround(0, hero.Position, 2);
        // The script's SelfExecute runs when the windup ends, ahead of the bubble aging of the same spell tick: in 0.01 s steps the bubble is made in
        // the last step and loses at most that step (one 0.25 s step would age it by the whole 0.25 s, the old module made it after the aging).
        for (var step = 0; step < 25; step++) SpellStep(match, .01);
        // DEV-06, 27 Sept 2026 base-game sweep: preserve the shrine provider during this mutation.
        var bubble = runtime.World.TimedVisionBubbles.Single(value => !mapBubbles.Contains(value.BubbleId));
        var bubbleIndex = runtime.World.TimedVisionBubbles.IndexOf(bubble);
        TestAssert.Equal(target.NetId, bubble.AnchorNetId);
        TestAssert.Near(100.0, bubble.Radius, 0.0, "EzrealArcaneShift BBAddUnitPerceptionBubble radius");
        TestAssert.Near(1.0, bubble.Remaining, 0.011, "shipped duration");
        target.Position += new Position2(300, 0);
        var providers = runtime.Dependencies.AdditionalProviders(hero.Team).ToArray();
        TestAssert.True(providers.Any(provider => provider.Position == target.Position && provider.Radius == 100.0));
        TestAssert.False(providers.Any(provider => provider.Position == bubble.Position),
            "no lingering provider at the old target position");
        TestAssert.False(runtime.Dependencies.RevealsStealth(hero.Team, target.Position),
            "Arcane Shift is ordinary sight, not detection");
        // Exercise the shared anchored detection query with the same timed provider.
        runtime.World.TimedVisionBubbles[bubbleIndex] = bubble with { RevealStealth = true };
        TestAssert.True(runtime.Dependencies.RevealsStealth(hero.Team, target.Position));
        TestAssert.False(runtime.Dependencies.RevealsStealth(hero.Team, bubble.Position));
        runtime.World.TimedVisionBubbles[bubbleIndex] = bubble with
        {
            RevealStealth = true, AnchorNetId = runtime.World.Allocate(UnitType.Monster),
        };
        TestAssert.False(runtime.Dependencies.AdditionalProviders(hero.Team)
            .Any(provider => provider.Radius == 100.0), "missing anchor must not restore origin sight");
        TestAssert.False(runtime.Dependencies.RevealsStealth(hero.Team, bubble.Position),
            "missing anchor must not restore origin detection");
        runtime.World.TimedVisionBubbles[bubbleIndex] = bubble;
        SpellStep(match, 1.01);
        TestAssert.Equal(0, runtime.World.TimedVisionBubbles.Count(value => !mapBubbles.Contains(value.BubbleId)),
            "anchor does not extend the one-second lifetime");
    }

    [Check("spells-audit-b4-arcane-five-candidate-cap")]
    public static void ArcaneShiftExaminesOnlyFiveNearestCandidates()
    {
        foreach (var hiddenCount in new[] { 4, 5 })
        {
            using var match = new Match("Ezreal", Team.Order);
            var runtime = match.Runtime; var hero = match.Hero(0); var target = match.Hero(1);
            var mapBubbles = runtime.World.TimedVisionBubbles.Select(bubble => bubble.BubbleId).ToHashSet();
            target.Position = hero.Position + new Position2(600, 0);
            for (var i = 0; i < hiddenCount; i++)
            {
                var minion = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
                    EntityKind.Minion, UnitType.Minion, target.Team, hero.Position + new Position2(100 + 100 * i, 0))
                    { Health = 1000, MaxHealth = 1000, Radius = 30 });
                runtime.SpellBuffs.Add(minion, "RegressionStealth", BuffType.Invisibility, 10,
                    minion.NetId, new BuffPayload { Stealthed = true });
                TestAssert.False(runtime.Combat.IsTargetable(minion, hero), "non-hero candidate is unseen");
            }
            match.CastGround(0, hero.Position, 2); SpellStep(match, .25);
            TestAssert.Equal(hiddenCount == 4 ? 1 : 0, EzrealFlights.InFlight(runtime.Spells, hero).Length,
                "unseen non-heroes are rejected after selecting the nearest five");
            // DEV-06, 27 Sept 2026 base-game sweep: exclude only the initial shrine bubble.
            TestAssert.Equal(hiddenCount == 4 ? 1 : 0,
                runtime.World.TimedVisionBubbles.Count(bubble => !mapBubbles.Contains(bubble.BubbleId)),
                "only a selected target receives a bubble");
            if (hiddenCount == 4)
                TestAssert.Equal(target.NetId, EzrealFlights.InFlight(runtime.Spells, hero).Single().TargetNetId);
        }
    }

    [Check("spells-audit-b4-annie-refund-update-boundary")]
    public static void DisintegrateRefundEndsAtFirstUpdate()
    {
        foreach (var lethal in new[] { true, false })
        {
            using var match = new Match("Annie", Team.Order, ally: true);
            var runtime = match.Runtime; var hero = match.Hero(0); var target = match.Hero(1);
            if (lethal) target.Health = 1;
            var mana = hero.Mana;
            match.Cast(0, 0, target);
            // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press; the impact comes after it.
            TestAssert.Near(mana, hero.Mana, .001, "the press spends nothing");
            SpellStep(match, .5);
            var spent = hero.Mana;
            // 19 October 2026: the 1 s Disintegrate mark is the script's buff, which its own BuffOnUpdateActions removes at the first update.
            if (lethal)
            {
                TestAssert.False(target.Alive, "Q itself is lethal");
                TestAssert.Near(mana, hero.Mana, .001, "lethal Q refunds its cost");
            }
            else
            {
                TestAssert.True(target.Alive, "Q leaves a surviving target");
                TestAssert.True(spent < mana, "Q spent its mana at its cast frame, before the impact");
                runtime.Buffs.TickBuffs(target, .01);
                SpellStep(match, .5);
                TestAssert.True(runtime.Combat.Kill(target, match.Hero(2).NetId), "unrelated kill at +0.5s");
                SpellStep(match, .01);
                TestAssert.Near(spent, hero.Mana, .001, "later kill cannot refund Q");
            }
        }
    }

    [Check("spells-audit-annie-impact-passive-and-stacks")]
    public static void AnnieImpactPassiveAndStacks()
    {
        using var match = new Match("Annie", Team.Order, enemyChampion: "Shaco");
        var hero = match.Hero(0); var target = match.Hero(1);
        for (var i = 0; i < 5; i++)
        {
            hero.SpellCooldowns[2] = 0; hero.Mana = hero.MaxMana;
            match.Cast(0, 2, hero); SpellStep(match, .5);
        }
        match.Runtime.Delivery.Flush();
        // The first stack is a 0xBF add; each later stack of the held row is a 0x1F count update (G14).
        // 19 October 2026 (Annie from her scripts): the hand-written module sent the four pre-ready counts and then showed only the ready particle;
        // Pyromania.luaobj counts the fifth stack on the same row and arms Pyromania_particle next to it, so the fifth count is on the wire too
        // (S-spell-shields shows the same 1..5 run).
        var counts = WireBuffCounts(match.Captures[0], hero.NetId, "Pyromania");
        TestAssert.True(counts.SequenceEqual(new[] { 1, 2, 3, 4, 5 }), "wire counts track each stack up to the ready one");
        // The script engine's passive: the fifth stack is the ready Pyromania_particle buff, and a spent charge starts the next cycle at one stack.
        bool Ready() => hero.Buffs.Any(buff => buff.Name == "Pyromania_particle");
        int Stacks() => hero.Buffs.Where(buff => buff.Name == "Pyromania").Sum(buff => buff.Stacks);
        TestAssert.True(Ready(), "five E casts arm the passive");
        hero.Mana = hero.MaxMana;
        match.Cast(0, 0, target);
        TestAssert.True(Ready(), "acceptance does not spend charged Pyromania");
        match.Cast(1, 3, target);
        match.Runtime.Dependencies.ChampionDispatcher.TickHero(match.Runtime.Spells, target, .3);
        // Fine steps: Hallucinate's cast frame (BBDestroyMissileForTarget, then the untargetable applicator) must run between Q's launch and
        // its arrival; one 1-second spell step runs the three in an order the script engine does not define.
        Advance(match, 1);
        TestAssert.True(Ready(), "dodged Q retains the charge");
        foreach (var buff in target.Buffs.Where(buff => buff.Name == "HallucinateApplicator").ToArray())
            match.Runtime.SpellBuffs.Remove(target, buff.Slot);
        hero.SpellCooldowns[0] = 0; hero.Mana = hero.MaxMana;
        match.Cast(0, 0, target); SpellStep(match, 1);
        TestAssert.True(!Ready() && Stacks() == 1, "landed Q spends the charge and starts the next cycle");
        TestAssert.True(target.Buffs.Any(buff => buff.Name == "Stun"), "landed charged Q stuns");
    }

    [Check("spells-audit-annie-skin-impact-effects")]
    public static void AnnieSkinImpactEffects()
    {
        foreach (var skin in new[] { 0, 5 })
        {
            using var match = new Match("Annie", Team.Order, skinId: skin);
            var hero = match.Hero(0); var target = match.Hero(1);
            var suffix = skin == 5 ? "_frost" : "";
            match.Cast(0, 0, target); Advance(match, 1); match.Runtime.Delivery.Flush();
            TestAssert.True(HasEffect(match.Captures[0], $"DisintegrateHit_tar{suffix}"), "Q target effect");
            TestAssert.True(HasEffect(match.Captures[0], $"Disintegrate_hit{suffix}"), "Q hit effect");
            hero.Mana = hero.MaxMana;
            // Native cone requests use a nearby cast point and a direction;
            // the damaging cone still reaches 625 units.
            match.CastGround(0, hero.Position + new Position2(50, 0), 1); Advance(match, .5);
            match.Runtime.Delivery.Flush();
            TestAssert.True(HasEffect(match.Captures[0], $"Incinerate_buf{suffix}"), "W skin effect");
            hero.Mana = hero.MaxMana;
            match.CastGround(0, target.Position, 3); Advance(match, .5); match.Runtime.Delivery.Flush();
            TestAssert.True(HasEffect(match.Captures[0], $"infernalguardian_tar{suffix}"), "R skin effect");
        }
    }

    [Check("spells-audit-orianna-contact-order")]
    public static void OriannaContactOrder()
    {
        foreach (var reverse in new[] { false, true })
        {
            using var match = new Match("Orianna", Team.Order, extraEnemy: true);
            var hero = match.Hero(0); var near = match.Hero(reverse ? 2 : 1); var far = match.Hero(reverse ? 1 : 2);
            near.Position = hero.Position + new Position2(250, 0); far.Position = hero.Position + new Position2(450, 0);
            var nearHp = near.Health; var farHp = far.Health;
            // Batch 713: an Instacast line flies from the step after its creation 0x41 (BbLineMissile.Tick), so the launch step is its own step here.
            match.CastGround(0, hero.Position + new Position2(700, 0)); SpellStep(match, 0); SpellStep(match, 1);
            TestAssert.Near(60, (nearHp - near.Health) * (1 + near.SpellBlock / 100), .001, "first physical contact is full damage");
            TestAssert.Near(54, (farHp - far.Health) * (1 + far.SpellBlock / 100), .001, "second contact receives falloff");
        }
    }

    [Check("spells-audit-orianna-q-landing-decay-counts-struck-units")]
    public static void OriannaLandingDecayCountsUnitsTheMissileAlreadyStruck()
    {
        using var match = new Match("Orianna", Team.Order, extraEnemy: true);
        var hero = match.Hero(0); hero.AbilityPower = 0;
        // The burst walk is in NetId order here (Reconstruction), so the struck unit takes the lower id.
        var struck = match.Hero(1).NetId < match.Hero(2).NetId ? match.Hero(1) : match.Hero(2);
        var burstOnly = ReferenceEquals(struck, match.Hero(1)) ? match.Hero(2) : match.Hero(1);
        struck.SpellBlock = 0; burstOnly.SpellBlock = 0;
        var landing = hero.Position + new Position2(600, 0);
        // Struck by the line and inside the 175-unit burst, so SpellOnMissileEnd takes its IzunaPercent step for it.
        struck.Position = landing + new Position2(-20, 0);
        // Outside the 145-unit line corridor, inside the burst.
        burstOnly.Position = landing + new Position2(0, 160);
        var struckHp = struck.Health; var burstHp = burstOnly.Health;
        // Batch 713: an Instacast line flies from the step after its creation 0x41 (BbLineMissile.Tick), so the launch step is its own step here.
        match.CastGround(0, landing); SpellStep(match, 0); SpellStep(match, 1);
        TestAssert.Near(60, struckHp - struck.Health, .001, "the line contact is full damage");
        TestAssert.Near(60 * .9 * .9, burstHp - burstOnly.Health, .001,
            "the burst walk spends one step on the struck unit before the unit it hits");
    }

    [Check("spells-audit-leesin-neutral-q2-cap")]
    public static void LeeSinNeutralQ2Cap()
    {
        using var match = new Match("LeeSin", Team.Order);
        var runtime = match.Runtime; var hero = match.Hero(0);
        match.Hero(1).Position = new(11000, 11000);
        var target = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Monster), EntityKind.Monster,
            UnitType.Monster, Team.Neutral, hero.Position + new Position2(200, 0))
            { Name = "RegressionNeutral", MaxHealth = 20000, Health = 10000, Radius = 30, Armor = 0 });
        match.CastGround(0, target.Position); SpellStep(match, .5); SpellStep(match, .2);
        // 19 October 2026: the recast target (LeeSinState.QTarget) is the BlindMonkQOne mark buff of the script now.
        TestAssert.True(target.Buffs.Any(buff => buff.Name == "BlindMonkQOne"), "Q1 marks the neutral");
        hero.Mana = 200; match.CastGround(0, target.Position); SpellStep(match, .01);
        var hp = target.Health; runtime.Movement.Tick(1); SpellStep(match, .01);
        TestAssert.Near(450, hp - target.Health, .001, "rank-one base plus capped 400 missing-health component");
    }

    [Check("spells-audit-ezreal-unseen-skillshots-and-blink-range")]
    public static void EzrealUnseenSkillshotsAndBlinkRange()
    {
        foreach (var slot in new[] { 0, 1, 3 })
        {
            double Damage(bool hidden)
            {
                using var match = new Match("Ezreal", Team.Order, enemyChampion: "Shaco");
                var target = match.Hero(1);
                if (hidden)
                    match.Runtime.SpellBuffs.Add(target, "RegressionStealth", BuffType.Invisibility, 10,
                        target.NetId, new BuffPayload { Stealthed = true });
                var hp = target.Health;
                match.CastGround(0, target.Position, slot); SpellStep(match, 1.6); SpellStep(match, .5);
                return hp - target.Health;
            }
            var visible = Damage(false);
            TestAssert.True(visible > 0, "visible control takes damage");
            TestAssert.Near(visible, Damage(true), .001, $"slot {slot} also collides with an unseen hero");
        }
        using var blink = new Match("Ezreal", Team.Order);
        var hero = blink.Hero(0); blink.Hero(1).Position = new(11000, 11000);
        var destination = hero.Position + new Position2(0, 475);
        TestAssert.True(blink.Runtime.Navigation.IsWalkable(destination), "clear max-range destination");
        blink.CastGround(0, destination, 2); SpellStep(blink, .5);
        TestAssert.Near(destination.X, hero.Position.X, 2, "Arcane Shift X endpoint");
        TestAssert.Near(destination.Z, hero.Position.Z, 2, "Arcane Shift retains its own 475 range");
    }

    [Check("spells-audit-hallucinate-cancels-champion-missiles")]
    public static void HallucinateCancelsChampionMissiles()
    {
        foreach (var champion in new[] { "Janna", "Ezreal", "LeeSin" })
        {
            using var match = new Match(champion, Team.Order, enemyChampion: "Shaco");
            var runtime = match.Runtime; var hero = match.Hero(0); var target = match.Hero(1);
            target.Position = hero.Position + new Position2(400, 0);
            if (champion == "Janna") { match.Cast(0, 1, target); SpellStep(match, .25); }
            if (champion == "Ezreal") { match.CastGround(0, hero.Position, 2); SpellStep(match, .25); }
            if (champion == "LeeSin")
            {
                match.Cast(0, 2, hero); SpellStep(match, .3); SpellStep(match, .11); hero.Mana = 200;
                target.Position = hero.Position + new Position2(700, 0);
                match.Cast(0, 2, hero); SpellStep(match, .001);
            }
            var hasMissile = champion switch
            {
                "Janna" => runtime.Spells.SharedProjectiles.Any(projectile =>
                    projectile.Snapshot.CasterNetId == hero.NetId && projectile.Snapshot.Launched),
                "Ezreal" => EzrealFlights.InFlight(runtime.Spells, hero).Length > 0,
                _ => LeeFlights(match, hero, "BlindMonkETwoMissile").Length > 0,
            };
            TestAssert.True(hasMissile, champion + " has a homing missile before R");
            // The scripted R cancels the missiles aimed at Shaco when its 0.25 s cast ends (HallucinateFull.TargetExecute), and Zephyr and
            // Essence Flux (1600 speed) would have landed from 400 units just before that: Shaco stands farther along the same line (the
            // homing flight follows him), so the missile is still in the air when the cancel runs.
            if (champion is "Janna" or "Ezreal") target.Position = hero.Position + new Position2(900, 0);
            // The applicator's status change refreshes the stat block, which replaces the harness's 5000 maximum with Shaco's own: take the
            // refreshed value first so that the comparison measures damage, not the fixture being reset.
            runtime.HeroStats.Refresh(target);
            var hp = target.Health;
            match.Cast(1, 3, target);
            // The cast windup of the scripted R (0.25 s) runs in the spell system's tick, not in the champion module's; the 0.25 s applicator follows.
            SpellStep(match, .3);
            TestAssert.True(target.Buffs.Any(buff => buff.Payload.Untargetable), "real Hallucinate applicator");
            SpellStep(match, 1);
            TestAssert.Near(hp, target.Health, .001, champion + " missile cannot resolve after cancellation");
            TestAssert.True(!target.Buffs.Any(buff => buff.Name == "BlindMonkETwoMissile"), "cancelled E2 cannot cripple");
        }
    }

    [Check("spells-audit-janna-rank-and-persistent-zephyr")]
    public static void JannaRankAndPersistentZephyr()
    {
        using var match = new Match("Janna", Team.Order);
        // Zephyr's passive is the buff SowTheWind that the character script adds while W is learned and ready (UpdateSelfBuffActions): +8/10/12/14/16 %
        // movement speed and Ghosted (BuffOnUpdateStats), removed when the cast's missile leaves (BuffOnLaunchMissile).
        var hero = match.Hero(0); SpellStep(match, 0); match.Runtime.Delivery.Flush();
        var buff = hero.Buffs.Single(buff => buff.Name == "SowTheWind");
        TestAssert.True(HasEffect(match.Captures[0], "SowTheWind_buf"), "head effect created");
        var effectId = BinaryPrimitives.ReadUInt32LittleEndian(match.Captures[0].Packets.First(packet =>
            Opcode(packet) == FxPackets.CreateGroupOpcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == SpellPackets.BuffNameHash("SowTheWind_buf")).Plaintext.Span[25..]);
        TestAssert.Near(.08, buff.Payload.MoveSpeedPercent, .0001, "rank one passive speed");
        hero.Level = 6; hero.SkillPoints = 1;
        // SowTheWind writes the speed in BuffOnUpdateStats, which batch 150 part 2a samples on the 0.25 s stats grid
        // (BbBuffRuntime.StatsUpdateInterval), not every update: the rank-two value is in the payload only after the
        // next sample of the buff, so the wait has to cross one (0.3 s here).
        TestAssert.True(match.Runtime.Spells.RankUp(hero, 1), "rank two accepted"); Advance(match, .3);
        TestAssert.True(ReferenceEquals(buff, hero.Buffs.Single(buff => buff.Name == "SowTheWind")), "same passive buff retained");
        TestAssert.Near(.10, buff.Payload.MoveSpeedPercent, .0001, "passive updates with W rank");
        var slot = buff.Slot; var duration = buff.ExpiresAt;
        match.Runtime.Delivery.Flush(); match.Clear();
        match.Cast(0, 1, match.Hero(1));
        TestAssert.True(ReferenceEquals(buff, hero.Buffs.Single(b => b.Name == "SowTheWind")),
            "JAN-03: cast acceptance retains the existing Zephyr instance");
        TestAssert.Equal(slot, buff.Slot);
        TestAssert.Near(duration, buff.ExpiresAt, 0);
        TestAssert.True(buff.Payload.Ghosted);
        foreach (var capture in match.Captures)
        {
            TestAssert.False(HasEffect(capture, "SowTheWind_tar") || HasEffect(capture, "SowTheWind_buf"),
                "no replacement burst or head effect at acceptance");
            TestAssert.False(capture.Packets.Any(p => Opcode(p) == 0x7F || Opcode(p) == 0x3D));
        }
        var windup = GameData.Default.Spell("SowTheWind", 5).CastTime;
        SpellStep(match, windup / 2);
        TestAssert.True(hero.Buffs.Contains(buff), "Zephyr survives the windup");
        SpellStep(match, windup / 2); match.Runtime.Delivery.Flush();
        foreach (var capture in match.Captures)
        {
            TestAssert.Equal(1, capture.Packets.Count(p => Opcode(p) == 0x3D &&
                BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[5..]) == effectId));
            TestAssert.Equal(1, capture.Packets.Count(p => Opcode(p) == 0x7F &&
                BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[6..]) == SpellPackets.BuffNameHash("SowTheWind"))); // Batch 357 (restates batch 181): the binding; Zephyr has no script
        }
        TestAssert.True(hero.Buffs.All(buff => buff.Name != "SowTheWind"), "passive clears during cooldown");
    }

    [Check("spells-audit-blitz-coefficients-random-lightning-and-shield")]
    public static void BlitzCoefficientsRandomLightningAndShield()
    {
        using var match = new Match("Blitzcrank", Team.Order, extraEnemy: true,
            random: new AnnieChecks.FixedRandomSource(.99));
        var runtime = match.Runtime; var hero = match.Hero(0); var near = match.Hero(1); var far = match.Hero(2);
        runtime.SpellBuffs.Add(hero, "RegressionAP", BuffType.Internal, 60, hero.NetId, new BuffPayload { AbilityPower = 100 });
        near.HealthRegen = far.HealthRegen = 0; // the strike is read through full ticks now, so no regeneration may blur the figures
        var nearHp = near.Health; var farHp = far.Health;
        // The aura buff's first BuffOnUpdateActions strikes at once (BBExecutePeriodically 2.5 s, ExecuteImmediately), then every 2.5 s.
        runtime.Tick(.1);
        TestAssert.Near(nearHp, near.Health, .001, "random selection need not pick closest");
        TestAssert.Near(120, (farHp - far.Health) * (1 + far.SpellBlock / 100), .001, "passive coefficient is .2");
        nearHp = near.Health; match.Cast(0, 3, hero); runtime.Tick(.3);
        TestAssert.Near(350, (nearHp - near.Health) * (1 + near.SpellBlock / 100), .001, "active coefficient is 1.0");
        hero.Health = hero.MaxHealth * .2; hero.Mana = hero.MaxMana;
        runtime.Combat.ApplyHit(near.NetId, hero.NetId, 10, DamageType.True,
            DamageSourceKind.Proc);
        runtime.Delivery.Flush();
        TestAssert.True(HasEffect(match.Captures[0], "SteamGolemShield"), "barrier persists");
        TestAssert.True(HasEffect(match.Captures[0], "SteamGolemShield_hit"), "trigger hit flashes");
        match.Clear();
        runtime.Combat.ApplyHit(near.NetId, hero.NetId, 10, DamageType.True,
            DamageSourceKind.Proc);
        runtime.Delivery.Flush();
        TestAssert.True(HasEffect(match.Captures[0], "SteamGolemShield_hit"), "subsequent shield hit flashes");
    }

    [Check("spells-audit-shaco-terrain-and-ally-clone-effect")]
    public static void ShacoTerrainAndAllyCloneEffect()
    {
        using var match = new Match("Shaco", Team.Order, ally: true);
        var runtime = match.Runtime; var hero = match.Hero(0);
        Position2? origin = null; Position2 destination = default;
        for (var x = 6000; x < 8500 && origin is null; x += 50)
            for (var z = 6000; z < 8500; z += 50)
            {
                var a = new Position2(x, z); var b = a + new Position2(150, 0);
                if (runtime.Navigation.IsWalkable(a) && !runtime.Navigation.IsWalkable(b))
                { origin = a; destination = b; break; }
            }
        TestAssert.True(origin.HasValue, "map wall found"); hero.Position = origin!.Value;
        match.CastGround(0, destination); runtime.Buffs.TickBuffs(hero, .06);
        TestAssert.True(runtime.Navigation.IsWalkable(hero.Position), "Deceive corrects blocked endpoint");
        foreach (var buff in hero.Buffs.Where(buff => buff.Name == "Deceive").ToArray()) runtime.SpellBuffs.Remove(hero, buff.Slot);
        hero.Position = new(7000, 7000); match.Hero(1).Position = new(7300, 7000); match.Hero(2).Position = new(7200, 7200);
        hero.Mana = hero.MaxMana; match.Cast(0, 3, hero); Advance(match, .8);
        var clone = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.ShacoClone && unit.Alive);
        TestAssert.True(runtime.Navigation.IsWalkable(hero.Position) && runtime.Navigation.IsWalkable(clone.Position), "R placements are walkable");
        TestAssert.True(HasEffect(match.Captures[0], "Jester_Copy") && HasEffect(match.Captures[2], "Jester_Copy"), "owner and ally identify clone");
        TestAssert.True(!HasEffect(match.Captures[1], "Jester_Copy"), "enemy does not receive clone marker");
    }

    [Check("spells-audit-shaco-hallucinate-spawn-clearance")]
    public static void ShacoHallucinateSpawnClearsOwner()
    {
        foreach (var roll in new[] { 0.0, 0.5 })
        {
            using var match = new Match("Shaco", Team.Order,
                random: new AnnieChecks.FixedRandomSource(roll));
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            // As in spells-live-shaco-clone-owner-spacing: the default enemy 200 units away would hold the clone in a fight and hide the escape.
            match.Hero(1).Position = new Position2(12000, 12000);
            // Batch 279: the cast's stop seats Shaco on his lattice anchor at once, and the sample is taken around him there.
            var origin = runtime.Movement.ClientAnchor(hero.Position);
            // HallucinateApplicator: BBGetRandomPointInAreaUnit (50 to 250 units; batch 284: the radius first, uniform in the radius as
            // the client draws it, then the angle), the clone through BBCloneUnitPet's summon placement, Shaco through the
            // teleport correction. The fixed roll gives the clone and Shaco's own landing the same sample, so the two overlap at the moment
            // of the spawn (the old build searched a second free point for him).
            var sampledRadius = 50.0 + roll * (250.0 - 50.0);
            var sampledClone = origin + new Position2(Math.Cos(2.0 * Math.PI * roll) * sampledRadius,
                Math.Sin(2.0 * Math.PI * roll) * sampledRadius);
            var expectedClone = runtime.Navigation.ClosestSummonPlacement(sampledClone, runtime.Movement.MovementRadius(hero));
            match.Cast(0, 3, hero);
            Advance(match, .52);
            var clone = runtime.World.Entities.Values.Single(unit =>
                unit.Kind == EntityKind.ShacoClone && unit.Alive);
            TestAssert.True(runtime.Navigation.IsWalkable(hero.Position) &&
                runtime.Navigation.IsWalkable(clone.Position), "both placements remain walkable");

            var packets = match.Captures[0].Packets;
            var spawn = packets.Single(packet => Opcode(packet) == 0x3C &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == clone.NetId);
            TestAssert.Near(expectedClone.X, BinaryPrimitives.ReadSingleLittleEndian(spawn.Plaintext.Span[10..]),
                .01, "clone spawn packet X uses corrected landing");
            TestAssert.Near(expectedClone.Z, BinaryPrimitives.ReadSingleLittleEndian(spawn.Plaintext.Span[18..]),
                .01, "clone spawn packet Z uses corrected landing");
            // Batch 142 step 6 (commit 0947f979, 20 October 2026) sends a teleport shorter than ClientTeleportDeadband
            // through a staging point first — "every script teleport goes through it (BbScriptMoves.Teleport)" — so the
            // 0.0 roll's packet carries two waypoint entries (6947,7065 then 6999,7001) where a single-entry packet's
            // offsets 20/22 are the landing. The landing is the group's last waypoint, whichever prelude the wire used.
            var teleport = packets.Last(packet => Opcode(packet) == VisionPackets.TeleportOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[15..]) == hero.NetId);
            var points = Decode(teleport).GetProperty("waypoints");
            var landed = points[points.GetArrayLength() - 1];
            var staged = Distance(origin, hero.Position) <= SpellSystem.ClientTeleportDeadband;
            TestAssert.Equal(staged ? 2 : 1, points.GetArrayLength(),
                "the owner teleport is staged only when the landing is inside the client's dead band");
            if (staged)
            {
                var prelude = new Position2(points[0][0].GetDouble(), points[0][1].GetDouble());
                TestAssert.True(Distance(origin, prelude) > SpellSystem.ClientTeleportDeadband &&
                    Distance(prelude, hero.Position) > SpellSystem.ClientTeleportDeadband,
                    $"staged prelude clears both ends of the short move: origin={origin}, prelude={prelude}, landing={hero.Position}");
            }
            TestAssert.Near(hero.Position.X, landed[0].GetDouble(), 1e-6,
                "owner teleport packet X matches the separated landing");
            TestAssert.Near(hero.Position.Z, landed[1].GetDouble(), 1e-6,
                "owner teleport packet Z matches the separated landing");

            // An overlap at the spawn is resolved by the clone's own follow order and the body solver (spells-live-shaco-clone-owner-spacing).
            Advance(match, 1.5);
            TestAssert.True(Distance(hero.Position, clone.Position) >= hero.Radius + clone.Radius - 2.0,
                $"Hallucinate roll {roll}: the clone clears its owner after the spawn: owner={hero.Position}, clone={clone.Position}");
        }
    }

    /// <summary>
    /// Batch 346 (restates the check for batch 329, commit df80527f): <c>ChangeCharacterData</c> (0x9F) derives from
    /// <c>DefaultPacketMixin&lt;159, PolicyBroadcast&gt;</c> and <c>PopCharacterData</c> / <c>PopAllCharacterData</c> (0x6A) from
    /// <c>DefaultPacketMixin&lt;106, PolicyBroadcast&gt;</c> in the client's debug data, and their handlers
    /// (CharacterDataStack Push/PopAll) test no visibility. So a client that holds the hero gets the transformation and its pop in fog too
    /// (was: vision-gated, repaired at the reveal). The reveal repeats nothing; a fresh connection still restores the model once.
    /// </summary>
    [Check("spells-audit-model-fog-and-reconnect")]
    public static void ChampionModelsSurviveFogAndReconnect()
    {
        foreach (var champion in new[] { "Nidalee", "Orianna" })
        {
            using var match = new Match(champion, Team.Order);
            var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
            enemy.Position = new(11000, 11000); runtime.Delivery.Flush(); match.Clear();
            match.CastGround(0, hero.Position + new Position2(500, 0), champion == "Nidalee" ? 3 : 0); Advance(match, 1);
            TestAssert.False(runtime.Session.Participants[1].View.Visible.Contains(hero.NetId), $"{champion}: the enemy cannot see the hero");
            TestAssert.Equal(1, match.Captures[1].Packets.Count(packet => Opcode(packet) == 0x9F),
                $"{champion}: the hidden transformation reaches the enemy that holds the hero (PolicyBroadcast)");
            enemy.Position = hero.Position + new Position2(200, 0); runtime.Delivery.Flush();
            TestAssert.Equal(1, match.Captures[1].Packets.Count(packet => Opcode(packet) == 0x9F), $"{champion}: the reveal pushes nothing more");
            match.Clear(); runtime.Delivery.Flush();
            TestAssert.True(!match.Captures[1].Packets.Any(packet => Opcode(packet) == 0x9F), "stable view does not push twice");
            var view = runtime.Session.Participants[1].View; view.Reset(); runtime.Delivery.Flush();
            TestAssert.Equal(1, match.Captures[1].Packets.Count(packet => Opcode(packet) == 0x9F), "fresh connection restores model");
            enemy.Position = new(11000, 11000); runtime.Delivery.Flush(); match.Clear();
            hero.SpellCooldowns[champion == "Nidalee" ? 3 : 2] = 0; hero.Mana = hero.MaxMana;
            match.Cast(0, champion == "Nidalee" ? 3 : 2, hero); Advance(match, 1);
            TestAssert.Equal(1, match.Captures[1].Packets.Count(packet => Opcode(packet) == 0x6A),
                $"{champion}: the hidden pop reaches the enemy too (PolicyBroadcast)");
            enemy.Position = hero.Position + new Position2(200, 0); runtime.Delivery.Flush();
            TestAssert.Equal(1, match.Captures[1].Packets.Count(packet => Opcode(packet) == 0x6A), $"{champion}: the reveal repeats no pop");
        }
    }

    /// <summary>
    /// Batch 256: <c>BBPopAllCharacterData</c>'s wire is its body's one 5-byte 0x6A, not a 9-byte pop per layer; the ledger drops every
    /// layer of the unit with it, so the repair pass pushes nothing back, and an observer's view holds none of them.
    /// </summary>
    [Check("spells-audit-model-pop-all-is-one-five-byte-pop")]
    public static void PopAllIsOneFiveBytePop()
    {
        using var match = new Match("Nidalee", Team.Order);
        var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
        enemy.Position = hero.Position + new Position2(200, 0); runtime.Delivery.Flush();
        runtime.Spells.Effects.SendPushCharacterData(hero, 9001, "OriannaNoBall");
        runtime.Spells.Effects.SendPushCharacterData(hero, 9002, "Udyr");
        runtime.Delivery.Flush();
        TestAssert.Equal(2, match.Captures[1].Packets.Count(packet => Opcode(packet) == 0x9F), "the observer is given both layers");
        match.Clear();
        runtime.Spells.Effects.SendPopAllCharacterData(hero);
        runtime.Delivery.Flush(); runtime.Delivery.Flush();
        var pops = match.Captures[1].Packets.Where(packet => Opcode(packet) == 0x6A).ToArray();
        TestAssert.Equal(1, pops.Length, "one 0x6A for the two layers");
        TestAssert.Equal(5, pops[0].Plaintext.Length, "the 5-byte pop-all");
        TestAssert.Equal(hero.NetId, BinaryPrimitives.ReadUInt32LittleEndian(pops[0].Plaintext.Span[1..]), "of the unit");
        TestAssert.False(match.Captures[1].Packets.Any(packet => Opcode(packet) == 0x9F), "nothing is pushed back");
        TestAssert.Equal(0, runtime.Delivery.CharacterDataOf(hero.NetId).Count, "the ledger holds no layer of the unit");
        TestAssert.False(runtime.Session.Participants[1].View.CharacterData.Any(key => key.Actor == hero.NetId), "nor does the view");
    }

    /// <summary>
    /// Batch 256: every 0x6A runs both of the client's pop handlers (one clears the stack, the other erases by id), so the fog repair's
    /// 9-byte pop of a stale layer also takes the live layers off the client. The delivered packets, replayed through that rule, must leave the
    /// client holding the layer the server still holds.
    /// </summary>
    [Check("spells-audit-model-stale-layer-repair-keeps-the-live-layer")]
    public static void StaleLayerRepairKeepsTheLiveLayer()
    {
        using var match = new Match("Nidalee", Team.Order);
        var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
        enemy.Position = hero.Position + new Position2(200, 0); runtime.Delivery.Flush(); match.Clear();
        runtime.Spells.Effects.SendPushCharacterData(hero, 9101, "OriannaNoBall");
        runtime.Spells.Effects.SendPushCharacterData(hero, 9102, "Udyr");
        runtime.Delivery.Flush();
        // The observer loses sight; the top layer is popped where it cannot see; it sees the unit again.
        enemy.Position = new(11000, 11000); runtime.Delivery.Flush();
        runtime.Spells.Effects.SendPopCharacterData(hero, 9102);
        runtime.Delivery.Flush();
        enemy.Position = hero.Position + new Position2(200, 0); runtime.Delivery.Flush(); runtime.Delivery.Flush();
        var stack = new List<(uint Id, string Name)>();
        foreach (var bytes in match.Captures[1].Packets.Select(packet => packet.Plaintext.ToArray()))
        {
            if (bytes.Length < 5 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1)) != hero.NetId) continue;
            if (bytes[0] == 0x9F) stack.Add((BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5)), Encoding.ASCII.GetString(bytes, 10, 64).TrimEnd('\0')));
            else if (bytes[0] == 0x6A) stack.Clear();
        }
        TestAssert.Equal("9101:OriannaNoBall", string.Join(" ", stack.Select(entry => $"{entry.Id}:{entry.Name}")),
            "the client ends with the live layer the server holds");
    }

    [Check("protocol.animation-states-native-layout")]
    public static void AnimationStatesNativeLayout()
    {
        TestAssert.True(SpellPackets.SetAutoAttackSpellSlot(0x12345678, -1).Plaintext.Span.SequenceEqual(
            Convert.FromHexString("2278563412FF")), "native signed autocast slot");
        var packet = SpellPackets.SetAnimationStates(0x12345678, [("Run", "Run_Ult")]);
        // Independently specified: 6-byte header, uint32 lengths, no terminators.
        // Playtest B3 #1 (26 Sept 2026) deliberately reverses the old "Run" key
        // (52756E): the client lowercases every lookup name, so the key is "run".
        TestAssert.True(packet.Plaintext.Span.SequenceEqual(Convert.FromHexString(
            "6E78563412010300000072756E0700000052756E5F556C74")), "native override-map layout");
        TestAssert.True(Encoding.ASCII.GetString(packet.Plaintext.Span[17..]) == "Run_Ult", "replacement string bytes");
        var padded = SpellPackets.SetAnimationStates(0x12345678, [("Run", "Run")]);
        var cipher = LegacyBlowfish.CreateDefault();
        var decoded = Amberline.Network.Diagnostics.WirePacketDecoder.DecodeWire(padded.ToWireBytes(cipher), padded.Channel, cipher);
        TestAssert.True(decoded.Fields.ContainsKey("states"), "animation decoder accepts transport padding");
    }
}
