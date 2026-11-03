using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Ai;
using Amberline.Simulation.BehaviorTrees;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Bots;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Items;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Neutral;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;
using Amberline.Simulation.Stats;

namespace Amberline.Network.Sessions;

internal sealed class ProductionServices
{
    private readonly WorldState _world;
    private readonly GameData _data;
    private readonly NavigationGrid _navigation;
    private readonly ReplicationSyncCounter _sync;
    public HeroStatsService? HeroStats { get; set; }
    public CombatService? Combat { get; set; }
    public BuffEffectService? BuffEffects { get; set; }
    public SpellSystem? Spells { get; set; }
    public ItemVisionService ItemVision { get; set; } = null!;
    public JungleScheduler? Scheduler { get; set; }
    public DuelSession? Session { get; set; }
    public ReplicationDelivery Delivery { get; set; } = null!;

    public ProductionServices(WorldState world, GameData data, NavigationGrid navigation,
        ReplicationSyncCounter sync)
    {
        _world = world; _data = data; _navigation = navigation; _sync = sync;
    }

    public IReadOnlyList<PlaintextPacket> BuildCreation(Entity entity)
    {
        if (entity.Kind == EntityKind.NidaleeTrap)
            return CreationWithStats(entity, SpawnPackets.SpawnMinion(new SpawnMinionSnapshot(
                entity.NetId, SessionSpawnService.MapNetNodeId, entity.Position,
                TeamMapping.ToNative(entity.Team), "Noxious Trap", "Nidalee_Spear",
                IgnoreCollision: true), _navigation.HeightAt(entity.Position)));
        if (entity.UnitType == UnitType.Hero)
        {
            var participant = Session?.Participants.FirstOrDefault(item => item.Hero == entity);
            return [SpawnPackets.CreateHero(entity.NetId, participant?.ClientId ?? uint.MaxValue,
                participant?.Name ?? entity.Name, entity.ChampionName, entity.Team == Team.Order,
                skinId: entity.SkinId, netNodeId: SessionSpawnService.MapNetNodeId,
                spawnPositionIndex: participant?.SpawnPositionIndex ?? 0,
                // NOT RETAIL: bot flag only where the client ships a game_bot_ name (see SessionSpawnService).
                isBot: participant is { IsBot: true } && _data.ClientNamesBot(entity.ChampionName))];
        }
        if (entity.IsSceneTurret || entity.UnitType == UnitType.Structure)
            return [SpawnPackets.CreateTurret(entity.NetId, SessionSpawnService.MapNetNodeId,
                entity.Name + "_A")];
        if (entity.Kind == EntityKind.Neutral && Scheduler is not null &&
            Scheduler.TryGetMember(entity.NetId, out var member))
        {
            var stats = member.Stats;
            return CreationWithStats(entity, SpawnPackets.CreateNeutral(new NeutralSpawnSnapshot(
                entity.NetId, NeutralDefinitions.NeutralNodeId,
                // Camp discovery is map-wide. Create at the public home point;
                // the visibility snapshot supplies the live position later.
                member.Home,
                member.GroupPosition, member.FacePosition, member.Name, member.Name,
                NeutralDefinitions.UniqueName(member.Name, member.CampId, 1, member.Member),
                NeutralDefinitions.MinimapIcon(member.Name),
                NeutralDefinitions.NeutralWireTeam,
                checked((int)Math.Round(stats.DamageBonus)), checked((int)Math.Round(stats.HealthBonus)),
                0, member.CampId)));
        }
        // Reconstruction (batch 482): the shrine guardian goes out through the neutral creation (0x66), not the minion packet (0x80); retail's
        // packet for it is not recovered. Batch 495: both packets make a minion (the 0x66 case calls the minion create routine), so
        // the packet does not choose the bar. The hero's bar route (batch 482) is its virtual bar slot, and the
        // minion's is the minion bar draw, which draws no bar when the unit is not targetable: its gate is AttackableUnit slot 4
        // (IsTargetable is nonzero and IsTargetableToTeam is not 0x1800000). The guardian is untargetable to both
        // teams (EncounterService, PointGuard's Targetable 0), and its full stat block carries that (CombatPackets.MinionTargetableToTeam).
        // The guardian names no minimap icon, so the neutral packet's icon field is written empty.
        if (entity.ChampionName is "OdinNeutralGuardian")
        {
            var at = new Position3(entity.Position.X, _navigation.HeightAt(entity.Position), entity.Position.Z);
            return CreationWithStats(entity, SpawnPackets.CreateNeutral(new NeutralSpawnSnapshot(
                entity.NetId, NeutralDefinitions.NeutralNodeId, at, at, at,
                entity.ChampionName, entity.ChampionName, entity.ChampionName, "",
                TeamMapping.ToNative(entity.Team), 0, 0, 0, 0)));
        }
        // Reconstruction (batch 476d): a neutral encounter unit that names a minimap icon (OdinCenterRelic_Left: CenterRelicLeft, the shipped
        // encounter's MinimapIcon) is created through the neutral creation packet (0x66), the only spawn packet with an icon field. The client
        // draws the icon from its minimap.ini section (the neutral texture, relativeTeams). Team-owned encounter units (the quest markers) keep 0x80.
        // Batch 511: the 0x66 icon is a camp's, kept per GroupNumber. Sent with group 0, every icon joined the camp the first
        // guardian made (icon empty), so none drew. A camp icon unit (Entity.MinimapCamp: health relics, speed shrines) now has its own group;
        // a unit with its own icon (the centre shards: their team textures, drawn only by the unit route) is told it by 0xE1 after its creation.
        // Batch 516: a centre shard is on a team (BehaviorTreeQuestHost.RelicPlacements) and keeps this route; it goes out on its own team.
        if (entity.MinimapIcon.Length > 0 && (entity.Team == Team.Neutral || entity.ChampionName == "OdinCenterRelic"))
        {
            var at = new Position3(entity.Position.X, _navigation.HeightAt(entity.Position), entity.Position.Z);
            var camp = entity.MinimapCamp;
            var creation = CreationWithStats(entity, SpawnPackets.CreateNeutral(new NeutralSpawnSnapshot(
                entity.NetId, NeutralDefinitions.NeutralNodeId, at, at, at,
                entity.ChampionName, entity.ChampionName, entity.ChampionName, camp != 0 ? entity.MinimapIcon : "",
                TeamMapping.ToNative(entity.Team), 0, 0, 0, camp)));
            return camp != 0 ? creation : [.. creation, SpawnPackets.UnitSetMinimapIcon(entity.NetId, entity.MinimapIcon)];
        }
        if (entity.PetState is { Kind: CloneKind.Tibbers } tibbers)
        {
            var spell = _data.Spell("InfernalGuardian");
            var character = _data.Champion("AnnieTibbers");
            var duration = Math.Max(0.0,
                entity.ExpiresAt - (Combat?.CurrentTime ?? tibbers.BornAt));
            return CreationWithStats(entity, SpawnPackets.SpawnPet(new SpawnPetSnapshot(
                entity.OwnerNetId, entity.NetId, checked((byte)entity.NodeId), entity.Position,
                "Tibbers", "AnnieTibbers", SkinId: entity.SkinId, Rank: tibbers.Rank,
                Duration: Float(duration),
                DamageBonus: checked((int)(spell.EffectNumber(3, tibbers.Rank) - character.BaseDamage)),
                HealthBonus: checked((int)(spell.EffectNumber(2, tibbers.Rank) - character.BaseHp)),
                BuffName: "InfernalGuardian", CopyInventory: true, ClearFocusTarget: false),
                _navigation.HeightAt(entity.Position)));
        }
        if (entity.PetState is { CloneSourceNetId: not 0 } pet &&
            _world.Get(pet.CloneSourceNetId) is { } source)
        {
            // SpawnPet creates the native minion itself. Sending SpawnMinion
            // first registers two objects under one ID and corrupts teardown.
            // Rebuild the same ownership on first sight and on reconnect.
            var duration = pet.SpawnDuration == 0.0 ? 0.0 : Math.Max(0.0,
                pet.SpawnDuration - ((Combat?.CurrentTime ?? pet.BornAt) - pet.BornAt));
            return CreationWithStats(entity, SpawnPackets.SpawnPet(new SpawnPetSnapshot(
                entity.OwnerNetId, entity.NetId, checked((byte)entity.NodeId), entity.Position,
                string.IsNullOrEmpty(source.Name) ? entity.Name : source.Name, entity.Name,
                CloneNetId: source.NetId, SkinId: entity.SkinId, Rank: 1, Duration: Float(duration),
                BuffName: pet.SpawnBuffName, CopyInventory: true, ClearFocusTarget: true,
                AiScript: "Pet.lua", ShowMinimapIcon: pet.ShowMinimapIcon),
                _navigation.HeightAt(entity.Position)));
        }
        var displayName = entity.IsSummonerBeacon ? "Beacon" : entity.Name;
        var skinName = entity.IsSummonerBeacon ? "SummonerBeacon" : entity.Name;
        return CreationWithStats(entity, SpawnPackets.SpawnMinion(new SpawnMinionSnapshot(entity.NetId,
            SessionSpawnService.MapNetNodeId, entity.Position, TeamMapping.ToNative(entity.Team),
            displayName, skinName, entity.IsWard, SkinId: entity.SkinId, VisibilitySize: (float)entity.VisibilitySize),
            _navigation.HeightAt(entity.Position)));
    }

