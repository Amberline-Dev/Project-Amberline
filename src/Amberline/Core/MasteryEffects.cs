using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Core;

/// <summary>
/// Typed combat hooks emitted by patch-126 mastery scripts. Values describe
/// the script output; event consumers still decide when to apply a hook.
/// </summary>
public sealed record MasteryCombatEffects(
    double ArdorAbilityPowerPercent = 0.0,
    double ArdorAttackSpeedPercent = 0.0,
    double ArdorDurationSeconds = 0.0,
    double StrengthOfSpiritHealthRegenPerMaxMana = 0.0,
    double StrengthOfSpiritDurationSeconds = 0.0,
    double HardenSkinPhysicalDamageBlock = 0.0,
    double NonTrueDamageDealtMultiplier = 1.0,
    double NonTrueDamageTakenMultiplier = 1.0,
    double NonHeroAiDamageReduction = 0.0,
    bool NimblenessOnDodge = false,
    double NimblenessDurationSeconds = 0.0,
    double NimblenessMovementSpeedPercent = 0.0,
    double NeutralBuffDurationPercent = 0.0,
    bool MonsterBuffs = false,
    bool MonsterBuffs2 = false,
    int OffensiveMasteryRank = 0,
    double OffensiveMasteryMinionDamageBonus = 0.0);

/// <summary>Typed summoner-spell and avatar variables from mastery scripts.</summary>
public sealed record MasterySummonerEffects(
    double SummonerCooldownBonus = 0.0,
    double ClairvoyanceDurationBonus = 0.0,
    double ClairvoyanceCooldownBonus = 0.0,
    double TeleportDelayBonus = 0.0,
    double TeleportCooldownBonus = 0.0,
    double PromoteCooldownBonus = 0.0,
    double SmiteGoldBonus = 0.0,
    double SmiteCooldownBonus = 0.0,
    double RevivePreservationBonus = 0.0,
    double ReviveCooldownBonus = 0.0,
    double GhostMovementBonus = 0.0,
    double GhostDurationBonus = 0.0,
    double BoostCooldownBonus = 0.0,
    double HealCooldownBonus = 0.0,
    double ExhaustArmorMod = 0.0,
    double ExhaustDurationBonus = 0.0,
    double FlashCooldownBonus = 0.0,
    double RallyAPMod = 0.0,
    double RallyDurationBonus = 0.0,
    double FortifySplashDamage = 0.0,
    double ClarityManaBonus = 0.0,
    bool BurningEmbersWhenIgniteOnCooldown = false,
    double BurningEmbersAbilityPowerBonus = 0.0);

/// <summary>Typed progression, economy and death-timer mastery effects.</summary>
public sealed record MasteryEconomyEffects(
    double ExperiencePercentBonus = 0.0,
    double GoldPer10Bonus = 0.0,
    double RespawnTimePercent = 0.0);

/// <summary>
/// Pure contribution from one already validated mastery page. The modifier
/// keys intentionally match RunePage and ItemData keys so the later stat
/// wiring can aggregate them without changing either owner.
/// </summary>
public sealed record MasteryContribution(
    IReadOnlyDictionary<string, double> Modifiers,
    MasteryCombatEffects Combat,
    MasterySummonerEffects Summoner,
    MasteryEconomyEffects Economy)
{
    public static MasteryContribution Empty { get; } = new(
        new ReadOnlyDictionary<string, double>(
            new Dictionary<string, double>(StringComparer.Ordinal)),
        new MasteryCombatEffects(),
        new MasterySummonerEffects(),
        new MasteryEconomyEffects());

    public bool IsEmpty => Modifiers.Count == 0 &&
                           Combat == new MasteryCombatEffects() &&
                           Summoner == new MasterySummonerEffects() &&
                           Economy == new MasteryEconomyEffects();
}

