namespace Amberline.Core;

/// <summary>Lifetime activity counters; respawn and reconnect retain the same instance.</summary>
public sealed class HeroMatchStatistics
{
    private readonly int[] _spellCasts = new int[4];
    private readonly int[] _summonerCasts = new int[2];
    private readonly HashSet<uint> _healedUnits = [];
    private double _lastChampionKillAt = double.NegativeInfinity;
    private int _multiKillCount;
    private double _matchStartedAt;
    private double _lifeStartedAt;
    private double? _deadSince;
    private double _completedDeadSeconds;
    private double _longestCompletedLife;
    private double? _disconnectedSince;
    private double? _lastDisconnectedAt;
    private double _completedDisconnectedSeconds;
    private double _lastActivityAt;
    private bool _currentlyAfk;
    private bool _wasAfk;
    private int _afkNoticeStage;

    public IReadOnlyList<int> SpellCasts { get; }
    public IReadOnlyList<int> SummonerCasts { get; }
    public double TotalDamageDealt { get; private set; }
    public double PhysicalDamageDealt { get; private set; }
    public double MagicDamageDealt { get; private set; }
    public double TotalDamageTaken { get; private set; }
    public double PhysicalDamageTaken { get; private set; }
    public double MagicDamageTaken { get; private set; }
    public double LargestCriticalStrike { get; private set; }
    public int TotalHeal { get; private set; }
    public int TotalUnitsHealed => _healedUnits.Count;
    public double GoldSpent { get; private set; }
    public int ItemsPurchased { get; private set; }
    public int ConsumablesPurchased { get; private set; }
    public int LargestMultiKill { get; private set; }
    public int DoubleKills { get; private set; }
    public int TripleKills { get; private set; }
    public int QuadraKills { get; private set; }
    public int PentaKills { get; private set; }
    public int UnrealKills { get; private set; }
    public double GoldEarned { get; private set; }
    public int FriendlyTurretsLost { get; private set; }
    public int FriendlyInhibitorsLost { get; private set; }
    public int FriendlyNexusesLost { get; private set; }
    public int SuperMonsterKills { get; private set; }
    public bool IsCurrentlyAfk => _currentlyAfk;
    public bool WasAfk => _wasAfk;
    /// <summary>Combat-clock time of the latest accepted activity (or match start).</summary>
    public double LastActivityAt => _lastActivityAt;
    /// <summary>Notices sent in the current idle episode: 0 none, 1 chat warning, 2 warning box, 3 kick.</summary>
    public int AfkNoticeStage => _afkNoticeStage;

    public HeroMatchStatistics()
    {
        SpellCasts = Array.AsReadOnly(_spellCasts);
        SummonerCasts = Array.AsReadOnly(_summonerCasts);
    }

    internal void RecordCast(int slot, bool summoner = false) =>
        (summoner ? _summonerCasts : _spellCasts)[slot]++;

    internal void StartMatch(double now)
    {
        _matchStartedAt = _lifeStartedAt = _lastActivityAt = now;
        _currentlyAfk = false;
        _wasAfk = false;
        _afkNoticeStage = 0;
    }

    /// <summary>Records accepted activity for the connected-player AFK policy.</summary>
    internal void RecordActivity(double now)
    {
        if (!double.IsFinite(now))
            throw new ArgumentOutOfRangeException(nameof(now));
        _lastActivityAt = now;
        _currentlyAfk = false;
        _afkNoticeStage = 0;
    }

    /// <summary>Moves this idle episode to <paramref name="stage"/>; false when already there.</summary>
    internal bool TryAdvanceAfkNotice(int stage)
    {
        if (stage <= _afkNoticeStage) return false;
        _afkNoticeStage = stage;
        return true;
    }

    /// <summary>
    /// Latches inactivity without changing transport disconnect accounting.
    /// The session calls this only for connected, ready human seats.
    /// </summary>
    internal void UpdateAfk(double now, double thresholdSeconds)
    {
        if (!double.IsFinite(now) || !double.IsFinite(thresholdSeconds) || thresholdSeconds < 0.0)
            throw new ArgumentOutOfRangeException(nameof(thresholdSeconds));
        if (_currentlyAfk || now - _lastActivityAt < thresholdSeconds) return;
        _currentlyAfk = _wasAfk = true;
    }

    internal void RecordDeath(double now)
    {
        if (_deadSince is not null) return;
        _longestCompletedLife = Math.Max(_longestCompletedLife, now - _lifeStartedAt);
        _deadSince = now;
    }

    internal void RecordRespawn(double now)
    {
        if (_deadSince is not double death) return;
        _completedDeadSeconds += Math.Max(0, now - death);
        _deadSince = null;
        _lifeStartedAt = now;
    }

    internal void RecordDisconnect(double now)
    {
        if (_disconnectedSince is not null) return;
        _disconnectedSince = _lastDisconnectedAt = now;
    }

    internal void RecordReconnect(double now)
    {
        if (_disconnectedSince is not double disconnected) return;
        _completedDisconnectedSeconds += Math.Max(0, now - disconnected);
        _disconnectedSince = null;
    }

