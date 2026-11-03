using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Simulation;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-live-nidalee-capacity-form-and-takedown")]
    public static void NidaleeRequiredFormAndTakedownSlotsRejectBeforeCost()
    {
        // 19 October 2026: Nidalee runs from her scripts. A cast is no longer refused for a buff that cannot fit (the hand-written validation did): the
        // script's cast runs, its buff is declined by the engine's one guard (BbBuffRuntime.Add) on a full table, and what depends on the buff
        // (the pushed form, the armed attack) never happens. R costs no mana; the cooldown the script writes at the cast is paid.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Nidalee", team);
            var hero = match.Hero(0);
            var humanBook = hero.ActiveSpellNames.ToArray();
            FillRemainingCapacity(hero);
            var mana = hero.Mana;
            TestAssert.True(match.TryCast(0, 3, hero), "the script's cast runs on a full table");
            TestAssert.Near(mana, hero.Mana, 1e-9);
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "AspectOfTheCougar"), "the form's buff is declined");
            TestAssert.True(hero.ScriptCharacterData is null, "and no cougar layer is pushed without it");
            TestAssert.True(hero.ActiveSpellNames.SequenceEqual(humanBook),
                "the declined R leaves the original human spellbook untouched");
            TestAssert.Equal("JavelinToss", hero.ActiveSpellNames.Count > 0
                ? hero.ActiveSpellNames[0] : match.Runtime.GameData.Champion("Nidalee").SpellNames[0]);

            FreeRemainingCapacity(hero);
            hero.SpellCooldowns[3] = 0;
            match.Cast(0, 3, hero);
            Advance(match, .5);
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "AspectOfTheCougar"));
            TestAssert.Equal("Takedown", hero.ActiveSpellNames[0]);
            FillRemainingCapacity(hero);
            hero.SpellCooldowns[0] = 0;
            match.TryCast(0, 0, hero);
            Advance(match, .2);
            TestAssert.False(hero.Buffs.Any(buff => buff.Name == "Takedown"), "a full cougar table declines the Takedown buff");
            TestAssert.True(hero.ScriptAttackOverride is null && !hero.SpellSeals.IsSealed(false, 0),
                "and nothing of the empowered attack is half armed (no override, the Q slot not sealed)");
            FreeRemainingCapacity(hero);
            hero.SpellCooldowns[0] = 0;
            match.Cast(0, 0, hero);
            Advance(match, .2);
            TestAssert.True(hero.Buffs.Any(buff => buff.Name == "Takedown"),
                "one freed owner slot still arms the ordinary empowered attack");

            using var lateHook = new Match("Nidalee", team);
            var caster = lateHook.Hero(0);
            var lateHumanBook = caster.ActiveSpellNames.ToArray();
            caster.Inventory = [new InventorySlot(3057), null, null, null, null, null];
            // Batch 147: the Sheen arm is Items/3057.luaobj's own ItemOnSpellCast, so its copy has to reach the
            // inventory slot first (BbItemModule.SyncInventory, one spell update) before the cast below.
            lateHook.Runtime.Tick(0.01);
            FillRemainingCapacity(caster);
            FreeRemainingCapacity(caster);
            lateHook.Cast(0, 3, caster);
            Advance(lateHook, .5);
            TestAssert.True(caster.Buffs.Any(buff => buff.Name == "Sheen"));
            TestAssert.False(caster.Buffs.Any(buff => buff.Name == "AspectOfTheCougar"));
            TestAssert.True(caster.ActiveSpellNames.SequenceEqual(lateHumanBook),
                "late R fallback leaves the original human spellbook untouched");
            TestAssert.Equal("JavelinToss", caster.ActiveSpellNames.Count > 0
                ? caster.ActiveSpellNames[0] : lateHook.Runtime.GameData.Champion("Nidalee").SpellNames[0]);
            TestAssert.True(caster.SpellCooldowns[3] > 0,
                "a post-validation item proc cannot leave a half-transformed free R");
        }
    }

    [Check("spells-live-nidalee-capacity-heal-and-triggered-trap")]
    public static void NidaleeHealAndTrapRetirementSurviveFullRecipient()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using (var surge = new Match("Nidalee", team, ally: true))
            {
                var ally = surge.Hero(2);
                ally.Health -= 300;
                FillRemainingCapacity(ally);
                var before = ally.Health;
                surge.Cast(0, 2, ally);
                Advance(surge, .5);
                TestAssert.True(ally.Health - before >= 49.0,
                    "Primal Surge heals even when its optional attack-speed row cannot fit");
                TestAssert.False(ally.Buffs.Any(buff => buff.Name == "PrimalSurge"));
                FreeRemainingCapacity(ally);
                surge.Hero(0).SpellCooldowns[2] = 0;
                surge.Cast(0, 2, ally);
                Advance(surge, .5);
                TestAssert.True(ally.Buffs.Any(buff => buff.Name == "PrimalSurge"),
                    "the ordinary buff and heal resume when one slot opens");
            }

            using (var trap = new Match("Nidalee", team))
            {
                var owner = trap.Hero(0);
                var enemy = trap.Hero(1);
                enemy.Position = owner.Position + new Position2(200, 0);
                trap.Runtime.Delivery.Flush();
                FillRemainingCapacity(enemy);
                trap.CastGround(0, enemy.Position, slot: 1);
                Advance(trap, 2.0);
                TestAssert.False(enemy.Buffs.Any(buff => buff.Name == "BushwhackDebuff"));
                // The trap's end is the script's own 4000-damage hit (Bushwhack.OnBuffDeactivate): the generic death, so the dead trap stays in the world
                // as a corpse for eight seconds (batch 108, row 18) and the check reads that it is not alive.
                TestAssert.False(trap.Runtime.World.Entities.Values.Any(unit =>
                    unit.Kind == EntityKind.NidaleeTrap && unit.OwnerNetId == owner.NetId && unit.Alive),
                    "a triggered full-table trap retires instead of retrying every tick");
            }
        }
    }

    // Ezreal's R used to be refused when its CantAttack lock could not fit and acknowledged without a missile when an item took the slot after
    // validation (spells-live-ezreal-r-capacity-lock-and-late-item-hook); phase 3 (19 October 2026) runs him from his scripts, whose buffs are declined on
    // a full table by the engine's one guard (BbBuffRuntime.Add) and whose R is never refused for it. The check is gone with its subject.

    // 19 October 2026: "spells-live-orianna-capacity-wave-and-e-arrival" pinned the hand-written wave and arrival shield at a full buff table; the
    // scripts add OrianaSlow, OrianaHaste and OrianaRedactShield with BBSpellBuffAdd, whose capacity rule is the engine's (BbBuffRuntime.Add).

    private static void FillRemainingCapacity(Entity unit)
    {
        for (var slot = 0; slot < SpellPackets.MaxBuffSlots; slot++)
        {
            if (unit.Buffs.Count == SpellPackets.MaxBuffSlots) break;
            if (unit.Buffs.Any(buff => buff.Slot == slot)) continue;
            unit.Buffs.Add(new BuffState($"RemainingCasterCapacity{slot}", slot: slot, expiresAt: 60));
        }
        TestAssert.Equal(SpellPackets.MaxBuffSlots, unit.Buffs.Count);
    }

    private static void FreeRemainingCapacity(Entity unit) =>
        unit.Buffs.Remove(unit.Buffs.First(buff =>
            buff.Name.StartsWith("RemainingCasterCapacity", StringComparison.Ordinal)));
}
