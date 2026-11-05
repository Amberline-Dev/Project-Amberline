using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Buffs;

namespace Amberline.Simulation.BehaviorTrees;

/// <summary>
/// The engine actions the capture leaves call (batch 448). Each member is optional: a leaf that needs a missing one fails its node and
/// reports it once, so a context without the hooks still runs the tree.
/// </summary>
public sealed record CapturePointHooks(
    Action<PlaintextPacket>? Send = null,
    Action<Entity, uint>? Kill = null,
    Func<Entity, double, bool>? AwardGold = null,
    // Batch 452: the guardian a champion channels the shipped OdinCaptureChannel on (its Target), or null when it channels no capture.
    // Batch 517: production hands the channel only once it has charged (SpellSystem.ChargedCaptureTarget), so the wind-up does not count.
    Func<Entity, Entity?>? CaptureChannelTarget = null,
    // Batch 462: the next replication sync id of a unit (the match's ReplicationSyncCounter), for the point turrets' state blocks.
    Func<uint, uint>? SyncId = null,
    // Batch 501: the engine actions of the guardians' shipped tower tree (OdinTowerBehavior, BehaviorLeaves.Tower). Replaces batch 466's
    // hand-written GuardianAttack step.
    TowerHooks? Tower = null,
    // Batch 471: a packet for one team's clients only (the quest tracker, 0x90, and the quest announcement).
    Action<Team, PlaintextPacket>? SendTeam = null,
    // Batch 480b: the hero whose capture channel (aimed at this guardian, on this team's side) took the point: the score's display source.
    Func<Entity, Team, Entity?>? CapturerOf = null,
    // Batch 505: a point event's damage to a team's nexus, down to the floor (CombatService.DamageDominionNexusAboveFloor).
    // Batch 531: the third argument shows the loss as floating text (a capture does, a neutralise does not).
    Action<Team, double, bool>? DamageNexusAboveFloor = null);

/// <summary>
/// Batch 448: the capture leaves of Dominion (map 8): the point's turret and vision state, the unit query of a capture, the capture
/// packets, kills, gold and announcements, and the debug and cosmetic nodes the capture trees run past. Each leaf's inputs and outputs
/// are the parameter names of its node in the shipped XML (OdinScriptBehaviorTrees.xml).
/// </summary>
public static partial class BehaviorLeaves
{
    public static readonly Dictionary<string, BehaviorLeaf> CaptureTable = new(StringComparer.Ordinal)
    {
        // DIAGNOSTICS: all 27 shipped DebugAction nodes carry Result SUCCESS (RunningLimit 0). They change no state; the node returns its
        // Result, so the sequence the designers wrote runs on (a failure would abort every capture tick).
        ["DebugAction"] = call => call.Text("Result") == "FAILURE" ? BehaviorStatus.Failure : BehaviorStatus.Success,
        // DIAGNOSTICS: debug circles are drawn by the client's debug UI only; the node succeeds and names no circle (id 0).
        ["AddDebugCircle"] = call => Output(call, "DebugCircleId", 0.0),
        ["ModifyDebugCircleColor"] = call => BehaviorStatus.Success,
        ["ModifyDebugCircleRadius"] = call => BehaviorStatus.Success,
        // DIAGNOSTICS: batch 455. The opening ceremony's RemoveDebugCircle nodes (OdinScriptBehaviorTrees.xml:4749 and 4754) had no leaf,
        // so the Action sequence failed before its Announcement_OnStartGame (FirstMessage false). The node succeeds and removes nothing.
        ["RemoveDebugCircle"] = call => BehaviorStatus.Success,

        // Turret state: the point's turret is hidden at the start and shown to its team while the team holds the point.
        ["SetUnitRendered"] = SetUnitRendered,
        // Reconstruction: Enable false makes the unit untargetable by both teams (the shipped uses set a bool).
        ["SetUnitTargetableState"] = SetUnitTargetableState,
        // Reconstruction: the Team input is the team that cannot target the unit (Entity.Stats.cs, BBSetNotTargetableToTeam).
        ["SetUnitNotTargetableToTeam"] = SetUnitNotTargetableToTeam,
        // Batch 456: the permanent percent sight modifier of the unit, the key the BuildingBlocks path adds to (VisibilityRules.Scaled).
        ["IncPermanentPercentBubbleRadiusMod"] = IncPermanentPercentBubbleRadiusMod,

        ["GetUnitsInTargetArea"] = GetUnitsInTargetArea,
        // Batch 498: the shipped test "Doesn't count if stealthed" (OdinScriptBehaviorTrees.xml:148-151, ReturnSuccessIf true). Success when the
        // unit is stealthed (VisibilityRules.IsStealthed), so the Selector skips it; an unstealthed champion fails it and is counted. No unit fails.
        ["TestUnitIsStealthed"] = call => call.Unit("Unit") is { } unit
            ? Test(Amberline.Network.Replication.VisibilityRules.IsStealthed(unit) == call.Flag("ReturnSuccessIf"))
            : BehaviorStatus.Failure,
        ["CapturePoint_SetValue"] = CapturePointSetValue,
        ["CapturePoint_SetRate"] = CapturePointSetRate,
        ["Announcement_OnCapturePointCaptured"] = call => Announce(call, captured: true),
        ["Announcement_OnCapturePointNeutralized"] = call => Announce(call, captured: false),
        ["KillUnit"] = KillUnit,
        ["KillSquad"] = KillSquad,
        ["GiveChampionGold"] = GiveChampionGold,

        ["Announcement_OnStartGame"] = StartAnnouncement,
        ["SetUnitCircularMovementRestriction"] = SetUnitCircularMovementRestriction,
        // Batch 460: switches a BTInstance (OdinScript.xml Name) off or on; the ceremony's last node turns itself off after the release
        // (OdinScriptBehaviorTrees.xml:4827, the only shipped call). Reconstruction: an instance OdinScript.xml starts disabled is not
        // started by an Enabled true (no shipped call does that).
        ["SetBTInstanceStatus"] = call =>
        {
            call.Context.InstanceEnabled[call.Text("Name")] = call.Flag("Enabled");
            return BehaviorStatus.Success;
        },
        // Reconstruction: a level prop is client scene data; no attackable prop is spawned here.
        ["SpawnAttackableLevelProp"] = call => NoOp(call, "level-prop:" + call.Text("PropName"), "the level prop is client scene data"),
        // Reconstruction: a perception bubble has no unit to reveal here; LocationIllumination's points are unset on this map (batch 444 gap 2).
        ["AddPositionPerceptionBubble"] = call =>
        {
            Output(call, "BubbleID", 0.0);
            return NoOp(call, "bubble", "perception bubbles are not modelled (Reconstruction)");
        },
    };

