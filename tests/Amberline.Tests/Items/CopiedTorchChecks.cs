using System;
using System.Linq;
using Amberline.Core;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Batch 375 (owner findings 22 and 94; 48 has no shipped add, see the record): a buff whose AutoBuffActivateEffect is
/// another spell's torch is withheld from the wire, so the stock client does not paint Dr. Mundo's or Aegis of the
/// Legion's visual on Garen or Rumble. The rule is <c>BbBuffRuntime.IsCopiedAutoBuffTemplate</c>. The live checks read the
/// 0xBF adds Garen's R and Rumble's W put on the wire; the rule check pins the decision for every shipped script it names.
/// The Dark Binding triple (<c>CarriesForeignBuffTemplate</c>) is a separate rule and keeps its checks above.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    [Check("items-live-copied-torch-garen-r-stays-server-side")]
    public static void GarenRPreCastStaysServerSide()
    {
        // Riot copied Dr. Mundo's BurningAgony template into GarenJusticePreCast (dr_mundo_burning_agony_cas_02.troy). The
        // owner saw none of it on Garen's ultimate in patch 125 (finding 22): the copy is withheld, the buff is kept server-side.
        Learn("GarenJusticePreCast");
        using var match = new Match("Garen", Team.Order);
        var garen = match.Hero(0);
        var enemy = match.Hero(1);
        garen.Position = new Position2(7000, 7000);
        enemy.Position = new Position2(7080, 7000);
        match.Clear();
        TestAssert.True(match.TryCast(0, 3, enemy), "Demacian Justice is accepted");
        var log = Record(match, 3.0);
        TestAssert.False(log.Adds.Any(add => add.Name == "GarenJusticePreCast"),
            $"the pre-cast add stays server-side (adds: {string.Join(", ", log.Adds.Select(add => add.Name))})");
    }

    [Check("items-live-copied-torch-rumble-heat-stays-server-side")]
    public static void RumbleWHeatFamilyStaysServerSide()
    {
        // Rumble's Heating Up family copies Aegis of the Legion's template (Aegis_buf.troy with the 034_Steel_Shield icon,
        // finding 94). Scrap Shield adds those heat buffs; none of them reaches the client, while the Overheat aura with its
        // own icon still does.
        Learn("RumbleHeatDelay", "RumbleHeatingUp", "RumbleHeatingUp2", "RumbleOverheat");
        using var match = new Match("Rumble", Team.Order);
        var rumble = match.Hero(0);
        match.Clear();
        TestAssert.True(match.TryCast(0, 1, rumble), "Scrap Shield is accepted");
        var log = Record(match, 3.0);
        var heat = new[] { "RumbleHeatDelay", "RumbleHeatingUp", "RumbleHeatingUp2" };
        TestAssert.False(log.Adds.Any(add => heat.Contains(add.Name)),
            $"no heat add reaches the client (adds: {string.Join(", ", log.Adds.Select(add => add.Name))})");
        TestAssert.True(log.Adds.Any(add => add.Name == "RumbleOverheat"),
            "the Overheat aura, with its own icon, still reaches the client");
    }

    [Check("items-live-copied-torch-hud-buff-still-reaches-the-client")]
    public static void GarenEHudBuffStillReachesTheClient()
    {
        // Bladestorm's add (GarenBladestorm, a combat enchancer with no torch) is the ordinary HUD buff the rule must leave alone.
        Learn("GarenBladestorm");
        using var match = new Match("Garen", Team.Order);
        var garen = match.Hero(0);
        match.Clear();
        TestAssert.True(match.TryCast(0, 2, garen), "Bladestorm is accepted");
        var log = Record(match, 3.0);
        TestAssert.True(log.Adds.Any(add => add.Name == "GarenBladestorm"),
            $"the Bladestorm HUD buff reaches the client (adds: {string.Join(", ", log.Adds.Select(add => add.Name))})");
    }

    [Check("items-live-leona-zenith-blade-adds-stay-server-side")]
    public static void LeonaZenithBladeSelfAddsStayServerSide()
    {
        // Batch 380 (finding 48): Zenith Blade's two adds on Leona. LeonaZenithBlade is her in-flight lock (a visible 0.5 s add
        // whose template carries PopupMessage1 'game_floatingtext_Snared'); LeonaZenithBladeMissile is the lunge (a 1.25 s add named
        // after the missile, whose template copies Teemo's Blind). Both stay server-side; the root on the enemy still reaches the client.
        Learn("LeonaZenithBlade", "LeonaZenithBladeMissile", "LeonaZenithBladeRoot", "LeonaZenithBladeBuffOrder", "LeonaSunlight");
        using var match = new Match("Leona", Team.Order);
        var leona = match.Hero(0);
        var enemy = match.Hero(1);
        leona.Position = new Position2(7000, 7000);
        enemy.Position = new Position2(7400, 7000);
        match.Clear();
        TestAssert.True(match.TryCast(0, 2, enemy), "Zenith Blade is accepted");
        var log = Record(match, 4.0);
        var onLeona = log.AddNamesOn(leona.NetId).ToArray();
        TestAssert.False(onLeona.Contains("LeonaZenithBlade") || onLeona.Contains("LeonaZenithBladeMissile"),
            $"no Zenith Blade add reaches Leona (adds on her: {string.Join(", ", onLeona)})");
        TestAssert.True(log.AddNamesOn(enemy.NetId).Contains("LeonaZenithBladeRoot"),
            $"the enemy's root reaches the client (adds on it: {string.Join(", ", log.AddNamesOn(enemy.NetId))})");
    }

    [Check("items-copied-torch-rule-pins-the-shipped-decisions")]
    public static void CopiedTorchRulePinsTheShippedDecisions()
    {
        // Withheld: Garen's pre-cast and GarenJustice (Dr. Mundo's and Evelynn's torches), Leona's chaos buff (LeBlanc's torch;
        // no shipped script adds it, so only this rule check can reach it), Rumble's heat family and the Overheat burn Rumble
        // copies from Dr. Mundo (the same icon and torch).
        foreach (var name in new[] { "GarenJusticePreCast", "GarenJustice", "LeonaZenithBladeBuffChaos", "RumbleHeatDelay",
                     "RumbleHeatingUp", "RumbleHeatingUp2", "RumbleOverheatDamage" })
            TestAssert.True(BbBuffRuntime.IsCopiedAutoBuffTemplate(RequireScript(name), ShippedScript), $"{name} carries a copied torch");
        // Kept: the origins of each torch, a buff with its own icon (RumbleGrenadeCounter's ammo count), and buffs with no
        // torch (Bladestorm and its leave buff). A SpellToggleSlot script is never withheld, because the client draws its toggle
        // state from the add; no shipped toggle carries a copied torch today, so that clause is pinned by the rule's code only.
        foreach (var name in new[] { "BurningAgony", "AegisoftheLegionAuraSelf", "LeviathanCap", "Consume", "OlafFrenziedStrikes",
                     "Ravage", "RumbleGrenadeCounter", "GarenBladestorm", "GarenBladestormLeave", "DarkBindingMissile" })
            TestAssert.False(BbBuffRuntime.IsCopiedAutoBuffTemplate(RequireScript(name), ShippedScript), $"{name} keeps its add");
    }

    [Check("items-live-copied-torch-warwick-q-meditate-copy-off-the-wire")]
    public static void WarwickQMeditateHealCopyStaysOffTheWire()
    {
        // Batch 401 (finding 127): Hungering Strike's TargetExecute creates Master Yi's Meditate_eff.troy on Warwick (bind Attacker,
        // SpecificUnitOnlyVar Owner), a copy of another champion's torch; the owner ruled on 27 October that such copies are bugs. The
        // create is withheld (BbBuffRuntime.IsCopiedEffectCreate); the Q's own HungeringStrike_tar.troy still goes out.
        Learn("Meditate_eff", "HungeringStrike_tar");
        using var match = new Match("Warwick", Team.Order);
        var warwick = match.Hero(0);
        var enemy = match.Hero(1);
        warwick.Position = new Position2(7000, 7000);
        enemy.Position = new Position2(7080, 7000);
        match.Clear();
        TestAssert.True(match.TryCast(0, 0, enemy), "Hungering Strike is accepted");
        var log = Record(match, 3.0);
        var creates = string.Join(", ", log.Creates.Select(create => create.Name));
        TestAssert.True(log.Creates.Any(create => create.Name == "HungeringStrike_tar"),
            $"the Q's own target particle reaches the client (creates: {creates})");
        TestAssert.False(log.Creates.Any(create => create.Name == "Meditate_eff"),
            $"the Meditate heal copy stays off the wire (creates: {creates})");
    }

    private static BbScript RequireScript(string name) =>
        ShippedScript(name) ?? throw new InvalidOperationException($"the shipped script {name} loads");
}
