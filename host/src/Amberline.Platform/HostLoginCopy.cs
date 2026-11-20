using System.Net;
using System.Security.AccessControl;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;

namespace Amberline.Platform;

/// <summary>
/// The other patch line's home install whose logins this setup offers to copy (<see cref="HostLoginCopy"/>): that line's
/// <c>FolderMove.HostName</c> and <c>InstallMarker</c>, and the patch the pages name. Only the 1.0.0.132 line's Program.cs passes one
/// (its <c>PlatformLine.CopyLoginsFrom</c>, the 1.0.0.126 line's); the 1.0.0.126 line's offers no copy. Batch 856: <paramref name="RemotePort"/>,
/// that line's default friends' port, where <see cref="HostOldShortcuts"/> answers its old shortcuts (its <c>platform.json</c> may name another).
/// Batch 857: <paramref name="LocksFrom"/>, that line's first release whose start locks its install (<see cref="Folders.InstallLock"/>), which the
/// pages name while its install is not locked.
/// </summary>
public sealed record EarlierHome(string Name, string Marker, string Patch, int RemotePort, string LocksFrom);

/// <summary>
/// Where <see cref="HostLoginCopy"/> looks for the other line's install: the user's local application data (its pointer, and its data from
/// before batch 637) and the drive of its default folder. <see cref="Nowhere"/>, every loop's default, looks nowhere, so a check never reads
/// this PC's; only <see cref="HostMode.MainAsync"/> passes <see cref="ThisPc"/>. <paramref name="OnlyYours"/> says whether a folder's or an open
/// file's security may be trusted (<see cref="Private(FileSystemSecurity)"/> on this PC; a check passes its own); null trusts nothing found.
/// Batch 857: it is asked on every route, the pointer's included.
/// </summary>
public sealed record CopyPlaces(string? LocalAppData, string? Drive, Func<FileSystemSecurity, bool>? OnlyYours = null)
{
    public static CopyPlaces Nowhere { get; } = new(null, null);

    /// <summary>This user's local application data and <c>C:\</c>, the drive of <see cref="HostInstall.DefaultFolder"/>, read by its ACL.</summary>
    public static CopyPlaces ThisPc() => new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"C:\", Private);

    /// <summary>As <see cref="Private(FileSystemSecurity)"/>, for the file or folder at <paramref name="path"/>; a path that cannot be read is not trusted.</summary>
    public static bool Private(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return Private(Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or SystemException) { return false; }
    }

    /// <summary>
    /// Batch 830 (review): whether <paramref name="security"/> has this Windows user, the Administrators group or SYSTEM as its owner, and no allow
    /// entry lets anyone else write, delete or change its permissions (entries that only pass on to children are left out: each file read is
    /// asked itself). Anything else, or not Windows, is not trusted. Batch 857: the install lock's own check (<see cref="Folders.InstallLock"/>),
    /// so a locked install passes it.
    /// </summary>
    public static bool Private(FileSystemSecurity security) => Folders.InstallLock.Private(security);
}