    private IReadOnlyList<PlaintextPacket> CreationWithStats(
        Entity entity, PlaintextPacket creation)
    {
        var packets = new List<PlaintextPacket> { creation };
        if (IsOrdinaryActor(entity))
            // A first observer reveal uses the source's creation template.
            // Dynamic actors have no spawn_stats entry, so their protocol
            // defaults (HP regen=0, action state=7) must not leak into
            // later action-time stat pushes for the same entity.
            packets.AddRange(BuildInitialStats(entity));
        return packets;
    }

    public IReadOnlyList<PlaintextPacket> BuildStats(Entity entity) =>
        BuildStats(entity, initialReveal: true, dynamicDefaults: false);

    private IReadOnlyList<PlaintextPacket> BuildInitialStats(Entity entity) =>
        BuildStats(entity, initialReveal: true, dynamicDefaults: true);

    public IReadOnlyList<PlaintextPacket> BuildStats(
        Entity entity, ReplicationObserver observer) =>
        BuildStats(entity, initialReveal: false, dynamicDefaults: false);

    private IReadOnlyList<PlaintextPacket> BuildStats(
        Entity entity, bool initialReveal, bool dynamicDefaults)
    {
        if (entity.Kind == EntityKind.NidaleeTrap)
            return [CombatPackets.MinionStats(entity.NetId, new MinionStatsSnapshot(
                Hp: 1, AttackDamage: 0, Speed: 0, Armor: 0, SpellBlock: 0,
                ActionState: entity.NoRender ? 1u << 13 : 0u,
                MagicImmune: 1, Invulnerable: 1, Targetable: 0),
                _sync.Next(entity.NetId))];
        if (entity.UnitType == UnitType.Hero && HeroStats is { } stats)
            return [CombatPackets.HeroStats(stats.Snapshot(entity), _sync.Next(entity.NetId))];
        // Public neutral registration can make a camp known before its first
        // visible reveal. Restore the complete template on every neutral
        // reveal, then append the narrow current-HP delta below: MinionStats
        // writes max health into both HP slots, while the delta preserves an
        // injured camp's current value.
        if (!initialReveal && entity.Kind == EntityKind.Neutral)
        {
            var packets = new List<PlaintextPacket>(
                BuildStats(entity, initialReveal: true, dynamicDefaults: false))
            {
                CombatPackets.ReplicationHp(entity.NetId, Float(entity.Health),
                    _sync.Next(entity.NetId), isHero: false, isTurret: false),
            };
            return packets;
        }

        // A creation burst carries the complete minion/monster template. A
        // normal re-entry carries current HP and attack speed, preserving max
        // health while restoring attack-speed changes that occurred in fog.
        if (!initialReveal && IsOrdinaryActor(entity))
        {
            var packets = new List<PlaintextPacket> { CombatPackets.MinionCurrentStats(
                entity.NetId, Float(entity.Health), Float(entity.AttackSpeedMod), _sync.Next(entity.NetId)) };
            packets.Add(CombatPackets.MinionMovementSpeed(entity.NetId, Float(entity.Speed),
                _sync.Next(entity.NetId)));
            // Once the end-game minion phase has latched NoRender, append the narrow
            // ActionState delta so late visibility does not lose bit 13.
            if (entity.NoRender)
            {
                var actionState = entity.Kind == EntityKind.ShacoBox ? 0u : 7u;
                if (entity.AnyBuffOrStatus(buff => buff.Payload.Ghosted)) actionState |= 1u << 10;
                if (entity.AnyBuffOrStatus(buff => buff.Payload.GhostProof)) actionState |= 1u << 11;
                packets.Add(CombatPackets.MinionActionState(entity.NetId,
                    actionState | (1u << 13), _sync.Next(entity.NetId)));
            }
            return packets;
        }
        if (entity.IsSceneTurret)
            return [CombatPackets.TurretStats(entity.NetId,
                new TurretStatsSnapshot(Float(entity.Health), Float(entity.AttackDamage),
                    Float(entity.MaxHealth), Float(entity.Armor), Float(entity.SpellBlock),
                    Float(entity.HealthRegen), entity.Protected, Float(entity.AttackSpeedMod),
                    Targetable: entity.Alive && !TurretRules.IsShrineTurret(entity)), _sync.Next(entity.NetId))];
        if (entity.Kind is EntityKind.Inhibitor or EntityKind.Nexus)
            return [CombatPackets.BuildingStats(entity.NetId, Float(entity.Health),
                entity.Protected ? CombatPackets.SFlagNontargetableEnemy :
                    CombatPackets.SFlagTargetableToAll,
                entity.Protected ? 1u : 0u, 1u, _sync.Next(entity.NetId))];
        if (IsGenericMinionStats(entity))
        {
            // replication.reveal does not call spawn_stats for dynamically
            // created pets/clones/boxes. Their initial MinionStats packet uses
            // protocol defaults for HP regen and action state. Attack speed
            // always carries the authoritative value, including copied pets.
            var dynamicActor = entity.Kind is EntityKind.Pet or EntityKind.ShacoClone or
                EntityKind.YorickGhoul or EntityKind.YorickRevenant or EntityKind.Ward or
                EntityKind.ShacoBox or EntityKind.ClonePet;
            var attackSpeed = Float(entity.AttackSpeedMod);
            // ANN-03: the native Tibbers template must retain his shipped regen
            // on first sight/reconnect, not overwrite it with the pet default.
            var hpRegen = dynamicDefaults && dynamicActor &&
                entity.PetState is not { Kind: CloneKind.Tibbers } ? 0.0f : Float(entity.HealthRegen);
            var combat = CharacterState.ScriptedCombatOf(entity, dynamicDefaults && dynamicActor ? 7u :
                entity.Kind == EntityKind.ShacoBox ? 0u : 7u);
            // The render bits (NoRender 13, ForceRenderParticles 14) and the script's combat state come from ScriptedCombatOf. A placemarker's stats
            // carry the force-render bit that the owner's retest of 24 October 2026 (finding 12) found missing.
            // A guardian's health and regen reach its clients as 0 (batch 516, ClientReplicationStore.WithoutHealth).
            return [CombatPackets.MinionStats(entity.NetId, new MinionStatsSnapshot(
                Float(entity.MaxHealth), Float(entity.AttackDamage), Float(entity.Speed),
                Float(entity.Armor), Float(entity.SpellBlock),
                attackSpeed, hpRegen, ActionState: combat.ActionState,
                MagicImmune: combat.MagicImmune, Invulnerable: combat.Invulnerable, Targetable: combat.Targetable,
                // The record's BaseStaticMPRegen as mPARRegenRate (batch 329): the camp spawns set it (0.5), no other spawn does.
                ParRegen: Float(entity.ManaRegen),
                // Batch 493e: the guardian's pool as it is; the client maps a neutral point's half to an empty ring itself.
                CurrentMana: ReplicatesPar(entity) ? Float(entity.Mana) : 0.0f,
                MaxMana: ReplicatesPar(entity) ? Float(entity.MaxMana) : 0.0f,
                TargetableToTeam: CombatPackets.MinionTargetableToTeam(entity)), _sync.Next(entity.NetId))];
        }
        return [];
    }

