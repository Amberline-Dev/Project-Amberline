using Amberline.Data;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: the operator's content folder (owner decision, 1 November 2026). What the stock AIR client shipped is read from the
/// player's own files; website-era content that never shipped in the client (the 2011 Home and Journal pages with their images and
/// news, the store's prices and sale calendar) is operator configuration: <c>home/</c> and <c>store/</c> of this folder supply it,
/// and without them the Platform serves neutral built-in defaults. The folder is <c>AMBERLINE_PLATFORM_CONTENT</c> when set, else
/// <c>content</c> in the Platform's data folder <see cref="DataHome"/>. Files and formats: docs/air-platform.md, "Operator content".
/// </summary>
public static class PlatformContent
{
    /// <summary>The Platform's data folder: <c>AMBERLINE_PLATFORM_HOME</c>, else <c>platform-data</c> beside the executable.</summary>
    public static string DataHome { get; } = Path.GetFullPath(Environment.GetEnvironmentVariable("AMBERLINE_PLATFORM_HOME") ??
        Path.Combine(AppContext.BaseDirectory, "platform-data"));

    public static string Root { get; } = CaseInsensitivePaths.Resolve(Environment.GetEnvironmentVariable("AMBERLINE_PLATFORM_CONTENT") is { Length: > 0 } root
        ? Path.GetFullPath(root) : Path.Combine(DataHome, "content"));

    /// <summary>The folder of the Home and Journal pages and their images.</summary>
    public static string Home => Locate("home");

    /// <summary>Whether the operator supplies the file (a path relative to <see cref="Root"/>, such as <c>store/prices.json</c>).</summary>
    public static bool Has(string relative) => File.Exists(Locate(relative));

    /// <summary>The text of an operator file, or null when the operator supplies none.</summary>
    public static string? Read(string relative) => Has(relative) ? File.ReadAllText(Locate(relative)) : null;

    private static string Locate(string relative) => CaseInsensitivePaths.Resolve(Path.Combine(Root, relative));
}
