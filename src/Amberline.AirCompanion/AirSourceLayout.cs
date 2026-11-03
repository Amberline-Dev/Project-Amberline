// Linked as source into the Platform as well (see RetailClients.cs).
#if AMBERLINE_HOST_FOLDERS
namespace Amberline.Platform.Folders;
#else
namespace Amberline.AirCompanion;
#endif

/// <summary>
/// The stock 1.0.0.126 AIR layout a source must hold before it is copied. Mirrors Get-AirSourceProblems in
/// tools/air-client-bootstrap.cmd (same entries, same wording); keep both in step (server.air-source-layout-refusal).
/// Sizes are the stock files of the reference lol_air_client, so a truncated download is refused before any copy exists.
/// </summary>
public static class AirSourceLayout
{
    private static readonly (string Name, long Size)[] Files =
    [
        ("LolClient.exe", 144384),
        ("LolClient.swf", 6944733),
        ("META-INF\\AIR\\application.xml", 5115),
        ("mimetype", 59),
        ("Adobe AIR\\Versions\\1.0\\Adobe AIR.dll", 10887040),
        ("css\\fonts.swf", 180286),
        ("mod_lgn.dat", 4598750),
        ("mod_man.dat", 14783133),
        ("mod_ser.dat", 562446),
    ];

    private static readonly string[] Folders =
    [
        "assets\\data",
        "assets\\help",
        "assets\\htmlTemplates",
        "assets\\images",
        "assets\\locale",
        "assets\\sounds",
        "assets\\storeImages",
        "assets\\swfs",
    ];

    /// <summary>What is missing or damaged in an AIR folder; empty when the stock layout is complete.</summary>
    public static IReadOnlyList<string> Problems(string root)
    {
        var problems = new List<string>();
        if (!Directory.Exists(root))
        {
            problems.Add("the AIR folder");
            return problems;
        }
        foreach (var (name, size) in Files)
        {
            var file = Path.Combine(root, name);
            if (!File.Exists(file)) problems.Add(name);
            else if (new FileInfo(file).Length != size) problems.Add(name + " (damaged)");
        }
        foreach (var folder in Folders)
        {
            var directory = Path.Combine(root, folder);
            if (!Directory.Exists(directory) || !Directory.EnumerateFileSystemEntries(directory).Any()) problems.Add(folder + "\\");
        }
        return problems;
    }
}
