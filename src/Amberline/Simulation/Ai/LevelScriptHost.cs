using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.Lua;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Combat;
// Amberline.Data has a reader-side LuaValue record; the runtime one wins here.
using LuaValue = Amberline.Scripting.Lua.LuaValue;

namespace Amberline.Simulation.Ai;

/// <summary>
/// One minion class's row of a Map1 LevelScript barracks bonus table. <c>*MinionArmor</c>,
/// <c>*MinionMagicResistance</c>, <c>*HPBonus</c> and <c>*DamageBonus</c> are cumulative bonuses
/// on top of the character's own base stats; <c>*ExpGiven</c> and <c>*GoldGiven</c> are the full
/// rewards the class pays (<c>*_GOLD_GIVEN</c>/<c>*_EXP_GIVEN</c> plus the bonus
/// <c>UpgradeMinionTimer</c> accumulated). The script's "Archer" is our
/// <see cref="MinionType.Caster"/> and its "Caster" our <see cref="MinionType.Siege"/> — the
/// naming trap every LevelScript reader hits.
/// </summary>
public sealed record LevelEconomyRow(
    double MinionArmor,
    double MinionMagicResistance,
    double HealthBonus,
    double DamageBonus,
    double ExperienceGiven,
    double GoldGiven)
{
    /// <summary>The character's base stats with this row's bonuses and rewards applied.</summary>
    public MinionStatDefinition StatsOf(MinionType type, Team team)
    {
        var baseStats = MinionDefinitions.BaseStats(type, team);
        return baseStats with
        {
            MaxHealth = baseStats.MaxHealth + HealthBonus,
            AttackDamage = baseStats.AttackDamage + DamageBonus,
            Armor = baseStats.Armor + MinionArmor,
            SpellBlock = baseStats.SpellBlock + MinionMagicResistance,
            ExperienceReward = ExperienceGiven,
            GoldReward = GoldGiven,
        };
    }
}