/// <summary>
/// NOT RETAIL (batch 830, owner 16 November 2026: asked "Should 132 setup offer to copy logins and users from a 126 install?", "Yes, add
/// it"; retail's logins lived in Riot's account databases): the own-login step and Users offer to copy the logins of the other line's
/// Amberline on this PC, while its store holds a login this one lacks. Its install is found as that line finds it: the pointer in this user's
/// local application data naming a folder with its marker, its default folder with the marker, then its data from before batch 637 in this
/// user's local application data; the store is that folder's <c>data\accounts.json</c> and the host's own login there its
/// <c>platform.json</c> <c>hostAccount</c>. Batch 830 review: any user may make a folder at the drive's root, so an install counts while its
/// folders, and each store file through the very handle it is read from, are this user's (<see cref="CopyPlaces.OnlyYours"/>); otherwise the page
/// refuses in plain words. Batch 857 (design step 5, owner 17 November 2026: that line locks itself): on every route, the one this user's own
/// pointer names included; an install that is not locked yet is told to update to <see cref="EarlierHome.LocksFrom"/> and start once, which locks
/// it (<see cref="Folders.InstallLock"/>). This line only reads it and never changes its permissions. On every route the install folder, the data
/// folder and the store files must not be links (a reparse point or a link target). Those files are only read, opened so their Amberline could still replace them, and never while it runs (its store's lease,
/// <c>accounts.lock</c>, is held): then the page says so and the host tries again. What is copied is <see cref="PlatformAccounts.CopiedLogin"/>;
/// progress stays there. Every copied login is an ordinary user: the host's own login there becomes the host's own here only once the host
/// types its password on the own-login step (<see cref="OwnPath"/>), checked against the copied hash.
/// </summary>
public sealed class HostLoginCopy(HostLoop loop, PlatformAccounts accounts, EarlierHome from)
{
    public const string SetupPath = "/host/setup/copy", UsersPath = "/host/users/copy", OwnPath = "/host/setup/own";

    /// <summary>The other line's store: its usable logins, the host's own login there, the rows it could not read; or why it is not read (it runs, others can change it, or it is reached through a link).</summary>
    internal sealed record Found(IReadOnlyList<PlatformAccounts.CopiedLogin> Logins, PlatformAccounts.CopiedLogin? Host, int Unreadable, string? Refused = null);

    public void Map(WebApplication app)
    {
        app.MapPost(SetupPath, (HttpContext context, IAntiforgery antiforgery) => CopyAsync(context, antiforgery, setup: true));
        app.MapPost(UsersPath, (HttpContext context, IAntiforgery antiforgery) => CopyAsync(context, antiforgery, setup: false));
        app.MapPost(OwnPath, OwnAsync);
    }

    /// <summary>
    /// The offer on the own-login step (<paramref name="setup"/>) or on Users: the logins this store lacks, and on the step whether the host's own
    /// is one of them; empty when there are none (so it is offered until they are copied), a note while that Amberline runs or others can
    /// change it. On the step, once the host's own login there was copied, the form that makes it the host's own here with its password.
    /// </summary>
    internal string Offer(string token, bool setup)
    {
        var found = Find();
        if (found?.Refused is { } refused) return $"<p class=warn>{refused}</p>";
        var missing = found?.Logins.Where(login => accounts.TakenRefusal(login.Username, login.Summoner) is null).ToArray() ?? [];
        var yours = setup && missing.Any(login => login.Username == found!.Host?.Username) ? ", yours among them" : "";
        var offer = missing.Length == 0 ? "" : $"""
            <form method=post action={(setup ? SetupPath : UsersPath)}>{token}
            <p>Copy the logins from your Amberline for {from.Patch}: {Logins(missing.Length)} found{yours}.</p>
            <p class=tip>Each keeps its username, summoner name and password. What they own and their games stay there.</p>
            <div class=actions><button type=submit class=secondary>Copy the logins</button></div></form>
            """;
        return setup && Copied(found) is { } mine ? offer + OwnForm(token, mine.Username, null) : offer;
    }

    private string Running => $"Your Amberline for {from.Patch} is running. Stop it, then open this page again to copy its logins.";

    private string Unsafe(string folder) =>
        $"Other people who use this PC can change the files of your Amberline for {from.Patch} in {WebUtility.HtmlEncode(folder)}, " +
        $"so its logins are not copied. Update it to {from.LocksFrom} or later and start it once.";

    private static string Logins(int count) => count == 1 ? "1 login" : $"{count} logins";

    /// <summary>The host's own login there as copied here: same username, salt and hash (so it came from that store), or null.</summary>
    private PlatformAccount? Copied(Found? found) => found?.Host is { } host
        ? accounts.ListAccounts().FirstOrDefault(account => account.Username == host.Username && account.Salt == host.Salt && account.PasswordHash == host.PasswordHash)
        : null;

