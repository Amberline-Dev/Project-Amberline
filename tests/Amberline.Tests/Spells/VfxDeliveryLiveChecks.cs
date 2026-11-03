using System;
using System.Linq;
using Amberline.Core;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Batch 410 (VFX triage findings 131, 129 and 85): wire checks on the packets of the caster (seat 0, Order) and of an enemy
/// (seat 1, Chaos) in a fresh match. The Wukong W and Warwick E checks pin what reaches both seats: the triage reading that the
/// clone's creates or the Blood Scent add never reached the wire came from the sweep's audience model and toggle state, and the
/// wire shows them on both seats. The Pantheon R check is the one withheld copy (the Annie torch in his Grand Skyfall inibins).
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-wukong-decoy-cas-and-death-creates-reach-both-seats")]
    public static void WukongWDecoyCreatesReachBothSeats()
    {
        // MonkeyKingKillCloneW (cast by the clone when its decoy buff ends) creates the cas effect on activation and the death effect
        // every half second, both bound to the clone; BBIf TeamID picks the team's own version (green for the caster, red for the foe).
        Learn("MonkeyKingDecoy", "MonkeyKingDecoyClone", "MonkeyKingKillCloneW", "monkeyKing_W_cas_team_ID_green",
            "monkeyKing_W_cas_team_ID_red", "MonkeyKing_W_death_team_ID_green", "MonkeyKing_W_death_team_ID_red", "MonkeyKing_Copy");
        using var match = new Match("MonkeyKing", Team.Order);
        var wukong = match.Hero(0);
        match.Clear();
        TestAssert.True(match.TryCast(0, 1, wukong), "Decoy is accepted");
        var (caster, foe) = RecordBoth(match, 3.5);
        var casterCreates = string.Join(", ", caster.Creates.Select(create => create.Name));
        var foeCreates = string.Join(", ", foe.Creates.Select(create => create.Name));
        TestAssert.True(caster.Creates.Any(create => create.Name == "monkeyKing_W_cas_team_ID_green"),
            $"the cas effect reaches the caster (creates: {casterCreates})");
        TestAssert.True(caster.Creates.Any(create => create.Name == "MonkeyKing_W_death_team_ID_green"),
            $"the death effect reaches the caster (creates: {casterCreates})");
        TestAssert.True(foe.Creates.Any(create => create.Name == "monkeyKing_W_cas_team_ID_red"),
            $"the cas effect reaches the foe (creates: {foeCreates})");
        TestAssert.True(foe.Creates.Any(create => create.Name == "MonkeyKing_W_death_team_ID_red"),
            $"the death effect reaches the foe (creates: {foeCreates})");
    }

    [Check("spells-warwick-blood-scent-toggle-add-reaches-both-seats")]
    public static void WarwickEBloodScentToggleAddReachesBothSeats()
    {
        // BloodScent.luaobj's SelfExecute adds BloodScent_internal (25000 s, BUFF_Aura, no IsHiddenOnClient) when the toggle is off.
        Learn("BloodScent", "BloodScent_internal", "BloodScent_target");
        using var match = new Match("Warwick", Team.Order);
        var warwick = match.Hero(0);
        match.Clear();
        TestAssert.True(match.TryCast(0, 2, warwick), "Blood Scent is accepted");
        var (caster, foe) = RecordBoth(match, 1.0);
        TestAssert.True(caster.AddNamesOn(warwick.NetId).Contains("BloodScent_internal"),
            $"the toggle add reaches the caster (adds on him: {string.Join(", ", caster.AddNamesOn(warwick.NetId))})");
        TestAssert.True(foe.AddNamesOn(warwick.NetId).Contains("BloodScent_internal"),
            $"the toggle add reaches the foe (adds on him: {string.Join(", ", foe.AddNamesOn(warwick.NetId))})");
    }

    [Check("spells-pantheon-grand-skyfall-annie-torch-stays-off-the-wire")]
    public static void PantheonGrandSkyfallAnnieTorchStaysOffTheWire()
    {
        // Batch 410 (finding 85): the three Grand Skyfall inibins name InfernalGuardian's infernalguardian_tar.troy as their hit effect,
        // and the stock default hit pass draws it on each unit the spell hits. Pantheon's other hit effects and his own R creates are
        // Pantheon-named, so this one is a copy from Annie's R. The copy is withheld the way Warwick's Meditate copy is (NOT RETAIL, owner
        // ruling 27 October 2026); the R's own creates still reach both seats.
        Learn("infernalguardian_tar", "pantheon_grandskyfall_cas", "pantheon_grandskyfall_tar_green", "pantheon_grandskyfall_up",
            "pantheon_grandskyfall_tar_red", "pantheon_grandskyfall_land");
        using var match = new Match("Pantheon", Team.Order);
        var pantheon = match.Hero(0);
        var enemy = match.Hero(1);
        pantheon.Position = new Position2(7000, 7000);
        enemy.Position = new Position2(7300, 7000);
        match.Clear();
        TestAssert.True(match.TryCast(0, 3, enemy), "Grand Skyfall is accepted");
        var (caster, foe) = RecordBoth(match, 4.0);
        var casterCreates = string.Join(", ", caster.Creates.Select(create => create.Name));
        var foeCreates = string.Join(", ", foe.Creates.Select(create => create.Name));
        TestAssert.True(caster.Creates.Any(create => create.Name == "pantheon_grandskyfall_cas"),
            $"the R's own cast effect reaches the caster (creates: {casterCreates})");
        TestAssert.False(caster.Creates.Any(create => create.Name == "infernalguardian_tar"),
            $"Annie's torch stays off the caster's wire (creates: {casterCreates})");
        TestAssert.False(foe.Creates.Any(create => create.Name == "infernalguardian_tar"),
            $"Annie's torch stays off the foe's wire (creates: {foeCreates})");
    }

    /// <summary>The seat-0 and seat-1 packets of one run, read with the same 0.1 s step as the shared Record helper.</summary>
    private static (WireLog Caster, WireLog Foe) RecordBoth(Match match, double seconds)
    {
        const double step = 0.1;
        var caster = new WireLog();
        var foe = new WireLog();
        for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += step)
        {
            match.Runtime.Tick(step);
            match.Runtime.Delivery.Flush();
            foreach (var packet in match.Captures[0].Packets) caster.Absorb(packet, elapsed);
            foreach (var packet in match.Captures[1].Packets) foe.Absorb(packet, elapsed);
            match.Clear();
        }
        caster.Summarise();
        foe.Summarise();
        return (caster, foe);
    }
}
