using Amberline.Data;

namespace Amberline.Scripting.Lua;

/// <summary>
/// The Lua 5.1 register machine. Every opcode is implemented from <c>lvm.c</c> / <c>lopcodes.h</c>:
/// in particular <c>EQ</c>/<c>LT</c>/<c>LE</c> take their jump condition in the A field
/// (<c>if ((RK(B) op RK(C)) ~= A) then PC++</c>), and <c>CALL</c>/<c>RETURN</c>/<c>VARARG</c>/<c>SETLIST</c>
/// treat a zero B or C as "up to the top of the frame".
/// </summary>
internal sealed class LuaInterpreter
{
    private readonly LuaState _state;
    private readonly List<LuaFrame> _frames = new();

    public LuaInterpreter(LuaState state) => _state = state;

    public IReadOnlyList<LuaFrame> Frames => _frames;

    /// <summary>Runs until the frame stack is back to <paramref name="stopDepth"/>, returning the host call's results.</summary>
    public LuaValue[] Execute(int stopDepth)
    {
        while (_frames.Count > stopDepth) RunFrame(_frames[^1]);
        return _state.HostResults;
    }

    public void Push(LuaFrame frame) => _frames.Add(frame);

    public void Pop() => _frames.RemoveAt(_frames.Count - 1);

    /// <summary>
    /// Pops every frame above <paramref name="depth"/>, closing their upvalues: the path an error
    /// takes out of a call, so a <c>pcall</c> that catches it finds the stack it started with.
    /// </summary>
    public void Unwind(int depth)
    {
        while (_frames.Count > depth)
        {
            _frames[^1].CloseUpvalues(0);
            Pop();
        }
    }