    /// <summary>
    /// The narrow combat block of a minion whose script changed a combat status (<see cref="CombatPackets.MinionCombatState"/>). Only a unit the
    /// generic minion stat block covers gets one: a turret, a building or a Noxious Trap has its own layout, so it gets nothing here.
    /// </summary>
    public IReadOnlyList<PlaintextPacket> BuildCombatState(Entity entity)
    {
        if (entity.Kind == EntityKind.NidaleeTrap || !IsGenericMinionStats(entity)) return [];
        var combat = CharacterState.ScriptedCombatOf(entity, entity.Kind == EntityKind.ShacoBox ? 0u : 7u);
        return [CombatPackets.MinionCombatState(entity.NetId, combat.ActionState, combat.MagicImmune,
            combat.Invulnerable, combat.Targetable, _sync.Next(entity.NetId))];
    }

    // Batch 479 (Reconstruction): a unit's PAR pool goes out in its minion block. The clones have always sent theirs; the capture point's
    // guardian sends its too, because the capture ring's fill is its PAR over its max PAR (OdinGuardianBuff and OdinGuardianSuppression
    // read GetPAR and GetMaxPAR). Batch 479b (Reconstruction): the centre crystals too. Their Odin_Prism shards are bound to their PAR flex
    // (OdinBombBuff, BindFlexToOwnerPAR), so a crystal with no pool on the wire draws its shards from 0 of 0. Other minions send none, as before.
    // Batch 512: a guardian's changes go out from its point tree's CapturePoint_SetValue (BehaviorLeaves.CapturePointSetValue, the tree's
    // progress mapped by the owner); a crystal's through this block, which every BBIncPAR pushes (BbNativeHandlers.IncPar).
    private static bool ReplicatesPar(Entity entity) =>
        entity.Kind is EntityKind.ShacoClone or EntityKind.YorickRevenant or EntityKind.ClonePet ||
        entity.ChampionName is "OdinNeutralGuardian" || entity.ChampionName?.StartsWith("OdinCenterRelic", StringComparison.Ordinal) == true;

