using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amberline.Tests;

/// <summary>
/// Re-records behaviour corpora from the current build (owner decision,
/// 28 September 2026). With AMBERLINE_RECORD_FIXTURES=1 a corpus check hands
/// its actual output here instead of asserting it; the file is written to
/// AMBERLINE_RECORD_DIR in the source fixture's layout (indentation, newline
/// and terminal newline). Source-backed evidence is never recorded.
/// </summary>
public static class FixtureRecorder
{


    public static bool Enabled =>
        Environment.GetEnvironmentVariable("AMBERLINE_RECORD_FIXTURES") == "1";




    private readonly record struct Layout(int Indent, string NewLine, string Ending)
    {
        // The copied fixture beside the test binary is byte-identical to the source.
        public static Layout Of(string fixture)
        {
            var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
            var newLine = text.Contains("\r\n") ? "\r\n" : "\n";
            // Some files use CRLF inside but end with a bare LF.
            var ending = text.EndsWith("\r\n") ? "\r\n" : text.EndsWith('\n') ? "\n" : "";
            var lines = text.Split('\n');
            var indent = lines.Length > 1 ? lines[1].TakeWhile(c => c == ' ').Count() : 0;
            return new Layout(indent, newLine, ending);
        }
    }
}
