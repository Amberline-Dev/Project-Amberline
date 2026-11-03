using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Simulation.BehaviorTrees;

/// <summary>
/// Batch 518: Dominion's personal score ("Personal Score", flash_scoreboard_icon9; the TitanBar's ODIN score and rank). Each award adds
/// its points to the champion's combat or objective part and goes to every client as 0xE2 (<see cref="DominionPackets.IncrementPlayerScore"/>):
/// the client adds it to that hero's score and stats, re-ranks the hero's team, and the scorer's own client shows the "Label: +N" callout.
/// Reconstruction: the retail server's score code did not ship. What did ship: the event list and its labels (the client's table
/// ScoreEvent_e), the categories (ScoreCategory_e), and the marker buffs the scripts add for the server to read
/// (OdinShieldRelicAura: OdinScoreSmallRelic; OdinCenterRelicAura, OdinBombSuppression: OdinScoreBigRelic; OdinCaptureChannel's
/// BuffOnPreDamage: OdinCaptureInterrupt on the channeller with the attacker as its caster). The points are community lists of 2011 and the
/// wiki, put back to 1.0.0.126 with the V1.0.0.127 notes (19 October 2011, wiki): "Minion Kill reward increased to 2 points from 1",
/// "Channel Interrupt reward increased to 5 from 2", "Martyr (dying on point) now gives an additional 10 points over its previous bonus",
/// "Point defense bonus is granted more regularly", "There is now a score bonus associated with getting a champion kill or assist while
/// being on or near a point" (so 1.0.0.126 has no kill-on-point bonus). The table and its sources are in
/// docs/maintenance/2026-10-31-batch-518-dominion-player-score.md. Which category an event takes is a Reconstruction (kills combat,
/// the rest objective).
/// </summary>
public sealed class DominionPlayerScore(WorldState world, Func<Position2, Team?> pointOwnerAt, Action<PlaintextPacket>? send)
{
    /// <summary>One score event: its key in the client's label table, its category and its points.</summary>
    public readonly record struct Rule(byte Label, byte Category, int Points);

    private const byte Combat = DominionPackets.CombatScore;
    private const byte Objective = DominionPackets.ObjectiveScore;

    // Points: EUW forum "Dominion Score List" (thread 416524) unless noted; the owner's retail screenshot shows "Champion Kill: +15".
    public static readonly Rule ChampionKill = new(14, Combat, 15);
    public static readonly Rule ChampionAssist = new(15, Combat, 5);
    public static readonly Rule Ace = new(16, Combat, 25);
    public static readonly Rule FirstBlood = new(52, Combat, 2);
    public static readonly Rule DoubleKill = new(21, Combat, 15);
    public static readonly Rule TripleKill = new(22, Combat, 25);
    public static readonly Rule QuadraKill = new(23, Combat, 35);
    public static readonly Rule PentaKill = new(24, Combat, 50);
    // The list's spree section is marked unsure by its author; it names no value for Dominating, God Like and Legendary (none sent).
    public static readonly Rule KillingSpree = new(25, Combat, 10);
    public static readonly Rule Rampage = new(26, Combat, 10);
    public static readonly Rule Unstoppable = new(27, Combat, 15);
    /// <summary>V1.0.0.127: "increased to 2 points from 1".</summary>
    public static readonly Rule MinionKill = new(31, Combat, 1);
    /// <summary>Captures and neutralises 40 (EUW list, EUNE "List of points in Dominion" thread 373273, wiki Dominion).</summary>
    public static readonly Rule ZoneCapture = new(45, Objective, 40);
    public static readonly Rule ZoneNeutralize = new(46, Objective, 40);
    /// <summary>Both 2011 lists give 10 (the wiki's undated table gives 25, a later value).</summary>
    public static readonly Rule NodeCaptureAssist = new(55, Objective, 10);
    public static readonly Rule NodeNeutralizeAssist = new(56, Objective, 10);
    /// <summary>5 (both lists); the wiki: "every 10 seconds" (after V1.0.0.127's "more regularly"; the 1.0.0.126 interval is not known).</summary>
    public static readonly Rule Defender = new(48, Objective, 5);
    public const double DefenseInterval = 10.0;
    /// <summary>V1.0.0.127: "increased to 5 from 2".</summary>
    public static readonly Rule Counter = new(49, Objective, 2);
    /// <summary>Martyr: the EUW list's "+10 + (10 x enemy assistants)" less V1.0.0.127's extra 10, so 10 per enemy assistant.</summary>
    public static readonly Rule Martyr = new(9, Objective, 10);
    public static readonly Rule QuestComplete = new(17, Objective, 20);
    /// <summary>Storm Shield (Relic Hunter) 3 (both lists, wiki); health pack 2 (EUW list, wiki).</summary>
    public static readonly Rule MajorRelicPickup = new(54, Objective, 3);
    public static readonly Rule ScavengerHunt = new(44, Objective, 2);

    private double _nextDefenseAt = DefenseInterval;