    // Batch 457: a turret replicated as a minion (Entity.ReplicatedAsMinion) is a minion object on the client and takes the minion blocks.
    private static bool IsGenericMinionStats(Entity entity) =>
        entity.UnitType is UnitType.Minion or UnitType.Monster || entity.ReplicatedAsMinion ||
        entity.Kind is EntityKind.Pet or EntityKind.ShacoClone or
            EntityKind.YorickGhoul or EntityKind.YorickRevenant or EntityKind.Ward or
            EntityKind.Neutral;

    private static bool IsOrdinaryActor(Entity entity) =>
        entity.UnitType is UnitType.Minion or UnitType.Monster || entity.ReplicatedAsMinion ||
        entity.Kind is EntityKind.Pet or EntityKind.ShacoClone or
        EntityKind.YorickGhoul or EntityKind.YorickRevenant or EntityKind.Ward or
        EntityKind.Neutral or EntityKind.ShacoBox;

    public IReadOnlyList<PlaintextPacket> BuildBuffs(Entity entity, ReplicationObserver observer)
    {
        var packets = new List<PlaintextPacket>();
        // A script-driven hero whose auto attack a buff overrode (Kayle's Righteous Fury) tells a late observer the slot too.
        if (entity.ScriptAttackOverride is { } scriptAttack)
            packets.Add(SpellPackets.SetAutoAttackSpellSlot(entity.NetId, scriptAttack.Slot));
        if (entity.HealthBarHidden) packets.Add(CombatPackets.ShowHealthBar(entity.NetId, false));
        if (entity.NotTargetableToAlly || entity.NotTargetableToEnemy)
            packets.Add(CombatPackets.UnitTargetableToTeam(entity,
                entity.UnitType == UnitType.Hero && entity.OwnerNetId == 0, _sync.Next(entity.NetId)));
        if (entity.ParColorOverride is { } parColor) packets.Add(SpellPackets.ChangeParColorOverride(entity.NetId, parColor));
        if (entity.VoiceOverride is { } voice) packets.Add(SpellPackets.ChangeCharacterVoice(entity.NetId, voice));
        if (entity.ScriptAnimationOverrides.Count > 0)
            packets.Add(SpellPackets.SetAnimationStates(entity.NetId,
                entity.ScriptAnimationOverrides.Select(pair => (pair.Key, pair.Value)).ToArray()));
        // The translucent layers a script pushed and has not popped (Deceive's stealth, the sleeping Jack in the Box).
        foreach (var (id, opacity, seconds) in entity.ScriptFades)
            if (opacity < 1.0f) packets.Add(SpellPackets.CharacterFade(entity.NetId, id, opacity, seconds));
        // A first sight/reconnect receives the remaining forced segment below,
        // but missed the one-shot packet that locked its caster clip. Rebuild
        // only a still-owned move; its ordinary completion sends the unlock.
        // The original start packet is routed after this snapshot when both
        // happen in one flush, so never duplicate that start on new sight.
        if (entity.Alive && entity.ForcedMove is { Completed: false } move &&
            !Delivery.IsAnimationPending(entity.NetId))
        {
            // Lee Sin's two dashes are script moves (BlindMonkQTwoDash and BlindMonkWOneDash play a locked, looping clip while the move
            // runs, BBPlayAnimation Lock=true); the move's effect is the script's name. Reconstruction: retail's late-observer clip behaviour is
            // not recovered (the note that retail sends the clip once, to the clients that saw the cast, is unverified); this rebuild is for an
            // observer who first sees the unit mid-dash.
            if (move.Effect == "BlindMonkQTwoDash")
                packets.Add(SpellPackets.PlayAnimation(entity.NetId, "Spell1b",
                    loop: true, lockAnimation: true, blend: false));
            else if (move.Effect == "BlindMonkWOneDash")
                packets.Add(SpellPackets.PlayAnimation(entity.NetId, "Spell2",
                    loop: true, lockAnimation: true, blend: false));
            // Nidalee's Pounce is a script move too: its buff locks Spell2 for the whole jump (BBPlayAnimation Lock=true, BBUnlockAnimation at the end).
            else if (move.Effect == "Pounce")
                packets.Add(SpellPackets.PlayAnimation(entity.NetId, "Spell2",
                    lockAnimation: true, blend: false));
            // Vayne's Tumble the same way: VayneTumble.OnBuffActivate plays Spell1 (Loop false, Blend false, Lock true) and BBMoves her; the
            // buff's deactivation unlocks it (batch 98 left this replay out, row 15).
            else if (move.Effect == "VayneTumble")
                packets.Add(SpellPackets.PlayAnimation(entity.NetId, "Spell1",
                    lockAnimation: true, blend: false));
        }
        // Batch 179: Recall's TeleportHome is the summoner module's channel effect, not a buff's, so the buff loop below cannot replay it. A late
        // observer gets it here, once per unit: the removed BuffEffectService arm gave it to the Recall buff, and the script keeps it persistent
        // (BBSpellEffectRemove of its EffectIDVar), so the replication layer replays it like any persistent visual.
        // Reconstruction: how retail's client replays an effect a channel owns to a late observer is not in the exe; this applies the buff rule.
        if (BuffEffects is not null && Spells?.RecallEffectId(entity) is { } recallFx && recallFx != 0 &&
            !Delivery.IsEffectPending(recallFx) && observer.View.Effects.Add(recallFx))
        {
            observer.View.Known.Add(recallFx);
            packets.Add(FxPackets.CreateGroup(SpellPackets.BuffNameHash("TeleportHome"), recallFx,
                entity.Position, new NavigationBounds(_navigation.MinX, _navigation.MaxX,
                _navigation.MinZ, _navigation.MaxZ),
                (x, z) => _navigation.HeightAt(new Position2(x, z)),
                targetNetId: entity.NetId, bindNetId: entity.NetId));
        }
        foreach (var buff in entity.Buffs)
        {
            // A server-only marker is a buff the client must never hold (BuffPayload.ServerOnlyMarker: a name whose
            // shipped script carries another spell's buff template would draw that spell's troy here too), so a first
            // sight or a reconnect sends nothing for it, as the live wire does (ProtocolSpellEffectSink.ReachesClient).
            if (!buff.Payload.ServerOnlyMarker && _data.HasClientBuff(buff.Name))
            {
                var slot = checked((byte)buff.Slot);
                var hash = SpellPackets.BuffNameHash(_data.ClientBuffName(buff.Name));
                var count = checked((byte)Math.Clamp(buff.Stacks > 0 ? buff.Stacks : 1, 1, byte.MaxValue));
                var duration = Float(Math.Max(0.0, buff.ExpiresAt));
                // A fog re-entry must not re-add a buff this client still
                // holds: 0xBF renews it and replays its activation sound
                // (playtest B2 YV8). First sight, reconnect and adds missed in
                // fog are absent from the ledger and still send one add. A
                // renewal missed in fog moves the end time (the ledger's
                // delivery time + duration) and is re-sent once; 0.1 s covers
                // the one 1/30 s step a queued add can lag the countdown. The
                // tooltip below carries no activation and is always refreshed.
                // ponytail: a server-side ExpiresAt refresh that sends no 0xBF
                // (VayneHunted, Vayne's Hunt speed part) also re-adds on re-entry.
                if (!observer.View.Buffs.TryGetValue((entity.NetId, slot), out var held) ||
                    held.Hash != hash || held.Count != count ||
                    Math.Abs(held.EndsAt - (Delivery.SimulationTime() + duration)) > 0.1)
                    packets.Add(SpellPackets.AddBuff(new BuffAdd(
                        entity.NetId, slot, (byte)buff.Type, hash, duration, buff.CasterNetId, count,
                        IsHidden: buff.HiddenOnClientByte)));
                if (buff.Slot is >= 0 and < SpellPackets.MaxBuffTooltipSlots &&
                    BuffEffectService.TryGetBuffTooltipValues(buff,
                        out var value0, out var value1, out var value2))
                {
                    packets.Add(SpellPackets.BuffTooltipVariables(entity.NetId,
                        buff.Slot, Float(value0), Float(value1), Float(value2)));
                }
            }
            if (BuffEffects is { } effects)
            {
                if (buff.Name == "GuardianAngel")
                    packets.Add(SpellPackets.PlayAnimation(entity.NetId, "Death", blend: false));
                // A late reveal must replay persistent particles and the
                // Zhonya pause state in addition to the AddBuff packet.
                effects.EnsureBuffFx(entity, buff);
                foreach (var (netId, name, bone) in BuffEffectService.PersistentVisuals(
                             entity, buff, enemyObserver: observer.Team != entity.Team))
                {
                    // Fresh effects retain ordinary queue order. Only restore
                    // an old effect that this observer missed while in fog.
                    if (Delivery.IsEffectPending(netId)) continue;
                    if (!observer.View.Effects.Add(netId)) continue;
                    observer.View.Known.Add(netId);
                    packets.Add(FxPackets.CreateGroup(SpellPackets.BuffNameHash(name[..^5]), netId,
                        entity.Position, new NavigationBounds(_navigation.MinX, _navigation.MaxX,
                        _navigation.MinZ, _navigation.MaxZ),
                        (x, z) => _navigation.HeightAt(new Position2(x, z)),
                        targetNetId: entity.NetId,
                        bindNetId: buff.Name == "Teleport_Target" ? 0 : entity.NetId,
                        boneHash: bone is null ? 0 : SpellPackets.BuffNameHash(bone)));
                }
                if (buff.Name == "ZhonyasRingShield")
                    effects.PauseAnimation(entity, true);
            }
        }
        return packets;
    }

