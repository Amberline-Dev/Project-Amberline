using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Amberline.Data;

public sealed class LuaFormatException : Exception
{
    public LuaFormatException(string message) : base(message) { }
    public LuaFormatException(string message, Exception inner) : base(message, inner) { }
}

public readonly record struct LuaChunkHeader(
    byte Version,
    byte Format,
    byte Endianness,
    byte IntegerSize,
    byte SizeTSize,
    byte InstructionSize,
    byte NumberSize,
    byte IntegralFlag);

public sealed record LuaLocal(string? Name, int StartPc, int EndPc);

public abstract record LuaConstant
{
    public sealed record Nil : LuaConstant;
    public sealed record Boolean(bool Value) : LuaConstant;
    public sealed record Number(double Value) : LuaConstant;
    public sealed record String(string Value) : LuaConstant;
}

public sealed record LuaPrototype(
    string? Source,
    int LineDefined,
    int LastLineDefined,
    byte UpvalueCount,
    byte ParameterCount,
    byte IsVararg,
    byte MaxStackSize,
    IReadOnlyList<uint> Code,
    IReadOnlyList<LuaConstant> Constants,
    IReadOnlyList<LuaPrototype> Prototypes,
    IReadOnlyList<int> LineInfo,
    IReadOnlyList<LuaLocal> Locals,
    IReadOnlyList<string?> UpvalueNames);

public sealed record LuaChunk(LuaChunkHeader Header, LuaPrototype TopLevel);

public enum LuaOpcode
{
    Move = 0, LoadK = 1, LoadBool = 2, LoadNil = 3, GetUpval = 4,
    GetGlobal = 5, GetTable = 6, SetGlobal = 7, SetUpval = 8, SetTable = 9,
    NewTable = 10, Self = 11, Add = 12, Sub = 13, Mul = 14, Div = 15,
    Mod = 16, Pow = 17, Unm = 18, Not = 19, Length = 20, Concat = 21,
    Jump = 22, Equal = 23, LessThan = 24, LessEqual = 25, Test = 26,
    TestSet = 27, Call = 28, TailCall = 29, Return = 30, ForLoop = 31,
    ForPrep = 32, TForLoop = 33, SetList = 34, Close = 35, Closure = 36,
    Vararg = 37
}

public readonly record struct LuaInstruction(uint Raw, int Opcode, int A, int B, int C, int Bx, int SBx)
{
    public LuaOpcode? KnownOpcode => Opcode is >= 0 and <= 37 ? (LuaOpcode)Opcode : null;

    public static LuaInstruction Decode(uint raw)
    {
        var op = (int)(raw & 0x3f);
        var a = (int)((raw >> 6) & 0xff);
        var c = (int)((raw >> 14) & 0x1ff);
        var b = (int)((raw >> 23) & 0x1ff);
        var bx = (int)((raw >> 14) & 0x3ffff);
        return new LuaInstruction(raw, op, a, b, c, bx, bx - 131071);
    }

    public bool IsConstantB => (B & 0x100) != 0;
    public bool IsConstantC => (C & 0x100) != 0;
    public int BRegisterOrConstantIndex => B & 0xff;
    public int CRegisterOrConstantIndex => C & 0xff;
}

public abstract record LuaValue
{
    public sealed record Nil : LuaValue;
    public sealed record Boolean(bool Value) : LuaValue;
    public sealed record Number(double Value) : LuaValue;
    public sealed record String(string Value) : LuaValue;
    public sealed record Global(string? Name) : LuaValue;
    public sealed record Array(IReadOnlyList<LuaValue> Values) : LuaValue;
    public sealed record Map(IReadOnlyDictionary<LuaValue, LuaValue> Values) : LuaValue;
}

public enum LuaLoadStatus { Success, Partial, Error }

public sealed record LuaLoadResult(
    LuaLoadStatus Status,
    IReadOnlyDictionary<string, LuaValue> Globals,
    string? Error,
    string? Reason,
    IReadOnlyList<string> Strings)
{
    public static LuaLoadResult ErrorResult(string error) => new(
        LuaLoadStatus.Error,
        new Dictionary<string, LuaValue>(StringComparer.Ordinal),
        error, null, Array.Empty<string>());
}