    private static BehaviorStatus Output(BehaviorCall call, string name, object? value)
    {
        call.Out(name, value);
        return BehaviorStatus.Success;
    }

    private static BehaviorStatus NoOp(BehaviorCall call, string key, string detail)
    {
        call.Note(key, detail);
        return BehaviorStatus.Success;
    }

    // Batch 462: a change reaches every client that holds the unit (the point turrets are known to every client from their spawn, batch 457),
    // in sight or not, as the minion's ActionState (NoRender is bit 13, CharacterState.ScriptedCombatOf). A client learned it only at the
    // unit's creation and a re-entry sent only a set bit, so a turret shown while out of a client's sight stayed hidden there.
    private static BehaviorStatus SetUnitRendered(BehaviorCall call)
    {
        if (call.Unit("Unit") is not { } unit) return BehaviorStatus.Failure;
        if (unit.NoRender == !call.Flag("Enable")) return BehaviorStatus.Success;
        unit.NoRender = !call.Flag("Enable");
        return SendState(call, unit, sync => CombatPackets.MinionActionState(unit.NetId,
            CharacterState.ScriptedCombatOf(unit).ActionState, sync));
    }

    private static BehaviorStatus SetUnitTargetableState(BehaviorCall call)
    {
        if (call.Unit("Unit") is not { } unit) return BehaviorStatus.Failure;
        return SetNotTargetable(call, unit, !call.Flag("Enable"), !call.Flag("Enable"));
    }

    // Batch 462: a targetability change reaches every client that holds the unit as its IsTargetableToTeam (CombatPackets.UnitTargetableToTeam,
    // the packet BBSetNotTargetableToTeam sends).
    private static BehaviorStatus SetNotTargetable(BehaviorCall call, Entity unit, bool toAlly, bool toEnemy)
    {
        if (unit.NotTargetableToAlly == toAlly && unit.NotTargetableToEnemy == toEnemy) return BehaviorStatus.Success;
        unit.NotTargetableToAlly = toAlly;
        unit.NotTargetableToEnemy = toEnemy;
        return SendState(call, unit, sync => CombatPackets.UnitTargetableToTeam(unit, isHero: false, sync));
    }

    // The minion layouts: only a unit the clients hold as a minion (a point turret, Entity.ReplicatedAsMinion) gets the block.
    private static BehaviorStatus SendState(BehaviorCall call, Entity unit, Func<uint, PlaintextPacket> build)
    {
        if (!unit.ReplicatedAsMinion && unit.UnitType is not (UnitType.Minion or UnitType.Monster)) return BehaviorStatus.Success;
        if (call.Context.Capture is not { Send: { } send, SyncId: { } syncId })
        {
            call.Note("state:none", "this context has no packet sender: the unit's state is not sent");
            return BehaviorStatus.Success;
        }
        send(build(syncId(unit.NetId)));
        return BehaviorStatus.Success;
    }

