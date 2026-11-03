using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-blitzcrank-b6-q-child-cast")]
    public static void RocketGrabStartsItsChildCastTracks()
    {
        AssertB6CastTrack("RocketGrabMissile", 0, 0, 2, "FistGrab_cas.troy", "R_Hand");
        AssertB6CastTrack("RocketGrabMissile", 1, 8, 1, "fist_pos.troy", "");
        // Rocket Grab is an instacast container whose script (RocketGrab.SelfExecute, BBSpellCast slot 0 ExtraSlots) casts the normal child
        // RocketGrabMissile, a CastType 3 line missile: parent acknowledgement, then the child's own cast, then the line after its 0.25 s.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Blitzcrank", team);
            var hero = match.Hero(0); var enemy = match.Hero(1);
            hero.SpellRanks[3] = 0; // Keep Static Field's unrelated passive out of the packet capture.
            enemy.Position = hero.Position + new Position2(400, 500);
            match.Runtime.Delivery.Flush(); match.Clear();
            var mana = hero.Mana;
            match.CastGround(0, hero.Position + new Position2(900, 0));
            TestAssert.Near(GameData.Default.Spell("RocketGrab", 5).ManaCosts[0], mana - hero.Mana, 1e-5, "only the accepted parent spends mana");
            foreach (var capture in match.Captures)
            {
                TestAssert.Equal(2, capture.Packets.Count(packet => Opcode(packet) == 0xBD), "one parent acknowledgement and one normal child");
                var child = capture.Packets.Single(packet => IsB6Child(packet, 0x00DD4395));
                var missileId = BinaryPrimitives.ReadUInt32LittleEndian(child.Plaintext.Span[28..]);
                TestAssert.True(missileId != 0, "the child uses the allocated gameplay missile");
                AssertB6ChildCast(child, hero, 0x00DD4395, 42, .25, missileId);
                TestAssert.False(capture.Packets.Any(packet => Opcode(packet) is 0x41 or 0xB8), "the cast alone owns the initial missile and animation");
            }
            match.Runtime.Tick(.1);
            TestAssert.True(match.Runtime.Spells.SharedProjectiles.Select(p => p.Snapshot).Any(p => p.CasterNetId == hero.NetId && p.MissileNetId != 0 && !p.Launched),
                "the line waits for the child's windup");
            match.Runtime.Tick(2.0);
            match.Runtime.Delivery.Flush();
            foreach (var capture in match.Captures)
            {
                var missileId = BinaryPrimitives.ReadUInt32LittleEndian(capture.Packets.Single(packet => IsB6Child(packet, 0x00DD4395)).Plaintext.Span[28..]);
                TestAssert.Equal(1, capture.Packets.Count(packet => IsB6Child(packet, 0x00DD4395)), "the child is not repeated");
                TestAssert.Equal(1, capture.Packets.Count(packet => IsB6Retirement(packet, missileId)), "one matching retirement at the end of the range");
            }
            TestAssert.False(match.Runtime.Spells.SharedProjectiles.Any(p => p.Snapshot.CasterNetId == hero.NetId && p.Snapshot.MissileNetId != 0),
                "the line is over");
        }
    }

    [Check("spells-blitz-passive-lightning-lethal-impact")]
    public static void BlitzPassiveLightningKeepsLethalImpact()
    {
        foreach (var minion in new[] { false, true })
        foreach (var lethal in new[] { false, true })
        {
            using var match = new Match("Blitzcrank", Team.Order);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var victim = match.Hero(1);
            if (minion)
            {
                victim.Position = hero.Position + new Position2(600, 0);
                victim = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
                    EntityKind.Minion, UnitType.Minion, victim.Team, hero.Position + new Position2(200, 0))
                    { Name = "BlitzLightningProbe", Created = true, Visible = true, Health = 1000, MaxHealth = 1000, Radius = 35, GoldReward = 25 });
            }
            victim.Health = lethal ? 1 : 1000;
            victim.SpellBlock = 0;
            victim.HealthRegen = 0;
            hero.AbilityPower = 0;
            runtime.Delivery.Flush();
            match.Clear();
            var gold = hero.Gold;
            // CharScriptBlitzcrank adds the StaticField aura buff on the first update; its BuffOnUpdateActions strikes at once
            // (BBExecutePeriodically 2.5 s, ExecuteImmediately) and then every 2.5 s.
            runtime.Tick(.1);
            runtime.Delivery.Flush();
            TestAssert.Equal(!lethal, victim.Alive, "lightning retains ordinary death resolution");
            TestAssert.Near(lethal ? 0 : 900, victim.Health, 1e-6, "rank-one passive deals 100 magic damage");
            TestAssert.Equal(lethal, hero.Gold > gold, "only a lethal strike awards the killer gold");
            foreach (var capture in match.Captures)
            {
                var particle = capture.Packets.Single(packet => IsNamedVisual(packet, "StaticField_tar"));
                TestAssert.Equal(victim.NetId, BinaryPrimitives.ReadUInt32LittleEndian(particle.Plaintext.Span[21..]));
                TestAssert.Equal(victim.NetId, BinaryPrimitives.ReadUInt32LittleEndian(particle.Plaintext.Span[29..]));
            }
        }
    }

    [Check("spells-blitz-active-and-barrier-lethal-impacts")]
    public static void BlitzActiveAndBarrierKeepLethalImpacts()
    {
        using (var match = new Match("Blitzcrank", Team.Order))
        {
            var hero = match.Hero(0);
            var victim = match.Hero(1);
            // Batch 340: R's cooldown starts at its cast frame, so CharScriptBlitzcrank (which adds the passive StaticField while R is
            // ready) runs its first poll inside the windup, and the passive's immediate bolt would land there. Let the passive settle first.
            SettlePassive(match);
            victim.Health = 1;
            match.Cast(0, 3, hero);
            match.Runtime.Tick(.3);
            match.Runtime.Delivery.Flush();
            TestAssert.False(victim.Alive, "active R kills");
            TestAssert.False(victim.Buffs.Any(buff => buff.Type == BuffType.Silence), "no corpse silence");
            foreach (var capture in match.Captures)
            {
                TestAssert.Equal(1, capture.Packets.Count(packet => IsNamedVisual(packet, "StaticField_hit")));
                TestAssert.Equal(1, capture.Packets.Count(packet => IsNamedVisual(packet, "StaticField_nova")));
            }
        }
        using (var match = new Match("Blitzcrank", Team.Order))
        {
            var hero = match.Hero(0);
            hero.Mana = 100;
            TestAssert.True(match.Runtime.Combat.ApplyHit(match.Hero(1).NetId, hero.NetId,
                10000, DamageType.True, DamageSourceKind.Spell), "barrier absorbs part of lethal damage");
            match.Runtime.Delivery.Flush();
            TestAssert.False(hero.Alive, "remaining damage is lethal");
            foreach (var capture in match.Captures)
                TestAssert.Equal(1, capture.Packets.Count(packet => IsNamedVisual(packet, "SteamGolemShield_hit")));
        }
    }

    [Check("spells-blitz-q-r-injected-data")]
    public static void BlitzMissileAndActiveReadSpellData()
    {
        // The script path reads the line's range per rank, speed and width, and the active's radius, from the loaded records (RocketGrabMissile,
        // StaticField); the active's damage is the script's own table and coefficient (250 + 1.0 AP at rank one), not Coefficient2.
        GameData Injected()
        {
            var data = new GameData();
            // A private cache keeps injected records local to this match; shipped files and the process-wide GameData.Default stay immutable.
            var spells = (Dictionary<(string Name, int? RankCount), SpellData>)typeof(GameData)
                .GetField("_spells", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(data)!;
            var missile = data.Spell("RocketGrabMissile", 5);
            var raw = missile.Raw.Keys.ToDictionary(key => key, key => missile.Raw[key]);
            raw[InibinReader.KeyHash("SpellData*LineWidth")] = InibinValue.FromDouble(120);
            // The client reads the range of the cast level (SpellData.CastRangeAt), so the injected 800 must replace the level entries too.
            for (var level = 1; level <= SpellData.MaxSpellLevel; level++)
                raw[InibinReader.KeyHash($"SpellData*CastRange{level}")] = InibinValue.FromDouble(800);
            spells[("RocketGrabMissile", 5)] = missile with
                { CastRange = 800, MissileSpeed = 400, Raw = new InibinData(raw) };
            spells[("StaticField", 3)] = data.Spell("StaticField", 3) with { CastRadius = 750 };
            return data;
        }

        // Width and speed: a victim 170 off the axis is inside the injected 120 + radius, and the line reaches x = 427 at 0.25 + 427 / 400 s.
        using (var match = new Match("Blitzcrank", Team.Order, data: Injected()))
        {
            var hero = match.Hero(0);
            var victim = match.Hero(1);
            hero.SpellRanks[3] = 0;
            victim.Position = hero.Position + new Position2(500, 170);
            victim.Radius = 65;
            victim.SpellBlock = 0;
            victim.HealthRegen = 0;
            match.CastGround(0, hero.Position + new Position2(1000, 0));
            match.Runtime.Tick(1.0);
            TestAssert.Near(victim.MaxHealth, victim.Health, 1e-9, "the injected speed 400 has not brought the line to the victim after one second");
            var health = victim.Health;
            match.Runtime.Tick(.6);
            TestAssert.True(victim.Health < health, "injected width hits the off-axis victim");
        }

        // Range: a victim 1050 on the axis is beyond the injected 800 (and inside the shipped 1050).
        using (var match = new Match("Blitzcrank", Team.Order, data: Injected()))
        {
            var hero = match.Hero(0);
            var far = match.Hero(1);
            hero.SpellRanks[3] = 0;
            far.Position = hero.Position + new Position2(1050, 0);
            far.SpellBlock = 0;
            far.HealthRegen = 0;
            var health = far.Health;
            match.CastGround(0, far.Position);
            match.Runtime.Tick(3.0);
            TestAssert.Near(health, far.Health, 1e-9, "the injected range 800 stops the line short of the victim");
        }

        // Static Field: the injected radius 750 reaches a victim 700 away; the damage is 250 + 1.0 AP (the script's DamageByLevel and ratio).
        using (var match = new Match("Blitzcrank", Team.Order, data: Injected()))
        {
            var hero = match.Hero(0);
            var victim = match.Hero(1);
            hero.AbilityPower = 100;
            hero.Mana = 1000;
            victim.Position = hero.Position + new Position2(700, 0);
            victim.SpellBlock = 0;
            victim.HealthRegen = 0;
            var health = victim.Health;
            match.Cast(0, 3, hero);
            match.Runtime.Tick(.3);
            TestAssert.Near(350, health - victim.Health, 1e-4, "injected 750 radius, 250 + 1.0 AP from the script");
        }
    }

    [Check("spells-live-b4-rocket-grab-explicit-followup-from-hold")]
    public static void RocketGrabOrdersAttackOnItsHeroAfterHold()
    {
        foreach (var order in new byte[] { 1, 10 })
        {
            using var match = new Match("Blitzcrank", Team.Order, extraEnemy: true);
            var runtime = match.Runtime; var hero = match.Hero(0); var target = match.Hero(1);
            target.Position = hero.Position + new Position2(450, 0);
            match.Hero(2).Position = hero.Position + new Position2(0, 150);
            SendOrder(match, order, hero.Position, 0);
            TestAssert.Equal(order == 1, hero.HeroAutoAcquire, "Hold retains idle acquisition; Stop disables it");
            match.CastGround(0, target.Position);
            // The line leaves after the child's 0.25 s and reaches the victim 450 away 0.25 s later; the pull (1800) takes about 0.3 s more.
            runtime.Tick(.55);
            var pull = target.ForcedMove;
            TestAssert.True(pull is { Completed: false, Effect: "RocketGrab2" }, "hook owns a living hero's pull");
            TestAssert.True(hero.HeroPursuit is null, "no follow-up before pull completion");
            runtime.Tick(.4);
            TestAssert.True(target.ForcedMove is null, "owned completed pull is retired");
            TestAssert.False(target.Buffs.Any(buff => buff.Name == "RocketGrab2"), "RocketGrab2 ends with its move (BuffOnMoveEnd)");
            TestAssert.True(target.Buffs.Any(buff => buff.Name == "Stun"), "the global Stun BBApplyStun added survives landing");
            TestAssert.True(hero.HeroAutoAcquire, "explicit attack replaces Hold");
            TestAssert.Equal(target.NetId, hero.AttackTarget ?? 0u, "grabbed hero wins over nearby enemy");
            TestAssert.Equal(target.NetId, hero.HeroPursuit!.Value.TargetNetId, "same explicit pursuit as right-click");
            match.Attack(0, match.Hero(2));
            runtime.Tick(.01);
            TestAssert.Equal(match.Hero(2).NetId, hero.AttackTarget ?? 0u, "completion order is issued only once");
        }
    }

    [Check("spells-live-b4-power-fist-replaces-unfinished-basic")]
    public static void PowerFistReplacesUnfinishedBasic()
    {
        using var match = new Match("Blitzcrank", Team.Order);
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        hero.SpellRanks[3] = 0;
        hero.AttackWindup = .4;
        hero.AttackPeriod = 10;
        hero.AttackRange = 1000;
        hero.CritChance = 0;
        enemy.Armor = 0;
        enemy.HealthRegen = 0;
        match.Attack(0, enemy);
        runtime.Combat.CombatTick(.1, regenerate: false);
        runtime.Combat.CombatTick(.2, regenerate: false);
        var old = runtime.Combat.PendingHits.Single(hit => hit.SourceNetId == hero.NetId);
        TestAssert.True(old.WindupRemaining > 0, "old basic is unfinished");
        var order = hero.AttackTarget;
        var pursuit = hero.HeroPursuit;
        var health = enemy.Health;
        match.Cast(0, 2, hero);
        TestAssert.False(runtime.Combat.PendingHits.Contains(old), "E cancels the old hit");
        TestAssert.Equal(order, hero.AttackTarget, "E preserves the attack target");
        TestAssert.Equal(pursuit, hero.HeroPursuit, "E preserves pursuit");
        TestAssert.Near(0, hero.AttackCooldown, 1e-9, "E resets the attack timer");
        var buff = hero.Buffs.Single(candidate => candidate.Name == "PowerFist");
        match.Clear();
        // Arming E refreshes attack stats through its DodgePiercing payload.
        hero.AttackWindup = .4;
        hero.AttackPeriod = 10;
        hero.AttackRange = 1000;
        TestAssert.True(runtime.Combat.QueueBasicAttack(hero, enemy, new AttackRolls(1, 1, 1)),
            "the replacement basic starts immediately");
        runtime.Combat.CombatTick(.31, regenerate: false);
        TestAssert.Near(health, enemy.Health, 1e-9, "the old hit deadline passes without damage");
        runtime.Combat.CombatTick(.1, regenerate: false);
        // playtest B4 / retail sweep, 27 Sept 2026: the zero-crossing tick
        // finishes windup; the next tick resolves even a melee impact.
        TestAssert.Near(health, enemy.Health, 1e-9, "crossing windup does not resolve damage early");
        runtime.Combat.CombatTick(.01, regenerate: false);
        runtime.Delivery.Flush();
        TestAssert.Near(hero.AttackDamage * 2, health - enemy.Health, 1e-6,
            "exactly one empowered hit lands");
        TestAssert.False(hero.Buffs.Contains(buff), "the hit consumes Power Fist");
        TestAssert.Near(GameData.Default.Spell("PowerFist", 5).Cooldowns[0],
            hero.SpellCooldowns[2], 1e-6, "consumption starts the full cooldown");
        var removal = SpellPackets.RemoveBuff(hero.NetId, (byte)buff.Slot,
            SpellPackets.BuffNameHash("PowerFist"));
        TestAssert.Equal(1, match.Captures[0].Packets.Count(packet =>
            packet.Plaintext.Span.SequenceEqual(removal.Plaintext.Span)), "one consumption packet");
        TestAssert.Equal(1, match.Captures[0].Packets.Count(packet => Opcode(packet) == 0x8A &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId &&
            packet.Plaintext.Span[5] == 2), "one cooldown transition after consumption");
        runtime.Combat.CombatTick(.5, regenerate: false);
        TestAssert.Near(hero.AttackDamage * 2, health - enemy.Health, 1e-6,
            "no abandoned swing lands later");
    }

    [Check("spells-live-b4-static-field-stun-keeps-windup")]
    public static void StaticFieldStunKeepsWindup()
    {
        using var match = new Match("Blitzcrank", Team.Order);
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        enemy.SpellBlock = 0;
        enemy.HealthRegen = 0;
        // Batch 340: the passive's first bolt (its add while R is ready) lands before the baseline, not inside R's windup.
        SettlePassive(match);
        var health = enemy.Health;
        match.Cast(0, 3, hero);
        runtime.Tick(.067);
        match.Clear();
        TestAssert.True(runtime.SpellBuffs.Add(hero, "OrianaStun", BuffType.Stun,
            .75, enemy.NetId) is not null, "Shockwave control arrives 67 ms after R");
        runtime.Tick(.2);
        runtime.Tick(.1);
        TestAssert.Near(250 + hero.AbilityPower, health - enemy.Health, 1e-6,
            "committed Static Field bursts exactly once through stun");
        // Batch 340: the cooldown starts at the cast frame, the eighth 1/30 s step after the press (the last of the 0.2 s tick; the
        // projectile phase runs before the cooldowns count down), so it has run one step and the 0.1 s tick, not 0.367.
        TestAssert.Near(30 - .1 - 1.0 / 30.0, hero.SpellCooldowns[3], 1e-6,
            "R retains its one thirty-second cooldown");
        TestAssert.False(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x39 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId),
            "stun without a basic windup sends no spell-cancelling attack stop");

        using var basic = new Match("Blitzcrank", Team.Order);
        var attacker = basic.Hero(0);
        attacker.AttackRange = 1000;
        attacker.AttackWindup = .4;
        TestAssert.True(basic.Runtime.Combat.QueueBasicAttack(attacker, basic.Hero(1),
            new AttackRolls(1, 1, 1)), "a genuine basic windup begins");
        basic.Clear();
        basic.Runtime.SpellBuffs.Add(attacker, "Stun", BuffType.Stun, .75, basic.Hero(1).NetId);
        basic.Runtime.Delivery.Flush();
        TestAssert.False(basic.Runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == attacker.NetId),
            "stun cancels the basic windup");
        TestAssert.Equal(1, basic.Captures[0].Packets.Count(packet => Opcode(packet) == 0x39 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == attacker.NetId),
            "a cancelled basic still sends one attack stop");
    }

    [Check("spells-live-b4-ezreal-protected-windups")]
    public static void EzrealProtectedWindupsSurviveControl()
    {
        var names = new[] { "EzrealMysticShot", "EzrealEssenceFlux", "EzrealArcaneShift", "EzrealTrueshotBarrage" };
        for (var slot = 0; slot < names.Length; slot++)
        {
            using var match = new Match("Ezreal", Team.Order);
            var hero = match.Hero(0);
            TestAssert.True(GameData.Default.Spell(names[slot]).CantCancelWhileWindingUp,
                names[slot] + " has the shipped protection flag");
            match.CastGround(0, match.Hero(1).Position, slot);
            match.Runtime.Tick(.067);
            match.Runtime.SpellBuffs.Add(hero, "Stun", BuffType.Stun, 2, match.Hero(1).NetId);
            match.Runtime.Tick(.01);
            TestAssert.True(EzrealFlights.WindingUp(match.Runtime.Spells, hero),
                names[slot] + " remains committed while stunned");
            match.Runtime.Tick(slot == 3 ? 1 : .25);
            TestAssert.True(!EzrealFlights.WindingUp(match.Runtime.Spells, hero) && hero.SpellCooldowns[slot] > 0,
                names[slot] + " completes without refunding its cooldown");
        }
        TestAssert.False(GameData.Default.Spell("HowlingGale").CantCancelWhileWindingUp,
            "a shipped string-zero flag is not treated as true");
    }

    [Check("spells-live-b4-rocket-grab-replaces-pounce")]
    public static void RocketGrabReplacesPounce()
    {
        using var match = new Match("Blitzcrank", Team.Order, enemyChampion: "Nidalee");
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        var nidalee = match.Hero(1);
        hero.SpellRanks[3] = 0;
        hero.HealthRegen = 0;
        match.Cast(1, 3, nidalee);
        runtime.Tick(.3);
        nidalee.Position = hero.Position + new Position2(180, 0);
        nidalee.Facing = new Position2(-1, 0);
        nidalee.CooldownsDisabled = true;
        nidalee.SpellCooldowns[1] = 0;
        match.CastGround(0, nidalee.Position);
        runtime.Tick(.25);
        TestAssert.True(nidalee.ForcedMove is null, "hook is launched before Pounce starts");
        match.Cast(1, 1, nidalee);
        var pounce = nidalee.ForcedMove ?? throw new InvalidOperationException("Pounce did not start");
        var health = hero.Health;
        runtime.Tick(.04);
        var pull = nidalee.ForcedMove ?? throw new InvalidOperationException("grab did not pull");
        TestAssert.Equal("RocketGrab2", pull.Effect, "hook takes ownership forty ms into Pounce");
        TestAssert.False(ReferenceEquals(pounce, pull), "pull replaces Pounce");
        TestAssert.False(pounce.Succeeded, "replacement cannot report a successful landing");
        // The pull is RocketGrab2's BBMove to Blitzcrank's position, FURTHEST_WITHIN_RANGE: on open ground the client-quantized point of his
        // centre, otherwise the furthest body-clear point of the ray toward it (owner decision B3 #3: never inside a wall).
        var landing = pull.Destination;
        // MovementSystem.FurthestWalkableLanding refuses a candidate farther from the victim than the request: the lattice puts the centre (7000, 7000)
        // at (6999, 7001), a unit beyond the requested 180, so the landing is the next candidate, one 10-unit backoff step (and the lattice) short.
        TestAssert.True(Distance(landing, hero.Position) < 12.0, "the pull lands within one backoff step of Blitzcrank's centre on open ground");
        TestAssert.True(runtime.Movement.Navigation.IsMovementWalkable(landing) &&
            runtime.Movement.Navigation.PointClearance(landing) >= runtime.Movement.MovementRadius(nidalee) + 4.0,
            "the landing is floor that fits the victim's body");
        var attempts = 0;
        // RocketGrab2 ends with its move (BuffOnMoveEnd); the global Stun BBApplyStun added keeps the 0.6 s.
        while (nidalee.Buffs.Any(buff => buff.Name == "Stun") && attempts < 20)
        {
            nidalee.SpellCooldowns[1] = 0;
            TestAssert.False(match.TryCast(1, 1, nidalee), "another Pounce is rejected throughout stun");
            runtime.Tick(.05);
            attempts++;
        }
        TestAssert.True(attempts > 1 && attempts < 20, "the full grab stun was observed and expired");
        TestAssert.Equal(landing, nidalee.Position, "the pull reaches its endpoint");
        TestAssert.Near(health, hero.Health, 1e-6, "replaced Pounce deals no landing damage");
    }

    [Check("spells-live-rocket-grab-replaces-a-running-displacement")]
    public static void RocketGrabReplacesAForeignDisplacement()
    {
        // Phase 3 (19 October 2026): RocketGrab2 is a BBMove with CANCEL_ORDER and no ownership test, so the grab takes over a knock-up or pull
        // another unit started (the hand-written module kept the first displacement's owner, a worker's reading of playtest B4 #9).
        foreach (var effect in new[] { "PowerFistSlow", "RocketGrab2" })
        {
            using var match = new Match("Blitzcrank", Team.Order);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            hero.SpellRanks[3] = 0;
            match.CastGround(0, enemy.Position);
            var runtime = match.Runtime;
            runtime.Movement.StartForcedMove(enemy.NetId, enemy.Position + new Position2(50, 0),
                1, 2, hero.NetId, effect);
            var original = enemy.ForcedMove;
            // The child leaves after the parent's 0.25 s and flies at the hook's speed to the foe 300 units away.
            for (var tick = 0; tick < 200 && !enemy.Buffs.Any(buff => buff.Name == "RocketGrab2"); tick++) runtime.Tick(.01);
            TestAssert.True(enemy.Buffs.Any(buff => buff.Name == "RocketGrab2"), "hook hit");
            TestAssert.False(ReferenceEquals(original, enemy.ForcedMove) || enemy.ForcedMove is null,
                "the grab's own pull replaced the running displacement");
            TestAssert.Equal("RocketGrab2", enemy.ForcedMove!.Effect, "the pull is the grab's");
        }
    }

    [Check("spells-live-blitzcrank-fist-return-target-position")]
    public static void FistReturnDoesNotMoveItsTargetToTheImpactPoint()
    {
        foreach (var outcome in new[] { "normal", "shield", "lethal", "caster-death" })
        {
            using var match = new Match("Blitzcrank", Team.Order);
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            hero.SpellRanks[3] = 0;
            enemy.Position = new Position2(7600, 7000);
            if (outcome == "shield")
                match.Runtime.SpellBuffs.Add(enemy, "BansheesVeil", BuffType.Aura, 10, enemy.NetId,
                    canMitigateDuration: false);
            if (outcome == "lethal") enemy.Health = 1;
            var health = enemy.Health;
            // RocketGrabMissile times the hand's return with DistanceBetweenObjects, which reads the gap between the two bodies (BbSpellHost):
            // the time of the pull that ends where the bodies touch. The hand-written Q used the centre distance.
            var duration = (Distance(hero.Position, enemy.Position) - hero.CombatRadius - enemy.CombatRadius) / 1350.0;
            match.Clear();
            match.Cast(0, 0, enemy);
            // The script adds the internal RocketGrabMissile buff to Blitzcrank at the impact (after the child's 0.25 s and the flight).
            BuffState? returnBuff = null;
            for (var tick = 0; tick < 200 && returnBuff is null; tick++)
            {
                match.Runtime.Tick(.01);
                returnBuff = hero.Buffs.FirstOrDefault(buff => buff.Name == "RocketGrabMissile");
            }
            TestAssert.True(returnBuff is not null, "the impact adds the return effect's buff");
            match.Runtime.Delivery.Flush();
            var effect = match.Captures[0].Packets.Single(packet => Opcode(packet) == 0x8C &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) ==
                SpellPackets.BuffNameHash("FistReturn_mis"));
            var bytes = effect.Plaintext.Span;
            var effectId = BinaryPrimitives.ReadUInt32LittleEndian(bytes[25..]);
            TestAssert.Equal(SpellPackets.BuffNameHash("R_hand"), BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
            TestAssert.Equal(SpellPackets.BuffNameHash("head"), BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]));
            TestAssert.Equal(hero.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]));
            TestAssert.Equal(enemy.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[29..]));
            TestAssert.True(bytes[41..49].SequenceEqual(bytes[49..57]),
                "native 0x8C target correction uses Blitzcrank's position");
            TestAssert.False(bytes[33..41].SequenceEqual(bytes[49..57]),
                "FistReturn starts at the victim while targeting Blitzcrank");
            // The effect lives as long as the internal RocketGrabMissile buff the script adds to Blitzcrank: gap between the bodies / 1350 s.
            // A buff's ExpiresAt is the time it has left (BuffState counts it down), not a clock reading.
            var left = returnBuff!.ExpiresAt;
            TestAssert.Near(duration, left, .02, "return duration is the gap between the bodies / 1350");
            if (outcome == "shield")
            {
                // The shield is popped when the unit is handed to TargetExecute (batch 84), before the script's own BBBreakSpellShields; the
                // script still creates the return effect, and the popped shield eats the damage.
                TestAssert.Near(health, enemy.Health, 0, "spell shield still prevents damage");
            }
            if (outcome == "lethal")
            {
                TestAssert.False(enemy.Alive, "Q kills");
                TestAssert.True(enemy.ForcedMove is null && !enemy.Buffs.Any(buff => buff.Name == "RocketGrab2"),
                    "lethal Q creates no corpse control");
            }
            if (outcome == "caster-death") match.Runtime.Combat.Kill(hero, enemy.NetId);
            else
            {
                match.Runtime.Tick(left - .1);
                TestAssert.True(hero.Buffs.Contains(returnBuff), "the return effect's buff survives until its deadline");
            }
            match.Runtime.Tick(1);
            match.Runtime.Delivery.Flush();
            TestAssert.False(hero.Buffs.Contains(returnBuff), "the timer or the death ended the buff and with it the effect");
            foreach (var capture in match.Captures)
                TestAssert.Equal(1, capture.Packets.Count(packet => Opcode(packet) == FxPackets.KillOpcode &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == effectId),
                    "return effect is removed exactly once for both observers");
        }
    }

    [Check("spells-live-power-fist-move-outlives-lock")]
    public static void PowerFistMoveOutlivesStunLock()
    {
        using var match = new Match("Blitzcrank", Team.Order,
            random: new AnnieChecks.FixedRandomSource(0));
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        var enemy = match.Hero(1);

        match.Cast(0, 2, hero);
        var expectedLanding = runtime.Movement.FurthestWalkableLanding(enemy.Position,
            enemy.Position + new Position2(80.0, 0), enemy);
        TestAssert.True(runtime.Combat.ApplyHit(hero.NetId, enemy.NetId, hero.AttackDamage,
            DamageType.Physical, DamageSourceKind.Basic), "Power Fist hit lands");
        var move = enemy.ForcedMove ?? throw new InvalidOperationException("Power Fist did not start a move");
        var distance = Math.Sqrt(Math.Pow(move.Destination.X - enemy.Position.X, 2) +
            Math.Pow(move.Destination.Z - enemy.Position.Z, 2));
        TestAssert.Equal(expectedLanding, move.Destination,
            "Power Fist uses the fixed eastward roll and quantized FURTHEST_WITHIN_RANGE landing");
        TestAssert.True(distance <= 80.0 + 1e-6 && distance > 0.0,
            "Power Fist stays within the shipped 80-unit ring on a clear floor");
        TestAssert.True(runtime.Movement.Navigation.IsMovementWalkable(move.Destination) &&
            runtime.Movement.Navigation.PointClearance(move.Destination) >= enemy.Radius + 4.0,
            "Power Fist's quantized landing clears the target body");
        TestAssert.Near(90.0, move.Speed, 1e-6, "Power Fist uses the shipped move speed");
        // A script move has no duration of its own (batch 241; restated in batch 252 from "distance / speed plus 0.05 s of slack"): the dash
        // ends when it reaches its destination (the landing below, at about distance / 90 s), and its timer is only the safety stop past the trip.
        TestAssert.True(move.Remaining > distance / 90.0 + 1.0,
            "forced movement lifetime is not the stun duration nor a trip-sized timer");

        runtime.Tick(.5);
        TestAssert.False(enemy.Buffs.Any(buff => buff.Name == "PowerFistSlow"),
            "the shipped 0.5-second action lock expires");
        TestAssert.True(ReferenceEquals(move, enemy.ForcedMove) && !move.Completed,
            "forced movement remains authoritative after the action lock expires");
        match.Clear();
        match.Move(1, enemy.Position + new Position2(300.0, 0.0));
        TestAssert.True(ReferenceEquals(move, enemy.ForcedMove) && !move.Completed,
            "a move order cannot interrupt the still-active Power Fist displacement");
        TestAssert.False(match.Captures[1].Packets.Any(packet => Opcode(packet) == 0xC1),
            "the client receives no ordinary waypoint before Power Fist lands");

        // Land before the periodic one-second stat refresh; the completion
        // itself must restore native input permissions.
        runtime.Tick(.5);
        TestAssert.True(enemy.ForcedMove is null,
            "forced movement is cleared only after the native-length move lands");
        TestAssert.True(match.Captures[1].Packets.Any(packet =>
            Opcode(packet) == 0xCC && packet.Plaintext.Length == 171 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[11..]) == enemy.NetId &&
            (BinaryPrimitives.ReadInt32LittleEndian(packet.Plaintext.Span[55..]) & 5) == 5),
            "landing immediately restores movement and attack permissions on the wire");
        TestAssert.Near(move.Destination.X, enemy.Position.X, 1e-6,
            "Power Fist lands at the forced-move endpoint on X");
        TestAssert.Near(move.Destination.Z, enemy.Position.Z, 1e-6,
            "Power Fist lands at the forced-move endpoint on Z");
    }

    [Check("spells-live-blitzcrank-wall-notch-grab-knockup")]
    public static void WallNotchGrabThenPowerFistKnocksUp()
    {
        // Playtest B3 #3, 26 Sept 2026: Blitzcrank stood in a walkable notch
        // 0.8u from terrain. Rocket Grab pulled the target onto that centre,
        // where its body did not fit, and Power Fist found no body-clear
        // point on its 80u ray, so it stunned without a knock-up.
        using var match = new Match("Blitzcrank", Team.Order,
            random: new AnnieChecks.FixedRandomSource(0));
        var runtime = match.Runtime;
        var grid = runtime.Navigation;
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        hero.SpellRanks[3] = 0; // Keep Static Field's passive zap off the probe.
        hero.Position = new Position2(8569, 6389);
        enemy.Position = new Position2(8846, 6815);
        var bodyClearance = runtime.Movement.MovementRadius(enemy) + 4.0;
        TestAssert.True(grid.IsMovementWalkable(hero.Position) &&
            grid.PointClearance(hero.Position) < bodyClearance,
            "Blitzcrank's centre is floor too tight for the target's body");
        runtime.Delivery.Flush(); match.Clear();

        match.CastGround(0, enemy.Position);
        for (var step = 0; step < 40 && enemy.ForcedMove is null; step++) runtime.Tick(.05);
        var pull = enemy.ForcedMove ?? throw new InvalidOperationException("Rocket Grab did not pull");
        TestAssert.Equal("RocketGrab2", pull.Effect, "the grab starts its pull");
        TestAssert.True(grid.IsMovementWalkable(pull.Destination) &&
            grid.PointClearance(pull.Destination) >= bodyClearance,
            $"the pull lands where the whole target body clears terrain: {pull.Destination}");
        TestAssert.True(Distance(pull.Destination, hero.Position) < 120.0,
            $"the pull still lands beside Blitzcrank: {pull.Destination}");

        for (var step = 0; step < 40 && enemy.ForcedMove is not null; step++) runtime.Tick(.05);
        TestAssert.True(enemy.ForcedMove is null, "the pull and its RocketGrab2 control retire");
        match.Cast(0, 2, hero);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "PowerFist"), "arm Power Fist");
        match.Clear();
        var start = enemy.Position;
        TestAssert.True(runtime.Combat.ApplyHit(hero.NetId, enemy.NetId, hero.AttackDamage,
            DamageType.Physical, DamageSourceKind.Basic), "Power Fist hit lands");
        var knockup = enemy.ForcedMove ?? throw new InvalidOperationException("Power Fist did not move the target");
        TestAssert.Equal("PowerFistSlow", knockup.Effect, "Power Fist starts its own move");
        TestAssert.True(Distance(start, knockup.Destination) > 0.0 &&
            grid.IsMovementWalkable(knockup.Destination),
            $"Power Fist knocks up onto movement floor: {start} -> {knockup.Destination}");
        runtime.Delivery.Flush();
        TestAssert.True(match.Captures.SelectMany(capture => capture.Packets).Any(packet =>
            Opcode(packet) == 0x67 && packet.Channel == 4 &&
            packet.EntityReferences.Any(reference => reference.NetId == enemy.NetId)),
            "the knock-up sends its dash");
    }

    [Check("spells-live-power-fist-building-immunity")]
    public static void PowerFistCannotMoveBuildings()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var kind in new[] { EntityKind.Turret, EntityKind.Inhibitor, EntityKind.Nexus })
        {
            using var match = new Match("Blitzcrank", team);
            var hero = match.Hero(0);
            var building = match.Runtime.World.Entities.Values.First(unit =>
                unit.Kind == kind && WorldState.IsEnemy(hero, unit) &&
                !unit.Name.EndsWith("Shrine", StringComparison.OrdinalIgnoreCase));
            building.Protected = false;
            building.Armor = 0;
            hero.Position = building.Position + new Position2(-100, 0);
            match.Runtime.Delivery.Flush();
            match.Cast(0, 2, hero);
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "PowerFist"), "arm Power Fist");
            match.Clear();
            var health = building.Health;
            var position = building.Position;
            TestAssert.True(match.Runtime.Combat.ApplyHit(hero.NetId, building.NetId, hero.AttackDamage,
                DamageType.Physical, DamageSourceKind.Basic), "empowered building hit lands");
            match.Runtime.Delivery.Flush();

            TestAssert.Near(hero.AttackDamage * 2, health - building.Health, 1e-6,
                "Power Fist retains its bonus attack damage against buildings");
            TestAssert.False(building.Buffs.Any(buff => buff.Name == "PowerFistSlow"),
                "shipped CO_IS_TYPE_AI / CO_IS_NOT_TURRET guards exclude buildings from knock-up");
            TestAssert.True(building.ForcedMove is null, "buildings never receive a forced movement");
            TestAssert.Equal(position, building.Position, "the building stays at its placement");
            TestAssert.False(match.Captures.SelectMany(capture => capture.Packets)
                .Any(packet => Opcode(packet) == 0x67 && packet.Channel == 4),
                "no native dash packet is sent for a building");
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "PowerFist"), "the hit consumes the empowerment");
            TestAssert.True(hero.SpellCooldowns[2] > 0, "the consumed empowerment starts its cooldown");
        }
    }

    [Check("spells-live-power-fist-npc-landing-keeps-current-health")]
    public static void PowerFistNpcLandingKeepsCurrentHealth()
    {
        // Playtest #5, 25 Sept 2026: the landing stat refresh is hero-only. A
        // minion/monster 0xCC template writes max health into current HP.
        foreach (var kind in new[] { EntityKind.Minion, EntityKind.Neutral })
        {
            using var match = new Match("Blitzcrank", Team.Order,
                random: new AnnieChecks.FixedRandomSource(0));
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            hero.SpellRanks[3] = 0; // Keep Static Field's passive zap off the probe.
            var unitType = kind == EntityKind.Minion ? UnitType.Minion : UnitType.Monster;
            var victim = runtime.World.Register(new Entity(runtime.World.Allocate(unitType), kind,
                unitType, kind == EntityKind.Minion ? Team.Chaos : Team.Neutral,
                hero.Position + new Position2(0, 150))
            {
                Name = kind == EntityKind.Minion ? "PowerFistLaneProbe" : "PowerFistCampProbe",
                Alive = true, Created = true, Visible = true,
                Health = 300, MaxHealth = 1000, HealthRegen = 0, Speed = 325, Radius = 35,
            });
            runtime.Tick(.1);
            TestAssert.True(runtime.Session.Participants[0].View.Visible.Contains(victim.NetId),
                $"{kind}: Blitzcrank's observer already sees the damaged victim");
            match.Cast(0, 2, hero);
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "PowerFist"), $"{kind}: arm Power Fist");
            match.Clear();
            TestAssert.True(runtime.Combat.ApplyHit(hero.NetId, victim.NetId, hero.AttackDamage,
                DamageType.Physical, DamageSourceKind.Basic), $"{kind}: empowered hit lands");
            TestAssert.True(victim.ForcedMove is { Effect: "PowerFistSlow" },
                $"{kind}: Power Fist starts its knock-up move");
            var health = victim.Health;
            TestAssert.True(health > 0 && health < 300, $"{kind}: the hit leaves the victim damaged");
            for (var step = 0; step < 30 && victim.ForcedMove is not null; step++)
                runtime.Tick(.05);
            runtime.Tick(.1);

            TestAssert.True(victim.ForcedMove is null, $"{kind}: the ~0.9-second knock-up lands");
            TestAssert.Equal(health, victim.Health, $"{kind}: server health is unchanged by landing");
            TestAssert.False(match.Captures.SelectMany(capture => capture.Packets).Any(packet =>
                Opcode(packet) == 0xCC && packet.Plaintext.Length == 111 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[11..]) == victim.NetId),
                $"{kind}: landing sends no full-HP minion stat template");
        }
    }

    // Batch 340: half a second of 30 Hz steps, so CharScriptBlitzcrank's poll adds the passive StaticField (R ready) and its immediate bolt
    // lands before a check starts measuring; the next bolt is 2.5 s later. The packets of the settle are cleared.
    private static void SettlePassive(Match match)
    {
        for (var step = 0; step < 15; step++) match.Runtime.Tick(1.0 / 30.0);
        match.Runtime.Delivery.Flush();
        match.Clear();
    }
}
