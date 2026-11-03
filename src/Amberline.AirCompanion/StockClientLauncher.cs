using System.Diagnostics;

namespace Amberline.AirCompanion;

public sealed record MaestroCreateRequest(string ServerHost, int ServerPort, string SecurityKey, long PlayerId)
{
    public static MaestroCreateRequest Parse(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var fields = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
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

    public string ToGameArgument() => $"{ServerHost} {ServerPort} {SecurityKey} {PlayerId}";
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
        var process = Process.Start(CreateStartInfo(Executable, request, MaestroPort));
        return process ?? throw new IOException("The stock game client could not be started.");
    }

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
    /// Nothing here names a drive; the same input on another volume only changes the paths.
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
            WorkingDirectory = Path.GetDirectoryName(fullPath)!,
            UseShellExecute = false,
        };
        // The stock client parses the Maestro port from token one, ignores tokens two and three,
        // and consumes the compound game payload from token four. The existing RemoteClient
        // convention is retained in positions two through four, with the source-required port
        // added to position one so native Maestro does not receive atoi("") == 0.
        foreach (var argument in new[] { maestroPort.ToString(System.Globalization.CultureInfo.InvariantCulture), "", "", request.ToGameArgument() })
            start.ArgumentList.Add(argument);
        return start;
    }
}