internal sealed class LuaUnsupportedOpcodeException : Exception
{
    public LuaUnsupportedOpcodeException(int opcode, int pc)
        : base($"opcode {(opcode is >= 0 and <= 37 ? ((LuaOpcode)opcode).ToString().ToUpperInvariant() : opcode.ToString(CultureInfo.InvariantCulture))} at pc={pc}")
    {
        Opcode = opcode;
        Pc = pc;
    }

    public int Opcode { get; }
    public int Pc { get; }
}

internal sealed class LuaPartialExtractionException : Exception
{
    public LuaPartialExtractionException(string message) : base(message) { }
}

internal sealed class LuaExecutionException : Exception
{
    public LuaExecutionException(string message) : base(message) { }
}

/// <summary>Reader and data-table reconstructor for the shipped Lua 5.1 .luaobj chunks.</summary>
public static class LuaObjectReader
{
    public static LuaChunk Parse(ReadOnlySpan<byte> data)
    {
        var reader = new Reader(data);
        if (!reader.Bytes(4).SequenceEqual(new byte[] { 0x1b, (byte)'L', (byte)'u', (byte)'a' }))
            throw new LuaFormatException("bad signature (not a Lua bytecode file)");

        var header = new LuaChunkHeader(reader.Byte(), reader.Byte(), reader.Byte(), reader.Byte(),
            reader.Byte(), reader.Byte(), reader.Byte(), reader.Byte());
        if (header.Version != 0x51 || header.Format != 0 || header.Endianness != 1 ||
            header.IntegerSize != 4 || header.SizeTSize != 4 || header.InstructionSize != 4 ||
            header.NumberSize != 8 || header.IntegralFlag != 0)
        {
            throw new LuaFormatException($"unsupported Lua chunk header: version=0x{header.Version:x2}, format={header.Format}, endian={header.Endianness}, int={header.IntegerSize}, size_t={header.SizeTSize}, instruction={header.InstructionSize}, number={header.NumberSize}, integral={header.IntegralFlag}");
        }
        return new LuaChunk(header, ReadPrototype(reader));
    }

    public static LuaChunk ParseFile(string path)
    {
        try
        {
            return Parse(File.ReadAllBytes(CaseInsensitivePaths.Resolve(path)));
        }
        catch (LuaFormatException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LuaFormatException($"cannot read {path}: {ex.Message}", ex);
        }
    }

    public static LuaChunk ParseData(string relativePath) => ParseFile(GameDataPaths.Default.Data(relativePath));

    public static LuaChunk ParseData(GameDataPaths paths, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return ParseFile(paths.Data(relativePath));
    }

    public static LuaLoadResult Load(string path)
    {
        var resolved = Resolve(path);
        byte[] data;
        try { data = File.ReadAllBytes(resolved); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return LuaLoadResult.ErrorResult(ex.Message); }

        LuaChunk chunk;
        try { chunk = Parse(data); }
        catch (Exception ex) when (ex is LuaFormatException or IndexOutOfRangeException)
        { return LuaLoadResult.ErrorResult($"not a valid .luaobj file: {ex.Message}"); }

        try
        {
            var globals = Reconstruct(chunk.TopLevel);
            return new LuaLoadResult(LuaLoadStatus.Success, globals, null, null, Array.Empty<string>());
        }
        catch (LuaPartialExtractionException ex)
        {
            return new LuaLoadResult(
                LuaLoadStatus.Partial,
                new Dictionary<string, LuaValue>(StringComparer.Ordinal),
                null,
                ex.Message,
                StringPool(chunk.TopLevel));
        }
        catch (LuaUnsupportedOpcodeException ex)
        {
            return new LuaLoadResult(
                LuaLoadStatus.Partial,
                new Dictionary<string, LuaValue>(StringComparer.Ordinal),
                null,
                $"unsupported opcode {OpcodeName(ex.Opcode)} at pc={ex.Pc} (real control flow, not a compiled data table)",
                StringPool(chunk.TopLevel));
        }
        catch (LuaExecutionException ex)
        {
            return LuaLoadResult.ErrorResult(ex.Message);
        }
    }

