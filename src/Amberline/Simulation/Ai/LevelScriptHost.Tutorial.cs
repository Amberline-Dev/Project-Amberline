using Amberline.Core;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Scripting.Lua;
using LuaValue = Amberline.Scripting.Lua.LuaValue;

namespace Amberline.Simulation.Ai;

/// <summary>
/// One TutorialScript native the engine answers outside the script's state: a client packet (objective text, popup, audio, camera,
/// fog, input lock, volume, ping) or a unit flag. <see cref="LevelScriptHost.TutorialEngine"/> handles it; the host stays packet-free.
/// </summary>
public sealed record TutorialCall(string Native, Entity? Unit = null, Entity? Other = null, Position3? From = null, Position3? To = null,
    string? Text = null, double Number = 0.0, bool Flag = false, uint Id = 0, IReadOnlyList<double>? Values = null);

/// <summary>
/// Batch 563 (phase 1 of the Basic Tutorial): map 3's <c>Scripts/TutorialScript.luaobj</c> in the level script's own state. The script
/// reads the LevelScript's globals (<c>CENTER_LANE</c>, <c>FRONT_TOWER</c>, <c>BACK_TOWER</c>: TutorialScript.txt proto 0.3 pc 0057-0074;
/// none is set by the TutorialScript itself) and arms its steps through the same <c>InitTimer</c>, so it runs in this environment, loaded
/// the way <c>LoadScriptIntoScript</c> loads a chunk. Its lessons advance through callbacks the script names: timers, the client's popup and
/// audio acknowledgements, and the <c>RegisterOn*Callback</c> conditions, which this host polls once per step.
/// </summary>
public sealed partial class LevelScriptHost
{
    public const string TutorialScriptName = "TutorialScript";

    /// <summary>
    /// The TutorialScript sets <c>TUTORIAL_START_TIME = 20</c> (main chunk pc 0000-0001) and never reads it, and neither the 126 client nor
    /// another shipped script names it. Batch 580 started <c>OnTutorialInit</c> that many seconds after the level's <c>OnLevelInit</c>; batch 593
    /// starts it at the game's start (Reconstruction, see <see cref="LoadTutorial"/>), so the global is only kept for the record.
    /// </summary>
    public const string TutorialStartTimeGlobal = "TUTORIAL_START_TIME";

    /// <summary>The engine half of the tutorial natives (packets and unit flags); without one (a check) the call is only logged.</summary>
    public Action<TutorialCall>? TutorialEngine { get; set; }

    /// <summary>
    /// The BuildingBlocks host that runs <c>BBSpawnMinion</c>, <c>BBSpellBuffAdd</c> and the other natives BBLuaConversionLibrary calls:
    /// (native, block parameters, the scope's root table, the hook's own unit). The composition points it at the unit-character module.
    /// </summary>
    public Action<string, BbTable, BbTable, Entity?>? RunBuildingBlock { get; set; }

    /// <summary>
    /// <c>GetTutorialPlayer()</c>. Reconstruction: the one human of the tutorial; the default is the first Order hero by net id (Ashe,
    /// seat 0 of the tutorial roster).
    /// </summary>
    public Func<Entity?>? TutorialPlayer { get; set; }

    /// <summary>
    /// DIAGNOSTICS: a check that drives the host without a session answers the client's acknowledgements itself after this many seconds: a
    /// popup's OK (C2S 0xD5) and an audio event's end (C2S 0x05). Null in a real match and in the headless tool, whose stand-in player sends
    /// them over the wire (batch 593).
    /// </summary>
    public double? TutorialAutoAcknowledgeSeconds { get; set; }

    /// <summary>
    /// Batch 580: <c>BBCreateItem</c> (the library's <c>CreateItem(unit, item)</c>, UnitVar and ItemID: BBLuaConversionLibrary.luaobj proto 0.2).
    /// The composition points it at <c>ShopService.Grant</c>. Reconstruction: the client registers the block as a bare return, so the server's grant is not in the shipped files; the item joins the inventory free of charge.
    /// The answer is null when the item was placed, else the refusal (logged as <c>tutorial.native</c>).
    /// </summary>
    public Func<Entity, int, string?>? GrantItem { get; set; }

    /// <summary>Where the tutorial's diagnostic lines go (the native calls the engine does not answer yet, every callback run).</summary>
    public Action<string, IReadOnlyDictionary<string, object?>>? TutorialLog { get; set; }

    /// <summary>Ends a unit the script retired with <c>ExpirationTimer</c> that outlived it (the composition points it at <c>CombatService.Kill</c>).</summary>
    public Action<Entity>? RetireUnit { get; set; }

    /// <summary>
    /// Batch 593: the lane route ahead of a unit the script spawned with <c>minion.lua</c> (the composition answers from the map's lane
    /// routes, <c>WaveScheduler.LaneRoute</c>), or null.
    /// </summary>
    public Func<Entity, List<Position2>?>? ScriptMinionRoute { get; set; }

    /// <summary>
    /// Batch 580: releases a tutorial bot from the generic bot script and orders it to attack a unit (the composition points it at the bot
    /// controller's seat release and the hero orders' attack). See <see cref="DirectTutorialBots"/>.
    /// </summary>
    public Action<Entity, Entity>? TutorialBotAttack { get; set; }

    /// <summary>DIAGNOSTICS: how many units the script gave <c>ExpirationTimer</c> are still alive (the headless report reads it).</summary>
    public int TutorialExpiredAlive => _expired.Count(id => _world.Get(id) is { Alive: true });

    /// <summary>Whether this host loaded the TutorialScript (map 3 only).</summary>
    public bool TutorialLoaded => _tutorial;

    /// <summary>Every tutorial step that ran, in order: the callback or timer name and the match-clock moment.</summary>
    public IReadOnlyList<(double At, string Step)> TutorialSteps => _tutorialSteps.ToArray();

    /// <summary>The callbacks registered and not yet fired, by kind (the checks and the headless report read them).</summary>
    public IReadOnlyList<string> TutorialWaits =>
        [.. _watches.Select(watch => $"{watch.Kind}:{watch.Callback}"),
         .. _popup is { } popup ? [$"Popup:{popup.Callback}"] : Array.Empty<string>(),
         .. _audio.Values.Select(audio => $"Audio:{audio.Callback}")];

    private bool _tutorial;
    private readonly List<(double At, string Step)> _tutorialSteps = new();
    private readonly List<TutorialWatch> _watches = new();
    private readonly Dictionary<uint, (string Callback, double At)> _audio = new();
    private (string Callback, double At)? _popup;
    private uint _nextAudioId;
    private double _shopOpenedAt = double.NegativeInfinity;
    private Position2? _cameraFocus;

