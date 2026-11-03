using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Amberline.Tools.Diagnostics;
using ToolsProgram = Amberline.Tools.Program;

namespace Amberline.Tests;

public static class ToolsChecks
{


    private static string Fixture(string name) => Path.Combine(Root(), "emu", "tests", "Amberline.Tests", "Fixtures", name);
    private static string StockExe() => Path.Combine(Root(), "live", "projects", "lol_game_client", "releases", "0.0.0.51", "League of Legends.exe");

    private static string Root()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "emu"))) current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("workspace root not found");
    }
}
