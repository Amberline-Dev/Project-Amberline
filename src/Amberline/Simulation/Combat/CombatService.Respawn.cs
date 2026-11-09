using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Stats;

namespace Amberline.Simulation.Combat;

public sealed partial class CombatService
{
    /// <summary>
    /// Batch 447: a team's spawn point on this map (MapDefinitions.TeamSpawn: Summoner's Rift's or the Crystal Scar's own). Any
    /// team that is not Order gets the Chaos spawn, as before.
    /// </summary>
    private static Position2 FountainOf(Team team) => MapDefinitions.TeamSpawn(team == Team.Order ? Team.Order : Team.Chaos);

    /// <summary>
    /// Called on the inhibitor edge, with the barracks and its new state
    /// (<c>true</c> = destroyed). Map1's shipped LevelScript answers the wave
    /// consequence in ApplyBarracksDestructionBonuses / ApplyBarracksRespawnReductions.
    /// </summary>
    public Action<Entity, bool>? BarracksChanged { get; set; }

    /// <summary>Schedules the normal hero respawn after lethal damage.</summary>
    public void ScheduleRespawn(Entity unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.UnitType != UnitType.Hero || unit.Alive || unit.IsTrainingDummy)
            return;
        unit.RespawnAt = _time + RespawnDuration(unit);
    }

    /// <summary>
    /// Ends Annie's Tibbers and Yorick's ghouls (Omen of War, Pestilence and Famine) at their owner's death; the same
    /// <see cref="Kill"/> path as any pet death sends their death packet, effects and buff removals, and no one is credited.
    /// <para>
    /// Tibbers, retail: InfernalGuardianBurning tests on every update whether its Attacker, Annie, is dead and removes itself, and
    /// its OnBuffDeactivate then deals 10000 InternalRaw damage to Tibbers; the LoL wiki Annie patch history (V0.8.22.115) says
    /// "Tibbers now dies after 60 seconds or when Annie dies", unchanged until V6.9.
    /// </para>
    /// <para>
    /// Yorick's ghouls, retail: they die with Yorick (owner research 18 Oct 2026). This replaces the earlier 18 October rule that
    /// let them live out their decay.
    /// </para>
    /// <para>
    /// Owner decision, 18 October 2026 (owner's information): no other pet dies with its owner. Shaco's Hallucinate clone and the
    /// Omen of Death revenant live out their own duration (the clone's 18 s, the revenant's 10 s) and keep running their Pet.lua AI,
    /// which never tests the owner's life; only a nil owner kills a pet there. Reconstruction: for the revenant the web research of
    /// 18 October 2026 found no 2011 source tying it to Yorick's death and the scripts have no death link; the clone rests on the
    /// owner's information, consistent with the Shaco patch history. Nothing else here ties a pet to its owner: Jack in the Box,
    /// Nidalee's Bushwhack traps (a 240 s lifetime and no owner test) and wards never died with the owner either.
    /// </para>
    /// </summary>
    public int DespawnPetsOf(uint ownerNetId)
    {
        var pets = _world.Entities.Values
            .Where(candidate => candidate.OwnerNetId == ownerNetId &&
                                candidate.PetState is { Kind: Pets.CloneKind.Tibbers or Pets.CloneKind.YorickGhoul } &&
                                candidate.Alive)
            .ToArray();
        foreach (var pet in pets) Kill(pet, 0);
        return pets.Length;
    }

    /// <summary>
    /// The barracks' countdown to its dampener's return, armed by the map script's <c>SetDisableMinionSpawn</c> (the barracks record holds the countdown
    /// seconds and a flag). The shared obj_Barracks update counts it down and at zero sets the linked
    /// dampener's state to 1 unless it already is; <see cref="ProcessRespawns"/> is that update's zero test here.
    /// </summary>
    public void ArmInhibitorReturn(Entity inhibitor, double seconds)
    {
        ArgumentNullException.ThrowIfNull(inhibitor);
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        inhibitor.RespawnAt = _time + seconds;
    }

    public int ProcessRespawns()
    {
        // End-game ceremony/result packets continue through CombatTick, but a
        // death whose timer expires during that ceremony must not re-enter the
        // match after the Nexus has already ended it.
        if (_gameOver) return 0;
        var respawned = 0;
        foreach (var unit in _world.Entities.Values.ToArray())
        {
            if (unit.Alive || unit.RespawnAt <= 0.0 || unit.RespawnAt > _time) continue;
            if (unit.UnitType == UnitType.Hero)
            {
                if (RespawnImmediately(unit)) respawned++;
                continue;
            }
            unit.Alive = true;
            unit.RespawnAt = 0.0;
            unit.Health = unit.MaxHealth;
            unit.Mana = unit.MaxMana;
            if (unit.Kind == EntityKind.Inhibitor)
            {
                BarracksChanged?.Invoke(unit, false);
                if (_transport.IsWireReady(unit)) _transport.SendInhibitorState(unit, false, 0.0);
            }
            _hooks.OnRespawn(unit);
            respawned++;
        }

        foreach (var unit in _world.Entities.Values.ToArray())
        {
            if (!unit.Alive && unit.DeathTime is double death && unit.RespawnAt <= 0.0 &&
                unit.UnitType is not UnitType.Turret and not UnitType.Structure &&
                _time - death >= CorpseReapSeconds)
                _world.Remove(unit.NetId);
        }
        RecomputeProtection();
        return respawned;
    }

    /// <summary>
    /// Applies the same hero state transition as ProcessRespawns without
    /// advancing the match clock or creating a kill/reward event.
    /// </summary>
    public bool RespawnImmediately(Entity unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.UnitType != UnitType.Hero || unit.Alive || unit.RespawnAt <= 0.0)
            return false;
        _deathRecapHistories.Remove(unit.NetId);
        _lastDeathRecaps.Remove(unit.NetId);
        unit.Alive = true;
        unit.RespawnAt = 0.0;
        unit.DeathTime = null;
        unit.MatchStatistics.RecordRespawn(_time);
        unit.Health = unit.MaxHealth;
        // The hero's reincarnation handler sets the pool to 0 for a Shield or Other character (rage, heat, flow, Mordekaiser's shield): a life
        // starts with an empty bar there, and with a full one for mana and energy.
        unit.Mana = _gameData.Champion(unit.StatCharacterName).PrimaryResource.StartsLifeEmpty() ? 0.0 : unit.MaxMana;
        // Owner verification, 18 October 2026: a retail video shows respawned champions appearing in the middle of the
        // fountain, so every seat respawns on its team's spawn point, not on a per-seat HeroSpawnOffsets slot (those
        // slots are the first spawn only, MapDefinitions.InitialHeroSpawn). The spawn point is the central point of the map's
        // Scene/__Spawn_T1.SCB and __Spawn_T2.SCB (MapLayout, read from the player's files since batch 595). Owner decision, 18
        // October 2026: the Order anchor is that exact point (MapDefinitions.HeroSpawn), no longer rounded to whole units.
        // The hero's respawn-location getter is a stub in the client (server only).
        unit.Position = unit.RespawnPoint ?? FountainOf(unit.Team);
        var stats = _heroStats ?? throw new InvalidOperationException(
            "hero respawns require the match HeroStatsService");
        stats.Refresh(unit);
        if (_transport.IsWireReady(unit))
        {
            _transport.SendRespawn(unit);
            _transport.SendHeroStats(unit);
        }
        _hooks.OnRespawn(unit);
        RecomputeProtection();
        return true;
    }

    private double RespawnDuration(Entity unit)
    {
        // Retail: stock respawn-time getter is
        //   max(0, (FlatTimeDead + FlatTimeDeadPerLevel * level + base) *
        //          (1 - (PercentTimeDead + ScriptPercentRespawn + level * PercentTimeDeadPerLevel)))
        // with base = the death timer manager's lookup (team, level) and the
        // mods applied only while DeathTimes' AllowDeathTimeMods (absent = on) holds.
        // The percent terms are the rune/mastery sum (rPercentTimeDeadMod, rune 5366
        // +0.05) and the script term (IncPercentRespawnTimeMod). Batch 159 part 3: that script term is
        // Spells/PositiveChampionDelta.luaobj's own row, summed into HeroStats.TimeDeadPercent with every other buff payload,
        // so the hand-written TeamSizeCatchUp.RespawnPercent reader that stood in for it is gone.
        // The flat terms (rFlatTimeDeadMod*) are 0 in every shipped item, rune and
        // talent (scanned), so they are not modelled. Only the result is floored at
        // 0: a negative sum lengthens the timer, a sum above 1 gives 0.
        // gcd_PercentRespawnTimeModMinimum (-0.95) is copied by the client and never applied.
        // Batch 454: AllowDeathTimeMods (map 8 ships it on, Map1 leaves it absent = on) gates the mods; the base and the scaling are the map's.
        // Batch 584: the base is the team's respawn wave (MapProgressionRules.DeathTime with the wave start; map 8 only). Called twice at one
        // death, at the same time, which gives the same value: the first call already moved the wave start.
        var reduction = _progression.AllowDeathTimeMods ? RespawnReduction(unit) : 0.0;
        var waveStart = _deathWaveStarts.GetValueOrDefault(unit.Team);
        var deathTime = _progression.DeathTime(unit.Level, _time, ref waveStart);
        _deathWaveStarts[unit.Team] = waveStart;
        return Math.Max(0.0, deathTime * (1.0 - reduction));
    }

    /// <summary>Each team's respawn-wave start (GlobalDeathTimerManager keeps one DeathTimerManager per team, +0x60).</summary>
    private readonly Dictionary<Team, double> _deathWaveStarts = new();

    private static double RespawnReduction(Entity hero) => hero.LoadoutRespawnTimePercent;
}
