using Amberline.Data;

namespace Amberline.Scripting.Lua;

/// <summary>
/// One activation record. Frames live on a list rather than the CLR stack, so a tail-recursive
/// script cannot overflow the process stack: <c>TAILCALL</c> re-initialises the current frame
/// in place instead of pushing another one.
/// </summary>
internal sealed class LuaFrame
{
    public LuaFrame(LuaClosure closure, LuaFrame? caller, int resultBase, int wantResults)
    {
        Closure = closure;
        Registers = new LuaValue[Math.Max((int)closure.Prototype.MaxStackSize, 2)];
        OpenUpvalues = new LuaUpvalue[Registers.Length];
        Caller = caller;
        ResultBase = resultBase;
        WantResults = wantResults;
        Top = Registers.Length;
    }

    public LuaClosure Closure { get; private set; }

    public LuaPrototype Prototype => Closure.Prototype;

    public LuaValue[] Registers { get; private set; }

    public LuaUpvalue?[] OpenUpvalues { get; private set; }

    public int Pc;

    /// <summary>Exclusive end of the value region left by the last multi-result instruction.</summary>
    public int Top;

    public LuaValue[] Varargs = Array.Empty<LuaValue>();

    /// <summary>The frame to receive this one's results, or <see langword="null"/> for a host call.</summary>
    public LuaFrame? Caller;

    /// <summary>Register in <see cref="Caller"/> where the results land.</summary>
    public int ResultBase;

    /// <summary>Results the caller wants, or -1 for "as many as there are".</summary>
    public int WantResults;

    /// <summary>The line of the instruction at <paramref name="pc"/>, or 0 when there is no debug info.</summary>
    public int LineAt(int pc) =>
        Prototype.LineInfo.Count > 0 && (uint)pc < (uint)Prototype.LineInfo.Count ? Prototype.LineInfo[pc] : 0;

    /// <summary>Tail calls reuse the frame but replace the function, the registers and the upvalues.</summary>
    public void Reuse(LuaClosure closure, LuaValue[] arguments)
    {
        foreach (var upvalue in OpenUpvalues) upvalue?.Close();
        Closure = closure;
        Registers = new LuaValue[Math.Max((int)closure.Prototype.MaxStackSize, 2)];
        OpenUpvalues = new LuaUpvalue[Registers.Length];
        Varargs = Array.Empty<LuaValue>();
        Pc = 0;
        Top = Registers.Length;
        Enter(arguments);
    }

    /// <summary>Places the call arguments into R0.. and stashes the rest as varargs.</summary>
    public void Enter(LuaValue[] arguments)
    {
        var parameters = Prototype.ParameterCount;
        for (var i = 0; i < parameters; i++)
            Registers[i] = i < arguments.Length ? arguments[i] : LuaValue.Nil;
        if (Prototype.IsVararg != 0 && arguments.Length > parameters)
        {
            Varargs = new LuaValue[arguments.Length - parameters];
            Array.Copy(arguments, parameters, Varargs, 0, Varargs.Length);
        }
    }

    /// <summary>Closes every open upvalue at or above <paramref name="fromRegister"/> (<c>luaF_close</c>).</summary>
    public void CloseUpvalues(int fromRegister)
    {
        for (var i = fromRegister; i < OpenUpvalues.Length; i++)
        {
            OpenUpvalues[i]?.Close();
            OpenUpvalues[i] = null;
        }
    }
}
