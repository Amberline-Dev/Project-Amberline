namespace Amberline.Core;

/// <summary>One announced heal: the unit's maximum health and the requested heal amount at that moment.</summary>
public readonly record struct HealAnnouncement(float MaxHealth, float Amount);

public sealed partial class Entity
{
    private HeroMatchStatistics? _matchStatistics;
    public HeroMatchStatistics MatchStatistics => _matchStatistics ??= new();

    /// <summary>
    /// Match-clock time of the most recent positive post-mitigation hit. The
    /// combat owner stamps this at the damage boundary; bot defense tasks use
    /// it for the shipped twenty-second recent-damage window.
    /// </summary>
    public double LastDamageAt { get; set; } = double.NegativeInfinity;

    /// <summary>
    /// True from the moment a basic-attack start (0x0D) is sent for this unit until it is sent a stop-attack (0x39). The
    /// stock client's unit state machine enters its attack state on the 0x0D and leaves it only on the 0x39 (HSM event 4,
    /// the state's stop handler), not when the swing, the missile or the attack period ends; while it is in that state
    /// the client's minion update starts the same attack again on the last target every attack
    /// period, and every cast start calls StopActor, which erases any path the unit is following.
    /// A 0x39 is therefore needed exactly while this is set; the "Stop-attack" section of docs/minions/client_path_follower_re.md explains why.
    /// <c>CombatPackets.StopAttack</c> is the one place that reads and clears it.
    /// </summary>
    public bool ClientAttackArmed { get; set; }

    /// <summary>
    /// <c>BBSkipNextAutoAttack</c>: the unit's next basic attack is not made, because a script cast a spell
    /// in its place (<c>BuffOnPreAttack</c> of Xin Zhao's Three Talon Strike, Garen's Decisive Strike, Leona's Shield of Daybreak, Trundle's Troll Smash).
    /// <c>CombatService.QueueBasicAttack</c> reads and clears it.
    /// </summary>
    public bool SkipNextAutoAttack { get; set; }

    /// <summary>
    /// The roll of the swing a script replaced (<see cref="SkipNextAutoAttack"/>), until the spell that replaced it reads it as <c>HitResult</c> (an extra-slot
    /// spell's <c>TargetExecute</c>, once) or the next swing is made: Xin Zhao's Three Talon Strike spells test <c>HitResult</c> for <c>HIT_Dodge</c> (a dodged
    /// swing still lands the combo) and <c>HIT_Critical</c> (twice the attack damage). The shipped scripts show that the swing's result reaches the spell that replaces it:
    /// <c>XenZhaoThrust.luaobj</c>, <c>XenZhaoThrust2.luaobj</c> and <c>XenZhaoThrust3.luaobj</c> test <c>HIT_Critical</c> (name hits;
    /// <c>HIT_Dodge</c> is from the earlier marker). Reconstruction: a spell scope carries no hit result of its own in this engine's model;
    /// retail's scope is not recovered.
    /// </summary>
    public Amberline.Simulation.Combat.BasicAttackOutcome? ReplacedSwing { get; set; }

    /// <summary>The spell a <c>BuffOnPreAttack</c> hook cast in place of the swing (<see cref="ReplacedSwing"/>): only that spell reads the roll.</summary>
    public string? SwingReplacedBy { get; set; }

    /// <summary>
    /// True when the server dropped the swing that is the client's current SpellCaster (a pending basic attack still in its
    /// windup, <see cref="ClientSwingMissileNetId"/>), and no 0x39 has carried that to the client yet. Until it is told, the
    /// client plays the swing to its cast frame (hit particles and sound on a target that takes no damage). The next 0x39
    /// then carries DestroyMissile, which also removes a shot whose launch the client reached first; without this flag the
    /// 0x39 carries nothing, because the hit is the server's and its shot must fly on. Set when the combat queue drops the
    /// hit, cleared by the 0x39, by the next 0x0D and by a cast start.
    /// </summary>
    public bool ClientSwingDropped { get; set; }

