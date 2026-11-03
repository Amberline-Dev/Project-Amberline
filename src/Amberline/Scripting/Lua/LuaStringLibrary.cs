using System.Globalization;
using System.Text;

namespace Amberline.Scripting.Lua;

/// <summary>The <c>string</c> library over <see cref="LuaPattern"/>, with real Lua patterns.</summary>
public static class LuaStringLibrary
{
    public static void Open(LuaState state)
    {
        var strings = new LuaTable();
        strings.Set(LuaValue.Of("byte"), new LuaNative("byte", Byte));
        strings.Set(LuaValue.Of("char"), new LuaNative("char", Char));
        strings.Set(LuaValue.Of("find"), new LuaNative("find", Find));
        strings.Set(LuaValue.Of("format"), new LuaNative("format", Format));
        strings.Set(LuaValue.Of("gmatch"), new LuaNative("gmatch", GMatch));
        strings.Set(LuaValue.Of("gsub"), new LuaNative("gsub", GSub));
        strings.Set(LuaValue.Of("len"), new LuaNative("len", (s, a) => new[] { LuaValue.Of((double)ArgString(a, 1, "len").Length) }));
        strings.Set(LuaValue.Of("lower"), new LuaNative("lower", (s, a) => new[] { LuaValue.Of(ArgString(a, 1, "lower").ToLowerInvariant()) }));
        strings.Set(LuaValue.Of("match"), new LuaNative("match", Match));
        strings.Set(LuaValue.Of("rep"), new LuaNative("rep", Rep));
        strings.Set(LuaValue.Of("reverse"), new LuaNative("reverse", (s, a) => new[] { LuaValue.Of(new string(ArgString(a, 1, "reverse").Reverse().ToArray())) }));
        strings.Set(LuaValue.Of("sub"), new LuaNative("sub", Sub));
        strings.Set(LuaValue.Of("upper"), new LuaNative("upper", (s, a) => new[] { LuaValue.Of(ArgString(a, 1, "upper").ToUpperInvariant()) }));
        state.SetGlobal("string", LuaValue.Of(strings));
    }

    private static string ArgString(LuaValue[] args, int index, string function)
    {
        if (args.Length < index) throw LuaErrors.BadArgument(index, function, "string", LuaValue.Nil);
        return args[index - 1].Type switch
        {
            LuaType.String => args[index - 1].AsString,
            LuaType.Number => LuaStrings.NumberToString(args[index - 1].AsNumber),
            _ => throw LuaErrors.BadArgument(index, function, "string", args[index - 1]),
        };
    }

    /// <summary>The subject index Lua uses: 1-based, negative counts back from the end, clamped.</summary>
    private static int Position(double index, int length, int fallback)
    {
        if (index is >= int.MinValue and <= int.MaxValue)
        {
            var value = (int)index;
            return value > 0 ? Math.Min(value, length + 1) : Math.Max(length + value + 1, fallback);
        }
        return fallback;
    }

    private static LuaValue[] Sub(LuaState state, LuaValue[] args)
    {
        var text = ArgString(args, 1, "sub");
        var start = Position(args.Length > 1 ? args[1].ToNumber() ?? 1 : 1, text.Length, 1);
        var end = args.Length > 2 ? Position(args[2].ToNumber() ?? text.Length, text.Length, text.Length) : text.Length;
        if (start > end) return new[] { LuaValue.Of(string.Empty) };
        return new[] { LuaValue.Of(text.Substring(start - 1, end - start + 1)) };
    }

    private static LuaValue[] Rep(LuaState state, LuaValue[] args)
    {
        var text = ArgString(args, 1, "rep");
        var count = (int)(args.Length > 1 ? args[1].ToNumber() ?? throw LuaErrors.BadArgument(2, "rep", "number", args[1]) : 0);
        var separator = args.Length > 2 ? ArgString(args, 3, "rep") : string.Empty;
        if (count <= 0) return new[] { LuaValue.Of(string.Empty) };
        return new[] { LuaValue.Of(string.Join(separator, Enumerable.Repeat(text, count))) };
    }

    private static LuaValue[] Byte(LuaState state, LuaValue[] args)
    {
        var text = ArgString(args, 1, "byte");
        var first = args.Length > 1 ? Position(args[1].ToNumber() ?? 1, text.Length, 1) : 1;
        var last = args.Length > 2 ? Position(args[2].ToNumber() ?? first, text.Length, first) : first;
        var results = new List<LuaValue>();
        for (var i = first; i <= last && i <= text.Length; i++)
            if (i >= 1) results.Add(LuaValue.Of((double)text[i - 1]));
        return results.ToArray();
    }

    private static LuaValue[] Char(LuaState state, LuaValue[] args)
    {
        var builder = new StringBuilder(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            var code = args[i].ToNumber() ?? throw LuaErrors.BadArgument(i + 1, "char", "number", args[i]);
            builder.Append((char)(int)code);
        }
        return new[] { LuaValue.Of(builder.ToString()) };
    }