/// <summary>
/// The mastery page's contribution, read from the shipped <c>DATA/Talents/&lt;id&gt;.luaobj</c> scripts rather than
/// from a table of coefficients. Each allocated talent runs through <see cref="BbLoadoutReport"/> at its rank (batch
/// 140, step 1a: the same conversion the champions had in batches 86-112), and what its blocks wrote is landed on
/// this record: <c>UpdateSelfBuffStats</c>'s <c>BBIncStat</c> onto the modifiers and the economy fields, its
/// <c>BBInc*PAR*</c> onto the mana modifiers, its <c>BBSpellBuffAdd</c> onto the combat fields through the buff's own
/// script, and the character hooks onto the damage fields through one synthetic hit.
/// </summary>
public static class MasteryEffects
{
    // The three economy accessors are the only IncStat names that land on a typed field instead of a modifier key.
    private static readonly Dictionary<string, string> EconomyLanding = new(StringComparer.Ordinal)
    {
        ["IncPercentEXPBonus"] = nameof(MasteryEconomyEffects.ExperiencePercentBonus),
        ["IncFlatGoldPer10Mod"] = nameof(MasteryEconomyEffects.GoldPer10Bonus),
        ["IncPercentRespawnTimeMod"] = nameof(MasteryEconomyEffects.RespawnTimePercent),
    };

    // The PAR-typed natives are the only non-BBIncStat names that land on a modifier key.
    private static readonly Dictionary<string, string> ResourceLanding = new(StringComparer.Ordinal)
    {
        ["BBIncPercentPARPoolMod"] = "PercentMPPoolMod",
        ["BBIncFlatPARPoolMod"] = "FlatMPPoolMod",
        ["BBIncFlatPARRegenMod"] = "FlatMPRegenMod",
        ["BBIncPercentPARRegenMod"] = "PercentMPRegenMod",
    };

    private static readonly ConcurrentDictionary<string, MasteryContribution> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// The utility mastery's neutral-buff markers (Talents/135.luaobj adds one at its rank). Both shipped scripts are empty
    /// chunks: the factor belongs to the neutral death scripts that read the marker from the killer (see <c>Calculate</c>), so
    /// the loadout installs the marker as a live buff on its hero (<c>BbLoadoutHooks</c>).
    /// </summary>
    public static readonly IReadOnlySet<string> NeutralBuffMarkers =
        new HashSet<string>(StringComparer.Ordinal) { "MonsterBuffs", "MonsterBuffs2" };

    public static MasteryContribution Calculate(MasteryPage page, int championLevel = 1)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (championLevel is < 1 or > 18)
            throw new ArgumentOutOfRangeException(nameof(championLevel), "Champion level must be 1..18.");

        // Empty pages are the neutral default for synthetic/non-player heroes.
        // Keep the level validation above so malformed callers still fail, then
        // avoid walking all 43 mastery definitions and allocating a dictionary.
        if (page.TotalPoints == 0)
            return MasteryContribution.Empty;

