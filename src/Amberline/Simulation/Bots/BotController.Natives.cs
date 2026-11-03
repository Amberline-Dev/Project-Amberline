using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.Lua;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Pets;
using LuaValue = Amberline.Scripting.Lua.LuaValue;

namespace Amberline.Simulation.Bots;

// The native-answer half of a bot's shipped AI. Bot.luaobj and the eight Task*.luaobj modules it
// loads decide what a bot does (which task is active, where it walks, what it swings at, when it
// shops), and every question they ask is answered here from the services this controller already
// owns. The movement, combat and cast plumbing below these answers is the engine's, never the
// script's: a native order is the same order a player's click produces, throttles and all.
public sealed partial class BotController
{
    /// <summary>The match clock the scripts read through <c>GetTime</c> and <c>os.time</c>.</summary>
    internal double Now => _combat.CurrentTime;

    /// <summary>The script's world half: <see cref="BotLuaAiEngine"/>, one instance per match.</summary>
    internal BotLuaAiEngine Engine { get; }

    /// <summary>One script instance per bot seat, driven from <see cref="Tick"/>.</summary>
    internal LuaAiService Host { get; }

    /// <summary>The seat's script state, looked up from the hero the natives are called for.</summary>
    internal BotState? StateOf(Entity hero) => _stateByHero.GetValueOrDefault(hero.NetId);

    // ---- the script's own step ----

    /// <summary>
    /// Runs the seat's shipped AI: its own <c>Bot.luaobj</c> instance, its own timers. The script
    /// arms <c>TimerHackDelayedInit</c> one second after <c>OnInit</c> and <c>TimerUpdate</c>
    /// every 250 ms from then on, so the cadence is the script's and the engine only raises the
    /// events it has no timer for (a level-up, a lost target) and reads back what it decided.
    /// </summary>
    internal void StepScript(Entity hero, BotState state, double now, double dt)
    {
        var instance = Host.InstanceFor(hero);
        if (instance is null) return;
        DispatchOnLevelUp(hero, state, instance);
        Host.StepUnit(hero, now, dt);
        if (Quests is not null) InstallDominionTask(instance, hero, state);
        MirrorTasks(instance, state);
    }

    /// <summary>
    /// <c>OnLevelUp</c>, once per level or skill-point change. Bot.luaobj's handler is
    /// <c>UpgradeSpellsInOrder(4, 2, 3, 1)</c>; the retry key is the same one the old level-up
    /// pass used, so a point a level gate refuses is tried again at the next level.
    /// </summary>
    private void DispatchOnLevelUp(Entity hero, BotState state, LuaAiInstance instance)
    {
        var key = hero.Level * 64 + hero.SkillPoints;
        if (hero.SkillPoints <= 0 || key == state.SkillKey) return;
        instance.Call("OnLevelUp");
        state.SkillKey = hero.Level * 64 + hero.SkillPoints;
    }

    /// <summary>
    /// Reads the script's decision back into the seat's state, so the measures and the checks see
    /// the tasks the script really holds (Bot.luaobj's <c>tasks</c> and <c>activeTask</c>) rather
    /// than a decision this controller no longer makes. The task names are the labels the script
    /// builds in <c>TimerHackDelayedInit</c>.
    /// </summary>
    private static void MirrorTasks(LuaAiInstance instance, BotState state)
    {
        var previous = state.Active;
        state.Tasks.Clear();
        state.Active = null;
        var active = instance.GetGlobal("activeTask");
        var activeTable = active.Type == LuaType.Table ? active.AsTable : null;
        var tasks = instance.GetGlobal("tasks");
        if (tasks.Type == LuaType.Table)
        {
            for (var index = 1; ; index++)
            {
                var entry = tasks.AsTable.Get(LuaValue.Of((double)index));
                if (entry.Type != LuaType.Table) break;
                if (MirrorTask(entry.AsTable) is not { } task) continue;
                state.Tasks.Add(task);
                if (ReferenceEquals(entry.AsTable, activeTable)) state.Active = task;
            }
        }

        // The tasks are re-read every step, so a switch is a change of which task is active
        // rather than a change of object identity.
        if (!SameTask(previous, state.Active)) state.Measure.TaskSwitches++;

        // The anti-kite latch is TaskKillHero's own module global (TaskKillHero.txt proto 0.0
        // SETGLOBAL 'ReducePriority', set and cleared only by that module's AntiKiteTimer, proto
        // 0.3). TimerHackDelayedInit loads the module once and setmetatable's every kill-hero task
        // table onto it (Bot.txt 0153), so the one global is shared by every kill-hero task of the
        // bot and outlives any one of them being active. It is therefore read off whichever task
        // table carries that module, not only the active one: no other task module defines the
        // name, so the search stops at the first one that does.
        var latched = false;
        if (tasks.Type == LuaType.Table)
        {
            for (var index = 1; ; index++)
            {
                var entry = tasks.AsTable.Get(LuaValue.Of((double)index));
                if (entry.Type != LuaType.Table) break;
                if (entry.AsTable.Metatable is not { } module) continue;
                var latch = module.Get(LuaValue.Of("ReducePriority"));
                if (latch.Type == LuaType.Nil) continue;
                latched = latch.Truthy;
                break;
            }
        }
        if (latched && !state.ReducePriority) state.Measure.AntiKiteLatches++;
        state.ReducePriority = latched;
    }

