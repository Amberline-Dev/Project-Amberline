namespace Amberline.Scripting.Lua;

/// <summary>
/// Lua patterns — not regular expressions. Ported from <c>lstrlib.c</c>: character classes
/// (<c>%a %c %d %l %p %s %u %w %x %z</c> and their complements), sets and ranges,
/// quantifiers <c>* + - ?</c>, captures with back-references <c>%1..%9</c>, anchors <c>^ $</c>,
/// the balanced match <c>%bxy</c> and the frontier <c>%f[set]</c>. <c>string.match(name, '%a+')</c>
/// in <c>NeutralMinionSpawn</c> is the call site that needs them.
/// </summary>
public static class LuaPattern
{
    /// <summary>A finished match: the span it covers, and the capture spans (start, length).</summary>
    public readonly record struct Result(int Start, int End, IReadOnlyList<(int Start, int Length)> Captures);

    /// <summary>Finds the first match of <paramref name="pattern"/> at or after <paramref name="from"/>.</summary>
    public static bool TryMatch(string subject, string pattern, int from, out Result result)
    {
        // A '^' anchors the match to the start of the subject.
        var anchored = pattern.Length > 0 && pattern[0] == '^';
        var start = anchored ? 1 : 0;
        for (var si = from; ; si++)
        {
            var captures = new List<(int Start, int Length)>();
            var end = Match(subject, si, pattern, start, captures);
            if (end >= 0)
            {
                result = new Result(si, end, captures);
                return true;
            }
            if (anchored || si >= subject.Length) break;
        }
        result = default;
        return false;
    }

    /// <summary>The captures of a match, or the whole match when the pattern has no captures.</summary>
    public static IReadOnlyList<string> CapturesOrWhole(string subject, Result result) =>
        result.Captures.Count == 0
            ? new[] { subject.Substring(result.Start, result.End - result.Start) }
            : result.Captures.Select(c => c.Length < 0 ? string.Empty : subject.Substring(c.Start, c.Length)).ToArray();

    private static int Match(string s, int si, string p, int pi, List<(int Start, int Length)> captures)
    {
        while (true)
        {
            if (pi >= p.Length) return si;

            switch (p[pi])
            {
                case '(':
                {
                    captures.Add((si, -1));
                    var end = Match(s, si, p, pi + 1, captures);
                    if (end >= 0) return end;
                    captures.RemoveAt(captures.Count - 1);
                    return -1;
                }

                case ')':
                {
                    for (var i = captures.Count - 1; i >= 0; i--)
                    {
                        if (captures[i].Length >= 0) continue;
                        captures[i] = (captures[i].Start, si - captures[i].Start);
                        var end = Match(s, si, p, pi + 1, captures);
                        if (end >= 0) return end;
                        captures[i] = (captures[i].Start, -1);
                        return -1;
                    }
                    throw new LuaError("invalid pattern capture");
                }

                case '$' when pi == p.Length - 1:
                    return si == s.Length ? si : -1;

                case '%' when pi + 1 < p.Length && p[pi + 1] is >= '1' and <= '9':
                {
                    var index = p[pi + 1] - '1';
                    if (index >= captures.Count || captures[index].Length < 0)
                        throw new LuaError("invalid capture index");
                    var (start, length) = captures[index];
                    var text = s.Substring(start, length);
                    if (si + length > s.Length || string.CompareOrdinal(s, si, text, 0, length) != 0) return -1;
                    si += length;
                    pi += 2;
                    continue;
                }

                case '%' when pi + 1 < p.Length && p[pi + 1] == 'b':
                {
                    if (pi + 3 >= p.Length) throw new LuaError("malformed pattern (missing arguments to '%b')");
                    var end = MatchBalance(s, si, p[pi + 2], p[pi + 3]);
                    if (end < 0) return -1;
                    si = end;
                    pi += 4;
                    continue;
                }

                case '%' when pi + 1 < p.Length && p[pi + 1] == 'f':
                {
                    var setStart = pi + 2;
                    if (setStart >= p.Length || p[setStart] != '[')
                        throw new LuaError("missing '[' after '%f' in pattern");
                    var setEnd = ClassEnd(p, setStart);
                    // The frontier is a zero-width assertion: the previous char is outside the
                    // set and the current one inside, and the subject position does not move.
                    var previous = si == 0 ? '\0' : s[si - 1];
                    var current = si < s.Length ? s[si] : '\0';
                    if (ClassMatches(previous, p, setStart, setEnd) || !ClassMatches(current, p, setStart, setEnd))
                        return -1;
                    pi = setEnd;
                    continue;
                }

                default:
                {
                    var itemEnd = ClassEnd(p, pi);
                    var quantifier = itemEnd < p.Length ? p[itemEnd] : '\0';
                    switch (quantifier)
                    {
                        case '*':
                            return MaxExpand(s, si, p, pi, itemEnd, captures);
                        case '+':
                            return SingleMatches(s, si, p, pi, itemEnd)
                                ? MaxExpand(s, si + 1, p, pi, itemEnd, captures)
                                : -1;
                        case '-':
                            return MinExpand(s, si, p, pi, itemEnd, captures);
                        case '?':
                        {
                            if (SingleMatches(s, si, p, pi, itemEnd))
                            {
                                var end = Match(s, si + 1, p, itemEnd + 1, captures);
                                if (end >= 0) return end;
                            }
                            return Match(s, si, p, itemEnd + 1, captures);
                        }

                        default:
                            if (!SingleMatches(s, si, p, pi, itemEnd)) return -1;
                            si++;
                            pi = itemEnd;
                            continue;
                    }
                }
            }
        }
    }