    private sealed class TutorialWatch(string kind, string callback)
    {
        public string Kind { get; } = kind;
        public string Callback { get; } = callback;
        public Entity? Unit { get; init; }
        public Entity? Other { get; init; }
        public Position2 Point { get; init; }
        public double Value { get; init; }
        public Team Team { get; init; }
        /// <summary>The polled counter at registration (a spell rank, a cast count), or whether the watched team had a minion yet.</summary>
        public double Baseline { get; set; }
        public double RegisteredAt { get; init; }
    }

    /// <summary>
    /// Batch 580: the HIGHLIGHT_* ids the script passes to <c>ToggleUIHighlight</c>/<c>HighlightHUDElement</c>, read from the 126 client's
    /// handlers (the debug data has no values for the enums). The element is case 0x52's selector: 1 the HUD, 2 the shop, 3 the titan bar
    /// (<see cref="Amberline.Network.Protocol.TutorialPackets.ToggleUiHighlightOpcode"/>). A HUD type indexes the Flash element names in the client's HUD table:
    /// 0 "spell", 1 "levelUp", 2 "summonerSpell", 3 "recall", 4 "shopButton", 5 "gold", 6 "experienceBar", 7 "healthBar",
    /// 8 "manaBar" (4-6 go to the tactical map's table in the same order). The titan bar's table holds only
    /// "camera" (0), and the shop's table is ShopHighlightType_t (<c>HIGHLIGHT_TYPE_RECOMMENDED_ITEM = 4</c>).
    /// </summary>
    private static readonly (string Name, double Value)[] HighlightIds =
    [
        ("HIGHLIGHT_UI_ELEMENT_HUD", 1), ("HIGHLIGHT_UI_ELEMENT_SHOP", 2), ("HIGHLIGHT_UI_ELEMENT_TITANBAR", 3),
        ("HIGHLIGHT_TYPE_RECOMMENDED_ITEM", 4), ("HIGHLIGHT_TYPE_SPELL", 0), ("HIGHLIGHT_TYPE_SPELL_LEVELUP", 1),
        ("HIGHLIGHT_TYPE_AVATAR_SPELL", 2), ("HIGHLIGHT_TYPE_RECALL", 3), ("HIGHLIGHT_TYPE_GOLD", 5), ("HIGHLIGHT_TYPE_XP", 6),
        ("HIGHLIGHT_TYPE_HP", 7), ("HIGHLIGHT_TYPE_CAMERA", 0),
    ];

    /// <summary>The client's <c>VolumeCategory</c>: the byte MuteVolumeCategory carries.</summary>
    private static readonly (string Name, double Value)[] VolumeIds =
    [
        ("VOLUME_MASTER", 0), ("VOLUME_MUSIC", 1), ("VOLUME_SFX", 2), ("VOLUME_ANNOUNCER", 3), ("VOLUME_UNIT_RESPONSES", 4),
        ("VOLUME_ENVIRONMENT", 5), ("VOLUME_SFX_MAIN", 6),
    ];

    /// <summary>The BB natives BBLuaConversionLibrary.luaobj calls (its 18 wrappers, DATA/BuildingBlocks).</summary>
    private static readonly string[] LibraryBlocks =
    [
        "BBAddPosPerceptionBubble", "BBApplyDamage", "BBCreateItem", "BBFaceDirection", "BBGetChampionBySkinName", "BBIncGold", "BBMove",
        "BBRemovePerceptionBubble", "BBSetCameraPosition", "BBSpellBuffAdd", "BBSpellBuffRemove", "BBSpellEffectCreate", "BBSpawnMinion",
        "BBTeleportToPosition", "BBAddDebugCircle", "BBRemoveDebugCircle", "BBModifyDebugCircleRadius", "BBModifyDebugCircleColor",
    ];

    /// <summary>
    /// Map 3: loads the TutorialScript into this state, installs its natives and constants, and arms <c>OnTutorialInit</c> for the game's
    /// first step. Called once from <see cref="OnLevelInit"/>.
    /// <para>
    /// Reconstruction (batch 593, owner's native game 1 November 2026): what starts the tutorial in retail is not in the shipped files. It
    /// starts with the game: OnTutorialInit's first lines lock every input (proto 0.3 pc 0222-0251), and with batch 580's 20 s wait the
    /// player was free for the first 20 seconds; in the owner's game the client ranked W (C2S 0x3E slot 1, server 19.6 s), so at the lesson
    /// that asks for W (EventSpendAbilityPoint, RegisterOnSpellLevelupCallback(1)) W was at its level-2 cap and the tutorial could not go on.
    /// </para>
    /// </summary>
    private void LoadTutorial()
    {
        _tutorial = true;
        RegisterTutorialNatives();
        // Reconstruction: the script reads BUFF_RENEW_EXISTING, BUFF_Internal, BUFF_CombatEnhancer (proto 0.3 pc 0026-0027) and the
        // library TRUE_DAMAGE and DAMAGESOURCE_RAW, which nothing in the level state sets. They are the client's BuildingBlocks registrations
        // (BbConstants); a name the level script already holds keeps its value.
        foreach (var (name, value) in BbConstants.All)
            if (!_state.TryGetGlobal(name, out var existing) || existing.Type == LuaType.Nil) _state.SetGlobal(name, LuaValue.Of(value));
        foreach (var (name, value) in HighlightIds.Concat(VolumeIds)) _state.SetGlobal(name, LuaValue.Of(value));
        Body(TutorialScriptName, () => { _catalog.RunIn(_state, TutorialScriptName); return Array.Empty<LuaValue>(); });
        _timers.Add(new AiTimer("OnTutorialInit", 0.0, false) { Deadline = _now });
    }

    /// <summary>OnTutorialPopupClosed (0xD5): the open popup's callback runs.</summary>
    public void TutorialPopupClosed(double now)
    {
        if (_popup is not { } popup) return;
        _popup = null;
        _now = now;
        RunTutorialStep(popup.Callback);
    }

    /// <summary>TutorialAudioEventFinished (0x05): the event's callback runs.</summary>
    public void TutorialAudioFinished(uint audioEventId, double now)
    {
        if (!_audio.Remove(audioEventId, out var audio)) return;
        _now = now;
        RunTutorialStep(audio.Callback);
    }

