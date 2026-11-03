using Amberline.AirCompanion;

namespace Amberline.Tests.Air;

/// <summary>NOT RETAIL: the bring-your-own-clients mode (<c>--play</c>): the stock folders it accepts and the connection it stages.</summary>
public static class AirPlayerChecks
{
    [Check("server.air-player-version-refuses-wrong-exe")]
    public static void OnlyTheStockGameAndAirFoldersAreAccepted()
    {
        var root = TemporaryDirectory("league-air-player-version");
        try
        {
            var game = Path.Combine(root, "game");
            Directory.CreateDirectory(Path.Combine(game, "DATA"));
            TestAssert.True(RetailClients.GameProblem(game) is not null, "a folder without League of Legends.exe was accepted");
            var executable = Path.Combine(game, "League of Legends.exe");
            File.WriteAllBytes(executable, new byte[4096]);
            var refused = RetailClients.GameProblem(game);
            TestAssert.True(refused is not null && refused.Contains("SHA-256", StringComparison.Ordinal),
                $"another build's League of Legends.exe was accepted: {refused}");
            TestAssert.True(RetailClients.GameProblem(game, RetailClients.Sha256(executable)) is null,
                "the game check must accept the executable whose hash it is given");
            Directory.Delete(Path.Combine(game, "DATA"));
            TestAssert.True(RetailClients.GameProblem(game, RetailClients.Sha256(executable)) is not null,
                "a game folder without DATA was accepted");

            var air = Path.Combine(root, "air");
            AirStockLayoutFixture.Write(air);
            var refusedAir = RetailClients.AirProblem(air);
            TestAssert.True(refusedAir is not null && refusedAir.Contains("SHA-256", StringComparison.Ordinal),
                $"another AIR client was accepted: {refusedAir}");
            TestAssert.True(RetailClients.AirProblem(air, RetailClients.Sha256(Path.Combine(air, "LolClient.exe"))) is null,
                "the AIR check must accept LolClient.exe when its hash is the expected one");
            var incomplete = Path.Combine(root, "air-incomplete");
            AirStockLayoutFixture.Write(incomplete, missing: "LolClient.swf");
            var missing = RetailClients.AirProblem(incomplete, RetailClients.Sha256(Path.Combine(incomplete, "LolClient.exe")));
            TestAssert.True(missing is not null && missing.Contains("the AIR client's .swf file", StringComparison.Ordinal),
                $"an AIR folder without LolClient.swf was accepted: {missing}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Check("server.air-player-connection-file-round-trip")]
    public static void OperatorConnectionStagesHostPortAndPinOnly()
    {
        var root = TemporaryDirectory("league-air-player-connection");
        try
        {
            var pin = new string('A', 64);
            var operatorFile = Path.Combine(root, "server.json");
            File.WriteAllText(operatorFile, "{\"host\":\"operator.example\",\"port\":8391,\"certificateSha256\":\"" + pin +
                "\",\"token\":\"" + new string('B', 64) + "\",\"registrationCode\":\"REG\"}");
            var invitation = Amberline.AirCompanion.Program.ReadRemoteInvitation(operatorFile);
            var stage = Path.Combine(root, "stage");
            var game = Path.Combine(root, "game");
            PlayerMode.WriteStage(stage, game, Path.Combine(root, "air"), invitation);

            var desktop = Amberline.AirCompanion.Program.LoadDesktopStage(stage);
            TestAssert.Equal("operator.example", desktop.RemoteInvitation!.Host);
            TestAssert.Equal(8391, desktop.RemoteInvitation.Port);
            TestAssert.Equal(pin, desktop.RemoteInvitation.CertificateSha25664hex);
            TestAssert.Equal(string.Empty, desktop.RemoteInvitation.Token64hex, "the operator's token must not reach the stage");
            TestAssert.True(desktop.RemoteInvitation.Bearer is null, "a bearer must not reach the stage");
            TestAssert.Equal(Path.Combine(Path.GetFullPath(game), "League of Legends.exe"), desktop.StockExecutable);
            TestAssert.Equal(8393, desktop.MaestroPort);
            var staged = File.ReadAllText(Path.Combine(stage, "stage.json")) + File.ReadAllText(Path.Combine(stage, "connections", "server.json"));
            TestAssert.False(staged.Contains("token", StringComparison.OrdinalIgnoreCase) || staged.Contains("registrationCode", StringComparison.Ordinal),
                "the stage kept the operator's token or registration code");

            foreach (var malformed in new[]
            {
                "{\"host\":\"bad host\",\"certificateSha256\":\"" + pin + "\"}",
                "{\"host\":\"operator.example\",\"port\":70000,\"certificateSha256\":\"" + pin + "\"}",
                "{\"host\":\"operator.example\",\"certificateSha256\":\"short\"}",
                "{\"port\":8391,\"certificateSha256\":\"" + pin + "\"}",
            })
            {
                File.WriteAllText(operatorFile, malformed);
                TestAssert.Throws<ArgumentException>(() => Amberline.AirCompanion.Program.ReadRemoteInvitation(operatorFile), malformed);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    private static string TemporaryDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
