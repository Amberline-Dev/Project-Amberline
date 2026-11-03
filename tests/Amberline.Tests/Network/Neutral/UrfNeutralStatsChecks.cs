using Amberline.Data;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network.Neutral;

// Batch 434: Urf (camp 13 of the Autumn rift) ships no UrfBasicAttack. The client names a basic attack <Character>BasicAttack,
// and the neutral stats calculator read that spell unconditionally. Urf's shipped spell in slot 1 is UrfSpell (Data*Spell1).
public static class UrfNeutralStatsChecks
{
    [Check("neutral.urf-no-basic-attack-spell-one-is-urfspell")]
    public static void UrfHasNoShippedBasicAttackAndItsSpellOneIsUrfSpell()
    {
        // CharScriptUrf's CharOnActivate sets SetCanAttack false, so the Urf never swings; UrfSpell is the spell it casts at Warwick.
        var data = GameData.Default;
        var raw = data.Champion("Urf").Raw;
        TestAssert.True(raw.TryGetValue(InibinReader.KeyHash("Data*Spell1"), out var spellOne) &&
            spellOne.Kind == InibinValueKind.String && spellOne.AsString() == "UrfSpell",
            "Urf.inibin's Data*Spell1 is UrfSpell");
        TestAssert.False(data.HasSpell("UrfBasicAttack"), "no UrfBasicAttack is shipped");
        TestAssert.Near(0.0, NeutralStatsCalculator.Calculate(data, "Urf").MissileSpeed, 1e-9,
            "a monster with no shipped basic attack flies no basic-attack missile");
    }
}
