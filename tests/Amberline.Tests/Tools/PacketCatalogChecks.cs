using System.Buffers.Binary;
using System.Reflection;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;
using Amberline.Tools.Diagnostics;

namespace Amberline.Tests;

/// <summary>
/// Pins <see cref="PacketCatalog"/> (the checker's channel map and net-ID layouts) to the real
/// Network/Protocol builders: every builder is called with distinct sentinel net IDs, and the catalog must
/// accept its channel and delivery, find every net ID the builder declares, and read nothing but net IDs.
/// </summary>
public static class PacketCatalogChecks
{
    private const uint A = 0x40010001, B = 0x40010002, C = 0x40010003, D = 0x40010004, E = 0x40010005;
    private const uint H = 0x40000001;

    private static readonly NavigationBounds Bounds = new(0, 14000, 0, 14000);
    private static readonly Position3 P3 = new(1, 2, 3);
    private static readonly Position2 P2 = new(100, 100);

    private sealed record Probe(string Builder, PlaintextPacket Packet);

    private static IEnumerable<Probe> Probes()
    {
        var info = new SpellCastInfo(7, 8, 1, A, C, P3, P3, [new SpellCastTarget(D, P3, 0), new SpellCastTarget(E, P3, 0)],
            1, 2, 0, P3);
        var castBlob = SpellPackets.BuildCastInfo(info);
        var recapOrigin = DeathRecapOrigin.BasicAttack(0, DeathRecapSourceType.Hero);
        var recap = new DeathRecapSnapshot(1, A, B, 15.0, DeathRecapSourceType.Hero,
            [new DeathRecapRecord(7.5, A, B, 42.0, 0.0, 0.0, recapOrigin)]);
        var schema = ClassicHeroStatsSchema.Load(GameDataPaths.Default);
        var classic = schema.Definitions.ToDictionary(definition => definition.Name, definition => definition.Kind switch
        {
            ClassicHeroStatKind.Int => ClassicHeroStatValue.FromInt(1),
            ClassicHeroStatKind.Float => ClassicHeroStatValue.FromFloat(1f),
            ClassicHeroStatKind.String => ClassicHeroStatValue.FromString("x"),
            _ => ClassicHeroStatValue.FromLongLong(1),
        });
        var minion = new SpawnMinionSnapshot(A, 0x40, P2, 100, "Minion_Basic", "Minion_Basic");
        var pet = new SpawnPetSnapshot(B, A, 0x40, P2, "Pet", "Pet", CloneNetId: C);
        var neutral = new NeutralSpawnSnapshot(A, 0x40, P3, P3, P3, "Wolf", "Wolf", "Wolf1", "", 300, 0, 0, 0, 0);
        var bubble = new PerceptionBubble(100, P3, 100f, 1f, 5);
        var ping = new Probe("PingPackets.MapPingNotification", PingPackets.MapPingNotification(H, P3, A, MapPingCategory.Offense));

        // LoadingPackets
        yield return new("LoadingPackets.RegistryAdd", LoadingPackets.RegistryAdd(1, 1ul));
        yield return new("LoadingPackets.RegistryRemove", LoadingPackets.RegistryRemove(1, 1ul));
        yield return new("LoadingPackets.QueryStatusAns", LoadingPackets.QueryStatusAns());
        yield return new("LoadingPackets.PingLoadInfo", LoadingPackets.PingLoadInfo(1, 1ul));
        yield return new("LoadingPackets.ReconnectAck", LoadingPackets.ReconnectAck(1ul));
        yield return new("LoadingPackets.PlayerReconnected", LoadingPackets.PlayerReconnected(0));
        yield return new("LoadingPackets.PlayerExited", LoadingPackets.PlayerExited(0));
        yield return new("LoadingPackets.TeamRosterUpdate", LoadingPackets.TeamRosterUpdate([1ul], [2ul]));
        yield return new("LoadingPackets.RequestReskin", LoadingPackets.RequestReskin(1ul, "Annie"));
        yield return new("LoadingPackets.RequestRename", LoadingPackets.RequestRename(1ul, "x"));
        foreach (var packet in LoadingPackets.LoadingBurst(1ul))
            yield return new("LoadingPackets.LoadingBurst", packet);
        yield return new("LoadingPackets.SynchVersionS2C", LoadingPackets.SynchVersionS2C());
        yield return new("LoadingPackets.WorldSendGameNumber", LoadingPackets.WorldSendGameNumber());
        // SpawnPackets
        yield return new("SpawnPackets.StartSpawn", SpawnPackets.StartSpawn());
        yield return new("SpawnPackets.CreateHero", SpawnPackets.CreateHero(A, 1ul, "Annie", "Annie", true));
        yield return new("SpawnPackets.CreateTurret", SpawnPackets.CreateTurret(A, 0x40, "T"));
        yield return new("SpawnPackets.SpawnMinion", SpawnPackets.SpawnMinion(minion, 0.0));
        yield return new("SpawnPackets.SpawnPet", SpawnPackets.SpawnPet(pet, 0.0));
        yield return new("SpawnPackets.CreateNeutral", SpawnPackets.CreateNeutral(neutral));
        yield return new("SpawnPackets.UnitSetMinimapIcon", SpawnPackets.UnitSetMinimapIcon(A, "CenterRelicLeft"));
        yield return new("SpawnPackets.UnitChangeTeam", SpawnPackets.UnitChangeTeam(A, Team.Order));
        yield return new("SpawnPackets.NeutralCampEmpty", SpawnPackets.NeutralCampEmpty(7, 1));
        foreach (var packet in SpawnPackets.AllTurrets([(A, "Turret_T1_C_01", (byte)0x40)]))
            yield return new("SpawnPackets.AllTurrets", packet);
        yield return new("SpawnPackets.EndSpawn", SpawnPackets.EndSpawn());
        yield return new("SpawnPackets.StartGame", SpawnPackets.StartGame());
        yield return new("SpawnPackets.GameClock", SpawnPackets.GameClock(1f));
        yield return new("SpawnPackets.SyncMissionStartTime", SpawnPackets.SyncMissionStartTime(0f));
        yield return new("SpawnPackets.SyncSimTimeFinal", SpawnPackets.SyncSimTimeFinal(1f, 0.1f));
        yield return new("SpawnPackets.SwitchNexusesToIdleParticles", SpawnPackets.SwitchNexusesToIdleParticles());
        yield return new("SpawnPackets.ToggleFow", SpawnPackets.ToggleFow());
        yield return new("SpawnPackets.AddPerceptionBubble", SpawnPackets.AddPerceptionBubble(bubble));
        yield return new("SpawnPackets.AddUnitPerceptionBubble", SpawnPackets.AddUnitPerceptionBubble(A, 100f, 1f, 5));
        yield return new("SpawnPackets.RemovePerceptionBubble", SpawnPackets.RemovePerceptionBubble(5));
        yield return new("SpawnPackets.SetLocalClientId", SpawnPackets.SetLocalClientId(1));
        // VisionPackets
        yield return new("VisionPackets.SetCameraPosition", VisionPackets.SetCameraPosition(A, P3));
        yield return new("VisionPackets.LockCamera", VisionPackets.LockCamera(A, true));
        yield return new("VisionPackets.ChangeCharacterData", VisionPackets.ChangeCharacterData(A, "Annie"));
        yield return new("VisionPackets.AvatarInfo", VisionPackets.AvatarInfo(A));
        yield return new("VisionPackets.WaypointList", VisionPackets.WaypointList(A, [P2, P2], 3));
        yield return new("VisionPackets.Dash", VisionPackets.Dash(A, P2, P2, 800f, Bounds, followNetId: B));
        yield return new("VisionPackets.Teleport", VisionPackets.Teleport(A, P2, 2, Bounds, 3, staging: new Position2(5, 5)));
        yield return new("VisionPackets.EnterLocalVisibility", VisionPackets.EnterLocalVisibility(A, 1f, 1f));
        yield return new("VisionPackets.LeaveLocalVisibility", VisionPackets.LeaveLocalVisibility(A));
        yield return new("VisionPackets.EnterVisibilityClient", VisionPackets.EnterVisibilityClient(A, "Minion_Basic"));
        foreach (var packet in VisionPackets.EnterVisibility(A, "Annie", 0, 1f, 1f))
            yield return new("VisionPackets.EnterVisibility", packet);
        foreach (var packet in VisionPackets.LeaveVisibility(A))
            yield return new("VisionPackets.LeaveVisibility", packet);
        yield return new("VisionPackets.EnterTeamVisibility", VisionPackets.EnterTeamVisibility(A, 1));
        yield return new("VisionPackets.LeaveTeamVisibility", VisionPackets.LeaveTeamVisibility(A, 1));
        // CombatPackets
        yield return new("CombatPackets.BasicAttack", CombatPackets.BasicAttack(A, B, C));
        yield return new("CombatPackets.NpcInstantStopAttack", CombatPackets.NpcInstantStopAttack(A, CombatPackets.LandedAttackFlags));
        // The decision behind it: a hero always gets its stop, so the probe has a packet to catalogue.
        yield return new("CombatPackets.StopAttack",
            CombatPackets.StopAttack(new Entity(A, EntityKind.Hero, UnitType.Hero, Team.Order, P2))!);
        yield return new("CombatPackets.ApplyDamage", CombatPackets.ApplyDamage(A, B, 5f));
        yield return new("CombatPackets.UnitAddGold", CombatPackets.UnitAddGold(A, B, 5f));
        yield return new("CombatPackets.NpcLevelUp", CombatPackets.NpcLevelUp(A, 2, 1));
        yield return new("CombatPackets.UnitApplyHeal", CombatPackets.UnitApplyHeal(A, 1000f, 50f));
        yield return new("CombatPackets.ReplicationHp", CombatPackets.ReplicationHp(A, 5f, 2, isHero: true));
        yield return new("CombatPackets.ReplicationHp", CombatPackets.ReplicationHp(A, 5f, 2, isTurret: true));
        yield return new("CombatPackets.HeroGold", CombatPackets.HeroGold(A, 1f, 2));
        yield return new("CombatPackets.HeroGoldExp", CombatPackets.HeroGoldExp(A, 1f, 2f, 2));
        yield return new("CombatPackets.HeroNeutralKills", CombatPackets.HeroNeutralKills(A, 1, 2));
        yield return new("CombatPackets.HeroStats", CombatPackets.HeroStats(A,
            new HeroStatsSnapshot(MaxHp: 1, MaxMp: 1, MoveSpeed: 1), 2));
        yield return new("CombatPackets.MinionStats", CombatPackets.MinionStats(A, 1f, 1f, 1f, syncId: 2));
        yield return new("CombatPackets.ReplicationPar", CombatPackets.ReplicationPar(A, 1f, 1f, 2));
        yield return new("CombatPackets.MinionActionState", CombatPackets.MinionActionState(A, 7, 2));
        yield return new("CombatPackets.MinionCombatState", CombatPackets.MinionCombatState(A, 7, 1, 1, 1, 2));
        yield return new("CombatPackets.MinionAttackSpeed", CombatPackets.MinionAttackSpeed(A, 1f, 2));
        yield return new("CombatPackets.MinionCurrentStats", CombatPackets.MinionCurrentStats(A, 1f, 1f, 2));
        yield return new("CombatPackets.MinionMovementSpeed", CombatPackets.MinionMovementSpeed(A, 1f, 2));
        yield return new("CombatPackets.MinionVisionPercentModifier", CombatPackets.MinionVisionPercentModifier(A, 0f, 2));
        yield return new("CombatPackets.UnitBubbleRadiusMods", CombatPackets.UnitBubbleRadiusMods(A, 100f, 0.5f, isHero: false, syncId: 2));
        yield return new("CombatPackets.UnitBubbleRadiusMods", CombatPackets.UnitBubbleRadiusMods(A, 100f, 0.5f, isHero: true, syncId: 2));
        yield return new("CombatPackets.UnitTargetableToTeam", CombatPackets.UnitTargetableToTeam(A, true, false, isHero: false, syncId: 2));
        yield return new("CombatPackets.UnitTargetableToTeam", CombatPackets.UnitTargetableToTeam(A, false, true, isHero: true, syncId: 2));
        yield return new("CombatPackets.UnitModelScale", CombatPackets.UnitModelScale(A, 1.5f, isHero: false, syncId: 2));
        yield return new("CombatPackets.UnitModelScale", CombatPackets.UnitModelScale(A, 1.5f, isHero: true, syncId: 2));
        yield return new("CombatPackets.TurretStats", CombatPackets.TurretStats(A, 1f, 1f, syncId: 2));
        yield return new("CombatPackets.TurretProtectionFlags", CombatPackets.TurretProtectionFlags(A, 0x01000000, 2));
        yield return new("CombatPackets.BuildingStats", CombatPackets.BuildingStats(A, 1f, 0x02000000, syncId: 2));
        yield return new("CombatPackets.DampenerState", CombatPackets.DampenerState(A, 1f, dead: true));
        yield return new("CombatPackets.AiSetTarget", CombatPackets.AiSetTarget(A, B));
        yield return new("CombatPackets.AiSetTargetHero", CombatPackets.AiSetTargetHero(A, B));
        yield return new("CombatPackets.NpcDie", CombatPackets.NpcDie(A, B));
        yield return new("CombatPackets.NpcDie", CombatPackets.NpcDie(A, B, opcode: 0x61));
        yield return new("CombatPackets.DeathRecap", CombatPackets.DeathRecap(recap, recap.Records));
        yield return new("CombatPackets.ForceDead", CombatPackets.ForceDead(A));
        yield return new("CombatPackets.ShowHealthBar", CombatPackets.ShowHealthBar(A, false));
        yield return new("CombatPackets.BuildingDie", CombatPackets.BuildingDie(A, B));
        yield return new("CombatPackets.HeroMultiKill", CombatPackets.HeroMultiKill(A, B, 2));
        yield return new("CombatPackets.ChampionDie", CombatPackets.ChampionDie(A, B, 300.0f, []));
        yield return new("CombatPackets.OnEventWorld", CombatPackets.OnEventWorld(79, A, B, 1));
        yield return new("CombatPackets.HeroReincarnate", CombatPackets.HeroReincarnate(A, P3));
        yield return new("CombatPackets.MoveCameraToPoint", CombatPackets.MoveCameraToPoint(A, P3));
        yield return new("CombatPackets.DisableHudForEndOfGame", CombatPackets.DisableHudForEndOfGame());
        yield return new("CombatPackets.SetGreyscaleEnabledWhenDead", CombatPackets.SetGreyscaleEnabledWhenDead(A, true));
        yield return new("CombatPackets.SetInputLockingFlag", CombatPackets.SetInputLockingFlag(1, true));
        yield return new("CombatPackets.FadeOutMainSfx", CombatPackets.FadeOutMainSfx(1f));
        yield return new("CombatPackets.FadeMinions", CombatPackets.FadeMinions(Team.Order, 0.5f, 1f));
        yield return new("CombatPackets.SetNexusSkin", CombatPackets.SetNexusSkin(Team.Order, 1));
        yield return new("CombatPackets.EndOfGameEvent", CombatPackets.EndOfGameEvent(true));
        yield return new("CombatPackets.EndGame", CombatPackets.EndGame(true));
        // DominionPackets: capture and score (batch 445); no net ID is declared, the keys are Reconstruction.
        yield return new("DominionPackets.CapturePropFlexValue", DominionPackets.CapturePropFlexValue(A, 7));
        yield return new("DominionPackets.CapturePointEvent", DominionPackets.CapturePointEvent(A, 1, 2, parFlex: true));
        yield return new("DominionPackets.CapturePointValue", DominionPackets.CapturePointValue(1, A, 2, apply: true));
        yield return new("DominionPackets.SetGameScore", DominionPackets.SetGameScore(Team.Order, 3));
        yield return new("DominionPackets.CreateRespawnPoint", DominionPackets.CreateRespawnPoint(1, A));
        yield return new("DominionPackets.SetRespawnPointState", DominionPackets.SetRespawnPointState(A, 1));
        yield return new("DominionPackets.IncrementPlayerStat", DominionPackets.IncrementPlayerStat(A, DominionPackets.NodeCaptureStat));
        yield return new("DominionPackets.IncrementPlayerScore", DominionPackets.IncrementPlayerScore(A, DominionPackets.CombatScore, 14,
            callout: true, 15f));
        yield return new("DominionPackets.QuestUpdate", DominionPackets.QuestUpdate(1, DominionPackets.PrimaryQuest,
            DominionPackets.QuestAdd, false, "game_quest_capture_a"));
        yield return new("DominionPackets.FloatingText", DominionPackets.FloatingText(A, DominionPackets.ScoreFloatingText, -2,
            "game_floating_enemy_kill"));
        // SpellPackets
        yield return new("SpellPackets.CharacterFade", SpellPackets.CharacterFade(A, 3, 0.2f));
        yield return new("SpellPackets.CharacterFade", SpellPackets.CharacterFade(A, 3));
        yield return new("SpellPackets.SetAutoAttackSpellSlot", SpellPackets.SetAutoAttackSpellSlot(A, 1));
        yield return new("SpellPackets.SetAnimationStates", SpellPackets.SetAnimationStates(A, [("a", "b")]));
        yield return new("SpellPackets.FaceDirection", SpellPackets.FaceDirection(A, P3));
        yield return new("SpellPackets.PushCharacterData", SpellPackets.PushCharacterData(A, 1, "Annie"));
        yield return new("SpellPackets.PopCharacterData", SpellPackets.PopCharacterData(A, 1));
        yield return new("SpellPackets.PopAllCharacterData", SpellPackets.PopAllCharacterData(A));
        yield return new("SpellPackets.SpellOffsetTarget", SpellPackets.SpellOffsetTarget(A, 1, B));
        yield return new("SpellPackets.PauseAnimation", SpellPackets.PauseAnimation(A, true));
        yield return new("SpellPackets.PlayAnimation", SpellPackets.PlayAnimation(A, "Spell1"));
        yield return new("SpellPackets.StopAnimation", SpellPackets.StopAnimation(A));
        yield return new("SpellPackets.SpellTooltipVariables", SpellPackets.SpellTooltipVariables(
            [new SpellTooltipEntry(1, 1f, 2f, 3f), new SpellTooltipEntry(2, 1f, 2f, 3f)], A));
        yield return new("SpellPackets.BuffTooltipVariables", SpellPackets.BuffTooltipVariables(A, 1, 1f, 2f, 3f));
        yield return new("SpellPackets.CastSpellAnswer", SpellPackets.CastSpellAnswer(A, info, 5));
        yield return new("SpellPackets.CastSpellAnswer", SpellPackets.CastSpellAnswer(A, castBlob, 5));
        yield return new("SpellPackets.UpgradeSpellAnswer", SpellPackets.UpgradeSpellAnswer(A, 1, 1, 1));
        yield return new("SpellPackets.SetCooldown", SpellPackets.SetCooldown(A, 1, 1f));
        yield return new("SpellPackets.SetSpellData", SpellPackets.SetSpellData(A, 7, 1));
        yield return new("SpellPackets.ChangeSlotSpellName", SpellPackets.ChangeSlotSpellName(A, 1, "Spell"));
        yield return new("SpellPackets.ChangeSlotSpellType", SpellPackets.ChangeSlotSpellType(A, 1, 1));
        yield return new("SpellPackets.MissileReplication", SpellPackets.MissileReplication(A, P3, P3, P3, P3, P3, P3, P3,
            1f, 0f, 0, info));
        yield return new("SpellPackets.MissileReplication", SpellPackets.MissileReplication(A, P3, P3, P3, P3, P3, P3, P3,
            1f, 0f, 0, castBlob));
        yield return new("SpellPackets.DestroyMissile", SpellPackets.DestroyMissile(C));
        yield return new("SpellPackets.ChainMissileSync", SpellPackets.ChainMissileSync(C, A, [B, A, B]));
        yield return new("SpellPackets.LineMissileHitList", SpellPackets.LineMissileHitList(C, [A, B]));
        yield return new("SpellPackets.FinishCast", SpellPackets.FinishCast(A));
        yield return new("SpellPackets.ModifyShield", SpellPackets.ModifyShield(A, 5f));
        yield return new("SpellPackets.RenewBuff", SpellPackets.RenewBuff(A, 1, 5f, B));
        yield return new("SpellPackets.UpdateBuffCount", SpellPackets.UpdateBuffCount(A, 1, 2, 5f, B));
        yield return new("SpellPackets.AddBuff", SpellPackets.AddBuff(new BuffAdd(A, 1, 1, 0x1234, 5f, B)));
        yield return new("SpellPackets.RemoveBuff", SpellPackets.RemoveBuff(A, 1, 0x1234));
        yield return new("SpellPackets.LevelUpSpell", SpellPackets.LevelUpSpell(A, 1));
        yield return new("SpellPackets.ChangeCharacterVoice", SpellPackets.ChangeCharacterVoice(A, "Ult"));
        yield return new("SpellPackets.ChangeParColorOverride", SpellPackets.ChangeParColorOverride(A, new ParColorOverride(0x11223344u, 0x55667788u)));
        yield return new("SpellPackets.ColorRemapFx", SpellPackets.ColorRemapFx(true, 1f, 100, 0xFF336699u, 0.5f));
        yield return new("CombatPackets.MusicCueCommand", CombatPackets.MusicCueCommand(A, MusicCueAction.Begin, 15));
        // FxPackets, ItemsPackets, ClassicHeroStatsPackets, and the single-purpose builders
        yield return new("FxPackets.CreateGroup", FxPackets.CreateGroup(1, C, P2, Bounds, (_, _) => 0.0, targetNetId: B,
            bindNetId: D, headerNetId: A));
        yield return new("FxPackets.Kill", FxPackets.Kill(C, A));
        yield return new("ItemsPackets.BuyItem", ItemsPackets.BuyItem(A, 1, 1001, 1, false));
        yield return new("ItemsPackets.RemoveItem", ItemsPackets.RemoveItem(A, 1, 1));
        yield return new("ItemsPackets.SwapItem", ItemsPackets.SwapItem(A, 1, 2));
        yield return new("ItemsPackets.UseItem", ItemsPackets.UseItem(A, 1, 1, 1));
        yield return new("ItemsPackets.SetItem", ItemsPackets.SetItem(A, 1, 1001, 1, 0));
        yield return new("ClassicHeroStatsPackets.HeroStats", ClassicHeroStatsPackets.HeroStats(A, schema, classic));
        yield return new("AfkPackets.OpenWarningMessage", AfkPackets.OpenWarningMessage());
        yield return new("AfkPackets.LocalizedChatText", AfkPackets.LocalizedChatText("key"));
        // Batch 563: the Basic Tutorial's client packets, one probe per opcode.
        foreach (var opcode in new[] { TutorialPackets.ShowObjectiveTextOpcode, TutorialPackets.RefreshObjectiveTextOpcode, TutorialPackets.ShowAuxiliaryTextOpcode,
                     TutorialPackets.RefreshAuxiliaryTextOpcode, TutorialPackets.OpenTutorialPopupOpcode })
            yield return new("TutorialPackets.Text", TutorialPackets.Text(opcode, "game_tutorial_key"));
        yield return new("TutorialPackets.Header", TutorialPackets.Header(TutorialPackets.HideObjectiveTextOpcode));
        yield return new("TutorialPackets.Header", TutorialPackets.Header(TutorialPackets.HideAuxiliaryTextOpcode));
        yield return new("TutorialPackets.Header", TutorialPackets.Header(TutorialPackets.CloseShopOpcode));
        yield return new("TutorialPackets.PlayVoAudioEvent", TutorialPackets.PlayVoAudioEvent("Tutorial", "Tutorial_Welcome", true, 7));
        yield return new("TutorialPackets.ToggleInputLockingFlag", TutorialPackets.ToggleInputLockingFlag(4));
        yield return new("TutorialPackets.MuteVolumeCategory", TutorialPackets.MuteVolumeCategory(1, true));
        yield return new("TutorialPackets.ToggleUiHighlight", TutorialPackets.ToggleUiHighlight(1, 6, 0, 0, true));
        yield return new("TutorialPackets.HighlightHudElement", TutorialPackets.HighlightHudElement(7, 0));
        yield return new("TutorialPackets.UnitHighlight", TutorialPackets.UnitHighlight(A, create: true));
        yield return new("TutorialPackets.UnitHighlight", TutorialPackets.UnitHighlight(A, create: false));
        yield return new("ChatPackets.Envelope", ChatPackets.Envelope(1, false, "hi"u8));
        yield return new("EmotePackets.Play", EmotePackets.Play(A, ChampionEmote.Dance));
        yield return new("PausePackets.Pause", PausePackets.Pause(1, 5));
        yield return new("PausePackets.Resume", PausePackets.Resume(1, false));
        yield return new("SurrenderPackets.Vote", SurrenderPackets.Vote(Team.Order, A, true, true, 1, 0, 5, 20.0));
        yield return new("SurrenderPackets.Status", SurrenderPackets.Status(Team.Order, SurrenderStatus.Agreed, 1, 0));
        yield return ping;
        foreach (var packet in TemporaryCasterPackets.Create(new MissileCasterProjection(A, B, Team.Order, P3, P2), info, _ => 2))
            yield return new("TemporaryCasterPackets.Create", packet);
    }

