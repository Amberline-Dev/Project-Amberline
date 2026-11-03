using Amberline.Core;

namespace Amberline.Network.Protocol;

public enum SurrenderStatus
{
    Failed = 0,
    TooEarly = 1,
    Cooldown = 2,
    AlreadyVoted = 3,
    Agreed = 4,
}

/// <summary>Stock patch-126 layouts; see docs/network/patch126-surrender.md.</summary>
public static class SurrenderPackets
{
    public const byte ClientOpcode = 0xAC;

    public static bool TryReadVote(ReadOnlySpan<byte> payload, out bool yes)
    {
        yes = false;
        // The stock request is shorter than an ECB block and is sent raw.
        // Its header is not an actor identity; high flag bits are native padding.
        if (payload.Length != 6 || payload[0] != ClientOpcode) return false;
        yes = (payload[5] & 1) != 0;
        return true;
    }

    public static PlaintextPacket Vote(Team team, uint voterNetId, bool yes,
        bool firstVote, int yesCount, int noCount, int participants, double secondsRemaining)
    {
        RequireTeam(team);
        if (participants is < 1 or > 10 || yesCount < 0 || noCount < 0 ||
            yesCount + noCount > participants || !double.IsFinite(secondsRemaining) || secondsRemaining < 0)
            throw new ArgumentOutOfRangeException(nameof(participants));
        using var writer = ProtocolEncoding.Writer(21, out var stream);
        writer.Write((byte)0xD2);
        writer.Write(0u);
        writer.Write((byte)((yes ? 1 : 0) | (firstVote ? 2 : 0)));
        writer.Write(voterNetId);
        writer.Write((byte)yesCount);
        writer.Write((byte)noCount);
        writer.Write((byte)participants);
        writer.Write(team == Team.Order ? 100 : 200);
        writer.Write(ProtocolEncoding.Float32(secondsRemaining, nameof(secondsRemaining)));
        return Packet(stream);
    }

    public static PlaintextPacket Status(Team team, SurrenderStatus status, int yes, int no)
    {
        RequireTeam(team);
        if (!Enum.IsDefined(status) || yes < 0 || no < 0 || yes + no > 10)
            throw new ArgumentOutOfRangeException(nameof(status));
        using var writer = ProtocolEncoding.Writer(15, out var stream);
        writer.Write((byte)0xAD);
        writer.Write(0u);
        writer.Write((int)status);
        writer.Write((byte)yes);
        writer.Write((byte)no);
        writer.Write(team == Team.Order ? 100 : 200);
        return Packet(stream);
    }

    private static void RequireTeam(Team team)
    {
        if (team is not (Team.Order or Team.Chaos)) throw new ArgumentOutOfRangeException(nameof(team));
    }

    private static PlaintextPacket Packet(MemoryStream stream) =>
        ProtocolEncoding.Packet(LeagueProtocolConstants.CombatChannel, ProtocolEncoding.Finish(stream),
            encrypt: true, LeaguePacketReliability.Reliable);
}
