namespace Amberline.Simulation.Ai;

/// <summary>
/// The engine half the units a <c>BBSpawnMinion</c> gave <c>idle.lua</c>, <c>Aggro.lua</c> or <c>Minion.lua</c> ask
/// for (a ward, a wall piece, a blood drop, Wukong's Nimbus clones): the whole ownership half
/// (<see cref="ILuaAiOwnedEngine"/>) and the whole lane-minion half
/// (<see cref="ILuaAiMinionEngine"/>). Idle.luaobj names nothing but <c>SetState</c> and Aggro
/// names the rest of both, so the two are one marker here: an extra binding is harmless, a missing
/// one is a real error, and <see cref="LuaAiInstance"/> already binds both halves for any engine
/// that carries them.
/// <para>
/// It is a marker and not a second copy of the members so that the host can tell these units from
/// a lane minion (whose <see cref="Minions.MinionLuaAiEngine"/> carries the same two halves) and
/// from a pet, without either of the other two claiming a ward.
/// </para>
/// </summary>
public interface ILuaAiSpawnedEngine : ILuaAiOwnedEngine, ILuaAiMinionEngine
{
}
