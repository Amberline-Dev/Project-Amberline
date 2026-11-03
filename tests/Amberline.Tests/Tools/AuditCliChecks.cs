using ToolsProgram = Amberline.Tools.Program;
using Amberline.Data;

namespace Amberline.Tests;

public static class AuditCliChecks
{
    [Check("tools.audit.invalid-data-root")]
    public static void AuditsRejectMissingAndEmptyData()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"amberline-missing-{Guid.NewGuid():N}");
        foreach (var kind in new[] { "champions", "items", "luaobj" })
            TestAssert.Equal(2, ToolsProgram.Main(["audit", kind, "--data-root", missing]),
                $"audit {kind} must reject a missing data root");

        var empty = Path.Combine(Path.GetTempPath(), $"amberline-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(empty, "DATA", "Items"));
        Directory.CreateDirectory(Path.Combine(empty, "DATA", "Characters"));
        Directory.CreateDirectory(Path.Combine(empty, "DATA", "Spells"));
        try
        {
            foreach (var kind in new[] { "champions", "items", "luaobj" })
                TestAssert.Equal(2, ToolsProgram.Main(["audit", kind, "--data-root", empty]),
                    $"audit {kind} must reject an empty data corpus");
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Check("tools.audit.rejects-corrupt-lua-source")]
    public static void ItemAuditRejectsCorruptPresentLuaSource()
    {
        var source = GameDataPaths.Default.Data("Items/2003.inibin");
        TestAssert.True(File.Exists(source), $"shipped item fixture is missing: {source}");
        var root = Path.Combine(Path.GetTempPath(), $"amberline-corrupt-item-{Guid.NewGuid():N}");
        var items = Path.Combine(root, "DATA", "Items");
        Directory.CreateDirectory(items);
        File.Copy(source, Path.Combine(items, "2003.inibin"));
        File.WriteAllBytes(Path.Combine(items, "2003.luaobj"), [0x4C, 0x55, 0x41, 0x00]);
        try
        {
            var exit = ToolsProgram.Main(["audit", "items", "--data-root", root]);
            TestAssert.Equal(2, exit,
                "a present malformed item Lua object must fail the audit instead of becoming empty source data");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
