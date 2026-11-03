using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The live missiles of one script engine by net id: what <c>BBDestroyMissile</c> and <c>BBGetMissilePosFromID</c> look up. The stock
/// client keeps the same thing in its missile manager (the missile manager's search,
/// the same search): both natives read an integer id, find the missile object and act on it, and an id the manager does not hold does
/// nothing. A missile is added when it leaves its caster and removed when its end hooks have run, so a destroyed or finished missile is
/// gone for the next call.
/// </summary>
internal sealed class BbMissileRegistry
{
    private readonly Dictionary<uint, BbMissileFlight> _live = new();

    public int Count => _live.Count;

    public void Add(BbMissileFlight flight)
    {
        if (flight.MissileNetId != 0) _live[flight.MissileNetId] = flight;
    }

    public void Remove(BbMissileFlight flight)
    {
        if (_live.TryGetValue(flight.MissileNetId, out var current) && ReferenceEquals(current, flight)) _live.Remove(flight.MissileNetId);
    }

    public BbMissileFlight? Find(uint missileNetId) => _live.TryGetValue(missileNetId, out var flight) ? flight : null;

    /// <summary>The live missiles, for the checks that follow a script's missile in flight (no behaviour: a snapshot of the registry).</summary>
    internal IReadOnlyList<BbMissileFlight> Live => _live.Values.ToArray();
}

/// <summary>
/// The script-visible life of one missile a script spell launched (a homing or chain <see cref="BbSpellProjectile"/>, a
/// <see cref="BbLineMissile"/>): it publishes the missile to the scripts and runs the missile hooks around the flight. The projectile
/// classes keep moving the missile and resolving its hits; this object is what they tell about it.
/// <list type="bullet">
/// <item><see cref="Launch"/> registers the missile and runs <c>BuffOnLaunchMissile</c> (scope <c>missileId</c>, <c>Target</c> = the unit a
/// homing missile flies at) on the script buffs of the caster.</item>
/// <item><see cref="Move"/> runs <c>SpellOnMissileUpdate</c> (scope <c>MissilePosition</c>) once for every <c>LuaOnMissileUpdateDistanceInterval</c>
/// units the missile flew.</item>
/// <item><see cref="Destroy"/> is <c>BBDestroyMissile</c>: the missile is flagged, the client is told once, and its owner ends it at its next step.</item>
/// <item><see cref="End"/> runs <c>SpellOnMissileEnd</c> and <c>BuffOnMissileEnd</c> (scope <c>MissileEndPosition</c>, <c>SpellName</c>) once, for a
/// missile that reached its end, hit its target, was destroyed by a script or was taken out with its target.</item>
/// </list>
/// Evidence: the client's flag (set by its setter, read by the missile and line update routines, which both
/// return 0 at once when it is set, the manager's "this missile is over"),
/// the loader reading <c>LuaOnMissileUpdateDistanceInterval</c>, and the
/// callbacks (the launch and missile-end handlers, which
/// are empty stubs on the client, so the dispatch is server-only: Reconstruction below).
/// </summary>
internal sealed class BbMissileFlight
{
    private readonly BbSpellHost _host;
    private readonly double _interval;
    private double _sinceUpdate;

    public BbMissileFlight(BbSpellHost host, BbScript script, string spellName, Entity caster, int slot, int rank, double manaCost,
        SpellCastInfo castInfo, SpellData data, uint targetNetId = 0)
    {
        _host = host;
        Script = script;
        SpellName = spellName;
        Caster = caster;
        Slot = slot;
        Rank = rank;
        ManaCost = manaCost;
        MissileNetId = castInfo.MissileNetId;
        TargetNetId = targetNetId;
        CastPosition = castInfo.TargetPosition;
        CastEndPosition = castInfo.TargetPositionEnd;
        _interval = UpdateInterval(data);
    }

    public BbScript Script { get; }
    /// <summary>The name the scripts compare with <c>SpellName</c>: the spell inibin's name of the cast that made the missile.</summary>
    public string SpellName { get; }
    public Entity Caster { get; }
    public int Slot { get; }
    public int Rank { get; }
    public double ManaCost { get; }
    public uint MissileNetId { get; }
    /// <summary>The unit a homing missile flies at (0 for a line missile); the targeted-flight retirement matches on it.</summary>
    public uint TargetNetId { get; }
    public Position3 CastPosition { get; }
    public Position3 CastEndPosition { get; }
    public Position2 Position { get; private set; }
    public bool Launched { get; private set; }
    /// <summary>A script destroyed the missile (<c>BBDestroyMissile</c>): its owner ends it instead of flying on.</summary>
    public bool Destroyed { get; private set; }
    public bool Ended { get; private set; }