    /// <summary>Adds the points to the champion's part and sends 0xE2 to every client (the scorer's own shows the callout).</summary>
    public void Award(Entity hero, Rule rule, int? points = null)
    {
        var amount = points ?? rule.Points;
        if (hero.UnitType != UnitType.Hero || amount <= 0) return;
        if (rule.Category == Combat) hero.OdinCombatScore += amount;
        else hero.OdinObjectiveScore += amount;
        send?.Invoke(DominionPackets.IncrementPlayerScore(hero.NetId, rule.Category, rule.Label, callout: true, amount));
    }

    /// <summary>
    /// A champion kill credited to <paramref name="killer"/> (CombatService.Kill). The kill, first blood, the multi-kill of
    /// <paramref name="multiKill"/>, the spree the streak reaches and the ace (the victim's team all dead) go to the killer; each assistant
    /// gets the assist. A killer on a point (<see cref="BehaviorTreeQuestHost.CaptureRadius"/>) counts a kill on a point: defence when its team
    /// holds the point, offence otherwise (no points in 1.0.0.126). A victim on its own team's point is a martyr.
    /// </summary>
    public void OnChampionKill(Entity killer, Entity victim, IReadOnlyList<Entity> assistants, int multiKill, bool firstBlood)
    {
        Award(killer, ChampionKill);
        if (firstBlood) Award(killer, FirstBlood);
        if (multiKill switch { 2 => DoubleKill, 3 => TripleKill, 4 => QuadraKill, 5 => PentaKill, _ => (Rule?)null } is { } multi)
            Award(killer, multi);
        if (killer.KillStreak switch { 3 => KillingSpree, 4 => Rampage, 5 => Unstoppable, _ => (Rule?)null } is { } spree)
            Award(killer, spree);
        if (world.Heroes().Where(hero => hero.Team == victim.Team).All(hero => !hero.Alive)) Award(killer, Ace);
        if (pointOwnerAt(killer.Position) is { } held)
        {
            if (held == killer.Team) killer.NodeKillsDefense++;
            else killer.NodeKillsOffense++;
        }
        foreach (var assistant in assistants) Award(assistant, ChampionAssist);
        if (assistants.Count > 0 && pointOwnerAt(victim.Position) == victim.Team) Award(victim, Martyr, Martyr.Points * assistants.Count);
    }

    /// <summary>A lane minion killed by a champion.</summary>
    public void OnMinionKill(Entity killer) => Award(killer, MinionKill);

    /// <summary>
    /// A script buff landed (SpellSystem.ScriptBuffAdded): the shipped score markers. The pickups name the picker as the target; the capture
    /// interrupt names the interrupter as the caster.
    /// </summary>
    public void OnScriptBuff(Entity target, Entity caster, string name)
    {
        switch (name)
        {
            case "OdinScoreSmallRelic": Award(target, ScavengerHunt); break;
            case "OdinScoreBigRelic": Award(target, MajorRelicPickup); break;
            case "OdinCaptureInterrupt" when caster.UnitType == UnitType.Hero && WorldState.IsEnemy(caster, target): Award(caster, Counter); break;
        }
    }

    /// <summary>A completed quest: each champion of the team counts a team objective and gets the quest's points.</summary>
    public void OnQuestComplete(Team team)
    {
        foreach (var hero in world.Heroes().Where(hero => hero.Team == team))
        {
            hero.TeamObjectives++;
            send?.Invoke(DominionPackets.IncrementPlayerStat(hero.NetId, DominionPackets.TeamObjectiveStat));
            Award(hero, QuestComplete);
        }
    }

    /// <summary>
    /// Point defence: every <see cref="DefenseInterval"/> seconds, each living champion on a point its team holds.
    /// ponytail: one match-wide cadence, not each champion's own time on the point; track per-champion time if the owner sees odd awards.
    /// </summary>
    public void Tick(double now)
    {
        if (now < _nextDefenseAt) return;
        _nextDefenseAt = now + DefenseInterval;
        foreach (var hero in world.Heroes().Where(hero => hero.Alive).ToArray())
            if (pointOwnerAt(hero.Position) == hero.Team) Award(hero, Defender);
    }

    /// <summary>
    /// A returning client's scores (Reconstruction: retail's reconnect burst is not recovered): each held champion's two parts as 0xE2 with
    /// no callout, so the client's ranks and TitanBar start right. The 0x4B rows that follow restate the stats the replay also added.
    /// </summary>
    public IEnumerable<PlaintextPacket> ReplayFor(IReadOnlySet<uint> known, uint ownHero)
    {
        foreach (var hero in world.Heroes().Where(hero => hero.NetId == ownHero || known.Contains(hero.NetId)))
        {
            if (hero.OdinCombatScore > 0)
                yield return DominionPackets.IncrementPlayerScore(hero.NetId, Combat, ChampionKill.Label, callout: false, hero.OdinCombatScore);
            if (hero.OdinObjectiveScore > 0)
                yield return DominionPackets.IncrementPlayerScore(hero.NetId, Objective, ZoneCapture.Label, callout: false, hero.OdinObjectiveScore);
        }
    }
}