    private static bool SameTask(BotTask? left, BotTask? right) =>
        left is null ? right is null
            : right is not null && left.Kind == right.Kind && left.Lane == right.Lane &&
                left.SubjectNetId == right.SubjectNetId;

    /// <summary>One entry of the script's <c>tasks</c> table, or null for a name no shipped task uses.</summary>
    private static BotTask? MirrorTask(LuaTable entry)
    {
        var name = entry.Get(LuaValue.Of("Name"));
        var label = name.Type == LuaType.String ? name.AsString : string.Empty;
        var priority = entry.Get(LuaValue.Of("Priority")).ToNumber() ?? 0.0;
        var done = entry.Get(LuaValue.Of("Done")).Truthy;
        BotTask? task = label switch
        {
            "CastSpell" => new BotTask(BotTaskKind.CastSpell),
            "Retreat" => new BotTask(BotTaskKind.Retreat),
            "KillLowHPMinion" => new BotTask(BotTaskKind.KillMinion),
            "KillNearbyStruct" => new BotTask(BotTaskKind.KillTower),
            "Buy Item" => new BotTask(BotTaskKind.Buy),
            DominionTaskName => new BotTask(BotTaskKind.Capture),
            _ when label.StartsWith("Push Lane ", StringComparison.Ordinal) &&
                int.TryParse(label["Push Lane ".Length..], out var lane) =>
                new BotTask(BotTaskKind.PushLane, lane),
            _ when label.StartsWith("Defend ", StringComparison.Ordinal) =>
                new BotTask(BotTaskKind.Defend, subjectNetId: HandleOf(entry, "StructureID")),
            _ when label.StartsWith("Kill Hero ", StringComparison.Ordinal) =>
                new BotTask(BotTaskKind.KillHero, subjectNetId: HandleOf(entry, "HeroID")),
            _ => null,
        };
        if (task is null) return null;
        task.Priority = priority;
        task.Done = done;
        return task;
    }

    /// <summary>
    /// A task's subject, the net id the scripts keep for a structure or a hero. The two tasks do
    /// not store the same shape: <c>GetStructures</c> hands out handles, so
    /// <c>SETTABLE ... 'StructureID'</c> stores a plain number (Bot.txt 0106); <c>GetHeroes</c>
    /// hands out objects, so <c>SETTABLE ... 'HeroID'</c> stores the object table and its
    /// <c>netId</c> field is the handle (Bot.txt 0146).
    /// </summary>
    private static uint HandleOf(LuaTable entry, string field)
    {
        var value = entry.Get(LuaValue.Of(field));
        if (value.Type == LuaType.Table)
            return (uint)(value.AsTable.Get(LuaValue.Of("netId")).ToNumber() ?? 0.0);
        return (uint)(value.ToNumber() ?? 0.0);
    }

    // ---- orders ----