    /// <summary>Batch 580: OnShopOpened (0x60) arrived; a <c>RegisterOnShopOpenedCallback</c> made before it fires on the next step.</summary>
    public void TutorialShopOpened(double now) => _shopOpenedAt = now;

    /// <summary>
    /// Batch 580: the ground point the player's camera looks at (WorldSendCameraServer, 0x30), which
    /// <c>RegisterOnCameraNearUnitCallback</c> watches. Reconstruction: the native's "camera near" is this point within its distance of the
    /// unit; the retail server's reading of the report is not in the shipped files.
    /// </summary>
    public void TutorialCameraMoved(Position2 focus) => _cameraFocus = focus;

    private void RunTutorialStep(string callback)
    {
        if (callback.Length == 0) return;
        _tutorialSteps.Add((_now, callback));
        TutorialLog?.Invoke("tutorial.step", new Dictionary<string, object?> { ["step"] = callback, ["at"] = _now });
        _waveShift = callback == "EventTurretDeath" ? ChampionBattleMidway() : null;
        try
        {
            Run(callback, () => _state.CallGlobal(callback));
        }
        finally
        {
            _waveShift = null;
        }
    }

    // The offset EventTurretDeath's wave is spawned with (ChampionBattleMidway); null outside that step.
    private Position2? _waveShift;

    /// <summary>
    /// NOT RETAIL (batch 671, owner decision 3 November 2026: "Master YI and the wave should spawn midway from the enemy tower to the enemy
    /// nexus and he should respawn"). Retail's EventTurretDeath (TutorialScript proto 0.74) frees Master Yi where he stands, at the Chaos
    /// spawn point (OnTutorialInit's Stun and SetNoRender, proto 0.3 pc 0252-0275), and its SpawnChaosMinions (proto 0.69) puts the wave on
    /// chaosBasicSpawnPoints and chaosWizardSpawnPoints around the Chaos HQ (the four basic points' centre is the HQ's to 15 units); he respawns
    /// at that spawn point and nothing in the script sends him back. Here, before that step runs, he is moved (the script's own
    /// TeleportToPosition, while still unseen) to the floor point midway between firstChaosTurret and the Chaos HQ, he respawns there, and the
    /// step's wave spawns on its shipped points moved by the HQ-to-midpoint offset, so its wizards stay behind its melee minions. Returns that
    /// offset, or null when a unit is missing.
    /// </summary>
    private Position2? ChampionBattleMidway()
    {
        if (GlobalUnit("firstChaosTurret") is not { } turret || FindNexus(Team.Chaos) is not { } hq ||
            !_state.TryGetGlobal("botID", out var botValue) || EntityOf([botValue], 0) is not { } bot) return null;
        var midway = new Position2((turret.Position.X + hq.Position.X) / 2.0, (turret.Position.Z + hq.Position.Z) / 2.0);
        midway = _navigation?.NearestMovementFloor(midway, maxCells: 4) ?? midway;
        Body("ChampionBattleMidway", () => _state.CallGlobal("TeleportToPosition", botValue,
            PointValue(new Position3(midway.X, _navigation?.HeightAt(midway) ?? 0.0, midway.Z))));
        bot.RespawnPoint = midway;
        return new Position2(midway.X - hq.Position.X, midway.Z - hq.Position.Z);
    }

    private void NoteTutorialTimer(string name)
    {
        _tutorialSteps.Add((_now, name));
        TutorialLog?.Invoke("tutorial.step", new Dictionary<string, object?> { ["step"] = name, ["at"] = _now, ["timer"] = true });
    }

    /// <summary>The per-step pass over the registered conditions and, in the headless tool, the client's acknowledgements.</summary>
    private void AdvanceTutorial(double now)
    {
        if (TutorialAutoAcknowledgeSeconds is double delay)
        {
            if (_popup is { } popup && now >= popup.At + delay) TutorialPopupClosed(now);
            foreach (var (id, audio) in _audio.ToArray())
                if (now >= audio.At + delay) TutorialAudioFinished(id, now);
        }
        foreach (var watch in _watches.ToArray())
        {
            if (!_watches.Contains(watch) || !Due(watch, now)) continue;
            _watches.Remove(watch);
            RunTutorialStep(watch.Callback);
        }
        DirectTutorialBots();
        // Reconstruction (batch 576, owner decision 1 November 2026): a unit whose ExpirationTimer ended and that outlived its 9999 true
        // damage dies then. The opening skirmish's minions do: SpawnStartingMinions gives them TutorialPlayerBuff (proto 0.5 pc 0352-0370),
        // whose BuffOnPreDamage cuts any hit that would kill to CurHealth - 1 (DATA/Spells/TutorialPlayerBuff.luaobj pc 0061-0085), and
        // retail runs BuffOnPreDamage for DAMAGESOURCE_INTERNALRAW too (GuardianAngel, YorickRADelay and WillRevive exempt it themselves), so
        // the shipped scripts alone leave them standing at 1 health. What retail's tutorial did with them is not in the shipped files.
        if (RetireUnit is not { } retire) return;
        foreach (var id in _expired.ToArray())
            if (_world.Get(id) is { Alive: true } unit && !unit.Buffs.Any(buff => buff.Name == "ExpirationTimer"))
            {
                TutorialLog?.Invoke("tutorial.expired-retired", new Dictionary<string, object?>
                {
                    ["unit"] = unit.NetId, ["name"] = unit.Name, ["health"] = unit.Health, ["at"] = now,
                });
                retire(unit);
            }
    }

    /// <summary>
    /// The tutorial bots' scripted fights (<see cref="DirectTutorialBots"/>): from the step that frees the bot (the script's global), it
    /// attacks the target (another global) whenever it is alive (batch 671; until its first death before).
    /// <para>
    /// Batch 664 (owner's hand test, 3 November 2026: "Minions froze after destroying last turret"): Master Yi's fight starts at
    /// EventTurretDeath, the step that frees him (proto 0.74 pc 0013-0022 removes his Stun and SetNoRender), not at EventLookOut. EventTurretDeath
    /// then waits for the player's camera to come within 1200 of him (<c>RegisterOnCameraNearUnitCallback(playerID, botID, 1200,
    /// 'EventLookOut')</c>), and EventLookOut's line is "look out": it makes him mortal and names the champion battle (proto 0.77). Batch
    /// 580 sent him only from EventLookOut, so he stood at his fountain, the camera of a player pushing the lane never found him and the
    /// tutorial stopped there, its Order minions standing at the dampener the script keeps invulnerable and untargetable until
    /// EventGreatWork (proto 0.81). Reconstruction: his own <c>MasterYi_Basic</c> AI does not ship; he comes for the player while still
    /// invulnerable, which EventLookOut lifts.
    /// </para>
    /// </summary>
    private static readonly (string Step, string Bot, string Target)[] TutorialBotFights =
    [
        ("EventOhNo", "allyID", "firstChaosTurret"),
        ("EventTurretDeath", "botID", "playerID"),
    ];

