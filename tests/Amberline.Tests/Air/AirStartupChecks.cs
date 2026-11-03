using System.Text.Json;
using Amberline.AirCompanion;

namespace Amberline.Tests.Air;

public static class AirStartupChecks
{
    [Check("server.air-desktop-health-contract")]
    public static void RequiresReadyHealthAndClientIdentity()
    {
        TestAssert.True(Amberline.AirCompanion.Program.IsReadyHealth("{\"status\":\"ready\",\"service\":\"Amberline.AirPlatform\",\"protocol\":1,\"client\":\"1.45.11_10_04_15_28\"}"));
        TestAssert.False(Amberline.AirCompanion.Program.IsReadyHealth("{\"status\":\"starting\",\"service\":\"Amberline.AirPlatform\",\"protocol\":1,\"client\":\"1.45.11_10_04_15_28\"}"));
        TestAssert.False(Amberline.AirCompanion.Program.IsReadyHealth("{\"status\":\"ready\",\"client\":\"1.45.11_10_04_15_28\"}"));
        TestAssert.False(Amberline.AirCompanion.Program.IsReadyHealth("{\"status\":\"ready\",\"service\":\"Amberline.AirPlatform\",\"protocol\":1,\"client\":\"1.45.11_10_04_14_27\"}"));
        TestAssert.False(Amberline.AirCompanion.Program.IsReadyHealth("not-json"));
    }

    [Check("server.air-desktop-stage-contract")]
    public static void ReadsHttpPortAndStockExecutableFromStagedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-startup-" + Guid.NewGuid().ToString("N"));
        var platformData = Path.Combine(root, "platform-data");
        Directory.CreateDirectory(platformData);
        var stock = Path.Combine(root, "stock", "League of Legends.exe");
        try
        {
            File.WriteAllText(Path.Combine(root, "stage.json"), "{\"StockExecutable\":\"" + stock.Replace("\\", "/") + "\",\"MaestroPort\":8403}");
            File.WriteAllText(Path.Combine(platformData, "platform.json"), "{\"httpPort\":8401}");

            var stage = Amberline.AirCompanion.Program.LoadDesktopStage(root);
            TestAssert.Equal(Path.GetFullPath(root), stage.Root);
            TestAssert.Equal(Path.GetFullPath(stock), stage.StockExecutable);
            TestAssert.Equal(8401, stage.HttpPort);
            TestAssert.Equal(8403, stage.MaestroPort);
            TestAssert.Equal(Path.Combine(root, "platform", "Amberline.Platform.exe"), stage.PlatformExecutable);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Check("server.air-desktop-remote-stage-contract")]
    public static void ReadsRemoteInvitationBesideTheStageOutsideTheAirCopy()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-remote-stage-" + Guid.NewGuid().ToString("N"));
        var connections = Path.Combine(root, "connections");
        Directory.CreateDirectory(connections);
        try
        {
            File.WriteAllText(Path.Combine(root, "stage.json"), "{\"remote\":true,\"stockExecutable\":\"C:/LoL/League of Legends.exe\",\"maestroPort\":8403}");
            TestAssert.Throws<FileNotFoundException>(() => Amberline.AirCompanion.Program.LoadDesktopStage(root));
            File.WriteAllText(Path.Combine(connections, "air-invitation.json"),
                "{\"host\":\"friend.example\",\"port\":8391,\"token\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"certificateSha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"registrationCode\":\"REG\",\"chatPort\":5222}");

            var stage = Amberline.AirCompanion.Program.LoadDesktopStage(root);
            TestAssert.Equal("friend.example", stage.RemoteInvitation!.Host);
            TestAssert.Equal(8391, stage.RemoteInvitation.Port);
            TestAssert.Equal("REG", stage.RemoteInvitation.RegistrationCode);
            TestAssert.Equal(8403, stage.MaestroPort);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Check("server.air-desktop-platform-startup-contract")]
    public static void StartsOnlyTheConfiguredHiddenPlatformWithTraceEnvironment()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-platform-startup-" + Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(root, "platform", "Amberline.Platform.exe");
        var data = Path.Combine(root, "platform-data");
        var log = Path.Combine(root, "logs", "platform.log");
        var start = Amberline.AirCompanion.Program.CreatePlatformStartInfo(root, executable, data, log);

        TestAssert.Equal(Path.GetFullPath(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"), start.FileName);
        TestAssert.Equal(Path.GetFullPath(root), start.WorkingDirectory);
        TestAssert.False(start.UseShellExecute);
        TestAssert.True(start.CreateNoWindow);
        TestAssert.False(start.RedirectStandardOutput || start.RedirectStandardError);
        TestAssert.Equal(0, start.ArgumentList.Count);
        TestAssert.True(start.Arguments.StartsWith("/d /s /c \"\"", StringComparison.Ordinal));
        TestAssert.True(start.Arguments.Contains(Path.GetFullPath(executable), StringComparison.Ordinal));
        TestAssert.True(start.Arguments.Contains(Path.GetFullPath(log), StringComparison.Ordinal));
        TestAssert.Equal(Path.GetFullPath(data), start.Environment["AMBERLINE_PLATFORM_HOME"]);
        TestAssert.Equal("1", start.Environment["AMBERLINE_PACKET_TRACE"]);
        TestAssert.Equal("1024", start.Environment["AMBERLINE_PACKET_TRACE_BUDGET_MB"]);
        TestAssert.Equal("3", start.Environment["LOG_FIDELITY_TRACE"]);
        TestAssert.Equal("1024", start.Environment["LOG_FIDELITY_BUDGET_MB"]);
        TestAssert.Equal("1", start.Environment["LOG_COLLISION"]);
        TestAssert.Equal(Path.GetFullPath(log), start.Environment["AMBERLINE_AIR_PLATFORM_LOG"]);
    }
}
