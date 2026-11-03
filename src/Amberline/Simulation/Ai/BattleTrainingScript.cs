using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Stats;

namespace Amberline.Simulation.Ai;

/// <summary>
/// Batch 674: Battle Training's server script, for Summoner's Rift in mode TUTORIAL. It runs next to the shipped Summoner's Rift LevelScript,
/// which keeps running (waves, upgrades, camps). Reconstruction throughout: the original script did not ship (no shipped DATA or LEVELS script
/// issues a quest, and only Map3 ships a TutorialScript). Its pieces are the clients':
/// <list type="bullet">
/// <item>the quest texts, keys <c>game_advanced_tutorial_quest_name_*</c> and <c>_tooltip_*</c> (DATA/Menu/fontconfig_en_US.txt 491-528), the
/// gold each tooltip names ("Reward: 50g"), and the tracker's three viewable quests per type (DATA/Globals/Quests.inibin MaxViewable);</item>
/// <item>the quest packet (<see cref="DominionPackets.QuestUpdate"/>) and the tracker's answer (<see cref="DominionPackets.QuestEvent"/>);</item>
/// <item>the order and the triggers from the announcer's Battle Training lines: the
/// tracker explained ("Move your mouse over the quest tracker"), "Complete your secondary quest by purchasing an item in the shop",
/// "Purchase a starting item from the shop and exit the base to start the battle!", minions that "will not spawn until your champion"
/// leaves, then "Clear a path to an enemy turret and destroy it!", "Push to the enemy inhibitor!", "Destroy the exposed enemy inhibitor.",
/// "Destroy the turrets protecting the enemy nexus.", "Victory is near! Destroy the enemy nexus!", and the secondary lines in their order:
/// the shop, minions, the Ancient Golem, the Lizard Elder, the Dragon, a champion, Recall, boots, then the wolves and the wraiths;</item>
/// <item>the weaker turrets and the trainee's bonus (Old tutorials, Battle Training; no dated
/// entry): outer turrets at normal health, "progressively less" deeper in, "down to 525 on the nexus turrets"; "15 bonus attack damage,
/// 30 bonus ability power, 25 bonus armor, and 31 bonus magic resistance".</item>
/// </list>
/// </summary>
public sealed class BattleTrainingScript
{
    public const string QuestKeyPrefix = "game_advanced_tutorial_quest_";

    /// <summary>The wiki's nexus turret health. Reconstruction: the inner and inhibitor turrets step evenly from the outer ones' to it.</summary>
    public const double NexusTurretHealth = 525.0;

    /// <summary>DATA/Globals/Quests.inibin <c>SecondaryQuests*MaxViewable</c>: the server keeps no more open than the tracker shows.</summary>
    public const int SecondaryViewable = 3;

    /// <summary>
    /// Reconstruction: the seconds after which the tracker lesson ends without its roll-over, so a client that never sends the event still
    /// gets its quests.
    /// </summary>
    public const double TrackerLessonSeconds = 60.0;

    /// <summary>The wiki's bonus, as the permanent modifiers a script's <c>IncPermanent*Mod</c> writes (<see cref="Entity.PermanentStatMods"/>).</summary>
    public static readonly IReadOnlyList<(string Modifier, double Amount)> TraineeBonus =
        [("FlatPhysicalDamageMod", 15.0), ("FlatMagicDamageMod", 30.0), ("FlatArmorMod", 25.0), ("FlatSpellBlockMod", 31.0)];

    private sealed record Quest(string Key, byte Type, int Reward, Func<Entity, bool> Done);

    private readonly WorldState _world;
    private readonly ItemCatalog _items;
    private readonly CombatService _combat;
    private readonly SpellSystem _spells;
    private readonly HeroStatsService _stats;
    private readonly WaveScheduler _waves;
    private readonly Action<PlaintextPacket> _send;
    private readonly Action<Entity> _publishTurret;
    private readonly Func<Entity?> _trainee;
    private readonly Queue<Quest> _primary = new();
    private readonly Queue<Quest> _secondary = new();
    private readonly Dictionary<uint, Quest> _open = new();
    private readonly HashSet<string> _monstersSlain = new(StringComparer.Ordinal);
    private uint _nextId = 1;
    private double? _startedAt;
    private uint? _trackerLesson;
    private (uint Id, byte Type)[] _placeholders = [];
    private bool _trackerSeen;
    private bool _battleStarted;
    private bool _wasRecalling;
    private bool _recalled;

