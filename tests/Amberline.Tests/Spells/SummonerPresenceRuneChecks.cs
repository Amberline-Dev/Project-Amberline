using System.Buffers.Binary;
using System.Text;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

/// <summary>
/// Rune 5378, Greater Quintessence of Presence ("UNIQUE: Reduces the cooldown of summoner spells by 4%").
/// The shipped SummonerExhaust and SummonerRevive AdjustCooldown scripts subtract a further 0.04 from the
/// cooldown multiplier while <c>AvatarVars.HasRune5378</c> is true, inside the Presence branch; no other
/// Summoner script reads it.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    private const int PresenceRuneId = 5378;

    /// <summary>
    /// The shipped data has no record for 5378 (Items stops at 5374, the AIR rune catalog too), so a page can
    /// hold it only through a host-supplied catalog, built here the way the launcher builds one.
    /// </summary>
    private static RunePage PresenceRunePage()
    {
        var shipped = new RuneCatalog(GameData.Default.Paths);
        var catalog = new RuneCatalog(shipped.All.Append(new RuneData(PresenceRuneId, RuneColor.Black,
            RuneTier.Three, "Greater Quintessence of Presence",
            "UNIQUE: Reduces the cooldown of summoner spells by 4%", string.Empty, [])));
        return RunePage.Create(catalog, black: [PresenceRuneId]);
    }

    /// <summary>Presence of the Master (120, Utility row 6: 20 earlier Utility points), optionally with Preservation (129).</summary>
    private static MasteryPage PresencePage(bool preservation)
    {
        var allocation = new Dictionary<int, int>
        {
            [124] = 1, [134] = 3, [132] = 3, [130] = 1, [116] = 4, [115] = 4, [117] = 1, [121] = 3, [120] = 1,
        };
        if (preservation) allocation[129] = 1;
        return MasteryPage.Create(new MasteryCatalog(), allocation);
    }

    private static double ShippedAdjustCooldown(string script, double presence, bool rune, double preservation)
    {
        var loaded = BbScriptLoader.Load(GameDataPaths.Default, $"Spells/{script}.luaobj");
        var blocks = loaded.Script!.Event("AdjustCooldown")!.Blocks;
        var run = new BbTest.Run(blocks, setup: scope =>
        {
            scope.AvatarVars!["SummonerCooldownBonus"] = presence;
            scope.AvatarVars["HasRune5378"] = rune;
            scope.AvatarVars["ReviveCooldownBonus"] = preservation;
        });
        return run.Scope.ReturnValue as double? ?? double.NaN;
    }

    /// <summary>One real summoner cast: the stored cooldown and the cooldown the HUD packet carries.</summary>
    private static (double Cooldown, float Hud) CastSummonerWith(string spell, MasteryPage? masteries, bool rune)
    {
        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        hero.SummonerSpells[0] = spell;
        hero.SummonerCooldowns[0] = 0.0;
        if (masteries is not null) hero.Masteries = masteries;
        if (rune) hero.Runes = PresenceRunePage();
        match.Runtime.HeroStats.Refresh(hero);
        if (spell == "SummonerRevive")
        {
            TestAssert.True(match.Runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, 1_000_000,
                DamageType.True), "lethal hit");
            TestAssert.False(hero.Alive, "Revive is dead-only");
        }
        match.Clear();
        match.Cast(0, 0, spell == "SummonerExhaust" ? enemy : hero, summoner: true);
        var hud = match.Captures[0].Packets.Where(packet => packet.Plaintext.Length == 10 &&
                Opcode(packet) == 0x8A && ReadUInt32(packet, 1) == hero.NetId && packet.Plaintext.Span[5] == 0x80)
            .Select(packet => BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[6..])).ToArray();
        TestAssert.Equal(1, hud.Length, $"{spell}: one HUD cooldown packet");
        return (hero.SummonerCooldowns[0], hud[0]);
    }

    [Check("spells-live-summoner-presence-rune-cooldowns")]
    public static void PresenceQuintessenceStacksWithPresenceInExhaustAndRevive()
    {
        var presence = PresencePage(preservation: false);
        var both = PresencePage(preservation: true);
        var preservationOnly = MasteryPage.Create(new MasteryCatalog(), new Dictionary<int, int> { [129] = 1 });

        // Exhaust: BaseCooldown = 210 * ((1 - 0.15) - 0.04). The rune check sits inside the SummonerCooldownBonus
        // CO_NOT_EQUAL 0 branch, which an unset (nil) bonus also takes (batch 303: AvatarVars reads nil, as
        // BuildingBlocksBase's GetTable does), so the rune alone gives 210 * 0.96.
        foreach (var (masteries, rune, expected, label) in new (MasteryPage?, bool, double, string)[]
        {
            (null, false, 210.0, "no page"),
            (null, true, 210.0 * 0.96, "rune alone: nil ~= 0 takes the multiplier branch"),
            (presence, false, 210.0 * 0.85, "Presence alone"),
            (presence, true, 210.0 * (1.0 - 0.15 - 0.04), "Presence and the rune"),
        })
        {
            var (cooldown, hud) = CastSummonerWith("SummonerExhaust", masteries, rune);
            TestAssert.Near(expected, cooldown, 1e-9, $"Exhaust, {label}");
            TestAssert.Near(expected, hud, 1e-3, $"Exhaust HUD cooldown, {label}");
        }

        // Revive: BaseCooldown = 540 * multiplier, then minus Preservation's flat 30; the script's own 540 (its
        // unmodified NewCooldown under nil, batch 303).
        foreach (var (masteries, rune, expected, label) in new (MasteryPage?, bool, double, string)[]
        {
            (null, false, 540.0, "no page"),
            (null, true, 540.0 * 0.96, "rune alone"),
            (presence, false, 459.0, "Presence alone"),
            (presence, true, 540.0 * (1.0 - 0.15 - 0.04), "Presence and the rune"),
            (preservationOnly, true, 540.0 * 0.96 - 30.0, "Preservation and the rune, no Presence"),
            (both, false, 540.0 * 0.85 - 30.0, "Presence and Preservation"),
            (both, true, 540.0 * (1.0 - 0.15 - 0.04) - 30.0, "Presence, Preservation and the rune"),
        })
        {
            var (cooldown, hud) = CastSummonerWith("SummonerRevive", masteries, rune);
            TestAssert.Near(expected, cooldown, 1e-9, $"Revive, {label}");
            TestAssert.Near(expected, hud, 1e-3, $"Revive HUD cooldown, {label}");
        }

        // The other summoner scripts have no rune branch, so the rune changes nothing for them.
        foreach (var (spell, baseCooldown) in new[]
                 { ("SummonerHeal", 270.0), ("SummonerHaste", 210.0), ("SummonerBoost", 150.0), ("SummonerMana", 180.0) })
        {
            var (without, _) = CastSummonerWith(spell, presence, rune: false);
            var (with, hud) = CastSummonerWith(spell, presence, rune: true);
            TestAssert.Near(baseCooldown * 0.85, without, 1e-9, $"{spell} Presence");
            TestAssert.Near(without, with, 0.0, $"{spell} ignores the rune");
            TestAssert.Near(without, hud, 1e-3, $"{spell} HUD cooldown");
        }
    }

    [Check("spells-live-summoner-presence-rune-matches-shipped-script")]
    public static void PresenceQuintessenceCooldownsEqualTheShippedAdjustCooldownScripts()
    {
        var presence = PresencePage(preservation: false);
        var both = PresencePage(preservation: true);
        foreach (var rune in new[] { false, true })
        {
            // Exhaust returns BaseCooldown; the script's own value is the oracle, never current runtime output.
            TestAssert.Near(210.0 * (1.0 - 0.15 - (rune ? 0.04 : 0.0)),
                ShippedAdjustCooldown("SummonerExhaust", 0.15, rune, 0.0), 1e-4, $"Exhaust script, rune {rune}");
            TestAssert.Near(ShippedAdjustCooldown("SummonerExhaust", 0.15, rune, 0.0),
                CastSummonerWith("SummonerExhaust", presence, rune).Cooldown, 1e-4, $"Exhaust cast, rune {rune}");

            // Revive defines NewCooldown only with Preservation, so compare that branch.
            TestAssert.Near(540.0 * (1.0 - 0.15 - (rune ? 0.04 : 0.0)) - 30.0,
                ShippedAdjustCooldown("SummonerRevive", 0.15, rune, 30.0), 1e-4, $"Revive script, rune {rune}");
            TestAssert.Near(ShippedAdjustCooldown("SummonerRevive", 0.15, rune, 30.0),
                CastSummonerWith("SummonerRevive", both, rune).Cooldown, 1e-4, $"Revive cast, rune {rune}");
        }
        // A bonus written as 0 closes the branch and the script returns nothing. No feeder writes 0 (Presence's
        // SetVarsByLevel writes 0.15 and an unallocated talent writes nothing), so a hero without Presence reads nil and
        // takes the branch (spells-summoners-cast-cooldowns-from-scripts, batch 303).
        TestAssert.True(double.IsNaN(ShippedAdjustCooldown("SummonerExhaust", 0.0, true, 0.0)),
            "an explicit 0 bonus sets nothing in the Exhaust script");
    }

    [Check("data.summoner-scripts-rune-5378-readers")]
    public static void OnlyExhaustAndReviveReadTheUnsetPresenceRuneFlag()
    {
        // Nothing in the shipped scripts sets AvatarVars.HasRune5378, and exactly two scripts read it.
        var paths = GameDataPaths.Default;
        var needle = Encoding.ASCII.GetBytes("HasRune");
        var found = new List<string>();
        foreach (var folder in new[] { "Spells", "Talents", "Buffs", "Items" })
            foreach (var file in Directory.EnumerateFiles(paths.Data(folder), "*.luaobj"))
                if (File.ReadAllBytes(file).AsSpan().IndexOf(needle) >= 0)
                    found.Add(Path.GetFileNameWithoutExtension(file));
        TestAssert.Equal(new[] { "SummonerExhaust", "SummonerRevive" }, found.Order().ToArray(),
            "scripts that mention a HasRune variable");
        TestAssert.False(new RuneCatalog(GameData.Default.Paths).Contains(PresenceRuneId),
            "the shipped rune records stop at 5374; the rune has no data record");
    }
}
