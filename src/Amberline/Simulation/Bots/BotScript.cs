namespace Amberline.Simulation.Bots;

/// <summary>
/// The cadence and the cast-loop shape of the shipped patch-126 bot scripts
/// (<c>DATA/Scripts/Bot.luaobj</c> and the <c>Task*.luaobj</c> modules it loads;
/// and the research listings of them). The scripts own
/// the decision: their own <c>UpdatePriority</c>/<c>BeginTask</c>/<c>Tick</c>
/// run on their own timers, so the priority formulas a C# port used to hold here
/// are gone and only what the engine's own step and the cast path still measure
/// against remains.
/// </summary>
internal static class BotScript
{
    // Bot.luaobj OnInit: InitTimer("TimerHackDelayedInit", 1.0, once). That timer
    // builds the task list and starts InitTimer("TimerUpdate", 0.25, repeat).
    public const double InitDelaySeconds = 1.0;
    public const double UpdateIntervalSeconds = 0.25;
    public const double FirstUpdateSeconds = InitDelaySeconds + UpdateIntervalSeconds;

    // TaskCastSpell.BeginTask (Annie, Sivir, Soraka, ...): i = 0; loop { slot = math.random(0, 3); castable -> cast and return;
    // else if 20 < i return; i = i + 1 }. The test follows the draw, so i = 0..21 each draw once: 22 attempts, not 21
    // (bytecode 0030 LESSTHAN 20 < R1, 0033 ADD, 0034 JMP back to the draw).
    public const int CastAttempts = 22;
    public const int CastSlotCount = 4;
}