    public BattleTrainingScript(WorldState world, ItemCatalog items, CombatService combat, SpellSystem spells, HeroStatsService stats,
        WaveScheduler waves, Action<PlaintextPacket> send, Action<Entity> publishTurret, Func<Entity?> trainee)
    {
        _world = world; _items = items; _combat = combat; _spells = spells; _stats = stats; _waves = waves; _send = send;
        _publishTurret = publishTurret; _trainee = trainee;
        // Primary quests, one at a time: the team's way to victory.
        foreach (var quest in new Quest[]
                 {
                     new("move_to_lane", DominionPackets.PrimaryQuest, 0, OutsideBase),
                     new("front_turret", DominionPackets.PrimaryQuest, 0, _ => EnemyTurrets().Any(turret => !turret.Alive)),
                     new("lane_turrets", DominionPackets.PrimaryQuest, 0, _ => EnemyLaneCleared()),
                     new("inhibitor", DominionPackets.PrimaryQuest, 0, _ => Enemy(EntityKind.Inhibitor).Any(inhibitor => !inhibitor.Alive)),
                     new("nexus_turrets", DominionPackets.PrimaryQuest, 0,
                         _ => EnemyTurrets().Where(turret => TierOf(turret) == TurretTier.Nexus).All(turret => !turret.Alive)),
                     new("nexus", DominionPackets.PrimaryQuest, 0, _ => Enemy(EntityKind.Nexus).Any(nexus => !nexus.Alive)),
                 })
            _primary.Enqueue(quest);
        // Secondary quests, optional, each paying its tooltip's gold.
        foreach (var quest in new Quest[]
                 {
                     new("starting_item", DominionPackets.SecondaryQuest, 50, hero => hero.Inventory.Any(slot => slot is not null)),
                     new("kill_minions", DominionPackets.SecondaryQuest, 100, hero => hero.LaneMinionsKilled >= 5),
                     new("ancient_golem", DominionPackets.SecondaryQuest, 200, _ => _monstersSlain.Contains("AncientGolem")),
                     new("lizard_elder", DominionPackets.SecondaryQuest, 200, _ => _monstersSlain.Contains("LizardElder")),
                     new("dragon", DominionPackets.SecondaryQuest, 300, _ => _monstersSlain.Contains("Dragon")),
                     new("slay_champion", DominionPackets.SecondaryQuest, 200, hero => hero.ChampionKills >= 1),
                     new("recall", DominionPackets.SecondaryQuest, 100, _ => _recalled),
                     new("buy_boots", DominionPackets.SecondaryQuest, 100, HasBoots),
                     new("wolves", DominionPackets.SecondaryQuest, 100, _ => _monstersSlain.Contains("GiantWolf")),
                     new("wraiths", DominionPackets.SecondaryQuest, 125, _ => _monstersSlain.Contains("Wraith")),
                 })
            _secondary.Enqueue(quest);
    }

    /// <summary>One step at game time <paramref name="now"/>.</summary>
    public void Tick(double now)
    {
        if (_trainee() is not { } trainee) return;
        if (_startedAt is null) Start(trainee, now);
        // The battle starts when the trainee leaves the base, whichever quest is open.
        if (!_battleStarted && OutsideBase(trainee))
        {
            _battleStarted = true;
            _waves.ReleaseWaves(now);
        }
        var recalling = _spells.RecallRemaining(trainee) > 0.0;
        if (_wasRecalling && !recalling && Within(trainee.Position, MapDefinitions.TeamSpawn(trainee.Team), 1200.0)) _recalled = true;
        _wasRecalling = recalling;

        if (_trackerLesson is { } lesson && (_trackerSeen || now >= _startedAt!.Value + TrackerLessonSeconds))
        {
            _trackerLesson = null;
            _send(DominionPackets.QuestUpdate(lesson, DominionPackets.PrimaryQuest, DominionPackets.QuestComplete, true));
            foreach (var (id, type) in _placeholders) _send(DominionPackets.QuestUpdate(id, type, DominionPackets.QuestHide, false));
            IssueNext(_primary);
            IssueNext(_secondary);
        }
        foreach (var (id, quest) in _open.ToArray())
        {
            if (!quest.Done(trainee)) continue;
            _open.Remove(id);
            _send(DominionPackets.QuestUpdate(id, quest.Type, DominionPackets.QuestComplete, true));
            if (quest.Reward > 0) _combat.AwardHeroGold(trainee, quest.Reward, null);
            if (quest.Type == DominionPackets.PrimaryQuest) IssueNext(_primary);
        }
        // Before the battle only the starting item is asked for; then the tracker's slots fill in the announcer's order.
        while (_battleStarted && _secondary.Count > 0 && _open.Values.Count(quest => quest.Type == DominionPackets.SecondaryQuest) < SecondaryViewable)
            IssueNext(_secondary);
    }

