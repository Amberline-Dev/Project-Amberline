using System.Text;
using Amberline.AirCompanion;

namespace Amberline.Tests.Air;

public static class MaestroChecks
{
    [Check("server.air-adl-startup-contract")]
    public static void PreservedDebugLauncherReceivesDescriptorRootAndRuntime()
    {
        var source = Path.GetFullPath(Path.Combine(Amberline.Data.GameData.Default.Paths.Root, "../../../../../lol_air_client"));
        var start = Amberline.AirCompanion.Program.CreateAirStartInfo(source);
        TestAssert.Equal(source, start.WorkingDirectory);
        TestAssert.Equal(4, start.ArgumentList.Count);
        TestAssert.Equal("-runtime", start.ArgumentList[0]);
        TestAssert.Equal(source, start.ArgumentList[1]);
        TestAssert.Equal(Path.Combine(source, "META-INF", "AIR", "application.xml"), start.ArgumentList[2]);
        TestAssert.Equal(source, start.ArgumentList[3]);
        TestAssert.False(start.UseShellExecute);
        TestAssert.True(start.RedirectStandardOutput && start.RedirectStandardError);
        TestAssert.True(start.CreateNoWindow);

        var incomplete = Path.Combine(Path.GetTempPath(), "league-air-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(incomplete);
        try
        {
            File.Copy(start.FileName, Path.Combine(incomplete, "LolClient.exe"));
            TestAssert.Throws<FileNotFoundException>(() => Amberline.AirCompanion.Program.CreateAirStartInfo(incomplete));
        }
        finally { Directory.Delete(incomplete, recursive: true); }
    }

    [Check("server.air-maestro-protocol")]
    public static void UsesLittleEndianHeaderAndBoundedReads()
    {
        using var wire = new MemoryStream();
        MaestroProtocol.WriteAsync(wire, MaestroMessageType.GameClientCreate, "127.0.0.1 5119 1234567890123456 7").GetAwaiter().GetResult();
        var bytes = wire.ToArray();
        TestAssert.Equal((byte)0x10, bytes[0]);
        TestAssert.Equal((byte)0x01, bytes[4]);
        TestAssert.Equal((byte)0x00, bytes[8]);
        TestAssert.Equal((byte)0x21, bytes[12]);
        var message = MaestroProtocol.ReadAsync(new ChunkedReadStream(bytes, 3)).GetAwaiter().GetResult();
        TestAssert.True(message is not null);
        TestAssert.Equal((int)MaestroMessageType.GameClientCreate, message!.Type);
        TestAssert.Equal("127.0.0.1 5119 1234567890123456 7", message.PayloadText);
        var badVersion = new byte[MaestroMessage.HeaderSize];
        badVersion[0] = MaestroMessage.HeaderSize;
        badVersion[4] = 2;
        TestAssert.Throws<InvalidDataException>(() => MaestroProtocol.ReadAsync(new MemoryStream(badVersion)).GetAwaiter().GetResult());
    }

    [Check("server.air-maestro-launch-validation")]
    public static void ParsesCreatePayloadAndBuildsFixedStockLaunch()
    {
        var request = MaestroCreateRequest.Parse("127.0.0.1 5119 1234567890123456 42");
        TestAssert.Equal("127.0.0.1", request.ServerHost);
        TestAssert.Equal(5119, request.ServerPort);
        TestAssert.Equal(42L, request.PlayerId);
        TestAssert.Throws<FormatException>(() => MaestroCreateRequest.Parse("cmd.exe 5119 1234567890123456 42 & whoami"));
        TestAssert.Throws<FormatException>(() => MaestroCreateRequest.Parse("http://127.0.0.1 5119 1234567890123456 42"));

        var root = Path.Combine(Path.GetTempPath(), "amberline-air-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "League of Legends.exe");
        File.WriteAllBytes(executable, []);
        try
        {
            var start = StockClientLauncher.CreateStartInfo(executable, request, 8393);
            TestAssert.Equal(Path.GetFullPath(executable), start.FileName);
            TestAssert.Equal(4, start.ArgumentList.Count);
            TestAssert.Equal("8393", start.ArgumentList[0]);
            TestAssert.Equal(string.Empty, start.ArgumentList[1]);
            TestAssert.Equal(string.Empty, start.ArgumentList[2]);
            TestAssert.Equal(request.ToGameArgument(), start.ArgumentList[3]);
            TestAssert.Throws<ArgumentException>(() => StockClientLauncher.CreateStartInfo(Path.Combine(root, "cmd.exe"), request));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("server.air-overlay-preparation")]
    public static void CopiesWithoutTouchingSourceAndWritesCrLfOverlay()
    {
        var root = Path.Combine(Path.GetTempPath(), "amberline-air-overlay-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "copy");
        Directory.CreateDirectory(source);
        var sourceProperties = "# keep\r\nlocale=en_US\r\nstartChat=true\r\n";
        File.WriteAllText(Path.Combine(source, "lol.properties"), sourceProperties, new UTF8Encoding(false));
        var localeSourceProperties = "host=public.example\r\nstartMaestro=false\r\n";
        File.WriteAllText(Path.Combine(source, "locale.properties"), localeSourceProperties, new UTF8Encoding(false));
        AirStockLayoutFixture.Write(source);
        using (var marked = File.Open(Path.Combine(source, "LolClient.swf"), FileMode.Open, FileAccess.Write))
            marked.Write("immutable"u8);
        Directory.CreateDirectory(Path.Combine(source, "logs"));
        File.WriteAllText(Path.Combine(source, "logs", "operator.log"), "private diagnostics");
        try
        {
            AirOverlay.Prepare(new AirOverlayOptions(source, destination, "http://127.0.0.1:8390"));
            TestAssert.False(Directory.Exists(Path.Combine(destination, "logs")), "new packages exclude operator logs");
            TestAssert.True(File.Exists(Path.Combine(source, "logs", "operator.log")), "source diagnostics remain intact");
            TestAssert.Equal(sourceProperties, File.ReadAllText(Path.Combine(source, "lol.properties")));
            TestAssert.Equal(localeSourceProperties, File.ReadAllText(Path.Combine(source, "locale.properties")));
            var overlay = File.ReadAllText(Path.Combine(destination, "lol.properties"));
            TestAssert.True(overlay.Contains("host=127.0.0.1\r\n", StringComparison.Ordinal));
            TestAssert.True(overlay.Contains("host_randomize=false\r\n", StringComparison.Ordinal));
            TestAssert.True(overlay.Contains("lq_uri=http://127.0.0.1:8390/login-queue/rest/queue\r\n", StringComparison.Ordinal));
            TestAssert.True(overlay.Contains("lq_uri_randomize=false\r\n", StringComparison.Ordinal));
            TestAssert.True(overlay.Contains("channel_protocol=rtmp\r\n", StringComparison.Ordinal));
            TestAssert.True(overlay.Contains("remoting_port=2099\r\n", StringComparison.Ordinal));
            TestAssert.True(overlay.Contains("getClientIpURL=http://127.0.0.1:8390/ip\r\n", StringComparison.Ordinal));
            TestAssert.True(overlay.Contains("rssFeedJoJURL=http://127.0.0.1:8390/rss/joj.xml\r\n", StringComparison.Ordinal));
            TestAssert.True(overlay.Contains("rssFeedStoreURL=http://127.0.0.1:8390/rss/store.xml\r\n", StringComparison.Ordinal));
            TestAssert.True(overlay.Contains("rssStatusURLs=http://127.0.0.1:8390/status/\r\n", StringComparison.Ordinal));
            TestAssert.True(overlay.Contains("storyPageURL=http://127.0.0.1:8390/story\r\n", StringComparison.Ordinal));
            TestAssert.True(overlay.Contains("lobbyLandingURL=http://127.0.0.1:8390/landing\r\n", StringComparison.Ordinal));
            TestAssert.True(overlay.Contains("startChat=true\r\n", StringComparison.Ordinal));
            TestAssert.True(overlay.Contains("xmpp_server_url=127.0.0.1\r\n", StringComparison.Ordinal));
            TestAssert.True(overlay.Contains("xmpp_use_ssl=false\r\n", StringComparison.Ordinal));
            TestAssert.True(overlay.Contains("startMaestro=true\r\n", StringComparison.Ordinal));
            TestAssert.True(overlay.Contains("maestro_port=8393\r\n", StringComparison.Ordinal));
            TestAssert.False(overlay.Replace("\r\n", "", StringComparison.Ordinal).Contains('\n'));
            TestAssert.True(File.ReadAllBytes(Path.Combine(destination, "LolClient.swf")).AsSpan()
                .SequenceEqual(File.ReadAllBytes(Path.Combine(source, "LolClient.swf"))), "LolClient.swf is copied unchanged");
            var localeOverlay = File.ReadAllText(Path.Combine(destination, "locale.properties"));
            TestAssert.True(localeOverlay.Contains("host=127.0.0.1\r\n", StringComparison.Ordinal));
            TestAssert.True(localeOverlay.Contains("startMaestro=true\r\n", StringComparison.Ordinal));
            TestAssert.False(localeOverlay.Contains("public.example", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("server.air-overlay-story-route-migration")]
    public static void MigratesOnlyKnownGeneratedStoryRoutes()
    {
        var root = Path.Combine(Path.GetTempPath(), "amberline-air-story-migration-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        var lolPath = Path.Combine(source, "lol.properties");
        var localePath = Path.Combine(source, "locale.properties");
        File.WriteAllText(lolPath, "storyPageURL=http://127.0.0.1:8390/landing\r\nlobbyLandingURL=http://127.0.0.1:8390/landing\nkeep=value\r\nstoryPageURL=http://127.0.0.1:8390/landing?operator=1\r\nstoryPageURL=https://127.0.0.1:8390/landing\r\nstoryPageURL=http://127.0.0.1:8390/custom\r\n", new UTF8Encoding(false));
        var localeText = "storyPageURL=http://127.0.0.1:8390/landing\nother=http://example.invalid/landing\r\n";
        File.WriteAllBytes(localePath, [0xEF, 0xBB, 0xBF, .. new UTF8Encoding(false).GetBytes(localeText)]);
        var originalLol = File.ReadAllBytes(lolPath);
        var originalLocale = File.ReadAllBytes(localePath);
        try
        {
            TestAssert.Equal(2, AirOverlay.MigrateStoryPageUrl(source));
            var lol = File.ReadAllText(lolPath);
            var locale = File.ReadAllText(localePath);
            TestAssert.True(lol.Contains("storyPageURL=http://127.0.0.1:8390/story\r\n", StringComparison.Ordinal));
            TestAssert.True(lol.Contains("lobbyLandingURL=http://127.0.0.1:8390/landing\n", StringComparison.Ordinal));
            TestAssert.True(lol.Contains("keep=value\r\n", StringComparison.Ordinal));
            TestAssert.True(lol.Contains("storyPageURL=http://127.0.0.1:8390/landing?operator=1\r\n", StringComparison.Ordinal));
            TestAssert.True(lol.Contains("storyPageURL=https://127.0.0.1:8390/landing\r\n", StringComparison.Ordinal));
            TestAssert.True(lol.Contains("storyPageURL=http://127.0.0.1:8390/custom\r\n", StringComparison.Ordinal));
            TestAssert.True(locale.Contains("storyPageURL=http://127.0.0.1:8390/story\n", StringComparison.Ordinal));
            TestAssert.True(locale.Contains("other=http://example.invalid/landing\r\n", StringComparison.Ordinal));
            var lolBackup = Directory.GetFiles(source, "lol.properties.story-landing-*.bak").Single();
            var localeBackup = Directory.GetFiles(source, "locale.properties.story-landing-*.bak").Single();
            TestAssert.Equal(originalLol, File.ReadAllBytes(lolBackup));
            TestAssert.Equal(originalLocale, File.ReadAllBytes(localeBackup));
            TestAssert.True(File.ReadAllBytes(localePath).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
            TestAssert.Equal(0, AirOverlay.MigrateStoryPageUrl(source));
            TestAssert.Equal(1, Directory.GetFiles(source, "lol.properties.story-landing-*.bak").Length);
            TestAssert.Equal(1, Directory.GetFiles(source, "locale.properties.story-landing-*.bak").Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("server.air-overlay-local-platform-only")]
    public static void RejectsRemoteOrNonOriginPlatformUrls()
    {
        TestAssert.Throws<ArgumentException>(() => AirOverlay.ParsePlatformUrl("https://127.0.0.1:8390"));
        TestAssert.Throws<ArgumentException>(() => AirOverlay.ParsePlatformUrl("http://example.invalid:8390"));
        TestAssert.Throws<ArgumentException>(() => AirOverlay.ParsePlatformUrl("http://127.0.0.1:8390/private"));
        TestAssert.Throws<ArgumentException>(() => AirOverlay.ParsePlatformUrl("http://127.0.0.1:8390/?token=secret"));
        TestAssert.Equal("http://127.0.0.1:8390/", AirOverlay.ParsePlatformUrl("http://127.0.0.1:8390").AbsoluteUri);
    }

    private sealed class ChunkedReadStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(chunkSize, buffer.Length)]);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            base.ReadAsync(buffer, offset, Math.Min(chunkSize, count), cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(chunkSize, buffer.Length)], cancellationToken);
    }
}
