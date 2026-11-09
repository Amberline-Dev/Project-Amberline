using Amberline.Core;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Buffs;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>One running channel of a script spell (a <c>ChannelDuration</c> global): who casts it, what it does and how long it has left.</summary>
internal sealed class BbChannel
{
    public required Entity Caster { get; init; }
    public required BbScript Script { get; init; }
    public required int Slot { get; init; }
    public required int Rank { get; init; }
    public required double ManaCost { get; init; }
    public required Position3 At { get; init; }
    public required Position3 End { get; init; }
    /// <summary>The unit the channel is aimed at.</summary>
    public Entity? Target { get; set; }
    public required double Remaining { get; set; }
    /// <summary>
    /// The channel's own <c>InstanceVars</c>, shared by all its hooks: Fiddlesticks' Drain stores the time of its last tick in
    /// <c>ChannelingStart</c> and the periodic block of <c>ChannelingUpdateActions</c> tracks it, the Odin capture channels keep their
    /// effect ids and charge flag there for <c>ChannelingSuccessStop</c>. Reconstruction (the exe has no table for it).
    /// </summary>
    public BbTable InstanceVars { get; } = new();
    /// <summary>The spell carries <c>CantCancelWhileChanneling</c>: the client refuses the plain stop of the player's own orders and casts.</summary>
    public bool Uncancellable { get; init; }
    /// <summary>The time the channel has run (the hook variable <c>AccumTime</c> of the Odin capture channels).</summary>
    public double Elapsed { get; set; }

    /// <summary>
    /// Reconstruction (batch 476): the unit's <see cref="Entity.AnimationSerial"/> after the channel's last looped animation play
    /// (<see cref="BbScriptChannels.NoteLoop"/>). The end releases the loop only while the serial still matches.
    /// </summary>
    public uint? LoopSerial { get; set; }
    /// <summary>The next point of the 0.25 s grid on which the update hooks run (<see cref="BbScriptChannels.Tick"/>); the first one is a quarter second after the start.</summary>
    public double ActionsNextAt { get; set; } = BbBuffRuntime.ActionsUpdateInterval;
    /// <summary>Where the caster stood when the channel began and at the last step; a different place is the stop source Move.</summary>
    public Position2 LastPosition { get; set; }
}

/// <summary>
/// The channelled spells of one champion's script engine (Phase 3, Janna's Monsoon). The spell script's <c>ChannelDuration</c> global (4 for
/// <c>ReapTheWhirlwind</c>, 8 for Recall, 2.2 for Bullet Time) is the length; its <c>ChannelingStart</c> hook runs when the cast frame has
/// passed, and the channel ends either when the time is up (<c>ChannelingStopCondition_Success</c>, source <c>TimeCompleted</c>: the hook
/// <c>ChannelingSuccessStop</c>) or when something stops it (condition <c>Cancel</c> with the source: the hook <c>ChannelingCancelStop</c>);
/// <c>ChannelingStop</c> runs in both. <c>ChannelingUpdateStats</c> and <c>ChannelingUpdateActions</c> run on the 0.25 s grid from the channel's start (Reconstruction), the
/// stats hook first, with <c>AccumTime</c> (the seconds the channel has run) as a hook variable. The stock client registers the two conditions, the eleven sources and the hook names
/// (its registration tables) and the native <c>BBStopChanneling</c> that a script uses to end its own channel
/// (<c>CasterVar</c>, <c>StopCondition</c>, <c>StopSource</c>); everything else here is a Reconstruction:
/// <list type="bullet">
/// <item>the sources: the caster's death (<c>Die</c>), a state that stops it from acting (a stun, silence, taunt, fear, sleep or suppression:
/// <c>StunnedOrSilencedOrTaunted</c>; a root or net does not), being moved off the spot (<c>Move</c>), a fresh order of the player (<c>Move</c>,
/// or <c>Attack</c> for an attack order) and an accepted cast (<c>Casting</c>);</item>
/// <item>the caster is not locked while it channels: the hand-written Monsoon was cancelled by the same events and let every other cast through.</item>
/// </list>
/// </summary>
internal sealed class BbScriptChannels
{
    private readonly BbSpellHost _host;
    private readonly Dictionary<Entity, BbChannel> _channels = new(ReferenceEqualityComparer.Instance);