    public static LuaLoadResult LoadData(string relativePath) => Load(GameDataPaths.Default.Data(relativePath));

    public static LuaLoadResult LoadData(GameDataPaths paths, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Load(paths.Data(relativePath));
    }

    public static string ListBytecode(string path)
    {
        var resolved = Resolve(path);
        byte[] data;
        try { data = File.ReadAllBytes(resolved); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return $"error: {ex.Message}"; }
        LuaChunk chunk;
        try { chunk = Parse(data); }
        catch (Exception ex) when (ex is LuaFormatException or IndexOutOfRangeException)
        { return $"error: not a valid .luaobj file: {ex.Message}"; }

        var lines = new List<string> { $"-- {path}" };
        ListPrototype(chunk.TopLevel, "0", lines);
        return string.Join(Environment.NewLine, lines);
    }

    public static string ListBytecodeData(string relativePath) => ListBytecode(GameDataPaths.Default.Data(relativePath));

    public static string ListBytecodeData(GameDataPaths paths, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return ListBytecode(paths.Data(relativePath));
    }

    private static LuaPrototype ReadPrototype(Reader reader)
    {
        var source = reader.LString();
        var lineDefined = reader.Int32();
        var lastLineDefined = reader.Int32();
        var nups = reader.Byte();
        var numparams = reader.Byte();
        var isVararg = reader.Byte();
        var maxstack = reader.Byte();

        var code = ReadList(reader, "code", static r => r.UInt32());
        var constants = ReadList(reader, "constants", static r => ReadConstant(r));
        var prototypes = ReadList(reader, "prototypes", static r => ReadPrototype(r));
        var lineInfo = ReadList(reader, "line info", static r => r.Int32());
        var locals = ReadList(reader, "local variables", static r => new LuaLocal(r.LString(), r.Int32(), r.Int32()));
        var upvalueNames = ReadList(reader, "upvalue names", static r => r.LString());
        return new LuaPrototype(source, lineDefined, lastLineDefined, nups, numparams, isVararg,
            maxstack, code, constants, prototypes, lineInfo, locals, upvalueNames);
    }

    private static List<T> ReadList<T>(Reader reader, string name, Func<Reader, T> read)
    {
        var count = reader.Count(name);
        var result = new List<T>(count);
        for (var i = 0; i < count; i++) result.Add(read(reader));
        return result;
    }

    private static LuaConstant ReadConstant(Reader reader) => reader.Byte() switch
    {
        0 => new LuaConstant.Nil(),
        1 => new LuaConstant.Boolean(reader.Byte() != 0),
        3 => new LuaConstant.Number(reader.Number()),
        4 => new LuaConstant.String(reader.LString() ?? string.Empty),
        var type => throw new LuaFormatException($"unknown constant type {type}")
    };

