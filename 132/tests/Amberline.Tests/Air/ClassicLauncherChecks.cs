using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amberline.AirCompanion;
using Amberline.AirCompanion.Rads;
using Amberline.Host;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using RtmpSharp.IO;

namespace Amberline.Tests.Air;

/// <summary>The classic launcher start: the stock 2011 launcher run against the player tool's stand-ins.</summary>
public static class ClassicLauncherChecks
{
    /// <summary>
    /// A fake launcher on the stand-in update service's message-mode pipe (a unique pipe and mutex name, never the launcher's) gets the
    /// replies that design 2026-11-12 §4 gives, each as one message: SetLogParameters [16,0x13,id,0], the release list
    /// and UpdateProject/UpdateSolution with versions from the release folders under the root, an unknown type zero-filled with hr=0;
    /// Connected completes at the first connection; after a disconnect the service listens again.
    /// </summary>
    [Check("server.classic-launcher-kernel")]
    public static void KernelPipeReplies()
    {
        var root = Path.Combine(Path.GetTempPath(), "amberline-b760-" + Guid.NewGuid().ToString("N"));
        var pipeName = "amberline-b760-" + Guid.NewGuid().ToString("N");
        foreach (var release in new[] { @"projects\lol_air_client\releases\0.0.0.126", @"projects\lol_air_client\releases\0.0.0.7", @"projects\lol_air_client\releases\notaversion", @"solutions\lol_game_client_sln\releases\0.0.0.70", @"projects\lol_air_client_config_eune\releases\0.0.0.1" })
            Directory.CreateDirectory(Path.Combine(root, release));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            using var kernel = new RadsKernelStandIn(root, _ => { }, pipeName, pipeName + "-mutex");
            TestAssert.Throws<InvalidOperationException>(() => new RadsKernelStandIn(root, _ => { }, pipeName + "-2", pipeName + "-mutex"), "a second kernel must refuse a held mutex");
            var serving = kernel.ServeAsync(cancel.Token);
            TestAssert.True(!kernel.Connected.IsCompleted, "Connected must wait for the first client");

            static byte[] U32(uint value) => BitConverter.GetBytes(value);
            static byte[] Str(string text) => [.. U32((uint)text.Length + 1), .. Encoding.ASCII.GetBytes(text), 0];
            static string Call(NamedPipeClientStream pipe, uint id, uint type, byte[] payload)
            {
                pipe.Write([.. U32(12 + (uint)payload.Length), .. U32(id), .. U32(type), .. payload]);
                var buffer = new byte[4096];
                var n = pipe.Read(buffer, 0, buffer.Length);
                TestAssert.True(pipe.IsMessageComplete, "the reply must be one pipe message");
                return string.Join(",", Enumerable.Range(0, n / 4).Select(i => BitConverter.ToUInt32(buffer, i * 4))) + (n % 4 == 0 ? "" : $"+{n % 4} bytes");
            }

            using (var launcher = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
            {
                launcher.Connect(10_000);
                TestAssert.True(kernel.Connected.Wait(TimeSpan.FromSeconds(10)), "Connected must complete at the first connection");
                launcher.ReadMode = PipeTransmissionMode.Message;
                TestAssert.Equal("16,19,1,0", Call(launcher, 1, 0x13, U32(3)), "0x13 SetLogParameters");
                TestAssert.Equal("20,7,2,0,126", Call(launcher, 2, 0x07, Str("lol_air_client")), "0x07 UpdateProject: the highest release folder, 0.0.0.126");
                TestAssert.Equal("20,15,3,0,70", Call(launcher, 3, 0x0F, [.. Str("lol_game_client_sln"), .. Str("en_US")]), "0x0F UpdateSolution: 0.0.0.70");
                TestAssert.Equal("28,5,4,0,2,7,126", Call(launcher, 4, 0x05, Str("lol_air_client")), "0x05 release list, ascending, folders that are not a version skipped");
                TestAssert.Equal("20,7,5,0,4294967295", Call(launcher, 5, 0x07, Str("lol_air_client_config_na")), "0x07 with no release folder: 0xFFFFFFFF");
                TestAssert.Equal("32,66,6,0,0,0,0,0", Call(launcher, 6, 0x42, [1, 2, 3]), "an unknown type: zero-filled, hr=0");
            }
            using (var again = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
            {
                again.Connect(10_000);
                again.ReadMode = PipeTransmissionMode.Message;
                TestAssert.Equal("16,23,7,0", Call(again, 7, 0x17, U32(1)), "after a disconnect the kernel listens again (0x17 Repair)");
                TestAssert.Equal("20,7,8,0,1", Call(again, 8, 0x07, Str("lol_air_client_config_eune")), "0x07 UpdateProject: the region config project's 0.0.0.1");
            }
            cancel.Cancel();
            TestAssert.True(serving.Wait(TimeSpan.FromSeconds(10)), "the kernel stops when cancelled");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The accepted launcher folder (Global Constraints): the 34 files of release 0.0.0.76 and LoLLauncher.exe's fingerprint. A folder of dummy
    /// files is accepted with its own EXE's hash, refused with the version sentence by the real fingerprint, refused (not thrown) while the EXE
    /// is locked, and refused naming a deleted file; the releasemanifest is found in the folder or its parent.
    /// </summary>
    [Check("server.classic-launcher-files")]
    public static void LauncherFilesAccepted()
    {
        var temp = TempFolder();
        var folder = Path.Combine(temp, "files");
        try
        {
            TestAssert.Equal("8d8ef1689faf225c7e1a0a8478dbca80778313fe92a48a852793683b16bd22da", ClassicLauncherFiles.LauncherSha256, "the 1.0.1.19 fingerprint");
            TestAssert.Equal(
                @"LoLLauncher.exe,LoLLauncher.exe.version,launcher.maestro.dll,LoLLauncher.swf,RADS.swf,slideshow.swf,Riot_SafetyCheck.exe,en_US.tra,de_DE.tra,el_GR.tra,en_GB.tra,es_ES.tra,fr_FR.tra,pl_PL.tra,ro_RO.tra,na_region.tra,euw_region.tra,euw_region_de_DE.tra,euw_region_es_ES.tra,euw_region_fr_FR.tra,eune_region.tra,eune_region_el_GR.tra,eune_region_pl_PL.tra,eune_region_ro_RO.tra,regions.txt,shards.txt,languages.txt,xml\slideshow.xml,img\ad0.png,img\ad1.png,img\ad2.png,img\ad3.png,img\ad4.png,img\ad5.png",
                string.Join(",", ClassicLauncherFiles.Required), "the release's 34 files");
            WriteLauncherFiles(folder);
            var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, "LoLLauncher.exe"))));
            TestAssert.Equal<string?>(null, ClassicLauncherFiles.Problem(folder, sha), "every file present and the EXE's own hash: accepted");
            TestAssert.Equal<string?>(null, ClassicLauncherFiles.Problem(folder, sha.ToUpperInvariant()), "the fingerprint compares without case");
            TestAssert.Equal<string?>("The classic launcher is not version 1.0.1.19.", ClassicLauncherFiles.Problem(folder), "a dummy EXE is not the accepted launcher");
            TestAssert.Equal<string?>("The classic launcher is not version 1.0.1.19.", ClassicLauncherFiles.Problem(folder, new string('0', 64)), "another hash");
            using (new FileStream(Path.Combine(folder, "LoLLauncher.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
                TestAssert.Equal<string?>("The classic launcher's program file could not be read.", ClassicLauncherFiles.Problem(folder, sha), "a locked EXE is a problem, not a throw");
            File.Delete(Path.Combine(folder, "xml", "slideshow.xml"));
            TestAssert.Equal<string?>(@"The classic launcher folder is missing xml\slideshow.xml.", ClassicLauncherFiles.Problem(folder, sha), "a missing file is named");
            // Final review: the sentence is player-facing, so a release file whose name names the game or its publisher is named by its role.
            static bool Neutral(string? text) => text is not null && !new[] { "riot", "lol", "league" }.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
            File.Delete(Path.Combine(folder, "Riot_SafetyCheck.exe"));
            var safetyCheck = ClassicLauncherFiles.Problem(folder, sha);
            TestAssert.True(Neutral(safetyCheck) && safetyCheck!.Contains("safety-check program", StringComparison.Ordinal), $"the missing safety check, in neutral words: {safetyCheck}");
            File.Delete(Path.Combine(folder, "LoLLauncher.exe"));
            var program = ClassicLauncherFiles.Problem(folder, sha);
            TestAssert.True(Neutral(program) && program!.Contains("program file", StringComparison.Ordinal), $"the missing program, in neutral words: {program}");

            TestAssert.Equal<string?>(null, ClassicLauncherFiles.ManifestFor(folder), "no releasemanifest yet");
            File.WriteAllBytes(Path.Combine(temp, "releasemanifest"), [1]);
            TestAssert.Equal<string?>(Path.Combine(temp, "releasemanifest"), ClassicLauncherFiles.ManifestFor(folder), "the parent's releasemanifest");
            TestAssert.Equal<string?>(Path.Combine(temp, "releasemanifest"), ClassicLauncherFiles.ManifestFor(folder + @"\"), "a trailing separator still finds the parent's");
            File.WriteAllBytes(Path.Combine(folder, "releasemanifest"), [2]);
            TestAssert.Equal<string?>(Path.Combine(folder, "releasemanifest"), ClassicLauncherFiles.ManifestFor(folder), "the folder's own comes first");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>
    /// The add-only RADS layout of the design's §3 and §6 on a temporary root: the system cfgs, the launcher release (the player's manifest,
    /// deploy\ and managedfiles\), the AIR (0.0.0.132) and game junctions, the three AIR config projects (0.0.0.132) and the nine region translations; no
    /// EULA, no region.tmp and no solution or configuration manifest (batch 814). A second run writes nothing; the launcher's own cfg change persists; a
    /// changed lol.properties is copied again; a moved AIR folder re-points its junction; a path that is not ours (a needed path, an unstamped
    /// system cfg, a release folder beside ours) refuses the start and stays untouched; a root with a space and a corrupt player manifest are
    /// refused, never thrown; Remove deletes only stamped paths, never follows a junction, leaves the shared projects folder with the game's
    /// managed layout, and after a locked file keeps a stamp of what remains so the next Remove finishes.
    /// </summary>
    [Check("server.classic-launcher-layout")]
    public static void LayoutAddOnly()
    {
        var temp = TempFolder();
        TestAssert.False(temp.Contains(' '), $"this check needs a temp folder without spaces: {temp}");
        var root = Path.Combine(temp, "rads");
        var launcher = Path.Combine(temp, "launcher", "files");
        var air = Path.Combine(temp, "air");
        var game = Path.Combine(temp, "game");
        var bareRoot = Path.Combine(temp, "bare-rads");
        var log = new List<string>();
        try
        {
            WriteLauncherFiles(launcher);
            uint V(string text) => RadsLayout.ParseVersion(text);
            var versions = new Dictionary<string, string> { ["LoLLauncher.exe"] = "0.0.0.33", ["RADS.swf"] = "0.0.0.26", ["img/ad0.png"] = "0.0.0.46", ["regions.txt"] = "0.0.0.47" };
            var entries = ClassicLauncherFiles.Required.Select(p => p.Replace('\\', '/'))
                .Select(p => new RadsLayout.ManifestFile(p, V(versions.GetValueOrDefault(p, "0.0.0.76")), new byte[16], 5, 1, 1)).ToList();
            var playerManifest = Path.Combine(temp, "launcher", "releasemanifest");   // beside the files, as in the release download
            RadsLayout.WriteManifest(playerManifest, "lol_launcher", V("0.0.0.76"), entries);
            Directory.CreateDirectory(air);
            File.WriteAllText(Path.Combine(air, "LolClient.exe"), "air exe");
            File.WriteAllText(Path.Combine(air, "lol.properties"), "host=127.0.0.1\n");
            Directory.CreateDirectory(game);
            File.WriteAllText(Path.Combine(game, "League of Legends.exe"), "game exe");

            var (layout, refusal) = ClassicLauncherLayout.Ensure(new(root, launcher, air, game, 8390), log.Add);
            TestAssert.Equal<string?>(null, refusal, "a fresh root is built");
            var release = Path.Combine(root, @"projects\lol_launcher\releases\0.0.0.76");
            var deploy = Path.Combine(release, "deploy");
            TestAssert.Equal(deploy, layout!.Deploy, "the launcher runs from its release's deploy folder");
            TestAssert.Equal(Path.Combine(deploy, "LoLLauncher.exe"), layout.LauncherExecutable, "the launcher's copy");

            foreach (var (name, line) in new[] { ("launcher.cfg", "airConfigProject = lol_air_client_config_na"), ("locale.cfg", "locale = en_us"), ("system.cfg", "Region = NA"), ("user.cfg", "disableP2P = true") })
                TestAssert.Equal(line + "\r\n", File.ReadAllText(Path.Combine(root, "system", name)), $"system\\{name}");
            TestAssert.True(File.ReadAllBytes(playerManifest).SequenceEqual(File.ReadAllBytes(Path.Combine(release, "releasemanifest"))), "the player's releasemanifest is taken along");
            // The region translations hold the English text and the news lines the layout rewrote: checked by server.classic-launcher-0076-layout.
            foreach (var file in ClassicLauncherFiles.Required.Where(p => !ClassicLauncherFiles.RegionFiles.Contains(p)))
                TestAssert.True(File.ReadAllBytes(Path.Combine(launcher, CopySource(file))).SequenceEqual(File.ReadAllBytes(Path.Combine(deploy, file))), $"deploy\\{file} is a copy");
            foreach (var entry in entries.Where(e => !ClassicLauncherFiles.RegionFiles.Contains(e.Path)))
            {
                var version = $"{entry.Version >> 24}.{(entry.Version >> 16) & 255}.{(entry.Version >> 8) & 255}.{entry.Version & 255}";
                var managed = Path.Combine(root, @"projects\lol_launcher\managedfiles", version, entry.Path.Replace('/', '\\'));
                TestAssert.True(File.ReadAllBytes(Path.Combine(launcher, CopySource(entry.Path))).SequenceEqual(File.ReadAllBytes(managed)), $"managedfiles\\{version}\\{entry.Path} per the manifest");
            }
            var airLink = Path.Combine(root, @"projects\lol_air_client\releases\0.0.0.132\deploy");
            var gameLink = Path.Combine(root, @"solutions\lol_game_client_sln\releases\0.0.0.70\deploy");
            TestAssert.Equal<string?>(air, new DirectoryInfo(airLink).LinkTarget, "the AIR release is a junction to the AIR folder");
            TestAssert.Equal<string?>(game, new DirectoryInfo(gameLink).LinkTarget, "the solution release is a junction to the game folder");
            string[] regions = ["na", "euw", "eune"];
            foreach (var region in regions)
                TestAssert.Equal("host=127.0.0.1\n", File.ReadAllText(Path.Combine(root, $@"projects\lol_air_client_config_{region}\releases\0.0.0.132\deploy\lol.properties")), $"config project {region}");
            // Batch 814: no solution or configuration manifest, so the 132 game never loads the 1.0.0.126 line's archives from the shared root.
            TestAssert.False(Entries(root).Any(p => Path.GetFileName(p) is "solutionmanifest" or "configurationmanifest"), "no solution or configuration manifest");

            TestAssert.False(Entries(root).Any(p => Path.GetFileName(p).StartsWith("eula_", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).Equals("region.tmp", StringComparison.OrdinalIgnoreCase)),
                "no EULA file and no region.tmp anywhere under the root");
            var stamp = File.ReadAllLines(Path.Combine(root, "projects", "lol_launcher", ClassicLauncherLayout.StampName));
            TestAssert.True(stamp.Contains(@"J projects\lol_air_client\releases\0.0.0.132\deploy") && stamp.Contains(@"J solutions\lol_game_client_sln\releases\0.0.0.70\deploy"), "junctions are stamped with J");
            TestAssert.True(stamp.Contains(@"projects\lol_launcher\releases\0.0.0.76\deploy\LoLLauncher.exe") && stamp.Contains("system"), "every created path is stamped, relative to the root");

            var before = Times(root);
            TestAssert.Equal<string?>(null, ClassicLauncherLayout.Ensure(new(root, launcher, air, game, 8390), log.Add).Refusal, "a second run is accepted");
            TestAssert.Equal(before, Times(root), "a second run changes no file time");

            File.WriteAllText(Path.Combine(root, "system", "system.cfg"), "Region = EUW\r\n");   // the launcher's own region change
            File.WriteAllText(Path.Combine(air, "lol.properties"), "host=10.0.0.2\n");
            TestAssert.Equal<string?>(null, ClassicLauncherLayout.Ensure(new(root, launcher, air, game, 8390), log.Add).Refusal, "a third run is accepted");
            TestAssert.Equal("Region = EUW\r\n", File.ReadAllText(Path.Combine(root, "system", "system.cfg")), "a cfg the launcher changed is never rewritten");
            foreach (var region in regions)
                TestAssert.Equal("host=10.0.0.2\n", File.ReadAllText(Path.Combine(root, $@"projects\lol_air_client_config_{region}\releases\0.0.0.132\deploy\lol.properties")), $"config project {region} follows the AIR file");

            var moved = Path.Combine(temp, "air-moved");
            Directory.Move(air, moved);
            TestAssert.Equal<string?>(null, ClassicLauncherLayout.Ensure(new(root, launcher, moved, game, 8390), log.Add).Refusal, "a moved AIR folder is accepted");
            TestAssert.Equal<string?>(moved, new DirectoryInfo(airLink).LinkTarget, "the junction is re-pointed at the moved AIR folder");

            // Batch 857 review (HIGH 3): a root Amberline made is marked and locked; a link its layout did not make stops the next full pass and
            // refuses the classic start; a root that is a link is refused before anything is written.
            TestAssert.True(File.Exists(Path.Combine(root, ClassicLauncherLayout.RootMarker)) && new DirectoryInfo(root).GetAccessControl().AreAccessRulesProtected,
                "a root Amberline made is marked and locked");
            var stray = Path.Combine(root, "stray");
            HostLoginCopyChecks.Junction(stray, moved);
            File.Delete(Path.Combine(root, ClassicLauncherLayout.RootLockRecord));
            TestAssert.True(ClassicLauncherLayout.Ensure(new(root, launcher, moved, game, 8390), log.Add).Refusal is { } strayReason &&
                strayReason.Contains(stray, StringComparison.Ordinal), "a link the layout did not make refuses the classic start");
            Directory.Delete(stray);
            TestAssert.Equal<string?>(null, ClassicLauncherLayout.Ensure(new(root, launcher, moved, game, 8390), log.Add).Refusal, "without it, accepted again");
            var linkRoot = Path.Combine(temp, "link-rads");
            HostLoginCopyChecks.Junction(linkRoot, moved);
            TestAssert.True(ClassicLauncherLayout.Ensure(new(linkRoot, launcher, moved, game, 8390), log.Add).Refusal is { } linkReason &&
                linkReason.Contains("is a link to another place", StringComparison.Ordinal), "a RADS root that is a link refuses the classic start");
            Directory.Delete(linkRoot);

            // Not ours (a real old install, the spike's leftovers): a needed path, an unstamped system cfg, or a release folder beside ours (the
            // stand-in would report its higher version). Refused by name; nothing is created or changed anywhere.
            void RefusedFor(string name, string relative, bool folder)
            {
                var other = Path.Combine(temp, name);
                var path = Path.Combine(other, relative);
                Directory.CreateDirectory(folder ? path : Path.GetDirectoryName(path)!);
                File.WriteAllText(folder ? Path.Combine(path, "keep.txt") : path, "old install");
                // Batch 857 review: a root Amberline did not make is used only while it is private (as a real one in the user's profile would be).
                Amberline.AirCompanion.InstallLock.Lock(other);
                var unchanged = Times(other);
                var (refusedLayout, reason) = ClassicLauncherLayout.Ensure(new(other, launcher, moved, game, 8390), log.Add, top: temp);
                TestAssert.True(refusedLayout is null && reason is not null && reason.Contains(path, StringComparison.Ordinal), $"{relative} refuses the start by name: {reason}");
                TestAssert.Equal(unchanged, Times(other), $"{relative}: a refused start leaves the root untouched");
            }
            RefusedFor("old-rads", @"projects\lol_launcher\releases\0.0.0.76\deploy", folder: true);
            RefusedFor("cfg-rads", @"system\user.cfg", folder: false);
            RefusedFor("old-air-rads", @"projects\lol_air_client\releases\0.0.0.200", folder: true);

            var spaced = Path.Combine(temp, "my rads");
            var spacedResult = ClassicLauncherLayout.Ensure(new(spaced, launcher, moved, game, 8390), log.Add);
            TestAssert.True(spacedResult.Layout is null && spacedResult.Refusal is { } spaceReason && spaceReason.Contains(spaced, StringComparison.Ordinal), $"a root with a space is refused: {spacedResult.Refusal}");
            TestAssert.False(Directory.Exists(spaced), "a refused root is not created");

            // Without a player manifest the layout writes one: every copied file at 0.0.0.76.
            var bare = Path.Combine(temp, "bare");
            WriteLauncherFiles(bare);
            TestAssert.Equal<string?>(null, ClassicLauncherLayout.Ensure(new(bareRoot, bare, moved, game, 8390), log.Add).Refusal, "a launcher without a manifest is accepted");
            var written = RadsLayout.ReadManifest(Path.Combine(bareRoot, @"projects\lol_launcher\releases\0.0.0.76\releasemanifest"));
            TestAssert.Equal("lol_launcher", written.Project, "the written manifest's project");
            TestAssert.Equal(string.Join(",", ClassicLauncherFiles.Required.Select(p => p.Replace('\\', '/')).Order(StringComparer.OrdinalIgnoreCase)),
                string.Join(",", written.Files.Select(f => f.Path).Order(StringComparer.OrdinalIgnoreCase)), "the written manifest lists every copied file");
            TestAssert.True(written.Files.All(f => f.Version == V("0.0.0.76")), "every file at 0.0.0.76");
            TestAssert.True(File.Exists(Path.Combine(bareRoot, @"projects\lol_launcher\managedfiles\0.0.0.76\img\ad5.png")), "managedfiles at 0.0.0.76");

            // A corrupt player manifest (its file table points past its rows) is a refusal, never a throw, and changes nothing.
            uint[] corrupt = [RadsLayout.ManifestMagic, RadsLayout.ManifestVersion, 0, 0, 1, 0, 1, 0, 5, 1, 0, 1, 1];
            File.WriteAllBytes(Path.Combine(bare, "releasemanifest"), [.. corrupt.SelectMany(word => BitConverter.GetBytes(word)), 0]);
            var bareBefore = Times(bareRoot);
            var corruptResult = ClassicLauncherLayout.Ensure(new(bareRoot, bare, moved, game, 8390), log.Add);
            TestAssert.True(corruptResult.Layout is null && corruptResult.Refusal is { } corruptReason && corruptReason.Contains(nameof(IndexOutOfRangeException), StringComparison.Ordinal),
                $"a corrupt manifest is refused: {corruptResult.Refusal}");
            TestAssert.Equal(bareBefore, Times(bareRoot), "a corrupt manifest changes nothing");

            // The launcher's own writes: a log at the root (never ours) and logs inside our deploy folder (removed with it). The game's managed
            // layout, built in the background after Ensure, shares projects\ and is never ours.
            File.WriteAllText(Path.Combine(root, "rads_lol_launcher.log"), "launcher log");
            Directory.CreateDirectory(Path.Combine(deploy, "logs"));
            File.WriteAllText(Path.Combine(deploy, "logs", "launcher.log"), "launcher log");
            var gameLayout = Path.Combine(root, @"projects\lol_game_client\releases\0.0.0.51\x");
            Directory.CreateDirectory(Path.GetDirectoryName(gameLayout)!);
            File.WriteAllText(gameLayout, "the game's archives");

            // A locked file inside projects\lol_launcher: Remove reports it and keeps a stamp of exactly what remains; the next Remove finishes.
            var stampFile = Path.Combine(root, @"projects\lol_launcher", ClassicLauncherLayout.StampName);
            // Final review: a hand-edited stamp line that climbs out of our project with ".." is skipped and logged; it never passes for our
            // project (removed whole), so the game's unstamped managed layout stays.
            const string tampered = @"projects\lol_launcher\..\lol_game_client";
            File.AppendAllText(stampFile, tampered + "\r\n");
            var removeLog = new List<string>();
            var removed = new List<string>();
            using (new FileStream(Path.Combine(deploy, "LoLLauncher.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
                removed.AddRange(ClassicLauncherLayout.Remove(root, removeLog.Add));
            TestAssert.True(File.Exists(gameLayout), "a tampered stamp line never deletes the game's unstamped layout");
            TestAssert.True(removeLog.Any(line => line.Contains("skipped the stamp line " + tampered, StringComparison.Ordinal)), "the tampered line is logged");
            TestAssert.True(removeLog.Any(line => line.Contains("could not remove", StringComparison.Ordinal)), "Remove reports the locked file");
            var left = File.ReadAllLines(stampFile);
            TestAssert.True(left.Contains(@"projects\lol_launcher\releases\0.0.0.76\deploy\LoLLauncher.exe") && left.Contains(@"projects\lol_launcher"), $"the stamp still lists what remains: {string.Join(" | ", left)}");
            TestAssert.True(left.All(line => Path.Exists(Path.Combine(root, line))), $"and only what remains: {string.Join(" | ", left)}");
            removed.AddRange(ClassicLauncherLayout.Remove(root, log.Add));

            TestAssert.True(File.Exists(Path.Combine(moved, "LolClient.exe")) && File.Exists(Path.Combine(moved, "lol.properties")), "the AIR files behind the junction survive");
            TestAssert.True(File.Exists(Path.Combine(game, "League of Legends.exe")), "the game files behind the junction survive");
            string[] stays = [Path.Combine(root, "projects"), Path.Combine(root, @"projects\lol_game_client"), Path.Combine(root, @"projects\lol_game_client\releases"),
                Path.Combine(root, @"projects\lol_game_client\releases\0.0.0.51"), gameLayout, Path.Combine(root, "rads_lol_launcher.log"),
                Path.Combine(root, ClassicLauncherLayout.RootMarker), Path.Combine(root, ClassicLauncherLayout.RootLockRecord)];   // batch 857: the root mark and record stay
            TestAssert.Equal(string.Join(",", stays.Order(StringComparer.Ordinal)), string.Join(",", Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)),
                "only the game's managed layout (in the shared projects folder) and the unstamped root log stay");
            foreach (var path in new[] { airLink, gameLink, Path.Combine(root, "system"), Path.Combine(root, "solutions"), Path.Combine(root, @"projects\lol_launcher"), Path.Combine(root, @"projects\lol_air_client_config_eune") })
                TestAssert.True(removed.Contains(path), $"Remove reports {path}");
            TestAssert.Equal(0, ClassicLauncherLayout.Remove(root, log.Add).Count, "a third Remove finds nothing");
            TestAssert.Equal(0, ClassicLauncherLayout.Remove(Path.Combine(temp, "old-rads"), log.Add).Count, "Remove on a root without a stamp removes nothing");
            TestAssert.True(File.Exists(Path.Combine(temp, @"old-rads\projects\lol_launcher\releases\0.0.0.76\deploy\keep.txt")), "the foreign file stays");
        }
        finally
        {
            // Junctions go first, as links: a recursive Directory.Delete over a junction removes the link but then reports access denied here
            // (its DeleteVolumeMountPoint step), which would hide the real failure.
            foreach (var built in Directory.Exists(temp) ? Directory.GetDirectories(temp) : []) ClassicLauncherLayout.Remove(built, _ => { });
            Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>
    /// Task 9 (NOT RETAIL: removing the classic start, design §2 and §3). On a temporary install with its 2011 launcher folder and a temporary RADS root
    /// that <c>Ensure</c> builds for it: "Remove the 2011 launcher" removes <c>&lt;install&gt;\launcher</c> and every stamped path (the AIR and game junctions
    /// as links only), keeps the install's AIR and game folders and the root's foreign files, and a second call changes nothing. A link inside the launcher
    /// folder is refused before anything changes; a locked file keeps the layout, its stamp and the folder, and the next call finishes. Amberline's removal
    /// always takes the layout; its AIR, game and 2011 launcher folders are game files, kept unless the game files are asked for (a link in the launcher
    /// folder refuses only then). The uninstall window offers the launcher removal only while that folder exists, and names it in the game-files question.
    /// </summary>
    [Check("server.classic-launcher-uninstall")]
    public static void RemovingTheLauncherOrAmberlineTakesOnlyWhatIsOurs()
    {
        var temp = TempFolder();
        TestAssert.False(temp.Contains(' '), $"this check needs a temp folder without spaces: {temp}");
        var profile = new PlayerProfile(Path.Combine(temp, "profile", "Local"), Path.Combine(temp, "profile", "Desktop"));
        var log = new List<string>();
        var linked = Path.Combine(temp, "install", PlayerInstall.LauncherFolder, "linked");
        var allLinked = Path.Combine(temp, "all", PlayerInstall.LauncherFolder, "linked");
        try
        {
            var root = Path.Combine(temp, "rads");
            var install = Path.Combine(temp, "install");
            var launcher = Path.Combine(install, PlayerInstall.LauncherFolder);
            var record = BuildInstall(install, root, log);
            var deploy = Path.Combine(root, @"projects\lol_launcher\releases\0.0.0.76\deploy");
            var airJunction = Path.Combine(root, @"projects\lol_air_client\releases\0.0.0.132\deploy");
            // The game's own managed layout and the launcher's root log are not ours: they stay.
            var gameLayout = Path.Combine(root, @"projects\lol_game_client\releases\0.0.0.51\x");
            Directory.CreateDirectory(Path.GetDirectoryName(gameLayout)!);
            File.WriteAllText(gameLayout, "the game's archives");
            File.WriteAllText(Path.Combine(root, "rads_lol_launcher.log"), "launcher log");
            OnSta(() =>
            {
                using var window = new PlayerInstallWindow.UninstallWindow(profile, record, Path.Combine(install, PlayerInstall.ToolFolder));
                TestAssert.True(FindNamed(window, LauncherChoice) is { Enabled: true }, "the window offers the launcher removal while its folder is there");
                var games = FindNamed(window, "Also delete the game files");
                TestAssert.True(games is not null && games.Text.Contains("the 2011 launcher", StringComparison.Ordinal),
                    "the game-files question names the 2011 launcher while its folder is there");
            });

            // A link inside the launcher folder: refused before anything changes; removing the link itself leaves its target alone.
            MakeJunction(linked, record.Air);
            RefusedWith(() => PlayerUninstall.RemoveLauncher(record, root, log.Add), "is a link");
            TestAssert.True(File.Exists(Path.Combine(launcher, "LoLLauncher.exe")) && File.Exists(Path.Combine(deploy, "LoLLauncher.exe")),
                "a refused removal changed a launcher file");
            TestAssert.Equal<string?>(record.Air, new DirectoryInfo(airJunction).LinkTarget, "a refused removal changed the layout");
            Directory.Delete(linked);
            TestAssert.True(File.Exists(Path.Combine(record.Air, "LolClient.exe")), "removing the link removed what it points at");
            // The launcher folder itself as a link: refused the same way, and what it points at is left alone.
            var linkedInstall = Path.Combine(temp, "linked-install");
            var elsewhere = Path.Combine(temp, "elsewhere");
            Directory.CreateDirectory(elsewhere);
            File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "mine");
            Directory.CreateDirectory(linkedInstall);
            MakeJunction(Path.Combine(linkedInstall, PlayerInstall.LauncherFolder), elsewhere);
            RefusedWith(() => PlayerUninstall.RemoveLauncher(new PlayerInstall.Record(linkedInstall, Path.Combine(linkedInstall, "air"), Path.Combine(linkedInstall, "game")), null), "is a link");
            TestAssert.True(File.Exists(Path.Combine(elsewhere, "keep.txt")), "a launcher folder that is a link leaves its target alone");

            // A locked file: the layout keeps its stamp and the launcher folder stays; the next call finishes the job.
            using (new FileStream(Path.Combine(deploy, "LoLLauncher.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
                RefusedWith(() => PlayerUninstall.RemoveLauncher(record, root, log.Add), "still in use");
            TestAssert.True(log.Any(line => line.Contains("could not remove", StringComparison.Ordinal)), "the locked file is logged");
            TestAssert.True(ClassicLauncherLayout.Remaining(root) is not null && Directory.Exists(launcher), "the stamp and the launcher folder stay");

            var removed = PlayerUninstall.RemoveLauncher(record, root, log.Add);
            TestAssert.False(Directory.Exists(launcher), "the launcher folder is removed");
            // The failed attempt already removed the junctions (as links, first), so the last call reports what was left: the launcher's project folder
            // (its outermost whole folder, which takes the copy inside it) and the launcher folder.
            TestAssert.True(removed.Contains(launcher) && removed.Contains(Path.Combine(root, @"projects\lol_launcher")), "the removal reports the launcher folder and the layout's launcher project");
            TestAssert.True(!Directory.Exists(airJunction) && new DirectoryInfo(airJunction).LinkTarget is null, "the AIR junction is gone");
            string[] stays = [Path.Combine(root, "projects"), Path.Combine(root, @"projects\lol_game_client"), Path.Combine(root, @"projects\lol_game_client\releases"),
                Path.Combine(root, @"projects\lol_game_client\releases\0.0.0.51"), gameLayout, Path.Combine(root, "rads_lol_launcher.log"),
                Path.Combine(root, ClassicLauncherLayout.RootMarker), Path.Combine(root, ClassicLauncherLayout.RootLockRecord)];   // batch 857: the root mark and record stay
            TestAssert.Equal(string.Join(",", stays.Order(StringComparer.Ordinal)), string.Join(",", Entries(root).Order(StringComparer.Ordinal)),
                "only the game's managed layout and the root's own log stay");
            TestAssert.True(File.Exists(Path.Combine(record.Air, "LolClient.exe")) && File.Exists(Path.Combine(record.Air, "lol.properties")) &&
                File.Exists(Path.Combine(record.Game, "League of Legends.exe")), "the AIR and game folders behind the junctions are intact");
            TestAssert.Equal(new[] { "air", "game" }, Directory.GetFileSystemEntries(install).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal).ToArray(),
                "the install keeps its AIR and game folders");
            var settled = Times(root) + Times(install);
            TestAssert.Equal(0, PlayerUninstall.RemoveLauncher(record, root, log.Add).Count, "a second call removes nothing");
            TestAssert.Equal(settled, Times(root) + Times(install), "a second call changes no file");
            OnSta(() =>
            {
                using var window = new PlayerInstallWindow.UninstallWindow(profile, record, Path.Combine(install, PlayerInstall.ToolFolder));
                TestAssert.True(FindNamed(window, LauncherChoice) is null, "no launcher removal without the launcher folder");
                var games = FindNamed(window, "Also delete the game files");
                TestAssert.True(games is not null && !games.Text.Contains("2011 launcher", StringComparison.Ordinal),
                    "the game-files question does not name a launcher that is not there");
            });

            // Amberline's removal always takes the layout. Its AIR, game and 2011 launcher folders are game files: kept unless the game files are asked for.
            var all = Path.Combine(temp, "all");
            var allRoot = Path.Combine(temp, "rads-all");
            var allRecord = BuildInstall(all, allRoot, log);
            var allLauncher = Path.Combine(all, PlayerInstall.LauncherFolder);
            var allAir = Path.Combine(allRoot, @"projects\lol_air_client\releases\0.0.0.132\deploy");
            MakeJunction(allLinked, allRecord.Air);
            // Asked for: a link in the launcher folder is refused before anything changes.
            RefusedWith(() => PlayerUninstall.RemoveAll(profile, allRecord, deleteGame: true, radsRoot: allRoot), "is a link");
            TestAssert.True(Directory.Exists(allLauncher) && Directory.Exists(allAir), "a refused removal changed the install or the layout");
            Directory.Delete(allLinked);
            // Kept: the folder is never walked, so a link in it does not refuse; the layout goes, and the AIR, game and launcher folders stay.
            MakeJunction(allLinked, allRecord.Air);
            var removal = PlayerUninstall.RemoveAll(profile, allRecord, deleteGame: false, radsRoot: allRoot);
            TestAssert.Equal(0, removal.Left.Count, string.Join(", ", removal.Left));
            TestAssert.Equal(new[] { Path.Combine(all, "air"), Path.Combine(all, "game"), allLauncher }, removal.Kept.ToArray(),
                "the AIR, game and 2011 launcher folders are kept");
            TestAssert.Equal(0, Entries(allRoot).Length, "nothing of the layout is left under the root");
            TestAssert.Equal<string?>(null, ClassicLauncherLayout.Remaining(allRoot), "no stamp is left");
            TestAssert.Equal(new[] { "air", "game", "launcher" }, Directory.GetFileSystemEntries(all).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal).ToArray(),
                "Amberline's install folder keeps its AIR, game and 2011 launcher folders");
            TestAssert.True(File.Exists(Path.Combine(allLauncher, "LoLLauncher.exe")) && File.Exists(Path.Combine(allRecord.Air, "LolClient.exe")),
                "the launcher's files and the AIR files behind the junction are intact");

            // Asked for: the game files, the 2011 launcher's folder and the layout all go (a fresh install here, with no link).
            var asked = Path.Combine(temp, "asked");
            var askedRoot = Path.Combine(temp, "rads-asked");
            var askedRecord = BuildInstall(asked, askedRoot, log);
            var everything = PlayerUninstall.RemoveAll(profile, askedRecord, deleteGame: true, radsRoot: askedRoot);
            TestAssert.Equal(0, everything.Left.Count, string.Join(", ", everything.Left));
            TestAssert.Equal(0, everything.Kept.Count, string.Join(", ", everything.Kept));
            TestAssert.Equal(0, Entries(askedRoot).Length, "the layout goes in this case too");
            TestAssert.False(Directory.Exists(asked), "the install folder is empty once the game files and the 2011 launcher's folder are gone");
        }
        finally
        {
            // Junctions go first, as links: a recursive delete over one can fail half way (see LayoutAddOnly).
            foreach (var junction in new[] { linked, allLinked, Path.Combine(temp, "linked-install", PlayerInstall.LauncherFolder) }.Where(Directory.Exists)) Directory.Delete(junction);
            foreach (var built in new[] { Path.Combine(temp, "rads"), Path.Combine(temp, "rads-all"), Path.Combine(temp, "rads-asked") }) ClassicLauncherLayout.Remove(built, _ => { });
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>
    /// When the classic start runs (design §2): a stage without a launcher folder (an operator's without launcherRoot) has no classic
    /// start and nothing to tell; wrong files, an unusable RADS root, a busy launcher Maestro port (8393, 8394, 8395) and a held update-service
    /// mutex each give the reason the player is told, in the fallback sentence; everything in order takes it.
    /// </summary>
    [Check("server.classic-launcher-decide")]
    public static void ClassicStartDecides()
    {
        var temp = TempFolder();
        try
        {
            var launcher = Path.Combine(temp, "install", "launcher");
            var stage = new Amberline.AirCompanion.Program.DesktopStage(Path.Combine(temp, "install", "servers", "0123456789abcdef"), Path.Combine(temp, "install", "air"),
                Path.Combine(temp, "install", "game", "League of Legends.exe"), "", "", 8390, 8393, LauncherRoot: launcher);
            const string root = @"c:\rads";
            bool Free(int port) => true;
            bool MutexFree(string name) => true;
            TestAssert.Equal(new ClassicPlan(false, null), ClassicStart.Decide(stage, root, Free, MutexFree), "no launcher folder: no classic start and nothing to tell");

            WriteLauncherFiles(launcher);
            var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(launcher, "LoLLauncher.exe"))));
            TestAssert.Equal(new ClassicPlan(false, "The classic launcher is not version 1.0.1.19."), ClassicStart.Decide(stage, root, Free, MutexFree), "wrong files: the files' problem");
            var unmanaged = ClassicStart.Decide(stage, null, Free, MutexFree, sha);
            TestAssert.True(!unmanaged.Use && unmanaged.Reason is { } rads && rads.Contains("RADS folder", StringComparison.Ordinal), $"an unusable RADS root: {unmanaged.Reason}");
            foreach (var port in new[] { 8393, 8394, 8395 })
            {
                var busy = ClassicStart.Decide(stage, root, other => other != port, MutexFree, sha);
                TestAssert.True(!busy.Use && busy.Reason is { } reason && reason.Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
                    $"port {port} busy: {busy.Reason}");
            }
            var held = ClassicStart.Decide(stage, root, Free, name => name != RadsKernelStandIn.MutexName, sha);
            TestAssert.True(!held.Use && !string.IsNullOrEmpty(held.Reason), $"the update service's mutex is held: {held.Reason}");
            TestAssert.Equal(new ClassicPlan(false, null), ClassicStart.Decide(stage with { LauncherRoot = null }, root, Free, MutexFree, sha), "a stage without a launcher folder never takes it");
            TestAssert.Equal(new ClassicPlan(true, null), ClassicStart.Decide(stage, root, Free, MutexFree, sha), "everything in order: the classic start");

            TestAssert.Equal("The classic launcher did not open: The classic launcher is not version 1.0.1.19. Starting the usual way.",
                ClassicStart.FallbackMessage("The classic launcher is not version 1.0.1.19."), "the fallback sentence, the reason's final period trimmed");

            // Never stuck: any exception of the classic steps (the stand-in's mutex can throw more than InvalidOperationException) is a reason
            // naming it, so the usual start follows; only the stop itself ends the start.
            using var running = new CancellationTokenSource();
            string? Guarded(Func<Task<string?>> steps, CancellationToken stop) => ClassicStart.GuardedAsync(steps, stop).GetAwaiter().GetResult();
            foreach (var thrown in new Exception[] { new WaitHandleCannotBeOpenedException("bad name."), new UnauthorizedAccessException("denied"), new IOException("pipe"),
                new OperationCanceledException("not the stop") })
            {
                var reason = Guarded(() => throw thrown, running.Token);
                TestAssert.True(reason is not null && reason.Contains(thrown.GetType().Name, StringComparison.Ordinal), $"{thrown.GetType().Name} becomes a reason: {reason}");
            }
            TestAssert.Equal<string?>("The classic launcher did not open: It stopped with IOException (pipe). Starting the usual way.",
                ClassicStart.FallbackMessage(Guarded(() => throw new IOException("pipe"), running.Token)!), "the guarded reason in the fallback sentence");
            TestAssert.Equal<string?>(null, Guarded(() => Task.FromResult<string?>(null), running.Token), "a session that ran: no reason");
            TestAssert.Equal<string?>("It closed before it was ready.", Guarded(() => Task.FromResult<string?>("It closed before it was ready."), running.Token), "a reason passes through");
            using var stopped = new CancellationTokenSource();
            stopped.Cancel();
            TestAssert.Throws<OperationCanceledException>(() => Guarded(() => throw new OperationCanceledException(stopped.Token), stopped.Token), "the stop is not a fallback");
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>
    /// The stage's launcher folder (design §1, §9): an installed player's server stage names it or derives &lt;install&gt;\launcher beside the
    /// install's air\; the host app's stage and the operator's (START-AIR; owner, 13 November 2026) read launcherRoot (none without it); the
    /// legacy play-mode stage has none.
    /// </summary>
    [Check("server.classic-launcher-stage")]
    public static void StageNamesTheLauncherFolder()
    {
        var temp = TempFolder();
        try
        {
            var pin = new string('A', 64);
            var install = Path.Combine(temp, "install");
            var server = Path.Combine(install, "servers", "0123456789abcdef");
            Directory.CreateDirectory(server);
            var connection = Path.Combine(server, "server.json");
            File.WriteAllText(connection, "{\"host\":\"one.example.test\",\"port\":8391,\"certificateSha256\":\"" + pin + "\"}");
            string Json(params (string Key, string Value)[] values) => "{" + string.Join(",", values.Select(v => $"\"{v.Key}\":\"{v.Value.Replace(@"\", @"\\")}\"")) + "}";
            var game = Path.Combine(install, "game", "League of Legends.exe");
            File.WriteAllText(Path.Combine(server, "stage.json"), Json(("airRoot", Path.Combine(install, "air")), ("stockExecutable", game)));
            TestAssert.Equal<string?>(Path.Combine(install, "launcher"), Amberline.AirCompanion.Program.LoadDesktopStage(server, 8393, connection).LauncherRoot, "a player stage: <install>\\launcher");
            var named = Path.Combine(temp, "elsewhere", "launcher");
            File.WriteAllText(Path.Combine(server, "stage.json"), Json(("airRoot", Path.Combine(install, "air")), ("stockExecutable", game), ("launcherRoot", named)));
            TestAssert.Equal<string?>(named, Amberline.AirCompanion.Program.LoadDesktopStage(server, 8393, connection).LauncherRoot, "a player stage that names its launcher folder");

            var host = Path.Combine(temp, "host-stage");
            Directory.CreateDirectory(host);
            File.WriteAllText(Path.Combine(host, "stage.json"), Json(("stockExecutable", game), ("airSource", Path.Combine(temp, "host-air"))).TrimEnd('}') + ",\"httpPort\":8390}");
            TestAssert.Equal<string?>(null, Amberline.AirCompanion.Program.LoadDesktopStage(host).LauncherRoot, "the host app's stage without launcherRoot");
            var hostLauncher = Path.Combine(temp, "host-launcher");
            File.WriteAllText(Path.Combine(host, "stage.json"), Json(("stockExecutable", game), ("launcherRoot", hostLauncher)).TrimEnd('}') + ",\"httpPort\":8390}");
            TestAssert.Equal<string?>(hostLauncher, Amberline.AirCompanion.Program.LoadDesktopStage(host).LauncherRoot, "the host app's stage reads launcherRoot");

            var operatorStage = Path.Combine(temp, "operator");
            Directory.CreateDirectory(Path.Combine(operatorStage, "platform-data"));
            File.WriteAllText(Path.Combine(operatorStage, "platform-data", "platform.json"), "{\"HttpPort\":8390}");
            File.WriteAllText(Path.Combine(operatorStage, "stage.json"), Json(("stockExecutable", game)));
            TestAssert.Equal<string?>(null, Amberline.AirCompanion.Program.LoadDesktopStage(operatorStage).LauncherRoot, "the operator's stage without launcherRoot");
            File.WriteAllText(Path.Combine(operatorStage, "stage.json"), Json(("stockExecutable", game), ("launcherRoot", hostLauncher)));
            TestAssert.Equal<string?>(hostLauncher, Amberline.AirCompanion.Program.LoadDesktopStage(operatorStage).LauncherRoot, "the operator's stage reads launcherRoot");

            var legacy = Path.Combine(temp, "legacy");
            PlayerMode.WriteStage(legacy, Path.GetDirectoryName(game)!, Path.Combine(temp, "player-air"), new AirRemoteInvitation("one.example.test", 8391, "", pin));
            TestAssert.Equal<string?>(null, Amberline.AirCompanion.Program.LoadDesktopStage(legacy).LauncherRoot, "the legacy play-mode stage has none");
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>
    /// Batch 761, task 7 (design §1): the installer's optional 2011 launcher folder. A folder that passes the launcher check moves to
    /// &lt;install&gt;\launcher; a folder under an old RADS root (a path with projects in it) is copied and its source stays; a folder that fails the
    /// check is refused with the check's own sentence before anything changes; an install that already holds its launcher keeps it when it is set up
    /// again; and every server's stage names &lt;install&gt;\launcher, with or without a launcher. Dummy files; each folder's pin is its dummy EXE's hash.
    /// </summary>
    [Check("server.classic-launcher-install", TimeoutSeconds = 300)]
    public static void TheInstallerTakesTheLauncherFolderInAndNamesItInTheStage()
    {
        var temp = TempFolder();
        try
        {
            var pin = new string('A', 64);
            var connection = new AirRemoteInvitation("one.example.test", 8391, CertificateSha25664hex: pin);
            var first = AirPlayerInstallChecks.Fixture.Make(Path.Combine(temp, "first"));
            var launcher = Path.Combine(temp, "first", "downloads", "launcher");
            var pins = first.Pins with { Launcher = WriteLauncherFiles(launcher) };

            // Refused before anything changes: a folder missing a file, and a folder of another version (the real fingerprint).
            var broken = Path.Combine(temp, "broken");
            WriteLauncherFiles(broken);
            File.Delete(Path.Combine(broken, "xml", "slideshow.xml"));
            RefusedWith(() => PlayerInstall.MakePlan(first.Profile, first.Air, first.Game, first.Install, pins, broken),
                @"The classic launcher folder is missing xml\slideshow.xml.");
            RefusedWith(() => PlayerInstall.MakePlan(first.Profile, first.Air, first.Game, first.Install, first.Pins, launcher),
                "The classic launcher is not version 1.0.1.19.");
            TestAssert.False(Directory.Exists(first.Install), "a refused plan leaves the install folder alone");

            // A launcher folder: planned as a move to <install>\launcher; the install takes it in, and the stage names it.
            var plan = PlayerInstall.MakePlan(first.Profile, first.Air, first.Game, first.Install, pins, launcher);
            var move = plan.Moves.Single(candidate => FolderMove.Same(candidate.Source, launcher));
            TestAssert.Equal(Path.Combine(first.Install, "launcher"), move.Target, "the launcher folder moves to <install>\\launcher");
            TestAssert.False(move.KeepSource, "a folder outside a RADS root is moved, not copied");
            var installed = PlayerInstall.Install(first.Profile, plan, connection, "Alpha", first.Tool);
            TestAssert.True(File.Exists(Path.Combine(first.Install, "launcher", "LoLLauncher.exe")), "the launcher is in the install");
            TestAssert.False(Directory.Exists(launcher), "the picked folder was moved, not left behind");
            TestAssert.Equal(Path.Combine(first.Install, "launcher"), StageLauncherRoot(installed.Server), "the stage names <install>\\launcher");

            // Set up again with the launcher already in place: nothing to move, and the install's own launcher is named.
            var record = PlayerInstall.ReadRecord(first.Profile)!;
            var again = PlayerInstall.MakePlan(first.Profile, record.Air, record.Game, record.Install, pins, Path.Combine(record.Install, "launcher"));
            TestAssert.Equal(0, again.Moves.Count, "a launcher already in the install is not moved again");
            TestAssert.Equal(Path.Combine(record.Install, "launcher"), again.Launcher, "and it is the install's launcher");
            var inside = Path.Combine(record.Install, "elsewhere");
            WriteLauncherFiles(inside);
            RefusedWith(() => PlayerInstall.MakePlan(first.Profile, record.Air, record.Game, record.Install, pins, inside),
                "The 2011 launcher folder is inside the install folder.");

            // A second server, added without a launcher of its own, still names the install's launcher in its stage.
            var bravo = PlayerInstall.AddServer(first.Profile, record, new AirRemoteInvitation("two.example.test", 8392, CertificateSha25664hex: new string('B', 64)), "Bravo");
            TestAssert.Equal(Path.Combine(record.Install, "launcher"), StageLauncherRoot(bravo.Folder), "a second server's stage names the launcher too");

            // Final review: the install's own launcher folder fails the check. A set-up install cannot change its install folder, so a picked folder and a
            // download are both sent to Uninstall's "Remove the 2011 launcher" instead of "Choose another install folder".
            const string incomplete = "The 2011 launcher in this install is incomplete. Remove it first. To do that, open Uninstall and choose Remove the 2011 launcher.";
            File.Delete(Path.Combine(record.Install, "launcher", "xml", "slideshow.xml"));
            var replacement = Path.Combine(temp, "replacement");
            WriteLauncherFiles(replacement);
            RefusedWith(() => PlayerInstall.MakePlan(first.Profile, record.Air, record.Game, record.Install, pins, replacement), incomplete);
            RefusedWith(() => PlayerDownload.Plan(record.Install, LauncherOffer(pins.Launcher), record, first.Profile, null, pins), incomplete);

            // A folder under an old RADS root (projects in its path) is copied: the old install's launcher stays where it was.
            var rads = Path.Combine(temp, "rads", "projects", "lol_launcher", "releases", "0.0.0.76", "deploy");
            var second = AirPlayerInstallChecks.Fixture.Make(Path.Combine(temp, "second"));
            var copy = PlayerInstall.MakePlan(second.Profile, second.Air, second.Game, second.Install,
                second.Pins with { Launcher = WriteLauncherFiles(rads) }, rads);
            TestAssert.True(copy.Moves.Single(candidate => FolderMove.Same(candidate.Source, rads)).KeepSource, "a launcher under projects is copied");
            TestAssert.True(copy.CopyBytes >= new FileInfo(Path.Combine(rads, "LoLLauncher.exe")).Length, "the copy counts toward the free space");
            PlayerInstall.Install(second.Profile, copy, connection, "Bravo", second.Tool);
            TestAssert.True(File.Exists(Path.Combine(rads, "LoLLauncher.exe")), "the old install's launcher stays where it was");
            TestAssert.True(File.Exists(Path.Combine(second.Install, "launcher", "LoLLauncher.exe")), "and the install has its own copy");
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>
    /// Review round 1 (task 7, fix 1): an install set up before its 2011 launcher keeps the AIR and game folders its record names, even when the host on
    /// this PC keeps the game where it is. The re-run adds the launcher; the record and the stage still name the install's own clients.
    /// </summary>
    [Check("server.classic-launcher-install-rerun", TimeoutSeconds = 300)]
    public static void ASetUpInstallTakesTheLauncherAndKeepsItsRecordedClients()
    {
        var temp = TempFolder();
        try
        {
            var pin = new string('A', 64);
            var connection = new AirRemoteInvitation("one.example.test", 8391, CertificateSha25664hex: pin);
            // The host on this PC reads the game where it is, so the install's record names a folder outside the install.
            var kept = AirPlayerInstallChecks.Fixture.Make(Path.Combine(temp, "kept"));
            WriteHostSettings(kept.Profile, ("dataRoot", kept.Game));
            PlayerInstall.Install(kept.Profile, PlayerInstall.MakePlan(kept.Profile, kept.Air, kept.Game, kept.Install, kept.Pins), connection, "Alpha", kept.Tool);
            var recorded = PlayerInstall.ExistingInstall(kept.Profile, kept.Pins) ?? throw new InvalidOperationException("the install does not pass its checks");
            TestAssert.Equal(kept.Game, recorded.Game, "the record names the game where the host reads it");

            // Set up again with the launcher, downloaded from the server: its files are staged, and the plan keeps the recorded clients.
            var staging = Path.Combine(kept.Install, PlayerDownload.Staging);
            var pins = kept.Pins with { Launcher = WriteLauncherFiles(Path.Combine(staging, "launcher")) };
            var offer = LauncherOffer(pins.Launcher);
            var rerun = PlayerDownload.Plan(kept.Install, offer, recorded, kept.Profile, null, pins);
            TestAssert.Equal((recorded.Air, recorded.Game), (rerun.Air, rerun.Game), "the re-run plan keeps the recorded AIR and game");
            PlayerDownload.Place(staging, rerun, Staged(offer, staging), pins, "Alpha", null, CancellationToken.None);
            PlayerInstall.Install(kept.Profile, rerun, connection, "Alpha", kept.Tool);

            var after = PlayerInstall.ReadRecord(kept.Profile) ?? throw new InvalidOperationException("no record after the re-run");
            TestAssert.Equal((recorded.Install, recorded.Air, recorded.Game), (after.Install, after.Air, after.Game), "the record still names the install's clients");
            TestAssert.True(File.Exists(Path.Combine(kept.Install, "launcher", "LoLLauncher.exe")), "the launcher was added");
            var server = Path.Combine(kept.Install, "servers", PlayerInstall.ServerId(pin));
            TestAssert.Equal(recorded.Air, StageText(server, "airRoot"), "the stage names the recorded AIR folder");
            TestAssert.Equal(Path.Combine(recorded.Game, "League of Legends.exe"), StageText(server, "stockExecutable"), "the stage names the recorded game");
            TestAssert.Equal(Path.Combine(kept.Install, "launcher"), StageText(server, "launcherRoot"), "the stage names the launcher");
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>
    /// Review round 1 (task 7, fix 2): a host on this PC keeps its 2011 launcher in the folder the player picks (its platform.json's launcherRoot). The
    /// install copies the launcher and never moves the host's folder, as it never moves the host's AIR or game.
    /// </summary>
    [Check("server.classic-launcher-install-host-keeps", TimeoutSeconds = 300)]
    public static void AHostsLauncherFolderIsCopiedNeverMoved()
    {
        var temp = TempFolder();
        try
        {
            var connection = new AirRemoteInvitation("one.example.test", 8391, CertificateSha25664hex: new string('A', 64));
            var hosted = AirPlayerInstallChecks.Fixture.Make(Path.Combine(temp, "hosted"));
            var launcher = Path.Combine(temp, "hosted", "launchers", "2011");
            var pins = hosted.Pins with { Launcher = WriteLauncherFiles(launcher) };
            WriteHostSettings(hosted.Profile, ("launcherRoot", launcher));
            var plan = PlayerInstall.MakePlan(hosted.Profile, hosted.Air, hosted.Game, hosted.Install, pins, launcher);
            TestAssert.True(plan.Moves.Single(move => FolderMove.Same(move.Source, launcher)).KeepSource, "the host's launcher folder is copied, never moved");
            PlayerInstall.Install(hosted.Profile, plan, connection, "Alpha", hosted.Tool);
            TestAssert.True(File.Exists(Path.Combine(launcher, "LoLLauncher.exe")) && Directory.Exists(Path.Combine(launcher, "xml")) &&
                Directory.Exists(Path.Combine(launcher, "img")), "the host's folder stays whole");
            TestAssert.True(File.Exists(Path.Combine(hosted.Install, "launcher", "LoLLauncher.exe")), "the install has its own copy");
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>
    /// Review round 1 (task 7, fix 3): a picked launcher folder that also holds other things gives up only the launcher's own names. The rest stays where
    /// it was, and the plan and the done page say what stayed (as the AIR client's move does, batch 639).
    /// </summary>
    [Check("server.classic-launcher-install-own-entries", TimeoutSeconds = 300)]
    public static void AFolderWithOtherThingsGivesUpOnlyTheLaunchersOwnEntries()
    {
        var temp = TempFolder();
        try
        {
            var connection = new AirRemoteInvitation("one.example.test", 8391, CertificateSha25664hex: new string('A', 64));
            var mixed = AirPlayerInstallChecks.Fixture.Make(Path.Combine(temp, "mixed"));
            var launcher = Path.Combine(temp, "mixed", "Downloads", "launcher");
            var pins = mixed.Pins with { Launcher = WriteLauncherFiles(launcher) };
            File.WriteAllText(Path.Combine(launcher, "notes.txt"), "the player's own notes");
            Directory.CreateDirectory(Path.Combine(launcher, "photos"));
            File.WriteAllText(Path.Combine(launcher, "photos", "holiday.jpg"), "a picture");
            var plan = PlayerInstall.MakePlan(mixed.Profile, mixed.Air, mixed.Game, mixed.Install, pins, launcher);
            TestAssert.True(plan.Notes.Any(note => note.Contains("notes.txt", StringComparison.Ordinal) && note.Contains("photos", StringComparison.Ordinal)),
                "the plan says what stays: " + string.Join(" | ", plan.Notes));
            var installed = PlayerInstall.Install(mixed.Profile, plan, connection, "Alpha", mixed.Tool);
            var target = Path.Combine(mixed.Install, "launcher");
            TestAssert.True(File.Exists(Path.Combine(target, "LoLLauncher.exe")) && Directory.Exists(Path.Combine(target, "img")), "the launcher's own files moved in");
            TestAssert.False(File.Exists(Path.Combine(target, "notes.txt")) || Directory.Exists(Path.Combine(target, "photos")), "the other things did not");
            TestAssert.True(File.Exists(Path.Combine(launcher, "notes.txt")) && File.Exists(Path.Combine(launcher, "photos", "holiday.jpg")),
                "the other things stay where they were");
            TestAssert.False(File.Exists(Path.Combine(launcher, "LoLLauncher.exe")), "the launcher's own files left the picked folder");
            TestAssert.True(installed.Stayed.Any(line => line.Contains("notes.txt", StringComparison.Ordinal) && line.Contains("photos", StringComparison.Ordinal)),
                "the done page says what stayed: " + string.Join(" | ", installed.Stayed));
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>
    /// The next-start question (design §2): no mark, no question; a classic start that never saw AIR leaves the mark and the next start asks;
    /// "the usual way this time" leaves it, so the start after asks again; a classic start that saw AIR clears it; a start without the classic
    /// option never asks.
    /// </summary>
    [Check("server.classic-launcher-next-start")]
    public static void NextStartAsksAfterAnUnfinishedClassicStart()
    {
        var stage = TempFolder();
        Directory.CreateDirectory(stage);
        try
        {
            var asked = 0;
            bool Usual() { asked++; return false; }
            bool Again() { asked++; return true; }
            ClassicPlan classic = new(true, null);
            TestAssert.False(ClassicStart.ShouldAsk(stage), "no mark: no question");
            TestAssert.True(ClassicStart.Takes(classic, stage, Usual) && asked == 0, "no mark: the classic start, unasked");

            ClassicStart.MarkStarted(stage);
            TestAssert.True(File.Exists(Path.Combine(stage, ClassicStart.UnfinishedMark)), "the launcher started: the mark is in the stage folder");
            TestAssert.True(ClassicStart.ShouldAsk(stage), "a classic start that never saw AIR: the next start asks");
            TestAssert.False(ClassicStart.Takes(classic, stage, Usual), "the usual way this time");
            TestAssert.Equal(1, asked, "asked once");
            TestAssert.True(ClassicStart.ShouldAsk(stage), "choosing the usual way leaves the mark");
            TestAssert.True(ClassicStart.Takes(classic, stage, Again), "open the classic launcher again");
            TestAssert.False(ClassicStart.Takes(new(false, "Port 8394 is in use by another program."), stage, Again), "no classic start possible: no question");
            TestAssert.Equal(2, asked, "the question only when the classic start can run");

            ClassicStart.MarkStarted(stage);
            ClassicStart.MarkAirSeen(stage);
            TestAssert.False(ClassicStart.ShouldAsk(stage), "AIR seen: the mark is cleared");
            ClassicStart.MarkAirSeen(stage);
            TestAssert.False(ClassicStart.ShouldAsk(stage), "clearing it twice is harmless");
        }
        finally
        {
            Directory.Delete(stage, recursive: true);
        }
    }

    /// <summary>
    /// A second start during a classic session (review round 1): the leftover search counts the launcher's copy under the RADS root and AIR and
    /// the game through the layout's junctions, besides the install's own folders; nothing else under the root counts, no root means no classic
    /// paths, and the game through its junction is still a game (never closed). Path matching only, no process.
    /// </summary>
    [Check("server.classic-launcher-leftovers")]
    public static void ClassicSessionIsALeftover()
    {
        const string root = @"C:\rads", install = @"D:\Amberline", air = @"D:\Amberline\air", game = @"D:\Amberline\game\League of Legends.exe";
        var (launcher, classicAir, classicGame) = ClassicLauncherLayout.Images(root);
        TestAssert.Equal(@"C:\rads\projects\lol_launcher\releases\0.0.0.76\deploy\LoLLauncher.exe", launcher, "the launcher's copy");
        TestAssert.Equal(@"C:\rads\projects\lol_air_client\releases\0.0.0.132\deploy\LolClient.exe", classicAir, "AIR through its junction");
        TestAssert.Equal(@"C:\rads\solutions\lol_game_client_sln\releases\0.0.0.70\deploy\League of Legends.exe", classicGame, "the game through its junction");
        foreach (var image in new[] { launcher, classicAir, classicGame, @"c:\RADS\projects\lol_launcher\releases\0.0.0.76\deploy\lollauncher.exe" })
            TestAssert.True(PlayerMode.Ours(image, install, air, game, root), $"{image} is a leftover of a classic session");
        foreach (var image in new[] { @"D:\Amberline\tool\Amberline.exe", @"D:\Amberline\air\LolClient.exe", game })
            TestAssert.True(PlayerMode.Ours(image, install, air, game, root), $"{image}: the install's own rules hold");
        foreach (var image in new[] { @"C:\rads\projects\lol_game_client\releases\0.0.0.51\League of Legends.exe", @"C:\rads\rads_user_kernel.exe",
            @"C:\Other\LolClient.exe", @"C:\rads\projects\lol_launcher\releases\0.0.0.76\deploy\Riot_SafetyCheck.exe" })
            TestAssert.False(PlayerMode.Ours(image, install, air, game, root), $"{image} is not ours");
        TestAssert.False(PlayerMode.Ours(launcher, install, air, game, null), "an unusable RADS root has no classic paths");
        TestAssert.True(PlayerMode.IsGame(classicGame) && PlayerMode.IsGame(game), "the game through its junction is a game: Cancel only, never closed");
        TestAssert.False(PlayerMode.IsGame(launcher) || PlayerMode.IsGame(classicAir), "the launcher and AIR can be closed");
    }

    /// <summary>
    /// Spec §7, the friend's view: a remote player tool's RTMP tunnel names the IPv4 address it reached the host at, the session keeps it
    /// while its connection lasts, and that player's game start names it as the server address; a player without a report keeps the
    /// advertised host. Only a strict dotted IPv4 counts. The game server is a stand-in script (ready at once, then waits for "stop").
    /// </summary>
    [Check("server.classic-launcher-game-address", TimeoutSeconds = 120)]
    public static void TheServerUsesTheAddressARemotePlayerToolReports() => GameAddressAsync().GetAwaiter().GetResult();

    private static async Task GameAddressAsync()
    {
        var reported = IPAddress.Parse("203.0.113.7");
        TestAssert.Equal(reported, AirRemoteTunnel.ParseGameAddress("203.0.113.7"));
        foreach (var bad in new[] { null, "", "host.example", "203.0.113.7 1", "::1", "1.2.3", "1.2.3.256", " 1.2.3.4", "1.2.3.4.", "+1.2.3.4",
                     "1.2.3.0x4", "1.2.3.4,1.2.3.4", "1.2.3.0004", "::ffff:1.2.3.4", "1.2.3.04", "010.0.0.1", "1.2.00.4" })
            TestAssert.True(AirRemoteTunnel.ParseGameAddress(bad) is null, $"'{bad}' is not a dotted IPv4 address");
        TestAssert.Equal(IPAddress.Parse("10.0.0.0"), AirRemoteTunnel.ParseGameAddress("10.0.0.0"), "a lone 0 is not a leading zero");
        // Final review: the player tool sends an IPv4 address in its canonical form, so a leading zero never reaches the platform or the game.
        TestAssert.Equal(IPAddress.Parse("010.0.0.1").ToString(), StockClientLauncher.GameAddress("010.0.0.1"), "the canonical form of a leading-zero address");
        TestAssert.Equal(AirRemoteTunnel.GameAddressHeader, AirRemoteProxy.GameAddressHeader, "the player tool sends the header the platform reads");

        var root = TempFolder();
        try
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            // End to end: the player tool's RTMP tunnel names the address it resolved for the invitation's host (here the literal 127.0.0.1).
            using (var certificate = AirRemoteChecks.CreateCertificate())
            {
                using var backend = new TcpListener(IPAddress.Loopback, 0);
                backend.Start();
                var rtmpPort = ((IPEndPoint)backend.LocalEndpoint).Port;
                var access = new AirRemoteAccess(Path.Combine(root, "remote"));
                var remotePort = AirRemoteChecks.FreePort();
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
                builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, remotePort, listen => listen.UseHttps(certificate)));
                await using var app = builder.Build();
                var peers = new AirTunnelPeers();
                AirRemoteTunnel.Map(app, access, rtmpPort, AirRemoteChecks.FreePort(), peers, TextWriter.Null);
                await app.StartAsync(stop.Token);
                await using var proxy = await AirRemoteProxy.StartWithPortsAsync(new AirRemoteInvitation("127.0.0.1", remotePort, access.Issue(1).Token,
                    certificate.GetCertHashString(HashAlgorithmName.SHA256), ChatPort: AirRemoteChecks.FreePort()),
                    AirRemoteChecks.FreePort(), AirRemoteChecks.FreePort(), AirRemoteChecks.FreePort(), stop.Token);
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, proxy.RtmpPort, stop.Token);
                await client.GetStream().WriteAsync(new byte[] { 3 }, stop.Token);
                using var accepted = await backend.AcceptTcpClientAsync(stop.Token);
                // The tunnel records its peers before it pumps the first byte.
                TestAssert.Equal(1, await accepted.GetStream().ReadAsync(new byte[1], stop.Token));
                TestAssert.Equal(IPAddress.Loopback, peers.GameAddress(rtmpPort, (IPEndPoint)accepted.Client.RemoteEndPoint!),
                    "the tunnel keeps the address the player tool reported");
                TestAssert.True(peers.GameAddress(rtmpPort, new IPEndPoint(IPAddress.Loopback, 1)) is null, "a connection the tunnel never made has none");
            }

            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            var friend = accounts.RegisterPublic("address_friend", "testing-password", "Address Friend");
            var local = accounts.RegisterPublic("address_local", "testing-password", "Address Local");
            var sessions = new PlatformSessions(accounts);
            foreach (var (connection, account) in new[] { ("tunnel", friend), ("direct", local) })
                TestAssert.True(sessions.AuthenticateChannel(connection, account.Username,
                    sessions.Login(connection, account.Username, sessions.IssueTicket(account), AirLine.ClientVersion).Token));
            sessions.ObserveGameAddress("tunnel", reported);
            sessions.ObserveGameAddress("direct", null);
            TestAssert.Equal(reported, sessions.ReportedGameAddress(friend.Id), "the tunnel session keeps its report");
            TestAssert.True(sessions.ReportedGameAddress(local.Id) is null, "a session without a report has none");

            var server = Path.Combine(root, "server-stand-in.cmd");
            File.WriteAllText(server, "@echo launcher.ready\r\n@set /p line=\r\n");
            Directory.CreateDirectory(Path.Combine(root, "game-data"));
            await using (var manager = new GameManager(new HostSettings(Path.Combine(root, "game-data"), server,
                FirstGamePort: AirRemoteChecks.FreePort(), MaxGames: 2, BindAddress: "127.0.0.1"), Path.Combine(root, "games")))
            {
                var clock = new GameAddressClock();
                var games = new PlatformGames(accounts, manager, "198.51.100.1", clock, reportedGameAddress: sessions.ReportedGameAddress);
                var pushed = new List<(long Account, AsObject Body)>();
                games.Push = (account, body) => { if (body is AsObject dto) pushed.Add((account, dto)); };
                var id = Convert.ToInt32(((AsObject)games.Create(friend, "Address", "", 1, 2, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE"))["id"]);
                games.Join(local, id, "");
                games.Start(friend.Id, id, Convert.ToInt64(((AsObject)games.Get(friend.Id, id))["optimisticLock"]));
                foreach (var account in new[] { friend, local }) { games.Pick(account.Id, 1); games.LockIn(account.Id); }
                clock.Now = clock.Now.AddSeconds(PlatformGames.PostPickServerSeconds);
                string? ServerIp(long account) => pushed.LastOrDefault(p => p.Account == account &&
                    p.Body.TypeName.EndsWith("PlayerCredentialsDto", StringComparison.Ordinal)).Body?["serverIp"] as string;
                while (ServerIp(local.Id) is null) { games.Tick(); await Task.Delay(100, stop.Token); }
                TestAssert.Equal("203.0.113.7", ServerIp(friend.Id), "the reporting player's game start names the address it reached the host at");
                TestAssert.Equal("198.51.100.1", ServerIp(local.Id), "a player without a report keeps the advertised host");
                TestAssert.Equal("203.0.113.7", ((AsObject)((AsObject)games.Reconnect(friend.Id)!)["playerCredentials"])["serverIp"], "and so does a reconnect");
            }

            sessions.Suspend("tunnel");
            TestAssert.True(sessions.ReportedGameAddress(friend.Id) is null, "a closed connection forgets its report");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class GameAddressClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>The uninstall window's launcher removal button, by its accessible name.</summary>
    private const string LauncherChoice = "Remove the 2011 launcher";

    /// <summary>An install with its 2011 launcher folder, AIR and game folders, and the RADS layout Ensure builds for them under <paramref name="root"/>.</summary>
    private static PlayerInstall.Record BuildInstall(string install, string root, List<string> log)
    {
        var record = new PlayerInstall.Record(install, Path.Combine(install, "air"), Path.Combine(install, "game"));
        var launcher = Path.Combine(install, PlayerInstall.LauncherFolder);
        WriteLauncherFiles(launcher);
        Directory.CreateDirectory(record.Air);
        File.WriteAllText(Path.Combine(record.Air, "LolClient.exe"), "air exe");
        File.WriteAllText(Path.Combine(record.Air, "lol.properties"), "host=127.0.0.1\n");
        Directory.CreateDirectory(record.Game);
        File.WriteAllText(Path.Combine(record.Game, "League of Legends.exe"), "game exe");
        TestAssert.Equal<string?>(null, ClassicLauncherLayout.Ensure(new(root, launcher, record.Air, record.Game, 8390), log.Add).Refusal, "the layout is built");
        return record;
    }

    /// <summary>A directory junction, made the way the layout makes its own (mklink: no administrator rights needed).</summary>
    private static void MakeJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        }) ?? throw new IOException("cmd.exe did not start");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException($"mklink /J {link} failed ({process.ExitCode}): {output.Trim()}");
    }

    /// <summary>Runs a window check on a single-threaded apartment, as Windows' controls expect, and passes its failure on.</summary>
    private static void OnSta(Action check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { check(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException(failure.Message, failure);
    }

    /// <summary>The control with <paramref name="name"/> as its accessible name, anywhere under <paramref name="parent"/>; null when there is none.</summary>
    private static System.Windows.Forms.Control? FindNamed(System.Windows.Forms.Control parent, string name)
    {
        foreach (System.Windows.Forms.Control child in parent.Controls)
        {
            if (child.AccessibleName == name) return child;
            if (FindNamed(child, name) is { } inner) return inner;
        }
        return null;
    }

    /// <summary>
    /// Batch 857 (the orchestrator's final rulings, 17 November 2026): a RADS root other Windows accounts can change, marked or not (an earlier
    /// Amberline's: its stamp, no mark, the drive's permissions), is never deleted or changed: the classic start falls back to the usual one and the
    /// reason tells the player to delete it. Once deleted, the next start lays it out again, locked.
    /// </summary>
    [Check("server.classic-launcher-earlier-root")]
    public static void AnOpenRootFallsBackUntilThePlayerDeletesIt()
    {
        var temp = InstallLockChecks.Hazard(TempFolder());
        var root = Path.Combine(temp, "rads");
        try
        {
            var launcher = Path.Combine(temp, "launcher");
            WriteLauncherFiles(launcher);
            var (air, game) = (Path.Combine(temp, "air"), Path.Combine(temp, "game"));
            Directory.CreateDirectory(air);
            File.WriteAllText(Path.Combine(air, "LolClient.exe"), "air exe");
            File.WriteAllText(Path.Combine(air, "lol.properties"), "host=127.0.0.1\n");
            Directory.CreateDirectory(game);
            File.WriteAllText(Path.Combine(game, "League of Legends.exe"), "game exe");
            var log = new List<string>();
            var request = new ClassicLayoutRequest(root, launcher, air, game, 8390);
            TestAssert.Equal<string?>(null, ClassicLauncherLayout.Ensure(request, log.Add).Refusal, "laid out, locked");
            string Delete(string folder) => $"Delete the folder {folder}. Amberline sets it up again at the next start, locked to your Windows account.";

            // Marked, but others can change it now (review B): the mark skips nothing.
            Amberline.AirCompanion.InstallLock.Restamp(root, null);
            File.WriteAllText(Path.Combine(root, "system", "system.cfg"), "Region = EUW\r\n");
            var open = InstallLockChecks.Sddl(root);
            TestAssert.True(ClassicLauncherLayout.Ensure(request, log.Add).Refusal is { } marked && marked.EndsWith(Delete(root), StringComparison.Ordinal),
                "a marked root others can change: the usual start, and the reason says to delete it");
            TestAssert.Equal(open, InstallLockChecks.Sddl(root), "and it is left as it is");

            // As an earlier version left it (its stamp, no mark, no record): the same, and still nothing is changed.
            File.Delete(Path.Combine(root, ClassicLauncherLayout.RootMarker));
            File.Delete(Path.Combine(root, ClassicLauncherLayout.RootLockRecord));
            var earlier = InstallLockChecks.Sddl(root);
            TestAssert.True(ClassicLauncherLayout.Ensure(request, log.Add).Refusal is { } stamped && stamped.EndsWith(Delete(root), StringComparison.Ordinal),
                "an earlier Amberline's root others can change: the usual start, and the reason says to delete it");
            TestAssert.True(InstallLockChecks.Sddl(root) == earlier && File.ReadAllText(Path.Combine(root, "system", "system.cfg")) == "Region = EUW\r\n",
                "and it is never deleted or changed");

            // Re-review 2 (HIGH 1): removing the layout from such a root is refused too. Nothing in it changes, and the player is told to delete it.
            RefusedWith(() => ClassicLauncherLayout.Remove(root, _ => { }), $"Amberline removed nothing there. Delete the folder {root} yourself.");
            var profile = new PlayerProfile(Path.Combine(temp, "profile", "Local"), Path.Combine(temp, "profile", "Desktop"));
            var removal = PlayerUninstall.RemoveAll(profile, new PlayerInstall.Record(Path.Combine(temp, "install"), air, game), deleteGame: false, radsRoot: root);
            TestAssert.True(removal.Left.Contains(root), $"the uninstall lists the root to delete by hand: {string.Join(", ", removal.Left)}");
            TestAssert.Equal(earlier, InstallLockChecks.Sddl(root), "and changes nothing in it");

            // The player deletes it: the next start lays it out again, locked.
            DeleteAsThePlayer(root);
            TestAssert.Equal<string?>(null, ClassicLauncherLayout.Ensure(request, log.Add).Refusal, "laid out again");
            TestAssert.True(File.Exists(Path.Combine(root, ClassicLauncherLayout.RootMarker)) && new DirectoryInfo(root).GetAccessControl().AreAccessRulesProtected &&
                Amberline.AirCompanion.InstallLock.Inspect(root, _ => true) is null, "marked and locked (its junctions not followed)");
            TestAssert.True(File.Exists(Path.Combine(air, "LolClient.exe")) && File.Exists(Path.Combine(game, "League of Legends.exe")),
                "the folders behind the junctions are untouched");

            // Re-review 2 (HIGH 1): in a root that passes, a stamped path reached through a link is never deleted, even when that link is recorded
            // (here in the other line's stamp, so the root's check passes it over).
            var victim = Directory.CreateDirectory(Path.Combine(temp, "victim")).FullName;
            File.WriteAllText(Path.Combine(victim, "keep.txt"), "mine");
            MakeJunction(Path.Combine(root, "system", "via"), victim);
            File.AppendAllLines(Path.Combine(root, @"projects\lol_launcher", "classic.stamp"), [@"J system\via"]);
            File.AppendAllLines(Path.Combine(root, @"projects\lol_launcher", ClassicLauncherLayout.StampName), [@"system\via\keep.txt"]);
            var removeLog = new List<string>();
            ClassicLauncherLayout.Remove(root, removeLog.Add);
            TestAssert.True(File.Exists(Path.Combine(victim, "keep.txt")), "a stamped file behind a link is never deleted");
            TestAssert.True(removeLog.Any(line => line.Contains(@"system\via\keep.txt: a link lies on its way", StringComparison.Ordinal)), "and the removal says why it kept it");
        }
        finally
        {
            if (Directory.Exists(root)) DeleteAsThePlayer(root);
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>Deletes a folder as File Explorer does: a junction in it goes as a link and is never followed.</summary>
    private static void DeleteAsThePlayer(string folder)
    {
        foreach (var child in Directory.EnumerateDirectories(folder))
            if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(child);
            else DeleteAsThePlayer(child);
        Directory.Delete(folder, recursive: true);
    }

    private static string TempFolder() => Path.Combine(Path.GetTempPath(), "amberline-b760-" + Guid.NewGuid().ToString("N"));

    /// <summary>The launcher folder a server's stage names (its stage.json's launcherRoot).</summary>
    private static string StageLauncherRoot(string server)
    {
        using var stage = JsonDocument.Parse(File.ReadAllText(Path.Combine(server, "stage.json")));
        return stage.RootElement.GetProperty("launcherRoot").GetString()!;
    }

    /// <summary>The installer's refusal, with the sentence <paramref name="text"/> in it; a failure when the action is not refused.</summary>
    private static void RefusedWith(Action action, string text)
    {
        try { action(); }
        catch (InstallRefusedException refused)
        {
            TestAssert.True(refused.Message.Contains(text, StringComparison.Ordinal), $"refused for another reason: {refused.Message}");
            return;
        }
        throw new InvalidOperationException($"not refused (expected: {text})");
    }

    /// <summary>One string value of a server's stage.json.</summary>
    private static string StageText(string server, string name)
    {
        using var stage = JsonDocument.Parse(File.ReadAllText(Path.Combine(server, "stage.json")));
        return stage.RootElement.GetProperty(name).GetString()!;
    }

    /// <summary>Writes the host's settings file (platform.json in the profile's Amberline data folder) with the given string values.</summary>
    private static void WriteHostSettings(PlayerProfile profile, params (string Key, string Value)[] values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(profile.HostSettings)!);
        File.WriteAllText(profile.HostSettings, "{" + string.Join(",", values.Select(value => $"\"{value.Key}\":\"{value.Value.Replace(@"\", @"\\")}\"")) + "}");
    }

    /// <summary>The launcher's files as a server's list gives them: every file of the release, the EXE with <paramref name="sha"/> (the other hashes are not checked here).</summary>
    /// <summary>
    /// Batch 857 review: <paramref name="offer"/> with each staged file's own size and SHA-256, as a real download's list has them (Place hashes every
    /// file again against the list); a file that is not staged keeps its entry.
    /// </summary>
    internal static PlayerDownload.Offer Staged(PlayerDownload.Offer offer, string staging)
    {
        var files = offer.Files.Select(entry => Path.Combine(staging, entry.Path.Replace('/', '\\')) is var path && File.Exists(path)
            ? entry with { Size = new FileInfo(path).Length, Sha256 = RetailClients.Sha256(path) } : entry).ToList();
        return new(files, files.Sum(entry => entry.Size));
    }

    private static PlayerDownload.Offer LauncherOffer(string sha)
    {
        var files = ClassicLauncherFiles.Required
            .Select(file => new PlayerDownload.Entry("launcher/" + file.Replace('\\', '/'), 1, file == "LoLLauncher.exe" ? sha : new string('D', 64)))
            .ToList();
        return new(files, files.Count);
    }

    /// <summary>
    /// Release 0.0.0.76 (the 1.0.0.132 line's classic launcher, batch 775): its fingerprint and version, its 34 required files in the release's order,
    /// the nine region translations of its regions.txt, and no EULA, NDA or unknown-region file among the required ones.
    /// </summary>
    [Check("server.classic-launcher-0076-set")]
    public static void Release0076Set()
    {
        TestAssert.Equal("8d8ef1689faf225c7e1a0a8478dbca80778313fe92a48a852793683b16bd22da", ClassicLauncherFiles.LauncherSha256, "the 1.0.1.19 fingerprint");
        TestAssert.Equal(
            @"LoLLauncher.exe,LoLLauncher.exe.version,launcher.maestro.dll,LoLLauncher.swf,RADS.swf,slideshow.swf,Riot_SafetyCheck.exe,en_US.tra,de_DE.tra,el_GR.tra,en_GB.tra,es_ES.tra,fr_FR.tra,pl_PL.tra,ro_RO.tra,na_region.tra,euw_region.tra,euw_region_de_DE.tra,euw_region_es_ES.tra,euw_region_fr_FR.tra,eune_region.tra,eune_region_el_GR.tra,eune_region_pl_PL.tra,eune_region_ro_RO.tra,regions.txt,shards.txt,languages.txt,xml\slideshow.xml,img\ad0.png,img\ad1.png,img\ad2.png,img\ad3.png,img\ad4.png,img\ad5.png",
            string.Join(",", ClassicLauncherFiles.Required), "the release's 34 files");
        TestAssert.Equal("na_region.tra,euw_region.tra,euw_region_de_DE.tra,euw_region_es_ES.tra,euw_region_fr_FR.tra,eune_region.tra,eune_region_el_GR.tra,eune_region_pl_PL.tra,eune_region_ro_RO.tra",
            string.Join(",", ClassicLauncherFiles.RegionFiles), "the nine region translations: NA en_us; EUW en_gb, de_de, es_es, fr_fr; EUNE en_gb, el_gr, pl_pl, ro_ro");
        TestAssert.False(ClassicLauncherFiles.Required.Any(file => file.StartsWith("eula_", StringComparison.OrdinalIgnoreCase) ||
            file.StartsWith("nda_", StringComparison.OrdinalIgnoreCase) || file.StartsWith("unknown_", StringComparison.OrdinalIgnoreCase)),
            "no EULA, NDA or unknown-region file is required");
        var temp = TempFolder();
        try
        {
            var folder = Path.Combine(temp, "files");
            var sha = WriteLauncherFiles(folder);
            TestAssert.Equal<string?>("The classic launcher is not version 1.0.1.19.", ClassicLauncherFiles.Problem(folder), "a dummy EXE is refused, naming the version the EXE's properties show");
            TestAssert.Equal<string?>(null, ClassicLauncherFiles.Problem(folder, sha), "the release's files with their own EXE's hash are accepted");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>
    /// Release 0.0.0.76's region translations: the layout points the two news lines of each of the nine at the local news page (<c>/launcher/news</c>)
    /// and keeps every other line, the region's own links and status addresses included, with its byte-order mark and CRLF endings.
    /// </summary>
    [Check("server.classic-launcher-0076-regions")]
    public static void Release0076Regions()
    {
        var shipped = RegionTranslation("eune_region_ro_RO.tra");
        var rewritten = ClassicLauncherLayout.WithNews(Encoding.UTF8.GetBytes(shipped), 8390);
        var expected = shipped.Replace("tr \"url_host_news\" = \"ll.example.invalid\"", "tr \"url_host_news\" = \"127.0.0.1:8390\"")
            .Replace("tr \"url_path_news\" = \"/pages/launcher/na\"", "tr \"url_path_news\" = \"/launcher/news\"");
        TestAssert.True(rewritten.SequenceEqual(Encoding.UTF8.GetBytes(expected)),
            "only the two news lines change; the byte-order mark, the CRLF endings and the status addresses stay");
        var plain = RegionTranslation("na_region.tra");
        var expectedPort = plain.Replace("tr \"url_host_news\" = \"ll.example.invalid\"", "tr \"url_host_news\" = \"127.0.0.1:8394\"")
            .Replace("tr \"url_path_news\" = \"/pages/launcher/na\"", "tr \"url_path_news\" = \"/launcher/news\"");
        TestAssert.True(ClassicLauncherLayout.WithNews(Encoding.UTF8.GetBytes(plain), 8394).SequenceEqual(Encoding.UTF8.GetBytes(expectedPort)),
            "the news port given is the one the translation names");
    }

    /// <summary>
    /// Release 0.0.0.76 on a temporary root: the release folder is 0.0.0.76 and its deploy folder holds exactly the required files (no extra name, no
    /// EULA or NDA file); each base translation is en_US.tra's text (the launcher stays English); each region translation is its region's English file's
    /// text (the lines and the first bytes of that file), with its two news lines pointed at the local page; the solution is 0.0.0.70 with no
    /// manifest (batch 814), its junction leads to the game folder, and the layout writes the releasemanifest the release does not ship.
    /// </summary>
    [Check("server.classic-launcher-0076-layout")]
    public static void Release0076Layout()
    {
        var temp = TempFolder();
        TestAssert.False(temp.Contains(' '), $"this check needs a temp folder without spaces: {temp}");
        var root = Path.Combine(temp, "rads");
        var launcher = Path.Combine(temp, "launcher", "files");
        var air = Path.Combine(temp, "air");
        var game = Path.Combine(temp, "game");
        try
        {
            WriteLauncherFiles(launcher);
            Directory.CreateDirectory(air);
            File.WriteAllText(Path.Combine(air, "LolClient.exe"), "air exe");
            File.WriteAllText(Path.Combine(air, "lol.properties"), "host=127.0.0.1\n");
            Directory.CreateDirectory(game);
            File.WriteAllText(Path.Combine(game, "League of Legends.exe"), "game exe");

            var (layout, refusal) = ClassicLauncherLayout.Ensure(new(root, launcher, air, game, 8390), _ => { });
            TestAssert.Equal<string?>(null, refusal, "a fresh root is built");
            var deploy = Path.Combine(root, @"projects\lol_launcher\releases\0.0.0.76\deploy");
            TestAssert.Equal(deploy, layout!.Deploy, "the launcher runs from its 0.0.0.76 deploy folder");
            TestAssert.True(Directory.GetFiles(deploy, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(deploy, p))
                .Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(ClassicLauncherFiles.Required.Order(StringComparer.OrdinalIgnoreCase)),
                "the deploy folder holds exactly the required files: no generated locale copy, no EULA or NDA file");
            foreach (var file in ClassicLauncherFiles.Required.Where(file => !ClassicLauncherFiles.RegionFiles.Contains(file)))
                TestAssert.True(File.ReadAllBytes(Path.Combine(launcher, CopySource(file))).SequenceEqual(File.ReadAllBytes(Path.Combine(deploy, file))),
                    $"deploy\\{file} is {CopySource(file)}'s text");
            // Each region translation is the English file of its region (the file its code names), with the news lines rewritten and nothing else changed.
            var englishOf = new Dictionary<string, string>
            {
                ["na_region.tra"] = "na_region.tra", ["euw_region.tra"] = "euw_region.tra", ["euw_region_de_DE.tra"] = "euw_region.tra",
                ["euw_region_es_ES.tra"] = "euw_region.tra", ["euw_region_fr_FR.tra"] = "euw_region.tra", ["eune_region.tra"] = "eune_region.tra",
                ["eune_region_el_GR.tra"] = "eune_region.tra", ["eune_region_pl_PL.tra"] = "eune_region.tra", ["eune_region_ro_RO.tra"] = "eune_region.tra",
            };
            static string[] OtherLines(byte[] bytes) =>
                Encoding.Latin1.GetString(bytes).Split('\n').Where(line => !line.Contains("url_host_news") && !line.Contains("url_path_news")).ToArray();
            foreach (var region in ClassicLauncherFiles.RegionFiles)
            {
                var english = File.ReadAllBytes(Path.Combine(launcher, englishOf[region]));
                var copy = File.ReadAllBytes(Path.Combine(deploy, region));
                var text = Encoding.Latin1.GetString(copy);
                TestAssert.True(text.Contains("tr \"url_host_news\" = \"127.0.0.1:8390\"\r\n") && text.Contains("tr \"url_path_news\" = \"/launcher/news\"\r\n"),
                    $"{region} points its news at the local page");
                TestAssert.True(OtherLines(english).SequenceEqual(OtherLines(copy)), $"{region} has the lines of {englishOf[region]}");
                TestAssert.True(english.AsSpan(0, 3).SequenceEqual(copy.AsSpan(0, 3)), $"{region} starts as {englishOf[region]} does");
            }
            TestAssert.True(File.Exists(Path.Combine(root, @"projects\lol_launcher\releases\0.0.0.76\releasemanifest")), "no releasemanifest ships, so the layout writes one");

            // Batch 814: the solution release holds only the game's junction, no manifest (the 132 game runs loose; batch 811 report, section 2).
            TestAssert.Equal(@"deploy", string.Join(",", Directory.GetFileSystemEntries(Path.Combine(root, @"solutions\lol_game_client_sln\releases\0.0.0.70"))
                .Select(Path.GetFileName)), "the 0.0.0.70 solution release holds only its junction");
            TestAssert.Equal<string?>(game, new DirectoryInfo(Path.Combine(root, @"solutions\lol_game_client_sln\releases\0.0.0.70\deploy")).LinkTarget,
                "the game through its 0.0.0.70 junction");
        }
        finally
        {
            ClassicLauncherLayout.Remove(root, _ => { });
            Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>
    /// Batch 814 (owner, 16 November 2026: one RADS root, and the line that starts its classic launcher takes it over). On a temporary root with
    /// the 1.0.0.126 line's layout as its build leaves it (classic.stamp, the name every stamp before batch 814 has, so it is 126's), the game's
    /// managed layout and the launcher's root log: a running launcher, AIR or game (the seam) refuses with nothing removed, asked about the root
    /// and the folders 126's junctions lead to; a 126 file in use refuses with 126's stamp kept, listing what is left, and nothing of 132 built;
    /// the next start removes 126's stamped paths (an unstamped junction inside a folder 126 owns whole as a link only) and builds 132's: its own
    /// stamp, AIR and config 0.0.0.132, the 0.0.0.70 junction, no manifest. The folders behind every junction, projects\lol_game_client and the
    /// root log are untouched, and the stand-in answers only 132's releases. A 126 start killed halfway (stamped, not all made), beside a 132
    /// stamp, is taken over too, and 132's stamp is kept. The production check finds a launcher-named program (a copy of ping) running from a folder.
    /// </summary>
    [Check("server.classic-launcher-swap")]
    public static void TakesTheRootOverFromThe126Line()
    {
        var temp = TempFolder();
        TestAssert.False(temp.Contains(' '), $"this check needs a temp folder without spaces: {temp}");
        var root = Path.Combine(temp, "rads");
        var launcher = Path.Combine(temp, "launcher");
        var (air, game) = (Path.Combine(temp, "air132"), Path.Combine(temp, "game132"));
        var (air126, game126, kept) = (Path.Combine(temp, "air126"), Path.Combine(temp, "game126"), Path.Combine(temp, "kept"));
        var log = new List<string>();
        Process? probe = null;
        static void Put(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        void Survives() => TestAssert.True(File.Exists(Path.Combine(air126, "LolClient.exe")) && File.Exists(Path.Combine(game126, "League of Legends.exe")) &&
            File.Exists(Path.Combine(kept, "keep.txt")), "the folders behind the junctions are intact");
        try
        {
            WriteLauncherFiles(launcher);
            Put(Path.Combine(air, "LolClient.exe"), "air 1.52");
            Put(Path.Combine(air, "lol.properties"), "host=132\n");
            Put(Path.Combine(game, "League of Legends.exe"), "game 132");
            Put(Path.Combine(air126, "LolClient.exe"), "air 1.45");
            Put(Path.Combine(game126, "League of Legends.exe"), "game 126");
            Put(Path.Combine(kept, "keep.txt"), "someone's");
            var gameLayout = Path.Combine(root, @"projects\lol_game_client\releases\0.0.0.51\x");
            Put(gameLayout, "the game's archives");
            Put(Path.Combine(root, "rads_lol_launcher.log"), "launcher log");
            var links = new Dictionary<string, string> { [Line126Air] = air126, [Line126Game] = game126 };
            OtherLayout(root, "classic.stamp", Line126, links);
            MakeJunction(Path.Combine(root, @"projects\lol_air_client\releases\0.0.0.126\linked"), kept);
            // Batch 857 review: a root Amberline did not mark may hold only junctions a layout stamp records (any other link refuses the classic start).
            File.AppendAllLines(Path.Combine(root, @"projects\lol_launcher\classic.stamp"), [@"J projects\lol_air_client\releases\0.0.0.126\linked"]);
            var request = new ClassicLayoutRequest(root, launcher, air, game, 8390);
            var stamp126 = Path.Combine(root, @"projects\lol_launcher\classic.stamp");
            var stamp132 = Path.Combine(root, @"projects\lol_launcher", ClassicLauncherLayout.StampName);
            TestAssert.Equal("classic132.stamp", ClassicLauncherLayout.StampName, "this line's stamp");
            // The classic root is the launcher's (the registry value through its seam, else c:\rads), never this line's C:\rads132.
            TestAssert.Equal<string?>(RadsLayout.DefaultRoot, RadsRegistry.Root(() => null), "no registry value: c:\\rads");
            TestAssert.Equal<string?>(@"E:\Games\RADS", RadsRegistry.Root(() => @"E:\Games\RADS"), "the registry's LocalRootFolder");
            TestAssert.True(RadsRegistry.Root(() => "rads") is null, "a relative value: no classic start");

            var untouched = Times(root);
            IReadOnlyList<string>? asked = null;
            const string running = "LolClient.exe is running from x. Close it first.";
            var busy = ClassicLauncherLayout.Ensure(request, log.Add, folders => { asked = folders; return running; });
            TestAssert.Equal<string?>(running, busy.Refusal, "a running launcher, AIR or game refuses the start");
            TestAssert.Equal(string.Join("|", root, air126, game126, kept), string.Join("|", asked!), "asked about the root and where 126's junctions lead");
            TestAssert.Equal(untouched, Times(root), "a refused takeover removes nothing");

            using (new FileStream(Path.Combine(root, @"projects\lol_launcher\releases\0.0.0.49\deploy\LoLLauncher.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var locked = ClassicLauncherLayout.Ensure(request, log.Add, _ => null);
                TestAssert.True(locked.Layout is null && locked.Refusal!.Contains("launcher files under", StringComparison.Ordinal) && locked.Refusal!.Contains("could not be removed", StringComparison.Ordinal), $"a 126 file in use refuses: {locked.Refusal}");
            }
            TestAssert.True(File.Exists(stamp126) && !File.Exists(stamp132), "126's stamp is kept and nothing of 132 is built");
            TestAssert.True(File.ReadAllLines(stamp126).All(line => Path.Exists(Path.Combine(root, line.StartsWith("J ", StringComparison.Ordinal) ? line[2..] : line))),
                "126's stamp lists only what is left");
            Survives();

            var (layout, refusal) = ClassicLauncherLayout.Ensure(request, log.Add, _ => null);
            TestAssert.Equal<string?>(null, refusal, "the next start finishes the takeover");
            string[] versions126 = ["0.0.0.49", "0.0.0.126", "0.0.0.1", "0.0.0.81"];
            foreach (var line in Line126.Where(line => line.Split('\\').Intersect(versions126).Any()))
            {
                var path = Path.Combine(root, line.StartsWith("J ", StringComparison.Ordinal) ? line[2..] : line);
                TestAssert.True(!Path.Exists(path) && new FileInfo(path).LinkTarget is null, $"126's {line} is gone");
            }
            TestAssert.False(File.Exists(stamp126), "126's stamp is gone");
            Survives();
            TestAssert.Equal("the game's archives", File.ReadAllText(gameLayout), "projects\\lol_game_client is untouched");
            TestAssert.Equal("launcher log", File.ReadAllText(Path.Combine(root, "rads_lol_launcher.log")), "the launcher's root log stays");
            TestAssert.Equal("airConfigProject = lol_air_client_config_na\r\n", File.ReadAllText(Path.Combine(root, @"system\launcher.cfg")), "132's own system cfgs");
            TestAssert.True(File.Exists(stamp132) && layout!.Deploy == Path.Combine(root, @"projects\lol_launcher\releases\0.0.0.76\deploy"), "132's layout, with its own stamp");
            TestAssert.Equal<string?>(air, new DirectoryInfo(Path.Combine(root, @"projects\lol_air_client\releases\0.0.0.132\deploy")).LinkTarget, "AIR through 0.0.0.132");
            TestAssert.Equal("0.0.0.132", string.Join(",", Directory.GetDirectories(Path.Combine(root, @"projects\lol_air_client\releases")).Select(Path.GetFileName)),
                "AIR's only release is 0.0.0.132, not 126's 0.0.0.126");
            TestAssert.Equal("host=132\n", File.ReadAllText(Path.Combine(root, @"projects\lol_air_client_config_na\releases\0.0.0.132\deploy\lol.properties")),
                "the config project carries 132's AIR file (its ports)");
            TestAssert.Equal<string?>(game, new DirectoryInfo(Path.Combine(root, @"solutions\lol_game_client_sln\releases\0.0.0.70\deploy")).LinkTarget, "the game through 0.0.0.70");
            TestAssert.False(Entries(root).Any(p => Path.GetFileName(p) is "solutionmanifest" or "configurationmanifest"), "no manifest under the root");

            // The stand-in answers 132's releases only: 126's managed game layout stays out of the launcher's sight.
            var pipe = "amberline-b814-" + Guid.NewGuid().ToString("N");
            using (var kernel = new RadsKernelStandIn(root, _ => { }, pipe, pipe + "-mutex", ClassicLauncherLayout.AnsweredReleases))
            {
                string Ask(uint type, params string[] names)
                {
                    byte[] payload = [.. names.SelectMany(name => BitConverter.GetBytes((uint)name.Length + 1).Concat(Encoding.ASCII.GetBytes(name + "\0")))];
                    var reply = kernel.Reply([.. BitConverter.GetBytes(12u + (uint)payload.Length), .. BitConverter.GetBytes(1u), .. BitConverter.GetBytes(type), .. payload], out _);
                    return string.Join(",", Enumerable.Range(4, reply.Length / 4 - 4).Select(i => BitConverter.ToUInt32(reply, i * 4)));
                }
                TestAssert.Equal("132", Ask(0x07, "lol_air_client"), "AIR: 0.0.0.132");
                TestAssert.Equal("76", Ask(0x07, "lol_launcher"), "the launcher: 0.0.0.76");
                TestAssert.Equal("132", Ask(0x07, "lol_air_client_config_euw"), "a config project: 0.0.0.132");
                TestAssert.Equal("70", Ask(0x0F, "lol_game_client_sln", "en_US"), "the solution: 0.0.0.70");
                TestAssert.Equal("0", Ask(0x05, "lol_game_client"), "the game's managed layout (126's 0.0.0.51) is not answered");
            }

            var killed = Path.Combine(temp, "rads-killed");
            OtherLayout(killed, "classic.stamp", Line126, links, created: Line126.Length / 2);
            const string own = @"projects\lol_air_client\releases\0.0.0.132";
            File.WriteAllText(Path.Combine(killed, @"projects\lol_launcher", ClassicLauncherLayout.StampName), own + "\r\n");
            TestAssert.Equal<string?>(null, ClassicLauncherLayout.Ensure(new(killed, launcher, air, game, 8390), log.Add, _ => null).Refusal, "a 126 start killed halfway is taken over");
            TestAssert.False(File.Exists(Path.Combine(killed, @"projects\lol_launcher\classic.stamp")), "its stamp is gone");
            TestAssert.True(File.ReadAllLines(Path.Combine(killed, @"projects\lol_launcher", ClassicLauncherLayout.StampName)).Contains(own), "132's own stamp is kept");
            Survives();

            var folder = Path.Combine(temp, "busy");
            Directory.CreateDirectory(folder);
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), Path.Combine(folder, "LoLLauncher.exe"));
            // About three seconds of pinging loopback: it ends on its own, nothing is killed.
            probe = Process.Start(new ProcessStartInfo(Path.Combine(folder, "LoLLauncher.exe"), "-n 4 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            string? found = null;
            for (var tries = 0; tries < 50 && (found = ClassicStart.RunningFrom([folder])) is null; tries++) Thread.Sleep(100);   // until its modules can be read
            TestAssert.True(found is not null && found.StartsWith("LoLLauncher.exe is running from " + folder, StringComparison.OrdinalIgnoreCase), $"found: {found}");
            TestAssert.Equal<string?>(null, ClassicStart.RunningFrom([kept]), "nothing runs from another folder");
        }
        finally
        {
            probe?.WaitForExit(15000);
            probe?.Dispose();
            foreach (var built in Directory.Exists(temp) ? Directory.GetDirectories(temp) : [])
            {
                ClassicLauncherLayout.Remove(built, _ => { });
                OtherLayoutLinks(built);
            }
            Directory.Delete(temp, recursive: true);
        }
    }

    private const string Line126Air = @"projects\lol_air_client\releases\0.0.0.126\deploy", Line126Game = @"solutions\lol_game_client_sln\releases\0.0.0.81\deploy";

    /// <summary>The 1.0.0.126 line's layout as its build stamps it on a root whose projects folder already holds the game's managed layout
    /// (126's ClassicLauncherLayout.Line.cs Plan, in creation order; one launcher file and one config project stand for all).</summary>
    private static readonly string[] Line126 =
    [
        @"projects\lol_launcher", "system", @"system\launcher.cfg", @"system\locale.cfg", @"system\system.cfg", @"system\user.cfg",
        @"projects\lol_launcher\releases", @"projects\lol_launcher\releases\0.0.0.49", @"projects\lol_launcher\releases\0.0.0.49\deploy",
        @"projects\lol_launcher\releases\0.0.0.49\releasemanifest", @"projects\lol_launcher\releases\0.0.0.49\deploy\LoLLauncher.exe",
        @"projects\lol_launcher\managedfiles", @"projects\lol_launcher\managedfiles\0.0.0.49", @"projects\lol_launcher\managedfiles\0.0.0.49\LoLLauncher.exe",
        @"projects\lol_air_client", @"projects\lol_air_client\releases", @"projects\lol_air_client\releases\0.0.0.126", "J " + Line126Air,
        @"projects\lol_air_client_config_na", @"projects\lol_air_client_config_na\releases", @"projects\lol_air_client_config_na\releases\0.0.0.1",
        @"projects\lol_air_client_config_na\releases\0.0.0.1\deploy", @"projects\lol_air_client_config_na\releases\0.0.0.1\deploy\lol.properties",
        "solutions", @"solutions\lol_game_client_sln", @"solutions\lol_game_client_sln\releases", @"solutions\lol_game_client_sln\releases\0.0.0.81",
        "J " + Line126Game, @"solutions\lol_game_client_sln\releases\0.0.0.81\solutionmanifest", @"solutions\lol_game_client_sln\releases\0.0.0.81\configurationmanifest",
    ];

    /// <summary>
    /// The other line's layout as its build leaves it (neither line's checks can run the other line's Ensure): <paramref name="stamp"/> written as
    /// the stamp <paramref name="stampName"/>, and its first <paramref name="created"/> lines made (a start killed halfway has stamped more than it
    /// made): a "J " line as a junction to the folder <paramref name="links"/> names, a cfg, program, properties or manifest as a file, the rest as folders.
    /// </summary>
    private static void OtherLayout(string root, string stampName, string[] stamp, IReadOnlyDictionary<string, string> links, int? created = null)
    {
        Directory.CreateDirectory(Path.Combine(root, @"projects\lol_launcher"));
        File.WriteAllLines(Path.Combine(root, @"projects\lol_launcher", stampName), stamp);
        foreach (var line in stamp.Take(created ?? stamp.Length))
        {
            var path = Path.Combine(root, line.StartsWith("J ", StringComparison.Ordinal) ? line[2..] : line);
            if (line.StartsWith("J ", StringComparison.Ordinal)) MakeJunction(path, links[line[2..]]);
            else if (Path.GetFileName(path) is "releasemanifest" or "solutionmanifest" or "configurationmanifest" or "lol.properties" || path.EndsWith(".cfg", StringComparison.Ordinal) ||
                path.EndsWith(".exe", StringComparison.Ordinal)) File.WriteAllText(path, "other line: " + line);
            else Directory.CreateDirectory(path);
        }
        // Batch 857 review: an earlier line's root is used as it is only while it is private (as one in a private place would be).
        Amberline.AirCompanion.InstallLock.Lock(root);
    }

    /// <summary>Cleanup: every junction left under <paramref name="root"/> removed as a link, so the temp folder's recursive delete never meets one.</summary>
    private static void OtherLayoutLinks(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.GetDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Where(path => new DirectoryInfo(path).LinkTarget is not null).OrderByDescending(path => path.Length))
            Directory.Delete(path);
    }

    /// <summary>A region translation shaped like release 0.0.0.76's: its news lines, its links and its status addresses. The Greek and the Romanian
    /// one begin with a byte-order mark, as shipped. Its first link names its file, so each file's text differs from the others'.</summary>
    private static string RegionTranslation(string file) =>
        (file is "eune_region_el_GR.tra" or "eune_region_ro_RO.tra" ? "\uFEFF" : "") +
        "tr \"flash_link_news\" = \"http://www.example.invalid/news/" + file + "\"\r\n\r\n" +
        "tr \"url_host_news\" = \"ll.example.invalid\"\r\n" +
        "tr \"url_path_news\" = \"/pages/launcher/na\"\r\n" +
        "tr \"url_host_status\" = \"status-api1.example.invalid\"\r\n" +
        "tr \"url_path_status\" = \"/v1/event/platform/na/status?lang=en\"";

    /// <summary>The file a layout copy of <paramref name="file"/> is written from: each base translation is en_US.tra's text (the launcher stays English).</summary>
    private static string CopySource(string file) => ClassicLauncherFiles.BaseFiles.Contains(file) ? "en_US.tra" : file;

    /// <summary>
    /// Every required launcher file with dummy bytes that name it. Returns the SHA-256 of the dummy LoLLauncher.exe: the launcher pin a check
    /// passes (batch 761, task 7: the installer and the download check the EXE against it).
    /// </summary>
    internal static string WriteLauncherFiles(string folder)
    {
        foreach (var file in ClassicLauncherFiles.Required)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, file))!);
            File.WriteAllText(Path.Combine(folder, file), ClassicLauncherFiles.RegionFiles.Contains(file) ? RegionTranslation(file) : "dummy " + file);
        }
        return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, "LoLLauncher.exe"))));
    }

    /// <summary>Files and folders under the root, never through a junction.</summary>
    private static string[] Entries(string root) =>
        Directory.GetFileSystemEntries(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint });

    private static string Times(string root) =>
        string.Join("\n", Entries(root).Order(StringComparer.Ordinal).Select(p => $"{p} {File.GetLastWriteTimeUtc(p).Ticks}"));

    /// <summary>
    /// Batch 761 (the classic launcher, section 8): the news body the 2011 launcher reads. Its status comes first, then the headlines in order,
    /// a link's slashes are written \/, and each date is the day its headline was saved ("MMM d", invariant culture). With no headline the
    /// welcome headline names the server and is dated today. The body is ASCII within 440 bytes: a name is cut only when it would not fit.
    /// </summary>
    [Check("server.classic-launcher-news")]
    public static void NewsBodyIsStatusFirstAndWithinItsBudget()
    {
        var today = new DateOnly(2026, 11, 20);
        // Batch 766: always five items, the missing ones blank, and the tail of spaces the launcher may drop.
        const string Blank = ",{\"title\":\"\",\"url\":\"\",\"date\":\"\"}";
        var blanks4 = string.Concat(Enumerable.Repeat(Blank, 4));
        var tail = new string(' ', LauncherNews.TailLength);
        TestAssert.Equal("{\"serverStatus\":1,\"news\":[{\"title\":\"Welcome to Test\",\"url\":\"\",\"date\":\"Nov 20\"}" + blanks4 + "]}" + tail,
            LauncherNews.Body([], "Test", today), "no headline: the welcome headline, dated today, then four blank items and the tail");
        TestAssert.Equal("{\"serverStatus\":1,\"news\":[{\"title\":\"Patch day\",\"url\":\"http:\\/\\/a.b\\/c\",\"date\":\"Nov 12\"}" + blanks4 + "]}" + tail,
            LauncherNews.Body([new LauncherHeadline("Patch day", "http://a.b/c", new DateOnly(2026, 11, 12))], "Test", today),
            "a link's slashes are written \\/, and the date is the day the headline was saved");
        TestAssert.Equal("{\"serverStatus\":1,\"news\":[{\"title\":\"Welcome to ABTest\",\"url\":\"\",\"date\":\"Nov 20\"}" + blanks4 + "]}" + tail,
            LauncherNews.Body([], "A\"B\\Test", today), "a name's quotation marks and backslashes are left out");

        var five = Enumerable.Range(1, 5).Select(i => new LauncherHeadline($"Server news {i}", $"http://a.b/{i}", new DateOnly(2026, 11, 12))).ToArray();
        var body = LauncherNews.Body(five, "Test", today);
        TestAssert.True(body.StartsWith("{\"serverStatus\":1,\"news\":[{\"title\":\"Server news 1\"", StringComparison.Ordinal),
            "five headlines: the status first, then the headlines in order");
        TestAssert.False(body.Contains("\"title\":\"\"", StringComparison.Ordinal), "five headlines: no blank item");
        TestAssert.True(Encoding.ASCII.GetByteCount(NewsJson(body)) <= LauncherNews.MaxBytes, $"five headlines stay within {LauncherNews.MaxBytes} bytes: {body.Length}");

        // A 200-letter name fits as it is; one longer than the room left after the fixed text is cut to fit exactly.
        TestAssert.Equal(404, Encoding.ASCII.GetByteCount(NewsJson(LauncherNews.Body([], new string('L', 200), today))), "a 200-letter server name fits");
        TestAssert.Equal(LauncherNews.MaxBytes, Encoding.ASCII.GetByteCount(NewsJson(LauncherNews.Body([], new string('L', 400), today))), "a longer name is cut to the budget");

        // What RADS.swf's handleHTTPResponse reads (batch 753 REPORT section 5): five rows, each found by searching on from the last one, then the
        // status. The welcome body gives one row and four blank ones, also when the launcher drops the last bytes of what it downloaded.
        foreach (var dropped in new[] { 0, 10, LauncherNews.TailLength })
        {
            var (titles, dates, status) = LauncherReads(LauncherNews.Body([], "Test", today)[..^dropped]);
            TestAssert.Equal("Welcome to Test||||", string.Join("|", titles), $"the launcher's titles with the last {dropped} bytes dropped");
            TestAssert.Equal("Nov 20||||", string.Join("|", dates), $"the launcher's dates with the last {dropped} bytes dropped");
            TestAssert.Equal('1', status, "the status the launcher reads");
        }
        var (fiveTitles, _, _) = LauncherReads(body);
        TestAssert.Equal("Server news 1|Server news 2|Server news 3|Server news 4|Server news 5", string.Join("|", fiveTitles), "five headlines read as five rows");
    }

    /// <summary>The JSON of a news body, without its tail of spaces.</summary>
    private static string NewsJson(string body)
    {
        TestAssert.True(body.EndsWith(new string(' ', LauncherNews.TailLength), StringComparison.Ordinal), "the body ends with its tail of spaces");
        return body[..^LauncherNews.TailLength];
    }

    /// <summary>
    /// RADS.swf's handleHTTPResponse (the 2011 launcher's RADS.swf, the same file in 1.0.1.19 and 1.0.1.19; batch 753 avm1_http.txt) as it reads a body: five times "title", "url" and
    /// "date", each the text from just after <c>"name":"</c> (StringUtil.getCharAfter: -1 when not found) to the next quotation mark, searching
    /// on from the last position (ActionScript substring: a negative end counts as 0, and the ends swap), then the digit after "serverStatus":.
    /// </summary>
    private static (string[] Titles, string[] Dates, char Status) LauncherReads(string body)
    {
        static int After(string needle, string text) => text.IndexOf(needle, StringComparison.Ordinal) is var at && at < 0 ? -1 : at + needle.Length;
        string Piece(int start, int end)
        {
            int Clamp(int value) => Math.Clamp(value, 0, body.Length);
            var (a, b) = (Clamp(start), Clamp(end));
            return a <= b ? body[a..b] : body[b..a];
        }
        string Read(ref int position, string needle)
        {
            position += After(needle, body[Math.Clamp(position, 0, body.Length)..]);
            return Piece(position, body.IndexOf('"', Math.Clamp(position, 0, body.Length)));
        }
        var (titles, dates, at) = (new string[5], new string[5], 0);
        for (var row = 0; row < 5; row++)
        {
            titles[row] = Read(ref at, "\"title\":\"");
            Read(ref at, "\"url\":\"");
            dates[row] = Read(ref at, "\"date\":\"");
        }
        var status = After("\"serverStatus\":", body);
        return (titles, dates, status >= 0 && status < body.Length ? body[status] : ' ');
    }

    /// <summary>
    /// Batch 761: Settings' "Launcher news" box and its checks. <see cref="LauncherNews.Problem"/> refuses six headlines, a title or link with a
    /// quotation mark, a backslash, an accent or an emoji, a headline without a title, and a set whose body would exceed 440 bytes (the message
    /// names the budget); it accepts five short ASCII headlines and a set of exactly 440 bytes. A save keeps the list in platform.json, applies it
    /// to the live controls at once, and serves it at /launcher/news to the launcher, which asks without a session; a refused save writes nothing
    /// and shows the rows as typed.
    /// </summary>
    [Check("server.classic-launcher-news-settings", TimeoutSeconds = 240)]
    public static void NewsSettingsRefuseBadTextAndServeWhatTheHostSaves() => NewsSettingsAsync().GetAwaiter().GetResult();

    private static async Task NewsSettingsAsync()
    {
        var short5 = Enumerable.Range(1, 5).Select(i => ($"Short news {i}", (string?)"http://a.b/x")).ToArray();
        TestAssert.Equal<string?>(null, LauncherNews.Problem(short5), "five short ASCII headlines are accepted");
        var six = Enumerable.Range(1, 6).Select(i => ($"News {i}", (string?)null)).ToArray();
        TestAssert.True(LauncherNews.Problem(six) is { } tooMany && tooMany.Contains('5'), $"six headlines are refused: {LauncherNews.Problem(six)}");
        foreach (var (title, link) in new (string Title, string? Link)[]
            { ("Say \"hi\"", null), ("C:\\news", null), ("Wiadomość", null), ("Patch \U0001F600", null), ("Ok", "http://a.b/\"x"), ("Ok", "http://a.b/\\x"), ("Ok", "http://a.b/ą"), ("", "http://a.b") })
            TestAssert.True(LauncherNews.Problem([(title, link)]) is not null, $"refused: '{title}' / '{link}'");
        // A fixed two-digit day: "Sep 30" is the widest date (6 bytes), so the budget is the same on every day of the year.
        var day = new DateOnly(2026, 9, 30);
        // Batch 766: one headline shares the 440 bytes with the four blank items that fill the five (4 x 31 bytes).
        TestAssert.Equal<string?>(null, LauncherNews.Problem([(new string('a', 247), (string?)null)]), "a set of exactly 440 bytes is accepted");
        TestAssert.Equal(LauncherNews.MaxBytes, Encoding.ASCII.GetByteCount(NewsJson(LauncherNews.Body([new LauncherHeadline(new string('a', 247), null, day)], "Test", day))),
            "and its JSON is exactly 440 bytes");
        var over = LauncherNews.Problem([(new string('a', 248), (string?)null)]);
        TestAssert.True(over is not null && over.Contains("440", StringComparison.Ordinal), $"one byte more is refused, and the message names the budget: {over}");

        await HostSettingsChecks.OnSettingsPageAsync("league-launcher-news", async (home, browser, accounts) =>
        {
            var settingsFile = home.Loop.Paths.Settings;
            var page = await GetPageAsync(browser, HostSettingsPage.PagePath);
            Has(page, "<h2 id=news-title>Launcher news</h2>", $"action={HostSettingsPage.NewsPath}", "253 bytes left of 440.");
            var box = page[page.IndexOf("<section class=folder aria-labelledby=news-title>", StringComparison.Ordinal)..];
            box = box[..box.IndexOf("</section>", StringComparison.Ordinal)];
            TestAssert.False(box.Contains("League", StringComparison.OrdinalIgnoreCase) || box.Contains("Riot", StringComparison.OrdinalIgnoreCase),
                "the box names neither the game nor its publisher: " + box);

            // A refused save names the row, shows the rows as typed, and writes and applies nothing.
            using (var refused = await PostNewsAsync(browser, ("title", "Fine news"), ("link", "http://a.b/ok"), ("title", "Say \"hi\""), ("link", "")))
            {
                var answer = await refused.Content.ReadAsStringAsync();
                TestAssert.Equal(HttpStatusCode.BadRequest, refused.StatusCode, answer);
                Has(answer, "Headline 2 has a character the launcher cannot show.", "value=\"Fine news\"", "value=\"http://a.b/ok\"", "value=\"Say &quot;hi&quot;\"");
            }
            TestAssert.False(File.ReadAllText(settingsFile).Contains("launcherNews", StringComparison.Ordinal), "a refused save writes nothing");
            TestAssert.Equal(0, accounts.Controls.LauncherNews.Count, "and applies nothing live");

            // Six headlines, and a headline without a title, are refused too.
            using (var tooMany = await PostNewsAsync(browser, Enumerable.Range(1, 6).SelectMany(i => new[] { ("title", $"News {i}"), ("link", "") }).ToArray()))
                TestAssert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode, await tooMany.Content.ReadAsStringAsync());
            using (var untitled = await PostNewsAsync(browser, ("title", ""), ("link", "http://a.b/x")))
                TestAssert.Equal(HttpStatusCode.BadRequest, untitled.StatusCode, await untitled.Content.ReadAsStringAsync());

            // Two headlines are saved: dated today, applied at once, kept in platform.json, and served to a launcher without a session.
            var savedOn = day;
            var expected = new[] { new LauncherHeadline("Server maintenance tonight", "http://a.b/news/1", savedOn), new LauncherHeadline("Second", null, savedOn) };
            using (var saved = await PostNewsAsync(browser, ("title", "Server maintenance tonight"), ("link", "http://a.b/news/1"), ("title", "Second"), ("link", "")))
            {
                TestAssert.Equal(HttpStatusCode.Redirect, saved.StatusCode, await saved.Content.ReadAsStringAsync());
                TestAssert.Equal(HostSettingsPage.PagePath + "?saved=news", saved.Headers.Location?.OriginalString, "back to Settings with its note");
            }
            TestAssert.True(accounts.Controls.LauncherNews.SequenceEqual(expected), "the live controls hold the saved headlines at once");
            var text = File.ReadAllText(settingsFile);
            using (var json = JsonDocument.Parse(text))
            {
                var first = json.RootElement.GetProperty("launcherNews")[0];
                TestAssert.True(first.GetProperty("title").GetString() == "Server maintenance tonight" && first.GetProperty("link").GetString() == "http://a.b/news/1" &&
                    first.GetProperty("saved").GetString() == savedOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), first.GetRawText());
            }
            TestAssert.True(PlatformControls.From(JsonSerializer.Deserialize<PlatformSettings>(text, HostFiles.Json)!).LauncherNews.SequenceEqual(expected),
                "a start from platform.json applies the same headlines");
            Has(await GetPageAsync(browser, HostSettingsPage.PagePath + "?saved=news"), "<p class=notice role=status>Saved. The 2011 launcher shows them the next time it opens.</p>",
                "188 bytes left of 440.");
            using var launcher = home.Client();
            using (var answer = await launcher.GetAsync(LauncherNews.Route))
            {
                var body = await answer.Content.ReadAsStringAsync();
                TestAssert.Equal(HttpStatusCode.OK, answer.StatusCode, body);
                TestAssert.Equal("application/json", answer.Content.Headers.ContentType?.MediaType, "the route answers JSON");
                TestAssert.Equal(LauncherNews.Body(expected, null, savedOn), body, "the launcher reads the saved headlines, without a session");
            }

            // Saving no headline clears the list: the launcher reads the welcome headline again.
            using (var cleared = await PostNewsAsync(browser))
                TestAssert.Equal(HttpStatusCode.Redirect, cleared.StatusCode, await cleared.Content.ReadAsStringAsync());
            TestAssert.Equal(0, accounts.Controls.LauncherNews.Count, "saving no headline clears the list");
            TestAssert.Equal(LauncherNews.Body([], null, savedOn), await launcher.GetStringAsync(LauncherNews.Route), "and the launcher reads the welcome headline");
        }, () => day);
    }

    private static async Task<string> GetPageAsync(HttpClient client, string path)
    {
        using var answer = await client.GetAsync(path);
        var page = await answer.Content.ReadAsStringAsync();
        TestAssert.Equal(HttpStatusCode.OK, answer.StatusCode, $"GET {path}: {page}");
        return page;
    }

    /// <summary>Posts the Settings' news box as the browser does: the antiforgery token of its page, then one field per input.</summary>
    private static async Task<HttpResponseMessage> PostNewsAsync(HttpClient browser, params (string Name, string Value)[] fields)
    {
        var token = HostModeChecks.Token(await browser.GetStringAsync(HostSettingsPage.PagePath));
        return await browser.PostAsync(HostSettingsPage.NewsPath, new FormUrlEncodedContent([token, .. fields.Select(field => KeyValuePair.Create(field.Name, field.Value))]));
    }

    private static void Has(string page, params string[] phrases)
    {
        foreach (var phrase in phrases) TestAssert.True(page.Contains(phrase, StringComparison.Ordinal), $"the page does not say <{phrase}>:\n{page}");
    }

    /// <summary>Saves the Settings' news box and expects the redirect back to Settings.</summary>
    private static async Task SaveAsync(HttpClient browser, params (string Name, string Value)[] fields)
    {
        using var answer = await PostNewsAsync(browser, fields);
        TestAssert.Equal(HttpStatusCode.Redirect, answer.StatusCode, await answer.Content.ReadAsStringAsync());
    }

    /// <summary>A body that is valid JSON, within the budget, with <paramref name="headlines"/> headlines and blank items up to five (batch 766).</summary>
    private static void AssertBodyWithin(string body, int headlines, string because)
    {
        TestAssert.True(Encoding.ASCII.GetByteCount(NewsJson(body)) <= LauncherNews.MaxBytes, $"{because}: {Encoding.ASCII.GetByteCount(body)} bytes");
        using var json = JsonDocument.Parse(body);
        var items = json.RootElement.GetProperty("news");
        TestAssert.Equal(LauncherNews.MaxHeadlines, items.GetArrayLength(), $"{because}: always five items");
        TestAssert.Equal(headlines, items.EnumerateArray().Count(item => item.GetProperty("title").GetString() is { Length: > 0 }), because);
    }

    /// <summary>
    /// Batch 761 fix round 1 (spec section 8: a headline's date is the day that headline was written): a save keeps the stored day of each
    /// headline whose title and link are unchanged, and dates a new or changed one with the day it is saved. Day A saves two headlines; day B
    /// saves them again with a third (the first two keep A, the third is B); day C changes the third's link (it is C, the others keep theirs).
    /// Platform.json holds the same dates and the launcher reads them. A hand-edited null list then reads as empty: the page renders and a save works.
    /// </summary>
    [Check("server.classic-launcher-news-dates", TimeoutSeconds = 180)]
    public static void NewsKeepsEachHeadlinesDayUnlessItChanges() => NewsDatesAsync().GetAwaiter().GetResult();

    private static Task NewsDatesAsync()
    {
        var day = new DateOnly(2026, 9, 30);
        var (dayA, dayB, dayC) = (day, new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 5));
        return HostSettingsChecks.OnSettingsPageAsync("league-launcher-dates", async (home, browser, accounts) =>
        {
            var settingsFile = home.Loop.Paths.Settings;
            day = dayA;
            await SaveAsync(browser, ("title", "Server maintenance tonight"), ("link", "http://a.b/news/1"), ("title", "Second"), ("link", ""));
            var atA = new[] { new LauncherHeadline("Server maintenance tonight", "http://a.b/news/1", dayA), new LauncherHeadline("Second", null, dayA) };
            TestAssert.True(accounts.Controls.LauncherNews.SequenceEqual(atA), "day A: both headlines are dated A");

            day = dayB;
            await SaveAsync(browser, ("title", "Server maintenance tonight"), ("link", "http://a.b/news/1"), ("title", "Second"), ("link", ""),
                ("title", "Third"), ("link", "http://a.b/3"));
            var atB = new[] { atA[0], atA[1], new LauncherHeadline("Third", "http://a.b/3", dayB) };
            TestAssert.True(accounts.Controls.LauncherNews.SequenceEqual(atB), "day B: the first two keep A, the third is dated B");

            day = dayC;
            await SaveAsync(browser, ("title", "Server maintenance tonight"), ("link", "http://a.b/news/1"), ("title", "Second"), ("link", ""),
                ("title", "Third"), ("link", "http://a.b/4"));
            var atC = new[] { atB[0], atB[1], new LauncherHeadline("Third", "http://a.b/4", dayC) };
            TestAssert.True(accounts.Controls.LauncherNews.SequenceEqual(atC), "day C: the changed third is dated C, the others keep theirs");
            TestAssert.True(PlatformControls.From(JsonSerializer.Deserialize<PlatformSettings>(File.ReadAllText(settingsFile), HostFiles.Json)!)
                .LauncherNews.SequenceEqual(atC), "platform.json holds the same dates");
            using (var launcher = home.Client())
                TestAssert.Equal(LauncherNews.Body(atC, null, dayC), await launcher.GetStringAsync(LauncherNews.Route), "the launcher reads the same dates");

            // A hand-edited null list reads as empty: the page renders its counter from nothing, and the first save works.
            var file = JsonNode.Parse(File.ReadAllText(settingsFile))!.AsObject();
            file["launcherNews"] = null;
            File.WriteAllText(settingsFile, file.ToJsonString());
            Has(await GetPageAsync(browser, HostSettingsPage.PagePath), "253 bytes left of 440.");
            await SaveAsync(browser, ("title", "Back to one"), ("link", ""));
            TestAssert.Equal(1, accounts.Controls.LauncherNews.Count, "a save after a null list stores what was saved");
        }, () => day);
    }

    /// <summary>
    /// Batch 761 fix round 1 (a hand-edited platform.json): a null launcherNews loads as empty, and the live controls start empty. A stored list
    /// with a quotation mark in a title, a null entry, more than five headlines and text past the budget still serves valid JSON of at most 440
    /// bytes: the quotation mark is left out, the list is cut to five, and the trailing headlines go until the body fits.
    /// </summary>
    [Check("server.classic-launcher-news-stored", TimeoutSeconds = 180)]
    public static void HandEditedNewsReadsSafelyAndServesWithinItsBudget() => HandEditedNewsAsync().GetAwaiter().GetResult();

    private static Task HandEditedNewsAsync()
    {
        var day = new DateOnly(2026, 9, 30);
        const string nullList = "{\"launcherNews\":null}";
        TestAssert.Equal(0, JsonSerializer.Deserialize<PlatformSettings>(nullList, HostFiles.Json)!.LauncherNews.Count, "a null launcherNews loads as empty");
        TestAssert.Equal(0, PlatformControls.From(JsonSerializer.Deserialize<PlatformSettings>(nullList, HostFiles.Json)!).LauncherNews.Count, "the live controls start empty");

        var long100 = new string('L', 100);
        var hostile = JsonSerializer.Deserialize<PlatformSettings>(JsonSerializer.Serialize(new
        {
            launcherNews = new object?[]
            {
                null,
                new { title = "He said \"hi\"", link = "http://a.b/1", saved = "2026-09-30" },
                new { title = long100 + "1", link = "http://a.b/2", saved = "2026-09-30" },
                new { title = long100 + "2", link = "http://a.b/3", saved = "2026-09-30" },
                new { title = long100 + "3", link = "http://a.b/4", saved = "2026-09-30" },
                new { title = long100 + "4", link = "http://a.b/5", saved = "2026-09-30" },
                new { title = "Six", link = (string?)null, saved = "2026-09-30" },
                new { title = "Seven", link = (string?)null, saved = "2026-09-30" },
            },
        }), HostFiles.Json)!;
        var body = LauncherNews.Body(hostile.LauncherNews, null, day);
        // Batch 766: two headlines, not three, since the blank items that fill the five share the budget (three and two blanks: 459 bytes).
        AssertBodyWithin(body, 2, "the stored list, cut to five and then to the budget, serves within 440 bytes");
        using (var json = JsonDocument.Parse(body))
            TestAssert.Equal("He said hi", json.RootElement.GetProperty("news")[0].GetProperty("title").GetString(), "the quotation mark is left out of the title");

        return HostSettingsChecks.OnSettingsPageAsync("league-launcher-stored", async (home, browser, accounts) =>
        {
            var file = JsonNode.Parse(File.ReadAllText(home.Loop.Paths.Settings))!.AsObject();
            file["launcherNews"] = null;
            File.WriteAllText(home.Loop.Paths.Settings, file.ToJsonString());
            Has(await GetPageAsync(browser, HostSettingsPage.PagePath), "253 bytes left of 440.");
            accounts.Controls = PlatformControls.From(hostile);
            using var launcher = home.Client();
            TestAssert.Equal(body, await launcher.GetStringAsync(LauncherNews.Route), "the launcher reads the same valid body");
        }, () => day);
    }

    /// <summary>
    /// Batch 761 (the classic launcher, section 6): the usual start puts the language back to English. SetLocale rewrites only the locale= line of
    /// AIR's locale.properties (added when missing; the endpoint lines stay); ResetLocaleCfg writes [General] LanguageLocaleRegion=en_US into the
    /// game's DATA\CFG\defaults\locale.cfg where that file exists and never creates it. The usual start's reset does both for a player's stage (an
    /// invitation) and the host app's stage (an AIR source); START-AIR's stage has neither and keeps its files (bytes and write time). A read-only
    /// file is logged and the other reset still runs.
    /// </summary>
    [Check("server.classic-launcher-english-reset")]
    public static void TheUsualStartPutsTheLanguageBackToEnglish()
    {
        var temp = TempFolder();
        var air = Path.Combine(temp, "air");
        var game = Path.Combine(temp, "game");
        var properties = Path.Combine(air, "locale.properties");
        var cfg = Path.Combine(game, "DATA", "CFG", "defaults", "locale.cfg");
        var exe = Path.Combine(game, "League of Legends.exe");
        const string launcherCfg = "[General]\r\nLanguageLocaleRegion=pl_PL\r\n";
        try
        {
            Directory.CreateDirectory(air);
            Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
            File.WriteAllText(properties, "host=127.0.0.1\r\nlocale=pl_PL\r\nlq_uri=http://127.0.0.1:8390/login-queue/rest/queue\r\n");
            AirOverlay.SetLocale(air);
            TestAssert.Equal("host=127.0.0.1\r\nlocale=en_US\r\nlq_uri=http://127.0.0.1:8390/login-queue/rest/queue\r\n", File.ReadAllText(properties),
                "locale=pl_PL becomes en_US and the endpoint lines stay");
            File.WriteAllText(properties, "locale=pl_PL");
            AirOverlay.SetLocale(air);
            TestAssert.Equal("locale=en_US\r\n", File.ReadAllText(properties), "a file that is only the locale line");
            File.WriteAllText(properties, "host=127.0.0.1\r\n");
            AirOverlay.SetLocale(air);
            TestAssert.Equal("host=127.0.0.1\r\nlocale=en_US\r\n", File.ReadAllText(properties), "no locale line: it is added");

            File.WriteAllText(cfg, launcherCfg);
            TestAssert.True(GameLanguagePack.ResetLocaleCfg(game), "the launcher's file is there: it is reset");
            TestAssert.Equal("[General]\r\nLanguageLocaleRegion=en_US\r\n", File.ReadAllText(cfg), "[General] and the English region, as the launcher writes it");
            var bare = Path.Combine(temp, "bare");
            TestAssert.False(GameLanguagePack.ResetLocaleCfg(bare), "no launcher file: nothing to reset");
            TestAssert.False(File.Exists(Path.Combine(bare, "DATA", "CFG", "defaults", "locale.cfg")), "and none is created");

            // The usual start's reset, for a player's stage (its invitation) and the host app's stage (its AIR source): both go back to English.
            var playerStage = new Amberline.AirCompanion.Program.DesktopStage(temp, air, exe, "", "", 8390, 8393,
                RemoteInvitation: new AirRemoteInvitation("one.example.test", 8391, "", new string('A', 64)));
            File.WriteAllText(properties, "host=127.0.0.1\r\nlocale=pl_PL\r\n");
            File.WriteAllText(cfg, launcherCfg);
            Amberline.AirCompanion.Program.ResetLanguageToEnglish(playerStage);
            TestAssert.Equal("host=127.0.0.1\r\nlocale=en_US\r\n", File.ReadAllText(properties), "a player's usual start: AIR's locale is English");
            TestAssert.Equal("[General]\r\nLanguageLocaleRegion=en_US\r\n", File.ReadAllText(cfg), "a player's usual start: the game's locale.cfg is English");
            var hostStage = new Amberline.AirCompanion.Program.DesktopStage(temp, air, exe, "", "", 8390, 8393, AirSource: Path.Combine(temp, "host-air"));
            File.WriteAllText(properties, "host=127.0.0.1\r\nlocale=pl_PL\r\n");
            File.WriteAllText(cfg, launcherCfg);
            Amberline.AirCompanion.Program.ResetLanguageToEnglish(hostStage);
            TestAssert.Equal("host=127.0.0.1\r\nlocale=en_US\r\n", File.ReadAllText(properties), "the host's own Play: AIR's locale is English");
            TestAssert.Equal("[General]\r\nLanguageLocaleRegion=en_US\r\n", File.ReadAllText(cfg), "the host's own Play: the game's locale.cfg is English");

            // START-AIR (the operator's stage: no invitation, no AIR source) keeps both files: the same bytes and the same write time.
            var operatorStage = new Amberline.AirCompanion.Program.DesktopStage(temp, air, exe, "", "", 8390, 8393);
            var stamp = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.WriteAllText(properties, "host=127.0.0.1\r\nlocale=pl_PL\r\n");
            File.WriteAllText(cfg, launcherCfg);
            File.SetLastWriteTimeUtc(properties, stamp);
            File.SetLastWriteTimeUtc(cfg, stamp);
            Amberline.AirCompanion.Program.ResetLanguageToEnglish(operatorStage);
            TestAssert.Equal("host=127.0.0.1\r\nlocale=pl_PL\r\n", File.ReadAllText(properties), "START-AIR: AIR's locale is left as it is");
            TestAssert.Equal(launcherCfg, File.ReadAllText(cfg), "START-AIR: the game's locale.cfg is left as it is");
            TestAssert.Equal(stamp, File.GetLastWriteTimeUtc(properties), "START-AIR: locale.properties is not written");
            TestAssert.Equal(stamp, File.GetLastWriteTimeUtc(cfg), "START-AIR: locale.cfg is not written");

            // A read-only locale.properties is logged and left alone; the game's locale.cfg is still reset, and nothing throws.
            File.WriteAllText(cfg, launcherCfg);
            File.WriteAllText(properties, "host=127.0.0.1\r\nlocale=pl_PL\r\n");
            File.SetAttributes(properties, FileAttributes.ReadOnly);
            Amberline.AirCompanion.Program.ResetLanguageToEnglish(playerStage);
            TestAssert.Equal("host=127.0.0.1\r\nlocale=pl_PL\r\n", File.ReadAllText(properties), "a read-only locale.properties is left as it is");
            TestAssert.Equal("[General]\r\nLanguageLocaleRegion=en_US\r\n", File.ReadAllText(cfg), "and the game's locale.cfg is still reset");
        }
        finally
        {
            if (File.Exists(properties)) File.SetAttributes(properties, FileAttributes.Normal);
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>
    /// Batch 761, task 8 (design §1, §9): the host's optional 2011 launcher folder. The folder check takes a folder that passes
    /// ClassicLauncherFiles.Problem (the release's files and the pinned EXE, here the dummy's own hash) and refuses one inside an Amberline
    /// installation; Play on this PC's stage names the folder only when it is accepted; the Settings folder page shows the optional row,
    /// refuses a picked folder that fails until it is cleared, saves an accepted one in platform.json (Amberline restarts into it), and
    /// Stop using this folder saves nothing by itself. Dummy files only; no launcher is started.
    /// </summary>
    [Check("server.classic-launcher-host", TimeoutSeconds = 240)]
    public static void TheHostsLauncherFolderIsOptionalCheckedAndNamedInItsStage() => HostLauncherAsync().GetAwaiter().GetResult();

    private static async Task HostLauncherAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var titles = new List<string>();
        var picks = new Queue<string>();
        string game = "", air = "", good = "", broken = "", inside = "", pin = "";
        var home = HostModeChecks.Home.Create("league-launcher-host", root =>
        {
            game = HostFolderChecks.Game(Path.Combine(root, "host-game", "projects", "lol_game_client", "releases", "0.0.0.51"));
            air = HostFolderChecks.Air(Path.Combine(root, "host-air"));
            good = Path.Combine(root, "downloads", "launcher");
            pin = WriteLauncherFiles(good);
            broken = Path.Combine(root, "downloads", "broken");
            WriteLauncherFiles(broken);
            File.Delete(Path.Combine(broken, "xml", "slideshow.xml"));
            // A folder inside an Amberline installation: the folder named platform-data is what marks one (FolderMove.HostInstallation).
            Directory.CreateDirectory(Path.Combine(root, "Amberline", "platform-data"));
            inside = Path.Combine(root, "Amberline", "launcher");
            WriteLauncherFiles(inside);
            return HostFolderChecks.Pins(game, air) with
            {
                LauncherSha256 = pin,
                Choose = title => { lock (titles) titles.Add(title); return picks.Dequeue(); },
            };
        });
        var setup = home.Loop.Folders;
        try
        {
            // The folder check: the release's files with their pin pass; the real pin refuses the dummy with the check's sentence.
            var accepted = HostFolders.Check(FolderKind.Launcher, good, setup);
            TestAssert.True(accepted.Ok && accepted.Folder == good, accepted.Problem ?? "the release's files with their own EXE's pin are accepted");
            TestAssert.Equal<string?>("The classic launcher is not version 1.0.1.19.", HostFolders.Check(FolderKind.Launcher, good, new()).Problem,
                "with the real pin the dummy EXE is refused, with the check's sentence");
            TestAssert.Equal<string?>(@"The classic launcher folder is missing xml\slideshow.xml.", HostFolders.Check(FolderKind.Launcher, broken, setup).Problem,
                "a folder missing one of the files is refused with the check's sentence");
            var within = HostFolders.Check(FolderKind.Launcher, inside, setup);
            TestAssert.True(!within.Ok && within.Problem is { } sentence && sentence.Contains("inside an Amberline installation", StringComparison.Ordinal),
                $"a folder inside an installation is refused in a short sentence: {within.Problem}");
            var missing = HostFolders.Check(FolderKind.Launcher, Path.Combine(home.Root, "nowhere"), setup);
            TestAssert.True(!missing.Ok && missing.Folder is null, "a folder that is not there is refused, and no row names it");

            // Play on this PC's stage: launcherRoot only for an accepted folder, and no key at all otherwise.
            var paths = HostPaths.Resolve(Path.Combine(home.Root, "stage", "data"), Path.Combine(home.Root, "stage", "Local"), Path.Combine(home.Root, "stage", "app", "platform"));
            var settings = new PlatformSettings(game, null!, HttpPort: 8390, AirRoot: air) { LauncherRoot = good };
            TestAssert.Equal<string?>(good, StageLauncher(HostPlay.WriteStage(paths, settings, pin)), "an accepted folder: the stage names it");
            TestAssert.Equal<string?>(null, StageLauncher(HostPlay.WriteStage(paths, settings)), "the real pin refuses the dummy: the stage names none");
            TestAssert.Equal<string?>(null, StageLauncher(HostPlay.WriteStage(paths, settings with { LauncherRoot = null }, pin)), "no folder: the stage names none");

            // The Settings folder page: the optional row, then the save of its folder.
            HostInviteChecks.WriteSettings(home, new { serverName = "Launcher League", publicHost = "amberline.example.test", addressSource = HostAddress.Typed, dataRoot = game, airRoot = air });
            var run = Task.Run(() => HostMode.RunAsync(home.Loop));
            try
            {
                using var browser = home.Client();
                await HostModeChecks.WaitForHealthAsync(browser, "ready", run);
                await home.SignInAsync(browser);
                var settingsFile = home.Loop.Paths.Settings;

                // The optional row: not chosen, with its own window button and nothing to stop using.
                var folders = await FoldersPageAsync(browser);
                Has(folders, "<h2 id=launcher-title>2011 launcher (optional)</h2>", "Not chosen.", "name=choose value=launcher");
                TestAssert.False(folders.Contains("name=clear value=launcher", StringComparison.Ordinal), "nothing to stop using yet: " + folders);

                // A picked folder that fails its check is refused with the check's sentence (400), and nothing is saved.
                Has(await PostFoldersAsync(browser, HttpStatusCode.BadRequest, ("game", game), ("air", air), ("launcher", broken)),
                    @"The classic launcher folder is missing xml\slideshow.xml.", "Not usable:");
                TestAssert.Equal<string?>(null, LauncherRootSetting(settingsFile), "a refused folder is not saved");

                // Choose: the window's folder is checked and shown; choosing saves nothing by itself.
                picks.Enqueue(good);
                Has(await PostFoldersAsync(browser, HttpStatusCode.OK, ("game", game), ("air", air), ("choose", "launcher")),
                    "Found:</span> " + WebUtility.HtmlEncode(good), "name=clear value=launcher");
                TestAssert.True(titles.SequenceEqual(["Choose the 2011 launcher folder"]), string.Join(",", titles));
                TestAssert.Equal<string?>(null, LauncherRootSetting(settingsFile), "choosing saves nothing");

                // Save: platform.json names the folder, and Amberline restarts into it.
                Has(await PostFoldersAsync(browser, HttpStatusCode.OK, ("game", game), ("air", air), ("launcher", good)), "<h1>Starting Amberline 132</h1>");
                await HostModeChecks.WaitForStartsAsync(home.Loop, 2, run);
                await HostModeChecks.WaitForHealthAsync(browser, "ready", run);
                TestAssert.Equal<string?>(good, LauncherRootSetting(settingsFile), "the save names the folder in platform.json");

                // The next start of the page shows it again. Stop using this folder saves nothing; a save without the row clears the name.
                Has(await FoldersPageAsync(browser), "Found:</span> " + WebUtility.HtmlEncode(good), "name=clear value=launcher");
                Has(await PostFoldersAsync(browser, HttpStatusCode.OK, ("game", game), ("air", air), ("clear", "launcher")), "Not chosen.");
                TestAssert.Equal<string?>(good, LauncherRootSetting(settingsFile), "stop using it saves nothing by itself");
                await PostFoldersAsync(browser, HttpStatusCode.OK, ("game", game), ("air", air));
                await HostModeChecks.WaitForStartsAsync(home.Loop, 3, run);
                await HostModeChecks.WaitForHealthAsync(browser, "ready", run);
                TestAssert.Equal<string?>(null, LauncherRootSetting(settingsFile), "a save without the row clears the name");
                TestAssert.False(File.ReadAllText(settingsFile).Contains("launcherRoot", StringComparison.Ordinal), "and platform.json no longer holds the key");
            }
            finally
            {
                await home.StopAsync(run);
            }
        }
        finally
        {
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            HostModeChecks.Delete(home.Root);
        }
    }

    /// <summary>The launcher folder a stage names (its launcherRoot), or null; a stage that names none has no such key at all.</summary>
    private static string? StageLauncher(string stage)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(stage, "stage.json")));
        if (!json.RootElement.TryGetProperty("launcherRoot", out var value)) return null;
        TestAssert.True(value.ValueKind == JsonValueKind.String, "a stage writes launcherRoot as text, or not at all");
        return value.GetString();
    }

    /// <summary>The 2011 launcher folder platform.json names, or null when it names none.</summary>
    private static string? LauncherRootSetting(string settingsFile) => JsonNode.Parse(File.ReadAllText(settingsFile))!["launcherRoot"]?.GetValue<string>();

    /// <summary>The Settings folder page once its first look is done (a slow disk shows a note that comes back by itself first).</summary>
    private static async Task<string> FoldersPageAsync(HttpClient browser)
    {
        for (var tries = 0; tries < 40; tries++)
        {
            var page = await GetPageAsync(browser, HostSettingsPage.FoldersPath);
            if (page.Contains("<label for=paste>", StringComparison.Ordinal)) return page;
            await Task.Delay(250);
        }
        throw new TimeoutException($"{HostSettingsPage.FoldersPath} never showed its form");
    }

    /// <summary>Posts the Settings folder page as the browser does: its antiforgery token, then one field per input; the answer's status is checked.</summary>
    private static async Task<string> PostFoldersAsync(HttpClient browser, HttpStatusCode expected, params (string Name, string Value)[] fields)
    {
        var token = HostModeChecks.Token(await FoldersPageAsync(browser));
        using var answer = await browser.PostAsync(HostSettingsPage.FoldersPath, new FormUrlEncodedContent([token, .. fields.Select(field => KeyValuePair.Create(field.Name, field.Value))]));
        var page = await answer.Content.ReadAsStringAsync();
        TestAssert.Equal(expected, answer.StatusCode, page);
        return page;
    }
}
