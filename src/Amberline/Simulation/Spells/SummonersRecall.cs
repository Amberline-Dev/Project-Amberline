using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Simulation.Spells;

public sealed partial class SpellSystem
{
    // Batch 164: SpellSystem's hand-written BreakCastStealth is gone. What ends Shaco's and Vayne's stealth on a cast is
    // their own shipped BuffOnSpellCast. Spells/Deceive.luaobj opens with
    // `BBIf SpellVars.CastingBreaksStealth == true -> BBSpellBuffRemoveCurrent(Owner)`, then
    // `BBElseIf SpellVars.CastingBreaksStealth == false -> BBElseIf SpellVars.DoesntTriggerSpellCasts != true -> remove`;
    // Spells/VayneTumbleFade.luaobj is `BBIf SpellVars.CastingBreaksStealth == true -> remove`, then
    // `BBElseIf SpellVars.DoesntTriggerSpellCasts != true -> remove`. So a cast that says CastingBreaksStealth true
    // (Recall.luaobj, SummonerSmite, SummonerHeal, SummonerExhaust, SummonerMana, SummonerTeleport, SummonerRally) takes
    // the stealth and one that says DoesntTriggerSpellCasts true (SummonerFlash and SummonerClairvoyance and
    // SummonerBoost say both this and CastingBreaksStealth false) keeps it. Raising the cast hooks here is the only
    // thing that had to be added for it.

    private bool HandleSummonerCast(Entity hero, SpellCastRequest request, double now)
    {
        if ((uint)request.Slot >= SummonerSlots.Length)
            return false;
        // ZhonyasRingShield explicitly seals both summoner slots. This gate
        // precedes every summoner family, including the advanced spells.
        if (hero.Buffs.Any(buff => buff.Name == "ZhonyasRingShield")) return false;
        // The same gate for a slot a script sealed (SpellSlotSeals); a queued cast meets it again at the landing.
        if (hero.SpellSeals.IsSealed(true, request.Slot)) return false;
        EnsureSpellLists(hero);
        // The slot holds a loadout display name, or the script name of a spell a shipped block wrote into it
        // (TeleportCancel, SummonerTeleport's BBSetSpell). Only the thirteen loadout spells are canonicalised;
        // anything else is already a script name.
        var spellName = SummonerLoadout.TryCanonicalFromDisplay(hero.SummonerSpells[request.Slot]) ??
                        hero.SummonerSpells[request.Slot];
        if (hero.SummonerCooldowns[request.Slot] > 0.0)
            return false;

        // Batch 142 step 2: the cast runs the shipped Summoner*.luaobj bound to (hero, summoner slot). What the engine
        // still decides is what no script does — that the unit is a legal target and in reach, the cast announcement and
        // the cooldown clock. Everything the spell does is the script's SelfExecute / TargetExecute.
        return RunSummonerScript(hero, request, spellName, now);
    }