    /// <summary>
    /// The missile ID of the swing that is the client's current SpellCaster, 0 when there is none: set by the 0x0D, cleared
    /// by the 0x39, by a cast start and by the cancellation of its launched hit. A cast replaces the current SpellCaster (the
    /// client force-stops the swing and plays its cast frame), so a swing the server drops after that has nothing left to
    /// tell the client, and a DestroyMissile sent for it would find the spell's own missile instead
    /// (<see cref="ClientSwingDropped"/>). While it is set and the swing was not dropped, the server keeps the swing's hit: the
    /// stop forces the cast frame the client may not have reached yet (<c>CombatPackets.StandingSwingFlags</c>); a hit the
    /// server cancelled after its launch clears it, so no frame is forced for a shot the hook has retired.
    /// </summary>
    public uint ClientSwingMissileNetId { get; set; }

    /// <summary>
    /// Seconds until the cast frame of the uncancellable spell windup this hero's client is running, counted down by
    /// <c>SpellSystem.Tick</c>; zero or negative when there is none. A cast packet starts it
    /// (<c>ProtocolSpellEffectSink.SendCast</c> via <c>SpellWindup.Begin</c>): the client's <c>InitSpell</c> sets
    /// <c>mTimeCast = now + ExtraCastTime + DesignerCastTime</c> on receipt, and until that cast frame a plain Stop is refused
    /// (<c>IsCancelBlocked</c>, <c>CantCancelWhileWindingUp</c>). The countdown runs on to <c>-SpellWindup.SettleSeconds</c>
    /// so an order held through the windup can wait for the stop to be accepted. <c>SpellWindup</c> is the one place that reads
    /// and writes it.
    /// </summary>
    public double CastWindupRemaining { get; set; }

    /// <summary>
    /// Seconds until the cast frame of the channel this hero's client is running (a spell with <c>CantCancelWhileChanneling</c> and a
    /// ChannelDuration), counted down by <c>SpellSystem.Tick</c>; zero or negative once the channel is open. The client opens it at the
    /// cast frame, so its refusal starts there and not at the cast packet. <c>SpellWindup</c>
    /// is the one place that reads and writes it.
    /// </summary>
    public double CastChannelOpensIn { get; set; }

    /// <summary>
    /// Seconds until the channel's end, the cast packet's mTimeSpellEnd (<c>ExtraCastTime + DesignerTotalTime</c>), counted down by
    /// <c>SpellSystem.Tick</c>; the window runs on to <c>-SpellWindup.SettleSeconds</c>. Null: no channel window (a hero that never cast one).
    /// </summary>
    public double? CastChannelEndsIn { get; set; }

    /// <summary>The channel's spell carries <c>CannotBeSuppressed</c>: its stop is refused even while the hero is suppressed or cannot cast.</summary>
    public bool CastChannelCannotBeSuppressed { get; set; }

    /// <summary>
    /// The script channels this unit runs, summed over every spell host bound to the match (a champion's, the summoner's, an item's). Only
    /// <c>BbScriptChannels</c> writes it: +1 when a channel starts, -1 when it stops. Reconstruction (the stock server's rule is in no binary we hold):
    /// while any channel runs, the unit starts no automatic attack of its own (owner hand-test, 26 October 2026: "any channel should disable
    /// autoattacking"). <c>HeroAttackOrderSystem.Blocked</c> and <c>MatchGameplayHooks.IsActionBlocked</c> read it; a player's new order still ends
    /// a channel that the client lets it end.
    /// </summary>
    public int ActiveScriptChannels { get; internal set; }