    /// <summary>
    /// The tracker's answer (<see cref="DominionPackets.QuestEvent"/>): the roll-over of the tracker lesson ends it; a click on a quest pings
    /// its place on the minimap for the trainee's team (the tooltips' "Click to see ... location"; Reconstruction: the hero's own ping, as
    /// the tutorial's script pings).
    /// </summary>
    public void ClientEvent(ReadOnlySpan<byte> payload)
    {
        if (DominionPackets.QuestEvent(payload) is not { } answer) return;
        var (kind, id) = answer;
        if (kind == DominionPackets.QuestRollOver && id == _trackerLesson) _trackerSeen = true;
        if (kind == DominionPackets.QuestMouseDown && _open.TryGetValue(id, out var quest) && _trainee() is { } trainee &&
            PlaceOf(quest.Key, trainee) is { } place)
            _send(PingPackets.MapPingNotification(trainee.NetId, new Position3(place.At.X, 0.0, place.At.Z), place.NetId,
                MapPingCategory.Offense, showChat: false));
    }

    /// <summary>A death with its credited killer (<see cref="CombatService.UnitDied"/>): the camps the trainee slays.</summary>
    public void UnitDied(Entity unit, Entity? killer)
    {
        if (unit.Kind == EntityKind.Neutral && killer is not null && ReferenceEquals(killer, _trainee())) _monstersSlain.Add(unit.Name);
    }

    private void Start(Entity trainee, double now)
    {
        _startedAt = now;
        _waves.WavesHeld = true;
        foreach (var (modifier, amount) in TraineeBonus)
            trainee.PermanentStatMods[modifier] = (trainee.PermanentStatMods.TryGetValue(modifier, out var current) ? current : 0.0) + amount;
        _stats.Refresh(trainee, push: true);
        WeakenEnemyTurrets();
        // The tracker lesson: both placeholders, then "Move Your Mouse Cursor Here", whose roll-over the client reports.
        _placeholders = [(Add("primary_quests", DominionPackets.PrimaryQuest), DominionPackets.PrimaryQuest),
            (Add("secondary_quests", DominionPackets.SecondaryQuest), DominionPackets.SecondaryQuest)];
        _trackerLesson = Add("view_quest_tooltip", DominionPackets.PrimaryQuest, rollovers: true);
    }

    /// <summary>The enemy's outer turrets keep their health; each tier deeper loses an even step, down to <see cref="NexusTurretHealth"/>.</summary>
    private void WeakenEnemyTurrets()
    {
        var turrets = EnemyTurrets().ToArray();
        var outer = turrets.Where(turret => TierOf(turret) == TurretTier.Outer).Select(turret => turret.MaxHealth).DefaultIfEmpty(0.0).Max();
        if (outer <= NexusTurretHealth) return;
        var step = (outer - NexusTurretHealth) / 3.0;
        foreach (var turret in turrets)
        {
            var depth = TierOf(turret) switch { TurretTier.Inner => 1, TurretTier.Inhibitor => 2, TurretTier.Nexus => 3, _ => 0 };
            if (depth == 0) continue;
            turret.MaxHealth = outer - depth * step;
            turret.Health = turret.MaxHealth;
            // The clients hold the created turret's health; its stat block tells them (the block a turret's own stat change sends).
            _publishTurret(turret);
        }
    }

    private uint Add(string key, byte type, bool rollovers = false)
    {
        var id = _nextId++;
        _send(DominionPackets.QuestUpdate(id, type, DominionPackets.QuestAdd, rollovers, QuestKeyPrefix + "name_" + key, QuestKeyPrefix + "tooltip_" + key));
        return id;
    }

    private void IssueNext(Queue<Quest> queue)
    {
        if (queue.TryDequeue(out var quest)) _open[Add(quest.Key, quest.Type)] = quest;
    }

