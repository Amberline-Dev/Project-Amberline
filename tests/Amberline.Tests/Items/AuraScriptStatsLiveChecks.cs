using Amberline.Core;
using Amberline.Simulation;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    /// <summary>
    /// The batch-145 aura group at the live loop: every number the eight aura items' own shipped scripts put on the
    /// heroes around them, measured on the live fields one period after the copy binds. Each aura re-adds its buffs
    /// from <c>BBExecutePeriodically</c> (0.9 s for 3001 and 3110, 1.0 s for the rest, ExecuteImmediately false), so
    /// the copy is one period old here rather than the first sync tick's. This is the coverage the deleted
    /// <c>ItemPassiveService.Auras</c> rows used to give: the rows' duration and payload are gone, the numbers now
    /// come from <c>DATA/Items/&lt;id&gt;.luaobj</c> and the <c>DATA/Spells</c> buffs it names. Each item is a match
    /// of its own so one item's aura cannot leak into the next. The numbers are compared at 1e-6: the host's shared
    /// number getter is float32 (BbNativeHandlers.Sum, the client's own number getter), so a delta read through a
    /// native accessor can land a few 1e-8 off the decimal the script writes (BBIncFlatPARRegenMod's 1.44 arrives as
    /// 1.44000006).
    /// </summary>
    [Check("items.live-aura-script-stats")]
    public static void AuraScriptsOwnTheirStats()
    {
        // The change one aura item made to three heroes' own live fields: the hero wearing it, an allied hero and a
        // hostile hero standing next to it (Match puts them at 7000/7200/7400, inside every aura range in the group).
        static (AuraViews views, double bareFoeAttackSpeed) Measure(int item)
        {
            using var match = new Match("Annie", Team.Order, ally: true, allyChampion: "Orianna");
            var runtime = match.Runtime;
            var source = match.Hero(0);
            var foe = match.Hero(1);
            var friend = match.Hero(2);
            // A hero's own champion script polls on the simulation tick as well, and only then does the cached field
            // move (Orianna's ball carries Command: Protect's +10 armour and magic resistance from the spawn), so one
            // tick settles the three baselines before anything is measured.
            runtime.Tick(1.15);
            source.Inventory = [new InventorySlot(item), null, null, null, null, null];
            runtime.HeroStats.Refresh(source);
            // The wearer's inibin rows are on already, so everything after this point is the script's own.
            var bareSelf = AuraFields.Of(source);
            var bareFoe = AuraFields.Of(foe);
            var bareFriend = AuraFields.Of(friend);
            runtime.Tick(1.15);
            return (new AuraViews(AuraFields.Of(source) - bareSelf,
                AuraFields.Of(friend) - bareFriend,
                AuraFields.Of(foe) - bareFoe), bareFoe.AttackSpeed);
        }

        // Abyssal Scepter's aura is hostile only: the self copy is a particle marker (AbyssalScepterAuraSelf holds no
        // BBIncStat), and the friend loop is an enemy loop.
        var abyssal = Measure(3001);
        TestAssert.Near(-20.0, abyssal.views.Foe.SpellBlock, 1e-6,
            "AbyssalScepterAura is -20 spell block on the enemy in range");
        TestAssert.Near(0.0, abyssal.views.Self.SpellBlock, 1e-6,
            "the Abyssal Scepter self copy is a marker only");
        TestAssert.Near(0.0, abyssal.views.Friend.SpellBlock, 1e-6,
            "Abyssal Scepter does not touch a friend's spell block");

        // Mana Manipulator's ManaRegenBonus is a PAR_MANA regen on the wearer and on every allied hero in range.
        var manipulator = Measure(3037);
        TestAssert.Near(1.44, manipulator.views.Self.ManaRegen, 1e-6,
            "ManaManipulatorAuraSelf is +1.44 PAR_MANA regen");
        TestAssert.Near(1.44, manipulator.views.Friend.ManaRegen, 1e-6,
            "ManaManipulatorAuraFriend is +1.44 PAR_MANA regen");
        TestAssert.Near(0.0, manipulator.views.Foe.ManaRegen, 1e-6,
            "Mana Manipulator does not touch an enemy");

        // Rallying Banner's hero arm shares one set of NextBuffVars between the self and the friend copy, and the
        // enemy arm takes its own ArmorMod into the RallyingBanner debuff.
        var banner = Measure(3050);
        TestAssert.Near(0.2, banner.views.Self.AttackSpeed, 1e-6,
            "RallyingBannerAuraSelf is +0.2 attack speed");
        TestAssert.Near(0.2, banner.views.Self.LifeSteal, 1e-6,
            "RallyingBannerAuraSelf is +0.2 life steal");
        TestAssert.Near(6.0, banner.views.Self.HealthRegen, 1e-6,
            "RallyingBannerAuraSelf is +6 health regen");
        TestAssert.Near(0.2, banner.views.Friend.AttackSpeed, 1e-6,
            "RallyingBannerAuraFriend shares the self copy's attack speed");
        TestAssert.Near(0.2, banner.views.Friend.LifeSteal, 1e-6,
            "RallyingBannerAuraFriend shares the self copy's life steal");
        TestAssert.Near(6.0, banner.views.Friend.HealthRegen, 1e-6,
            "RallyingBannerAuraFriend shares the self copy's health regen");
        TestAssert.Near(-20.0, banner.views.Foe.Armor, 1e-6,
            "RallyingBanner is -20 armor on the enemy in range");

        // Emblem of Valor is one buff for every friend in range, the wearer included.
        var emblem = Measure(3097);
        TestAssert.Near(2.0, emblem.views.Self.HealthRegen, 1e-6,
            "EmblemOfValor is +2 health regen on the wearer");
        TestAssert.Near(2.0, emblem.views.Friend.HealthRegen, 1e-6,
            "EmblemOfValor is +2 health regen on the friend");

        // Soul Shroud's CooldownReduction and ManaRegenMod reach the self and the friend copy alike.
        var shroud = Measure(3099);
        TestAssert.Near(2.4, shroud.views.Self.ManaRegen, 1e-6,
            "SoulShroudAuraSelf is +2.4 PAR_MANA regen");
        TestAssert.Near(-0.1, shroud.views.Self.CooldownMod, 1e-6,
            "SoulShroudAuraSelf is -0.10 cooldown");
        TestAssert.Near(2.4, shroud.views.Friend.ManaRegen, 1e-6,
            "SoulShroudAuraFriend is +2.4 PAR_MANA regen");
        TestAssert.Near(-0.1, shroud.views.Friend.CooldownMod, 1e-6,
            "SoulShroudAuraFriend is -0.10 cooldown");

        // Aegis of the Legion's three NextBuffVars are one flat bundle for the wearer and the friend.
        var aegis = Measure(3105);
        TestAssert.Near(15.0, aegis.views.Self.SpellBlock, 1e-6,
            "AegisoftheLegionAuraSelf is +15 spell block");
        TestAssert.Near(12.0, aegis.views.Self.Armor, 1e-6,
            "AegisoftheLegionAuraSelf is +12 armor");
        TestAssert.Near(8.0, aegis.views.Self.AttackDamage, 1e-6,
            "AegisoftheLegionAuraSelf is +8 attack damage");
        TestAssert.Near(15.0, aegis.views.Friend.SpellBlock, 1e-6,
            "AegisoftheLegionAuraFriend is +15 spell block");
        TestAssert.Near(12.0, aegis.views.Friend.Armor, 1e-6,
            "AegisoftheLegionAuraFriend is +12 armor");
        TestAssert.Near(8.0, aegis.views.Friend.AttackDamage, 1e-6,
            "AegisoftheLegionAuraFriend is +8 attack damage");

        // Frozen Heart splits in two: the FrozenHeart self buff is the -0.20 cooldown the old inventory row carried,
        // and FrozenHeartAura is the hostile multiplicative attack-speed slow.
        var frozen = Measure(3110);
        TestAssert.Near(-0.2, frozen.views.Self.CooldownMod, 1e-6,
            "the FrozenHeart self buff is -0.20 cooldown on the wearer");
        TestAssert.Near(0.0, frozen.views.Self.AttackSpeed, 1e-6,
            "the Frozen Heart wearer is not slowed by his own aura");
        TestAssert.Near(frozen.bareFoeAttackSpeed * 0.8,
            frozen.bareFoeAttackSpeed + frozen.views.Foe.AttackSpeed, 1e-6,
            "FrozenHeartAura is a 0.8 multiplier on the enemy's attack speed");
        TestAssert.Near(0.0, frozen.views.Foe.CooldownMod, 1e-6,
            "FrozenHeartAura carries no cooldown onto the enemy");

        // Will of the Ancients shares one AP and spell-vamp bundle with the wearer and every friend in range.
        var ancients = Measure(3152);
        TestAssert.Near(30.0, ancients.views.Self.AbilityPower, 1e-6,
            "WillOfTheAncientsSelf is +30 ability power");
        TestAssert.Near(0.25, ancients.views.Self.SpellVamp, 1e-6,
            "WillOfTheAncientsSelf is +0.25 spell vamp");
        TestAssert.Near(30.0, ancients.views.Friend.AbilityPower, 1e-6,
            "WillOfTheAncientsFriendly is +30 ability power");
        TestAssert.Near(0.25, ancients.views.Friend.SpellVamp, 1e-6,
            "WillOfTheAncientsFriendly is +0.25 spell vamp");
    }

    /// <summary>The live fields one aura in the group writes, as the change the item made to them.</summary>
    private sealed record AuraFields(double Armor, double SpellBlock, double AttackDamage, double AbilityPower,
        double HealthRegen, double ManaRegen, double CooldownMod, double LifeSteal, double SpellVamp,
        double AttackSpeed)
    {
        public static AuraFields Of(Entity hero) => new(hero.Armor, hero.SpellBlock, hero.AttackDamage,
            hero.AbilityPower, hero.HealthRegen, hero.ManaRegen, hero.CooldownMod, hero.LifeSteal,
            hero.SpellVamp, hero.AttackSpeedMod);

        public static AuraFields operator -(AuraFields after, AuraFields before) => new(
            after.Armor - before.Armor, after.SpellBlock - before.SpellBlock,
            after.AttackDamage - before.AttackDamage, after.AbilityPower - before.AbilityPower,
            after.HealthRegen - before.HealthRegen, after.ManaRegen - before.ManaRegen,
            after.CooldownMod - before.CooldownMod, after.LifeSteal - before.LifeSteal,
            after.SpellVamp - before.SpellVamp, after.AttackSpeed - before.AttackSpeed);
    }

    private sealed record AuraViews(AuraFields Self, AuraFields Friend, AuraFields Foe);
}
