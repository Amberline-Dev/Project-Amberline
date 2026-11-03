using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-lee-r-secondary-full-target-hits-once")]
    public static void LeeSecondaryKickCapacityStillHitsOnlyOnce()
    {
        // 19 October 2026: the sweep is the kick buff's BuffOnUpdateActions and the marker is BlindMonkRMarker (the script's own once-per-kick
        // guard). The hand-written per-kick set that stood in for a marker the table could not hold has no counterpart, so the 0 free case
        // (a marker that cannot be added is swept again on every tick) is gone.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var free in new[] { 2, 3 })
        {
            using var match = new Match("LeeSin", team, extraEnemy: true);
            var lee = match.Hero(0);
            var primary = match.Hero(1);
            var secondary = match.Hero(2);
            lee.Position = new Position2(7032, 6560);
            primary.Position = new Position2(6999, 6757);
            secondary.Position = new Position2(6941, 7102);
            secondary.Inventory = [new InventorySlot(3117), null, null, null, null, null];
            // Settles the equipment baseline: otherwise the first stat refresh (the one the Boots penalty's own add triggers) sees Mobility Boots
            // as newly bought and clears the penalty it has just added.
            match.Runtime.HeroStats.Refresh(secondary);
            FillControlCapacity(secondary);
            for (var slot = 0; slot < free; slot++) FreeControlCapacity(secondary);
            match.Runtime.Delivery.Flush();
            match.Clear();
            match.Cast(0, 3, primary);
            Advance(match, .65);
            var hits = SecondaryKickDamagePackets(match, secondary.NetId);
            TestAssert.Equal(1, hits, $"{team}/{free}: swept secondary damage lands once");
            var damage = secondary.MatchStatistics.TotalDamageTaken;
            Advance(match, .15);
            TestAssert.Equal(hits, SecondaryKickDamagePackets(match, secondary.NetId),
                "a missing marker never allows a second hit on later kick ticks");
            TestAssert.Near(damage, secondary.MatchStatistics.TotalDamageTaken, 1e-8);
            TestAssert.Equal(free != 0, secondary.Buffs.Any(buff => buff.Name == "BlindMonkRMarker"));
            TestAssert.Equal(free == 3, secondary.Buffs.Any(buff => buff.Name == "BlindMonkRDamage"));
            TestAssert.Equal(free != 0, secondary.Buffs.Any(buff => buff.Name == "BootsOfMobilityDebuff"),
                "the independent combat-hit Boots hook can use the slot left after the marker");
            TestAssert.Equal(free == 3, secondary.ForcedMove is not null);
        }

        // A real marker remains target-global even with no spare slot. The
        // per-kick set only substitutes for a marker that could not be added.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("LeeSin", team, extraEnemy: true);
            var lee = match.Hero(0);
            var primary = match.Hero(1);
            var secondary = match.Hero(2);
            lee.Position = new Position2(7032, 6560);
            primary.Position = new Position2(6999, 6757);
            secondary.Position = new Position2(6941, 7102);
            FillControlCapacity(secondary);
            var filler = secondary.Buffs.First(buff => buff.Name.StartsWith(
                "ControlCapacity", StringComparison.Ordinal));
            secondary.Buffs.Remove(filler);
            // The script adds the marker with the swept unit itself as the caster and tests for it with that caster (BBIfNotHasBuff Unit / Unit).
            secondary.Buffs.Add(new BuffState("BlindMonkRMarker", slot: filler.Slot,
                expiresAt: 2.0, casterNetId: secondary.NetId));
            match.Runtime.Delivery.Flush();
            match.Clear();
            match.Cast(0, 3, primary);
            Advance(match, .8);
            TestAssert.Equal(0, SecondaryKickDamagePackets(match, secondary.NetId),
                $"{team}: an existing target marker still excludes a swept hit at capacity");
        }
    }

    [Check("spells-live-lee-w-capacity-preserves-dash-and-recast")]
    public static void LeeSafeguardCapacityKeepsDashAndWindow()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using (var self = new Match("LeeSin", team))
            {
                var lee = self.Hero(0);
                FillControlCapacity(lee);
                var energy = lee.Mana;
                self.Cast(0, 1, lee);
                Advance(self, .3);
                TestAssert.True(lee.Mana < energy &&
                    !lee.Buffs.Any(buff => buff.Name == "BlindMonkPassive"),
                    "a full table omits Flurry without aborting the committed W cost");
                TestAssert.False(lee.Buffs.Any(buff => buff.Name == "BlindMonkWOneShield"));
                // The recast window is the script's BlindMonkWManager buff: a full table cannot hold it, so W stays Safeguard with its
                // ordinary cooldown (the hand-written window lived outside the buff table).
                TestAssert.False(lee.Buffs.Any(buff => buff.Name == "BlindMonkWManager"),
                    "a full table cannot open Iron Will's window");
                TestAssert.True(lee.SpellCooldowns[1] > 0 && !lee.Buffs.Any(buff => buff.Name == "BlindMonkWTwo"),
                    "the committed Safeguard keeps its cooldown and has no Iron Will to cast");

                FreeControlCapacity(lee);
                lee.Mana = lee.MaxMana;
                lee.SpellCooldowns[1] = 0;
                self.Cast(0, 1, lee);
                TestAssert.Equal(1, lee.Buffs.Count(buff => buff.Name == "BlindMonkPassive"));
                TestAssert.Equal(64, lee.Buffs.Count);
                Advance(self, .3);
                lee.Mana = lee.MaxMana;
                lee.SpellCooldowns[1] = 0;
                self.Cast(0, 1, lee);
                TestAssert.Equal(1, lee.Buffs.Count(buff => buff.Name == "BlindMonkPassive"),
                    "same-name Flurry renewal at capacity keeps its one buff");
                TestAssert.Equal(64, lee.Buffs.Count);
            }

            using (var ally = new Match("LeeSin", team, ally: true))
            {
                var lee = ally.Hero(0);
                var friend = ally.Hero(2);
                ally.Hero(1).Position = new Position2(7600, 7600);
                friend.Position = new Position2(7350, 7000);
                // 19 October 2026: the dash is the script's BlindMonkWOneDash buff, so it needs a slot on the caster: Flurry (added by the
                // character script's cast hook before the spell runs) takes the first of two free slots and the dash the second. The
                // buff is still on Lee when its BuffOnMoveSuccess adds the shields, so Lee's own shield and the recast manager find
                // the table full while the ally, with one free slot, gets its shield.
                FillControlCapacity(lee);
                FreeControlCapacity(lee);
                FreeControlCapacity(lee);
                FillControlCapacity(friend);
                FreeControlCapacity(friend);
                var start = lee.Position;
                ally.Cast(0, 1, friend);
                Advance(ally, .5);
                TestAssert.True(lee.Position != start,
                    "Safeguard's movement survives a nearly full caster buff table");
                TestAssert.False(lee.Buffs.Any(buff => buff.Name == "BlindMonkWOneShield"));
                TestAssert.True(lee.Buffs.Any(buff => buff.Name == "BlindMonkPassive"));
                TestAssert.True(friend.Buffs.Any(buff => buff.Name == "BlindMonkWOneShield"),
                    "the ally independently receives a shield when its slot is free");
            }
        }
    }

    [Check("spells-live-blitz-w-e-capacity-commits-cooldowns")]
    public static void BlitzDelayedSelfBuffCapacityKeepsCommittedCooldowns()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var slot in new[] { 1, 2 })
        {
            using (var denied = new Match("Blitzcrank", team))
            {
                var blitz = denied.Hero(0);
                FillControlCapacity(blitz);
                var mana = blitz.Mana;
                // 19 October 2026 (script path): the scripts never guard the 64-slot table and the hand-written refusal at validation was
                // non-retail; the cast is accepted and charged, and the engine's add guard declines the self buff.
                TestAssert.True(denied.TryCast(0, slot, blitz),
                    $"{team}/slot{slot}: a full table does not refuse an instant self buff");
                TestAssert.True(blitz.Mana < mana, "the accepted cast spends its mana");
                TestAssert.False(blitz.Buffs.Any(buff => buff.Name == (slot == 1 ? "Overdrive" : "PowerFist")));
            }

            using (var lateHook = new Match("Blitzcrank", team))
            {
                var blitz = lateHook.Hero(0);
                blitz.Inventory = [new InventorySlot(3057), null, null, null, null, null];
                // Batch 147: the Sheen arm is Items/3057.luaobj's own ItemOnSpellCast, so its copy has to reach the
                // inventory slot first (BbItemModule.SyncInventory, one spell update) before the cast below.
                lateHook.Runtime.Tick(0.01);
                FillControlCapacity(blitz);
                FreeControlCapacity(blitz);
                var mana = blitz.Mana;
                lateHook.Cast(0, slot, blitz);
                Advance(lateHook, .05);
                TestAssert.True(blitz.Buffs.Any(buff => buff.Name == "Sheen"));
                // Power Fist's SelfExecute resets the slot cooldown to 0 itself (the fist waits armed; the cooldown starts when it is spent),
                // so a fist the full table could not hold leaves a ready E; Overdrive keeps its ordinary cooldown.
                TestAssert.True(blitz.Mana < mana && (slot == 1 ? blitz.SpellCooldowns[slot] > 0 : blitz.SpellCooldowns[slot] == 0),
                    "an accepted cast keeps its mana; W its cooldown, E the zero its script sets");
                TestAssert.False(blitz.Buffs.Any(buff => buff.Name ==
                    (slot == 1 ? "Overdrive" : "PowerFist")));
            }
        }
    }

    private static int SecondaryKickDamagePackets(Match match, uint targetNetId) =>
        match.Captures[0].Packets.Count(packet => Opcode(packet) == 0x68 &&
            Decode(packet).GetProperty("target_netid").GetUInt32() == targetNetId);
}