    /// <summary>
    /// Reconstruction (batch 580): the tutorial's own bot AI (<c>MasterYi_Basic</c>, <c>Nunu_Basic</c>, the <c>TutorialManager</c>) does not ship,
    /// so its two bots run the generic <c>Bot.luaobj</c> under the script's own Stun, SetNoRender and invulnerability, and this host does the
    /// two things the lessons need from them that the generic script does not do:
    /// <list type="bullet">
    /// <item>The ally is teleported to <c>singedGetsOwnedPos</c> (EventEnemyDefenses, proto 0.51 pc 0027-0030), 1230 units from the enemy
    /// turret, then freed and made mortal in EventOhNo (proto 0.53 pc 0002-0056), whose lessons wait for it to fall below half health
    /// (EnemyMinionSetup) and die (PostDeathBubble); the position's name says it is owned there. It attacks the script's
    /// <c>firstChaosTurret</c>.</item>
    /// <item>The enemy champion stays at its fountain under Stun and SetNoRender until EventTurretDeath (proto 0.74 pc 0013-0022) frees it;
    /// from then it attacks the player (batch 664), and EventLookOut (proto 0.77), when the player's camera finds it, makes it mortal,
    /// highlights it and waits for its death (EventGreatWork), the "champion battle" objective.</item>
    /// </list>
    /// A freed bot leaves the bot script for good; the order is renewed while it is alive and has none. NOT RETAIL (batch 671, owner decision
    /// 3 November 2026: Master Yi "should respawn"): the fight goes on after a respawn (batch 580 ended it at the bot's first death, and Yi
    /// stood at his fountain from then on); Yi respawns midway (<see cref="ChampionBattleMidway"/>) and comes for the player again. Nunu's
    /// death timer outlasts the tutorial.
    /// </summary>
    private void DirectTutorialBots()
    {
        // The end-of-game ceremony halts every AI; the fights end with it.
        if (TutorialBotAttack is not { } attack || _endOfGame) return;
        foreach (var (step, botName, targetName) in TutorialBotFights)
        {
            if (!TutorialStepRan(step) || GlobalUnit(botName) is not { Alive: true } bot) continue;
            if (bot.AttackTarget is not null || bot.HeroPursuit is not null || bot.Buffs.Any(buff => buff.Name == "Stun") ||
                GlobalUnit(targetName) is not { Alive: true } target ||
                _botOrderAt.TryGetValue(step, out var ordered) && _now < ordered + TutorialBotOrderSeconds)
                continue;
            _botOrderAt[step] = _now;
            TutorialLog?.Invoke("tutorial.bot-order", new Dictionary<string, object?>
            {
                ["unit"] = bot.NetId, ["name"] = bot.Name, ["target"] = target.NetId, ["target_name"] = target.Name, ["at"] = _now,
            });
            attack(bot, target);
        }
    }

    private readonly Dictionary<string, double> _botOrderAt = new(StringComparer.Ordinal);

    /// <summary>Reconstruction: a scripted fight's order is renewed no more than once in this many seconds (it ends when its target is out of sight).</summary>
    private const double TutorialBotOrderSeconds = 2.0;

    private Entity? GlobalUnit(string name) => _state.TryGetGlobal(name, out var value) ? EntityOf([value], 0) : null;

    private bool Due(TutorialWatch watch, double now)
    {
        var player = TutorialPlayerUnit();
        switch (watch.Kind)
        {
            case "UnitNearPosition":
                return watch.Unit is { Alive: true } unit && Within(unit.Position, watch.Point, watch.Value);
            case "UnitNearUnit":
                return watch.Unit is { Alive: true } near && watch.Other is { } other && Within(near.Position, other.Position, watch.Value);
            case "UnitKilled":
                return watch.Unit is { } victim && (!victim.Alive || _world.Get(victim.NetId) is null);
            case "UnitBelowPercentHealth":
                return watch.Unit is { Alive: true, MaxHealth: > 0.0 } hurt && hurt.Health / hurt.MaxHealth < watch.Value;
            case "LastMinionKilled":
            {
                // Reconstruction: "the last minion" of the team is its last living lane or script-spawned fighter; the watch arms once the team
                // has one (the script registers right after spawning the wave) and fires when none is left.
                var alive = _world.Entities.Values.Any(entity => entity.Alive && entity.Team == watch.Team && IsTutorialMinion(entity));
                if (alive) watch.Baseline = 1.0;
                return !alive && watch.Baseline > 0.0;
            }
            case "SpellLevelup":
                return player is not null && (int)watch.Value < player.SpellRanks.Count && player.SpellRanks[(int)watch.Value] > watch.Baseline;
            case "SpellCast":
                return player is not null && (int)watch.Value < player.MatchStatistics.SpellCasts.Count &&
                       player.MatchStatistics.SpellCasts[(int)watch.Value] > watch.Baseline;
            case "SpellAvatarCast":
                return player is not null && (int)watch.Value < player.MatchStatistics.SummonerCasts.Count &&
                       player.MatchStatistics.SummonerCasts[(int)watch.Value] > watch.Baseline;
            case "ItemPurchased":
                return player is not null && player.Inventory.Any(slot => slot is not null && slot.ItemId == (int)watch.Value);
            case "ShopOpened":
                // Reconstruction: an opening at or after the registration counts (the client sends 0x60 each time the shop opens).
                return _shopOpenedAt >= watch.RegisteredAt;
            case "CameraNearUnit":
                return _cameraFocus is { } focus && watch.Other is { Alive: true } seen && Within(focus, seen.Position, watch.Value);
            default:
                return false;
        }
    }

