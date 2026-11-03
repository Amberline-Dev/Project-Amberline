using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Stats;

namespace Amberline.Simulation.Combat;

public sealed partial class CombatService
{
    /// <summary>
    /// <c>BBIncGold</c>: the script's gold joins the hero's gold and earned total through the award the kills use, and the 0x25 (UnitAddGold) the kill
    /// path sends after its award goes out too, from the unit the gold came from (the floating text of Parley's mark). Reconstruction (27 October 2026,
    /// owner finding 20): the shipped scripts do not say what retail sends for script gold; the kill's packet is the one the client shows gold with.
    /// </summary>
    public bool AwardHeroGold(Entity hero, double gold, Entity? source)
    {
        var awarded = AwardHeroProgress(hero, gold: gold);
        if (source is not null && _transport.IsWireReady(source)) _transport.SendGold(source, hero, gold);
        return awarded;
    }

    public bool AwardHeroProgress(Entity hero, double gold = 0.0, double experience = 0.0,
        bool allowExperienceWhileDead = false)
    {
        ArgumentNullException.ThrowIfNull(hero);
        if (!hero.Alive && !allowExperienceWhileDead) experience = 0.0;
        if (gold == 0.0 && experience == 0.0) return false;
        ApplyGold(hero, gold);
        hero.Experience += experience * (1.0 + ExperienceBonus(hero));
        while (hero.Level < 18 &&
            hero.Experience >= _progression.ExperienceThreshold(hero.Level + 1))
        {
            hero.Level++;
            hero.SkillPoints++;
            if (_transport.IsWireReady(hero)) _transport.SendLevelUp(hero);
            var stats = _heroStats ?? throw new InvalidOperationException(
                "hero level transitions require the match HeroStatsService");
            stats.Refresh(hero);
            if (_transport.IsWireReady(hero)) _transport.SendHeroStats(hero);
            _hooks.OnLevelChanged(hero);
        }
        // Gold and experience travel together here (a kill, an assist, a script's BBIncGold, a balance tool), so this
        // one block is the replication both of them were waiting for.
        if (_transport.IsWireReady(hero)) _transport.SendHeroGoldExp(hero);
        return true;
    }

    /// <summary>
    /// Income and team rewards: the gold itself, then its replication at once (see <see cref="ReplicateGold"/>).
    /// A caller that publishes gold and experience in one block (<see cref="AwardHeroProgress"/>) applies the gold
    /// through <see cref="ApplyGold"/> instead.
    /// </summary>
    private void GrantGold(Entity hero, double amount)
    {
        ApplyGold(hero, amount);
        ReplicateGold(hero);
    }

    private static void ApplyGold(Entity hero, double amount)
    {
        hero.Gold += amount;
        hero.MatchStatistics.RecordGoldEarned(amount);
    }

    /// <summary>
    /// Retail replicates a hero stat when it changes (the replication manager pushes the variable that moved). The
    /// owner's HUD draws the whole gold, <c>floor(mGold)</c> into <c>GoldCurrent_%d</c> with no smoothing of its own,
    /// so the new figure reaches the client the moment that whole value moves — every source: ambient gold, gold per
    /// ten, a kill, an assist, a turret's team reward, a sale, an item script — and nothing at all while it does not.
    /// The delta is gold alone; the periodic hero block is not resent for it.
    /// </summary>
    private void ReplicateGold(Entity hero)
    {
        if (!_transport.IsWireReady(hero)) return;
        if (hero.ReplicatedGold is double sent &&
            Entity.ShownGold(sent) == Entity.ShownGold(hero.Gold))
            return;
        hero.MarkReplicatedGold();
        _transport.SendHeroGold(hero);
    }

    private Entity[] ResolveChampionExperienceRecipients(Entity victim, Entity killer,
        IReadOnlyList<Entity> assistants)
    {
        var recipients = new Dictionary<uint, Entity>();
        if (killer.UnitType == UnitType.Hero &&
            WorldState.IsEnemy(killer, victim))
            recipients[killer.NetId] = killer;
        foreach (var assistant in assistants)
        {
            if (assistant.UnitType == UnitType.Hero &&
                WorldState.IsEnemy(assistant, victim))
                recipients[assistant.NetId] = assistant;
        }

        foreach (var hero in _world.Heroes())
        {
            if (!hero.Alive || !WorldState.IsEnemy(hero, victim)) continue;
            if (DistanceSquared(hero.Position, victim.Position) >
                ChampionExperienceShareRadius * ChampionExperienceShareRadius) continue;
            recipients[hero.NetId] = hero;
        }
        return recipients.Values.ToArray();
    }

    // Map1 Constants.var gcd_PercentEXPBonusMinimum (-1) and gcd_PercentEXPBonusMaximum (5): "the lowest/highest EXPBonus
    // Percent Mod" the sum can reach. The stock client only copies them into its global data, so the clamp itself
    // is the server's (Reconstruction: placed on the summed bonus, loadout plus PositiveChampionDelta). The catch-up's
    // 0.05 x T is PositiveChampionDelta.luaobj's own IncPercentEXPBonus row, part of the loadout figure since batch 159
    // part 3; the hand-written TeamSizeCatchUp.ExperiencePercent reader that stood in for it is gone.
    private double ExperienceBonus(Entity hero)
    {
        var values = _gameData.LevelConstants.Values;
        return Math.Clamp(hero.LoadoutExperiencePercent,
            values["gcd_PercentEXPBonusMinimum"], values["gcd_PercentEXPBonusMaximum"]);
    }

    private static double GoldPer10Bonus(Entity hero) => hero.LoadoutGoldPer10;
}
