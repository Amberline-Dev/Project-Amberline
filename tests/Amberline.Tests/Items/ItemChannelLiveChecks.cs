using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("items-live-qss-sleep-and-other-debuffs")]
    public static void QuicksilverRemovesSleepAlongsideOtherDebuffs()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var hero = match.Hero(0);
            hero.Inventory = [new InventorySlot(3140), new InventorySlot(3140), null, null, null, null];
            var debuffs = new[]
            {
                (Name: "Sleep", Type: BuffType.Sleep),
                (Name: "Suppression", Type: BuffType.Suppression),
                (Name: "Slow", Type: BuffType.Slow),
                (Name: "SummonerDot", Type: BuffType.DamageOverTime),
            };
            foreach (var debuff in debuffs)
                match.Runtime.Buffs.AddBuff(hero, debuff.Name, debuff.Type, 5, match.Hero(1).NetId);
            match.Runtime.Buffs.AddBuff(hero, "QssTestPositive", BuffType.CombatEnhancer, 5, hero.NetId);
            var removedSlots = hero.Buffs.Where(b => debuffs.Any(debuff => debuff.Name == b.Name))
                .Select(b => b.Slot).ToArray();
            match.Runtime.Delivery.Flush();
            match.Clear();
            match.Cast(0, 4, hero);
            TestAssert.False(hero.Buffs.Any(b => debuffs.Any(debuff => debuff.Name == b.Name)));
            TestAssert.True(hero.Buffs.Any(b => b.Name == "QssTestPositive"));
            TestAssert.Near(90, hero.ItemCooldowns[0], 0);
            TestAssert.Near(90, hero.ItemCooldowns[1], 0, "duplicate copies share QSS cooldown");
            foreach (var slot in removedSlots)
                TestAssert.True(match.Captures[0].Packets.Any(p => Opcode(p) == 0x7F &&
                    BinaryPrimitives.ReadUInt32LittleEndian(p.Plaintext.Span[1..]) == hero.NetId &&
                    p.Plaintext.Span[5] == slot), "cleansed debuffs are removed on the client");
        }
    }

    [Check("items-live-qss-preserves-janna-forced-move")]
    public static void QuicksilverPreservesJannaForcedMoveUntilNaturalExpiry()
    {
        foreach (var spellSlot in new[] { 0, 3 })
        {
            using var match = new Match("Janna", Team.Order);
            var runtime = match.Runtime;
            var janna = match.Hero(0);
            var target = match.Hero(1);
            target.Inventory = [new InventorySlot(3140), null, null, null, null, null];

            var carrierName = spellSlot == 0 ? "Move" : "MoveAway";
            if (spellSlot == 0)
            {
                target.Position = janna.Position + new Position2(300.0, 0.0);
                match.Cast(0, 0, target);
                match.Cast(0, 0, target);
            }
            else
            {
                match.Cast(0, 3, janna);
            }
            // The wave reaches the target, or Monsoon's windup ends, a few frames later: the script buff starts the displacement.
            var forcedMove = AwaitForcedMove(match, target, carrierName) ?? throw new InvalidOperationException(
                $"Janna {spellSlot} did not start forced movement");
            var carrier = target.Buffs.Single(buff => buff.Name == carrierName);
            TestAssert.True(carrier.Payload.NonDispellable,
                $"Janna {carrierName} carries the shipped non-dispel metadata");

            var beforeQss = target.Position;
            var remainingBeforeQss = forcedMove.Remaining;
            match.Clear();
            match.Cast(1, 4, target);
            TestAssert.True(target.Buffs.Any(buff => buff.Name == carrierName),
                $"QSS preserves Janna {carrierName}");
            TestAssert.True(ReferenceEquals(forcedMove, target.ForcedMove),
                $"QSS preserves Janna {carrierName} movement state");
            TestAssert.Near(90, target.ItemCooldowns[0], 0,
                "QSS consumes its item cooldown");
            TestAssert.False(match.Captures.SelectMany(capture => capture.Packets).Any(packet =>
                Opcode(packet) == 0x7F &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == target.NetId &&
                packet.Plaintext.Span[5] == carrier.Slot),
                $"QSS emits no remove packet for Janna {carrierName}");

            runtime.Tick(.1);
            if (spellSlot == 0)
                TestAssert.True(target.ForcedMove is { Completed: false } &&
                    target.ForcedMove.Remaining < remainingBeforeQss,
                    "Janna Q forced movement continues after QSS");
            else
                TestAssert.True(target.Position != beforeQss,
                    "Janna R forced movement continues after QSS");
            runtime.Tick(spellSlot == 0 ? 1.0 : .8);
            TestAssert.False(target.Buffs.Any(buff => buff.Name == carrierName),
                $"Janna {carrierName} expires through the normal lifecycle");
            TestAssert.True(target.ForcedMove is null,
                $"Janna {carrierName} clears movement on natural expiry");
        }
    }

    // The client's can-use-spell check: QSS carries CanCastWhileDisabled and CannotBeSuppressed, every other item spell
    // is disabled by fear, silence, stun, taunt, sleep, suppression and stasis, and a root alone blocks none of them.
    [Check("items-live-qss-and-root-follow-spell-disable-flags")]
    public static void QuicksilverBypassesOrdinaryFearActionLockOnly()
    {
        using (var qss = new Match("Shaco", Team.Order))
        {
            var hero = qss.Hero(1);
            hero.Inventory = [new InventorySlot(3140), null, null, null, null, null];
            qss.Cast(0, 1, hero);
            for (var step = 0; step < 60 && !hero.Buffs.Any(buff => buff.Name == "Fear"); step++)
                qss.Runtime.Tick(0.05);
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "Fear"),
                "the real Shaco box applies its action-disabled fear");
            TestAssert.True(qss.TryCast(1, 4, hero),
                "QSS can cleanse the real Shaco box fear");
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "Fear"),
                "QSS removes the box fear after the castability bypass");
        }

        using (var ordinary = new Match("Annie", Team.Order))
        {
            var hero = ordinary.Hero(0);
            hero.Inventory = [new InventorySlot(3069), null, null, null, null, null];
            ordinary.Runtime.Buffs.AddBuff(hero, "Fear", BuffType.Fear, 5.0,
                ordinary.Hero(1).NetId, new BuffPayload { ActionDisabled = true });
            TestAssert.False(ordinary.TryCast(0, 4, hero),
                "ordinary items remain blocked by action-disabled fear");
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "Fear"));
        }

        using (var stasis = new Match("Annie", Team.Order))
        {
            var hero = stasis.Hero(0);
            hero.Inventory = [new InventorySlot(3140), new InventorySlot(3157),
                null, null, null, null];
            stasis.Cast(0, 5, hero);
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "ZhonyasRingShield"));
            // Batch 335 (owner, 26 October 2026, "Block in stasis"; NOT RETAIL): retail's OnCanUseSpell lets QSS's CanCastWhileDisabled
            // through the stasis's SetStunned; the owner refuses every spell in the stasis (SpellOwnerGate.InStasis).
            TestAssert.False(stasis.TryCast(0, 4, hero),
                "QSS is refused in stasis, CanCastWhileDisabled or not (owner decision, batch 335)");
        }

        using (var root = new Match("Annie", Team.Order))
        {
            var hero = root.Hero(0);
            hero.Inventory = [new InventorySlot(2003), new InventorySlot(3157), null, null, null, null];
            hero.Health = hero.MaxHealth * 0.5;
            root.Runtime.Buffs.AddBuff(hero, "Root", BuffType.Snare, 5.0, root.Hero(1).NetId);
            TestAssert.True(root.TryCast(0, 4, hero),
                "a root clears CanMove only; a Health Potion has no CantCastWhileRooted flag");
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "RegenerationPotion"));
            TestAssert.True(root.TryCast(0, 5, hero), "Zhonya's Hourglass works while rooted");
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "ZhonyasRingShield"));
        }

        using (var silence = new Match("Annie", Team.Order))
        {
            var hero = silence.Hero(0);
            hero.Inventory = [new InventorySlot(2003), null, null, null, null, null];
            hero.Health = hero.MaxHealth * 0.5;
            silence.Runtime.Buffs.AddBuff(hero, "Silence", BuffType.Silence, 5.0, silence.Hero(1).NetId);
            TestAssert.False(silence.TryCast(0, 4, hero),
                "a silence clears CanCast; items without CanCastWhileDisabled are disabled");
        }
    }

    // Batch 335 (owner, 26 October 2026, "Block in stasis"; NOT RETAIL): Remove Scurvy ships CanCastWhileDisabled, so retail's shared
    // OnCanUseSpell lets it through Zhonya's stasis (SetStunned; the script seals only the summoner slots). The owner refuses every spell in
    // the stasis: the gate refuses it, the HUD greys it, and it is usable again once the stasis ends. A plain stun still lets it through
    // (scripting.bb-gangplank-remove-scurvy-casts-through-script-control).
    [Check("items-live-stasis-refuses-remove-scurvy")]
    public static void StasisRefusesRemoveScurvy()
    {
        using var match = new Match("Gangplank", Team.Order);
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        hero.Inventory = [new InventorySlot(3157), null, null, null, null, null];
        var here = new Position3(hero.Position.X, 0.0, hero.Position.Z);
        TestAssert.True(runtime.Spells.CanCast(hero, 1, hero.NetId, here, runtime.Spells.CurrentTime).Allowed,
            "Remove Scurvy is usable before the stasis");
        match.Cast(0, 4, hero);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "ZhonyasRingShield"), "Zhonya's Hourglass puts him in stasis");
        TestAssert.Equal(SpellCastFailure.Disabled,
            runtime.Spells.CanCast(hero, 1, hero.NetId, here, runtime.Spells.CurrentTime).Failure,
            "Remove Scurvy is refused in the stasis");
        TestAssert.Equal(0u, runtime.HeroStats.Snapshot(hero).CanCastBits1 & 0xFu, "the HUD greys every slot in the stasis");
        TestAssert.False(match.TryCast(0, 1, hero), "a Remove Scurvy request in the stasis is refused");
        for (var step = 0; step < 60 && hero.Buffs.Any(buff => buff.Name == "ZhonyasRingShield"); step++) runtime.Tick(0.05);
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "ZhonyasRingShield"), "the stasis ends");
        TestAssert.Equal(0x2u, runtime.HeroStats.Snapshot(hero).CanCastBits1 & 0x2u, "the HUD shows Remove Scurvy castable again");
        TestAssert.True(match.TryCast(0, 1, hero), "Remove Scurvy is cast after the stasis");
    }

    [Check("items-live-guardian-angel-clears-source-set")]
    public static void GuardianAngelClearsPositiveStatusesBeforeRevival()
    {
        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        hero.Inventory = [new InventorySlot(3026), null, null, null, null, null];
        // Batch 149: the copy binds to its inventory slot on the next spell update (BbItemModule.SyncInventory); only
        // then does Items/3026.luaobj keep WillRevive alive, whose BuffOnPreDamage is the revive.
        match.Runtime.Tick(0.01);
        match.Runtime.Buffs.AddBuff(hero, "ReviveHaste", BuffType.Haste, 5.0, hero.NetId);
        match.Runtime.Buffs.AddBuff(hero, "ReviveInvisibility", BuffType.Invisibility, 5.0,
            hero.NetId);
        TestAssert.True(match.Runtime.Combat.ApplyHit(enemy.NetId, hero.NetId,
            hero.Health + 1.0, DamageType.True));
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "GuardianAngel"));
        TestAssert.False(hero.Buffs.Any(buff => buff.Name is "ReviveHaste" or
            "ReviveInvisibility"), "Guardian Angel clears the shipped positive set");
        TestAssert.True(hero.Alive && hero.Health > 0.0,
            "the first lethal hit enters the normal Guardian Angel revival state");
        match.Runtime.Tick(4.1);
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "GuardianAngel"));
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "HasBeenRevived"));
        TestAssert.True(match.Runtime.Combat.ApplyHit(enemy.NetId, hero.NetId,
            hero.Health + 1.0, DamageType.True));
        TestAssert.False(hero.Alive, "a second lethal hit follows the normal death path");
    }

    [Check("items-live-janna-stasis-cancels-monsoon")]
    public static void StasisStopsMonsoonHealingAndItsEffect()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Janna", team, ally: true);
            var hero = match.Hero(0);
            var ally = match.Hero(2);
            hero.Inventory = [new InventorySlot(3157), null, null, null, null, null];
            match.Cast(0, 3, hero);
            Advance(match, .3);
            TestAssert.True(JannaChannel(match, hero) is not null, "Monsoon is channelling once its windup ended");
            // ZhonyasHourglass.luaobj calls BBStopChanneling before the stasis buff, so the
            // channel ends at activation rather than on the next tick.
            match.Clear();
            match.Cast(0, 4, hero);
            TestAssert.True(hero.Buffs.Any(b => b.Name == "ZhonyasRingShield"));
            TestAssert.True(JannaChannel(match, hero) is null, "activation ends Monsoon at once");
            ally.Health = 100;
            match.Runtime.Spells.TickLateChampions(.5);
            match.Runtime.Delivery.Flush();
            TestAssert.True(JannaChannel(match, hero) is null, "stasis has the shipped stunned status and interrupts Monsoon");
            TestAssert.False(hero.Buffs.Any(b => b.Name == "ReapTheWhirlwind"));
            TestAssert.Near(100, ally.Health, 0, "no channel pulse is allowed during stasis");
        }
    }

    [Check("spells-summoners-stasis-seals-both-slots")]
    public static void StasisSealsEverySummonerFamily()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Annie", team);
            var hero = match.Hero(0);
            hero.Inventory = [new InventorySlot(3157), null, null, null, null, null];
            match.Cast(0, 4, hero);
            foreach (var spell in new[] { "SummonerFlash", "SummonerDot", "SummonerHeal", "SummonerHaste",
                         "SummonerExhaust", "SummonerSmite", "SummonerBoost", "SummonerClairvoyance",
                         "SummonerMana", "SummonerTeleport", "SummonerRevive", "SummonerRally", "SummonerFortify" })
            for (int slot = 0; slot < 2; slot++)
            {
                hero.SummonerSpells[slot] = spell;
                var packet = new byte[34]; packet[0] = 0xA2; packet[5] = (byte)((slot << 1) | 1);
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), hero.NetId);
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(30), match.Hero(1).NetId);
                foreach (var offset in new[] { 6, 18 })
                {
                    BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(offset), (float)hero.Position.X);
                    BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(offset + 8), (float)hero.Position.Z);
                }
                match.Clear();
                TestAssert.False(match.Runtime.Spells.HandleCastRequest(hero, packet, match.Runtime.Spells.CurrentTime),
                    $"{team}/{spell}/slot {slot} is sealed during stasis");
                TestAssert.Near(0, hero.SummonerCooldowns[slot], 0, "rejection must not spend cooldown");
                match.Runtime.Delivery.Flush();
                TestAssert.False(match.Captures.SelectMany(c => c.Packets).Any(p => Opcode(p) == 0xBD));
            }
            match.Runtime.Tick(2.6);
            TestAssert.False(hero.Buffs.Any(b => b.Name == "ZhonyasRingShield"));
            hero.Health = 100;
            hero.SummonerSpells[0] = "SummonerHeal";
            match.Cast(0, 0, hero, summoner: true);
            TestAssert.True(hero.Health > 100, "the slot is usable when stasis expires");
            TestAssert.Near(270, hero.SummonerCooldowns[0], 0);
        }
    }
}
