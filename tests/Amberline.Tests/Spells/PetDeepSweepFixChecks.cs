using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-annie-b6-basic-hit-skin-and-lethal-particles")]
    public static void AnnieBasicHitsCreateOneBoundSkinParticle()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var skin in new[] { 0, 5 })
        foreach (var critical in new[] { false, true })
        foreach (var lethal in new[] { false, true })
        {
            using var match = new Match("Annie", team, skinId: skin, extraEnemy: true);
            var runtime = match.Runtime; var annie = match.Hero(0); var target = match.Hero(1);
            annie.CritChance = critical ? 1 : 0;
            var hit = runtime.Combat.BasicAttackDamage(annie, target, new AttackRolls(.5, 1, 1));
            TestAssert.Equal(critical, hit.Critical, "normal/crit attack selection");
            if (lethal) target.Health = 1;
            match.Clear();
            TestAssert.True(runtime.Combat.ApplyHit(annie.NetId, target.NetId, hit.Damage,
                DamageType.Physical, DamageSourceKind.Basic, typeByte: critical ? (byte)0x83 : (byte)0x84));
            runtime.Delivery.Flush();
            TestAssert.Equal(!lethal, target.Alive);
            foreach (var capture in match.Captures)
            {
                // AnnieBasicAttack{,2}/AnnieCritAttack TargetExecute, skin 5.
                var effects = capture.Packets.Where(packet => Opcode(packet) == 0x8C &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) is 0x0B375F22u or 0x0D6AE984u).ToArray();
                TestAssert.Equal(1, effects.Length, "exactly one impact, including lethal normal/crit hits");
                var bytes = effects[0].Plaintext.Span;
                TestAssert.Equal(skin == 5 ? 0x0D6AE984u : 0x0B375F22u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[6..]));
                TestAssert.Equal(target.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]));
                TestAssert.Equal(target.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[29..]));
                TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
                TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]));
            }
        }
        using var ezrealMatch = new Match("Ezreal", Team.Order);
        ezrealMatch.Cast(0, 0, ezrealMatch.Hero(1));
        SpellStep(ezrealMatch, .3); SpellStep(ezrealMatch, .5);
        ezrealMatch.Runtime.Delivery.Flush();
        TestAssert.True(ezrealMatch.Captures[0].Packets.Any(packet => Opcode(packet) == 0x68), "Ezreal Q lands");
        TestAssert.False(ezrealMatch.Captures.SelectMany(capture => capture.Packets).Any(packet =>
            IsNamedVisual(packet, "AnnieBasicAttack_tar") || IsNamedVisual(packet, "AnnieBasicAttack_tar_frost")),
            "Q's Basic damage category must not dispatch Annie's attack effect");
        using var petMatch = new Match("Annie", Team.Order);
        petMatch.Cast(0, 3, petMatch.Hero(1)); SpellStep(petMatch, .5);
        var tibbers = petMatch.Runtime.World.Entities.Values.Single(unit =>
            unit.Kind == EntityKind.Pet && unit.OwnerNetId == petMatch.Hero(0).NetId);
        petMatch.Runtime.Delivery.Flush(); petMatch.Clear();
        TestAssert.True(petMatch.Runtime.Combat.ApplyHit(tibbers.NetId, petMatch.Hero(1).NetId, 1,
            DamageType.Physical, DamageSourceKind.Basic));
        petMatch.Runtime.Delivery.Flush();
        TestAssert.False(petMatch.Captures.SelectMany(capture => capture.Packets).Any(packet =>
            IsNamedVisual(packet, "AnnieBasicAttack_tar") || IsNamedVisual(packet, "AnnieBasicAttack_tar_frost")),
            "Tibbers shares ChampionName=Annie but has his own attack script");
    }

    [Check("spells-shaco-b5-clone-backstab-snapshot")]
    public static void ShacoCloneBackstabSurvivesTargetTurn()
    {
        using var match = new Match("Shaco", Team.Order);
        var runtime = match.Runtime; var hero = match.Hero(0); var enemy = match.Hero(1);
        var clone = SpawnHallucinateClone(match, hero);
        runtime.Combat.InterruptBasicAttack(clone);
        clone.Position = hero.Position; enemy.Position = clone.Position + new Position2(100, 0);
        clone.Facing = enemy.Facing = new Position2(1, 0);
        // Combat debuffs refresh hero stats: normalize the harness's artificial 5000 HP before measuring.
        runtime.HeroStats.Refresh(enemy);
        clone.AttackDamage = 100; clone.CritChance = 0; enemy.Armor = 0;
        var hit = runtime.Combat.BasicAttackDamage(clone, enemy, new AttackRolls(1, 1, 1));
        TestAssert.Near(.75, clone.Buffs.Single(buff => buff.Name == "FromBehind").ExpiresAt, 1e-9);
        enemy.Facing = new Position2(-1, 0);
        var health = enemy.Health;
        runtime.Combat.ApplyHit(clone.NetId, enemy.NetId, hit.Damage, DamageType.Physical, DamageSourceKind.Basic);
        // Hit hooks apply Backstab and the clone modifier; the stat refresh restores the target's base armor.
        // The clone is an obj_AI_Minion (SpawnPet), so the queued basic-attack amount already carries
        // Map1 dr_UnitToHero (0.7).
        var unitToHero = GameData.Default.LevelConstants.DamageRatios.UnitToHero;
        TestAssert.Near(100 * unitToHero * 1.2 * .75 * 100 / (100 + enemy.Armor), health - enemy.Health, 1e-6,
            "100 * 0.7 unit-to-hero * 1.2 Backstab * .75 clone, then armor");
    }

    [Check("spells-annie-tibbers-native-pet-spawn-sight-reconnect")]
    public static void TibbersNativePetCreationAndRemainingLifetime()
    {
        foreach (var rank in new[] { 1, 2, 3 })
        {
            using var match = new Match("Annie", Team.Order, skinId: 5);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = new Position2(12000, 12000);
            runtime.Delivery.Flush();
            match.Clear();
            var pet = AnnieChecks.SummonTibbers(runtime, owner, rank);
            // SummonTibbers steps only the spell clock through the windup, and a pet's remaining lifetime is read from the combat clock:
            // bring the combat clock to the same time (the fixed-step simulation cannot be asked for exactly 0.26 s of windup).
            runtime.Combat.CombatTick(0.26, regenerate: false);
            runtime.Delivery.Flush();
            AssertTibbersCreation(match.Captures[0].Packets, owner, pet, rank, 45);
            // Restated in batch 343 (this check predated the owner decision of 26 October 2026, "Follow retail", master 4dd331f7 and 3df51528):
            // CharSpawnPet (0x3C) is PolicyBroadcast in the client's debug data and BBSpawnPet sends it through the owner's TryBroadcast
            // (batch 329's record), so the enemy that holds Annie creates the hidden Tibbers at his spawn, with the spawn's full lifetime, once,
            // and is told he left its sight. The old expectation was "hidden Tibbers is not created for the enemy".
            AssertTibbersCreation(match.Captures[1].Packets, owner, pet, rank, 45);
            TestAssert.False(runtime.Session.Participants[1].View.Visible.Contains(pet.NetId), "the enemy does not see the hidden Tibbers");

            owner.SpellRanks[3] = rank == 3 ? 1 : 3;
            runtime.Combat.CombatTick(7);
            match.Clear();
            enemy.Position = owner.Position + new Position2(200, 0);
            runtime.Delivery.Flush();
            // The enemy's first sight reveals the Tibbers it created at the spawn: neither client creates him again. A late creation, with the
            // remaining lifetime, is the fresh view below.
            TestAssert.True(runtime.Session.Participants[1].View.Visible.Contains(pet.NetId), "the enemy sees Tibbers");
            TestAssert.False(match.Captures.SelectMany(capture => capture.Packets).Any(packet => Opcode(packet) is 0x3C or 0x80 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == pet.NetId),
                "first enemy sight does not recreate the owner's pet");

            TestAssert.True(runtime.Delivery.RemoveObserver(1));
            var capture = new Capture();
            var observer = new ReplicationObserver(1, owner.Team, owner.NetId, capture);
            observer.View.Known.UnionWith(new[] { owner.NetId, enemy.NetId });
            runtime.Delivery.AddObserver(observer);
            runtime.Delivery.Flush();
            AssertTibbersCreation(capture.Packets, owner, pet, rank, 38);
            capture.Packets.Clear();
            runtime.Delivery.Flush();
            TestAssert.False(capture.Packets.Any(packet => Opcode(packet) is 0x3C or 0x80),
                "repeated baseline flush does not duplicate creation");
        }
    }

    private static void AssertTibbersCreation(IReadOnlyList<PlaintextPacket> packets,
        Entity owner, Entity pet, int rank, float duration)
    {
        var creations = packets.Where(packet => (Opcode(packet) is 0x3C or 0x80) &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == pet.NetId).ToArray();
        TestAssert.Equal(1, creations.Length, "exactly one creation, no competing SpawnMinion");
        var bytes = creations[0].Plaintext.Span;
        TestAssert.Equal((byte)0x3C, bytes[0]);
        TestAssert.Equal(208, bytes.Length);
        TestAssert.Equal(owner.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]));
        TestAssert.Equal((byte)pet.NodeId, bytes[9]);
        TestAssert.Near(pet.Position.X, BinaryPrimitives.ReadSingleLittleEndian(bytes[10..]), .001);
        TestAssert.Near(pet.Position.Z, BinaryPrimitives.ReadSingleLittleEndian(bytes[18..]), .001);
        TestAssert.Equal(rank, BinaryPrimitives.ReadInt32LittleEndian(bytes[22..]));
        TestAssert.Near(duration, BinaryPrimitives.ReadSingleLittleEndian(bytes[26..]), .001);
        TestAssert.Equal(25 * (rank - 1), BinaryPrimitives.ReadInt32LittleEndian(bytes[30..]));
        TestAssert.Equal(400 * (rank - 1), BinaryPrimitives.ReadInt32LittleEndian(bytes[34..]));
        TestAssert.Equal("Tibbers", System.Text.Encoding.ASCII.GetString(bytes.Slice(38, 32)).TrimEnd('\0'));
        TestAssert.Equal("AnnieTibbers", System.Text.Encoding.ASCII.GetString(bytes.Slice(70, 32)).TrimEnd('\0'));
        TestAssert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(bytes[102..]));
        TestAssert.Equal("InfernalGuardian", System.Text.Encoding.ASCII.GetString(bytes.Slice(106, 64)).TrimEnd('\0'));
        TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[170..]), "non-clone pet");
        TestAssert.Equal((byte)1, bytes[174], "BBSpawnPet defaults CopyItems on and leaves focus unchanged");
        TestAssert.Equal("Pet.lua", System.Text.Encoding.ASCII.GetString(bytes.Slice(175, 32)).TrimEnd('\0'));
        TestAssert.Equal((byte)0, bytes[207]);
        var stats = packets.Where(packet => Opcode(packet) == 0xCC && packet.Plaintext.Length == 111 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[11..]) == pet.NetId).ToArray();
        TestAssert.True(stats.Length > 0, "pet creation includes its stat template");
        foreach (var packet in stats)
            // Batch 52: the minion LOCAL1 block has 21 entries; mHPRegenRate is entry 17, 19 + 4 * 17 = byte 87 (until then the
            // block had 23 entries and the HP regen sat in entry 18, byte 91, where the client's mPARRegenRate is).
            TestAssert.Near(.5, BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[87..]), 0,
                "ANN-03: first sight and reconnect retain Tibbers' native regeneration");
    }

    [Check("spells-annie-lethal-q-w-r-skin-effects")]
    public static void AnnieLethalImpactsKeepBothSkinBranches()
    {
        foreach (var skin in new[] { 0, 5 })
        foreach (var slot in new[] { 0, 1, 3 })
        {
            using var match = new Match("Annie", Team.Order, skinId: skin);
            var owner = match.Hero(0);
            var target = match.Hero(1);
            // W's command range is 75: a 200-unit aim queues approach movement
            // before its windup, so the fixture must begin within command range.
            target.Position = owner.Position + new Position2(50, 0);
            target.Health = 1;
            // Armed: the fifth Pyromania stack's ready charge (script engine, 19 October 2026).
            match.Runtime.Buffs.AddBuff(owner, "Pyromania_particle", BuffType.CombatEnhancer, 25000.0, owner.NetId);
            if (slot == 0) match.Cast(0, slot, target);
            else match.CastGround(0, target.Position, slot);
            TestAssert.True(owner.PendingCast is null, "lethal probe casts without approach movement");
            Advance(match, .6);
            match.Runtime.Delivery.Flush();
            TestAssert.False(target.Alive, $"skin {skin}, slot {slot}: impact is lethal");
            TestAssert.False(target.Buffs.Any(buff => buff.Name == "Stun"), "no persistent control on the corpse");
            var suffix = skin == 5 ? "_frost" : "";
            foreach (var capture in match.Captures)
            {
                // VIS-06, 27 Sept 2026 base-game sweep: restore R for every
                // capture; creation-time sight survives the victim's death.
                var effect = slot switch
                {
                    0 => "DisintegrateHit_tar" + suffix,
                    1 => "Incinerate_buf" + suffix,
                    _ => "infernalguardian_tar" + suffix,
                };
                TestAssert.True(HasEffect(capture, effect), $"skin {skin}, slot {slot}, capture {match.Captures.IndexOf(capture)}: lethal impact keeps its effect");
                if (slot == 0)
                    TestAssert.True(HasEffect(capture, "Disintegrate_hit" + suffix), "Q keeps its second impact effect");
            }
            if (slot == 3)
            {
                // Creation sight belongs to that event, not to subsequent bursts.
                // With the victim already dead, its team cannot see this new R.
                // The first R's own RevealSpellCaster bubble (400 units around Annie for 4.5 s,
                // InfernalGuardian's absent flag = true) would still give that team sight of this
                // point, so let it end first: the check isolates "no sight provider".
                Advance(match, RevealSeconds);
                match.Clear();
                owner.SpellRanks[3] = 1;
                owner.SpellCooldowns[3] = 0.0;
                owner.Mana = owner.MaxMana;
                match.CastGround(0, target.Position, 3);
                Advance(match, .3);
                match.Runtime.Delivery.Flush();
                TestAssert.True(HasEffect(match.Captures[0], "infernalguardian_tar" + suffix));
                TestAssert.False(HasEffect(match.Captures[1], "infernalguardian_tar" + suffix),
                    "VIS-06: unobserved ground effects are not broadcast or given stale recipients");
            }
        }
    }

    [Check("pets-live-tibbers-delayed-snapshot-burn-and-alt-orders")]
    public static void TibbersBurnSnapshotAndOrders()
    {
        using var match = new Match("Annie", Team.Order);
        var runtime = match.Runtime;
        var annie = match.Hero(0);
        var enemy = match.Hero(1);
        runtime.Tick(10.1);
        annie.AbilityPower = 100;
        enemy.Position = annie.Position + new Position2(200, 0);
        match.CastGround(0, annie.Position, 3);
        runtime.Tick(.5);
        var tibbers = runtime.World.Entities.Values.Single(unit =>
            unit.Kind == EntityKind.Pet && unit.OwnerNetId == annie.NetId);
        var burn = tibbers.Buffs.Single(buff => buff.Name == "InfernalGuardianBurning");
        // 19 October 2026: the aura is the InfernalGuardianBurning script buff. InfernalGuardian.SelfExecute stores
        // FinalDamage = DamageAmount (35) + 0.2 x the owner's AP (BBMath, Lua doubles: the script's own 0.2, not the inibin's float32
        // coefficient) in the buff's InstanceVars at the cast frame, so the AP of the summon is what the buff keeps.
        var instance = ((BbChampionModule)runtime.Dependencies.ChampionDispatcher.FindModule("Annie")!).Buffs!.Instances
            .Single(item => item.Name == "InfernalGuardianBurning" && ReferenceEquals(item.Owner, tibbers));
        TestAssert.Near(35.0 + 0.2 * 100.0, (double)instance.InstanceVars["FinalDamage"]!, 1e-9, "summon AP is captured");
        TestAssert.Equal(annie.NetId, burn.CasterNetId);

        SendOwnedPetOrder(match, tibbers, 11, tibbers.Position);
        // ORDER_PETHARDSTOP is AI_PET_HARDSTOP in Pet.lua's OnOrder.
        TestAssert.Equal(PetLuaAiEngine.StatePetHardStop, match.Runtime.Pets.StateOf(tibbers)!.Value,
            "Alt hold reaches owned Tibbers");
        var damage = enemy.MatchStatistics.TotalDamageTaken;
        annie.AbilityPower = 300;
        var untilBurn = instance.StartTime + 1.0 - runtime.Spells.CurrentTime;
        TestAssert.True(untilBurn > .1, "summon has not already reached its first burn");
        runtime.Tick(untilBurn - .05);
        TestAssert.Near(damage, enemy.MatchStatistics.TotalDamageTaken, 1e-6,
            "first burn waits a full second");
        runtime.Tick(.1);
        TestAssert.True(enemy.MatchStatistics.TotalDamageTaken > damage,
            "aura still damages while Tibbers holds position");
        var expected = 55.0 * 100.0 / (100.0 + enemy.SpellBlock);
        TestAssert.Near(expected, enemy.MatchStatistics.TotalDamageTaken - damage, .02,
            "burn retains summon AP through the combat path");

        var destination = tibbers.Position + new Position2(300, 0);
        SendOwnedPetOrder(match, tibbers, 6, destination);
        // ORDER_PETHARDMOVE is AI_PET_HARDMOVE in Pet.lua's OnOrder.
        TestAssert.Equal(PetLuaAiEngine.StatePetHardMove, match.Runtime.Pets.StateOf(tibbers)!.Value,
            "Alt move reaches owned Tibbers");
        TestAssert.True(tibbers.Waypoints.Count > 0 || tibbers.Target is not null,
            "Alt move installs a route");
    }

    [Check("pets-live-tibbers-ghoul-aoe-and-owner-havoc")]
    public static void TibbersGhoulAndHavocDamage()
    {
        using (var havocMatch = new Match("Annie", Team.Order, enemyChampion: "Shaco"))
        {
            var runtime = havocMatch.Runtime;
            var annie = havocMatch.Hero(0);
            var victim = havocMatch.Hero(1);
            annie.Masteries = MasteryPage.Create(new MasteryCatalog(), new Dictionary<int, int>
            {
                [103] = 3, [106] = 3, [136] = 1, [126] = 1,
                [102] = 4, [105] = 4, [146] = 1, [101] = 3, [123] = 1,
            });
            runtime.HeroStats.Refresh(annie);
            TestAssert.Near(1.04, annie.LoadoutMasteries.Combat.NonTrueDamageDealtMultiplier,
                1e-9, "rank-one Havoc is active on Annie");
            havocMatch.CastGround(0, annie.Position, 3);
            runtime.Tick(.5);
            var tibbers = runtime.World.Entities.Values.Single(unit =>
                unit.Kind == EntityKind.Pet && unit.OwnerNetId == annie.NetId);
            tibbers.AttackCooldown = 999;
            // Park Tibbers on the script's hard stop (ORDER_PETHARDSTOP -> AI_PET_HARDSTOP), which
            // acquires nothing, in place of the retired PetState.Order = 11 write.
            runtime.Pets.Command(annie, 11, tibbers.Position, actorNetId: tibbers.NetId);
            // The script's Tibbers (batch 86) has already picked the victim during the half second above and wound up a swing; drop it, so
            // the second below holds the burn alone (the cooldown only stops the next one).
            runtime.Combat.InterruptBasicAttack(tibbers);
            tibbers.AttackTarget = null;
            victim.Position = tibbers.Position + new Position2(200, 0);
            victim.SpellBlock = 0;
            runtime.Pets.Tick(runtime.Combat.CurrentTime, 0);
            var before = victim.MatchStatistics.TotalDamageTaken;
            for (var step = 0; step < 31; step++) runtime.Tick(1.0 / 30.0);
            TestAssert.Near((35.0 + 0.2 * annie.AbilityPower) * 1.04,
                victim.MatchStatistics.TotalDamageTaken - before, .02,
                "Annie is the burn attacker and contributes Havoc's 1.04 multiplier");
        }
    }

    [Check("pets-live-shaco-fear-npc-open-and-wall-movement")]
    public static void BoxFearMovesMinionAndWallAdjacentCamp()
    {
        using (var openMatch = new Match("Shaco", Team.Order,
                   random: new AnnieChecks.FixedRandomSource(.75)))
        {
            var runtime = openMatch.Runtime;
            var victim = new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
                UnitType.Minion, Team.Chaos, new Position2(11000, 11000))
            {
                Name = "FearMinion", MinionType = MinionType.Melee, Health = 5000,
                MaxHealth = 5000, Speed = 325, Radius = 35, Created = true, Visible = true,
            };
            runtime.World.Register(victim);
            VerifyFearWalk(openMatch, victim, new Position2(7000, 7000), wall: false);
        }

        using (var wallMatch = new Match("Shaco", Team.Order,
                   random: new AnnieChecks.FixedRandomSource(.75)))
        {
            var runtime = wallMatch.Runtime;
            runtime.Tick(100.1);
            var camp = runtime.World.Entities.Values.First(unit =>
                unit.Kind == EntityKind.Neutral && unit.Name == "wolf");
            camp.MaxHealth = camp.Health = 5000;
            var start = Enumerable.Range(0, 110)
                .Select(index => new Position2(6913, 6635 - index * 5))
                .First(point => runtime.Navigation.PointClearance(point,
                                    runtime.Movement.MovementRadius(camp) + 4) >=
                                    runtime.Movement.MovementRadius(camp) + 4 &&
                                runtime.Navigation.IsMovementWalkable(point + new Position2(0, 230)) &&
                                runtime.Navigation.BrushAllowsVision(point + new Position2(0, 230), point) &&
                                FearDistance(point, runtime.Movement.FirstCollisionLanding(point,
                                    point + new Position2(0, -375), camp.Speed * .6, .5, camp)) is > 12 and < 75);
            camp.NeutralHome = new Position3(start.X, 0, start.Z);
            VerifyFearWalk(wallMatch, camp, start, wall: true);
        }
    }

    private static void VerifyFearWalk(Match match, Entity victim, Position2 start, bool wall)
    {
        var runtime = match.Runtime;
        var boxPoint = start + new Position2(0, 230);
        match.Hero(0).Position = boxPoint;
        match.Hero(1).Position = new Position2(12000, 12000);
        match.CastGround(0, boxPoint, 1);
        for (var tick = 0; tick < 75; tick++) runtime.Tick(1.0 / 30.0);
        victim.Position = start;
        victim.Waypoints.Clear();
        victim.Target = null;
        if (wall) victim.NeutralState = "retreat";
        for (var tick = 0; tick < 10 && victim.Buffs.All(buff => buff.Name != "Fear"); tick++)
            runtime.Tick(1.0 / 30.0);
        TestAssert.True(victim.Buffs.Any(buff => buff.Name == "Fear"),
            "an armed box fears the live NPC");
        var fearedStart = victim.Position;
        var move = victim.ForcedMove ?? throw new InvalidOperationException("Fear has no forced move");
        TestAssert.True(FearDistance(fearedStart, move.Destination) > 12,
            "fear selects a reachable nonzero waypoint");
        if (wall)
        {
            TestAssert.True(FearDistance(fearedStart, move.Destination) < 75,
                "wall-adjacent fear stops at the first terrain or body boundary");
            var clearance = runtime.Movement.MovementRadius(victim) + 4;
            TestAssert.True(runtime.Navigation.PointClearance(move.Destination, clearance) >=
                clearance, "wall landing keeps the victim's movement body clear");
        }
        for (var tick = 0; tick < 5; tick++) runtime.Tick(1.0 / 30.0);
        TestAssert.True(FearDistance(fearedStart, victim.Position) > 5,
            "minion/camp AI does not cancel the active fear walk");
        TestAssert.True(victim.ForcedMove == move,
            "the feared NPC retains the same forced move while travelling");
    }

    private static double FearDistance(Position2 a, Position2 b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    [Check("pets-live-shaco-box-hidden-brush-acquisition")]
    public static void BoxWaitsForTeamSightBeforeWaking()
    {
        using var match = new Match("Shaco", Team.Order);
        var runtime = match.Runtime;
        var shaco = match.Hero(0);
        var enemy = match.Hero(1);
        runtime.Tick(10.1);
        shaco.Position = new Position2(4300, 6650);
        enemy.Position = new Position2(4300, 6900);
        runtime.Delivery.Flush();
        TestAssert.False(runtime.Session.Participants[0].View.Visible.Contains(enemy.NetId),
            "brush victim is hidden before the box casts");
        match.CastGround(0, shaco.Position, 1);
        runtime.Tick(3.0);
        var box = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.ShacoBox);
        // JackInTheBox.BuffOnUpdateActions wakes the box by adding EndKill (5 s) to itself and removing Stealth.
        TestAssert.False(box.Buffs.Any(buff => buff.Name == "EndKill"),
            "an unseen brush occupant cannot wake a box");
        TestAssert.False(runtime.Spells.SharedProjectiles.Any(projectile =>
            projectile.Snapshot.SpellName == "ShacoBoxSpell"),
            "an unseen brush occupant cannot start a shot");
        enemy.Position = box.Position + new Position2(0, -200);
        runtime.Tick(.1);
        TestAssert.True(box.Buffs.Any(buff => buff.Name == "EndKill"),
            "the same box wakes when its team can see the target");
    }

    [Check("pets-live-shaco-box-missile-late-observer")]
    public static void BoxMissileRefreshesForLateObserver()
    {
        using var match = new Match("Shaco", Team.Order);
        var runtime = match.Runtime;
        var shaco = match.Hero(0);
        var enemy = match.Hero(1);
        runtime.Tick(10.1);
        enemy.Position = shaco.Position + new Position2(320, 0);
        match.CastGround(0, shaco.Position, 1);
        for (var tick = 0; tick < 120 && !runtime.Spells.SharedProjectiles.Any(projectile =>
                 projectile.Snapshot is { SpellName: "ShacoBoxSpell", Launched: true }); tick++)
            runtime.Tick(1.0 / 30.0);
        var shot = runtime.Spells.SharedProjectiles.Single(projectile =>
            projectile.Snapshot is { SpellName: "ShacoBoxSpell", Launched: true }).Snapshot;
        var capture = new Capture();
        var observer = new ReplicationObserver(90, Team.Order, shaco.NetId, capture);
        observer.View.Known.UnionWith(new[] { shaco.NetId, enemy.NetId, shot.CasterNetId });
        runtime.Delivery.AddObserver(observer);
        runtime.Delivery.Flush();
        capture.Packets.Clear();
        runtime.Tick(1.0 / 30.0);
        TestAssert.True(capture.Packets.Any(packet => packet.Plaintext.Span[0] == 0x41 &&
            packet.Plaintext.Span.IndexOf(BitConverter.GetBytes(shot.MissileNetId)) >= 0),
            "an in-flight box missile is offered to a newly seeing observer");
    }

    [Check("pets-live-shaco-clone-mana-on-spawn")]
    public static void CloneStatsCarryCopiedMana()
    {
        using var match = new Match("Shaco", Team.Order);
        var runtime = match.Runtime;
        var shaco = match.Hero(0);
        var clone = SpawnHallucinateClone(match, shaco);
        var stats = match.Captures[0].Packets.Last(packet => packet.Plaintext.Length == 111 &&
            packet.Plaintext.Span[0] == 0xCC &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[11..]) == clone.NetId);
        TestAssert.True(clone.Mana > 0 && clone.MaxMana > 0, "clone keeps source mana in server state");
        TestAssert.Near(clone.Mana, BinaryPrimitives.ReadSingleLittleEndian(stats.Plaintext.Span[23..]), .001,
            "initial clone stats publish current mana");
        TestAssert.Near(clone.MaxMana, BinaryPrimitives.ReadSingleLittleEndian(stats.Plaintext.Span[31..]), .001,
            "initial clone stats publish max mana");
    }

    [Check("pets-live-shaco-clone-chases-through-owner-ring")]
    public static void CloneChasesEnemyNearOwner()
    {
        using var match = new Match("Shaco", Team.Order);
        var runtime = match.Runtime;
        var shaco = match.Hero(0);
        var enemy = match.Hero(1);
        runtime.Tick(10.1);
        var clone = SpawnHallucinateClone(match, shaco);
        clone.Position = shaco.Position + new Position2(-200, 0);
        clone.Waypoints.Clear();
        clone.Target = null;
        enemy.Position = shaco.Position + new Position2(100, 0);
        var before = enemy.MatchStatistics.TotalDamageTaken;
        SendOwnedPetOrder(match, clone, 5, enemy.Position, enemy.NetId);
        for (var tick = 0; tick < 80 && enemy.MatchStatistics.TotalDamageTaken <= before; tick++)
            runtime.Tick(.1);
        TestAssert.True(enemy.MatchStatistics.TotalDamageTaken > before,
            "a clone crosses the owner's follow ring to attack a nearby enemy");
    }

    [Check("pets-live-shaco-clone-flanks-blocked-attack-circle")]
    public static void CloneFindsLegalAttackPointAroundOwnerBody()
    {
        using var match = new Match("Shaco", Team.Order);
        var runtime = match.Runtime;
        var owner = match.Hero(0);
        var enemy = match.Hero(1);
        runtime.Tick(10.1);
        var clone = SpawnHallucinateClone(match, owner);
        owner.Position = new Position2(7299, 7001);
        enemy.Position = new Position2(12000, 12000);
        clone.Position = new Position2(7301, 7133);
        clone.Waypoints.Clear();
        clone.Target = null;
        var target = new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
            UnitType.Minion, Team.Chaos, new Position2(7480, 7000))
        {
            Name = "PursuitTarget", MinionType = MinionType.Melee,
            Health = 5000, MaxHealth = 5000, Speed = 0, Radius = 35.75,
            Created = true, Visible = true,
        };
        runtime.World.Register(target);
        var blocker = new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
            UnitType.Minion, Team.Order, new Position2(7420, 7180))
        {
            Name = "FriendlyBlocker", MinionType = MinionType.Melee,
            Health = 5000, MaxHealth = 5000, Speed = 0, Radius = 55.8,
            Created = true, Visible = true,
        };
        runtime.World.Register(blocker);
        // playtest B4 / retail sweep, 27 Sept 2026: move the clone outside
        // the larger attack circle and place the blocker on its radial stop.
        var reach = CombatService.AttackReach(clone, target);
        clone.Position = target.Position + new Position2(-reach - 180.0, 0);
        owner.Position = clone.Position + new Position2(0, -180);
        blocker.Position = target.Position + new Position2(-reach + 8.0, 0);
        var approachStart = clone.Position;
        TestAssert.False(runtime.Combat.CanAttack(clone, target), "flank starts outside shared reach");
        runtime.Delivery.Flush();
        var initialHealth = target.Health;
        SendOwnedPetOrder(match, clone, 5, target.Position, target.NetId);
        TestAssert.True(clone.Waypoints.Count > 1 || clone.Target is not null,
            "clone chooses a body-clear approach point when the radial point is blocked");
        for (var tick = 0; tick < 160 && target.Health >= initialHealth; tick++)
            runtime.Tick(.05);
        TestAssert.True(target.Health < initialHealth,
            "clone reaches legal attack range and hits past the blocked radial point");

        // A planner may return a traversable waypoint that is outside the
        // attack circle for every sampled goal. None may survive rejection.
        clone.Position = approachStart;
        clone.Waypoints.Clear();
        clone.Target = null;
        runtime.Navigation.FindPathOverride = (start, _) =>
            [start, new Position2(start.X, start.Z + 30)];
        runtime.Navigation.FindPathAroundUnitsOverride = (start, _, _) =>
            [start, new Position2(start.X, start.Z + 30)];
        SendOwnedPetOrder(match, clone, 5, target.Position, target.NetId);
        TestAssert.True(clone.Waypoints.Count == 0 && clone.Target is null,
            "all rejected attack-circle routes leave no hidden server movement");
        runtime.Navigation.FindPathOverride = null;
        runtime.Navigation.FindPathAroundUnitsOverride = null;
    }

    [Check("pets-live-yorick-ghouls-chase-and-hit-through-crowd")]
    public static void EachGhoulChasesAndHitsBehindLiveBodies()
    {
        foreach (var kind in new[] { "Spectral", "Ravenous", "Decayed" })
        {
            using var match = new Match("Yorick", Team.Order);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            runtime.Tick(10.1);
            owner.Position = new Position2(7350, 7000);
            owner.AttackTarget = null;
            owner.AttackCooldown = 999;
            match.Hero(1).Position = new Position2(12000, 12000);
            var target = new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
                UnitType.Minion, Team.Chaos, new Position2(7550, 7000))
            {
                Name = "GhoulPursuitTarget", MinionType = MinionType.Melee,
                Health = 5000, MaxHealth = 5000, Speed = 0, Radius = 35.75,
                Created = true, Visible = true,
            };
            runtime.World.Register(target);
            var blocker = new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
                UnitType.Minion, Team.Order, new Position2(7450, 7110))
            {
                Name = "GhoulPursuitBlocker", MinionType = MinionType.Melee,
                Health = 5000, MaxHealth = 5000, Speed = 0, Radius = 55.8,
                Created = true, Visible = true,
            };
            runtime.World.Register(blocker);
            runtime.Delivery.Flush();
            var ghoul = YorickScriptSupport.SpawnGhoul(runtime, owner, kind, new Position2(7200, 7000));
            var initialHealth = target.Health;
            var acquired = false;
            var closest = double.PositiveInfinity;
            var lastRoute = string.Empty;
            var lastDestination = "none";
            for (var tick = 0; tick < 100 && ghoul.Alive && target.Health >= initialHealth; tick++)
            {
                runtime.Tick(.05);
                acquired |= ghoul.AttackTarget == target.NetId;
                closest = Math.Min(closest, FearDistance(ghoul.Position, target.Position));
                lastRoute = string.Join(';', ghoul.Waypoints);
                if (runtime.Pets.LastDestinations.TryGetValue(ghoul.NetId, out var destination))
                    lastDestination = destination.ToString();
            }
            TestAssert.True(acquired, kind + " acquires the visible minion past live bodies");
            TestAssert.True(target.Health < initialHealth,
                kind + " reaches and hits that minion within its five-second life; " +
                $"ghoul={ghoul.Position} owner={owner.Position} target={target.Position} " +
                $"radius={ghoul.Radius:R} ownerRadius={owner.Radius:R} " +
                $"targetRadius={target.Radius:R} attackRange={ghoul.AttackRange:R} " +
                $"closest={closest:R} lastRoute={lastRoute} lastDestination={lastDestination} " +
                $"alive={ghoul.Alive} hp={ghoul.Health:R} targetHp={target.Health:R} " +
                $"time={runtime.Combat.CurrentTime:R} pendingHits={runtime.Combat.PendingHits.Count}");
        }
    }

    [Check("pets-live-yorick-ghoul-chase-keeps-long-routes")]
    public static void GhoulChaseDoesNotStreamShortRoutes()
    {
        // Playtest B2 YV10, 26 Sept 2026: chasing the exact reach point let a
        // faster ghoul finish its route every movement tick, then re-issue a
        // ~30-unit route on the next AI tick.
        using var match = new Match("Yorick", Team.Order);
        var runtime = match.Runtime;
        var owner = match.Hero(0);
        var enemy = match.Hero(1);
        owner.Position = new Position2(7100, 7450);
        enemy.Position = new Position2(6800, 7600);
        enemy.Speed = 250.0; // A slowed hero: the ghoul reaches attack range mid-chase.
        var ghoul = YorickScriptSupport.SpawnUnit(runtime, owner, "Ravenous", new Position2(6500, 7600),
            runtime.Combat.CurrentTime);
        TestAssert.True(ghoul.Speed > enemy.Speed + 50.0,
            $"the ghoul outruns the walking hero (ghoul {ghoul.Speed:R}, hero {enemy.Speed:R})");
        match.Move(1, new Position2(8300, 7600));
        runtime.Delivery.Flush();
        match.Clear();
        for (var tick = 0; tick < 20; tick++) runtime.Tick(.1);
        runtime.Delivery.Flush();

        TestAssert.True(enemy.Position.X > 7100, "the hero walks straight away from the ghoul");
        TestAssert.Equal((uint?)enemy.NetId, ghoul.AttackTarget, "the ghoul keeps chasing that hero");
        var routes = match.Captures[0].Packets
            .Where(packet => Opcode(packet) == 0xC1 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == ghoul.NetId)
            .Select(packet => Decode(packet).GetProperty("waypoints").EnumerateArray()
                .Select(point => new Position2(point[0].GetDouble(), point[1].GetDouble())).ToArray())
            .ToArray();
        var moving = routes.Where(route => route.Length > 1)
            .Select(route => route.Zip(route.Skip(1), (from, to) => FearDistance(from, to)).Sum())
            .Order().ToArray();
        var summary = string.Join(", ", moving.Select(length => length.ToString("F0")));
        // Restated in batch 229 (owner, 25 October 2026: retail re-sends a path whenever its script or native state asks, even if
        // worse): the count of re-sent routes and stops is not bounded. What B2 guards is the stub, a ~30-unit route re-issued on
        // every tick, so no moving route may be a stub.
        TestAssert.True(moving.Length > 0 && moving[0] > 50.0,
            $"no moving ghoul route is a stub under 50 units ({routes.Count(route => route.Length > 1)} routes): {summary}");
        TestAssert.True(moving.Length > 0 && moving[moving.Length / 2] > 100.0,
            $"median moving ghoul route exceeds 100 units: {summary}");
    }

    private static Entity SpawnHallucinateClone(Match match, Entity owner)
    {
        match.Cast(0, 3, owner);
        var runtime = match.Runtime;
        // Hallucinate's cast first installs a 0.25-second applicator; its
        // expiry creates the clone. Advance normal production frames through
        // both phases instead of assuming the cast alone spawns the pet.
        for (var tick = 0; tick < 120 && !runtime.World.Entities.Values.Any(unit =>
                 unit.Kind == EntityKind.ShacoClone && unit.OwnerNetId == owner.NetId); tick++)
            runtime.Tick(1.0 / 30.0);
        return runtime.World.Entities.Values.Single(unit =>
            unit.Kind == EntityKind.ShacoClone && unit.OwnerNetId == owner.NetId);
    }

    private static void SendOwnedPetOrder(Match match, Entity pet, byte order,
        Position2 destination, uint targetNetId = 0)
    {
        var request = new byte[22];
        request[0] = 0x75;
        request[5] = order;
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), pet.NetId);
        BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(6), (float)destination.X);
        BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(14), (float)destination.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(18), targetNetId);
        var accepted = false;
        match.Runtime.Session.Receive(1, 1, match.Runtime.Cipher.Encrypt(request), context =>
            accepted = match.Runtime.Dependencies.Gameplay(context));
        match.Runtime.Delivery.Flush();
        TestAssert.True(accepted, $"owned pet order {order} is accepted");
    }
}