    /// <summary>
    /// DIAGNOSTICS: what the script waits for its player to do, for the headless tool's stand-in player: the point or unit a near-callback
    /// watches (walk there), else the enemy a kill callback waits on (attack it). Null when the wait is not a walk or a kill.
    /// </summary>
    public (Position2 Point, Entity? Target)? TutorialPlayerGoal()
    {
        if (TutorialPlayerUnit() is not { Alive: true } player) return null;
        foreach (var watch in _watches)
        {
            if (watch.Kind == "UnitNearPosition" && ReferenceEquals(watch.Unit, player)) return (watch.Point, null);
            if (watch.Kind == "UnitNearUnit" && ReferenceEquals(watch.Unit, player) && watch.Other is { } other) return (other.Position, null);
        }
        foreach (var watch in _watches)
        {
            var enemy = watch.Kind switch
            {
                "UnitKilled" or "UnitBelowPercentHealth" when watch.Unit is { Alive: true } unit && unit.Team != player.Team => unit,
                "LastMinionKilled" when watch.Team != player.Team => _world.Entities.Values
                    .Where(entity => entity.Alive && entity.Team == watch.Team && IsTutorialMinion(entity))
                    .OrderBy(entity => (entity.Position.X - player.Position.X) * (entity.Position.X - player.Position.X) +
                                       (entity.Position.Z - player.Position.Z) * (entity.Position.Z - player.Position.Z))
                    .FirstOrDefault(),
                _ => null,
            };
            if (enemy is not null) return (enemy.Position, enemy);
        }
        return null;
    }

    /// <summary>DIAGNOSTICS: the spell slot a pending level-up wait watches (the headless stand-in spends a point there), or null.</summary>
    public int? TutorialLevelUpSlot() => TutorialWaitValue("SpellLevelup") is double slot ? (int)slot : null;

    /// <summary>
    /// DIAGNOSTICS (batch 580): the value a pending wait of this kind watches (a spell or summoner slot, an item id), or null, for the
    /// headless stand-in player. The camera and shop waits answer 0 and the unit the camera has to reach is <see cref="TutorialCameraGoal"/>.
    /// </summary>
    public double? TutorialWaitValue(string kind) => _watches.FirstOrDefault(watch => watch.Kind == kind)?.Value;

    /// <summary>
    /// DIAGNOSTICS (batch 593): the open popup and the audio events whose end a callback waits for, each with the moment it opened, so the
    /// headless stand-in can answer them with the client's own C2S 0xD5 and 0x05.
    /// </summary>
    public (double? PopupAt, IReadOnlyList<(uint Id, double At)> Audio) TutorialPendingClientAnswers() =>
        (_popup?.At, [.. _audio.Select(entry => (entry.Key, entry.Value.At))]);

    /// <summary>DIAGNOSTICS (batch 580): the unit a pending camera wait wants in view, or null.</summary>
    public Entity? TutorialCameraGoal() => _watches.FirstOrDefault(watch => watch.Kind == "CameraNearUnit")?.Other;

    /// <summary>DIAGNOSTICS (batch 580): whether a step of that name has run.</summary>
    public bool TutorialStepRan(string step) => _tutorialSteps.Any(entry => entry.Step == step);

    /// <summary>DIAGNOSTICS: the units a pending kill wait still counts (name, net id, position, health, buffs), for the headless report.</summary>
    public IReadOnlyList<string> TutorialWaitUnits() =>
    [
        .. _watches.Where(watch => watch.Kind == "LastMinionKilled")
            .SelectMany(watch => _world.Entities.Values.Where(entity => entity.Alive && entity.Team == watch.Team && IsTutorialMinion(entity)))
            .Concat(_watches.Where(watch => watch.Kind == "UnitKilled" && watch.Unit is { Alive: true }).Select(watch => watch.Unit!))
            .Select(unit => FormattableString.Invariant(
                $"{unit.Name}#{unit.NetId}@({unit.Position.X:0},{unit.Position.Z:0}) hp {unit.Health:0}/{unit.MaxHealth:0} buffs {string.Join("+", unit.Buffs.Select(buff => buff.Name))}")),
    ];

    private static bool Within(Position2 a, Position2 b, double distance) =>
        (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z) <= distance * distance;

    // Reconstruction: a fighter the script has retired is out of the count. KillStartingMinions ends the opening skirmish with
    // ExpirationTimer (DATA/Spells/ExpirationTimer.luaobj: SetNoRender, SetTargetable false, then 9999 true damage on deactivation), but the
    // same minions carry TutorialPlayerBuff from SpawnStartingMinions (proto 0.5 pc 0352-0370), whose BuffOnPreDamage leaves them at 1 health
    // (headless seed 563, batch 576: all sixteen). The lessons treat them as gone, so they are, and AdvanceTutorial ends them when the timer
    // has run out (batch 576).
    private readonly HashSet<uint> _expired = new();

    private bool IsTutorialMinion(Entity entity) =>
        entity.UnitType is UnitType.Minion or UnitType.Monster && !entity.Placemarker && !_expired.Contains(entity.NetId) &&
        !Amberline.Simulation.Pets.SpawnedUnitAi.IsIdle(entity.AiScript);

    private Entity? TutorialPlayerUnit() =>
        TutorialPlayer?.Invoke() ?? _world.Heroes().Where(hero => hero.Team == Team.Order).OrderBy(hero => hero.NetId).FirstOrDefault();

    // Reconstruction: a registration fires once and is gone; the UnRegister calls name only its subject (a unit, a team, a slot or an item).
    private void Watch(TutorialWatch watch) => _watches.Add(watch);

    private void Unwatch(string kind, LuaValue[] args)
    {
        var unit = EntityOf(args, 0);
        var number = Number(args, 0);
        _watches.RemoveAll(watch => watch.Kind == kind && (unit is not null ? ReferenceEquals(watch.Unit, unit)
            : kind == "LastMinionKilled" ? watch.Team == (number == 200.0 ? Team.Chaos : Team.Order)
            : watch.Value == number || kind == "ItemPurchased"));
    }

    private void Unbound(string native, LuaValue[] args, string why)
    {
        TutorialLog?.Invoke("tutorial.native", new Dictionary<string, object?>
        {
            ["native"] = native, ["args"] = string.Join(", ", args.Select(Describe)), ["why"] = why,
        });
    }

    private string Describe(LuaValue value) => value.Type switch
    {
        LuaType.String => value.AsString,
        LuaType.Number => value.ToNumber()?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
        LuaType.Boolean => value.Truthy ? "true" : "false",
        LuaType.Table when value.AsTable.Get(LuaValue.Of("netId")).ToNumber() is double id => $"unit {(uint)id}",
        LuaType.Table => "table",
        _ => "nil",
    };

    private void Engine(TutorialCall call, LuaValue[] args)
    {
        if (TutorialEngine is { } engine) engine(call);
        else Unbound(call.Native, args, "no engine");
    }

