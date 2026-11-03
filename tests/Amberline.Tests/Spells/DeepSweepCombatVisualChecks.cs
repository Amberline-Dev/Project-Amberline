using System.Buffers.Binary;
using System.Text;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-vayne-b5-tumble-movement-buff")]
    public static void VayneTumblePublishesAndRetiresMovementBuff()
    {
        foreach (var ending in new[] { "landing", "interruption", "death" })
        {
            using var match = new Match("Vayne", Team.Order);
            var runtime = match.Runtime; var hero = match.Hero(0);
            match.CastGround(0, hero.Position + new Position2(300, 0));
            var buff = hero.Buffs.Single(buff => buff.Name == "VayneTumble");
            TestAssert.Equal(BuffType.Internal, buff.Type);
            TestAssert.Near(.5, buff.ExpiresAt, 1e-9, "VAY-04: nominal script duration");
            // Batch 181: VayneTumbleBonus carries the same wire name (its BuffName is VayneTumble), so the movement buff is told apart by its slot.
            var hash = SpellPackets.BuffNameHash("VayneTumble");
            var slot = buff.Slot;
            TestAssert.Equal(1, match.Captures[0].Packets.Count(packet => Opcode(packet) == SpellPackets.BuffAddOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[9..]) == hash && packet.Plaintext.Span[5] == slot));
            TestAssert.False(match.Captures[0].Packets.Any(packet => IsNamedVisual(packet, "Global_Haste")),
                "VAY-04: native hand effects have no explicit duplicate");
            if (ending == "death") runtime.Combat.Kill(hero, match.Hero(1).NetId);
            else if (ending == "interruption")
            {
                runtime.Movement.StartForcedMove(hero.NetId, hero.Position + new Position2(0, 300),
                    500, 1, match.Hero(1).NetId, "Replacement");
                var replacement = hero.ForcedMove;
                // 19 October 2026: the script engine notices the replaced dash in its own step (BbScriptMoves.Tick, the shared projectile phase), and
                // VayneTumble.BuffOnMoveEnd removes the buff; the hand-written module did it in TickLateChampions.
                runtime.Spells.TickSharedProjectilePhase(0);
                TestAssert.True(ReferenceEquals(replacement, hero.ForcedMove), "foreign move remains owned");
            }
            else runtime.Tick(.4);
            runtime.Delivery.Flush();
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "VayneTumble"), "VAY-04: remove on " + ending);
            TestAssert.Equal(1, match.Captures[0].Packets.Count(packet => Opcode(packet) == 0x7F &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == hash && packet.Plaintext.Span[5] == slot));
        }
    }

    [Check("spells-live-deep-stasis-line-pass-through")]
    public static void StasisBodiesDoNotSwallowFreshLineMissiles()
    {
        foreach (var champion in new[] { "LeeSin", "Nidalee" })
        foreach (var item in new[] { 3026, 3157 })
        {
            using var match = new Match(champion, Team.Order, extraEnemy: true);
            var runtime = match.Runtime;
            var caster = match.Hero(0);
            var body = match.Hero(1);
            var rear = match.Hero(2);
            body.Position = caster.Position + new Position2(350, 0);
            rear.Position = caster.Position + new Position2(700, 0);
            body.Inventory = [new InventorySlot(item), null, null, null, null, null];
            // The copy binds to its slot on the next item poll (BbItemModule.SyncInventory), the way a real buy does;
            // Items/3026.luaobj's UpdateSelfBuffActions then opens the Guardian Angel revive (Zhonya's own active is
            // the cast on slot 4 below).
            runtime.Tick(0.01);
            if (item == 3026)
            {
                body.Health = 1;
                runtime.Combat.ApplyHit(caster.NetId, body.NetId, 100, DamageType.Physical,
                    DamageSourceKind.Spell);
            }
            else match.Cast(1, 4, body);
            TestAssert.True(body.Buffs.Any(buff => buff.Payload.Untargetable),
                $"{champion}/{item}: protected body is untargetable");
            var before = rear.MatchStatistics.TotalDamageTaken;
            match.CastGround(0, rear.Position);
            for (var tick = 0; tick < 30; tick++) runtime.Tick(.05);
            TestAssert.True(rear.MatchStatistics.TotalDamageTaken > before,
                $"{champion}/{item}: line missile reaches the enemy behind stasis");
        }
    }

    [Check("spells-live-deep-stasis-targetability")]
    public static void ItemStasisCarriesExplicitUntargetability()
    {
        foreach (var item in new[] { 3026, 3157 })
        {
            using var match = new Match("Annie", Team.Order);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var victim = match.Hero(1);
            victim.Inventory = [new InventorySlot(item), null, null, null, null, null];
            // The same copy-bind tick (batch 172; batch 149 step 5 named it for the other item checks).
            runtime.Tick(0.01);
            if (item == 3026)
            {
                victim.Health = 1;
                TestAssert.True(runtime.Combat.ApplyHit(owner.NetId, victim.NetId, 100,
                    DamageType.Physical, DamageSourceKind.Spell));
            }
            else match.Cast(1, 4, victim);
            var name = item == 3026 ? "GuardianAngel" : "ZhonyasRingShield";
            TestAssert.True(victim.Buffs.Single(buff => buff.Name == name).Payload.Untargetable,
                $"{name} excludes the body from fresh line acquisition");
            runtime.Tick(item == 3026 ? 4.1 : 2.6);
            TestAssert.False(victim.Buffs.Any(buff => buff.Payload.Untargetable),
                $"{name} expires without leaving targetability disabled");
        }
    }

    [Check("spells-live-deep-item-area-untargetable")]
    public static void ItemAreasSkipFreshUntargetableVictims()
    {
        foreach (var item in new[] { 3068, 3077 })
        {
            using var match = new Match("Annie", Team.Order, extraEnemy: true);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var target = match.Hero(1);
            var victim = match.Hero(2);
            target.Position = owner.Position + new Position2(100, 0);
            victim.Position = owner.Position + new Position2(130, 0);
            owner.Inventory = [new InventorySlot(item), null, null, null, null, null];
            runtime.HeroStats.Refresh(owner, push: false);
            runtime.SpellBuffs.Add(victim, "HallucinateProbe", BuffType.Internal, 1.0,
                victim.NetId, new BuffPayload { Untargetable = true },
                canMitigateDuration: false);
            var before = victim.MatchStatistics.TotalDamageTaken;
            if (item == 3068) runtime.Tick(.05);
            else runtime.Combat.ApplyHit(owner.NetId, target.NetId,
                owner.AttackDamage, DamageType.Physical, DamageSourceKind.Basic);
            TestAssert.Equal(before, victim.MatchStatistics.TotalDamageTaken,
                $"item {item} does not freshly acquire an untargetable victim");
            TestAssert.True(target.MatchStatistics.TotalDamageTaken > 0,
                $"item {item} still hits an eligible nearby target");
        }
    }

    [Check("spells-live-deep-lee-e-effects-animation")]
    public static void LeeTempestAndCripplePublishExplicitVisuals()
    {
        using var match = new Match("LeeSin", Team.Order);
        var runtime = match.Runtime;
        var lee = match.Hero(0);
        var victim = match.Hero(1);
        victim.Position = lee.Position + new Position2(180, 0);
        match.Cast(0, 2, lee);
        runtime.Tick(.3);
        runtime.Delivery.Flush();
        foreach (var name in new[] { "blindMonk_E_cas", "blindMonk_thunderCrash_impact_unit_tar",
                     "blindMonk_E_thunderCrash_tar", "blindMonk_E_thunderCrash_unit_tar_blood" })
            TestAssert.True(match.Captures[0].Packets.Any(packet => IsNamedVisual(packet, name)),
                $"Lee E1 emits {name}");
        // E1 publishes its short recast cooldown when the delayed impact
        // resolves. Advance four source frames so the accepted E2 follows
        // that 0.1-second gate instead of racing it on the same tick.
        for (var frame = 0; frame < 4; frame++) runtime.Tick(1.0 / 30.0);
        match.Clear();
        match.Cast(0, 2, lee);
        runtime.Tick(.3);
        runtime.Delivery.Flush();
        TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xB8 &&
            Encoding.ASCII.GetString(packet.Plaintext.Span[13..^1]) == "Spell2b"),
            "Lee E2 requests Spell2b");
        match.Clear();
        runtime.Tick(.6);
        runtime.Delivery.Flush();
        TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x2B),
            "Lee E2 unlocks the gesture after its half-second self buff");
    }

    [Check("spells-live-deep-nidalee-trap-setup")]
    public static void BushwhackAnimatesTheSpawnedTrap()
    {
        using var match = new Match("Nidalee", Team.Order);
        var owner = match.Hero(0);
        match.Hero(1).Position = owner.Position + new Position2(1200, 0);
        match.CastGround(0, owner.Position + new Position2(240, 0), slot: 1);
        match.Runtime.Tick(1.0);
        match.Runtime.Delivery.Flush();
        var trap = match.Runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.NidaleeTrap);
        var packets = match.Captures[0].Packets;
        var spawn = packets.FindIndex(packet => Opcode(packet) == 0x80 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == trap.NetId);
        var animations = packets.Select((packet, index) => (packet, index))
            .Where(entry => Opcode(entry.packet) == 0xB8 &&
                BinaryPrimitives.ReadUInt32LittleEndian(entry.packet.Plaintext.Span[1..]) == trap.NetId)
            .ToArray();
        TestAssert.Equal(1, animations.Length, "trap has exactly one setup animation");
        TestAssert.True(spawn >= 0 && animations[0].index > spawn,
            "trap template precedes its actor-owned Spell1 animation");
        var bytes = animations[0].packet.Plaintext.Span;
        TestAssert.Equal("Spell1", Encoding.ASCII.GetString(bytes[13..^1]));
        TestAssert.Equal(0x248u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..]));
        TestAssert.Near(1.0, BinaryPrimitives.ReadSingleLittleEndian(bytes[9..]), 0);
    }

    [Check("spells-live-deep-annie-w-shield-particle")]
    public static void IncinerateShowsItsTargetParticleThroughSpellShield()
    {
        using var match = new Match("Annie", Team.Order);
        var runtime = match.Runtime;
        var annie = match.Hero(0);
        var victim = match.Hero(1);
        victim.Position = annie.Position + new Position2(200, 0);
        runtime.SpellBuffs.Add(victim, "BansheesVeil", BuffType.Aura, 10,
            victim.NetId, canMitigateDuration: false);
        match.CastDirected(0, 1, annie.Position, victim.Position);
        runtime.Tick(.5);
        runtime.Delivery.Flush();
        TestAssert.True(match.Captures[0].Packets.Any(packet =>
            IsNamedVisual(packet, "Incinerate_buf")),
            "Incinerate's target effect survives spell-shield absorption");
    }

    [Check("spells-live-w18-tibbers-aura-leaves-a-spell-shield-up")]
    public static void TibbersAuraLeavesABansheesVeilUp()
    {
        // InfernalGuardianBurning.luaobj sets DoesntTriggerSpellCasts and no DoesntBreakShields, and the third branch of BuffOnBeingSpellHit
        // pops only a spell that does not set DoesntTriggerSpellCasts: the aura's ticks leave the veil up (owner decision, 18 October 2026).
        // The victim stands 300 units from the landing point, inside the aura (350) and outside the burst (250.3).
        using var match = new Match("Annie", Team.Order);
        var runtime = match.Runtime;
        var annie = match.Hero(0);
        var victim = match.Hero(1);
        var landing = annie.Position + new Position2(500, 0);
        victim.Position = landing + new Position2(300, 0);
        runtime.SpellBuffs.Add(victim, "BansheesVeil", BuffType.Aura, 10,
            victim.NetId, canMitigateDuration: false);
        var health = victim.Health;
        match.CastGround(0, landing, slot: 3);
        runtime.Tick(.5);
        TestAssert.True(runtime.World.Entities.Values.Any(unit => unit.Kind == EntityKind.Pet && unit.OwnerNetId == annie.NetId),
            "Tibbers was summoned");
        TestAssert.True(victim.Buffs.Any(buff => buff.Name == "BansheesVeil") && victim.Health == health,
            "the burst does not reach the veiled hero");
        runtime.Tick(1.5);
        TestAssert.True(victim.Health < health, "the aura ticked");
        TestAssert.True(victim.Buffs.Any(buff => buff.Name == "BansheesVeil"), "and left the veil up");
    }

    [Check("spells-live-deep-frost-tibbers-aura-replay")]
    public static void FrostTibbersAuraUsesOneSkinEffectForBirthAndReplay()
    {
        using var match = new Match("Annie", Team.Order, skinId: 5);
        var runtime = match.Runtime;
        var annie = match.Hero(0);
        match.CastGround(0, annie.Position + new Position2(200, 0), slot: 3);
        runtime.Tick(.5);
        runtime.Delivery.Flush();
        var tibbers = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.Pet &&
            unit.OwnerNetId == annie.NetId);
        TestAssert.True(tibbers.Buffs.Any(unit => unit.Name == "InfernalGuardianBurning"), "Tibbers carries the burning buff");
        // 19 October 2026: the aura effect is created by InfernalGuardianBurning.OnBuffActivate (BBSpellEffectCreate, id kept in the
        // buff's InstanceVars for OnBuffDeactivate), no longer by BuffEffectService's table: the script instance owns the one effect.
        var instance = ((BbChampionModule)runtime.Dependencies.ChampionDispatcher.FindModule("Annie")!).Buffs!.Instances
            .Single(item => item.Name == "InfernalGuardianBurning" && ReferenceEquals(item.Owner, tibbers));
        TestAssert.True(instance.InstanceVars["a"] is not null, "aura owns one persistent effect");
        TestAssert.True(match.Captures[0].Packets.Any(packet =>
            IsNamedVisual(packet, "SunfireCape_Aura_frost")), "initial frost aura");
        match.Clear();
        runtime.Session.Participants[0].View.Reset();
        runtime.Delivery.Flush();
        TestAssert.True(match.Captures[0].Packets.Any(packet =>
            IsNamedVisual(packet, "SunfireCape_Aura_frost")), "replayed frost aura");
        TestAssert.False(match.Captures[0].Packets.Any(packet =>
            IsNamedVisual(packet, "SunfireCapeAura_tar")), "no base aura on replay");
    }

    [Check("spells-live-deep-vayne-q-explicit-particles")]
    public static void VayneTumbleSelectsExplicitParticlesWithoutHandEffectDuplication()
    {
        foreach (var finalHour in new[] { false, true })
        {
            using var match = new Match("Vayne", Team.Order);
            var vayne = match.Hero(0);
            if (finalHour) match.Cast(0, 3, vayne);
            match.Clear();
            match.CastGround(0, vayne.Position + new Position2(300, 0));
            var names = finalHour
                ? new[] { "vayne_ult_invis_cas", "vayne_ult_invis_cas_02" }
                : new[] { "vayne_q_cas", "vayne_ult_invis_cas_02" };
            foreach (var name in names)
                TestAssert.Equal(1, match.Captures[0].Packets.Count(packet =>
                    IsNamedVisual(packet, name)), $"Q branch emits {name} once");
            foreach (var name in finalHour
                         ? new[] { "vayne_q_cas" }
                         : new[] { "vayne_ult_invis_cas" })
                TestAssert.False(match.Captures[0].Packets.Any(packet => IsNamedVisual(packet, name)),
                    $"Q branch does not emit {name}");
        }
    }

    [Check("items-live-deep-targeted-missile-late-sight")]
    public static void TargetedItemsRefreshInFlightMissilesForLateSight()
    {
        foreach (var item in new[] { 3128, 3146 })
        {
            using var match = new Match("Annie", Team.Order);
            var runtime = match.Runtime;
            var caster = match.Hero(0);
            var target = match.Hero(1);
            caster.Inventory = [new InventorySlot(item), null, null, null, null, null];
            target.Position = caster.Position + new Position2(640, 0);
            match.Cast(0, 4, target);
            var lateCapture = new Capture();
            var late = new ReplicationObserver(90, caster.Team, caster.NetId, lateCapture);
            late.View.Known.UnionWith(runtime.Session.Participants.Select(participant => participant.Hero!.NetId));
            runtime.Delivery.AddObserver(late);
            runtime.Delivery.Flush();
            lateCapture.Packets.Clear();
            runtime.Tick(1.0 / 30.0);
            runtime.Delivery.Flush();
            TestAssert.True(lateCapture.Packets.Any(packet => Opcode(packet) == 0x41),
                $"late observer receives the live item {item} missile before impact");
        }
    }
}
