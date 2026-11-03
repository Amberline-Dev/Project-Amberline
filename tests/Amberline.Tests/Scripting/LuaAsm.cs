using Amberline.Data;

namespace Amberline.Tests;

/// <summary>
/// Hand-assembles Lua 5.1 prototypes for the interpreter checks. The shipped corpus is
/// bytecode-only and the tree has no compiler, so the semantics checks build the handful of
/// prototypes they need directly from the <c>lopcodes.h</c> encodings. The operand shapes used
/// here (SETLIST's C-1 block index, TFORLOOP followed by one JMP, FORPREP/FORLOOP sBx counted
/// from the instruction after) are the ones read off the bytecode listing of NeutralMinionSpawn.
/// </summary>
internal sealed class LuaAsm
{
    /// <summary>Bx is 18 bits; this is its midpoint, so sBx = Bx - 131071.</summary>
    private const int SBxOffset = 131071;

    /// <summary>Bit that marks an RK operand as constant <c>B/C &amp; 0xff</c> rather than a register.</summary>
    private const int RK = 0x100;

    private readonly List<uint> _code = new();
    private readonly List<LuaConstant> _constants = new();
    private readonly List<LuaPrototype> _protos = new();
    private readonly List<int> _lines = new();

    public byte MaxStack = 16;
    public byte Params;
    public byte IsVararg;
    public byte Upvalues;
    public string Source = "@semantics.lua";

    /// <summary>The source line recorded for every instruction emitted from here on.</summary>
    public int Line = 1;

    /// <summary>The index the next emitted instruction will get.</summary>
    public int Here => _code.Count;

    // ---- constants -------------------------------------------------------------------------------

    private int Constant(LuaConstant value)
    {
        for (var i = 0; i < _constants.Count; i++)
            if (_constants[i] == value) return i;
        _constants.Add(value);
        return _constants.Count - 1;
    }

    public int Kn(double value) => Constant(new LuaConstant.Number(value));
    public int Ks(string value) => Constant(new LuaConstant.String(value));
    public int Kb(bool value) => Constant(new LuaConstant.Boolean(value));

    /// <summary>An RK operand naming a constant.</summary>
    public int Knk(double value) => RK | Kn(value);

    /// <summary>An RK operand naming a constant.</summary>
    public int Ksk(string value) => RK | Ks(value);

    /// <summary>An RK operand naming a constant.</summary>
    public int Kbk(bool value) => RK | Kb(value);

    /// <summary>An RK operand naming a constant nil (its index is allocated like any other).</summary>
    public int Knilk() => RK | Constant(new LuaConstant.Nil());

    /// <summary>Loads a constant into a register, whichever kind it is.</summary>
    public void Load(int a, double value) => LoadK(a, Kn(value));

    /// <summary>Loads a constant into a register, whichever kind it is.</summary>
    public void Load(int a, string value) => LoadK(a, Ks(value));

    /// <summary>Loads a constant into a register, whichever kind it is.</summary>
    public void Load(int a, bool value) => LoadK(a, Kb(value));

    // ---- encoding --------------------------------------------------------------------------------

    private void Emit(uint raw)
    {
        _code.Add(raw);
        _lines.Add(Line);
    }

    private static uint Abc(LuaOpcode op, int a, int b, int c) =>
        (uint)((int)op | (a << 6) | (c << 14) | (b << 23));

    private static uint Abx(LuaOpcode op, int a, int bx) => (uint)((int)op | (a << 6) | (bx << 14));

    private static uint Asbx(LuaOpcode op, int a, int sbx) => Abx(op, a, sbx + SBxOffset);

    // ---- opcodes ---------------------------------------------------------------------------------

    public void Move(int a, int b) => Emit(Abc(LuaOpcode.Move, a, b, 0));
    public void LoadK(int a, int k) => Emit(Abx(LuaOpcode.LoadK, a, k));
    public void LoadBool(int a, int b, int c = 0) => Emit(Abc(LuaOpcode.LoadBool, a, b, c));
    public void LoadNil(int a, int b) => Emit(Abc(LuaOpcode.LoadNil, a, b, 0));
    public void GetUpval(int a, int b) => Emit(Abc(LuaOpcode.GetUpval, a, b, 0));
    public void GetGlobal(int a, int name) => Emit(Abx(LuaOpcode.GetGlobal, a, name));
    public void GetTable(int a, int b, int c) => Emit(Abc(LuaOpcode.GetTable, a, b, c));
    public void SetGlobal(int a, int name) => Emit(Abx(LuaOpcode.SetGlobal, a, name));
    public void SetUpval(int a, int b) => Emit(Abc(LuaOpcode.SetUpval, a, b, 0));
    public void SetTable(int a, int b, int c) => Emit(Abc(LuaOpcode.SetTable, a, b, c));
    public void NewTable(int a, int b = 0, int c = 0) => Emit(Abc(LuaOpcode.NewTable, a, b, c));
    public void Self(int a, int b, int c) => Emit(Abc(LuaOpcode.Self, a, b, c));
    public void Add(int a, int b, int c) => Emit(Abc(LuaOpcode.Add, a, b, c));
    public void Sub(int a, int b, int c) => Emit(Abc(LuaOpcode.Sub, a, b, c));
    public void Mul(int a, int b, int c) => Emit(Abc(LuaOpcode.Mul, a, b, c));
    public void Div(int a, int b, int c) => Emit(Abc(LuaOpcode.Div, a, b, c));
    public void Mod(int a, int b, int c) => Emit(Abc(LuaOpcode.Mod, a, b, c));
    public void Pow(int a, int b, int c) => Emit(Abc(LuaOpcode.Pow, a, b, c));
    public void Unm(int a, int b) => Emit(Abc(LuaOpcode.Unm, a, b, 0));
    public void Not(int a, int b) => Emit(Abc(LuaOpcode.Not, a, b, 0));
    public void Length(int a, int b) => Emit(Abc(LuaOpcode.Length, a, b, 0));
    public void Concat(int a, int b, int c) => Emit(Abc(LuaOpcode.Concat, a, b, c));

