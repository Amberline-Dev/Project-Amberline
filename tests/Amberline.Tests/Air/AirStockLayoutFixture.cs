namespace Amberline.Tests.Air;

/// <summary>
/// Test oracle for a stock 1.0.0.126 AIR folder, taken from the reference <c>lol_air_client</c> (file sizes match
/// docs/research/2026-09-21-air126-platform.md). The friend launcher and the companion must agree with it.
/// Files are sized with SetLength, so the fixture costs no real disk writes.
/// </summary>
public static class AirStockLayoutFixture
{
    public static readonly (string Name, long Size)[] Files =
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

    public static readonly string[] Folders =
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

    /// <summary>
    /// Writes the stock layout under <paramref name="root"/>. <paramref name="missing"/> drops one file or folder,
    /// <paramref name="truncated"/> shortens one file by a byte, and <paramref name="emptied"/> keeps a folder with no file.
    /// </summary>
    public static void Write(string root, string? missing = null, string? truncated = null, string? emptied = null)
    {
        foreach (var (name, size) in Files)
        {
            if (name == missing) continue;
            var file = Path.Combine(root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var stream = File.Create(file);
            stream.SetLength(name == truncated ? size - 1 : size);
        }
        foreach (var folder in Folders)
        {
            if (folder == missing) continue;
            var directory = Path.Combine(root, folder);
            Directory.CreateDirectory(directory);
            if (folder != emptied) File.WriteAllBytes(Path.Combine(directory, "stock.bin"), [0]);
        }
    }
}
