using Amberline.Scripting.Lua;

namespace Amberline.Tests.Scripting;

/// <summary>
/// <c>table.sort</c> is Lua 5.1's <c>ltablib.c</c> <c>auxsort</c>, not a stable sort. The expected orders below are what the
/// client's own copy of <c>auxsort</c> produces for these inputs, so records with equal keys
/// come out in retail's order. A stable sort would give 2,5,10,7,... for the first input and 1,2,3,... for the last.
/// </summary>
public static class LuaTableSortChecks
{
    [Check("scripting.lua-table-sort-is-auxsort")]
    public static void EqualKeysEndInAuxsortOrder()
    {
        AssertIdOrder(new[] { 5, 1, 5, 3, 1, 5, 2, 3, 4, 1 }, new[] { 10, 2, 5, 7, 8, 4, 9, 6, 3, 1 }, "ten records with ties");
        AssertIdOrder(new[] { 3, 3, 1, 2, 3, 1, 2, 2, 1, 3, 2, 1 }, new[] { 12, 9, 3, 6, 8, 4, 7, 11, 10, 5, 2, 1 },
            "twelve records with ties");
        AssertIdOrder(new[] { 4, 4, 4, 4, 4, 4, 4 }, new[] { 1, 5, 6, 4, 2, 3, 7 }, "seven equal keys");
    }

    [Check("scripting.lua-table-sort-order-errors")]
    public static void InvalidOrderFunctionAndBadArgumentAsLua()
    {
        var state = NewState();
        var sort = TableSort(state);
        var numbers = new LuaTable();
        for (var i = 1; i <= 5; i++) numbers.Set(LuaValue.Of((double)i), LuaValue.Of((double)i));
        var alwaysTrue = new LuaNative("alwaysTrue", (_, _) => new[] { LuaValue.Of(true) });

        TestAssert.Equal("invalid order function for sorting",
            ErrorOf(() => state.Call(sort, LuaValue.Of(numbers), LuaValue.Of(alwaysTrue))),
            "a comparator that is never false runs an index past the end (ltablib.c: i > u)");
        TestAssert.Equal("bad argument #2 to 'sort' (function expected, got number)",
            ErrorOf(() => state.Call(sort, LuaValue.Of(numbers), LuaValue.Of(5.0))),
            "luaL_checktype(L, 2, LUA_TFUNCTION) for a non-nil comparator");
    }

    private static void AssertIdOrder(int[] keys, int[] expectedIds, string label)
    {
        var state = NewState();
        var table = new LuaTable();
        for (var i = 0; i < keys.Length; i++)
        {
            var record = new LuaTable();
            record.Set(LuaValue.Of("k"), LuaValue.Of((double)keys[i]));
            record.Set(LuaValue.Of("id"), LuaValue.Of((double)(i + 1)));
            table.Set(LuaValue.Of((double)(i + 1)), LuaValue.Of(record));
        }
        var byKey = new LuaNative("byKey", (_, args) => new[] { LuaValue.Of(Key(args[0]) < Key(args[1])) });

        state.Call(TableSort(state), LuaValue.Of(table), LuaValue.Of(byKey));

        var ids = Enumerable.Range(1, keys.Length)
            .Select(i => table.Get(LuaValue.Of((double)i)).AsTable.Get(LuaValue.Of("id")).AsNumber)
            .Select(id => (int)id);
        TestAssert.Equal(string.Join(",", expectedIds), string.Join(",", ids), label);
    }

    private static double Key(LuaValue record) => record.AsTable.Get(LuaValue.Of("k")).AsNumber;

    private static LuaValue TableSort(LuaState state) => state.GetGlobal("table").AsTable.Get(LuaValue.Of("sort"));

    private static LuaState NewState()
    {
        var state = new LuaState();
        state.OpenLibs();
        return state;
    }

    private static string ErrorOf(Action action)
    {
        try
        {
            action();
        }
        catch (LuaError error)
        {
            return error.Message;
        }
        return "no error";
    }
}
