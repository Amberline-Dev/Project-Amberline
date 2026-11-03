using Amberline.Data;

namespace Amberline.Server;

public sealed record RemoteCatalog(int Version, Dictionary<string, ChampionSkin[]> Champions,
    RuneData[] Runes, MasteryDefinition[] Masteries);
public sealed record GuestInvitation(string Token, string CertificateSha256, int Port = 5120);
/// <summary>MapId is the server's <c>--map-id</c>: 1 (Summoner's Rift), 2 (the Harrowing rift, Autumn), 3 (the Basic Tutorial), 4 (Twisted Treeline) or 8 (The Crystal Scar); the Platform picks it.</summary>
public sealed record CreateGameRequest(Guid RequestId, MatchPlayer[] Players, int MapId = 1);
public sealed record RemoteSeat(int Id, MatchPlayer Player, bool Claimed, bool Yours, bool Connected, bool Loaded);
public sealed record RemoteGame(Guid Id, string Owner, string State, int Port, RemoteSeat[] Seats,
    bool CanStop, string? Error);
public sealed record JoinGameReply(int Port, int PlayerId, string Key);
public sealed record ManagedMatchStatus(bool Started, bool Finished, int[] Connected, int[] Loaded);