    private static Dictionary<string, LuaValue> Reconstruct(LuaPrototype top)
    {
        var registers = Enumerable.Repeat<LuaValue>(new LuaValue.Nil(), 300).ToArray();
        var globals = new Dictionary<string, LuaValue>(StringComparer.Ordinal);
        var order = new List<string>();
        var code = top.Code;
        var i = 0;
        while (i < code.Count)
        {
            var instruction = LuaInstruction.Decode(code[i]);
            var op = instruction.Opcode;
            var a = instruction.A;
            var b = instruction.B;
            var c = instruction.C;
            LuaValue Rk(int x) => (x & 0x100) != 0
                ? ConstantValue(top.Constants, x & 0xff)
                : Register(registers, x);

            switch ((LuaOpcode?)instruction.KnownOpcode)
            {
                case LuaOpcode.Move: registers[a] = Register(registers, b); break;
                case LuaOpcode.LoadK: registers[a] = ConstantValue(top.Constants, instruction.Bx); break;
                case LuaOpcode.LoadBool:
                    registers[a] = new LuaValue.Boolean(b != 0);
                    if (c != 0) i++;
                    break;
                case LuaOpcode.LoadNil:
                    for (var k = a; k <= b; k++) registers[k] = new LuaValue.Nil();
                    break;
                case LuaOpcode.GetGlobal:
                {
                    var name = ConstantString(top.Constants, instruction.Bx);
                    registers[a] = globals.TryGetValue(name, out var value) ? value : new LuaValue.Global(name);
                    break;
                }
                case LuaOpcode.GetTable:
                    registers[a] = GetTable(Register(registers, b), Rk(c));
                    break;
                case LuaOpcode.SetGlobal:
                {
                    var name = ConstantString(top.Constants, instruction.Bx);
                    if (!globals.ContainsKey(name)) order.Add(name);
                    globals[name] = Register(registers, a);
                    break;
                }
                case LuaOpcode.SetTable:
                    SetTable(Register(registers, a), Rk(b), Rk(c));
                    break;
                case LuaOpcode.NewTable:
                    registers[a] = new TableBuilderValue();
                    break;
                case LuaOpcode.Self:
                {
                    var table = Register(registers, b);
                    registers[a + 1] = table;
                    registers[a] = GetTable(table, Rk(c));
                    break;
                }
                case LuaOpcode.SetList:
                    if (b == 0)
                        throw new LuaPartialExtractionException($"SETLIST at pc={i} uses B=0 (variable result count)");
                    if (c == 0)
                        throw new LuaPartialExtractionException($"SETLIST at pc={i} uses C=0 (extension word)");
                    var target = Register(registers, a) as TableBuilderValue
                        ?? throw new LuaExecutionException($"SETLIST target at pc={i} is not a table");
                    var first = checked((c - 1) * 50);
                    if (first != target.ArrayItems.Count)
                        throw new LuaPartialExtractionException(
                            $"SETLIST at pc={i} is noncontiguous (block starts at {first + 1}, existing array length is {target.ArrayItems.Count})");
                    for (var j = 0; j < b; j++) target.ArrayItems.Add(Register(registers, a + 1 + j));
                    break;
                case LuaOpcode.Closure:
                {
                    if ((uint)instruction.Bx >= (uint)top.Prototypes.Count)
                        throw new LuaExecutionException($"CLOSURE references missing prototype {instruction.Bx} at pc={i}");
                    registers[a] = new LuaValue.Global(null);
                    i += top.Prototypes[instruction.Bx].UpvalueCount;
                    break;
                }
                case LuaOpcode.Return:
                    break;
                case null:
                    throw new LuaUnsupportedOpcodeException(op, i);
                default:
                    throw new LuaUnsupportedOpcodeException(op, i);
            }
            i++;
        }

        var result = new Dictionary<string, LuaValue>(StringComparer.Ordinal);
        foreach (var name in order) result[name] = Clean(globals[name]);
        return result;
    }

    private static LuaValue Register(LuaValue[] registers, int index)
    {
        if ((uint)index >= (uint)registers.Length)
            throw new LuaExecutionException($"register index {index} is out of range");
        return registers[index];
    }

    private static LuaValue ConstantValue(IReadOnlyList<LuaConstant> constants, int index)
    {
        if ((uint)index >= (uint)constants.Count)
            throw new LuaExecutionException($"constant index {index} is out of range");
        return constants[index] switch
        {
            LuaConstant.Nil => new LuaValue.Nil(),
            LuaConstant.Boolean b => new LuaValue.Boolean(b.Value),
            LuaConstant.Number n => new LuaValue.Number(n.Value),
            LuaConstant.String s => new LuaValue.String(s.Value),
            _ => throw new LuaExecutionException("unknown constant")
        };
    }

    private static string ConstantString(IReadOnlyList<LuaConstant> constants, int index)
    {
        if ((uint)index >= (uint)constants.Count || constants[index] is not LuaConstant.String value)
            throw new LuaExecutionException($"constant {index} is not a string");
        return value.Value;
    }

    private static LuaValue GetTable(LuaValue table, LuaValue key) => table is TableBuilderValue builder
        ? builder.TableMap.TryGetValue(key, out var value) ? value : new LuaValue.Nil()
        : new LuaValue.Nil();