    public BbScriptChannels(BbSpellHost host) => _host = host;

    /// <summary>The channels this engine is running (a check reads it).</summary>
    public IReadOnlyCollection<BbChannel> Active => _channels.Values;

    public BbChannel? Of(Entity caster) => _channels.TryGetValue(caster, out var channel) ? channel : null;

    /// <summary>
    /// Reconstruction (batch 476): a looped animation played for a casting unit (BBPlayAnimation Loop) is the channel's loop. The serial is
    /// recorded here, and the channel's end releases the loop while nothing has played or stopped the unit's animation since (<see cref="Stop"/>).
    /// </summary>
    public void NoteLoop(Entity caster)
    {
        if (_channels.TryGetValue(caster, out var channel)) channel.LoopSerial = caster.AnimationSerial;
    }

    private static readonly string[] Hooks =
    [
        "ChannelingStart", "ChannelingUpdateStats", "ChannelingUpdateActions", "ChannelingSuccessStop", "ChannelingCancelStop", "ChannelingStop",
    ];

    /// <summary>
    /// The length of a spell's channel, or 0 for a spell that does not channel: a script is a channel when it names a <c>ChannelDuration</c> and
    /// has a channel hook. Fiddlesticks' Drain (the slot spell: an instacast whose <c>TargetExecute</c> casts the channel <c>DrainChannel</c>), the
    /// Dominion <c>GateMarker</c> and Warwick's <c>InfiniteDuress_buff</c> carry the global with no hook, and the channel is their child's.
    /// </summary>
    public static double DurationOf(BbScript script) =>
        script.Global("ChannelDuration") is BbValue.Number { Value: > 0.0 } duration && Hooks.Any(hook => script.Event(hook) is not null)
            ? duration.Value : 0.0;

    /// <summary>
    /// The cast frame of a channelled spell has passed: the channel begins and its <c>ChannelingStart</c> hook runs. A hero channels one spell
    /// at a time, so a channel still running (a cast that did not go through the player's request, a child cast) ends first as a cast.
    /// </summary>
    public void Begin(BbScript script, Entity caster, int slot, int rank, double manaCost, Position3 at, Position3 end, Entity? target)
    {
        var duration = DurationOf(script);
        if (duration <= 0.0 || !caster.Alive) return;
        Stop(caster, BbChannelingStopCondition.ChannelingStopCondition_Cancel, BbChannelingStopSource.ChannelingStopSource_Casting);
        var channel = new BbChannel
        {
            Caster = caster, Script = script, Slot = slot, Rank = rank, ManaCost = manaCost, At = at, End = end, Target = target,
            Remaining = duration, LastPosition = caster.Position,
            Uncancellable = _host.Spells.Data.SpellHasUncancellableChannel(script.Name),
        };
        // Miss Fortune's Bullet Time faces "CastPosition" in ChannelingStart and copies InstanceVars.CastPosition in ChannelingUpdateActions, and
        // nothing in the script sets either: the cast point is a variable of the channel's hooks and of its InstanceVars. Reconstruction.
        channel.InstanceVars["CastPosition"] = BbTable.Point(at.X, at.Y, at.Z);
        // Reconstruction: the cast point is the channel's TargetPos too. Grand Skyfall's ChannelingSuccessStop casts its flight at
        // InstanceVars.TargetPos (and hands it to the flight's NextBuffVars), and no script of the champion writes that entry: its
        // SelfExecute keeps the point in CharVars.
        channel.InstanceVars["TargetPos"] = BbTable.Point(at.X, at.Y, at.Z);
        _channels[caster] = channel;
        caster.ActiveScriptChannels++;
        _host.Router.RunChanneling(channel, "ChannelingStart");
    }