    /// <summary>
    /// Batch 456 (script = retail, the values; Reconstruction, the radius): the shipped trees' deltas are -1 on every point turret at the start
    /// (a hidden turret sees nothing, OdinScriptBehaviorTrees.xml:2932-2986), +1.5 on the turret of the team that takes the point (:1326, :1404)
    /// and -1.5 on the turret of the team that loses it (:1486, :1527). The delta adds to <c>PermanentStatMods[PercentBubbleRadiusMod]</c>, the
    /// same accumulator BbSpellHost.IncStat adds a script's IncPermanentPercentBubbleRadiusMod to; VisibilityRules.Scaled reads it.
    /// </summary>
    private static BehaviorStatus IncPermanentPercentBubbleRadiusMod(BehaviorCall call)
    {
        if (call.Unit("Unit") is not { } unit) return BehaviorStatus.Failure;
        const string key = Amberline.Network.Replication.VisibilityRules.PermanentPercentBubbleKey;
        unit.PermanentStatMods[key] = (unit.PermanentStatMods.TryGetValue(key, out var current) ? current : 0.0) + call.Number("Delta");
        return BehaviorStatus.Success;
    }

    private static BehaviorStatus SetUnitNotTargetableToTeam(BehaviorCall call)
    {
        if (call.Unit("Unit") is not { } unit) return BehaviorStatus.Failure;
        var own = call.TeamOf("Team") == unit.Team;
        // The native replaces the unit's flags (the flags word is reset to its default, then the new value
        // is written), so a recaptured point's turret is not left untargetable to its own team by the neutralise before it.
        return SetNotTargetable(call, unit, own, !own);
    }

    // Reconstruction: SpellFlags are the shipped names (SPELL_* in BbSpellFlags). The caster's team decides friend and enemy; neutral
    // units need AffectNeutral; dead units need AffectDead; untargetable units need AffectUntargetable. Type flags filter when any is set.
    // Batch 456: a point turret's champion query (EvaluateCapturePoint's "Check for local champions", OdinScriptBehaviorTrees.xml:127) keeps a
    // champion only while it channels the capture on this point's guardian (ChannelsCapture). Minion queries are unchanged.
    private static BehaviorStatus GetUnitsInTargetArea(BehaviorCall call)
    {
        if (call.Unit("Unit") is not { } caster) return BehaviorStatus.Failure;
        if (call.Vector("TargetLocation") is not { } centre) return BehaviorStatus.Failure;
        var radius = call.Number("Radius");
        var flags = SpellFlagsOf(call.Text("SpellFlags"));
        var channelOnly = flags.HasFlag(BbSpellFlags.SPELL_AffectHeroes) && IsPointTurret(call.Context, caster);
        var found = new List<Entity>();
        foreach (var unit in call.Context.World.Entities.Values)
        {
            var dx = unit.Position.X - centre.X;
            var dz = unit.Position.Z - centre.Z;
            if (dx * dx + dz * dz > radius * radius) continue;
            if (!InArea(unit, caster, flags)) continue;
            if (channelOnly && unit.UnitType == UnitType.Hero && !ChannelsCapture(call.Context, unit, centre, radius)) continue;
            found.Add(unit);
        }
        return Output(call, "Output", found);
    }

    /// <summary>
    /// Batch 456 (Reconstruction, part 1): whether a champion channels the capture on the point at <paramref name="point"/>, that is its
    /// OdinCaptureChannel targets a guardian within <paramref name="radius"/> of the point (batch 449's channel, batch 452's hook). Wiki, Capture
    /// point: a champion channels for a set amount of time to capture it; Dominion: more channelling champions capture it faster (the wiki's
    /// capture point and Dominion pages). The shipped
    /// tree counted every hero in its radius and names no channel, so the channel is the rule the engine adds. Shared by the point turret's
    /// champion count and by BehaviorTreeQuestHost.ApplyPointTurretRules. Batch 517: the hook answers only a charged channel (past the shipped
    /// channel's 1.5 s wind-up, InstanceVars.ChargeTimePassed), so the click and the wind-up add no progress.
    /// </summary>
    internal static bool ChannelsCapture(BehaviorTreeContext context, Entity hero, Position3 point, double radius) =>
        context.Capture?.CaptureChannelTarget?.Invoke(hero) is { } guardian &&
        Math.Pow(guardian.Position.X - point.X, 2) + Math.Pow(guardian.Position.Z - point.Z, 2) <= radius * radius;

    /// <summary>Batch 456: a point turret is one of GetTurret's answers for positions 2 to 6 (point A to E, batch 448).</summary>
    private static bool IsPointTurret(BehaviorTreeContext context, Entity unit) =>
        Enumerable.Range(2, 5).Any(position => context.Turret(Team.Order, 0, position) == unit || context.Turret(Team.Chaos, 0, position) == unit);