    private void RegisterTutorialNatives()
    {
        _state.Register("GetTutorialPlayer", (_, _) => new[] { StructureOf(TutorialPlayerUnit()) });

        // ---- The BuildingBlocks natives behind BBLuaConversionLibrary.luaobj ----------------------------------------------
        // Reconstruction: GetHashedGameObjName keys a
        // unit variable in passThroughParams; the key only has to be what the block's TargetVar/AttackerVar names, so it is the name itself.
        _state.Register("GetHashedGameObjName", (_, args) => new[] { args.Length > 0 ? args[0] : LuaValue.Nil });
        foreach (var block in LibraryBlocks)
            _state.Register(block, (_, args) => BuildingBlock(block, args));

        // ---- Experience and unit flags -------------------------------------------------------------------------------------
        // IncExp(unit, exp): the experience goes through the combat service's award, the same
        // path a camp's share takes (AwardNeutralExperience), so the level-ups apply.
        _state.Register("IncExp", (_, args) =>
        {
            if (EntityOf(args, 0) is { UnitType: UnitType.Hero } hero && Number(args, 1) is > 0.0 and var experience)
                AwardNeutralExperience?.Invoke(hero, experience);
            return Array.Empty<LuaValue>();
        });
        _state.Register("SetCanAutoAcquireTargets", (_, args) =>
        {
            Engine(new TutorialCall("SetCanAutoAcquireTargets", Unit: EntityOf(args, 0), Flag: args.Length > 1 && args[1].Truthy), args);
            return Array.Empty<LuaValue>();
        });

        // ---- Client UI: proven packets go to the engine, the rest are logged ------------------------------------------------
        foreach (var native in new[] { "ShowObjectiveText", "RefreshObjectiveText", "ShowAuxiliaryText", "RefreshAuxiliaryText" })
            _state.Register(native, (_, args) =>
            {
                Engine(new TutorialCall(native, Text: Text(args, 0)), args);
                return Array.Empty<LuaValue>();
            });
        // Batch 580: the TutorialScript's CloseShop (EventEnemyDefenses proto 0.51 pc 0015) closes the shop window its shop lesson opened.
        // Reconstruction: here it is CloseShop and not the end-of-game latch EndOfGame.luaobj's CloseShop stands for (LevelScriptHost),
        // so the player can still shop later in the tutorial; the retail native's server half is not in the shipped files.
        foreach (var native in new[] { "HideObjectiveText", "HideAuxiliaryText", "ToggleFogOfWar", "CloseShop" })
            _state.Register(native, (_, args) =>
            {
                Engine(new TutorialCall(native), args);
                return Array.Empty<LuaValue>();
            });
        // Batch 580: LockCamera(lock) and ResumeGame() name no unit; the engine sends them for the tutorial player.
        _state.Register("LockCamera", (_, args) =>
        {
            Engine(new TutorialCall("LockCamera", Unit: TutorialPlayerUnit(), Flag: args.Length > 0 && args[0].Truthy), args);
            return Array.Empty<LuaValue>();
        });
        _state.Register("ResumeGame", (_, args) =>
        {
            Engine(new TutorialCall("ResumeGame", Unit: TutorialPlayerUnit()), args);
            return Array.Empty<LuaValue>();
        });
        // ToggleUIHighlight(element, on, type, number, subCategory) (EventEachTime proto 0.23 pc 0004-0010) and HighlightHUDElement(type,
        // number) (TutorialPhase1 proto 0.93 pc 0021-0024): the numbers are the packets' bytes.
        _state.Register("ToggleUIHighlight", (_, args) =>
        {
            Engine(new TutorialCall("ToggleUIHighlight", Flag: args.Length > 1 && args[1].Truthy,
                Values: [Number(args, 0), Number(args, 2), Number(args, 3), Number(args, 4)]), args);
            return Array.Empty<LuaValue>();
        });
        _state.Register("HighlightHUDElement", (_, args) =>
        {
            Engine(new TutorialCall("HighlightHUDElement", Values: [Number(args, 0), Number(args, 1)]), args);
            return Array.Empty<LuaValue>();
        });
        _state.Register("OpenTutorialPopup", (_, args) =>
        {
            _popup = (Text(args, 1), _now);
            Engine(new TutorialCall("OpenTutorialPopup", Text: Text(args, 0)), args);
            return Array.Empty<LuaValue>();
        });
        _state.Register("PlayTutorialAudioEvent", (_, args) =>
        {
            var callback = Text(args, 1);
            var id = ++_nextAudioId;
            if (callback.Length > 0) _audio[id] = (callback, _now);
            Engine(new TutorialCall("PlayTutorialAudioEvent", Text: Text(args, 0), Flag: callback.Length > 0, Id: id), args);
            return Array.Empty<LuaValue>();
        });
        _state.Register("MoveCameraToPoint", (_, args) =>
        {
            Engine(new TutorialCall("MoveCameraToPoint", Unit: EntityOf(args, 0), From: PointOf(args.Length > 1 ? args[1] : LuaValue.Nil),
                To: PointOf(args.Length > 2 ? args[2] : LuaValue.Nil), Number: Number(args, 3)), args);
            return Array.Empty<LuaValue>();
        });
        _state.Register("ToggleInputLockingFlag", (_, args) =>
        {
            Engine(new TutorialCall("ToggleInputLockingFlag", Number: Number(args, 0)), args);
            return Array.Empty<LuaValue>();
        });
        _state.Register("MuteVolumeCategory", (_, args) =>
        {
            Engine(new TutorialCall("MuteVolumeCategory", Number: Number(args, 0), Flag: args.Length > 1 && args[1].Truthy), args);
            return Array.Empty<LuaValue>();
        });
        _state.Register("PingMinimapPosition", (_, args) =>
        {
            Engine(new TutorialCall("PingMinimapPosition", Unit: EntityOf(args, 0), To: PointOf(args.Length > 1 ? args[1] : LuaValue.Nil)), args);
            return Array.Empty<LuaValue>();
        });
        _state.Register("PingMinimapUnit", (_, args) =>
        {
            var target = EntityOf(args, 1);
            var at = target?.Position ?? Position2.Zero;
            Engine(new TutorialCall("PingMinimapUnit", Unit: EntityOf(args, 0), Other: target,
                To: new Position3(at.X, _navigation?.HeightAt(at) ?? 0.0, at.Z)), args);
            return Array.Empty<LuaValue>();
        });
        foreach (var native in new[] { "CreateUnitHighlight", "RemoveUnitHighlight" })
            _state.Register(native, (_, args) =>
            {
                Engine(new TutorialCall(native, Unit: EntityOf(args, 0)), args);
                return Array.Empty<LuaValue>();
            });

        // ---- Callbacks ------------------------------------------------------------------------------------------------------
        _state.Register("RegisterOnUnitNearPositionCallback", (_, args) =>
        {
            var point = PointOf(args.Length > 1 ? args[1] : LuaValue.Nil);
            Watch(new TutorialWatch("UnitNearPosition", Text(args, 3))
            {
                Unit = EntityOf(args, 0), Point = new Position2(point.X, point.Z), Value = Number(args, 2), RegisteredAt = _now,
            });
            return Array.Empty<LuaValue>();
        });
        _state.Register("RegisterOnUnitNearUnitCallback", (_, args) =>
        {
            Watch(new TutorialWatch("UnitNearUnit", Text(args, 3))
            {
                Unit = EntityOf(args, 0), Other = EntityOf(args, 1), Value = Number(args, 2), RegisteredAt = _now,
            });
            return Array.Empty<LuaValue>();
        });
        _state.Register("RegisterOnCameraNearUnitCallback", (_, args) =>
        {
            Watch(new TutorialWatch("CameraNearUnit", Text(args, 3))
            {
                Unit = EntityOf(args, 0), Other = EntityOf(args, 1), Value = Number(args, 2), RegisteredAt = _now,
            });
            return Array.Empty<LuaValue>();
        });
        _state.Register("RegisterOnUnitKilledCallback", (_, args) =>
        {
            Watch(new TutorialWatch("UnitKilled", Text(args, 1)) { Unit = EntityOf(args, 0), RegisteredAt = _now });
            return Array.Empty<LuaValue>();
        });
        _state.Register("RegisterOnUnitBelowPercentHealthCallback", (_, args) =>
        {
            Watch(new TutorialWatch("UnitBelowPercentHealth", Text(args, 2)) { Unit = EntityOf(args, 0), Value = Number(args, 1), RegisteredAt = _now });
            return Array.Empty<LuaValue>();
        });
        _state.Register("RegisterOnLastMinionKilledCallback", (_, args) =>
        {
            Watch(new TutorialWatch("LastMinionKilled", Text(args, 1)) { Team = ScriptTeamIdToTeam(Number(args, 0)), RegisteredAt = _now });
            return Array.Empty<LuaValue>();
        });
        _state.Register("RegisterOnShopOpenedCallback", (_, args) =>
        {
            Watch(new TutorialWatch("ShopOpened", Text(args, 0)) { RegisteredAt = _now });
            return Array.Empty<LuaValue>();
        });
        _state.Register("RegisterOnItemPurchasedCallback", (_, args) =>
        {
            Watch(new TutorialWatch("ItemPurchased", Text(args, 1)) { Value = Number(args, 0), RegisteredAt = _now });
            return Array.Empty<LuaValue>();
        });
        // The slot callbacks watch the tutorial player's own counters: the slot's rank, its casts, its summoner casts
        // (Entity.SpellRanks, HeroMatchStatistics.SpellCasts and SummonerCasts). Reconstruction: the native's subject is the player.
        foreach (var (native, kind) in new[]
                 {
                     ("RegisterOnSpellLevelupCallback", "SpellLevelup"), ("RegisterOnSpellCastCallback", "SpellCast"),
                     ("RegisterOnSpellAvatarCastCallback", "SpellAvatarCast"),
                 })
            _state.Register(native, (_, args) =>
            {
                var slot = (int)Number(args, 0);
                var player = TutorialPlayerUnit();
                double Counter(IReadOnlyList<int> values) => slot >= 0 && slot < values.Count ? values[slot] : 0;
                var baseline = player is null ? 0.0 : kind switch
                {
                    "SpellLevelup" => Counter(player.SpellRanks),
                    "SpellCast" => Counter(player.MatchStatistics.SpellCasts),
                    _ => Counter(player.MatchStatistics.SummonerCasts),
                };
                Watch(new TutorialWatch(kind, Text(args, 1)) { Value = slot, Baseline = baseline, RegisteredAt = _now });
                return Array.Empty<LuaValue>();
            });
        foreach (var (native, kind) in new[]
                 {
                     ("UnRegisterOnUnitKilledCallback", "UnitKilled"), ("UnRegisterOnUnitBelowPercentHealthCallback", "UnitBelowPercentHealth"),
                     ("UnRegisterOnLastMinionKilledCallback", "LastMinionKilled"), ("UnRegisterOnItemPurchasedCallback", "ItemPurchased"),
                     ("UnRegisterOnSpellLevelupCallback", "SpellLevelup"), ("UnRegisterOnSpellCastCallback", "SpellCast"),
                     ("UnRegisterOnSpellAvatarCastCallback", "SpellAvatarCast"),
                 })
            _state.Register(native, (_, args) =>
            {
                Unwatch(kind, args);
                return Array.Empty<LuaValue>();
            });
    }