    /// <summary>
    /// <c>LuaOnMissileUpdateDistanceInterval</c>: the distance in game units between two <c>SpellOnMissileUpdate</c> runs
    /// (Rumble's Equalizer 180, Kog'Maw's Void Ooze 125, Nocturne's Duskbringer 200, Galio's Righteous Gust 175, Orianna's Dissonance 100).
    /// A spell without the key reads 0 and gets no update run: Reconstruction, the five shipped users all carry an interval.
    /// </summary>
    internal static double UpdateInterval(SpellData data)
    {
        var interval = ChampionSpellSupport.RawNumber(data, "SpellData*LuaOnMissileUpdateDistanceInterval");
        return interval > 1e-6 ? interval : 0.0;
    }

    /// <summary>
    /// The missile leaves its caster. It is registered before the hook runs (Orianna's <c>BuffOnLaunchMissile</c> reads the missile's
    /// position with <c>BBGetMissilePosFromID</c> at once). Reconstruction: the dispatch is server-only; the moment is the cast frame, when
    /// the client builds the missile object (the cast routine).
    /// </summary>
    public void Launch(Position2 at, Entity? target)
    {
        if (Launched || Ended || MissileNetId == 0) return;
        Launched = true;
        Position = at;
        _host.Router.Missiles.Add(this);
        _host.Router.RunLaunchMissile(this, target);
    }

    /// <summary>
    /// The missile flew to <paramref name="to"/>. <c>SpellOnMissileUpdate</c> runs for each full <c>LuaOnMissileUpdateDistanceInterval</c>
    /// flown since the last run, at the point of the path where that distance was reached. Reconstruction: when the first run happens (here
    /// after the first interval, not at the launch) and whether the distance is counted along the path are server-only.
    /// </summary>
    public void Move(Position2 to, Entity? target = null)
    {
        var from = Position;
        Position = to;
        if (_interval <= 0.0 || !Launched || Ended || Destroyed) return;
        var length = ChampionSpellSupport.Distance(from, to);
        var flown = 0.0;
        while (!Destroyed && _sinceUpdate + (length - flown) >= _interval - 1e-9)
        {
            flown += _interval - _sinceUpdate;
            _sinceUpdate = 0.0;
            var fraction = length <= 1e-9 ? 1.0 : Math.Min(1.0, flown / length);
            var here = new Position2(from.X + (to.X - from.X) * fraction, from.Z + (to.Z - from.Z) * fraction);
            _host.Router.RunMissileUpdate(this, here, target);
        }
        if (!Destroyed) _sinceUpdate = Math.Max(0.0, _sinceUpdate + length - flown);
    }

    /// <summary>
    /// <c>BBDestroyMissile</c>: flags a launched missile, once. The client is told now (<c>DestroyClientMissile</c>); the projectile that
    /// owns the flight sees <see cref="Destroyed"/> at its next step (or straight after the hit that called this) and ends it, which is the
    /// stock manager's order (the flag, then the next <c>UpdateProjectile</c> returns 0). Returns false for a missile that was destroyed or
    /// ended already.
    /// </summary>
    public bool Destroy()
    {
        if (!Launched || Ended || Destroyed) return false;
        Destroyed = true;
        _host.Spells.Effects.SendDestroyMissile(MissileNetId);
        return true;
    }

    /// <summary>
    /// The flight is over: the missile reached the end of its path, its target, or was destroyed. The end hooks run once, with the missile
    /// already gone from the registry (a script that destroys its own ended missile in <c>SpellOnMissileEnd</c> finds nothing, as in
    /// <c>OrianaIzuna</c> and <c>OrianaRedact</c>). Reconstruction: the dispatch is server-only; a destroyed missile ends through the same
    /// path as a finished one because the client's manager removes both through one return value, and because <c>OrianaRedact</c>'s
    /// recovery branch (<c>Found == false</c>: no unit carries the ball) is only reachable after the missile was destroyed or lost its target.
    /// </summary>
    public void End(Position2 at, Entity? target)
    {
        if (Ended) return;
        Ended = true;
        Position = at;
        _host.Router.Missiles.Remove(this);
        if (Launched) _host.Router.RunMissileEnd(this, target);
    }
}
