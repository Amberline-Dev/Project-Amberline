using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Server;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Stats;
using InventorySlot = Amberline.Simulation.InventorySlot;

namespace Amberline.Tests;

/// <summary>
/// Production-graph acceptance checks for the seven patch-126 core summoners.
/// These drive SpellSystem and the real match-owned combat/buff/vision services;
/// no spell implementation is duplicated in the test project.
/// </summary>
public static class SummonerCoreChecks
{
    [Check("spells-summoners-core-loadout-slots-and-metadata")]
    public static void LoadoutSlotsAndMetadata()
    {
        var runtime = CreateRuntime();
        var caster = Hero(runtime, Team.Order, SpawnPoint(),
            ["SummonerDot", "SummonerFlash"]);
        var target = Hero(runtime, Team.Chaos,
            new Position2(caster.Position.X + 300.0, caster.Position.Z), ["SummonerHeal", "SummonerHaste"]);

        TestAssert.True(Cast(runtime, caster, 0, caster.Position, target.NetId),
            "slot 0 must dispatch the selected Ignite spell");
        TestAssert.Equal(180.0, caster.SummonerCooldowns[0],
            "Ignite cooldown must belong to the selected slot");
        TestAssert.True(target.Buffs.Any(buff => buff.Name == "SummonerDot"),
            "slot 0 Ignite must apply its damage-over-time buff");

        caster.SummonerCooldowns[1] = 0.0;
        var launch = caster.Position;
        TestAssert.True(Cast(runtime, caster, 1,
            new Position2(launch.X + 200.0, launch.Z + 150.0)),
            "slot 1 must dispatch the selected Flash spell");
        TestAssert.True(caster.Position != launch,
            "selected Flash must move the hero when it occupies slot 1");

        var roster = LoadingPackets.SynchVersionS2C(players:
        [new LeagueLoadingPlayer(1, "OrderSeat", "Annie", 100,
            "SummonerDot", "SummonerFlash")]);
        var rosterBytes = roster.Plaintext.ToArray();
        var spell1Hash = SpellPackets.BuffNameHash("SummonerDot");
        var spell2Hash = SpellPackets.BuffNameHash("SummonerFlash");
        var spell1Offset = FindHash(rosterBytes, spell1Hash);
        var spell2Offset = FindHash(rosterBytes, spell2Hash);
        TestAssert.True(spell1Offset >= 0 && spell2Offset > spell1Offset,
            "loading metadata must retain selected slot order");
        TestAssert.True(ContainsHash(rosterBytes, spell1Hash),
            "loading metadata must carry selected spell 1");
        TestAssert.True(ContainsHash(rosterBytes, spell2Hash),
            "loading metadata must carry selected spell 2");

        var oldJson = "{\"Name\":\"Legacy\",\"Champion\":\"Annie\",\"Team\":\"Order\"}";
        var oldPlayer = System.Text.Json.JsonSerializer.Deserialize<MatchPlayer>(oldJson,
            MatchRoster.JsonOptions) ?? throw new InvalidOperationException("legacy player JSON missing");
        TestAssert.Equal(SummonerLoadout.DefaultFirst, oldPlayer.SummonerSpell1,
            "older roster JSON must default spell 1 to Flash");
        TestAssert.Equal(SummonerLoadout.DefaultSecond, oldPlayer.SummonerSpell2,
            "older roster JSON must default spell 2 to Ignite");
        TestAssert.Throws<ArgumentException>(() => MatchRoster.Validate([
            new MatchPlayer("Duplicate", "Annie", Team.Order),
            new MatchPlayer("duplicate", "Orianna", Team.Chaos),
        ]), "roster must reject duplicate player names");

        var avatar = VisionPackets.AvatarInfo(caster.NetId,
            "SummonerDot", "SummonerFlash").Plaintext.Span;
        TestAssert.Equal(SpellPackets.BuffNameHash("SummonerDot"),
            BinaryPrimitives.ReadUInt32LittleEndian(avatar[125..129]),
            "avatar metadata spell 1");
        TestAssert.Equal(SpellPackets.BuffNameHash("SummonerFlash"),
            BinaryPrimitives.ReadUInt32LittleEndian(avatar[129..133]),
            "avatar metadata spell 2");
    }

    [Check("spells-summoners-core-validation-and-finite-input")]
    public static void ValidationAndFiniteInput()
    {
        var nan = EncodeCast(0, new Position2(float.NaN, 100.0), 0);
        var infinity = EncodeCast(0, new Position2(100.0, 100.0), 0,
            endX: float.PositiveInfinity);
        TestAssert.True(SpellSystem.ParseCastRequest(nan) is null,
            "NaN cast coordinates must be rejected before summoner dispatch");
        TestAssert.True(SpellSystem.ParseCastRequest(infinity) is null,
            "infinite cast coordinates must be rejected before summoner dispatch");

        var runtime = CreateRuntime();
        var caster = Hero(runtime, Team.Order, SpawnPoint(),
            ["SummonerClairvoyance", "SummonerExhaust"]);
        var before = caster.SummonerCooldowns.ToArray();
        var outside = new Position2(runtime.Navigation.MaxX + 100.0,
            caster.Position.Z);
        TestAssert.False(Cast(runtime, caster, 0, outside),
            "Clairvoyance must reject a point outside Map1 navigation bounds");
        TestAssert.Equal(before[0], caster.SummonerCooldowns[0],
            "rejected out-of-map Clairvoyance must not start cooldown");

        var farTarget = Hero(runtime, Team.Chaos,
            new Position2(caster.Position.X + 1000.0, caster.Position.Z),
            ["SummonerHeal", "SummonerHaste"]);
        // A unit-targeted summoner out of range walks into range like any unit-targeted cast (Reconstruction) when the caster's
        // team sees the target, and is refused when it does not (the walk would reveal a fogged position): the request is
        // queued, nothing is cast and no cooldown starts until the target is within 550.
        var seen = runtime.Spells.CanSeeTarget(caster, farTarget);
        TestAssert.Equal(seen, Cast(runtime, caster, 1, caster.Position, farTarget.NetId),
            "Exhaust on a target outside 550 range queues an approach only for a visible target");
        TestAssert.Equal(seen, caster.PendingCast is { Request.IsSummoner: true },
            "the out-of-range Exhaust is held as a pending summoner cast only for a visible target");
        TestAssert.Equal(before[1], caster.SummonerCooldowns[1],
            "queued out-of-range Exhaust must not start cooldown");
    }