    /// <summary>
    /// One BBLuaConversionLibrary wrapper's block: <c>BBxxx(passThroughParams, perBlockParams)</c>. The two Lua tables become the block's
    /// scope and parameters (units are the host's entities, points and tables the BuildingBlocks tables), the block runs, and what it wrote
    /// into its scope (<c>DestObj</c>, <c>BubbleIDVar</c>) is copied back for the wrapper to return. A block that raises is logged and
    /// skipped, so one unsupported native does not end the lesson it is part of.
    /// </summary>
    private LuaValue[] BuildingBlock(string function, LuaValue[] args)
    {
        if (RunBuildingBlock is not { } run)
        {
            Unbound(function, args, "no BuildingBlocks host");
            return Array.Empty<LuaValue>();
        }
        var vars = args.Length > 0 && args[0].Type == LuaType.Table ? args[0].AsTable : new LuaTable();
        var scope = ToBb(vars);
        var parameters = args.Length > 1 && args[1].Type == LuaType.Table ? ToBb(args[1].AsTable) : new BbTable();
        // Batch 580: BBCreateItem (UnitVar, ItemID) is the engine's item grant (GrantItem), not a BuildingBlocks native of the unit module.
        if (function == "BBCreateItem")
        {
            var refusal = GrantItem is { } grant && parameters["UnitVar"] is string unitVar && scope[unitVar] is Entity holder &&
                parameters["ItemID"] is double item ? grant(holder, (int)item) : "no item grant";
            if (refusal is not null) Unbound(function, args, refusal);
            return Array.Empty<LuaValue>();
        }
        // Reconstruction: a spawn names no owner; the map's own building of the spawn's team stands in, so the unit is credited to no hero.
        var owner = function == "BBSpawnMinion" && parameters["Team"] is double team && team is 100.0 or 200.0
            ? _world.Entities.Values.FirstOrDefault(entity => entity.Kind == EntityKind.Nexus && entity.Team == ScriptTeamIdToTeam(team))
            : TutorialPlayerUnit();
        if (function == "BBSpellBuffAdd" && parameters["BuffName"] is "ExpirationTimer" && scope["Target"] is Entity expiring)
            _expired.Add(expiring.NetId);
        // Reconstruction (batch 580): the shipped TutorialSpellBuffRemove names its target TargetBar (BBLuaConversionLibrary.luaobj proto 0.10
        // pc 0011; no other shipped script has the key), and the client's unit parameter proxy reads only "%sVar", so a missing TargetVar is
        // "Target was required but not found!". Every Stun, SetNoRender and JudicatorIntervention the lessons lift
        // goes through it (EventEnemyDefenses, EventOhNo, EventTurretDeath), so the key the block reads is passed on; the retail server's
        // reading of the misspelling is not in the shipped files.
        if (function == "BBSpellBuffRemove" && parameters["TargetVar"] is null && parameters["TargetBar"] is string misspelled)
            parameters["TargetVar"] = misspelled;
        // NOT RETAIL (batch 671): EventTurretDeath's wave spawns midway (ChampionBattleMidway).
        if (function == "BBSpawnMinion" && _waveShift is { } shift && parameters["PosVar"] is string shiftedVar &&
            scope[shiftedVar] is BbTable shipped && shipped["x"] is double shippedX && shipped["z"] is double shippedZ)
        {
            var moved = new Position2(shippedX + shift.X, shippedZ + shift.Z);
            scope[shiftedVar] = BbTable.Point(moved.X, _navigation?.HeightAt(moved) ?? shipped["y"] as double? ?? 0.0, moved.Z);
        }
        // Reconstruction (batch 576): a unit the script spawns on a blocked cell starts on the nearest floor cell, the search batch 556 gives a
        // hero's spawn slot (four cells at most). SpawnStartingMinions puts a Red_Minion_Basic at (8133, 8059) (proto 0.5 pc 0202-0218), 77
        // units from Turret_T2_L_01 and inside its footprint, where it stood for the whole skirmish (batch 563: 233 blocked-cell lines). The
        // client puts an actor standing on a blocked cell back on floor at its next stop; the server's placement is not shipped.
        if (function == "BBSpawnMinion" && parameters["PosVar"] is string posVar && scope[posVar] is BbTable spawnAt &&
            spawnAt["x"] is double x && spawnAt["z"] is double z &&
            _navigation?.NearestMovementFloor(new Position2(x, z), maxCells: 4) is { } floor && floor != new Position2(x, z))
            scope[posVar] = BbTable.Point(floor.X, spawnAt["y"] as double? ?? 0.0, floor.Z);
        try
        {
            run(function, parameters, scope, owner);
        }
        catch (Exception error) when (error is BbRuntimeException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            Unbound(function, args, error.Message);
        }
        // Reconstruction (batch 593, owner's native game 1 November 2026: "minions just standing there" in the opening scroll): a unit the
        // script spawns with minion.lua walks its lane. SpawnStartingMinions puts each side's minions on the centre lane, the Chaos ones by
        // the Chaos turret and the Order ones by the Order base 6000 units away (proto 0.5), EventInLoL pans the camera along it for 18 s
        // (proto 0.9) and EventForThisBattle ends them; EnemyMinionSetup's wave is the one EventMinionAttack waits to see attack. Minion.lua
        // moves only by its forward navigation (TimerMoveForward, Minion.txt proto 0.4), which a unit without a barracks lane answers by
        // standing (SpawnedUnitLuaAiEngine); how retail's engine laned a scripted minion is not in the shipped files.
        if (function == "BBSpawnMinion" && ScriptMinionRoute is { } laneOf && parameters["AiScript"] is string ai &&
            Amberline.Simulation.Pets.SpawnedUnitAi.Canonical(ai) == Amberline.Simulation.Pets.SpawnedUnitAi.Minion &&
            parameters["DestVar"] is string spawnedVar && scope[spawnedVar] is Entity spawned)
            spawned.ScriptLaneRoute = laneOf(spawned);
        foreach (var (key, value) in scope.Entries)
            if (key is string name) vars.Set(LuaValue.Of(name), FromBb(value));
        return Array.Empty<LuaValue>();
    }

