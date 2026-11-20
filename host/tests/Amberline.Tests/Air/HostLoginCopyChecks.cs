using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amberline.Platform;
using static Amberline.Tests.Air.HostModeChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 830 (owner, 16 November 2026: "Yes, add it"): the 1.0.0.132 setup offers to copy the logins of a 1.0.0.126 Amberline on the same
/// PC, and the 1.0.0.126 setup never does. Everything is made up and throwaway: a fake 1.0.0.126 install (its pointer in a fake local
/// application data, the folder with its marker from <c>126/patch.json</c> "home", its <c>data</c> with a store and <c>platform.json</c>), a
/// temporary 132 home on free loopback ports. Never this PC's profile or a real install; no password or hash is ever printed.
/// </summary>
public static class HostLoginCopyChecks
{
    private const string OldPassword = "Synthetic-old-Pw-830", HerePassword = "Synthetic-here-Pw-830";

    [Check("server.host-copy-126-logins", TimeoutSeconds = 300)]
    public static void The132SetupCopiesThe126LoginsAndNothingElse() => CopyAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Batch 830 review: the real ACL read behind <see cref="CopyPlaces.OnlyYours"/> on this PC (<see cref="CopyPlaces.Private"/>), on throwaway
    /// folders: only this user, Administrators and SYSTEM is trusted; a write entry for Users on the folder, or one that only its children
    /// inherit (then the folder itself stays trusted, its file not), or a missing path is not.
    /// </summary>
    [Check("server.host-copy-trusts-only-private-folders")]
    public static void OnlyAFolderOthersCannotChangeIsTrusted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-host-copy-acl");
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var folder = Directory.CreateDirectory(Path.Combine(root, "Amberline"));
            // A new object each time: a security object writes only what changed since its own last write.
            DirectorySecurity Own()
            {
                var own = new DirectorySecurity();
                own.SetAccessRuleProtection(true, false);
                foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                             new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                    own.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
                return own;
            }
            folder.SetAccessControl(Own());
            var file = Path.Combine(folder.FullName, "accounts.json");
            File.WriteAllText(file, "{}");
            TestAssert.True(CopyPlaces.Private(folder.FullName) && CopyPlaces.Private(file), "only this user, Administrators and SYSTEM: trusted");