    [Check("spells-summoners-core-suppression-vs-ordinary-cc")]
    public static void SuppressionVersusOrdinaryCrowdControl()
    {
        var runtime = CreateRuntime();
        var caster = Hero(runtime, Team.Order, SpawnPoint(),
            ["SummonerHeal", "SummonerHaste"]);
        var enemy = Hero(runtime, Team.Chaos,
            new Position2(caster.Position.X + 200.0, caster.Position.Z),
            ["SummonerHeal", "SummonerHaste"]);
        runtime.Buffs.AddBuff(caster, "OrdinaryStun", BuffType.Stun,
            10.0, enemy.NetId, new BuffPayload { ActionDisabled = true },
            canMitigateDuration: false);
        TestAssert.True(Cast(runtime, caster, 0, caster.Position),
            "ordinary stun must not block core summoners");
        TestAssert.Equal(270.0, caster.SummonerCooldowns[0],
            "Heal must enter cooldown through ordinary stun");

        caster.SummonerCooldowns[0] = 0.0;
        runtime.Buffs.AddBuff(caster, "Suppression", BuffType.Suppression,
            10.0, enemy.NetId, canMitigateDuration: false);
        TestAssert.False(Cast(runtime, caster, 0, caster.Position),
            "suppression must block core summoners separately");
        TestAssert.Equal(0.0, caster.SummonerCooldowns[0],
            "suppressed cast must not start cooldown");
    }

    [Check("spells-summoners-core-heal")]
    public static void HealHalfRepeatAndMinionRecipients()
    {
        var runtime = CreateRuntime();
        var caster = Hero(runtime, Team.Order, SpawnPoint(),
            ["SummonerHeal", "SummonerDot"], level: 5);
        var ally = Hero(runtime, Team.Order,
            new Position2(caster.Position.X + 100.0, caster.Position.Z),
            ["SummonerFlash", "SummonerDot"], level: 1);
        var minion = new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, Team.Order,
            new Position2(caster.Position.X + 120.0, caster.Position.Z))
        {
            Alive = true, Created = true, Health = 100.0, MaxHealth = 1000.0,
            Radius = 35.0, Name = "Minion_Basic", ChampionName = "Annie",
        };
        runtime.World.Register(minion);
        caster.Health = 100.0;
        ally.Health = 100.0;

        TestAssert.True(Cast(runtime, caster, 0, caster.Position),
            "Heal must cast while the caster is missing health");
        var baseline = 140.0 + 20.0 * caster.Level;
        TestAssert.Near(100.0 + baseline, caster.Health, 1e-9,
            "caster receives the full Heal baseline");
        TestAssert.Near(100.0 + baseline * 0.5, ally.Health, 1e-9,
            "allied hero receives half the Heal baseline");
        TestAssert.Near(100.0 + baseline * 0.5, minion.Health, 1e-9,
            "allied minion receives half the Heal baseline");
        TestAssert.True(ally.Buffs.Any(buff => buff.Name == "SummonerHealCheck" &&
            buff.Type == BuffType.CombatDehancer && buff.CasterNetId == ally.NetId),
            "first allied Heal must add recipient-owned HealCheck");