    /// <summary>
    /// Runs the shipped <c>Summoner&lt;Name&gt;.luaobj</c> for this cast (batch 142 step 2). False when the request is
    /// refused or no script answers it; the ten core summoners have no other answering path any more.
    /// <para>
    /// The engine keeps what no script decides: that the unit is a legal target and in reach (the client's target and
    /// the inibin's target flags), which units a self or area cast reaches (the same set the hand-written path applied),
    /// the cast announcement, and the cooldown clock. The spell's own work is the script's <c>SelfExecute</c> (once, at
    /// the cast frame) and <c>TargetExecute</c> (once per unit reached).
    /// </para>
    /// <para>
    /// Batch 142 part 2: the hand-written casts' validation gates are a trust boundary — the request is client input —
    /// so every finite-input, reach and legality check of those casts runs here, before any script. A refused request
    /// spends no cooldown and runs no hook. What the gates cover is the cast point in the map and in reach
    /// (Clairvoyance's in-map test and its 25000), the unit target's legality and reach, the 64-row buff table holding
    /// the rows the script adds (<see cref="SummonerBuffsFit"/>), and Flash's landing existing (a clear point the body
    /// fits) with a staging point for the short teleport. The script's own <c>CanCast</c> runs
    /// after them: it is the spell's rule, not the request's.
    /// </para>
    /// </summary>
    private bool RunSummonerScript(Entity hero, SpellCastRequest request, string spellName, double now)
    {
        var module = SummonerScripts;
        if (module is null || LoadoutHooks?.OwnerFor(hero) is not { } owner) return false;
        if (module.Binding(hero, request.Slot, owner) is not { } binding) return false;

        if (!SummonerCasterGate(hero, spellName)) return false;
        var point = new Position2(request.Position.X, request.Position.Z);
        // Reconstruction: the cast range is the inibin's 25000; the stock client clamps the minimap click, the in-map test only
        // refuses a hand-made request outside the navigation bounds.
        if (spellName == "SummonerClairvoyance" &&
            (!IsInMap(point) || CoreDistance(hero.Position, point) > 25000.0)) return false;
        var reached = SummonerScriptTargets(hero, request, spellName);
        if (reached is null) return false;
        // The one unit the cast announcement names: the unit-targeted summoners (Ignite, Exhaust, Smite). A self or an
        // area cast names no unit — Heal's area reaches allies and Fortify's reaches turrets, and both announce the
        // ground at the cast point with no target, as the hand-written casts sent them.
        var target = AnnouncesUnitTarget(spellName) ? reached[0] : null;
        if (!SummonerBuffsFit(hero, spellName, reached)) return false;

        // Flash's own gate, the one the hand-written cast ran before it announced anything: the landing has to exist,
        // and a landing the stock client would drop (a move inside ClientTeleportDeadband) has to have a staging point
        // to be sent through.
        Position2? landing = null;
        if (spellName == "SummonerFlash")
        {
            landing = FlashDestination(hero, point);
            if (landing is not { } flashLanding) return false;
            if (_effects.IsReferenceReady(hero) && !OutsideTeleportDeadband(hero.Position, flashLanding) &&
                TeleportStagingPoint(hero, flashLanding) is null) return false;
        }

        if (!module.CanCast(hero, request.Slot, binding)) return false;

        var cooldown = module.AdjustCooldown(hero, request.Slot, binding);
        hero.SummonerCooldowns[request.Slot] = cooldown;

        // The point the script reads as its CastPos / TargetPos (BBGetCastSpellTargetPos) and the point the announcement
        // names: the unit's ground for a unit cast, the ground at the client's requested point otherwise — the points the
        // hand-written casts named. Flash is the one exception: its script reads the raw cast point and clamps it to 475
        // along its own facing (SummonerFlash.luaobj SelfExecute), while the announcement names the landing that clamp
        // and the placement correction produce, at y 0 as the hand-written cast announced it.
        var position = spellName == "SummonerFlash"
            ? new Position3(point.X, 0.0, point.Z)
            : Ground(target is null ? point : target.Position);
        var end = landing is { } at ? new Position3(at.X, 0.0, at.Z) : position;
        // Revive is the one summoner whose cast answer follows its script: TargetExecute stands the hero up
        // (BBReincarnateHero) and the announcement has to describe the respawned hero at the fountain, or the client
        // reconciles the new life back to the corpse (the ordering the hand-written cast kept). Its script reads no
        // cast position of its own — the particle binds to Owner — so the pre-respawn points still reach the hooks.
        var announceAfterScript = spellName == "SummonerRevive";
        if (!announceAfterScript)
            SendSummonerCast(hero, request, spellName, cooldown, hero.Position, end, target);

        // CharOnSpellCast and BuffOnSpellCast of the caster's script buffs, the same boundary a champion cast crosses
        // (BbChampionModule sets CastTarget the same way before it). The one summoner that has such a hook is
        // Teleport_DeathRemoval: on a TeleportCancel cast it takes the Teleport_Target / Teleport_Turret marker off the
        // unit Teleport was arriving at and removes itself. The summoner slots are not champion slots 0..3, so the rank
        // is pinned to 0 rather than read from SpellRanks. Every module answers, not only the summoner module's own
        // runtime: Shaco's Deceive and Vayne's VayneTumbleFade end on this cast (their own BuffOnSpellCast) and sit on
        // the champion module. Batch 164 replaced the hand-written BreakCastStealth with that script hook.
        _championHooks.OnNamedSpellCast(hero, request.Slot, spellName,
            SummonerTargetsUnit(hero, request.Slot) ? _world.Get(request.TargetNetId) : null);

        module.SelfExecute(hero, request.Slot, binding, position, end);
        foreach (var unit in reached) module.TargetExecute(hero, request.Slot, binding, unit, position, end);
        if (announceAfterScript)
            SendSummonerCast(hero, request, spellName, cooldown, hero.Position,
                new Position3(hero.Position.X, 0.0, hero.Position.Z));
        return true;
    }