    private BbTable ToBb(LuaTable table)
    {
        var result = new BbTable();
        foreach (var (key, value) in table.Entries)
        {
            object? bbKey = key.Type switch { LuaType.String => key.AsString, LuaType.Number => key.ToNumber(), _ => null };
            if (bbKey is null) continue;
            result[bbKey] = ToBb(value);
        }
        return result;
    }

    private object? ToBb(LuaValue value) => value.Type switch
    {
        LuaType.Number => value.ToNumber(),
        LuaType.String => value.AsString,
        LuaType.Boolean => value.Truthy,
        LuaType.Table when value.AsTable.Get(LuaValue.Of("netId")).ToNumber() is double id =>
            _world.Get((uint)id) ?? (_handledStructures.TryGetValue((uint)id, out var handled) ? handled : null),
        LuaType.Table when value.AsTable.ContainsKey(LuaValue.Of("x")) && value.AsTable.ContainsKey(LuaValue.Of("z")) =>
            BbTable.Point(Number(value.AsTable, "x"), Number(value.AsTable, "y"), Number(value.AsTable, "z")),
        LuaType.Table => ToBb(value.AsTable),
        _ => null,
    };

    private LuaValue FromBb(object? value) => value switch
    {
        null => LuaValue.Nil,
        double number => LuaValue.Of(number),
        string text => LuaValue.Of(text),
        bool flag => LuaValue.Of(flag),
        Entity unit => StructureOf(unit),
        BbTable table => FromBbTable(table),
        _ => LuaValue.Nil,
    };

    private LuaValue FromBbTable(BbTable table)
    {
        var result = new LuaTable();
        foreach (var (key, value) in table.Entries)
        {
            var luaKey = key switch { string text => LuaValue.Of(text), double number => LuaValue.Of(number), _ => LuaValue.Nil };
            if (luaKey.Type != LuaType.Nil) result.Set(luaKey, FromBb(value));
        }
        return LuaValue.Of(result);
    }
}
