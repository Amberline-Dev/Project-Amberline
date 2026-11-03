using System.Text.Json;
using Amberline.Data;
using Amberline.Tools.Audits;
using Amberline.Tools.Diagnostics;

namespace Amberline.Tools;

/// <summary>Offline tools: the headless bot match and the audits of the player's own game data.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { PrintHelp(); return 0; }
            return args[0].ToLowerInvariant() switch
            {
                "match" => HeadlessMatch.RunCli(args.Skip(1).ToArray()),
                "triage" => TriageReport.Run(args.Skip(1).ToArray()),
                "audit" => Audit(args.Skip(1).ToArray()),
                _ => Fail($"unknown subcommand '{args[0]}'; use 'help'"),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or
            LuaFormatException or InibinException or ArgumentException or FormatException or OverflowException or
            KeyNotFoundException or IndexOutOfRangeException or NotSupportedException or InvalidOperationException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static int Audit(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("audit requires champions, items or luaobj");
        var kind = args[0].ToLowerInvariant() switch
        {
            "champions" or "champion-roster" => "champions",
            "items" or "item-support" => "items",
            "luaobj" or "lua" => "luaobj",
            _ => throw new ArgumentException($"unknown audit '{args[0]}'; use champions, items or luaobj"),
        };
        var options = args.Skip(1).ToArray();
        ValidateFlags(options, "--data-root", "--out");
        var rootText = Option(options, "--data-root");
        var paths = string.IsNullOrWhiteSpace(rootText) ? GameDataPaths.Default : new GameDataPaths(rootText);
        RequireAuditData(kind, paths);
        var json = kind switch
        {
            "champions" => ChampionRosterAudit.Inventory(paths).ToJson(),
            "items" => ItemSupportAudit.Audit(paths).ToJson(),
            _ => LuaObjAudit.Audit(paths).ToJson(),
        };
        var output = Option(options, "--out");
        if (output is not null)
        {
            var full = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, json + Environment.NewLine);
        }
        else Console.WriteLine(json);

        using var document = JsonDocument.Parse(json);
        var failures = kind switch
        {
            "champions" => document.RootElement.GetProperty("CharacterScriptErrors").GetInt32() +
                document.RootElement.GetProperty("SpellScriptErrors").GetInt32(),
            "items" => document.RootElement.GetProperty("ModifierGaps").GetArrayLength() +
                document.RootElement.GetProperty("BehaviorGaps").GetArrayLength() +
                document.RootElement.GetProperty("ActiveGaps").GetArrayLength(),
            _ => document.RootElement.GetProperty("Divergences").GetArrayLength(),
        };
        return failures == 0 ? 0 : 1;
    }

    private static void RequireAuditData(string kind, GameDataPaths paths)
    {
        if (!Directory.Exists(paths.Root)) throw new ArgumentException($"game-data root does not exist: {paths.Root}");
        var dataRoot = Path.Combine(paths.Root, "DATA");
        if (!Directory.Exists(dataRoot)) throw new ArgumentException($"game-data DATA directory does not exist: {dataRoot}");
        switch (kind)
        {
            case "champions":
                if (!Directory.Exists(paths.Data("Characters")) || !Directory.EnumerateDirectories(paths.Data("Characters")).Any())
                    throw new ArgumentException("game-data DATA/Characters is missing or empty");
                break;
            case "items":
                if (!Directory.Exists(paths.Data("Items")) || !Directory.EnumerateFiles(paths.Data("Items"), "*.inibin").Any())
                    throw new ArgumentException("game-data DATA/Items is missing or empty");
                break;
            case "luaobj":
                var levelScript = Path.Combine(paths.Root, "LEVELS", "Map1", "Scripts", "LevelScript.luaobj");
                if (!Directory.Exists(paths.Data("Spells")) || !File.Exists(levelScript))
                    throw new ArgumentException("game-data Lua audit corpus is missing or empty");
                break;
        }
    }

    private static string? Option(string[] args, string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

    private static int Fail(string message) { Console.Error.WriteLine(message); return 2; }

    private static void ValidateFlags(string[] args, params string[] allowed)
    {
        var set = allowed.ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("-", StringComparison.Ordinal)) continue;
            if (!set.Contains(args[i])) throw new ArgumentException($"unknown option '{args[i]}'");
            if (i + 1 >= args.Length || args[i + 1].StartsWith("-", StringComparison.Ordinal)) throw new ArgumentException($"{args[i]} requires a value");
            i++;
        }
    }

    private static void PrintHelp() => Console.WriteLine(string.Join(Environment.NewLine, new[]
    {
        "Amberline.Tools (offline)",
        "  audit champions|items|luaobj|data [--data-root path] [--out report.json]",
        "  match --seed N [--minutes cap=60] [--out dir] [--speed max] [--roster 10|6|3 champions] [--players 10|6] [--map-id 1|2|3|4|8]  (headless 10-bot, 3v3 or tutorial match; exit 0 natural, 2 cap, 3 crash)",
        "  triage game-log-dir [--out report.md] [--window 30]",
        "  triage --compare dirA dirB",
    }));
}