    /// <summary>
    /// Whether the cast announcement names the one unit the request targeted (Ignite, Exhaust, Smite, Teleport, Garrison) rather
    /// than the ground at the cast point with no unit: the self and area summoners announce without a target.
    /// </summary>
    private static bool AnnouncesUnitTarget(string spellName) =>
        spellName is "SummonerDot" or "SummonerExhaust" or "SummonerSmite" or "SummonerTeleport" or "SummonerOdinGarrison";

    /// <summary>
    /// The units a summoner cast reaches. Null when the request is refused (no legal unit target). A unit-targeted
    /// summoner reaches the one unit the client named; a self or area summoner reaches the set its hand-written cast
    /// applied the effect to — which is the set the script's own <c>TargetExecute</c> reads as <c>Target</c>, since none
    /// of the shipped summoner scripts enumerates for a single-target effect.
    /// </summary>
    private Entity[]? SummonerScriptTargets(Entity hero, SpellCastRequest request, string spellName)
    {
        // Batch 502: Garrison (SpellData*TargettingType 1) is unit-targeted too; it was cast as a self cast, so its TargetExecute
        // read the caster and the OdinGuardianBuff test never met a guardian.
        if (AnnouncesUnitTarget(spellName))
        {
            var target = _world.Get(request.TargetNetId);
            if (target is null || !SummonerTargetLegal(hero, spellName, target)) return null;
            if (Distance(hero.Position, target.Position) > SummonerCastRange(spellName)) return null;
            return [target];
        }
        if (spellName == "SummonerHeal")
        {
            const double radius = 600.0;
            var allies = _world.Entities.Values
                .Where(unit => unit.Alive && unit.Team == hero.Team && !unit.IsWard &&
                               (unit.UnitType is UnitType.Hero or UnitType.Minion or UnitType.Monster) &&
                               Distance(unit.Position, hero.Position) <= radius &&
                               _combat.IsTargetable(unit, hero))
                .ToList();
            if (!allies.Contains(hero)) allies.Insert(0, hero);
            // Reconstruction (SummonerBuffsFit): retail's outcome for an ally whose table cannot hold Heal's own marker is unrecovered; it is skipped as one
            // recipient — no heal, FX or assist is emitted for a unit that cannot hold the required row.
            return allies.Where(unit => BuffLifecycle.CanAddNamedBuffs(unit, "SummonerHealCheck")).ToArray();
        }
        if (spellName == "SummonerFortify")
        {
            // Batch 220 (owner decision, 25 October 2026): the script's TargetExecute tests only the target's type (CO_IS_TYPE_TURRET),
            // and the inibin's area (AffectFriends | AffectTurrets, CastRadius 25000) reaches every friendly turret on Map1, the
            // fountain's included. The distance test is the whole map, so it is not repeated here.
            return _world.Entities.Values
                .Where(unit => unit.Alive && unit.Team == hero.Team && unit.UnitType == UnitType.Turret)
                .ToArray();
        }
        // A self cast: the caster is the unit TargetExecute reads as Target.
        return [hero];
    }

    private void SendSummonerCast(Entity hero, SpellCastRequest request,
        string spellName, double cooldown, Position2 launch, Position3 target,
        Entity? targetEntity = null)
    {
        var data = _data.Spell(spellName);
        // RevealSpellCaster with the one listed unit target (Ignite, Exhaust, Smite), as for champion spells.
        if (targetEntity is not null && data.RevealsChampion) _combat.RevealAttack(hero, targetEntity);
        if (!_effects.IsReferenceReady(hero)) return;
        var targets = targetEntity is null
            ? Array.Empty<SpellCastTarget>()
            : [new SpellCastTarget(targetEntity.NetId, target, 0)];
        var cast = new SpellCastInfo(ProtocolHash(data.Name), hero.NetId, 1, hero.NetId, 0,
            target, target, targets, (float)cooldown, 0.0f, (byte)request.Slot,
            new Position3(launch.X, 0.0, launch.Z),
            AttackSpeedModifier: 1.0f, DesignerCastTime: (float)data.CastTime,
            ExtraCastTime: 0.0f, DesignerTotalTime: (float)data.TotalTime);
        _effects.SendCast(hero, cast, _world.Clock.SyncId());
        _effects.SendCooldown(hero, request.Slot, cooldown, summoner: true);
        // The cast particle is the script's own BBSpellEffectCreate (every shipped summoner script draws
        // Summoner_Cast.troy from its SelfExecute or TargetExecute), so the engine does not draw it a second time.
    }


