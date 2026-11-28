using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;

namespace Amberline.Tests;

/// <summary>
/// Batch 946 (community report, 28 November 2026: in the Basic Tutorial "ashe doesn't really seem to attack at all"). The TutorialScript
/// switches its player's automatic acquisition off in OnTutorialInit (SetCanAutoAcquireTargets(playerID, false), proto 0.3 pc 0037-0040)
/// and on again in EventEnemyDefenses (proto 0.51 pc 0020-0023), so the minion fight is played with the switch off. Ashe at 500 units from
/// an enemy, the switch off: she does not attack on her own (retail as shipped), an attack order fights until it is changed, and an
/// attack-move now fights what it meets as well (before: it walked up to the enemy and stood, 0 damage in 10 s), a champion or a unit
/// the script spawned (the fight's Aggro.lua wizard). Once an attack-move has arrived the hero is idle again and the switch holds.
/// </summary>
public static class B946AutoAcquireChecks
{
    [Check("attacks.b946-attack-move-fights-while-the-script-holds-automatic-acquisition-off")]
    public static void AttackMoveFightsWhileTheScriptHoldsAutomaticAcquisitionOff()
    {
        TestAssert.Equal(0.0, Damage(null, switchOn: false), "idle, switch off: no attack of her own");
        TestAssert.True(Damage(null, switchOn: true) > 0.0, "idle, switch on: she attacks the enemy in reach (the control)");
        TestAssert.True(Damage(3, switchOn: false) > 0.0, "an attack order, switch off: she attacks");
        TestAssert.True(Damage(7, switchOn: false) > 0.0, "an attack-move at the enemy, switch off: she attacks what she meets");
        TestAssert.True(Damage(7, switchOn: false, wizard: true) > 0.0, "an attack-move at the script's Aggro.lua wizard, switch off: she attacks it");
        TestAssert.Equal(0.0, Damage(7, switchOn: false, arriveFirst: true), "an attack-move that arrived on empty ground leaves her idle: no attack");
    }

    /// <summary>
    /// The damage Ashe deals the enemy in 10 s after the order (null: none, she only stands with her idle scan armed). The enemy is the
    /// Chaos champion, or a Red_Minion_Wizard the foe spawns with Aggro.lua (the TutorialScript's BBSpawnMinion, EventNormalBattle). With
    /// <paramref name="arriveFirst"/> the attack-move goes to empty ground away from the enemy, which is brought in after the arrival.
    /// </summary>
    private static double Damage(byte? order, bool switchOn, bool wizard = false, bool arriveFirst = false)
    {
        using var match = new BbM3.Match("Ashe", foeChampion: "Orianna");
        var hero = match.Hero;
        var near = new Position2(hero.Position.X + 500.0, hero.Position.Z);
        var far = new Position2(12000.0, 3000.0);
        match.Foe.MaxHealth = match.Foe.Health = 50000.0;
        match.Foe.Position = wizard || arriveFirst ? far : near;
        var enemy = match.Foe;
        if (wizard)
        {
            var before = match.Runtime.World.Entities.Keys.ToHashSet();
            BbBlock Block(string function, params (string Key, BbValue Value)[] fields) =>
                new(function, fields.Select(field => new BbField(field.Key, field.Value)).ToArray(), []);
            // The foe spawns it where it stands (500 from Ashe), then leaves.
            var script = new BbScript("B946Spawn", "Spells/B946Spawn.luaobj", [], [new BbEventTable("SelfExecute",
            [
                Block("BBGetUnitPosition", ("UnitVar", new BbValue.Text("Owner")), ("PositionVar", new BbValue.Text("P"))),
                Block("BBSpawnMinion", ("Name", new BbValue.Text("Wizard")), ("Skin", new BbValue.Text("Red_Minion_Wizard")),
                    ("AiScript", new BbValue.Text("Aggro.lua")), ("PosVar", new BbValue.Text("P")), ("Team", new BbValue.Reference("TEAM_CHAOS")),
                    ("DestVar", new BbValue.Text("U"))),
            ])], []);
            match.Foe.Position = near;
            var at = new Position3(near.X, 0, near.Z);
            match.Module.Router!.RunSelfExecute(script, match.Foe, 0, 1, 0.0, at, at, null);
            match.Foe.Position = far;
            var spawned = match.Runtime.World.Entities.Values.Where(unit => !before.Contains(unit.NetId)).ToList();
            TestAssert.True(spawned.Count == 1, "the wizard is spawned: " + string.Join(", ", spawned.Select(unit => unit.Name)) + " " +
                string.Join(" | ", match.Module.ScriptFailures.Select(failure => $"{failure.Hook}/{failure.BlockPath}: {failure.Message}")));
            enemy = spawned[0];
            TestAssert.True(Math.Abs(enemy.Position.X - near.X) < 100.0 && Math.Abs(enemy.Position.Z - near.Z) < 100.0,
                $"the wizard stands 500 from Ashe ({enemy.Position.X:0},{enemy.Position.Z:0})");
        }
        hero.CanAutoAcquireTargets = switchOn;
        // The harness seats every hero with its idle scan off (as a Stop leaves it); a player's order turns it on, so the idle cases arm it.
        hero.HeroAutoAcquire = true;
        match.Advance(0.5);
        var view = match.Runtime.Session.Participants[0].View;
        view.Known.Add(enemy.NetId);
        view.Visible.Add(enemy.NetId);
        if (order is byte kind)
        {
            var point = arriveFirst ? new Position2(hero.Position.X, hero.Position.Z - 300.0) : enemy.Position;
            var packet = ClientGameplayPackets.MoveOrder(hero.NetId, kind, point, kind == 3 ? enemy.NetId : 0u);
            TestAssert.True(match.Runtime.Session.DispatchGameplay(1, 1, packet.Plaintext.Span, match.Runtime.Dependencies.Gameplay), $"order {kind}");
        }
        if (arriveFirst)
        {
            match.Advance(3.0);
            TestAssert.True(hero.Waypoints.Count == 0 && hero.AttackMoveGoal is null, "the attack-move arrived and left no goal");
            match.Foe.Position = new Position2(hero.Position.X + 500.0, hero.Position.Z);
        }
        var health = enemy.Health;
        match.Advance(10.0);
        return health - (enemy.Alive ? enemy.Health : 0.0);
    }
}
