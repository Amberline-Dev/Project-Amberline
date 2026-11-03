using Amberline.Core;

namespace Amberline.Simulation.Combat;

/// <summary>Map1 structure protection state machine.</summary>
public static class ProtectionRules
{
    public static void Recompute(WorldState world, bool engaged, Action<Entity>? changed = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!engaged) return;
        // Batch 563: the Basic Tutorial's structures are the TutorialScript's (OnTutorialInit pairs SetInvulnerable with SetTargetable on
        // each, TutorialScript.txt proto 0.3 pc 0080-0123, and later events release them). This ladder is Map1's LevelScript port, so on
        // map 3 it only reports what the script left.
        if (MapDefinitions.OnTutorial)
        {
            foreach (var structure in world.Entities.Values.Where(entity =>
                         IsTurret(entity) || entity.Kind is EntityKind.Inhibitor or EntityKind.Nexus).ToArray())
            {
                if (structure.ProtectionSent == structure.Protected) continue;
                structure.ProtectionSent = structure.Protected;
                changed?.Invoke(structure);
            }
            return;
        }

        var byName = world.Entities.Values
            .Where(entity => !string.IsNullOrEmpty(entity.Name) &&
                             (entity.Kind is EntityKind.Turret or EntityKind.Inhibitor or
                              EntityKind.Nexus || entity.UnitType is UnitType.Turret or UnitType.Structure))
            .GroupBy(entity => entity.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var turret in world.Entities.Values.Where(IsTurret))
        {
            var tier = MapDefinitions.TurretTiers.TryGetValue(turret.Name, out var value)
                ? value : TurretTier.Outer;
            // Nexus turrets are computed once from team inhibitor state below.
            // Setting them false here would emit a false/true pair every scan.
            if (tier == TurretTier.Nexus) continue;
            var protectedState = tier switch
            {
                TurretTier.Outer => false,
                TurretTier.Fountain => true,
                TurretTier.Inner or TurretTier.Inhibitor =>
                    MapDefinitions.TurretFront.TryGetValue(turret.Name, out var frontName) &&
                    byName.TryGetValue(frontName, out var front) && front.Alive,
                _ => false,
            };
            Set(turret, protectedState, changed);
        }

        foreach (var inhibitor in world.Entities.Values.Where(entity => entity.Kind == EntityKind.Inhibitor))
        {
            var guard = MapDefinitions.TurretGuardsInhibitor
                .FirstOrDefault(pair => string.Equals(pair.Value, inhibitor.Name, StringComparison.Ordinal)).Key;
            var protectedState = false;
            if (!string.IsNullOrEmpty(guard))
            {
                var cursor = guard;
                for (var depth = 0; depth < 3 && cursor is not null; depth++)
                {
                    if (byName.TryGetValue(cursor, out var turret) && turret.Alive)
                    {
                        protectedState = true;
                        break;
                    }

                    cursor = MapDefinitions.TurretFront.TryGetValue(cursor, out var next) ? next : null;
                }
            }

            Set(inhibitor, protectedState, changed);
        }

        // Map1/LevelScript.luaobj HandleDestroyedObject (proto 0.14) and its dampener
        // BarrackReactiveEvent respawn handler (proto 0.13): the releases are one-way.
        // Any dampener death makes the team's two HQ towers (HQ_TOWER1/2) vulnerable for
        // good, and DeactivateCorrectStructure (proto 0.7) makes the nexus vulnerable once
        // both HQ towers have died. A dampener respawn only restores the dampener itself
        // (SetInvulnerable(false)/SetTargetable(true)), so nothing re-protects the towers
        // or the nexus. StructureFell is the per-structure monotonic latch; it is set here
        // because Kill recomputes protection on every structure death.
        foreach (var structure in byName.Values)
            if (!structure.Alive) structure.StructureFell = true;

        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var inhibitors = world.Entities.Values
                .Where(entity => entity.Kind == EntityKind.Inhibitor && entity.Team == team)
                .ToArray();
            var dampenerFell = inhibitors.Any(entity => entity.StructureFell);
            var nexusTurrets = world.Entities.Values.Where(entity => IsTurret(entity) &&
                entity.Team == team && MapDefinitions.TurretTiers.TryGetValue(entity.Name, out var tier) &&
                tier == TurretTier.Nexus).ToArray();
            // A world without dampeners has nothing to guard its HQ towers (unchanged).
            foreach (var turret in nexusTurrets) Set(turret, inhibitors.Length > 0 && !dampenerFell, changed);

            // A world without HQ towers falls back to the dampener latch (previous behaviour).
            var nexusProtected = nexusTurrets.Length > 0
                ? !nexusTurrets.All(entity => entity.StructureFell)
                : inhibitors.Length > 0 && !dampenerFell;
            foreach (var nexus in world.Entities.Values.Where(entity => entity.Kind == EntityKind.Nexus && entity.Team == team))
                Set(nexus, nexusProtected, changed);
        }
    }

    public static void Engage(WorldState world, Action<Entity>? changed = null) => Recompute(world, true, changed);

    private static bool IsTurret(Entity entity) => entity.Kind == EntityKind.Turret || entity.UnitType == UnitType.Turret;

    private static void Set(Entity entity, bool value, Action<Entity>? changed)
    {
        // A structure's targetability is the same bit, and the ladder owns both. Map1's shipped
        // AllowDamageOnBuildings always moves them together (LevelScript.txt:1643-1660 pairs
        // SetInvulnerable(t,true) with SetNotTargetableToTeam(t,true,enemyTeam) on every guarded
        // tower, and SetInvulnerable(t,false) with SetTargetable(t,true) on the released front
        // tower), and ProtocolCombatTransport / MatchRuntime / ReplicationTruth already report
        // IsTargetableToTeam from Protected alone (SFlagNontargetableEnemy vs SFlagTargetableToAll).
        // Reconciling here is what undoes a script SetNotTargetableToTeam that outlives the
        // guard it was written for: AllowDamageOnBuildings re-arms every tower at 10s, so a tower
        // this ladder has since released would otherwise stay locked out of minion target scans.
        entity.NotTargetableToAlly = false;
        entity.NotTargetableToEnemy = value;
        entity.Protected = value;
        // The level script's SetInvulnerable writes Protected before this ladder runs (LevelScriptHost), so a comparison with
        // Protected swallowed the engage protection of the inner towers: the server held them protected while the clients
        // kept the unprotected state they were last sent. The transition is judged against what the clients were sent.
        if (entity.ProtectionSent == value) return;
        entity.ProtectionSent = value;
        changed?.Invoke(entity);
    }
}
