using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Tests.Scripting;

/// <summary>
/// Lua 5.1 converts a numeric string operand of an arithmetic operator (<c>luaV_tonumber</c>, which is <c>luaO_str2d</c>).
/// The BuildingBlocks interpreter's operand conversion does the same, so <c>"10" * 2</c> is 20 and a non-numeric string raises.
/// </summary>
public static class BbArithmeticCoercionChecks
{
    [Check("scripting.bb-arithmetic-coerces-numeric-strings")]
    public static void NumericStringsConvertAndOtherStringsRaise()
    {
        TestAssert.Equal(10.0, BbRuntime.Number("10"), "\"10\" is converted to 10 for arithmetic");
        TestAssert.Equal(-2.5, BbRuntime.Number(" -2.5 "), "surrounding whitespace is allowed (luaO_str2d)");
        TestAssert.Equal(16.0, BbRuntime.Number("0x10"), "a hexadecimal constant string converts");
        TestAssert.Equal(1000.0, BbRuntime.Number("1e3"), "an exponent converts");
        TestAssert.Equal("attempt to perform arithmetic on a string value", ErrorOf(() => BbRuntime.Number("abc")),
            "a string that is not a number raises the arithmetic error");
    }

    private static string ErrorOf(Action action)
    {
        try
        {
            action();
        }
        catch (BbRuntimeException error)
        {
            return error.Message;
        }
        return "no error";
    }
}
