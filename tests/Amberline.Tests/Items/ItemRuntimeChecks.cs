using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Items;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Neutral;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Stats;

namespace Amberline.Tests.Items;

/// <summary>
/// Item replay checks against archived migration fixtures. Execution uses the
/// C# item, buff, combat, effect, stats, and protocol services.
/// Only clock, random, and packet delivery are test
/// boundaries; item effects are never reproduced by the checker.
/// </summary>
public static class ItemRuntimeChecks
{
    [Check("spells-blitz-item-lethal-impact-visuals")]
    public static void LethalItemChainsKeepImpactsWithoutCorpseProcs()
    {
        // Batch 147: the Cleaver's half is the production graph now — Items/3071.luaobj ItemOnHitUnit runs on the hit
        // itself (the Modify stage of CombatService.ApplyHit, while the target is still alive), so the mark is
        // installed like any other and MatchGameplayHooks.RecipientDeathMarks retires it with its recipient.
        var fixture = ItemAcceptanceFollowupChecks.StartProduction(
        [
            new SessionParticipantDefinition(1, 0, "Cleaver attacker", "Annie", Team.Order),
            new SessionParticipantDefinition(2, 1, "Cleaver victim", "Orianna", Team.Chaos),
        ], random: new FixedRandomSource(0.0));
        using var production = fixture.Runtime;
        var cleaver = fixture.Hero(0);
        var marked = fixture.Hero(1);
        cleaver.HeroAutoAcquire = marked.HeroAutoAcquire = false;
        cleaver.Position = new Position2(7000, 7000);
        marked.Position = new Position2(7100, 7000);
        cleaver.Inventory = [new InventorySlot(3071), null, null, null, null, null];
        // A copy binds to its inventory slot on the next spell update (BbItemModule.SyncInventory), the way a real
        // buy does.
        production.Tick(0.01);
        var capture = new CaptureTransport();
        production.Delivery.AddObserver(new ReplicationObserver(9001, cleaver.Team,
            cleaver.NetId, capture, ready: true));
        foreach (var (lethal, full) in new[]
                 { (false, false), (false, true), (true, false), (true, true) })
        {
            marked.Buffs.Clear();
            if (full)
                for (var slot = 0; slot < 64; slot++)
                    marked.Buffs.Add(new BuffState($"B5Capacity{slot}", new BuffPayload(),
                        BuffType.Internal, slot, 30, marked.NetId));
            marked.MaxHealth = 1000;
            marked.Health = lethal ? 5 : 1000;
            marked.Alive = true;
            capture.Packets.Clear();
            TestAssert.True(production.Combat.ApplyHit(cleaver.NetId, marked.NetId, 10,
                DamageType.True, DamageSourceKind.Basic), $"{lethal}/{full}: the basic lands");
            production.Delivery.Flush();
            var impacts = capture.Packets.Where(packet => packet.Plaintext.Span[0] == 0x8C &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) ==
                    SpellPackets.BuffNameHash("BlackCleave_itm")).ToArray();
            TestAssert.Equal(1, impacts.Length,
                $"{lethal}/{full}: BLZ-05 the Cleaver impact fires with the hit it belongs to");
            foreach (var impact in impacts)
            {
                TestAssert.Equal(marked.NetId,
                    BinaryPrimitives.ReadUInt32LittleEndian(impact.Plaintext.Span[21..]));
                TestAssert.Equal(marked.NetId,
                    BinaryPrimitives.ReadUInt32LittleEndian(impact.Plaintext.Span[29..]));
            }
            TestAssert.Equal(1, capture.Packets.Count(packet => packet.Plaintext.Span[0] == 0x68 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == marked.NetId),
                $"{lethal}/{full}: the Cleaver brings an impact and a debuff, never damage");
            if (lethal)
            {
                TestAssert.False(marked.Alive, $"{lethal}/{full}: lethal hit completes death");
                TestAssert.False(marked.Buffs.Any(buff => buff.Name == "BlackCleaver"),
                    $"{lethal}/{full}: the corpse keeps no mark after its recipient dies");
            }
            else if (full)
                TestAssert.False(marked.Buffs.Any(buff => buff.Name == "BlackCleaver"),
                    $"{lethal}/{full}: BLZ-05 transient impact does not require a persistent debuff slot");
            else
                TestAssert.True(marked.Buffs.Any(buff => buff.Name == "BlackCleaver"),
                    "the living target is marked by the hit's own add (Items/3071.luaobj)");
        }