    /// <summary>Records one credited Dragon/Worm-style epic objective kill.</summary>
    internal void RecordSuperMonsterKill() => SuperMonsterKills++;

    internal void RecordGoldEarned(double amount)
    {
        if (amount > 0) GoldEarned += amount;
    }

    internal void RecordStructureLoss(EntityKind kind)
    {
        switch (kind)
        {
            case EntityKind.Turret: FriendlyTurretsLost++; break;
            case EntityKind.Inhibitor: FriendlyInhibitorsLost++; break;
            case EntityKind.Nexus: FriendlyNexusesLost++; break;
        }
    }

    internal void RecordPurchase(double cost, bool consumable)
    {
        GoldSpent += cost;
        ItemsPurchased++;
        if (consumable) ConsumablesPurchased++;
    }

    internal int RecordChampionKill(double now, double window)
    {
        // A chain is time-based, independent of the death-reset killing spree.
        _multiKillCount = now - _lastChampionKillAt <= window ? _multiKillCount + 1 : 1;
        _lastChampionKillAt = now;
        LargestMultiKill = Math.Max(LargestMultiKill, _multiKillCount);
        // The stock event callbacks increment each attained tier separately.
        switch (_multiKillCount)
        {
            case 2: DoubleKills++; break;
            case 3: TripleKills++; break;
            case 4: QuadraKills++; break;
            case 5: PentaKills++; break;
            case >= 6: UnrealKills++; break;
        }
        return _multiKillCount;
    }

    internal void RecordDamageDealt(double amount, DamageType type, bool critical)
    {
        TotalDamageDealt += amount;
        if (type == DamageType.Physical) PhysicalDamageDealt += amount;
        if (type == DamageType.Magic) MagicDamageDealt += amount;
        if (critical) LargestCriticalStrike = Math.Max(LargestCriticalStrike, amount);
    }

    internal void RecordDamageTaken(double amount, DamageType type)
    {
        TotalDamageTaken += amount;
        if (type == DamageType.Physical) PhysicalDamageTaken += amount;
        if (type == DamageType.Magic) MagicDamageTaken += amount;
    }

    internal void RecordHealing(uint recipientNetId, double restored)
    {
        if (restored <= 0) return;
        // CLASSIC_Stats declares TOTAL_HEAL as INT. Stock AddToStat<float>
        // the float overload truncates each addition when the destination is an INT.
        TotalHeal += (int)(float)restored;
        _healedUnits.Add(recipientNetId);
    }

    /// <summary>Includes open life/disconnect intervals at the requested combat time.</summary>
    public HeroMatchStatisticsSnapshot Snapshot(double now) => new(
        TotalDamageDealt, PhysicalDamageDealt, MagicDamageDealt,
        TotalDamageTaken, PhysicalDamageTaken, MagicDamageTaken, LargestCriticalStrike,
        TotalHeal, TotalUnitsHealed, GoldSpent, ItemsPurchased, ConsumablesPurchased,
        Array.AsReadOnly(_spellCasts.ToArray()), Array.AsReadOnly(_summonerCasts.ToArray()),
        LargestMultiKill, DoubleKills, TripleKills, QuadraKills, PentaKills, UnrealKills,
        GoldEarned, FriendlyTurretsLost, FriendlyInhibitorsLost, FriendlyNexusesLost,
        Math.Max(0, now - _matchStartedAt),
        _completedDeadSeconds + (_deadSince is double death ? Math.Max(0, now - death) : 0),
        Math.Max(_longestCompletedLife, _deadSince is null ? Math.Max(0, now - _lifeStartedAt) : 0),
        _completedDisconnectedSeconds + (_disconnectedSince is double lost ? Math.Max(0, now - lost) : 0),
        _lastDisconnectedAt is double last ? Math.Max(0, last - _matchStartedAt) : null,
        _lastDisconnectedAt is double disconnectedAt ? Math.Max(0, now - disconnectedAt) : 0.0,
        _wasAfk, SuperMonsterKills);
}

public sealed record HeroMatchStatisticsSnapshot(
    double TotalDamageDealt, double PhysicalDamageDealt, double MagicDamageDealt,
    double TotalDamageTaken, double PhysicalDamageTaken, double MagicDamageTaken,
    double LargestCriticalStrike, int TotalHeal, int TotalUnitsHealed,
    double GoldSpent, int ItemsPurchased, int ConsumablesPurchased,
    IReadOnlyList<int> SpellCasts, IReadOnlyList<int> SummonerCasts,
    int LargestMultiKill, int DoubleKills, int TripleKills, int QuadraKills, int PentaKills, int UnrealKills,
    double GoldEarned, int FriendlyTurretsLost, int FriendlyInhibitorsLost, int FriendlyNexusesLost,
    double TimePlayed, double TotalTimeSpentDead, double LongestTimeSpentLiving,
    double TimeSpentDisconnected, double? LastDisconnectedAt,
    double TimeSinceLastDisconnect = 0.0, bool WasAfk = false, int SuperMonsterKills = 0);