    // The CastRange of the summoner inibins: SummonerDot 600 (IgniteRange), SummonerExhaust 550, SummonerSmite 783,
    // SummonerTeleport 25000.
    private const double ExhaustRange = 550.0;
    private const double SmiteRange = 783.0;
    private const double TeleportRange = 25000.0;
    // SummonerOdinGarrison.inibin CastRange 1250, CastRangeUseBoundingBoxes 0 (centre distance).
    private const double GarrisonRange = 1250.0;

    /// <summary>
    /// Whether <paramref name="target"/> is a legal unit for the summoner, everything the cast checks except the distance.
    /// Each unit-target summoner reads its own <c>SpellData*Flags</c> through the shared gate (<see cref="ShippedFlagsAdmit"/>):
    /// SummonerDot AffectEnemies | AffectNeutral | AffectMinions | AffectHeroes, SummonerExhaust AffectEnemies | AffectNeutral |
    /// AffectHeroes, SummonerSmite AffectEnemies | AffectNeutral | AffectMinions | NotAffectSelf, so Exhaust takes an enemy
    /// champion and Smite a neutral monster or an enemy minion. A hero clone is a champion to the gate
    /// (<see cref="BbSpellTargeting.KindFlag"/>), so Exhaust takes Shaco's clone and Smite does not. None of the three carries the
    /// ward opt-in, so a ward (and the Rally beacon, a ward-flagged minion) is not a legal target. Teleport takes an allied minion,
    /// ward or turret (<see cref="TeleportTargetLegal"/>). An enemy must be visible to the caster's team.
    /// </summary>
    private bool SummonerTargetLegal(Entity hero, string spellName, Entity target)
    {
        if (!target.Alive) return false;
        // SummonerTeleport's own target lock (Teleport_Target) sets SetInvulnerable on the unit the cast lands at and is added
        // BUFF_RENEW_EXISTING, so the second teleport at a still-locked target is the shipped script's own case. Invulnerability
        // is a damage rule (see IsStasis) and this cast deals none, so the teleport takes the unit the client can click
        // (TeleportTargetLegal) rather than the full targetability gate; every other summoner keeps it.
        // Batch 502: Garrison takes the client's own gate too (the shipped flags test, the valid-target test), not this
        // one. Its flags carry AffectUseable, which lets a useable unit through though it is not targetable to the caster's team (the capture
        // point guardian, encounter Targetable 0), and the guardian's OdinGuardianBuff sets SetInvulnerable on every stats update, which the
        // client's gate never reads.
        if (spellName is not ("SummonerTeleport" or "SummonerOdinGarrison") && !_combat.IsTargetable(target, hero)) return false;
        // The same sight rule as every unit-targeted champion cast (CastTargetAvailable): an enemy the caster's team cannot see is
        // not a legal target, so neither the cast nor the walk toward it (which would reveal its fogged position) happens, and a
        // queued approach ends as soon as the target steps into fog.
        if (WorldState.IsEnemy(hero, target) && _targetVisible?.Invoke(hero, target) == false) return false;
        return spellName switch
        {
            "SummonerDot" or "SummonerExhaust" or "SummonerSmite" or "SummonerOdinGarrison" => ShippedFlagsAdmit(hero, spellName, target),
            "SummonerTeleport" => TeleportTargetLegal(hero, target),
            _ => false,
        };
    }

