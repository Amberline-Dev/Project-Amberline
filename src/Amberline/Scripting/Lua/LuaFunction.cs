using Amberline.Data;

namespace Amberline.Scripting.Lua;

/// <summary>A callable: a script closure or a registered C# native.</summary>
public abstract class LuaFunction
{
    public virtual string Name => "?";
}

/// <summary>A host function. The engine registers these; the interpreter never invents them.</summary>
public delegate LuaValue[] LuaNativeFunc(LuaState state, LuaValue[] args);

public sealed class LuaNative : LuaFunction
{
    public LuaNative(string name, LuaNativeFunc invoke)
    {
        Name = name;
        Invoke = invoke;
    }

    public override string Name { get; }

    public LuaNativeFunc Invoke { get; }

    public LuaTable? Environment { get; init; }
}

/// <summary>A script function: one loaded prototype plus its captured upvalues and environment.</summary>
public sealed class LuaClosure : LuaFunction
{
    public LuaClosure(LuaPrototype prototype, LuaUpvalue[] upvalues, LuaTable environment)
    {
        Prototype = prototype;
        Upvalues = upvalues;
        Environment = environment;
    }

    public LuaPrototype Prototype { get; }

    public LuaUpvalue[] Upvalues { get; }

    public LuaTable Environment { get; set; }

    public string ChunkSource => Prototype.Source ?? "?";

    public override string Name
    {
        get
        {
            var source = Prototype.Source ?? "?";
            return LuaErrors.ChunkLabel(source) + ":" + Prototype.LineDefined.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}

/// <summary>
/// One upvalue cell. While the owning frame is alive it aliases a register of that frame
/// (<c>luaF_findupval</c>); <see cref="Close"/> freezes the value when the register goes away,
/// so two closures created over the same register keep sharing the variable.
/// </summary>
public sealed class LuaUpvalue
{
    private LuaValue[]? _registers;
    private int _index;
    private LuaValue _closed;

    public LuaUpvalue(LuaValue[] registers, int index)
    {
        _registers = registers;
        _index = index;
    }

    public LuaUpvalue(LuaValue value)
    {
        _registers = null;
        _closed = value;
    }

    public bool IsOpen => _registers is not null;

    public LuaValue Value
    {
        get => _registers is null ? _closed : _registers[_index];
        set
        {
            if (_registers is null) _closed = value;
            else _registers[_index] = value;
        }
    }

    public void Close()
    {
        if (_registers is null) return;
        _closed = _registers[_index];
        _registers = null;
    }
}
