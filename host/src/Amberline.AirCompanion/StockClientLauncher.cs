using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Amberline.AirCompanion;

/// <summary>
/// The Maestro create payload, in two forms. A player's is "&lt;host&gt; &lt;port&gt; &lt;key&gt; &lt;summonerId&gt;" (the native game server). AIR's spectator launch
/// is "spectator &lt;host&gt; &lt;key&gt; &lt;gameId&gt; &lt;platformId&gt;" (batch 849): <see cref="PlatformId"/> is then set, and the stock client reads the game
/// from the local spectator grid on its fixed port, which the launch does not carry. A spectator's <see cref="ServerHost"/> is that grid's host,
/// and its launch argument comes from the grid's constants, never from <see cref="ServerHost"/>.
/// </summary>
public sealed record MaestroCreateRequest(string ServerHost, int ServerPort, string SecurityKey, long PlayerId, string? PlatformId = null)
{
    /// <summary>The local spectator grid's host, which the stock client's spectator launch names (design B3, C3).</summary>
    private const string SpectatorGridHost = "127.0.0.1";
    /// <summary>The stock client's fixed spectator grid port; a spectator launch never carries it.</summary>
    internal const int SpectatorGridPort = 8088;

    public bool IsSpectator => PlatformId is not null;

    public static MaestroCreateRequest Parse(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var fields = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length > 0 && fields[0] == "spectator") return ParseSpectator(fields);
        if (fields.Length != 4)
            throw new FormatException("A Maestro create payload must contain server host, port, key and player ID.");