    private static void SetTable(LuaValue table, LuaValue key, LuaValue value)
    {
        if (table is not TableBuilderValue builder)
            throw new LuaExecutionException("SETTABLE target is not a table");
        builder.TableMap[key] = value;
    }

    private static LuaValue Clean(LuaValue value)
    {
        if (value is not TableBuilderValue table) return value;
        var map = new Dictionary<LuaValue, LuaValue>();
        foreach (var (key, child) in table.TableMap) map[key] = Clean(child);
        var array = table.ArrayItems.Select(Clean).ToArray();
        if (array.Length > 0 && map.Count == 0) return new LuaValue.Array(array);
        if (map.Count > 0 && array.Length == 0) return new LuaValue.Map(map);
        if (map.Count == 0) return new LuaValue.Map(map);
        map[new LuaValue.String("[array]")] = new LuaValue.Array(array);
        return new LuaValue.Map(map);
    }

    private static List<string> StringPool(LuaPrototype prototype)
    {
        var result = new List<string>();
        CollectStrings(prototype, result);
        return result;
    }

    private static void CollectStrings(LuaPrototype prototype, List<string> output)
    {
        foreach (var constant in prototype.Constants)
            if (constant is LuaConstant.String value) output.Add(value.Value);
        foreach (var child in prototype.Prototypes) CollectStrings(child, output);
    }

    private static void ListPrototype(LuaPrototype prototype, string path, List<string> output)
    {
        output.Add($"-- proto {path}: params={prototype.ParameterCount} vararg={prototype.IsVararg} upvals={prototype.UpvalueCount} maxstack={prototype.MaxStackSize} nconsts={prototype.Constants.Count} ninstr={prototype.Code.Count} nprotos={prototype.Prototypes.Count}");
        for (var i = 0; i < prototype.Constants.Count; i++)
            output.Add($"  K{i} = {ConstantLiteral(prototype.Constants[i])}");
        for (var i = 0; i < prototype.Code.Count; i++)
            output.Add($"  {i:0000}  {FormatInstruction(prototype.Code[i], prototype.Constants)}");
        for (var i = 0; i < prototype.Prototypes.Count; i++)
            ListPrototype(prototype.Prototypes[i], $"{path}.{i}", output);
    }

    private static string FormatInstruction(uint raw, IReadOnlyList<LuaConstant> constants)
    {
        var instruction = LuaInstruction.Decode(raw);
        var name = OpcodeName(instruction.Opcode);
        if (name is "LOADK" or "GETGLOBAL" or "SETGLOBAL")
            return $"{name} R{instruction.A} K{instruction.Bx}={ConstantLiteralAt(constants, instruction.Bx)}";
        if (name == "CLOSURE") return $"{name} R{instruction.A} proto#{instruction.Bx}";
        if (name == "JMP") return $"{name} sBx={instruction.SBx}";
        if (name is "FORLOOP" or "FORPREP") return $"{name} R{instruction.A} sBx={instruction.SBx}";
        if (name == "CONCAT") return $"{name} R{instruction.A} R{instruction.B} R{instruction.C}";
        if (name is "ADD" or "SUB" or "MUL" or "DIV" or "MOD" or "POW" or "EQ" or "LT" or "LE" or "SETTABLE")
            return $"{name} R{instruction.A} {RkLiteral(instruction.B, constants)} {RkLiteral(instruction.C, constants)}";
        if (name is "GETTABLE" or "SELF")
            return $"{name} R{instruction.A} R{instruction.B} {RkLiteral(instruction.C, constants)}";
        return $"{name} A={instruction.A} B={instruction.B} C={instruction.C}";
    }

    private static string RkLiteral(int value, IReadOnlyList<LuaConstant> constants) =>
        (value & 0x100) != 0 ? $"K{value & 0xff}={ConstantLiteralAt(constants, value & 0xff)}" : $"R{value}";

    private static string ConstantLiteralAt(IReadOnlyList<LuaConstant> constants, int index) =>
        (uint)index < (uint)constants.Count ? ConstantLiteral(constants[index]) : $"<bad-constant-{index}>";

