namespace Amberline.Scripting.Lua;

/// <summary>
/// The C runtime's <c>rand()</c> state of one retail game: MSVCR80's <c>_holdrand</c>, which <c>_initptd</c> sets to 1.
/// Retail runs each game in its own server process, so one state belongs to one match here (<c>WorldState.LuaRandom</c>),
/// and every Lua state of that match draws from it. A standalone <see cref="LuaState"/> gets a fresh state at 1.
/// </summary>
public sealed class CrtState
{
    /// <summary><c>_holdrand</c>: <c>rand()</c> steps it, and <c>srand()</c> sets it.</summary>
    public uint HoldRand { get; set; } = 1;
}
