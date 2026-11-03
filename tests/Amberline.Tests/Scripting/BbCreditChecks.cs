using Amberline.Core;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine, wave 3 (W31b, E06): <c>BuffOnKill</c> / <c>CharOnKillUnit</c> for the hero credited a kill, <c>BuffOnAssist</c> /
/// <c>CharOnAssistUnit</c> for each assistant of a champion's death, and the native <c>BBIncGold</c>. The probe scripts keep what the hooks read; the
/// events come from the production kill path (<c>CombatService.Kill</c>). The shipped users are Voracity (Irelia, Katarina), Highlander, Silent Killer,
/// Battle Fury, Draw a Bead, Carnivore, Enrage, Archer's Mark, Second Sight and Veigar's, Akali's and Karthus' character scripts. Written, not run.
/// </summary>
public static class BbCreditChecks
{
    private static BbScript KillProbe() => BbW3Rig.Buff("W3KillProbe",
        new BbEventTable("BuffOnKill", [BbW3Rig.Keep("KillOwner", "Owner"), BbW3Rig.Keep("KillAttacker", "Attacker"), BbW3Rig.Keep("KillTarget", "Target"),
            BbW3Rig.Count("Kills")]),
        new BbEventTable("BuffOnAssist", [BbW3Rig.Keep("AssistOwner", "Owner"), BbW3Rig.Keep("AssistTarget", "Target"), BbW3Rig.Count("Assists")]));

    private static BbScript GoldProbe() => BbW3Rig.Buff("W3GoldProbe",
        new BbEventTable("BuffOnKill", [BbW3Rig.Block("BBIncGold", "TargetVar", "Owner", "Delta", 25.0)]),
        new BbEventTable("BuffOnAssist", [BbW3Rig.Block("BBIncGold", "TargetVar", "Owner", "Delta", 7.0)]));

    private static BbScript CharacterProbe() => BbW3Rig.Character("W3CreditChar",
        new BbEventTable("CharOnKillUnit", [BbW3Rig.KeepChar("CharKillOwner", "Owner"), BbW3Rig.KeepChar("CharKillTarget", "Target")]),
        new BbEventTable("CharOnAssistUnit", [BbW3Rig.KeepChar("CharAssistOwner", "Owner"), BbW3Rig.KeepChar("CharAssistTarget", "Target")]));

    [Check("scripting.bb-credit-kill-hooks-run-on-the-killers-buffs-and-character-script-with-the-dead-unit-as-target")]
    public static void KillReachesTheKillersScripts()
    {
        using var rig = new BbW3Rig(CharacterProbe(), KillProbe());
        var hero = rig.Match.Hero;
        var foe = rig.Match.Foe;
        var probe = rig.Give(hero, KillProbe());
        rig.Match.Runtime.Combat.Kill(foe, hero.NetId);
        rig.NoFailures();
        TestAssert.True(ReferenceEquals(probe.InstanceVars["KillOwner"], hero) && ReferenceEquals(probe.InstanceVars["KillAttacker"], hero),
            "Owner and Attacker are the killer");
        TestAssert.True(ReferenceEquals(probe.InstanceVars["KillTarget"], foe), "Target is the unit that died");
        TestAssert.True(ReferenceEquals(rig.CharVars(hero)["CharKillOwner"], hero) && ReferenceEquals(rig.CharVars(hero)["CharKillTarget"], foe),
            "CharOnKillUnit ran with the same units");
        TestAssert.True(probe.InstanceVars["AssistOwner"] is null, "and no assist hook ran for the killer");
    }

    [Check("scripting.bb-credit-kill-hook-runs-for-any-unit-the-hero-kills-not-only-champions")]
    public static void KillHookRunsForMinions()
    {
        using var rig = new BbW3Rig(null, KillProbe());
        var hero = rig.Match.Hero;
        var probe = rig.Give(hero, KillProbe());
        var minion = BbM3.Enemy(rig.Match, "W3Minion", new Position2(hero.Position.X + 150, hero.Position.Z));
        rig.Match.Runtime.Combat.Kill(minion, hero.NetId);
        rig.NoFailures();
        TestAssert.True(ReferenceEquals(probe.InstanceVars["KillTarget"], minion), "a minion kill reaches BuffOnKill (Carnivore, Second Sight and Archer's Mark pay for it)");
        TestAssert.Equal(1.0, (double)probe.InstanceVars["Kills"]!, "once");
    }

    [Check("scripting.bb-credit-assist-hook-runs-for-each-assistant-of-a-champion-death-not-the-killer")]
    public static void AssistReachesTheAssistants()
    {
        using var rig = new BbW3Rig(CharacterProbe(), KillProbe());
        var hero = rig.Match.Hero;
        var ally = rig.Match.Ally;
        var foe = rig.Match.Foe;
        var killerProbe = rig.Give(hero, KillProbe());
        var allyProbe = rig.Give(ally, KillProbe());
        rig.Match.Runtime.Combat.ApplyHit(ally.NetId, foe.NetId, 10.0, DamageType.Magic, DamageSourceKind.Spell);
        rig.Match.Runtime.Combat.Kill(foe, hero.NetId);
        rig.NoFailures();
        TestAssert.Equal(1, ally.ChampionAssists, "the ally was credited an assist");
        TestAssert.True(ReferenceEquals(allyProbe.InstanceVars["AssistOwner"], ally) && ReferenceEquals(allyProbe.InstanceVars["AssistTarget"], foe),
            "BuffOnAssist ran on the assistant with the champion that died as Target");
        TestAssert.True(killerProbe.InstanceVars["AssistOwner"] is null, "the killer got no assist hook");
        TestAssert.True(allyProbe.InstanceVars["KillOwner"] is null, "and the assistant no kill hook");
    }