            var writable = folder.GetAccessControl();
            writable.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Write, AccessControlType.Allow));
            folder.SetAccessControl(writable);
            TestAssert.False(CopyPlaces.Private(folder.FullName), "Users may write in the folder: not trusted");
            folder.SetAccessControl(Own());
            TestAssert.True(CopyPlaces.Private(folder.FullName), "the write entry gone: trusted again");

            var inherited = folder.GetAccessControl();
            inherited.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify, Inherit, PropagationFlags.InheritOnly,
                AccessControlType.Allow));
            folder.SetAccessControl(inherited);
            TestAssert.True(CopyPlaces.Private(folder.FullName) && !CopyPlaces.Private(file), "an entry only children inherit: the folder stays trusted, its file is not");
            TestAssert.False(CopyPlaces.Private(Path.Combine(root, "missing")), "a missing path is not trusted");
            folder.SetAccessControl(Own());
        }
        finally { Delete(root); }
    }

    private const InheritanceFlags Inherit =
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    private static async Task CopyAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        // A new login takes its first champion from the champion list: read it the suite's way first (see HostStepsChecks).
        TestAssert.True(Amberline.Platform.AirGameDtos.ChampionNames.Count > 0, "the champion list loads");
        var home = Home.Create("league-host-copy");
        var pc = Path.Combine(home.Root, "pc");
        var oldData = Fake126(pc);
        var before = Tree(pc);
        // This server already has carl (another person, another password) before the copy.
        long carlHere;
        string carlSecret;
        using (var store = new PlatformAccounts(home.Loop.Paths.Data))
        {
            var carl = store.RegisterPublic("carl", HerePassword, "Carl Here");
            (carlHere, carlSecret) = (carl.Id, carl.Salt + carl.PasswordHash);
        }
        File.WriteAllText(home.Loop.Paths.Settings, JsonSerializer.Serialize(new
        {
            httpPort = home.Port, rtmpPort = home.RtmpPort, xmppPort = home.XmppPort, remotePort = home.RemotePort, bindAddress = "127.0.0.1",
            publicHost = "127.0.0.1", addressSource = HostAddress.Typed, dataRoot = home.Game, airRoot = home.Air, serverName = "Test Server",
        }));
        home.Loop.CopyFrom = new CopyPlaces(Path.Combine(pc, "Local"), Path.Combine(pc, "C"), _ => true);
        var line132 = WorkspaceFiles.Patch == "132";
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = home.Client();
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            await NextAsync(browser, "/host/setup/login");
            if (!line132)
            {
                // The 1.0.0.126 line: no offer on the step or on Users, and no copy route, although a 1.0.0.126 install is there.
                foreach (var path in new[] { "/host/setup/login", HostInvite.UsersPath })
                    TestAssert.False((await browser.GetStringAsync(path)).Contains("Copy the logins", StringComparison.Ordinal), path + " offers no copy on 1.0.0.126");
                foreach (var path in new[] { HostLoginCopy.SetupPath, HostLoginCopy.UsersPath, HostLoginCopy.OwnPath })
                    using (var refused = await PostAsync(browser, "/host/setup/login", path))
                        TestAssert.Equal(HttpStatusCode.NotFound, refused.StatusCode, path + " is no route on 1.0.0.126");
            }
            else
            {
                // Batch 830 review: found only at the drive's root (no pointer of this user's), the install counts only while its folders and,
                // through the handle each is read from, its store files are this user's (CopyPlaces.OnlyYours); else the step and the copy refuse
                // in plain words and copy nothing.
                var install = Path.GetDirectoryName(oldData)!;
                var asked = 0;
                var trusted = false;
                home.Loop.CopyFrom = new CopyPlaces(Path.Combine(pc, "Elsewhere"), Path.Combine(pc, "C"), _ => { Interlocked.Increment(ref asked); return trusted; });
                const string Others = "so its logins are not copied. Update it to 0.1.26 or later and start it once.";
                await RefusedAsync(browser, $"Other people who use this PC can change the files of your Amberline for 1.0.0.126 in {install}, {Others}", "the drive's root, changeable by others");
                trusted = true;
                Interlocked.Exchange(ref asked, 0);
                TestAssert.True((await browser.GetStringAsync("/host/setup/login")).Contains("4 logins found, yours among them.", StringComparison.Ordinal),
                    "the drive's root, this user's alone: offered");
                TestAssert.Equal(4, asked, "the ACLs asked: the install and data folders, then the two store files' open handles");
                if (OperatingSystem.IsWindows())
                {
                    // The real reading: folders and files private to this user are offered; a store file whose own ACL lets Users write is refused,
                    // read from the handle the file is parsed from.
                    home.Loop.CopyFrom = new CopyPlaces(Path.Combine(pc, "Elsewhere"), Path.Combine(pc, "C"), CopyPlaces.Private);
                    using var identity = WindowsIdentity.GetCurrent();
                    foreach (var folder in new[] { install, oldData }) new DirectoryInfo(folder).SetAccessControl(PrivateTo<DirectorySecurity>(identity.User!));
                    foreach (var file in Directory.GetFiles(install, "*", SearchOption.AllDirectories)) new FileInfo(file).SetAccessControl(PrivateTo<FileSecurity>(identity.User!));
                    TestAssert.True((await browser.GetStringAsync("/host/setup/login")).Contains("4 logins found, yours among them.", StringComparison.Ordinal),
                        "the drive's root, private by its real ACLs: offered");
                    var writable = PrivateTo<FileSecurity>(identity.User!);
                    writable.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Write, AccessControlType.Allow));
                    new FileInfo(Path.Combine(oldData, "accounts.json")).SetAccessControl(writable);
                    await RefusedAsync(browser, $"Other people who use this PC can change the files of your Amberline for 1.0.0.126 in {install}, {Others}", "accounts.json writable by Users");
                }
                // Batch 830 review: never through a link, on any route; here this user's own pointer names an install whose data is a junction.
                var (name126, marker126) = Home126();
                var linked = Path.Combine(home.Root, "linked");
                var linkedInstall = Path.Combine(linked, "Games", name126);
                Directory.CreateDirectory(linkedInstall);
                File.WriteAllText(Path.Combine(linkedInstall, marker126), "{}");
                CopyTree(oldData, Path.Combine(linked, "elsewhere"));
                Junction(Path.Combine(linkedInstall, "data"), Path.Combine(linked, "elsewhere"));
                Directory.CreateDirectory(Path.Combine(linked, "Local", name126));
                File.WriteAllText(Path.Combine(linked, "Local", name126, "install.json"), JsonSerializer.Serialize(new { folder = linkedInstall }));
                home.Loop.CopyFrom = new CopyPlaces(Path.Combine(linked, "Local"), null, _ => true);
                await RefusedAsync(browser, $"Your Amberline for 1.0.0.126 in {linkedInstall} contains a link to another place, " +
                    "so its logins are not copied.", "a junction data folder");
                // Batch 857 (design step 5, route (b)): the install this user's own pointer names counts only while it is locked, as the drive's root's;
                // not locked, the page says what to do. Locked as that line's own start locks it (the same InstallLock), it is offered, and this
                // line changes none of its permissions.
                home.Loop.CopyFrom = new CopyPlaces(Path.Combine(pc, "Local"), Path.Combine(pc, "C"), _ => false);
                await RefusedAsync(browser, $"Other people who use this PC can change the files of your Amberline for 1.0.0.126 in {install}, {Others}", "the pointer's install, not locked");
                if (OperatingSystem.IsWindows())
                {
                    InstallLockChecks.Hazard(Path.Combine(pc, "C"));
                    Amberline.AirCompanion.InstallLock.Restamp(install, null);
                    home.Loop.CopyFrom = new CopyPlaces(Path.Combine(pc, "Local"), Path.Combine(pc, "C"), CopyPlaces.Private);
                    await RefusedAsync(browser, $"Other people who use this PC can change the files of your Amberline for 1.0.0.126 in {install}, {Others}", "the pointer's install, open to the drive's users");
                    TestAssert.Equal(null, Amberline.AirCompanion.InstallLock.Lock(install, top: pc), "the 1.0.0.126 start's own lock");
                    var locked = InstallLockChecks.Sddl(install);
                    TestAssert.True((await browser.GetStringAsync("/host/setup/login")).Contains("4 logins found, yours among them.", StringComparison.Ordinal),
                        "the pointer's install, locked: offered");
                    TestAssert.Equal(locked, InstallLockChecks.Sddl(install), "this line changed no permission of the 1.0.0.126 install");
                }
                home.Loop.CopyFrom = new CopyPlaces(Path.Combine(pc, "Local"), Path.Combine(pc, "C"), _ => true);

                // While the 1.0.0.126 Amberline runs (its store's lease held), the step says so and nothing is read or copied.
                using (new FileStream(Path.Combine(oldData, "accounts.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var page = await browser.GetStringAsync("/host/setup/login");
                    TestAssert.True(page.Contains("Your Amberline for 1.0.0.126 is running. Stop it, then open this page again", StringComparison.Ordinal) &&
                        !page.Contains("Copy the logins from", StringComparison.Ordinal) && page.Contains("<button type=submit>Continue</button>", StringComparison.Ordinal),
                        "running: the step says so and keeps its own form: " + page);
                    using var refused = await PostAsync(browser, "/host/setup/login", HostLoginCopy.SetupPath);
                    var text = await refused.Content.ReadAsStringAsync();
                    TestAssert.Equal(HttpStatusCode.Conflict, refused.StatusCode, text);
                    TestAssert.True(text.Contains("is running. Stop it", StringComparison.Ordinal), text);
                }
                // Users offers the same logins (no "yours" there); the step names the host's own among them. 4: host_old, ann, bob, dave; carl
                // and gus have a name this server has, eve's row is damaged.
                TestAssert.True((await browser.GetStringAsync(HostInvite.UsersPath)).Contains(
                    "<p>Copy the logins from your Amberline for 1.0.0.126: 4 logins found.</p>", StringComparison.Ordinal), "Users offers the copy");
                var step = await browser.GetStringAsync("/host/setup/login");
                TestAssert.True(step.Contains("<p>Copy the logins from your Amberline for 1.0.0.126: 4 logins found, yours among them.</p>", StringComparison.Ordinal) &&
                    step.Contains($"<form method=post action={HostLoginCopy.SetupPath}>", StringComparison.Ordinal) &&
                    step.Contains("<h2>Or make a new login</h2>", StringComparison.Ordinal) && step.Contains("<button type=submit>Continue</button>", StringComparison.Ordinal),
                    "the step offers the copy beside its own form: " + step);
                using (var forged = await browser.PostAsync(HostLoginCopy.SetupPath, new FormUrlEncodedContent(new Dictionary<string, string>())))
                    TestAssert.Equal(HttpStatusCode.BadRequest, forged.StatusCode, "the copy needs the form's antiforgery token");

                using (var copied = await PostAsync(browser, "/host/setup/login", HostLoginCopy.SetupPath))
                {
                    var text = await copied.Content.ReadAsStringAsync();
                    TestAssert.Equal(HttpStatusCode.OK, copied.StatusCode, text);
                    foreach (var part in new[]
                             {
                                 "Step 3 of 4", "<p class=notice role=status>4 logins copied from your Amberline for 1.0.0.126.</p>",
                                 "<p>Not copied, because a login here already has the username or summoner name: carl, gus.</p>",
                                 "<p class=warn>1 login could not be read and was not copied.</p>",
                                 $"<form method=post action={HostLoginCopy.OwnPath} novalidate>",
                                 "<p>Your login <b>host_old</b> was copied from your Amberline for 1.0.0.126. Type its password to make it your own login here.</p>",
                                 $"<a class=button href={HostSteps.Start}>Continue</a>",
                             })
                        TestAssert.True(text.Contains(part, StringComparison.Ordinal), $"the result says {part}: {text}");
                    TestAssert.False(text.Contains("is ready", StringComparison.Ordinal), "no login becomes the host's own by the copy: " + text);
                }
                // Batch 830 review: the copied host's login is an ordinary user until the host types its password; the step offers that.
                TestAssert.False(File.ReadAllText(home.Loop.Paths.Settings).Contains("hostAccount", StringComparison.Ordinal), "the copy sets no host login");
                await NextAsync(browser, "/host/setup/login");
                var own = await browser.GetStringAsync("/host/setup/login");
                TestAssert.True(own.Contains($"<form method=post action={HostLoginCopy.OwnPath} novalidate>", StringComparison.Ordinal) &&
                    !own.Contains("Copy the logins from", StringComparison.Ordinal) && own.Contains("<h2>Or make a new login</h2>", StringComparison.Ordinal),
                    "the step asks for the copied login's password: " + own);
                using (var forged = await browser.PostAsync(HostLoginCopy.OwnPath, new FormUrlEncodedContent(new Dictionary<string, string> { ["password"] = OldPassword })))
                    TestAssert.Equal(HttpStatusCode.BadRequest, forged.StatusCode, "the password form needs its antiforgery token");
                const string Wrong = "Synthetic-wrong-Pw-830";
                using (var wrong = await PostAsync(browser, "/host/setup/login", HostLoginCopy.OwnPath, ("password", Wrong)))
                {
                    var text = await wrong.Content.ReadAsStringAsync();
                    TestAssert.True(wrong.StatusCode == HttpStatusCode.BadRequest && text.Contains("This is not the password of that login.", StringComparison.Ordinal) &&
                        !text.Contains(Wrong, StringComparison.Ordinal), $"a wrong password is refused and never shown back ({wrong.StatusCode})");
                }
                TestAssert.False(File.ReadAllText(home.Loop.Paths.Settings).Contains("hostAccount", StringComparison.Ordinal), "a wrong password sets no host login");
                await NextAsync(browser, "/host/setup/login");
                using (var right = await PostAsync(browser, "/host/setup/login", HostLoginCopy.OwnPath, ("password", OldPassword)))
                    TestAssert.True(right.StatusCode == HttpStatusCode.Redirect && right.Headers.Location?.OriginalString == HostSteps.Start,
                        $"the right password makes it the host's own ({right.StatusCode})");
                // The host's own login is done, so the steps go on to the address (here already saved: the dashboard).
                await NextAsync(browser, HostDashboard.PagePath);
                TestAssert.False((await browser.GetStringAsync(HostInvite.UsersPath)).Contains("Copy the logins", StringComparison.Ordinal), "everything copied: no offer");

                // Each copied login signs in with its 1.0.0.126 password; the disabled one stays out; carl keeps this server's.
                foreach (var (user, password, status) in new[]
                         {
                             ("host_old", OldPassword, HttpStatusCode.OK), ("ann", OldPassword, HttpStatusCode.OK), ("dave", OldPassword, HttpStatusCode.OK),
                             ("bob", OldPassword, HttpStatusCode.Forbidden), ("carl", OldPassword, HttpStatusCode.Forbidden), ("carl", HerePassword, HttpStatusCode.OK),
                             ("eve", OldPassword, HttpStatusCode.Forbidden),
                         })
                    using (var login = await LoginAsync(browser, user, password))
                        TestAssert.Equal(status, login.StatusCode, $"{user} signs in as expected");

                // Users: ann deleted here is offered again, and the copy from Users lists every other one as already here.
                var users = await browser.GetStringAsync(HostInvite.UsersPath);
                var ann = Regex.Match(users, "<b>ann</b>.*?name=accountId value=(\\d+)>", RegexOptions.Singleline).Groups[1].Value;
                TestAssert.True(users.Contains("<b>bob</b> <span class=tip>Bob There</span> <span class=mark>Disabled</span>", StringComparison.Ordinal) && ann.Length > 0, "Users lists them: " + users);
                using (var deleted = await PostAsync(browser, HostInvite.UsersPath, HostInvite.UsersPath + "/delete", ("accountId", ann)))
                    TestAssert.Equal(HttpStatusCode.OK, deleted.StatusCode, await deleted.Content.ReadAsStringAsync());
                TestAssert.True((await browser.GetStringAsync(HostInvite.UsersPath)).Contains(
                    "<p>Copy the logins from your Amberline for 1.0.0.126: 1 login found.</p>", StringComparison.Ordinal), "the deleted login is offered again");
                using (var again = await PostAsync(browser, HostInvite.UsersPath, HostLoginCopy.UsersPath))
                {
                    var text = await again.Content.ReadAsStringAsync();
                    TestAssert.Equal(HttpStatusCode.OK, again.StatusCode, text);
                    TestAssert.True(text.Contains("<p class=notice role=status>1 login copied from your Amberline for 1.0.0.126.</p>", StringComparison.Ordinal) &&
                        text.Contains("<p>Not copied, because a login here already has the username or summoner name: host_old, bob, carl, gus, dave.</p>", StringComparison.Ordinal) &&
                        !text.Contains("is ready", StringComparison.Ordinal) && text.Contains($"<a class=button href={HostInvite.UsersPath}>Back to Users</a>", StringComparison.Ordinal) &&
                        text.Contains("<nav class=side", StringComparison.Ordinal), "the copy from Users: " + text);
                }
            }
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }

        // The 1.0.0.126 install is as it was, file for file, and nothing was added to it (names only in the message).
        var after = Tree(pc);
        TestAssert.Equal(string.Join(", ", before.Keys.Order(StringComparer.Ordinal)), string.Join(", ", after.Keys.Order(StringComparer.Ordinal)), "the 1.0.0.126 install's files");
        foreach (var (file, hash) in before)
            TestAssert.True(after[file].SequenceEqual(hash), file + " of the 1.0.0.126 install changed");

        using (var store = new PlatformAccounts(home.Loop.Paths.Data))
        {
            var accounts = store.ListAccounts();
            var carl = accounts.Single(account => account.Username == "carl");
            TestAssert.True(carl.Id == carlHere && carl.Salt + carl.PasswordHash == carlSecret && carl.Summoner == "Carl Here", "this server's carl is untouched");
            if (!line132)
            {
                TestAssert.Equal(1, accounts.Length, "1.0.0.126 copied nothing");
                Delete(home.Root);
                return;
            }
            TestAssert.Equal("ann, bob, carl, dave, host_old", string.Join(", ", accounts.Select(account => account.Username).Order(StringComparer.Ordinal)), "the logins here");
            using (var json = JsonDocument.Parse(File.ReadAllText(home.Loop.Paths.Settings)))
                TestAssert.Equal(accounts.Single(account => account.Username == "host_old").Id, json.RootElement.GetProperty("hostAccount").GetInt64(),
                    "the host's own login there is the host's own here");
            // New ids from this store, in its order (carl had the first); the summoner names as they were there.
            TestAssert.Equal("2 4 5 6", string.Join(" ", accounts.Where(account => account.Id != carl.Id).Select(account => account.Id).Order()),
                "the copied logins' ids (ann copied again after her delete)");
            foreach (var account in accounts) TestAssert.Equal(1_000_000 + account.Id, account.SummonerId, account.Username + "'s summoner id");
            foreach (var (user, summoner) in new[] { ("host_old", "Old Host"), ("ann", "Ann There"), ("bob", "Bob There"), ("dave", "Dave Older") })
                TestAssert.Equal(summoner, accounts.Single(account => account.Username == user).Summoner, user + "'s summoner name");
            TestAssert.True(accounts.Single(account => account.Username == "bob").AccessRemovedAt is not null, "bob stays disabled");
            TestAssert.True(accounts.Single(account => account.Username == "ann").SignedUp, "ann keeps her Signed up mark");
            TestAssert.True(accounts.Where(account => account.Username != "bob").All(account => account.AccessRemovedAt is null), "the others can sign in");
            // Progress is a new login's: ann (a level-1 login there with IP and champions) as a new level-1 login, the others as a new level-30 one.
            var fresh = store.RegisterPublic("ref_fresh", HerePassword, "Ref Fresh", maxed: false);
            var maxed = store.RegisterPublic("ref_maxed", HerePassword, "Ref Maxed", maxed: true);
            foreach (var account in store.ListAccounts().Where(account => account.Username is "host_old" or "ann" or "bob" or "dave"))
                TestAssert.Equal(Shape(account.Username == "ann" ? fresh : maxed), Shape(account), account.Username + " starts as a new login");
        }
        // No password here in plain text (the store keeps the hash), and the window never names one.
        foreach (var file in Directory.GetFiles(home.Loop.Paths.Data, "*", SearchOption.AllDirectories))
            foreach (var secret in new[] { OldPassword, HerePassword })
                TestAssert.True(File.ReadAllBytes(file).AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) < 0, "a password is in " + Path.GetFileName(file));
        TestAssert.False(home.Screen.ToString().Contains(OldPassword, StringComparison.Ordinal), "the window shows a password");
        Delete(home.Root);
    }

    /// <summary>
    /// The fake 1.0.0.126 install under <paramref name="pc"/>: its default folder <c>C\&lt;name&gt;</c> with its marker, which
    /// <c>Local\&lt;name&gt;\install.json</c> names, and its data: host_old (the host's own, level 30), ann (level 1 with progress, signed up), bob (disabled), carl (a username this
    /// server has), gus (a summoner name it has, in other case), dave (an older row: in-record LastSeen, no optional fields), eve (a damaged salt).
    /// </summary>
    private static string Fake126(string pc)
    {
        var (name, marker) = Home126();
        var install = Path.Combine(pc, "C", name);
        var data = Path.Combine(install, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(install, marker), "{}");
        Directory.CreateDirectory(Path.Combine(pc, "Local", name));
        File.WriteAllText(Path.Combine(pc, "Local", name, "install.json"), JsonSerializer.Serialize(new { folder = install }));
        long host;
        using (var store = new PlatformAccounts(data))
        {
            host = store.RegisterPublic("host_old", OldPassword, "Old Host").Id;
            store.RegisterPublic("ann", OldPassword, "Ann There", maxed: false, signedUp: true);
            store.RemoveAccess(store.RegisterPublic("bob", OldPassword, "Bob There").Id);
            store.RegisterPublic("carl", OldPassword, "Carl There");
            store.RegisterPublic("gus", OldPassword, "carl here");
            store.RegisterPublic("dave", OldPassword, "Dave Older");
            store.RegisterPublic("eve", OldPassword, "Eve There");
        }
        var path = Path.Combine(data, "accounts.json");
        var state = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        foreach (var row in state["Accounts"]!.AsArray().Select(node => node!.AsObject()))
            switch (row["Username"]!.GetValue<string>())
            {
                case "host_old":
                    row["ProfileIconId"] = 7;
                    row["RunePages"] = new JsonArray(row["RunePages"]!.AsArray()[0]!.DeepClone());
                    break;
                case "ann":
                    row["Economy"]!["Ip"] = 5000;
                    row["Economy"]!["Level"] = 12;
                    row["Economy"]!["OwnedChampions"] = new JsonArray(1, 2);
                    break;
                case "dave":
                    foreach (var optional in new[] { "ProfileIconId", "TutorialFlag", "HelpFlag", "RunePages", "Economy", "LeavesClearedAt", "Dodges" }) row.Remove(optional);
                    row["LastSeen"] = "2026-11-01T12:00:00+00:00";
                    break;
                case "eve":
                    row["Salt"] = "not hexadecimal";
                    break;
            }
        File.WriteAllText(path, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(data, "platform.json"), JsonSerializer.Serialize(new { httpPort = 8390, hostAccount = host, serverName = "Old Server" }));
        return data;
    }

    /// <summary>The 1.0.0.126 line's home install name and marker (<c>126/patch.json</c> "home"), on either line.</summary>
    internal static (string Name, string Marker) Home126()
    {
        using var facts = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(WorkspaceFiles.PatchFolder)!, "126", "patch.json")));
        return (facts.RootElement.GetProperty("home").GetProperty("name").GetString()!, facts.RootElement.GetProperty("home").GetProperty("marker").GetString()!);
    }

    /// <summary>The step and the copy both refuse with <paramref name="message"/> and offer nothing.</summary>
    private static async Task RefusedAsync(HttpClient browser, string message, string what)
    {
        var step = await browser.GetStringAsync("/host/setup/login");
        TestAssert.True(step.Contains($"<p class=warn>{message}</p>", StringComparison.Ordinal) && !step.Contains("Copy the logins from", StringComparison.Ordinal),
            $"{what}: the step refuses: {step}");
        using var refused = await PostAsync(browser, "/host/setup/login", HostLoginCopy.SetupPath);
        var text = await refused.Content.ReadAsStringAsync();
        TestAssert.True(refused.StatusCode == HttpStatusCode.Conflict && text.Contains(message, StringComparison.Ordinal), $"{what}: the copy refuses ({refused.StatusCode}): {text}");
    }

    /// <summary>A protected ACL: full control for <paramref name="user"/>, Administrators and SYSTEM only (a folder's passed on to its children).</summary>
    internal static T PrivateTo<T>(SecurityIdentifier user) where T : FileSystemSecurity, new()
    {
        var security = new T();
        security.SetAccessRuleProtection(true, false);
        var inherit = security is DirectorySecurity ? Inherit : InheritanceFlags.None;
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
    }

    /// <summary>A directory junction (no privilege needed, unlike a symbolic link) at <paramref name="link"/> to <paramref name="target"/>.</summary>
    internal static void Junction(string link, string target)
    {
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target }, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        mklink.StandardOutput.ReadToEnd();
        mklink.WaitForExit();
        TestAssert.True(mklink.ExitCode == 0 && Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is not null, "the junction was made: " + mklink.StandardError.ReadToEnd());
    }

    /// <summary>Every file under <paramref name="root"/> by its relative path, with its SHA-256 (compared, never printed).</summary>
    internal static Dictionary<string, byte[]> Tree(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(file => Path.GetRelativePath(root, file), file => SHA256.HashData(File.ReadAllBytes(file)), StringComparer.Ordinal);

    /// <summary>What a new login gets, apart from its names and id: the loadout, the rune pages (without their times), economy and flags.</summary>
    private static string Shape(PlatformAccount account) => JsonSerializer.Serialize(new
    {
        Loadout = account.Loadout with { Name = "" },
        Pages = PlatformAccounts.RunePagesFor(account).Select(page => page with { CreatedAt = default }),
        account.Economy, account.ProfileIconId, account.TutorialFlag, account.HelpFlag, account.LeavesClearedAt, account.Dodges,
    }, Amberline.Server.MatchRoster.JsonOptions);

    private static async Task NextAsync(HttpClient client, string next)
    {
        using var answer = await client.GetAsync(HostSteps.Start);
        TestAssert.Equal(next, answer.Headers.Location?.OriginalString, "the next first-run step");
    }

    /// <summary>Posts to <paramref name="path"/> with the antiforgery token of the page at <paramref name="from"/>, as the browser would.</summary>
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string from, string path, params (string Name, string Value)[] fields)
    {
        var token = Token(await client.GetStringAsync(from));
        var form = new Dictionary<string, string> { [token.Key] = token.Value };
        foreach (var (name, value) in fields) form[name] = value;
        return await client.PostAsync(path, new FormUrlEncodedContent(form));
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string user, string password) =>
        client.PostAsync(Amberline.Platform.Program.LoginQueuePath,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["payload"] = $"user={user},password={password}" }));
}