    /// <summary>
    /// <c>SetStateAndCloseToTarget(state, target)</c>: take the state and close on the target. The
    /// state is the task module's own id and is written here, in the same step as the attack
    /// (the shape the ownership family already has), so <c>GetState</c> answers it afterwards.
    /// A disabled hero takes no order (see <see cref="IsActionLocked"/>).
    /// </summary>
    internal void NativeSetStateAndCloseToTarget(Entity hero, double state, Entity? target)
    {
        if (StateOf(hero) is not { } bot) return;
        bot.HeldMove = null;
        if (IsActionLocked(hero)) return;
        bot.ScriptState = state;
        if (target is not { Alive: true } victim) return;
        EnsureAttack(hero, bot, victim, Now);
    }

    /// <summary>
    /// <c>SetStateAndMove(state, point)</c>: take the state and walk to the point.
    /// Reconstruction (batch 581): a walk ordered while the hero cannot take it (<see cref="IsActionLocked"/>, or a root, a
    /// displacement or a windup that <see cref="IssueMove"/> waits out) is held and issued once the hero can move, unless a later
    /// order native replaces it (<see cref="ReleaseHeldMove"/>). TaskRetreat orders its walk once, in BeginTask, and its Tick is
    /// empty (TaskRetreat.txt proto 0.1, 0.2), so the shipped task only works if the order outlives what held the unit: dropped,
    /// it left a bot that was stunned as its retreat began standing at low health with Retreat active (Ezreal 24 s after
    /// OrianaStun in seed 9801; 28 to 93 s of such standing per 25-minute match). The other tasks re-order on every Tick.
    /// </summary>
    internal void NativeSetStateAndMove(Entity hero, double state, Position2 point)
    {
        if (StateOf(hero) is not { } bot) return;
        bot.HeldMove = null;
        var locked = IsActionLocked(hero);
        if (!locked) bot.ScriptState = state;
        if (locked || MoveHeldBack(hero))
        {
            bot.HeldMove = (state, point, NativeActiveTaskName(hero, null));
            return;
        }
        IssueMove(hero, bot, point, Now);
    }

    /// <summary>
    /// Issues the walk <see cref="NativeSetStateAndMove"/> held, once nothing holds the hero any more and the task that ordered
    /// it is still the active one (a new task's own BeginTask orders for it).
    /// </summary>
    private void ReleaseHeldMove(Entity hero, BotState bot)
    {
        if (bot.HeldMove is not { } held || IsActionLocked(hero) || MoveHeldBack(hero)) return;
        if (held.Task != NativeActiveTaskName(hero, null)) { bot.HeldMove = null; return; }
        NativeSetStateAndMove(hero, held.State, held.Point);
    }

    /// <summary><c>StopMove()</c>: drop the current route without touching the attack.</summary>
    internal void NativeStopMove(Entity hero)
    {
        if (StateOf(hero) is not { } bot) return;
        bot.HeldMove = null;
        StopMove(hero, bot);
    }

    /// <summary><c>IsMoving()</c>.</summary>
    internal bool NativeIsMoving(Entity hero) => IsMoving(hero);

    /// <summary><c>BuyItem(itemId)</c>: try the purchase; the scripts ignore the result.</summary>
    internal void NativeBuyItem(Entity hero, int itemId)
    {
        if (StateOf(hero) is not { } bot) return;
        BuyItem(hero, bot, itemId);
    }

    /// <summary>
    /// <c>ServerCastSpell(slot)</c>: cast a champion slot the way a player's cast order does,
    /// at the target the AI is holding (at the bot itself when the spell only admits allies).
    /// The slot's aim is the engine's: the <c>TaskCastSpell</c> template names the slot and
    /// nothing else, so a draw that has no sensible aim comes to nothing here. A retail bot
    /// without its own template aims from the spell data (<see cref="AimBySpellData"/>, NOT RETAIL
    /// owner decision 18 October 2026); every other champion aims plainly at the held target.
    /// </summary>
    internal bool NativeServerCastSpell(Entity hero, double slot)
    {
        if (IsActionLocked(hero) || StateOf(hero) is not { } bot) return false;
        var index = (int)Math.Round(slot);
        if (index < 0 || index >= BotScript.CastSlotCount) return false;
        var target = CurrentTarget(hero) ?? hero;
        var ordered = CastsBySpellData(hero.ChampionName)
            ? AimBySpellData(hero, index, target) is { } request && CastAimed(hero, request, Now)
            : ServerCastSpell(hero, target, index, Now);
        if (!ordered)
        {
            SampleSuperseded(hero, bot);
            bot.Measure.CastRefused[index]++;
            return false;
        }
        CastOrdered(hero, bot, index);
        return true;
    }