    public IReadOnlyList<PlaintextPacket> BuildBall(BallProjection ball)
    {
        var data = _data.Champion("OriannaBall");
        var packets = new List<PlaintextPacket>
        {
            // Keep the native placemarker template ahead of its first C2
            // reveal. A later observer receives this same 0x80/0xCC pair
            // before the C2, exactly like the source Delivery.templates path.
            SpawnPackets.SpawnMinion(new SpawnMinionSnapshot(
                ball.NetId, SessionSpawnService.MapNetNodeId, ball.Position,
                ball.Team == Team.Order ? 100 : 200, "TheDoomBall", "OriannaBall",
                IgnoreCollision: true, SkinId: ball.SkinId), _navigation.HeightAt(ball.Position)),
            CombatPackets.MinionStats(ball.NetId, new MinionStatsSnapshot(
                Float(data.BaseHp), Float(data.BaseDamage), Float(data.MoveSpeed),
                Float(data.Armor), Float(data.SpellBlock), 1.0f, 0.0f,
                ActionState: (1u << 10) | (1u << 14) |
                    (ball.NoRender ? 1u << 13 : 0u),
                MagicImmune: 1, Invulnerable: 1, Targetable: 0),
                // Every other 0xCC builder numbers its block from the unit's counter; a fixed 1 repeats
                // the sync of the ball's queued action-time stats, which the client then drops.
                _sync.Next(ball.NetId)),
        };
        return packets;
    }

    private static float Float(double value) => ProtocolEncoding.Float32(value, nameof(value));
}