    /// <summary>
    /// Reconstruction (the stock server's stop is not in any binary we hold): whether the cast clip of a hero's last windup child cast (<c>BBSpellCast</c> of an extra-slot spell with a cast time of its own:
    /// Mystic Shot, Essence Flux, Spiral Blade, Rocket Grab; not one with ForceCastingOrChannelling, Yorick's summons, which the client runs as a SpellCaster) may still be playing. The clip outlasts the quarter-second windup, so the first
    /// order after the cast frame stops it (<c>SpellSystem.CancelOrder</c> sends the blended StopAnimation), which keeps the hero from sliding
    /// in it (owner playtest 25 September 2026, #7 and #22). Set by <c>BbChampionModule.CastChild</c>; cleared by that stop and by death.
    /// </summary>
    public bool CastClipActive { get; set; }

    /// <summary>
    /// Reconstruction (batch 476): the animation plays and stops the protocol sink has sent for this unit. A use channel that plays a looped
    /// animation records the value, and its end releases the loop only while the value still matches, so a later play or stop is never doubled.
    /// </summary>
    public uint AnimationSerial { get; set; }

    /// <summary>Loaded character animation weights; empty snapshots use the base attack.</summary>
    public IReadOnlyList<float> BasicAttackAnimationProbabilities { get; set; } = [];

    /// <summary>Number of champion kills credited to this hero.</summary>
    public int ChampionKills { get; set; }

    /// <summary>Number of champion deaths recorded for this hero.</summary>
    public int ChampionDeaths { get; set; }

    /// <summary>Number of champion assists recorded for this hero.</summary>
    public int ChampionAssists { get; set; }

    /// <summary>Lane minions last-hit by this hero or an owned unit; excludes pets and wards.</summary>
    public int LaneMinionsKilled { get; set; }

    /// <summary>Enemy pets, wards and other non-lane minions last-hit by this hero or an owned unit.</summary>
    public int OtherMinionsKilled { get; set; }

    /// <summary>Individual jungle monsters last-hit by this hero or an owned unit.</summary>
    public int NeutralMinionsKilled { get; set; }

    /// <summary>Current consecutive champion-kill streak.</summary>
    public int KillStreak { get; set; }

    // Native ProccessKillingSpree counts a spree at two consecutive kills;
    // its spoken announcement starts separately at three.
    public int KillingSprees { get; set; }
    public int LargestKillingSpree { get; set; }

    public int TurretsKilled { get; set; }
    public int InhibitorsKilled { get; set; }
    public int NexusesKilled { get; set; }

    /// <summary>Dominion: the capture points this champion captured and neutralised (the ODIN collection's NODE_CAPTURE, NODE_NEUTRALIZE).</summary>
    public int NodeCaptures { get; set; }
    public int NodeNeutralizes { get; set; }

    /// <summary>
    /// Batch 518, Dominion personal score (DominionPlayerScore): the combat and objective parts (the ODIN collection's COMBAT_PLAYER_SCORE and
    /// OBJECTIVE_PLAYER_SCORE; TOTAL_PLAYER_SCORE is their sum), and the counts the score events keep (NODE_CAPTURE_ASSIST,
    /// NODE_NEUTRALIZE_ASSIST, TEAM_OBJECTIVE; the kills on a point, which the ODIN collection does not hold, for the match result).
    /// </summary>
    public int OdinCombatScore { get; set; }
    public int OdinObjectiveScore { get; set; }
    public int NodeCaptureAssists { get; set; }
    public int NodeNeutralizeAssists { get; set; }
    public int NodeKillsOffense { get; set; }
    public int NodeKillsDefense { get; set; }
    public int TeamObjectives { get; set; }

    /// <summary>Current consecutive champion-death streak.</summary>
    public int DeathStreak { get; set; }

    /// <summary>
    /// Script-driven heals that raised this unit's health since the last delivery flush. The
    /// stock client's heal routine announces each one as UnitApplyHeal; the replication
    /// layer drains this list (filled by <c>Healing.Apply</c>).
    /// Reconstruction: retail's send timing for these is not recovered (the marker's claim that IncHealth sends at the call is unverified); this server batches the sends per delivery flush.
    /// </summary>
    public List<HealAnnouncement> PendingHealAnnouncements { get; } = [];

}