    private void RunFrame(LuaFrame frame)
    {
        var prototype = frame.Prototype;
        var code = prototype.Code;
        var constants = prototype.Constants;
        var registers = frame.Registers;
        try
        {
            while (true)
            {
                var instruction = LuaInstruction.Decode(code[frame.Pc]);
                var a = instruction.A;
                var b = instruction.B;
                var c = instruction.C;
                frame.Pc++;

                switch (instruction.Opcode)
                {
                    case (int)LuaOpcode.Move:
                        registers[a] = registers[b];
                        break;

                    case (int)LuaOpcode.LoadK:
                        registers[a] = Constant(constants, instruction.Bx);
                        break;

                    case (int)LuaOpcode.LoadBool:
                        registers[a] = LuaValue.Of(b != 0);
                        if (c != 0) frame.Pc++;
                        break;

                    case (int)LuaOpcode.LoadNil:
                        for (var i = a; i <= b; i++) registers[i] = LuaValue.Nil;
                        break;

                    case (int)LuaOpcode.GetUpval:
                        registers[a] = frame.Closure.Upvalues[b].Value;
                        break;

                    case (int)LuaOpcode.GetGlobal:
                        registers[a] = _state.Index(LuaValue.Of(frame.Closure.Environment), LuaValue.Of(ConstantName(constants, instruction.Bx)));
                        break;

                    case (int)LuaOpcode.GetTable:
                        registers[a] = _state.Index(registers[b], Rk(registers, constants, c));
                        break;

                    case (int)LuaOpcode.SetGlobal:
                        _state.NewIndex(LuaValue.Of(frame.Closure.Environment), LuaValue.Of(ConstantName(constants, instruction.Bx)), registers[a]);
                        break;

                    case (int)LuaOpcode.SetUpval:
                        frame.Closure.Upvalues[b].Value = registers[a];
                        break;

                    case (int)LuaOpcode.SetTable:
                        _state.NewIndex(registers[a], Rk(registers, constants, b), Rk(registers, constants, c));
                        break;

                    case (int)LuaOpcode.NewTable:
                        registers[a] = LuaValue.Of(new LuaTable());
                        break;

                    case (int)LuaOpcode.Self:
                    {
                        var target = registers[b];
                        registers[a + 1] = target;
                        registers[a] = _state.Index(target, Rk(registers, constants, c));
                        break;
                    }

                    case (int)LuaOpcode.Add:
                        registers[a] = Arith(ArithOp.Add, Rk(registers, constants, b), Rk(registers, constants, c));
                        break;

                    case (int)LuaOpcode.Sub:
                        registers[a] = Arith(ArithOp.Sub, Rk(registers, constants, b), Rk(registers, constants, c));
                        break;

                    case (int)LuaOpcode.Mul:
                        registers[a] = Arith(ArithOp.Mul, Rk(registers, constants, b), Rk(registers, constants, c));
                        break;

                    case (int)LuaOpcode.Div:
                        registers[a] = Arith(ArithOp.Div, Rk(registers, constants, b), Rk(registers, constants, c));
                        break;

                    case (int)LuaOpcode.Mod:
                        registers[a] = Arith(ArithOp.Mod, Rk(registers, constants, b), Rk(registers, constants, c));
                        break;

                    case (int)LuaOpcode.Pow:
                        registers[a] = Arith(ArithOp.Pow, Rk(registers, constants, b), Rk(registers, constants, c));
                        break;

                    case (int)LuaOpcode.Unm:
                        // luaV_arith passes the operand twice for the unary minus, so a lone
                        // number or numeric string is enough and only a real table reaches __unm.
                        registers[a] = Arith(ArithOp.Unm, registers[b], registers[b]);
                        break;

                    case (int)LuaOpcode.Not:
                        registers[a] = LuaValue.Of(!registers[b].Truthy);
                        break;

                    case (int)LuaOpcode.Length:
                        registers[a] = _state.LengthOf(registers[b]);
                        break;

                    case (int)LuaOpcode.Concat:
                    {
                        var value = Rk(registers, constants, b);
                        for (var i = b + 1; i <= c; i++) value = _state.Concat(value, registers[i]);
                        registers[a] = value;
                        break;
                    }

                    case (int)LuaOpcode.Jump:
                        frame.Pc += instruction.SBx;
                        break;

                    // A is the jump-condition flag: `if ((RK(B) op RK(C)) ~= A) then PC++`, so the
                    // following JMP runs when the comparison equals A and is skipped when it does not.
                    case (int)LuaOpcode.Equal:
                        if (_state.Equal(Rk(registers, constants, b), Rk(registers, constants, c)) != (a != 0))
                            frame.Pc++;
                        break;

                    case (int)LuaOpcode.LessThan:
                        if (_state.OrderLess(Rk(registers, constants, b), Rk(registers, constants, c)) != (a != 0))
                            frame.Pc++;
                        break;

                    case (int)LuaOpcode.LessEqual:
                        if (_state.OrderLessEqual(Rk(registers, constants, b), Rk(registers, constants, c)) != (a != 0))
                            frame.Pc++;
                        break;

                    case (int)LuaOpcode.Test:
                        if (registers[a].Truthy != (c != 0)) frame.Pc++;
                        break;

                    case (int)LuaOpcode.TestSet:
                        if (registers[b].Truthy == (c != 0)) registers[a] = registers[b];
                        else frame.Pc++;
                        break;

                    case (int)LuaOpcode.Call:
                    {
                        // A pushed frame takes over; a native call has already placed its results.
                        if (_state.StartCall(registers[a], CollectArguments(frame, registers, a + 1, b), frame, a, c - 1))
                            return;
                        break;
                    }

                    case (int)LuaOpcode.TailCall:
                    {
                        // The frame keeps its caller and result slot; a Lua callee reuses it in place.
                        if (_state.StartCall(registers[a], CollectArguments(frame, registers, a + 1, b),
                                frame.Caller, frame.ResultBase, frame.WantResults, frame))
                            return;
                        CloseAndPop(frame);
                        return;
                    }

                    case (int)LuaOpcode.Return:
                    {
                        LuaValue[] results;
                        if (b == 0)
                        {
                            var count = Math.Max(0, frame.Top - a);
                            results = new LuaValue[count];
                            Array.Copy(registers, a, results, 0, count);
                        }
                        else
                        {
                            results = new LuaValue[b - 1];
                            Array.Copy(registers, a, results, 0, results.Length);
                        }
                        _state.PlaceResults(frame.Caller, frame.ResultBase, frame.WantResults, results);
                        CloseAndPop(frame);
                        return;
                    }

                    case (int)LuaOpcode.ForLoop:
                    {
                        var step = registers[a + 2].AsNumber;
                        var index = registers[a].AsNumber + step;
                        var limit = registers[a + 1].AsNumber;
                        if (step > 0 ? index <= limit : index >= limit)
                        {
                            frame.Pc += instruction.SBx;
                            registers[a] = LuaValue.Of(index);
                            registers[a + 3] = LuaValue.Of(index);
                        }
                        break;
                    }

                    case (int)LuaOpcode.ForPrep:
                    {
                        var init = ForNumber(registers[a], "initial value");
                        var limit = ForNumber(registers[a + 1], "limit");
                        var step = ForNumber(registers[a + 2], "step");
                        registers[a] = LuaValue.Of(init - step);
                        registers[a + 1] = LuaValue.Of(limit);
                        registers[a + 2] = LuaValue.Of(step);
                        frame.Pc += instruction.SBx;
                        break;
                    }

                    case (int)LuaOpcode.TForLoop:
                    {
                        // The iterator runs to completion before the jump is decided, exactly as
                        // luaD_call does inside OP_TFORLOOP: this is one nested run, not a new frame.
                        var results = _state.Call(registers[a], registers[a + 1], registers[a + 2]);
                        for (var i = 0; i < c; i++)
                            registers[a + 3 + i] = i < results.Length ? results[i] : LuaValue.Nil;

                        var jump = LuaInstruction.Decode(code[frame.Pc]);
                        frame.Pc++;
                        if (jump.Opcode != (int)LuaOpcode.Jump)
                            throw new LuaError("TFORLOOP is not followed by a JMP");
                        if (!registers[a + 3].IsNil)
                        {
                            registers[a + 2] = registers[a + 3];
                            frame.Pc += jump.SBx;
                        }
                        break;
                    }

                    case (int)LuaOpcode.SetList:
                    {
                        if (registers[a].Type != LuaType.Table)
                            throw LuaErrors.TypeError(registers[a], "index");
                        var target = registers[a].AsTable;
                        var realC = c == 0 ? (int)code[frame.Pc++] : c;
                        var count = b == 0 ? Math.Max(0, frame.Top - (a + 1)) : b;
                        var baseIndex = (realC - 1) * 50;
                        for (var i = 0; i < count; i++)
                            target.Set(LuaValue.Of((double)(baseIndex + i + 1)), registers[a + 1 + i]);
                        break;
                    }

                    case (int)LuaOpcode.Close:
                        frame.CloseUpvalues(a);
                        break;

                    case (int)LuaOpcode.Closure:
                    {
                        var child = prototype.Prototypes[instruction.Bx];
                        var upvalues = new LuaUpvalue[child.UpvalueCount];
                        for (var i = 0; i < upvalues.Length; i++)
                        {
                            // The pseudo-ops start at the pc the fetch already advanced to,
                            // exactly as lvm.c's `for (j = 0; j < nup; j++, pc++)` reads them.
                            var capture = LuaInstruction.Decode(code[frame.Pc++]);
                            if (capture.Opcode == (int)LuaOpcode.Move)
                                upvalues[i] = FindUpvalue(frame, capture.B);
                            else if (capture.Opcode == (int)LuaOpcode.GetUpval)
                                upvalues[i] = frame.Closure.Upvalues[capture.B];
                            else
                                throw new LuaError("bad CLOSURE upvalue pseudo-op");
                        }
                        registers[a] = LuaValue.Of(new LuaClosure(child, upvalues, frame.Closure.Environment));
                        break;
                    }

                    case (int)LuaOpcode.Vararg:
                    {
                        var count = b == 0 ? frame.Varargs.Length : b - 1;
                        for (var i = 0; i < count; i++)
                            registers[a + i] = i < frame.Varargs.Length ? frame.Varargs[i] : LuaValue.Nil;
                        frame.Top = a + count;
                        break;
                    }

                    default:
                        throw new LuaError($"unknown opcode {instruction.Opcode}");
                }
            }
        }
        catch (LuaError error) when (!error.IsLocated)
        {
            // frame.Pc points past the failing instruction; give the error its script and line.
            throw error.At(prototype.Source, frame.LineAt(frame.Pc > 0 ? frame.Pc - 1 : 0));
        }
    }

