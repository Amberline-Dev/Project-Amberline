using Amberline.Core;

namespace Amberline.Simulation.Encounters;

/// <summary>A mutator the script added to an encounter (AddMutatorToEncounter): its file, its tags and the mutation index it was given.</summary>
public sealed record EncounterMutator(string Name, string Tags, int MutationIndex);

/// <summary>A live encounter: its definition, the count and tags the script gave it, and the mutators added to it.</summary>
public sealed record LiveEncounter(int Id, EncounterDefinition Definition, int Count, string Tags, IReadOnlyList<EncounterMutator> Mutators);

/// <summary>
/// A squad an encounter spawned: its name (the script's SquadName, else the definition's name) and the units it spawned. Batch 499: a staggered
/// squad (SpawnDelay) grows as its later units spawn (<see cref="EncounterService.SpawnDue"/>).
/// </summary>
public sealed record SpawnedSquad(int Id, string Name, IReadOnlyList<Entity> Units);

/// <summary>Batch 499: the lane a squad was told to push (SquadPushLane: Lane, Reverse, PatrolMode).</summary>
public sealed record SquadLane(int Lane, bool Reverse, bool PatrolMode);

/// <summary>
/// The encounters a Dominion script creates (CreateEncounterFromDefinition), spawns squads from (SpawnSquadFromEncounter), adds
/// mutators to (AddMutatorToEncounter) and deletes (DeleteEncounter), and the squads they spawned (GetSquadNameOfUnit).
/// Units come from <c>spawnUnit</c>, the match's own spawn path. Reconstruction: creating an encounter spawns nothing (the shipped
/// shrine and wave trees create an encounter, then spawn its squads in a separate node); deleting an encounter leaves its squads in
/// the world.
/// </summary>
public sealed class EncounterService
{
    // Reconstruction: a name the shipped trees use that no file holds. SpawnPointGuard (OdinScriptBehaviorTrees.xml:4962) creates
    // "PointGuard" for a capture point's guard, and the only shipped encounter of a capture point is OdinCapturePointNeutral.json
    // (SquadAI "Guard", one OdinNeutralGuardian with BehaviorTreeAI 1). No other name is aliased.
    private static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PointGuard"] = "OdinCapturePointNeutral",
    };

    private readonly Func<string, Position2, Team, Entity?>? _spawnUnit;
    private readonly Func<string, Position2, Team, Entity?>? _spawnMinion;
    private readonly Dictionary<int, LiveEncounter> _encounters = new();
    private readonly Dictionary<int, SpawnedSquad> _squads = new();
    private readonly Dictionary<uint, int> _squadOfUnit = new();
    private readonly Dictionary<int, SquadLane> _lanes = new();
    // Batch 499: the later units of staggered squads (SpawnDelay), each with its stats as the squad's mutators stood at the squad's spawn.
    private readonly List<(int Squad, EncounterMonster Monster, string Skin, Position2 At, Team Team, bool Minion,
        IReadOnlyList<(string Stat, double Amount)> Mutations, double Due)> _pending = new();
    private int _nextEncounter = 1;
    private int _nextSquad = 1;

    /// <param name="catalog">The map's shipped encounter and mutator definitions.</param>
    /// <param name="spawnUnit">Spawns one unit of a character record at a position on a team; null when the map has no spawn path.</param>
    /// <param name="spawnMinion">Batch 451: the same seam for a minion (the units of a SquadAI Wave definition); null spawns them with <paramref name="spawnUnit"/>.</param>
    public EncounterService(EncounterCatalog catalog, Func<string, Position2, Team, Entity?>? spawnUnit = null,
        Func<string, Position2, Team, Entity?>? spawnMinion = null)
    {
        Catalog = catalog;
        _spawnUnit = spawnUnit;
        _spawnMinion = spawnMinion;
    }

    public EncounterCatalog Catalog { get; }

    /// <summary>The encounter with that id, or null once it is deleted or was never created.</summary>
    public LiveEncounter? Encounter(int id) => _encounters.GetValueOrDefault(id);

    /// <summary>The squad with that id, or null when it never spawned.</summary>
    public SpawnedSquad? Squad(int id) => _squads.GetValueOrDefault(id);

    /// <summary>
    /// CreateEncounterFromDefinition: an encounter of the named shipped definition, its id; null when no file holds that name (after
    /// the <see cref="Aliases"/> rule).
    /// </summary>
    public int? CreateEncounter(string definitionName, int count, string tags)
    {
        var name = Catalog.Encounters.ContainsKey(definitionName) || !Aliases.TryGetValue(definitionName, out var alias)
            ? definitionName
            : alias;
        if (!Catalog.Encounters.TryGetValue(name, out var definition)) return null;
        var id = _nextEncounter++;
        _encounters[id] = new LiveEncounter(id, definition, count, tags, Array.Empty<EncounterMutator>());
        return id;
    }

    /// <summary>DeleteEncounter: the encounter is removed. Its squads stay in the world (Reconstruction).</summary>
    public bool DeleteEncounter(int encounterId) => _encounters.Remove(encounterId);

    /// <summary>Batch 448: whether a shipped mutator file is named <paramref name="mutatorName"/> (RangedWaveBase is not).</summary>
    public bool HasMutator(string mutatorName) => Catalog.Mutators.ContainsKey(mutatorName);

    /// <summary>
    /// AddMutatorToEncounter: records a mutator of a shipped mutator file on the encounter, with its tags and index. Batch 499: the squads
    /// spawned afterwards take its stats (<see cref="SpawnSquad"/>, <see cref="MutatorDefinition.At"/>).
    /// </summary>
    public bool AddMutator(int encounterId, string mutatorName, string tags, int mutationIndex)
    {
        if (!_encounters.TryGetValue(encounterId, out var encounter) || !Catalog.Mutators.ContainsKey(mutatorName)) return false;
        var mutators = encounter.Mutators.Append(new EncounterMutator(mutatorName, tags, mutationIndex)).ToList();
        _encounters[encounterId] = encounter with { Mutators = mutators };
        return true;
    }

    /// <summary>
    /// Batch 499: UpdateMutator (SpawnMinionWave's "catchup" selector, OdinScriptBehaviorTrees.xml:3851-3878): the encounter's mutator of that file
    /// takes the new mutation index, for the squads spawned from now on. False when the encounter carries no such mutator.
    /// </summary>
    public bool UpdateMutator(int encounterId, string mutatorName, int mutationIndex)
    {
        if (!_encounters.TryGetValue(encounterId, out var encounter) || encounter.Mutators.All(mutator => mutator.Name != mutatorName)) return false;
        _encounters[encounterId] = encounter with
        {
            Mutators = encounter.Mutators.Select(mutator => mutator.Name == mutatorName ? mutator with { MutationIndex = mutationIndex } : mutator).ToList(),
        };
        return true;
    }

    /// <summary>Batch 499: SquadPushLane records the lane the squad pushes; the map's host gives the units their route (BehaviorTreeQuestHost).</summary>
    public bool PushLane(int squadId, SquadLane lane)
    {
        if (!_squads.ContainsKey(squadId)) return false;
        _lanes[squadId] = lane;
        return true;
    }

    /// <summary>Batch 499: the lane a unit's squad pushes, or null when its squad was given none (or no squad spawned it).</summary>
    public SquadLane? LaneOf(Entity unit) =>
        _squadOfUnit.TryGetValue(unit.NetId, out var id) ? _lanes.GetValueOrDefault(id) : null;

    /// <summary>
    /// SpawnSquadFromEncounter: one squad of the encounter at <paramref name="position"/> on <paramref name="team"/>. Every monster of
    /// every group spawns <c>Count</c> times; a monster with no skin (the wave placeholders) spawns nothing. A monster with
    /// Targetable 0 is not targetable by any team (Reconstruction). Returns the squad id, or null when no unit spawned.
    /// </summary>
    /// <summary>
    /// Reconstruction (batch 476e; the owner's retail minimap screenshot: health packs show as a green "+", speed boosts have their own icon, the
    /// two centre shards show as small coloured icons). An encounter that names no icon takes its character's minimap.ini entry: OdinShieldRelic
    /// (health packs) draws [HealthPack] (healthPack.dds); OdinSpeedShrine (speed boosts) draws [Shrine] (shrineIcon.dds), the shrine icon that
    /// is neither a fountain texture nor a crystal's. The icon reaches the client only in the neutral creation packet (0x66); see BuildCreation.
    /// </summary>
    internal static string CharacterMinimapIcon(string skin) => skin switch
    {
        "OdinShieldRelic" => "HealthPack",
        "OdinSpeedShrine" => "Shrine",
        _ => string.Empty,
    };

    /// <summary>
    /// Batch 511: the client draws a minimap icon two ways (the tactical map). A camp (the 0x66 MinimapIcon of its first member, kept
    /// per GroupNumber) draws the section's Neutral texture whatever its members' render state. A unit's own icon (0xE1) draws
    /// its team's texture, only while the unit is seen and its NoRender is clear. The health relic (NoRender under
    /// OdinShieldRelicAura) and the speed shrine (NeverRender) can only show as camps, so the character icons above are camp icons, one camp
    /// per place (a respawn at the same place rejoins its camp). The numbers start at <see cref="FirstMinimapCamp"/>, clear of the level
    /// scripts' jungle camps (1 to 12 on the Rift) and of the guardians' shared group 0 (no icon). Reconstruction: retail's numbers are not shipped.
    /// </summary>
    private const int FirstMinimapCamp = 100;
    private readonly Dictionary<Position2, int> _minimapCamps = new();

    private int MinimapCampAt(Position2 at) =>
        _minimapCamps.TryGetValue(at, out var camp) ? camp : _minimapCamps[at] = FirstMinimapCamp + _minimapCamps.Count;

    /// <param name="now">
    /// Batch 499: the game time of the spawn, for a definition that staggers its units (<see cref="EncounterDefinition.SpawnDelay"/>); null spawns
    /// every unit at once (the host's own spawns: guardians, relics, barriers, whose definitions name no delay).
    /// </param>
    public int? SpawnSquad(int encounterId, Position2 position, Team team, string squadName, double? now = null)
    {
        if (!_encounters.TryGetValue(encounterId, out var encounter)) return null;
        // Batch 451: a SquadAI Wave definition (SmallWave, SuperMinionWave) spawns minions.
        var minion = encounter.Definition.SquadAi == "Wave" && _spawnMinion is not null;
        if ((minion ? _spawnMinion : _spawnUnit) is null) return null;
        // Batch 499 (Reconstruction: the fields' names are the evidence; the server's spawner did not ship). MaxMonstersPerSpawn units spawn at
        // once and the next ones SpawnDelay seconds apart (SmallWave.json: one unit every 0.75 s, so a wave walks out in file, not stacked on its
        // marker). The squad's mutators are read once, at the squad's spawn: the trees update them for the next lane in the same step.
        var definition = encounter.Definition;
        var stagger = now is not null && definition.SpawnDelay > 0.0 && definition.MaxMonstersPerSpawn > 0;
        var id = _nextSquad;
        var units = new List<Entity>();
        var order = 0;
        foreach (var group in definition.MonsterGroups)
        foreach (var monster in group.Monsters)
        {
            var skin = TeamSkin(monster, team);
            if (skin.Length == 0) continue;
            var mutations = Mutations(encounter, monster);
            for (var index = 0; index < monster.Count; index++, order++)
            {
                var due = stagger ? now!.Value + order / definition.MaxMonstersPerSpawn * definition.SpawnDelay : 0.0;
                if (stagger && due > now!.Value) _pending.Add((id, monster, skin, position, team, minion, mutations, due));
                else if (Spawn(monster, skin, position, team, minion, mutations) is { } unit) units.Add(unit);
            }
        }
        if (units.Count == 0)
        {
            _pending.RemoveAll(pending => pending.Squad == id);
            return null;
        }
        _nextSquad++;
        var name = squadName.Length > 0 ? squadName : definition.Name;
        _squads[id] = new SpawnedSquad(id, name, units);
        foreach (var unit in units) _squadOfUnit[unit.NetId] = id;
        return id;
    }

    /// <summary>Batch 499: spawns the staggered squads' units that are due by <paramref name="now"/> and returns them (each joins its squad).</summary>
    public IReadOnlyList<Entity> SpawnDue(double now)
    {
        if (_pending.Count == 0) return [];
        var spawned = new List<Entity>();
        foreach (var pending in _pending.Where(pending => pending.Due <= now).ToList())
        {
            _pending.Remove(pending);
            if (!_squads.TryGetValue(pending.Squad, out var squad) ||
                Spawn(pending.Monster, pending.Skin, pending.At, pending.Team, pending.Minion, pending.Mutations) is not { } unit) continue;
            ((List<Entity>)squad.Units).Add(unit);
            _squadOfUnit[unit.NetId] = squad.Id;
            spawned.Add(unit);
        }
        return spawned;
    }

    private Entity? Spawn(EncounterMonster monster, string skin, Position2 position, Team team, bool minion,
        IReadOnlyList<(string Stat, double Amount)> mutations)
    {
        if ((minion ? _spawnMinion : _spawnUnit)?.Invoke(skin, position, team) is not { } unit) return null;
        // Batch 476d: the encounter's minimap icon (the neutral creation packet carries it; ProductionMatchComposition.BuildCreation).
        unit.MinimapIcon = monster.MinimapIcon.Length > 0 ? monster.MinimapIcon : CharacterMinimapIcon(skin);
        // Batch 511: a character icon is a camp icon (see FirstMinimapCamp); the encounter's own icon (the centre shards) is the unit's (0xE1).
        if (monster.MinimapIcon.Length == 0 && unit.MinimapIcon.Length > 0) unit.MinimapCamp = MinimapCampAt(position);
        if (!monster.Targetable)
        {
            unit.NotTargetableToAlly = true;
            unit.NotTargetableToEnemy = true;
        }
        // Batch 499 (Reconstruction): the encounter's own bounty replaces the character's when the file names one (SmallWave.json: a caster 30 gold
        // and 13 experience, where Odin_Blue_Minion_Caster.inibin gives 10 and 51; the superminion 60 and 30, where its record gives 150 and 500).
        if (monster.GoldAwarded is { } gold) unit.GoldReward = gold;
        if (monster.ExperienceAwarded is { } experience) unit.ExperienceReward = experience;
        foreach (var (stat, amount) in mutations) Mutate(unit, stat, amount);
        return unit;
    }

    /// <summary>
    /// Batch 499: the stat amounts the encounter's mutators give a monster. A mutator applies to a monster that carries one of its comma-separated
    /// Tags (CapturePointManager adds MeleeWaveBase and MeleeWaveProgression with "Melee" and CapturePointMutator with "Melee,Ranged",
    /// OdinScriptBehaviorTrees.xml:2057-2101; SmallWave's casters carry "Melee", its superminion "SuperMinion", so the superminion takes none).
    /// Reconstruction: a mutator with no tags applies to every monster.
    /// </summary>
    private IReadOnlyList<(string Stat, double Amount)> Mutations(LiveEncounter encounter, EncounterMonster monster) =>
        encounter.Mutators
            .Where(mutator => Selects(mutator.Tags, monster.Tags ?? []) && Catalog.Mutators.ContainsKey(mutator.Name))
            .SelectMany(mutator => Catalog.Mutators[mutator.Name].At(mutator.MutationIndex))
            .ToList();

    private static bool Selects(string tags, IReadOnlyList<string> monsterTags)
    {
        var wanted = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return wanted.Length == 0 || wanted.Any(tag => monsterTags.Contains(tag, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Batch 499: one mutated stat on a spawned unit, the way a permanent stat lands on a non-hero (BbSpellHost.PermanentOnPet): the resistances
    /// join the unit's baseline, health raises the pool and the current health with it (a unit spawns full), attack damage adds. The wave
    /// mutators name only these four; the test mutators' PercentCooldownMod and FlatDodgeMod (TestMutator.json, ProgressiveMutator.json) are
    /// added by no tree and are not applied.
    /// </summary>
    private static void Mutate(Entity unit, string stat, double amount)
    {
        switch (stat)
        {
            case "FlatHPPoolMod":
                unit.MaxHealth += amount;
                unit.Health = Math.Min(unit.MaxHealth, unit.Health + amount);
                break;
            case "FlatPhysicalDamageMod": unit.AttackDamage += amount; break;
            case "FlatArmorMod": Buffs.BuffLifecycle.IncreaseNpcBaseResistances(unit, amount, 0.0); break;
            case "FlatSpellBlockMod": Buffs.BuffLifecycle.IncreaseNpcBaseResistances(unit, 0.0, amount); break;
        }
    }

    /// <summary>
    /// Batch 451: the skin a team's squad spawns for a monster. The team's own skin (OrderSkinName for Order, ChaosSkinName for Chaos)
    /// when the file names one, else SkinName. SmallWave.json (the Odin waves) names only the team skins, so the team skin is the
    /// shipped rule (Reconstruction for a neutral squad: SkinName).
    /// </summary>
    private static string TeamSkin(EncounterMonster monster, Team team)
    {
        var own = team == Team.Order ? monster.OrderSkinName : team == Team.Chaos ? monster.ChaosSkinName : "";
        return own.Length > 0 ? own : monster.SkinName;
    }

    /// <summary>GetSquadNameOfUnit: the name of the squad that spawned the unit, or null for a unit no squad spawned.</summary>
    public string? SquadNameOf(Entity unit) =>
        _squadOfUnit.TryGetValue(unit.NetId, out var id) && _squads.TryGetValue(id, out var squad) ? squad.Name : null;
}
