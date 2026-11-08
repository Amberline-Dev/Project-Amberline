using Amberline.Core;
using Amberline.Core.Buffs;

namespace Amberline.Network.Replication;

public static class VisibilityRules
{
    public const double HeroVisionRange = 1350.0;
    public const double MinionVisionRange = 1200.0;
    public const double WardVisionRange = 1100.0;
    /// <summary>
    /// Turret, inhibitor and nexus sight: the CharacterData loader's PerceptionBubbleRadius default (the client's default float constant, 1350),
    /// <see cref="ShopVisionRange"/> also is. Batch 599: no file value to read: no shipped turret character, nor OrderNexus, ChaosNexus,
    /// OrderInhibitor or ChaosInhibitor, has the key. Open: the scenes' ObjectCFG building sections carry PerceptionBubbleRadius 1350, and
    /// Map3's HQs and two Chaos barracks 800; that the client's building bubble reads it is not proven, so the tutorial keeps 1350.
    /// </summary>
    public const double TurretVisionRange = 1350.0;
    /// <summary>
    /// The shop's sight, which lights the fountain. <c>obj_Shop</c> makes its own bubble (its virtual slot 34) without
    /// the IsUnit flag, so the client draws its whole disc, walls and brush included; Map1's ObjectCFG gives the shops no
    /// PerceptionBubbleRadius, so it is the default 1350. The client draws it once the shop is revealed
    /// (SessionSpawnService.BuildShopVisibility). Owner decision, 18 October 2026: fountain sight 1350 for both teams.
    /// </summary>
    public const double ShopVisionRange = 1350.0;
    public const double ShacoBoxVisionRange = 700.0;
    /// <summary>The bubble radius above which the client's fog draws a bubble without a line-of-sight mask (over 3000).</summary>
    public const double NoMaskBubbleRadius = 3000.0;
    /// <summary>
    /// Reconstruction: an enemy fountain turret that stops being targeted or sighted stays revealed this long before
    /// its team-visibility bit is cleared, so a hero skimming the edge of the fountain does not flicker the shrine
    /// on and off (the Python-era server's pre-combat shrine-vision phase). The stock client has no such delay and the
    /// retail server's shrine reveal rules are not in the client. Lane minions and pets no longer linger: the lane
    /// minion fog linger of the Python server was an invention and was deleted by the A4 audit (18 October 2026).
    /// </summary>
    public const double ShrineHideDebounceSeconds = 2.0;

    // This is the two-coordinate form of CPython's vector_norm used by
    // math.dist.  The game has several visibility decisions exactly on a
    // radius boundary, so a rounded x*x + z*z can change the result by one
    // ULP.  Lossless scaling, FMA-backed error-free products, compensated
    // summation, and the final square-root correction retain the source
    // comparison without special-casing entity kinds.  The algorithm is
    // based on Ogita, Rump, and Oishi, "Accurate Sum and Dot Product".
    private static double SourceDistance(Position2 first, Position2 second)
    {
        var x = Math.Abs(first.X - second.X);
        var z = Math.Abs(first.Z - second.Z);
        var max = Math.Max(x, z);
        if (max == 0.0)
            return 0.0;
        if (double.IsInfinity(max))
            return double.PositiveInfinity;
        if (double.IsNaN(max))
            return double.NaN;

        // ILogB is floor(log2(max)); CPython's frexp exponent is one larger.
        var exponent = Math.ILogB(max) + 1;
        var scale = Math.ScaleB(1.0, -exponent);
        x *= scale;
        z *= scale;

        var csum = 1.0;
        var fraction1 = 0.0;
        var fraction2 = 0.0;

        AddSquare(x, ref csum, ref fraction1, ref fraction2);
        AddSquare(z, ref csum, ref fraction1, ref fraction2);

        var h = Math.Sqrt(csum - 1.0 + (fraction1 + fraction2));
        var product = Multiply(-h, h);
        var sum = FastSum(csum, product.High);
        csum = sum.High;
        fraction1 += product.Low;
        fraction2 += sum.Low;
        var correction = csum - 1.0 + (fraction1 + fraction2);
        h += correction / (2.0 * h);
        return Math.ScaleB(h, exponent);
    }

    private static void AddSquare(
        double value,
        ref double csum,
        ref double fraction1,
        ref double fraction2)
    {
        var product = Multiply(value, value);
        var sum = FastSum(csum, product.High);
        csum = sum.High;
        fraction1 += product.Low;
        fraction2 += sum.Low;
    }

