using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-audit-mobility-real-hallucinate-line-contact")]
    public static void RealHallucinateApplicatorCannotAbsorbLineSkillshots()
    {
        foreach (var champion in new[] { "LeeSin", "Nidalee" })
        {
            using var match = new Match(champion, Team.Order, enemyChampion: "Shaco");
            var caster = match.Hero(0);
            var shaco = match.Hero(1);
            shaco.Position = caster.Position + new Position2(300, 0);
            // The applicator's status change refreshes the stat block, which replaces the harness's 5000 maximum with Shaco's own: take the
            // refreshed value first so that the comparison measures damage, not the fixture being reset.
            match.Runtime.HeroStats.Refresh(shaco);
            var hp = shaco.Health;

            match.CastGround(0, shaco.Position);
            match.Cast(1, 3, shaco);
            Advance(match, .46);

            TestAssert.True(shaco.Buffs.Any(buff => buff.Name == "HallucinateApplicator" &&
                buff.Payload.Untargetable), "real Hallucinate is still in its untargetable window");
            TestAssert.Near(hp, shaco.Health, .001,
                $"{champion} Q cannot damage the vanished real Shaco");
            // 19 October 2026: the recast target (LeeSinState.QTarget) is the BlindMonkQOne mark buff of the script now.
            if (champion == "LeeSin")
                TestAssert.False(shaco.Buffs.Any(buff => buff.Name is "BlindMonkQOne" or "BlindMonkQOneChaos"),
                    "Sonic Wave does not establish a recast target on vanished Shaco");
        }
    }

    [Check("spells-audit-mobility-line-contact-skips-untargetable")]
    public static void LineContactSkipsUntargetableAndReachesNextEnemy()
    {
        foreach (var champion in new[] { "LeeSin", "Nidalee" })
        {
            using var match = new Match(champion, Team.Order, extraEnemy: true);
            var caster = match.Hero(0);
            var near = match.Hero(1);
            var far = match.Hero(2);
            near.Position = caster.Position + new Position2(180, 0);
            far.Position = caster.Position + new Position2(340, 0);
            match.Runtime.SpellBuffs.Add(near, "RegressionUntargetable", BuffType.Internal, 2,
                near.NetId, new BuffPayload { Untargetable = true });
            var nearHp = near.Health;
            var farHp = far.Health;

            match.CastGround(0, far.Position);
            Advance(match, 1);

            TestAssert.Near(nearHp, near.Health, .001, $"{champion} ignores the untargetable first body");
            TestAssert.True(far.Health < farHp, $"{champion} continues to the target behind it");
            if (champion == "LeeSin")
            {
                TestAssert.True(far.Buffs.Any(buff => buff.Name is "BlindMonkQOne" or "BlindMonkQOneChaos"),
                    "Sonic Wave marks the first eligible contact");
                TestAssert.False(near.Buffs.Any(buff => buff.Name is "BlindMonkQOne" or "BlindMonkQOneChaos"),
                    "Sonic Wave does not mark the untargetable body");
            }
        }
    }

    [Check("spells-audit-mobility-line-contact-preserves-stealth")]
    public static void LineContactStillHitsUnseenStealthedEnemy()
    {
        foreach (var champion in new[] { "LeeSin", "Nidalee" })
        {
            using var match = new Match(champion, Team.Order, extraEnemy: true);
            var caster = match.Hero(0);
            var near = match.Hero(1);
            var far = match.Hero(2);
            near.Position = caster.Position + new Position2(180, 0);
            far.Position = caster.Position + new Position2(340, 0);
            match.Runtime.SpellBuffs.Add(near, "RegressionStealth", BuffType.Invisibility, 2,
                near.NetId, new BuffPayload { Stealthed = true });
            var nearHp = near.Health;
            var farHp = far.Health;

            match.CastGround(0, far.Position);
            Advance(match, 1);

            TestAssert.True(near.Health < nearHp, $"{champion} skillshot reveals contact through stealth");
            TestAssert.Near(farHp, far.Health, .001, $"{champion} stops at the first eligible body");
        }
    }

    // 19 October 2026: Vayne plays from CharScriptVayne. Her Silver Bolts aura (the carrier of every mark) is added by CharOnLevelUpSpell when W reaches
    // level 1; this Match sets the ranks directly, so a check that needs the marks runs the module's rank-up hook the way SpellSystem.RankUp does.
    private static void LearnSilverBolts(Match match, params Entity[] vaynes)
    {
        foreach (var vayne in vaynes)
        {
            TestAssert.Equal(1, vayne.SpellRanks[1], "W is at its first rank (CharOnLevelUpSpell adds the aura at level 1)");
            match.Runtime.Dependencies.ChampionDispatcher.OnRankUp(match.Runtime.Spells, vayne, 1, 1);
            TestAssert.True(vayne.Buffs.Any(buff => buff.Name == "VayneSilveredBolts"), "the first W rank added the Silver Bolts aura");
        }
    }

    // The rings are the scripts' own effects: VayneSilverParticle1 creates vayne_W_ring1 and keeps its id in the instance variable GlobeOne, VayneSilveredDebuff
    // creates vayne_W_ring2 (second stack) and keeps its id in GlobeTwo (BBSpellEffectCreate EffectIDVar). The hand-written marks kept both ids in the buff payload.
    private static uint ScriptEffect(Match match, BuffState buff, string variable)
    {
        var module = (Amberline.Simulation.Spells.Bb.BbChampionModule)match.Runtime.Dependencies.ChampionDispatcher.FindModule("Vayne")!;
        return module.Buffs!.InstanceOf(buff)?.InstanceVars[variable] is double id ? (uint)id : 0u;
    }

    [Check("spells-audit-mobility-vayne-silver-bolts-mirror-ownership")]
    public static void OpposingVaynesKeepIndependentNeutralMarks()
    {
        using var match = new Match("Vayne", Team.Order, enemyChampion: "Vayne");
        var runtime = match.Runtime;
        var first = match.Hero(0);
        var second = match.Hero(1);
        var neutral = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Monster),
            EntityKind.Monster, UnitType.Monster, Team.Neutral,
            first.Position + new Position2(100, 100))
            { Name = "RegressionNeutral", MaxHealth = 5000, Health = 5000, Radius = 30, Armor = 0 });
        LearnSilverBolts(match, first, second);
        Advance(match, .02);

        void Hit(Entity vayne) => TestAssert.True(runtime.Combat.ApplyHit(vayne.NetId, neutral.NetId,
            10, DamageType.Physical, DamageSourceKind.Basic), "basic attack lands on neutral");
        int Stacks(Entity vayne) => neutral.Buffs.Single(buff =>
            buff.Name == "VayneSilveredDebuff" && buff.CasterNetId == vayne.NetId).Stacks;
        Hit(first);
        var firstRing = neutral.Buffs.Single(buff => buff.Name == "VayneSilverParticle1");
        var firstRingFx = ScriptEffect(match, firstRing, "GlobeOne");
        TestAssert.True(firstRingFx != 0, "the first stack's ring is a real effect");
        Hit(second);
        TestAssert.Equal(1, Stacks(first), "first Vayne keeps her initial mark");
        TestAssert.Equal(1, Stacks(second), "second Vayne starts her own mark");
        var rings = neutral.Buffs.Where(buff => buff.Name == "VayneSilverParticle1").ToArray();
        TestAssert.Equal(1, rings.Length, "the shipped replace-existing ring is shared");
        TestAssert.Equal(neutral.NetId, rings[0].CasterNetId, "the victim casts its shared ring");
        TestAssert.False(neutral.Buffs.Contains(firstRing), "second mark activation replaced the first ring");
        TestAssert.True(ScriptEffect(match, rings[0], "GlobeOne") is var secondRingFx && secondRingFx != 0 && secondRingFx != firstRingFx,
            "with a ring effect of its own");

        Hit(second);
        TestAssert.Equal(1, Stacks(first), "other caster does not advance first Vayne's mark");
        TestAssert.Equal(2, Stacks(second), "second Vayne advances only her mark");
        TestAssert.False(neutral.Buffs.Any(buff => buff.Name == "VayneSilverParticle1"),
            "second-stack activation removes the shared first ring");
        TestAssert.Near(4970, neutral.Health, .001, "second caster cannot proc on her second hit");

        Hit(second);
        TestAssert.Equal(1, Stacks(first), "first Vayne's mark survives the other's proc");
        TestAssert.False(neutral.Buffs.Any(buff => buff.Name == "VayneSilveredDebuff" &&
            buff.CasterNetId == second.NetId), "third hit consumes only its caster's mark");
        TestAssert.False(neutral.Buffs.Any(buff => buff.Name == "VayneSilverParticle1"),
            "third-hit deactivation leaves no shared first ring");
        TestAssert.Near(4760, neutral.Health, .001, "third hit applies the neutral-capped proc");
    }

    [Check("spells-audit-mobility-vayne-mirror-switch-and-expiry")]
    public static void VayneMirrorTargetSwitchAndExpiryStayCasterOwned()
    {
        using var match = new Match("Vayne", Team.Order, enemyChampion: "Vayne");
        var runtime = match.Runtime;
        var first = match.Hero(0);
        var second = match.Hero(1);
        var original = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Monster),
            EntityKind.Monster, UnitType.Monster, Team.Neutral,
            first.Position + new Position2(100, 100))
            { Name = "RegressionNeutralA", MaxHealth = 5000, Health = 5000, Radius = 30, Armor = 0 });
        var alternate = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Monster),
            EntityKind.Monster, UnitType.Monster, Team.Neutral,
            first.Position + new Position2(150, 150))
            { Name = "RegressionNeutralB", MaxHealth = 5000, Health = 5000, Radius = 30, Armor = 0 });
        LearnSilverBolts(match, first, second);
        Advance(match, .02);

        void Hit(Entity vayne, Entity victim) => TestAssert.True(runtime.Combat.ApplyHit(
            vayne.NetId, victim.NetId, 10, DamageType.Physical, DamageSourceKind.Basic),
            "mirror Vayne attack lands");
        bool Mark(Entity victim, Entity vayne) => victim.Buffs.Any(buff =>
            buff.Name == "VayneSilveredDebuff" && buff.CasterNetId == vayne.NetId);
        BuffState Ring(Entity victim) => victim.Buffs.Single(buff =>
            buff.Name == "VayneSilverParticle1" && buff.CasterNetId == victim.NetId);

        Hit(first, original);
        var firstRing = Ring(original);
        Hit(second, original);
        var secondRingFx = ScriptEffect(match, Ring(original), "GlobeOne");
        TestAssert.True(secondRingFx != 0, "shared ring owns a real particle effect");
        TestAssert.False(original.Buffs.Contains(firstRing), "second mark activation replaced the first ring");

        Hit(first, alternate);
        TestAssert.False(Mark(original, first), "switch removes only first caster's old mark");
        TestAssert.True(Mark(original, second), "switch retains second caster's mark");
        TestAssert.False(original.Buffs.Any(buff => buff.Name == "VayneSilverParticle1"),
            "first mark deactivation removes the shared target-owned ring");
        TestAssert.True(Mark(alternate, first), "switch starts first caster's new mark");

        Hit(first, original);
        TestAssert.False(Mark(alternate, first), "switching back removes first caster's alternate mark");
        TestAssert.True(Mark(original, first) && Mark(original, second),
            "switching back restores independent marks");
        TestAssert.True(ScriptEffect(match, Ring(original), "GlobeOne") is var latestRingFx && latestRingFx != 0 && latestRingFx != secondRingFx,
            "switching back creates one shared ring, with a new effect, for the latest first stack");

        Advance(match, 2);
        Hit(second, original); // Refresh second caster while first caster is aging out.
        var refreshed = original.Buffs.Single(buff => buff.Name == "VayneSilveredDebuff" &&
            buff.CasterNetId == second.NetId);
        var secondRingTwoFx = ScriptEffect(match, refreshed, "GlobeTwo");
        TestAssert.True(secondRingTwoFx != 0, "second stack owns its effect handle");
        match.Clear();
        Advance(match, 1.6);

        TestAssert.False(Mark(original, first), "first caster's unrefreshed mark expires");
        TestAssert.False(original.Buffs.Any(buff => buff.Name == "VayneSilverParticle1"),
            "second-stack activation already removed the shared first ring");
        var survivor = original.Buffs.Single(buff => buff.Name == "VayneSilveredDebuff" &&
            buff.CasterNetId == second.NetId);
        TestAssert.Equal(2, survivor.Stacks, "second caster's renewed stack survives");
        TestAssert.Equal(secondRingTwoFx, ScriptEffect(match, survivor, "GlobeTwo"),
            "first caster's expiry preserves second caster's effect handle");
        TestAssert.False(match.Captures[0].Packets.Any(packet => Opcode(packet) == FxPackets.KillOpcode && packet.Plaintext.Length >= 9 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == secondRingTwoFx),
            "and the effect is still alive");
    }

    [Check("spells-audit-mobility-vayne-shared-ring-deactivation")]
    public static void ExpiringVayneMarkRemovesSharedTargetRing()
    {
        using var match = new Match("Vayne", Team.Order, enemyChampion: "Vayne");
        var runtime = match.Runtime;
        var first = match.Hero(0);
        var second = match.Hero(1);
        var neutral = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Monster),
            EntityKind.Monster, UnitType.Monster, Team.Neutral,
            first.Position + new Position2(100, 100))
            { Name = "RegressionNeutral", MaxHealth = 5000, Health = 5000, Radius = 30, Armor = 0 });
        LearnSilverBolts(match, first, second);
        Advance(match, .02);
        TestAssert.True(runtime.Combat.ApplyHit(first.NetId, neutral.NetId,
            10, DamageType.Physical, DamageSourceKind.Basic), "first mark lands");
        Advance(match, 2);
        TestAssert.True(runtime.Combat.ApplyHit(second.NetId, neutral.NetId,
            10, DamageType.Physical, DamageSourceKind.Basic), "second mark lands later");
        var ring = neutral.Buffs.Single(buff => buff.Name == "VayneSilverParticle1");
        var ringFx = ScriptEffect(match, ring, "GlobeOne");
        TestAssert.Equal(neutral.NetId, ring.CasterNetId, "the victim casts the shared ring");
        TestAssert.True(ringFx != 0, "the ring effect has an owned effect ID");
        match.Clear();

        Advance(match, 1.6);

        TestAssert.False(neutral.Buffs.Any(buff => buff.Name == "VayneSilveredDebuff" &&
            buff.CasterNetId == first.NetId), "first Vayne's older mark expires");
        TestAssert.True(neutral.Buffs.Any(buff => buff.Name == "VayneSilveredDebuff" &&
            buff.CasterNetId == second.NetId), "second Vayne's newer mark remains");
        TestAssert.False(neutral.Buffs.Any(buff => buff.Name == "VayneSilverParticle1"),
            "deactivating the older mark removes the target-owned shared ring");
        TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == FxPackets.KillOpcode &&
            packet.Plaintext.Length >= 9 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == ringFx),
            "mark deactivation retires the shared ring's effect ID");
    }

    [Check("spells-audit-mobility-vayne-rooted-tumble-rejected")]
    public static void RootedVayneCannotStartTumble()
    {
        foreach (var control in new[] { BuffType.Snare, BuffType.Internal })
        {
            using var match = new Match("Vayne", Team.Order);
            var hero = match.Hero(0);
            var origin = hero.Position;
            var mana = hero.Mana;
            match.Runtime.SpellBuffs.Add(hero, "RegressionMovementLock", control, 2,
                match.Hero(1).NetId, new BuffPayload { MovementDisabled = control == BuffType.Internal });

            TestAssert.False(match.TryCastGround(0, 0, origin + new Position2(300, 0)),
                "rooted Tumble is rejected before commitment");
            Advance(match, .1);
            TestAssert.Near(origin.X, hero.Position.X, .001, "rejected Tumble does not move X");
            TestAssert.Near(origin.Z, hero.Position.Z, .001, "rejected Tumble does not move Z");
            TestAssert.True(hero.ForcedMove is null, "rejected Tumble creates no forced move");
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "VayneTumbleBonus"),
                "rejected Tumble does not arm an attack");
            TestAssert.Near(0, hero.SpellCooldowns[0], .001, "rejected Tumble spends no cooldown");
            TestAssert.True(hero.Mana >= mana, "rejected Tumble spends no mana");
            if (control == BuffType.Snare)
                TestAssert.True(match.TryCast(0, 2, match.Hero(1)), "Condemn stays legal while rooted");
            else
                TestAssert.True(match.TryCast(0, 3, hero), "Final Hour stays legal while rooted");
        }
    }

    [Check("spells-audit-mobility-vayne-lee-r-startup-root")]
    public static void LeeSinKickStartupRootRejectsNewTumble()
    {
        using var match = new Match("Vayne", Team.Order, enemyChampion: "LeeSin");
        var vayne = match.Hero(0);
        var lee = match.Hero(1);
        match.Cast(1, 3, vayne);
        TestAssert.True(vayne.Buffs.Any(buff => buff.Name == "BlindMonkRRoot" &&
            buff.Payload.MovementDisabled), "Dragon's Rage applies its startup movement lock");
        var mana = vayne.Mana;
        TestAssert.False(match.TryCastGround(0, 0, vayne.Position + new Position2(300, 0)),
            "new Tumble is rejected during the startup root");
        TestAssert.Near(mana, vayne.Mana, .001, "startup-root rejection spends no mana");
        TestAssert.True(vayne.ForcedMove is null, "rejected Q does not start Vayne movement");
    }

    [Check("spells-audit-mobility-vayne-preaccepted-tumble")]
    public static void RootAfterAcceptedTumbleDoesNotUndoItsAcceptance()
    {
        using var match = new Match("Vayne", Team.Order);
        var vayne = match.Hero(0);
        var origin = vayne.Position;
        match.CastGround(0, origin + new Position2(300, 0));
        var move = vayne.ForcedMove;
        TestAssert.True(move is not null, "Tumble was accepted before control arrived");
        match.Runtime.SpellBuffs.Add(vayne, "RegressionLateSnare", BuffType.Snare, 2,
            match.Hero(1).NetId, new BuffPayload { MovementDisabled = true });
        Advance(match, .1);
        TestAssert.True(vayne.Position.X > origin.X,
            "later root does not retroactively reject the already accepted Tumble");
        TestAssert.True(vayne.Buffs.Any(buff => buff.Name == "VayneTumbleBonus"),
            "preaccepted Tumble retains its armed bonus");
    }

    [Check("spells-audit-mobility-lethal-hit-does-not-mark-revived-life")]
    public static void LethalLineAndAreaHitsDoNotMarkYorickTemporaryLife()
    {
        foreach (var (champion, slot, mark) in new[]
        {
            ("LeeSin", 0, "BlindMonkQOne"),
            ("LeeSin", 2, "BlindMonkEOne"),
            ("Ezreal", 1, "EzrealEssenceFluxMissile"),
        })
        {
            using var match = new Match(champion, Team.Order, enemyChampion: "Yorick");
            var caster = match.Hero(0);
            var yorick = match.Hero(1);
            ArmYorickSelfRevival(match);
            yorick.Health = 20;
            match.CastGround(0, yorick.Position, slot);
            Advance(match, .7);

            TestAssert.True(yorick.YorickRevival is not null && yorick.Alive,
                $"{champion}/{slot} lethal hit enters Yorick's temporary life");
            TestAssert.False(yorick.Buffs.Any(buff => buff.Name == mark ||
                mark == "BlindMonkQOne" && buff.Name == "BlindMonkQOneChaos"),
                $"{champion}/{slot} does not attach an old-life mark to the new life");
            // 19 October 2026: no recast window against the new life: BlindMonkQManager removes itself when its marked unit has no mark.
            if (champion == "LeeSin" && slot == 0)
                TestAssert.False(caster.Buffs.Any(buff => buff.Name == "BlindMonkQManager") ||
                    caster.ActiveSpellNames.Count > 0 && caster.ActiveSpellNames[0] == "BlindMonkQTwo",
                    "lethal Q cannot expose a recast against the new life");
        }
    }

    [Check("spells-audit-mobility-lethal-condemn-does-not-push-revived-life")]
    public static void LethalCondemnDoesNotPushYorickTemporaryLife()
    {
        foreach (var lethalFromSilverBolts in new[] { false, true })
        {
            using var match = new Match("Vayne", Team.Order, enemyChampion: "Yorick");
            var vayne = match.Hero(0);
            var yorick = match.Hero(1);
            LearnSilverBolts(match, vayne);
            ArmYorickSelfRevival(match);
            if (lethalFromSilverBolts)
            {
                for (var hit = 0; hit < 2; hit++)
                    TestAssert.True(match.Runtime.Combat.ApplyHit(vayne.NetId, yorick.NetId,
                        10, DamageType.Physical, DamageSourceKind.Basic), "preload two Silver Bolts stacks");
                TestAssert.Equal(2, yorick.Buffs.Single(buff => buff.Name == "VayneSilveredDebuff").Stacks);
            }
            yorick.Health = 20;
            match.Cast(0, 2, yorick);
            Advance(match, .65);

            TestAssert.True(yorick.YorickRevival is not null && yorick.Alive,
                "the lethal Condemn interaction enters Yorick's temporary life");
            TestAssert.True(yorick.ForcedMove is null,
                "the old-life Condemn cannot knock back the reviving body");
            TestAssert.False(yorick.Buffs.Any(buff => buff.Name is "VayneCondemnMissile" or "Stun"),
                "the old-life Condemn cannot attach displacement or stun to the reviving body");
            TestAssert.Near(1, yorick.Health, .001,
                "later Condemn damage cannot consume the one-HP temporary life");
        }
    }

    [Check("spells-audit-mobility-lethal-condemn-wall-does-not-stun-revived-life")]
    public static void LethalCondemnWallHitDoesNotStunYorickTemporaryLife()
    {
        using var match = new Match("Vayne", Team.Order, enemyChampion: "Yorick");
        var vayne = match.Hero(0);
        var yorick = match.Hero(1);
        vayne.Position = new Position2(6913, 6835);
        yorick.Position = new Position2(6913, 6635);
        TestAssert.True(match.Runtime.Navigation.IsWalkable(vayne.Position) &&
            match.Runtime.Navigation.IsWalkable(yorick.Position), "captured wall setup is walkable");
        ArmYorickSelfRevival(match);
        yorick.Armor = 0;
        yorick.HealthRegen = 0;
        yorick.Health = 70; // First rank-one hit is 45; the wall hit is lethal.
        match.Cast(0, 2, yorick);
        Advance(match, .9);

        TestAssert.True(yorick.YorickRevival is not null && yorick.Alive,
            "wall damage enters Yorick's temporary life");
        TestAssert.True(yorick.ForcedMove is null,
            "the completed Condemn move is released before the new life");
        TestAssert.False(yorick.Buffs.Any(buff => buff.Name == "Stun"),
            "the old-life wall stun cannot attach to Yorick's reviving body");
    }

    private static void ArmYorickSelfRevival(Match match)
    {
        var yorick = match.Hero(1);
        yorick.HealthRegen = 0;
        match.Cast(1, 3, yorick);
        Advance(match, .5);
        TestAssert.True(yorick.Buffs.Any(buff => buff.Name == "YorickReviveAllySelf"),
            "Yorick's real self-R revival marker is armed");
        foreach (var clone in match.Runtime.World.Entities.Values.Where(unit =>
                     unit.Kind == EntityKind.YorickRevenant && unit.Alive))
        {
            clone.AttackTarget = null;
            // Restated in batch 263: inside Pet.lua's 2000-unit leash and off the casts' lines. At (11000, 11000) the leash blink
            // (TimerScanDistance's SetActorPositionFromObject) put the revenant on Yorick: on his exact point it lost the line
            // missile's tie to him, on the blink's one-point path anchor (now seated at once) it stands a unit closer and takes the hit.
            clone.Position = yorick.Position + new Position2(0, 1600);
            // The old machines parked a revenant with an Order 11 in its PetState and in its
            // controller buff; Pet.lua holds that order in its own AI state now (wire 11 is
            // ORDER_PETHARDSTOP -> AI_PET_HARDSTOP) and the controller buff carries no order.
            TestAssert.True(match.Runtime.Pets.Command(yorick, 11, clone.Position, actorNetId: clone.NetId),
                "the revenant takes the hard stop");
        }
    }
}
