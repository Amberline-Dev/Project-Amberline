using Amberline.Network.Sessions;

namespace Amberline.Core;

/// <summary>
/// Minimal sink for source-compatible session records. The gameplay library
/// only knows this boundary; the executable owns the JSONL writer, console
/// filtering, and session metadata.
/// </summary>
public interface ISessionLogSink
{
    void Write(string category, string level, string message,
        IReadOnlyDictionary<string, object?>? properties = null);

    /// <summary>
    /// Admission gate for expensive packet-detail formatting. Existing sinks
    /// remain source-compatible; a sink that does not budget detail accepts it.
    /// Operational records must not use this gate.
    /// </summary>
    bool Admit(string category, string level) => true;

    void MatchCompleted(MatchResult result) => Write("GAMEPLAY", "INFO", "match.ended",
        new Dictionary<string, object?> { ["result"] = result });
}
