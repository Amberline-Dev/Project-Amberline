using System.Globalization;

namespace Amberline.Scripting.Lua;

/// <summary>
/// Number and string conversions with Lua 5.1's exact presentation: <c>%.14g</c> for numbers
/// (<c>LUAI_NUMFFORMAT</c>) and <c>luaO_str2d</c> for reading numbers out of strings.
/// </summary>
public static class LuaStrings
{
    /// <summary>The whitespace <c>strtod</c> skips at both ends of a numeric string.</summary>
    private static ReadOnlySpan<char> NumberWhitespace => " \t\n\v\f\r";

    /// <summary>C's <c>%.14g</c>, the format <c>tostring</c> gives numbers (<c>LUAI_NUMFFORMAT</c>).</summary>
    public static string NumberToString(double value) => NumberToString(value, 14);

    /// <summary>
    /// C's <c>%g</c> with a precision: that many significant digits, trailing zeros trimmed,
    /// scientific when the decimal exponent is &lt; -4 or &gt;= the precision.
    /// </summary>
    public static string NumberToString(double value, int precision)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsPositiveInfinity(value)) return "inf";
        if (double.IsNegativeInfinity(value)) return "-inf";
        if (value == 0) return double.IsNegative(value) ? "-0" : "0";

        // "E(p-1)" is exactly `precision` significant digits in a fixed shape, which is where %g rounds.
        var text = value.ToString("E" + (precision - 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var negative = text[0] == '-';
        var digitsStart = negative ? 1 : 0;
        var eIndex = text.IndexOf('E');
        var digits = text[digitsStart..eIndex].Replace(".", string.Empty, StringComparison.Ordinal);
        var exponent = int.Parse(text[(eIndex + 1)..], CultureInfo.InvariantCulture);
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) digits = "0";

        string body;
        if (exponent < -4 || exponent >= precision)
        {
            var mantissa = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
            var sign = exponent < 0 ? '-' : '+';
            body = mantissa + "e" + sign + Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
        }
        else if (exponent >= 0)
        {
            // No trailing '.' when the digits stop exactly at the decimal point: %g writes 42 as
            // "42", never "42.".
            body = digits.Length <= exponent
                ? digits.PadRight(exponent + 1, '0')
                : digits[..(exponent + 1)] + (digits.Length > exponent + 1 ? "." + digits[(exponent + 1)..] : string.Empty);
        }
        else
        {
            body = "0." + new string('0', -exponent - 1) + digits;
        }

        return negative ? "-" + body : body;
    }

    /// <summary>
    /// <c>luaO_str2d</c>: a decimal number (sign, fraction and exponent) or a hexadecimal
    /// integer constant, with surrounding whitespace allowed. Returns <see langword="null"/>
    /// when the whole string is not a number.
    /// </summary>
    public static double? StringToNumber(string text)
    {
        var span = text.AsSpan().Trim(NumberWhitespace);
        if (span.IsEmpty) return null;

        // strtod-style inf/infinity/nan, which Lua 5.1 accepts because it delegates to the C library.
        var lowered = span.ToString().ToLowerInvariant();
        if (lowered is "inf" or "+inf" or "infinity" or "+infinity") return double.PositiveInfinity;
        if (lowered is "-inf" or "-infinity") return double.NegativeInfinity;
        if (lowered is "nan" or "+nan" or "-nan") return double.NaN;

        if (span.Length > 2 && span[0] == '0' && (span[1] == 'x' || span[1] == 'X'))
            return HexToNumber(span[2..]);

        return double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static double? HexToNumber(ReadOnlySpan<char> digits)
    {
        double value = 0;
        var any = false;
        foreach (var c in digits)
        {
            int digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1,
            };
            if (digit < 0) return null;
            value = value * 16 + digit;
            any = true;
        }
        return any ? value : null;
    }

    /// <summary>ASCII byte comparison, matching <c>l_strcmp</c> in the C locale.</summary>
    public static int CompareOrdinalBytes(string left, string right) =>
        string.CompareOrdinal(left, right);
}
