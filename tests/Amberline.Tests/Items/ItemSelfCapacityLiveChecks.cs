using System.Buffers.Binary;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Simulation;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("items-live-self-active-capacity-before-fx")]
    public static void SelfItemActivesRejectFullTablesBeforeCastFxAndRenewInPlace()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var (itemId, alias) in new[]
                 {
                     (3142, "SpectralFury"), (3157, "ZhonyasRingShield"),
                     (3131, "LightstrikerBuff"), (3183, "Haste"),
                     (3184, "OdinEntropicClaymore"),
                 })
        {
            using var match = new Match("Annie", team);
            var hero = match.Hero(0);
            hero.Inventory = [new InventorySlot(itemId), null, null, null, null, null];
            match.Runtime.HeroStats.Refresh(hero);
            FillItemBuffSlots(match.Runtime, hero, 64);
            var filler = hero.Buffs.Last();
            match.Runtime.Delivery.Flush();
            match.Clear();

            TestAssert.False(match.TryCast(0, 4, hero),
                $"{team}/{itemId}: new buff needs a slot");
            TestAssert.Near(0, hero.ItemCooldowns[0], 0,
                $"{team}/{itemId}: rejected active spends no cooldown");
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == alias));
            TestAssert.False(match.Captures[0].Packets.Any(packet => Opcode(packet) is
                FxPackets.CreateGroupOpcode or 0x8A or 0xBD or 0xA7),
                $"{team}/{itemId}: rejection precedes cast FX and committed wire");

            TestAssert.True(match.Runtime.Buffs.RemoveBuff(hero, filler.Slot));
            match.Clear();
            TestAssert.True(match.TryCast(0, 4, hero),
                $"{team}/{itemId}: one freed slot allows the normal cast");
            var original = hero.Buffs.Single(buff => buff.Name == alias);
            TestAssert.Equal(64, hero.Buffs.Count);
            if (itemId is 3142 or 3184)
            {
                var fxHash = SpellPackets.BuffNameHash("spectral_fury_cas");
                var packets = match.Captures[0].Packets;
                var fx = packets.FindIndex(packet => Opcode(packet) == FxPackets.CreateGroupOpcode &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == fxHash);
                var buff = packets.FindIndex(packet => Opcode(packet) == SpellPackets.BuffAddOpcode &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[9..]) ==
                    SpellPackets.BuffNameHash(alias));
                TestAssert.True(fx >= 0 && buff > fx,
                    $"{team}/{itemId}: successful cast preserves FX-before-buff order");
            }
            if (itemId == 3157) continue; // stasis itself blocks a recast
            hero.ItemCooldowns[0] = 0.0;
            match.Clear();
            TestAssert.True(match.TryCast(0, 4, hero),
                $"{team}/{itemId}: exact-name renewal works at 64 occupied slots");
            TestAssert.Equal(64, hero.Buffs.Count);
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == alias));
            // Batch 149 step 4: the add type is the shipped script's own now, not the AddBuff every active used to
            // make. SpectralFury and LightstrikerBuff say BUFF_RENEW_EXISTING and keep the live instance in place;
            // Prilisa's Haste and Entropic Claymore's own alias say BUFF_REPLACE_EXISTING, and that path removes the
            // old same-name instance and makes a new one in the table it retained.
            var renewed = hero.Buffs.Single(buff => buff.Name == alias);
            if (itemId is 3183 or 3184)
                TestAssert.False(ReferenceEquals(original, renewed),
                    "BUFF_REPLACE_EXISTING makes a new same-name instance while retaining capacity");
            else
                TestAssert.True(ReferenceEquals(original, renewed),
                    "BUFF_RENEW_EXISTING keeps the live instance while retaining capacity");
        }

        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var itemId in new[] { 3140, 3180 })
        {
            using var match = new Match("Annie", team);
            var hero = match.Hero(0);
            hero.Inventory = [new InventorySlot(itemId), null, null, null, null, null];
            match.Runtime.HeroStats.Refresh(hero);
            FillItemBuffSlots(match.Runtime, hero, 64);
            TestAssert.True(match.TryCast(0, 4, hero),
                $"{team}/{itemId}: QSS/Odyn has no direct buff capacity requirement");
        }
    }

    [Check("items-live-aoe-active-saturated-recipients")]
    public static void AoeItemActivesSkipOnlySaturatedRecipients()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using (var match = new Match("Annie", team, ally: true))
            {
                var hero = match.Hero(0);
                var ally = match.Hero(2);
                hero.Inventory = [new InventorySlot(3069), null, null, null, null, null];
                match.Runtime.HeroStats.Refresh(hero);
                FillItemBuffSlots(match.Runtime, hero, 64);
                match.Runtime.Delivery.Flush();
                match.Clear();
                TestAssert.True(match.TryCast(0, 4, hero), "Shurelya still casts for an eligible ally");
                TestAssert.False(hero.Buffs.Any(buff => buff.Name == "Haste"));
                TestAssert.True(ally.Buffs.Any(buff => buff.Name == "Haste"));
                FillItemBuffSlots(match.Runtime, ally, 64);
                hero.ItemCooldowns[0] = 0.0;
                TestAssert.True(match.TryCast(0, 4, hero),
                    "Shurelya renews an existing Haste at a full table");
                TestAssert.Equal(1, ally.Buffs.Count(buff => buff.Name == "Haste"));
                TestAssert.True(match.Runtime.Combat.Kill(match.Hero(1), ally.NetId));
                TestAssert.Equal(1, hero.ChampionAssists,
                    "Shurelya credits support only for the ally who received Haste");
            }
            using (var match = new Match("Annie", team, extraEnemy: true))
            {
                var hero = match.Hero(0);
                var full = match.Hero(1);
                var free = match.Hero(2);
                full.Position = hero.Position + new Position2(180, 0);
                free.Position = hero.Position + new Position2(250, 0);
                hero.Inventory = [new InventorySlot(3187), null, null, null, null, null];
                match.Runtime.HeroStats.Refresh(hero);
                FillItemBuffSlots(match.Runtime, full, 64);
                match.Runtime.Delivery.Flush();
                match.Clear();
                TestAssert.True(match.TryCastGround(0, 4, full.Position),
                    "Sweeper casts and creates its bubble with a mixed-capacity area");
                // Batch 149 step 4: the shipped HextechSweeperArea buff marks the heroes from its own
                // BuffOnUpdateActions (BBExecutePeriodically 0.25 with ExecuteImmediately, so its first buff
                // update runs the marking loop), not from the cast; the hand-written active marked them at once.
                // One simulation step is that first update (TickRate 0: every step).
                match.Runtime.Tick(0.01);
                TestAssert.False(full.Buffs.Any(buff => buff.Name == "OdinLightbringer"));
                TestAssert.True(free.Buffs.Any(buff => buff.Name == "OdinLightbringer"));
                TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0xBD),
                    "a saturated recipient does not cancel the AOE cast");
            }
        }
    }

    [Check("items-live-randuin-pair-and-mobility-capacity")]
    public static void RanduinRequiresEveryRecipientSlotWithoutHalfDebuff()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using (var match = new Match("Annie", team, ally: true))
            {
                var hero = match.Hero(0);
                var enemy = match.Hero(1);
                hero.Inventory = [new InventorySlot(3143), null, null, null, null, null];
                match.Runtime.HeroStats.Refresh(hero);
                FillItemBuffSlots(match.Runtime, enemy, 63);
                var filler = enemy.Buffs.Last();
                TestAssert.True(match.TryCast(0, 4, hero));
                TestAssert.False(enemy.Buffs.Any(buff => buff.Name is "Slow" or "BlackOmen"),
                    "one free slot cannot commit Randuin's two-debuff recipient pair");
                TestAssert.True(match.Runtime.Combat.Kill(enemy, match.Hero(2).NetId));
                // Batch 149 step 4: Spells/RanduinsOmen.luaobj marks every unit its cast loop visits with its own
                // BBApplyAssistMarker after the pair, with no branch on whether the pair landed. A declined group is
                // the capacity case the script never sees, so Reconstruction keeps the hand-written rule the marker
                // came with: the recipient that got no debuff gets no credit (BbHookFrame.HeldMarkers).
                TestAssert.Equal(0, hero.ChampionAssists,
                    "skipped recipient creates no support credit");

                match.Runtime.Combat.ScheduleRespawn(enemy);
                TestAssert.True(match.Runtime.Combat.RespawnImmediately(enemy));
                enemy.Position = hero.Position + new Position2(200, 0);
                TestAssert.True(match.Runtime.Buffs.RemoveBuff(enemy, filler.Slot));
                hero.ItemCooldowns[0] = 0.0;
                match.Clear();
                TestAssert.True(match.TryCast(0, 4, hero));
                TestAssert.True(enemy.Buffs.Any(buff => buff.Name == "Slow"));
                TestAssert.True(enemy.Buffs.Any(buff => buff.Name == "BlackOmen"));
                TestAssert.Equal(64, enemy.Buffs.Count);
                var packets = match.Captures[0].Packets;
                var castFx = packets.FindIndex(packet => Opcode(packet) == FxPackets.CreateGroupOpcode &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) ==
                    SpellPackets.BuffNameHash("RanduinsOmen_cas"));
                var slowAdd = packets.FindIndex(packet => Opcode(packet) == SpellPackets.BuffAddOpcode &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[9..]) ==
                    SpellPackets.BuffNameHash("Slow"));
                TestAssert.True(castFx >= 0 && slowAdd > castFx,
                    "Randuin's successful cast retains FX-before-debuff wire order");
                hero.ItemCooldowns[0] = 0.0;
                TestAssert.True(match.TryCast(0, 4, hero),
                    "same-name Randuin renewal is admitted with all slots occupied");
                TestAssert.Equal(1, enemy.Buffs.Count(buff => buff.Name == "Slow"));
                TestAssert.Equal(1, enemy.Buffs.Count(buff => buff.Name == "BlackOmen"));
            }
            using (var match = new Match("Annie", team))
            {
                var hero = match.Hero(0);
                var enemy = match.Hero(1);
                hero.Inventory = [new InventorySlot(3143), null, null, null, null, null];
                enemy.Inventory = [new InventorySlot(3117), null, null, null, null, null];
                // Batch 147 step 4: Items/3117.luaobj starts its Mobility penalty from its own BuffOnAllowAdd now, so
                // the copy has to be bound to the slot first (BbItemModule.SyncInventory, the same next-spell-update
                // latency the 3022 cases already account for).
                match.Runtime.Tick(0.01);
                match.Runtime.HeroStats.Refresh(hero);
                match.Runtime.HeroStats.Refresh(enemy);
                FillItemBuffSlots(match.Runtime, enemy, 62);
                var filler = enemy.Buffs.Last();
                TestAssert.True(match.TryCast(0, 4, hero));
                TestAssert.False(enemy.Buffs.Any(buff => buff.Name is
                    "Slow" or "BlackOmen" or "BootsOfMobilityDebuff"),
                    "two free slots cannot fit Slow, BlackOmen, and nested Mobility Boots debuff");
                TestAssert.True(match.Runtime.Buffs.RemoveBuff(enemy, filler.Slot));
                hero.ItemCooldowns[0] = 0.0;
                TestAssert.True(match.TryCast(0, 4, hero));
                foreach (var name in new[] { "Slow", "BlackOmen", "BootsOfMobilityDebuff" })
                    TestAssert.True(enemy.Buffs.Any(buff => buff.Name == name),
                        $"three available slots commit {name}");
            }
            using (var match = new Match("Annie", team))
            {
                var hero = match.Hero(0);
                var enemy = match.Hero(1);
                hero.Inventory = [new InventorySlot(3143), null, null, null, null, null];
                match.Runtime.HeroStats.Refresh(hero);
                TestAssert.True(match.Runtime.SpellBuffs.Add(enemy, "ResistantSkin",
                    BuffType.Aura, 10.0, enemy.NetId) is not null);
                FillItemBuffSlots(match.Runtime, enemy, 63);
                TestAssert.True(match.TryCast(0, 4, hero));
                TestAssert.False(enemy.Buffs.Any(buff => buff.Name == "Slow"),
                    "ResistantSkin rejects the Slow branch");
                TestAssert.True(enemy.Buffs.Any(buff => buff.Name == "BlackOmen"),
                    "the eligible attack-speed debuff still uses its one free slot");
            }
            using (var match = new Match("Annie", team, enemyChampion: "Yorick"))
            {
                var hero = match.Hero(0);
                hero.Inventory = [new InventorySlot(3143), null, null, null, null, null];
                match.Runtime.HeroStats.Refresh(hero);
                match.CastGround(1, hero.Position + new Position2(220, 0), slot: 1);
                match.Runtime.Tick(.3);
                var ghoul = match.Runtime.World.Entities.Values.Single(unit =>
                    unit.Kind == EntityKind.YorickGhoul && unit.Alive);
                TestAssert.True(ghoul.Buffs.Any(buff => buff.Name == "YorickDecayedLogic"),
                    "production Yorick ghoul runs its shipped Logic buff");
                FillItemBuffSlots(match.Runtime, ghoul, 63);
                TestAssert.True(match.TryCast(0, 4, hero));
                TestAssert.False(ghoul.Buffs.Any(buff => buff.Name == "Slow"),
                    "ghoul's shipped slow rejection remains active");
                TestAssert.True(ghoul.Buffs.Any(buff => buff.Name == "BlackOmen"),
                    "Randuin still applies its eligible branch to the ghoul");
            }
        }
    }
}
