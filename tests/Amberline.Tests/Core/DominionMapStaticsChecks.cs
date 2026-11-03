using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Replication;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// The statics a match reads (<see cref="MapDefinitions"/>, <see cref="NeutralDefinitions"/>) follow the match's map: the process default
/// (<see cref="GameData.Default"/>), which a server or headless process configures once for its map. These checks are named
/// <c>server.*</c>, so they run serially in a fresh process and may configure map 8 without leaking into the map-1 checks.
/// </summary>
public static class DominionMapStaticsChecks
{
    [Check("server.dominion-map-8-turrets-are-the-four-shrines")]
    public static void TheCrystalScarTurretRosterIsTheScriptsFourShrines()
    {
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        var turrets = MapDefinitions.Turrets;
        TestAssert.Equal(4, turrets.Count, "CreateLevelProps creates four shrine turrets on map 8, not Summoner's Rift's twenty-four");
        // Positions: the shipped scene objects' CentralPoints (batch 443, .artifacts/b443/scb_points.py, the DataAudit parser).
        // Tiers: CreateChildTurret's towerId 1 is the inhibitor tier, 2 the inner tier (MapDefinitions.TurretTierOf).
        var expected = new (string Name, Team Team, TurretTier Tier, double X, double Z)[]
        {
            ("Turret_OrderTurretShrine", Team.Order, TurretTier.Inhibitor, 811.9843, 3015.2476),
            ("Turret_OrderTurretShrine1", Team.Order, TurretTier.Inner, 200.1049, 5230.8940),
            ("Turret_ChaosTurretShrine", Team.Chaos, TurretTier.Inhibitor, 13045.1826, 2990.1941),
            ("Turret_ChaosTurretShrine1", Team.Chaos, TurretTier.Inner, 13727.4189, 5240.3247),
        };
        foreach (var row in expected)
        {
            var turret = turrets.Single(definition => definition.Name == row.Name);
            TestAssert.Equal(row.Team, turret.Team, row.Name + " team");
            TestAssert.Equal(row.Tier, turret.Tier, row.Name + " tier");
            TestAssert.Near(row.X, turret.Position.X, 1e-3, row.Name + " X");
            TestAssert.Near(row.Z, turret.Position.Z, 1e-3, row.Name + " Z");
        }
        TestAssert.Equal("OdinOrderTurretShrine", MapDefinitions.TurretCharacter(TurretTier.Inhibitor, Team.Order),
            "the order shrine's character is the script's");
        TestAssert.Equal("OdinChaosTurretShrine", MapDefinitions.TurretCharacter(TurretTier.Inner, Team.Chaos),
            "the chaos shrine's character is the script's");
    }

    [Check("server.dominion-map-8-shrines-attack-with-the-shrine-spell-they-ship")]
    public static void TheCrystalScarShrinesAttackWithTheShrineSpellTheyShip()
    {
        // The Odin shrines (OdinOrderTurretShrine, OdinChaosTurretShrine) ship no <character>BasicAttack spell. The shipped shrine spells are
        // OrderTurretShrineBasicAttack and ChaosTurretShrineBasicAttack (batch 443), which their character data is named after.
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        // Batch 527: both shrine spells are CastType 0, so they fly nothing (their stored MissileSpeed 500 landed the hit about 2 s late).
        var order = TurretRules.StatsFor(TurretTier.Inhibitor, Team.Order, GameData.Default);
        TestAssert.Equal(0, BbChampionModule.CastType(GameData.Default.Spell("OrderTurretShrineBasicAttack")), "the order shrine spell is CastType 0");
        TestAssert.Near(0.0, order.MissileSpeed, 1e-9, "the order shrine's hit lands at its cast frame");
        var chaos = TurretRules.StatsFor(TurretTier.Inner, Team.Chaos, GameData.Default);
        TestAssert.Equal(0, BbChampionModule.CastType(GameData.Default.Spell("ChaosTurretShrineBasicAttack")), "the chaos shrine spell is CastType 0");
        TestAssert.Near(0.0, chaos.MissileSpeed, 1e-9, "the chaos shrine's hit lands at its cast frame");
    }

    [Check("server.dominion-map-8-turret-bodies-cover-its-own-tiers")]
    public static void TheCrystalScarTurretBodiesAreReadForItsOwnTiers()
    {
        // VisibilityRules reads each turret's body from its character. The Crystal Scar has no outer or nexus turret, so the table must
        // hold the tiers the map creates, not all five (MapDefinitions.Turrets).
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        var shrine = new Entity(0x50000001u, EntityKind.Turret, UnitType.Turret, Team.Order, Position2.Zero) { Name = "Turret_OrderTurretShrine" };
        var body = VisibilityRules.TurretBody(shrine);
        TestAssert.True(body.Selection > 0 && body.Gameplay > 0, "the order shrine has a selection and a combat body");
    }

    [Check("server.dominion-map-8-keeps-the-summoners-rift-structures-out")]
    public static void TheCrystalScarHasNoSummonersRiftNexusInhibitorOrStamp()
    {
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        // Batch 447: map 8's nexuses are its two Odin crystal props (not the Summoner's Rift HQs, HQ_T1 and HQ_T2), which the score
        // drains. CreateLevelProps names no HQ and no barracks, so the map-1 buildings are not its (barracks are a later phase).
        var nexuses = MapDefinitions.Nexuses;
        TestAssert.Equal(2, nexuses.Count, "map 8 has one Odin crystal nexus per team (batch 447)");
        TestAssert.True(nexuses.All(nexus => nexus.Name.StartsWith("LevelProp_Odin_SOG_", StringComparison.Ordinal)),
            "map 8's nexuses are its shipped crystal props, not HQ_T1 or HQ_T2");
        TestAssert.Equal(0, MapDefinitions.Inhibitors.Count, "map 8 creates no Summoner's Rift barracks");
        TestAssert.Equal(0, MapDefinitions.StructureStamps.Count, "map 8 stamps no Summoner's Rift structure into its nav grid");
    }
}