    private static LuaValue[] Match(LuaState state, LuaValue[] args)
    {
        var text = ArgString(args, 1, "match");
        var pattern = ArgString(args, 2, "match");
        var from = StartIndex(args, 3, text.Length) - 1;
        if (!LuaPattern.TryMatch(text, pattern, Math.Max(0, from), out var result))
            return new[] { LuaValue.Nil };
        return ToValues(text, result);
    }

    private static LuaValue[] Find(LuaState state, LuaValue[] args)
    {
        var text = ArgString(args, 1, "find");
        var pattern = ArgString(args, 2, "find");
        var from = Math.Max(0, StartIndex(args, 3, text.Length) - 1);
        var plain = args.Length > 4 && args[4].Truthy;

        if (plain || !HasMagic(pattern))
        {
            var at = text.IndexOf(pattern, from, StringComparison.Ordinal);
            return at < 0
                ? new[] { LuaValue.Nil }
                : new[] { LuaValue.Of((double)(at + 1)), LuaValue.Of((double)(at + pattern.Length)) };
        }

        if (!LuaPattern.TryMatch(text, pattern, from, out var result)) return new[] { LuaValue.Nil };
        var values = new List<LuaValue> { LuaValue.Of((double)(result.Start + 1)), LuaValue.Of((double)result.End) };
        values.AddRange(ToValues(text, result));
        return values.ToArray();
    }

    private static bool HasMagic(string pattern) =>
        pattern.Any(c => c is '.' or '+' or '*' or '?' or '[' or '^' or '$' or '%' or '(' or '-');

    private static LuaValue[] GMatch(LuaState state, LuaValue[] args)
    {
        var text = ArgString(args, 1, "gmatch");
        var pattern = ArgString(args, 2, "gmatch");
        var position = 0;
        return new[]
        {
            LuaValue.Of(new LuaNative("gmatch-iterator", (s, a) =>
            {
                if (!LuaPattern.TryMatch(text, pattern, position, out var result)) return new[] { LuaValue.Nil };
                // An empty match still has to advance, or the iterator would loop forever.
                position = result.End > position ? result.End : result.End + 1;
                return ToValues(text, result);
            })),
        };
    }

    private static LuaValue[] GSub(LuaState state, LuaValue[] args)
    {
        var text = ArgString(args, 1, "gsub");
        var pattern = ArgString(args, 2, "gsub");
        var replacement = args.Length > 3 ? args[3] : LuaValue.Of(string.Empty);
        var limit = args.Length > 4 ? (int)(args[4].ToNumber() ?? double.PositiveInfinity) : int.MaxValue;
        var builder = new StringBuilder();
        var position = 0;
        var count = 0;
        while (count < limit && LuaPattern.TryMatch(text, pattern, position, out var result))
        {
            builder.Append(text, position, result.Start - position);
            builder.Append(Replacement(state, replacement, text, result));
            count++;
            position = result.End > result.Start ? result.End : result.Start + 1;
            if (position > text.Length) break;
        }
        builder.Append(text, position, text.Length - position);
        return new[] { LuaValue.Of(builder.ToString()), LuaValue.Of((double)count) };
    }

    private static string Replacement(LuaState state, LuaValue replacement, string text, LuaPattern.Result result)
    {
        switch (replacement.Type)
        {
            case LuaType.String:
            {
                var source = replacement.AsString;
                var builder = new StringBuilder();
                for (var i = 0; i < source.Length; i++)
                {
                    var c = source[i];
                    if (c != '%')
                    {
                        builder.Append(c);
                        continue;
                    }
                    if (++i >= source.Length) builder.Append('%');
                    else if (source[i] == '0' || !char.IsAsciiDigit(source[i])) builder.Append(source[i]);
                    else
                    {
                        var index = source[i] - '1';
                        var captures = LuaPattern.CapturesOrWhole(text, result);
                        builder.Append(index < captures.Count ? captures[index] : string.Empty);
                    }
                }
                return builder.ToString();
            }

            case LuaType.Table:
            {
                var captures = LuaPattern.CapturesOrWhole(text, result);
                var key = LuaValue.Of(captures.Count > 0 ? captures[0] : string.Empty);
                var value = replacement.AsTable.Get(key);
                return value.IsNil ? string.Empty : value.ToPrintableString();
            }

            case LuaType.Function:
            {
                var captures = LuaPattern.CapturesOrWhole(text, result);
                var arguments = captures.Select(LuaValue.Of).ToArray();
                var results = state.Call(replacement, arguments);
                return results.Length == 0 || results[0].IsNil ? string.Empty : results[0].ToPrintableString();
            }

            default:
                return replacement.ToPrintableString();
        }
    }

