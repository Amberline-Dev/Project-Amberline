using Amberline.Core;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine, wave 3 (W31b, E12): <c>BuffOnLaunchAttack</c> and, for a ranged attack, <c>BuffOnLaunchMissile</c> of the attacking hero's script
/// buffs, at the moment the swing's windup ends and the attack leaves. The probe buff keeps what the hooks read; the events come from the production attack
/// path (<c>CombatService.QueueBasicAttack</c>). The shipped users are Camouflage (Teemo), Hide in Shadows (Twitch), Lux's Prismatic Barrier stealth, the Monkey
/// King's decoy stealth (launch attack) and Ashe's Frost Shot, Twitch's Spray and Pray (launch missile). Written, not run.
/// </summary>
public static class BbLaunchHookChecks
{
    private static BbScript LaunchProbe() => BbW3Rig.Buff("W3LaunchProbe",
        new BbEventTable("BuffOnLaunchAttack", [BbW3Rig.Keep("AttackOwner", "Owner"), BbW3Rig.Keep("AttackTarget", "Target"), BbW3Rig.Count("Attacks")]),
        new BbEventTable("BuffOnLaunchMissile", [BbW3Rig.Keep("MissileOwner", "Owner"), BbW3Rig.Keep("MissileTarget", "Target"), BbW3Rig.Keep("MissileId", "missileId"),
            BbW3Rig.Count("Missiles")]));

    private static void Swing(BbW3Rig rig)
    {
        var hero = rig.Match.Hero;
        var foe = rig.Match.Foe;
        hero.AttackCooldown = 0.0;
        TestAssert.True(rig.Match.Runtime.Combat.QueueBasicAttack(hero, foe, new AttackRolls(1.0, 1.0, 1.0)), "the swing was accepted");
    }

    [Check("scripting.bb-launch-attack-hook-runs-when-the-windup-ends-and-the-missile-hook-only-for-an-override-attack-with-the-missile-id")]
    public static void LaunchHooksRunAtTheEndOfTheWindup()
    {
        using var rig = new BbW3Rig(null, LaunchProbe());
        var hero = rig.Match.Hero;
        var foe = rig.Match.Foe;
        var probe = rig.Give(hero, LaunchProbe());
        TestAssert.True(hero.MissileSpeed > 0.0, "Ryze attacks with a missile");
        Swing(rig);
        if (hero.AttackWindup > 0.0)
            TestAssert.True(probe.InstanceVars["Attacks"] is null, "nothing has left at the start of the swing");
        rig.Match.Advance(1.0);
        rig.NoFailures();
        TestAssert.Equal(1.0, (double)probe.InstanceVars["Attacks"]!, "BuffOnLaunchAttack ran once");
        TestAssert.True(ReferenceEquals(probe.InstanceVars["AttackOwner"], hero) && ReferenceEquals(probe.InstanceVars["AttackTarget"], foe), "Owner is the attacker, Target the unit attacked");
        TestAssert.True(probe.InstanceVars["Missiles"] is null, "a plain swing does not reach BuffOnLaunchMissile (Orianna's ball buffs keep their missile's id there)");
        // The swing of a script's attack override (Frost Shot, Spray and Pray) does, with the unit attacked and the missile's id.
        hero.ScriptAttackOverride = new ScriptAttackOverride(0, "RyzeBasicAttack2", 1);
        Swing(rig);
        rig.Match.Advance(1.0);
        rig.NoFailures();
        TestAssert.Equal(2.0, (double)probe.InstanceVars["Attacks"]!, "BuffOnLaunchAttack ran again");
        TestAssert.Equal(1.0, (double)probe.InstanceVars["Missiles"]!, "and BuffOnLaunchMissile, for the override attack's missile");
        TestAssert.True(ReferenceEquals(probe.InstanceVars["MissileTarget"], foe) && (double)probe.InstanceVars["MissileId"]! > 0.0, "with the unit attacked and the missile's id");
    }

    [Check("scripting.bb-launch-attack-hook-runs-for-a-melee-swing-and-the-missile-hook-does-not")]
    public static void MeleeHasNoMissile()
    {
        using var rig = new BbW3Rig(null, LaunchProbe());
        var hero = rig.Match.Hero;
        var probe = rig.Give(hero, LaunchProbe());
        hero.MissileSpeed = 0.0;
        hero.ScriptAttackOverride = new ScriptAttackOverride(0, "RyzeBasicAttack2", 1);
        Swing(rig);
        rig.Match.Advance(1.0);
        rig.NoFailures();
        TestAssert.Equal(1.0, (double)probe.InstanceVars["Attacks"]!, "BuffOnLaunchAttack ran for the melee swing");
        TestAssert.True(probe.InstanceVars["Missiles"] is null, "and no missile hook, the swing has none");
    }

    [Check("scripting.bb-launch-attack-hook-runs-for-each-swing-and-not-for-a-swing-that-never-leaves")]
    public static void EachSwingLaunchesOnce()
    {
        using var rig = new BbW3Rig(null, LaunchProbe());
        var hero = rig.Match.Hero;
        var probe = rig.Give(hero, LaunchProbe());
        for (var swing = 0; swing < 3; swing++)
        {
            Swing(rig);
            rig.Match.Advance(1.5);
        }
        TestAssert.Equal(3.0, (double)probe.InstanceVars["Attacks"]!, "one launch per swing");
        // A swing dropped in its windup (the attacker is stunned) never launches.
        Swing(rig);
        rig.Match.Runtime.Combat.CancelPendingWindups(hero.NetId);
        rig.Match.Advance(1.5);
        TestAssert.Equal(3.0, (double)probe.InstanceVars["Attacks"]!, "a cancelled windup launches nothing");
        rig.NoFailures();
    }
}