    /// <summary>
    /// The summoner's own <c>SpellData*Flags</c> through the shared gate (<see cref="BbSpellTargeting.Targetable"/>, then
    /// <see cref="BbSpellTargeting.RejectTarget"/>). A ward needs the ward opt-in bit (0x04000000), which SummonerTeleport and
    /// SummonerOdinGarrison carry. SummonerOdinGarrison's 201493524 is Instacast | NonDispellable | AffectEnemies | AffectFriends |
    /// AffectMinions | AffectTurrets | AffectWards | AffectUseable: an allied or enemy minion, turret or ward, never a neutral one (no
    /// AffectNeutral), so a neutral point is refused and an owned point's guardian is taken through AffectUseable. Its script refunds
    /// a cast on anything else (TargetExecute: no OdinGuardianBuff on the target, BBSetSlotSpellCooldownTime 1).
    /// </summary>
    private bool ShippedFlagsAdmit(Entity hero, string spellName, Entity target)
    {
        var flags = BbSpellTargeting.Flags(_data.Spell(spellName));
        if (target.IsWard && ((long)flags & WardOptInFlag) == 0) return false;
        var names = BbSpellTargeting.Names(flags);
        return BbSpellTargeting.Targetable(hero, target, names) && BbSpellTargeting.RejectTarget(hero, target, names) is null;
    }

    /// <summary>
    /// Whether <paramref name="target"/> is a unit SummonerTeleport may be cast on, read from the spell's own
    /// <c>SpellData*Flags</c> (201500676 = Instacast | AffectFriends | NotAffectSelf | AffectMinions | AffectTurrets |
    /// the ward opt-in | AffectUseable): an allied minion, ward or turret. There is no AffectHeroes, AffectEnemies or
    /// AffectNeutral, so never a hero, an enemy or a neutral monster. The shipped script names no unit kind at all —
    /// <c>SummonerTeleport.TargetExecute</c> scatters the landing around whatever the cast reached — so the inibin is
    /// the whole rule, and the ward bit (0x04000000) is what makes a ward clickable here as for Lee Sin's Safeguard
    /// (<see cref="CastTargetAvailable"/>).
    /// <para>
    /// The fountain turret is an allied turret like any other (batch 220, owner decision 25 October 2026): the shipped
    /// <c>SummonerTeleport.TargetExecute</c> only types the target (CO_IS_TYPE_AI, then CO_IS_TYPE_TURRET), and nothing in
    /// the script or the inibin names the shrine.
    /// </para>
    /// </summary>
    private bool TeleportTargetLegal(Entity hero, Entity target)
    {
        // The unit the client cannot click (the same clauses BbSpellTargeting.Admits applies to a scan: this spell carries no
        // AffectUntargetable). Invulnerability is deliberately not among them — the cast's own Teleport_Target sets it and
        // BUFF_RENEW_EXISTING is the shipped script's answer to a target that is still locked.
        if (target.Protected || target.AnyBuffOrStatus(buff => buff.Payload.Untargetable) ||
            target.NotTargetableTo(hero)) return false;
        return ShippedFlagsAdmit(hero, "SummonerTeleport", target);
    }

    private static double SummonerCastRange(string spellName) => spellName switch
    {
        "SummonerDot" => IgniteRange,
        "SummonerExhaust" => ExhaustRange,
        "SummonerSmite" => SmiteRange,
        // SummonerTeleport.inibin CastRange 25000: the whole map, so the reach never refuses a cast.
        "SummonerTeleport" => TeleportRange,
        "SummonerOdinGarrison" => GarrisonRange,
        _ => double.PositiveInfinity,
    };

    /// <summary>The canonical spell name of a summoner slot; null for a slot that holds no spell or an invalid slot.</summary>
    private string? SummonerSpellName(Entity hero, int slot)
    {
        if ((uint)slot >= SummonerSlots.Length || slot >= hero.SummonerSpells.Count) return null;
        var raw = hero.SummonerSpells[slot];
        return raw.Length == 0 ? null : SummonerLoadout.TryCanonicalFromDisplay(raw) ?? raw;
    }

    private SpellData? SummonerSpellData(Entity hero, int slot) =>
        SummonerSpellName(hero, slot) is { } name ? _data.Spell(name) : null;

    /// <summary>
    /// The target of a unit-targeted summoner (Ignite, Exhaust, Smite) request that the cast would accept except that the target is
    /// farther than the spell's range; null when the request is something else or is refused for another reason. Such a request
    /// walks into range like any unit-targeted champion spell instead of being refused.
    /// </summary>
    private Entity? SummonerOutOfRangeTarget(Entity hero, SpellCastRequest request)
    {
        if (!request.IsSummoner) return null;
        var name = SummonerSpellName(hero, request.Slot);
        if (name is not ("SummonerDot" or "SummonerExhaust" or "SummonerSmite" or "SummonerOdinGarrison")) return null;
        if (!SummonerCasterGate(hero, name) || hero.SummonerCooldowns[request.Slot] > 0.0 ||
            hero.Buffs.Any(buff => buff.Name == "ZhonyasRingShield")) return null;
        var target = _world.Get(request.TargetNetId);
        return target is not null && SummonerTargetLegal(hero, name, target) &&
               Distance(hero.Position, target.Position) > SummonerCastRange(name) ? target : null;
    }