    private static bool InArea(Entity unit, Entity caster, BbSpellFlags flags)
    {
        if (!unit.Alive && !flags.HasFlag(BbSpellFlags.SPELL_AffectDead)) return false;
        // Batch 501: a ward needs the 0x04000000 opt-in besides AffectMinions (the native unit predicate, the rule
        // ChampionSpellDispatcher.IsAreaEffectTarget keeps); no shipped tree passes it, so no area query of a tree returns a ward.
        if (unit.IsWard) return false;
        if (unit.NotTargetableTo(caster) && !flags.HasFlag(BbSpellFlags.SPELL_AffectUntargetable)) return false;
        var side = unit.Team == caster.Team ? BbSpellFlags.SPELL_AffectFriends
            : unit.Team == Team.Neutral ? BbSpellFlags.SPELL_AffectNeutral
            : BbSpellFlags.SPELL_AffectEnemies;
        if (!flags.HasFlag(side)) return false;
        var kind = unit.UnitType switch
        {
            UnitType.Hero => BbSpellFlags.SPELL_AffectHeroes,
            UnitType.Minion => BbSpellFlags.SPELL_AffectMinions,
            UnitType.Turret => BbSpellFlags.SPELL_AffectTurrets,
            UnitType.Structure => BbSpellFlags.SPELL_AffectBuildings,
            _ => (BbSpellFlags)0,
        };
        var typed = flags & (BbSpellFlags.SPELL_AffectHeroes | BbSpellFlags.SPELL_AffectMinions |
                             BbSpellFlags.SPELL_AffectTurrets | BbSpellFlags.SPELL_AffectBuildings);
        return typed == 0 || (kind != 0 && flags.HasFlag(kind));
    }