    /// <summary>"Move your champion away from the summoner platform to one of the three lanes outside the base walls": farther from the own
    /// nexus than any own inhibitor. Reconstruction: the base's edge is its inhibitors.</summary>
    private bool OutsideBase(Entity hero)
    {
        if (!hero.Alive || Own(hero.Team, EntityKind.Nexus).FirstOrDefault() is not { } nexus) return false;
        var edge = Own(hero.Team, EntityKind.Inhibitor).Select(inhibitor => Distance(inhibitor.Position, nexus.Position)).DefaultIfEmpty(0.0).Max();
        return edge > 0.0 && Distance(hero.Position, nexus.Position) > edge;
    }

    private bool HasBoots(Entity hero) =>
        hero.Inventory.Any(slot => slot is { } item && (item.ItemId == BootsOfSpeed || _items.Get(item.ItemId)?.RecipeItems.Contains(BootsOfSpeed) == true));

    private const int BootsOfSpeed = 1001;

    /// <summary>All of one enemy lane's turrets: its inhibitor turret and the two in front of it (<see cref="MapDefinitions.TurretFront"/>).</summary>
    private bool EnemyLaneCleared()
    {
        var turrets = EnemyTurrets().ToDictionary(turret => turret.Name, StringComparer.Ordinal);
        foreach (var guard in MapDefinitions.TurretGuardsInhibitor.Keys.Where(turrets.ContainsKey))
        {
            var lane = new List<string> { guard };
            while (MapDefinitions.TurretFront.TryGetValue(lane[^1], out var front)) lane.Add(front);
            if (lane.All(name => turrets.TryGetValue(name, out var turret) && !turret.Alive)) return true;
        }
        return false;
    }

    /// <summary>Where a quest's "Click to see" points: the shop, a structure still standing, or the trainee's side's camp.</summary>
    private (Position2 At, uint NetId)? PlaceOf(string key, Entity trainee)
    {
        var spawn = MapDefinitions.TeamSpawn(trainee.Team);
        if (key == "starting_item") return (spawn, 0u);
        string? monster = key switch
        {
            "ancient_golem" => "AncientGolem", "lizard_elder" => "LizardElder", "dragon" => "Dragon", "wolves" => "GiantWolf", "wraiths" => "Wraith",
            _ => null,
        };
        var place = monster is not null
            ? _world.Entities.Values.Where(unit => unit.Alive && unit.Kind == EntityKind.Neutral && unit.Name == monster)
                .MinBy(unit => Distance(unit.Position, spawn))
            : Structure(key, spawn);
        return place is null ? null : (place.Position, place.NetId);
    }

    private Entity? Structure(string key, Position2 spawn) =>
        key switch
        {
            "front_turret" => EnemyTurrets().Where(turret => turret.Alive && TierOf(turret) == TurretTier.Outer).MinBy(turret => Distance(turret.Position, spawn)),
            "lane_turrets" => EnemyTurrets().Where(turret => turret.Alive && TierOf(turret) is TurretTier.Outer or TurretTier.Inner or TurretTier.Inhibitor)
                .MinBy(turret => Distance(turret.Position, spawn)),
            "inhibitor" => Enemy(EntityKind.Inhibitor).Where(inhibitor => inhibitor.Alive).MinBy(inhibitor => Distance(inhibitor.Position, spawn)),
            "nexus_turrets" => EnemyTurrets().FirstOrDefault(turret => turret.Alive && TierOf(turret) == TurretTier.Nexus),
            "nexus" => Enemy(EntityKind.Nexus).FirstOrDefault(),
            _ => null,
        };

    private Team EnemyTeam => _trainee()?.Team == Team.Chaos ? Team.Order : Team.Chaos;

    private IEnumerable<Entity> EnemyTurrets() =>
        Enemy(EntityKind.Turret).Where(turret => TierOf(turret) is not TurretTier.Fountain);

    private IEnumerable<Entity> Enemy(EntityKind kind) => Own(EnemyTeam, kind);

    private IEnumerable<Entity> Own(Team team, EntityKind kind) =>
        _world.Entities.Values.Where(unit => unit.Kind == kind && unit.Team == team);

    private static TurretTier TierOf(Entity turret) =>
        MapDefinitions.TurretTiers.TryGetValue(turret.Name, out var tier) ? tier : TurretTier.Outer;

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static bool Within(Position2 a, Position2 b, double distance) => Distance(a, b) <= distance;
}