        caster.SummonerCooldowns[0] = 0.0;
        TestAssert.True(Cast(runtime, caster, 0, caster.Position),
            "Heal must be castable again after the fixture clears cooldown");
        TestAssert.Near(100.0 + baseline * 0.5 + baseline * 0.25, ally.Health, 1e-9,
            "repeat allied Heal must be quarter baseline");
    }

    [Check("spells-summoners-core-clarity-mana-only")]
    public static void ClarityManaOnly()
    {
        var runtime = CreateRuntime();
        var caster = Hero(runtime, Team.Order, SpawnPoint(),
            ["SummonerMana", "SummonerDot"], level: 5, champion: "Annie");
        var allyMana = Hero(runtime, Team.Order,
            new Position2(caster.Position.X + 100.0, caster.Position.Z),
            ["SummonerFlash", "SummonerDot"], level: 1, champion: "Orianna");
        var allyEnergy = Hero(runtime, Team.Order,
            new Position2(caster.Position.X + 120.0, caster.Position.Z),
            ["SummonerFlash", "SummonerDot"], level: 1, champion: "LeeSin");
        caster.Mana = allyMana.Mana = allyEnergy.Mana = 0.0;
        var energyBefore = allyEnergy.Mana;

        TestAssert.True(Cast(runtime, caster, 0, caster.Position),
            "Clarity must cast for a mana champion");
        var baseline = 160.0 + 30.0 * caster.Level;
        TestAssert.Near(baseline, caster.Mana, 1e-9,
            "caster receives full Clarity baseline");
        TestAssert.Near(baseline * 0.5, allyMana.Mana, 1e-9,
            "allied mana hero receives half Clarity baseline");
        TestAssert.Equal(energyBefore, allyEnergy.Mana,
            "Clarity must not restore allied energy champions");
    }

    [Check("spells-summoners-core-smite-targets-range-and-true-damage")]
    public static void SmiteTargetsRangeAndTrueDamage()
    {
        var runtime = CreateRuntime();
        // Batch 53 (5e53dd3): an enemy summoner target must be visible to the caster's team. The former spawn-point fixture put
        // the 782/783-unit targets into the fountain wall (not walkable, so never seen); open mid-map ground keeps them in sight.
        var caster = Hero(runtime, Team.Order, new Position2(7000.0, 7000.0),
            ["SummonerSmite", "SummonerDot"], level: 5);
        var point = caster.Position;
        var neutral = AddTarget(runtime, EntityKind.Neutral, UnitType.Monster,
            Team.Neutral, new Position2(point.X + 782.0, point.Z));
        neutral.Armor = 500.0;
        TestAssert.True(runtime.Spells.CanSeeTarget(caster, neutral), "the fixture target is in the caster's sight");
        TestAssert.True(Cast(runtime, caster, 0, neutral.Position, neutral.NetId),
            "Smite must accept a neutral monster at 782 units");
        TestAssert.Near(455.0, neutral.Health, 1e-9,
            "level-5 Smite must deal 545 true damage through armor");
        TestAssert.Equal(75.0, caster.SummonerCooldowns[0],
            "Smite must use its 75-second cooldown");

        caster.SummonerCooldowns[0] = 0.0;
        var enemyMinion = AddTarget(runtime, EntityKind.Minion, UnitType.Minion,
            Team.Chaos, new Position2(point.X + 783.0, point.Z));
        TestAssert.True(Cast(runtime, caster, 0, enemyMinion.Position, enemyMinion.NetId),
            "Smite must accept an enemy minion at exactly 783 units");
        TestAssert.Near(455.0, enemyMinion.Health, 1e-9,
            "Smite enemy-minion damage must use the same true-damage contract");

        foreach (var invalid in new[]
        {
            Hero(runtime, Team.Chaos, new Position2(point.X + 100.0, point.Z),
                ["SummonerHeal", "SummonerHaste"]),
            AddTarget(runtime, EntityKind.Minion, UnitType.Minion, Team.Order,
                new Position2(point.X + 100.0, point.Z + 80.0)),
            AddTarget(runtime, EntityKind.Minion, UnitType.Minion, Team.Chaos,
                new Position2(point.X + 100.0, point.Z + 120.0), ward: true),
            AddTarget(runtime, EntityKind.Nexus, UnitType.Structure, Team.Chaos,
                new Position2(point.X + 100.0, point.Z + 160.0)),
        })
        {
            caster.SummonerCooldowns[0] = 0.0;
            TestAssert.False(Cast(runtime, caster, 0, invalid.Position, invalid.NetId),
                $"Smite must reject {invalid.Kind}/{invalid.UnitType} invalid target");
            TestAssert.Equal(0.0, caster.SummonerCooldowns[0],
                "rejected Smite target must not start cooldown");
        }

        // Batch 53 (0b25350, 5e53dd3): a legal neutral beyond 783 units is no longer refused when the caster's team sees it. The
        // request is queued as a walk into range (Reconstruction), so nothing is cast: no cooldown and no damage. A target the
        // team cannot see is still refused.
        var far = AddTarget(runtime, EntityKind.Neutral, UnitType.Monster, Team.Neutral,
            new Position2(point.X + 784.0, point.Z + 200.0));
        TestAssert.True(runtime.Spells.CanSeeTarget(caster, far), "the beyond-range fixture target is in sight");
        caster.SummonerCooldowns[0] = 0.0;
        TestAssert.True(Cast(runtime, caster, 0, far.Position, far.NetId),
            "Smite on a visible neutral beyond 783 units queues an approach");
        TestAssert.True(caster.PendingCast is { Request.IsSummoner: true }, "the out-of-range Smite is held as a pending cast");
        TestAssert.Equal(0.0, caster.SummonerCooldowns[0], "a queued out-of-range Smite starts no cooldown");
        TestAssert.Equal(far.MaxHealth, far.Health, "a queued out-of-range Smite deals no damage");
        var fogged = AddTarget(runtime, EntityKind.Neutral, UnitType.Monster, Team.Neutral,
            new Position2(point.X - 1500.0, point.Z));
        TestAssert.False(runtime.Spells.CanSeeTarget(caster, fogged), "the fogged fixture target is outside the caster's sight");
        TestAssert.False(Cast(runtime, caster, 0, fogged.Position, fogged.NetId),
            "Smite refuses a neutral beyond 783 units that the caster's team cannot see");
        TestAssert.Equal(0.0, caster.SummonerCooldowns[0], "the refused Smite starts no cooldown");
    }

    [Check("spells-summoners-core-exhaust-outgoing-damage")]
    public static void ExhaustOutgoingDamageAndTrueDamageException()
    {
        var runtime = CreateRuntime();
        var caster = Hero(runtime, Team.Order, SpawnPoint(),
            ["SummonerExhaust", "SummonerDot"]);
        var exhausted = Hero(runtime, Team.Chaos,
            new Position2(caster.Position.X + 200.0, caster.Position.Z),
            ["SummonerHeal", "SummonerHaste"]);
        var victim = Hero(runtime, Team.Order,
            new Position2(caster.Position.X + 250.0, caster.Position.Z),
            ["SummonerHeal", "SummonerHaste"]);

        TestAssert.True(Cast(runtime, caster, 0, exhausted.Position,
            exhausted.NetId), "Exhaust must apply to an enemy hero in range");
        TestAssert.True(exhausted.Buffs.Any(buff => buff.Name == "ExhaustDebuff"),
            "Exhaust damage modifier must be attached to the debuffed source");

        exhausted.AttackDamage = 100.0;
        victim.Armor = victim.SpellBlock = 0.0;
        victim.Health = victim.MaxHealth = 1000.0;
        TestAssert.True(runtime.Combat.ApplyHit(exhausted.NetId, victim.NetId,
            100.0, DamageType.Physical, DamageSourceKind.Basic),
            "basic damage from exhausted source must resolve");
        TestAssert.Near(970.0, victim.Health, 1e-9,
            "Exhaust must reduce basic outgoing damage to 30 percent");

        victim.Health = 1000.0;
        TestAssert.True(runtime.Combat.ApplyHit(exhausted.NetId, victim.NetId,
            100.0, DamageType.Magic, DamageSourceKind.Spell),
            "ordinary spell damage from exhausted source must resolve");
        TestAssert.Near(935.0, victim.Health, 1e-9,
            "Exhaust must reduce non-true outgoing damage to 65 percent");

        victim.Health = 1000.0;
        TestAssert.True(runtime.Combat.ApplyHit(exhausted.NetId, victim.NetId,
            100.0, DamageType.True, DamageSourceKind.Spell),
            "true damage from exhausted source must resolve");
        TestAssert.Near(900.0, victim.Health, 1e-9,
            "Exhaust must not reduce true damage");
    }

    [Check("spells-summoners-core-ghost-speed-and-collision")]
    public static void GhostSpeedAndCollision()
    {
        var runtime = CreateRuntime();
        var hero = Hero(runtime, Team.Order, SpawnPoint(),
            ["SummonerHaste", "SummonerDot"]);
        TestAssert.True(Cast(runtime, hero, 0, hero.Position),
            "Ghost must cast");
        var ghost = hero.Buffs.Single(buff => buff.Name == "SummonerHaste");
        TestAssert.True(ghost.Payload.Ghosted, "Ghost must set collision bypass");
        TestAssert.Near(0.27, ghost.Payload.MoveSpeedPercent, 1e-12,
            "Ghost must add 27 percent movement speed");

        var catalog = new ItemCatalog(GameData.Default.Paths);
        var empty = new InventorySlot?[Amberline.Simulation.Inventory.SlotCount];
        var baseStats = HeroStatCalculator.Calculate(GameData.Default, catalog,
            new HeroStatsInput(hero.ChampionName, hero.Level, empty));
        var ghostStats = HeroStatCalculator.Calculate(GameData.Default, catalog,
            new HeroStatsInput(hero.ChampionName, hero.Level, empty, hero.Buffs));
        TestAssert.Near(baseStats.MoveSpeed * 1.27, ghostStats.MoveSpeed, 1e-9,
            "Ghost effective movement speed");

        // This is the reviewed planted-body case used by the production
        // movement solver. Production NPC pairs use a 0.75 movement shell,
        // while this captured geometry was recorded with a 35.7437 movement radius.
        // Scale only the synthetic fixture radii so pair movement restores
        // that captured body geometry; Entity.Radius policy is covered by the
        // dedicated NPC movement checks.
        const double capturedMovementRadius = 35.7437;
        const double capturedNpcPairScale = 0.75;
        const double fixtureRadius = capturedMovementRadius / capturedNpcPairScale;
        var start = new Position2(7209.0, 7407.0);
        var destination = new Position2(6941.0, 7129.0);
        var normal = new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, Team.Order, start)
        {
            Alive = true, Created = true, Speed = 325.0, Radius = fixtureRadius,
            Health = 1000.0, MaxHealth = 1000.0,
            Waypoints = [destination], Name = "Minion_Basic", ChampionName = "Annie",
        };
        runtime.World.Register(normal);
        foreach (var position in new[]
        {
            new Position2(7167.0, 7317.0), new Position2(7125.0, 7425.0),
            new Position2(7153.0, 7333.0),
        })
        {
            var blocker = new Entity(runtime.World.Allocate(UnitType.Minion),
                EntityKind.Minion, UnitType.Minion, Team.Chaos, position)
            {
                Alive = true, Created = true, Speed = 325.0, Radius = fixtureRadius,
                Health = 1000.0, MaxHealth = 1000.0,
                Name = "Minion_Basic", ChampionName = "Annie",
            };
            runtime.World.Register(blocker);
        }
        var before = normal.Position;
        runtime.Movement.Tick();
        TestAssert.Equal(before, normal.Position,
            "ordinary mover must remain planted in the reviewed collision case");

        var ghostMover = Hero(runtime, Team.Order, start,
            ["SummonerHaste", "SummonerDot"]);
        TestAssert.True(Cast(runtime, ghostMover, 0, ghostMover.Position),
            "Ghost collision probe must cast");
        ghostMover.Waypoints = [destination];
        var ghostBefore = ghostMover.Position;
        runtime.Movement.Tick();
        TestAssert.True(ghostMover.Position != ghostBefore,
            $"Ghosted mover must advance through planted bodies (before {ghostBefore}, after {ghostMover.Position}, waypoints {ghostMover.Waypoints.Count}, speed {ghostMover.Speed:R}, alive {ghostMover.Alive})");
    }

    /// <summary>
    /// Batch 222: the Cleanse window is the shipped SummonerBoost script's BuffOnAllowAdd and nothing else. A SummonerBoost buff with no script
    /// behind it shortens no control, so the engine's native copy (removed) is what this check would catch.
    /// </summary>
    [Check("spells-summoners-core-cleanse-window-is-the-script")]
    public static void CleanseWindowIsTheShippedScriptsCopy()
    {
        var runtime = CreateRuntime();
        var caster = Hero(runtime, Team.Order, SpawnPoint(), []);
        var enemy = Hero(runtime, Team.Chaos,
            new Position2(caster.Position.X + 100.0, caster.Position.Z), []);
        runtime.Buffs.AddBuff(caster, "SummonerBoost", BuffType.Internal, 3.0, caster.NetId);
        var slow = runtime.Buffs.AddBuff(caster, "HostileSlow", BuffType.Slow, 10.0, enemy.NetId);
        TestAssert.True(slow is not null, "hostile slow must be accepted next to a scriptless SummonerBoost");
        TestAssert.Near(10.0, slow!.ExpiresAt, 1e-9,
            $"a SummonerBoost with no script must not shorten a control (actual {slow.ExpiresAt:R})");
    }

    // Batch 222: the 35 percent is SummonerBoost's shipped BuffOnAllowAdd (x0.35 on the nine control types, DATA/Spells/SummonerBoost.luaobj),
    // written back once by BbHookRouter.RunAllowAdd. The native window that repeated it is gone. The expected 3.5 is unchanged, so this check
    // holds before and after the change; it does not prove the single source by itself.
    [Check("spells-summoners-core-cleanse-and-duration-window")]
    public static void CleanseAndDurationWindow()
    {
        var runtime = CreateRuntime();
        var caster = Hero(runtime, Team.Order, SpawnPoint(),
            ["SummonerBoost", "SummonerDot"]);
        var enemy = Hero(runtime, Team.Chaos,
            new Position2(caster.Position.X + 100.0, caster.Position.Z),
            ["SummonerHeal", "SummonerHaste"]);
        foreach (var type in new[] { BuffType.Blind, BuffType.Silence, BuffType.Stun,
                     BuffType.Taunt, BuffType.Slow, BuffType.Snare, BuffType.Sleep,
                     BuffType.Fear, BuffType.Net })
            runtime.Buffs.AddBuff(caster, $"CleanseEligible-{type}", type,
                10.0, enemy.NetId, canMitigateDuration: false);
        TestAssert.Equal(9, caster.Buffs.Count(buff => buff.Name.StartsWith("CleanseEligible-")),
            "Cleanse fixture contains every distinct ordinary control type");
        runtime.Buffs.AddBuff(caster, "ScriptProtectedControl", BuffType.Stun,
            10.0, enemy.NetId, new BuffPayload { NonDispellable = true },
            canMitigateDuration: false);
        runtime.Buffs.AddBuff(caster, "DamageOverTime", BuffType.DamageOverTime,
            10.0, enemy.NetId, canMitigateDuration: false);

        TestAssert.True(Cast(runtime, caster, 0, caster.Position),
            "Cleanse must cast with ordinary control effects active");
        TestAssert.False(caster.Buffs.Any(buff => buff.Name.StartsWith("CleanseEligible-")),
            "Cleanse must remove the contracted ordinary control effects");
        TestAssert.True(caster.Buffs.Any(buff => buff.Name == "ScriptProtectedControl"),
            "Cleanse must preserve shipped non-dispellable control");
        TestAssert.True(caster.Buffs.Any(buff => buff.Name == "DamageOverTime"),
            "Cleanse must preserve damage-over-time buffs");
        TestAssert.True(caster.Buffs.Any(buff => buff.Name == "SummonerBoost"),
            "Cleanse must add the three-second duration window");

        var reduced = runtime.Buffs.AddBuff(caster, "HostileSlow", BuffType.Slow,
            10.0, enemy.NetId);
        TestAssert.True(reduced is not null, "hostile slow must be accepted in the window");
        TestAssert.Near(3.5, reduced!.ExpiresAt, 1e-9,
            $"Cleanse must reduce eligible hostile control to 35 percent (actual {reduced.ExpiresAt:R})");

        caster.SummonerCooldowns[0] = 0.0;
        runtime.Buffs.AddBuff(caster, "Suppression", BuffType.Suppression,
            10.0, enemy.NetId, canMitigateDuration: false);
        TestAssert.False(Cast(runtime, caster, 0, caster.Position),
            "Cleanse must not bypass suppression");
    }

    [Check("spells-summoners-core-clairvoyance-visibility-lifecycle")]
    public static void ClairvoyanceVisibilityLifecycle()
    {
        var runtime = CreateRuntime();
        var world = runtime.World;
        var caster = Hero(runtime, Team.Order, SpawnPoint(),
            ["SummonerClairvoyance", "SummonerDot"]);
        var target = Hero(runtime, Team.Chaos, new Position2(7000.0, 7000.0),
            ["SummonerHeal", "SummonerHaste"]);

        var point = FindUnseenPoint(runtime, target);
        target.Position = point;
        var before = RawVisibility(runtime, Team.Order);
        TestAssert.False(before.Contains(target.NetId),
            "target must be hidden by normal fog before Clairvoyance");
        TestAssert.True(Cast(runtime, caster, 0, point),
            "Clairvoyance must accept an in-map point within range");
        TestAssert.Equal(1, world.TimedVisionBubbles.Count,
            "first Clairvoyance must create one independent bubble");
        TestAssert.True(RawVisibility(runtime, Team.Order).Contains(target.NetId),
            "team-owned Clairvoyance must reveal a normal-fog enemy");

        target.StealthHidden = true;
        TestAssert.False(runtime.Dependencies.RevealsStealth(Team.Order, point),
            "Clairvoyance must not reveal stealth");
        TestAssert.False(RawVisibility(runtime, Team.Order).Contains(target.NetId),
            "stealthed enemy must remain hidden inside Clairvoyance");
        target.StealthHidden = false;

        // SummonerClairvoyance.luaobj SelfExecute adds the Owner's buff with BUFF_REPLACE_EXISTING and keeps the bubble id and both
        // eye particle ids in its NextBuffVars, and OnBuffDeactivateBuildingBlocks removes all three. A second cast therefore replaces
        // the first buff and with it the first bubble: one Clairvoyance at a time. The hand-written cast left the earlier bubble
        // standing (its comment marked that non-retail), so the check used to demand two distinct bubbles.
        var otherPoint = new Position2(point.X + 300.0, point.Z);
        caster.SummonerCooldowns[0] = 0.0;
        TestAssert.True(Cast(runtime, caster, 0, otherPoint),
            "a second Clairvoyance must be allowed after its fixture cooldown reset");
        TestAssert.Equal(1, world.TimedVisionBubbles.Count,
            "a second Clairvoyance replaces the first cast's buff and its bubble");
        TestAssert.Equal(1, world.TimedVisionBubbles.Select(bubble => bubble.Position)
            .Distinct().Count(), "the surviving bubble is the second cast's own location");
        TestAssert.True(Distance(world.TimedVisionBubbles[0].Position, otherPoint) < 1e-9,
            "the surviving bubble is the second cast's own location");

        caster.Alive = false;
        TestAssert.True(RawVisibility(runtime, Team.Order).Contains(target.NetId),
            "a dead flag alone does not remove the bubble; only the death hook below does");
        var capture = new CaptureTransport();
        var reconnectObserver = new ReplicationObserver(1, Team.Order, caster.NetId,
            capture);
        runtime.Delivery.AddObserver(reconnectObserver);
        runtime.Delivery.Flush();
        var ids = world.TimedVisionBubbles.Select(bubble => bubble.BubbleId).ToHashSet();
        var adds = capture.Packets.Where(packet => packet.Plaintext.Span[0] == 0x7B &&
            ids.Contains(BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[33..]))).ToArray();
        TestAssert.Equal(1, adds.Length,
            "reconnect receives the live timed bubble immediately, independent of item refresh");
        TestAssert.Equal(1, reconnectObserver.View.TimedTerrainIds.Count);
        // Batch 270: there is no periodic item projection any more to duplicate the timed bubble (the adds above are its only copy).
        TestAssert.False(runtime.Dependencies.AdditionalProviders(Team.Chaos)
            .Any(provider => Distance(provider.Position, point) <= 1400.0),
            "enemy team must not receive the Order Clairvoyance provider");

        capture.Packets.Clear();
        runtime.Spells.Tick(6.0);
        runtime.Delivery.Flush();
        TestAssert.Equal(0, world.TimedVisionBubbles.Count,
            "Clairvoyance bubbles must expire after six seconds");
        TestAssert.Equal(1, capture.Packets.Count(packet => packet.Plaintext.Span[0] == 0x38 &&
            ids.Contains(BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]))),
            "the native timed bubble retires promptly after expiry");
        TestAssert.False(RawVisibility(runtime, Team.Order).Contains(target.NetId),
            "expired Clairvoyance must stop revealing the enemy");

        // SummonerClairvoyance.luaobj keeps the bubble in a buff on the caster that has no PersistsThroughDeath: the death
        // retires it (the buff manager's removal of non-persistent buffs), and its OnBuffDeactivateBuildingBlocks drops the bubble and
        // both eye particles. That is the real death path, not the hand-written summoner death hook.
        caster.Alive = true;
        caster.SummonerCooldowns[0] = 0.0;
        TestAssert.True(Cast(runtime, caster, 0, point), "a fresh Clairvoyance is allowed");
        TestAssert.Equal(1, world.TimedVisionBubbles.Count, "the fresh Clairvoyance creates one bubble");
        TestAssert.True(runtime.Combat.Kill(caster), "the caster's death runs the production kill path");
        TestAssert.Equal(0, world.TimedVisionBubbles.Count,
            "the caster's death ends Clairvoyance (bubble and eye particles)");
    }

    /// <summary>
    /// Batch 551: Plentiful Bounty's Smite gold shows its floating text. SummonerSmite.luaobj's TargetExecute ends with
    /// <c>BBIf AvatarVars.SmiteGoldBonus == 5 → BBIncGold Owner DeltaVar SmiteGoldBonus</c> (Talents/126.luaobj writes the 5), and
    /// <c>BBIncGold</c> sends the kill rewards' 0x25 (UnitAddGold) from the hook's Target, the smitten unit (owner finding 20,
    /// batch 376), so the client draws "+5" over the monster. Batch 107 still listed the text as missing.
    /// </summary>
    [Check("spells-summoners-core-smite-bounty-gold-floats-over-the-target")]
    public static void SmiteBountyGoldFloatsOverTheTarget()
    {
        var runtime = CreateRuntime();
        var caster = Hero(runtime, Team.Order, new Position2(7000.0, 7000.0), ["SummonerSmite", "SummonerDot"], level: 5);
        caster.Masteries = MasteryPage.Create(new MasteryCatalog(),
            new Dictionary<int, int> { [103] = 3, [106] = 3, [136] = 1, [126] = 1, [102] = 4, [107] = 1 });
        runtime.HeroStats.Refresh(caster);
        var monster = AddTarget(runtime, EntityKind.Neutral, UnitType.Monster, Team.Neutral,
            new Position2(caster.Position.X + 500.0, caster.Position.Z));
        var capture = new CaptureTransport();
        runtime.Delivery.AddObserver(new ReplicationObserver(1, Team.Order, caster.NetId, capture));
        runtime.Delivery.Flush();
        capture.Packets.Clear();
        var gold = caster.Gold;

        TestAssert.True(Cast(runtime, caster, 0, monster.Position, monster.NetId), "Smite on the monster");
        runtime.Delivery.Flush();
        TestAssert.True(monster.Alive, "the monster survives the 545");
        TestAssert.Near(gold + 5.0, caster.Gold, 1e-9, "Plentiful Bounty pays its 5");
        var texts = capture.Packets.Where(packet => packet.Plaintext.Length >= 17 && packet.Plaintext.Span[0] == 0x25 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]) == caster.NetId)
            .Select(packet => (From: BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]),
                Gold: BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[13..])))
            .ToArray();
        TestAssert.True(texts.Any(text => text.From == monster.NetId && Math.Abs(text.Gold - 5.0f) < 1e-6),
            "a 0x25 of 5 gold from the monster reaches the caster's client: " + string.Join(",", texts));
    }

    [Check("spells-summoners-core-mastery-avatar-vars")]
    public static void MasteryAvatarVars()
    {
        var catalog = new MasteryCatalog();

        var presenceRuntime = CreateRuntime();
        var presence = Hero(presenceRuntime, Team.Order, SpawnPoint(),
            ["SummonerHeal", "SummonerDot"]);
        presence.Masteries = MasteryPage.Create(catalog, new Dictionary<int, int>
        {
            [124] = 1, [134] = 3, [132] = 3, [130] = 1,
            [116] = 4, [115] = 4, [117] = 1, [121] = 3, [120] = 1,
        });
        presenceRuntime.HeroStats.Refresh(presence);
        TestAssert.True(Cast(presenceRuntime, presence, 0, presence.Position),
            "Presence page Heal cast");
        TestAssert.Near(229.5, presence.SummonerCooldowns[0], 1e-9,
            "Presence applies before spell-specific cooldown adjustments");

        var hasteRuntime = CreateRuntime();
        var haste = Hero(hasteRuntime, Team.Order, SpawnPoint(),
            ["SummonerHaste", "SummonerDot"]);
        haste.Masteries = MasteryPage.Create(catalog,
            new Dictionary<int, int> { [130] = 1 });
        hasteRuntime.HeroStats.Refresh(haste);
        TestAssert.True(Cast(hasteRuntime, haste, 0, haste.Position),
            "Haste page Ghost cast");
        var ghost = haste.Buffs.Single(buff => buff.Name == "SummonerHaste");
        TestAssert.Near(11.5, ghost.ExpiresAt, 1e-9,
            "Haste extends Ghost duration");
        TestAssert.Near(0.33, ghost.Payload.MoveSpeedPercent, 1e-9,
            "Haste adds Ghost movement speed");

        var crippleRuntime = CreateRuntime();
        var cripple = Hero(crippleRuntime, Team.Order, SpawnPoint(),
            ["SummonerExhaust", "SummonerDot"]);
        var victim = Hero(crippleRuntime, Team.Chaos,
            new Position2(cripple.Position.X + 100.0, cripple.Position.Z),
            ["SummonerHeal", "SummonerHaste"]);
        cripple.Masteries = MasteryPage.Create(catalog,
            new Dictionary<int, int> { [136] = 1 });
        crippleRuntime.HeroStats.Refresh(cripple);
        TestAssert.True(Cast(crippleRuntime, cripple, 0, victim.Position, victim.NetId),
            "Cripple page Exhaust cast");
        var exhaust = victim.Buffs.Single(buff => buff.Name == "ExhaustDebuff");
        TestAssert.Near(-10.0, exhaust.Payload.Armor, 1e-9,
            "Cripple applies armor reduction");
        TestAssert.Near(-10.0, exhaust.Payload.SpellBlock, 1e-9,
            "Cripple applies magic-resistance reduction");
        TestAssert.Near(3.5, exhaust.ExpiresAt, 1e-9,
            "Cripple extends Exhaust duration");

        var insightRuntime = CreateRuntime();
        var insight = Hero(insightRuntime, Team.Order, SpawnPoint(),
            ["SummonerMana", "SummonerDot"]);
        var manaAlly = Hero(insightRuntime, Team.Order,
            new Position2(insight.Position.X + 100.0, insight.Position.Z),
            ["SummonerHeal", "SummonerHaste"]);
        manaAlly.Mana = 0.0;
        insight.Masteries = MasteryPage.Create(catalog, new Dictionary<int, int>
        {
            [124] = 1, [134] = 3, [132] = 3, [130] = 1,
            [116] = 4, [115] = 4, [117] = 1, [121] = 3, [147] = 1,
        });
        insightRuntime.HeroStats.Refresh(insight);
        TestAssert.True(Cast(insightRuntime, insight, 0, insight.Position),
            "Insight page Clarity cast");
        TestAssert.Near(190.0, manaAlly.Mana, 1e-9,
            "Insight restores the full Clarity amount to allies");

        var embersRuntime = CreateRuntime();
        var embers = Hero(embersRuntime, Team.Order, SpawnPoint(),
            ["SummonerDot", "SummonerFlash"]);
        var embersTarget = Hero(embersRuntime, Team.Chaos,
            new Position2(embers.Position.X + 100.0, embers.Position.Z),
            ["SummonerHeal", "SummonerHaste"]);
        embers.Masteries = MasteryPage.Create(catalog, new Dictionary<int, int>
        {
            [103] = 3, [136] = 1, [126] = 1, [106] = 3, [146] = 1,
        });
        embersRuntime.HeroStats.Refresh(embers);
        TestAssert.True(Cast(embersRuntime, embers, 0, embersTarget.Position,
            embersTarget.NetId), "Burning Embers Ignite cast");
        embersRuntime.Spells.Tick(0.0);
        var embersBuff = embers.Buffs.Single(buff => buff.Name == "BurningEmbers");
        TestAssert.Near(25000.0, embersBuff.ExpiresAt, 1e-9,
            "Burning Embers starts with the shipped 25000-second duration");
        TestAssert.Near(10.0, embersBuff.Payload.AbilityPower, 1e-9,
            "Burning Embers adds its shipped ability power");
        for (var second = 0; second < 26; second++)
        {
            embersRuntime.Spells.Tick(1.0);
            embersRuntime.Buffs.TickBuffs(embers, 1.0);
        }
        TestAssert.True(embers.Buffs.Any(buff => buff.Name == "BurningEmbers"),
            "Burning Embers stays while Ignite remains on cooldown");
        TestAssert.Near(24974.0, embers.Buffs.Single(buff => buff.Name == "BurningEmbers").ExpiresAt,
            1e-9, "Burning Embers is installed once and never renewed");
        embers.SummonerCooldowns[0] = 0.0;
        embersRuntime.Spells.Tick(0.0);
        TestAssert.False(embers.Buffs.Any(buff => buff.Name == "BurningEmbers"),
            "Burning Embers is removed when Ignite is ready");
    }

    private static MatchRuntime CreateRuntime() =>
        MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default, solo: true);

    private static Position2 SpawnPoint() =>
        new(MapDefinitions.HeroSpawn.X, MapDefinitions.HeroSpawn.Z);

    private static Entity Hero(MatchRuntime runtime, Team team, Position2 position,
        IReadOnlyList<string> spells, int level = 1, string champion = "Annie")
    {
        var hero = new Entity(runtime.World.Allocate(UnitType.Hero), EntityKind.Hero,
            UnitType.Hero, team, position)
        {
            Name = team == Team.Order ? "OrderSeat" : "ChaosSeat",
            ChampionName = champion,
            Alive = true,
            Created = true,
            Visible = true,
            Health = 1000.0,
            MaxHealth = 1000.0,
            Mana = 1000.0,
            MaxMana = 1000.0,
            Level = level,
            Speed = 325.0,
            Radius = 35.0,
            AttackDamage = 100.0,
            AttackPeriod = 1.6,
            AttackWindup = 0.3,
        };
        hero.SummonerSpells = spells.ToList();
        runtime.World.Register(hero);
        return hero;
    }

    private static Entity AddTarget(MatchRuntime runtime, EntityKind kind, UnitType unitType,
        Team team, Position2 position, bool ward = false)
    {
        var target = new Entity(runtime.World.Allocate(unitType), kind, unitType, team, position)
        {
            Name = kind.ToString(),
            ChampionName = "Annie",
            Alive = true,
            Created = true,
            Visible = true,
            IsWard = ward,
            Health = 1000.0,
            MaxHealth = 1000.0,
            Mana = 0.0,
            MaxMana = 0.0,
            Speed = 325.0,
            Radius = 35.0,
        };
        runtime.World.Register(target);
        return target;
    }

    private static bool Cast(MatchRuntime runtime, Entity caster, int slot,
        Position2 point, uint target = 0, float? endX = null)
    {
        return runtime.Spells.HandleCastRequest(caster,
            EncodeCast(slot, point, target, endX), runtime.World.Clock.Now());
    }

    private static byte[] EncodeCast(int slot, Position2 point, uint target,
        float? endX = null)
    {
        var payload = new byte[34];
        payload[0] = 0xA2;
        payload[5] = checked((byte)((slot << 1) | 1));
        WriteSingle(payload, 6, (float)point.X);
        WriteSingle(payload, 10, 0.0f);
        WriteSingle(payload, 14, (float)point.Z);
        WriteSingle(payload, 18, endX ?? (float)point.X);
        WriteSingle(payload, 22, 0.0f);
        WriteSingle(payload, 26, (float)point.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(30, 4), target);
        return payload;
    }

    private static void WriteSingle(byte[] payload, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4),
            BitConverter.SingleToInt32Bits(value));

    private static bool ContainsHash(ReadOnlySpan<byte> bytes, uint hash)
    {
        return FindHash(bytes, hash) >= 0;
    }

    private static int FindHash(ReadOnlySpan<byte> bytes, uint hash)
    {
        Span<byte> encoded = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(encoded, hash);
        return bytes.IndexOf(encoded);
    }

    private static HashSet<uint> RawVisibility(MatchRuntime runtime, Team team)
    {
        var providers = VisibilityRules.BuildTeamProviders(runtime.World, team,
            runtime.Delivery.Balls, runtime.Dependencies.AdditionalProviders);
        return VisibilityRules.ComputeRawVisibility(runtime.World, team,
            runtime.Dependencies.VisionEnvironment, providers,
            point => runtime.Dependencies.RevealsStealth(team, point));
    }

    private static Position2 FindUnseenPoint(MatchRuntime runtime, Entity target)
    {
        var candidates = new[]
        {
            new Position2(7000.0, 7000.0), new Position2(8000.0, 7000.0),
            new Position2(7000.0, 8000.0), new Position2(9000.0, 6000.0),
            new Position2(6000.0, 9000.0), new Position2(10500.0, 6500.0),
        };
        foreach (var point in candidates)
        {
            if (point.X < runtime.Navigation.MinX || point.X > runtime.Navigation.MaxX ||
                point.Z < runtime.Navigation.MinZ || point.Z > runtime.Navigation.MaxZ)
                continue;
            target.Position = point;
            if (!RawVisibility(runtime, Team.Order).Contains(target.NetId))
                return point;
        }
        throw new InvalidOperationException("could not find an in-map normal-fog CV point");
    }

    private static double Distance(Position2 first, Position2 second)
    {
        var dx = first.X - second.X;
        var dz = first.Z - second.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }



    private sealed class CaptureTransport : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = new();
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }
}
