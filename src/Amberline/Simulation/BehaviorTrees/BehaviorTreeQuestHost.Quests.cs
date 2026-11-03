using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;

namespace Amberline.Simulation.BehaviorTrees;

/// <summary>
/// Batch 471: Dominion's capture point quests. Reconstruction: no shipped tree or script issues them. The shipped ActivateQuest and
/// TestQuestClicked nodes stand only in SpawnManagerTest (OdinScriptBehaviorTrees.xml:3404-3742), a developer's wave-spawn panel that
/// OdinScript.xml never starts, and nothing creates the quest encounters. So the server's quest driver did not ship; it is rebuilt here
/// from the wiki's rules (Dominion, Quest: "A quest is issued to both teams after the 5 minute mark. This involves capturing an
/// enemy-held point and defending a capture point you control. Completing these quests will end the quest set, sap 20 enemy Nexus
/// health, and bestow a buff 'Mark of the Conqueror'"; "A new quest is issued 5 minutes after the completion of the first.") and the
/// shipped pieces: the client's quest packet (<see cref="DominionPackets.QuestUpdate"/>), the tracker keys (fontconfig_en_US.txt:5106-5115,
/// game_quest_capture_a..e and game_quest_defend_a..e), the quest announcement (<see cref="CombatPackets.QuestReceived"/>), the point
/// markers (LEVELS/Map8/Encounters/OdinQuestIcon.json, minimap icon Quest, and OdinQuestIcon_Chaos.json, Quest_Chaos: an
/// OdinQuestIndicator whose CharOnActivate marks the guardian within 300 with OdinQuestIndicator, cleared by OdinQuestParticleRemover when
/// the marker dies) and the reward (OdinQuestBuffGiver.json: an OdinQuestBuff unit whose CharOnActivate gives every allied champion
/// OdinQuestBuff for 50 s, x1.15 / x1.3 with the mastery's MonsterBuffs / MonsterBuffs2, marks the guardians within 300 with
/// OdinQuestBuffParticle and then kills itself).
/// </summary>
public sealed partial class BehaviorTreeQuestHost
{
    /// <summary>The first set (wiki: "after the 5 minute mark") and the wait after a completion (wiki: "5 minutes after").</summary>
    public const double FirstQuestAt = 300.0;
    public const double QuestRenewal = 300.0;

    /// <summary>Wiki: a completed quest saps 20 enemy nexus health.</summary>
    public const double QuestNexusSap = 20.0;

    /// <summary>The match's random source for the quest points; null takes the lowest candidate (a check's harness).</summary>
    public IRandomSource? Random { get; init; }

    /// <summary>One team's half of a quest set: the point it must capture, the point it defends, its two quest ids and its marker.</summary>
    private sealed record QuestSide(Team Team, int Target, int Defend, uint CaptureQuest, uint DefendQuest, Entity? Marker);

    private QuestSide[]? _questSet;
    private double _nextQuestAt = FirstQuestAt;
    private uint _nextQuestId = 1;

    private void ServeQuests(double now)
    {
        if (_questSet is { } open)
        {
            EndQuestsOnCapture(open, now);
            return;
        }
        if (now < _nextQuestAt || _captures.Count != CapturePointNames.Length) return;
        // Reconstruction: each team's capture target is a point the other team holds, picked at random; a set waits until both teams hold
        // a point. The two targets cross: what one team must capture, the other must defend (the wiki's capture and defend pair).
        if (QuestPoint(Team.Chaos) is not { } orderTarget || QuestPoint(Team.Order) is not { } chaosTarget) return;
        _questSet = [IssueQuests(Team.Order, orderTarget, chaosTarget), IssueQuests(Team.Chaos, chaosTarget, orderTarget)];
    }

    private int? QuestPoint(Team holder)
    {
        var held = CapturePointOwners.Where(pair => pair.Value == holder).Select(pair => pair.Key).OrderBy(point => point).ToArray();
        if (held.Length == 0) return null;
        return held[Random is { } random ? random.NextIndex(held.Length) : 0];
    }

