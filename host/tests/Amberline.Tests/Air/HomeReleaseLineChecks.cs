using System.Reflection;
using System.Text.Json;
using Amberline.AirCompanion;
using Amberline.Host;
using Amberline.Platform;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 810: a home release of this patch line carries the line's own ports and patch facts, end to end. The expected values are read
/// from this line's patch.json, and the other line's patch.json must not agree with them. Nothing is started or bound: the check reads the
/// defaults and constants the host and the companion compile in, and the platform.json a new home host writes (the --init record,
/// Platform Program.cs) serialized in memory. The release's README and BUILD.json are checked by <c>server.host-release-layout</c>.
/// </summary>
public static class HomeReleaseLineChecks
{
    [Check("server.home-release-carries-the-line")]
    public static void HomeReleaseCarriesTheLine()
    {
        var patch = WorkspaceFiles.Patch;
        var own = Facts.Read(Path.Combine(WorkspaceFiles.PatchFolder, "patch.json"));
        var sibling = Facts.Read(Path.Combine(Path.GetDirectoryName(WorkspaceFiles.PatchFolder)!, patch == "132" ? "126" : "132", "patch.json"));
        var problems = new List<string>();
        void Expect(object expected, object? actual, string what)
        {
            if (!Equals(expected, actual)) problems.Add($"{what} is {actual}, expected {expected}");
        }

        // The facts build-server.ps1 and tools/guide.ps1 read from patch.json.
        if (!own.Label.StartsWith($"1.0.0.{patch} (", StringComparison.Ordinal)) problems.Add($"patch.json label is {own.Label}");
        Expect($"patch-1.0.0.{patch}-client-bundle.zip", own.ClientZip, "patch.json clientZip");

        // The Platform's constants and the defaults the host compiles in.
        Expect(own.Web, PlatformLine.HttpPort, "PlatformLine.HttpPort");
        Expect(own.Remote, PlatformLine.RemotePort, "PlatformLine.RemotePort");
        Expect(own.Rtmp, PlatformLine.RtmpPort, "PlatformLine.RtmpPort");
        Expect(own.Chat, PlatformLine.XmppPort, "PlatformLine.XmppPort");
        Expect(own.GameFirst, PlatformLine.FirstGamePort, "PlatformLine.FirstGamePort");
        Expect("patch-" + patch, PlatformLine.PatchName, "PlatformLine.PatchName");
        var settings = typeof(PlatformSettings).GetConstructors().Single();
        Expect(own.Web, Default(settings, "HttpPort"), "PlatformSettings' HttpPort default");
        Expect(own.Remote, Default(settings, "RemotePort"), "PlatformSettings' RemotePort default");
        Expect(own.Rtmp, Default(settings, "RtmpPort"), "PlatformSettings' RtmpPort default");
        Expect(own.Chat, Default(settings, "XmppPort"), "PlatformSettings' XmppPort default");
        Expect(own.GameFirst, Default(settings, "FirstGamePort"), "PlatformSettings' FirstGamePort default");
        Expect(own.Web, HostMode.DefaultPort, "HostMode.DefaultPort");
        Expect(own.Web, Default(typeof(PlatformServices).GetConstructors().Single(), "httpPort"), "PlatformServices' httpPort default");
        var map = typeof(AirRemoteTunnel).GetMethods(BindingFlags.Public | BindingFlags.Static).Single(method => method.Name == "Map");
        Expect(own.Rtmp, Default(map, "rtmpPort"), "AirRemoteTunnel.Map's rtmpPort default");
        Expect(own.Chat, Default(map, "chatPort"), "AirRemoteTunnel.Map's chatPort default");
        var xmpp = typeof(AirXmppHost).GetConstructors().Single(ctor => ctor.GetParameters().Any(parameter => parameter.Name == "port"));
        Expect(own.Chat, Default(xmpp, "port"), "AirXmppHost's port default");

        // The friends' companion block (CompanionLine): the store origin and the friend's AIR copy name the same ports.
        Expect(own.Web, CompanionLine.PlatformHttpPort, "CompanionLine.PlatformHttpPort");
        Expect(own.Rtmp, CompanionLine.RtmpPort, "CompanionLine.RtmpPort");
        Expect(own.Chat, CompanionLine.ChatPort, "CompanionLine.ChatPort");
        Expect("http://127.0.0.1:" + own.Web, CompanionLine.LocalPlatform, "CompanionLine.LocalPlatform");

        // Batch 828: the line's home install (patch.json "home", which tools/guide.ps1 reads): its name and marker, and what the code derives from them.
        Expect(own.HomeName, FolderMove.HostName, "FolderMove.HostName");
        Expect(own.Marker, FolderMove.InstallMarker, "FolderMove.InstallMarker");
        Expect(@"C:\" + own.HomeName, HostInstall.DefaultFolder, "HostInstall.DefaultFolder");
        Expect(own.HomeName + ".lnk", HostInstall.ShortcutName, "HostInstall.ShortcutName");
        // Batch 867: the name the host's pages give the product (the wordmark, the titles, the headings), as its icon is named.
        Expect(own.HomeName, HostPages.Product, "HostPages.Product");
        Expect(own.HomeName + " sign-in", HostFirewall.SignInRule, "HostFirewall.SignInRule");
        Expect(own.HomeName + " games", HostFirewall.GamesRule, "HostFirewall.GamesRule");
        Expect(Path.Combine("local", own.HomeName, "install.json"), FolderMove.PointerPath("local"), "FolderMove.PointerPath");
        if (own.HomeName == sibling.HomeName) problems.Add($"the {patch} home install name {own.HomeName} is the other line's");
        if (own.Marker == sibling.Marker) problems.Add($"the {patch} install marker {own.Marker} is the other line's");
        // Batch 829: the friend's player install and the release take the same name (the player folder is "<home> Player", the zip
        // "<home with hyphens>-<v>-win-x64.zip"; batch 917: the players' app "<home with hyphens>-Player-<v>.exe").
        Expect(Path.Combine("local", own.HomeName + " Player"), FolderMove.PlayerFolder("local"), "FolderMove.PlayerFolder");
        Expect(own.HomeName + " Player", PlatformBootstrap.PlayerFolderName, "PlatformBootstrap.PlayerFolderName (the player app's folder)");
        Expect(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + own.HomeName, PlayerUninstall.AppsKey, "PlayerUninstall.AppsKey");
        Expect(own.HomeName.Replace(' ', '-') + "-1.2.3-win-x64.zip", HostUpdates.ZipName(new(1, 2, 3)), "HostUpdates.ZipName");
        // The player folder from before batch 647 (Amberline\player) is the 1.0.0.126 line's alone; 132's lies inside its own player folder.
        if (patch != "126" && !FolderMove.Inside(FolderMove.OldPlayerFolder("local"), FolderMove.PlayerFolder("local")))
            problems.Add($"the {patch} player folder from before batch 647 ({FolderMove.OldPlayerFolder("local")}) is outside its own player folder");

        // The platform.json a new home host writes: the --init record (Platform Program.cs), with the ports it stores explicitly.
        using (var written = JsonDocument.Parse(JsonSerializer.Serialize(new PlatformSettings("data", "server"), HostFiles.Json)))
        {
            var root = written.RootElement;
            Expect(own.Web, root.GetProperty("httpPort").GetInt32(), "a new platform.json's httpPort");
            Expect(own.Remote, root.GetProperty("remotePort").GetInt32(), "a new platform.json's remotePort");
            Expect(own.Rtmp, root.GetProperty("rtmpPort").GetInt32(), "a new platform.json's rtmpPort");
            Expect(own.Chat, root.GetProperty("xmppPort").GetInt32(), "a new platform.json's xmppPort");
            Expect(own.GameFirst, root.GetProperty("firstGamePort").GetInt32(), "a new platform.json's firstGamePort");
        }

        // Not a copy of the other line: each of this line's ports differs from the other line's, but chat: the 1.52 client's main chat connection
        // always uses 5222 (ChatServiceImpl.connect gets port 0), which is the 126 line's chat port too, so both lines share it (16 November 2026).
        foreach (var (name, mine, theirs) in new[]
                 {
                     ("HTTP", own.Web, sibling.Web), ("remote", own.Remote, sibling.Remote), ("RTMP", own.Rtmp, sibling.Rtmp),
                     ("game", own.GameFirst, sibling.GameFirst),
                 })
            if (mine == theirs) problems.Add($"the {patch} {name} port {mine} is the other line's");
        TestAssert.True(problems.Count == 0, $"the {patch} home release does not carry its own line: " + string.Join("; ", problems));
    }

    private static object? Default(System.Reflection.MethodBase method, string parameter) =>
        method.GetParameters().Single(candidate => candidate.Name == parameter).DefaultValue;

    /// <summary>The ports, patch facts and home install (batch 828) of one line's patch.json.</summary>
    private sealed record Facts(string Label, string ClientZip, int Web, int Remote, int Rtmp, int Chat, int GameFirst, string HomeName, string Marker)
    {
        public static Facts Read(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var ports = root.GetProperty("ports");
            return new Facts(root.GetProperty("label").GetString()!, root.GetProperty("clientZip").GetString()!,
                ports.GetProperty("web").GetInt32(), ports.GetProperty("remote").GetInt32(), ports.GetProperty("rtmp").GetInt32(),
                ports.GetProperty("chat").GetInt32(), ports.GetProperty("gameFirst").GetInt32(),
                root.GetProperty("home").GetProperty("name").GetString()!, root.GetProperty("home").GetProperty("marker").GetString()!);
        }
    }
}
