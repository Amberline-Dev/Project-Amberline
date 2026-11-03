namespace Amberline.Platform;

/// <summary>
/// One player report from the end-of-game screen (<c>ReportPlayerView.sendReport</c>). <see cref="Comment"/> is the reporter's
/// own text, untrusted: it is kept as plain text (at most <see cref="PlatformAccounts.MaxReportComment"/> characters, line
/// breaks as <c>\n</c>, no other control characters), never logged, and HTML-encoded wherever it is shown.
/// <see cref="Outcome"/> is null while the report is open, then "Reviewed" or "Dismissed".
/// </summary>
public sealed record PlatformReport(Guid Id, DateTimeOffset At, long ReporterAccountId, long ReporterSummonerId,
    long ReportedSummonerId, string Offense, long GameId, string Comment, string? Outcome = null);

public sealed partial class PlatformAccounts
{
    /// <summary>The offense codes of the stock report dialog (ReportPlayerView's offence list); nothing else is stored.</summary>
    public static readonly string[] ReportOffenses =
    [
        "VERBAL_ABUSE", "NO_COMMUNICATION_WITH_TEAM", "SPAMMING", "OFFENSIVE_LANGUAGE", "UNSKILLED_PLAYER",
        "INAPPROPRIATE_NAME", "ASSISTING_ENEMY", "NEGATIVE_ATTITUDE", "INTENTIONAL_FEEDING", "LEAVING_AFK",
    ];

    public static readonly string[] ReportOutcomes = ["Reviewed", "Dismissed"];

    /// <summary>ReportPlayerView's comment box: <c>maxChars</c> 500 ("Additional Comments (500 characters max)").</summary>
    public const int MaxReportComment = 500;

    // ponytail: the newest 1000 reports ride in accounts.json (a closed one leaves first); a store of their own if a host ever
    // needs a longer history.
    private const int MaxReports = 1000;

    /// <summary>
    /// Keeps a report by the session's own account. NOT RETAIL: retail sent the complaint to the Tribunal; here the host reviews
    /// it on <c>/manage</c>. A longer comment than the client's box allows is cut, not refused, since the stock call has no
    /// error handler.
    /// </summary>
    public PlatformReport RecordReport(long reporterAccountId, long reportedSummonerId, string offense, long gameId, string? comment)
    {
        if (reportedSummonerId < 1 || gameId < 0) throw new ArgumentException("Invalid report.");
        if (!ReportOffenses.Contains(offense)) throw new ArgumentException("Unknown report offense.");
        var text = new string((comment ?? "").Replace("\r\n", "\n").Replace('\r', '\n')
            .Where(c => c == '\n' || !char.IsControl(c)).ToArray()).Trim();
        if (text.Length > MaxReportComment) text = text[..MaxReportComment];
        lock (_gate)
        {
            var reporter = RequireAccount(reporterAccountId);
            var report = new PlatformReport(Guid.NewGuid(), _time.GetUtcNow(), reporter.Id, reporter.SummonerId, reportedSummonerId,
                offense, gameId, text);
            var reports = (_state.Reports ?? []).ToList();
            if (reports.Count >= MaxReports) reports.Remove(reports.FirstOrDefault(r => r.Outcome is not null) ?? reports[0]);
            Save(_state with { Reports = [.. reports, report] });
            return report;
        }
    }

    public PlatformReport[] ListReports()
    {
        lock (_gate) return [.. _state.Reports ?? []];
    }

    /// <summary>NOT RETAIL: host tool. Closes a report as "Reviewed" or "Dismissed"; it stays listed.</summary>
    public void CloseReport(Guid id, string outcome)
    {
        if (!ReportOutcomes.Contains(outcome)) throw new ArgumentException("Choose Reviewed or Dismissed.");
        lock (_gate)
        {
            var reports = _state.Reports ?? [];
            if (!reports.Any(r => r.Id == id)) throw new ArgumentException("Report does not exist.");
            Save(_state with { Reports = reports.Select(r => r.Id == id ? r with { Outcome = outcome } : r).ToArray() });
        }
    }
}