        // The report depends only on the allocation and the champion level, and HeroStats asks for it on every stat
        // refresh; the record is immutable, so the same page and level can share one answer.
        return Cache.GetOrAdd(Key(page, championLevel), _ => FromScripts(page, championLevel));
    }

    private static string Key(MasteryPage page, int championLevel) =>
        string.Join(",", page.Allocations.Select(pair =>
            pair.Key.ToString(CultureInfo.InvariantCulture) + ":" + pair.Value.ToString(CultureInfo.InvariantCulture))) +
        "@" + championLevel.ToString(CultureInfo.InvariantCulture);

    private static MasteryContribution FromScripts(MasteryPage page, int championLevel)
    {
        var talents = BbLoadoutReport.Talents(page.Allocations, championLevel);
        var modifiers = new Dictionary<string, double>(StringComparer.Ordinal);
        var economy = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var talent in talents.Values)
        {
            foreach (var (accessor, delta) in talent.Stats)
            {
                if (delta == 0.0) continue;
                if (EconomyLanding.TryGetValue(accessor, out var field)) Add(economy, field, delta);
                // Inc<Name> is the modifier key <Name>, the same names ItemData and RunePage use.
                else Add(modifiers, accessor.StartsWith("Inc", StringComparison.Ordinal) ? accessor[3..] : accessor, delta);
            }
            foreach (var (native, delta) in talent.ResourceMods)
            {
                if (delta == 0.0) continue;
                if (ResourceLanding.TryGetValue(native, out var key)) Add(modifiers, key, delta);
            }
        }

        return new MasteryContribution(
            new ReadOnlyDictionary<string, double>(modifiers),
            Combat(talents),
            SummonerEffects(talents),
            new MasteryEconomyEffects(
                ExperiencePercentBonus: Take(economy, nameof(MasteryEconomyEffects.ExperiencePercentBonus)),
                GoldPer10Bonus: Take(economy, nameof(MasteryEconomyEffects.GoldPer10Bonus)),
                RespawnTimePercent: Take(economy, nameof(MasteryEconomyEffects.RespawnTimePercent))));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Combat fields — each is one shipped talent's own hook or its marker buff's script
    // ------------------------------------------------------------------------------------------------------------

    private static MasteryCombatEffects Combat(IReadOnlyDictionary<int, BbTalentReport> talents)
    {
        // Ardor (111) and Strength of Spirit (112) ask for a marker buff whose script holds the number; the report
        // runs that buff with the holder's stats reading 1, so the captured add is the factor it applies.
        var ardor = Buff(talents, 111);
        var spirit = Buff(talents, 112);
        var nimbleness = DodgeBuff(talents, 127);

        // Utility (135) adds either MonsterBuffs or MonsterBuffs2; both shipped scripts are empty chunks, so the
        // only thing the script says is which one it added. The neutral death scripts that read the marker carry the factor (see below).
        var markers = talents.TryGetValue(135, out var utility)
            ? utility.Buffs.Select(buff => buff.Name).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        // Offensive Mastery (137) passes its rank to the buff, whose BuffOnHitUnit adds {2, 4}[Level] to a landed
        // basic attack against an AI non-hero.
        var offensive = Buff(talents, 137);

        return new MasteryCombatEffects(
            ArdorAbilityPowerPercent: Stat(ardor, "IncFlatMagicDamageMod"),
            ArdorAttackSpeedPercent: Stat(ardor, "IncPercentAttackSpeedMod"),
            ArdorDurationSeconds: Duration(ardor),
            StrengthOfSpiritHealthRegenPerMaxMana: Stat(spirit, "IncFlatHPRegenMod"),
            StrengthOfSpiritDurationSeconds: Duration(spirit),
            HardenSkinPhysicalDamageBlock: Block(talents, 113, "CharOnBeingHit", BbDamageProbe.Physical),
            NonTrueDamageDealtMultiplier: Multiplier(talents, 123, "CharOnPreDealDamage", BbDamageProbe.Physical),
            NonTrueDamageTakenMultiplier: Multiplier(talents, 125, "CharOnPreDamage", BbDamageProbe.Physical),
            NonHeroAiDamageReduction: Block(talents, 144, "CharOnPreDamage", BbDamageProbe.AiNonHero),
            NimblenessOnDodge: nimbleness is not null,
            NimblenessDurationSeconds: Duration(nimbleness),
            NimblenessMovementSpeedPercent: Stat(nimbleness, "IncPercentMovementSpeedMod"),
            // The 1.15 / 1.30 neutral-buff duration factors are the shipped death scripts' own: Spells/CrestoftheAncientGolem.luaobj's
            // BuffOnDeath multiplies its NewDuration by 1.15 when the killer holds MonsterBuffs and by 1.3 for MonsterBuffs2
            // (0266-0315 and 0424-0433), and Spells/BlessingoftheLizardElder.luaobj, CrestOfNaturesFury, CrestOfFlowingWater and
            // dragonApplicator read the same marker. Batch 196: BbLoadoutHooks installs the marker live on its hero at creation
            // (NeutralBuffMarkers), so those scripts apply the factor. The Baron's buff reads no marker. The tooltip's percent is
            // kept here as the loadout report's own reading of rank 135.
            NeutralBuffDurationPercent: new[] { 0.0, 0.15, 0.30 }[Rank(talents, 135)],
            MonsterBuffs: markers.Contains("MonsterBuffs"),
            MonsterBuffs2: markers.Contains("MonsterBuffs2"),
            OffensiveMasteryRank: offensive is null ? 0 : (int)offensive.Number("Level"),
            OffensiveMasteryMinionDamageBonus: offensive is null ? 0.0 : BbLoadoutReport.BuffOnHitBonus(offensive));
    }

    /// <summary>
    /// The single marker buff the talent's self-buff hooks asked for (111 Ardor, 112, 137, 146 BurningEmbers). The
    /// probe world recorded the add when the script's own gate opened; when it never did (146 only adds BurningEmbers
    /// while a live <c>SummonerDot</c> slot is on cooldown, and the probe has no summoner spellbook), the buff comes
    /// from the <c>BBSpellBuffAdd</c> block the script carries, so the name and the numbers stay the shipped ones.
    /// </summary>
    private static BbLoadoutBuffAdd? Buff(IReadOnlyDictionary<int, BbTalentReport> talents, int id)
    {
        if (!talents.TryGetValue(id, out var talent)) return null;
        if (talent.Buffs.LastOrDefault() is { } captured) return captured;
        foreach (var (hook, block) in talent.Binding.Script.Walk())
        {
            if (hook is not ("UpdateSelfBuffActions" or "UpdateSelfBuffStats")) continue;
            if (!string.Equals(block.Function, "BBSpellBuffAdd", StringComparison.Ordinal)) continue;
            if (block.Text("BuffName") is not { Length: > 0 } name) continue;
            return new BbLoadoutBuffAdd(name, block.Number("Duration") ?? 0.0, new BbTable());
        }
        return null;
    }

    /// <summary>The buff 127's <c>CharOnDodge</c> asks for (Nimbleness); the hook runs on its own probe.</summary>
    private static BbLoadoutBuffAdd? DodgeBuff(IReadOnlyDictionary<int, BbTalentReport> talents, int id) =>
        talents.TryGetValue(id, out var talent) ? talent.HookBuffs("CharOnDodge").LastOrDefault() : null;

    /// <summary>What the buff's own script adds to a stat, with the holder's stats reading 1 (a pure factor).</summary>
    private static double Stat(BbLoadoutBuffAdd? add, string accessor)
    {
        if (add is null) return 0.0;
        var stats = BbLoadoutReport.BuffStats(add);
        return stats.TryGetValue(accessor, out var value) ? value : 0.0;
    }

    private static double Duration(BbLoadoutBuffAdd? add) => add?.Duration ?? 0.0;

    private static int Rank(IReadOnlyDictionary<int, BbTalentReport> talents, int id) =>
        talents.TryGetValue(id, out var talent) ? talent.Rank : 0;

    /// <summary>The factor a damage hook leaves on a hit (Havoc 123, Tenacity 125).</summary>
    private static double Multiplier(IReadOnlyDictionary<int, BbTalentReport> talents, int id, string hook,
        BbDamageProbe probe) =>
        talents.TryGetValue(id, out var talent) ? talent.Damage(hook, probe).Multiplier : 1.0;

    /// <summary>The flat block a damage hook takes off a hit (Harden Skin 113, Defensive 144).</summary>
    private static double Block(IReadOnlyDictionary<int, BbTalentReport> talents, int id, string hook,
        BbDamageProbe probe) =>
        talents.TryGetValue(id, out var talent) ? talent.Damage(hook, probe).Block : 0.0;

    // ------------------------------------------------------------------------------------------------------------
    // Summoner fields — step 1c: the AvatarVars the feeder scripts write in SetVarsByLevel
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The summoner fields are the <c>AvatarVars</c> the feeder scripts write in their <c>SetVarsByLevel</c>, read
    /// back per talent (batch 142 step 1c). 120 writes <c>SummonerCooldownBonus</c>, 122 the two Clairvoyance
    /// bonuses, 124 the Teleport and Promote ones, 126 Smite's two, 129 Revive's two, 130 Ghost's two, 131 Boost's,
    /// 133 Heal's, 136 Exhaust's two, 140 Flash's, 143 Rally's two, 145 <c>FortifySplashDamage</c> and 147
    /// <c>ManaBonus</c>. Each probe owns its own loadout, so a key is read from the one feeder that writes it and an
    /// unallocated feeder's unwritten (nil) key reads 0 here.
    /// </summary>
    /// <remarks>
    /// Two record names now match the shipped keys exactly: <c>RevivePreservationBonus</c> (129, was
    /// <c>ReviveHealthBonus</c>) and <c>RallyAPMod</c> (143, was <c>RallyAbilityPowerBonus</c>). The one key that
    /// keeps its own name is 147's <c>ManaBonus</c>, which this record calls <c>ClarityManaBonus</c>; the summoner
    /// script reads the shipped key directly, so only this typed view renames it.
    /// </remarks>
    private static MasterySummonerEffects SummonerEffects(IReadOnlyDictionary<int, BbTalentReport> talents)
    {
        double Avatar(int id, string key) => talents.TryGetValue(id, out var talent) ? talent.Avatar(key) : 0.0;

        // 146 is the one feeder that is not an AvatarVars write: its UpdateSelfBuffActions adds the BurningEmbers
        // marker buff, whose own script holds the number (BuffOnUpdateStats BBIncStat IncFlatMagicDamageMod 10).
        var embers = Buff(talents, 146);

        return new MasterySummonerEffects(
            SummonerCooldownBonus: Avatar(120, "SummonerCooldownBonus"),
            ClairvoyanceDurationBonus: Avatar(122, "ClairvoyanceDurationBonus"),
            ClairvoyanceCooldownBonus: Avatar(122, "ClairvoyanceCooldownBonus"),
            TeleportDelayBonus: Avatar(124, "TeleportDelayBonus"),
            TeleportCooldownBonus: Avatar(124, "TeleportCooldownBonus"),
            PromoteCooldownBonus: Avatar(124, "PromoteCooldownBonus"),
            SmiteGoldBonus: Avatar(126, "SmiteGoldBonus"),
            SmiteCooldownBonus: Avatar(126, "SmiteCooldownBonus"),
            RevivePreservationBonus: Avatar(129, "RevivePreservationBonus"),
            ReviveCooldownBonus: Avatar(129, "ReviveCooldownBonus"),
            GhostMovementBonus: Avatar(130, "GhostMovementBonus"),
            GhostDurationBonus: Avatar(130, "GhostDurationBonus"),
            BoostCooldownBonus: Avatar(131, "BoostCooldownBonus"),
            HealCooldownBonus: Avatar(133, "HealCooldownBonus"),
            ExhaustArmorMod: Avatar(136, "ExhaustArmorMod"),
            ExhaustDurationBonus: Avatar(136, "ExhaustDurationBonus"),
            FlashCooldownBonus: Avatar(140, "FlashCooldownBonus"),
            RallyAPMod: Avatar(143, "RallyAPMod"),
            RallyDurationBonus: Avatar(143, "RallyDurationBonus"),
            FortifySplashDamage: Avatar(145, "FortifySplashDamage"),
            ClarityManaBonus: Avatar(147, "ManaBonus"),
            BurningEmbersWhenIgniteOnCooldown: embers is not null,
            BurningEmbersAbilityPowerBonus: Stat(embers, "IncFlatMagicDamageMod"));
    }

    private static void Add(IDictionary<string, double> modifiers, string name, double value)
    {
        if (value == 0.0) return;
        modifiers[name] = modifiers.TryGetValue(name, out var previous)
            ? previous + value
            : value;
    }

    private static double Take(IDictionary<string, double> values, string name) =>
        values.TryGetValue(name, out var value) ? value : 0.0;
}