    /// <summary>The form that makes the copied login the host's own: its password, never filled in or shown back.</summary>
    private string OwnForm(string token, string username, string? problem) => $"""
        <form method=post action={OwnPath} novalidate>{token}
        <h2>Your login from {from.Patch}</h2>
        {HostPages.Errors((problem, problem is null || problem == HostSteps.Expired ? null : "ownpassword"))}
        <p>Your login <b>{WebUtility.HtmlEncode(username)}</b> was copied from your Amberline for {from.Patch}. Type its password to make it your own login here.</p>
        {HostPages.Field("ownpassword", "Its password", null, problem == HostSteps.Expired ? null : problem,
            "type=password required maxlength=128 autocomplete=current-password", name: "password")}
        <div class=actions><button type=submit>Use this login</button></div></form>
        """;

    /// <param name="then">Batch 856: what follows a copy that was not refused (none to copy included): its words close the page. Move my
    /// 1.0.0.126 players here (<see cref="HostOldShortcuts"/>) turns guiding on there; a refused or expired copy never runs it.</param>
    internal async Task<IResult> CopyAsync(HttpContext context, IAntiforgery antiforgery, bool setup, Func<string>? then = null)
    {
        var next = setup ? $"<a class=button href={HostSteps.Start}>Continue</a>" : $"<a class=button href={HostInvite.UsersPath}>Back to Users</a>";
        IResult Page(string content, int status = StatusCodes.Status200OK) =>
            HostMode.Page($"{content}<div class=actions>{next}</div>", status, step: setup ? 3 : 0, nav: !setup);
        const string Title = "<h1>Copy the logins</h1>";
        if (await HostSteps.FormAsync(context, antiforgery) is null) return Page(Title + HostPages.Errors((HostSteps.Expired, null)), StatusCodes.Status400BadRequest);
        var found = Find();
        if (found?.Refused is { } refused) return Page($"{Title}<p class=warn>{refused}</p>", StatusCodes.Status409Conflict);
        if (found is null || found.Logins.Count == 0)
            return then is null ? Page($"{Title}<p>Amberline found no logins in your Amberline for {from.Patch}.</p>", StatusCodes.Status404NotFound)
                : Page($"{Title}<p>Amberline found no logins to copy in your Amberline for {from.Patch}.</p>{then()}");
        var (copied, taken) = accounts.Copy(found.Logins);
        // NOT RETAIL: the host page's activity feed says what the host did, never a name (batch 748's feed names one added user).
        if (copied.Length > 0) loop.Activity.Add($"{Logins(copied.Length)} copied from the Amberline for {from.Patch}");
        // Every copied login is an ordinary user; on the first run the host's own there is offered with its password (OwnAsync).
        var own = setup && !HasOwn() && Copied(found) is { } mine ? OwnForm(Program.RegistrationToken(context, antiforgery), mine.Username, null) : "";
        var names = string.Join(", ", taken.Select(WebUtility.HtmlEncode));
        return Page($"""
            <h1>Logins copied</h1>
            <p class=notice role=status>{Logins(copied.Length)} copied from your Amberline for {from.Patch}.</p>
            {(taken.Length == 0 ? "" : $"<p>Not copied, because a login here already has the username or summoner name: {names}.</p>")}
            {(found.Unreadable == 0 ? "" : $"<p class=warn>{Logins(found.Unreadable)} could not be read and {(found.Unreadable == 1 ? "was" : "were")} not copied.</p>")}
            <p class=tip>Each copied login keeps its password and starts here as a new login. What they own and their games stay with your Amberline for {from.Patch}. Users need this server's invite link to play here.</p>
            {own}{then?.Invoke()}
            """);
    }

    private bool HasOwn() => HostMode.ReadOrDefault(loop.Paths.Settings).HostAccount is long id && accounts.Find(id) is not null;

