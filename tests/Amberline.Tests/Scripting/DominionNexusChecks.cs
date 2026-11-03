using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Ai;

namespace Amberline.Tests;

/// <summary>
/// Batch 477: the Crystal Scar's nexuses are the two Odin crystals, not HQ_T1 and HQ_T2. The shipped
/// <c>DATA/Scripts/EndOfGame.luaobj</c> finds each team's nexus with <c>GetHQ(team)</c> (<c>orderHQ</c>, <c>chaosHQ</c>) and freezes it:
/// <c>SetInvulnerable(hq, true)</c>, <c>SetTargetable(hq, false)</c> and <c>SetBuildingHealthRegenEnabled(hq, false)</c>
/// (the bytecode listing of the shipped chunk). The name lookup answered nothing on map 8, so the crystals were never frozen.
/// </summary>
public static class DominionNexusChecks
{
    [Check("server.dominion-end-of-game-freezes-both-crystals")]
    public static void TheCeremonyFreezesBothCrystalNexuses()
    {
        var world = new WorldState();
        var order = world.Register(CrystalNexus(world, Team.Order, "LevelProp_Odin_SOG_Order_Crystal", new Position2(1740.5343, 4480.0933)));
        var chaos = world.Register(CrystalNexus(world, Team.Chaos, "LevelProp_Odin_SOG_Chaos_Crystal", new Position2(12163.1846, 4568.9819)));
        var errors = new List<string>();
        var engine = new RecordingEngine();
        var host = new LevelScriptHost(world, new AiScriptCatalog(GameDataPaths.Default.ForMap(GameMaps.CrystalScar)))
        {
            OnError = failure => errors.Add(failure.Hook + ": " + failure.Message),
            TotalTeamMinionsSpawned = _ => 0,
            EndOfGameEngine = engine,
        };
        host.OnLevelInit(0.0);
        // Chaos's crystal falls: the winner is Order, so the ceremony's losing team is Chaos.
        TestAssert.True(host.EndOfGameCeremony(Team.Order, chaos, 0.0), "the shipped ceremony is loaded");
        TestAssert.Equal(0, errors.Count, "the ceremony raises nothing: " + string.Join("; ", errors));

        foreach (var (label, crystal) in new[] { ("the winning Order crystal", order), ("the destroyed Chaos crystal", chaos) })
        {
            // SetInvulnerable and SetTargetable write the entity from the host; regeneration goes to the engine (CombatService).
            TestAssert.True(crystal.Protected, label + " is invulnerable (SetInvulnerable through GetHQ)");
            TestAssert.True(crystal.NotTargetableToAlly && crystal.NotTargetableToEnemy,
                label + " is untargetable (SetTargetable false through GetHQ)");
            TestAssert.True(engine.RegenOff.Contains(crystal), label + " has its regeneration off (SetBuildingHealthRegenEnabled through GetHQ)");
        }
    }

    /// <summary>The ceremony's engine seam with the one call this check follows; the others are no-ops (the production engine is CombatService).</summary>
    private sealed class RecordingEngine : IEndOfGameEngine
    {
        public List<Entity> RegenOff { get; } = [];

        public void SetBuildingHealthRegenEnabled(Entity building, bool enabled)
        {
            if (!enabled) RegenOff.Add(building);
        }

        public void SetInputLockingFlag(double flag, bool locked) { }
        public void DisableHudForEndOfGame() { }
        public void SetBarracksSpawnEnabled(bool enabled) { }
        public void CloseShop() { }
        public void HaltAllAi() { }
        public void MoveCameraToPoint(Entity hero, Position3 point, double seconds) { }
        public void SetGreyscaleEnabledWhenDead(Entity hero, bool enabled) { }
        public void FadeOutMainSfx(double seconds) { }
        public void SetNexusSkin(Team team, int skin) { }
        public void FadeMinions(Team team, double amount, double seconds) { }
        public void SetMinionsNoRender(Team team, bool noRender) { }
        public void EndGame(Team winner) { }
    }

    private static Entity CrystalNexus(WorldState world, Team team, string name, Position2 position) =>
        new(world.Allocate(UnitType.Structure), EntityKind.Nexus, UnitType.Structure, team, position)
        {
            Name = name,
            Health = 500.0,
            MaxHealth = 500.0,
        };
}