    /// <summary>
    /// <c>UpgradeSpellsInOrder(4, 2, 3, 1)</c>: spend the hero's skill points in the order the
    /// arguments name. The helper's index conversion is unrecovered and the four arguments are
    /// read as one-based R, W, E, Q positions, so slot = argument - 1
    /// (<c>Reconstruction:</c>) — the same reading the old C# port took of Bot.luaobj's literal,
    /// now that the script supplies the order itself.
    /// </summary>
    internal void NativeUpgradeSpellsInOrder(Entity hero, IReadOnlyList<double> order)
    {
        var spent = true;
        while (spent && hero.SkillPoints > 0)
        {
            spent = false;
            foreach (var argument in order)
            {
                var slot = (int)Math.Round(argument) - 1;
                if (slot < 0 || slot >= BotScript.CastSlotCount) continue;
                if (!_spells.CanRankUp(hero, slot).Allowed) continue;
                spent = _spells.RankUp(hero, slot);
                break;
            }
        }
    }

    /// <summary>
    /// <c>SetDebugSlotText(slot, text, colour)</c>: one line of the bot's debug overlay. The
    /// overlay has no server half, so the line is taken and dropped (as the research record has
    /// it: not sent). The task names the scripts print are their own, so nothing is lost.
    /// </summary>
    internal void NativeSetDebugSlotText(Entity hero, double slot, string text, double colour)
    {
    }

    /// <summary><c>Say(text)</c>: the bot's chat bubble. Not sent, like the debug overlay.</summary>
    internal void NativeSay(Entity hero, string text)
    {
    }

    // ---- the unit ----

    /// <summary><c>GetAttackRange()</c>.</summary>
    internal double NativeAttackRange(Entity hero) => hero.AttackRange;

    /// <summary><c>GetMP()</c>.</summary>
    /// NOT RETAIL (owner decision, 22 October 2026): a hero with no mana pool answers 1.0 here and
    /// in <see cref="NativeMaxMana"/>, so TaskCastSpell's <c>GetMP() / GetMaxMP()</c>
    /// (TaskCastSpell.txt proto 0.0, 0008-0010) reads 1.0 rather than the shipped 0/0 NaN and
    /// its cast task still wins. Retail: the NaN makes the priority NaN and the task never starts (the script's arithmetic; retail's GetMP answer is native and not recovered),
    /// which leaves a no-pool champion (Garen, Katarina) a bot that never casts — the opposite of
    /// "every roster champion is a playable bot". No retail bot lacks a mana pool: the case arises only for Garen, Katarina and
    /// Riven, none of the 18 retail bots.
    /// NOT RETAIL (batch 341, the same owner decision): a Shield pool answers 1.0 too. Mordekaiser's Iron Man pool is empty at the spawn and
    /// fills only from his spells' hits, so GetMP() / GetMaxMP() read 0 and his bot never cast (0 casts in a 15-minute match). He is not a
    /// retail bot; no retail bot has a Shield pool.
    internal double NativeMana(Entity hero) => ReadsPool(hero) ? hero.Mana : 1.0;

    /// <summary><c>GetMaxMP()</c>. See <see cref="NativeMana"/> for the no-pool arm.</summary>
    internal double NativeMaxMana(Entity hero) => ReadsPool(hero) ? hero.MaxMana : 1.0;

    private bool ReadsPool(Entity hero) =>
        hero.MaxMana > 0.0 && _data.Champion(hero.StatCharacterName).PrimaryResource != PrimaryResourceType.Shield;

    /// <summary><c>GetGold()</c>.</summary>
    internal double NativeGold(Entity hero) => hero.Gold;

    /// <summary><c>GetNumInvSlotsAvailable()</c>.</summary>
    internal double NativeFreeInventorySlots(Entity hero) => FreeInventorySlots(hero);

    /// <summary><c>GetItemPrice(itemId)</c>: what the shop would charge this inventory right now.</summary>
    internal double NativeItemPrice(Entity hero, int itemId) =>
        TryItemPrice(hero, itemId, out var price) ? price : 0.0;