    /// <summary>Any three-operand opcode: arithmetic, comparisons and SETTABLE/GETTABLE.</summary>
    public void Binary(LuaOpcode op, int a, int b, int c) => Emit(Abc(op, a, b, c));

    /// <summary>Any two-operand opcode: UNM, NOT and LENGTH.</summary>
    public void Unary(LuaOpcode op, int a, int b) => Emit(Abc(op, a, b, 0));

    /// <summary><paramref name="sbx"/> is relative to the instruction after this one.</summary>
    public void Jump(int sbx) => Emit(Asbx(LuaOpcode.Jump, 0, sbx));

    public void Equal(int a, int b, int c) => Emit(Abc(LuaOpcode.Equal, a, b, c));
    public void LessThan(int a, int b, int c) => Emit(Abc(LuaOpcode.LessThan, a, b, c));
    public void LessEqual(int a, int b, int c) => Emit(Abc(LuaOpcode.LessEqual, a, b, c));
    public void Test(int a, int c) => Emit(Abc(LuaOpcode.Test, a, 0, c));
    public void TestSet(int a, int b, int c) => Emit(Abc(LuaOpcode.TestSet, a, b, c));

    /// <summary><paramref name="b"/> is argument count + 1, <paramref name="c"/> is result count + 1; 0 means multret.</summary>
    public void Call(int a, int b, int c) => Emit(Abc(LuaOpcode.Call, a, b, c));

    public void TailCall(int a, int b) => Emit(Abc(LuaOpcode.TailCall, a, b, 0));

    /// <summary><paramref name="b"/> is result count + 1; 0 means everything up to the top.</summary>
    public void Return(int a, int b) => Emit(Abc(LuaOpcode.Return, a, b, 0));

    /// <summary><paramref name="sbx"/> is relative to the instruction after this one.</summary>
    public void ForLoop(int a, int sbx) => Emit(Asbx(LuaOpcode.ForLoop, a, sbx));

    /// <summary><paramref name="sbx"/> is relative to the instruction after this one.</summary>
    public void ForPrep(int a, int sbx) => Emit(Asbx(LuaOpcode.ForPrep, a, sbx));

    /// <summary>Generic-for step: results land in <c>R(a+3)</c>..<c>R(a+2+c)</c>; the next instruction must be a JMP.</summary>
    public void TForLoop(int a, int c) => Emit(Abc(LuaOpcode.TForLoop, a, 0, c));

    /// <summary><paramref name="c"/> is the 1-based block of 50 slots; 0 means the next instruction word holds it.</summary>
    public void SetList(int a, int b, int c) => Emit(Abc(LuaOpcode.SetList, a, b, c));

    /// <summary>Appends a raw instruction word, used as SETLIST's extra block operand.</summary>
    public void Word(int value) => Emit((uint)value);

    public void Close(int a) => Emit(Abc(LuaOpcode.Close, a, 0, 0));

    /// <summary>
    /// Builds a closure for <see cref="AddChild"/>'s prototype index. The pseudo-ops that follow
    /// (one per upvalue) are emitted with <see cref="CaptureRegister"/>/<see cref="CaptureUpvalue"/>.
    /// </summary>
    public void Closure(int a, int proto) => Emit(Abx(LuaOpcode.Closure, a, proto));

    /// <summary>Captures an enclosing register (the <c>MOVE</c> pseudo-op after <see cref="Closure"/>).</summary>
    public void CaptureRegister(int register) => Emit(Abc(LuaOpcode.Move, 0, register, 0));

    /// <summary>Captures an enclosing upvalue (the <c>GETUPVAL</c> pseudo-op after <see cref="Closure"/>).</summary>
    public void CaptureUpvalue(int index) => Emit(Abc(LuaOpcode.GetUpval, 0, index, 0));

    public void Vararg(int a, int b) => Emit(Abc(LuaOpcode.Vararg, a, b, 0));

    // ---- building --------------------------------------------------------------------------------

    /// <summary>Registers a nested prototype and returns its index for <see cref="Closure"/>.</summary>
    public int AddChild(LuaAsm child)
    {
        _protos.Add(child.Build());
        return _protos.Count - 1;
    }

    public LuaPrototype Build() => new(
        Source,
        LineDefined: 1,
        LastLineDefined: 1,
        UpvalueCount: Upvalues,
        ParameterCount: Params,
        IsVararg: IsVararg,
        MaxStackSize: MaxStack,
        Code: _code,
        Constants: _constants,
        Prototypes: _protos,
        LineInfo: _lines,
        Locals: Array.Empty<LuaLocal>(),
        UpvalueNames: Array.Empty<string?>());

    /// <summary>A chunk with the header the shipped files carry: 5.1, format 0, little-endian.</summary>
    public LuaChunk Chunk() => new(new LuaChunkHeader(0x51, 0, 1, 4, 4, 4, 8, 0), Build());
}
