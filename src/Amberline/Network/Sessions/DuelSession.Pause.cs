using Amberline.Network.Protocol;

namespace Amberline.Network.Sessions;

public sealed partial class DuelSession
{
    // Stock GameTime defaults (TotalLimitSecs 1800, ResumeDelay 5; no shipped override). Reconstruction:
    // sharing the budget between both teams and letting any ready human seat pause or resume; the original server's
    // permissions are not in the client.
    private const double PauseLimitSeconds = 1800;
    private const double ResumeDelaySeconds = 5;
    private double _pauseSecondsUsed;
    private double? _pausedAt;
    private double? _resumeAt;
    private uint _resumeClientId = uint.MaxValue;

    public bool Paused => _pausedAt.HasValue;
    public double PauseSecondsRemaining => Math.Max(0, PauseLimitSeconds - _pauseSecondsUsed -
        (_pausedAt is double start ? Math.Max(0, World.Clock.Monotonic() - start) : 0));

    private void HandlePause(List<SessionOutboundPacket> output,
        SessionParticipant participant, ReadOnlySpan<byte> payload)
    {
        if (participant.IsBot || !Started || _combatGameOver() || !participant.Spawned || !participant.Ready ||
            participant.Hero is null || !PausePackets.IsRequest(payload)) return;

        // The client's header, client ID, timer and flag bytes are not authority.
        // In particular the native request's timer bytes may be uninitialized.
        var now = World.Clock.Monotonic();
        if (payload[0] == PausePackets.PauseOpcode)
        {
            if (Paused || PauseSecondsRemaining <= 0) return;
            _pausedAt = now;
            MarkActivity(participant);
            AddPauseBroadcast(output, PausePackets.Pause(participant.ClientId,
                checked((int)Math.Ceiling(PauseSecondsRemaining))));
        }
        else if (Paused && _resumeAt is null)
        {
            _resumeClientId = participant.ClientId;
            _resumeAt = now + ResumeDelaySeconds;
            MarkActivity(participant);
            AddPauseBroadcast(output, PausePackets.Resume(participant.ClientId, delayed: true));
        }
    }

    private void UpdatePause(List<SessionOutboundPacket> output, double now)
    {
        if (_pausedAt is not double start) return;
        var budgetEndsAt = start + PauseLimitSeconds - _pauseSecondsUsed;
        if (!_combatGameOver() && now < Math.Min(_resumeAt ?? double.PositiveInfinity, budgetEndsAt)) return;

        var duration = Math.Max(0, now - start);
        _pauseSecondsUsed = Math.Min(PauseLimitSeconds, _pauseSecondsUsed + duration);
        _pausedAt = null;
        _resumeAt = null;
        // The initial protection scheduler uses wall time, but protection must
        // retain its unpaused duration just like the simulation-owned timers.
        if (_protectAt is double protectAt) _protectAt = protectAt + duration;
        AddPauseBroadcast(output, PausePackets.Resume(_resumeClientId, delayed: false));
        AddPauseBroadcast(output, SpawnPackets.GameClock(
            ProtocolEncoding.Float32(Math.Max(0, _combatClock() - _startClock), "gameTime")));
        _resumeClientId = uint.MaxValue;
    }

    private void AddPauseBroadcast(List<SessionOutboundPacket> output, PlaintextPacket packet)
    {
        foreach (var participant in _participants.Where(seat => seat.Spawned && seat.Ready))
            output.AddRange(ToParticipant(participant, [packet]));
    }

    private void AddReconnectPause(List<PlaintextPacket> packets)
    {
        if (!Paused || PauseSecondsRemaining <= 0) return;
        // Replaying a delayed resume restarts the client's full five seconds.
        // A returning client stays paused until the authoritative final resume.
        packets.Add(PausePackets.Pause(uint.MaxValue,
            checked((int)Math.Ceiling(PauseSecondsRemaining))));
    }
}