    /// <summary>
    /// Recall (<see cref="RecallSlot"/>, Spells/Recall.luaobj; batch 179). The engine keeps the request gate (a trust boundary: alive,
    /// not already recalling, no stun, silence or taunt, the buff table holds the Recall row), the cast announcement and the stand-still
    /// of the cast. The windup, the channel, its cancels on damage, on a fresh order or a spell and on a stop, the teleport home and the
    /// effects are the script's: <see cref="BbSummonerModule.BeginRecall"/> starts the channel at the cast frame (ChannelingStart).
    /// </summary>
    private bool HandleRecall(Entity hero, SpellCastRequest request, double now)
    {
        if (!hero.Alive || RecallRemaining(hero) > 0.0 || RecallDisabled(hero) ||
            !BuffLifecycle.CanAddNamedBuffs(hero, _data.RecallSpellName))
            return false;
        if (SummonerScripts is not { } module || LoadoutHooks?.OwnerFor(hero) is not { } owner) return false;
        if (module.RecallBinding(owner) is not { } binding) return false;
        // CharOnSpellCast and BuffOnSpellCast of the caster's script buffs, the same boundary every other cast crosses
        // (RunSummonerScript raises it for the summoner slots; a champion cast gets it through MatchGameplayHooks.OnSpellCast).
        // Spells/Recall.luaobj sets CastingBreaksStealth true, so the stealth buffs' own BuffOnSpellCast ends them.
        _championHooks.OnNamedSpellCast(hero, RecallSlot, _data.RecallSpellName, null);
        // The recall's stop is a one-point path, installed on receipt (the client's follow-path routine): stopped and seated on its anchor.
        var anchor = _movement.InstallStop(hero)[0];
        hero.AttackTarget = null;
        hero.AttackMoveGoal = null;
        hero.HeroPursuit = null;
        hero.AnchorPath = [anchor];
        hero.LastAnchorTime = now;

        var data = _data.Spell(_data.RecallSpellName);
        // Batch 460: the cast point carries the ground height (it was 0).
        var position = Ground(anchor);
        // The packet's total is the cast time plus the script's ChannelDuration, the rule every channelled cast follows
        // (BbChampionModule.Channelled): the client's SpellCaster runs through the channel, not one cast time short of it.
        if (_effects.IsReferenceReady(hero))
        {
            var cast = new SpellCastInfo(ProtocolHash(data.Name), hero.NetId, 1,
                hero.NetId, 0, position, position, Array.Empty<SpellCastTarget>(),
                0.0f, 0.0f, RecallSlot, position,
                AttackSpeedModifier: 1.0f, DesignerCastTime: (float)data.CastTime,
                ExtraCastTime: 0.0f,
                DesignerTotalTime: (float)(data.CastTime + BbScriptChannels.DurationOf(binding.Script)),
                StartCastTime: 0.0f, Flags: 0x04);
            _effects.SendCast(hero, cast, _world.Clock.SyncId());
            _effects.SendWaypoints(hero, [anchor], _world.Clock.SyncId());
        }
        module.BeginRecall(hero, binding, position, data.CastTime);
        return true;
    }

    /// <summary>
    /// The seconds of Recall the hero has left: the windup to the cast frame plus the channel's own time. 0 when it does not recall.
    /// The clock is the summoner module's (there is no engine timer for Recall any more).
    /// </summary>
    public double RecallRemaining(Entity hero) => SummonerScripts?.RecallRemaining(hero) ?? 0.0;

    /// <summary>The TeleportHome effect the running Recall holds (its <c>EffectIDVar</c>), 0 when there is none.</summary>
    public uint RecallEffectId(Entity hero) => SummonerScripts?.RecallEffectId(hero) ?? 0u;

