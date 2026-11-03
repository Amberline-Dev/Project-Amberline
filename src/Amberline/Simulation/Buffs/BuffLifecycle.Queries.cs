using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Stats;
using Amberline.Simulation.Minions;

namespace Amberline.Simulation.Buffs;

public sealed partial class BuffLifecycle
{
    internal bool UsesMana(Entity unit) => unit.UnitType == UnitType.Hero && _heroStats.UsesMana(unit);

    public bool HasBuff(Entity unit, string name) =>
        unit.Buffs.Any(buff => buff.Name == name);

    public BuffPayload? BuffPayload(Entity unit, string name) =>
        unit.Buffs.FirstOrDefault(buff => buff.Name == name)?.Payload;

    // Batch 536: a unit the lane engine drives (MinionAiTargeting.IsLiveMinion): a Rift lane minion, or a Dominion wave unit, which has no lane
    // type (batch 451, OdinWaves.ScriptOf). The release hooks (OnTauntEnd, OnFearEnd, OnCanMove, OnCanAttack) and the post-control anchors
    // tested MinionType only, so an Odin minion took OnFearBegin (the add path has no type test) and never OnFearEnd: minionLib's TimerFeared
    // kept wandering it with the expired fear, a zero-length Fear move each second that cancelled its orders (the map 8 bot match, seed 9801: a superminion frozen at one spot for 110 s, 60 lifecycle reports).
    private static bool IsLaneMinion(Entity unit) =>
        unit.UnitType == UnitType.Minion && (unit.MinionType is not null || OdinWaves.ScriptOf(unit) is not null);

    private static double DistanceSquared(Position2 first, Position2 second) =>
        (first.X - second.X) * (first.X - second.X) +
        (first.Z - second.Z) * (first.Z - second.Z);
}
