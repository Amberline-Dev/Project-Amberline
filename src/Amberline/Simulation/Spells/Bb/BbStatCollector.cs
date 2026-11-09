using Amberline.Core;
using Amberline.Network.Replication;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>Stat deltas a <c>BuffOnUpdateStats</c> hook asked for, collected instead of applied (see <see cref="BbBuffRuntime"/>).</summary>
internal sealed class BbStatCollector
{
    private readonly Dictionary<BbIncStat, double> _totals = new();
    private readonly Dictionary<BbSetStatus, bool> _statuses = new();
    public IReadOnlyDictionary<BbIncStat, double> Totals => _totals;
    private readonly Dictionary<Entity, Dictionary<BbIncStat, double>> _foreign = new(ReferenceEqualityComparer.Instance);
    /// <summary>The stat deltas the hook asked for on units other than its own (YorickSummonSpectral gives the ghoul its attack damage and speed).</summary>
    public IReadOnlyDictionary<Entity, Dictionary<BbIncStat, double>> Foreign => _foreign;
    private readonly Dictionary<Entity, Dictionary<BbSetStatus, bool>> _foreignStatuses = new(ReferenceEqualityComparer.Instance);
    /// <summary>The statuses the hook set on units other than its own (SkarnerImpale: Skarner's CanAttack while his victim is held, batch 318).</summary>
    public IReadOnlyDictionary<Entity, Dictionary<BbSetStatus, bool>> ForeignStatuses => _foreignStatuses;
    public void SetForeign(Entity unit, BbSetStatus status, bool value)
    {
        if (!_foreignStatuses.TryGetValue(unit, out var statuses)) _foreignStatuses[unit] = statuses = new Dictionary<BbSetStatus, bool>();
        statuses[status] = value;
    }
    public void AddForeign(Entity unit, BbIncStat stat, double delta)
    {
        if (!_foreign.TryGetValue(unit, out var totals)) _foreign[unit] = totals = new Dictionary<BbIncStat, double>();
        totals[stat] = (totals.TryGetValue(stat, out var current) ? current : 0.0) + delta;
    }
    /// <summary>The unit statuses the hook set (<c>BBSetStatus</c>), the last value written per status.</summary>
    public IReadOnlyDictionary<BbSetStatus, bool> Statuses => _statuses;
    public void Add(BbIncStat stat, double delta) =>
        _totals[stat] = (_totals.TryGetValue(stat, out var current) ? current : 0.0) + delta;
    public void Set(BbSetStatus status, bool value) => _statuses[status] = value;
    /// <summary><c>BBIncMaxHealth</c> on a hero: the total delta, and the part of it that does not heal (IncCurrentHealth false).</summary>
    public double MaxHealth { get; private set; }
    public double NonHealingMaxHealth { get; private set; }
    /// <summary><c>BBIncScaleSkinCoef</c> of the holder's own stats hook: the sum of the model scale increments.</summary>
    public double ScaleSkinCoef { get; private set; }
    public void AddScaleSkinCoef(double delta) => ScaleSkinCoef += delta;
    /// <summary><c>BBIncFlatPARRegenMod</c> and <c>BBIncPercentPARRegenMod</c> of the holder's own pool.</summary>
    public double ParRegenFlat { get; private set; }
    public double ParRegenPercent { get; private set; }
    /// <summary>
    /// <c>BBIncFlatPARPoolMod</c> and <c>BBIncPercentPARPoolMod</c> of the holder's own pool (Rod of Ages' ageing, Tear of the Goddess'
    /// growth): the flat half joins the buff payload's <c>MaxMp</c> like every other script pool row.
    /// </summary>
    public double ParPoolFlat { get; private set; }
    public double ParPoolPercent { get; private set; }
    /// <summary>
    /// <c>BBSetPARColorOverride</c> / <c>BBClearPARColorOverride</c> of the holder's own stats hook, in call order (null: a clear). The sample
    /// applies them one by one once the hook ended, each with its packet, as the retail body sends one per call (Renekton's Predator sets a
    /// colour and clears it in one pass: two packets). It is not part of the signature: the colours are the unit's state, not a stat.
    /// </summary>
    public List<ParColorOverride?> ParColorCalls { get; } = new();
    public void SetParColor(ParColorOverride? value) => ParColorCalls.Add(value);
    public void AddParRegen(double delta, bool percent)
    {
        if (percent) ParRegenPercent += delta;
        else ParRegenFlat += delta;
    }
    public void AddParPool(double delta, bool percent)
    {
        if (percent) ParPoolPercent += delta;
        else ParPoolFlat += delta;
    }
    public void AddMaxHealth(double delta, bool healing)
    {
        MaxHealth += delta;
        if (!healing) NonHealingMaxHealth += delta;
    }

    /// <summary>
    /// Takes another sample's deltas on (several inventory copies polled into one summary). Stat totals add, a status
    /// keeps the last write the way a single hook's would, and the health, resource and foreign sums add up.
    /// </summary>
    public void Merge(BbStatCollector other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var (stat, value) in other._totals) Add(stat, value);
        foreach (var (status, value) in other._statuses) _statuses[status] = value;
        foreach (var (unit, totals) in other._foreign)
            foreach (var (stat, value) in totals) AddForeign(unit, stat, value);
        foreach (var (unit, statuses) in other._foreignStatuses)
            foreach (var (status, value) in statuses) SetForeign(unit, status, value);
        MaxHealth += other.MaxHealth;
        NonHealingMaxHealth += other.NonHealingMaxHealth;
        ScaleSkinCoef += other.ScaleSkinCoef;
        ParRegenFlat += other.ParRegenFlat;
        ParRegenPercent += other.ParRegenPercent;
        ParPoolFlat += other.ParPoolFlat;
        ParPoolPercent += other.ParPoolPercent;
        ParColorCalls.AddRange(other.ParColorCalls);
    }

    /// <summary>A stable text of everything the hook asked for; two samples are equal when their signatures are.</summary>
    public string Signature() => string.Join(";",
        _totals.OrderBy(pair => pair.Key).Select(pair => FormattableString.Invariant($"{pair.Key}={pair.Value:R}"))
            .Concat(_statuses.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"))
            .Concat(MaxHealth == 0.0 ? [] : [FormattableString.Invariant($"MaxHealth={MaxHealth:R}/{NonHealingMaxHealth:R}")])
            .Concat(ScaleSkinCoef == 0.0 ? [] : [FormattableString.Invariant($"ScaleSkinCoef={ScaleSkinCoef:R}")])
            .Concat(ParRegenFlat == 0.0 && ParRegenPercent == 0.0 ? [] : [FormattableString.Invariant($"ParRegen={ParRegenFlat:R}/{ParRegenPercent:R}")])
            .Concat(ParPoolFlat == 0.0 && ParPoolPercent == 0.0 ? [] : [FormattableString.Invariant($"ParPool={ParPoolFlat:R}/{ParPoolPercent:R}")])
            .Concat(_foreign.OrderBy(pair => pair.Key.NetId).SelectMany(unit => unit.Value.OrderBy(pair => pair.Key)
                .Select(pair => FormattableString.Invariant($"{unit.Key.NetId:x}.{pair.Key}={pair.Value:R}"))))
            .Concat(_foreignStatuses.OrderBy(pair => pair.Key.NetId).SelectMany(unit => unit.Value.OrderBy(pair => pair.Key)
                .Select(pair => $"{unit.Key.NetId:x}.{pair.Key}={pair.Value}"))));
}
