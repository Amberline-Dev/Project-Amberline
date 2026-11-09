using Amberline.Core;
using Amberline.Scripting.Lua;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Simulation.Bots;

// The per-champion cast template the shipped files do not all carry, and the aim of a cast.
public sealed partial class BotController
{
    // ---- the per-champion data the shipped files do not carry ----

    /// <summary>
    /// NOT RETAIL, owner decision 22 October 2026: the per-champion bot data a champion's shipped
    /// files do not carry, written onto the module table <c>LoadScript</c> has just produced. Every
    /// roster champion is a bot and the shipped Bot/Task scripts drive it, so what its own
    /// <c>Characters/&lt;Name&gt;/Scripts</c> folder does not ship is reconstructed here rather
    /// than a C# port being kept as a fallback. Only <c>TaskCastSpell</c> is ever missing: the
    /// other seven modules <c>TimerHackDelayedInit</c> loads are shared and complete.
    /// Retail: a champion with no own <c>TaskCastSpell.luaobj</c> runs the shared
    /// <c>DATA/Scripts/TaskCastSpell</c>, whose <c>BeginTask</c> is a bare RETURN
    /// (proto 0.1) and whose <c>Tick</c> draws
    /// one slot and casts nothing (proto 0.2) — that bot never casts at all.
    /// The reconstruction is Annie's own template with its chat lines dropped (the server never
    /// sends them): the bytecode listing of Annie's TaskCastSpell (proto 0.1) is the loop, and
    /// the same shape is what Sivir, Soraka, Lux, Sona and Tristana ship (their own research-*.txt
    /// files: same loop bound, same empty <c>Tick</c> proto 0.2). The shipped half of the module
    /// (the <c>UpdatePriority</c> the two agree on, the empty <c>OnLevelUp</c>/<c>OnTargetLost</c>)
    /// is left as it is.
    /// DIAGNOSTICS: the loop's slot draw is the seat's <c>IRandomSource</c>, not the template's
    /// <c>math.random(0, 3)</c>, so a check can pin the sequence — the Lua VM's <c>math.random</c>
    /// is one static generator with no seam to inject into.
    /// </summary>
    internal void NativeReconstructModuleData(Entity hero, string name, LuaTable module)
    {
        if (!IsCastTemplate(name) || hero.UnitType != UnitType.Hero || hero.ChampionName.Length == 0)
            return;
        if (HasOwnCastTemplate(hero.ChampionName)) return;
        module.Set(LuaValue.Of("BeginTask"), LuaValue.Of(new LuaNative("BeginTask",
            (_, _) => ReconstructedBeginTask(hero))));
        module.Set(LuaValue.Of("Tick"), LuaValue.Of(new LuaNative("Tick",
            (_, _) => Array.Empty<LuaValue>())));
    }