    /// <summary>
    /// The team hears the quest announcement (event 98 with one of its champions as the source, for the "your team" text) and gets its
    /// capture quest (primary) and its defend quest (secondary) in its tracker; its target is marked. Reconstruction: both go to the team's
    /// clients only, with no tooltip, no reward text and no rollover handling (every shipped ActivateQuest node passes HandleRollOver false
    /// and an empty Tooltip); the quest ids count up from 1.
    /// </summary>
    private QuestSide IssueQuests(Team team, int target, int defend)
    {
        var source = Context.World.Heroes().FirstOrDefault(hero => hero.Team == team);
        SendTeam(team, CombatPackets.OnEventWorld(CombatPackets.QuestReceived, source?.NetId ?? 0u, 0, GameMaps.CrystalScar));
        var side = new QuestSide(team, target, defend, _nextQuestId++, _nextQuestId++, null);
        foreach (var add in QuestAdds(side)) SendTeam(team, add);
        return side with { Marker = SpawnAtPoint(team == Team.Order ? "OdinQuestIcon" : "OdinQuestIcon_Chaos", target, team) };
    }

    /// <summary>The tracker adds of a team's quests: its capture quest (primary) and its defend quest (secondary).</summary>
    private static PlaintextPacket[] QuestAdds(QuestSide side) =>
    [
        DominionPackets.QuestUpdate(side.CaptureQuest, DominionPackets.PrimaryQuest, DominionPackets.QuestAdd, false,
            "game_quest_capture_" + PointLetter(side.Target)),
        DominionPackets.QuestUpdate(side.DefendQuest, DominionPackets.SecondaryQuest, DominionPackets.QuestAdd, false,
            "game_quest_defend_" + PointLetter(side.Defend)),
    ];

    /// <summary>
    /// The set ends when a team holds its target. Reconstruction: the winner's two quests complete (flag 1) and the other team's two fail
    /// (flag 0: Quests.ini FailedText and FailedSound); the client retires a completed quest itself, so no hide is sent. Both markers die,
    /// the reward giver spawns on the captured point for the winner, the other nexus loses <see cref="QuestNexusSap"/>, and the next set
    /// is due <see cref="QuestRenewal"/> seconds later.
    /// </summary>
    private void EndQuestsOnCapture(QuestSide[] set, double now)
    {
        var owners = CapturePointOwners;
        if (set.FirstOrDefault(side => owners.GetValueOrDefault(side.Target) == side.Team) is not { } winner) return;
        foreach (var side in set)
        {
            var won = side.Team == winner.Team;
            SendTeam(side.Team, DominionPackets.QuestUpdate(side.CaptureQuest, DominionPackets.PrimaryQuest, DominionPackets.QuestComplete, won));
            SendTeam(side.Team, DominionPackets.QuestUpdate(side.DefendQuest, DominionPackets.SecondaryQuest, DominionPackets.QuestComplete, won));
            if (side.Marker is { Alive: true } marker)
            {
                if (Context.Capture?.Kill is { } kill) kill(marker, 0u);
                else Context.Note("quest-kill", "this context has no kill hook: the quest marker stays");
            }
        }
        SpawnAtPoint("OdinQuestBuffGiver", winner.Target, winner.Team);
        // Batch 518 (Reconstruction): the winner's champions each count the team objective and get Quest Complete's score.
        PlayerScore.OnQuestComplete(winner.Team);
        // Batch 505: no floor (forum post, 15 February 2012: "completing a quest gives -20 health regardless of current health (Can win the
        // game)"), so a sap at 20 or less ends the match through the damage path.
        Score?.DamageNexus(winner.Team == Team.Order ? Team.Chaos : Team.Order, QuestNexusSap);
        _questSet = null;
        _nextQuestAt = now + QuestRenewal;
    }

    private static char PointLetter(int point) => (char)('a' + point - 1);

    private void SendTeam(Team team, PlaintextPacket packet)
    {
        if (Context.Capture?.SendTeam is { } send) send(team, packet);
        else Context.Note("quest-send", "this context has no team sender: the quest packets are not sent");
    }

    /// <summary>One unit of a shipped encounter on a capture point (its CapturePosition), for a team; null when it does not spawn.</summary>
    private Entity? SpawnAtPoint(string definition, int point, Team team)
    {
        if (Context.Encounters is not { } encounters || _captures[point - 1].Variable("CapturePosition") is not Position3 at) return null;
        if (encounters.CreateEncounter(definition, 1, "") is not { } encounter)
        {
            Context.Note("quest:" + definition, $"no encounter definition named {definition}");
            return null;
        }
        var squad = encounters.SpawnSquad(encounter, new Position2(at.X, at.Z), team, "");
        encounters.DeleteEncounter(encounter);
        return squad is { } id ? encounters.Squad(id)?.Units.FirstOrDefault() : null;
    }
}