    /// <summary>
    /// Once per spell step, after the movement phase: the time of every channel runs down, and a caster that died, can no longer act or was moved
    /// stops it. <c>ChannelingUpdateStats</c> and then <c>ChannelingUpdateActions</c> run on the 0.25 s grid from the channel's start (Reconstruction; Bullet Time ends itself
    /// from the actions; the Odin capture channels read <c>AccumTime</c> in both and end themselves with <c>BBStopChanneling</c> from the stats).
    /// </summary>
    public void Tick(double dt)
    {
        if (_channels.Count == 0 && _windups.Count == 0) return;
        TickChannels(dt);
        TickWindups(dt);
    }

    /// <summary>
    /// Batch 678: use casts (<c>BbSummonerModule.BeginUseChannel</c>) whose windup still runs: the script, the slot, the cast point, the unit
    /// used and the seconds left to the cast frame. The cast answer goes out at the use, and the client's SpellCaster starts the channel and
    /// its bar at the cast frame, <c>DesignerCastTime</c> later; so does this channel, as every channelled cast's <c>ChannelingStart</c>
    /// runs at its cast frame (<see cref="Begin"/>). The use channel used to begin at the use, a cast time ahead of the client's bar.
    /// </summary>
    private readonly Dictionary<Entity, (BbScript Script, int Slot, Position3 At, Entity Target, double Remaining)> _windups =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Batch 678: begins the channel <paramref name="windup"/> seconds from now, at the cast's frame (at once for none).</summary>
    public void BeginAfter(double windup, BbScript script, Entity caster, int slot, Position3 at, Entity target)
    {
        _windups.Remove(caster);
        if (windup > 0.0) _windups[caster] = (script, slot, at, target, windup);
        else Begin(script, caster, slot, 1, 0.0, at, at, target);
    }