    private static int MaxExpand(string s, int si, string p, int item, int itemEnd, List<(int Start, int Length)> captures)
    {
        var count = 0;
        while (SingleMatches(s, si + count, p, item, itemEnd)) count++;
        while (count >= 0)
        {
            var end = Match(s, si + count, p, itemEnd + 1, captures);
            if (end >= 0) return end;
            count--;
        }
        return -1;
    }

    private static int MinExpand(string s, int si, string p, int item, int itemEnd, List<(int Start, int Length)> captures)
    {
        while (true)
        {
            var end = Match(s, si, p, itemEnd + 1, captures);
            if (end >= 0) return end;
            if (!SingleMatches(s, si, p, item, itemEnd)) return -1;
            si++;
        }
    }

    private static int MatchBalance(string s, int si, char open, char close)
    {
        if (si >= s.Length || s[si] != open) return -1;
        var depth = 1;
        for (var i = si + 1; i < s.Length; i++)
        {
            // The close character is tested first, so an open/close pair of the same letter
            // nests exactly as matchbalance does.
            if (s[i] == close)
            {
                if (--depth == 0) return i + 1;
            }
            else if (s[i] == open) depth++;
        }
        return -1;
    }

    /// <summary>One past the end of the single-char item at <paramref name="pi"/>, as <c>classend</c> computes it.</summary>
    private static int ClassEnd(string p, int pi)
    {
        var c = p[pi++];
        if (c == '%')
        {
            if (pi >= p.Length) throw new LuaError("malformed pattern (ends with '%')");
            return pi + 1;
        }
        if (c == '[')
        {
            if (pi < p.Length && p[pi] == '^') pi++;
            while (true)
            {
                if (pi >= p.Length) throw new LuaError("malformed pattern (missing ']')");
                if (p[pi++] == '%' && pi < p.Length) pi++;
                if (pi >= p.Length) throw new LuaError("malformed pattern (missing ']')");
                if (p[pi] == ']') return pi + 1;
            }
        }
        return pi;
    }

    private static bool SingleMatches(string s, int si, string p, int item, int itemEnd)
    {
        if (si >= s.Length) return false;
        return p[item] switch
        {
            '.' => true,
            '%' => ClassMatches(s[si], p[item + 1]),
            '[' => ClassMatches(s[si], p, item, itemEnd),
            _ => p[item] == s[si],
        };
    }

    private static bool ClassMatches(char c, char classLetter)
    {
        var inverted = char.IsUpper(classLetter);
        var result = char.ToLowerInvariant(classLetter) switch
        {
            'a' => char.IsLetter(c),
            'c' => char.IsControl(c),
            'd' => char.IsDigit(c),
            'l' => char.IsLower(c),
            'p' => char.IsPunctuation(c),
            's' => char.IsWhiteSpace(c),
            'u' => char.IsUpper(c),
            'w' => char.IsLetterOrDigit(c),
            'x' => char.IsAsciiHexDigit(c),
            'z' => c == '\0',
            _ => classLetter == c,
        };
        return inverted ? !result : result;
    }

    /// <summary>The <c>[...]</c> set spanning <paramref name="item"/>..<paramref name="itemEnd"/>.</summary>
    private static bool ClassMatches(char c, string p, int item, int itemEnd)
    {
        var index = item + 1;
        var inverted = index < itemEnd - 1 && p[index] == '^';
        if (inverted) index++;
        var matched = false;
        while (index < itemEnd - 1)
        {
            if (p[index] == '%')
            {
                index++;
                if (ClassMatches(c, p[index])) { matched = true; break; }
                index++;
                continue;
            }
            if (index + 2 < itemEnd - 1 && p[index + 1] == '-')
            {
                if (p[index] <= c && c <= p[index + 2]) { matched = true; break; }
                index += 3;
                continue;
            }
            if (p[index] == c) { matched = true; break; }
            index++;
        }
        return inverted ? !matched : matched;
    }
}