    /// <summary>
    /// <c>CanUseSpell(slot)</c>: 0 when the slot can be cast now. This is the spellbook's can-use check,
    /// which has no range or target test. The non-zero verdict is the client's own answer (see <see cref="CanUseSpellCode"/>).
    /// The shipped scripts test only for 0. Each call is one draw of the
    /// template's <c>math.random(0, 3)</c>, so the draw counters are kept here.
    /// </summary>
    internal double NativeCanUseSpell(Entity hero, double slot)
    {
        if (StateOf(hero) is not { } bot) return 1.0;
        var index = (int)Math.Round(slot);
        if (index < 0 || index >= BotScript.CastSlotCount) return 1.0;
        bot.Measure.CastDraws[index]++;
        var usable = _spells.CanUseSpell(hero, index, Now);
        if (usable.Allowed)
        {
            bot.Measure.CastUsable[index]++;
            return 0.0;
        }
        // A Disabled verdict names its cause (sealed slot, silence, a script's CanCast ...); the
        // other verdicts are self-explaining.
        var gate = usable.Failure == SpellCastFailure.Disabled
            ? $"{"QWER"[index]}:Disabled:{usable.Reason}" : $"{"QWER"[index]}:{usable.Failure}";
        bot.Measure.CastGate[gate] = bot.Measure.CastGate.GetValueOrDefault(gate) + 1;
        return CanUseSpellCode(usable.Failure);
    }

    /// <summary>
    /// The spellbook's can-use refusal code (research patch126-bot-scripts.md 80-88): 1 no spell data, 2 not learned, 4 cooldown,
    /// 5 mana, and 3 for the cast bits and the owner's OnCanUseSpell (dead, control states, a sealed slot).
    /// </summary>
    private static double CanUseSpellCode(SpellCastFailure failure) => failure switch
    {
        SpellCastFailure.Slot => 1.0,
        SpellCastFailure.Rank => 2.0,
        SpellCastFailure.Cooldown => 4.0,
        SpellCastFailure.Mana => 5.0,
        _ => 3.0,
    };

    // ---- the world ----

    /// <summary>
    /// <c>GetPos([object])</c>: the hero's own position, or that unit's. A unit that has already
    /// left the world has no position and reads as the map origin.
    /// </summary>
    internal Position2 NativePositionOf(Entity hero, Entity? other) =>
        other is null ? hero.Position : other.Position;

    /// <summary><c>GetHP([object])</c>.</summary>
    internal double NativeHealth(Entity hero, Entity? other) => (other ?? hero).Health;

    /// <summary><c>GetMaxHP([object])</c>.</summary>
    internal double NativeMaxHealth(Entity hero, Entity? other) => (other ?? hero).MaxHealth;

    /// <summary><c>IsDead(object)</c>: the object's unit is gone or not alive.</summary>
    internal bool NativeIsDead(Entity hero, Entity? other) => other is null || !other.Alive;

    /// <summary>
    /// <c>GetLastTookDamageTime(object)</c>: the match time of its last damage. A unit that never
    /// took damage answers negative infinity, which is never inside a script's recent window.
    /// </summary>
    internal double NativeLastTookDamageTime(Entity hero, Entity? other) =>
        (other ?? hero).LastDamageAt;

    /// <summary>
    /// <c>GetMinions(team, lane)</c>: the side's alive minions, filtered to one lane
    /// (<c>-1</c> is every lane). A corpse is not in the list: the scripts pick their next target
    /// out of it and ask <c>IsDead</c> themselves. Jungle camps are on no side at all, so the
    /// team already keeps them out — TaskKillMinion's <c>FindNearLowestHPMinion</c> takes
    /// <c>GetMinions(GetOtherTeam(), -1)</c> and expects every enemy body a minion target can be
    /// (the wave's <see cref="MinionType"/> marks a scheduled lane minion only, which is what
    /// the old C# scan needed when it looked for a minion to follow).
    /// </summary>
    internal IReadOnlyList<Entity> NativeMinionsOf(Entity hero, double team, double lane)
    {
        if (!TeamMapping.TryFromNative((int)team, out var side)) return [];
        var laneIndex = (int)Math.Round(lane);
        var minions = new List<Entity>();
        foreach (var other in _world.Entities.Values)
        {
            if (!other.Alive || other.Team != side || other.Kind != EntityKind.Minion) continue;
            if (laneIndex >= 0 && LaneIndex(other.MinionLane) != laneIndex) continue;
            minions.Add(other);
        }
        return minions;
    }