    /// <summary>Batch 678: whether the caster's use cast of <paramref name="spellName"/> at <paramref name="target"/> is still in its windup.</summary>
    public bool WindsUp(Entity caster, Entity target, string spellName) =>
        _windups.TryGetValue(caster, out var pending) && ReferenceEquals(pending.Target, target) &&
        string.Equals(pending.Script.Name, spellName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Batch 678 (Reconstruction): a stop before the cast frame (a fresh order, an accepted cast, a script's <c>BBStopChanneling</c>) drops
    /// the cast: no channel began, so no channel hook runs. False when the caster winds none up.
    /// </summary>
    public bool CancelWindup(Entity caster) => _windups.Remove(caster);

    // A windup that reaches its cast frame inside this step begins its channel with the time the step ran past the frame, so the channel ends
    // ChannelDuration after the frame whatever the step size (Recall's rule, BbSummonerModule.Tick). Reconstruction: the caster's death, a state
    // that stops it from acting, a displacement or the used unit's death drops the cast, as they stop a channel.
    private void TickWindups(double dt)
    {
        foreach (var (caster, pending) in _windups.ToArray())
        {
            var state = CharacterState.Of(caster);
            if (!caster.Alive || !pending.Target.Alive || !state.CanCast || state.Taunted || caster.ForcedMove is not null)
            {
                _windups.Remove(caster);
                continue;
            }
            var remaining = pending.Remaining - dt;
            if (remaining > 1e-9)
            {
                _windups[caster] = pending with { Remaining = remaining };
                continue;
            }
            _windups.Remove(caster);
            Begin(pending.Script, caster, pending.Slot, 1, 0.0, pending.At, pending.At, pending.Target);
            if (Of(caster) is { } channel && ReferenceEquals(channel.Script, pending.Script))
            {
                channel.Remaining += remaining;
                channel.Elapsed -= remaining;
            }
        }
    }

    private void TickChannels(double dt)
    {
        if (_channels.Count == 0) return;
        foreach (var channel in _channels.Values.ToArray())
        {
            var caster = channel.Caster;
            if (!ReferenceEquals(Of(caster), channel)) continue;
            if (!caster.Alive) { Stop(caster, Cancel, BbChannelingStopSource.ChannelingStopSource_Die); continue; }
            // Batch 665: the death of the unit a channel is aimed at does not end the channel; the shipped scripts that end on it say so
            // themselves. DrainChannel's ChannelingUpdateActions and AlZaharNetherGrasp's buff stop with BBStopChanneling LostTarget on
            // "Target CO_IS_DEAD", InfiniteDuress_buff when its target loses Suppression, and OdinCaptureChannelBomb runs on past the centre
            // relic's death: its ChannelingSuccessStop sets the use cooldown 0 for a dead Target, and its ChannelingCancelStop's
            // OdinBombSuccessParticle branch (a 1 s buff given at the pickup) ends a channel the player cancels after the pickup without
            // the cancel effect. The engine stop added on 19 October (c55336e9e, a Reconstruction) cut that channel and its client bar at the pickup.
            var state = CharacterState.Of(caster);
            if (!state.CanCast || state.Taunted)
            {
                Stop(caster, Cancel, BbChannelingStopSource.ChannelingStopSource_StunnedOrSilencedOrTaunted);
                continue;
            }
            if (caster.ForcedMove is not null || BbSpellHost.Distance(caster.Position, channel.LastPosition) > 1e-4)
            {
                Stop(caster, Cancel, BbChannelingStopSource.ChannelingStopSource_Move);
                continue;
            }
            channel.Remaining -= dt;
            channel.Elapsed += dt;
            // Reconstruction (batch 216 follow-up): the update hooks run on the 0.25 s grid from the channel's start, the grid BuffOnUpdateActions
            // uses (BbBuffRuntime.ActionsUpdateInterval), not on every spell step (1/30 s). A due point runs once and the next one is a whole interval
            // on. Evidence: Bullet Time's eight waves are counted by ChannelingUpdateActions (Count >= 8 ends the 2.2 s channel), so they come a
            // quarter second apart; its tooltip says "for 2 seconds" and gives no rate. Drain's own gate is 0.5 s and stays.
            if (channel.Elapsed + 1e-9 >= channel.ActionsNextAt)
            {
                var missed = Math.Floor((channel.Elapsed - channel.ActionsNextAt) / BbBuffRuntime.ActionsUpdateInterval + 1e-9);
                channel.ActionsNextAt += Math.Max(1.0, missed + 1.0) * BbBuffRuntime.ActionsUpdateInterval;
                _host.Router.RunChanneling(channel, "ChannelingUpdateStats");
                if (!ReferenceEquals(Of(caster), channel)) continue;
                _host.Router.RunChanneling(channel, "ChannelingUpdateActions");
                if (!ReferenceEquals(Of(caster), channel)) continue;
            }
            if (channel.Remaining <= 1e-9)
                Stop(caster, BbChannelingStopCondition.ChannelingStopCondition_Success, BbChannelingStopSource.ChannelingStopSource_TimeCompleted);
        }
    }

    private const BbChannelingStopCondition Cancel = BbChannelingStopCondition.ChannelingStopCondition_Cancel;

    /// <summary>
    /// A fresh order or an accepted cast of the player (<paramref name="reason"/> "order" or "spell request") reaches the caster's channel. The client
    /// refuses the stop that order or cast ends in while the unit channels a spell with <c>CantCancelWhileChanneling</c> and can still cast
    /// (<c>IsCancelBlocked</c>), so the channel goes on; every other channel ends.
    /// </summary>
    public bool CancelByPlayer(Entity caster, string reason)
    {
        if (Of(caster) is not { } channel) return false;
        if (channel.Uncancellable)
        {
            var state = CharacterState.Of(caster);
            if (state.CanCast && !state.Suppressed && !state.Taunted) return false;
        }
        return Stop(caster, Cancel, SourceOf(reason), soft: true);
    }

    /// <summary>
    /// Ends the caster's channel; false when it runs none. This is what <c>BBStopChanneling</c> and every stop source call. The wire
    /// follows the client (W25c, <c>SpellCaster_Client</c>): a channel that runs out (<c>TimeCompleted</c>) is ended by the client's own SpellCaster at
    /// <c>mTimeSpellEnd</c>, which plays the winddown clip, so the server sends nothing (a stop would cut the clip and the bar); a death needs no
    /// packet (the unit's death routine stops the SpellCaster); every other end sends the status word and a stop. A stop the player's own order or
    /// cast caused (<paramref name="soft"/>) is the ordinary one, because the client has already stopped itself in its move and cast handlers; a stop
    /// the server decides (a stun, a lost target, a script's <c>BBStopChanneling</c>, a displacement) is the forced one, which the client cannot
    /// refuse for a spell with <c>CantCancelWhileChanneling</c>.
    /// </summary>
    public bool Stop(Entity caster, BbChannelingStopCondition condition, BbChannelingStopSource source, bool soft = false)
    {
        if (!_channels.Remove(caster, out var channel)) return false;
        caster.ActiveScriptChannels--;
        if (channel.Uncancellable) SpellWindup.End(caster);
        _host.Router.RunChanneling(channel, condition == BbChannelingStopCondition.ChannelingStopCondition_Success
            ? "ChannelingSuccessStop" : "ChannelingCancelStop");
        _host.Router.RunChanneling(channel, "ChannelingStop");
        _host.RetireOwnedEffects(channel.InstanceVars);
        // The caster leaves the channel pose: the client is told the status word and stops where it stands. A displacement that moves the caster
        // sends its own terminal waypoint.
        var spells = _host.Spells;
        // Reconstruction (batch 476): a use channel (BbSummonerModule.BeginUseChannel, no client cast) has no client SpellCaster to end its pose, so
        // its looped Channel animation (OdinCaptureChannelBomb's ChannelingUpdateStats BBPlayAnimation, Loop) outlives it. The end releases that loop with
        // the stop a cancel sends (SpellSystem.StopCastGesture, 0x2B), for the channeller only, and only while the loop is still the unit's current
        // animation. The capture channel's ChannelingSuccessStop already unlocks its loop (OdinCaptureChannel.txt:1080), which moves the serial on,
        // so it is not doubled. the animation routine replaces the running animations only on flag 0x8, and nothing else clears a loop.
        if (channel.Slot == BbSummonerModule.CaptureChannelNoSlot && caster.Alive && channel.LoopSerial == caster.AnimationSerial &&
            spells.Effects.IsReferenceReady(caster))
            spells.Effects.StopAnimation(caster);
        if (source != BbChannelingStopSource.ChannelingStopSource_TimeCompleted && spells.Effects.IsReferenceReady(caster) && caster.Alive)
        {
            spells.Effects.SendStats(caster);
            if (soft) spells.Effects.SendStopAttack(caster); else spells.Effects.SendForcedStop(caster);
            if (caster.ForcedMove is null)
                spells.Effects.SendWaypoints(caster, spells.Movement.InstallStop(caster), spells.World.Clock.SyncId());
        }
        return true;
    }

    /// <summary>
    /// The stop source of a reason the hero hooks pass (<c>IChampionSpellHooks.CancelChannel</c>): a fresh order is a move (an attack order
    /// is the source Attack, but both end the channel and no shipped script tells them apart), an accepted cast is Casting, and the reason
    /// of <c>BBStopChanneling</c> ("BBStopChanneling Cancel ChannelingStopSource_LostTarget", see <see cref="ConditionOf"/>) names its source after the
    /// last space; any other reason (Zhonya's Hourglass) is Unknown.
    /// </summary>
    public static BbChannelingStopSource SourceOf(string reason) => reason switch
    {
        "order" => BbChannelingStopSource.ChannelingStopSource_Move,
        "spell request" => BbChannelingStopSource.ChannelingStopSource_Casting,
        _ => Enum.TryParse<BbChannelingStopSource>(reason[(reason.LastIndexOf(' ') + 1)..], out var named)
            ? named : BbChannelingStopSource.ChannelingStopSource_Unknown,
    };

    /// <summary>
    /// The stop condition of a reason: <c>BBStopChanneling</c> passes the script's <c>StopCondition</c> as the word after its name ("Success" or
    /// "Cancel"; the Odin capture channels end themselves as a success with the source Move), every other stop is a cancel.
    /// </summary>
    public static BbChannelingStopCondition ConditionOf(string reason) =>
        reason.StartsWith("BBStopChanneling Success ", StringComparison.Ordinal)
            ? BbChannelingStopCondition.ChannelingStopCondition_Success : BbChannelingStopCondition.ChannelingStopCondition_Cancel;
}