        var host = fields[0];
        if (host.Length is 0 or > 253 || host.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-'))
            throw new FormatException("The Maestro game host must be a bare IPv4 address or DNS name.");
        if (Uri.CheckHostName(host) is not (UriHostNameType.IPv4 or UriHostNameType.Dns))
            throw new FormatException("The Maestro game host must be a bare IPv4 address or DNS name.");
        if (!int.TryParse(fields[1], out var port) || port is < 1 or > 65535)
            throw new FormatException("The Maestro game port must be between 1 and 65535.");
        if (fields[2].Length != 16 || fields[2].Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new FormatException("The Maestro security key must be 16 ASCII letters or digits.");
        if (!long.TryParse(fields[3], out var playerId) || playerId < 1)
            throw new FormatException("The Maestro player ID must be a positive 64-bit integer.");
        return new MaestroCreateRequest(host, port, fields[2], playerId);
    }

    private static MaestroCreateRequest ParseSpectator(string[] fields)
    {
        // The stock client reads the launch as mode, host, key, game ID and platform ID. Each
        // field is checked against what the client can use; a refusal names the field and never echoes the key.
        if (fields.Length != 5)
            throw new FormatException("A spectator launch must contain the host, key, game ID and platform ID after the word spectator.");
        // NOT RETAIL: retail's spectator launch named the publisher's public spectator host (design C8). This PC's own grid is the only host here.
        if (fields[1] != SpectatorGridHost)
            throw new FormatException("The spectator host must be the local spectator grid, 127.0.0.1.");
        // Standard base64 of 24 bytes: 32 characters, and '+' and '/' are normal in it (about two keys in three carry one).
        if (fields[2].Length != 32 || !fields[2].All(character => char.IsAsciiLetterOrDigit(character) || character is '+' or '/'))
            throw new FormatException("The spectator key must be 32 standard base64 characters.");
        // The client keys its decryption with the game ID as a 32-bit long; the platform's key wrap keeps the same bound.
        if (!long.TryParse(fields[3], out var gameId) || gameId is < 1 or > int.MaxValue)
            throw new FormatException("The spectator game ID must be between 1 and 2147483647.");
        if (fields[4].Length > 8 || !fields[4].All(character => char.IsAsciiDigit(character) || char.IsAsciiLetterUpper(character)))
            throw new FormatException("The spectator platform ID must be 1 to 8 capital letters or digits.");
        return new MaestroCreateRequest(SpectatorGridHost, SpectatorGridPort, fields[2], gameId, fields[4]);
    }

    public string ToGameArgument() => PlatformId is null
        ? $"{ServerHost} {ServerPort} {SecurityKey} {PlayerId}"
        : $"spectator {SpectatorGridHost} {SecurityKey} {PlayerId} {PlatformId}";
}

public sealed class StockClientLauncher
{
    public StockClientLauncher(string executable, int maestroPort = 8393)
    {
        Executable = Path.GetFullPath(executable ?? throw new ArgumentNullException(nameof(executable)));
        if (!string.Equals(Path.GetFileName(Executable), "League of Legends.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The configured stock executable must be the game executable.", nameof(executable));
        if (!File.Exists(Executable)) throw new FileNotFoundException("The configured stock executable was not found.", Executable);
        if (maestroPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(maestroPort));
        MaestroPort = maestroPort;
    }

    public string Executable { get; }
    public int MaestroPort { get; }

    public Process Launch(MaestroCreateRequest request)
    {
        // A spectator's host is the local grid, which its argument never reads from ServerHost, so it is not resolved.
        var start = request.IsSpectator ? request : request with { ServerHost = GameAddress(request.ServerHost) };
        var process = Process.Start(CreateStartInfo(Executable, start, MaestroPort));
        return process ?? throw new IOException("The stock game client could not be started.");
    }

    /// <summary>
    /// NOT RETAIL: retail's platform always sent the game server's number address. A host may give players a name (a domain on a
    /// rented server), which the stock client cannot use: it fails with "Enet could not connect! Status - 7" (owner, 12 November
    /// 2026, batch 749). A name is resolved to its IPv4 address at each launch, so a changed DNS record counts for the next game. An IPv4
    /// address goes out in its canonical form: "010.0.0.1" is 8.0.0.1 here and to the stock client, and the platform refuses a leading zero.
    /// </summary>
    public static string GameAddress(string host) =>
        IPAddress.TryParse(host, out var address) ? (address.AddressFamily == AddressFamily.InterNetwork ? address.ToString() : host)
            : Dns.GetHostAddresses(host, AddressFamily.InterNetwork).FirstOrDefault()?.ToString()
              ?? throw new IOException($"The game server name {host} has no IPv4 address.");

    public static ProcessStartInfo CreateStartInfo(string executable, MaestroCreateRequest request, int maestroPort = 8393)
    {
        var start = PlanStart(executable, request, maestroPort);
        if (!File.Exists(start.FileName)) throw new FileNotFoundException("The configured stock executable was not found.", start.FileName);
        return start;
    }

    /// <summary>
    /// The complete launch plan, derived from the picked executable alone (no file access): the game starts
    /// from its own folder, which is where the stock client resolves <c>DATA\</c>, <c>LEVELS\</c>,
    /// <c>..\Air\preferences\</c> and writes its logs, so the plan holds on any drive or folder name.
    /// Nothing here names a drive; the same input on another volume only changes the paths. How rads.dll is to read that folder is the
    /// release line's (<see cref="CompanionLine.GameWorkingDirectory"/>, batch 889).
    /// </summary>
    public static ProcessStartInfo PlanStart(string executable, MaestroCreateRequest request, int maestroPort = 8393)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fullPath = Path.GetFullPath(executable ?? throw new ArgumentNullException(nameof(executable)));
        if (!string.Equals(Path.GetFileName(fullPath), "League of Legends.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The configured stock executable must be the game executable.", nameof(executable));
        if (maestroPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(maestroPort));
        var start = new ProcessStartInfo(fullPath)
        {
            WorkingDirectory = CompanionLine.GameWorkingDirectory(Path.GetDirectoryName(fullPath)!),
            UseShellExecute = false,
        };
        // The stock client parses the Maestro port from token one, ignores tokens two and three,
        // and consumes the compound game payload from token four. Tokens two and three are empty
        // (the fixed shape of the four-argument native launch), with the source-required port
        // added to position one so native Maestro does not receive atoi("") == 0.
        foreach (var argument in new[] { maestroPort.ToString(System.Globalization.CultureInfo.InvariantCulture), "", "", request.ToGameArgument() })
            start.ArgumentList.Add(argument);
        return start;
    }
}