    /// <summary>
    /// Ends the hero's Recall (a zombie's revival, a death the scripts do not stop): the windup is dropped or the channel ends as a
    /// cancel, and the script's ChannelingCancelStop removes the TeleportHome effect and the Recall buff. The teleport home is
    /// the script's own ChannelingSuccessStop (BBTeleportToKeyLocation, the camera, teleportarrive.troy). Batch 179.
    /// </summary>
    public bool CancelRecall(Entity hero)
    {
        ArgumentNullException.ThrowIfNull(hero);
        return SummonerScripts?.CancelRecall(hero) == true;
    }

    private Position2? FlashDestination(Entity hero, Position2 requested)
    {
        var origin = hero.Position;
        var dx = requested.X - origin.X;
        var dz = requested.Z - origin.Z;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        if (!double.IsFinite(distance)) return null;
        if (distance <= 1e-9) return ClientPoint(origin);

        var landing = CorrectTeleportDestination(hero, requested, FlashRange);
        if (landing != ClientPoint(origin)) return landing;
        // A clear cast point that quantizes onto the start keeps the
        // short-teleport shim; a correction back onto the start rejects.
        var travel = Math.Min(FlashRange, distance);
        var target = ClientPoint(new Position2(origin.X + dx / distance * travel,
            origin.Z + dz / distance * travel));
        return landing == target && FlashPointClear(hero, target) ? landing : null;
    }

    /// <summary>
    /// Resolves a BBTeleportToPosition landing (Flash, Ezreal E, Shaco Q/R)
    /// after the caller applies its own spell range.
    /// </summary>
    internal Position2 CorrectTeleportDestination(Entity hero, Position2 requested, double maxRange = double.PositiveInfinity)
    {
        // Batch 460: a held unit (Dominion's setup) blinks no farther than the edge of its hold (MovementSystem.HeldInside).
        requested = _movement.HeldInside(hero, requested);
        var origin = hero.Position;
        var dx = requested.X - origin.X;
        var dz = requested.Z - origin.Z;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        if (!double.IsFinite(distance) || distance <= 1e-9) return ClientPoint(origin);
        var travel = Math.Min(maxRange, distance);
        var ux = dx / distance;
        var uz = dz / distance;
        var target = ClientPoint(new Position2(origin.X + ux * travel,
            origin.Z + uz * travel));
        // The native handler spiral-searches around the requested cell, not
        // back along the cast ray. Terrain, the 140u turret stamp (playtest
        // B2 BJ4/YV11) and a pocket too tight for the body all take the
        // nearest client-grid point that fits the whole body (owner decision,
        // playtest B3 #3/#9, 26 Sept 2026); an open point is unchanged.
        var grid = _movement.Navigation;
        var body = _movement.MovementRadius(hero);
        var landing = grid.ClosestSummonPlacement(target, body);
        if (FlashPointClear(hero, landing)) return landing;
        if (landing != target)
        {
            dx = landing.X - origin.X;
            dz = landing.Z - origin.Z;
            travel = Math.Sqrt(dx * dx + dz * dz);
            if (travel <= 1e-9) return ClientPoint(origin);
            ux = dx / travel;
            uz = dz / travel;
        }
        // Reconstruction: a unit on the landing keeps the 10-unit backoff toward the caster, limited to floor that fits the body
        // (the native BBTeleportToPosition correction is only known to spiral-search, its occupied-landing rule is not recovered).
        for (travel = Math.Max(0.0, travel - 10.0); travel > 0.0;
             travel = Math.Max(0.0, travel - 10.0))
        {
            var candidate = ClientPoint(new Position2(origin.X + ux * travel,
                origin.Z + uz * travel));
            if (FlashPointClear(hero, candidate) && grid.FitsSummonPlacement(candidate, body))
                return candidate;
        }
        return ClientPoint(origin);
    }

    private bool FlashPointClear(Entity hero, Position2 candidate)
    {
        if (!_movement.Navigation.IsMovementWalkable(candidate)) return false;
        return !_world.Entities.Values.Any(unit => unit != hero && unit.Alive &&
            !unit.IsWard && unit.Radius > 0.0 &&
            unit.UnitType is UnitType.Hero or UnitType.Minion or UnitType.Turret or
                UnitType.Monster or UnitType.Structure &&
            Distance(candidate, unit.Position) < hero.Radius + unit.Radius);
    }

