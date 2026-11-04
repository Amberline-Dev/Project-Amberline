using System.Net.NetworkInformation;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Amberline.Core;
using Amberline.Data;
using Amberline.Host;
using Amberline.Network.Protocol;
using Amberline.Server;

namespace Amberline.Tests.Network;

public static class RemoteHostChecks
{
    private static string Executable(string project)
    {
        var package = Environment.GetEnvironmentVariable("AMBERLINE_TEST_REMOTE_PACKAGE");
        return package is null
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", project, "release", project + ".exe"))
            : Path.Combine(package, project == "Amberline.Server" ? "server" : "", project + ".exe");
    }



    [Check("server.remote-host-lifecycle")]
    public static void ConcurrentGamesAndOwnership() => Run().GetAwaiter().GetResult();

    [Check("server.remote-testing-diagnostics-inheritance")]
    public static void TestingDiagnosticsReachTheGameServer() => TestingDiagnosticsAsync().GetAwaiter().GetResult();

    private static async Task TestingDiagnosticsAsync()
    {
        var settings = new Dictionary<string, string>
        {
            ["AMBERLINE_PACKET_TRACE"] = "1", ["AMBERLINE_PACKET_TRACE_BUDGET_MB"] = "1024",
            ["LOG_FIDELITY_TRACE"] = "3", ["LOG_FIDELITY_BUDGET_MB"] = "256", ["LOG_COLLISION"] = "1",
        };
        var saved = settings.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var root = Path.Combine(Path.GetTempPath(), "league-testing-host-" + Guid.NewGuid().ToString("N"));
        var occupied = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Select(endpoint => endpoint.Port).ToHashSet();
        var port = Enumerable.Range(57000, 1000).First(candidate => !occupied.Contains(candidate));
        try
        {
            foreach (var pair in settings) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            await using var manager = new GameManager(new HostSettings(GameData.Default.Paths.Root,
                Executable("Amberline.Server"), FirstGamePort: port, MaxGames: 1, BindAddress: "127.0.0.1"), root);
            var owner = new HostGuest("owner", "Owner", "");
            var game = manager.Create(owner, new CreateGameRequest(Guid.NewGuid(), [new("Tester", "Annie", Team.Order)]), local: false);
            await WaitUntil(() => manager.List(owner).Single(item => item.Id == game.Id).State == "Waiting for players");
            await manager.StopAsync(owner, game.Id);
            using var metadata = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(root, "meta.json", SearchOption.AllDirectories).Single()));
            var json = metadata.RootElement;
            TestAssert.True(json.GetProperty("packet_trace").GetBoolean());
            TestAssert.Equal(1024 * 1024 * 1024, json.GetProperty("packet_detail_budget_bytes").GetInt32());
            TestAssert.Equal(3, json.GetProperty("fidelity_trace_interval_ticks").GetInt32());
            TestAssert.Equal(256 * 1024 * 1024, json.GetProperty("fidelity_trace_budget_bytes").GetInt32());
            TestAssert.True(json.GetProperty("collision_trace").GetBoolean());
        }
        finally
        {
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    [Check("server.remote-error-state")]
    public static void ErrorStateKeepsLifecycleFailuresOnly() => ErrorStateAsync().GetAwaiter().GetResult();

    private static async Task ErrorStateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-error-state-check-" + Guid.NewGuid().ToString("N"));
        var executable = Executable("Amberline.Server");
        var occupied = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Select(p => p.Port).ToHashSet();
        var port = Enumerable.Range(55690, 500).First(p => !occupied.Contains(p) && !occupied.Contains(p + 1));
        var owner = new HostGuest("owner", "Owner", "");
        var roster = new[] { new MatchPlayer("Alice", "Annie", Team.Order) };

        await using (var healthy = new GameManager(
            new HostSettings(GameData.Default.Paths.Root, executable, FirstGamePort: port, MaxGames: 1, BindAddress: "127.0.0.1"),
            Path.Combine(root, "healthy")))
        {
            var game = healthy.Create(owner, new CreateGameRequest(Guid.NewGuid(), roster), local: false);
            await WaitUntil(() => healthy.List(owner).Single(item => item.Id == game.Id).State == "Waiting for players");
            await healthy.StopAsync(owner, game.Id);
            var stopped = healthy.List(owner).Single(item => item.Id == game.Id);
            TestAssert.Equal("Stopped", stopped.State);
            TestAssert.True(stopped.Error is null,
                "informational stderr from a normal server stop must not become RemoteGame.Error");
        }

        var missingData = Path.Combine(root, "missing-data");
        Directory.CreateDirectory(missingData);
        Guid failedId;
        string error;
        await using (var failed = new GameManager(
            new HostSettings(missingData, executable, FirstGamePort: port + 1, MaxGames: 1, BindAddress: "127.0.0.1"),
            Path.Combine(root, "failed")))
        {
            var game = failed.Create(owner, new CreateGameRequest(Guid.NewGuid(), roster), local: false);
            failedId = game.Id;
            await WaitUntil(() => failed.List(owner).Single(item => item.Id == game.Id).State == "Failed");
            error = failed.List(owner).Single(item => item.Id == game.Id).Error ?? "";
            TestAssert.True(error.Length > 0 && error.Length <= 500,
                "startup failure context must be present and bounded");
            TestAssert.False(error.StartsWith("[I", StringComparison.Ordinal),
                "informational child output must not be published as a failure");
        }

        var nonlaunchable = Path.Combine(root, "not-a-server.exe");
        File.WriteAllBytes(nonlaunchable, []);
        await using (var nonlaunchableManager = new GameManager(
            new HostSettings(missingData, nonlaunchable, FirstGamePort: port + 2, MaxGames: 1, BindAddress: "127.0.0.1"),
            Path.Combine(root, "nonlaunchable")))
        {
            var game = nonlaunchableManager.Create(owner,
                new CreateGameRequest(Guid.NewGuid(), roster), local: false);
            await WaitUntil(() => nonlaunchableManager.List(owner)
                .Single(item => item.Id == game.Id).State == "Failed");
            var startup = nonlaunchableManager.List(owner).Single(item => item.Id == game.Id);
            TestAssert.Equal("Failed", startup.State,
                "a process that cannot launch remains a public failure");
            TestAssert.True(startup.Error is { Length: > 0 and <= 500 },
                "a pre-association Process.Start failure retains bounded public context");
        }

        await using var reloaded = new GameManager(
            new HostSettings(missingData, executable, FirstGamePort: port + 1, MaxGames: 1, BindAddress: "127.0.0.1"),
            Path.Combine(root, "failed"));
        var retained = reloaded.List(owner).Single(item => item.Id == failedId);
        TestAssert.Equal("Failed", retained.State,
            "a lifecycle failure remains failed after final stop handling");
        TestAssert.Equal(error, retained.Error,
            "a meaningful failure is retained after later informational stop records");
    }

    private static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-remote-check-" + Guid.NewGuid().ToString("N"));
        var executable = Executable("Amberline.Server");
        var occupied = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Select(p => p.Port).ToHashSet();
        var port = Enumerable.Range(55190, 500).First(p => !occupied.Contains(p) && !occupied.Contains(p + 1));
        var settings = new HostSettings(GameData.Default.Paths.Root, executable, FirstGamePort: port, MaxGames: 2, BindAddress: "127.0.0.1");
        await using var manager = new GameManager(settings, root);
        var alice = new HostGuest("alice", "Alice", "");
        var bob = new HostGuest("bob", "Bob", "");
        var admin = new HostGuest("admin", "Admin", "", true);
        MatchPlayer[] roster = [new("Alice", "Annie", Team.Order), new("Bob", "Orianna", Team.Chaos)];
        var request = new CreateGameRequest(Guid.NewGuid(), roster);
        var first = manager.Create(alice, request, local: false);
        var duplicate = manager.Create(alice, request, local: false);
        TestAssert.Equal(first.Id, duplicate.Id, "retry must not start another process");
        var second = manager.Create(bob, new(Guid.NewGuid(), roster), local: false);
        TestAssert.True(first.Port != second.Port, "simultaneous games need different ports");
        TestAssert.Throws<RemoteFault>(() => manager.Create(admin, new(Guid.NewGuid(), roster), local: false));
        await WaitUntil(() => manager.List(admin).All(g => g.State == "Waiting for players"));
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners();
        foreach (var gamePort in new[] { first.Port, second.Port })
            TestAssert.True(listeners.Any(endpoint => endpoint.Port == gamePort && IPAddress.IsLoopback(endpoint.Address)) &&
                listeners.Where(endpoint => endpoint.Port == gamePort).All(endpoint => IPAddress.IsLoopback(endpoint.Address)),
                "managed check servers stay on loopback");
        var firstJoin = manager.Join(alice, first.Id, 1, local: false);
        var secondJoin = manager.Join(bob, second.Id, 1, local: false);
        TestAssert.True(firstJoin.Key != LegacyBlowfish.DefaultKeyAscii && firstJoin.Key != secondJoin.Key);
        TestAssert.Equal(firstJoin.Key, manager.Join(alice, first.Id, 1, local: false).Key, "reconnect preserves match credentials");
        TestAssert.Throws<RemoteFault>(() => manager.Join(bob, first.Id, 1, local: false));
        TestAssert.Throws<RemoteFault>(() => manager.Join(bob, first.Id, 11, local: false));
        try { await manager.StopAsync(bob, first.Id); throw new Exception("non-owner stop was accepted"); }
        catch (RemoteFault ex) { TestAssert.Equal(403, ex.Status); }
        await manager.StopAsync(alice, first.Id);
        TestAssert.Equal("Stopped", manager.List(admin).Single(g => g.Id == first.Id).State);
        TestAssert.Equal("Waiting for players", manager.List(admin).Single(g => g.Id == second.Id).State);
        await manager.StopAsync(alice, first.Id);
        TestAssert.Throws<RemoteFault>(() => manager.Join(alice, first.Id, 1, local: false));
        await WaitUntil(() => manager.List(admin).All(g => g.Id != first.Id));
        TestAssert.Equal(second.Id, manager.List(admin).Single().Id, "hiding a stopped game leaves the active game visible");
        TestAssert.Equal(first.Id, manager.Create(alice, request, local: false).Id, "hidden games still deduplicate create retries");
        await manager.StopAsync(admin, second.Id);
        await using var restarted = new GameManager(settings, root);
        TestAssert.Equal(0, restarted.List(admin).Length, "stopped games do not reappear after a host restart");
        TestAssert.Equal(2, Directory.GetFiles(root, "game.json", SearchOption.AllDirectories).Length,
            "hiding games preserves their host records");
    }

    /// <summary>
    /// Batch 684: Settings' Games at the same time reaches the running manager. A lower number ends no running game, and a new game
    /// waits until fewer run than it allows, even while a running game holds a port above the lowered range; a higher one counts at once.
    /// </summary>
    [Check("server.host-games-capacity", TimeoutSeconds = 240)]
    public static void LowerGameCountEndsNoGameAndNewGamesWait() => CapacityAsync().GetAwaiter().GetResult();

    private static async Task CapacityAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-games-capacity-check-" + Guid.NewGuid().ToString("N"));
        var occupied = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Select(p => p.Port).ToHashSet();
        var port = Enumerable.Range(56190, 500).First(p => !occupied.Contains(p) && !occupied.Contains(p + 1));
        // The system's cmd.exe stands in for the match server: it waits on its input, so each game stays Starting (a running game)
        // until it is stopped (its "stop" line, then the closed input, ends it). It binds no port and needs no ENet bridge.
        await using var manager = new GameManager(new HostSettings(GameData.Default.Paths.Root, Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            FirstGamePort: port, MaxGames: 2, BindAddress: "127.0.0.1"), root);
        var admin = new HostGuest("admin", "Admin", "", true);
        MatchPlayer[] roster = [new("Alice", "Annie", Team.Order)];
        // A guest of its own per game: one guest may create only every few seconds.
        RemoteGame Create(string guest) => manager.Create(new HostGuest(guest, guest, ""), new CreateGameRequest(Guid.NewGuid(), roster), local: false);
        void Busy(string guest, string what)
        {
            try { Create(guest); throw new Exception(what + ": a game started"); }
            catch (RemoteFault fault) { TestAssert.Equal(409, fault.Status, what); }
        }
        try
        {
            var first = Create("first");
            var second = Create("second");
            TestAssert.Equal((port, port + 1), (first.Port, second.Port), "two games, two ports");
            TestAssert.True(manager.List(admin).All(g => g.State == "Starting"), "both run");

            manager.MaxGames = 1;
            TestAssert.Equal(1, manager.MaxGames);
            TestAssert.True(manager.List(admin).All(g => g.State == "Starting"), "a lower number ends no running game");
            Busy("third", "two running of one");
            await manager.StopAsync(admin, first.Id);
            Busy("third", "the second game still runs, above the lowered range");
            await manager.StopAsync(admin, second.Id);
            var third = Create("third");
            TestAssert.Equal(port, third.Port, "a freed slot takes the lowered range's port");
            TestAssert.Equal("Stopped", manager.List(admin).Single(g => g.Id == second.Id).State, "stopped by the host, not by the lower number");

            manager.MaxGames = 2;
            TestAssert.Equal(port + 1, Create("fourth").Port, "a higher number counts at once");
            Busy("fifth", "two running of two");
            foreach (var refused in new[] { 0, 33 })
                TestAssert.Throws<ArgumentOutOfRangeException>(() => manager.MaxGames = refused, $"{refused} games are refused");
            TestAssert.Equal(2, manager.MaxGames, "a refused number changes nothing");
        }
        finally
        {
            foreach (var game in manager.List(admin).Where(g => g.CanStop)) await manager.StopAsync(admin, game.Id);
        }
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!predicate())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException("Host game readiness timed out.");
            await Task.Delay(100);
        }
    }

    [Check("server.remote-invitations-catalog-key")]
    public static void InvitationsCatalogAndMatchKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-invitation-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var invitation = HostFiles.Invite(root, "Alice", 5120);
        TestAssert.True(HostFiles.Authenticate(root, invitation.Token) is not null);
        TestAssert.True(HostFiles.Authenticate(root, new string('0', 64)) is null);
        HostFiles.Write(Path.Combine(root, "guests.json"), Array.Empty<HostGuest>());
        TestAssert.True(HostFiles.Authenticate(root, invitation.Token) is null);
        var runes = new RuneCatalog(GameData.Default.Paths);
        var copied = new RuneCatalog(runes.All);
        TestAssert.Equal(runes.Count, copied.Count);
        var masteries = new MasteryCatalog(GameData.Default.Paths);
        TestAssert.Equal(masteries.Definitions.Count, new MasteryCatalog(masteries.Definitions).Definitions.Count);
        var old = Environment.GetEnvironmentVariable("AMBERLINE_MATCH_KEY");
        try
        {
            const string key = "Abcd1234Abcd1234";
            Environment.SetEnvironmentVariable("AMBERLINE_MATCH_KEY", key);
            using var logger = ServerLogger.Open(Path.Combine(root, "logs"), new StringWriter());
            using var network = new BuiltinServerMatchFactory().Create(ServerOptions.Parse(["--solo", "--ephemeral", "--bind", "127.0.0.1"]), logger);
            var expected = new LegacyBlowfishCipher(Encoding.ASCII.GetBytes(key)).Encrypt(new byte[8]);
            TestAssert.Equal(expected, network.Runtime.Cipher.Encrypt(new byte[8]));
        }
        finally { Environment.SetEnvironmentVariable("AMBERLINE_MATCH_KEY", old); }
    }
}