/// <summary>
/// The four classes' rows of one barracks table plus <c>EXP_GIVEN_RADIUS</c>, in the emulator's
/// minion classes: exactly what <c>GetMinionSpawnInfo</c> copies into its answer
/// (LevelScript.txt proto 0.6 pc0202-0252).
/// </summary>
public sealed record LevelEconomy(
    LevelEconomyRow Melee, LevelEconomyRow Caster, LevelEconomyRow Siege, LevelEconomyRow Super,
    double ExperienceRadius)
{
    public LevelEconomyRow RowOf(MinionType type) => type switch
    {
        MinionType.Melee => Melee,
        MinionType.Caster => Caster,
        MinionType.Siege => Siege,
        MinionType.Super => Super,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public MinionStatDefinition StatsOf(MinionType type, Team team) => RowOf(type).StatsOf(type, team);
}

/// <summary>
/// One <c>GetMinionSpawnInfo</c> answer, in the emulator's minion classes. The script's fields are
/// <c>NumOfMeleeMinionsPerWave</c>, <c>NumOfArcherMinionsPerWave</c>, <c>NumOfCasterMinionsPerWave</c>
/// and <c>NumOfSuperMinionsPerWave</c>: its "Archer" is our <see cref="MinionType.Caster"/> (the
/// ranged minion) and its "Caster" is our <see cref="MinionType.Siege"/> (the cannon). The economy
/// row is that barracks table's own at the moment of the ask.
/// </summary>
public sealed record LevelWaveInfo(
    int Melee, int Caster, int Siege, int Super,
    double WaveSpawnRateSeconds, double SingleMinionSpawnDelaySeconds,
    string MeleeName, string CasterName, string SiegeName, string SuperName,
    LevelEconomy Economy)
{
    public int Count(MinionType type) => type switch
    {
        MinionType.Melee => Melee,
        MinionType.Caster => Caster,
        MinionType.Siege => Siege,
        MinionType.Super => Super,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public string NameOf(MinionType type) => type switch
    {
        MinionType.Melee => MeleeName,
        MinionType.Caster => CasterName,
        MinionType.Siege => SiegeName,
        MinionType.Super => SuperName,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>The wave's members in the order the script lists them: melee, archer, caster, super.</summary>
    public IReadOnlyList<MinionType> Members
    {
        get
        {
            var result = new List<MinionType>(Melee + Caster + Siege + Super);
            for (var index = 0; index < Melee; index++) result.Add(MinionType.Melee);
            for (var index = 0; index < Caster; index++) result.Add(MinionType.Caster);
            for (var index = 0; index < Siege; index++) result.Add(MinionType.Siege);
            for (var index = 0; index < Super; index++) result.Add(MinionType.Super);
            return result;
        }
    }
}

/// <summary>
/// One <c>CreateChildTurret(name, character, team, towerId, laneId)</c> row of Map1's shipped
/// <c>CreateLevelProps.luaobj</c>. The tower ids are the script's own
/// <c>BACK_TOWER</c>/<c>MIDDLE_TOWER</c>/<c>FRONT_TOWER</c>/<c>HQ_TOWER2</c>/<c>HQ_TOWER1</c> =
/// 1/2/3/4/5, and 0 for the two fountain shrines; the lane ids are <c>RIGHT/CENTER/LEFT</c> =
/// 0/1/2. The script names no position: retail's <c>CreateChildTurret</c> puts the child at the
/// scene object's own placement (the building's creation), which stays the engine's scene data.
/// </summary>
public sealed record LevelTurret(string Name, string Character, Team Team, int TowerId, int LaneId);

/// <summary>
/// One <c>SpawnNeutralMinion(camp, campIndex, groupIndex, memberIndex)</c> request from Map1's
/// shipped <c>NeutralMinionSpawn.luaobj</c>: one member of one generation of one camp, with the
/// unique name <c>InitializeNeutralMinionInfo</c> gave it and the health/damage bonuses the
/// script's own camp table carries at the moment it asked. Indices are the script's own 1-based
/// ones, so member 1 opens a generation.
/// </summary>
public sealed record LevelNeutralSpawn(
    int Camp, int Group, int Member,
    string Name, string UniqueName,
    Position3 Position, Position3 Face,
    double HealthBonus, double DamageBonus, int CampLevel,
    double ScheduledAt);

/// <summary>
/// One <c>NeutralMinionCamps</c> entry as the map's <c>NeutralMinionInit</c> left it: the camp's
/// groups and its spawn and face points, plus the two delays the engine's own spawn and respawn
/// scheduling reads (<c>GroupDelaySpawnTime</c> before the first generation, and the respawn after
/// an empty camp: <c>GroupsRespawnTime</c>, or <c>RespawnTime</c> on Map4). Map1's twelve camps each
/// carry one group with <c>GroupsChance = {100}</c>, so a generation is that group. Map4's camps 1-6
/// list two alternative groups; the script's own roll by <c>GroupsChance</c> picks one, and
/// <c>SpawnNeutralMinion</c> names its members from this same table, so every group is kept.
/// </summary>
public sealed record LevelNeutralCamp(
    int Number,
    IReadOnlyList<IReadOnlyList<string>> Groups,
    IReadOnlyList<Position3> Positions,
    IReadOnlyList<Position3> FacePositions,
    double GroupDelaySpawnTime,
    double GroupsRespawnTime,
    int CampLevel,
    double HealthBonus,
    double DamageBonus);

/// <summary>
/// Map1's shipped <c>LevelScript.luaobj</c> running on the batch-139 Lua 5.1 VM, one state per
/// match. The engine calls into the script's own globals — <c>OnLevelInit</c>, the timers
/// <c>InitTimer</c> arms, <c>GetMinionSpawnInfo</c> per barracks per wave, the two barracks
/// bookkeeping functions, the three neutral-camp entry points and <c>OnPostLevelLoad</c> — and
/// supplies the natives those names. Nothing of the old hand-written wave schedule is consulted:
/// the composition, the cadence and the stagger all come back out of the script's answer table,
/// and the same is true of the map's neutral camps and its turret roster.
/// </summary>
public sealed partial class LevelScriptHost : IEndOfGameScript
{
    public const string ScriptName = "LevelScript";

    private readonly WorldState _world;
    private readonly AiScriptCatalog _catalog;
    private readonly LuaState _state;
    private readonly List<AiTimer> _timers = new();
    private readonly List<(string Kind, string Name)> _preloads = new();
    private readonly List<LevelTurret> _levelTurrets = new();
    private LuaValue _nullObject;
    private double _now;
    // The structures a destruction handler was given, by netId: their handles stay resolvable while the handler runs, even when
    // the world no longer holds the fallen structure.
    private readonly Dictionary<uint, Entity> _handledStructures = new();
    // The ground the units stand on: GetPosition's Y (batch 212). Null for a host with no map (fixtures, the data probe).
    private readonly NavigationGrid? _navigation;
    private double? _fireTime;
    private Team? _askingTeam;
    private bool _initialized;
    private bool _postLoaded;
    private bool _endOfGame;

    public LevelScriptHost(WorldState world, AiScriptCatalog? catalog = null, NavigationGrid? navigation = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _catalog = catalog ?? new AiScriptCatalog();
        _navigation = navigation;
        _state = new LuaState(world.LuaRandom);
        _state.OpenLibs();
        AiGlobals.Install(_state);
        // Keep the sentinel the globals installed: LevelScript compares GetTurret()/GetDampener()
        // answers with it, so a missing structure has to come back as that very value and not as
        // Lua nil (nil ~= Nil is true, which would send a missing turret down the present branch).
        _nullObject = _state.TryGetGlobal("Nil", out var sentinel) ? sentinel : LuaValue.Nil;
        InstallOsLibrary();
        RegisterNatives();
        // The main chunk only defines its globals and helper closures; running it is what puts
        // OnLevelInit, GetMinionSpawnInfo and the barracks bonuses in place.
        Body("main chunk", () => _state.DoChunk(_catalog.Load(ScriptName)));
    }

    /// <summary>Where a shipped script's failure goes; the composition root points this at the session log.</summary>
    public Action<AiScriptFailure>? OnError { get; set; }

    /// <summary>
    /// What <c>os.time()</c> answers. A standalone host answers 0, so its random stream is fixed for the checks. The match
    /// composition sets it: the wall clock in production (retail's C <c>time()</c>, which the shipped Bot and LevelScript
    /// scripts feed to <c>math.randomseed</c>), the match's seed in a seeded match (<c>DIAGNOSTICS:</c> the match tool's
    /// <c>--seed</c>, so the stream repeats).
    /// </summary>
    public Func<double> ScriptClock { get; set; } = () => 0.0;

    /// <summary>
    /// <c>SetDisableMinionSpawn(barracks, seconds)</c>: the barracks' countdown to its inhibitor's return (the match composition wires
    /// <c>CombatService.ArmInhibitorReturn</c>). A standalone host arms nothing.
    /// </summary>
    public Action<Entity, double>? DisableMinionSpawn { get; set; }

    /// <summary>
    /// The engine's answer for <c>GetTotalTeamMinionsSpawned</c>. The native takes no arguments
    /// and the client's copy of it is a zero-returning stub
    /// as §2c of docs/research/map1_levelscript_findings.md records, so its server scope is unknown
    /// (<c>Reconstruction:</c>). LevelScript only raises it from inside <c>GetMinionSpawnInfo</c>,
    /// once per wave, so the host scopes the answer to the team whose barracks is being asked and
    /// the composition installs the population policy the wave scheduler used to apply inline.
    /// </summary>
    public Func<Team, int>? TotalTeamMinionsSpawned { get; set; }

    /// <summary>
    /// Raised when the script moved a structure's engine state (<c>SetInvulnerable</c>,
    /// <c>SetTargetable</c>, <c>SetNotTargetableToTeam</c>). The composition root points this at
    /// the map's protection ladder, which is the engine's port of the script's own
    /// <c>HandleDestroyedObject</c>/<c>DeactivateCorrectStructure</c> and is recomputed from world
    /// state; without it <c>AllowDamageOnBuildings</c> would leave a structure locked that the
    /// ladder has already released. The ladder reconciles the targetability flags along with
    /// <c>Protected</c>, because the shipped script always moves them together
    /// (LevelScript.txt:1643-1660) and the replication model reports one from the other.
    /// </summary>
    public Action? StructureStateChanged { get; set; }

    /// <summary>
    /// The engine's answer to the script's <c>SpawnNeutralMinion</c>: create one camp member and
    /// report whether it exists. Raised once per member, in the order the script lists them, so
    /// member 1 of a generation is the generation's first call.
    /// </summary>
    public Func<LevelNeutralSpawn, bool>? SpawnNeutralMember { get; set; }

    /// <summary>
    /// Raised when the script armed one of its own camp timers through
    /// <c>luaNeutralInitTimer(fn, seconds, false)</c>, with the camp the closure belongs to
    /// (<c>NeutralTimers</c> index, equal to the camp number on Map1) and the match-clock moment
    /// the callback is due. <c>NeutralMinionDeath</c> arms the respawn of an emptied camp this
    /// way and <c>SpawnInitialNeutralMinions</c> the first generation of a camp whose
    /// <c>GroupDelaySpawnTime</c> is not zero.
    /// </summary>
    public Action<int, double>? NeutralTimerArmed { get; set; }


    /// <summary>
    /// Pays a camp's experience for the native <c>GiveExpToNearHeroesFromNeutral(unit, exp, team, radius)</c>: one call per
    /// living hero of <c>team</c> within <c>radius</c> of the monster, with that hero's share. The composition hands in the
    /// combat service's <c>AwardHeroProgress</c>, so the experience bonus and the level-ups apply as they do to any award.
    /// </summary>
    public Action<Entity, double>? AwardNeutralExperience { get; set; }

    /// <summary>Raised per <c>CreateChildTurret</c> row of <c>CreateLevelProps()</c>.</summary>
    public Action<LevelTurret>? TurretCreated { get; set; }

    /// <summary>
    /// The engine's half of the end-of-game ceremony. Map1's <c>OnLevelInit</c> loads
    /// <c>EndOfGame.lua</c> into this very environment (LevelScript.txt pc 0059-0061), so the
    /// ceremony's globals are the ones the natives below are bound for; without this the chunk's
    /// calls are dropped and no ceremony runs.
    /// </summary>
    public IEndOfGameEngine? EndOfGameEngine { get; set; }

    /// <summary>
    /// Batch 691: the client input-locking flags (<see cref="AiGlobals.InputLockMask"/> bits) this state's scripts hold, as their
    /// <c>SetInputLockingFlag</c> (0x89) and <c>ToggleInputLockingFlag</c> (0xA3) natives sent them to the clients. The server refuses
    /// the client commands they cover (ProductionGameplayDispatcher.InputLocks, the shop's buy and sell), and a returning client is sent
    /// them again (DuelSession.ScriptInputLocks).
    /// </summary>
    public uint InputLocks { get; private set; }

    /// <summary>The bit of one <c>INPUT_*</c> id (300-307), or 0 for any other value (an unset global reads 0).</summary>
    private static uint InputLockBit(double id) => id is >= 300 and <= 307 ? AiGlobals.InputLockMask(id) : 0u;

    /// <summary>Character and spell preload requests, in the order the script raised them.</summary>
    public IReadOnlyList<(string Kind, string Name)> Preloads => _preloads.ToArray();

    /// <summary>The live timers, for the checks.</summary>
    public IReadOnlyList<(string Name, double Interval, bool Repeat)> Timers =>
        _timers.Where(timer => !timer.Stopped)
            .Select(timer => (timer.Name, timer.Interval, timer.Repeat))
            .ToArray();

    /// <summary>One global of the script's chunk (batch 212: <c>losingHQPosition</c> in the checks).</summary>
    internal LuaValue ScriptGlobal(string name) => _state.GetGlobal(name);

    /// <summary>DIAGNOSTICS: sets one numeric global of the script's chunk, so a check can show that a script constant decides an outcome.</summary>
    internal void SetScriptGlobal(string name, double value) => _state.SetGlobal(name, LuaValue.Of(value));

    /// <summary>Every name the script's global table holds, for the checks.</summary>
    public IReadOnlyList<string> GlobalNames =>
        _state.Globals.Keys.Where(key => key.Type == LuaType.String)
            .Select(key => key.AsString)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// The script's <c>INITIAL_TIME_TO_SPAWN</c>: the match-clock seconds of the first wave. The
    /// engine reads the level script's global for this, so the hand-written first-wave constant is
    /// not consulted.
    /// </summary>
    public double FirstSpawnSeconds => Number("INITIAL_TIME_TO_SPAWN", 90.0);

    /// <summary>
    /// NeutralMinionSpawn's <c>HERO_EXP_RADIUS</c>: the radius every
    /// <c>GiveExpToNearHeroesFromNeutral</c> call is made with. The engine's kill path shares
    /// neutral experience over the same distance.
    /// </summary>
    public double HeroExperienceRadius => Number("HERO_EXP_RADIUS", 800.0);

    /// <summary>The twelve <c>NeutralMinionCamps</c> entries, as <c>NeutralMinionInit</c> left them.</summary>
    public IReadOnlyList<LevelNeutralCamp> NeutralCamps
    {
        get
        {
            var table = TableOf("NeutralMinionCamps");
            var camps = new List<LevelNeutralCamp>();
            for (var number = 1; number <= table.Length; number++)
            {
                var entry = table.Get(LuaValue.Of((double)number));
                if (entry.Type != LuaType.Table) continue;
                var camp = entry.AsTable;
                camps.Add(new LevelNeutralCamp(number,
                    GroupsOf(camp),
                    Points(ArrayOf(camp, "Positions")),
                    Points(ArrayOf(camp, "FacePositions")),
                    Number(camp, "GroupDelaySpawnTime"),
                    RespawnOf(camp),
                    (int)Math.Round(Number(camp, "CampLevel")),
                    Number(camp, "HealthBonus"),
                    Number(camp, "DamageBonus")));
            }
            return camps;
        }
    }

    /// <summary>
    /// NeutralMinionSpawn's <c>DefaultNeutralMinionValues</c>: the experience each monster name
    /// pays on death. The script's own gold and soul columns are zero for every name; the gold a
    /// neutral pays comes from its shipped CharacterData (see
    /// <see cref="NeutralRewardRequested"/>).
    /// </summary>
    public IReadOnlyDictionary<string, double> NeutralExperience
    {
        get
        {
            var table = TableOf("DefaultNeutralMinionValues");
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (key, value) in table.Entries)
            {
                if (key.Type != LuaType.String || value.Type != LuaType.Table) continue;
                values[key.AsString] = Number(value.AsTable, "ExpGiven");
            }
            return values;
        }
    }

    /// <summary>The turret rows <c>CreateLevelProps()</c> emitted, in the order it emitted them.</summary>
    public IReadOnlyList<LevelTurret> LevelTurrets => _levelTurrets.ToArray();

    /// <summary>
    /// Runs <c>OnLevelInit</c>: the preloads, the seed, the chained map scripts and the timers.
    /// Once per host — the second call is a no-op, so a fixture may init and still hand the host
    /// to a <c>WaveScheduler</c> whose <c>Start</c> inits it again.
    /// </summary>
    public void OnLevelInit(double now)
    {
        if (!double.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(now));
        if (_initialized) return;
        _initialized = true;
        _now = now;
        Call("OnLevelInit");
        // Batch 563: the Basic Tutorial's own script joins the level's state (LevelScriptHost.Tutorial.cs).
        if (_catalog.Paths.MapId == GameMaps.Tutorial) LoadTutorial();
    }

    /// <summary>
    /// Runs the engine's <c>InitializeNeutralMinionInfo</c> (NeutralMinionSpawn.luaobj proto
    /// 0.2): the per-member unique names of every camp and one respawn closure per camp, stored
    /// in <c>NeutralTimers</c> and on the camp as its <c>Timer</c>. OnLevelInit never calls it —
    /// like <c>SpawnInitialNeutralMinions</c> and <c>NeutralMinionDeath</c> it is an engine entry
    /// point.
    /// </summary>
    public void InitializeNeutralMinionInfo() => Call("InitializeNeutralMinionInfo");

    /// <summary>
    /// Runs the engine's <c>SpawnInitialNeutralMinions(camp)</c> (proto 0.3) for one camp, as of
    /// <paramref name="at"/>. The parameter is the camp's own index — the function is
    /// single-camp and guards with <c>if param &gt; #NeutralMinionCamps then return end</c>. A
    /// camp whose <c>GroupDelaySpawnTime</c> is positive only arms its timer here; one with no
    /// delay spawns immediately and keeps the health bonus of 0 its camp table carries.
    /// </summary>
    public void SpawnInitialNeutralMinions(int camp, double at)
    {
        if (camp < 1) throw new ArgumentOutOfRangeException(nameof(camp));
        if (!double.IsFinite(at)) throw new ArgumentOutOfRangeException(nameof(at));
        _now = at;
        _fireTime = at;
        try
        {
            Call("SpawnInitialNeutralMinions", LuaValue.Of((double)camp));
        }
        finally
        {
            _fireTime = null;
        }
    }

    /// <summary>
    /// The split of <c>GiveExpToNearHeroesFromNeutral(unit, exp, team, radius)</c> (Reconstruction, see the native's registration): the
    /// living heroes of the team within the radius share <paramref name="experience"/> with
    /// <see cref="ProgressionRules.SharedUnitExperience"/> (solo factor 1.0), each paid through <paramref name="award"/>. Internal, visible
    /// to the test assembly, so the combat checks run this split with the production awarder (<c>CombatService.AwardHeroProgress</c>)
    /// and no Lua state.
    /// </summary>
    internal static void ShareNeutralExperience(WorldState world, Entity monster, Team team, double experience, double radius,
        Action<Entity, double> award)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(monster);
        ArgumentNullException.ThrowIfNull(award);
        var recipients = world.Heroes().Where(hero => hero.Alive && hero.Team == team &&
            (hero.Position.X - monster.Position.X) * (hero.Position.X - monster.Position.X) +
            (hero.Position.Z - monster.Position.Z) * (hero.Position.Z - monster.Position.Z) <= radius * radius).ToArray();
        var share = ProgressionRules.SharedUnitExperience(experience, recipients.Length, 1.0);
        foreach (var hero in recipients) award(hero, share);
    }

    /// <summary>
    /// Runs the engine's <c>NeutralMinionDeath(uniqueName, unit, team)</c> (proto 0.4) for one
    /// dead camp member. The script marks that member dead in <c>AliveTracker</c>, pays the death
    /// reward through the two natives, and arms the camp's respawn timer when the whole
    /// generation is gone — so the engine reports every member's death, not only the last.
    /// </summary>
    public void NeutralMinionDeath(string uniqueName, Entity unit, Team beneficiary, double at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uniqueName);
        ArgumentNullException.ThrowIfNull(unit);
        if (!double.IsFinite(at)) throw new ArgumentOutOfRangeException(nameof(at));
        _now = at;
        Call("NeutralMinionDeath", LuaValue.Of(uniqueName), StructureOf(unit),
            LuaValue.Of(TeamScriptId(beneficiary)));
    }

    /// <summary>
    /// Runs <c>OnPostLevelLoad</c>: Map1 chains <c>CreateLevelProps.lua</c> and calls
    /// <c>CreateLevelProps()</c>, whose twenty-four <c>CreateChildTurret</c> rows are the map's
    /// turret roster. Once per host, like <see cref="OnLevelInit"/>.
    /// </summary>
    public void OnPostLevelLoad()
    {
        if (_postLoaded) return;
        _postLoaded = true;
        Call("OnPostLevelLoad");
    }

    /// <summary>
    /// The per-tick pass: every timer whose deadline has arrived, at most once per step each. The
    /// end-of-game ceremony's phase clock is left for <see cref="AdvanceEndOfGame"/>, which runs it
    /// to completion regardless of the step size.
    /// </summary>
    public void Advance(double now, double dt)
    {
        _now = now;
        foreach (var timer in _timers.ToArray())
        {
            if (timer.EndOfGame) continue;
            StepTimer(timer, now, dt);
        }
        if (_tutorial) AdvanceTutorial(now);
    }

    /// <summary>One timer's fire and the phase bookkeeping around it, shared by the two passes.</summary>
    private void StepTimer(AiTimer timer, double now, double dt)
    {
        if (timer.Stopped) return;
        // InitTimer and luaNeutralInitTimer both arm at the call (the delay is from that moment),
        // so this only fills in a timer that was recorded without one.
        timer.Deadline ??= now - dt + timer.Interval;
        var forceNext = timer.ForceNext;
        timer.ForceNext = false;
        if (forceNext) timer.Deadline = now - dt;
        if (!forceNext && now + 1e-9 < timer.Deadline) return;
        // A camp callback's spawns are stamped with the moment the script scheduled them for,
        // not with a step that happened to run late.
        var previousFire = _fireTime;
        _fireTime = timer.Deadline;
        try
        {
            // A closure timer has no global to look up: Run's name gate would drop it.
            if (timer.Closure is { } closure)
                Body(timer.Name, () => _state.Call(closure));
            else
            {
                if (_tutorial) NoteTutorialTimer(timer.Name);
                Run(timer.Name, () => _state.CallGlobal(timer.Name));
            }
        }
        finally
        {
            _fireTime = previousFire;
        }
        if (!timer.Repeat)
        {
            timer.Stopped = true;
            return;
        }
        if (timer.Interval <= 0.0)
        {
            timer.Deadline = now;
            return;
        }
        // Keep the phase across a stalled step: one fire per step, and the next deadline moves
        // on by as many whole intervals as this step ran past the old one.
        var overdue = Math.Max(0.0, now - timer.Deadline.Value);
        timer.Deadline += (Math.Floor(overdue / timer.Interval) + 1.0) * timer.Interval;
    }

    /// <summary>
    /// Stops the named <c>InitTimer</c> callbacks <c>OnLevelInit</c> armed
    /// (<c>UpgradeMinionTimer</c>, <c>IncreaseCannonMinionSpawnRate</c>,
    /// <c>AllowDamageOnBuildings</c>). <c>DIAGNOSTICS:</c> a diagnostic seam (batch 219 item 2). Retail keeps one script
    /// environment and one timer list, and runs these timers on the match clock. Two callers stop them here: a standalone
    /// <c>JungleScheduler</c> (the checks, which have no wave scheduler, and whose own host would otherwise re-run the
    /// level's economy and structure timers) and the <c>--visibility-baseline</c> server option (no waves). The camp
    /// closures <c>luaNeutralInitTimer</c> arms are left alone.
    /// </summary>
    public void StopNamedTimers()
    {
        foreach (var timer in _timers)
            if (timer.Closure is null) timer.Stopped = true;
    }

    /// <summary>Makes every live timer run on the next step and keep its phase from that step.</summary>
    public void RephaseTimers()
    {
        foreach (var timer in _timers)
            if (!timer.Stopped) timer.ForceNext = true;
    }

    /// <summary>
    /// Runs this level's own timers forward to <paramref name="now"/>, one fire per deadline, so
    /// a fixture can read the script's tables at a later match time. Live matches leave the clock
    /// to <see cref="Advance"/> on the wave tick. Timers of zero interval repeat every step and
    /// never schedule, so they are left to the final step.
    /// </summary>
    public void AdvanceTo(double now)
    {
        if (!double.IsFinite(now) || now < _now)
            throw new ArgumentOutOfRangeException(nameof(now));
        if (_timers.Any(timer => !timer.Stopped && timer.Deadline is null)) Advance(_now, 0.0);
        for (var guard = 0; guard < 10000; guard++)
        {
            var next = double.PositiveInfinity;
            foreach (var timer in _timers)
                if (!timer.Stopped && timer.Interval > 0.0 && timer.Deadline is double deadline && deadline < next)
                    next = deadline;
            if (next > now) break;
            Advance(next, next - _now);
        }
        if (now > _now) Advance(now, now - _now);
    }

    /// <summary>
    /// The one barracks answer a wave is built from. <paramref name="waveIndex"/> is zero-based;
    /// the native barracks counter is incremented before the Lua call, so the script sees <c>waveIndex + 1</c> as its
    /// <c>CASTER_MINION_SPAWN_FREQUENCY</c> phase — cannons on human waves 3, 6, 9, not 1, 4, 7.
    /// </summary>
    public LevelWaveInfo GetMinionSpawnInfo(Team team, string lane, int waveIndex)
    {
        if (waveIndex < 0) throw new ArgumentOutOfRangeException(nameof(waveIndex));
        ValidateLane(lane);
        var barracks = ScriptLaneId(lane);
        var counter = waveIndex + 1.0;
        // The script's budget block runs once per wave (its LAST_WAVE guard) and reads the
        // argless GetTotalTeamMinionsSpawned while it does: scope that answer to this team.
        _askingTeam = team;
        LuaValue result;
        try
        {
            result = Call("GetMinionSpawnInfo", LuaValue.Of(barracks), LuaValue.Of(counter),
                LuaValue.Of(0.0), LuaValue.Of(ScriptTeamId(team)), LuaValue.Of(counter));
        }
        finally
        {
            _askingTeam = null;
        }
        if (result.Type != LuaType.Table)
            return new LevelWaveInfo(0, 0, 0, 0, 30.0, 0.8,
                MinionDefinitions.CharacterName(MinionType.Melee, team),
                MinionDefinitions.CharacterName(MinionType.Caster, team),
                MinionDefinitions.CharacterName(MinionType.Siege, team),
                MinionDefinitions.CharacterName(MinionType.Super, team),
                EconomyOf(team, lane));
        var table = result.AsTable;
        // A barracks whose inhibitor fell keeps spawning: the shared obj_Barracks update never reads the SetDisableMinionSpawn
        // countdown in its wave spawn (batch 310; the native below).
        int Spawn(string key) => Count(table, key);
        return new LevelWaveInfo(
            Spawn("NumOfMeleeMinionsPerWave"),
            Spawn("NumOfArcherMinionsPerWave"),
            Spawn("NumOfCasterMinionsPerWave"),
            Spawn("NumOfSuperMinionsPerWave"),
            Number(table, "WaveSpawnRate") / 1000.0,
            Number(table, "SingleMinionSpawnDelay") / 1000.0,
            Text(table, "MeleeMinionName"), Text(table, "ArcherMinionName"),
            Text(table, "CasterMinionName"), Text(table, "SuperMinionName"),
            EconomyOf(team, lane));
    }

    /// <summary>
    /// The economy row of one barracks table — the same fields <c>GetMinionSpawnInfo</c> copies
    /// into its answer, read without the wave ask (which moves the script's own budget block and
    /// <c>LAST_WAVE</c> state). Every gold, XP, upgrade and inhibitor number a minion is born
    /// with comes out of here.
    /// </summary>
    public LevelEconomy EconomyOf(Team team, string lane)
    {
        if (team is not (Team.Order or Team.Chaos)) throw new ArgumentOutOfRangeException(nameof(team));
        ValidateLane(lane);
        var table = BarracksTable(team, lane);
        return new LevelEconomy(
            RowOf(table, "Melee"), RowOf(table, "Archer"), RowOf(table, "Caster"), RowOf(table, "Super"),
            Number("EXP_GIVEN_RADIUS", 1250.0));
    }

    /// <summary>
    /// An inhibitor fell: the shipped <c>HandleDestroyedObject</c> (LevelScript proto 0.14) runs for it. That handler arms the
    /// lane's barracks' countdown to the inhibitor's return (<c>DisableBarracksSpawn</c>), locks the inhibitor, and calls <c>ApplyBarracksDestructionBonuses</c> with
    /// the team whose minions push the lane, the opposite of the dead structure's owner (pc 0119-0122).
    /// </summary>
    public void OnBarracksFell(Entity inhibitor)
    {
        ArgumentNullException.ThrowIfNull(inhibitor);
        _handledStructures[inhibitor.NetId] = inhibitor;
        Call("HandleDestroyedObject", StructureOf(inhibitor));
    }

    /// <summary>An inhibitor came back: <c>ApplyBarracksRespawnReductions(beneficiary, lane)</c>.</summary>
    public void OnBarracksRestored(Entity inhibitor)
    {
        ArgumentNullException.ThrowIfNull(inhibitor);
        Call("ApplyBarracksRespawnReductions", LuaValue.Of(ScriptTeamId(MinionDefinitions.Opposing(inhibitor.Team))),
            LuaValue.Of(ScriptLaneId(LaneOfInhibitor(inhibitor.Name))));
    }

    /// <summary>NOT RETAIL (batch 509): the Crystal Scar's main-SFX fade delay, the crystal's 4.46 s fall sound rounded up.</summary>
    public const double CrystalFallSoundSeconds = 4.5;

    /// <summary>
    /// The engine's entry into <c>EndOfGame.luaobj</c>: Map1's <c>HandleDestroyedObject</c> HQ
    /// branch does exactly this for a fallen nexus (proto 0.14 pc 0016-0024, <c>EndOfGameCeremony
    /// (TEAM_ORDER, obj)</c> when the chaos HQ died and the other way round), so the engine calls
    /// that same global with the same two arguments. The script then locks the inputs, freezes both
    /// HQs, halts every AI and arms its own phase timers. Answers whether the chunk had the hook.
    /// </summary>
    public bool EndOfGameCeremony(Team winner, Entity destroyedNexus, double now)
    {
        ArgumentNullException.ThrowIfNull(destroyedNexus);
        if (!double.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(now));
        _now = now;
        if (!_state.TryGetGlobal("EndOfGameCeremony", out var entry) || entry.Type != LuaType.Function)
            return false;
        // Everything it arms from here on is the ceremony's phase clock, not the map's economy.
        _endOfGame = true;
        // NOT RETAIL (batch 509; owner, 31 October 2026: the nexus fall has no sound): on the Crystal Scar the main-SFX fade waits for the
        // crystal's fall sound. Retail runs EndOfGame.lua's ceremony on map 8 too: the map's OnLevelInit loads it over the map's own
        // EndOfGameCeremony (LevelScript.luaobj proto 0.1 pc 0133; the script loader runs the file in the same state).
        // That ceremony fades master/sfx/main 1 s after the fall, over 2 s (EOG_MAIN_SFX_FADE_DELAY_TIME). The crystal's Death AnimEffect
        // Odin_Crystal_Cracked_* plays Buildings/Odin/EOG/buffacitvateglobal (2D) and buffactivatelocal (3D). Both are in the category
        // sfx/main/spells/Odin and last 4.46 s (LoL_SFX_odin.fsb), so the fade silenced them. Map 8's own ceremony (proto 0.15) has no fade.
        if (MapDefinitions.OnCrystalScar) _state.SetGlobal("EOG_MAIN_SFX_FADE_DELAY_TIME", LuaValue.Of(CrystalFallSoundSeconds));
        Body("EndOfGameCeremony", () => _state.CallGlobal("EndOfGameCeremony",
            LuaValue.Of(ScriptTeamId(winner)), StructureOf(destroyedNexus)));
        return true;
    }

    /// <summary>
    /// The ceremony's phase clock: every timer the ceremony armed whose deadline has arrived, to
    /// completion — <c>DestroyNexusPhase</c> arms the next two with delays of its own
    /// (EndOfGame.txt proto 0.3 pc 0009-0018) and one coarse step has to run that whole ladder.
    /// The map's own timers are not touched: the wave scheduler advances those, and it is stopped
    /// once the match is over. Batch 580: the Basic Tutorial's callbacks keep being polled, because its last lessons run over the
    /// ceremony (the chaos HQ's UnitKilled callback EventSucceedDelay arms EventSucceededCombined's VO 6 s later, TutorialScript.txt
    /// proto 0.87-0.90); what they arm then is ceremony time like the rest.
    /// </summary>
    public void AdvanceEndOfGame(double now)
    {
        if (!double.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(now));
        _now = now;
        for (var guard = 0; guard < 32; guard++)
        {
            var due = _timers.Where(timer => timer.EndOfGame && !timer.Stopped &&
                    timer.Deadline is double deadline && now + 1e-9 >= deadline)
                .OrderBy(timer => timer.Deadline).ToList();
            if (due.Count == 0) break;
            foreach (var timer in due) StepTimer(timer, now, 0.0);
        }
        if (_tutorial) AdvanceTutorial(now);
    }

    /// <summary>One of the script's own globals, for the checks and the wave scheduler.</summary>
    public double Number(string name, double fallback) =>
        _state.TryGetGlobal(name, out var value) ? value.ToNumber() ?? fallback : fallback;

    private LuaValue Call(string hook, params LuaValue[] arguments) =>
        Run(hook, () => _state.CallGlobal(hook, arguments));

    /// <summary>Runs the callback the script defined under this name, when there is one.</summary>
    private LuaValue Run(string hook, Func<LuaValue[]> body)
    {
        if (!_state.TryGetGlobal(hook, out var entry) || entry.Type != LuaType.Function)
            return LuaValue.Nil;
        return Body(hook, body);
    }

    /// <summary>Runs a piece of the script and reports what it raises, named after the hook.</summary>
    private LuaValue Body(string hook, Func<LuaValue[]> body)
    {
        try
        {
            var results = body();
            return results.Length > 0 ? results[0] : LuaValue.Nil;
        }
        catch (Exception error)
        {
            OnError?.Invoke(new AiScriptFailure(ScriptName, hook, error.Message));
            return LuaValue.Nil;
        }
    }

    /// <summary>
    /// Retail's <c>os</c> library is C <c>time()</c>, and LevelScript feeds it to <c>math.randomseed</c>, so a match's
    /// stream follows <see cref="ScriptClock"/>: the wall clock in production, the match's seed in a seeded match.
    /// </summary>
    private void InstallOsLibrary()
    {
        var os = new LuaTable();
        os.Set(LuaValue.Of("time"), LuaValue.Of(new LuaNative("os.time", (_, _) => new[] { LuaValue.Of(ScriptClock()) })));
        _state.SetGlobal("os", LuaValue.Of(os));
    }

    private void RegisterNatives()
    {
        // Asset preloads: the server loads what it needs when it needs it, so these only record
        // what the script asked for (the checks read the list).
        _state.Register("PreloadCharacter", (_, args) =>
        {
            _preloads.Add(("character", Text(args, 0)));
            return Array.Empty<LuaValue>();
        });
        _state.Register("PreloadSpell", (_, args) =>
        {
            _preloads.Add(("spell", Text(args, 0)));
            return Array.Empty<LuaValue>();
        });

        // NeutralMinionSpawn paints its camps with this at load time; the coordinates round-trip
        // as a table of them (Reconstruction: retail handed out a vector userdata).
        _state.Register("Make3DPoint", (_, args) => new[]
        {
            PointValue(new Position3(Number(args, 0), Number(args, 1), Number(args, 2))),
        });

        _state.Register("InitTimer", (_, args) =>
        {
            var name = Text(args, 0);
            if (name.Length == 0) return Array.Empty<LuaValue>();
            _timers.RemoveAll(timer => timer.Name == name);
            // The delay runs from the call, not from the next step that happens to notice the
            // timer: EndOfGame's DestroyNexusPhase arms its own two follow-ups, and those have to
            // land one interval after the phase fired (EndOfGame.txt proto 0.3 pc 0009-0018).
            var interval = Math.Max(0.0, Number(args, 1));
            _timers.Add(new AiTimer(name, interval, args.Length > 2 && args[2].Truthy)
            {
                Deadline = (_fireTime ?? _now) + interval,
                // Everything the ceremony arms is its own phase clock, not the map's economy.
                EndOfGame = _endOfGame,
            });
            return Array.Empty<LuaValue>();
        });
        _state.Register("StopTimer", (_, args) =>
        {
            foreach (var timer in _timers.Where(timer => timer.Name == Text(args, 0)))
                timer.Stopped = true;
            return Array.Empty<LuaValue>();
        });
        _state.Register("ResetAndStartTimer", (_, args) =>
        {
            foreach (var timer in _timers.Where(timer => timer.Name == Text(args, 0)))
            {
                timer.Stopped = false;
                timer.Deadline = _now + timer.Interval;
            }
            return Array.Empty<LuaValue>();
        });

        _state.Register("dofile", (_, args) =>
        {
            _catalog.RunIn(_state, Text(args, 0));
            return Array.Empty<LuaValue>();
        });
        _state.Register("LoadScriptIntoScript", (_, args) =>
        {
            _catalog.RunIn(_state, Text(args, 0));
            return Array.Empty<LuaValue>();
        });
        _state.Register("LoadScript", (_, args) =>
        {
            var environment = new LuaTable();
            var chunk = _catalog.Load(Text(args, 0));
            _state.Call(LuaValue.Of(new LuaClosure(chunk.TopLevel, Array.Empty<LuaUpvalue>(), environment)));
            return new[] { LuaValue.Of(environment) };
        });

        _state.Register("GetTotalTeamMinionsSpawned", (_, _) => new[]
        {
            LuaValue.Of(TotalTeamMinionsSpawned?.Invoke(_askingTeam ?? Team.Order) ?? 0),
        });

        // Batch 477: GetHQ answers the team's nexus by team, as EndOfGame.luaobj reads it (orderHQ = GetHQ(TEAM_ORDER), chaosHQ =
        // GetHQ(TEAM_CHAOS), each frozen by SetInvulnerable, SetTargetable and SetBuildingHealthRegenEnabled). The Summoner's Rift
        // nexuses are HQ_T1 (Order) and HQ_T2 (Chaos); the Crystal Scar's are its two Odin crystals, which no name lookup finds.
        _state.Register("GetHQ", (_, args) => new[] { StructureOf(FindNexus(ScriptTeamIdToTeam(Number(args, 0)))) });
        _state.Register("GetTurret", (_, args) => new[]
        {
            StructureOf(FindEntity(TurretName(ScriptTeamIdToTeam(Number(args, 0)),
                (int)Number(args, 1), (int)Number(args, 2)))),
        });
        _state.Register("GetDampener", (_, args) => new[]
        {
            StructureOf(FindEntity(InhibitorName(ScriptTeamIdToTeam(Number(args, 0)),
                LaneIdToString((int)Number(args, 1))))),
        });

        _state.Register("SetInvulnerable", (_, args) =>
        {
            if (EntityOf(args, 0) is { } unit)
            {
                unit.Protected = args.Length > 1 && args[1].Truthy;
                StructureStateChanged?.Invoke();
            }
            return Array.Empty<LuaValue>();
        });
        _state.Register("SetTargetable", (_, args) =>
        {
            if (EntityOf(args, 0) is { } unit)
            {
                var targetable = args.Length > 1 && args[1].Truthy;
                unit.NotTargetableToAlly = !targetable;
                unit.NotTargetableToEnemy = !targetable;
                StructureStateChanged?.Invoke();
            }
            return Array.Empty<LuaValue>();
        });
        // HandleDestroyedObject (LevelScript proto 0.14), the shipped handler of a fallen inhibitor, reads these natives. An inhibitor is
        // no HQ (nil), is a dampener, is no turret AI, and its barracks handle is the team id plus the lane id (the
        // script hands it back to SetDisableMinionSpawn). The script's own team, lane and HQ constants are the same numbers (TEAM_ORDER 100, TEAM_CHAOS 200).
        _state.Register("GetHQType", (_, args) => EntityOf(args, 0) is { Kind: EntityKind.Nexus } nexus
            ? new[] { LuaValue.Of(nexus.Team == Team.Order ? 1.0 : 2.0) } : Array.Empty<LuaValue>());
        _state.Register("IsDampener", (_, args) => new[] { LuaValue.Of(EntityOf(args, 0)?.Kind == EntityKind.Inhibitor) });
        _state.Register("IsTurretAI", (_, args) => new[] { LuaValue.Of(EntityOf(args, 0)?.Kind == EntityKind.Turret) });
        _state.Register("GetLinkedBarrack", (_, args) => new[] { args.Length > 0 ? args[0] : LuaValue.Nil });
        _state.Register("GetTeamID", (_, args) => EntityOf(args, 0) is { } team
            ? new[] { LuaValue.Of(ScriptTeamId(team.Team)) } : Array.Empty<LuaValue>());
        _state.Register("GetLane", (_, args) => EntityOf(args, 0) is { } lane
            ? new[] { LuaValue.Of(ScriptLaneId(LaneOfInhibitor(lane.Name))) } : Array.Empty<LuaValue>());
        _state.Register("GetDampenerType", (_, args) => EntityOf(args, 0) is { } dampener
            ? new[] { LuaValue.Of((dampener.Team == Team.Order ? ScriptTeamId(Team.Order) : 0.0) +
                ScriptLaneId(LaneOfInhibitor(dampener.Name))) }
            : Array.Empty<LuaValue>());
        _state.Register("GetBarracks", (_, args) => new[] { LuaValue.Of(Number(args, 0) + Number(args, 1)) });
        // The barracks' Lua record: DisableBarracksSpawn counts NumOfSpawnDisables in it (the table the economy reads).
        _state.Register("GetLuaBarracks", (_, args) => new[]
        {
            LuaValue.Of(BarracksTable(ScriptTeamIdToTeam(Number(args, 0)), LaneIdToString((int)Number(args, 1)))),
        });
        // SetDisableMinionSpawn(barracks, seconds) stores the time in the barracks record and sets its countdown flag. The
        // obj_Barracks update counts it down and at zero puts the linked dampener back in state 1 (the state setter
        // ): it is the inhibitor's respawn clock, DisableBarracksSpawn's DISABLE_MINION_SPAWN_BASE_TIME 300 + MAG 0 x
        // NumOfSpawnDisables (LevelScript pc 0142-0145). The wave spawn does not read it, so it withholds no wave (batch 310; it did).
        // The countdown is the combat service's (CombatService.ArmInhibitorReturn, batch 314), on the inhibitor GetBarracks' handle names
        // (the team id plus the lane id, the dampener finds by the barracks' team and lane).
        _state.Register("SetDisableMinionSpawn", (_, args) =>
        {
            var handle = Number(args, 0);
            var team = handle >= ScriptTeamId(Team.Chaos) ? Team.Chaos : Team.Order;
            if (FindEntity(InhibitorName(team, LaneIdToString((int)(handle - ScriptTeamId(team))))) is { } inhibitor)
                DisableMinionSpawn?.Invoke(inhibitor, Number(args, 1));
            return Array.Empty<LuaValue>();
        });
        // SetDampenerState(dampener, state) switches the dampener to that state when it differs.
        // Reconstruction: HandleDestroyedObject passes the global DampenerRegenerationState, which no shipped script sets and the client
        // does not register (no such string in the client), so its value was the server's; the inhibitor's dead state is CombatService's.
        _state.Register("SetDampenerState", (_, _) => Array.Empty<LuaValue>());
        // The script's diagnostic line (its one call is the "could not find the linking barracks" branch, not reached for an inhibitor).
        _state.Register("Log", (_, _) => Array.Empty<LuaValue>());
        _state.Register("SetNotTargetableToTeam", (_, args) =>
        {
            if (EntityOf(args, 0) is not { } unit) return Array.Empty<LuaValue>();
            // AllowDamageOnBuildings passes the constant of the team that must not target the
            // structure (TEAM_CHAOS for an Order turret), so the flag lands on the side the
            // observer is on: not-targetable-to-enemy for the owning team's structure.
            var blocked = args.Length > 2 && ScriptTeamIdToTeam(Number(args, 2)) != unit.Team;
            if (args.Length > 1 && args[1].Truthy)
            {
                if (blocked) unit.NotTargetableToEnemy = true;
                else unit.NotTargetableToAlly = true;
            }
            else
            {
                if (blocked) unit.NotTargetableToEnemy = false;
                else unit.NotTargetableToAlly = false;
            }
            StructureStateChanged?.Invoke();
            return Array.Empty<LuaValue>();
        });

        // ---- NeutralMinionSpawn.luaobj natives ---------------------------------------------
        // SpawnNeutralMinion is one member of one generation, called back to back for a group; the
        // engine creates each one as it is asked.
        _state.Register("SpawnNeutralMinion", (_, args) =>
        {
            if (args.Length < 4 || args[0].Type != LuaType.Table) return Array.Empty<LuaValue>();
            var camp = args[0].AsTable;
            var campIndex = (int)Math.Round(Number(args, 1));
            var groupIndex = (int)Math.Round(Number(args, 2));
            var memberIndex = (int)Math.Round(Number(args, 3));
            var group = ArrayOf(ArrayOf(camp, "Groups"), groupIndex);
            var request = new LevelNeutralSpawn(campIndex, groupIndex, memberIndex,
                Text(group, memberIndex),
                Text(ArrayOf(ArrayOf(camp, "UniqueNames"), groupIndex), memberIndex),
                Point(ArrayOf(camp, "Positions"), memberIndex),
                Point(ArrayOf(camp, "FacePositions"), memberIndex),
                Number(camp, "HealthBonus"), Number(camp, "DamageBonus"),
                (int)Math.Round(Number(camp, "CampLevel")),
                _fireTime ?? _now);
            SpawnNeutralMember?.Invoke(request);
            return Array.Empty<LuaValue>();
        });

        // luaNeutralInitTimer(fn, seconds, repeat) arms one of the script's own closures. The
        // delay runs from the call, which is what makes the camp's first generation and its
        // respawn land on the schedule the script wrote.
        _state.Register("luaNeutralInitTimer", (_, args) =>
        {
            if (args.Length == 0 || args[0].Type != LuaType.Function) return Array.Empty<LuaValue>();
            var closure = args[0];
            _timers.RemoveAll(timer => timer.Closure is { } armed && armed.Equals(closure));
            // The delay runs from the moment of the call: the initial spawn is a call made as of
            // the spawn moment, and a respawn is one made as of the death.
            var deadline = (_fireTime ?? _now) + Math.Max(0.0, Number(args, 1));
            var timer = new AiTimer("neutral-camp", Math.Max(0.0, Number(args, 1)),
                args.Length > 2 && args[2].Truthy, closure) { Deadline = deadline };
            _timers.Add(timer);
            var camp = CampOfTimer(closure);
            if (camp is not null)
                NeutralTimerArmed?.Invoke(camp.Value, deadline);
            return Array.Empty<LuaValue>();
        });

        // Reconstruction: GiveExpToNearHeroesFromNeutral(unit, exp, team, radius) pays inside the script, as retail's does
        // (NeutralMinionSpawn.luaobj NeutralMinionDeath, proto 0.4). Its split is unrecoverable: the client's body is a bare `ret`
        // and the debug data names only the script helper's wrapper (batch 310). The living heroes of the team within the radius share the
        // experience with ProgressionRules.SharedUnitExperience (solo factor 1.0), the split the kill path paid before batch 219.
        _state.Register("GiveExpToNearHeroesFromNeutral", (_, args) =>
        {
            var award = AwardNeutralExperience;
            var monster = EntityOf(args, 0);
            if (award is null || monster is null) return Array.Empty<LuaValue>();
            ShareNeutralExperience(_world, monster, ScriptTeamIdToTeam(Number(args, 2)), Number(args, 1), Number(args, 3), award);
            return Array.Empty<LuaValue>();
        });
        // IncGold(unit, gold) adds the amount to the unit's gold field ("Gold = " in the debug dump) through the gold setter. Its one shipped call (NeutralMinionSpawn NeutralMinionDeath pc 0063-0067) passes
        // DefaultNeutralMinionValues' GoldGiven, 0.0 for every jungle monster, so paying nothing is exact (batch 310); the gold a camp
        // gives is the CharacterData gold the kill path pays.
        _state.Register("IncGold", (_, _) => Array.Empty<LuaValue>());

        // ---- CreateLevelProps.luaobj natives -----------------------------------------------
        _state.Register("CreateChildTurret", (_, args) =>
        {
            if (args.Length < 5) return Array.Empty<LuaValue>();
            var row = new LevelTurret(Text(args, 0), Text(args, 1),
                ScriptTeamIdToTeam(Number(args, 2)), (int)Math.Round(Number(args, 3)),
                (int)Math.Round(Number(args, 4)));
            _levelTurrets.Add(row);
            TurretCreated?.Invoke(row);
            return Array.Empty<LuaValue>();
        });

        // ---- EndOfGame.luaobj natives ------------------------------------------------------
        // Map1's OnLevelInit loads EndOfGame.lua into this very environment (LevelScript.txt pc
        // 0059-0061), so the ceremony calls these. Everything that is not state on a unit the
        // host already owns goes through EndOfGameEngine; without one (a fixture) the call is
        // dropped, and the ceremony still runs its own phases.
        _state.Register("GetPosition", (_, args) =>
        {
            // The native returns the object's stored position, Y included (the client object's position field). Batch 212:
            // Reconstruction: the emulator's entities store no Y (Position2), so the Y is the height the spawn and move
            // packets send for the unit's ground (NavigationGrid.HeightAt, as ProductionMatchComposition sends it). A host
            // without a map keeps the ground plane at 0.
            var at = EntityOf(args, 0)?.Position ?? Position2.Zero;
            var height = _navigation?.HeightAt(at) ?? 0.0;
            return new[] { PointValue(new Position3(at.X, height, at.Z)) };
        });
        _state.Register("SetBuildingHealthRegenEnabled", (_, args) =>
        {
            if (EntityOf(args, 0) is { } building)
                EndOfGameEngine?.SetBuildingHealthRegenEnabled(building, args.Length > 1 && args[1].Truthy);
            return Array.Empty<LuaValue>();
        });
        _state.Register("SetInputLockingFlag", (_, args) =>
        {
            var locked = args.Length > 1 && args[1].Truthy;
            EndOfGameEngine?.SetInputLockingFlag(Number(args, 0), locked);
            var flag = InputLockBit(Number(args, 0));
            InputLocks = locked ? InputLocks | flag : InputLocks & ~flag;
            return Array.Empty<LuaValue>();
        });
        _state.Register("DisableHUDForEndOfGame", (_, _) =>
        {
            EndOfGameEngine?.DisableHudForEndOfGame();
            return Array.Empty<LuaValue>();
        });
        _state.Register("SetBarracksSpawnEnabled", (_, args) =>
        {
            EndOfGameEngine?.SetBarracksSpawnEnabled(args.Length > 0 && args[0].Truthy);
            return Array.Empty<LuaValue>();
        });
        // Reconstruction: The Crystal Scar's EndOfGameCeremony calls ToggleBarracks() with no argument and no result, once, after the HUD switch
        // (map 8 LevelScript.luaobj proto 0.15, pc 0080). The shipped files do not say what it switches. The ceremony runs while the barracks
        // still spawn, so it turns their spawn off through the engine's SetBarracksSpawnEnabled switch. Map 8 has no barracks before a later phase.
        _state.Register("ToggleBarracks", (_, _) =>
        {
            EndOfGameEngine?.SetBarracksSpawnEnabled(false);
            return Array.Empty<LuaValue>();
        });
        _state.Register("CloseShop", (_, _) =>
        {
            EndOfGameEngine?.CloseShop();
            return Array.Empty<LuaValue>();
        });
        _state.Register("HaltAllAI", (_, _) =>
        {
            EndOfGameEngine?.HaltAllAi();
            return Array.Empty<LuaValue>();
        });
        _state.Register("LuaForEachChampion", (_, args) =>
        {
            var team = Number(args, 0);
            var hook = Text(args, 1);
            foreach (var hero in _world.Heroes())
            {
                // TEAM_UNKNOWN visits every champion (EndOfGame.txt proto 0.0 pc 0078-0084).
                if (team != 0.0 && hero.Team != ScriptTeamIdToTeam(team)) continue;
                Run(hook, () => _state.CallGlobal(hook, StructureOf(hero)));
            }
            return Array.Empty<LuaValue>();
        });
        _state.Register("MoveCameraFromCurrentPositionToPoint", (_, args) =>
        {
            if (EntityOf(args, 0) is { } hero)
                EndOfGameEngine?.MoveCameraToPoint(hero, PointOf(args.Length > 1 ? args[1] : LuaValue.Nil),
                    Number(args, 2));
            return Array.Empty<LuaValue>();
        });
        _state.Register("SetGreyscaleEnabledWhenDead", (_, args) =>
        {
            if (EntityOf(args, 0) is { } hero)
                EndOfGameEngine?.SetGreyscaleEnabledWhenDead(hero, args.Length > 1 && args[1].Truthy);
            return Array.Empty<LuaValue>();
        });
        _state.Register("FadeOutMainSFX", (_, args) =>
        {
            EndOfGameEngine?.FadeOutMainSfx(Number(args, 0));
            return Array.Empty<LuaValue>();
        });
        _state.Register("SetHQCurrentSkin", (_, args) =>
        {
            EndOfGameEngine?.SetNexusSkin(ScriptTeamIdToTeam(Number(args, 0)), (int)Math.Round(Number(args, 1)));
            return Array.Empty<LuaValue>();
        });
        _state.Register("FadeMinions", (_, args) =>
        {
            EndOfGameEngine?.FadeMinions(ScriptTeamIdToTeam(Number(args, 0)), Number(args, 1), Number(args, 2));
            return Array.Empty<LuaValue>();
        });
        _state.Register("SetMinionsNoRender", (_, args) =>
        {
            EndOfGameEngine?.SetMinionsNoRender(ScriptTeamIdToTeam(Number(args, 0)), args.Length > 1 && args[1].Truthy);
            return Array.Empty<LuaValue>();
        });
        _state.Register("EndGame", (_, args) =>
        {
            EndOfGameEngine?.EndGame(ScriptTeamIdToTeam(Number(args, 0)));
            return Array.Empty<LuaValue>();
        });
    }

    /// <summary>
    /// The camp whose <c>Timer</c> field is this closure, or null when
    /// <c>luaNeutralInitTimer</c> was handed something that is not a camp timer.
    /// </summary>
    private int? CampOfTimer(LuaValue closure)
    {
        if (!_state.TryGetGlobal("NeutralMinionCamps", out var value) || value.Type != LuaType.Table)
            return null;
        var camps = value.AsTable;
        for (var number = 1; number <= camps.Length; number++)
        {
            var entry = camps.Get(LuaValue.Of((double)number));
            if (entry.Type != LuaType.Table) continue;
            var timer = entry.AsTable.Get(LuaValue.Of("Timer"));
            if (timer.Equals(closure)) return number;
        }
        return null;
    }

    private Entity? FindEntity(string? name) =>
        name is null ? null : _world.Entities.Values.FirstOrDefault(entity => entity.Name == name);

    // Batch 477: a team's nexus, whatever the map calls it (HQ_T1/HQ_T2 on the Summoner's Rift, the Odin crystals on the Crystal Scar).
    private Entity? FindNexus(Team team) =>
        _world.Entities.Values.FirstOrDefault(entity => entity.Kind == EntityKind.Nexus && entity.Team == team);

    /// <summary>
    /// The engine's null-object sentinel: LevelScript compares structure answers with the global
    /// <c>Nil</c>, so an absent structure has to be that very table and never Lua nil.
    /// </summary>
    private LuaValue StructureOf(Entity? entity)
    {
        if (entity is null) return _nullObject;
        var handle = new LuaTable();
        handle.Set(LuaValue.Of("netId"), LuaValue.Of((double)entity.NetId));
        return LuaValue.Of(handle);
    }

    private Entity? EntityOf(LuaValue[] args, int index)
    {
        if (index >= args.Length || args[index].Type != LuaType.Table) return null;
        var netId = args[index].AsTable.Get(LuaValue.Of("netId")).ToNumber();
        return netId is null ? null
            : _world.Get((uint)netId.Value) ?? (_handledStructures.TryGetValue((uint)netId.Value, out var handled) ? handled : null);
    }

    /// <summary>
    /// The script's lane ids: <c>RIGHT_LANE = 0</c>, <c>CENTER_LANE = 1</c>, <c>LEFT_LANE = 2</c>.
    /// <c>Reconstruction:</c> the Map1 barracks and turret names use the same letters for the same
    /// lanes (Barracks_T1_L1 is the Order top inhibitor, Turret_T1_L_02 the top inner tower), so
    /// the left lane is "L" and the right one "R". Only self-consistency matters for the bonus
    /// tables; the identity decides which lane's minions get super minions.
    /// </summary>
    private static double ScriptLaneId(string lane) => lane switch
    {
        "R" => 0.0,
        "C" => 1.0,
        "L" => 2.0,
        _ => throw new ArgumentException($"Unknown Map1 lane '{lane}'.", nameof(lane)),
    };

    private static string LaneIdToString(int laneId) => laneId switch
    {
        0 => "R",
        1 => "C",
        2 => "L",
        _ => throw new ArgumentOutOfRangeException(nameof(laneId)),
    };

    private static double ScriptTeamId(Team team) => team switch
    {
        Team.Order => 100.0,
        Team.Chaos => 200.0,
        _ => throw new ArgumentOutOfRangeException(nameof(team)),
    };

    /// <summary>
    /// The script's team constants for every team a neutral death can name, including the camps'
    /// own <c>TEAM_NEUTRAL = 300</c>.
    /// </summary>
    private static double TeamScriptId(Team team) => team switch
    {
        Team.Order => 100.0,
        Team.Chaos => 200.0,
        _ => 300.0,
    };

    private static Team ScriptTeamIdToTeam(double teamId) => teamId switch
    {
        100.0 => Team.Order,
        200.0 => Team.Chaos,
        _ => throw new ArgumentOutOfRangeException(nameof(teamId)),
    };

    /// <summary>
    /// <c>GetTurret(team, lane, tower)</c>'s turret: the map's own <c>CreateChildTurret</c> row with that team, tower id and lane (H7: on
    /// Map4, HQ_TOWER2 is tower id 4 on <c>Turret_T?_C_01</c>; on Summoner's Rift the rows give the names the old Map1 table did). Tower ids
    /// are the script's <c>BACK_TOWER</c>/<c>MIDDLE_TOWER</c>/<c>FRONT_TOWER</c>/<c>HQ_TOWER2</c>/<c>HQ_TOWER1</c> = 1..5; a tower the map
    /// does not create answers the <c>Nil</c> sentinel. <c>Reconstruction:</c> the two nexus towers are answered for every lane (the rows put
    /// them on one lane: centre on Summoner's Rift, right on Map4), inert because every script path that names them treats them alike. The
    /// rows are this host's own once <see cref="OnPostLevelLoad"/> ran CreateLevelProps, else its map's (<see cref="MapScriptData"/>).
    /// </summary>
    private string? TurretName(Team team, int laneId, int towerId) =>
        TurretName(_levelTurrets.Count > 0 ? _levelTurrets : MapScriptData.TurretsFor(_catalog.Paths), team, laneId, towerId);

    internal static string? TurretName(IReadOnlyList<LevelTurret> rows, Team team, int laneId, int towerId)
    {
        if (towerId is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(towerId));
        var hq = towerId is 4 or 5;
        if (!hq) LaneIdToString(laneId);
        return rows.FirstOrDefault(row => row.Team == team && row.TowerId == towerId && (hq || row.LaneId == laneId))?.Name;
    }

    private static string InhibitorName(Team team, string lane) =>
        (team == Team.Order ? "Barracks_T1_" : "Barracks_T2_") + lane + "1";

    private static string LaneOfInhibitor(string name)
    {
        foreach (var lane in MapDefinitions.Lanes)
            if (name.EndsWith(lane + "1", StringComparison.Ordinal)) return lane;
        throw new ArgumentException($"'{name}' is not an inhibitor of the match's map.", nameof(name));
    }

    // The match map's lanes (batch 556: Map4's are L and R, its script's LEFT_LANE 2 and RIGHT_LANE 0, so no centre wave is asked for).
    private static void ValidateLane(string lane)
    {
        if (!MapDefinitions.Lanes.Contains(lane, StringComparer.Ordinal))
            throw new ArgumentException($"The match's map has no lane '{lane}'.", nameof(lane));
    }

    private static int Count(LuaTable table, string name) =>
        (int)Math.Round(table.Get(LuaValue.Of(name)).ToNumber() ?? 0.0);

    /// <summary>
    /// The bonus table one barracks keeps. <c>OrderBarracksBonuses</c>/<c>ChaosBarracksBonuses</c>
    /// are <c>{OrderBarrack0, OrderBarrack1, OrderBarrack2}</c> and Lua indexes them at
    /// <c>barrackId + 1</c> (LevelScript.txt proto 0.6 pc0028-0034), so lane "R" is
    /// <c>Barrack0</c>.
    /// </summary>
    private LuaTable BarracksTable(Team team, string lane)
    {
        var name = (team == Team.Order ? "OrderBarrack" : "ChaosBarrack") + (int)ScriptLaneId(lane);
        return _state.TryGetGlobal(name, out var value) && value.Type == LuaType.Table
            ? value.AsTable
            : throw new InvalidOperationException($"LevelScript defines no {name} table.");
    }

    private static LevelEconomyRow RowOf(LuaTable table, string prefix) => new(
        Number(table, prefix + "MinionArmor"),
        Number(table, prefix + "MinionMagicResistance"),
        Number(table, prefix + "HPBonus"),
        Number(table, prefix + "DamageBonus"),
        Number(table, prefix + "ExpGiven"),
        Number(table, prefix + "GoldGiven"));

    private static double Number(LuaTable table, string name) =>
        table.Get(LuaValue.Of(name)).ToNumber() ?? 0.0;

    private static string Text(LuaTable table, string name)
    {
        var value = table.Get(LuaValue.Of(name));
        return value.Type == LuaType.String ? value.AsString : "";
    }

    /// <summary>One named table global, or an empty table when the script has no such name.</summary>
    private LuaTable TableOf(string name) =>
        _state.TryGetGlobal(name, out var value) && value.Type == LuaType.Table
            ? value.AsTable
            : new LuaTable();

    /// <summary>One 1-based array slot of a Lua table, as a table.</summary>
    private static LuaTable ArrayOf(LuaTable table, int index)
    {
        var value = table.Get(LuaValue.Of((double)index));
        return value.Type == LuaType.Table ? value.AsTable : new LuaTable();
    }

    /// <summary>One named field of a Lua table, as a table.</summary>
    private static LuaTable ArrayOf(LuaTable table, string name)
    {
        var value = table.Get(LuaValue.Of(name));
        return value.Type == LuaType.Table ? value.AsTable : new LuaTable();
    }

    /// <summary>The string values of a Lua array, in order.</summary>
    private static IReadOnlyList<string> Strings(LuaTable table)
    {
        var values = new List<string>(table.Length);
        for (var index = 1; index <= table.Length; index++)
            values.Add(Text(table, index));
        return values;
    }

    /// <summary>Every group of a camp in the script's order: <c>Groups[g]</c> is group g's member names.</summary>
    private static IReadOnlyList<IReadOnlyList<string>> GroupsOf(LuaTable camp)
    {
        var groups = ArrayOf(camp, "Groups");
        var values = new List<IReadOnlyList<string>>(groups.Length);
        for (var index = 1; index <= groups.Length; index++)
            values.Add(Strings(ArrayOf(groups, index)));
        return values;
    }

    /// <summary>
    /// A camp's respawn after it empties: <c>GroupsRespawnTime</c> where the map names it (Map1, Map2), else <c>RespawnTime</c>
    /// (Map4; its NeutralMinionDeath arms luaNeutralInitTimer with that field, NeutralMinionSpawn proto 0.4 pc 0090-0098).
    /// </summary>
    private static double RespawnOf(LuaTable camp) =>
        camp.Get(LuaValue.Of("GroupsRespawnTime")).ToNumber() ?? Number(camp, "RespawnTime");

    /// <summary>The <c>Make3DPoint</c> values of a Lua array, in order.</summary>
    private static IReadOnlyList<Position3> Points(LuaTable table)
    {
        var values = new List<Position3>(table.Length);
        for (var index = 1; index <= table.Length; index++)
            values.Add(Point(table, index));
        return values;
    }

    private static string Text(LuaTable table, int index)
    {
        var value = table.Get(LuaValue.Of((double)index));
        return value.Type == LuaType.String ? value.AsString : "";
    }

    private static Position3 Point(LuaTable table, int index)
    {
        var value = table.Get(LuaValue.Of((double)index));
        return value.Type == LuaType.Table ? PointOf(value.AsTable) : new Position3(0.0, 0.0, 0.0);
    }

    /// <summary>One <c>Make3DPoint</c> / <c>GetPosition</c> vector, as the script holds it.</summary>
    private static Position3 PointOf(LuaTable table) =>
        new(Number(table, "x"), Number(table, "y"), Number(table, "z"));

    private static Position3 PointOf(LuaValue value) =>
        value.Type == LuaType.Table ? PointOf(value.AsTable) : new Position3(0.0, 0.0, 0.0);

    private static LuaValue PointValue(Position3 point)
    {
        var table = new LuaTable();
        table.Set(LuaValue.Of("x"), LuaValue.Of(point.X));
        table.Set(LuaValue.Of("y"), LuaValue.Of(point.Y));
        table.Set(LuaValue.Of("z"), LuaValue.Of(point.Z));
        return LuaValue.Of(table);
    }

    private static double Number(LuaValue[] args, int index) =>
        index < args.Length ? args[index].ToNumber() ?? 0.0 : 0.0;

    private static string Text(LuaValue[] args, int index) =>
        index < args.Length && args[index].Type == LuaType.String ? args[index].AsString : "";
}