    private static LuaValue[] ToValues(string text, LuaPattern.Result result)
    {
        if (result.Captures.Count == 0) return new[] { LuaValue.Of(text.Substring(result.Start, result.End - result.Start)) };
        return LuaPattern.CapturesOrWhole(text, result).Select(c => LuaValue.Of(c)).ToArray();
    }

    private static int StartIndex(LuaValue[] args, int index, int length)
    {
        if (args.Length < index) return 1;
        var value = args[index - 1].ToNumber() ?? throw LuaErrors.BadArgument(index, "match", "number", args[index - 1]);
        return Position(value, length, 1);
    }

    /// <summary><c>string.format</c>: the C conversions, with <c>tostring</c>'s rules for <c>%s</c>.</summary>
    private static LuaValue[] Format(LuaState state, LuaValue[] args)
    {
        var template = ArgString(args, 1, "format");
        var builder = new StringBuilder();
        var argument = 1;
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '%')
            {
                builder.Append(template[i]);
                continue;
            }
            if (i + 1 < template.Length && template[i + 1] == '%')
            {
                builder.Append('%');
                i++;
                continue;
            }

            var specifier = ReadSpecifier(template, ref i);
            var conversion = specifier[^1];
            argument++;
            if (argument > args.Length) throw LuaErrors.BadArgument(argument, "format", "value", LuaValue.Nil);
            builder.Append(FormatOne(state, specifier, conversion, args[argument - 1]));
        }
        return new[] { LuaValue.Of(builder.ToString()) };
    }

    /// <summary>Reads <c>%[-+ #0]*[width][.precision]conv</c> and leaves <paramref name="i"/> on the conversion.</summary>
    private static string ReadSpecifier(string template, ref int i)
    {
        var start = i;
        i++;
        while (i < template.Length && template[i] is '-' or '+' or ' ' or '#' or '0') i++;
        while (i < template.Length && char.IsAsciiDigit(template[i])) i++;
        if (i < template.Length && template[i] == '.')
        {
            i++;
            while (i < template.Length && char.IsAsciiDigit(template[i])) i++;
        }
        if (i >= template.Length || template[i] is not ('d' or 'i' or 'u' or 'c' or 'x' or 'X' or 'o' or 'f' or 'g' or 'G' or 'e' or 'E' or 's' or 'q'))
            throw new LuaError("invalid conversion to 'format'");
        return template[start..(i + 1)];
    }

    private static string FormatOne(LuaState state, string specifier, char conversion, LuaValue value)
    {
        if (conversion is 's' or 'q')
        {
            var text = value.Type == LuaType.String ? value.AsString : LuaState.ToDisplayString(state, value);
            return conversion == 's' ? Pad(specifier, text) : Quote(text);
        }

        var number = value.ToNumber() ?? throw LuaErrors.BadArgument(1, "format", "number", value);
        var integer = (long)number;
        return conversion switch
        {
            'd' or 'i' or 'u' => Pad(specifier, integer.ToString(CultureInfo.InvariantCulture)),
            'c' => Pad(specifier, ((char)(int)integer).ToString()),
            'x' => Pad(specifier, integer.ToString("x", CultureInfo.InvariantCulture)),
            'X' => Pad(specifier, integer.ToString("X", CultureInfo.InvariantCulture)),
            'o' => Pad(specifier, Convert.ToString(integer, 8)),
            'f' => Pad(specifier, number.ToString("F" + Precision(specifier, 6).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)),
            'e' => Pad(specifier, number.ToString("E" + Precision(specifier, 6).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture).ToLowerInvariant()),
            'E' => Pad(specifier, number.ToString("E" + Precision(specifier, 6).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)),
            'g' => Pad(specifier, LuaStrings.NumberToString(number, Precision(specifier, 6))),
            'G' => Pad(specifier, LuaStrings.NumberToString(number, Precision(specifier, 6)).ToUpperInvariant()),
            _ => throw new LuaError($"invalid conversion '%{conversion}' to 'format'"),
        };
    }

    private static int Precision(string specifier, int fallback)
    {
        var dot = specifier.IndexOf('.');
        if (dot < 0) return fallback;
        var digits = new string(specifier[(dot + 1)..^1].Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 0 ? 0 : int.Parse(digits, CultureInfo.InvariantCulture);
    }

    private static string Pad(string specifier, string text)
    {
        var body = specifier[1..^1];
        var leftAlign = body.StartsWith('-');
        var dot = body.IndexOf('.');
        var widthText = new string(body[..(dot < 0 ? body.Length : dot)].Where(char.IsAsciiDigit).ToArray());
        if (widthText.Length == 0) return text;
        var width = int.Parse(widthText, CultureInfo.InvariantCulture);
        if (text.Length >= width) return text;
        var padding = new string(' ', width - text.Length);
        return leftAlign ? text + padding : padding + text;
    }

    private static string Quote(string text)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\0' => "\\0",
                _ => c.ToString(),
            });
        }
        return builder.Append('"').ToString();
    }
}
