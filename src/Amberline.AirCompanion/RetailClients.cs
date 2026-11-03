using System.Security.Cryptography;

// Linked as source into the Platform as well (its setup page checks the host's folders); keep this file free of other companion types.
#if AMBERLINE_HOST_FOLDERS
namespace Amberline.Platform.Folders;
#else
namespace Amberline.AirCompanion;
#endif

/// <summary>
/// The pinned identity of the stock 1.0.0.126 files the companion runs: the game executable (the value native/exehost/exehost.c checks)
/// and the AIR client's LolClient.exe. Shared by the bring-your-own mode and the archive installer. This file names no download, so it
/// stays in a build without <c>ClientFilesInstaller.cs</c>.
/// </summary>
internal static class RetailClients
{
    /// <summary>SHA-256 of the stock <c>League of Legends.exe</c> 1.0.0.126 (8,363,008 bytes).</summary>
    internal const string GameExecutableSha256 = "AD8FFDFA9620898F4BFE8565A56AA0C2263C107DE152F31B7B007CAE3F631539";

    /// <summary>SHA-256 of the stock AIR client's <c>LolClient.exe</c> (Adobe's launcher under the client's name).</summary>
    internal const string LolClientSha256 = "193ED9D875838EC276A7AE6C667DFCC434027D8775E132DA361CBD0CDDFFED30";

    /// <summary>Why <paramref name="folder"/> is not the stock game folder (its League of Legends.exe and DATA), or null when it is.</summary>
    internal static string? GameProblem(string folder, string expectedSha256 = GameExecutableSha256)
    {
        var executable = Path.Combine(folder, "League of Legends.exe");
        if (!File.Exists(executable)) return "there is no game executable in it";
        if (!Directory.Exists(Path.Combine(folder, "DATA"))) return "there is no DATA folder beside the game executable";
        return Sha256(executable) == expectedSha256 ? null : "The game executable is not the 1.0.0.126 game (its SHA-256 differs)";
    }

    /// <summary>Why <paramref name="folder"/> is not the complete stock AIR 1.0.0.126 folder (the layout, then LolClient.exe), or null when it is.</summary>
    internal static string? AirProblem(string folder, string expectedSha256 = LolClientSha256)
    {
        var problems = AirSourceLayout.Problems(folder);
        if (problems.Count > 0) return "it is not the complete AIR client (missing or damaged: " + string.Join(", ", problems).Replace("LolClient.swf", "the AIR client's .swf file").Replace("LolClient.exe", "the AIR client's executable") + ")";
        return Sha256(Path.Combine(folder, "LolClient.exe")) == expectedSha256 ? null : "The AIR client's executable is not the 1.0.0.126 AIR client (its SHA-256 differs)";
    }

    internal static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