    // Builders that make client-to-server requests; they are not part of the S2C catalog and sit on channel 1.
    private static readonly string[] ClientBuilders = ["ClientGameplayPackets.MoveOrder", "PingPackets.MapPingRequest"];

    [Check("packet-rules.catalog-matches-builders")]
    public static void CatalogMatchesBuilders()
    {
        var catalog = PacketCatalog.Default;
        var probes = Probes().ToArray();
        TestAssert.True(probes.Length > 120, $"probe list is complete enough ({probes.Length})");
        Span<uint> found = stackalloc uint[512];
        var sentinels = new HashSet<uint> { A, B, C, D, E, H };
        foreach (var probe in probes)
        {
            var packet = probe.Packet;
            var payload = packet.Plaintext.Span;
            var what = $"{probe.Builder} 0x{payload[0]:X2}";
            if (PacketCatalog.IsChatChannel(packet.Channel))
            {
                TestAssert.Equal(PacketCatalog.ChatReliable, packet.Reliability == LeaguePacketReliability.Reliable, what + " chat delivery");
                continue;
            }
            TestAssert.True(catalog.TryGet(payload[0], packet.Channel, out var spec),
                $"{what} on channel {packet.Channel} has no catalog row");
            TestAssert.Equal(spec.Reliable, packet.Reliability == LeaguePacketReliability.Reliable,
                $"{what} delivery {packet.Reliability} vs the catalog's {(spec.Reliable ? "reliable" : "unreliable")}");

            // 0xCC is parsed by the checker, not read by offsets: its one entry's unit is the declared reference.
            if (payload[0] == 0xCC)
            {
                TestAssert.Equal(packet.EntityReferences.Single().NetId, BinaryPrimitives.ReadUInt32LittleEndian(payload[11..]),
                    what + " unit netid");
                continue;
            }
            // Every net ID the builder declares (apart from effect/missile IDs, which the checker does not track)
            // is read by the catalog...
            var read = spec.ReadReferences(payload, found);
            var readIds = found[..read].ToArray();
            foreach (var reference in packet.EntityReferences)
            {
                if (reference.Role is LeaguePacketEntityRole.Effect or LeaguePacketEntityRole.Create) continue;
                // FX bind/target and cast targets are catalog rows; the effect ID sentinel C is a Create role there.
                TestAssert.True(readIds.Contains(reference.NetId),
                    $"{what} declares {reference.Role} 0x{reference.NetId:X8} but the catalog does not read it");
            }
            // ...and what the catalog reads really is a net ID: nothing else is in the probe's sentinel set.
            foreach (var id in readIds)
                TestAssert.True(sentinels.Contains(id),
                    $"{what}: the catalog reads 0x{id:X8}, which is not a net ID the probe wrote");

            // Creation rows: the spawn builders' Create reference is the one the catalog reads.
            var created = new uint[8];
            var createdCount = spec.ReadCreations(payload, created);
            if (payload[0] is 0x4F or 0xA5 or 0x80 or 0x3C or 0x66 && packet.Channel == 3)
            {
                var declared = packet.EntityReferences.Where(reference => reference.Role == LeaguePacketEntityRole.Create)
                    .Select(reference => reference.NetId).ToArray();
                TestAssert.Equal(declared.OrderBy(id => id).ToArray(), created[..createdCount].OrderBy(id => id).ToArray(),
                    what + " creation");
            }
            else TestAssert.Equal(0, createdCount, what + " creates nothing");
        }

        // The client-request builders stay on channel 1, which the catalog never lists for S2C.
        TestAssert.Equal((byte)1, ClientGameplayPackets.MoveOrder(A, 2, P2).Channel);
        TestAssert.Equal((byte)1, PingPackets.MapPingRequest(A, P3, 0, MapPingCategory.Offense).Channel);
        TestAssert.False(catalog.TryGet(0x75, 1, out _) || catalog.TryGet(0x5A, 1, out _), "no S2C row on channel 1");

        // Every public builder is probed above: adding a builder (a new opcode) without a probe and a catalog
        // row fails here, which is how the checker "flags opcodes missing from the catalog" at build time.
        var probed = probes.Select(probe => probe.Builder).Concat(ClientBuilders).ToHashSet(StringComparer.Ordinal);
        var builders = new[] { typeof(LoadingPackets), typeof(SpawnPackets), typeof(VisionPackets), typeof(CombatPackets),
                typeof(SpellPackets), typeof(FxPackets), typeof(ItemsPackets), typeof(ClassicHeroStatsPackets),
                typeof(AfkPackets), typeof(ChatPackets), typeof(EmotePackets), typeof(PausePackets),
                typeof(SurrenderPackets), typeof(PingPackets), typeof(ClientGameplayPackets), typeof(TemporaryCasterPackets), typeof(TutorialPackets) }
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => method.ReturnType == typeof(PlaintextPacket) ||
                                 method.ReturnType == typeof(IReadOnlyList<PlaintextPacket>))
                .Select(method => $"{type.Name}.{method.Name}"))
            .Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var missing = builders.Where(name => !probed.Contains(name)).ToArray();
        TestAssert.True(missing.Length == 0, "builders without a probe and catalog row: " + string.Join(", ", missing));

        // Catalog rows are unique per opcode and channel, and 0xCC is the one opcode the checker parses itself.
        TestAssert.True(catalog.TryGet(0xCC, 3, out var replication) && replication.Reliable, "0xCC is reliable on channel 3");
        TestAssert.Equal(catalog.Specs.Count, catalog.Specs.Select(spec => (spec.Opcode, spec.Channel)).Distinct().Count());
    }

    /// <summary>The replication layout the checker parses, pinned to the real encoders for every 0xCC builder.</summary>
    [Check("packet-rules.replication-layout-matches-builders")]
    public static void ReplicationLayoutMatchesBuilders()
    {
        // Each builder writes one entry whose header fields sit where the checker reads them.
        var packets = new (string Name, PlaintextPacket Packet, byte SetMaps)[]
        {
            ("HeroStats", CombatPackets.HeroStats(A, new HeroStatsSnapshot(MaxHp: 1, MaxMp: 1, MoveSpeed: 1), 9), 0x0B),
            ("MinionStats", CombatPackets.MinionStats(A, 1f, 1f, 1f, syncId: 9), 0x0A),
            ("TurretStats", CombatPackets.TurretStats(A, 1f, 1f, syncId: 9), 0x0A),
            ("BuildingStats", CombatPackets.BuildingStats(A, 1f, 0, syncId: 9), 0x02),
            ("ReplicationHp", CombatPackets.ReplicationHp(A, 1f, 9, isHero: true), 0x08),
            ("HeroGold", CombatPackets.HeroGold(A, 1f, 9), 0x01),
            ("HeroGoldExp", CombatPackets.HeroGoldExp(A, 1f, 1f, 9), 0x01),
            ("MinionMovementSpeed", CombatPackets.MinionMovementSpeed(A, 1f, 9), 0x08),
            ("UnitBubbleRadiusMods", CombatPackets.UnitBubbleRadiusMods(A, 1f, 1f, isHero: false, syncId: 9), 0x08),
            ("UnitTargetableToTeam", CombatPackets.UnitTargetableToTeam(A, true, false, isHero: false, syncId: 9), 0x02),
            ("UnitModelScale", CombatPackets.UnitModelScale(A, 1.5f, isHero: false, syncId: 9), 0x08),
            ("TurretProtectionFlags", CombatPackets.TurretProtectionFlags(A, 0, 9), 0x02),
        };
        foreach (var (name, packet, setMaps) in packets)
        {
            var bytes = packet.Plaintext.Span;
            TestAssert.Equal((byte)0xCC, bytes[0], name);
            TestAssert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..]), name + " syncID at 5");
            TestAssert.Equal((byte)1, bytes[9], name + " entry count at 9");
            TestAssert.Equal(setMaps, bytes[10], name + " setMaps at 10");
            TestAssert.Equal(A, BinaryPrimitives.ReadUInt32LittleEndian(bytes[11..]), name + " unit netid at 11");
            // A real parse of the builder's bytes finds nothing malformed.
            var checker = new PacketStreamChecker(1);
            checker.Observe(new PacketObservation(0, 0, packet.Channel, true, packet.Plaintext));
            TestAssert.Equal(0L, checker.TotalsByKind.GetValueOrDefault(PacketRuleKinds.MalformedReplication), name + " parses");
        }
    }
}
