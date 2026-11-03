namespace Amberline.Scripting.Lua;

/// <summary>
/// A Lua table: one key/value map with a recorded insertion order, so <c>pairs</c>/<c>next</c>
/// are deterministic. The map holds both array slots and hash keys, exactly as a Lua table does.
/// </summary>
public sealed class LuaTable
{
    private readonly Dictionary<LuaValue, LuaValue> _values = new();
    private readonly List<LuaValue> _order = new();

    public LuaTable? Metatable { get; set; }

    /// <summary>Number of recorded keys; the tests and audits read this.</summary>
    public int Count => _values.Count;

    public LuaValue Get(LuaValue key) => _values.TryGetValue(key, out var value) ? value : LuaValue.Nil;

    public bool TryGet(LuaValue key, out LuaValue value) => _values.TryGetValue(key, out value);

    public bool ContainsKey(LuaValue key) => _values.ContainsKey(key);

    /// <summary>
    /// <c>rawset</c> and the interpreter's <c>SETTABLE</c> without a metamethod. Lua 5.1 raises
    /// <c>table index is nil</c> / <c>table index is NaN</c> and treats any other key as legal.
    /// </summary>
    public void Set(LuaValue key, LuaValue value)
    {
        if (key.IsNil) throw new LuaError("table index is nil");
        if (key.Type == LuaType.Number && double.IsNaN(key.AsNumber)) throw new LuaError("table index is NaN");

        if (value.IsNil)
        {
            if (_values.Remove(key)) _order.Remove(key);
            return;
        }

        if (!_values.ContainsKey(key)) _order.Add(key);
        _values[key] = value;
    }

    /// <summary>
    /// The array border: the largest <c>n</c> with every slot <c>1..n</c> present. Real Lua's
    /// <c>luaH_getn</c> may pick another border for a sparse table; any border is legal, and
    /// <c>Reconstruction:</c> this one is always the first.
    /// </summary>
    public int Length
    {
        get
        {
            var n = 0;
            while (_values.ContainsKey(LuaValue.Of((double)(n + 1)))) n++;
            return n;
        }
    }

    /// <summary>
    /// The <c>next</c> sequence: the contiguous array slots <c>1, 2, …</c> first, then every
    /// remaining key in insertion order.
    /// <c>Reconstruction:</c> real Lua 5.1 walks its internal hash order, which is unspecified
    /// and depends on the exact insertion sequence; a defined order keeps the interpreter and
    /// its checks reproducible.
    /// </summary>
    public IEnumerable<LuaValue> Keys
    {
        get
        {
            var border = Length;
            for (var i = 1; i <= border; i++) yield return LuaValue.Of((double)i);
            foreach (var key in _order)
            {
                if (key.Type == LuaType.Number && key.AsNumber >= 1 && key.AsNumber <= border &&
                    key.AsNumber == Math.Floor(key.AsNumber))
                    continue;
                yield return key;
            }
        }
    }

    /// <summary>The key after <paramref name="key"/> in <see cref="Keys"/>, or nil at the end.</summary>
    public LuaValue Next(LuaValue key)
    {
        if (key.IsNil) return Keys.FirstOrDefault();

        var seen = false;
        foreach (var candidate in Keys)
        {
            if (seen) return candidate;
            if (candidate == key) seen = true;
        }
        return LuaValue.Nil;
    }

    public IEnumerable<KeyValuePair<LuaValue, LuaValue>> Entries
    {
        get
        {
            foreach (var key in Keys)
            {
                if (_values.TryGetValue(key, out var value)) yield return new KeyValuePair<LuaValue, LuaValue>(key, value);
            }
        }
    }
}