    private void CloseAndPop(LuaFrame frame)
    {
        frame.CloseUpvalues(0);
        Pop();
    }

    private static LuaValue[] CollectArguments(LuaFrame frame, LuaValue[] registers, int first, int b)
    {
        if (b != 0)
        {
            var arguments = new LuaValue[b - 1];
            Array.Copy(registers, first, arguments, 0, arguments.Length);
            return arguments;
        }

        var count = Math.Max(0, frame.Top - first);
        var multret = new LuaValue[count];
        Array.Copy(registers, first, multret, 0, count);
        return multret;
    }

    private static LuaUpvalue FindUpvalue(LuaFrame frame, int register)
    {
        var existing = frame.OpenUpvalues[register];
        if (existing is not null) return existing;
        var created = new LuaUpvalue(frame.Registers, register);
        frame.OpenUpvalues[register] = created;
        return created;
    }

    private double ForNumber(LuaValue value, string what)
    {
        if (value.ToNumber() is double number) return number;
        throw new LuaError($"'for' {what} must be a number");
    }

    private enum ArithOp { Add, Sub, Mul, Div, Mod, Pow, Unm }

    private LuaValue Arith(ArithOp op, LuaValue left, LuaValue right)
    {
        if (left.ToNumber() is double x && right.ToNumber() is double y)
        {
            return LuaValue.Of(op switch
            {
                ArithOp.Add => x + y,
                ArithOp.Sub => x - y,
                ArithOp.Mul => x * y,
                ArithOp.Div => x / y,
                ArithOp.Mod => Mod(x, y),
                ArithOp.Pow => Math.Pow(x, y),
                _ => -x,
            });
        }

        var name = op switch
        {
            ArithOp.Add => "__add",
            ArithOp.Sub => "__sub",
            ArithOp.Mul => "__mul",
            ArithOp.Div => "__div",
            ArithOp.Mod => "__mod",
            ArithOp.Pow => "__pow",
            _ => "__unm",
        };
        var metamethod = _state.BinaryMetamethod(name, left, right);
        if (metamethod is not null) return _state.CallMetamethod(metamethod, left, right);
        throw LuaErrors.Arithmetic(left, right);
    }

