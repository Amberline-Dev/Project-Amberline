namespace Amberline.Data;

/// <summary>
/// The stock client's text-config parser, used for the shipped <c>.cfg</c> / <c>.ini</c> files the server reads
/// (<c>GamePermanent.cfg</c> [Vision] and [Disconnect], <c>NSEAI.cfg</c>).
/// Whitespace between entries is space, CR and LF (not tab); a line whose first character is <c>#</c>, <c>'</c> or <c>;</c> is a
/// comment to the end of the line; <c>[name]</c> opens a section; otherwise the key runs to the first <c>=</c> and the value starts after
/// spaces, tabs and <c>=</c> and ends at the first of <c>\n \r # ;</c>, trimmed of spaces and tabs with one pair of surrounding quotes
/// removed. Keys are looked up by the SDBM hash of <c>section*key</c>, so section and key are case-insensitive, and a repeated key keeps
/// its FIRST value (the client logs "Duplicate key/value pair" and drops the later one). A pair outside any section is ignored.
/// Numbers are read through <c>atof</c> (<see cref="InibinValue.AtofPrefix"/>).
/// </summary>
public sealed class ClientTextConfig
{
    private readonly Dictionary<uint, string> _values;

    private ClientTextConfig(Dictionary<uint, string> values, int duplicates)
    {
        _values = values;
        Duplicates = duplicates;
    }

    /// <summary>Later copies of a key the parser dropped.</summary>
    public int Duplicates { get; }

    public int Count => _values.Count;

    public static ClientTextConfig Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path, System.Text.Encoding.Latin1));
    }

    public static ClientTextConfig Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var values = new Dictionary<uint, string>();
        var duplicates = 0;
        var section = "";
        var position = 0;
        while (position < text.Length)
        {
            position = IndexOfNot(text, " \n\r", position);
            if (position < 0) break;
            var c = text[position];
            if (c == '[')
            {
                var close = text.IndexOf(']', position + 1);
                if (close < 0) break;
                section = text.Substring(position + 1, close - position - 1);
                position = close + 1;
            }
            else if (c is '#' or '\'' or ';')
            {
                var eol = text.IndexOf('\n', position);
                if (eol < 0) break;
                position = IndexOfNot(text, " \n\r", eol);
                if (position < 0) break;
            }
            else
            {
                var bracket = text.IndexOf('[', position);
                var equals = text.IndexOf('=', position);
                if (equals < 0) break;
                // A '[' before the next '=' is not a key: skip one character, as the client does.
                if (bracket >= 0 && bracket < equals) { position++; continue; }
                var name = text.Substring(position, equals - position).Trim(' ', '\t');
                var valueStart = IndexOfNot(text, " \t=", equals);
                if (valueStart < 0) break;
                var valueEnd = text.IndexOfAny(['\n', '\r', '#', ';'], valueStart);
                var raw = (valueEnd < 0 ? text[valueStart..] : text[valueStart..valueEnd]).Trim(' ', '\t');
                if (raw.Length > 1 && ((raw[0] == '"' && raw[^1] == '"') || (raw[0] == '\'' && raw[^1] == '\'')))
                    raw = raw[1..^1];
                if (name.Length > 0 && section.Length > 0 && !values.TryAdd(InibinReader.KeyHash(section + "*" + name), raw))
                    duplicates++;
                position = valueEnd < 0 ? text.Length : valueEnd;
            }
        }
        return new ClientTextConfig(values, duplicates);
    }

    public bool TryGet(string section, string key, out string value) =>
        _values.TryGetValue(InibinReader.KeyHash(section + "*" + key), out value!);

    /// <summary>The value as <c>atof</c> reads it, or <paramref name="fallback"/> when the key is absent.</summary>
    public double Float(string section, string key, double fallback) =>
        TryGet(section, key, out var text) ? InibinValue.AtofPrefix(text) : fallback;

    private static int IndexOfNot(string text, string set, int from)
    {
        for (var i = from; i < text.Length; i++)
            if (!set.Contains(text[i])) return i;
        return -1;
    }
}
