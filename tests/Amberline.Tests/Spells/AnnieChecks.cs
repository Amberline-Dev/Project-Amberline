using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amberline.Core;
using Amberline.Simulation.Spells.Champions;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Stats;

namespace Amberline.Tests;

/// <summary>
/// The Molten Shield recast check of the script-driven Annie and the shared replay helpers (fixtures, entity readers, recording
/// stand-ins for the services) that the other champion checks use. Until 19 October 2026 this file also held the replay of the
/// hand-written Annie against <c>annie-cast-corpus.json</c> and its shield-lifetime check; they went with the hand-written module.
/// See docs/maintenance/2026-10-19-w21-annie-script.md. The fixture file stays on disk as evidence, and the Annie cast
/// is now covered by the <c>scripting.bb-annie-*</c> checks.
/// </summary>
public static class AnnieChecks
{
    [Check("spells-annie-molten-shield-recast-renews")]
    public static void MoltenShieldRecastRenewsTheLiveBuff()
    {
        // MoltenShield.luaobj: BBSpellBuffAdd BuffAddType=BUFF_RENEW_EXISTING, MaxStack=1, Duration=15. A recast keeps the
        // live instance (same slot and state object, same buff effect) and takes the new rank's variables.
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default,
            orderChampion: "Annie", chaosChampion: "Orianna", solo: true, readySettleSeconds: 0);
        runtime.World.Clock.SetVirtual(0);
        runtime.Session.CreateWorld();
        var owner = runtime.World.Heroes().Single(hero => hero.Team == Team.Order);
        owner.Position = new Position2(7000, 7000);
        owner.SpellRanks[2] = 1;
        var request = new SpellCastRequest(2, false, new Position3(7000, 0, 7000), new Position3(7000, 0, 7000), owner.NetId);
        TestAssert.True(runtime.Spells.HandleCastRequest(owner, request, 0));
        var first = owner.Buffs.Single(buff => buff.Name == "MoltenShield");
        var armor = owner.Armor;
        runtime.Buffs.TickBuffs(owner, 5.0);
        TestAssert.Near(10, first.ExpiresAt, 1e-9, "5 of the 15 seconds elapsed");
        owner.SpellRanks[2] = 3;
        owner.SpellCooldowns[2] = 0;
        owner.Mana = owner.MaxMana;
        TestAssert.True(runtime.Spells.HandleCastRequest(owner, request, 5));
        var renewed = owner.Buffs.Single(buff => buff.Name == "MoltenShield");
        TestAssert.True(ReferenceEquals(first, renewed), "the recast renews the same buff instance");
        TestAssert.Near(15, renewed.ExpiresAt, 1e-9, "the duration is renewed to the script's 15 seconds");
        TestAssert.Near(30, renewed.Payload.Armor, 1e-9, "rank 3 variables replace rank 1");
        TestAssert.Near(armor + 20, owner.Armor, 1e-9, "armor follows the renewed rank");
        TestAssert.Equal(1, owner.Buffs.Count(buff => buff.Name == "MoltenShield"));
    }

    /// <summary>
    /// Summons Tibbers the way the game does since 19 October 2026: the production R cast of the script-driven Annie (the windup ends and
    /// <c>InfernalGuardian.SelfExecute</c> runs <c>BBSpawnPet</c>). It replaces the shortcut of the hand-written module, a
    /// <c>PendingSummon</c> with nothing left to wait for. The caster stands on the point (the cast range is 600) and learns R at
    /// <paramref name="rank"/>; the spell clock runs the 0.25 s windup.
    /// </summary>
    internal static Entity SummonTibbers(MatchRuntime runtime, Entity owner, int rank, Position2? at = null)
    {
        var point = at ?? owner.Position;
        owner.Position = point;
        owner.SpellRanks[3] = rank;
        owner.SpellCooldowns[3] = 0.0;
        owner.Mana = owner.MaxMana;
        var aim = new Position3(point.X, 0, point.Z);
        TestAssert.True(runtime.Spells.HandleCastRequest(owner, new SpellCastRequest(3, false, aim, aim, owner.NetId),
            runtime.World.Clock.Now()), "Annie R accepted");
        runtime.Spells.Tick(0.26);
        return runtime.World.Entities.Values.Single(unit => unit.PetState?.Kind == Amberline.Simulation.Pets.CloneKind.Tibbers &&
                                                            unit.OwnerNetId == owner.NetId);
    }

    // Preserve the original input preparation; outputs never supply timing.
    internal static void PrepareInputTiming(Entity entity, JsonElement input)
    {
        if (entity.UnitType != UnitType.Hero || entity.ChampionName != "Annie") return;
        var speed = input.GetProperty("attack_speed_mod").GetDouble();
        if (Math.Abs(input.GetProperty("atk_period").GetDouble() - 1.6 / speed) > 1e-7) return;
        entity.AttackPeriod = Math.Clamp(1.6 * (1 + 0.07999999821186066) / speed, .4, 5);
        entity.AttackWindup = entity.AttackPeriod * .195794753;
    }

    internal static Entity ReadEntity(JsonElement raw)
    {
        var entity = new Entity(raw.GetProperty("netid").GetUInt32(),
            EntityKindExtensions.FromSourceType(raw.GetProperty("etype").GetString()),
            (UnitType)raw.GetProperty("unitType").GetInt32(),
            (Team)raw.GetProperty("team").GetInt32(), Point(raw.GetProperty("position")))
        {
            Health = raw.GetProperty("hp").GetDouble(), MaxHealth = raw.GetProperty("max_hp").GetDouble(),
            Mana = raw.GetProperty("mana").GetDouble(), MaxMana = raw.GetProperty("max_mana").GetDouble(),
            Armor = raw.GetProperty("armor").GetDouble(), SpellBlock = raw.GetProperty("spell_block").GetDouble(),
            AttackDamage = raw.GetProperty("atk_damage").GetDouble(), AbilityPower = raw.GetProperty("ability_power").GetDouble(),
            Speed = raw.GetProperty("speed").GetDouble(), Radius = raw.GetProperty("radius").GetDouble(),
            AttackRange = raw.GetProperty("atk_range").GetDouble(), AttackPeriod = raw.GetProperty("atk_period").GetDouble(),
            AttackWindup = raw.GetProperty("windup").GetDouble(), AttackSpeedMod = raw.GetProperty("attack_speed_mod").GetDouble(),
            MissileSpeed = raw.GetProperty("missile_speed").GetDouble(), AttackCooldown = raw.GetProperty("attack_cd").GetDouble(),
            Level = raw.GetProperty("level").GetInt32(), SkillPoints = raw.GetProperty("skill_points").GetInt32(),
            Alive = raw.GetProperty("alive").GetBoolean(),
            Created = raw.GetProperty("created").GetBoolean(), Visible = raw.GetProperty("visible").GetBoolean(),
            OwnerNetId = raw.GetProperty("owner_netid").GetUInt32(), ExpiresAt = SpellReplayRecording.Number(raw.GetProperty("expires_at")),
            Gold = raw.GetProperty("gold").GetDouble(), Experience = raw.GetProperty("exp").GetDouble(),
            RespawnAt = raw.GetProperty("respawn_at").GetDouble(), CooldownMod = raw.GetProperty("cooldown_mod").GetDouble(),
            HealthRegen = raw.GetProperty("hp_regen").GetDouble(), ManaRegen = raw.GetProperty("mp_regen").GetDouble(),
            ChampionName = raw.GetProperty("champion").GetString() ?? "Annie", Name = raw.GetProperty("name").GetString() ?? "",
            Facing = Point(raw.GetProperty("facing")),
        };
        if (raw.GetProperty("attack_target").ValueKind != JsonValueKind.Null)
            entity.AttackTarget = raw.GetProperty("attack_target").GetUInt32();
        entity.SpellRanks = raw.GetProperty("spell_ranks").EnumerateArray().Select(v => v.GetInt32()).ToList();
        entity.SpellCooldowns = raw.GetProperty("spell_cooldowns").EnumerateArray().Select(v => v.GetDouble()).ToList();
        entity.SummonerCooldowns = raw.GetProperty("summoner_cooldowns").EnumerateArray().Select(v => v.GetDouble()).ToList();
        entity.ItemCooldowns = raw.GetProperty("item_cooldowns").EnumerateArray().Select(v => v.GetDouble()).ToList();
        entity.Waypoints = Points(raw.GetProperty("waypoints")).ToList();
        LoadBuffs(entity, raw.GetProperty("buffs"));
        // Keep the archived cast setup consistent with the independently
        // verified Annie attack offsets used whenever a buff refreshes stats.
        PrepareInputTiming(entity, raw);
        return entity;
    }

    private static void LoadBuffs(Entity entity, JsonElement raw)
    {
        foreach (var value in raw.EnumerateArray())
        {
            var payload = new BuffPayload();
            if (value.TryGetProperty("payload", out var fields) && fields.ValueKind == JsonValueKind.Object)
            {
                SetBool(fields, "action_disabled", v => payload.ActionDisabled = v);
                SetBool(fields, "movement_disabled", v => payload.MovementDisabled = v);
                SetBool(fields, "stunned", v => payload.ActionDisabled = v);
                SetBool(fields, "silenced", v => payload.Silenced = v);
            }
            entity.Buffs.Add(new BuffState(value.GetProperty("name").GetString() ?? "FixtureBuff", payload,
                (BuffType)value.GetProperty("type").GetInt32(), value.GetProperty("slot").GetInt32(),
                value.GetProperty("expires_at").GetDouble(), value.GetProperty("caster").GetUInt32(),
                value.GetProperty("stacks").GetInt32(),
                value.GetProperty("max_stack").ValueKind == JsonValueKind.Null ? null : value.GetProperty("max_stack").GetInt32()));
        }
    }

    private static void SetBool(JsonElement value, string name, Action<bool> setter)
    {
        if (value.TryGetProperty(name, out var field) && field.ValueKind is JsonValueKind.True or JsonValueKind.False)
            setter(field.GetBoolean());
    }

    internal static Position2 Point(JsonElement value) => value.GetArrayLength() == 3
        ? new Position2(value[0].GetDouble(), value[2].GetDouble())
        : new Position2(value[0].GetDouble(), value[1].GetDouble());

    internal static IEnumerable<Position2> Points(JsonElement value) =>
        value.EnumerateArray().Select(Point);

    private static void Near(double expected, double actual, string context) =>
        TestAssert.Near(expected, actual, 1e-6, context +
            $" expected {expected.ToString("R", CultureInfo.InvariantCulture)} actual {actual.ToString("R", CultureInfo.InvariantCulture)}");

    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    internal sealed class ReplayHooks : IChampionSpellHooks
    {
        public void OnSpellCast(Entity hero, int slot, double now) { }
        public void OnNamedSpellCast(Entity hero, int slot, string spellName, Entity? target) { }
        public bool CastPyromania(Entity hero, bool damaging) => false;
        public void CancelChannel(Entity hero, string reason) { }
    }

    internal sealed class ReplayCombat(CombatService service, FixtureCombatHooks hooks) : ISpellCombatService
    {
        private readonly CombatService _service = service;
        private readonly FixtureCombatHooks _hooks = hooks;
        public bool IsTargetable(Entity target, Entity? observer) => _hooks.IsTargetable(target, observer);
        public double HealingMultiplier(Entity target) => _service.HealingReceivedMultiplier(target);
        public bool ApplyDamage(Entity source, Entity target, double amount, DamageType type,
            DamageSourceKind? sourceKind, double vampScale = 1.0) =>
            _service.ApplyHit(source.NetId, target.NetId, amount, type, sourceKind,
                vampScale: vampScale);
        public bool ApplyDamage(Entity source, Entity target, double amount, DamageType type,
            DamageSourceKind? sourceKind, double vampScale, bool isReflect,
            bool sourceIsPeriodic, uint? spellOwnerNetId) =>
            _service.ApplyHit(source.NetId, target.NetId, amount, type, sourceKind,
                isReflect: isReflect, sourceIsPeriodic: sourceIsPeriodic,
                spellOwnerNetId: spellOwnerNetId, vampScale: vampScale);
        public bool ApplyDamage(Entity source, Entity target, double amount, DamageType type,
            DamageSourceKind? sourceKind, DeathRecapOrigin recapOrigin, bool bypassSpellShield) =>
            _service.ApplyHit(source.NetId, target.NetId, amount, type, sourceKind,
                recapOrigin: recapOrigin, bypassSpellShield: bypassSpellShield);
        public bool ConsumeSpellShield(Entity source, Entity target) =>
            _hooks.ConsumeSpellShield(source, target, DamageSourceKind.Spell, force: false);
        public bool ConsumeSpellShield(Entity source, Entity target, bool force) =>
            _hooks.ConsumeSpellShield(source, target, DamageSourceKind.Spell, force);
        public void InterruptBasicAttack(Entity attacker) => _service.InterruptBasicAttack(attacker);
        public void Kill(Entity unit, uint killerNetId) => _service.Kill(unit, killerNetId);
    }

    internal sealed class ReplayBuffs(BuffLifecycle lifecycle) : ISpellBuffService
    {
        public void ScriptStatusGained(Entity unit, bool wasHardControlled, bool wasRooted) => lifecycle.ScriptStatusGained(unit, wasHardControlled, wasRooted);
        public void ScriptStatusLost(Entity unit, bool couldMove, bool couldAttack) => lifecycle.ScriptStatusLost(unit, couldMove, couldAttack);
        public BuffState? AddOverlapping(Entity unit, string name, BuffType type, double duration, uint casterNetId,
            BuffPayload? payload = null, bool canMitigateDuration = true, int? maxStack = null) =>
            lifecycle.AddOverlappingBuff(unit, name, type, duration, casterNetId, payload, canMitigateDuration, maxStack);
        public BuffState? Add(Entity unit, string name, BuffType type, double duration, uint casterNetId,
            BuffPayload? payload = null, bool canMitigateDuration = true) =>
            lifecycle.AddBuff(unit, name, type, duration, casterNetId, payload, canMitigateDuration);
        public BuffState? AddStacking(Entity unit, string name, BuffType type, double duration,
            uint casterNetId, int maxStacks, BuffPayload perStackPayload,
            bool replaceExistingPayload = false, bool canMitigateDuration = true) =>
            lifecycle.AddStackingBuff(unit, name, type, duration, casterNetId, maxStacks,
                perStackPayload, replaceExistingPayload, canMitigateDuration: canMitigateDuration);
        public bool Remove(Entity unit, int slot) => lifecycle.RemoveBuff(unit, slot);
        public bool Has(Entity unit, string name) => lifecycle.HasBuff(unit, name);
        public void Tick(Entity unit, double dt) => lifecycle.TickBuffs(unit, dt);
        public void RefreshMovementSpeed(Entity unit, bool push = true) => lifecycle.RefreshMovementSpeed(unit, push);
        // The production boundary (BuffLifecycleAdapter) answers these two; a replayed script buff's
        // BuffOnUpdateStats resample needs them, so the harness carries the same arms.
        public bool ReplaceScriptPayload(Entity unit, BuffState buff, BuffPayload perStackPayload) =>
            lifecycle.ReplaceScriptPayload(unit, buff, perStackPayload);
        public bool DropScriptStack(Entity unit, BuffState buff, BuffPayload perStackPayload, int count = 1) =>
            lifecycle.DropScriptStack(unit, buff, perStackPayload, count);
    }

    internal sealed class FixedRandomSource(double value) : IRandomSource
    {
        public double NextDouble() => value;
    }

    internal sealed class ReplayQueue : ICombatPacketQueue
    {
        public List<PlaintextPacket> Packets { get; } = new();
        public void Enqueue(PlaintextPacket packet, bool global = false)
        {
            Packets.Add(packet);
        }
        public void EnqueueLifecycle(uint victimNetId, uint killerNetId,
            Func<uint, PlaintextPacket> buildPacket) => Packets.Add(buildPacket(killerNetId));
        public void Clear() => Packets.Clear();
    }

    internal sealed class FixtureStatsHooks : IHeroStatsHooks
    {
        public void UpdateAttackTiming(Entity hero, double attackSpeedMod) =>
            HeroAttackTimings.ApplyRefresh(hero, GameData.Default);
        public IReadOnlyList<double> ManaCosts(Entity hero) =>
            GameData.Default.ManaCostsForRanks(hero.SpellRanks, hero.ChampionName);
        public bool IsPendingActionBlocked(Entity hero) => hero.ChampionState?.BlocksActions == true;
        public bool IsSpellToggledOn(Entity hero, int spellSlot) => false;
        public void RemoveBuff(Entity hero, int slot, bool emitWire)
        {
            for (var index = hero.Buffs.Count - 1; index >= 0; index--)
                if (hero.Buffs[index].Slot == slot) hero.Buffs.RemoveAt(index);
        }
    }

    internal sealed class FixtureCombatHooks : ICombatHooks
    {
        private BuffLifecycle? _buffs;
        private IBuffEffectHooks? _buffEffects;
        private ISpellEffectSink? _effects;
        public void AttachBuffLifecycle(BuffLifecycle buffs, IBuffEffectHooks effects,
            ISpellEffectSink spellEffects)
        {
            _buffs = buffs;
            _buffEffects = effects;
            _effects = spellEffects;
        }
        public bool IsInvulnerable(Entity unit) => unit.Buffs.Any(buff => buff.Type == BuffType.Invulnerability);
        public bool IsGodMode(Entity unit) => unit.GodMode;
        public bool IsTargetable(Entity unit, Entity? observer) => unit.Alive &&
            !unit.Buffs.Any(buff => buff.Payload.Untargetable);
        public void OnBoxDamageSensor(Entity? source, Entity target, DamageSourceKind? sourceKind) { }
        public void OnBasicAttackPreDamage(Entity source, Entity target) { }
        public bool ConsumeSpellShield(Entity? source, Entity target, DamageSourceKind? sourceKind, bool force) => false;
        public bool TryFinishExistingRevival(Entity unit, uint killerNetId) => false;
        public PreDamageResult PreDamage(Entity? source, Entity target, double amount,
            DamageType damageType, DamageSourceKind? sourceKind) => PreDamageResult.Unhandled;
        public double ModifyDamageBeforeMitigation(Entity? source, Entity target, double amount,
            DamageType damageType, DamageSourceKind? sourceKind) => amount;
        public double ModifyDamageAfterMitigationBeforeShields(Entity target, Entity? source,
            double amount, DamageType damageType, DamageSourceKind? sourceKind) => amount;
        public void OnBasicAttackStart(Entity attacker, Entity target) { }
        public void BreakAttackStealth(Entity attacker) { }
        public double ModifyBasicAttackDamage(Entity source, Entity target, double damage) => damage;
        public double ModifyOutgoingDamage(Entity? source, Entity target, double damage,
            DamageType type, DamageSourceKind? sourceKind) => damage;
        public double ModifyIncomingDamage(Entity target, Entity? source, double damage,
            DamageType type, DamageSourceKind? sourceKind) => damage;
        public double AbsorbDamage(Entity target, Entity? source, double damage,
            DamageType type, DamageSourceKind? sourceKind) => damage;
        public double PreprocessItemDamage(Entity? source, Entity target, double damage,
            DamageType type, DamageSourceKind? sourceKind) => damage;
        public double ModifyHealthRegeneration(Entity hero, double amount, double dt) => amount;
        public double ModifyManaRegeneration(Entity hero, double amount, double dt) => amount;
        public void OnUnitDamaged(Entity? source, Entity target, double damage, double rawDamage,
            DamageSourceKind? sourceKind, bool reflected, bool sourceIsPeriodic) { }
        public void OnMoltenShieldReflect(Entity source, Entity target) =>
            _effects?.CreateEffect("AnnieSparks.troy", source.Position,
                targetNetId: source.NetId, bindNetId: source.NetId);
        public void OnDamageShieldChanged(Entity target, BuffState buff, double delta, bool depleted) =>
            _buffEffects?.SendDamageShieldDelta(target, delta);
        public void RemoveBuff(Entity target, int slot)
        {
            if (_buffs is not null) _buffs.RemoveBuff(target, slot);
            else
                for (var index = target.Buffs.Count - 1; index >= 0; index--)
                    if (target.Buffs[index].Slot == slot) target.Buffs.RemoveAt(index);
        }
        public void OnBasicAttackHit(Entity source, Entity target, double rawDamage, DamageType type) { }
        public void OnBasicAttackMiss(Entity source) { }
        public void OnPendingHitCanceled(PendingHit hit) { }
        public void OnPendingCastCanceled(Entity unit) { }
        public void OnDeath(Entity unit, uint killerNetId) { }
        public void OnPetDeath(Entity pet) { }
        public void OnNeutralDeath(Entity neutral, uint killerNetId) { }
        public void OnItemKill(Entity? killer, Entity victim) { }
        public void OnItemDeath(Entity hero) { }
        public void OnKill(Entity? killer, Entity victim) { }
        public void OnRespawn(Entity unit) { }
        public void OnLevelChanged(Entity hero) { }
        public void OnNeutralDamaged(Entity target, Entity? source) { }
        public void OnItemPassiveTick(double now, double dt) { }
        public void OnPeriodicDamage(Entity target, Entity? source, double amount,
            DamageType type, DamageSourceKind? sourceKind) { }
        public void OnEndGame(Team winner) { }
        public void HaltAllAi() { }
        public bool IsActionBlocked(Entity unit) => unit.Buffs.Any(buff =>
            buff.Type is BuffType.Stun or BuffType.Invulnerability || buff.Payload.ActionDisabled);
    }

    internal sealed class FixtureBuffServices : IBuffServices
    {
        public FixtureBuffServices(FixtureCombatHooks hooks, IBuffEffectHooks effects,
            CombatService combat, IChampionBuffHooks? champions = null)
        {
            Combat = new CombatHooks(hooks, combat);
            Casts = new CastHooks();
            Champions = champions ?? new ChampionHooks();
            Effects = effects;
            Items = new ItemHooks();
        }
        public ICombatBuffHooks Combat { get; }
        public ICastBuffHooks Casts { get; }
        public IChampionBuffHooks Champions { get; }
        public IBuffEffectHooks Effects { get; }
        public IItemBuffHooks Items { get; }

        private sealed class CombatHooks(FixtureCombatHooks hooks, CombatService combat) : ICombatBuffHooks
        {
            public bool IsPendingActionBlocked(Entity unit) => hooks.IsActionBlocked(unit);
            public double HealingMultiplier(Entity unit) => 1.0;
            public void InterruptBasicAttack(Entity unit) => combat.InterruptBasicAttack(unit);
            public void InterruptBasicAttack(Entity unit, out bool cancelledWindup) =>
                cancelledWindup = combat.InterruptBasicAttack(unit) > 0;
            public void ApplyPeriodicDamage(PeriodicDamageRequest request) => combat.ApplyHit(
                request.SourceNetId, request.TargetNetId, request.Amount,
                request.DamageType, request.SourceKind,
                sourceIsPeriodic: request.SourceIsPeriodic,
                spellOwnerNetId: request.SpellOwnerNetId == 0 ? null : request.SpellOwnerNetId);
            public void Kill(Entity unit, uint killerNetId = 0) => combat.Kill(unit, killerNetId);
        }
        private sealed class CastHooks : ICastBuffHooks
        {
            public void CancelPendingCast(Entity hero) => hero.PendingCast = null;
        }
        private sealed class ChampionHooks : IChampionBuffHooks
        {
            public void OnBuffTick(Entity unit, BuffState buff, double dt) { }
            public void OnBuffRemoved(Entity unit, BuffState buff) { }
            public void OnBuffRemovedAfterWire(Entity unit, BuffState buff) { }
        }
        private sealed class ItemHooks : IItemBuffHooks
        {
            public bool HasItem(Entity unit, int itemId) => unit.Inventory.Any(slot => slot?.ItemId == itemId);
            public void SyncItemSlow(Entity target) { }
        }
    }
}