    /// <summary><c>luaV_mod</c>: the remainder takes the sign of the divisor, not of C#'s <c>%</c>.</summary>
    private static double Mod(double x, double y)
    {
        var remainder = x % y;
        return (remainder > 0 ? y < 0 : remainder < 0 && y > 0) ? remainder + y : remainder;
    }

    private static LuaValue Rk(LuaValue[] registers, IReadOnlyList<LuaConstant> constants, int operand) =>
        (operand & 0x100) != 0 ? Constant(constants, operand & 0xff) : registers[operand];

    private static LuaValue Constant(IReadOnlyList<LuaConstant> constants, int index)
    {
        if ((uint)index >= (uint)constants.Count) throw new LuaError($"constant index {index} is out of range");
        return constants[index] switch
        {
            LuaConstant.Nil => LuaValue.Nil,
            LuaConstant.Boolean value => LuaValue.Of(value.Value),
            LuaConstant.Number value => LuaValue.Of(value.Value),
            LuaConstant.String value => LuaValue.Of(value.Value),
            _ => throw new LuaError("unknown constant"),
        };
    }

    private static string ConstantName(IReadOnlyList<LuaConstant> constants, int index)
    {
        if ((uint)index >= (uint)constants.Count || constants[index] is not LuaConstant.String value)
            throw new LuaError($"constant {index} is not a string");
        return value.Value;
    }
}
