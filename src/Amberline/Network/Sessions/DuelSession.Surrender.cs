using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Network.Sessions;

public sealed partial class DuelSession
{
    // Reconstruction: no client code reads these numbers (the surrender panel only takes the timeout the server sends in 0xD2) and the
    // server key names are unrecovered. The shipped LEVELS/Map1/Mission.inibin string entries hold exactly 1200, 60, 60.0, 180, 180 and
    // .7, which matches the classic period rules recorded in
    // docs/network/patch126-surrender.md: first vote 20 minutes after the start, 60-second ballot, 3-minute retry after a failed
    // ballot, approval of 70% of the team's human seats (A10 audit, 18 October 2026). Batch 315: the client's one read of the file
    // (LEVELS\%s\Mission.ini, the HUD) takes only the two ExtraSummonerItem keys, so the surrender keys'
    // names and reader are server code.
    private const double FirstSurrenderSeconds = 20 * 60;
    private const double SurrenderVoteSeconds = 60;
    private const double SurrenderRetrySeconds = 3 * 60;
    private readonly Dictionary<Team, TeamSurrenderVote> _surrenderVotes = new();

    private sealed class TeamSurrenderVote
    {
        public Dictionary<ulong, bool> Votes { get; } = new();
        public double ExpiresAt { get; set; }
        public double RetryAt { get; set; }
        public int Yes => Votes.Count(vote => vote.Value);
        public int No => Votes.Count - Yes;
    }

    private void HandleSurrender(List<SessionOutboundPacket> output,
        SessionParticipant voter, ReadOnlySpan<byte> payload)
    {
        if (voter.IsBot || !Started || Paused || _combatGameOver() || !voter.Spawned || !voter.Ready ||
            voter.Hero is not { } hero || !SurrenderPackets.TryReadVote(payload, out var yes)) return;

        var now = _combatClock();
        ExpireSurrenderVotes(output, now);
        if (!_surrenderVotes.TryGetValue(voter.Team, out var vote))
            _surrenderVotes.Add(voter.Team, vote = new TeamSurrenderVote());

        var first = vote.Votes.Count == 0;
        if (first)
        {
            if (!yes) return; // /noff cannot open a ballot.
            if (now - _startClock < FirstSurrenderSeconds)
            {
                Reply(SurrenderStatus.TooEarly);
                return;
            }
            if (now < vote.RetryAt)
            {
                Reply(SurrenderStatus.Cooldown);
                return;
            }
            vote.ExpiresAt = now + SurrenderVoteSeconds;
        }
        if (!vote.Votes.TryAdd(voter.PlayerId, yes))
        {
            Reply(SurrenderStatus.AlreadyVoted);
            return;
        }
        MarkActivity(voter);

        // Surrender is a human vote. Bots remain in the fixed roster for
        // gameplay/economy accounting but never enter this denominator.
        var members = HumanSurrenderMembers(voter.Team);
        AddSurrenderTeam(output, voter.Team, SurrenderPackets.Vote(voter.Team, hero.NetId,
            yes, first, vote.Yes, vote.No, members, vote.ExpiresAt - now));
        if (vote.Yes * 10 >= members * 7)
        {
            // Completion queues the ordinary ceremony. Session replies are sent
            // before delivery flush, so clients see the agreed status first.
            if (_combat.Surrender(voter.Team))
            {
                var packet = SurrenderPackets.Status(voter.Team, SurrenderStatus.Agreed, vote.Yes, vote.No);
                foreach (var seat in _participants.Where(seat => !seat.IsBot && seat.Spawned && seat.Ready))
                    output.AddRange(ToParticipant(seat, [packet]));
                _surrenderVotes.Clear();
            }
        }
        else if (vote.Votes.Count == members)
            FailSurrender(output, voter.Team, vote, now, members);

        void Reply(SurrenderStatus status) => output.AddRange(ToParticipant(voter,
            [SurrenderPackets.Status(voter.Team, status, vote.Yes, vote.No)]));
    }

    private void ExpireSurrenderVotes(List<SessionOutboundPacket> output, double now)
    {
        if (_combatGameOver())
        {
            _surrenderVotes.Clear();
            return;
        }
        foreach (var (team, vote) in _surrenderVotes)
        {
            if (vote.Votes.Count == 0 || now < vote.ExpiresAt) continue;
            FailSurrender(output, team, vote, vote.ExpiresAt,
                HumanSurrenderMembers(team));
        }
    }

    private void FailSurrender(List<SessionOutboundPacket> output, Team team,
        TeamSurrenderVote vote, double failedAt, int members)
    {
        AddSurrenderTeam(output, team,
            SurrenderPackets.Status(team, SurrenderStatus.Failed, vote.Yes, members - vote.Yes));
        vote.Votes.Clear();
        vote.RetryAt = failedAt + SurrenderRetrySeconds;
    }

    private void AddSurrenderTeam(List<SessionOutboundPacket> output, Team team, PlaintextPacket packet)
    {
        foreach (var seat in _participants.Where(seat => seat.Team == team && !seat.IsBot && seat.Spawned && seat.Ready))
            output.AddRange(ToParticipant(seat, [packet]));
    }

    private void AddReconnectSurrender(List<PlaintextPacket> packets, SessionParticipant recipient)
    {
        var now = _combatClock();
        if (_combatGameOver() || !_surrenderVotes.TryGetValue(recipient.Team, out var vote) ||
            vote.Votes.Count == 0 || now >= vote.ExpiresAt) return;
        var members = HumanSurrenderMembers(recipient.Team);
        var first = true;
        foreach (var (playerId, yes) in vote.Votes)
        {
            var voter = _participants.Single(seat => seat.PlayerId == playerId);
            packets.Add(SurrenderPackets.Vote(recipient.Team, voter.Hero!.NetId,
                yes, first, vote.Yes, vote.No, members, vote.ExpiresAt - now));
            // The native first-vote bit initializes the panel and timer. Only
            // this fresh client receives the replay; teammates hear no repeat.
            first = false;
        }
    }

    private int HumanSurrenderMembers(Team team) =>
        _participants.Count(seat => seat.Team == team && !seat.IsBot);
}