    [Check("scripting.bb-credit-inc-gold-adds-to-the-heros-gold-and-earned-total-and-comes-after-the-kill-reward")]
    public static void IncGoldPaysAfterTheReward()
    {
        using var rig = new BbW3Rig(null, GoldProbe());
        var hero = rig.Match.Hero;
        var foe = rig.Match.Foe;
        rig.Match.Runtime.Combat.Kill(rig.Match.FarFoe, hero.NetId);
        var before = hero.Gold;
        rig.Match.Runtime.Combat.Kill(foe, hero.NetId);
        var control = hero.Gold - before;
        rig.Give(hero, GoldProbe());
        var enemy = BbM3.Enemy(rig.Match, "W3Victim", new Position2(hero.Position.X + 150, hero.Position.Z));
        before = hero.Gold;
        var earned = hero.MatchStatistics.GoldEarned;
        rig.Match.Runtime.Combat.Kill(enemy, hero.NetId);
        rig.NoFailures();
        TestAssert.Near(enemy.GoldReward + 25.0, hero.Gold - before, 1e-6, "the last hit gold and the 25 the script gave");
        TestAssert.Near(enemy.GoldReward + 25.0, hero.MatchStatistics.GoldEarned - earned, 1e-6, "both count as earned");
        TestAssert.True(control > 0.0, "the control champion kill paid its bounty");
    }

    // Batch 287: BuffOnKill reads the victim's buffs the way the shipped DetonatingShot reads its mark (BBIfHasBuff of Target), so every buff the death
    // clears is still on the victim while the credit runs: a script buff without PersistsThroughDeath and a row of the audited lists
    // (MatchGameplayHooks.RecipientDeathMarks: WormAttack, added as a plain row with no script). The clear follows the credit and takes both.
    [Check("scripting.bb-credit-kill-hook-sees-the-victims-buffs-and-the-death-clears-them-after-the-credit")]
    public static void TheDeathClearsTheVictimsBuffsAfterTheCredit()
    {
        var mark = BbW3Rig.Buff("W3VictimMark");
        var probe = BbW3Rig.Buff("W3MarkProbe", new BbEventTable("BuffOnKill", [
            BbTest.Block("BBIfHasBuff", ["OwnerVar", "Target", "AttackerVar", "Attacker", "BuffName", "W3VictimMark"], BbW3Rig.Count("ScriptMarkSeen")),
            BbTest.Block("BBIfHasBuff", ["OwnerVar", "Target", "AttackerVar", "Attacker", "BuffName", "WormAttack"], BbW3Rig.Count("ListedRowSeen"))]));
        using var rig = new BbW3Rig(null, probe, mark);
        var hero = rig.Match.Hero;
        var foe = rig.Match.Foe;
        var held = rig.Give(hero, probe);
        rig.Give(foe, mark, caster: hero);
        TestAssert.True(rig.Match.Runtime.Buffs.AddBuff(foe, "WormAttack", Amberline.Core.Buffs.BuffType.Internal, 30.0, hero.NetId) is not null,
            "the audited row is on the foe");
        rig.Match.Runtime.Combat.Kill(foe, hero.NetId);
        rig.NoFailures();
        TestAssert.Equal(1.0, (double?)held.InstanceVars["ScriptMarkSeen"] ?? 0.0, "BuffOnKill saw the victim's script mark");
        TestAssert.Equal(1.0, (double?)held.InstanceVars["ListedRowSeen"] ?? 0.0, "and the audited row: no clear runs before the credit");
        TestAssert.False(foe.Buffs.Any(buff => buff.Name is "W3VictimMark" or "WormAttack"), "the death cleared both after the credit: " + BbM3.Names(foe));
    }

    [Check("scripting.bb-credit-inc-gold-refuses-a-negative-delta-and-gives-a-non-hero-nothing")]
    public static void IncGoldEdges()
    {
        var take = BbW3Rig.Buff("W3TakeGold", new BbEventTable("BuffOnKill", [BbW3Rig.Block("BBIncGold", "TargetVar", "Owner", "Delta", -5.0)]));
        using var rig = new BbW3Rig(null, take);
        var hero = rig.Match.Hero;
        rig.Give(hero, take);
        var before = hero.Gold;
        rig.Match.Runtime.Combat.Kill(rig.Match.Foe, hero.NetId);
        TestAssert.True(rig.Module.ScriptFailures.Count == 1 && rig.Module.ScriptFailures[0].Message.Contains("negative", StringComparison.Ordinal),
            "a negative delta is reported as unbound, not guessed");
        TestAssert.True(hero.Gold >= before, "and takes no gold");
    }
}