        // Batch 149 step 5: Sword of the Divine (3131) is Items/3131.luaobj + Spells/LightstrikerApplicator.luaobj now,
        // so the replay half this check used to open with — which armed the hand-written Item3131Stacks /
        // Item3178Stacks and walked the on-hit chain of the other converted procs through a harness with no script
        // engine — is gone. What it was named for is the script's own: the fourth hit is
        // Spells/LightstrikerApplicator.luaobj's AttackCounter == 4 block, which deals the flat 100 magic PROC and
        // plays sword_of_the_divine_tar.troy when Target CO_IS_TYPE_AI. Batch 172 corrected what that gate means
        // (see the assertion below): it is the client's base-AI type bit, so heroes carry it too and the troy plays
        // on both kinds - the hand-written row's behaviour, and no deviation from the script at all.
        var divine = ItemAcceptanceFollowupChecks.StartProduction(
        [
            new SessionParticipantDefinition(1, 0, "Divine attacker", "Annie", Team.Order),
            new SessionParticipantDefinition(2, 1, "Divine victim", "Orianna", Team.Chaos),
        ], random: new FixedRandomSource(0.0));
        using var divineRuntime = divine.Runtime;
        var wielder = divine.Hero(0);
        wielder.HeroAutoAcquire = false;
        wielder.Position = new Position2(7000, 7000);
        wielder.Inventory = [new InventorySlot(3131), null, null, null, null, null];
        divineRuntime.Tick(0.01);
        var divineCapture = new CaptureTransport();
        divineRuntime.Delivery.AddObserver(new ReplicationObserver(9001, wielder.Team,
            wielder.NetId, divineCapture, ready: true));
        foreach (var (kind, type) in new[]
                 { (EntityKind.Minion, UnitType.Minion), (EntityKind.Hero, UnitType.Hero) })
        {
            var victim = kind == EntityKind.Hero
                ? divine.Hero(1)
                : divineRuntime.World.Register(new Entity(divineRuntime.World.Allocate(UnitType.Minion),
                    EntityKind.Minion, UnitType.Minion, Team.Chaos, new Position2(7100, 7000))
                    { Name = "Minion_T200Basic", Health = 5, MaxHealth = 1000, Armor = 0, SpellBlock = 0,
                        Radius = 0, Alive = true, Created = true });
            if (kind == EntityKind.Hero) divine.Hero(1).HeroAutoAcquire = false;
            victim.Position = new Position2(7100, 7000);
            victim.MaxHealth = 1000;
            victim.Health = 5;
            victim.Alive = true;
            victim.Armor = 0;
            victim.SpellBlock = 0;
            victim.Buffs.Clear();
            divineCapture.Packets.Clear();
            // The observer learns the target at the flush that follows its creation, before the lethal hit can
            // take it out of the visible set: a lethal effect-create is routed only to a viewer that already
            // knows every unit it names.
            divineRuntime.Delivery.Flush();
            divineCapture.Packets.Clear();
            ItemScriptState.ArmWindowCounter(divineRuntime, wielder, "LightstrikerApplicator", "AttackCounter", 3.0);
            TestAssert.True(divineRuntime.Combat.ApplyHit(wielder.NetId, victim.NetId, 10,
                DamageType.True, DamageSourceKind.Basic), $"{kind}: the lethal fourth hit lands");
            // The hit only queues its packets; the observer sees them at the delivery flush, the way a match's
            // step ends (MatchRuntime.RunSimulationStep). Without it the capture is always empty.
            divineRuntime.Delivery.Flush();
            TestAssert.False(victim.Alive, $"{kind}: lethal hit completes death");
            var impacts = divineCapture.Packets.Where(packet => packet.Plaintext.Span[0] == 0x8C &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) ==
                    SpellPackets.BuffNameHash("sword_of_the_divine_tar")).ToArray();
            // Batch 172: `CO_IS_TYPE_AI` is the client's base-AI type bit, not "a minion or a monster".
            // The script helper's IsObjectAI reads bit 0x400 of the object's type flags, the same bit the client's base-AI check reads,
            // and obj_AI_Hero derives from
            // obj_AI_Base: heroes and turrets pass it and only the structures (obj_Barracks, obj_BarracksDampener,
            // obj_HQ - objIsBuilding, 2632760) do not. The emulator's host predicate is the same list
            // (BbSpellHost.IsObjectAI: Hero, Minion, Monster, Turret), and scripting.bb-vayne-e-condemn... pins
            // another `CO_IS_TYPE_AI` gate drawing vayne_Q_tar on a hero target. So the fourth-hit troy plays on
            // both kinds here; batch 149 step 5's "heroes get no troy" reading of this gate was wrong.
            TestAssert.Equal(1, impacts.Length,
                $"{kind}: BBSpellEffectCreate is guarded by Target CO_IS_TYPE_AI (heroes are obj_AI_Base)");
            foreach (var impact in impacts)
            {
                TestAssert.Equal(victim.NetId, BinaryPrimitives.ReadUInt32LittleEndian(impact.Plaintext.Span[21..]));
                TestAssert.Equal(victim.NetId, BinaryPrimitives.ReadUInt32LittleEndian(impact.Plaintext.Span[29..]));
            }
        }
    }

    [Check("items.runtime.b4-wits-end-target-matrix")]
    public static void WitsEndOnlyDamagesNonTurretAi()
    {
        // Batch 149 step 5: the proc is Spells/WitsEnd.luaobj's own BuffOnHitUnit now — the WitsEnd window
        // Items/3091.luaobj opens on the copy's own OnActivate carries it, and the matrix is that hook's own gates:
        // a hit that is neither dodge nor miss on a Target CO_IS_TYPE_AI takes the BBApplyDamage 42 MAGIC DAMAGESOURCE_PROC
        // of the CO_IS_TYPE_TURRET branch's BBElse, a turret takes the two stack rows of the branch itself, and the
        // non-AI BBElse stacks without dealing anything (Spells/WitsEnd.luaobj, BuffOnHitUnit). The archived
        // item-onhit-corpus.json case 32 this check used to drive was the recording of the hand-written
        // ItemPassiveService row for the same matrix; that row is gone and its harness runs no script engine, so the
        // numbers move to the production graph the way every other batch 149 on-hit check drove them. The 10 below is
        // the hit itself (the old passive callback dealt nothing), so the arms read 990 and 948.
        var fixture = ItemAcceptanceFollowupChecks.StartProduction(
        [
            new SessionParticipantDefinition(1, 0, "Wit's End attacker", "Annie", Team.Order),
            new SessionParticipantDefinition(2, 1, "Wit's End victim", "Orianna", Team.Chaos),
        ], random: new FixedRandomSource(0.0));
        using var production = fixture.Runtime;
        var attacker = fixture.Hero(0);
        var victim = fixture.Hero(1);
        attacker.HeroAutoAcquire = victim.HeroAutoAcquire = false;
        attacker.Position = new Position2(7000, 7000);
        victim.Position = new Position2(7100, 7000);
        attacker.Inventory = [new InventorySlot(3091), null, null, null, null, null];
        // The copy binds to its slot and opens the WitsEnd window on the next spell update (BbItemModule.SyncInventory).
        production.Tick(0.01);
        foreach (var (kind, type, proc) in new[]
        {
            (EntityKind.Hero, UnitType.Hero, true),
            (EntityKind.Minion, UnitType.Minion, true),
            (EntityKind.Monster, UnitType.Monster, true),
            (EntityKind.Turret, UnitType.Turret, false),
            (EntityKind.Inhibitor, UnitType.Structure, false),
            (EntityKind.Nexus, UnitType.Structure, false),
        })
        {
            var target = kind == EntityKind.Hero
                ? victim
                : production.World.Register(new Entity(production.World.Allocate(type), kind, type, Team.Chaos,
                    new Position2(7100, 7000)) { Alive = true, Created = true, Protected = false });
            target.MaxHealth = 1000;
            target.Health = 1000;
            target.Armor = 0;
            target.SpellBlock = 0;
            TestAssert.True(production.Combat.ApplyHit(attacker.NetId, target.NetId, 10,
                DamageType.Physical, DamageSourceKind.Basic), $"{kind}: the hit lands");
            TestAssert.Near(proc ? 948 : 990, target.Health, 1e-9,
                $"{kind}: only non-turret AI receives the 42 magic proc");
        }
    }

    [Check("items.runtime.b4-youmuu-melee-extensions")]
    public static void YoumuuExtendsTwicePerMeleeActivation()
    {
        // Batch 149 step 4: the extension is the shipped Spells/SpectralFury.luaobj BuffOnHitUnit now and the
        // active that arms it is Spells/YoumusBlade.luaobj's own SelfExecute (spectral_fury_cas.troy, the
        // SpectralFury add at 4 s, CharVars.SpectralCount = 0). The replay harness this check used to drive has no
        // script engine and hero.ItemState.SpectralCount went with the hand-written pair, so the numbers move to
        // the production graph: a melee owner renews the window to its own remaining + 2 s twice per activation
        // (DurationVar is an addend of the 2 s Duration), a ranged one never does, and the cap is the script's own
        // CharVars.SpectralCount at 2. Both spellings of the retail number are the script's; nothing is compared
        // against the old hand-written path any more.
        foreach (var champion in new[] { "Shaco", "Annie" })
        {
            var fixture = ItemAcceptanceFollowupChecks.StartProduction(
            [
                new SessionParticipantDefinition(1, 0, "Youmuu owner", champion, Team.Order),
                new SessionParticipantDefinition(2, 1, "Youmuu target", "Orianna", Team.Chaos),
            ], random: new FixedRandomSource(0.0));
            using var production = fixture.Runtime;
            var hero = fixture.Hero(0);
            var target = fixture.Hero(1);
            hero.Position = new Position2(7000, 7000);
            target.Position = new Position2(7100, 7000);
            hero.HeroAutoAcquire = target.HeroAutoAcquire = false;
            hero.Inventory = [new InventorySlot(3142), null, null, null, null, null];
            // The copy binds to its inventory slot and opens its own YoumusBlade window on the next spell update
            // (BbItemModule.Poll), the way a real buy does.
            production.Tick(0.01);
            var request = new byte[34];
            request[0] = 0xA2;
            request[5] = 4 << 1;
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
            for (var activation = 0; activation < 2; activation++)
            {
                hero.ItemCooldowns[0] = 0;
                TestAssert.True(production.Dependencies.Gameplay(new SessionGameplayContext(
                    production.Session.Participants[0], hero, 1, request, null, null)),
                    $"{champion}: activation {activation} is accepted");
                var fury = hero.Buffs.Single(buff => buff.Name == "SpectralFury");
                TestAssert.Near(4, fury.ExpiresAt, 0);
                TestAssert.Near(0, Counter(production, hero, "SpectralCount"), 1e-9,
                    $"{champion}: the active resets the script's own counter");
                for (var hit = 1; hit <= 3; hit++)
                {
                    TestAssert.True(production.Combat.ApplyHit(hero.NetId, target.NetId, 10,
                        DamageType.Physical, DamageSourceKind.Basic), $"{champion}: hit {hit} lands");
                    TestAssert.Near(champion == "Shaco" ? 4 + 2 * Math.Min(hit, 2) : 4,
                        fury.ExpiresAt, 0, $"{champion}: hit {hit} has the retail extension cap");
                }
                TestAssert.Near(champion == "Shaco" ? 2 : 0,
                    Counter(production, hero, "SpectralCount"), 1e-9,
                    $"{champion}: the counter walks to the script's own cap and no further");
                production.Buffs.TickBuffs(hero, fury.ExpiresAt);
                TestAssert.False(hero.Buffs.Any(buff => buff.Name == "SpectralFury"));
            }
        }
    }

    /// <summary>One counter of an item script's own <c>CharVars</c> (the table its hooks run with).</summary>
    private static double Counter(MatchRuntime production, Entity hero, string name) =>
        (double)(production.Spells.ItemScripts!.CharVars(hero)[name] ?? 0.0);

    [Check("items.runtime.b4-cripple-additive-active-multiplicative")]
    public static void RetaliationSubtractsBonusAttackSpeed()
    {
        // Batch 147: Warden's Mail (3082) retaliates from Items/3082.luaobj ItemOnBeingHit now, and its Cripple row
        // carries the additive AttackSpeedMod -0.35. Batch 149 step 2: Randuin's Omen (3143) retaliates from its own
        // window's BuffOnBeingHit (Spells/RanduinsOmen.luaobj adds the same Slow/Cripple pair), so both halves of the
        // pair run on the production graph. Batch 149 step 4: so does the active, whose BlackOmen the archived fixture
        // this check used to finish in reached through the hand-written cast (see the end of the block).
        var fixture = ItemAcceptanceFollowupChecks.StartProduction(
        [
            new SessionParticipantDefinition(1, 0, "Retaliation victim", "Annie", Team.Order),
            new SessionParticipantDefinition(2, 1, "Warden holder", "Orianna", Team.Chaos),
        ], random: new FixedRandomSource(0.0));
        using (var production = fixture.Runtime)
        {
            var basicAttacker = fixture.Hero(0);
            var warden = fixture.Hero(1);
            basicAttacker.HeroAutoAcquire = warden.HeroAutoAcquire = false;
            basicAttacker.Position = new Position2(7000, 7000);
            warden.Position = new Position2(7100, 7000);
            warden.Inventory = [new InventorySlot(3082), null, null, null, null, null];
            // Batch 147: a copy binds to its inventory slot on the next spell update (BbItemModule.SyncInventory),
            // the way a real buy does.
            production.Tick(0.01);
            // Include every non-buff contribution in the total +100% bonus.
            var baseSpeed = basicAttacker.AttackSpeedMod;
            production.Buffs.AddBuff(basicAttacker, "B4AttackSpeed", BuffType.CombatEnhancer, 30,
                basicAttacker.NetId, new BuffPayload { AttackSpeedMod = 2 - baseSpeed });
            TestAssert.Near(2, basicAttacker.AttackSpeedMod, 1e-6);
            // Batch 222: the retaliation's roll is the BuildingBlocks roll on the match's Lua stream; the next Lua draw is 0 (the proc passes).
            LuaRollSeam.Next(production.World.LuaRandom, 0.0);
            TestAssert.True(production.Combat.ApplyHit(basicAttacker.NetId, warden.NetId, 10,
                DamageType.Physical, DamageSourceKind.Basic), "the Warden retaliation hit lands");
            TestAssert.Near(1.65, basicAttacker.AttackSpeedMod, 1e-6,
                "retail sweep B4, 27 Sept 2026: passive subtracts .35, rather than multiplying by .65");
            production.Buffs.TickBuffs(basicAttacker, 3.1);
            TestAssert.Near(2, basicAttacker.AttackSpeedMod, 1e-6);

            // Batch 149 step 2: 3143 opens its RanduinsOmen window from UpdateSelfBuffActions on the next poll
            // (ExecuteImmediately true), and that buff's BuffOnBeingHit is the retaliation pair.
            warden.Inventory = [new InventorySlot(3143), null, null, null, null, null];
            production.Tick(0.01);
            TestAssert.True(warden.Buffs.Any(buff => buff.Name == "RanduinsOmen"),
                "the copy opens its own window on the poll");
            // Batch 222: the retaliation's roll is the BuildingBlocks roll on the match's Lua stream; the next Lua draw is 0 (the proc passes).
            LuaRollSeam.Next(production.World.LuaRandom, 0.0);
            TestAssert.True(production.Combat.ApplyHit(basicAttacker.NetId, warden.NetId, 10,
                DamageType.Physical, DamageSourceKind.Basic), "the Randuin retaliation hit lands");
            TestAssert.Near(1.65, basicAttacker.AttackSpeedMod, 1e-6,
                "retail sweep B4, 27 Sept 2026: the window's Cripple subtracts .35 too");
            production.Buffs.TickBuffs(basicAttacker, 3.1);
            TestAssert.Near(2, basicAttacker.AttackSpeedMod, 1e-6);

            // Batch 149 step 4: the active is Spells/RanduinsOmen.luaobj's own SelfExecute now (the Slow/BlackOmen
            // pair per recipient with NextBuffVars at -0.35 and its assist marker), so the hand-written BlackOmen the
            // archived fixture reached is gone. What the use does is the shipped pair's own: BlackOmen's
            // Spells/BlackOmen.luaobj BuffOnUpdateStats reads its InstanceVars.AttackSpeedMod and asks for
            // IncPercentMultiplicativeAttackSpeedMod, so the active multiplies by .65 where the Cripple row subtracts
            // .35 (Slow's own additive attack-speed row is the legacy UpdateBuffs table the engine contract excludes,
            // docs/scripting/building-blocks-engine.md — the same reason the passive half reads 1.65 and not 1.30).
            // The replay harness that used to take this sample has no script engine and ItemService.Use reaches no
            // script there any more, so the sample moves onto the production graph like the pair above. The
            // case-32 fixture of item-onhit-corpus.json is not re-recorded.
            warden.ItemCooldowns[0] = 0.0;
            var request = new byte[34];
            request[0] = 0xA2;
            request[5] = 4 << 1;
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), warden.NetId);
            TestAssert.True(production.Dependencies.Gameplay(new SessionGameplayContext(
                production.Session.Participants[1], warden, 1, request, null, null)),
                "the Randuin active is accepted");
            var blackOmen = basicAttacker.Buffs.Single(buff => buff.Name == "BlackOmen");
            TestAssert.True(blackOmen.Payload.Has(BuffPayloadField.AttackSpeedMult),
                "the shipped BlackOmen row is the multiplicative one");
            TestAssert.Near(1.30, basicAttacker.AttackSpeedMod, 1e-9,
                "BlackOmen active retains its multiplicative reduction");
        }
    }

    [Check("items.runtime.b4-spellblade-launch-delay")]
    public static void SpellbladeDelayStartsAtLaunchAndSurvivesImpact()
    {
        foreach (var item in new[] { 3057, 3078, 3100 })
        foreach (var windup in new[] { 0.0, 0.25 })
        {
            using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
                GameData.Default, solo: true, random: new FixedRandomSource(0.99));
            var hero = new Entity(runtime.World.Allocate(UnitType.Hero), EntityKind.Hero,
                UnitType.Hero, Team.Order, new Position2(7000, 7000))
            {
                ChampionName = "Annie", Created = true, Visible = true,
                Inventory = [new(item), null, null, null, null, null],
                HeroAutoAcquire = false,
            };
            var target = new Entity(runtime.World.Allocate(UnitType.Hero), EntityKind.Hero,
                UnitType.Hero, Team.Chaos, new Position2(7400, 7000))
            {
                ChampionName = "Annie", Created = true, Visible = true,
                Health = 10000, MaxHealth = 10000, HeroAutoAcquire = false,
            };
            runtime.World.Register(hero);
            runtime.World.Register(target);
            runtime.HeroStats.Refresh(hero);
            // Batch 147: the spell-cast item runs from its shipped Items/<id>.luaobj now, and its copy binds to the
            // inventory slot on the next spell update (BbItemModule.SyncInventory), the way a real buy does. This
            // fixture registers its two heroes into a match whose session never starts, so the simulation driver
            // stops before SpellSystem.Tick and the poll never reaches them: the check runs that same bind step
            // itself. The arm below is the script's ItemOnSpellCast, and the 1.4 s delay at the launch is the
            // Sheen / LichBane buff's own CharOnLaunchAttack.
            runtime.Spells.ItemScripts?.SyncInventory(hero);
            runtime.Dependencies.ChampionHooks.OnSpellCast(hero, 0, 0);
            hero.AttackWindup = windup;
            hero.MissileSpeed = 1000;
            TestAssert.True(runtime.Combat.QueueBasicAttack(hero, target, new AttackRolls(0.99, 0.99, 0.99)));
            if (windup > 0)
            {
                TestAssert.False(hero.Buffs.Any(buff => buff.Name == "SheenDelay"), "windup is not launch");
                runtime.Combat.InterruptBasicAttack(hero);
                TestAssert.False(hero.Buffs.Any(buff => buff.Name == "SheenDelay"), "cancelled windup has no delay");
                hero.AttackCooldown = 0;
                TestAssert.True(runtime.Combat.QueueBasicAttack(hero, target, new AttackRolls(0.99, 0.99, 0.99)));
                runtime.Combat.CombatTick(windup, regenerate: false);
            }
            // Script wins (batch 147): the timer is the Duration the shipped Spells/Sheen.luaobj and
            // Spells/LichBane.luaobj pass to their own CharOnLaunchAttack add of SheenDelay (1.4), and a Lua
            // number is a float — so it is the float 1.4 and not the double the hand-written path wrote.
            // The impact must not restart it with the shorter spellblade delay of that same buff's
            // BuffOnHitUnit (1.2 for IsSheen, 1.3 for Trinity Force, 1.25 for Lich Bane).
            var launchDelay = (double)(float)1.4;
            var delay = hero.Buffs.Single(buff => buff.Name == "SheenDelay");
            TestAssert.Near(launchDelay, delay.ExpiresAt, 1e-9, $"{item}: timer starts at launch");
            runtime.Buffs.TickBuffs(hero, 0.4);
            runtime.Combat.CombatTick(0.4, regenerate: false);
            TestAssert.True(ReferenceEquals(delay, hero.Buffs.Single(buff => buff.Name == "SheenDelay")),
                "impact keeps the original delay instance");
            TestAssert.Near(launchDelay - 0.4, delay.ExpiresAt, 1e-9, "impact does not restart the timer");
            TestAssert.False(hero.Buffs.Any(buff => buff.Name is "Sheen" or "LichBane"));
        }
    }


    [Check("items.runtime.sunfire-renewal-and-death")]
    public static void SunfireRenewsInPlaceAndClearsOnDeath()
    {
        // Batch 149 step 1: Sunfire Cape (3068) owns its self buff now. Items/3068.luaobj's own
        // UpdateSelfBuffActions re-adds Spells/SunfireCloakParticle.luaobj every second with
        // BUFF_RENEW_EXISTING (Duration 1.5) while its owner is not dead, and its ItemOnDeath takes that buff
        // away — so the check drives the production graph (BbItemModule's own poll) instead of
        // ItemPassiveService.Tick, whose Sunfire timer is gone. The item-passive-corpus case it borrowed for
        // its two heroes is not read here any more.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var enemy = team == Team.Order ? Team.Chaos : Team.Order;
            var fixture = ItemAcceptanceFollowupChecks.StartProduction(
            [
                new SessionParticipantDefinition(1, 0, "Sunfire owner", "Annie", team),
                new SessionParticipantDefinition(2, 1, "Sunfire target", "Orianna", enemy),
            ]);
            using var runtime = fixture.Runtime;
            var owner = fixture.Hero(0);
            var target = fixture.Hero(1);
            owner.Position = new Position2(7000.0, 7000.0);
            target.Position = new Position2(7050.0, 7000.0);
            owner.HeroAutoAcquire = target.HeroAutoAcquire = false;
            owner.Inventory = [new InventorySlot(3068), null, null, null, null, null];
            runtime.HeroStats.Refresh(owner);
            // The copy binds to its slot on the next spell update and its BBExecutePeriodically runs with
            // ExecuteImmediately, so the particle and the first burn are that poll's own work.
            runtime.Tick(0.05);
            var self = owner.Buffs.Single(buff => buff.Name == "SunfireCloakParticle");
            var targetAura = target.Buffs.Single(buff => buff.Name == "SunfireCapeAura");
            var selfSlot = self.Slot;
            var targetSlot = targetAura.Slot;

            var ownerWire = new CaptureTransport();
            var targetWire = new CaptureTransport();
            runtime.Delivery.AddObserver(new ReplicationObserver(
                9001, owner.Team, owner.NetId, ownerWire, ready: true));
            runtime.Delivery.AddObserver(new ReplicationObserver(
                9002, target.Team, target.NetId, targetWire, ready: true));
            runtime.Delivery.Flush();
            ownerWire.Packets.Clear();
            targetWire.Packets.Clear();

            // One more period: the copy's own renewal of the particle and the particle's BuffOnUpdateActions
            // burn and aura renewal both fire again.
            runtime.Tick(1.1);
            runtime.Delivery.Flush();
            var renewedSelf = owner.Buffs.Single(buff => buff.Name == "SunfireCloakParticle");
            var renewedTarget = target.Buffs.Single(buff => buff.Name == "SunfireCapeAura");
            TestAssert.True(ReferenceEquals(self, renewedSelf),
                $"{team}: Sunfire self renewal keeps the live buff object");
            TestAssert.True(ReferenceEquals(targetAura, renewedTarget),
                $"{team}: Sunfire target renewal keeps the live buff object");
            TestAssert.Equal(selfSlot, renewedSelf.Slot,
                $"{team}: Sunfire self renewal keeps the live slot");
            TestAssert.Equal(targetSlot, renewedTarget.Slot,
                $"{team}: Sunfire target renewal keeps the live slot");
            TestAssert.True(ownerWire.Packets.Concat(targetWire.Packets).All(packet =>
                    packet.Plaintext.Length == 0 || packet.Plaintext.Span[0] != 0x7F),
                $"{team}: Sunfire renewal emits no remove/re-add churn");

            // The production tick regenerates the survivor's health, so the stop is read on the damage counter the
            // burn feeds rather than on the health total (the bare ItemPassives.Tick this check used to drive left
            // the pool alone).
            var damageBeforeDeath = target.MatchStatistics.TotalDamageTaken;
            TestAssert.True(runtime.Combat.Kill(owner, target.NetId),
                $"{team}: the Sunfire owner's death is accepted");
            TestAssert.False(owner.Buffs.Any(buff => buff.Name == "SunfireCloakParticle"),
                $"{team}: Sunfire owner particle is removed on death");
            runtime.Tick(1.0);
            TestAssert.Near(damageBeforeDeath, target.MatchStatistics.TotalDamageTaken, 1e-9,
                $"{team}: Sunfire damage stops when the owner is dead");
        }
    }

    [Check("items.runtime.mana-aura-resource-domain")]
    public static void ManaAuraRuntimeTagsBothTeamsAndCleansUp()
    {
        // Batch 145 step 2: Items/3037.luaobj and Items/3099.luaobj own these auras now, so the check drives the
        // production graph (BbItemModule's own poll) instead of ItemPassiveService.SyncAuras, whose aura rows are
        // gone. The item-passive-corpus case it borrowed for its three heroes is not read here any more.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var fixture = ItemAcceptanceFollowupChecks.StartProduction(
            [
                new SessionParticipantDefinition(1, 0, "Aura source", "Annie", team),
                new SessionParticipantDefinition(2, 1, "Mana ally", "Orianna", team),
                new SessionParticipantDefinition(3, 2, "Energy ally", "LeeSin", team),
            ]);
            using var runtime = fixture.Runtime;
            var source = fixture.Hero(0);
            var ally = fixture.Hero(1);
            var energy = fixture.Hero(2);
            source.Position = new Position2(7000, 7000);
            ally.Position = new Position2(7050, 7000);
            energy.Position = new Position2(7100, 7000);
            foreach (var hero in new[] { source, ally, energy })
            {
                hero.HeroAutoAcquire = false;
                runtime.HeroStats.Refresh(hero);
            }
            var allyRegen = ally.ManaRegen;
            var energyRegen = energy.ManaRegen;
            var energyCooldown = energy.CooldownMod;

            source.Inventory = [new InventorySlot(3037), new InventorySlot(3099),
                null, null, null, null];
            // Both scripts re-add from BBExecutePeriodically (1.0 s, ExecuteImmediately false), so the aura is a
            // period old when it can be measured and not the first sync tick's work.
            runtime.Tick(1.15);

            var sourceAuras = source.Buffs.Where(buff =>
                buff.Name is "ManaManipulatorAuraSelf" or "SoulShroudAuraSelf").ToArray();
            var allyAuras = ally.Buffs.Where(buff =>
                buff.Name is "ManaManipulatorAuraFriend" or "SoulShroudAuraFriend").ToArray();
            var energyAuras = energy.Buffs.Where(buff =>
                buff.Name is "ManaManipulatorAuraFriend" or "SoulShroudAuraFriend").ToArray();
            TestAssert.Equal(2, sourceAuras.Length, $"{team} source aura count");
            TestAssert.Equal(2, allyAuras.Length, $"{team} mana aura count");
            TestAssert.Equal(2, energyAuras.Length, $"{team} energy aura count");
            // The resource gate moved from the payload's ResourceDomain tag (the hand-written rows' own) into the
            // shipped scripts' accessor: both aura buffs call BBIncFlatPARRegenMod with PARType = PAR_MANA and the
            // engine's IncPARHelper refuses a target whose power is not mana. Lee Sin's copies therefore carry no
            // MpRegen field at all, and the calculator's energy line has nothing to pick up.
            TestAssert.True(energyAuras.All(buff => buff.Payload.MpRegen == 0.0),
                $"{team} Lee Sin's aura copies carry no PAR_MANA regen");
            TestAssert.True(sourceAuras.Concat(allyAuras).All(buff => buff.Payload.MpRegen > 0.0),
                $"{team} mana holders receive the aura's own regen");

            // The host's shared number getter is float32 (BbNativeHandlers.Sum, the client's own number getter), so
            // BBIncFlatPARRegenMod's deltas arrive as 1.44000006 and 2.40000010 and their sum is off the decimal 3.84
            // in the seventh digit. The script's numbers are still 1.44 and 2.4.
            TestAssert.Near(allyRegen + 1.44 + 2.4, ally.ManaRegen, 1e-6,
                $"{team} mana ally receives both production aura regen values");
            TestAssert.Near(energyRegen, energy.ManaRegen, 1e-9,
                $"{team} Lee Sin excludes PAR_MANA aura regen");
            TestAssert.Near(energyCooldown - 0.10, energy.CooldownMod, 1e-9,
                $"{team} Lee Sin keeps Soul Shroud cooldown reduction");

            // The re-add is a renewal of the held instance (BUFF_RENEW_EXISTING), so five more periods keep one
            // copy of each name rather than a fresh slot per fire.
            runtime.Tick(5.0);
            TestAssert.Equal(2, ally.Buffs.Count(buff =>
                buff.Name is "ManaManipulatorAuraFriend" or "SoulShroudAuraFriend"),
                $"{team} aura refresh does not duplicate slots");
            source.Inventory = Inventory.Empty();
            runtime.Tick(3.0);
            TestAssert.Near(allyRegen, ally.ManaRegen, 1e-9,
                $"{team} mana regen reverses after aura expiry");
            TestAssert.Near(energyRegen, energy.ManaRegen, 1e-9,
                $"{team} energy regen remains clean after aura expiry");
            TestAssert.Near(energyCooldown, energy.CooldownMod, 1e-9,
                $"{team} cooldown reduction reverses after aura expiry");
        }
    }

    private static void ReplayUse(JsonElement testCase)
    {
        var name = testCase.GetProperty("name").GetString() ?? "item";
        var runtime = Runtime.Create(testCase.GetProperty("initial"));
        var hero = runtime.World.Get(testCase.GetProperty("hero").GetUInt32())
            ?? throw new InvalidOperationException(name + ": hero missing");
        var request = ReadUseRequest(testCase.GetProperty("request"));
        var result = runtime.Items.Use(hero, request);
        // `accepted` is a recorded output like `final`: record mode refreshes it instead of asserting.
        if (FixtureRecorder.Enabled) CorpusSnapshot.Set(testCase.GetProperty("accepted"), result.Accepted);
        else TestAssert.Equal(testCase.GetProperty("accepted").GetBoolean(), result.Accepted,
            name + " accepted");
        // Batch 149 step 4: the flat targeted damage of Deathfire Grasp / Hextech Gunblade and the ground vision
        // bubbles are gone with the hand-written actives (their expected values and the Bubble compare went with them).
        // Accepted rows of the active group never reach this method any more; the rejected ones left here deal no
        // damage and create no bubbles. `finalBubbles` stays in the archived file as data.
        CompareEntities(testCase.GetProperty("final"), runtime.World, name + " final");
        ComparePackets(testCase.GetProperty("packets"), runtime.Queue.Packets, runtime.Cipher, name);
    }



    private static void ReplayPassive(JsonElement testCase)
    {
        var name = $"passive-{testCase.GetProperty("variant").GetString()}";
        var runtime = Runtime.Create(testCase.GetProperty("initial"));
        var hero = runtime.World.Get(testCase.GetProperty("hero").GetUInt32())
            ?? throw new InvalidOperationException(name + ": hero missing");
        foreach (var itemId in testCase.GetProperty("items").EnumerateArray())
            TestAssert.True(Inventory.HasItem(hero.Inventory, itemId.GetInt32()),
                name + ": initial item missing " + itemId.GetInt32());

        foreach (var eventElement in testCase.GetProperty("events").EnumerateArray())
        {
            var operation = eventElement.GetProperty("operation");
            var operationName = operation.GetProperty("op").GetString() ?? string.Empty;
            var tooltipBefore = CaptureDynamicTooltipValues(hero);
            var result = ApplyPassiveOperation(runtime, hero, operation);
            CompareOperationResult(eventElement.GetProperty("result"), result,
                name + " " + operationName);
            CompareEntities(eventElement.GetProperty("entities"), runtime.World,
                name + " " + operationName);
            ComparePackets(eventElement.GetProperty("packets"), runtime.Queue.Packets,
                runtime.Cipher, name + " " + operationName,
                dynamicTooltipPackets: BuildExpectedDynamicTooltipPackets(
                    hero, tooltipBefore, operationName));
            runtime.Queue.Clear();
        }
    }

    private static void ReplayOnHit(JsonElement testCase)
    {
        var name = $"onhit-{testCase.GetProperty("items")[0].GetInt32()}-" +
                   testCase.GetProperty("targetKind").GetString();
        var runtime = Runtime.Create(testCase.GetProperty("initial"),
            testCase.GetProperty("roll").GetDouble());
        var attacker = runtime.World.Get(testCase.GetProperty("hero").GetUInt32())
            ?? throw new InvalidOperationException(name + ": attacker missing");
        var target = runtime.World.Get(testCase.GetProperty("target").GetUInt32())
            ?? throw new InvalidOperationException(name + ": target missing");
        foreach (var eventElement in testCase.GetProperty("events").EnumerateArray())
        {
            var op = eventElement.GetProperty("op").GetString();
            if (op == "basicHook")
            {
                runtime.Passives.OnBasicAttackHit(attacker, target,
                    eventElement.GetProperty("rawDamage").GetDouble(),
                    ParseDamageType(eventElement.GetProperty("damageType").GetString()));
            }
            else if (op == "buffTick")
            {
                var dt = eventElement.GetProperty("dt").GetDouble();
                runtime.World.Clock.Advance(dt);
                foreach (var unit in runtime.World.Entities.Values.ToArray())
                    runtime.Buffs.TickBuffs(unit, dt);
            }
            else
            {
                throw new InvalidOperationException(name + ": unsupported event " + op);
            }
            CompareEntities(eventElement.GetProperty("entities"), runtime.World, name + " " + op);
            ComparePackets(eventElement.GetProperty("packets"), runtime.Queue.Packets,
                runtime.Cipher, name + " " + op,
                dynamicTooltipPackets: BuildExpectedOnHitTooltipPackets(attacker, target, op));
            runtime.Queue.Clear();
        }
    }

    private static DamageType ParseDamageType(string? value) => value?.ToLowerInvariant() switch
    {
        "physical" => DamageType.Physical,
        "magic" => DamageType.Magic,
        "true" => DamageType.True,
        _ => throw new InvalidOperationException("unsupported damage type " + value),
    };

    private static object? ApplyPassiveOperation(Runtime runtime, Entity hero, JsonElement operation)
    {
        var op = operation.GetProperty("op").GetString();
        switch (op)
        {
            case "spellCast":
                // Batch 149 step 5: the cast-triggered passives are the shipped scripts' own ItemOnSpellCast
                // (3057, 3078, 3100, 3124 in batch 147, and the Tear family's SpellVars.DoesntTriggerSpellCasts
                // guard in Spells/TearOfTheGoddessTrack.luaobj), so the hand-written OnChampionSpellCast this
                // replayed is gone and every archived case that recorded one is skipped (see the pass over the
                // passive corpus above).
                throw new InvalidOperationException("superseded passive operation " + op);
            case "basicHook":
            {
                var target = runtime.World.Get(0x40010001u)
                    ?? throw new InvalidOperationException("passive basic target missing");
                runtime.Passives.OnBasicAttackHit(hero, target, 100.0, DamageType.Physical);
                return null;
            }
            case "tick":
            {
                // Batch 149 step 5: ItemPassiveService.Tick and its SyncAuras are gone — the aura loop an item
                // still needs is its own script's UpdateSelfBuffStats, and this harness has no script engine — so
                // a tick only advances the clock and expires the buffs.
                var dt = operation.GetProperty("dt").GetDouble();
                runtime.World.Clock.Advance(dt);
                foreach (var unit in runtime.World.Entities.Values.ToArray())
                    runtime.Buffs.TickBuffs(unit, dt);
                return null;
            }
            case "killHook" or "assistHook" or "deathHook":
                // Batch 149 step 1: the kill, assist and death callbacks are the shipped scripts' own
                // (Items/3041, 3068, 3072, 3083, 3138 and 3141's ItemOnKill / ItemOnAssist / ItemOnDeath), so
                // the hand-written ItemPassiveService hooks these events replayed are gone and every archived
                // case that recorded one is skipped (see the pass over the corpus above). What a death still
                // does in that service is one counter of an unconverted item, which no archived deathHook row
                // is about.
                throw new InvalidOperationException("superseded passive operation " + op);
            case "consumeShield":
            {
                var target = runtime.World.Get(operation.GetProperty("target").GetUInt32())
                    ?? throw new InvalidOperationException("passive shield target missing");
                var attacker = runtime.World.Entities.Values.FirstOrDefault(unit =>
                    unit.UnitType == UnitType.Hero && unit.Team != target.Team && unit.NetId != target.NetId)
                    ?? throw new InvalidOperationException("passive shield attacker missing");
                return runtime.Damage.ConsumeSpellShield(attacker, target, DamageSourceKind.Spell, force: false);
            }
            case "counter":
                SetCounter(hero, operation.GetProperty("key").GetString()!,
                    operation.GetProperty("value").GetDouble());
                return null;
            default:
                throw new InvalidOperationException("unsupported passive operation " + op);
        }
    }

    private static void CompareOperationResult(JsonElement expected, object? actual, string context)
    {
        if (expected.ValueKind == JsonValueKind.Null)
        {
            TestAssert.True(actual is null, context + " result should be null");
            return;
        }
        if (expected.ValueKind == JsonValueKind.True || expected.ValueKind == JsonValueKind.False)
        {
            TestAssert.True(actual is bool, context + " result type");
            TestAssert.Equal(expected.GetBoolean(), (bool)actual!, context + " result");
            return;
        }
        TestAssert.True(actual is double, context + " result type");
        TestAssert.Near(expected.GetDouble(), (double)actual!, 1e-8, context + " result");
    }

    private static void SetCounter(Entity hero, string key, double value)
    {
        switch (key)
        {
            // Batch 149 step 1: the snowball trio's and Sunfire Cape's hero counters are gone with their
            // hand-written paths — the stacks are the script's own *Stats buff and the Sunfire timer is
            // Spells/SunfireCloakParticle.luaobj's — and every archived case that records one is skipped.
            // Batch 149 step 5: so are the Tear family's reservoirs (CharVars.TearBonusMana and the copies'
            // InstanceVars.CooldownResevoir), Sword of the Divine's and Ionic Spark's fourth-hit arm (the window
            // buff's InstanceVars.AttackCounter), Force of Nature's clock (Spells/ForceofNature.luaobj's own
            // BuffOnUpdateActions) and Banshee's Veil's purchase bookkeeping (Items/3102.luaobj's OnActivate).
            default: throw new InvalidOperationException("unsupported item counter " + key);
        }
    }

    private static ItemUseRequest ReadUseRequest(JsonElement request) => new(
        checked((byte)request.GetProperty("slot").GetInt32()),
        ReadPosition3(request.GetProperty("pos")),
        ReadPosition3(request.GetProperty("end_pos")),
        request.GetProperty("target_netid").GetUInt32(),
        request.GetProperty("is_summoner").GetBoolean());

    private static Position3 ReadPosition3(JsonElement value) => new(
        value[0].GetDouble(), value[1].GetDouble(), value[2].GetDouble());

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static Dictionary<string, object?> SnapshotEntity(Entity actual, JsonElement shape)
    {
        object? P(Position2? point) => point is { } p ? new[] { p.X, p.Z } : null;
        return shape.EnumerateObject().ToDictionary(field => field.Name, field => field.Name switch
        {
            "ability_power" => (object?)(actual.AbilityPower),
            "alive" => (object?)(actual.Alive),
            "armor" => (object?)(actual.Armor),
            "armor_pen_flat" => (object?)(actual.ArmorPenetrationFlat),
            "armor_pen_percent" => (object?)(actual.ArmorPenetrationPercent),
            "atk_damage" => (object?)(actual.AttackDamage),
            "atk_period" => (object?)(actual.AttackPeriod),
            "atk_range" => (object?)(actual.AttackRange),
            "attack_cd" => (object?)(actual.AttackCooldown),
            "attack_speed_mod" => (object?)(actual.AttackSpeedMod),
            "attack_target" => (object?)(actual.AttackTarget),
            "buffs" => (object?)(actual.Buffs.Select(buff => SnapshotBuff(buff, shape.GetProperty("buffs"))).ToArray()),
            "champion" => (object?)(actual.ChampionName),
            "cooldown_mod" => (object?)(actual.CooldownMod),
            "created" => (object?)(actual.Created),
            "crit_chance" => (object?)(actual.CritChance),
            "crit_multiplier" => (object?)(actual.CritMultiplier),
            "death_time" => (object?)(actual.DeathTime),
            "dodge" => (object?)(actual.Dodge),
            "equippedItemIds" => (object?)(actual.EquippedItemIds.OrderBy(id => id).ToArray()),
            "etype" => (object?)(actual.EntityType),
            "exp" => (object?)(actual.Experience),
            "expires_at" => (object?)(actual.ExpiresAt),
            "facing" => (object?)(P(actual.Facing)),
            "global_gold_reward" => (object?)(actual.GlobalGoldReward),
            "gold" => (object?)(actual.Gold),
            "gold_reward" => (object?)(actual.GoldReward),
            "hardness" => (object?)(actual.Hardness),
            "hp" => (object?)(actual.Health),
            "hp_regen" => (object?)(actual.HealthRegen),
            "inventory" => (object?)(SnapshotInventory(actual, shape.GetProperty("inventory"))),
            "isWard" => (object?)(actual.IsWard),
            "itemCounters" => (object?)(SnapshotCounters(actual, shape.GetProperty("itemCounters"))),
            "item_cooldowns" => (object?)(actual.ItemCooldowns),
            "level" => (object?)(actual.Level),
            "life_steal" => (object?)(actual.LifeSteal),
            "magic_pen_flat" => (object?)(actual.MagicPenetrationFlat),
            "magic_pen_percent" => (object?)(actual.MagicPenetrationPercent),
            "mana" => (object?)(actual.Mana),
            "max_hp" => (object?)(actual.MaxHealth),
            "max_mana" => (object?)(actual.MaxMana),
            "missile_speed" => (object?)(actual.MissileSpeed),
            "mp_regen" => (object?)(actual.ManaRegen),
            "name" => (object?)(actual.Name),
            "netid" => (object?)(actual.NetId),
            "odynStoredDamage" => (object?)0.0, // retired field (batch 149 step 4): the window keeps its own CharVars.StoredDamage now
            "owner_netid" => (object?)(actual.OwnerNetId),
            "pendingSummon" => (object?)null,
            "position" => (object?)(P(actual.Position)),
            "pyro_stacks" => (object?)0,
            "radius" => (object?)(actual.Radius),
            "respawn_at" => (object?)(actual.RespawnAt),
            "revealsStealth" => (object?)(actual.RevealsStealth),
            "skill_points" => (object?)(actual.SkillPoints),
            "speed" => (object?)(actual.Speed),
            "spell_block" => (object?)(actual.SpellBlock),
            "spell_cooldowns" => (object?)(actual.SpellCooldowns),
            "spell_ranks" => (object?)(actual.SpellRanks),
            "spell_vamp" => (object?)(actual.SpellVamp),
            "summoner_cooldowns" => (object?)(actual.SummonerCooldowns),
            "target" => (object?)(P(actual.Target)),
            "team" => (object?)((int)actual.Team),
            "unitType" => (object?)((int)actual.UnitType),
            "visible" => (object?)(actual.Visible),
            "waypoints" => (object?)(actual.Waypoints.Select(p => P(p)).ToArray()),
            "windup" => (object?)(actual.AttackWindup),
            "xp_reward" => (object?)(actual.ExperienceReward),
            _ => throw new InvalidOperationException("Unmapped item output: " + field.Name),
        });
    }

    private static object?[] SnapshotInventory(Entity entity, JsonElement shape) => entity.Inventory.Select((slot, index) =>
        slot is null ? null : (object)new Dictionary<string, object?>
        {
            ["itemId"] = slot.ItemId, ["count"] = slot.Count, ["charges"] = slot.Charges,
            // Batch 149 step 5: InventoryItemState is empty. The Bloodthirster's and Warmog's growth counters
            // (step 1) and Rod of Ages' ageing (step 5) are the copy's own script InstanceVars now — Items/3072,
            // 3083 and 3027 — so no per-slot state is left to record and every archived state key is superseded.
            ["state"] = new Dictionary<string, object?>(),
        }).ToArray();

    private static Dictionary<string, object?> SnapshotCounters(Entity actual, JsonElement shape)
    {
        var snapshot = new Dictionary<string, object?>();
        foreach (var field in shape.EnumerateObject())
            if (field.Name is not ("banshee_initial_pending" or "banshee_pet_initialized"
                    or "tear_bonus_mana" or "tear_spell_reservoir" or "tear_spell_refill"
                    or "manamune_attack_reservoir" or "manamune_attack_refill"
                    or "item_3131" or "item_3178" or "force_of_nature_elapsed"
                    or "3131" or "3178" or "snowball_3041" or "snowball_3138" or "snowball_3141"
                    or "sunfire_elapsed" or "sunfire_initialized"))
                throw new InvalidOperationException("Unknown item counter: " + field.Name);
        return snapshot;
    }

    private static Dictionary<string, object?> SnapshotBuff(BuffState buff, JsonElement shapes)
    {
        var shape = shapes.EnumerateArray().FirstOrDefault(row => row.GetProperty("name").GetString() == buff.Name);
        var row = new Dictionary<string, object?>
        {
            ["name"] = buff.Name, ["slot"] = buff.Slot, ["type"] = (int)buff.Type,
            ["expires_at"] = buff.ExpiresAt, ["caster"] = buff.CasterNetId,
        };
        if (buff.Stacks != 1 || shape.ValueKind == JsonValueKind.Object && shape.TryGetProperty("stacks", out _)) row["stacks"] = buff.Stacks;
        if (buff.MaxStack is not null || shape.ValueKind == JsonValueKind.Object && shape.TryGetProperty("max_stack", out _)) row["max_stack"] = buff.MaxStack;
        var payload = new Dictionary<string, object?>();
        var original = shape.ValueKind == JsonValueKind.Object ? shape.GetProperty("payload") : default;
        if (buff.Payload.Has(BuffPayloadField.AbilityPower) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("ability_power", out _)) payload["ability_power"] = buff.Payload.AbilityPower;
        if (buff.Payload.Has(BuffPayloadField.Armor) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("armor", out _)) payload["armor"] = buff.Payload.Armor;
        if (buff.Payload.Has(BuffPayloadField.ArmorPenFlat) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("armor_pen_flat", out _)) payload["armor_pen_flat"] = buff.Payload.ArmorPenFlat;
        if (buff.Payload.Has(BuffPayloadField.ArmorPenPercent) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("armor_pen_percent", out _)) payload["armor_pen_percent"] = buff.Payload.ArmorPenPercent;
        if (buff.Payload.Has(BuffPayloadField.AttackDamage) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("attack_damage", out _)) payload["attack_damage"] = buff.Payload.AttackDamage;
        if (buff.Payload.Has(BuffPayloadField.AttackSpeedMod) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("attack_speed_mod", out _)) payload["attack_speed_mod"] = buff.Payload.AttackSpeedMod;
        if (buff.Payload.Has(BuffPayloadField.AttackSpeedMult) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("attack_speed_mult", out _)) payload["attack_speed_mult"] = buff.Payload.AttackSpeedMult;
        if (buff.Payload.Has(BuffPayloadField.CooldownMod) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("cooldown_mod", out _)) payload["cooldown_mod"] = buff.Payload.CooldownMod;
        if (buff.Payload.Has(BuffPayloadField.CooldownModBase) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("cooldown_mod_base", out _)) payload["cooldown_mod_base"] = buff.Payload.CooldownModBase;
        if (buff.Payload.Has(BuffPayloadField.CooldownModPerLevel) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("cooldown_mod_per_level", out _)) payload["cooldown_mod_per_level"] = buff.Payload.CooldownModPerLevel;
        if (buff.Payload.Has(BuffPayloadField.CritChance) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("crit_chance", out _)) payload["crit_chance"] = buff.Payload.CritChance;
        if (buff.Payload.Has(BuffPayloadField.DodgePiercing) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("dodge_piercing", out _)) payload["dodge_piercing"] = buff.Payload.DodgePiercing;
        if (buff.Payload.Has(BuffPayloadField.DamagePerTick) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("damage_per_tick", out _)) payload["damage_per_tick"] = buff.Payload.DamagePerTick;
        if (buff.Payload.Has(BuffPayloadField.DamageShield) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("damage_shield", out _)) payload["damage_shield"] = buff.Payload.DamageShield;
        if (buff.Payload.Has(BuffPayloadField.HealingReceivedMult) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("healing_received_mult", out _)) payload["healing_received_mult"] = buff.Payload.HealingReceivedMult;
        if (buff.Payload.Has(BuffPayloadField.HpPerTick) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("hp_per_tick", out _)) payload["hp_per_tick"] = buff.Payload.HpPerTick;
        if (buff.Payload.Has(BuffPayloadField.HpPercentPerTick) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("hp_percent_per_tick", out _)) payload["hp_percent_per_tick"] = buff.Payload.HpPercentPerTick;
        if (buff.Payload.Has(BuffPayloadField.HpRegen) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("hp_regen", out _)) payload["hp_regen"] = buff.Payload.HpRegen;
        if (buff.Payload.Has(BuffPayloadField.HpRegenMult) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("hp_regen_mult", out _)) payload["hp_regen_mult"] = buff.Payload.HpRegenMult;
        if (buff.Payload.Has(BuffPayloadField.ItemSlowPercent) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("item_slow_percent", out _)) payload["item_slow_percent"] = buff.Payload.ItemSlowPercent;
        if (buff.Payload.Has(BuffPayloadField.LifeSteal) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("life_steal", out _)) payload["life_steal"] = buff.Payload.LifeSteal;
        if (buff.Payload.Has(BuffPayloadField.LifeStealMult) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("life_steal_mult", out _)) payload["life_steal_mult"] = buff.Payload.LifeStealMult;
        if (buff.Payload.Has(BuffPayloadField.MagicShield) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("magic_shield", out _)) payload["magic_shield"] = buff.Payload.MagicShield;
        if (buff.Payload.Has(BuffPayloadField.MaxHp) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("max_hp", out _)) payload["max_hp"] = buff.Payload.MaxHp;
        if (buff.Payload.Has(BuffPayloadField.MaxMp) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("max_mp", out _)) payload["max_mp"] = buff.Payload.MaxMp;
        if (buff.Payload.Has(BuffPayloadField.MaxManaRegenPercent) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("max_mana_regen_percent", out _)) payload["max_mana_regen_percent"] = buff.Payload.MaxManaRegenPercent;
        if (buff.Payload.Has(BuffPayloadField.MissChance) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("miss_chance", out _)) payload["miss_chance"] = buff.Payload.MissChance;
        if (buff.Payload.Has(BuffPayloadField.MpPerTick) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("mp_per_tick", out _)) payload["mp_per_tick"] = buff.Payload.MpPerTick;
        if (buff.Payload.Has(BuffPayloadField.MpRegen) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("mp_regen", out _)) payload["mp_regen"] = buff.Payload.MpRegen;
        if (buff.Payload.Has(BuffPayloadField.MoveSpeedFlat) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("move_speed_flat", out _)) payload["move_speed_flat"] = buff.Payload.MoveSpeedFlat;
        if (buff.Payload.Has(BuffPayloadField.MoveSpeedMult) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("move_speed_mult", out _)) payload["move_speed_mult"] = buff.Payload.MoveSpeedMult;
        if (buff.Payload.Has(BuffPayloadField.MoveSpeedPercent) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("move_speed_percent", out _)) payload["move_speed_percent"] = buff.Payload.MoveSpeedPercent;
        if (buff.Payload.Has(BuffPayloadField.Period) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("period", out _)) payload["period"] = buff.Payload.Period;
        if (buff.Payload.Has(BuffPayloadField.PeriodElapsed) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("period_elapsed", out _)) payload["period_elapsed"] = buff.Payload.PeriodElapsed;
        if (buff.Payload.Has(BuffPayloadField.Reflect) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("reflect", out _)) payload["reflect"] = buff.Payload.Reflect;
        if (buff.Payload.Has(BuffPayloadField.ReflectScale) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("reflect_scale", out _)) payload["reflect_scale"] = buff.Payload.ReflectScale;
        if (buff.Payload.Has(BuffPayloadField.SpellBlock) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("spell_block", out _)) payload["spell_block"] = buff.Payload.SpellBlock;
        if (buff.Payload.Has(BuffPayloadField.SpellBlockMult) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("spell_block_mult", out _)) payload["spell_block_mult"] = buff.Payload.SpellBlockMult;
        if (buff.Payload.Has(BuffPayloadField.SpellVamp) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("spell_vamp", out _)) payload["spell_vamp"] = buff.Payload.SpellVamp;
        if (buff.Payload.Has(BuffPayloadField.SpellVampMult) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("spell_vamp_mult", out _)) payload["spell_vamp_mult"] = buff.Payload.SpellVampMult;
        if (buff.Payload.Has(BuffPayloadField.SpellbladeBonus) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("spellblade_bonus", out _)) payload["spellblade_bonus"] = buff.Payload.SpellbladeBonus;
        if (buff.Payload.Has(BuffPayloadField.SpellbladeDelay) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("spellblade_delay", out _)) payload["spellblade_delay"] = buff.Payload.SpellbladeDelay;
        if (buff.Payload.FxNetId != 0 || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("fx_netid", out _)) payload["fx_netid"] = buff.Payload.FxNetId;
        if (buff.Payload.Stacks != 1 || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("stacks", out _)) payload["stacks"] = buff.Payload.Stacks;
        if (buff.Payload.Has(BuffPayloadField.QRatio) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("q_ratio", out _)) payload["q_ratio"] = buff.Payload.QRatio;
        if (buff.Payload.ActionDisabled || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("action_disabled", out _)) payload["action_disabled"] = buff.Payload.ActionDisabled;
        if (buff.Payload.AbilityDisabled || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("ability_disabled", out _)) payload["ability_disabled"] = buff.Payload.AbilityDisabled;
        if (buff.Payload.Invulnerable || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("invulnerable", out _)) payload["invulnerable"] = buff.Payload.Invulnerable;
        if (buff.Payload.MovementDisabled || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("movement_disabled", out _)) payload["movement_disabled"] = buff.Payload.MovementDisabled;
        if (buff.Payload.Silenced || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("silenced", out _)) payload["silenced"] = buff.Payload.Silenced;
        if (buff.Payload.SourceIsPeriodic || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("source_is_periodic", out _)) payload["source_is_periodic"] = buff.Payload.SourceIsPeriodic;
        if (buff.Payload.Stealthed || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("stealthed", out _)) payload["stealthed"] = buff.Payload.Stealthed;
        if (buff.Payload.Untargetable || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("untargetable", out _)) payload["untargetable"] = buff.Payload.Untargetable;
        if (buff.Payload.Has(BuffPayloadField.DamagePerTick) || original.ValueKind == JsonValueKind.Object && original.TryGetProperty("damage_type", out _))
            payload["damage_type"] = buff.Payload.DamageType.ToString().ToLowerInvariant();
        row["payload"] = payload;
        return row;
    }

    private static void CompareEntities(JsonElement expected, WorldState world, string context,
        IReadOnlySet<uint>? movementSoftCapExpected = null)
    {
        if (FixtureRecorder.Enabled)
        {
            var shapes = expected.EnumerateArray().ToArray();
            CorpusSnapshot.Set(expected, world.Entities.Values.Select(entity => SnapshotEntity(entity,
                shapes.FirstOrDefault(row => row.GetProperty("netid").GetUInt32() == entity.NetId) is var row &&
                row.ValueKind == JsonValueKind.Object ? row : shapes[0])).ToArray());
            return;
        }
        TestAssert.Equal(expected.GetArrayLength(), world.Entities.Count, context + " entity count");
        foreach (var raw in expected.EnumerateArray())
        {
            var netId = raw.GetProperty("netid").GetUInt32();
            var actual = world.Get(netId) ?? throw new InvalidOperationException(
                context + ": missing entity " + netId);
            double? expectedSpeed = null;
            if (movementSoftCapExpected?.Contains(netId) == true)
                expectedSpeed = ArchivedMovementSoftCap(raw.GetProperty("speed").GetDouble());
            CompareEntity(raw, actual, context + " entity " + netId, expectedSpeed);
        }
    }

    private static void CompareEntity(JsonElement expected, Entity actual, string context,
        double? expectedSpeed = null)
    {
        Near(expected, "hp", actual.Health, context);
        Near(expected, "max_hp", actual.MaxHealth, context);
        Near(expected, "mana", actual.Mana, context);
        Near(expected, "max_mana", actual.MaxMana, context);
        Near(expected, "armor", actual.Armor, context);
        Near(expected, "spell_block", actual.SpellBlock, context);
        Near(expected, "atk_damage", actual.AttackDamage, context);
        Near(expected, "ability_power", actual.AbilityPower, context);
        if (expectedSpeed.HasValue)
            TestAssert.Near(expectedSpeed.Value, actual.Speed, 1e-8, context + " speed soft cap");
        else
            Near(expected, "speed", actual.Speed, context);
        Near(expected, "atk_range", actual.AttackRange, context);
        TestAssert.Near(expected.GetProperty("atk_period").GetDouble(),
            actual.AttackPeriod, 1e-6, context + " attack period");
        Near(expected, "attack_speed_mod", actual.AttackSpeedMod, context);
        TestAssert.Near(expected.GetProperty("cooldown_mod").GetDouble(), actual.CooldownMod, 1e-6, context + " cooldown");
        Near(expected, "hp_regen", actual.HealthRegen, context);
        Near(expected, "mp_regen", actual.ManaRegen, context);
        NearIfPresent(expected, "life_steal", actual.LifeSteal, context);
        NearIfPresent(expected, "spell_vamp", actual.SpellVamp, context);
        NearIfPresent(expected, "crit_chance", actual.CritChance, context);
        NearIfPresent(expected, "crit_multiplier", actual.CritMultiplier, context);
        NearIfPresent(expected, "dodge", actual.Dodge, context);
        NearIfPresent(expected, "hardness", actual.Hardness, context);
        NearIfPresent(expected, "armor_pen_flat", actual.ArmorPenetrationFlat, context);
        NearIfPresent(expected, "armor_pen_percent", actual.ArmorPenetrationPercent, context);
        NearIfPresent(expected, "magic_pen_flat", actual.MagicPenetrationFlat, context);
        NearIfPresent(expected, "magic_pen_percent", actual.MagicPenetrationPercent, context);
        Near(expected, "attack_cd", actual.AttackCooldown, context);
        TestAssert.Near(expected.GetProperty("windup").GetDouble(),
            actual.AttackWindup, 1e-6, context + " windup");
        Near(expected, "missile_speed", actual.MissileSpeed, context);
        Near(expected, "radius", actual.Radius, context);
        Near(expected, "expires_at", actual.ExpiresAt, context);
        Near(expected, "respawn_at", actual.RespawnAt, context);
        // Batch 149 step 4: `odynStoredDamage` is a retired field (Entity.OdynStoredDamage is gone; the window
        // keeps what it prevented in the shipped Spells/OdynsVeil.luaobj's own CharVars.StoredDamage), so the
        // archived rows that still carry the key are superseded archived data here.
        NearIfPresent(expected, "gold_reward", actual.GoldReward, context);
        NearIfPresent(expected, "global_gold_reward", actual.GlobalGoldReward, context);
        NearIfPresent(expected, "xp_reward", actual.ExperienceReward, context);
        Near(expected, "gold", actual.Gold, context);
        Near(expected, "exp", actual.Experience, context);
        TestAssert.Equal(expected.GetProperty("alive").GetBoolean(), actual.Alive, context + " alive");
        TestAssert.Equal(expected.GetProperty("created").GetBoolean(), actual.Created, context + " created");
        TestAssert.Equal(expected.GetProperty("visible").GetBoolean(), actual.Visible, context + " visible");
        TestAssert.Equal(expected.GetProperty("unitType").GetInt32(), (int)actual.UnitType, context + " unit type");
        TestAssert.Equal(expected.GetProperty("team").GetInt32(), (int)actual.Team, context + " team");
        TestAssert.Equal(expected.GetProperty("name").GetString(), actual.Name, context + " name");
        TestAssert.Equal(expected.GetProperty("champion").GetString(), actual.ChampionName, context + " champion");
        TestAssert.Equal(expected.GetProperty("owner_netid").GetUInt32(), actual.OwnerNetId, context + " owner");
        TestAssert.Equal(expected.GetProperty("isWard").GetBoolean(), actual.IsWard, context + " ward");
        TestAssert.Equal(expected.GetProperty("revealsStealth").GetBoolean(), actual.RevealsStealth,
            context + " reveals stealth");
        ComparePoint(expected.GetProperty("position"), actual.Position, context + " position");
        ComparePoint(expected.GetProperty("facing"), actual.Facing, context + " facing");
        var expectedAttackTarget = expected.GetProperty("attack_target");
        TestAssert.Equal(expectedAttackTarget.ValueKind == JsonValueKind.Null
                ? (uint?)null : expectedAttackTarget.GetUInt32(),
            actual.AttackTarget, context + " attack target");
        var expectedDeath = expected.GetProperty("death_time");
        TestAssert.Equal(expectedDeath.ValueKind == JsonValueKind.Null
                ? (double?)null : expectedDeath.GetDouble(), actual.DeathTime, context + " death time");
        CompareLists(expected, "spell_ranks", actual.SpellRanks.Select(value => (double)value), context);
        CompareLists(expected, "spell_cooldowns", actual.SpellCooldowns, context);
        CompareLists(expected, "summoner_cooldowns", actual.SummonerCooldowns, context);
        CompareLists(expected, "item_cooldowns", actual.ItemCooldowns, context);
        if (expected.TryGetProperty("equippedItemIds", out var equipped))
            TestAssert.Equal(equipped.EnumerateArray().Select(value => value.GetInt32()),
                actual.EquippedItemIds.OrderBy(value => value), context + " equipped ids");
        CompareInventory(expected.GetProperty("inventory"), actual, context);
        CompareCounters(expected.GetProperty("itemCounters"), actual, context);
        CompareBuffs(expected.GetProperty("buffs"), actual, context);
    }

    private static void CompareInventory(JsonElement expected, Entity actual, string context)
    {
        TestAssert.Equal(expected.GetArrayLength(), actual.Inventory.Count, context + " inventory count");
        for (var i = 0; i < expected.GetArrayLength(); i++)
        {
            var raw = expected[i];
            var slot = actual.Inventory[i];
            if (raw.ValueKind == JsonValueKind.Null)
            {
                TestAssert.True(slot is null, context + " inventory slot " + i);
                continue;
            }
            TestAssert.True(slot is not null, context + " missing inventory slot " + i);
            TestAssert.Equal(raw.GetProperty("itemId").GetInt32(), slot!.ItemId, context + " item id");
            TestAssert.Equal(raw.GetProperty("count").GetInt32(), slot.Count, context + " item count");
            TestAssert.Equal(raw.GetProperty("charges").GetInt32(), slot.Charges, context + " charges");
            if (raw.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object)
                CompareItemState(state, slot.State, context + " item state");
        }
    }

    private static void CompareItemState(JsonElement expected, InventoryItemState actual, string context)
    {
        foreach (var property in expected.EnumerateObject())
        {
            // Batch 149 step 1: the Bloodthirster's and Warmog's growth counters are the copy's own script
            // InstanceVars now (Items/3072 and 3083) and every archived case that carried a non-zero one is
            // skipped, so their keys are superseded archived data here. Batch 149 step 5: so is all of Rod of
            // Ages' ageing (Items/3027.luaobj's own BonusHealth / BonusMana / BonusAbilityPower), and
            // InventoryItemState is empty.
            if (property.Name is "bloodthirster_attack_damage" or "bloodthirster_life_steal"
                or "warmog_health" or "warmog_regen" or "rod_of_ages_elapsed" or "rod_of_ages_stacks") continue;
            throw new InvalidOperationException("unsupported expected state " + property.Name);
        }
    }

    private static void CompareCounters(JsonElement expected, Entity actual, string context)
    {
        foreach (var property in expected.EnumerateObject())
        {
            // Batch 149 step 1: the snowball trio's stacks and Sunfire Cape's timer are the shipped scripts'
            // own now, so their archived counters are superseded data — every case that records one is
            // skipped (see the pass over the passive corpus) and their numbers live in the production checks
            // named there. Batch 149 step 5: so are the Tear family's reservoirs (CharVars.TearBonusMana and
            // the copies' InstanceVars.CooldownResevoir), Sword of the Divine's and Ionic Spark's fourth-hit arm
            // (the window buff's InstanceVars.AttackCounter), Force of Nature's clock
            // (Spells/ForceofNature.luaobj's own BuffOnUpdateActions) and Banshee's Veil's purchase bookkeeping
            // (Items/3102.luaobj's OnActivate); HeroItemState keeps one flag no script can answer for.
            if (property.Name is "snowball_3041" or "snowball_3138" or "snowball_3141"
                or "sunfire_elapsed" or "sunfire_initialized"
                or "tear_bonus_mana" or "tear_spell_reservoir" or "tear_spell_refill"
                or "manamune_attack_reservoir" or "manamune_attack_refill"
                or "3131" or "3178" or "item_3131" or "item_3178" or "force_of_nature_elapsed"
                or "banshee_initial_pending" or "banshee_pet_initialized") continue;
            throw new InvalidOperationException("unsupported expected counter " + property.Name);
        }
    }

    private static void CompareBuffs(JsonElement expected, Entity actual, string context)
    {
        TestAssert.Equal(expected.GetArrayLength(), actual.Buffs.Count, context + " buff count");
        for (var i = 0; i < expected.GetArrayLength(); i++)
        {
            var raw = expected[i];
            var buff = actual.Buffs[i];
            TestAssert.Equal(raw.GetProperty("name").GetString(), buff.Name, context + " buff name");
            TestAssert.Equal(raw.GetProperty("slot").GetInt32(), buff.Slot, context + " buff slot");
            TestAssert.Equal(raw.GetProperty("type").GetInt32(), (int)buff.Type, context + " buff type");
            TestAssert.Equal(raw.GetProperty("caster").GetUInt32(), buff.CasterNetId, context + " buff caster");
            var expectedStacks = raw.TryGetProperty("stacks", out var stacks)
                ? stacks.GetInt32() : 1;
            TestAssert.Equal(expectedStacks, buff.Stacks, context + " buff stacks");
            var max = raw.TryGetProperty("max_stack", out var maxValue)
                ? maxValue : default;
            TestAssert.Equal(max.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    ? (int?)null : max.GetInt32(),
                buff.MaxStack, context + " buff max stack");
            Near(raw, "expires_at", buff.ExpiresAt, context + " buff");
            ComparePayload(raw.GetProperty("payload"), buff.Payload, context + " payload");
        }
    }

    private static void ComparePayload(JsonElement expected, BuffPayload actual, string context)
    {
        foreach (var property in expected.EnumerateObject())
        {
            var key = property.Name;
            if (property.Value.ValueKind == JsonValueKind.Number)
            {
                var actualValue = PayloadNumber(actual, key);
                TestAssert.Near(property.Value.GetDouble(), actualValue, 1e-8, context + " " + key);
            }
            else if (property.Value.ValueKind == JsonValueKind.True ||
                     property.Value.ValueKind == JsonValueKind.False)
            {
                TestAssert.Equal(property.Value.GetBoolean(), PayloadBool(actual, key), context + " " + key);
            }
            else if (property.Value.ValueKind == JsonValueKind.String && key == "damage_type")
            {
                TestAssert.Equal(property.Value.GetString(), actual.DamageType.ToString().ToLowerInvariant(),
                    context + " " + key);
            }
            else
            {
                throw new InvalidOperationException(context + ": unsupported payload " + key);
            }
        }
    }

    private static double PayloadNumber(BuffPayload payload, string key) => key switch
    {
        "ability_power" => payload.AbilityPower,
        "armor" => payload.Armor,
        "armor_pen_flat" => payload.ArmorPenFlat,
        "armor_pen_percent" => payload.ArmorPenPercent,
        "attack_damage" => payload.AttackDamage,
        "attack_speed_mod" => payload.AttackSpeedMod,
        "attack_speed_mult" => payload.AttackSpeedMult,
        "cooldown_mod" => payload.CooldownMod,
        "cooldown_mod_base" => payload.CooldownModBase,
        "cooldown_mod_per_level" => payload.CooldownModPerLevel,
        "crit_chance" => payload.CritChance,
        "dodge_piercing" => payload.DodgePiercing,
        "damage_per_tick" => payload.DamagePerTick,
        "damage_shield" => payload.DamageShield,
        "healing_received_mult" => payload.HealingReceivedMult,
        "hp_per_tick" => payload.HpPerTick,
        "hp_percent_per_tick" => payload.HpPercentPerTick,
        "hp_regen" => payload.HpRegen,
        "hp_regen_mult" => payload.HpRegenMult,
        "item_slow_percent" => payload.ItemSlowPercent,
        "life_steal" => payload.LifeSteal,
        "life_steal_mult" => payload.LifeStealMult,
        "magic_shield" => payload.MagicShield,
        "max_hp" => payload.MaxHp,
        "max_mp" => payload.MaxMp,
        "max_mana_regen_percent" => payload.MaxManaRegenPercent,
        "miss_chance" => payload.MissChance,
        "mp_per_tick" => payload.MpPerTick,
        "mp_regen" => payload.MpRegen,
        "move_speed_flat" => payload.MoveSpeedFlat,
        "move_speed_mult" => payload.MoveSpeedMult,
        "move_speed_percent" => payload.MoveSpeedPercent,
        "period" => payload.Period,
        "period_elapsed" => payload.PeriodElapsed,
        "reflect" => payload.Reflect,
        "reflect_scale" => payload.ReflectScale,
        "spell_block" => payload.SpellBlock,
        "spell_block_mult" => payload.SpellBlockMult,
        "spell_vamp" => payload.SpellVamp,
        "spell_vamp_mult" => payload.SpellVampMult,
        "spellblade_bonus" => payload.SpellbladeBonus,
        "spellblade_delay" => payload.SpellbladeDelay,
        "fx_netid" => payload.FxNetId,
        "stacks" => payload.Stacks,
        "q_ratio" => payload.QRatio,
        _ => throw new InvalidOperationException("unsupported numeric payload " + key),
    };

    private static bool PayloadBool(BuffPayload payload, string key) => key switch
    {
        "action_disabled" => payload.ActionDisabled,
        "ability_disabled" => payload.AbilityDisabled,
        "invulnerable" => payload.Invulnerable,
        "movement_disabled" => payload.MovementDisabled,
        "silenced" => payload.Silenced,
        "source_is_periodic" => payload.SourceIsPeriodic,
        "stealthed" => payload.Stealthed,
        "untargetable" => payload.Untargetable,
        _ => throw new InvalidOperationException("unsupported boolean payload " + key),
    };

    private static void CompareLists(JsonElement expected, string key,
        IEnumerable<double> actual, string context)
    {
        var expectedValues = expected.GetProperty(key).EnumerateArray().Select(value => value.GetDouble()).ToArray();
        var actualValues = actual.ToArray();
        TestAssert.Equal(expectedValues.Length, actualValues.Length, context + " " + key + " count");
        for (var i = 0; i < actualValues.Length; i++)
            TestAssert.Near(expectedValues[i], actualValues[i], 1e-8, context + " " + key + "[" + i + "]");
    }

    private static void Near(JsonElement expected, string key, double actual, string context)
    {
        var value = expected.GetProperty(key).GetDouble();
        TestAssert.Near(value, actual, 1e-8, context + " " + key +
            " expected=" + value.ToString("R", CultureInfo.InvariantCulture) +
            " actual=" + actual.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void NearIfPresent(JsonElement expected, string key, double actual, string context)
    {
        if (expected.TryGetProperty(key, out var value))
            TestAssert.Near(value.GetDouble(), actual, 1e-8, context + " " + key);
    }

    private static void ComparePoint(JsonElement expected, Position2 actual, string context)
    {
        var x = expected[0].GetDouble();
        var z = expected.GetArrayLength() == 3 ? expected[2].GetDouble() : expected[1].GetDouble();
        TestAssert.Near(x, actual.X, 1e-8, context + " x");
        TestAssert.Near(z, actual.Z, 1e-8, context + " z");
    }

    private static IReadOnlySet<uint> ServerOnlyBuffHashes(JsonElement initial)
    {
        var hashes = new HashSet<uint>();
        foreach (var entity in initial.EnumerateArray())
        foreach (var buff in entity.GetProperty("buffs").EnumerateArray())
        {
            var name = buff.GetProperty("name").GetString();
            if (!string.IsNullOrWhiteSpace(name) && !GameData.Default.HasClientBuff(name))
                hashes.Add(SpellPackets.BuffNameHash(name));
        }
        return hashes;
    }

    private static bool IsServerOnlyBuffRemoval(JsonElement packet,
        IReadOnlySet<uint>? serverOnlyBuffHashes)
    {
        if (serverOnlyBuffHashes is null || serverOnlyBuffHashes.Count == 0 ||
            !packet.TryGetProperty("plaintext", out var raw)) return false;
        var plaintext = Convert.FromHexString(raw.GetString()!);
        if (plaintext.Length is not (10 or 16) || plaintext[0] != 0x7F) return false;
        for (var index = 10; index < plaintext.Length; index++)
            if (plaintext[index] != 0) return false;
        return serverOnlyBuffHashes.Contains(BinaryPrimitives.ReadUInt32LittleEndian(plaintext.AsSpan(6, 4)));
    }

    private static void ComparePackets(JsonElement expected,
        IReadOnlyList<PlaintextPacket> actual, LegacyBlowfishCipher cipher, string context,
        IReadOnlySet<uint>? serverOnlyBuffHashes = null,
        IReadOnlyList<PlaintextPacket>? dynamicTooltipPackets = null,
        IReadOnlySet<uint>? movementSoftCapExpected = null, bool historicalPenetration = false)
    {
        // This oracle validates emitted refreshes; a spell callback need not refresh stats.
        if (dynamicTooltipPackets is { Count: > 0 } && actual.Any(packet => packet.Plaintext.Span[0] == 0x83))
        {
            var tooltips = actual.Where(packet => packet.Plaintext.Span[0] == 0x83).ToArray();
            TestAssert.Equal(dynamicTooltipPackets.Count, tooltips.Length, context + $" tooltip count expected={dynamicTooltipPackets.Count} actual={tooltips.Length}; expected=" + string.Join("|", dynamicTooltipPackets.Select(p => Convert.ToHexString(p.Plaintext.Span))) + "; actual=" + string.Join("|", tooltips.Select(p => Convert.ToHexString(p.Plaintext.Span))));
            for (var i = 0; i < tooltips.Length; i++)
            {
                TestAssert.Equal(dynamicTooltipPackets[i].Channel, tooltips[i].Channel);
                TestAssert.Equal(dynamicTooltipPackets[i].Reliability, tooltips[i].Reliability);
                TestAssert.Equal(dynamicTooltipPackets[i].ToWireBytes(cipher), tooltips[i].ToWireBytes(cipher));
            }
        }
        if (FixtureRecorder.Enabled)
        {
            CorpusSnapshot.Set(expected, actual.Select(packet => new
            {
                channel = packet.Channel, flags = (byte)packet.Reliability,
                wire = Convert.ToHexString(packet.ToWireBytes(cipher)).ToLowerInvariant(),
                plaintext = Convert.ToHexString(cipher.Decrypt(packet.ToWireBytes(cipher))).ToLowerInvariant(),
            }).ToArray());
            return;
        }
        TestAssert.Equal(expected.GetArrayLength(), actual.Count, context + " packet count");
        for (var index = 0; index < actual.Count; index++)
        {
            var row = expected[index];
            var packet = actual[index];
            TestAssert.Equal(row.GetProperty("channel").GetByte(), packet.Channel, context + " channel");
            TestAssert.Equal(row.GetProperty("flags").GetByte(), (byte)packet.Reliability, context + " flags");
            var wire = packet.ToWireBytes(cipher);
            TestAssert.Equal(Convert.FromHexString(row.GetProperty("plaintext").GetString()!), cipher.Decrypt(wire), context + " plaintext");
            TestAssert.Equal(Convert.FromHexString(row.GetProperty("wire").GetString()!), wire, context + " wire");
        }
    }

    private static IReadOnlyList<PlaintextPacket> BuildExpectedDynamicTooltipPackets(
        Entity hero, IReadOnlyList<DynamicTooltipValue> before, string operation)
    {
        // Batch 149 step 1: the snowball trio's, The Bloodthirster's and Warmog's tooltip rows are the copy's
        // own BBSetSpellToolTipVar writes now (Items/3041, 3138, 3141, 3072 and 3083), so the hand-written
        // row checks of their kill / assist / death callbacks are gone with those archived cases. What is
        // left is the unconverted Rod of Ages' ageing refresh.
        var final = CaptureDynamicTooltipValues(hero);
        if (final.Count == 0) return Array.Empty<PlaintextPacket>();

        if (operation == "tick")
        {
            var previous = before.ToDictionary(value => value.Slot);
            var working = before.ToDictionary(value => value.Slot);
            var snapshots = new List<PlaintextPacket>();
            foreach (var value in final)
            {
                // TickRodOfAges refreshes after each changed slot: the first
                // duplicate therefore publishes a partial state; later packets
                // include already-updated earlier slots.
                if (value.ItemId != 3027 || !previous.TryGetValue(value.Slot, out var old) ||
                    TooltipValueEqual(old, value)) continue;
                working[value.Slot] = value;
                snapshots.Add(BuildDynamicTooltipPacket(hero.NetId,
                    working.Values.OrderBy(item => item.Slot).ToArray()));
            }
            if (snapshots.Count > 0) return snapshots.ToArray();
        }

        return [BuildDynamicTooltipPacket(hero.NetId, final)];
    }

    private static IReadOnlyList<PlaintextPacket> BuildExpectedOnHitTooltipPackets(
        Entity attacker, Entity target, string operation)
    {
        if (operation != "basicHook") return Array.Empty<PlaintextPacket>();
        var packets = new List<PlaintextPacket>();
        if (attacker.Buffs.FirstOrDefault(buff => buff.Name == "WitsEndCounter") is { } wits)
            packets.Add(SpellPackets.BuffTooltipVariables(attacker.NetId, wits.Slot,
                (float)(5.0 * wits.Stacks), 0, 0));
        if (target.Buffs.FirstOrDefault(buff => buff.Name == "MaladyCounter") is { } malady)
            packets.Add(SpellPackets.BuffTooltipVariables(target.NetId, malady.Slot,
                (float)(6.0 * malady.Stacks), 0, 0));
        return packets;
    }

    private static JsonElement AdaptGunbladeFinalFx(JsonElement expected, uint recipientNetId)
    {
        var root = JsonNode.Parse(expected.GetRawText())!.AsArray();
        foreach (var entity in root)
        {
            if (entity!["netid"]?.GetValue<uint>() != recipientNetId) continue;
            foreach (var buff in entity!["buffs"]!.AsArray())
            {
                if (buff!["name"]?.GetValue<string>() != "BilgewaterCutlass") continue;
                var payload = buff["payload"]?.AsObject();
                if (payload?["fx_netid"] is not JsonNode fx) continue;
                payload["fx_netid"] = checked(fx.GetValue<uint>() + 2u);
            }
        }
        return JsonSerializer.SerializeToElement(root);
    }

    private static IReadOnlyList<DynamicTooltipValue> CaptureDynamicTooltipValues(Entity hero)
    {
        var values = new List<DynamicTooltipValue>();
        for (var index = 0; index < hero.Inventory.Count; index++)
        {
            var slot = hero.Inventory[index];
            if (slot is null) continue;
            // Batch 149 step 1: The Bloodthirster (3072) and Warmog's Armor (3083) write their own rows from
            // their copy's InstanceVars through BBSetSpellToolTipVar now (asserted in
            // items-live-dynamic-tooltip-slots), so this harness has no stand-in row for them.
            // Batch 149 step 5: so do the Tear family (3003, 3004, 3070 — CharVars.TearBonusMana) and Rod of
            // Ages (3027 — its copy's InstanceVars.BonusHealth / BonusMana / BonusAbilityPower), so this harness
            // has no stand-in row for those either, and no item's dynamic tooltip row is left to it.
        }
        return values;
    }

    private static PlaintextPacket BuildDynamicTooltipPacket(
        uint heroNetId, IReadOnlyList<DynamicTooltipValue> values) =>
        SpellPackets.SpellTooltipVariables(values.Select(value =>
            new SpellTooltipEntry(checked((byte)value.Slot), (float)value.First,
                (float)value.Second, (float)value.Third)).ToArray(), heroNetId);

    private static bool TooltipValueEqual(DynamicTooltipValue left,
        DynamicTooltipValue right) =>
        left.ItemId == right.ItemId && left.Slot == right.Slot &&
        left.First == right.First && left.Second == right.Second &&
        left.Third == right.Third;

    private readonly record struct DynamicTooltipValue(
        int Slot, int ItemId, double First, double Second, double Third);

    private static bool Adapt3157CastMask(string context, byte[] expectedPlaintext)
    {
        // The maintained 3157 fixture predates the native stats-mask field
        // ordering.  Only its two documented one-byte masks differ; keep the
        // rest of the packet and its exact wire bytes under comparison.
        if (!context.StartsWith("3157-", StringComparison.Ordinal) ||
            expectedPlaintext.Length <= 31 || expectedPlaintext[0] != 0xCC ||
            expectedPlaintext[27] != 0xFF || expectedPlaintext[31] != 0xF0)
            return false;
        expectedPlaintext[27] = 0xF0;
        expectedPlaintext[31] = 0xFF;
        return true;
    }

    private static bool AdaptMovementSoftCap(byte[] expectedPlaintext,
        IReadOnlySet<uint>? expectedEntities)
    {
        // Keep archived full-stat snapshots strict except for the reviewed
        // Classic soft-cap projection. Archived waypoint packets carry no
        // speed field; the new NPC 0xCC map-8/property-4 updates are projected
        // into the expected stream separately from this hero stat rewrite.
        if (expectedEntities is null || expectedPlaintext.Length != 176 ||
            expectedPlaintext[0] != 0xCC || expectedPlaintext[10] != 0x0B ||
            expectedPlaintext[15] != 0xFF)
            return false;
        var netId = BinaryPrimitives.ReadUInt32LittleEndian(expectedPlaintext.AsSpan(11));
        if (!expectedEntities.Contains(netId))
            return false;
        var expectedSpeed = BitConverter.ToSingle(expectedPlaintext, 163);
        if (expectedSpeed >= 220.0 && expectedSpeed <= 415.0)
            return false;
        BinaryPrimitives.WriteSingleLittleEndian(expectedPlaintext.AsSpan(163),
            (float)ArchivedMovementSoftCap(expectedSpeed));
        return true;
    }

    private static IReadOnlySet<uint> MovementSoftCapEntities(JsonElement final)
    {
        var expected = new HashSet<uint>();
        foreach (var entity in final.EnumerateArray())
        {
            var speed = entity.GetProperty("speed").GetDouble();
            if (speed <= 415.0)
                continue;
            expected.Add(entity.GetProperty("netid").GetUInt32());
        }
        return expected;
    }

    private static IReadOnlySet<uint> MovementLowerSoftCapEntities(JsonElement entities)
    {
        var expected = new HashSet<uint>();
        foreach (var entity in entities.EnumerateArray())
        {
            // The lower curve is a hero compatibility rule. NPC movement
            // keeps its existing cap behavior, and wards/turrets can have a
            // zero speed row without being eligible for this projection.
            var unitType = entity.GetProperty("unitType").GetInt32();
            if (unitType != 0 || entity.GetProperty("speed").GetDouble() >= 220.0)
                continue;
            var hasNegativeMovementPayload = entity.GetProperty("buffs").EnumerateArray()
                .Any(buff => buff.GetProperty("payload").EnumerateObject()
                    .Any(property => property.Name is ("move_speed_mult" or "move_speed_percent") &&
                        property.Value.GetDouble() < 0.0));
            if (hasNegativeMovementPayload)
                expected.Add(entity.GetProperty("netid").GetUInt32());
        }
        return expected;
    }

    private static IReadOnlySet<uint> OnHitMovementLowerSoftCapEntities(
        JsonElement testCase, JsonElement entities, int caseIndex)
    {
        var expected = new HashSet<uint>(MovementLowerSoftCapEntities(entities));
        // Some reviewed item-slow events retain an intermediate full hero
        // snapshot below 220 even though the final state has already been
        // restored to 310. Keep that packet projection tied to the approved
        // hero cases and target id, rather than deriving it from final speed.
        if (testCase.GetProperty("targetKind").GetString() == "hero" &&
            ReviewedHeroSlowItems(caseIndex) is not null)
            expected.Add(testCase.GetProperty("target").GetUInt32());
        return expected;
    }

    private static bool IsReviewedLowerMovementCase(JsonElement testCase)
    {
        if (testCase.TryGetProperty("itemId", out var itemId))
            return itemId.GetInt32() is 3143 or 3144 or 3146;
        return testCase.GetProperty("items").EnumerateArray()
            .Any(item => item.GetInt32() is 3022 or 3044 or 3078 or 3123 or 3184);
    }

    private static JsonElement ProjectNpcMovementPackets(JsonElement testCase,
        JsonElement expectedEvent, int caseIndex, int eventIndex,
        LegacyBlowfishCipher cipher,
        IReadOnlyDictionary<uint, uint> previousNpcReplicationSyncs)
    {
        if (testCase.GetProperty("targetKind").GetString() is not ("minion" or "neutral"))
            return expectedEvent;

        var expectedItems = ReviewedNpcSlowItems(caseIndex);
        if (expectedItems is null) return expectedEvent;

        var targetNetId = testCase.GetProperty("target").GetUInt32();
        var initialTarget = testCase.GetProperty("initial").EnumerateArray()
            .Single(entity => entity.GetProperty("netid").GetUInt32() == targetNetId);
        if (!initialTarget.GetProperty("created").GetBoolean() ||
            !initialTarget.GetProperty("visible").GetBoolean())
            throw new InvalidDataException(
                $"NPC movement projection case {caseIndex} is not created and visible");

        var itemIds = testCase.GetProperty("items").EnumerateArray()
            .Select(item => item.GetInt32()).ToArray();
        if (!itemIds.SequenceEqual(expectedItems))
            throw new InvalidDataException(
                $"NPC movement projection item sequence changed for case {caseIndex}");

        var archivedSlows = testCase.GetProperty("events")[0].GetProperty("entities")
            .EnumerateArray()
            .Single(entity => entity.GetProperty("netid").GetUInt32() == targetNetId);
        var archivedBuffs = archivedSlows.GetProperty("buffs").EnumerateArray().ToArray();
        var slowBuffs = archivedBuffs
            .Where(buff => buff.TryGetProperty("payload", out var payload) &&
                payload.TryGetProperty("item_slow_percent", out var percent) &&
                percent.GetDouble() > 0.0)
            .ToArray();
        var slowPercents = slowBuffs
            .Select(buff => buff.GetProperty("payload")
                .GetProperty("item_slow_percent").GetDouble())
            .Distinct()
            .OrderBy(percent => percent)
            .ToArray();
        if (slowPercents.Length == 0)
            throw new InvalidDataException(
                $"NPC movement projection case {caseIndex} has no archived item slow payload");

        var root = JsonNode.Parse(expectedEvent.GetRawText())?.AsObject()
            ?? throw new InvalidDataException("NPC movement expected event is not an object");
        var packets = root["packets"]?.AsArray()
            ?? throw new InvalidDataException("NPC movement expected packets are missing");
        var baseline = initialTarget.GetProperty("speed").GetDouble();
        var marker = archivedBuffs.SingleOrDefault(buff =>
            buff.GetProperty("name").GetString() == "ItemSlow");
        if (marker.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new InvalidDataException(
                $"NPC movement projection case {caseIndex} has no ItemSlow marker");
        var markerSlot = checked((byte)marker.GetProperty("slot").GetInt32());
        var internalSlow = slowBuffs.Where(buff =>
                buff.GetProperty("name").GetString()?.StartsWith("Internal_", StringComparison.Ordinal) == true)
            .Select(buff => checked((byte)buff.GetProperty("slot").GetInt32()))
            .ToArray();
        if (internalSlow.Length != slowPercents.Length)
            throw new InvalidDataException(
                $"NPC movement projection case {caseIndex} slow payload set changed");

        var target = expectedEvent.GetProperty("entities").EnumerateArray()
            .Single(entity => entity.GetProperty("netid").GetUInt32() == targetNetId);
        var finalSpeed = target.GetProperty("speed").GetDouble();
        var expectedSpeed = eventIndex == 8
            ? baseline : baseline * (1.0 - slowPercents[^1]);
        TestAssert.Near(expectedSpeed, finalSpeed, 1e-8,
            $"onhit-{caseIndex} archived NPC final speed projection");
        var movementAdds = slowPercents.Length;

        var restoreAfter = -1;
        if (eventIndex == 8)
        {
            // A sole contribution restores after its internal 7F. With the
            // 30% and 35% pair, the weaker row expires first and ItemSlow's
            // batched removal sends one delta after the marker 7F.
            var restoreSlot = movementAdds == 1 ? internalSlow[0] : markerSlot;
            restoreAfter = FindBuffRemovalIndex(packets, restoreSlot) + 1;
            if (restoreAfter <= 0)
                throw new InvalidDataException(
                    $"NPC movement removal packet missing for case {caseIndex}");
        }

        // The new speed packet shares the NPC replication counter with old
        // health/stat maps. Shift only target replication packets after each
        // inserted packet; old packet order and payload fields stay strict.
        if (eventIndex > 0)
        {
            var oldSyncBeforeRestore = restoreAfter < 0 ? 0u : MaxNpcReplicationSync(
                packets, targetNetId, restoreAfter);
            for (var index = 0; index < packets.Count; index++)
            {
                var offset = checked((uint)movementAdds +
                    (restoreAfter >= 0 && index >= restoreAfter ? 1u : 0u));
                ShiftNpcReplicationSync(packets[index]?.AsObject(), targetNetId,
                    offset, cipher);
            }

            if (restoreAfter >= 0)
            {
                var previousSync = previousNpcReplicationSyncs
                    .GetValueOrDefault(targetNetId);
                var movement = CombatPackets.MinionMovementSpeed(targetNetId,
                    (float)baseline, checked(Math.Max(previousSync,
                        oldSyncBeforeRestore + (uint)movementAdds) + 1u));
                packets.Insert(restoreAfter, ReplayPacketNode(movement, cipher));
                return JsonSerializer.SerializeToElement(root);
            }
            return JsonSerializer.SerializeToElement(root);
        }

        if (eventIndex == 0)
        {
            foreach (var packet in packets)
            {
                var plaintext = packet?["plaintext"]?.GetValue<string>();
                if (plaintext is null)
                    throw new InvalidDataException(
                        $"NPC movement case {caseIndex} has an unexpected archived packet");
                var bytes = Convert.FromHexString(plaintext);
                if (bytes.Length < 25 || bytes[0] != SpellPackets.BuffAddOpcode)
                    throw new InvalidDataException(
                        $"NPC movement case {caseIndex} has an unexpected archived packet");
            }

            var markerIndexes = FindBuffPacketIndexes(packets, SpellPackets.BuffAddOpcode,
                markerSlot);
            if (markerIndexes.Count < movementAdds)
                throw new InvalidDataException(
                    $"NPC movement marker packet count changed for case {caseIndex}");
            var transitions = new List<(double Speed, int AfterPacket)>();
            if (movementAdds == 1)
            {
                transitions.Add((baseline * (1.0 - slowPercents[0]),
                    markerIndexes[0] + 1));
            }
            else if (movementAdds == 2)
            {
                transitions.Add((baseline * (1.0 - slowPercents[0]),
                    markerIndexes[0] + 1));
                transitions.Add((baseline * (1.0 - slowPercents[1]),
                    markerIndexes[^1] + 1));
            }
            else
            {
                throw new InvalidDataException(
                    $"NPC movement projection case {caseIndex} has too many slow families");
            }

            for (var index = transitions.Count - 1; index >= 0; index--)
            {
                var baseSync = previousNpcReplicationSyncs.GetValueOrDefault(targetNetId);
                var movement = CombatPackets.MinionMovementSpeed(targetNetId,
                    (float)transitions[index].Speed, checked(baseSync + (uint)index + 1u));
                packets.Insert(transitions[index].AfterPacket,
                    ReplayPacketNode(movement, cipher));
            }
            return JsonSerializer.SerializeToElement(root);
        }

        return expectedEvent;
    }

    private static JsonElement ProjectHeroItemSlowRemovalPackets(JsonElement testCase,
        JsonElement expectedEvent, int caseIndex, int eventIndex,
        LegacyBlowfishCipher cipher)
    {
        if (eventIndex != 8 || testCase.GetProperty("targetKind").GetString() != "hero")
            return expectedEvent;

        var expectedItems = ReviewedHeroSlowItems(caseIndex);
        if (expectedItems is null) return expectedEvent;
        var itemIds = testCase.GetProperty("items").EnumerateArray()
            .Select(item => item.GetInt32()).ToArray();
        if (!itemIds.SequenceEqual(expectedItems))
            throw new InvalidDataException(
                $"Hero item-slow projection item sequence changed for case {caseIndex}");

        var targetNetId = testCase.GetProperty("target").GetUInt32();
        var archivedTarget = testCase.GetProperty("events")[0].GetProperty("entities")
            .EnumerateArray().Single(entity =>
                entity.GetProperty("netid").GetUInt32() == targetNetId);
        var slowBuffs = archivedTarget.GetProperty("buffs").EnumerateArray()
            .Where(buff => buff.GetProperty("payload").TryGetProperty(
                "item_slow_percent", out var percent) && percent.GetDouble() > 0.0)
            .ToArray();
        if (slowBuffs.Length == 0)
            throw new InvalidDataException(
                $"Hero item-slow projection case {caseIndex} has no archived slow payload");
        var marker = archivedTarget.GetProperty("buffs").EnumerateArray()
            .SingleOrDefault(buff => buff.GetProperty("name").GetString() == "ItemSlow");
        if (marker.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new InvalidDataException(
                $"Hero item-slow projection case {caseIndex} has no ItemSlow marker");
        var markerSlot = checked((byte)marker.GetProperty("slot").GetInt32());

        var root = JsonNode.Parse(expectedEvent.GetRawText())?.AsObject()
            ?? throw new InvalidDataException("Hero item-slow expected event is not an object");
        var packets = root["packets"]?.AsArray()
            ?? throw new InvalidDataException("Hero item-slow expected packets are missing");
        var internalSlots = slowBuffs
            .Where(buff => buff.GetProperty("name").GetString()?.StartsWith(
                "Internal_", StringComparison.Ordinal) == true)
            .Select(buff => checked((byte)buff.GetProperty("slot").GetInt32()))
            .Distinct()
            .ToArray();
        if (internalSlots.Length == 0 || internalSlots.Length != slowBuffs.Length)
            throw new InvalidDataException(
                $"Hero item-slow projection case {caseIndex} slow payload set changed");

        // The archived implementation refreshed hero stats once from the
        // item-slow synchronizer for each internal contribution, immediately
        // before that contribution's remove packet. The current state-only
        // synchronizer no longer emits those redundant CC rows. Work in the
        // patched packet order and remove one adjacent row per internal slot;
        // the visible ItemSlow marker removal retains its own stat refresh.
        var removalOrder = internalSlots
            .Select(slot => (Slot: slot, Index: FindTargetBuffRemovalIndex(
                packets, targetNetId, slot)))
            .OrderBy(row => row.Index)
            .ToArray();
        if (removalOrder.Any(row => row.Index < 0) ||
            removalOrder.Select(row => row.Index).Distinct().Count() != removalOrder.Length)
            throw new InvalidDataException(
                $"Hero item-slow removal order changed for case {caseIndex}");
        if (FindTargetBuffRemovalIndex(packets, targetNetId, markerSlot) < 0)
            throw new InvalidDataException(
                $"Hero item-slow marker removal packet missing for case {caseIndex}");

        foreach (var removal in removalOrder)
        {
            var removalIndex = FindTargetBuffRemovalIndex(packets,
                targetNetId, removal.Slot);
            if (removalIndex <= 0)
                throw new InvalidDataException(
                    $"Hero item-slow pre-removal stats packet missing for case {caseIndex}, " +
                    $"slot {removal.Slot}");

            var statsIndex = removalIndex - 1;
            var plaintext = packets[statsIndex]?["plaintext"]?.GetValue<string>();
            if (plaintext is null)
                throw new InvalidDataException(
                    $"Hero item-slow pre-removal stats packet is missing for case {caseIndex}");
            var bytes = Convert.FromHexString(plaintext);
            if (bytes.Length < 19 || bytes[0] != 0xCC ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(11, 4)) != targetNetId ||
                bytes[10] != 0x0B ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(15, 4)) != 0xFF)
                throw new InvalidDataException(
                    $"Hero item-slow pre-removal stats projection changed for case {caseIndex}, " +
                    $"slot {removal.Slot}");

            packets.RemoveAt(statsIndex);
            for (var index = statsIndex; index < packets.Count; index++)
                ShiftNpcReplicationSync(packets[index]?.AsObject(), targetNetId, -1, cipher);
        }
        return JsonSerializer.SerializeToElement(root);
    }

    private static int[]? ReviewedHeroSlowItems(int caseIndex) => caseIndex switch
    {
        88 or 89 => [3022],
        96 => [3044],
        104 => [3078],
        144 or 145 => [3184],
        176 or 177 => [3022, 3044, 3078],
        184 or 185 => [3123, 3184],
        _ => null,
    };

    private static int[]? ReviewedNpcSlowItems(int caseIndex) => caseIndex switch
    {
        90 or 91 or 94 or 95 => [3022],
        98 or 102 => [3044],
        106 or 110 => [3078],
        146 or 147 or 150 or 151 => [3184],
        178 or 179 or 182 or 183 => [3022, 3044, 3078],
        186 or 187 or 190 or 191 => [3123, 3184],
        _ => null,
    };

    private static List<int> FindBuffPacketIndexes(JsonArray packets, byte opcode, byte slot)
    {
        var indexes = new List<int>();
        for (var index = 0; index < packets.Count; index++)
        {
            var plaintext = packets[index]?["plaintext"]?.GetValue<string>();
            if (plaintext is null) continue;
            var bytes = Convert.FromHexString(plaintext);
            if (bytes.Length >= 6 && bytes[0] == opcode && bytes[5] == slot)
                indexes.Add(index);
        }
        return indexes;
    }

    private static int FindBuffRemovalIndex(JsonArray packets, byte slot)
    {
        var indexes = FindBuffPacketIndexes(packets, 0x7F, slot);
        return indexes.Count == 1 ? indexes[0] : -1;
    }

    private static int FindTargetBuffRemovalIndex(JsonArray packets, uint targetNetId,
        byte slot)
    {
        var found = -1;
        for (var index = 0; index < packets.Count; index++)
        {
            var plaintext = packets[index]?["plaintext"]?.GetValue<string>();
            if (plaintext is null) continue;
            var bytes = Convert.FromHexString(plaintext);
            if (bytes.Length < 6 || bytes[0] != 0x7F || bytes[5] != slot ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1, 4)) != targetNetId)
                continue;
            if (found >= 0) return -1;
            found = index;
        }
        return found;
    }

    private static uint MaxNpcReplicationSync(JsonArray packets, uint targetNetId,
        int count)
    {
        var max = 0u;
        for (var index = 0; index < count; index++)
        {
            var plaintext = packets[index]?["plaintext"]?.GetValue<string>();
            if (plaintext is null) continue;
            var bytes = Convert.FromHexString(plaintext);
            if (bytes.Length < 15 || bytes[0] != 0xCC ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(11, 4)) != targetNetId)
                continue;
            max = Math.Max(max, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5, 4)));
        }
        return max;
    }

    private static uint MaxNpcReplicationSync(JsonElement packets, uint targetNetId)
    {
        var max = 0u;
        foreach (var packet in packets.EnumerateArray())
        {
            var plaintext = packet.GetProperty("plaintext").GetString();
            if (plaintext is null) continue;
            var bytes = Convert.FromHexString(plaintext);
            if (bytes.Length < 15 || bytes[0] != 0xCC ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(11, 4)) != targetNetId)
                continue;
            max = Math.Max(max, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5, 4)));
        }
        return max;
    }

    private static void ShiftNpcReplicationSync(JsonObject? packet, uint targetNetId,
        uint offset, LegacyBlowfishCipher cipher)
    {
        ShiftNpcReplicationSync(packet, targetNetId, checked((int)offset), cipher);
    }

    private static void ShiftNpcReplicationSync(JsonObject? packet, uint targetNetId,
        int offset, LegacyBlowfishCipher cipher)
    {
        if (packet is null || offset == 0) return;
        var plaintext = packet["plaintext"]?.GetValue<string>();
        if (plaintext is null) return;
        var bytes = Convert.FromHexString(plaintext);
        if (bytes.Length < 15 || bytes[0] != 0xCC ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(11, 4)) != targetNetId)
            return;
        var syncId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5, 4));
        var updatedSyncId = offset > 0
            ? checked(syncId + (uint)offset)
            : checked(syncId - (uint)-offset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5, 4), updatedSyncId);
        packet["plaintext"] = Convert.ToHexString(bytes);
        packet["wire"] = Convert.ToHexString(cipher.Encrypt(bytes));
    }

    private static JsonObject ReplayPacketNode(PlaintextPacket packet,
        LegacyBlowfishCipher cipher)
    {
        var wire = packet.ToWireBytes(cipher);
        return new JsonObject
        {
            ["channel"] = (int)packet.Channel,
            ["flags"] = (int)packet.Reliability,
            ["wire"] = Convert.ToHexString(wire),
            ["plaintext"] = Convert.ToHexString(cipher.Decrypt(wire)),
        };
    }

    private static double ArchivedMovementSoftCap(double speed) => speed < 220.0
        ? 110.0 + speed * 0.5
        : speed > 490.0
        ? speed * 0.5 + 230.0
        : speed > 415.0
            ? speed * 0.8 + 83.0
            : speed;

    private static readonly IReadOnlySet<uint> EmptyMovementSoftCapOverlay =
        new HashSet<uint>();

    private sealed class Runtime
    {
        private Runtime() { }

        public required WorldState World { get; init; }
        public required ReplayQueue Queue { get; init; }
        public required LegacyBlowfishCipher Cipher { get; init; }
        public required ItemService Items { get; init; }
        public required ItemPassiveService Passives { get; init; }
        public required ItemDamageService Damage { get; init; }
        public required BuffLifecycle Buffs { get; init; }
        public required CombatService Combat { get; init; }

        public static Runtime Create(JsonElement rawEntities, double randomRoll = 0.5)
        {
            var world = new WorldState();
            world.Clock.SetVirtual(0.0);
            foreach (var raw in rawEntities.EnumerateArray()) world.Register(ReadEntity(raw));

            var grid = NavigationGrid.LoadDefault();
            var queue = new ReplayQueue();
            var syncIds = new ReplicationSyncCounter();
            var data = GameData.Default;
            var catalog = new ItemCatalog(data.Paths);
            var statsHooks = new FixtureStatsHooks();
            var stats = new HeroStatsService(data, catalog, statsHooks,
                new ProtocolHeroStatsTransport(queue, syncIds));
            var hooks = new RuntimeCombatHooks();
            var transport = new ProtocolCombatTransport(queue, stats,
                unit => unit.UnitType is not (UnitType.Minion or UnitType.Monster) || unit.Created,
                syncIds);
            var combatService = new CombatService(world, hooks, transport,
                new FixedRandomSource(randomRoll), stats);
            var scheduler = new JungleScheduler(world, data, campIds: [1]);
            var movement = new MovementSystem(world, grid);
            var neutralSink = new ProtocolNeutralPacketSink(packet => queue.Enqueue(packet), syncIds,
                world.Allocate, new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ),
                (x, z) => grid.HeightAt(new Position2(x, z)));
            var effects = new ProtocolSpellEffectSink(queue, syncIds,
                new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ),
                (x, z) => grid.HeightAt(new Position2(x, z)), world.Allocate,
                unit =>
                {
                    if (unit.UnitType == UnitType.Hero) stats.Push(unit);
                    else queue.Enqueue(CombatPackets.MinionStats(unit.NetId,
                        new MinionStatsSnapshot((float)unit.Health, (float)unit.AttackDamage,
                            (float)unit.Speed, (float)unit.Armor, (float)unit.SpellBlock,
                            (float)unit.AttackSpeedMod), syncIds.Next(unit.NetId)));
                });
            var buffEffects = new BuffEffectService(effects,
                new ProtocolBuffVisionSink(world.Get, world.Allocate, queue),
                _ => world.Clock.SyncId());
            var itemHooks = new MutableItemBuffHooks();
            var buffServices = new FixtureBuffServices(hooks, buffEffects, combatService, itemHooks);
            var lifecycle = new BuffLifecycle(world, stats, buffServices);
            statsHooks.Attach(lifecycle);
            hooks.AttachBuffLifecycle(lifecycle, buffEffects, effects);
            var neutral = new NeutralAiService(world, scheduler, movement, combatService,
                neutralSink, effects, new LifecycleSpellBuffs(lifecycle),
                gameData: data, random: new FixedRandomSource(randomRoll));
            hooks.AttachNeutral(neutral);
            var combat = new RuntimeCombat(combatService, hooks);
            var passive = new ItemPassiveService(world, data, lifecycle, buffEffects,
                combat, effects, stats, new FixedRandomSource(randomRoll));
            itemHooks.Attach(passive);
            var damage = new ItemDamageService(world, lifecycle, buffEffects, passive,
                combat, effects);
            hooks.AttachItems(passive, damage);
            var itemTransport = new ProtocolItemRuntimeTransport(queue,
                unit => unit.UnitType is not (UnitType.Minion or UnitType.Monster) || unit.Created,
                grid, syncIds);
            var items = new ItemService(world, catalog, lifecycle, buffEffects, combat,
                effects, itemTransport, grid);
            return new Runtime
            {
                World = world,
                Queue = queue,
                Cipher = LegacyBlowfish.CreateDefault(),
                Items = items,
                Passives = passive,
                Damage = damage,
                Buffs = lifecycle,
                Combat = combatService,
            };
        }
    }

    private sealed class ReplayQueue : ICombatPacketQueue
    {
        public List<PlaintextPacket> Packets { get; } = new();
        public void Enqueue(PlaintextPacket packet, bool global = false) => Packets.Add(packet);
        public void EnqueueLifecycle(uint victimNetId, uint killerNetId,
            Func<uint, PlaintextPacket> buildPacket) => Packets.Add(buildPacket(killerNetId));
        public void Clear() => Packets.Clear();
    }

    /// <summary>One observer's wire for the production half of a replay check.</summary>
    private sealed class CaptureTransport : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = new();
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }

    private sealed class FixedRandomSource(double value) : IRandomSource
    {
        public double NextDouble() => value;
    }

    private sealed class FixtureStatsHooks : IHeroStatsHooks
    {
        private BuffLifecycle? _lifecycle;
        public void Attach(BuffLifecycle lifecycle) => _lifecycle = lifecycle;
        public void UpdateAttackTiming(Entity hero, double attackSpeedMod) =>
            HeroAttackTimings.ApplyRefresh(hero, GameData.Default);
        public IReadOnlyList<double> ManaCosts(Entity hero) =>
            GameData.Default.ManaCostsForRanks(hero.SpellRanks, hero.ChampionName);
        public bool IsPendingActionBlocked(Entity hero) => false;
        public bool IsSpellToggledOn(Entity hero, int spellSlot) => false;
        public void RemoveBuff(Entity hero, int slot)
        {
            RemoveBuff(hero, slot, true);
        }
        public void RemoveBuff(Entity hero, int slot, bool emitWire)
        {
            if (_lifecycle is not null)
            {
                _lifecycle.RemoveBuff(hero, slot, emitWire);
                return;
            }
            for (var index = hero.Buffs.Count - 1; index >= 0; index--)
                if (hero.Buffs[index].Slot == slot) hero.Buffs.RemoveAt(index);
        }
    }

    private sealed class RuntimeCombat(
        CombatService service, RuntimeCombatHooks hooks) : ISpellCombatService
    {
        public bool IsTargetable(Entity target, Entity? observer) => hooks.IsTargetable(target, observer);
        public double HealingMultiplier(Entity target) => service.HealingReceivedMultiplier(target);
        public bool ApplyDamage(Entity source, Entity target, double amount, DamageType type,
            DamageSourceKind? sourceKind, double vampScale = 1.0) =>
            service.ApplyHit(source.NetId, target.NetId, amount, type, sourceKind, vampScale: vampScale);
        public bool ApplyDamage(Entity source, Entity target, double amount, DamageType type,
            DamageSourceKind? sourceKind, double vampScale, bool isReflect,
            bool sourceIsPeriodic, uint? spellOwnerNetId) =>
            service.ApplyHit(source.NetId, target.NetId, amount, type, sourceKind,
                vampScale: vampScale, isReflect: isReflect,
                sourceIsPeriodic: sourceIsPeriodic, spellOwnerNetId: spellOwnerNetId);
        public bool ApplyDamage(Entity source, Entity target, double amount, DamageType type,
            DamageSourceKind? sourceKind, DeathRecapOrigin recapOrigin, bool bypassSpellShield) =>
            service.ApplyHit(source.NetId, target.NetId, amount, type, sourceKind,
                recapOrigin: recapOrigin, bypassSpellShield: bypassSpellShield);
        public bool ConsumeSpellShield(Entity source, Entity target) =>
            hooks.ConsumeSpellShield(source, target, DamageSourceKind.Spell);
        public bool ConsumeSpellShield(Entity source, Entity target, bool force) =>
            hooks.ConsumeSpellShield(source, target, DamageSourceKind.Spell, force);
        public void InterruptBasicAttack(Entity attacker) => service.InterruptBasicAttack(attacker);
        public void Kill(Entity unit, uint killerNetId) => service.Kill(unit, killerNetId);
    }

    private sealed class RuntimeCombatHooks : ICombatHooks
    {
        private BuffLifecycle? _buffs;
        private IBuffEffectHooks? _buffEffects;
        private ISpellEffectSink? _effects;
        private ItemPassiveService? _passives;
        private ItemDamageService? _damage;
        private NeutralAiService? _neutral;

        public void AttachBuffLifecycle(BuffLifecycle buffs, IBuffEffectHooks effects,
            ISpellEffectSink spellEffects)
        {
            _buffs = buffs;
            _buffEffects = effects;
            _effects = spellEffects;
        }

        public void AttachItems(ItemPassiveService passive, ItemDamageService damage)
        {
            _passives = passive;
            _damage = damage;
        }

        public void AttachNeutral(NeutralAiService neutral) => _neutral = neutral;

        public bool IsInvulnerable(Entity unit) => unit.Buffs.Any(buff =>
            buff.Type == BuffType.Invulnerability || buff.Payload.Invulnerable);
        public bool IsGodMode(Entity unit) => unit.GodMode;
        public bool IsTargetable(Entity unit, Entity? observer) => unit.Alive &&
            !unit.Buffs.Any(buff => buff.Payload.Untargetable);
        public void OnBoxDamageSensor(Entity? source, Entity target, DamageSourceKind? sourceKind) { }
        public void OnBasicAttackPreDamage(Entity source, Entity target) { }
        public bool ConsumeSpellShield(Entity? source, Entity target,
            DamageSourceKind? sourceKind) => ConsumeSpellShield(source, target, sourceKind, false);
        public bool ConsumeSpellShield(Entity? source, Entity target,
            DamageSourceKind? sourceKind, bool force) =>
            _damage?.ConsumeSpellShield(source, target, sourceKind, force) ?? false;
        public bool TryFinishExistingRevival(Entity unit, uint killerNetId) => false;
        public PreDamageResult PreDamage(Entity? source, Entity target, double amount,
            DamageType damageType, DamageSourceKind? sourceKind) => PreDamageResult.Unhandled;
        public double ModifyDamageBeforeMitigation(Entity? source, Entity target, double amount,
            DamageType damageType, DamageSourceKind? sourceKind) => amount;
        public double ModifyDamageAfterMitigationBeforeShields(Entity target, Entity? source,
            double amount, DamageType damageType, DamageSourceKind? sourceKind) => amount;
        public void OnBasicAttackStart(Entity attacker, Entity target) { }
        public void BreakAttackStealth(Entity attacker) { }
        public double ModifyBasicAttackDamage(Entity source, Entity target, double damage) => damage;
        public double ModifyOutgoingDamage(Entity? source, Entity target, double damage,
            DamageType type, DamageSourceKind? sourceKind) => damage;
        public double ModifyIncomingDamage(Entity target, Entity? source, double damage,
            DamageType type, DamageSourceKind? sourceKind) => damage;
        public double AbsorbDamage(Entity target, Entity? source, double damage,
            DamageType type, DamageSourceKind? sourceKind) => damage;
        public double PreprocessItemDamage(Entity? source, Entity target, double damage,
            DamageType type, DamageSourceKind? sourceKind) =>
            _damage?.PreprocessItemDamage(source, target, damage, type, sourceKind) ?? damage;
        public double ModifyHealthRegeneration(Entity hero, double amount, double dt) => amount;
        public double ModifyManaRegeneration(Entity hero, double amount, double dt) => amount;
        public void OnUnitDamaged(Entity? source, Entity target, double damage, double rawDamage,
            DamageSourceKind? sourceKind, bool reflected, bool sourceIsPeriodic) =>
            _damage?.OnUnitDamaged(source, target, damage, rawDamage, sourceKind, reflected, sourceIsPeriodic);
        public void OnMoltenShieldReflect(Entity source, Entity target) =>
            _effects?.CreateEffect("AnnieSparks.troy", source.Position,
                targetNetId: source.NetId, bindNetId: source.NetId);
        public void OnDamageShieldChanged(Entity target, BuffState buff, double delta, bool depleted) =>
            _buffEffects?.SendDamageShieldDelta(target, delta);
        public void RemoveBuff(Entity target, int slot)
        {
            if (_buffs is not null) _buffs.RemoveBuff(target, slot);
            else for (var i = target.Buffs.Count - 1; i >= 0; i--)
                if (target.Buffs[i].Slot == slot) target.Buffs.RemoveAt(i);
        }
        public void OnBasicAttackHit(Entity source, Entity target, double rawDamage, DamageType type) =>
            _damage?.OnBasicAttackHit(source, target, rawDamage, type);
        // Batch 147: the avoided-swing item callbacks run at the swing's impact (ItemOnBeingDodged /
        // ItemOnMiss), so nothing is left of the roll-time hand-written path.
        public void OnBasicAttackMiss(Entity source) { }
        public void OnPendingHitCanceled(PendingHit hit) { }
        public void OnPendingCastCanceled(Entity unit) { }
        public void OnDeath(Entity unit, uint killerNetId) { }
        public void OnPetDeath(Entity pet) { }
        public void OnNeutralDeath(Entity neutral, uint killerNetId) { }
        // Batch 149 step 1: the kill, assist and death credits are the shipped scripts' own
        // (BbItemModule.RunCreditChain over each copy's ItemOnKill / ItemOnAssist / ItemOnDeath) and this
        // replay harness wires no script engine, so the credit that used to reach ItemPassiveService here is
        // gone with that hand-written path. Every archived case that recorded one is skipped. What a death
        // still does in that service is one counter of an unconverted item.
        public void OnItemKill(Entity? killer, Entity victim) { }
        // Batch 149 step 5: ItemPassiveService.OnItemDeath and its Tick are gone — a death's item work is
        // BbItemModule's own credit chain and the periodics are each copy's UpdateSelfBuffActions — and this
        // replay harness wires no script engine, so nothing reaches them here either.
        public void OnItemDeath(Entity hero) { }
        public void OnKill(Entity? killer, Entity victim) { }
        public void OnRespawn(Entity unit) { }
        public void OnLevelChanged(Entity hero) { }
        public void OnNeutralDamaged(Entity target, Entity? source)
        {
            if (source is not null) _neutral?.OnTakeDamage(target, source);
        }
        public void OnItemPassiveTick(double now, double dt) { }
        public void OnPeriodicDamage(Entity target, Entity? source, double amount,
            DamageType type, DamageSourceKind? sourceKind) { }
        public void OnEndGame(Team winner) { }
        public void HaltAllAi() { }
        public bool IsActionBlocked(Entity unit) => unit.Buffs.Any(buff =>
            buff.Type is BuffType.Stun or BuffType.Invulnerability || buff.Payload.ActionDisabled);
    }

    private sealed class MutableItemBuffHooks : IItemBuffHooks
    {
        private ItemPassiveService? _passives;
        public void Attach(ItemPassiveService passives) => _passives = passives;
        public bool HasItem(Entity unit, int itemId) => Inventory.HasItem(unit.Inventory, itemId);
        public void SyncItemSlow(Entity target) => _passives?.SyncItemSlow(target);
    }

    private sealed class FixtureBuffServices : IBuffServices
    {
        public FixtureBuffServices(RuntimeCombatHooks hooks, IBuffEffectHooks effects,
            CombatService combat, IItemBuffHooks items)
        {
            Combat = new CombatHooks(hooks, combat);
            Casts = new CastHooks();
            Champions = new ChampionHooks();
            Effects = effects;
            Items = items;
        }

        public ICombatBuffHooks Combat { get; }
        public ICastBuffHooks Casts { get; }
        public IChampionBuffHooks Champions { get; }
        public IBuffEffectHooks Effects { get; }
        public IItemBuffHooks Items { get; }

        private sealed class CombatHooks(RuntimeCombatHooks hooks, CombatService combat) : ICombatBuffHooks
        {
            public bool IsPendingActionBlocked(Entity unit) => hooks.IsActionBlocked(unit);
            public double HealingMultiplier(Entity unit) => 1.0;
            public void InterruptBasicAttack(Entity unit) => combat.InterruptBasicAttack(unit);
            public void InterruptBasicAttack(Entity unit, out bool cancelledWindup) =>
                cancelledWindup = combat.InterruptBasicAttack(unit) > 0;
            public void ApplyPeriodicDamage(PeriodicDamageRequest request) => combat.ApplyHit(
                request.SourceNetId, request.TargetNetId, request.Amount, request.DamageType,
                request.SourceKind, sourceIsPeriodic: request.SourceIsPeriodic,
                spellOwnerNetId: request.SpellOwnerNetId == 0 ? null : request.SpellOwnerNetId);
            public void Kill(Entity unit, uint killerNetId = 0) => combat.Kill(unit, killerNetId);
        }

        private sealed class CastHooks : ICastBuffHooks
        {
            public void CancelPendingCast(Entity hero) => hero.PendingCast = null;
        }

        private sealed class ChampionHooks : IChampionBuffHooks
        {
            public void OnBuffTick(Entity unit, BuffState buff, double dt) { }
            public void OnBuffRemoved(Entity unit, BuffState buff) { }
            public void OnBuffRemovedAfterWire(Entity unit, BuffState buff) { }
        }
    }

    private sealed class LifecycleSpellBuffs(BuffLifecycle lifecycle) : ISpellBuffService
    {
        public void ScriptStatusGained(Entity unit, bool wasHardControlled, bool wasRooted) => lifecycle.ScriptStatusGained(unit, wasHardControlled, wasRooted);
        public void ScriptStatusLost(Entity unit, bool couldMove, bool couldAttack) => lifecycle.ScriptStatusLost(unit, couldMove, couldAttack);
        public BuffState? AddOverlapping(Entity unit, string name, BuffType type, double duration, uint casterNetId,
            BuffPayload? payload = null, bool canMitigateDuration = true, int? maxStack = null) =>
            lifecycle.AddOverlappingBuff(unit, name, type, duration, casterNetId, payload, canMitigateDuration, maxStack);
        public BuffState? Add(Entity unit, string name, BuffType type, double duration,
            uint casterNetId, BuffPayload? payload = null, bool canMitigateDuration = true) =>
            lifecycle.AddBuff(unit, name, type, duration, casterNetId, payload, canMitigateDuration);
        public BuffState? AddStacking(Entity unit, string name, BuffType type, double duration,
            uint casterNetId, int maxStacks, BuffPayload perStackPayload,
            bool replaceExistingPayload = false, bool canMitigateDuration = true) =>
            lifecycle.AddStackingBuff(unit, name, type, duration, casterNetId,
                maxStacks, perStackPayload, replaceExistingPayload, canMitigateDuration: canMitigateDuration);
        public bool Remove(Entity unit, int slot) => lifecycle.RemoveBuff(unit, slot);
        public bool Has(Entity unit, string name) => lifecycle.HasBuff(unit, name);
        public void Tick(Entity unit, double dt) => lifecycle.TickBuffs(unit, dt);
        public void RefreshMovementSpeed(Entity unit, bool push = true) => lifecycle.RefreshMovementSpeed(unit, push);
    }

    private static Entity ReadEntity(JsonElement raw)
    {
        var entity = new Entity(raw.GetProperty("netid").GetUInt32(),
            EntityKindExtensions.FromSourceType(raw.GetProperty("etype").GetString()),
            (UnitType)raw.GetProperty("unitType").GetInt32(),
            (Team)raw.GetProperty("team").GetInt32(), Point(raw.GetProperty("position")))
        {
            Health = raw.GetProperty("hp").GetDouble(),
            MaxHealth = raw.GetProperty("max_hp").GetDouble(),
            Mana = raw.GetProperty("mana").GetDouble(),
            MaxMana = raw.GetProperty("max_mana").GetDouble(),
            Armor = raw.GetProperty("armor").GetDouble(),
            SpellBlock = raw.GetProperty("spell_block").GetDouble(),
            AttackDamage = raw.GetProperty("atk_damage").GetDouble(),
            AbilityPower = raw.GetProperty("ability_power").GetDouble(),
            Speed = raw.GetProperty("speed").GetDouble(),
            Radius = raw.GetProperty("radius").GetDouble(),
            AttackRange = raw.GetProperty("atk_range").GetDouble(),
            AttackPeriod = raw.GetProperty("atk_period").GetDouble(),
            AttackWindup = raw.GetProperty("windup").GetDouble(),
            AttackSpeedMod = raw.GetProperty("attack_speed_mod").GetDouble(),
            MissileSpeed = raw.GetProperty("missile_speed").GetDouble(),
            AttackCooldown = raw.GetProperty("attack_cd").GetDouble(),
            Level = raw.GetProperty("level").GetInt32(),
            SkillPoints = raw.GetProperty("skill_points").GetInt32(),
            Alive = raw.GetProperty("alive").GetBoolean(),
            Created = raw.GetProperty("created").GetBoolean(),
            Visible = raw.GetProperty("visible").GetBoolean(),
            OwnerNetId = raw.GetProperty("owner_netid").GetUInt32(),
            ExpiresAt = SpellReplayRecording.Number(raw.GetProperty("expires_at")),
            Gold = raw.GetProperty("gold").GetDouble(),
            Experience = raw.GetProperty("exp").GetDouble(),
            RespawnAt = raw.GetProperty("respawn_at").GetDouble(),
            CooldownMod = raw.GetProperty("cooldown_mod").GetDouble(),
            HealthRegen = raw.GetProperty("hp_regen").GetDouble(),
            ManaRegen = raw.GetProperty("mp_regen").GetDouble(),
            ChampionName = raw.GetProperty("champion").GetString() ?? "Annie",
            Name = raw.GetProperty("name").GetString() ?? string.Empty,
            Facing = Point(raw.GetProperty("facing")),
            IsWard = raw.TryGetProperty("isWard", out var ward) && ward.GetBoolean(),
            RevealsStealth = raw.TryGetProperty("revealsStealth", out var reveal) && reveal.GetBoolean(),
        };
        SetOptionalNumber(raw, "life_steal", value => entity.LifeSteal = value);
        SetOptionalNumber(raw, "spell_vamp", value => entity.SpellVamp = value);
        SetOptionalNumber(raw, "crit_chance", value => entity.CritChance = value);
        SetOptionalNumber(raw, "crit_multiplier", value => entity.CritMultiplier = value);
        SetOptionalNumber(raw, "dodge", value => entity.Dodge = value);
        SetOptionalNumber(raw, "hardness", value => entity.Hardness = value);
        SetOptionalNumber(raw, "gold_reward", value => entity.GoldReward = value);
        SetOptionalNumber(raw, "global_gold_reward", value => entity.GlobalGoldReward = value);
        SetOptionalNumber(raw, "xp_reward", value => entity.ExperienceReward = value);
        if (raw.TryGetProperty("attack_target", out var attackTarget) &&
            attackTarget.ValueKind != JsonValueKind.Null)
            entity.AttackTarget = attackTarget.GetUInt32();
        entity.SpellRanks = raw.GetProperty("spell_ranks").EnumerateArray().Select(value => value.GetInt32()).ToList();
        entity.SpellCooldowns = raw.GetProperty("spell_cooldowns").EnumerateArray().Select(value => value.GetDouble()).ToList();
        entity.SummonerCooldowns = raw.GetProperty("summoner_cooldowns").EnumerateArray().Select(value => value.GetDouble()).ToList();
        entity.ItemCooldowns = raw.GetProperty("item_cooldowns").EnumerateArray().Select(value => value.GetDouble()).ToList();
        entity.Waypoints = raw.GetProperty("waypoints").EnumerateArray().Select(Point).ToList();
        LoadInventory(entity, raw.GetProperty("inventory"));
        if (raw.TryGetProperty("equippedItemIds", out var equipped))
            entity.SetEquippedItemIds(equipped.EnumerateArray().Select(value => value.GetInt32()));
        else
            entity.SetEquippedItemIds(entity.Inventory.Where(slot => slot is not null)
                .Select(slot => slot!.ItemId));
        LoadCounters(entity, raw.GetProperty("itemCounters"));
        LoadBuffs(entity, raw.GetProperty("buffs"));
        // Preserve the input adapter's distinction between explicit timings and
        // the old Annie default; expected outputs never initialize the runtime.
        if (entity.UnitType == UnitType.Hero && entity.ChampionName == "Annie" &&
            Math.Abs(entity.AttackPeriod - 1.6 / entity.AttackSpeedMod) <= 1e-7)
        {
            entity.AttackPeriod = Math.Clamp(1.6 * 1.0799999982118607 / entity.AttackSpeedMod, 0.4, 5.0);
            entity.AttackWindup = entity.AttackPeriod * 0.195794753;
        }
        return entity;
    }

    private static void LoadInventory(Entity entity, JsonElement values)
    {
        var slots = new List<InventorySlot?>(Inventory.SlotCount);
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.Null)
            {
                slots.Add(null);
                continue;
            }
            var state = new InventoryItemState();
            if (value.TryGetProperty("state", out var stateRaw) && stateRaw.ValueKind == JsonValueKind.Object)
                LoadItemState(state, stateRaw);
            slots.Add(new InventorySlot(value.GetProperty("itemId").GetInt32(),
                value.GetProperty("count").GetInt32(), value.GetProperty("charges").GetInt32(), state));
        }
        while (slots.Count < Inventory.SlotCount) slots.Add(null);
        entity.Inventory = slots;
    }

    private static void LoadItemState(InventoryItemState state, JsonElement raw)
    {
        foreach (var property in raw.EnumerateObject())
        {
            // Batch 149 step 1: the Bloodthirster's and Warmog's growth counters are the copy's own script
            // InstanceVars now (Items/3072 and 3083), so their archived keys are superseded data and are not
            // loaded. Batch 149 step 5: so is all of Rod of Ages' ageing (Items/3027.luaobj's own
            // BonusHealth / BonusMana / BonusAbilityPower) and InventoryItemState is empty.
            if (property.Name is "bloodthirster_attack_damage" or "bloodthirster_life_steal"
                or "warmog_health" or "warmog_regen" or "rod_of_ages_elapsed" or "rod_of_ages_stacks") continue;
            throw new InvalidOperationException("unsupported item state " + property.Name);
        }
    }

    private static void LoadCounters(Entity entity, JsonElement raw)
    {
        foreach (var property in raw.EnumerateObject())
        {
            // Batch 149 step 5: the counters this used to restore are the shipped scripts' own tables now (the
            // same superseded keys CompareCounters steps past), so an archived entity's copy of one is not loaded.
            if (property.Name is "tear_bonus_mana" or "tear_spell_reservoir" or "tear_spell_refill"
                or "manamune_attack_reservoir" or "manamune_attack_refill"
                or "3131" or "3178" or "item_3131" or "item_3178" or "force_of_nature_elapsed"
                or "banshee_initial_pending" or "banshee_pet_initialized"
                or "snowball_3041" or "snowball_3138" or "snowball_3141"
                or "sunfire_elapsed" or "sunfire_initialized") continue;
            var value = property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? (property.Value.GetBoolean() ? 1.0 : 0.0)
                : property.Value.GetDouble();
            SetCounter(entity, property.Name, value);
        }
    }

    private static void LoadBuffs(Entity entity, JsonElement raw)
    {
        foreach (var value in raw.EnumerateArray())
        {
            var payload = new BuffPayload();
            if (value.TryGetProperty("payload", out var fields) && fields.ValueKind == JsonValueKind.Object)
                LoadPayload(payload, fields);
            var stacks = value.TryGetProperty("stacks", out var stacksValue)
                ? stacksValue.GetInt32() : 1;
            entity.Buffs.Add(new BuffState(value.GetProperty("name").GetString() ?? "FixtureBuff",
                payload, (BuffType)value.GetProperty("type").GetInt32(), value.GetProperty("slot").GetInt32(),
                value.GetProperty("expires_at").GetDouble(), value.GetProperty("caster").GetUInt32(), stacks,
                value.GetProperty("max_stack").ValueKind == JsonValueKind.Null
                    ? null : value.GetProperty("max_stack").GetInt32()));
        }
    }

    private static void LoadPayload(BuffPayload payload, JsonElement fields)
    {
        foreach (var property in fields.EnumerateObject())
        {
            var key = property.Name;
            if (property.Value.ValueKind == JsonValueKind.Number)
            {
                var value = property.Value.GetDouble();
                switch (key)
                {
                    case "ability_power": payload.AbilityPower = value; break;
                    case "armor": payload.Armor = value; break;
                    case "armor_pen_flat": payload.ArmorPenFlat = value; break;
                    case "armor_pen_percent": payload.ArmorPenPercent = value; break;
                    case "attack_damage": payload.AttackDamage = value; break;
                    case "attack_speed_mod": payload.AttackSpeedMod = value; break;
                    case "attack_speed_mult": payload.AttackSpeedMult = value; break;
                    case "cooldown_mod": payload.CooldownMod = value; break;
                    case "cooldown_mod_base": payload.CooldownModBase = value; break;
                    case "cooldown_mod_per_level": payload.CooldownModPerLevel = value; break;
                    case "crit_chance": payload.CritChance = value; break;
                    case "dodge_piercing": payload.DodgePiercing = value; break;
                    case "damage_per_tick": payload.DamagePerTick = value; break;
                    case "damage_shield": payload.DamageShield = value; break;
                    case "healing_received_mult": payload.HealingReceivedMult = value; break;
                    case "hp_per_tick": payload.HpPerTick = value; break;
                    case "hp_percent_per_tick": payload.HpPercentPerTick = value; break;
                    case "hp_regen": payload.HpRegen = value; break;
                    case "hp_regen_mult": payload.HpRegenMult = value; break;
                    case "item_slow_percent": payload.ItemSlowPercent = value; break;
                    case "life_steal": payload.LifeSteal = value; break;
                    case "life_steal_mult": payload.LifeStealMult = value; break;
                    case "magic_shield": payload.MagicShield = value; break;
                    case "max_hp": payload.MaxHp = value; break;
                    case "max_mp": payload.MaxMp = value; break;
                    case "max_mana_regen_percent": payload.MaxManaRegenPercent = value; break;
                    case "miss_chance": payload.MissChance = value; break;
                    case "mp_per_tick": payload.MpPerTick = value; break;
                    case "mp_regen": payload.MpRegen = value; break;
                    case "move_speed_flat": payload.MoveSpeedFlat = value; break;
                    case "move_speed_mult": payload.MoveSpeedMult = value; break;
                    case "move_speed_percent": payload.MoveSpeedPercent = value; break;
                    case "period": payload.Period = value; break;
                    case "period_elapsed": payload.PeriodElapsed = value; break;
                    case "reflect": payload.Reflect = value; break;
                    case "reflect_scale": payload.ReflectScale = value; break;
                    case "spell_block": payload.SpellBlock = value; break;
                    case "spell_block_mult": payload.SpellBlockMult = value; break;
                    case "spell_vamp": payload.SpellVamp = value; break;
                    case "spell_vamp_mult": payload.SpellVampMult = value; break;
                    case "spellblade_bonus": payload.SpellbladeBonus = value; break;
                    case "spellblade_delay": payload.SpellbladeDelay = value; break;
                    case "fx_netid": payload.FxNetId = checked((uint)value); break;
                    case "stacks": payload.Stacks = checked((int)value); break;
                    case "q_ratio": payload.QRatio = value; break;
                    default: throw new InvalidOperationException("unsupported numeric payload " + key);
                }
            }
            else if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var value = property.Value.GetBoolean();
                switch (key)
                {
                    case "action_disabled": payload.ActionDisabled = value; break;
                    case "ability_disabled": payload.AbilityDisabled = value; break;
                    case "invulnerable": payload.Invulnerable = value; break;
                    case "movement_disabled": payload.MovementDisabled = value; break;
                    case "silenced": payload.Silenced = value; break;
                    case "source_is_periodic": payload.SourceIsPeriodic = value; break;
                    case "stealthed": payload.Stealthed = value; break;
                    case "untargetable": payload.Untargetable = value; break;
                    default: throw new InvalidOperationException("unsupported boolean payload " + key);
                }
            }
            else if (property.Value.ValueKind == JsonValueKind.String && key == "damage_type")
            {
                payload.DamageType = ParseDamageType(property.Value.GetString());
            }
            else
            {
                throw new InvalidOperationException("unsupported payload " + key);
            }
        }
    }

    private static void SetOptionalNumber(JsonElement raw, string key, Action<double> setter)
    {
        if (raw.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number)
            setter(value.GetDouble());
    }

    private static Position2 Point(JsonElement value) => value.GetArrayLength() == 3
        ? new Position2(value[0].GetDouble(), value[2].GetDouble())
        : new Position2(value[0].GetDouble(), value[1].GetDouble());

}