    private static (double High, double Low) Multiply(double left, double right)
    {
        var high = left * right;
        var low = Math.FusedMultiplyAdd(left, right, -high);
        return (high, low);
    }

    private static (double High, double Low) FastSum(double left, double right)
    {
        var high = left + right;
        var low = (left - high) + right;
        return (high, low);
    }

    public static bool ProviderReveals(
        IVisionEnvironment environment,
        VisionProvider provider,
        Position2 target)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return Reveals(environment, provider, target, environment.BrushIdAt(target) is not null);
    }

    /// <summary>
    /// Whether the team sees the point: a global vision cell (the Crystal Scar's ring, <see cref="IVisionEnvironment.HasGlobalVision"/>), or
    /// one of its providers. Reconstruction for the server's rule (batch 714): every client lights the global vision cells for its own team,
    /// and the wiki's Dominion page says "The outer track is almost entirely revealed and is not in a fog of war", so a unit or an effect on
    /// one is seen by both teams; the cell of the point decides (the client lights the coarser fog cells over them). Stealth still hides a
    /// unit there (<see cref="RevealsEntity"/>): the cells are sight, not true sight.
    /// </summary>
    /// <param name="targetSelectionRadius">
    /// The target unit's CharacterData SelectionRadius; negative for a point.
    /// </param>
    public static bool ProvidersReveal(
        IVisionEnvironment environment,
        IEnumerable<VisionProvider> providers,
        Position2 target,
        double targetSelectionRadius = -1.0)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(providers);
        if (environment.HasGlobalVision(target)) return true;
        var targetInBrush = environment.BrushIdAt(target, targetSelectionRadius) is not null;
        return providers.Any(provider => Reveals(environment, provider, target, targetInBrush));
    }

    // The client's sight walk on its own flags (IVisionEnvironment.UnitLineOfSightTest), with both units' brush decided by
    // IsInWallOfGrass at their SelectionRadius. Reconstruction: the range is today's, the 2D distance within the provider's radius
    // (the unit line-of-sight test is server-only; the client's overlay draws each bubble at its radius plus 0.9 of a field cell,
    // with a dimmed outer ring, which is rendering).
    private static bool Reveals(
        IVisionEnvironment environment,
        VisionProvider provider,
        Position2 target,
        bool targetInBrush)
    {
        if (SourceDistance(provider.Position, target) > provider.Radius)
            return false;
        // the fog field's perception-bubble update: a bubble without IsUnit, or over 3000, lights its
        // whole disc, walls and brush included (the map write with no mask). Reconstruction for the server's rule: the
        // client draws the unit there lit, so the unit is revealed.
        if (!provider.IsUnit || provider.Radius > NoMaskBubbleRadius)
            return true;
        var viewerInBrush = environment.BrushIdAt(provider.Position, provider.SelectionRadius) is not null;
        return environment.UnitLineOfSightTest(provider.Position, provider.SelectionRadius, viewerInBrush, target, targetInBrush);
    }

    /// <summary>A turret (the fountain shrines included), an inhibitor or a nexus.</summary>
    public static bool IsBuilding(Entity entity) =>
        entity.Kind is EntityKind.Turret or EntityKind.Inhibitor or EntityKind.Nexus ||
        entity.UnitType is UnitType.Turret or UnitType.Structure;

    public static bool IsShrine(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        // Batch 457: a turret the clients hold as a minion (the Odin point turrets, OdinOrderTurretShrine) is no scene shrine on the wire.
        // Batch 665: the shrine is TurretRules.IsShrineTurret's (Summoner's Rift's fountain tier, every Crystal Scar turret), not a name that ends
        // in "Shrine": the Crystal Scar's Turret_OrderTurretShrine1 and Turret_ChaosTurretShrine1 (CreateLevelProps) went out as lane towers.
        return entity.IsSceneTurret && Simulation.Combat.TurretRules.IsShrineTurret(entity);
    }

    /// <summary>
    /// The sight radius modifiers of a unit: what its script buffs add through <c>IncFlatBubbleRadiusMod</c> / <c>IncPercentBubbleRadiusMod</c>
    /// plus the permanent ones (<c>IncPermanent*BubbleRadiusMod</c>, <see cref="Entity.PermanentStatMods"/>). The client's
    /// <c>mFlatBubbleRadiusMod</c> and <c>mPercentBubbleRadiusMod</c> are the same two sums (CharacterIntermediate: the buffs' share is
    /// rebuilt on every stats update, the permanent share is kept).
    /// </summary>
    public static (double Flat, double Percent) BubbleRadiusMods(Entity entity)
    {
        entity.PermanentStatMods.TryGetValue(PermanentFlatBubbleKey, out var flat);
        entity.PermanentStatMods.TryGetValue(PermanentPercentBubbleKey, out var percent);
        foreach (var buff in entity.Buffs)
        {
            if (buff.Payload.Has(Amberline.Core.Buffs.BuffPayloadField.BubbleRadiusFlat)) flat += buff.Payload.BubbleRadiusFlat;
            if (buff.Payload.Has(Amberline.Core.Buffs.BuffPayloadField.BubbleRadiusPercent)) percent += buff.Payload.BubbleRadiusPercent;
        }
        return (flat, percent);
    }

    /// <summary>The <see cref="Entity.PermanentStatMods"/> keys of <c>IncPermanentFlatBubbleRadiusMod</c> and <c>IncPermanentPercentBubbleRadiusMod</c>.</summary>
    public const string PermanentFlatBubbleKey = "FlatBubbleRadiusMod";
    public const string PermanentPercentBubbleKey = "PercentBubbleRadiusMod";

    /// <summary>
    /// The radius of a unit's server sight: its perception radius with its modifiers applied the way the client does
    /// (<c>(percent + 1) * base + flat</c>, <see cref="BubbleRadiusMods"/>), and for a unit spawned with a VisibilitySize the larger of that
    /// and its <see cref="VisibilitySizeRange"/> (both bubbles sit on the unit).
    /// </summary>
    public static double TerrainVisionRange(Entity entity) =>
        Math.Max(Scaled(entity, BaseTerrainVisionRange(entity)), VisibilitySizeRange(entity));

    /// <summary>
    /// The second bubble of a unit spawned with a <c>VisibilitySize</c> (0 without one or once dead), scaled like its own, and the radius of the
    /// attached terrain bubble the server sends for it (<c>ReplicationDelivery.SyncTerrain</c>). Reconstruction: the client's symbol data names the field
    /// (<c>mVisibilitySize</c>, <c>Set/GetVisibilitySize</c>), but no client routine reads it (the spawn packet stores it and nowhere else), so what the retail server did with it is unknown; it is read as sight.
    /// </summary>
    public static double VisibilitySizeRange(Entity entity) =>
        entity.Alive && entity.VisibilitySize > 0.0 ? Scaled(entity, entity.VisibilitySize) : 0.0;

    private static double Scaled(Entity entity, double radius)
    {
        if (radius <= 0.0) return radius;
        var (flat, percent) = BubbleRadiusMods(entity);
        return flat == 0.0 && percent == 0.0 ? radius : Math.Max(0.0, radius * (1.0 + percent) + flat);
    }

    // The client draws each hero's and minion-class unit's own bubble from its CharacterData PerceptionBubbleRadius (the hero and minion creators;
    // the server sends no bubble for it, ReplicationDelivery.SyncTerrain). The values below match it for heroes, lane minions, pets,
    // ShacoBox, (batch 306) every script unit and (batch 310) the Rally beacon. Reconstruction (owner decision, 26 October 2026): a ward sees
    // 1100 (its script's bubble; the client also draws the SightWard character's default 1350).
    private static double BaseTerrainVisionRange(Entity entity)
    {
        // A destroyed inhibitor keeps its sight: its team's client keeps drawing its bubble, because obj_BarracksDampener's
        // BuildingDie handler never sets the dead flag (the building's own handler does) and its dead
        // state (the state switch) clears only its own fields, while the unit stays seen
        // (ReplicationDelivery keeps a unit seen at its death). Reconstruction: the retail server's sight of it is not in the shipped files.
        if (!entity.Alive && entity.Kind != EntityKind.Inhibitor) return 0.0;
        // Batch 310: the Rally beacon (SummonerRally's BBSpawnMinion: Skin SummonerBeacon, IsWard true, VisibilitySize 0.0) is a minion-class
        // unit, and the minion creator makes its CharacterData bubble with no ward test (SummonerBeacon.inibin has no
        // PerceptionBubbleRadius: 1350). No beacon script adds or tunes a bubble (SummonerRally, BeaconAura*), and it is no item ward, so the
        // owner's ward 1100 does not apply. Before: no sight.
        if (entity.IsSummonerBeacon) return CharacterBubble(entity);
        if (entity.IsWard || entity.Kind == EntityKind.Ward) return WardVisionRange;
        if (entity.UnitType == UnitType.Turret ||
            entity.Kind is EntityKind.Turret or EntityKind.Inhibitor or EntityKind.Nexus)
            return TurretVisionRange;
        // Stock CharacterData loads PerceptionBubbleRadius with a 1350 default.
        // ShacoBox overrides it to 700; Tibbers, ghouls and cloned hero skins do not.
        return entity.Kind switch
        {
            EntityKind.Hero when entity.UnitType == UnitType.Hero && entity.OwnerNetId == 0 => HeroVisionRange,
            // MechMelee has no character override, so super minions inherit 1350.
            EntityKind.Minion => entity.MinionType == MinionType.Super ? HeroVisionRange : MinionVisionRange,
            EntityKind.ShacoBox => ShacoBoxVisionRange,
            EntityKind.Pet or EntityKind.YorickGhoul or EntityKind.ShacoClone or
                EntityKind.YorickRevenant or EntityKind.ClonePet => HeroVisionRange,
            // Batch 306 (owner decision, 26 October 2026): a BBSpawnMinion unit (BbNativeHandlers.SpawnGeneric) without a VisibilitySize
            // sees its CharacterData PerceptionBubbleRadius, which TerrainVisionRange scales by the script's modifiers: Teemo's mushroom
            // 405 (-0.7), Heimerdinger's turrets 700, Anivia's wall 1350, TestCubeRender10Vision 0; batch 473: none for a ServerOnly
            // character (TestCube: Singed's trail and the shrunk HiddenMinion cubes, which saw 1350 and 135 before; CharacterBubble).
            // The bubble is made by the minion creator, which is shared client/server
            // code (the shared source; the client-only halves are in a separate tree, and the client-only bubble code),
            // so the retail server made the same bubble. The patch history agrees: V7.14 removed
            // "Giving vision around the cast area" from Anivia's Crystallize and Trundle's Pillar of Ice, whose 1.0.0.126 scripts add
            // no bubble of their own. Before this batch such a unit saw nothing. Batch 310: OnCreate reads only the CharacterData perception radius and
            // the two modifiers, never the spawn's VisibilitySize (the stored size field), so a unit with a size sees this bubble as well; its size is
            // VisibilitySizeRange (batch 473: Talon's blood drop and Lux's beam cubes are ServerOnly TestCubes, so they see only their size).
            EntityKind.Monster => CharacterBubble(entity),
            _ => 0.0,
        };
    }

    // Batch 473 (owner decision, 29 October 2026: "Yes match the client"): the minion creator makes this bubble only when the unit's
    // ServerOnly check (the base unit's check, kept in the minion's table slot) returns false. Its answer is bit 0x20 of the CharacterData flags byte,
    // which the loader
    // fills from [Data] ServerOnly with NeverRender as its default (ChampionData.ServerOnly). So a ServerOnly unit (TestCube, SpellBook1,
    // XerathArcaneBarrageLauncher and the Dominion OdinSpeedShrine / OdinQuestBuff / OdinMinionSpawnPortal) has no CharacterData sight;
    // the code is shared client/server (the shared minion source), so retail's server made none either. A spawn's
    // VisibilitySize bubble (VisibilitySizeRange, which OnCreate never reads) and the bubbles its scripts add are separate and stay.
    // the hero creator has no such test, and the other arms of BaseTerrainVisionRange hold no ServerOnly character.
    private static double CharacterBubble(Entity entity)
    {
        var data = Amberline.Data.GameData.Default.Champion(entity.ChampionName);
        return data.ServerOnly ? 0.0 : data.PerceptionBubbleRadius;
    }

    /// <summary>
    /// Whether any provider sees a building. A turret, the fountain shrines included, is seen through the client's own
    /// turret line-of-sight test (<see cref="IVisionEnvironment.TurretLineOfSightTest"/>) with the provider as the viewer.
    /// Reconstruction: the client test takes a unit, and a provider that is not one (the shop, a timed or attack-reveal
    /// bubble, an item-vision ward) is a viewer at its centre with its own radius and SelectionRadius.
    /// <para>
    /// Reconstruction: inhibitors and nexuses (<c>obj_Building</c>, no line-of-sight test of their own in the client) reveal from their
    /// footprint edge, with a cell diagonal margin, rather than from their object origin only (the Python server's helper, tuned
    /// against building bars that stayed dark; a building's centre sits on opaque navigation cells). The sight radius is the shipped
    /// CharacterData PerceptionBubbleRadius (1350); the footprint edge rule is not in the client. Unrecoverable (batch 310): the
    /// test is a virtual that the base unit class introduces (the turret's table slot 155), and the barracks dampener's
    /// table has only 127 slots, so no building class carries one in any build we hold.
    /// </para>
    /// </summary>
    public static bool BuildingRevealed(
        IVisionEnvironment environment,
        IEnumerable<VisionProvider> providers,
        Entity building)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(building);
        // A building on a global vision cell is seen as any unit there is (ProvidersReveal).
        if (environment.HasGlobalVision(building.Position)) return true;
        if (building.Kind == EntityKind.Turret || building.UnitType == UnitType.Turret)
        {
            var (selection, gameplay) = TurretBody(building);
            return providers.Any(provider => environment.TurretLineOfSightTest(provider.Position, provider.SelectionRadius,
                provider.Radius, building.Position, selection, gameplay));
        }
        var center = building.Position;
        var footprint = building.Kind == EntityKind.Nexus ? 440.0 : Math.Max(0.0, building.Radius);
        var margin = environment.CellSize * Math.Sqrt(2.0);
        foreach (var provider in providers)
        {
            var dx = provider.Position.X - center.X;
            var dz = provider.Position.Z - center.Z;
            var distance = SourceDistance(provider.Position, center);
            if (distance > provider.Radius)
                continue;
            var scale = Math.Min(1.0, (footprint + margin) / Math.Max(distance, 1.0));
            var edge = new Position2(center.X + dx * scale, center.Z + dz * scale);
            if (ProviderReveals(environment, provider, edge))
                return true;
        }
        return false;
    }

    /// <summary>
    /// A turret's CharacterData SelectionRadius and GameplayCollisionRadius, which its LineOfSightTest reads and the
    /// turret entity does not carry: 130 and 88.4 on every Map1 turret character. An unknown name reads as an outer turret.
    /// </summary>
    public static (double Selection, double Gameplay) TurretBody(Entity turret)
    {
        ArgumentNullException.ThrowIfNull(turret);
        // Batch 452: a turret with no lane tier but a character record of its own (the Odin point turrets, OdinOrderTurretShrine) reads that
        // record's bodies; the tier table has no key for it and the outer-tower fallback has no team pair on The Crystal Scar.
        if (!MapDefinitions.TurretTiers.ContainsKey(turret.Name) &&
            Amberline.Data.GameData.Default.OptionalChampion(turret.Name) is { } own)
            return (own.SelectionRadius, own.CombatRadius);
        var tier = MapDefinitions.TurretTiers.TryGetValue(turret.Name, out var value) ? value : TurretTier.Outer;
        return TurretBodies.GetValue(MapDefinitions.Layout, BodiesOf)[(tier, turret.Team == Team.Chaos ? Team.Chaos : Team.Order)];
    }

    // Per map layout (batch 545: the roster follows the match's map, so its bodies do too).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MapLayout,
        IReadOnlyDictionary<(TurretTier Tier, Team Team), (double Selection, double Gameplay)>> TurretBodies = new();

    // The tiers and teams the match's own roster creates (Summoner's Rift's ten pairs; The Crystal Scar's four shrine pairs, batch 443).
    private static IReadOnlyDictionary<(TurretTier Tier, Team Team), (double Selection, double Gameplay)> BodiesOf(MapLayout layout) =>
        layout.Turrets
            .Select(turret => (turret.Tier, turret.Team)).Distinct()
            .ToDictionary(key => key, key =>
            {
                var data = Amberline.Data.GameData.Default.Champion(layout.TurretCharacter(key.Tier, key.Team));
                return (data.SelectionRadius, data.CombatRadius);
            });

    public static IReadOnlyList<VisionProvider> BuildTeamProviders(
        WorldState world,
        Team team,
        IEnumerable<BallProjection> balls,
        Func<Team, IEnumerable<VisionProvider>> additionalProviders)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(balls);
        ArgumentNullException.ThrowIfNull(additionalProviders);
        var providers = new List<VisionProvider>();
        if (team is Team.Order or Team.Chaos)
            providers.Add(new(MapDefinitions.ShopPosition(team), ShopVisionRange, 0.0, isUnit: false));
        AddAttackReveals(world, team, providers);

        foreach (var entity in world.Entities.Values)
        {
            // Item vision supplies wards, including their stealth-detection rules; the Rally beacon is no item ward and sees its own bubble.
            if (entity.Team != team || (entity.IsWard || entity.Kind == EntityKind.Ward) && !entity.IsSummonerBeacon) continue;
            var radius = TerrainVisionRange(entity);
            if (radius <= 0.0) continue;
            // A structure sees from its own position (the shrine too), as the bubble its client object makes, with that bubble's
            // grass-check radius: a turret's SelectionRadius, 0 for an inhibitor or nexus.
            var grassRadius = !IsBuilding(entity) ? entity.SelectionRadius
                : entity.Kind == EntityKind.Turret || entity.UnitType == UnitType.Turret ? TurretBody(entity).Selection : 0.0;
            providers.Add(new(entity.Position, radius, grassRadius));
        }

        // The Orianna ball also has the timed bubble its OrianaGhostMinion script adds (BBAddPosPerceptionBubble, 225); additionalProviders
        // carries it. Since batch 306 the ball unit sees its own OriannaBall bubble (700) too, as above.

        providers.AddRange(additionalProviders(team));
        return providers;
    }

    /// <summary>Whether Paranoia's nearsight holds on the unit (NearSight.luaobj's SetNearSight, the NearSight buff).</summary>
    public static bool IsNearSighted(Entity entity) => entity.Buffs.Any(buff => buff.Type == BuffType.NearSight);

    /// <summary>
    /// The sight list of one hero's client. A nearsighted hero keeps its own bubble and the reveals of enemy attackers, and loses every other
    /// bubble of its team (champions, minions, turrets, wards, the shop, the item vision): the client's near-sight gate
    /// (read in BbNativeHandlers.ApplyNearSight) passes a sight bubble only when the unit itself owns it. Any other hero gets the team's list.
    /// Reconstruction: whether an enemy attacker's reveal passes the gate is not recovered; it is kept, since a reveal is no ally's sight.
    /// The gate is per observer, but the gameplay targeting (ProductionMatchComposition.TeamSight) still reads the team's list.
    /// </summary>
    public static IReadOnlyList<VisionProvider> BuildObserverProviders(
        WorldState world,
        Entity hero,
        IEnumerable<BallProjection> balls,
        Func<Team, IEnumerable<VisionProvider>> additionalProviders)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(hero);
        if (!IsNearSighted(hero)) return BuildTeamProviders(world, hero.Team, balls, additionalProviders);
        var providers = new List<VisionProvider>();
        AddAttackReveals(world, hero.Team, providers);
        var radius = TerrainVisionRange(hero);
        if (radius > 0.0) providers.Add(new(hero.Position, radius, hero.SelectionRadius));
        return providers;
    }

    // The bubble an enemy attacker shows its attacked team for a short time (RevealAttacker): owned by the attacker, not by the team.
    private static void AddAttackReveals(WorldState world, Team team, List<VisionProvider> providers)
    {
        if (team is not (Team.Order or Team.Chaos)) return;
        foreach (var entity in world.Entities.Values)
            if (entity.Alive && entity.AttackRevealRemaining[(int)team] > 0.0)
                providers.Add(new(entity.AttackRevealPosition[(int)team], 400.0, entity.SelectionRadius));
    }

    public static bool IsStealthed(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        // A placed ward is hidden by the Stealth buff its ward script (Spells/SightWard, VisionWard, WriggleLanternWard)
        // adds two seconds after the placement, not by a ward flag: the two-second reveal is that script's LifeTime window
        // and its OnBuffDeactivate/Stealth rows already say when the ward shows. Teleport's target-side Destealth still
        // wins for the length of the channel (it is only ever added to a ward).
        if (entity.IsWard && entity.Buffs.Any(buff => buff.Name == "Destealth")) return false;
        return entity.StealthHidden || entity.AnyBuffOrStatus(buff => buff.Payload.Stealthed);
    }

    /// <summary>The current sight rule for one entity, shared with gameplay targeting.</summary>
    public static bool RevealsEntity(
        WorldState world,
        Team team,
        IVisionEnvironment environment,
        IEnumerable<VisionProvider> providers,
        Func<Position2, bool> revealsStealth,
        Entity entity)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(revealsStealth);
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.UnitType == UnitType.Missile || !entity.Alive) return false;
        if (entity.Team == team || entity.SeenByBothTeams) return true;
        var specific = SpecificReveal(world, team, entity);

        var isBuilding = IsBuilding(entity);
        // Sonic Wave's mark reveals its unit through the script's perception bubbles (BlindMonkQOne: 400 and a 50 unit true sight,
        // BBAddUnitPerceptionBubble, WorldState.TimedVisionBubbles), so the unit is inside a provider of the marker's team.
        var revealed = isBuilding
            ? BuildingRevealed(environment, providers, entity)
            : ProvidersReveal(environment, providers, entity.Position, entity.SelectionRadius);
        if (specific is { } bubble)
            return !IsStealthed(entity) || bubble.RevealStealth || revealed && revealsStealth(entity.Position);
        return revealed && (!IsStealthed(entity) || revealsStealth(entity.Position));
    }

    /// <summary>
    /// The timed bubble of <paramref name="team"/> that makes <paramref name="entity"/> seen by itself, or null: a bubble that names it and nothing
    /// else (<c>RevealSpecificUnitOnly</c>: Skarner's Impale on its victim, Maokai's Unstable Growth on Maokai, a sprung sapling), while the unit is
    /// inside it, and the unit-attached bubble of that very unit (Lee Sin's mark: <c>BBAddUnitPerceptionBubble</c> 400 and 50 on the marked unit,
    /// Talon's bleed, Bushwhack's victim). The bubble starts at the unit, so the unit is inside it in whatever cell it stands in: a marked unit
    /// kicked over a wall stays seen although the line of sight from a blocked cell to itself is closed (the captured leave and re-entry of
    /// 23 September 2026). The centre follows the anchor; the unit's stealth is revealed only if a bubble that qualifies reveals stealth.
    /// Reconstruction of the client's <c>mRevealSpecificNetworkID</c> rule (the post-callback of the unit's sight check: a bubble that names a unit applies to
    /// that unit only) and of "a unit is inside its own bubble".
    /// </summary>
    public static TimedVisionBubble? SpecificReveal(WorldState world, Team team, Entity entity)
    {
        TimedVisionBubble? found = null;
        foreach (var bubble in world.TimedVisionBubbles)
        {
            // Batch 452: a client-only bubble is one client's, so it gives the team no sight.
            if (bubble.Team != team || bubble.Remaining <= 0.0 || bubble.ClientOnlyNetId != 0) continue;
            var own = bubble.RevealUnitNetId == 0 && bubble.AnchorNetId == entity.NetId;
            if (bubble.RevealUnitNetId != entity.NetId && !own) continue;
            if (!own)
            {
                var centre = bubble.AnchorNetId == 0 ? bubble.Position : world.Get(bubble.AnchorNetId) is { Alive: true } anchor ? anchor.Position : (Position2?)null;
                if (centre is not { } point) continue;
                var dx = point.X - entity.Position.X;
                var dz = point.Z - entity.Position.Z;
                if (dx * dx + dz * dz > bubble.Radius * bubble.Radius) continue;
            }
            // Several bubbles may qualify; the one that reveals stealth wins.
            if (bubble.RevealStealth) return bubble;
            found ??= bubble;
        }
        return found;
    }

    /// <summary>
    /// Computes one raw visibility set for one team. It is deliberately free
    /// of observer state; the delivery layer applies known/visible lifetimes.
    /// </summary>
    /// <param name="globalVision">
    /// False for a nearsighted hero's own set: its client does not light the global vision cells (the client takes them out of
    /// the fog while the local hero has a NearSight buff), so the ring reveals nothing to it (Reconstruction: the server's side of it).
    /// </param>
    public static HashSet<uint> ComputeRawVisibility(
        WorldState world,
        Team team,
        IVisionEnvironment environment,
        IReadOnlyList<VisionProvider> providers,
        Func<Position2, bool>? revealsStealth = null,
        ReplicationInstrumentation? instrumentation = null,
        bool globalVision = true)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(providers);
        revealsStealth ??= static _ => false;
        instrumentation?.CountRaw(team);
        // Each provider's brush area is scanned once per pass, not per target.
        var passEnvironment = new PassBrushCache(environment, globalVision);

        var index = world.IndexUnits(world.Entities.Values.Where(entity => entity.UnitType != UnitType.Missile));
        var candidates = new HashSet<Entity>();
        foreach (var provider in providers)
        {
            // Nexus footprints can be large; include the reviewed maximum in
            // the spatial superset, then apply exact edge and LOS predicates.
            foreach (var entity in index.Query(provider.Position.X, provider.Position.Z,
                         provider.Radius + 440.0))
                candidates.Add(entity);
        }

        // A unit a bubble reveals alone, or carries a bubble of its own, may be outside every provider.
        foreach (var bubble in world.TimedVisionBubbles)
            if (bubble.Team == team && bubble.Remaining > 0.0 && bubble.ClientOnlyNetId == 0 &&
                world.Get(bubble.RevealUnitNetId != 0 ? bubble.RevealUnitNetId : bubble.AnchorNetId) is { } revealed &&
                revealed.UnitType != UnitType.Missile)
                candidates.Add(revealed);

        // An allied entity is always available to its own observer even when a
        // malformed position falls outside the spatial index query; so is a unit both teams see (batch 673), and one on a global vision cell.
        foreach (var entity in world.Entities.Values)
        {
            if (entity.UnitType != UnitType.Missile &&
                (entity.Team == team || entity.SeenByBothTeams || passEnvironment.HasGlobalVision(entity.Position)))
                candidates.Add(entity);
        }

        var visible = new HashSet<uint>();
        foreach (var entity in candidates)
            if (RevealsEntity(world, team, passEnvironment, providers, revealsStealth, entity))
                visible.Add(entity.NetId);
        if (world.VisibilityLinks.Count > 0) AddLinkedUnits(world, visible);
        return visible;
    }

    /// <summary>
    /// <c>BBLinkVisibility</c>: the visible set closed over the links, so a team that sees one unit of a chain sees all of it. A unit that was
    /// never announced to clients (a placemarker, a unit not created) is not added. Reconstruction: the native is a bare parse in the client
    /// (<c>Unit1</c> and <c>Unit2</c>, both required); the shipped callers link a beam's cubes to each other and to the caster, which only
    /// makes sense as "the particles on these units are drawn whenever any of them is seen".
    /// </summary>
    private static void AddLinkedUnits(WorldState world, HashSet<uint> visible)
    {
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (first, second) in world.VisibilityLinks)
            {
                var seesFirst = visible.Contains(first);
                if (seesFirst == visible.Contains(second)) continue;
                if (!world.Entities.TryGetValue(seesFirst ? second : first, out var other) || other.Placemarker || !other.Created) continue;
                visible.Add(other.NetId);
                changed = true;
            }
        }
    }

    /// <summary>Memoizes unit brush (IsInWallOfGrass) for one visibility pass; a nearsighted hero's pass has no global vision.</summary>
    private sealed class PassBrushCache(IVisionEnvironment inner, bool globalVision) : IVisionEnvironment
    {
        private readonly Dictionary<(Position2 Point, double Radius), int?> _brush = new();

        public bool HasGlobalVision(Position2 point) => globalVision && inner.HasGlobalVision(point);
        public double CellSize => inner.CellSize;
        public bool HasVisionLos(Position2 source, Position2 target) => inner.HasVisionLos(source, target);
        public bool BrushAllowsVision(Position2 source, Position2 target) => inner.BrushAllowsVision(source, target);
        public int? BrushIdAt(Position2 point) => inner.BrushIdAt(point);

        public int? BrushIdAt(Position2 point, double selectionRadius)
        {
            if (!_brush.TryGetValue((point, selectionRadius), out var brush))
                _brush[(point, selectionRadius)] = brush = inner.BrushIdAt(point, selectionRadius);
            return brush;
        }

        public bool UnitLineOfSightTest(Position2 viewer, double viewerSelectionRadius, bool viewerInBrush, Position2 target,
            bool targetInBrush) => inner.UnitLineOfSightTest(viewer, viewerSelectionRadius, viewerInBrush, target, targetInBrush);

        public bool TurretLineOfSightTest(Position2 viewer, double viewerSelectionRadius, double? viewerPerceptionRadius,
            Position2 turret, double turretSelectionRadius, double turretGameplayRadius) =>
            inner.TurretLineOfSightTest(viewer, viewerSelectionRadius, viewerPerceptionRadius, turret, turretSelectionRadius,
                turretGameplayRadius);

        public bool CampMarkerLineOfSightTest(Position2 viewer, double viewerSelectionRadius, double viewerPathfindingRadius,
            Position2 marker) => inner.CampMarkerLineOfSightTest(viewer, viewerSelectionRadius, viewerPathfindingRadius, marker);
    }

    /// <param name="globalVision">As <see cref="ComputeRawVisibility"/>'s.</param>
    public static HashSet<uint> ComputeVisibleBalls(
        Team team,
        IEnumerable<BallProjection> balls,
        IVisionEnvironment environment,
        IReadOnlyList<VisionProvider> providers,
        bool globalVision = true)
    {
        ArgumentNullException.ThrowIfNull(balls);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(providers);
        var passEnvironment = new PassBrushCache(environment, globalVision);
        var result = new HashSet<uint>();
        foreach (var ball in balls)
        {
            if (!ball.Rendered) continue;
            if (ball.Team == team || ProvidersReveal(passEnvironment, providers, ball.Position))
                result.Add(ball.NetId);
        }
        return result;
    }
}
