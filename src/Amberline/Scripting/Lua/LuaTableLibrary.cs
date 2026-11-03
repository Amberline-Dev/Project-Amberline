namespace Amberline.Scripting.Lua;

/// <summary>
/// The <c>table</c> library. <c>table.sort</c> is Lua 5.1's <c>ltablib.c</c> <c>auxsort</c>: an unstable quicksort whose
/// comparisons, swaps and order-function errors are retail's, so elements that compare equal end up where the client's Lua
/// puts them. The client EXE carries the same function (the one that raises
/// "invalid order function for sorting"), and the port follows it step for step.
/// </summary>
public static class LuaTableLibrary
{
    public static void Open(LuaState state)
    {
        var table = new LuaTable();
        table.Set(LuaValue.Of("concat"), new LuaNative("concat", Concat));
        table.Set(LuaValue.Of("insert"), new LuaNative("insert", Insert));
        table.Set(LuaValue.Of("remove"), new LuaNative("remove", Remove));
        table.Set(LuaValue.Of("sort"), new LuaNative("sort", Sort));
        table.Set(LuaValue.Of("getn"), new LuaNative("getn", GetN));
        table.Set(LuaValue.Of("maxn"), new LuaNative("maxn", MaxN));
        table.Set(LuaValue.Of("foreach"), new LuaNative("foreach", ForEach));
        table.Set(LuaValue.Of("foreachi"), new LuaNative("foreachi", ForEachI));
        state.SetGlobal("table", LuaValue.Of(table));
    }

    private static LuaTable ArgTable(LuaValue[] args, int index, string function)
    {
        if (args.Length < index || args[index - 1].Type != LuaType.Table)
            throw LuaErrors.BadArgument(index, function, "table", args.Length < index ? LuaValue.Nil : args[index - 1]);
        return args[index - 1].AsTable;
    }

    private static LuaValue[] GetN(LuaState state, LuaValue[] args) =>
        new[] { LuaValue.Of((double)ArgTable(args, 1, "getn").Length) };

    private static LuaValue[] MaxN(LuaState state, LuaValue[] args)
    {
        var table = ArgTable(args, 1, "maxn");
        double largest = 0;
        foreach (var (key, _) in table.Entries)
            if (key.Type == LuaType.Number && key.AsNumber > largest) largest = key.AsNumber;
        return new[] { LuaValue.Of(largest) };
    }

    private static LuaValue[] Concat(LuaState state, LuaValue[] args)
    {
        var table = ArgTable(args, 1, "concat");
        var separator = args.Length > 1 && !args[1].IsNil
            ? (args[1].Type == LuaType.String ? args[1].AsString : LuaStrings.NumberToString(args[1].AsNumber))
            : string.Empty;
        var first = args.Length > 2 && !args[2].IsNil ? (int)(args[2].ToNumber() ?? 1) : 1;
        var last = args.Length > 3 && !args[3].IsNil ? (int)(args[3].ToNumber() ?? table.Length) : table.Length;
        var parts = new List<string>();
        for (var i = first; i <= last; i++)
        {
            var value = table.Get(LuaValue.Of((double)i));
            if (value.Type is not (LuaType.String or LuaType.Number))
                throw new LuaError($"invalid value (at index {i.ToString(System.Globalization.CultureInfo.InvariantCulture)}) in table for 'concat'");
            parts.Add(value.ToPrintableString());
        }
        return new[] { LuaValue.Of(string.Join(separator, parts)) };
    }

    private static LuaValue[] Insert(LuaState state, LuaValue[] args)
    {
        var table = ArgTable(args, 1, "insert");
        if (args.Length < 2) throw LuaErrors.BadArgument(2, "insert", "value", LuaValue.Nil);
        if (args.Length == 2)
        {
            table.Set(LuaValue.Of((double)(table.Length + 1)), args[1]);
            return Array.Empty<LuaValue>();
        }

        var position = (int)(args[1].ToNumber() ?? throw LuaErrors.BadArgument(2, "insert", "number", args[1]));
        if (position < 1 || position > table.Length + 1)
            throw new LuaError("bad argument #2 to 'insert' (position out of bounds)");
        for (var i = table.Length; i >= position; i--)
            table.Set(LuaValue.Of((double)(i + 1)), table.Get(LuaValue.Of((double)i)));
        table.Set(LuaValue.Of((double)position), args[2]);
        return Array.Empty<LuaValue>();
    }