    /// <summary>
    /// The staging point of a short teleport: a point outside <c>ClientTeleportDeadband</c> of both the launch and the
    /// landing, clear of bodies and terrain, that the move can be sent through in two packets. Null when the move is not
    /// short (nothing to stage), or when no such point exists — the caller refuses the move then.
    /// <para>
    /// Reconstruction: the stock client applies a teleport packet only when its teleport id is new and the unit is not within
    /// <c>ClientTeleportDeadband</c> of the packet's point (the teleport branch of the waypoint handler in the client,
    /// the waypoint handler loads the literal 50.0, not the grid cell size, passes it to the
    /// unit's position setter and tests the result through the distance helper, whose form was not read), so a shorter
    /// move sent as one packet is dropped on the client. Retail's server packet for such a
    /// move is not in the shipped files, so the two-packet staging (the first point outside the dead band) is kept as the form
    /// every client applies. The rule is the wire's, so every script teleport goes through it (<c>BbScriptMoves.Teleport</c>),
    /// not only Flash's.
    /// </summary>
    internal Position2? ShortTeleportStaging(Entity unit, Position2 launch, Position2 destination) =>
        _effects.IsReferenceReady(unit) && !OutsideTeleportDeadband(launch, destination)
            ? TeleportStagingPoint(unit, destination)
            : null;

    private Position2? TeleportStagingPoint(Entity hero, Position2 destination)
    {
        var grid = _movement.Navigation;
        var center = grid.WorldToCell(destination);
        var maxRadius = checked((int)Math.Ceiling(FlashRange / grid.CellSize));
        for (var radius = 1; radius <= maxRadius; radius++)
        {
            var offsets = Enumerable.Range(1, radius - 1)
                .SelectMany(offset => new[] { offset, -offset })
                .Append(radius).Append(-radius).Prepend(0);
            foreach (var offset in offsets)
            foreach (var cell in new[]
            {
                new NavCell(center.Column + radius, center.Row + offset),
                new NavCell(center.Column - radius, center.Row + offset),
                new NavCell(center.Column + offset, center.Row + radius),
                new NavCell(center.Column + offset, center.Row - radius),
            })
            {
                if (!grid.IsInBounds(cell)) continue;
                var candidate = ClientPoint(grid.CellToWorld(cell));
                if (!OutsideTeleportDeadband(hero.Position, candidate) ||
                    !OutsideTeleportDeadband(candidate, destination) ||
                    Math.Max(Math.Abs(candidate.X - destination.X),
                        Math.Abs(candidate.Z - destination.Z)) > FlashRange ||
                    !FlashPointClear(hero, candidate)) continue;
                return candidate;
            }
        }
        return null;
    }

    private Position2 ClientPoint(Position2 point) => Geometry.ClientWaypoints([point],
        new NavigationBounds(_movement.Navigation.MinX, _movement.Navigation.MaxX,
            _movement.Navigation.MinZ, _movement.Navigation.MaxZ))[0];

    private static bool OutsideTeleportDeadband(Position2 first, Position2 second) =>
        Math.Abs(second.X - first.X) > ClientTeleportDeadband ||
        Math.Abs(second.Z - first.Z) > ClientTeleportDeadband;

    // Recall.inibin has no CanCastWhileDisabled: the owner's can-use callback refuses it while the caster is
    // Suppressed, cannot cast (Stun, Silence, Fear, Sleep, Suppression, stasis), Feared, Asleep or Taunted, but a root does
    // not matter (no CantCastWhileRooted). Taunt.luaobj also stops a running channel; the same set cancels a running recall.
    private static bool RecallDisabled(Entity hero) => hero.AnyBuffOrStatus(buff =>
        buff.Type is BuffType.Stun or BuffType.Silence or BuffType.Taunt or BuffType.Fear or BuffType.Sleep or
            BuffType.Suppression or BuffType.Invulnerability ||
        buff.Payload.ActionDisabled || buff.Payload.Silence || buff.Payload.Silenced);

    // Only SummonerFlash (CanMove) and SummonerRevive (dead owner) ship a
    // CanCastBuildingBlocks; both refuse a dashing owner, so neither queues.
    private static bool SummonerRefusesDash(Entity hero, int slot) =>
        slot < hero.SummonerSpells.Count && hero.SummonerSpells[slot] != "TeleportCancel" &&
        SummonerLoadout.CanonicalFromDisplay(hero.SummonerSpells[slot]) is "SummonerFlash" or "SummonerRevive";
}