    /// <summary>
    /// Batch 830 review: the copied login becomes the host's own only with its password, checked against the hash copied from the other store
    /// (<see cref="PlatformAccounts.Authenticate"/>, so a disabled one never does), and only while this host has none.
    /// </summary>
    private async Task<IResult> OwnAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (HasOwn()) return Results.Redirect(HostSteps.Start);
        var form = await HostSteps.FormAsync(context, antiforgery);
        if (Copied(Find()) is not { } mine)
            return HostMode.Page($"<h1>Your own login</h1><p>Amberline found no copied login of yours.</p><div class=actions><a class=button href={HostSteps.Start}>Continue</a></div>",
                StatusCodes.Status404NotFound, step: 3);
        var problem = form is null ? HostSteps.Expired
            : accounts.Authenticate(mine.Username, form["password"].ToString()) is { } signedIn && signedIn.Id == mine.Id ? null
            : "This is not the password of that login.";
        if (problem is not null)
            return HostMode.Page("<h1>Your own login</h1>" + OwnForm(Program.RegistrationToken(context, antiforgery), mine.Username, problem) +
                $"<p><a href={HostSteps.Start}>Make a new login instead</a></p>", StatusCodes.Status400BadRequest, step: 3);
        HostMode.Change(loop.Paths.Settings, saved => saved.HostAccount is long id && accounts.Find(id) is not null ? saved : saved with { HostAccount = mine.Id });
        return Results.Redirect(HostSteps.Start);
    }

    /// <summary>The other line's store as it is now, or null when no install of it holds one (or it cannot be read; the log says which part failed).</summary>
    internal Found? Find()
    {
        foreach (var data in DataFolders())
        {
            var install = Path.GetDirectoryName(data)!;
            var store = Path.Combine(data, "accounts.json");
            if (!File.Exists(store)) continue;
            // Batch 830 review: no route reads through a link (a junction or symbolic link could point anywhere).
            if (new[] { install, data, store, Path.Combine(data, "platform.json") }.Any(Linked)) return new([], null, 0, Link(install));
            if (!(FolderYours(install) && FolderYours(data))) return new([], null, 0, Unsafe(install));
            try { return Leased(data) ? new([], null, 0, Running) : Read(data, install); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                Console.Error.WriteLine($"Amberline could not read the logins of the Amberline for {from.Patch} ({ex.GetType().Name}).");
                return null;
            }
        }
        return null;
    }

    /// <summary>Whether <paramref name="path"/> is a link: a reparse point (a junction, a symbolic link) or anything with a link target.</summary>
    private static bool Linked(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.Exists && (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null);
    }

    /// <summary>Whether a folder of that install may be trusted (<see cref="CopyPlaces.OnlyYours"/> on its ACL; without one, or unreadable, not).</summary>
    private bool FolderYours(string folder)
    {
        if (!OperatingSystem.IsWindows() || loop.CopyFrom.OnlyYours is not { } check) return false;
        try { return check(new DirectoryInfo(folder).GetAccessControl()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }

    /// <summary>Whether an open store file may be trusted: the security of the very handle it is then read from (batch 830 review), never its path's.</summary>
    private bool FileYours(FileStream stream)
    {
        if (!OperatingSystem.IsWindows() || loop.CopyFrom.OnlyYours is not { } check) return false;
        try { return check(stream.GetAccessControl()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }

    private string Link(string folder) =>
        $"Your Amberline for {from.Patch} in {WebUtility.HtmlEncode(folder)} contains a link to another place, " +
        "so its logins are not copied.";

    /// <summary>
    /// The other line's data folders, in the order its own start would take them: the pointer's install, the default folder at the drive's root,
    /// then the one from before batch 637 in this user's local application data. Batch 857: <see cref="Find"/> asks the ACLs of each.
    /// </summary>
    private IEnumerable<string> DataFolders()
    {
        var local = loop.CopyFrom.LocalAppData;
        if (local is not null && Pointer(Path.Combine(local, from.Name, "install.json"), from.Marker) is { } pointed) yield return Path.Combine(pointed, "data");
        if (loop.CopyFrom.Drive is { } drive && File.Exists(Path.Combine(drive, from.Name, from.Marker))) yield return Path.Combine(drive, from.Name, "data");
        if (local is not null) yield return Path.Combine(local, from.Name, "data");
    }

    /// <summary>The install folder the other line's pointer names while that folder holds its <paramref name="marker"/> (that line's <c>FolderMove.ReadPointer</c>).</summary>
    internal static string? Pointer(string path, string marker)
    {
        try
        {
            using var stream = Open(path);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("folder", out var value) &&
                value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } folder && Path.IsPathFullyQualified(folder) &&
                File.Exists(Path.Combine(folder, marker)) ? folder : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    /// <summary>Whether that Amberline runs: its store's lease is held (PlatformAccounts opens <c>accounts.lock</c> with no sharing).</summary>
    internal static bool Leased(string data)
    {
        try
        {
            using var lease = new FileStream(Path.Combine(data, "accounts.lock"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
    }

    /// <summary>The store of the install <paramref name="install"/>, read; each file only after its open handle passed <see cref="FileYours"/>.</summary>
    private Found Read(string data, string install)
    {
        long? hostId = null;
        var settings = Path.Combine(data, "platform.json");
        if (File.Exists(settings))
        {
            using var stream = Open(settings);
            if (!FileYours(stream)) return new([], null, 0, Unsafe(install));
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("hostAccount", out var id) &&
                id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var value)) hostId = value;
        }
        List<PlatformAccounts.CopiedLogin> logins = [];
        PlatformAccounts.CopiedLogin? host = null;
        var unreadable = 0;
        using (var stream = Open(Path.Combine(data, "accounts.json")))
        {
            if (!FileYours(stream)) return new([], null, 0, Unsafe(install));
            using var document = JsonDocument.Parse(stream);
            foreach (var row in document.RootElement.GetProperty("Accounts").EnumerateArray())
            {
                if (Login(row) is not { } login) { unreadable++; continue; }
                logins.Add(login);
                if (hostId is { } wanted && row.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var value) && value == wanted)
                    host = login;
            }
        }
        return new(logins, host, unreadable);
    }

    /// <summary>A row of the other line's <c>accounts.json</c> (PlatformAccount's names), or null when it lacks a part or this store would refuse it.</summary>
    private static PlatformAccounts.CopiedLogin? Login(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || Text(row, "Username") is not { } username || Text(row, "Summoner") is not { } summoner ||
            Text(row, "Salt") is not { } salt || Text(row, "PasswordHash") is not { } hash) return null;
        // No economy is the level-30 login with everything unlocked; an economy is the level-1 start unless it unlocked everything.
        var maxed = !row.TryGetProperty("Economy", out var economy) || economy.ValueKind == JsonValueKind.Null ||
            economy.ValueKind == JsonValueKind.Object && economy.TryGetProperty("AllUnlocked", out var all) && all.ValueKind == JsonValueKind.True;
        DateTimeOffset? removed = null;
        if (row.TryGetProperty("AccessRemovedAt", out var at) && at.ValueKind != JsonValueKind.Null)
        {
            if (at.ValueKind != JsonValueKind.String || !at.TryGetDateTimeOffset(out var when)) return null;
            removed = when;
        }
        var login = new PlatformAccounts.CopiedLogin(username, summoner, salt, hash, maxed,
            row.TryGetProperty("SignedUp", out var signedUp) && signedUp.ValueKind == JsonValueKind.True, removed);
        return PlatformAccounts.Usable(login) ? login : null;
    }

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Read only, sharing write and delete, so the other Amberline could still replace the file meanwhile.</summary>
    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