    /// <summary>Whether a <c>LoadScript</c> name is the cast template's (<c>TaskCastSpell.lua</c>).</summary>
    private static bool IsCastTemplate(string name)
    {
        var normalized = name.Replace('\\', '/');
        var leaf = normalized[(normalized.LastIndexOf('/') + 1)..];
        if (leaf.EndsWith(".luaobj", StringComparison.OrdinalIgnoreCase)) leaf = leaf[..^7];
        else if (leaf.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) leaf = leaf[..^4];
        return leaf.Equals("TaskCastSpell", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The reconstructed <c>BeginTask</c>: the champion template's cast loop
    /// (research-Annie-TaskCastSpell.txt proto 0.1, the <c>Say</c> lines dropped). Up to
    /// <see cref="BotScript.CastAttempts"/> times draw a slot 0..3; the first slot whose
    /// <c>CanUseSpell</c> answers 0 gets <c>ServerCastSpell(slot)</c> and the task ends, whether or
    /// not that cast came out. <c>CanUseSpell</c> is the spellbook's can-use check, which has no range or
    /// target test.
    /// </summary>
    private LuaValue[] ReconstructedBeginTask(Entity hero)
    {
        for (var attempt = 0; attempt < BotScript.CastAttempts; attempt++)
        {
            var slot = _random.NextIndex(BotScript.CastSlotCount);
            if (NativeCanUseSpell(hero, slot) != 0.0) continue;
            NativeServerCastSpell(hero, slot);
            break;
        }
        return Array.Empty<LuaValue>();
    }

    /// <summary>Diagnostics (<see cref="BotMeasure"/>) and the order count of one accepted cast order.</summary>
    private static void CastOrdered(Entity hero, BotState state, int slot)
    {
        SampleSuperseded(hero, state);
        state.OrdersIssued++;
        state.Measure.Casts++;
        state.Measure.CastOrdered[slot]++;
        if (hero.PendingCast is null) return;
        state.Measure.CastQueued[slot]++;
        state.QueuedPending = hero.PendingCast;
        state.QueuedSlot = slot;
        state.QueuedCastsBefore = hero.MatchStatistics.SpellCasts[slot];
    }

    /// <summary>Diagnostics only: a queued cast still waiting when TaskCastSpell issues the next order was replaced by it.</summary>
    private static void SampleSuperseded(Entity hero, BotState state)
    {
        if (state.QueuedSlot < 0 || ReferenceEquals(hero.PendingCast, state.QueuedPending)) return;
        state.Measure.CastQueuedSuperseded[state.QueuedSlot]++;
        state.QueuedSlot = -1;
        state.QueuedPending = null;
    }

    /// <summary>Diagnostics only: how a cast order queued behind a range walk ended (cast, or dropped while which task was active).</summary>
    private static void SampleQueuedCast(Entity hero, BotState state)
    {
        if (state.QueuedSlot < 0 || ReferenceEquals(hero.PendingCast, state.QueuedPending)) return;
        var measure = state.Measure;
        var slot = state.QueuedSlot;
        state.QueuedSlot = -1;
        state.QueuedPending = null;
        if (hero.MatchStatistics.SpellCasts[slot] > state.QueuedCastsBefore) { measure.CastQueuedDone[slot]++; return; }
        measure.CastQueuedDropped[slot]++;
        var task = state.Active is { } active ? active.Kind.ToString() : "None";
        measure.CastDroppedWhile[task] = measure.CastDroppedWhile.GetValueOrDefault(task) + 1;
    }

    private enum CastMode
    {
        /// <summary>A retail bot with its own TaskCastSpell.luaobj (Annie, Sivir, Soraka): the shipped template and the plain aim.</summary>
        ChampionScript,
        /// <summary>A champion with no retail bot: the reconstructed template and the plain aim (NOT RETAIL, owner decision 22 October 2026).</summary>
        Template,
        /// <summary>A retail bot with no champion script (15 of the 18): the reconstructed template with the spell-data aim (NOT RETAIL, owner decision 18 October 2026).</summary>
        SpellData,
    }

    /// <summary>
    /// Which cast aim a champion's bot uses. A retail bot (the client ships game_bot_&lt;Name&gt;) has its own template only if its
    /// Characters/&lt;Name&gt;/Scripts folder ships a TaskCastSpell.luaobj; LoadTask('TaskCastSpell.lua') reaches the shared
    /// script otherwise (the lookup order is inferred from that file layout; LoadTask is a server native).
    /// </summary>
    private CastMode CastModeOf(string champion)
    {
        if (_castMode.TryGetValue(champion, out var mode)) return mode;
        mode = !_data.ClientNamesBot(champion) ? CastMode.Template
            : HasOwnCastTemplate(champion) ? CastMode.ChampionScript : CastMode.SpellData;
        _castMode[champion] = mode;
        return mode;
    }

    /// <summary>Whether the champion ships its own cast template, the one thing a bot's script reads per champion.</summary>
    private bool HasOwnCastTemplate(string champion) =>
        champion.Length > 0 &&
        File.Exists(_data.Paths.Data($"Characters/{champion}/Scripts/TaskCastSpell.luaobj"));

    /// <summary>NOT RETAIL, owner decision 18 October 2026: whether a champion's bot casts by spell data (a retail bot without a champion script).</summary>
    internal bool CastsBySpellData(string champion) => CastModeOf(champion) == CastMode.SpellData;

    /// <summary>
    /// NOT RETAIL, owner decision 18 October 2026 (retail: the generic TaskCastSpell casts nothing). The request a bot makes
    /// for a slot of a retail bot without a champion script, or null when the spell has no sensible aim right now (the slot is then
    /// skipped). Targeted spells go to a unit the spell's flags admit (an enemy, else a hurt ally or the bot), self spells to the
    /// bot, self-area spells only with an enemy inside their radius, ground spells and skillshots at the target (led when a
    /// missile flies), cones from the bot toward a target inside their length.
    /// </summary>
    private SpellCastRequest? AimBySpellData(Entity hero, int slot, Entity target)
    {
        var spell = _spells.SpellDataOf(hero, slot);
        var rank = Math.Max(hero.SpellRanks[slot], 1);
        var flags = BotCast.FlagNames(spell);
        var range = spell.CastRangeAt(rank);
        switch (BotCast.Classify(spell, flags))
        {
            case BotAim.Self:
                return OnUnit(slot, hero);
            case BotAim.SelfArea:
                return Distance(hero.Position, target.Position) <= BotCast.AreaRadius(spell, rank) ? OnUnit(slot, hero) : null;
            case BotAim.EnemyUnit:
                return PickEnemy(hero, target, flags, range) is { } enemy ? OnUnit(slot, enemy) : null;
            case BotAim.AllyUnit:
                return PickAlly(hero, flags, range) is { } ally ? OnUnit(slot, ally) : null;
            case BotAim.EnemyOrAllyUnit:
                if (PickEnemy(hero, target, flags, range) is { } either) return OnUnit(slot, either);
                return PickAlly(hero, flags, range) is { } friend ? OnUnit(slot, friend) : null;
            case BotAim.EnemyPoint:
                return PickEnemy(hero, target, flags, range) is { } victim
                    ? OnPoint(slot, BotCast.LeadsTarget(spell) ? Predict(hero, victim, spell.MissileSpeed) : victim.Position)
                    : null;
            case BotAim.AllyPoint:
                return OnPoint(slot, hero.Position);
            case BotAim.Cone:
                var length = BotCast.ConeLength(spell, rank);
                if (PickEnemy(hero, target, flags, length) is not { } inCone ||
                    Distance(hero.Position, inCone.Position) > length)
                    return null;
                var origin = new Position3(hero.Position.X, 0.0, hero.Position.Z);
                var aimed = new Position3(inCone.Position.X, 0.0, inCone.Position.Z);
                return new SpellCastRequest(slot, false, origin, aimed, 0u);
            default:
                return null;
        }
    }

    private static SpellCastRequest OnUnit(int slot, Entity unit)
    {
        var point = new Position3(unit.Position.X, 0.0, unit.Position.Z);
        return new SpellCastRequest(slot, false, point, point, unit.NetId);
    }

    private static SpellCastRequest OnPoint(int slot, Position2 point)
    {
        var position = new Position3(point.X, 0.0, point.Z);
        return new SpellCastRequest(slot, false, position, position, 0u);
    }

    /// <summary>The bot's current target when the spell's flags admit it, else the nearest admitted visible enemy within reach.</summary>
    private Entity? PickEnemy(Entity hero, Entity target, HashSet<string> flags, double reach)
    {
        if (target.Team != hero.Team && IsVisibleTarget(hero, target) &&
            BbSpellTargeting.RejectTarget(hero, target, flags) is null)
            return target;
        Entity? best = null;
        var bestDistance = reach > 0.0 ? reach * reach : double.PositiveInfinity;
        foreach (var other in _world.Entities.Values)
        {
            if (other.Team == hero.Team || other.Team == Team.Neutral || !other.Alive ||
                BbSpellTargeting.RejectTarget(hero, other, flags) is not null || !IsVisibleTarget(hero, other))
                continue;
            var distance = DistanceSquared(hero.Position, other.Position);
            if (distance >= bestDistance) continue;
            best = other;
            bestDistance = distance;
        }
        return best;
    }

    /// <summary>The allied hero within reach with the lowest health fraction, the bot included (unless the spell excludes self).</summary>
    private Entity? PickAlly(Entity hero, HashSet<string> flags, double reach)
    {
        Entity? best = null;
        var bestFraction = double.PositiveInfinity;
        foreach (var other in _world.Entities.Values)
        {
            if (other.UnitType != UnitType.Hero || other.Team != hero.Team || !other.Alive ||
                BbSpellTargeting.RejectTarget(hero, other, flags) is not null)
                continue;
            if (!ReferenceEquals(other, hero) && reach > 0.0 &&
                DistanceSquared(hero.Position, other.Position) > reach * reach)
                continue;
            var fraction = other.MaxHealth > 0.0 ? other.Health / other.MaxHealth : 1.0;
            if (fraction >= bestFraction) continue;
            best = other;
            bestFraction = fraction;
        }
        return best;
    }

    /// <summary>Where a missile of the given speed meets a target that keeps walking its route.</summary>
    private static Position2 Predict(Entity hero, Entity target, double missileSpeed)
    {
        if (target.Waypoints.Count == 0 || target.Speed <= 0.0) return target.Position;
        var next = target.Waypoints[0];
        var remaining = Distance(target.Position, next);
        if (remaining <= 1.0) return target.Position;
        var travel = Math.Min(target.Speed * Distance(hero.Position, target.Position) / missileSpeed, remaining);
        var scale = travel / remaining;
        return new Position2(target.Position.X + (next.X - target.Position.X) * scale,
            target.Position.Z + (next.Z - target.Position.Z) * scale);
    }

    /// <summary>
    /// Casts an aimed request through the player's cast path. A range failure is queued as an approach-and-cast by the request
    /// handler (as for an out-of-range click); any other refusal means the slot cannot be cast at this aim.
    /// </summary>
    private bool CastAimed(Entity hero, SpellCastRequest request, double now)
    {
        var validation = _spells.CanCast(hero, request.Slot, request.TargetNetId, request.Position, now);
        if (!validation.Allowed && validation.Failure != SpellCastFailure.Range) return false;
        return _spells.HandleCastRequest(hero, request, now);
    }
}