    private static BbSpellFlags SpellFlagsOf(string text)
    {
        var flags = (BbSpellFlags)0;
        foreach (var token in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (Enum.TryParse<BbSpellFlags>("SPELL_" + token, out var flag)) flags |= flag;
        return flags;
    }

    // Reconstruction (batch 445 layouts): the point's capture index is CapturePointID - 1. SetValue sends 0xDB (a constant flex value from the
    // progress -100..100, mapped to 0..255). Batch 466: the netId the point's records name (0xDB, 0xDC, 0xDE) is its guardian's. 0xDB looks its key
    // up in the client's unit map and binds its flex to that unit (batch 493e: mode 0, the point's CapturePointFlexValue), and the guardian's PAR is the capture meter
    // (OdinGuardianSuppression and OdinGuardianBuff move it with BBIncPAR; OdinNeutralGuardian.inibin names capture_point_gauge.troy); 0xDC
    // writes only when the unit exists on the client. Every client holds the guardian from the start: the neutral guardian
    // carries both teams' sight (OdinGuardianBuff OnBuffActivate blocks 6 and 7). Batch 448 named the Order point turret, which no client holds now.
    // Batch 475 (proven in the client): the 0xDB flex (mode 0) keeps the capture index in its
    // byte. Batch 494 (proven, corrects batch 475): the 0xDC byte is not a progress. The record's PAR type is the one the point
    // reads its unit with: the 0xDC handler is the point's attach routine (unit, PrimaryAbilityResourceType)
    // and GetGoalValue is the unit's
    // getter with that type, GetPARPercent (which it calls): mPAR / mMaxPAR when the type is the unit's, else 0. The
    // channel bar of a use cast at the point is that value (GetPercentCaptured): the progress byte named
    // no PAR type, so the bar sat at 0 for an Order capturer of a neutral point and at full for a Chaos one.
    private static BehaviorStatus CapturePointSetValue(BehaviorCall call)
    {
        if (CapturePointTurret(call) is not { } point) return BehaviorStatus.Failure;
        var index = (byte)(call.Number("CapturePoint") - 1);
        // Batch 493e (Reconstruction, replaces batch 479b's mode 1): the gauge is bound in mode 0, a CapturePointFlexValue of the point's
        // index (the 0xDB handler). Its GetValue is the record's progress field, times -1 for a Chaos viewer
        // and the client writes that field itself each frame (for every record, once
        // the CapturePoints list exists) eases it toward GetGoalValue at 1.08 times the rate it measures between targets, and
        // snaps a gap above 0.25. Batch 479b saw mode 0 stay at 0 because the 0xDC named no PAR type then (batch 494). With the guardian's raw
        // pool, mode 0 is empty on a neutral point (half the pool), fills toward the capturing team for both capturers, and is the same value the
        // channel bar and the minimap read. PARFlexValue (mode 1) showed the raw fraction: half on a neutral point, emptying for a Chaos capture.
        // The 0xDC below attaches the point's index to the guardian.
        Send(call, point.NetId, DominionPackets.CapturePointEvent(point.NetId, 0, index, parFlex: false));
        Send(call, point.NetId, DominionPackets.CapturePointValue(index, point.NetId, GuardianParType, apply: true));
        // Batch 512 (Reconstruction, owner "one timer", 31 October 2026): the node's Value (CaptureProgress, -100 Chaos to +100 Order) and Team
        // (CurrentPointOwner) write the guardian's pool, the meter the ring and the channel bar read. The client reads that pool back through
        // GetGoalValue: GetPARPercent p mapped by the guardian's team, neutral 2 x (p - 0.5), Order p, Chaos -p (0.5 is
        // the constant). CaptureMeterPool is its inverse, so the client's value is CaptureProgress / 100: the ring fills as the tree's progress
        // nears the flip at 99, the minions' +/-9 moves it, and a neutral point at rest (0) is empty. The bar follows (halved for an
        // owned point by its caller): 0 to half while neutralising, half to full while capturing, for either team. Nothing shipped
        // shows the server's node writing the PAR; the node carries exactly the two inputs the inverse needs.
        var pool = CaptureMeterPool(point, call.Number("Value"), call.TeamOf("Team"));
        if ((float)pool == (float)point.Mana) return BehaviorStatus.Success;
        point.Mana = pool;
        // The same 0xCC (mMP, mMaxMP) the batch 479 ring used; the connection's replication store cuts it to mMP.
        if (call.Context.Capture is { Send: { } send, SyncId: { } syncId })
            send(CombatPackets.ReplicationPar(point.NetId, (float)pool, (float)point.MaxMana, syncId(point.NetId)));
        return BehaviorStatus.Success;
    }

    /// <summary>
    /// Batch 512 (Reconstruction): the guardian pool that shows <paramref name="progress"/> (the tree's CaptureProgress, -100 to 100) on a point
    /// held by <paramref name="owner"/>: the inverse of the client's GetGoalValue (see <see cref="CapturePointSetValue"/>).
    /// </summary>
    internal static double CaptureMeterPool(Entity guardian, double progress, Team owner) => guardian.MaxMana * Math.Clamp(owner switch
    {
        Team.Order => progress / 100.0,
        Team.Chaos => -progress / 100.0,
        _ => 0.5 + progress / 200.0,
    }, 0.0, 1.0);

    /// <summary>
    /// Batch 694 (Reconstruction): the progress that <paramref name="par"/> of the guardian's pool is on a point held by <paramref name="owner"/>,
    /// the change <see cref="CaptureMeterPool"/> maps back to that much pool (unclamped).
    /// </summary>
    internal static double CaptureProgressOfPar(Entity guardian, double par, Team owner) => guardian.MaxMana <= 0.0 ? 0.0 : par / guardian.MaxMana * owner switch
    {
        Team.Order => 100.0,
        Team.Chaos => -100.0,
        _ => 200.0,
    };

    // The guardian's PAR type as the client loads it: OdinNeutralGuardian.inibin names no Data*PARType, and the loader's default "MP" is
    // PrimaryAbilityResourceType 0 (PrimaryResourceType.Mana). Its pool is the capture meter (BaseMP 50000; BBIncPAR in the shipped buffs).
    private const byte GuardianParType = (byte)Amberline.Data.PrimaryResourceType.Mana;

    // Batch 475: no packet. The shipped CaptureRate and AttackerCount have no client field (the 0xDC value byte is the progress, and a rate
    // written there overwrote it). Reconstruction.
    private static BehaviorStatus CapturePointSetRate(BehaviorCall call)
    {
        if (CapturePointTurret(call) is null) return BehaviorStatus.Failure;
        call.Note("rate:none", "the capture rate has no client field (Reconstruction): no packet is sent");
        return BehaviorStatus.Success;
    }

    /// <summary>
    /// Batch 550: Priscilla's Blessing (item 3183, fontconfig <c>game_item_description_3183</c>: "+15% Capture Rate (does not stack with other
    /// Capture Rate modifiers)"). Its passive is no stat and no tree node but three shipped scripts:
    /// <c>Items/3183.luaobj</c> UpdateSelfBuffActions keeps the PrilisasBlessing buff on the holder (BBExecutePeriodically every 5 s, 6 s);
    /// <c>Spells/OdinCaptureChannel.luaobj</c> ChannelingStart copies it onto the channel's Target, the guardian, while the channel lasts
    /// (BBGetBuffCountFromAll Owner PrilisasBlessing &gt; 0: BBSpellBuffAdd Target PrilisasBlessing 25000 s; both stops remove that stack);
    /// <c>Spells/OdinGuardianSuppression.luaobj</c> BuffOnUpdateActions drains the guardian every 0.25 s by MaxPAR x 0.0294 x DamageMultiplier,
    /// DamageMultiplier = 0.4 x (TotalBuffCount - 1) + 1.31 while the guardian holds a PrilisasBlessing, + 1.06 without (a count test, so two
    /// holders give what one does: "does not stack"); TotalBuffCount is the larger of its OdinGuardianSuppressionOrder and Chaos stacks.
    /// Reconstruction (batch 512, owner "one timer"): that drain no longer moves the meter (BbNativeHandlers.IncPar), the tree's CapturePointMath
    /// rate does, so the champions' rate there takes the drain's own ratio, x1.236 for one channeller, x1.174 for two. The script's ratio, not
    /// the tooltip's 15 %: the scripts are the server's code. Every other tree gets 1.
    /// </summary>
    internal static double CaptureRateScale(BehaviorCall call)
    {
        if (call.TreeName != "CapturePointMath" || BehaviorCall.ToNumber(call.InstanceVariable("CapturePointID")) is not { } point ||
            call.Context.PointGuardian((int)point + 1) is not { } guardian || BuffCount(guardian, "PrilisasBlessing") <= 0.0) return 1.0;
        var extra = Math.Max(BuffCount(guardian, "OdinGuardianSuppressionOrder"), BuffCount(guardian, "OdinGuardianSuppressionChaos")) - 1.0;
        return (0.4 * extra + 1.31) / (0.4 * extra + 1.06);
    }

    // BBGetBuffCountFromAll as BbSpellHost.SpellBuffCount answers it with no caster: each buff of the name counts its stacks, at least 1.
    private static double BuffCount(Entity unit, string name) =>
        unit.Buffs.Where(buff => string.Equals(buff.Name, name, StringComparison.OrdinalIgnoreCase)).Sum(buff => Math.Max(1, buff.Stacks));

    // Reconstruction: GetTurret position 2 is point A (the shipped GetTurret inputs), and the point's guardian is kept by that position.
    private static Entity? CapturePointTurret(BehaviorCall call) => call.Context.PointGuardian((int)call.Number("CapturePoint") + 1);

    // The trees set a point's value and rate on every tick; a record goes to the clients only when its bytes change (the smoke run sent
    // each point's 0xDB and 0xDC every step, 150 packets a second per client). Reconstruction: retail's send cadence is not recovered.
    private static BehaviorStatus Send(BehaviorCall call, uint netId, PlaintextPacket packet)
    {
        if (call.Context.Capture?.Send is not { } send)
        {
            call.Note("packets:none", "this context has no packet sender: the capture packets are not sent");
            return BehaviorStatus.Success;
        }
        var key = (packet.Plaintext.Span[0], netId);
        if (call.Context.SentCapture.TryGetValue(key, out var last) && last.Plaintext.Span.SequenceEqual(packet.Plaintext.Span))
            return BehaviorStatus.Success;
        call.Context.SentCapture[key] = packet;
        send(packet);
        return BehaviorStatus.Success;
    }

    // The 18-byte 0x4A's i32 is the announcement's one token argument. @MapNumber@ (the start texts, game_announcement_OnStartMessage1Map8)
    // and @CapturePoint@ (the capture texts) read the same dword (both client entries read the same field). The
    // start events carry the map number 8; the capture events carry the capture index (batch 475).
    private const int DominionMapNumber = 8;

    // Batch 455: the captured and neutralised announcements go out as 0x4A (ids: CombatPackets.CapturePointEventId). The text and the sound read
    // @src your team | enemy team@ (Announcements_ODIN.ini:95-172), so the source only has to be a unit of the acting team every client holds.
    // Batch 466 (Reconstruction): that is a champion of the team, the first one channelling the capture on the point, else any of the team's
    // champions (every client holds every champion). Batch 455 named the team's point turret, which no client holds now; the guardian takes the
    // new team only after the tree's tick (BehaviorTreeQuestHost.GuardianTakesOwner) and is neutral after a neutralisation.
    private static BehaviorStatus Announce(BehaviorCall call, bool captured)
    {
        var point = (int)call.Number("CapturePoint");
        if (point is < 1 or > 5) return BehaviorStatus.Failure;
        var team = call.TeamOf("Team");
        var source = call.Context.Turret(team, 0, point + 1) is { } anchor ? CreditChannellers(call.Context, team, anchor.Position, captured) : null;
        source ??= call.Context.World.Heroes().FirstOrDefault(hero => hero.Team == team);
        DamageOpposingNexus(call, team, captured ? CaptureNexusDamage : NeutralizeNexusDamage, showLoss: captured);
        // Batch 475: the text's @CapturePoint@ is "game_capture_point_<argument>" (fontconfig keys 0 to 4, Quarry to Boneyard), so the
        // argument is the capture index (point - 1). Batch 455 sent the map number here, which the client showed as game_capture_point_8.
        return SendEvent(call, CombatPackets.CapturePointEventId(captured, point), source?.NetId ?? 0u, point - 1);
    }

    /// <summary>
    /// Batch 505 (Reconstruction): the nexus damage of a point event, to the acting team's opponent (the announcement's Team is the capturer, or
    /// the attacker that neutralised, so the opponent is the point's former owner on a neutralise). No shipped tree or script names it. Sources:
    /// a forum post of 15 February 2012 (owner evidence, after V1.0.0.127): "while the nexus is above 100 health ... neutralizing a point gives
    /// -3 health; capping a point gives -2 health"; the wiki (Nexus, Dominion, undated): "Taking a capture point. This would immediately deal 2
    /// damage to the opposing Nexus"; Patch History V1.0.0.127 names "Neutralizes" among what the floor stops. The damage stops at the floor
    /// (CombatService.DominionKillFloor, 125 in 1.0.0.126). The shipped announcement nodes (Announcement_OnCapturePointCaptured,
    /// OdinScriptBehaviorTrees.xml:1338 and :1416; Announcement_OnCapturePointNeutralized, :1504 and :1533) are the nodes every capture and
    /// neutralise runs once, so the damage rides on them.
    /// </summary>
    public const double CaptureNexusDamage = 2.0;

    /// <summary>Batch 505 (Reconstruction): a neutralise's damage to the opposing nexus (see <see cref="CaptureNexusDamage"/>).</summary>
    public const double NeutralizeNexusDamage = 3.0;

    // Batch 531 (Reconstruction): only a capture's loss shows the floating text. Wiki (Nexus, Notes): "Whenever a Nexus lost health from a
    // takedown or point capture, players on that team were notified via a small text prompt atop their champion. (-2 Your Nexus Health)".
    // Nothing found (shipped files, wiki, guides) shows a text for a neutralise, so it shows none.
    private static void DamageOpposingNexus(BehaviorCall call, Team team, double amount, bool showLoss)
    {
        if (team is not (Team.Order or Team.Chaos)) return;
        if (call.Context.Capture?.DamageNexusAboveFloor is { } damage) damage(team == Team.Order ? Team.Chaos : Team.Order, amount, showLoss);
        else call.Note("nexus:none", "this context has no nexus hook: the point event damages no nexus");
    }

    /// <summary>
    /// Batch 462 (Reconstruction): the ODIN collection's NODE_CAPTURE and NODE_NEUTRALIZE (ODIN_Stats.inibin; the client reads them from the
    /// 0x4B, ClassicHeroStatsSnapshot). Each champion of the acting team (the announcement's Team: the capturer, or the attacker that
    /// neutralised, OdinScriptBehaviorTrees.xml:1338, :1504) that channels the capture on the point (<see cref="ChannelsCapture"/>; the point
    /// turrets stand on the point) when its tree announces the change counts one. No shipped script or tree counts them. Batch 467: each
    /// credit also goes to every client as the client's own increment, 0xE3 (<see cref="DominionPackets.IncrementPlayerStat"/>), so the
    /// scoreboard counts it at once instead of at the hero's next 0x4B. Returns the first credited.
    /// Batch 518 (Reconstruction): each channeller also gets the capture's or the neutralise's personal score, and each other living champion
    /// of the team within the capture radius of the point counts an assist (NODE_CAPTURE_ASSIST or NODE_NEUTRALIZE_ASSIST, 0xE3 8 or 9) and
    /// gets the assist's score (<see cref="DominionPlayerScore"/>; the wiki: "Assisting a capture or neutralize (nearby, not channeling)").
    /// </summary>
    private static Entity? CreditChannellers(BehaviorTreeContext context, Team team, Position2 point, bool captured)
    {
        var at = new Position3(point.X, 0.0, point.Z);
        const double radius = BehaviorTreeQuestHost.CaptureRadius;
        Entity? first = null;
        foreach (var hero in context.World.Entities.Values)
        {
            if (hero.UnitType != UnitType.Hero || !hero.Alive || hero.Team != team) continue;
            var channels = ChannelsCapture(context, hero, at, radius);
            if (!channels && Math.Pow(hero.Position.X - point.X, 2) + Math.Pow(hero.Position.Z - point.Z, 2) > radius * radius) continue;
            if (channels && captured) hero.NodeCaptures++;
            else if (channels) hero.NodeNeutralizes++;
            else if (captured) hero.NodeCaptureAssists++;
            else hero.NodeNeutralizeAssists++;
            // Not through Send's de-duplication: the same increment twice is two credits.
            context.Capture?.Send?.Invoke(DominionPackets.IncrementPlayerStat(hero.NetId, (channels, captured) switch
            {
                (true, true) => DominionPackets.NodeCaptureStat,
                (true, false) => DominionPackets.NodeNeutralizeStat,
                (false, true) => DominionPackets.NodeCaptureAssistStat,
                _ => DominionPackets.NodeNeutralizeAssistStat,
            }));
            context.PlayerScore?.Award(hero, (channels, captured) switch
            {
                (true, true) => DominionPlayerScore.ZoneCapture,
                (true, false) => DominionPlayerScore.ZoneNeutralize,
                (false, true) => DominionPlayerScore.NodeCaptureAssist,
                _ => DominionPlayerScore.NodeNeutralizeAssist,
            });
            if (channels) first ??= hero;
        }
        return first;
    }

    // Batch 455: the start messages are the shipped Announcement_OnStartGame leaves: FirstMessage true (CapturePointManager,
    // OdinScriptBehaviorTrees.xml:3169) is OnStartGameMessage1 (79), false (OdinOpeningCeremony, :4759) is OnStartGameMessage2 (80).
    // Each goes out once per match.
    private static BehaviorStatus StartAnnouncement(BehaviorCall call)
    {
        // NOT RETAIL (owner, 29 October 2026): the first message ("battle begins in 30 seconds") plays at OpeningCeremonyTiming.FirstAnnouncementSeconds,
        // sent by the quest host's tick (SendOpeningAnnouncement). This shipped node only records that it ran.
        if (call.Flag("FirstMessage"))
        {
            call.Note("announce:first-scheduled", "the first start message plays 30 s before the release (OpeningCeremonyTiming)");
            return BehaviorStatus.Success;
        }
        var eventId = CombatPackets.StartGameMessage2;
        if (!call.Context.AnnouncedOnce.Add("start:" + eventId)) return BehaviorStatus.Success;
        return SendEvent(call, eventId, 0u);
    }

    /// <summary>NOT RETAIL: the first start message, sent by the quest host at its scheduled time (see <see cref="StartAnnouncement"/>).</summary>
    internal static void SendOpeningAnnouncement(System.Action<PlaintextPacket> send, byte eventId)
        => send(CombatPackets.OnEventWorld(eventId, 0u, 0, DominionMapNumber));

    // Batch 458: the opening ceremony (OdinScriptBehaviorTrees.xml:4711, :4721) holds each champion inside 1200 of its team's start point
    // (SpawnBarrierRadius, :4637) on its first tick, and lets every champion go with Radius 0 once the game time passes 45 (:4743-4778).
    // Reconstruction: Radius 0 is the release (the only shipped use of 0 is that release). Every shipped node passes Camera false, so no
    // camera restriction is applied (the level script's own SetCircularCameraRestriction is a separate native).
    private static BehaviorStatus SetUnitCircularMovementRestriction(BehaviorCall call)
    {
        if (call.Unit("Player") is not { } unit || call.Vector("TargetPosition") is not { } centre) return BehaviorStatus.Failure;
        var radius = call.Number("Radius");
        unit.MovementRestriction = radius > 0.0 ? (new Position2(centre.X, centre.Z), radius) : null;
        return BehaviorStatus.Success;
    }

    private static BehaviorStatus SendEvent(BehaviorCall call, byte eventId, uint sourceNetId, int argument = DominionMapNumber)
    {
        if (call.Context.Capture?.Send is not { } send)
        {
            call.Note("announce:none", "this context has no packet sender: the announcement is not sent");
            return BehaviorStatus.Success;
        }
        send(CombatPackets.OnEventWorld(eventId, sourceNetId, 0, argument));
        return BehaviorStatus.Success;
    }

    private static BehaviorStatus KillUnit(BehaviorCall call)
    {
        if (call.Unit("Unit") is not { } unit) return BehaviorStatus.Failure;
        return KillWith(call, unit, call.Unit("Killer")?.NetId ?? 0u);
    }

    // The squad a point's guard is (TurretGuardId): its units die, and a squad already gone succeeds.
    private static BehaviorStatus KillSquad(BehaviorCall call)
    {
        if (Encounters(call) is not { } service) return BehaviorStatus.Failure;
        if (service.Squad((int)call.Number("SquadId")) is not { } squad) return BehaviorStatus.Success;
        foreach (var unit in squad.Units.Where(unit => unit.Alive))
        {
            if (KillWith(call, unit, 0u) == BehaviorStatus.Failure) return BehaviorStatus.Failure;
        }
        return BehaviorStatus.Success;
    }

    private static BehaviorStatus KillWith(BehaviorCall call, Entity unit, uint killerNetId)
    {
        if (call.Context.Capture?.Kill is not { } kill)
        {
            call.Note("kill:none", "this context has no kill hook: the kill does not happen");
            return BehaviorStatus.Failure;
        }
        kill(unit, killerNetId);
        return BehaviorStatus.Success;
    }

    private static BehaviorStatus GiveChampionGold(BehaviorCall call)
    {
        if (call.Unit("Unit") is not { } hero) return BehaviorStatus.Failure;
        if (call.Context.Capture?.AwardGold is not { } award)
        {
            call.Note("gold:none", "this context has no gold hook: the gold is not awarded");
            return BehaviorStatus.Failure;
        }
        return award(hero, call.Number("GoldAmount")) ? BehaviorStatus.Success : BehaviorStatus.Failure;
    }
}
