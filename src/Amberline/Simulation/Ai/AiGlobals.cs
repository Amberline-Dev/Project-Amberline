using Amberline.Scripting.Lua;

namespace Amberline.Simulation.Ai;

/// <summary>
/// The host globals a shipped AI script reads before it can run a single callback: the AI state
/// ids, the stop reasons, the order/input/team tables and the engine's null-object sentinel.
/// Their numeric values are <c>Reconstruction:</c>: retail's ids are not in the shipped Lua
/// objects, the client exe or its PDB, so they only have to be distinct and stable across a run.
/// The batch-139 recording host installs the same table (its check pins the inventory) and
/// <c>docs/scripting/lua-vm.md</c> lists every name.
/// </summary>
public static class AiGlobals
{
    /// <summary><c>AI_HALTED</c>: the state every callback returns early from, and the one <c>HaltAI</c> sets.</summary>
    public const double Halted = 0;

    /// <summary>Installs the id table and the host's <c>Nil</c> sentinel into one script state.</summary>
    public static void Install(LuaState state)
    {
        foreach (var (name, value) in Table) state.SetGlobal(name, LuaValue.Of(value));

        // `Nil` is the engine's null-object sentinel, not Lua nil: LevelScript compares it with
        // GetTurret()/GetDampener() results, so it has to be a value that is never equal to nil.
        state.SetGlobal("Nil", LuaValue.Of(new LuaTable()));
    }

    /// <summary>
    /// The 0x89 wire bit one <c>INPUT_*</c> id stands for. The packet carries one flag at a time and the client's callback
    /// switches on that single value. The input-locking flags are in docs/network/patch126-end-game.md: bit 1 camera
    /// toggle, 2 movement, 4 abilities, 8 summoners, 16 shop, 32 chat, 64 minimap movement, 128 camera movement. The ids
    /// themselves are <c>Reconstruction:</c> (see the table); they are internal. <c>EndOfGame.luaobj</c> locks the seven non-chat ones one call at a time
    /// (EndOfGame.txt proto 0.0 pc 0046-0073) and chat in its final phase (proto 0.5).
    /// </summary>
    public static uint InputLockMask(double flag) => flag switch
    {
        301 => 1,   // INPUT_CAMERALOCKING
        305 => 2,   // INPUT_MOVEMENT
        300 => 4,   // INPUT_ABILITIES
        307 => 8,   // INPUT_SUMMONERSPELLS
        306 => 16,  // INPUT_SHOP
        303 => 32,  // INPUT_CHAT
        304 => 64,  // INPUT_MINIMAPMOVEMENT
        302 => 128, // INPUT_CAMERAMOVEMENT
        _ => throw new ArgumentOutOfRangeException(nameof(flag),
            $"No input-locking flag is known for id {flag}."),
    };

    /// <summary>The id table, in the order the batch-139 inventory lists it.</summary>
    public static IReadOnlyList<(string Name, double Value)> Table { get; } = new (string, double)[]
    {
        ("AI_HALTED", 0), ("AI_IDLE", 1), ("AI_HARDIDLE", 2), ("AI_ATTACKMOVESTATE", 3),
        ("AI_ATTACKMOVE_ATTACKING", 4), ("AI_TAUNTED", 5), ("AI_FEARED", 6), ("AI_RETREAT", 7),
        ("AI_SOFTATTACK", 8), ("AI_HARDATTACK", 9), ("AI_STANDING", 10), ("AI_CHARMED", 11),
        ("AI_ATTACK_GOING_TO_LAST_KNOWN_LOCATION", 12), ("LOST_VISIBILITY", 13),
        ("AI_PET_IDLE", 14), ("AI_PET_SPAWNING", 15), ("AI_PET_HARDIDLE", 16),
        ("AI_PET_HARDIDLE_ATTACKING", 17), ("AI_PET_HARDATTACK", 18), ("AI_PET_HARDMOVE", 19),
        ("AI_PET_HARDRETURN", 20), ("AI_PET_HARDSTOP", 21), ("AI_PET_ATTACK", 22),
        ("AI_PET_ATTACKMOVE", 23), ("AI_PET_ATTACKMOVE_ATTACKING", 24), ("AI_PET_RETURN", 25),
        ("AI_PET_RETURN_ATTACKING", 26), ("AI_PET_HOLDPOSITION", 27),
        ("AI_PET_HOLDPOSITION_ATTACKING", 28), ("AI_PET_MOVE", 29),
        // The two engaging states the corpus reads and never writes: Turret.luaobj sets
        // AI_HARDIDLE_ATTACKING whenever it takes a target, Leashed.lua sets AI_ATTACK when it
        // engages. Reconstruction: the values only have to be distinct and stable; an unset global
        // reads nil, which SetState/NetSetState turn into 0 = AI_HALTED, so each state must exist.
        ("AI_HARDIDLE_ATTACKING", 30), ("AI_ATTACK", 31),
        // Hero.luaobj writes AI_MOVE for a MOVETO (OnOrder proto 0.2 pc 0078) and tests it in
        // TimerDistanceScan (proto 0.13 pc 0030). Reconstruction: the value only has to be
        // distinct and stable; an unset global reads nil, and SetStateAndMoveInPos would store
        // 0 = AI_HALTED, so the entry must exist.
        ("AI_MOVE", 32),
        ("STOPREASON_IMMEDIATELY", 100), ("STOPREASON_MOVING", 101), ("STOPREASON_TARGET_LOST", 102),
        ("ORDER_ATTACKMOVE", 200), ("ORDER_ATTACKTO", 201), ("ORDER_MOVETO", 202), ("ORDER_STOP", 203),
        ("ORDER_HOLD", 204), ("ORDER_TAUNT", 205), ("ORDER_PETHARDATTACK", 206),
        ("ORDER_PETHARDMOVE", 207), ("ORDER_PETHARDRETURN", 208), ("ORDER_PETHARDSTOP", 209),
        ("INPUT_ABILITIES", 300), ("INPUT_CAMERALOCKING", 301), ("INPUT_CAMERAMOVEMENT", 302),
        ("INPUT_CHAT", 303), ("INPUT_MINIMAPMOVEMENT", 304), ("INPUT_MOVEMENT", 305),
        ("INPUT_SHOP", 306), ("INPUT_SUMMONERSPELLS", 307),
        ("TEAM_UNKNOWN", 0), ("TEAM_ORDER", 100), ("TEAM_CHAOS", 200), ("TEAM_NEUTRAL", 300),
        ("TEAM_MAX", 400),
        ("DAMAGESOURCE_INTERNALRAW", 1), ("BUFF_Taunt", 7),
    };
}
