using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;

namespace Amberline.Platform;

/// <summary>One headline the host wrote for the 2011 launcher's news: its title, an optional link and the day its title and link were saved.</summary>
public sealed record LauncherHeadline(string Title, string? Link, DateOnly Saved);

/// <summary>
/// NOT RETAIL (batch 761; the classic launcher start, spec section 8): the news and server status the 2011 launcher reads when it opens.
/// Retail's launcher fetched its news from the publisher's site; here the host writes up to five headlines on Settings (<see cref="HostSettingsPage"/>)
/// and <see cref="Map"/> serves them at <see cref="Route"/>. The status is always 1 while the server answers. The body is ASCII JSON within
/// <see cref="MaxBytes"/>, the most the launcher reads, whatever is stored: <see cref="Body"/> cleans the list and drops what does not fit.
/// </summary>
public static class LauncherNews
{
    public const string Route = "/launcher/news";
    public const int MaxHeadlines = 5, MaxBytes = 440;

    // "Sep 30" is the widest date the body shows (6 bytes): a set that fits with it fits on every day of the year.
    private static readonly DateOnly Widest = new(2000, 9, 30);

    /// <summary>
    /// The body the launcher reads: its status, then the headlines in order, or the welcome headline while there are none. The headlines are what
    /// <see cref="Usable"/> keeps, and the trailing ones go until the body is within <see cref="MaxBytes"/>.
    /// </summary>
    public static string Body(IReadOnlyList<LauncherHeadline> news, string? serverName, DateOnly today)
    {
        var shown = Fit(Usable(news));
        IReadOnlyList<LauncherHeadline> listed = shown.Count > 0 ? shown : [Welcome(serverName, today)];
        return Serialize(listed);
    }

    /// <summary>Null when the headlines can be saved; else the host's words for why not.</summary>
    public static string? Problem(IReadOnlyList<(string Title, string? Link)> headlines)
    {
        if (headlines.Count > MaxHeadlines) return $"The launcher shows at most {MaxHeadlines} headlines.";
        for (var index = 0; index < headlines.Count; index++)
        {
            var (title, link) = headlines[index];
            var number = index + 1;
            if (string.IsNullOrWhiteSpace(title)) return $"Headline {number} needs a title.";
            if (!title.All(Printable)) return $"Headline {number} has a character the launcher cannot show. Use plain letters, digits and punctuation only.";
            if (!(link ?? "").All(Printable)) return $"The link of headline {number} has a character the launcher cannot show. Use plain letters, digits and punctuation only.";
        }
        var bytes = Bytes(headlines);
        return bytes > MaxBytes ? $"The headlines take {bytes} bytes, but the launcher shows at most {MaxBytes} bytes of news. Shorten a headline or remove one." : null;
    }

    /// <summary>
    /// The headlines a save stores: a row whose title and link are those of a stored headline keeps that headline's day (each stored headline
    /// counts once); a new or changed row is dated <paramref name="today"/>. Spec section 8: a headline's date is the day it was written.
    /// </summary>
    public static LauncherHeadline[] Dated(IReadOnlyList<LauncherHeadline> stored, IReadOnlyList<(string Title, string? Link)> rows, DateOnly today)
    {
        var unmatched = Usable(stored).ToList();
        var dated = new List<LauncherHeadline>(rows.Count);
        foreach (var (title, link) in rows)
        {
            var match = unmatched.FindIndex(old => old.Title == title && old.Link == link);
            if (match < 0)
            {
                dated.Add(new LauncherHeadline(title, link, today));
                continue;
            }
            dated.Add(unmatched[match]);
            unmatched.RemoveAt(match);
        }
        return [.. dated];
    }

    /// <summary>
    /// What a stored list shows: no null entries, each title and link with every character the launcher cannot show left out, no headline
    /// without a title, and at most five. A list that is already valid comes back unchanged, so a save and a start read the same headlines.
    /// </summary>
    public static IReadOnlyList<LauncherHeadline> Usable(IEnumerable<LauncherHeadline>? stored) =>
        (stored ?? []).Where(headline => headline is not null).Select(headline =>
        {
            var title = Plain(headline.Title);
            var link = Plain(headline.Link);
            return string.IsNullOrWhiteSpace(title) ? null : new LauncherHeadline(title, link.Length == 0 ? null : link, headline.Saved);
        }).OfType<LauncherHeadline>().Take(MaxHeadlines).ToArray();

    /// <summary>The bytes a set of headlines takes in the body, each date at its widest: what <see cref="Problem"/> keeps within <see cref="MaxBytes"/>.</summary>
    public static int Bytes(IReadOnlyList<(string Title, string? Link)> headlines) =>
        Size(headlines.Select(headline => new LauncherHeadline(headline.Title, headline.Link, Widest)));

    /// <summary>Serves <see cref="Body"/> at <see cref="Route"/>, read at each request (so a Save applies at once); <paramref name="today"/> is the day it reads as.</summary>
    public static void Map(WebApplication app, Func<IReadOnlyList<LauncherHeadline>> news, Func<string?> serverName, Func<DateOnly> today) =>
        app.MapGet(Route, () => Results.Text(Body(news(), serverName(), today()), "application/json"));

    /// <summary>The local day: the day a saved headline is dated with, and the day the route reads as today.</summary>
    public static DateOnly LocalDay() => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>The leading headlines that fit: the trailing ones go first, until the body is within <see cref="MaxBytes"/>.</summary>
    private static IReadOnlyList<LauncherHeadline> Fit(IReadOnlyList<LauncherHeadline> shown)
    {
        while (shown.Count > 0 && Size(shown) > MaxBytes) shown = shown.Take(shown.Count - 1).ToArray();
        return shown;
    }

    /// <summary>The welcome headline: the server's name (printable ASCII only), cut so the body fits; "this server" without one.</summary>
    private static LauncherHeadline Welcome(string? serverName, DateOnly today)
    {
        var name = Plain(serverName).Trim();
        if (name.Length == 0) name = "this server";
        var room = MaxBytes - Size([new LauncherHeadline("Welcome to ", null, today)]);
        return new LauncherHeadline($"Welcome to {name[..Math.Min(name.Length, room)]}", null, today);
    }

    /// <summary>The JSON <c>{"serverStatus":1,"news":[{"title","url","date"},...]}</c>: only what <see cref="Usable"/> keeps is written, and a link's slashes are written <c>\/</c>.</summary>
    private static string Serialize(IEnumerable<LauncherHeadline> news) =>
        "{\"serverStatus\":1,\"news\":[" + string.Join(",", Usable(news).Select(item =>
            $"{{\"title\":\"{item.Title}\",\"url\":\"{(item.Link ?? "").Replace("/", "\\/", StringComparison.Ordinal)}\"," +
            $"\"date\":\"{item.Saved.ToString("MMM d", CultureInfo.InvariantCulture)}\"}}")) + "]}";

    /// <summary>The UTF-8 bytes of the body that <paramref name="news"/> makes.</summary>
    private static int Size(IEnumerable<LauncherHeadline> news) => Encoding.UTF8.GetByteCount(Serialize(news));

    /// <summary>The text with every character the launcher cannot show left out: a quotation mark, a backslash, or anything outside printable ASCII.</summary>
    private static string Plain(string? text) => new string((text ?? "").Where(Printable).ToArray());

    /// <summary>A character the launcher shows: printable ASCII without a quotation mark or a backslash, so the JSON needs no other escape.</summary>
    private static bool Printable(char character) => character is >= ' ' and <= '~' and not '"' and not '\\';
}