    private static string ConstantLiteral(LuaConstant constant) => constant switch
    {
        LuaConstant.Nil => "nil",
        LuaConstant.Boolean b => b.Value ? "true" : "false",
        LuaConstant.Number n => NumberLiteral(n.Value),
        LuaConstant.String s => $"'{s.Value.Replace("'", "\\'", StringComparison.Ordinal)}'",
        _ => "?"
    };

    private static string NumberLiteral(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return double.IsFinite(value) && !text.Contains('.', StringComparison.Ordinal) &&
               !text.Contains('e', StringComparison.OrdinalIgnoreCase) ? text + ".0" : text;
    }

    private static string OpcodeName(int opcode) => opcode is >= 0 and <= 37
        ? ((LuaOpcode)opcode).ToString().ToUpperInvariant() switch
        {
            "GETUPVAL" => "GETUPVAL", "GETGLOBAL" => "GETGLOBAL", "GETTABLE" => "GETTABLE",
            "SETGLOBAL" => "SETGLOBAL", "SETUPVAL" => "SETUPVAL", "SETTABLE" => "SETTABLE",
            "NEWTABLE" => "NEWTABLE", "LOADBOOL" => "LOADBOOL", "LOADNIL" => "LOADNIL",
            "FORLOOP" => "FORLOOP", "FORPREP" => "FORPREP", "TFORLOOP" => "TFORLOOP",
            "TAILCALL" => "TAILCALL", "RETURN" => "RETURN", "CLOSURE" => "CLOSURE",
            "SETLIST" => "SETLIST", "VARARG" => "VARARG", "CONCAT" => "CONCAT",
            "MOVE" => "MOVE", "LOADK" => "LOADK", "SELF" => "SELF", "ADD" => "ADD",
            "SUB" => "SUB", "MUL" => "MUL", "DIV" => "DIV", "MOD" => "MOD", "POW" => "POW",
            "UNM" => "UNM", "NOT" => "NOT", "LENGTH" => "LEN", "JUMP" => "JMP",
            "EQUAL" => "EQ", "LESS_THAN" => "LT", "LESS_EQUAL" => "LE", "TEST" => "TEST",
            "TEST_SET" => "TESTSET", "CALL" => "CALL", "CLOSE" => "CLOSE", _ => ((LuaOpcode)opcode).ToString().ToUpperInvariant()
        }
        : $"OP{opcode}";

    private static string Resolve(string path) => Path.IsPathRooted(path) ? CaseInsensitivePaths.Resolve(path) : GameDataPaths.Default.Data(path);

    private sealed class Reader
    {
        private readonly ReadOnlyMemory<byte> _data;
        private int _position;
        public Reader(ReadOnlySpan<byte> data) => _data = data.ToArray();
        public byte Byte() => Bytes(1)[0];
        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Bytes(4));
        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));
        public double Number() => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(Bytes(8)));
        public byte[] Bytes(int count)
        {
            if (count < 0 || count > _data.Length - _position) throw new LuaFormatException("truncated file");
            var bytes = _data.Span.Slice(_position, count).ToArray();
            _position += count;
            return bytes;
        }
        public string? LString()
        {
            var size = UInt32();
            if (size == 0) return null;
            if (size > int.MaxValue) throw new LuaFormatException("Lua string is too large");
            var bytes = Bytes((int)size);
            if (bytes[^1] != 0) throw new LuaFormatException("Lua string is missing its NUL terminator");
            var chars = new char[bytes.Length - 1];
            for (var i = 0; i < chars.Length; i++) chars[i] = bytes[i] < 128 ? (char)bytes[i] : '\ufffd';
            return new string(chars);
        }
        public int Count(string name)
        {
            var count = Int32();
            if (count < 0 || count > _data.Length - _position)
                throw new LuaFormatException($"invalid {name} count {count}");
            return count;
        }
    }

    private sealed record TableBuilderValue : LuaValue
    {
        public Dictionary<LuaValue, LuaValue> TableMap { get; } = new();
        public List<LuaValue> ArrayItems { get; } = new();
    }
}