    /// <summary>
    /// <c>GetHeroes(team)</c>: the side's own heroes, summons excluded (a bot's kill-hero task is
    /// built from this list once, so a hero that is currently dead stays in it and the scripts
    /// ask <c>IsDead</c>).
    /// </summary>
    internal IReadOnlyList<Entity> NativeHeroesOf(Entity hero, double team)
    {
        if (!TeamMapping.TryFromNative((int)team, out var side)) return [];
        var heroes = new List<Entity>();
        foreach (var other in _world.Entities.Values)
        {
            if (other.UnitType != UnitType.Hero || other.Team != side || other.OwnerNetId != 0)
                continue;
            heroes.Add(other);
        }
        return heroes;
    }

    /// <summary>
    /// <c>GetStructures(team)</c>: the side's structures as <b>handles</b> (net ids), the one
    /// list the scripts do not use as objects — every entry goes through <c>GetObject</c>. A
    /// structure that has fallen is gone from the list.
    /// </summary>
    internal IReadOnlyList<uint> NativeStructuresOf(Entity hero, double team)
    {
        if (!TeamMapping.TryFromNative((int)team, out var side)) return [];
        var structures = new List<uint>();
        foreach (var other in _world.Entities.Values)
        {
            if (!other.Alive || other.Team != side || other.MaxHealth <= 0.0 || !IsStructure(other))
                continue;
            structures.Add(other.NetId);
        }
        return structures;
    }

    /// <summary><c>GetObject(handle)</c>: the object behind a handle.</summary>
    internal Entity? NativeByNetId(uint netId) => _world.Get(netId);

    /// <summary>
    /// <c>objGetName(object)</c>: the name the bot prints for it. The answer is the unit's own
    /// type name (a champion's champion name, a spawned unit's name), never a seat's — the task
    /// names it builds go into the debug overlay.
    /// </summary>
    internal string NativeNameOf(Entity? unit) =>
        unit is null ? string.Empty
            : unit.UnitType == UnitType.Hero ? unit.ChampionName
            : unit.Name.Length > 0 ? unit.Name : unit.EntityType;

    /// <summary><c>GetTeam()</c>, as the host's <c>TEAM_*</c> id.</summary>
    internal double NativeTeamId(Entity hero) => TeamMapping.ToNative(hero.Team);

    /// <summary><c>GetOtherTeam()</c>.</summary>
    internal double NativeOtherTeamId(Entity hero) =>
        TeamMapping.ToNative(hero.Team == Team.Order ? Team.Chaos : Team.Order);

    // ---- the bot's own book ----

    /// <summary><c>GetRegroupPos()</c>: where healing and the shop coincide — the team's fountain.</summary>
    internal Position2 NativeRegroupPos(Entity hero) => Fountain(hero.Team);

    /// <summary><c>GetMaxTravelDistSquared()</c>: the scale of the tasks' distance terms.</summary>
    internal double NativeMaxTravelDistSquared(Entity hero) => MaxTravelDistanceSquared;

    /// <summary>
    /// <c>GetMinionDistanceToLane(object)</c>: the minion's distance to its own lane's route
    /// polyline. A minion with no lane answers 0, which reads as "on the lane".
    /// </summary>
    internal double NativeMinionDistanceToLane(Entity hero, Entity minion) =>
        minion.MinionLane is null
            ? 0.0
            : MinionDistanceToLane(minion, _waves.LaneRoute(minion.Team, minion.MinionLane));

    /// <summary>
    /// <c>GetActiveTaskName([hero])</c>: the <c>Name</c> field of the active task, its own or
    /// another bot's — TaskPushLane compares the answer against a task instance's own name to
    /// count how many teammates are pushing the same lane. A seat with no active task answers
    /// empty, and a teammate the scripts do not drive (a human) has none at all.
    /// </summary>
    internal string NativeActiveTaskName(Entity hero, Entity? other)
    {
        var instance = Host.ExistingInstanceFor(other ?? hero);
        if (instance is null) return string.Empty;
        var active = instance.GetGlobal("activeTask");
        if (active.Type != LuaType.Table) return string.Empty;
        var name = active.AsTable.Get(LuaValue.Of("Name"));
        return name.Type == LuaType.String ? name.AsString : string.Empty;
    }

