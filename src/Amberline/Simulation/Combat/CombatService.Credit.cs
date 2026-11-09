using Amberline.Core;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Stats;

namespace Amberline.Simulation.Combat;

public sealed partial class CombatService
{
    public DeathRecapSnapshot? GetLastDeathRecap(uint heroNetId) =>
        _lastDeathRecaps.TryGetValue(heroNetId, out var snapshot) ? snapshot : null;

    /// <summary>
    /// Records the source-side assist marker emitted by support spells and
    /// debuffs. Allied support is retained on the recipient (for example Heal
    /// on the champion who later gets a kill); an enemy marker is retained on
    /// the affected victim (for example Exhaust). Pet and clone sources are
    /// resolved recursively to their owning champion.
    /// </summary>
    public void RecordSupportAssist(Entity source, Entity recipient,
        double durationSeconds = AssistCreditSeconds)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(recipient);
        if (!double.IsFinite(durationSeconds) || durationSeconds < 0.0)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));

        var sourceHero = ResolveHeroOwner(source);
        if (sourceHero is null || sourceHero.NetId == recipient.NetId) return;
        var expiresAt = _time + AssistCreditSeconds;
        if (sourceHero.Team == recipient.Team)
        {
            if (!_supportCredits.TryGetValue(recipient.NetId, out var supporters))
                _supportCredits[recipient.NetId] = supporters = new();
            if (!supporters.TryGetValue(sourceHero.NetId, out var previous) || expiresAt > previous)
                supporters[sourceHero.NetId] = expiresAt;
            return;
        }

        RecordAssistCredit(recipient, sourceHero, damage: false, expiresAt);
    }

    private void RecordDamageCredit(Entity? source, Entity? spellOwner,
        Entity target, double damage)
    {
        if (damage <= 0.0 || target.UnitType != UnitType.Hero) return;
        // Snapshot support markers on the actual damaging unit while it is
        // still registered. Pets/turrets may be removed before the victim's
        // eventual death, so resolving only their hero owner at kill time
        // would lose a valid assist.
        PropagateSupportCredits(source, target);
        if (spellOwner is not null && spellOwner.NetId != source?.NetId)
            PropagateSupportCredits(spellOwner, target);
        var sourceHero = ResolveHeroOwner(spellOwner ?? source);
        if (sourceHero is null) return;
        if (!WorldState.IsEnemy(sourceHero, target)) return;
        RecordAssistCredit(target, sourceHero, damage: true, _time + AssistCreditSeconds);
    }

    private void PropagateSupportCredits(Entity? markedRecipient, Entity target)
    {
        if (markedRecipient is null ||
            !_supportCredits.TryGetValue(markedRecipient.NetId, out var supporters)) return;
        foreach (var pair in supporters)
        {
            if (pair.Value < _time) continue;
            var supporter = _world.Get(pair.Key);
            if (supporter is not null && supporter.UnitType == UnitType.Hero &&
                WorldState.IsEnemy(supporter, target))
                RecordAssistCredit(target, supporter, damage: false,
                    _time + AssistCreditSeconds);
        }
    }

    private void RecordAssistCredit(Entity target, Entity sourceHero,
        bool damage, double expiresAt)
    {
        if (!_assistCredits.TryGetValue(target.NetId, out var credits))
            _assistCredits[target.NetId] = credits = new();
        if (!credits.TryGetValue(sourceHero.NetId, out var credit))
            credits[sourceHero.NetId] = credit = new AssistCredit();
        credit.ExpiresAt = Math.Max(credit.ExpiresAt, expiresAt);
        credit.LastDamageAt = damage ? _time : credit.LastDamageAt;
    }

    private Entity? ResolveHeroOwner(Entity? unit)
    {
        // Direct hero credit needs no collection; retain cycle detection for pets.
        HashSet<uint>? visited = null;
        while (unit is not null)
        {
            if (unit.UnitType == UnitType.Hero && unit.Kind == EntityKind.Hero)
                return unit;
            if (unit.OwnerNetId == 0 || !(visited ??= []).Add(unit.NetId)) return null;
            unit = _world.Get(unit.OwnerNetId);
        }
        return null;
    }

    private Entity? ResolveKillCredit(Entity unit, uint explicitKillerNetId)
    {
        var explicitKiller = ResolveHeroOwner(_world.Get(explicitKillerNetId));
        if (explicitKiller is not null && WorldState.IsEnemy(explicitKiller, unit))
            return explicitKiller;

        if (_assistCredits.TryGetValue(unit.NetId, out var credits))
        {
            var recent = credits
                // Native GetLastHeroToAttack keeps a separate timestamp for
                // actual damage.  A support/debuff marker may extend the
                // assist window, but it must never turn an old hit into a
                // turret/neutral execution credit.
                .Where(pair => double.IsFinite(pair.Value.LastDamageAt) &&
                    pair.Value.LastDamageAt >= _time - AssistCreditSeconds)
                .Select(pair => (_world.Get(pair.Key), pair.Value.LastDamageAt))
                .Where(pair => pair.Item1 is not null && WorldState.IsEnemy(pair.Item1, unit))
                .OrderByDescending(pair => pair.LastDamageAt)
                .Select(pair => pair.Item1)
                .FirstOrDefault();
            if (recent is not null) return recent;
        }
        return null;
    }

    private IReadOnlyList<Entity> ResolveAssistants(Entity victim, Entity? killer)
    {
        var byId = new Dictionary<uint, Entity>();
        if (_assistCredits.TryGetValue(victim.NetId, out var credits))
        {
            foreach (var pair in credits)
            {
                if (pair.Value.ExpiresAt < _time || pair.Key == killer?.NetId) continue;
                var hero = _world.Get(pair.Key);
                if (hero is not null && hero.UnitType == UnitType.Hero &&
                    WorldState.IsEnemy(hero, victim))
                    byId[hero.NetId] = hero;
            }
        }

        // Allied support markers belong to the participating hero who was
        // helped. If that hero damaged the victim and another ally got the
        // last hit, carry the support marker through that contributor too.
        // This also resolves a marked pet/owned unit back to its hero owner.
        var participatingRecipients = byId.Values.ToArray();
        if (killer is not null)
            participatingRecipients = participatingRecipients.Append(killer).ToArray();
        foreach (var recipient in participatingRecipients)
        {
            foreach (var entry in _supportCredits)
            {
                var markedRecipient = _world.Get(entry.Key);
                if (markedRecipient is null ||
                    (markedRecipient.NetId != recipient.NetId &&
                     ResolveHeroOwner(markedRecipient)?.NetId != recipient.NetId))
                    continue;
                foreach (var pair in entry.Value)
                {
                    if (pair.Value < _time || pair.Key == killer?.NetId) continue;
                    var hero = _world.Get(pair.Key);
                    if (hero is not null && hero.UnitType == UnitType.Hero &&
                        WorldState.IsEnemy(hero, victim))
                        byId[hero.NetId] = hero;
                }
            }
        }
        return byId.Values.ToArray();
    }

    private void ClearCreditHistory(uint netId)
    {
        // A hero's death ends the hero's own incoming damage/support history,
        // but it does not revoke contributions already made to another
        // champion.  The native event manager keeps those source entries
        // alive until their assist window expires, including when the source
        // dies before the credited victim.
        _assistCredits.Remove(netId);
        _supportCredits.Remove(netId);
    }

    private void RecordDeathRecapDamage(
        Entity? source,
        Entity target,
        double amount,
        DamageType damageType,
        DamageSourceKind? sourceKind,
        DeathRecapOrigin? explicitOrigin,
        Entity? spellOwner)
    {
        if (amount <= 0.0 || target.UnitType != UnitType.Hero || target.Kind != EntityKind.Hero ||
            source is null || source.NetId == 0 ||
            !TryGetDeathRecapSourceType(source, out var sourceType))
            return;
        // A named spell may attribute a pet's periodic damage to the owning
        // hero, but only when the live attacker proves that ownership. Basic
        // and unclassified damage always validate against the actual attacker;
        // an arbitrary spellOwner must not forge a recap parent or source type.
        var originSource = source;
        var projectToParentWhenAttackerUnavailable = false;
        if (explicitOrigin is { EventSource: DeathRecapEventSource.Spell } &&
            spellOwner is not null &&
            (ReferenceEquals(source, spellOwner) || source.OwnerNetId == spellOwner.NetId) &&
            TryGetDeathRecapSourceType(spellOwner, out var ownerSourceType))
        {
            originSource = spellOwner;
            sourceType = ownerSourceType;
            projectToParentWhenAttackerUnavailable = source.NetId != spellOwner.NetId;
        }
        var origin = explicitOrigin ?? (sourceKind == DamageSourceKind.Basic
            ? DeathRecapOrigin.BasicAttack(CompactTeam(source.Team), sourceType)
            : default);
        if (!origin.IsSafeForSource(originSource, sourceType)) return;

        var physical = damageType == DamageType.Physical ? amount : 0.0;
        var magical = damageType == DamageType.Magic ? amount : 0.0;
        var trueDamage = damageType == DamageType.True ? amount : 0.0;
        if (!_deathRecapHistories.TryGetValue(target.NetId, out var history))
            _deathRecapHistories[target.NetId] = history = new DeathRecapHistory();
        history.Add(new DeathRecapRecord(_time, target.NetId, source.NetId,
            physical, magical, trueDamage, origin,
            projectToParentWhenAttackerUnavailable), _time);
    }

    private DeathRecapSnapshot? CaptureDeathRecap(Entity victim, uint killerNetId)
    {
        if (victim.UnitType != UnitType.Hero || victim.Kind != EntityKind.Hero)
            return null;
        var history = _deathRecapHistories.Remove(victim.NetId, out var existing)
            ? existing : null;
        if (history is null) return null;
        var records = history.Snapshot(_time);
        if (records.Count == 0) return null;
        var killer = _world.Get(killerNetId);
        if (killer is null || !TryGetDeathRecapSourceType(killer, out var killerSourceType))
            return null;
        return new DeathRecapSnapshot(++_deathRecapGeneration, victim.NetId, killerNetId,
            DeathRecapHistory.WindowSeconds, killerSourceType, records);
    }

    private static byte CompactTeam(Team team) => team switch
    {
        Team.Order => 0,
        Team.Chaos => 1,
        Team.Neutral => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(team)),
    };

    private static bool TryGetDeathRecapSourceType(Entity source, out DeathRecapSourceType sourceType)
    {
        sourceType = source.Kind switch
        {
            EntityKind.Hero => DeathRecapSourceType.Hero,
            EntityKind.Pet => DeathRecapSourceType.Pet,
            EntityKind.ShacoClone or EntityKind.YorickRevenant or EntityKind.ClonePet => DeathRecapSourceType.Clone,
            EntityKind.Turret or EntityKind.Inhibitor or EntityKind.Nexus => DeathRecapSourceType.Tower,
            EntityKind.Minion or EntityKind.Monster or EntityKind.Neutral => DeathRecapSourceType.Minion,
            _ when source.UnitType == UnitType.Turret || source.UnitType == UnitType.Structure =>
                DeathRecapSourceType.Tower,
            _ when source.UnitType is UnitType.Minion or UnitType.Monster =>
                DeathRecapSourceType.Minion,
            _ => DeathRecapSourceType.Unknown,
        };
        return sourceType != DeathRecapSourceType.Unknown;
    }

    private static DeathRecapOrigin? EmpoweredBasicAttackRecapOrigin(Entity source,
        bool? attackOverride)
    {
        if (source.UnitType != UnitType.Hero || source.Team is not (Team.Order or Team.Chaos))
            return null;
        // A BuildingBlocks attack override armed by a champion spell (Vayne's Tumble bonus, Kayle's Righteous Fury) names that spell.
        if ((attackOverride ?? true) && source.ScriptAttackOverride is { OriginSlot: >= 0 and <= 3, OriginRank: >= 1 } armed)
            return DeathRecapOrigin.ChampionSpell(source, armed.OriginSlot, armed.OriginRank);
        return null;
    }

    private void RecordStructureLoss(Entity structure)
    {
        if (structure.Kind is not (EntityKind.Turret or EntityKind.Inhibitor or EntityKind.Nexus)) return;
        foreach (var hero in _world.Heroes().Where(hero => hero.Team == structure.Team))
            hero.MatchStatistics.RecordStructureLoss(structure.Kind);
    }
}