    private static LuaValue[] Remove(LuaState state, LuaValue[] args)
    {
        var table = ArgTable(args, 1, "remove");
        var size = table.Length;
        var position = args.Length > 1 && !args[1].IsNil ? (int)(args[1].ToNumber() ?? size) : size;
        if (size == 0) return new[] { LuaValue.Nil };
        if (position < 1 || position > size + 1)
            throw new LuaError("bad argument #2 to 'remove' (position out of bounds)");
        var removed = table.Get(LuaValue.Of((double)position));
        for (var i = position; i < size; i++)
            table.Set(LuaValue.Of((double)i), table.Get(LuaValue.Of((double)(i + 1))));
        table.Set(LuaValue.Of((double)size), LuaValue.Nil);
        return new[] { removed };
    }

    private static LuaValue[] Sort(LuaState state, LuaValue[] args)
    {
        var table = ArgTable(args, 1, "sort");
        var comparator = args.Length > 1 ? args[1] : LuaValue.Nil;
        if (!comparator.IsNil && comparator.Type != LuaType.Function)
            throw LuaErrors.BadArgument(2, "sort", "function", comparator);
        AuxSort(state, table, comparator, 1, table.Length);
        return Array.Empty<LuaValue>();
    }

    /// <summary>
    /// <c>auxsort</c> of <c>ltablib.c</c>, as the client's copy compiles it: the same comparisons in the same order and
    /// argument order, the same swaps, and the same "invalid order function for sorting" checks. A comparator that is not a
    /// strict order therefore leaves the table in retail's order, or raises where retail raises. Sorts in place.
    /// </summary>
    private static void AuxSort(LuaState state, LuaTable table, LuaValue comparator, int l, int u)
    {
        while (l < u)
        {
            // sort a[l], a[(l+u)/2] and a[u]
            if (SortLess(state, comparator, table.Get(Index(u)), table.Get(Index(l))))
                Swap(table, l, u);
            if (u - l == 1) break;
            var i = (l + u) / 2;
            if (SortLess(state, comparator, table.Get(Index(i)), table.Get(Index(l))))
                Swap(table, i, l);
            else if (SortLess(state, comparator, table.Get(Index(u)), table.Get(Index(i))))
                Swap(table, i, u);
            if (u - l == 2) break;

            // the pivot P = a[i] moves to a[u-1]; only a[l+1] .. a[u-2] are left to partition
            var pivot = table.Get(Index(i));
            Swap(table, i, u - 1);
            i = l;
            var j = u - 1;
            while (true)
            {
                // repeat ++i until a[i] >= P
                while (SortLess(state, comparator, table.Get(Index(++i)), pivot))
                {
                    if (i > u) throw new LuaError("invalid order function for sorting");
                }
                // repeat --j until a[j] <= P
                while (SortLess(state, comparator, pivot, table.Get(Index(--j))))
                {
                    if (j < l) throw new LuaError("invalid order function for sorting");
                }
                if (j < i) break;
                Swap(table, i, j);
            }
            Swap(table, u - 1, i);

            // the smaller half is sorted by recursion, the larger one by the next pass of this loop
            if (i - l < u - i)
            {
                j = l;
                i = i - 1;
                l = i + 2;
            }
            else
            {
                j = i + 1;
                i = u;
                u = j - 2;
            }
            AuxSort(state, table, comparator, j, i);
        }
    }

    private static bool SortLess(LuaState state, LuaValue comparator, LuaValue left, LuaValue right) =>
        comparator.IsNil ? state.OrderLess(left, right) : state.CallOne(comparator, left, right).Truthy;

    private static void Swap(LuaTable table, int first, int second)
    {
        var value = table.Get(Index(first));
        table.Set(Index(first), table.Get(Index(second)));
        table.Set(Index(second), value);
    }

    private static LuaValue Index(int index) => LuaValue.Of((double)index);

    private static LuaValue[] ForEach(LuaState state, LuaValue[] args)
    {
        var table = ArgTable(args, 1, "foreach");
        if (args.Length < 2 || args[1].Type != LuaType.Function)
            throw LuaErrors.BadArgument(2, "foreach", "function", args.Length < 2 ? LuaValue.Nil : args[1]);
        foreach (var (key, value) in table.Entries)
        {
            var results = state.Call(args[1], key, value);
            if (results.Length > 0 && !results[0].IsNil) return new[] { results[0] };
        }
        return new[] { LuaValue.Nil };
    }

    private static LuaValue[] ForEachI(LuaState state, LuaValue[] args)
    {
        var table = ArgTable(args, 1, "foreachi");
        if (args.Length < 2 || args[1].Type != LuaType.Function)
            throw LuaErrors.BadArgument(2, "foreachi", "function", args.Length < 2 ? LuaValue.Nil : args[1]);
        for (var i = 1; i <= table.Length; i++)
        {
            var key = LuaValue.Of((double)i);
            var results = state.Call(args[1], key, table.Get(key));
            if (results.Length > 0 && !results[0].IsNil) return new[] { results[0] };
        }
        return new[] { LuaValue.Nil };
    }
}
