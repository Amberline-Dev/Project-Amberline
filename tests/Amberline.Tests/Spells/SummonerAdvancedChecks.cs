using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Platform;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

public static class SummonerAdvancedChecks
{
    [Check("spells-summoners-b4-teleport-mastery-completion-cancel")]
    public static void TeleportMasteriesOnlyReduceCompletedCooldown()
    {
        foreach (var slot in new[] { 0, 1 })
        {
            using var runtime = CreateRuntime();
            var hero = Hero(runtime, Team.Order, SpawnPoint(),
                slot == 0 ? ["SummonerTeleport", "SummonerDot"] : ["SummonerDot", "SummonerTeleport"]);
            hero.Masteries = MasteryPage.Create(new MasteryCatalog(), new Dictionary<int, int>
            {
                [124] = 1, [134] = 3, [132] = 3, [130] = 1,
                [116] = 4, [115] = 4, [117] = 1, [121] = 3, [120] = 1,
            });
            runtime.HeroStats.Refresh(hero);
            var target = new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
                UnitType.Minion, Team.Order, new Position2(hero.Position.X + 300, hero.Position.Z))
            {
                Name = "Minion_Basic", Health = 1000, MaxHealth = 1000, Created = true, Radius = 35,
            };
            runtime.World.Register(target);
            TestAssert.True(Cast(runtime, hero, slot, target.Position, target.NetId));
            // 124 shortens the channel to 4.0 - 0.5, so the buff runs out inside this step and its
            // OnBuffDeactivate writes the completed cooldown after the step's own decrement.
            Tick(runtime, 3.5);
            TestAssert.Equal("SummonerTeleport", hero.SummonerSpells[slot]);
            TestAssert.Near(250, hero.SummonerCooldowns[slot], 1e-9,
                "retail sweep B4, 27 Sept 2026: completion uses 300 * .85 - 5");
            hero.SummonerCooldowns[slot] = 0;
            TestAssert.True(Cast(runtime, hero, slot, target.Position, target.NetId));
            Tick(runtime, 1);
            TestAssert.True(Cast(runtime, hero, slot, target.Position));
            TestAssert.Near(180, hero.SummonerCooldowns[slot], 1e-9, "cancel ignores cooldown masteries");
        }
    }

    [Check("spells-summoners-advanced-slot-name-packet")]
    public static void SlotNamePacket()
    {
        var packet = SpellPackets.ChangeSlotSpellName(0x1234, 1,
            "TeleportCancel", isSummoner: true);
        TestAssert.True(SpellPackets.TryReadChangeSlotSpellName(
            packet.Plaintext.Span, out var decoded), "typed 0x35 decoder");
        TestAssert.Equal((uint)0x1234, decoded.NetId, "slot name net id");
        TestAssert.Equal((byte)1, decoded.Slot, "slot name slot");
        TestAssert.True(decoded.IsSummoner, "slot name summoner bit");
        TestAssert.Equal("TeleportCancel", decoded.SpellName, "slot name");
        var wire = WirePacketDecoder.Decode(packet.Plaintext.Span, packet.Channel);
        TestAssert.True(wire.HasDecoder, "slot name wire decoder");
        TestAssert.Equal("TeleportCancel", wire.Fields["spell_name"] as string,
            "slot name diagnostic field");
    }

    [Check("spells-summoners-advanced-teleport")]
    public static void TeleportLifecycle()
    {
        var runtime = CreateRuntime();
        var hero = Hero(runtime, Team.Order, SpawnPoint(),
            ["SummonerTeleport", "SummonerDot"]);
        var target = new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, Team.Order,
            new Position2(hero.Position.X + 300.0, hero.Position.Z))
        {
            Name = "Minion_Basic", ChampionName = "Annie", Health = 1000.0,
            MaxHealth = 1000.0, Alive = true, Created = true, Radius = 35.0,
        };
        runtime.World.Register(target);
        var original = hero.SummonerSpells[0];
        TestAssert.True(Cast(runtime, hero, 0, target.Position, target.NetId),
            "Teleport cast");
        TestAssert.Equal("TeleportCancel", hero.SummonerSpells[0],
            "Teleport must expose cancel spell");
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "SummonerTeleport"),
            "Teleport applies its native channel buff");
        TestAssert.True(target.Buffs.Any(buff => buff.Name == "Teleport_Target"),
            "Teleport target lock");
        Tick(runtime, 4.0);
        TestAssert.Equal(original, hero.SummonerSpells[0],
            "Teleport must restore selected slot");
        TestAssert.Equal(300.0, hero.SummonerCooldowns[0],
            "completed Teleport cooldown");
        // SummonerTeleport.TargetExecute adds Teleport_Target with Duration 0.1 on top of its DurationVar BuffDuration, so the
        // target lock runs a tenth of a second past the channel — the same 0.1 OnBuffDeactivate adds to the elapsed time before
        // its TotalTime >= BuffDuration arrival test. Nothing on the target is removed at the arrival (the hand-written
        // ReleaseTeleportTarget did); the lock expires on its own timer, and it outlives the channel by that tail.
        TestAssert.True(target.Buffs.Any(buff => buff.Name == "Teleport_Target"),
            "the shipped target lock outlives the channel by its 0.1s tail");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "SummonerTeleport"),
            "completed Teleport channel cleanup");
        Tick(runtime, 0.2);
        TestAssert.False(target.Buffs.Any(buff => buff.Name == "Teleport_Target"),
            "completed Teleport target cleanup");

        hero.SummonerCooldowns[0] = 0.0;
        TestAssert.True(Cast(runtime, hero, 0, target.Position, target.NetId),
            "second Teleport cast");
        var channel = hero.Buffs.Single(buff => buff.Name == "SummonerTeleport");
        TestAssert.True(runtime.SpellBuffs.Remove(hero, channel.Slot),
            "external channel removal");
        // The shipped OnBuffDeactivate writes the interrupted cooldown and restores the slot as the
        // buff leaves, so both are already in before any further step (a step would only tick it down).
        TestAssert.Equal(original, hero.SummonerSpells[0],
            "removed Teleport channel cancels the pending cast");
        TestAssert.Equal(180.0, hero.SummonerCooldowns[0],
            "removed Teleport channel uses interrupted cooldown");
        Tick(runtime, 0.1);
        TestAssert.Equal(original, hero.SummonerSpells[0],
            "the slot stays restored");

        hero.SummonerCooldowns[0] = 0.0;
        TestAssert.True(Cast(runtime, hero, 0, target.Position, target.NetId),
            "third Teleport cast");
        TestAssert.False(Cast(runtime, hero, 0, target.Position),
            "TeleportCancel is held by its one-second cooldown");
        Tick(runtime, 1.0);
        TestAssert.True(Cast(runtime, hero, 0, target.Position),
            "TeleportCancel request after cooldown");
        TestAssert.Equal(original, hero.SummonerSpells[0],
            "cancel restores selected spell");
        TestAssert.Equal(180.0, hero.SummonerCooldowns[0],
            "interrupted Teleport cooldown");
    }

    [Check("spells-summoners-teleport-movement-and-silence-gates")]
    public static void TeleportRefusesWhileImmobileButNotWhileSilencedAndOnlyNewControlInterrupts()
    {
        // SummonerTeleport.inibin CantCastWhileRooted=True, native OnCanUseSpell
        // (the client's cast check): the cast needs CanMove. A Silence already
        // on the caster is not a gate; only a newly added control interrupts.
        var runtime = CreateRuntime();
        var hero = Hero(runtime, Team.Order, SpawnPoint(), ["SummonerTeleport", "SummonerDot"]);
        var target = new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, Team.Order,
            new Position2(hero.Position.X + 300.0, hero.Position.Z))
        {
            Name = "Minion_Basic", ChampionName = "Annie", Health = 1000.0,
            MaxHealth = 1000.0, Alive = true, Created = true, Radius = 35.0,
        };
        runtime.World.Register(target);

        foreach (var type in new[] { BuffType.Snare, BuffType.Stun, BuffType.Sleep,
                     BuffType.Net, BuffType.Suppression })
        {
            var control = runtime.SpellBuffs.Add(hero, "TeleportGateProbe", type, 5.0, target.NetId,
                canMitigateDuration: false);
            TestAssert.True(control is not null, "probe buff added");
            TestAssert.False(Cast(runtime, hero, 0, target.Position, target.NetId),
                $"{type} (CanMove false) blocks Teleport");
            TestAssert.Equal(0.0, hero.SummonerCooldowns[0], $"{type}: refused cast keeps the cooldown");
            TestAssert.Equal("SummonerTeleport", hero.SummonerSpells[0], $"{type}: slot unchanged");
            runtime.SpellBuffs.Remove(hero, control!.Slot);
        }
        // SetFeared (bit 6) and SetTaunted (bit 5) leave the CanMove bit alone.
        foreach (var type in new[] { BuffType.Fear, BuffType.Taunt })
        {
            var control = runtime.SpellBuffs.Add(hero, "TeleportGateProbe", type, 5.0, target.NetId,
                canMitigateDuration: false);
            TestAssert.True(Cast(runtime, hero, 0, target.Position, target.NetId),
                $"{type} does not clear CanMove, so it does not block Teleport");
            runtime.SpellBuffs.Remove(hero, control!.Slot);
            // The channel ends by expiry, by TeleportCancel or by death (BbBuffRuntime.OnUnitDeath); here it is
            // taken off directly so the next iteration starts from a clean slot, the way a finished channel does.
            EndChannel(runtime, hero);
            hero.SummonerCooldowns[0] = 0.0;
        }
        var movementLock = runtime.SpellBuffs.Add(hero, "TeleportGateProbe", BuffType.Internal, 5.0,
            target.NetId, new BuffPayload { MovementDisabled = true }, canMitigateDuration: false);
        TestAssert.False(Cast(runtime, hero, 0, target.Position, target.NetId), "a movement lock blocks Teleport");
        runtime.SpellBuffs.Remove(hero, movementLock!.Slot);

        runtime.SpellBuffs.Add(hero, "TeleportGateSilence", BuffType.Silence, 20.0, target.NetId,
            canMitigateDuration: false);
        TestAssert.True(Cast(runtime, hero, 0, target.Position, target.NetId),
            "a pre-existing Silence does not block the start");
        Tick(runtime, 0.5);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "SummonerTeleport"),
            "the pre-existing Silence does not interrupt the channel");

        // The shipped BuffOnAllowAdd of the channel buff ends it the moment a newly added hard control
        // lands (its own Type is in the script's list), so the removal and its 180 are already in when
        // the add returns — no step is needed to notice the interrupt.
        runtime.SpellBuffs.Add(hero, "TeleportGateFreshStun", BuffType.Stun, 1.0, target.NetId,
            canMitigateDuration: false);
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "SummonerTeleport"),
            "a newly added Stun interrupts the channel");
        TestAssert.Equal("SummonerTeleport", hero.SummonerSpells[0], "interrupt restores the slot");
        TestAssert.Equal(180.0, hero.SummonerCooldowns[0], "interrupted Teleport cooldown");
    }

    [Check("spells-summoners-advanced-revive")]
    public static void ReviveLifecycle()
    {
        var runtime = CreateRuntime();
        var hero = Hero(runtime, Team.Order, SpawnPoint(),
            ["SummonerRevive", "SummonerDot"], level: 5);
        runtime.HeroStats.Refresh(hero);
        var baseSpeed = hero.Speed;
        hero.Alive = false;
        hero.Health = 0.0;
        hero.RespawnAt = 20.0;
        TestAssert.True(Cast(runtime, hero, 0, hero.Position),
            "dead-only Revive cast");
        TestAssert.True(hero.Alive, "Revive must use immediate respawn lifecycle");
        TestAssert.Equal(0.0, hero.RespawnAt, "Revive clears respawn timer");
        // Owner decision 16 Oct 2026: base 540 for every player (shipped data says 510).
        TestAssert.Equal(540.0, hero.SummonerCooldowns[0], "Revive cooldown");
        var boost = hero.Buffs.Single(buff => buff.Name == "SummonerReviveSpeedBoost");
        // SummonerReviveSpeedBoost.luaobj's BuffOnUpdateStats takes 0.045 off InstanceVars.MoveSpeedMod and publishes
        // what is left as IncPercentMultiplicativeMovementSpeedMod. The hook runs on the shipped 0.25 s stats grid
        // (BbBuffRuntime.StatsUpdateInterval), so the modifier is a step function of the elapsed time — not the linear
        // ramp the hand-written TickReviveSpeed drew from 2.2 over the twelve seconds. Batch 256 restatement (the bundle 4 red
        // of batch 252 item 5): retail runs no hook of a buff before its OnBuffActivate, so the sample BbBuffRuntime.Add takes
        // before the buff exists works on a copy of the variables and the only sample that steps InstanceVars.MoveSpeedMod is
        // the grid's zero after the activation (OnBuffActivate here only requires the variable, 0000-0010). This hook mutates its
        // own variable on every call, so what the buff stores is 2.2 - 0.045 (before batch 252 the pre-activation sample stepped
        // it too: 2.2 - 2 * 0.045).
        TestAssert.Near(2.2 - 0.045, boost.Payload.MoveSpeedMult, 1e-9,
            "Revive stores the shipped additive speed modifier, stepped down by the sample after its activation");
        static double SoftCap(double speed) => speed <= 415.0 ? speed :
            speed <= 490.0 ? 415.0 + (speed - 415.0) * 0.8 :
            415.0 + 75.0 * 0.8 + (speed - 490.0) * 0.5;
        // After N samples of the stats grid the modifier is 2.2 - (N + 1) * 0.045: the add's one, then one step per
        // sample. One sample per update at most (BbBuffRuntime.OnTick keeps overdue time), which is one step per
        // production simulation step of 0.25 s.
        static double Modifier(int samples) => 2.2 - (samples + 1) * 0.045;
        TestAssert.Near(SoftCap(baseSpeed * (1.0 + Modifier(0))), hero.Speed, 1e-9,
            "Revive initial effective speed multiplier");
        for (var sample = 0; sample < 24; sample++) Tick(runtime, 0.25);
        TestAssert.Near(SoftCap(baseSpeed * (1.0 + Modifier(24))), hero.Speed, 1e-9,
            "Revive midpoint: 24 grid samples into the twelve seconds");
        // At 47 samples (11.75 s) 0.04 is left of the shipped modifier, the last step before the buff ends at twelve seconds;
        // the script's own clamp (BBIf CO_LESS_THAN 0.0 → BBSetVarInTable 0.0) is never reached in the buff's life.
        for (var sample = 0; sample < 23; sample++) Tick(runtime, 0.25);
        TestAssert.Near(SoftCap(baseSpeed * (1.0 + Modifier(47))), hero.Speed, 1e-9,
            "Revive tail: 47 grid samples in, 0.04 of the shipped modifier left");
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "SummonerReviveSpeedBoost"),
            "the boost is still on for the last quarter second");
        Tick(runtime, 0.25);
        TestAssert.Near(baseSpeed, hero.Speed, 1e-9,
            "Revive speed returns to base at twelve seconds");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "SummonerReviveSpeedBoost"),
            "Revive speed buff expiry");

        var catalog = new MasteryCatalog();
        Entity PrepareDeadRevive(MatchRuntime match, MasteryPage page)
        {
            var candidate = Hero(match, Team.Order, SpawnPoint(),
                ["SummonerRevive", "SummonerDot"], level: 5);
            candidate.Masteries = page;
            match.HeroStats.Refresh(candidate);
            candidate.Alive = false;
            candidate.Health = 0.0;
            candidate.RespawnAt = 20.0;
            return candidate;
        }

        var unrelatedRuntime = CreateRuntime();
        var unrelated = PrepareDeadRevive(unrelatedRuntime,
            MasteryPage.Create(catalog, new Dictionary<int, int> { [130] = 1 }));
        TestAssert.True(Cast(unrelatedRuntime, unrelated, 0, unrelated.Position),
            "unrelated mastery Revive cast");
        TestAssert.Equal(540.0, unrelated.SummonerCooldowns[0],
            "unrelated mastery keeps the 540 base Revive cooldown (owner decision 16 Oct 2026)");

        var preservationRuntime = CreateRuntime();
        var preservation = PrepareDeadRevive(preservationRuntime,
            MasteryPage.Create(catalog, new Dictionary<int, int>
            {
                [133] = 1, [110] = 3, [129] = 1, [109] = 3, [112] = 1,
            }));
        TestAssert.True(Cast(preservationRuntime, preservation, 0, preservation.Position),
            "Preservation Revive cast");
        TestAssert.Equal(510.0, preservation.SummonerCooldowns[0],
            "Preservation subtracts its flat 30 s Revive bonus from the 540 base");
        TestAssert.Equal(400.0,
            preservation.Buffs.Single(buff => buff.Name == "ReviveMarker").Payload.MaxHp,
            "Preservation emits the shipped ReviveMarker health bonus");

        var genericRuntime = CreateRuntime();
        var generic = PrepareDeadRevive(genericRuntime,
            MasteryPage.Create(catalog, new Dictionary<int, int>
            {
                [124] = 1, [134] = 3, [132] = 3, [130] = 1,
                [116] = 4, [115] = 4, [117] = 1, [121] = 3, [120] = 1,
            }));
        TestAssert.True(Cast(genericRuntime, generic, 0, generic.Position),
            "Presence Revive cast");
        TestAssert.Near(459.0, generic.SummonerCooldowns[0], 1e-9,
            "Presence uses the shipped 540 generic Revive base before its multiplier");
    }

    // Owner decision 16 Oct 2026: Revive's base cooldown is 540 s for every player.
    // The shipped SummonerRevive SpellData says 510; the script's 540 literal used to
    // apply only on Presence's branch. Masteries adjust from 540 as the script does:
    // Presence 540 * (1 - 0.15), Preservation minus a flat 30 s.
    [Check("spells-summoners-advanced-revive-base-cooldown")]
    public static void ReviveBaseCooldownIs540()
    {
        var catalog = new MasteryCatalog();
        double Cooldown(int slot, Dictionary<int, int>? page, bool noCooldown = false)
        {
            var runtime = CreateRuntime();
            var hero = Hero(runtime, Team.Order, SpawnPoint(),
                slot == 0 ? ["SummonerRevive", "SummonerDot"] : ["SummonerDot", "SummonerRevive"],
                level: 5);
            if (page is not null) hero.Masteries = MasteryPage.Create(catalog, page);
            hero.CooldownsDisabled = noCooldown;
            runtime.HeroStats.Refresh(hero);
            hero.Alive = false;
            hero.Health = 0.0;
            hero.RespawnAt = 20.0;
            TestAssert.True(Cast(runtime, hero, slot, hero.Position), $"Revive cast, slot {slot}");
            return hero.SummonerCooldowns[slot];
        }

        // Valid pages, as in ReviveLifecycle: Presence (120) and Preservation (129) trees.
        var presence = new Dictionary<int, int>
        {
            [124] = 1, [134] = 3, [132] = 3, [130] = 1,
            [116] = 4, [115] = 4, [117] = 1, [121] = 3, [120] = 1,
        };
        var preservation = new Dictionary<int, int>
            { [133] = 1, [110] = 3, [129] = 1, [109] = 3, [112] = 1 };
        foreach (var slot in new[] { 0, 1 })
        {
            TestAssert.Equal(540.0, Cooldown(slot, null), $"no page, slot {slot}");
            TestAssert.Equal(540.0, Cooldown(slot, new Dictionary<int, int>()), $"empty page, slot {slot}");
            TestAssert.Equal(540.0, Cooldown(slot, new Dictionary<int, int> { [130] = 1 }),
                $"unrelated mastery, slot {slot}");
            TestAssert.Near(459.0, Cooldown(slot, presence), 1e-9,
                $"Presence scales the 540 base, slot {slot}");
            TestAssert.Equal(510.0, Cooldown(slot, preservation),
                $"Preservation subtracts 30 from the 540 base, slot {slot}");
            TestAssert.Equal(0.0, Cooldown(slot, null, noCooldown: true), $"no-cooldown mode, slot {slot}");
        }
    }

    /// <summary>
    /// Batch 303: the cooldown every summoner cast starts with no mastery page and no rune, pinned before and after
    /// <c>AvatarVars</c> reads an unset entry as Lua nil. Under nil each script's own <c>AdjustCooldown</c> takes its
    /// <c>SummonerCooldownBonus CO_NOT_EQUAL 0</c> branch and returns its base literal times 1 (Revive's 540 through its
    /// <c>NewCooldown</c>), so the router hands back the number itself and no engine fallback answers. Every literal is the
    /// inibin <c>Cooldown</c> except Revive's (SpellData 510). Teleport has no <c>AdjustCooldown</c>: its cast starts the 1 s
    /// its <c>OnBuffActivate</c> gives the <c>TeleportCancel</c> placeholder, and the 300 is written at the channel's end
    /// (<c>spells-summoners-advanced-teleport</c>).
    /// </summary>
    [Check("spells-summoners-cast-cooldowns-from-scripts")]
    public static void EverySummonerCastStartsItsScriptCooldown()
    {
        foreach (var (spell, expected) in new (string, double)[]
        {
            ("SummonerFlash", 255.0), ("SummonerHeal", 270.0), ("SummonerExhaust", 210.0), ("SummonerHaste", 210.0),
            ("SummonerSmite", 75.0), ("SummonerDot", 180.0), ("SummonerMana", 180.0), ("SummonerBoost", 150.0),
            ("SummonerClairvoyance", 55.0), ("SummonerFortify", 300.0), ("SummonerRally", 270.0),
            ("SummonerRevive", 540.0), ("SummonerTeleport", 1.0),
        })
        {
            var runtime = CreateRuntime();
            var hero = Hero(runtime, Team.Order, SpawnPoint(),
                [spell, spell == "SummonerHeal" ? "SummonerFlash" : "SummonerHeal"], level: 5);
            runtime.HeroStats.Refresh(hero);
            var at = hero.Position;
            Entity Unit(EntityKind kind, UnitType type, Team team, string name, double dx)
            {
                var unit = new Entity(runtime.World.Allocate(type), kind, type, team, new Position2(at.X + dx, at.Z))
                {
                    Name = name, ChampionName = "Annie", Health = 1000.0, MaxHealth = 1000.0,
                    Alive = true, Created = true, Visible = true, Radius = 35.0,
                };
                runtime.World.Register(unit);
                return unit;
            }

            var module = runtime.Spells.SummonerScripts!;
            var binding = module.Binding(hero, 0, runtime.Spells.LoadoutHooks!.OwnerFor(hero)!)!;
            var scripted = module.Router!.RunAdjustCooldown(binding.Script, hero, 0, binding.Rank, binding);
            if (spell == "SummonerTeleport")
                TestAssert.True(scripted is null, "Teleport has no AdjustCooldown hook");
            else
                TestAssert.Near(expected, scripted ?? double.NaN, 1e-9, $"{spell}: the script's own AdjustCooldown answers");
            if (spell is not ("SummonerRevive" or "SummonerTeleport"))
                TestAssert.Near(GameData.Default.Spell(spell, 1).Cooldowns[0], expected, 1e-9, $"{spell}: inibin Cooldown");

            uint target = spell switch
            {
                "SummonerDot" or "SummonerExhaust" => Unit(EntityKind.Hero, UnitType.Hero, Team.Chaos, "ChaosSeat", 300.0).NetId,
                "SummonerSmite" => Unit(EntityKind.Neutral, UnitType.Monster, Team.Neutral, "Neutral", 300.0).NetId,
                "SummonerTeleport" => Unit(EntityKind.Minion, UnitType.Minion, Team.Order, "Minion_Basic", 300.0).NetId,
                _ => 0,
            };
            if (spell == "SummonerFortify") Unit(EntityKind.Turret, UnitType.Turret, Team.Order, "Turret_T1_C1", 150.0);
            if (spell == "SummonerRevive")
            {
                hero.Alive = false;
                hero.Health = 0.0;
                hero.RespawnAt = 20.0;
            }
            var point = spell switch
            {
                "SummonerFlash" => new Position2(at.X + 200.0, at.Z + 150.0),
                "SummonerClairvoyance" => new Position2(at.X + 1000.0, at.Z + 1000.0),
                _ => new Position2(at.X + (target != 0 ? 300.0 : 0.0), at.Z),
            };
            TestAssert.True(Cast(runtime, hero, 0, point, target), $"{spell} cast");
            TestAssert.Near(expected, hero.SummonerCooldowns[0], 1e-9, $"{spell}: cast cooldown");
            TestAssert.Equal(0, module.ScriptFailures.Count, $"{spell}: no script failure");
        }
    }

    [Check("spells-summoners-advanced-fortify-rally")]
    public static void FortifyAndRallyLifecycle()
    {
        var runtime = CreateRuntime();
        var hero = Hero(runtime, Team.Order, SpawnPoint(),
            ["SummonerFortify", "SummonerRally"], level: 4);
        // Batch 203: the tower buff is the shipped APBonusDamageToTowers add (a champion's CharOnActivate; this helper-built hero has none, so the
        // add is made here). Its OnBuffActivate puts FortifyCheck on the hero, once per summoner slot holding SummonerFortify. FortifyBuff is the
        // shipped FortifyCheck script's own work, and its BuffOnUpdateActions runs on that buff's TickRate of 3, so a spell tick alone has no
        // ready marker yet.
        runtime.Spells.UnitScripts!.AddShippedBuff(hero, "APBonusDamageToTowers", BuffType.Internal, 25000.0);
        runtime.Spells.Tick(0.0);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "FortifyCheck"),
            "FortifyCheck on a hero that carries Fortify");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "FortifyBuff"),
            "the shipped three-second FortifyCheck poll has not run yet");
        runtime.Buffs.TickBuffs(hero, 3.0);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "FortifyBuff"),
            "ready Fortify marker");
        var turret = new Entity(runtime.World.Allocate(UnitType.Turret),
            EntityKind.Turret, UnitType.Turret, Team.Order,
            new Position2(hero.Position.X + 150.0, hero.Position.Z))
        {
            Name = "Turret_T1_C1", ChampionName = "Annie", Health = 1000.0,
            MaxHealth = 1000.0, Alive = true, Created = true,
            AttackPeriod = 1.0, AttackSpeedMod = 1.0,
        };
        runtime.World.Register(turret);
        var minion = new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, Team.Order,
            new Position2(hero.Position.X + 250.0, hero.Position.Z))
        {
            Name = "Minion_Basic", ChampionName = "Annie", Health = 1000.0,
            MaxHealth = 1000.0, Alive = true, Created = true,
        };
        runtime.World.Register(minion);
        // FortifyBuff's shipped BuffOnHitUnit adds 9.0 to DamageAmount of its holder's basic attack against an AI unit that is
        // neither a turret nor a hero (FortifyBuff.luaobj). The hand-written FortifyBasicAttackBonus answered the same number
        // as a query; the script only shows it on a landed swing, so the marked swing and the cooling one are compared.
        var marked = Swing(runtime, hero, minion);
        TestAssert.True(Cast(runtime, hero, 0, hero.Position),
            "Fortify cast");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "FortifyBuff"),
            "Fortify marker removed while cooling down");
        TestAssert.Near(9.0, marked - Swing(runtime, hero, minion), 1e-9,
            "ready Fortify basic AI bonus");
        var fortify = turret.Buffs.Single(buff => buff.Name == "SummonerFortify");
        TestAssert.True(fortify.Payload.PhysicalImmune && fortify.Payload.MagicImmune &&
            !fortify.Payload.Invulnerable,
            "Fortify turret immunity");
        TestAssert.Near(2.0, turret.AttackSpeedMod, 1e-9,
            "Fortify turret attack speed");

        hero.SummonerCooldowns[1] = 0.0;
        TestAssert.True(Cast(runtime, hero, 1, hero.Position), "Rally cast");
        // SummonerRally.luaobj spawns the banner itself (BBSpawnMinion Name 'Beacon', Skin 'SummonerBeacon', IsWard, no
        // GoldRedirectTarget) and gives it BeaconAura; the engine holds no rally clock of its own any more.
        var beacon = runtime.World.Entities.Values.Single(unit =>
            unit.Name == "Beacon");
        TestAssert.True(beacon.IsWard && beacon.IsSummonerBeacon && beacon.OwnerNetId == 0,
            "Rally beacon uses ward wire flag without pet ownership");
        // The health is the character record's pool (base 200) plus BeaconAura's own IncFlatHPPoolMod of
        // NextBuffVars.BonusHealth (OwnerLevel * 25), the same number the hand-written beacon set in one piece.
        TestAssert.Near(200.0 + 25.0 * hero.Level, beacon.MaxHealth, 1e-9,
            "Rally beacon health");
        TestAssert.True(beacon.Buffs.Any(buff =>
            buff.Name == "BeaconAura" && buff.Type == BuffType.Aura && buff.Payload.MagicImmune),
            "Rally beacon wears the shipped BeaconAura buff with magic immunity");
        // The aura scan is BeaconAura's own BuffOnUpdateActions (BBExecutePeriodically every second, then
        // BBForEachUnitInTargetAreaAddBuff), so it is the beacon's buffs that have to be updated: production updates
        // every entity's every simulation step. The first update seeds the periodic's LastTimeExecuted (its
        // ExecuteImmediately is false), which one coarse check step does the same way before the clock reaches the due one.
        runtime.Buffs.TickBuffs(beacon, 0.0);
        runtime.Spells.Tick(1.0);
        runtime.Buffs.TickBuffs(beacon, 1.0);
        runtime.Buffs.TickBuffs(minion, 1.0);
        TestAssert.True(minion.Buffs.Any(buff => buff.Name == "BeaconAuraNoParticle"),
            "Rally first aura scan");
        // The banner's end is the shipped BeaconAura.OnBuffDeactivate: 5000 internal raw damage to its own Owner.
        runtime.Spells.Tick(14.1);
        runtime.Buffs.TickBuffs(beacon, 14.1);
        TestAssert.False(beacon.Alive, "Rally beacon expires");
    }

    [Check("spells-summoners-fortify-splash-and-rally-ap")]
    public static void FortifySplashAndRallyAbilityPower()
    {
        var runtime = CreateRuntime();
        var hero = Hero(runtime, Team.Order, SpawnPoint(),
            ["SummonerFortify", "SummonerDot"], level: 4);
        hero.Masteries = MasteryPage.Create(new MasteryCatalog(), new Dictionary<int, int>
        {
            [110] = 3, [109] = 3, [114] = 4, [112] = 3, [113] = 3,
            [144] = 2, [127] = 1, [108] = 4, [131] = 1, [111] = 3,
            [145] = 1,
        });
        runtime.HeroStats.Refresh(hero);
        runtime.Spells.Tick(0.0);

        Entity Unit(UnitType type, EntityKind kind, Team team, Position2 position,
            double health, double armor = 0.0)
        {
            var unit = new Entity(runtime.World.Allocate(type), kind, type, team, position)
            {
                Name = type == UnitType.Turret ? "Turret_T1_C1" : "FortifyTarget",
                ChampionName = "Annie", Health = health, MaxHealth = health,
                Armor = armor, Alive = true, Created = true,
            };
            runtime.World.Register(unit);
            return unit;
        }

        var turret = Unit(UnitType.Turret, EntityKind.Turret, Team.Order,
            new Position2(hero.Position.X + 150.0, hero.Position.Z), 1000.0);
        TestAssert.True(Cast(runtime, hero, 0, hero.Position),
            "Fortify splash cast");
        // The mastery marker is the script's own NextBuffVars hand-off (SummonerFortify.luaobj TargetExecute:
        // `NextBuffVars.Splash = (AvatarVars.FortifySplashDamage == 1.0)`), read back as the buff instance's InstanceVars,
        // not a payload flag on the row.
        TestAssert.True(Splash(runtime, turret), "Fortify carries the mastery splash marker on the turret");

        var primary = Unit(UnitType.Minion, EntityKind.Minion, Team.Chaos,
            new Position2(turret.Position.X + 150.0, turret.Position.Z), 50.0);
        var enemyHero = Unit(UnitType.Hero, EntityKind.Hero, Team.Chaos,
            new Position2(primary.Position.X + 100.0, primary.Position.Z), 1000.0, 100.0);
        var enemyMinion = Unit(UnitType.Minion, EntityKind.Minion, Team.Chaos,
            new Position2(primary.Position.X + 200.0, primary.Position.Z), 1000.0);
        var enemyMonster = Unit(UnitType.Monster, EntityKind.Monster, Team.Chaos,
            new Position2(primary.Position.X + 250.0, primary.Position.Z), 1000.0);
        var farMinion = Unit(UnitType.Minion, EntityKind.Minion, Team.Chaos,
            new Position2(primary.Position.X + 251.0, primary.Position.Z), 1000.0);
        var enemyTurret = Unit(UnitType.Turret, EntityKind.Turret, Team.Chaos,
            new Position2(primary.Position.X, primary.Position.Z + 100.0), 1000.0);

        // The primary is lethal. ApplyHit invokes the basic-hit hook before
        // Kill, so Fortify must still splash around this victim.
        TestAssert.True(runtime.Combat.ApplyHit(turret.NetId, primary.NetId, 100.0,
            DamageType.Physical, DamageSourceKind.Basic), "lethal fortified turret hit");
        TestAssert.False(primary.Alive, "primary victim dies after Fortify callback");
        TestAssert.Near(975.0, enemyHero.Health, 1e-9,
            "Fortify secondary physical hit respects 100 armor");
        TestAssert.Near(950.0, enemyMinion.Health, 1e-9,
            "Fortify hits an enemy minion");
        TestAssert.Near(950.0, enemyMonster.Health, 1e-9,
            "Fortify keeps the native minion-AI mapping for Monster entities");
        TestAssert.Near(1000.0, farMinion.Health, 1e-9,
            "Fortify excludes eligible AI actors outside 250 units");
        TestAssert.Near(1000.0, enemyTurret.Health, 1e-9,
            "Fortify excludes enemy turrets");

        var ordinaryRuntime = CreateRuntime();
        var ordinaryHero = Hero(ordinaryRuntime, Team.Order, SpawnPoint(),
            ["SummonerFortify", "SummonerDot"], level: 4);
        ordinaryRuntime.Spells.Tick(0.0);
        Entity Ordinary(UnitType type, EntityKind kind, Team team, Position2 position,
            double health)
        {
            var unit = new Entity(ordinaryRuntime.World.Allocate(type), kind, type, team, position)
            {
                Name = type == UnitType.Turret ? "Turret_T1_C1" : "FortifyTarget",
                ChampionName = "Annie", Health = health, MaxHealth = health,
                Alive = true, Created = true,
            };
            ordinaryRuntime.World.Register(unit);
            return unit;
        }
        var ordinaryTurret = Ordinary(UnitType.Turret, EntityKind.Turret, Team.Order,
            new Position2(ordinaryHero.Position.X + 150.0, ordinaryHero.Position.Z), 1000.0);
        var ordinaryPrimary = Ordinary(UnitType.Minion, EntityKind.Minion, Team.Chaos,
            new Position2(ordinaryTurret.Position.X + 150.0, ordinaryTurret.Position.Z), 1000.0);
        var ordinarySecondary = Ordinary(UnitType.Minion, EntityKind.Minion, Team.Chaos,
            new Position2(ordinaryPrimary.Position.X + 100.0, ordinaryPrimary.Position.Z), 1000.0);
        TestAssert.True(Cast(ordinaryRuntime, ordinaryHero, 0, ordinaryHero.Position),
            "ordinary Fortify cast");
        TestAssert.False(Splash(ordinaryRuntime, ordinaryTurret), "ordinary Fortify has no splash marker");
        TestAssert.True(ordinaryRuntime.Combat.ApplyHit(ordinaryTurret.NetId,
            ordinaryPrimary.NetId, 100.0, DamageType.Physical, DamageSourceKind.Basic),
            "ordinary fortified turret hit");
        TestAssert.Near(1000.0, ordinarySecondary.Health, 1e-9,
            "ordinary Fortify does not recurse into splash");

        var rallyRuntime = CreateRuntime();
        var rallyHero = Hero(rallyRuntime, Team.Order, SpawnPoint(),
            ["SummonerRally", "SummonerDot"], level: 4);
        rallyHero.Masteries = MasteryPage.Create(new MasteryCatalog(), new Dictionary<int, int>
        {
            [103] = 3, [106] = 3, [136] = 1, [126] = 1, [102] = 4,
            [105] = 4, [146] = 1, [101] = 3, [123] = 1, [143] = 1,
        });
        rallyRuntime.HeroStats.Refresh(rallyHero);
        var ally = new Entity(rallyRuntime.World.Allocate(UnitType.Minion), EntityKind.Minion,
            UnitType.Minion, Team.Order,
            new Position2(rallyHero.Position.X, rallyHero.Position.Z + 200.0))
        {
            Name = "RallyAlly", ChampionName = "Annie", Health = 1000.0,
            MaxHealth = 1000.0, Alive = true, Created = true,
        };
        rallyRuntime.World.Register(ally);
        TestAssert.True(Cast(rallyRuntime, rallyHero, 0, rallyHero.Position),
            "Improved Rally cast");
        // Each banner spreads its aura from its own BeaconAura.BuffOnUpdateActions (the shipped BBExecutePeriodically
        // every second, then BBForEachUnitInTargetAreaAddBuff), so a step updates the banners' buffs the way production
        // updates every entity's every simulation step. The scan's first update only seeds its LastTimeExecuted
        // (ExecuteImmediately is false), which production's first step after the cast is; a seed runs that update at the
        // cast clock before the step carries the clock past the due second.
        void Step(double seconds, bool seed = false)
        {
            var banners = rallyRuntime.World.Entities.Values
                .Where(entity => entity.IsSummonerBeacon).ToArray();
            if (seed)
                foreach (var banner in banners) rallyRuntime.Buffs.TickBuffs(banner, 0.0);
            rallyRuntime.Spells.Tick(seconds);
            foreach (var banner in banners) rallyRuntime.Buffs.TickBuffs(banner, seconds);
        }

        Step(1.0, seed: true);
        var amount = 8.5294 + 1.47059 * rallyHero.Level;
        var apAura = ally.Buffs.Single(buff => buff.Name == "BeaconAuraNoParticleAP");
        var apAuraSlot = apAura.Slot;
        TestAssert.False(ally.Buffs.Any(buff => buff.Name == "BeaconAuraNoParticle"),
            "Improved Rally does not add the ordinary aura alongside AP");
        TestAssert.Near(amount, apAura.Payload.AttackDamage, 1e-9,
            "Improved Rally physical aura value");
        TestAssert.Near(amount * 2.0, apAura.Payload.AbilityPower, 1e-9,
            "Improved Rally AP aura value");

        var ordinaryRally = Hero(rallyRuntime, Team.Order,
            new Position2(rallyHero.Position.X + 300.0, rallyHero.Position.Z),
            ["SummonerRally", "SummonerDot"], level: 4);
        TestAssert.True(Cast(rallyRuntime, ordinaryRally, 0, ordinaryRally.Position),
            "ordinary Rally cast beside Improved Rally");
        Step(1.0, seed: true);
        var renewedApAura = ally.Buffs.Single(buff => buff.Name == "BeaconAuraNoParticleAP");
        TestAssert.True(ReferenceEquals(apAura, renewedApAura),
            "Improved Rally renews the existing aura instance");
        TestAssert.Equal(apAuraSlot, renewedApAura.Slot,
            "Improved Rally renewal keeps the aura slot");
        TestAssert.Equal(1, ally.Buffs.Count(buff => buff.Name == "BeaconAuraNoParticleAP"),
            "Improved Rally renews one AP aura across ticks");
        TestAssert.Equal(1, ally.Buffs.Count(buff => buff.Name == "BeaconAuraNoParticle"),
            "ordinary Rally renews one ordinary aura across ticks");
        foreach (var beacon in rallyRuntime.World.Entities.Values
                     .Where(entity => entity.IsSummonerBeacon).ToArray())
            rallyRuntime.Combat.Kill(beacon, 0);
        rallyRuntime.Spells.Tick(1.2);
        rallyRuntime.Buffs.TickBuffs(ally, 1.2);
        TestAssert.False(ally.Buffs.Any(buff => buff.Name is
            "BeaconAuraNoParticleAP" or "BeaconAuraNoParticle"),
            "Rally aura expires after both beacons stop renewing it");
    }

    /// <summary>
    /// Batch 551: the phase of the swing <c>SummonerFortify.BuffOnHitUnit</c> reads as <c>DamageAmount</c>. The shipped scripts settle
    /// it: the splash is <c>DamageAmount x 0.5</c> dealt again as <c>PHYSICAL_DAMAGE</c> through <c>BBApplyDamage</c>, which takes
    /// each secondary's armour, so the amount it halves is the hit before the primary's armour (after it, armour would be taken
    /// twice); and the hook is the one 49 shipped on-hit scripts write <c>DamageAmount</c> in (APBonusDamageToTowers adds AP / 2.5,
    /// StructureDamageReduction keeps 0.25), which only means something before mitigation. The splash check above hits an
    /// unarmoured primary, so both phases gave the same number; this one armours the primary.
    /// </summary>
    [Check("spells-summoners-fortify-splash-reads-the-unmitigated-hit")]
    public static void FortifySplashReadsTheUnmitigatedHit()
    {
        var runtime = CreateRuntime();
        var hero = Hero(runtime, Team.Order, SpawnPoint(), ["SummonerFortify", "SummonerDot"], level: 4);
        hero.Masteries = MasteryPage.Create(new MasteryCatalog(), new Dictionary<int, int>
        {
            [110] = 3, [109] = 3, [114] = 4, [112] = 3, [113] = 3,
            [144] = 2, [127] = 1, [108] = 4, [131] = 1, [111] = 3,
            [145] = 1,
        });
        runtime.HeroStats.Refresh(hero);
        runtime.Spells.Tick(0.0);
        Entity Unit(UnitType type, EntityKind kind, Team team, Position2 position, double armor)
        {
            var unit = new Entity(runtime.World.Allocate(type), kind, type, team, position)
            {
                Name = type == UnitType.Turret ? "Turret_T1_C1" : "FortifyTarget",
                ChampionName = "Annie", Health = 1000.0, MaxHealth = 1000.0,
                Armor = armor, Alive = true, Created = true,
            };
            runtime.World.Register(unit);
            return unit;
        }

        var turret = Unit(UnitType.Turret, EntityKind.Turret, Team.Order,
            new Position2(hero.Position.X + 150.0, hero.Position.Z), 0.0);
        TestAssert.True(Cast(runtime, hero, 0, hero.Position), "Fortify cast");
        TestAssert.True(Splash(runtime, turret), "Reinforce arms the splash");
        var primary = Unit(UnitType.Minion, EntityKind.Minion, Team.Chaos,
            new Position2(turret.Position.X + 150.0, turret.Position.Z), 100.0);
        var secondary = Unit(UnitType.Minion, EntityKind.Minion, Team.Chaos,
            new Position2(primary.Position.X + 100.0, primary.Position.Z), 0.0);
        TestAssert.True(runtime.Combat.ApplyHit(turret.NetId, primary.NetId, 100.0,
            DamageType.Physical, DamageSourceKind.Basic), "fortified turret hit on an armoured primary");
        TestAssert.Near(950.0, primary.Health, 1e-9, "the primary takes 100 through its 100 armour");
        TestAssert.Near(950.0, secondary.Health, 1e-9,
            "the splash is half the unmitigated 100 (50), not half the primary's mitigated 50 (25)");
    }

    /// <summary>
    /// Batch 148 part 4: Promote is not offered for Summoner's Rift in this build, so it stays unimplemented and its
    /// shipped <c>SummonerPromote.luaobj</c> stays unrun. The spell's assets ship, but the spell record does not, and the
    /// AIR client's own summoner list does not carry it either. See
    /// docs/maintenance/2026-10-21-batch-148-advanced-summoners.md.
    /// </summary>
    [Check("spells-summoners-promote-not-offered")]
    public static void PromoteIsNotOfferedInThisBuild()
    {
        var paths = GameData.Default.Paths;
        // The spell's assets ship: the cast script, its two buff rows and the cast particle.
        foreach (var asset in new[] { "SummonerPromote.luaobj", "SummonerPromote.fx",
                     "PromoteAura.luaobj", "PromoteBuff.luaobj" })
            TestAssert.True(File.Exists(paths.Data($"Spells/{asset}")), $"{asset} ships");

        // The spell record does not. The stock client builds its spell-name index from Data\Spells\*.inibin (the
        // listing helper's "Data\Spells\*.ini" pattern matches those files, see GameFolderReport), so a spell with no
        // record cannot be named at all: the client asserts on a ChangeSlotSpellName whose spell it cannot find
        // (the spell-book change routine), which is what GameData.HasSpell guards.
        TestAssert.False(File.Exists(paths.Data("Spells/SummonerPromote.inibin")),
            "SummonerPromote ships no spell record");
        TestAssert.False(GameData.Default.HasSpell("SummonerPromote"),
            "the client's spell index cannot resolve SummonerPromote");

        // The Dominion variant of the same spell does have a record: the missing one is Map1's, not the pair's.
        TestAssert.True(File.Exists(paths.Data("Spells/SummonerOdinPromote.inibin")),
            "SummonerOdinPromote (Dominion) ships a spell record");
        TestAssert.True(GameData.Default.HasSpell("SummonerOdinPromote"), "and the index resolves it");

        // The thirteen loadout spells are exactly the classic spell records; the only other Summoner*.inibin files are
        // the two Dominion summoners (Garrison and Promote), which this runtime does not offer either.
        foreach (var name in SummonerLoadout.CanonicalNames)
            TestAssert.True(GameData.Default.HasSpell(name), $"{name} has a spell record");
        var records = Directory.EnumerateFiles(paths.Data("Spells"), "Summoner*.inibin")
            .Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal).ToArray();
        var expected = SummonerLoadout.CanonicalNames
            .Append("SummonerOdinGarrison").Append("SummonerOdinPromote")
            .Order(StringComparer.Ordinal).ToArray();
        TestAssert.Equal(expected, records, "the summoner spell records of this build");

        // The AIR client's own summoner list agrees: the ids recovered from the four patch-126 SWFs hold the thirteen
        // classic spells and leave Promote's id 8 unused, so the loadout never offers it.
        TestAssert.Equal(SummonerLoadout.CanonicalNames.Count, AirGameDtos.SpellNames.Count,
            "the AIR summoner ids are the thirteen classic spells");
        TestAssert.False(AirGameDtos.SpellNames.Values.Contains("SummonerPromote",
            StringComparer.OrdinalIgnoreCase), "the AIR summoner list has no Promote");
        // Batch 500: Promote is the Dominion loadout name (map 8 only, AirGameDtos.SpellNamesFor), never a classic one.
        TestAssert.True(SummonerLoadout.TryCanonicalFromDisplay("Promote") == "SummonerOdinPromote",
            "Promote is the Dominion loadout name");
        TestAssert.False(SummonerLoadout.CanonicalNames.Contains("SummonerOdinPromote"), "and it is not a classic loadout name");
    }

    [Check("spells-summoners-advanced-dead-and-cc-gates")]
    public static void DeadAndCrowdControlGates()
    {
        var runtime = CreateRuntime();
        var hero = Hero(runtime, Team.Order, SpawnPoint(),
            ["SummonerTeleport", "SummonerFortify"]);
        var target = new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, Team.Order,
            new Position2(hero.Position.X + 300.0, hero.Position.Z))
        {
            Name = "Minion_Basic", ChampionName = "Annie", Health = 1000.0,
            MaxHealth = 1000.0, Alive = true, Created = true,
        };
        runtime.World.Register(target);
        hero.Alive = false;
        hero.RespawnAt = 10.0;
        var before = hero.SummonerCooldowns.ToArray();
        TestAssert.False(Cast(runtime, hero, 0, target.Position, target.NetId),
            "dead Teleport rejected");
        TestAssert.Equal(before[0], hero.SummonerCooldowns[0],
            "dead Teleport does not cool down");
        // The dead gate is the spell's own SpellData*IsDisabledWhileDead, which Fortify alone among the summoners ships as 0
        // (the unit's cast check), so a dead hero's Fortify goes out through the same script path as a living cast.
        TestAssert.True(Cast(runtime, hero, 1, hero.Position),
            "dead Fortify remains castable");
    }

    private static MatchRuntime CreateRuntime() =>
        MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default, solo: true);

    private static Position2 SpawnPoint() =>
        new(MapDefinitions.HeroSpawn.X, MapDefinitions.HeroSpawn.Z);

    private static Entity Hero(MatchRuntime runtime, Team team, Position2 position,
        IReadOnlyList<string> spells, int level = 1)
    {
        var hero = new Entity(runtime.World.Allocate(UnitType.Hero), EntityKind.Hero,
            UnitType.Hero, team, position)
        {
            Name = team == Team.Order ? "OrderSeat" : "ChaosSeat",
            ChampionName = "Annie", Alive = true, Created = true, Visible = true,
            Health = 1000.0, MaxHealth = 1000.0, Mana = 1000.0, MaxMana = 1000.0,
            Level = level, Speed = 325.0, Radius = 35.0, AttackDamage = 100.0,
            AttackPeriod = 1.6, AttackWindup = 0.3,
        };
        hero.SummonerSpells = spells.ToList();
        runtime.World.Register(hero);
        return hero;
    }

    private static bool Cast(MatchRuntime runtime, Entity caster, int slot,
        Position2 point, uint target = 0) =>
        runtime.Spells.HandleCastRequest(caster,
            EncodeCast(slot, point, target), runtime.World.Clock.Now());

    /// <summary>
    /// One production simulation step (ProductionMatchComposition.Tick): the spell clock first, then every
    /// unit's buffs. A channel buff therefore runs its <c>OnBuffDeactivate</c> after the step's own cooldown
    /// decrement, and a spell tick alone never ends a buff.
    /// </summary>
    private static void Tick(MatchRuntime runtime, double seconds)
    {
        runtime.Spells.Tick(seconds);
        foreach (var unit in runtime.World.Entities.Values.ToArray()) runtime.Buffs.TickBuffs(unit, seconds);
    }

    /// <summary>
    /// Takes the running Teleport channel off its caster the way an external removal does, so the next cast
    /// starts from a clean slot. The shipped <c>OnBuffDeactivate</c> restores the slot and writes the
    /// interrupted cooldown as the buff leaves.
    /// </summary>
    private static void EndChannel(MatchRuntime runtime, Entity hero)
    {
        var channel = hero.Buffs.SingleOrDefault(buff => buff.Name == "SummonerTeleport");
        if (channel is not null) runtime.SpellBuffs.Remove(hero, channel.Slot);
    }

    /// <summary>The health one plain basic attack of <paramref name="attacker"/> takes off <paramref name="target"/>.</summary>
    private static double Swing(MatchRuntime runtime, Entity attacker, Entity target)
    {
        var before = target.Health;
        TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, target.NetId, 100.0,
            DamageType.Physical, DamageSourceKind.Basic), "measured swing lands");
        return before - target.Health;
    }

    /// <summary>Whether the turret's shipped <c>SummonerFortify</c> instance carries the mastery splash marker.</summary>
    private static bool Splash(MatchRuntime runtime, Entity turret)
    {
        var fortify = turret.Buffs.Single(buff => buff.Name == "SummonerFortify");
        return runtime.Spells.SummonerScripts!.Buffs!.InstanceOf(fortify)!.InstanceVars["Splash"] is true;
    }

    private static byte[] EncodeCast(int slot, Position2 point, uint target)
    {
        var payload = new byte[34];
        payload[0] = 0xA2;
        payload[5] = checked((byte)((slot << 1) | 1));
        WriteSingle(payload, 6, (float)point.X);
        WriteSingle(payload, 10, 0.0f);
        WriteSingle(payload, 14, (float)point.Z);
        WriteSingle(payload, 18, (float)point.X);
        WriteSingle(payload, 22, 0.0f);
        WriteSingle(payload, 26, (float)point.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(30, 4), target);
        return payload;
    }

    private static void WriteSingle(byte[] payload, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4),
            BitConverter.SingleToInt32Bits(value));


}