    /// <summary><c>GetAreAllBarracksStarted(team)</c>.</summary>
    internal bool NativeAreAllBarracksStarted(Entity hero, double team) =>
        TeamMapping.TryFromNative((int)team, out var side) && AreAllBarracksStarted(side);

    /// <summary><c>GetTargetHealth()</c>: the held target's health, 0 without one.</summary>
    internal double NativeTargetHealth(Entity hero) => CurrentTargetHealth(hero);

    // ---- the shared half the bot scripts name ----

    /// <summary>
    /// <c>FindTargetInAcR</c>: no bot script names it. A bot's task list chooses its targets, so
    /// the shared acquisition ladder has no bot equivalent (<c>Reconstruction:</c>) and the
    /// nearest visible enemy within attack range is the same answer a hero's own idle
    /// acquisition gives.
    /// </summary>
    internal Entity? NativeFindTarget(Entity hero)
    {
        Entity? best = null;
        var bestSquared = hero.AttackRange * hero.AttackRange;
        foreach (var other in _world.Entities.Values)
        {
            if (other.NetId == hero.NetId || other.Team == hero.Team || other.Team == Team.Neutral ||
                !other.Alive || !IsVisibleTarget(hero, other))
                continue;
            var distanceSquared = DistanceSquared(hero.Position, other.Position);
            if (distanceSquared > bestSquared) continue;
            best = other;
            bestSquared = distanceSquared;
        }
        return best;
    }

    /// <summary>
    /// <c>SetTarget(target)</c>: the retained attack target. No bot script names it; it is the
    /// same ensure-the-attack order the tasks issue through
    /// <c>SetStateAndCloseToTarget</c> (<c>Reconstruction:</c>).
    /// </summary>
    internal void NativeSetTarget(Entity hero, Entity? target)
    {
        if (StateOf(hero) is not { } bot) return;
        if (target is not { Alive: true } victim)
        {
            TurnOffAutoAttack(hero);
            return;
        }
        EnsureAttack(hero, bot, victim, Now);
    }

    /// <summary><c>GetTarget()</c>: the target the bot is attacking, if it still lives.</summary>
    internal Entity? NativeCurrentTarget(Entity hero) => CurrentTarget(hero);

    /// <summary>The net id the bot is holding, which is its attack target.</summary>
    internal uint? NativeHeldTargetId(Entity hero) => hero.AttackTarget;

    /// <summary><c>GetTauntTarget()</c>: the shared answer, the newest live Taunt buff's caster.</summary>
    internal Entity? NativeTauntTarget(Entity hero) => PetControl.TauntTarget(_world, hero);

    /// <summary>
    /// The engine's target-lost test, the one <c>OnLostTarget</c> is raised for: the target died,
    /// became untargetable or changed side. Leaving attack range is not a loss.
    /// </summary>
    internal bool NativeTargetStillValid(Entity hero, Entity target) =>
        _combat.CanAttackIgnoringRange(hero, target);

    /// <summary><c>TargetInAttackRange()</c>.</summary>
    internal bool NativeTargetInAttackRange(Entity hero, Entity? target) =>
        target is not null && CombatService.InRange(hero, target);

    /// <summary><c>TurnOnAutoAttack(target)</c>: close in and keep swinging.</summary>
    internal void NativeTurnOnAutoAttack(Entity hero, Entity? target)
    {
        if (StateOf(hero) is not { } bot) return;
        bot.HeldMove = null;
        if (IsActionLocked(hero)) return;
        if (target is not { Alive: true } victim) return;
        EnsureAttack(hero, bot, victim, Now);
    }

    /// <summary><c>TurnOffAutoAttack(stopReason)</c>.</summary>
    internal void NativeTurnOffAutoAttack(Entity hero) => TurnOffAutoAttack(hero);

    /// <summary><c>GetState()</c>: the task's own state id.</summary>
    internal double NativeAiState(Entity hero) => StateOf(hero)?.ScriptState ?? 0.0;

    /// <summary><c>SetState()</c>/<c>NetSetState()</c>: the task's own state id.</summary>
    internal void NativeSetAiState(Entity hero, double state)
    {
        if (StateOf(hero) is not { } bot) return;
        bot.ScriptState = state;
    }
}
