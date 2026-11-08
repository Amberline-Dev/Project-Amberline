using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-yorick-b6-w-child-cast")]
    public static void YorickDecayedStartsItsNormalSummonCast()
    {
        AssertB6CastTrack("YorickSummonDecayed", 0, 0, 2, "BrandPOF_cas.troy", "r_hand");
        AssertB6CastTrack("YorickSummonDecayed", 1, 0, 2, "BrandPOF_cas.troy", "L_hand");
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var death in new[] { false, true })
        {
            using var match = new Match("Yorick", team);
            var hero = match.Hero(0);
            match.CastGround(0, hero.Position + new Position2(100, 0), 1);
            foreach (var capture in match.Captures)
            {
                TestAssert.Equal(2, capture.Packets.Count(packet => Opcode(packet) == 0xBD));
                AssertB6ChildCast(capture.Packets.Single(packet => IsB6Child(packet, 0x00556F54)),
                    hero, 0x00556F54, 42, .25, 0, 0x04);
                TestAssert.False(capture.Packets.Any(packet => Opcode(packet) is 0xB8 or 0x41),
                    "native summon cast owns the animation and has no missile");
            }
            SpellStep(match, .1);
            if (death) match.Runtime.Combat.Kill(hero, match.Hero(1).NetId);
            SpellStep(match, .14);
            TestAssert.False(match.Runtime.World.Entities.Values.Any(unit => unit.Kind == EntityKind.YorickGhoul),
                "no summon before the existing .25-second windup");
            SpellStep(match, .02); SpellStep(match, .1); match.Runtime.Delivery.Flush();
            var ghouls = match.Runtime.World.Entities.Values.Where(unit => unit.Kind == EntityKind.YorickGhoul).ToArray();
            TestAssert.Equal(death ? 0 : 1, ghouls.Length, "death cancels; completion summons exactly once");
            if (!death)
                foreach (var capture in match.Captures)
                    TestAssert.Equal(1, capture.Packets.Count(packet => Opcode(packet) == 0x80 &&
                        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == ghouls[0].NetId),
                        "one native ghoul creation per observer");
        }
    }

    [Check("spells-leesin-b6-e2-native-hand-buff")]
    public static void LeeE2PublishesCanonicalSelfBuffAndRetiresIt()
    {
        // 19 October 2026: the "cast" and "death" endings are gone. They pinned the hand-written module, which retired the E2 self buff at the next
        // cast and at its caster's death; BlindMonkETwo.luaobj has no such rule (the half second ends the buff) and sets PersistsThroughDeath.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var ending in new[] { "expiry" })
        {
            using var match = new Match("LeeSin", team, ally: true);
            var runtime = match.Runtime; var lee = match.Hero(0);
            match.Cast(0, 2, lee); SpellStep(match, .4);
            runtime.Combat.CombatTick(2, regenerate: false);
            match.Clear(); match.Cast(0, 2, lee);
            var buff = lee.Buffs.Single(candidate => candidate.Name == "BlindMonkETwo");
            foreach (var capture in match.Captures)
            {
                // BlindMonkETwo.luaobj SelfExecute[5] adds the buff under its binding name, and the add carries that name (Batch 357: binding stem; the script's BuffName
                // RaiseMorale is Gangplank's script, so it is never sent.
                var added = capture.Packets.Single(packet => Opcode(packet) == 0xBF &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[9..]) == SpellPackets.BuffNameHash("BlindMonkETwo"));
                var bytes = added.Plaintext.Span;
                TestAssert.Equal(lee.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]));
                TestAssert.Equal(lee.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]));
                TestAssert.Equal((byte)BuffType.Internal, bytes[6]);
                // Batch 365 (wire grace): the second cast renews the live copy, and a renewal-type add (BUFF_RENEW/REPLACE/STACKS_AND_RENEWS) carries one step of margin on its wire duration.
                TestAssert.Near((float)(.5 + Amberline.Simulation.Spells.Bb.BbBuffRuntime.WireGraceStep), BinaryPrimitives.ReadSingleLittleEndian(bytes[17..]), 0);
                TestAssert.False(capture.Packets.Any(packet => Opcode(packet) == 0x8C &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == 0x0DA97A01u),
                    "native hand slots must not gain explicit copies");
            }
            match.Clear();
            if (ending == "expiry")
            {
                // 19 October 2026: the script buff expires in the buff lifecycle (a full runtime tick), not in the spell step the module ticked it in.
                runtime.Tick(.49);
                TestAssert.True(lee.Buffs.Contains(buff), "E2 self buff survives until its half-second deadline");
                runtime.Tick(.02);
                // Batch 365 (wire grace): the expiry's remove is held one step and goes out at the end of the step after the expiry.
                runtime.Tick(1.0 / 30.0);
            }
            runtime.Delivery.Flush();
            TestAssert.False(lee.Buffs.Contains(buff), ending + " retires E2 self buff");
            foreach (var capture in match.Captures)
            {
                var removed = capture.Packets.Single(packet => Opcode(packet) == 0x7F &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == SpellPackets.BuffNameHash("BlindMonkETwo"));
                TestAssert.Equal(lee.NetId, BinaryPrimitives.ReadUInt32LittleEndian(removed.Plaintext.Span[1..]));
                TestAssert.Equal((byte)buff.Slot, removed.Plaintext.Span[5]);
                TestAssert.False(capture.Packets.Any(packet => Opcode(packet) == 0x8C &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == 0x0DA97A01u));
            }
        }
    }

    [Check("spells-vayne-b6-silver-bolts-unbound-flash")]
    public static void SilverBoltsFlashIsUnboundFromAttackAndCondemn()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var condemn in new[] { false, true })
        foreach (var lethal in new[] { false, true })
        {
            using var match = new Match("Vayne", team, extraEnemy: true);
            var runtime = match.Runtime; var vayne = match.Hero(0); var target = match.Hero(1);
            LearnSilverBolts(match, vayne);
            for (var hit = 0; hit < 2; hit++)
                TestAssert.True(runtime.Combat.ApplyHit(vayne.NetId, target.NetId, 1,
                    DamageType.True, DamageSourceKind.Basic));
            runtime.Delivery.Flush();
            foreach (var capture in match.Captures)
                foreach (var hash in new[] { 0x01F8CFB1u, 0x01F8CFB2u })
                {
                    var ring = capture.Packets.Single(packet => Opcode(packet) == 0x8C &&
                        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == hash);
                    TestAssert.Equal(target.NetId, BinaryPrimitives.ReadUInt32LittleEndian(ring.Plaintext.Span[29..]),
                        "Silver Bolts rings remain victim-bound");
                }
            match.Clear();
            var impact = target.Position;
            if (lethal) target.Health = 1;
            if (condemn)
            {
                match.Cast(0, 2, target); SpellStep(match, .25); SpellStep(match, .5);
            }
            else TestAssert.True(runtime.Combat.ApplyHit(vayne.NetId, target.NetId, 1,
                DamageType.True, DamageSourceKind.Basic));
            runtime.Delivery.Flush();
            TestAssert.Equal(!lethal, target.Alive, "third-hit outcome");
            foreach (var capture in match.Captures)
            {
                // VayneSilveredBolts and VayneCondemnMissile: Nothing / Target.
                var flash = capture.Packets.Single(packet => Opcode(packet) == 0x8C &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == 0x0B11FAC2u);
                var bytes = flash.Plaintext.Span;
                TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[29..]));
                TestAssert.Equal(target.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]));
                TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
                TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]));
                var grid = runtime.Navigation;
                TestAssert.Equal((short)Math.Truncate((impact.X - (int)((int)(grid.MinX + grid.MaxX) / 2.0)) / 2),
                    BinaryPrimitives.ReadInt16LittleEndian(bytes[33..]), "sampled impact X");
                TestAssert.Equal((short)Math.Truncate((impact.Z - (int)((int)(grid.MinZ + grid.MaxZ) / 2.0)) / 2),
                    BinaryPrimitives.ReadInt16LittleEndian(bytes[39..]), "sampled impact Z");
            }
        }
    }

    [Check("spells-janna-b6-persistent-target-bones-and-clone-privacy")]
    public static void ZephyrAndCloneMarkerReplayBothAttachmentBones()
    {
        foreach (var champion in new[] { "Janna", "Shaco" })
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match(champion, team, ally: true);
            var runtime = match.Runtime; var hero = match.Hero(0);
            var owner = champion == "Shaco" ? SpawnHallucinateClone(match, hero) : hero;
            // SowTheWind is added by the character script's UpdateSelfBuffActions, which runs in the spell system's tick.
            if (champion == "Janna") { runtime.Spells.Tick(.01); runtime.Spells.TickLateChampions(.01); }
            runtime.Delivery.Flush();
            AssertAttachments("initial");
            match.Clear();
            foreach (var participant in runtime.Session.Participants) participant.View.Reset();
            runtime.Delivery.Flush();
            AssertAttachments("reconnect");

            void AssertAttachments(string phase)
            {
                var hash = champion == "Janna" ? 0x0AA4B2C6u : 0x0C156CD9u;
                var bone = champion == "Janna" ? 0x0006EB74u : 0x00079664u;
                for (var index = 0; index < match.Captures.Count; index++)
                {
                    var packets = match.Captures[index].Packets.Where(packet => Opcode(packet) == 0x8C &&
                        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == hash).ToArray();
                    var allowed = champion == "Janna" || runtime.Session.Participants[index].Team == team;
                    TestAssert.Equal(allowed ? 1 : 0, packets.Length, champion + "/" + phase + " recipient");
                    foreach (var packet in packets)
                    {
                        var bytes = packet.Plaintext.Span;
                        TestAssert.Equal(bone, BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]), "script target bone");
                        TestAssert.Equal(bone, BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]), "script bind bone");
                        TestAssert.Equal(owner.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]));
                        TestAssert.Equal(owner.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[29..]));
                    }
                }
            }
        }
    }

    [Check("spells-leesin-b5-script-particles")]
    public static void LeeScriptParticlesSurviveLethalHits()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var branch in new[] { "q1", "q2", "e", "r" })
        {
            using var match = new Match("LeeSin", team, extraEnemy: branch == "r");
            var lee = match.Hero(0); var victim = match.Hero(1);
            var origin = lee.Position;
            if (branch == "q2")
            {
                match.Cast(0, 0, victim); SpellStep(match, .25); SpellStep(match, .5);
                match.Runtime.Delivery.Flush(); match.Clear();
                match.Cast(0, 0, victim);
                match.Runtime.Delivery.Flush();
                foreach (var capture in match.Captures)
                {
                    var burst = capture.Packets.Single(packet => IsNamedVisual(packet, "blindMonk_Q_resonatingStrike_02"));
                    TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(burst.Plaintext.Span[29..]),
                        "LEE-05: Q2 departure burst is unbound");
                    var position = Decode(burst).GetProperty("groups")[0].GetProperty("effects")[0]
                        .GetProperty("position_compressed");
                    var grid = match.Runtime.Navigation;
                    var expected = FxPackets.CompressPosition(origin,
                        new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ), (x, z) => grid.HeightAt(new Position2(x, z)));
                    TestAssert.Near(expected.X, position[0].GetDouble(), 0);
                    TestAssert.Near(expected.Z, position[2].GetDouble(), 0);
                }
                match.Clear(); victim.Health = 1;
                match.Runtime.Movement.Tick(.5); SpellStep(match, 0);
            }
            else if (branch == "r")
            {
                var secondary = match.Hero(2);
                secondary.Position = victim.Position + new Position2(150, 0);
                secondary.Health = 1;
                // 19 October 2026: the secondary strike is BlindMonkRKick's BuffOnUpdateActions, which runs when the kicked unit's buffs tick, so the
                // kick is stepped by the whole runtime (the spell system alone does not tick buffs).
                // Batch 175: the bare hook's first line was one ActionsUpdateInterval after its add, so the sweep landed 0.25 s after the kick
                // buff existed — the cast's 0.25 s windup plus that line. Batch 720: it runs on the kicked unit's next 0.25 s pass, 0 to 0.25 s
                // after the add, inside the same 0.6 s.
                match.Cast(0, 3, victim); match.Runtime.Tick(.25); match.Runtime.Tick(.35);
                victim = secondary;
            }
            else
            {
                victim.Health = 1;
                match.Cast(0, branch == "e" ? 2 : 0, victim);
                SpellStep(match, .25); SpellStep(match, .5);
            }
            match.Runtime.Delivery.Flush();
            TestAssert.False(victim.Alive, branch + " victim dies");
            var names = branch switch
            {
                "r" => new[] { "blind_monk_ult_unit_impact" },
                "e" => new[] { "blindMonk_thunderCrash_impact_unit_tar", "blindMonk_E_thunderCrash_tar",
                    "blindMonk_E_thunderCrash_unit_tar_blood" },
                "q2" => new[] { "blindMonk_Q_resonatingStrike_tar", "blindMonk_Q_resonatingStrike_tar_blood",
                    "blindmonk_resonatingstrike_tar_sound" },
                _ => new[] { "blindMonk_Q_resonatingStrike_tar", "blindMonk_Q_resonatingStrike_tar_blood" },
            };
            foreach (var capture in match.Captures)
            {
                foreach (var name in names)
                {
                    var effect = capture.Packets.Single(packet => IsNamedVisual(packet, name));
                    TestAssert.Equal(victim.NetId, BinaryPrimitives.ReadUInt32LittleEndian(effect.Plaintext.Span[29..]),
                        "LEE-05: lethal impact remains bound to the hit victim");
                    if (branch is "q1" or "r")
                    {
                        var damageIndex = capture.Packets.FindIndex(packet => Opcode(packet) == 0x68 &&
                            Decode(packet).GetProperty("target_netid").GetUInt32() == victim.NetId);
                        TestAssert.True(damageIndex > capture.Packets.IndexOf(effect),
                            "script creates the transient impact before lethal damage");
                    }
                }
                if (branch == "e")
                    foreach (var name in new[] { "blindMonk_thunderCrash_impact_cas", "blindMonk_thunderCrash_impact_02" })
                    {
                        var effect = capture.Packets.Single(packet => IsNamedVisual(packet, name));
                        TestAssert.Equal(lee.NetId, BinaryPrimitives.ReadUInt32LittleEndian(effect.Plaintext.Span[29..]));
                        TestAssert.Equal(SpellPackets.BuffNameHash("C_BUFFBONE_GLB_CHEST_LOC"),
                            BinaryPrimitives.ReadUInt32LittleEndian(effect.Plaintext.Span[16..]));
                    }
            }
        }
    }

    [Check("spells-live-audit-power-fist-target-particle")]
    public static void PowerFistTargetParticleIncludesLethalHits()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var outcome in new[] { "normal", "shield", "lethal" })
        {
            using var match = new Match("Blitzcrank", team);
            var runtime = match.Runtime;
            var blitz = match.Hero(0);
            var victim = match.Hero(1);
            if (outcome == "shield")
                runtime.SpellBuffs.Add(victim, "BansheesVeil", BuffType.Aura, 10.0,
                    victim.NetId, canMitigateDuration: false);
            if (outcome == "lethal") victim.Health = 1;
            match.Cast(0, 2, blitz);
            match.Clear();
            TestAssert.True(runtime.Combat.ApplyHit(blitz.NetId, victim.NetId,
                blitz.AttackDamage, DamageType.Physical, DamageSourceKind.Basic),
                $"{team}/{outcome}: empowered basic lands");
            runtime.Delivery.Flush();
            var expected = outcome == "normal" ? 1 : 0;
            TestAssert.Equal(expected, victim.Buffs.Count(buff => buff.Name == "PowerFistSlow"),
                $"{team}/{outcome}: only an eligible living unshielded victim is controlled");
            foreach (var capture in match.Captures)
            {
                var particles = capture.Packets.Where(packet => IsNamedVisual(packet, "Powerfist_tar")).ToArray();
                // Champion sweep BLZ-03, 27 Sept 2026: lethal hits retain the
                // transient impact without installing corpse control.
                TestAssert.Equal(outcome == "shield" ? 0 : 1, particles.Length,
                    $"{team}/{outcome}: unshielded lethal hits retain their target particle");
                if (outcome == "shield") continue;
                var bytes = particles[0].Plaintext.Span;
                TestAssert.Equal(SpellPackets.BuffNameHash("head"),
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]),
                    $"{team}: target impact binds to head");
                TestAssert.Equal(victim.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]),
                    $"{team}: target impact references the controlled victim");
                TestAssert.Equal(victim.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[29..]),
                    $"{team}: target impact binds to the controlled victim");
            }
        }
    }

    [Check("spells-live-audit-piltover-overdrive-particles")]
    public static void PiltoverOverdriveParticlesReplayAndRetireWithBuff()
    {
        foreach (var skin in new[] { 0, 4 })
        {
            using var match = new Match("Blitzcrank", Team.Order, skinId: skin);
            var runtime = match.Runtime;
            var blitz = match.Hero(0);
            var enemy = match.Hero(1);
            match.Cast(0, 1, blitz);
            for (var tick = 0; tick < 100 && !blitz.Buffs.Any(buff => buff.Name == "Overdrive"); tick++)
                runtime.Tick(.01);
            // Overdrive.luaobj OnBuffActivate (BBGetSkinID == 4): eight pipes, the classic body effect, two wheels; every other skin only the
            // body effect. The script keeps their ids to remove them in OnBuffDeactivate, so the engine replays them to late observers.
            var created = match.Captures[0].Packets.Where(packet => IsNamedVisual(packet, "SteamGolem_Piltover_Overdrive") ||
                IsNamedVisual(packet, "SteamGolem_Piltover_Overdrive_Tires")).ToArray();
            var extras = created.Select(packet => BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[25..])).ToArray();
            TestAssert.Equal(skin == 4 ? 10 : 0, extras.Length,
                $"skin {skin}: only Piltover Customs has pipes and wheels");
            var body = match.Captures[0].Packets.Single(packet => IsNamedVisual(packet, "Overdrive_buf"));
            var bodyId = BinaryPrimitives.ReadUInt32LittleEndian(body.Plaintext.Span[25..]);
            TestAssert.True(bodyId != 0, $"skin {skin}: classic body effect remains");
            var allIds = extras.Append(bodyId).ToArray();
            foreach (var capture in match.Captures)
            {
                foreach (var id in allIds)
                    TestAssert.Equal(1, capture.Packets.Count(packet => IsVisualId(packet, id)),
                        $"skin {skin}: initial visual {id:x8} reaches both observers");
                for (var index = 0; index < extras.Length; index++)
                {
                    var packet = capture.Packets.Single(packet => IsVisualId(packet, extras[index]));
                    var bytes = packet.Plaintext.Span;
                    var bone = index < 8
                        ? $"BUFFBONE_CSTM_PIPE_{(index < 4 ? "L" : "R")}_{index % 4 + 1}"
                        : index == 8 ? "BL_wheel" : "BR_wheel";
                    var name = index < 8 ? "SteamGolem_Piltover_Overdrive"
                        : "SteamGolem_Piltover_Overdrive_Tires";
                    TestAssert.Equal(SpellPackets.BuffNameHash(name),
                        BinaryPrimitives.ReadUInt32LittleEndian(bytes[6..]),
                        $"skin {skin}: attachment {index} asset");
                    TestAssert.Equal(SpellPackets.BuffNameHash(bone),
                        BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]),
                        $"skin {skin}: attachment {index} bone");
                }
            }

            // Refreshing an already visible buff must keep its existing IDs.
            match.Clear();
            runtime.Buffs.SyncUnitBuffs(blitz);
            runtime.Delivery.Flush();
            TestAssert.True(allIds.All(id => match.Captures.All(capture =>
                !capture.Packets.Any(packet => IsVisualId(packet, id)))),
                $"skin {skin}: buff sync does not duplicate visual IDs");

            // A reconnect resets the observer's known set. The buff's own
            // replay path must rebuild every attachment with its original bone.
            match.Clear();
            runtime.Session.Participants[0].View.Reset();
            runtime.Delivery.Flush();
            foreach (var id in allIds)
                TestAssert.Equal(1, match.Captures[0].Packets.Count(packet => IsVisualId(packet, id)),
                    $"skin {skin}: reconnect restores visual {id:x8}");

            // An enemy entering sight after the cast gets the same IDs once.
            match.Clear();
            enemy.Position = new Position2(10000, 10000);
            runtime.Tick(.1);
            var enemyView = runtime.Session.Participants[1].View;
            TestAssert.False(enemyView.Visible.Contains(blitz.NetId),
                $"skin {skin}: the opposing observer loses sight of Blitzcrank");
            TestAssert.True(allIds.All(enemyView.Effects.Contains),
                $"skin {skin}: known buff effects persist through temporary fog");
            match.Clear();
            enemy.Position = blitz.Position;
            runtime.Tick(.1);
            TestAssert.True(enemyView.Visible.Contains(blitz.NetId),
                $"skin {skin}: the opposing observer regains sight of Blitzcrank");
            foreach (var id in allIds)
                TestAssert.Equal(0, match.Captures[1].Packets.Count(packet => IsVisualId(packet, id)),
                    $"skin {skin}: retained visual {id:x8} is not created twice on re-entry");

            match.Clear();
            runtime.Tick(8.1);
            TestAssert.False(blitz.Buffs.Any(candidate => candidate.Name == "Overdrive"),
                $"skin {skin}: buff naturally expires");
            foreach (var capture in match.Captures)
                foreach (var id in allIds)
                    TestAssert.Equal(1, capture.Packets.Count(packet => IsVisualKill(packet, id)),
                        $"skin {skin}: expiration retires visual {id:x8}");
        }
    }

    [Check("spells-live-audit-pet-area-targetability")]
    public static void PetAreasSkipUntargetableButHitStealthedUnits()
    {
        foreach (var mode in new[] { "normal", "untargetable", "stealthed" })
        {
            using (var match = new Match("Shaco", Team.Order))
            {
                var runtime = match.Runtime;
                var owner = match.Hero(0);
                var victim = match.Hero(1);
                victim.Position = owner.Position + new Position2(200, 0);
                victim.Health = victim.MaxHealth = 5000;
                match.Cast(0, 3, owner);
                runtime.Tick(.6);
                var clone = runtime.World.Entities.Values.Single(unit =>
                    unit.Kind == EntityKind.ShacoClone && unit.Alive);
                clone.Position = victim.Position + new Position2(50, 0);
                AddAreaTargetState(runtime, victim, mode);
                var before = victim.Health;
                var controller = clone.Buffs.Single(buff => buff.Name == "HallucinateFull");
                runtime.Buffs.RemoveBuff(clone, controller.Slot);
                TestAssert.Equal(mode == "untargetable", victim.Health == before,
                    $"Shaco nova/{mode}: fresh area selection respects untargetability, not stealth");
            }

            using (var match = new Match("Annie", Team.Order))
            {
                var runtime = match.Runtime;
                var owner = match.Hero(0);
                var victim = match.Hero(1);
                victim.Position = owner.Position + new Position2(200, 0);
                victim.Health = victim.MaxHealth = 5000;
                match.CastGround(0, victim.Position, slot: 3);
                runtime.Tick(.5);
                var tibbers = runtime.World.Entities.Values.Single(unit =>
                    unit.Kind == EntityKind.Pet && unit.OwnerNetId == owner.NetId && unit.Alive);
                tibbers.Position = victim.Position + new Position2(50, 0);
                AddAreaTargetState(runtime, victim, mode);
                var before = victim.Health;
                // The aura is the InfernalGuardianBurning script buff: its first pulse is one second after the summon.
                runtime.Spells.Tick(1.01);
                runtime.Buffs.TickBuffs(tibbers, 1.01);
                TestAssert.Equal(mode == "untargetable", victim.Health == before,
                    $"Tibbers aura/{mode}: new periodic area hit respects untargetability, not stealth");
            }
        }
    }

    [Check("spells-live-audit-power-fist-wall-landing")]
    public static void PowerFistFindsBodySafeMap1Landing()
    {
        using var match = new Match("Blitzcrank", Team.Order,
            random: new AnnieChecks.FixedRandomSource(0));
        var runtime = match.Runtime;
        var grid = runtime.Movement.Navigation;
        var blitz = match.Hero(0);
        var victim = match.Hero(1);
        Position2? witness = null;
        Position2 landing = default;
        for (var row = 80; row < grid.CountY - 80 && witness is null; row += 2)
        for (var column = 80; column < grid.CountX - 80 && witness is null; column += 2)
        {
            var start = grid.CellToWorld(new NavCell(column, row));
            if (!grid.IsMovementWalkable(start) ||
                grid.PointClearance(start) < victim.Radius + 4.0) continue;
            var casterStart = start + new Position2(-160, 0);
            if (!grid.IsMovementWalkable(casterStart) ||
                grid.PointClearance(casterStart) < blitz.Radius + 4.0) continue;
            var requested = start + new Position2(80, 0);
            if (grid.IsMovementWalkable(requested) &&
                grid.PointClearance(requested) >= victim.Radius + 4.0) continue;
            var resolved = runtime.Movement.FurthestWalkableLanding(start, requested, victim);
            if (resolved == start || resolved == requested) continue;
            witness = start;
            landing = resolved;
        }
        TestAssert.True(witness is not null,
            "Map1 has a clear starting body with blocked eastward 80-unit Power Fist landing");
        var origin = witness!.Value;
        victim.Position = origin;
        blitz.Position = origin + new Position2(-160, 0);
        match.Cast(0, 2, blitz);
        TestAssert.True(runtime.Combat.ApplyHit(blitz.NetId, victim.NetId,
            blitz.AttackDamage, DamageType.Physical, DamageSourceKind.Basic),
            "controlled wall witness receives Power Fist basic");
        var move = victim.ForcedMove ?? throw new InvalidOperationException(
            "safe backoff should permit the knock-up displacement");
        TestAssert.Equal(landing, move.Destination,
            "the real hit chooses the same quantized landing as the map search");
        TestAssert.True(grid.IsMovementWalkable(move.Destination) &&
            grid.PointClearance(move.Destination) >= victim.Radius + 4.0,
            "landed physical body clears Map1 terrain");
        TestAssert.True(VisualDistance(origin, move.Destination) <= 80.0 + 1e-6,
            "backoff stays inside the source's 80-unit search radius");
        runtime.Tick(1.0);
        TestAssert.Equal(landing, runtime.Movement.ClientAnchor(victim.Position),
            "authoritative forced movement finishes in the advertised client landing cell");
        TestAssert.True(VisualDistance(landing, victim.Position) <= 1e-6,
            "authoritative forced movement finishes at its advertised landing within float tolerance");
    }

    [Check("spells-live-audit-revival-post-hit-controls")]
    public static void LethalHitDoesNotControlNewYorickRevivalLife()
    {
        foreach (var spell in new[] { "RocketGrab", "StaticField", "TwoShivPoison" })
        {
            using var match = new Match(spell == "TwoShivPoison" ? "Shaco" : "Blitzcrank",
                Team.Order, enemyChampion: "Yorick");
            var runtime = match.Runtime;
            var caster = match.Hero(0);
            var yorick = match.Hero(1);
            yorick.Position = caster.Position + new Position2(200, 0);
            match.Cast(1, 3, yorick);
            runtime.Tick(.5);
            TestAssert.True(yorick.Buffs.Any(buff => buff.Name == "YorickReviveAllySelf"),
                $"{spell}: self R marks the original life");
            foreach (var clone in runtime.World.Entities.Values.Where(unit =>
                         unit.Kind == EntityKind.YorickRevenant && unit.Alive))
            {
                clone.Position = yorick.Position + new Position2(0, 900);
                // Pet.lua holds the order in its AI state (wire 11 is ORDER_PETHARDSTOP ->
                // AI_PET_HARDSTOP); the controller buff carries no order any more.
                TestAssert.True(runtime.Pets.Command(yorick, 11, clone.Position, actorNetId: clone.NetId),
                    $"{spell}: the revenant takes the hard stop");
            }
            yorick.Health = 1;
            match.Clear();
            if (spell == "RocketGrab") match.CastGround(0, yorick.Position);
            else if (spell == "StaticField") match.Cast(0, 3, caster);
            else match.Cast(0, 2, yorick);
            for (var tick = 0; tick < 200 && yorick.YorickRevival is null; tick++)
                runtime.Tick(.01);
            TestAssert.True(yorick.YorickRevival is not null && yorick.Alive && yorick.Health > 0,
                $"{spell}: damage starts the one-HP temporary life");
            if (spell == "RocketGrab")
            {
                TestAssert.False(yorick.Buffs.Any(buff => buff.Name == "RocketGrab2"),
                    "lethal hook does not stun the new life");
                TestAssert.True(yorick.ForcedMove is null,
                    "lethal hook does not pull the new life");
                TestAssert.True(match.Captures.SelectMany(capture => capture.Packets)
                    .Any(packet => IsNamedVisual(packet, "FistReturn_mis")),
                    "hook retains its impact return particle");
            }
            else if (spell == "StaticField")
            {
                TestAssert.False(yorick.Buffs.Any(buff => buff.Type == BuffType.Silence),
                    "lethal Static Field does not silence the new life");
                TestAssert.True(match.Captures.SelectMany(capture => capture.Packets)
                    .Any(packet => IsNamedVisual(packet, "StaticField_hit")),
                    "Static Field retains its victim impact particle");
            }
            else TestAssert.False(yorick.Buffs.Any(buff => buff.Name == "TwoShivPoison"),
                "lethal shiv does not poison the new life");
        }
    }

    [Check("spells-live-audit-leesin-q-mark-indicator-unbound")]
    public static void LeeQMarkIndicatorHasNoBone()
    {
        // Playtest #9, 25 Sept 2026: shipped BlindMonkQOne/Chaos create the
        // ground-ring indicator without a bone; 'head' lifted it off the feet.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team);
            var runtime = match.Runtime;
            var lee = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = lee.Position + new Position2(300, 0);
            var mark = team == Team.Order ? "BlindMonkQOne" : "BlindMonkQOneChaos";
            match.Cast(0, 0, enemy);
            for (var tick = 0; tick < 100 && !enemy.Buffs.Any(buff => buff.Name == mark); tick++)
                runtime.Tick(.01);
            TestAssert.True(enemy.Buffs.Any(buff => buff.Name == mark), $"{team}: Q1 marks the target");
            runtime.Delivery.Flush();
            var indicator = match.Captures[0].Packets.Single(packet =>
                IsNamedVisual(packet, "blindMonk_Q_tar_indicator"));
            var bytes = indicator.Plaintext.Span;
            TestAssert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]),
                $"{team}: Q mark indicator carries no bone");
            TestAssert.Equal(enemy.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[29..]),
                $"{team}: Q mark indicator is bound to the marked target");
        }
    }

    [Check("spells-live-audit-leesin-q-hit-spark")]
    public static void LeeQHitsCreateShippedSparkAndBlood()
    {
        // Playtest B2 NL1, 26 Sept 2026: the shipped Q1 mark activation and
        // BlindMonkQTwoDashParticle create one-shot hit effects on the victim.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team);
            var runtime = match.Runtime;
            var lee = match.Hero(0);
            var enemy = match.Hero(1);
            enemy.Position = lee.Position + new Position2(300, 0);
            var mark = team == Team.Order ? "BlindMonkQOne" : "BlindMonkQOneChaos";
            match.Cast(0, 0, enemy);
            for (var tick = 0; tick < 100 && !enemy.Buffs.Any(buff => buff.Name == mark); tick++)
                runtime.Tick(.01);
            TestAssert.True(enemy.Buffs.Any(buff => buff.Name == mark), $"{team}: Q1 marks the target");
            runtime.Delivery.Flush();
            AssertHitEffects(match, enemy, $"{team} Q1",
                "blindMonk_Q_resonatingStrike_tar", "blindMonk_Q_resonatingStrike_tar_blood");

            match.Clear();
            lee.Mana = lee.MaxMana;
            match.Cast(0, 0, enemy);
            for (var frame = 0; lee.ForcedMove is not null && frame < 60; frame++)
                runtime.Tick(1.0 / 30);
            TestAssert.True(lee.ForcedMove is null, $"{team}: Q2 reaches its mark");
            runtime.Delivery.Flush();
            AssertHitEffects(match, enemy, $"{team} Q2", "blindMonk_Q_resonatingStrike_tar",
                "blindMonk_Q_resonatingStrike_tar_blood", "blindmonk_resonatingstrike_tar_sound");
        }

        static void AssertHitEffects(Match match, Entity target, string label, params string[] names)
        {
            foreach (var capture in match.Captures)
            foreach (var name in names)
            {
                var effects = capture.Packets.Where(packet => IsNamedVisual(packet, name)).ToArray();
                TestAssert.Equal(1, effects.Length, $"{label}: one {name} per client");
                var bytes = effects[0].Plaintext.Span;
                TestAssert.Equal(target.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]),
                    $"{label}: {name} targets the hit unit");
                TestAssert.Equal(target.NetId, BinaryPrimitives.ReadUInt32LittleEndian(bytes[29..]),
                    $"{label}: {name} is bound to the hit unit");
            }
        }
    }

    private static double VisualDistance(Position2 first, Position2 second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Z - second.Z, 2));

    private static void AddAreaTargetState(Amberline.Network.Sessions.MatchRuntime runtime,
        Entity victim, string mode)
    {
        if (mode == "normal") return;
        var payload = mode == "untargetable"
            ? new BuffPayload { Untargetable = true }
            : new BuffPayload { Stealthed = true };
        runtime.SpellBuffs.Add(victim, "Probe" + mode, BuffType.Internal, 2.0,
            victim.NetId, payload, canMitigateDuration: false);
    }

    private static bool IsNamedVisual(PlaintextPacket packet, string name) =>
        Opcode(packet) == 0x8C && packet.Plaintext.Length >= 30 &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) ==
            SpellPackets.BuffNameHash(name);

    private static bool IsVisualId(PlaintextPacket packet, uint id) =>
        Opcode(packet) == 0x8C && packet.Plaintext.Length >= 30 &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[25..]) == id;

    private static bool IsVisualKill(PlaintextPacket packet, uint id) =>
        Opcode(packet) == 0x3D && packet.Plaintext.Length >= 9 &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == id;
}
