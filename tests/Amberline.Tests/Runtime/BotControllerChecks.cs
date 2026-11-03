using Amberline.Core;
using System.Buffers.Binary;
using System.Diagnostics;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Bots;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Pets;

namespace Amberline.Tests;

public static class BotControllerChecks
{
    private static readonly string[] CurrentTen = ChampionCatalog.PlayableNames.Take(10).ToArray();

    private static readonly Position2 QuietMid = new(6981.74, 7229.17);
    private static readonly Position2 ChaosFountain = new(13933.7890625, 14170.09375);
    private static readonly Position2 OrderFountain = new(
        MapDefinitions.HeroSpawn.X, MapDefinitions.HeroSpawn.Z);

    // ------------------------------------------------------------------
    // Script formulas and arbitration (observed from the shipped scripts)
    // ------------------------------------------------------------------

    // Every number below is the Priority a shipped Task*.luaobj wrote into its own task table,
    // read back after a live update (MirrorTasks). The expected value is hand-evaluated from the
    // bytecode listing and named by its instruction; where the deleted C# port disagreed, the script
    // wins (owner, 20 October 2026). Terms that need a whole match's worth of state — the 110
    // health burst and the structure-damage recency — are pinned by their own behaviour checks
    // (runtime.bots-retreat-two-second-health-sample,
    // runtime.bots-defend-structure-and-push-lane) rather than re-derived here.
    [Check("runtime.bots-script-priority-formulas")]
    public static void PriorityFormulasAreTheShippedScripts()
    {
        // Bot.luaobj proto 0.3 arms TimerHackDelayedInit 1.0 s after OnInit; proto 0.4 builds the
        // task list and arms TimerUpdate every 0.25 s from then on.
        TestAssert.Near(1.25, BotScript.FirstUpdateSeconds, 1e-12,
            "Bot.luaobj proto 0.3/0.4: 1.0 s TimerHackDelayedInit, then the first 0.25 s TimerUpdate");
        TestAssert.Equal(22, BotScript.CastAttempts, "TaskCastSpell proto 0.0's draw loop (0030-0034)");
        TestAssert.Equal(4, BotScript.CastSlotCount, "math.random(0, 3)");

        // ---- TaskCastSpell proto 0.0: (GetMP() / GetMaxMP()) * 0.8 * 2.0 * 0.3 when GetTarget()
        // is set (0003-0005 test it), else Priority = 0 (0016).
        var cast = Start(OneBot("Orianna", Team.Chaos), 501, new AnnieChecks.FixedRandomSource(0.5));
        var castBot = cast.Session.Participants.Single(seat => seat.IsBot).Hero!;
        cast.Bots!.Tick(0.0);
        Quiet(castBot);
        castBot.SpellRanks = [0, 0, 0, 0];
        castBot.Mana = castBot.MaxMana;
        castBot.Position = QuietMid;
        // Past TaskKillMinion's engage distance (GetAttackRange() + 500, module proto 0) so that
        // task scores 0 too and the mana arithmetic is the only thing under test.
        var castTarget = AddBotMinionTarget(cast, castBot, 3000.0);
        Decide(cast, 1.25);
        var castState = StateOf(cast, castBot);
        TestAssert.Near(0.0, TaskOf(castState, BotTaskKind.CastSpell).Priority, 1e-12,
            "TaskCastSpell proto 0.0 0016: no GetTarget(), no priority");

        castBot.AttackTarget = castTarget.NetId;
        Decide(cast, 1.5);
        TestAssert.Near(0.48, TaskOf(castState, BotTaskKind.CastSpell).Priority, 1e-12,
            "TaskCastSpell proto 0.0 0007-0015: 1.0 * 0.8 * 2.0 * 0.3 at full mana");
        castBot.Mana = castBot.MaxMana * 0.5;
        Decide(cast, 1.75);
        TestAssert.Near(0.24, TaskOf(castState, BotTaskKind.CastSpell).Priority, 1e-12,
            "the same expression at half mana: 0.48 * 0.5");
        TestAssert.True(TaskOf(castState, BotTaskKind.CastSpell).Priority < 0.5,
            "0.48 can never beat the 0.5 of an engaged hero (TaskKillHero proto 0.0 0013-0014)");

        // NOT RETAIL, owner decision 22 October 2026 (BotController.NativeMana): a hero with no
        // mana pool answers 1/1 to GetMP()/GetMaxMP(). Retail: the shipped 0/0 is NaN, so the
        // priority is NaN and a no-pool champion's bot never casts.
        castBot.MaxMana = 0.0;
        castBot.Mana = 0.0;
        Decide(cast, 2.0);
        TestAssert.Near(0.48, TaskOf(castState, BotTaskKind.CastSpell).Priority, 1e-12,
            "Non-retail: a hero with no mana pool still casts");

        // ---- TaskKillMinion proto 0.1 (0003-0008): Priority = 0.45 whenever
        // FindNearLowestHPMinion() finds one inside MIN_ENGAGE_DIST = GetAttackRange() + 500
        // (module proto 0), else 0.
        var minion = Start(OneBot("Orianna", Team.Chaos), 502, new AnnieChecks.FixedRandomSource(0.5));
        var minionBot = minion.Session.Participants.Single(seat => seat.IsBot).Hero!;
        minion.Bots!.Tick(0.0);
        Quiet(minionBot);
        minionBot.Mana = 0.0;
        minionBot.Position = QuietMid;
        Decide(minion, 1.25);
        var minionState = StateOf(minion, minionBot);
        TestAssert.Near(0.0, TaskOf(minionState, BotTaskKind.KillMinion).Priority, 1e-12,
            "TaskKillMinion proto 0.1: no minion in range, no priority");
        AddBotMinionTarget(minion, minionBot, 300.0);
        Decide(minion, 1.5);
        TestAssert.Near(0.45, TaskOf(minionState, BotTaskKind.KillMinion).Priority, 1e-12,
            "TaskKillMinion proto 0.1 0003-0008: a minion inside GetAttackRange() + 500 scores 0.45");

        // ---- TaskKillHero proto 0.0 (0006-0030): dist = max(GetDist(GetPos(), GetPos(HeroID)), 1);
        // Priority = 0.5 when dist < HERO_ENGAGE_DIST = GetAttackRange() + 500 (module proto 0),
        // 0.5 * (1 - (dist - E) / E) while dist < 2E, else 0. There is no sight test.
        var hunt = Start(OneBot("Orianna", Team.Chaos), 503, new AnnieChecks.FixedRandomSource(0.5));
        var huntBot = hunt.Session.Participants.Single(seat => seat.IsBot).Hero!;
        hunt.Bots!.Tick(0.0);
        Quiet(huntBot);
        huntBot.Mana = 0.0;
        huntBot.Position = QuietMid;
        var huntTarget = AddBotTarget(hunt, huntBot, 30.0);
        var engage = huntBot.AttackRange + 500.0;
        double HeroPriority() => StateOf(hunt, huntBot).Tasks
            .First(task => task.Kind == BotTaskKind.KillHero && task.SubjectNetId == huntTarget.NetId)
            .Priority;

        // One update per band, all before the AntiKiteTimer BeginTask arms (2.0 s later) can
        // latch the module's ReducePriority and zero every kill-hero priority.
        var bands = new (double Distance, double Expected, string Why)[]
        {
            (1.0, 0.5, "the distance is clamped to at least 1 (0006-0007)"),
            (engage, 0.5, "the linear band starts at 0.5 (0013-0014)"),
            (1.5 * engage, 0.25, "halfway through the band (0019-0024)"),
            (2.0 * engage, 0.0, "twice the engage distance is 0 (0025-0027)"),
        };
        var step = 1.25;
        foreach (var band in bands)
        {
            huntBot.Position = QuietMid;
            huntBot.Waypoints.Clear();
            huntBot.AttackTarget = null;
            huntTarget.Position = new Position2(QuietMid.X + band.Distance, QuietMid.Z);
            huntTarget.Alive = true;
            Decide(hunt, step += 0.25);
            TestAssert.Near(band.Expected, HeroPriority(), 1e-12,
                $"TaskKillHero proto 0.0 at {band.Distance:0} units: {band.Why}");
        }
        huntTarget.Alive = false;
        Decide(hunt, step += 0.25);
        TestAssert.Near(0.0, HeroPriority(), 1e-12,
            "TaskKillHero proto 0.0 0020-0022: a dead hero scores 0");

        // ---- TaskRetreat proto 0.0 (0035-0040): Priority = 1.0 * (0.25 * near + 0.75) * missing,
        // near = max(1 - GetDist(GetRegroupPos(), GetPos()) / sqrt(GetMaxTravelDistSquared()), 0)
        // (0004-0019), missing = max(1 - GetHP() / GetMaxHP(), 0) (0020-0029). 0030-0034 set
        // missing = 1 when near > 0.9 and the bot is hurt at all. All three updates run before
        // game time 2.0, so the two-second burst sample (0072-0085) has no comparison to make.
        var travel = BotController.MaxTravelDistance;
        var retreat = Start(OneBot("Orianna", Team.Chaos), 504, new AnnieChecks.FixedRandomSource(0.5));
        var retreatBot = retreat.Session.Participants.Single(seat => seat.IsBot).Hero!;
        retreat.Bots!.Tick(0.0);
        Quiet(retreatBot);
        retreatBot.Mana = 0.0;
        retreatBot.MaxHealth = 1000.0;
        var retreatState = StateOf(retreat, retreatBot);
        // Restated in batch 254 (bundle 4 red since batch 248): GetPos() is where the hero stands when the formula runs, and an order
        // the decision issues first seats the hero on its path's first point, the 2-unit lattice anchor (the client's server-path
        // follower): 1.09 units from the placed point in the first case. near is computed from that position;
        // the missing-health factor (Missing) and the formula are unchanged.
        var retreatCases = new (double Health, double Dist, double Missing, string Why)[]
        {
            (500.0, 0.5 * travel, 0.5, "half health, halfway across the map"),
            (900.0, 0.05 * travel, 1.0,
                "within a tenth of the travel scale the bot counts as fully injured (0030-0034)"),
            (1000.0, 0.05 * travel, 0.0, "a healthy bot never retreats"),
        };
        step = 1.25;
        foreach (var testCase in retreatCases)
        {
            retreatBot.Health = testCase.Health;
            var placed = new Position2(ChaosFountain.X - testCase.Dist, ChaosFountain.Z);
            retreatBot.Position = placed;
            retreatBot.Waypoints.Clear();
            retreatBot.AttackTarget = null;
            Decide(retreat, step += 0.25);
            TestAssert.True(DistanceSquared(retreatBot.Position, placed) <= 2.0 * 2.0,
                $"TaskRetreat proto 0.0: {testCase.Why}: the hero stands on the placed point or its lattice anchor");
            var near = Math.Max(1.0 - Math.Sqrt(DistanceSquared(ChaosFountain, retreatBot.Position)) / travel, 0.0);
            TestAssert.Near(testCase.Missing * (0.25 * near + 0.75), TaskOf(retreatState, BotTaskKind.Retreat).Priority,
                1e-9, $"TaskRetreat proto 0.0: {testCase.Why}");
        }

        // ---- TaskBuyItem proto 0.2. ITEMS_TO_BUY = {1001, 2003, 1006, 1007, 1042, 1029} and
        // BUY_INDEX = 1 (module proto 0), so the first item is the 350 gold boots. The gates are
        // GetNumInvSlotsAvailable() < 1 (0008-0011) and GetGold() < GetItemPrice (0014-0017); the
        // band is 0.8 * scale under 500 units of the regroup point (0034-0037), 6000 / d * 0.2 *
        // scale between 3000 and 6000 (0038-0043), else 0.2 * scale (0044-0045), clamped at 0.9.
        // scale is 1.1 when 2 * price < gold (0026-0029), else 1.0. Gold is held exactly at the
        // price: UpdatePriority counts that as affordable, while Tick's strict price < gold gate
        // buys nothing, so BUY_INDEX never moves under this check.
        var buy = Start(OneBot("Orianna", Team.Chaos), 505, new AnnieChecks.FixedRandomSource(0.5));
        var buyBot = buy.Session.Participants.Single(seat => seat.IsBot).Hero!;
        buy.Bots!.Tick(0.0);
        Quiet(buyBot);
        buyBot.Mana = 0.0;
        var buyState = StateOf(buy, buyBot);
        Decide(buy, 1.25);
        TestAssert.Near(0.0, TaskOf(buyState, BotTaskKind.Buy).Priority, 1e-12,
            "TaskBuyItem proto 0.2 0008-0011: a full inventory has no free slot");

        FreeSlot(buyBot, 0);
        buyBot.Gold = 350.0;
        var buyCases = new (double Dist, double Expected, string Why)[]
        {
            (100.0, 0.8, "affordable near the regroup point"),
            (4000.0, 6000.0 / 4000.0 * 0.2, "3000..6000: 6000 / d * 0.2"),
            (6000.0, 0.2, "6000 and beyond: 0.2"),
            (3000.0, 0.2, "exactly 3000 falls through both range tests to 0.2"),
        };
        step = 1.25;
        foreach (var buyCase in buyCases)
        {
            buyBot.Position = new Position2(ChaosFountain.X - buyCase.Dist, ChaosFountain.Z);
            buyBot.Waypoints.Clear();
            buyBot.AttackTarget = null;
            Decide(buy, step += 0.25);
            TestAssert.Near(buyCase.Expected, TaskOf(buyState, BotTaskKind.Buy).Priority, 1e-12,
                $"TaskBuyItem proto 0.2: {buyCase.Why}");
        }
        // scale is observed away from the shop: Tick only buys within MIN_DIST_TO_SHOP (proto 0.4
        // 0019-0021) and, when it does buy, it ends by re-running UpdatePriority (0047-0048), so
        // the mirrored priority after a purchase is the next list entry's band. Out there nothing
        // buys and the 1.1 of a gold pile above twice the price is what the task reports.
        foreach (var scaled in new (double Dist, double Expected)[]
                 {
                     (4000.0, 6000.0 / 4000.0 * 0.2 * 1.1),
                     (6000.0, 0.2 * 1.1),
                 })
        {
            buyBot.Position = new Position2(ChaosFountain.X - scaled.Dist, ChaosFountain.Z);
            buyBot.Waypoints.Clear();
            buyBot.AttackTarget = null;
            buyBot.Gold = 800.0;
            Decide(buy, step += 0.25);
            TestAssert.Near(scaled.Expected, TaskOf(buyState, BotTaskKind.Buy).Priority, 1e-12,
                "TaskBuyItem proto 0.2 0026-0029: more than twice the price scales by 1.1 | " +
                Diag(buy, buyBot, buyState));
        }
        buyBot.Gold = 300.0;
        Decide(buy, step += 0.25);
        TestAssert.Near(0.0, TaskOf(buyState, BotTaskKind.Buy).Priority, 1e-12,
            "TaskBuyItem proto 0.2 0014-0017: not enough gold");
    }

    // Bot.luaobj proto 0.6 (TimerUpdate) is the whole arbiter: best starts as the last task and the
    // backwards walk replaces it only on a strictly higher Priority, and activeTask changes only to
    // a task whose Priority is strictly higher. Both are observed on two kill-hero tasks, which are
    // the only pair a fixture can tie at 0.5.
    [Check("runtime.bots-task-arbitration-tie-and-switch")]
    public static void EqualPrioritiesResolveToTheLastTaskAndNeverSwitchTheActiveOne()
    {
        var runtime = Start(OneBot("Orianna", Team.Chaos), 511, new AnnieChecks.FixedRandomSource(0.5));
        var bot = runtime.Session.Participants.Single(seat => seat.IsBot).Hero!;
        runtime.Bots!.Tick(0.0);
        Quiet(bot);
        bot.Mana = 0.0;
        bot.Position = QuietMid;
        var first = AddBotTarget(runtime, bot, 400.0);
        var second = AddBotTarget(runtime, bot, 3000.0);
        double PriorityOf(Entity target) => StateOf(runtime, bot).Tasks
            .First(task => task.Kind == BotTaskKind.KillHero && task.SubjectNetId == target.NetId)
            .Priority;

        Decide(runtime, 1.25);
        var state = StateOf(runtime, bot);
        TestAssert.True(state.Active is { Kind: BotTaskKind.KillHero, SubjectNetId: var subject } &&
                        subject == first.NetId,
            "only the hero inside HERO_ENGAGE_DIST scores 0.5, so it is the best task");
        // MirrorTasks rebuilds every BotTask each step (the script's tables are the truth and only
        // their fields are copied out), so the running task is identified by its fields, not by a
        // reference that never survives one step.
        var activeBeforeTie = state.Active!;
        // Taking up a task at all counts as a switch (null -> task), so the count is read here and
        // only its movement from now on is the arbiter's.
        var switchesBeforeTie = state.Measure.TaskSwitches;

        // TimerUpdate's backward walk (Bot.txt proto 0.6, 0037-0041) replaces best only on a
        // strictly higher Priority, so a second task at the same 0.5 does not take over from the
        // one already running (0082: activeTask.Priority < best.Priority is false).
        second.Position = new Position2(bot.Position.X + 400.0, bot.Position.Z);
        Decide(runtime, 1.5);
        state = StateOf(runtime, bot);
        TestAssert.Near(0.5, PriorityOf(second), 1e-12, "the second hero is now tied at 0.5");
        TestAssert.True(SameTask(state.Active, activeBeforeTie),
            "an equal priority keeps the running task");
        TestAssert.Equal(switchesBeforeTie, state.Measure.TaskSwitches, "and no switch is counted");

        // A strictly higher priority does replace it: drop the first hero out of the band.
        first.Position = new Position2(bot.Position.X + 3000.0, bot.Position.Z);
        Decide(runtime, 1.75);
        state = StateOf(runtime, bot);
        TestAssert.Near(0.0, PriorityOf(first), 1e-12, "the first hero is out of the band now");
        TestAssert.True(state.Active is { Kind: BotTaskKind.KillHero, SubjectNetId: var moved } &&
                        moved == second.NetId,
            "a strictly higher priority replaces the running task");
        TestAssert.Equal(switchesBeforeTie + 1, state.Measure.TaskSwitches);

        // When two tasks tie from the first update, best is the later list entry (proto 0.6 0041
        // replaces best only on strictly higher, so the last of the tied ones stays). state.Tasks
        // keeps the script's own list order.
        var tiedRuntime = Start(OneBot("Orianna", Team.Chaos), 512, new AnnieChecks.FixedRandomSource(0.5));
        var tiedBot = tiedRuntime.Session.Participants.Single(seat => seat.IsBot).Hero!;
        tiedRuntime.Bots!.Tick(0.0);
        Quiet(tiedBot);
        tiedBot.Mana = 0.0;
        tiedBot.Position = QuietMid;
        AddBotTarget(tiedRuntime, tiedBot, 400.0);
        AddBotTarget(tiedRuntime, tiedBot, 400.0);
        Decide(tiedRuntime, 1.25);
        var tiedState = StateOf(tiedRuntime, tiedBot);
        var tied = tiedState.Tasks
            .Where(task => task.Kind == BotTaskKind.KillHero && task.Priority > 0.0).ToArray();
        TestAssert.Equal(2, tied.Length, "both heroes in range score 0.5");
        TestAssert.True(ReferenceEquals(tiedState.Active, tied[^1]),
            "of two equal maxima the later list entry wins");
    }

    [Check("runtime.bots-task-list-order-and-unused-scripts")]
    public static void TaskListFollowsBotLuaobjAndSkipsWanderAndSummoners()
    {
        var roster = CurrentTen.Select((champion, index) =>
            new SessionParticipantDefinition((ulong)index + 1, (uint)index,
                $"List {index + 1}", champion, index < 5 ? Team.Order : Team.Chaos,
                IsBot: index != 5)).ToArray();
        var runtime = Start(roster, 302);
        var bot = runtime.Session.Participants.First(seat => seat.IsBot && seat.Team == Team.Order).Hero!;
        runtime.Bots!.Tick(0.0);
        Decide(runtime, 1.25);
        var tasks = StateOf(runtime, bot).Tasks;

        var structures = runtime.World.Entities.Values.Count(entity => entity.Team == Team.Order &&
            entity.Kind is EntityKind.Turret or EntityKind.Inhibitor or EntityKind.Nexus &&
            entity.MaxHealth > 0.0);
        var enemyHeroes = runtime.World.Entities.Values.Count(entity => entity.UnitType == UnitType.Hero &&
            entity.OwnerNetId == 0 && entity.Team == Team.Chaos);
        var expected = new List<BotTaskKind>
        {
            BotTaskKind.CastSpell, BotTaskKind.Retreat, BotTaskKind.KillMinion, BotTaskKind.KillTower,
            BotTaskKind.PushLane, BotTaskKind.PushLane, BotTaskKind.PushLane,
        };
        expected.AddRange(Enumerable.Repeat(BotTaskKind.Defend, structures));
        expected.Add(BotTaskKind.Buy);
        expected.AddRange(Enumerable.Repeat(BotTaskKind.KillHero, enemyHeroes));
        TestAssert.True(tasks.Select(task => task.Kind).SequenceEqual(expected),
            "Bot.luaobj order: cast, retreat, kill minion, kill structure, three lanes, defend each structure, buy, kill each hero");
        TestAssert.True(tasks.Where(task => task.Kind == BotTaskKind.PushLane).Select(task => task.Lane)
                .SequenceEqual(new[] { 0, 1, 2 }), "Push Lane 0, 1, 2");
        TestAssert.Equal(5, enemyHeroes, "every enemy hero, including the human, gets a kill-hero task");
        TestAssert.True(structures >= 15, $"every friendly turret, inhibitor and nexus gets a defend task ({structures})");

        // The scripts never cast a summoner spell (Bot.dat only lists the actions).
        foreach (var seat in runtime.Session.Participants.Where(seat => seat.IsBot))
            TestAssert.True(seat.Hero!.SummonerCooldowns.All(cooldown => cooldown == 0.0),
                "bots never use summoner spells");
    }

    [Check("runtime.bots-first-update-after-init-delay")]
    public static void FirstTaskUpdateFollowsTheInitDelayAndTheFirstTimerTick()
    {
        var runtime = Start(OneBot("Orianna", Team.Chaos), 301);
        var seat = runtime.Session.Participants.Single(participant => participant.IsBot);
        var bot = seat.Hero!;
        runtime.Bots!.Tick(0.0);

        Decide(runtime, 1.0);
        TestAssert.True(bot.Inventory.All(slot => slot is null),
            "TimerHackDelayedInit has fired but the first TimerUpdate has not");
        TestAssert.Equal(0, runtime.Bots.OrdersIssued(seat.PlayerId),
            "no order before the first 250 ms task update");

        Decide(runtime, 1.25);
        TestAssert.True(Inventory.HasItem(bot.Inventory, 1001),
            "the first update at 1.25 s buys the first list item at the fountain");
        TestAssert.True(runtime.Bots.OrdersIssued(seat.PlayerId) > 0);
    }

    // ------------------------------------------------------------------
    // Task behaviour
    // ------------------------------------------------------------------

    [Check("runtime.bots-buy-list-cycle-and-price-rule")]
    public static void BuyTaskCyclesTheListAndRequiresAStrictlyHigherGold()
    {
        var runtime = Start(OneBot("Orianna", Team.Chaos), 311);
        var bot = runtime.Session.Participants.Single(participant => participant.IsBot).Hero!;
        bot.SkillPoints = 0;
        runtime.Bots!.Tick(0.0);

        // BuyItem fires only when price < gold: exactly the price buys nothing.
        bot.Gold = 350.0;
        Decide(runtime, 1.25);
        var state = StateOf(runtime, bot);
        TestAssert.True(state.Active is { Kind: BotTaskKind.Buy },
            "an affordable item near the regroup point makes the buy task active");
        TestAssert.False(Inventory.HasItem(bot.Inventory, 1001),
            "a price equal to the gold is not enough for the Tick");
        bot.Gold = 351.0;
        Decide(runtime, 1.5);
        TestAssert.True(Inventory.HasItem(bot.Inventory, 1001), "one more gold buys the boots");

        // The list is walked in order, one item per update.
        bot.Gold = 10000.0;
        var expected = new[] { 2003, 1006, 1007, 1042, 1029 };
        for (var index = 0; index < expected.Length; index++)
        {
            Decide(runtime, 1.75 + 0.25 * index);
            TestAssert.True(Inventory.HasItem(bot.Inventory, expected[index]),
                $"list entry {expected[index]} is bought in order");
            for (var later = index + 1; later < expected.Length; later++)
                TestAssert.False(Inventory.HasItem(bot.Inventory, expected[later]),
                    $"list entry {expected[later]} is not bought early");
        }
        TestAssert.Equal(6, bot.Inventory.Count(slot => slot is not null), "six slots hold the six list items");

        // TaskBuyItem proto 0.4 calls BuyItem and ChangeItemToBuy as two separate calls and runs
        // the second whatever the first returned (0039-0043), behind no gate but the gold test at
        // 0037 (GetItemPrice(ItemToBuy) < GetGold()). The list therefore advances on the update,
        // not on the purchase: an update that finds nowhere to put the item still walks it. The
        // slot bought is handed back after every update below, so none of them finds a full
        // inventory and the seven advances are exactly the seven entries — and the seventh is the
        // first one again, which is the wrap proto 0.0 0006-0007 performs (BUY_INDEX goes 6 -> 7
        // and 6 < 7 sets it back to 1). The old C# port advanced the list only on an accepted
        // purchase and expected the wrap one purchase after the list's end; the script wins.
        for (var slot = 0; slot < Inventory.SlotCount; slot++) FreeSlot(bot, slot);
        bot.Gold = 10000.0;
        var list = new[] { 1001, 2003, 1006, 1007, 1042, 1029, 1001 };
        for (var index = 0; index < list.Length; index++)
        {
            var beforeWrap = bot.Inventory.Select(slot => slot?.ItemId ?? 0).ToArray();
            Decide(runtime, 3.0 + 0.25 * index);
            var afterWrap = bot.Inventory.Select(slot => slot?.ItemId ?? 0).ToArray();
            var wrapped = afterWrap.Where((id, slot) => id != beforeWrap[slot]).ToArray();
            TestAssert.True(wrapped.SequenceEqual(new[] { list[index] }),
                $"one update buys list entry {list[index]} | bought=[{string.Join(",", wrapped)}] " +
                $"before=[{string.Join(",", beforeWrap)}] after=[{string.Join(",", afterWrap)}] " +
                Diag(runtime, bot, state));
            for (var slot = 0; slot < afterWrap.Length; slot++)
                if (beforeWrap[slot] == 0 && afterWrap[slot] != 0) FreeSlot(bot, slot);
        }

        // TaskBuyItem proto 0.2 0008-0011: GetNumInvSlotsAvailable() < 1 writes Priority 0 and
        // returns before any of the gold arithmetic.
        bot.Inventory = Enumerable.Repeat<InventorySlot?>(new InventorySlot(1001), Inventory.SlotCount)
            .ToArray();
        bot.Gold = 10000.0;
        Decide(runtime, 4.75);
        TestAssert.Near(0.0, TaskOf(state, BotTaskKind.Buy).Priority, 1e-12,
            "a full inventory has no free slot, so the buy task stops");
    }

    [Check("runtime.bots-kill-minion-lowest-health-and-no-neutrals")]
    public static void KillMinionTargetsTheLowestHealthEnemyMinionInRangeAndIgnoresCamps()
    {
        var runtime = Start(OneBot("Orianna", Team.Chaos), 321);
        var bot = runtime.Session.Participants.Single(participant => participant.IsBot).Hero!;
        runtime.Bots!.Tick(0.0);
        Quiet(bot);
        bot.Position = QuietMid;
        bot.Mana = 0.0; // keep the cast task (0.48 * mana fraction) below the kill task

        var healthy = AddBotMinionTarget(runtime, bot, 300.0, 500.0);
        var lowest = AddBotMinionTarget(runtime, bot, 700.0, 200.0);
        var outOfRange = AddBotMinionTarget(runtime, bot, 1500.0, 50.0);
        var camp = new Entity(runtime.World.Allocate(UnitType.Monster), EntityKind.Monster,
            UnitType.Monster, Team.Neutral, new Position2(bot.Position.X - 100.0, bot.Position.Z))
        {
            Name = "wolf", Alive = true, Created = true, Visible = true,
            MaxHealth = 100.0, Health = 10.0, Radius = 35.0,
        };
        runtime.World.Register(camp);

        Decide(runtime, 1.25);
        var state = StateOf(runtime, bot);
        TestAssert.Near(0.45, TaskOf(state, BotTaskKind.KillMinion).Priority, 1e-12,
            "TaskKillMinion scores 0.45 whenever an enemy minion is in range");
        TestAssert.True(state.Active is { Kind: BotTaskKind.KillMinion });
        // TaskKillMinion proto 0.4 (Tick) only swings once the chosen minion is inside
        // MIN_FIGHT_DIST = GetAttackRange() + 50 (module, proto 0); beyond it the same target is
        // walked at (SetStateAndMove(AI_FOLLOW, GetPos(target))) with the attack turned off. The
        // lowest-health candidate is 700 units out, so the choice shows on the route's end.
        TestAssert.Equal(lowest.NetId, ChosenOf(bot, healthy, lowest, outOfRange, camp),
            "the lowest-health minion inside attack range + 500 is chosen, not the nearest");

        healthy.Health = 100.0;
        Decide(runtime, 1.5);
        // TaskKillMinion re-runs FindNearLowestHPMinion on every Tick (proto 0.4) and points at
        // whatever is lowest now; the script has no settle window.
        TestAssert.Equal(healthy.NetId, ChosenOf(bot, healthy, lowest, outOfRange, camp),
            "the task re-points at the newly lowest minion on the next update");
        Decide(runtime, 2.5);
        TestAssert.Equal(healthy.NetId, ChosenOf(bot, healthy, lowest, outOfRange, camp),
            "the task then retargets to whichever minion became the lowest");
        TestAssert.True(bot.AttackTarget != camp.NetId && bot.AttackTarget != outOfRange.NetId,
            "jungle camps and minions beyond the engage distance are never chosen");

        healthy.Alive = false;
        Decide(runtime, 2.75);
        TestAssert.Equal(lowest.NetId, ChosenOf(bot, healthy, lowest, outOfRange, camp),
            "a dead minion is skipped at once");
    }

    /// <summary>
    /// The body a kill task is on: the one the bot swings at, or — when its Tick is still closing
    /// (TaskKillMinion proto 0.4: <c>SetStateAndMove(AI_FOLLOW, GetPos(target))</c>) — the one the
    /// route's end is.
    /// </summary>
    private static uint ChosenOf(Entity bot, params Entity[] candidates)
    {
        if (bot.AttackTarget is uint attacking) return attacking;
        if (bot.Waypoints.Count == 0) return 0u;
        var end = bot.Waypoints[^1];
        return candidates.Where(candidate => candidate.Alive)
            .OrderBy(candidate => DistanceSquared(candidate.Position, end)).First().NetId;
    }

    [Check("runtime.bots-kill-tower-priority-and-target")]
    public static void KillTowerScoresPointFourAndRisesForAWeakTower()
    {
        var runtime = Start(OneBot("Orianna", Team.Chaos), 331);
        var bot = runtime.Session.Participants.Single(participant => participant.IsBot).Hero!;
        runtime.Bots!.Tick(0.0);
        var turret = runtime.World.Entities.Values.First(entity =>
            entity.Kind == EntityKind.Turret && entity.Team == Team.Order &&
            runtime.Combat.IsTargetable(entity, bot));
        var toCenter = new Position2(7000.0 - turret.Position.X, 7000.0 - turret.Position.Z);
        var length = Math.Sqrt(toCenter.X * toCenter.X + toCenter.Z * toCenter.Z);
        Quiet(bot);
        bot.Mana = 0.0;
        bot.Position = new Position2(turret.Position.X + toCenter.X / length * 650.0,
            turret.Position.Z + toCenter.Z / length * 650.0);

        // The task picks the nearest visible, targetable enemy structure inside attack range + 800.
        var engage = bot.AttackRange + 800.0;
        var candidates = runtime.World.Entities.Values
            .Where(entity => entity.Team == Team.Order && entity.Alive &&
                entity.Kind is EntityKind.Turret or EntityKind.Inhibitor or EntityKind.Nexus &&
                runtime.Combat.IsTargetable(entity, bot) && runtime.Spells.CanSeeTarget(bot, entity) &&
                DistanceSquared(bot.Position, entity.Position) < engage * engage)
            .OrderBy(entity => DistanceSquared(bot.Position, entity.Position))
            .ToArray();
        var expected = candidates.FirstOrDefault();
        TestAssert.True(expected is not null, "the fixture bot sees an attackable enemy structure");

        Decide(runtime, 1.25);
        var state = StateOf(runtime, bot);
        // TaskKillTower proto 0.1: hp / maxHp < 0.2 scores 1 - hp / maxHp (0008-0013), else 0.4
        // (0014). The fixture structure is untouched, so the healthy branch applies.
        TestAssert.Near(0.4, TaskOf(state, BotTaskKind.KillTower).Priority, 1e-12,
            "TaskKillTower proto 0.1 0014: a healthy tower in range scores 0.4");
        TestAssert.True(state.Active is { Kind: BotTaskKind.KillTower },
            "the nearest enemy structure in attack range + 800 becomes the task");
        // Like the minion task, TaskKillTower only swings once it is close enough and walks at its
        // target until then, so the choice is read off the route's end rather than the attack.
        TestAssert.Equal(expected!.NetId, ChosenOf(bot, candidates), "the bot attacks the nearest structure");

        expected.Health = expected.MaxHealth * 0.1;
        Decide(runtime, 1.5);
        TestAssert.Near(0.9, TaskOf(state, BotTaskKind.KillTower).Priority, 1e-9,
            "below 20 % health the score is 1 - health fraction");
    }

    // NOT RETAIL, owner decision 22 October 2026: Janna has no own TaskCastSpell.luaobj, so
    // BotController.NativeReconstructModuleData gives its TaskCastSpell module a BeginTask with
    // Annie's 22-draw shape (Annie TaskCastSpell BeginTask) and a bare-RETURN Tick
    // (DATA/Scripts/TaskCastSpell proto 0.2). Retail: the shared DATA template's BeginTask is
    // itself a bare RETURN (the TaskCastSpell bytecode listing, proto 0.1), so the
    // bot casts nothing and is not a bot at all. The cast happens in BeginTask only.
    [Check("runtime.bots-cast-spell-task-priority-and-begin-cast")]
    public static void CastTaskCastsInBeginTaskOnly()
    {
        var runtime = Start(OneBot("Janna", Team.Chaos), 341, new AnnieChecks.FixedRandomSource(0.5));
        var bot = runtime.Session.Participants.Single(participant => participant.IsBot).Hero!;
        runtime.Bots!.Tick(0.0);
        Quiet(bot);
        bot.SpellRanks = [0, 0, 1, 0];
        bot.SkillPoints = 0;
        bot.Mana = bot.MaxMana;
        bot.Position = QuietMid;
        var minionTarget = AddBotMinionTarget(runtime, bot, 30.0);

        Decide(runtime, 1.25);
        var state = StateOf(runtime, bot);
        TestAssert.True(state.Active is { Kind: BotTaskKind.KillMinion },
            "the kill task starts first; the cast task has no target yet");
        TestAssert.Equal(0, bot.MatchStatistics.SpellCasts[2]);

        bot.AttackTarget = minionTarget.NetId;
        Decide(runtime, 1.5);
        TestAssert.Near(0.48, TaskOf(state, BotTaskKind.CastSpell).Priority, 1e-9,
            "TaskCastSpell proto 0.0 0007-0015: with a target and full mana the cast task scores 0.48");
        TestAssert.True(state.Active is { Kind: BotTaskKind.CastSpell },
            "0.48 beats the 0.45 of the minion kill task");
        // FixedRandomSource(0.5) draws slot 2 every time (IRandomSource.NextIndex), so the
        // reconstructed BeginTask casts Eye of the Storm on the bot itself and returns.
        TestAssert.Equal(1, bot.MatchStatistics.SpellCasts[2],
            "the reconstructed BeginTask casts the first usable slot");
        TestAssert.Equal(22, BotScript.CastAttempts, "Annie's draw loop, 0030-0034");

        // Tick is a bare RETURN (DATA/Scripts/TaskCastSpell proto 0.2), so nothing more happens
        // while the task stays active, however long it runs.
        for (var step = 0; step < 4; step++)
        {
            bot.AttackTarget = minionTarget.NetId;
            bot.HeroPursuit = new HeroPursuitState(minionTarget.NetId, minionTarget.Position);
            bot.SpellCooldowns[2] = 0.0;
            bot.Mana = bot.MaxMana;
            Decide(runtime, 1.75 + 0.25 * step);
        }
        TestAssert.True(state.Active is { Kind: BotTaskKind.CastSpell },
            "the cast task stays active while its target is alive");
        TestAssert.Equal(1, bot.MatchStatistics.SpellCasts.Sum(),
            "the cast task's Tick draws and casts nothing: BeginTask was the only cast");
    }

    // TaskCastSpell.BeginTask gates the slot on the spellbook's can-use check, which
    // has no range or target test; ServerCastSpell then casts. A target beyond the spell's range
    // must therefore make the hero approach and cast, not skip the slot as the plain cast gate did.
    [Check("runtime.bots-cast-out-of-range")]
    public static void CastTargetBeyondSpellRangeIsApproachedAndCast()
    {
        var range = GameData.Default.Spell(GameData.Default.Champion("Janna").SpellNames[1]).CastRange;
        TestAssert.True(range > 100.0, "Janna's Zephyr has a real cast range");
        var (runtime, bot, target, state) = ArmedCastBot(
            new SlotScript(1), [0, 1, 0, 0], range + 100.0);
        TestAssert.True(runtime.Spells.CanSeeTarget(bot, target),
            "the target just beyond the spell's range is in sight");
        var plain = runtime.Spells.CanCast(bot, 1, target.NetId,
            new Position3(target.Position.X, 0.0, target.Position.Z), 1.25);
        TestAssert.True(!plain.Allowed && plain.Failure == SpellCastFailure.Range,
            "the plain cast gate refuses the target as out of range");
        TestAssert.True(runtime.Spells.CanUseSpell(bot, 1, 1.25).Allowed,
            "CanUseSpell has no range test, so the slot is usable");

        Decide(runtime, 1.25);
        TestAssert.True(state.Active is { Kind: BotTaskKind.CastSpell },
            "0.48 beats the 0.45 of the minion kill task, so the cast task starts");
        TestAssert.True(bot.PendingCast is not null && bot.Waypoints.Count > 0,
            "the out-of-range cast is queued and the hero walks toward the target");
        TestAssert.Equal(0, bot.MatchStatistics.SpellCasts[1], "nothing is cast while out of range");

        for (var step = 0; step < 60 && bot.MatchStatistics.SpellCasts[1] == 0; step++)
            runtime.Tick(0.1);
        TestAssert.Equal(1, bot.MatchStatistics.SpellCasts[1],
            "the bot casts Zephyr once the approach brings it into range");
        // Batch 346 (batch 340's rule): Zephyr is a windup, which pays its mana and starts its cooldown at its cast frame
        // (DoCast_ClientAndServer -> ApplyCostsOfSpell), not when the queued cast starts. Run the windup to its cast frame.
        var castFrame = BbM3.CastFrame(GameData.Default.Champion("Janna").SpellNames[1]);
        TestAssert.True(castFrame > 0.0, "Zephyr has a windup");
        for (var elapsed = 0.0; elapsed < castFrame + 0.1; elapsed += 0.1) runtime.Tick(0.1);
        TestAssert.True(bot.SpellCooldowns[1] > 0.0 && bot.Mana < bot.MaxMana,
            "the approached cast pays its cooldown and mana like any cast, at its cast frame");
        TestAssert.Equal(1, bot.MatchStatistics.SpellCasts[1], "the windup runs to its cast frame without a second cast");
        TestAssert.True(bot.PendingCast is null, "the queued cast is consumed");
        TestAssert.True(Math.Sqrt(DistanceSquared(bot.Position, target.Position)) <= range + 1.0,
            "the hero stands inside the spell's range when it casts");
    }

    // Batch 568 (batch 547 record, "Open"; the owner's game 059670b5: a bot Fiddlesticks ended its Drain 0.21 s after it opened). A
    // Fiddlesticks bot drains a minion, and the minion then stands just out of attack reach but inside DrainChannel's 650 leash, so
    // TaskKillMinion's Tick (proto 0.4 0030-0047: not TargetInAttackRange -> TurnOffAutoAttack, SetStateAndMove AI_FOLLOW) re-orders
    // every 250 ms. The channel must run its 5 s ChannelDuration with the bot standing still, and the bot walks after it ends.
    // Red before the batch: the move native's order path ended the channel at the next task update.
    [Check("runtime.bots-own-orders-do-not-end-a-channel")]
    public static void BotOrdersHoldWhileItsChannelRuns()
    {
        var runtime = Start(OneBot("FiddleSticks", Team.Chaos), 568, new SlotScript(1));
        var bot = runtime.Session.Participants.Single(participant => participant.IsBot).Hero!;
        runtime.Bots!.Tick(0.0);
        var state = StateOf(runtime, bot);
        Quiet(bot);
        bot.SpellRanks = [0, 1, 0, 0];
        bot.Mana = bot.MaxMana;
        bot.Position = QuietMid;
        var minion = AddBotMinionTarget(runtime, bot, 400.0, health: 5000.0);
        bot.AttackTarget = minion.NetId;

        Decide(runtime, 1.25);
        TestAssert.True(state.Active is { Kind: BotTaskKind.CastSpell }, "the cast task starts (0.48 over KillLowHPMinion's 0.45)");
        TestAssert.Equal(1, bot.MatchStatistics.SpellCasts[1], "the bot casts Drain on its target");
        var reach = CombatService.AttackReach(bot, minion);
        TestAssert.True(reach + 20.0 < 640.0, $"a point out of attack reach ({reach:0}) lies inside DrainChannel's 650 leash");

        const double step = 1.0 / 30.0;
        double? opened = null, closed = null;
        var openedAt = bot.Position;
        var moved = 0.0;
        for (var index = 0; index < (int)Math.Round(8.0 / step) && closed is null; index++)
        {
            runtime.Tick(step);
            if (opened is null && bot.ActiveScriptChannels > 0)
            {
                opened = runtime.Combat.CurrentTime;
                openedAt = bot.Position;
                minion.Position = new Position2(bot.Position.X + reach + 20.0, bot.Position.Z);
            }
            if (opened is not null && bot.ActiveScriptChannels > 0)
                moved = Math.Max(moved, Math.Sqrt(DistanceSquared(openedAt, bot.Position)));
            if (opened is not null && bot.ActiveScriptChannels == 0) closed = runtime.Combat.CurrentTime;
        }
        TestAssert.True(opened is not null, "Drain opens its channel");
        TestAssert.True(closed is not null, "the channel ends inside eight seconds");
        TestAssert.Near(5.0, closed!.Value - opened!.Value, 2 * step + 1e-6,
            $"the channel runs its ChannelDuration while the kill task re-orders (task {state.Active?.Kind}, orders {state.OrdersIssued})");
        TestAssert.True(moved < 1.0, $"the bot stands still in its channel (moved {moved:0.0} units)");
        TestAssert.True(minion.Alive && minion.Health < 5000.0, "the drained minion took the channel's damage");

        for (var index = 0; index < 30 && bot.Waypoints.Count == 0; index++) runtime.Tick(step);
        TestAssert.True(bot.Waypoints.Count > 0, "after the channel the kill task's order walks the bot to its minion again");
    }

    // Batch 187: NativeCanUseSpell returns the spellbook's can-use verdict, not a bare 1 (research patch126-bot-scripts.md 80-88: 2 not
    // learned, 3 cast bit or owner refusal, 4 cooldown, 5 mana). The shipped scripts test only == 0, so no decision changes.
    [Check("runtime.bots-can-use-spell-codes-follow-spellbook")]
    public static void NonzeroVerdictsCarryTheSpellbookCode()
    {
        var (runtime, bot, _, _) = ArmedCastBot(new SlotScript(1), [0, 1, 0, 0], 400.0);
        var native = typeof(BotController).GetMethod("NativeCanUseSpell",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        double Verdict(int slot) => (double)native.Invoke(runtime.Bots!, [bot, (double)slot])!;
        TestAssert.Equal(0.0, Verdict(1), "W is learned, ready and affordable");
        TestAssert.Equal(2.0, Verdict(0), "Q is not learned: code 2");
        bot.Mana = 0.0;
        TestAssert.Equal(5.0, Verdict(1), "W costs mana the bot does not have: code 5");
    }

    // A slot that CanUseSpell refuses (cooldown, silence, rank) is still skipped: no cast and no
    // approach. The control arm shows the same bot casts at once when nothing is wrong.
    [Check("runtime.bots-cast-unusable-slot-is-still-skipped")]
    public static void CooldownSilenceManaAndRankStillSkipTheSlot()
    {
        var (control, controlBot, _, controlState) = ArmedCastBot(new SlotScript(1), [0, 1, 0, 0], 400.0);
        Decide(control, 1.25);
        TestAssert.True(controlState.Active is { Kind: BotTaskKind.CastSpell } &&
                        controlBot.MatchStatistics.SpellCasts[1] == 1,
            "control: an in-range usable W is cast by BeginTask");

        var arms = new (string Name, int[] Ranks, Action<Entity> Arrange)[]
        {
            ("cooldown", [0, 1, 0, 0], bot => bot.SpellCooldowns[1] = 5.0),
            ("silence", [0, 1, 0, 0], bot => bot.Buffs.Add(new BuffState("BotFixtureSilence",
                new BuffPayload { Silence = true }, BuffType.Silence, expiresAt: 100.0))),
            ("rank", [0, 0, 0, 0], _ => { }),
        };
        foreach (var arm in arms)
        {
            var (runtime, bot, _, state) = ArmedCastBot(new SlotScript(1), arm.Ranks, 400.0, arm.Arrange);
            Decide(runtime, 1.25);
            TestAssert.True(state.Active is { Kind: BotTaskKind.CastSpell },
                $"{arm.Name}: the cast task still begins (its priority only needs a target and mana)");
            TestAssert.True(bot.MatchStatistics.SpellCasts.Sum() == 0 && bot.PendingCast is null &&
                            bot.Waypoints.Count == 0,
                $"{arm.Name}: the refused slot is neither cast nor approached");
        }

        // The mana arm cannot begin the cast task at all: TaskCastSpell proto 0.0 makes the
        // priority the mana fraction itself (0007-0015), so an empty pool leaves the task under
        // the 0.45 of the minion kill task and BeginTask never runs.
        var (dry, dryBot, _, dryState) = ArmedCastBot(new SlotScript(1), [0, 1, 0, 0], 400.0,
            bot => bot.Mana = 1.0);
        Decide(dry, 1.25);
        TestAssert.Near(1.0 / dryBot.MaxMana * 0.48,
            TaskOf(dryState, BotTaskKind.CastSpell).Priority, 1e-12,
            "TaskCastSpell proto 0.0: an empty pool gives the task almost no priority");
        TestAssert.True(dryState.Active is not { Kind: BotTaskKind.CastSpell },
            "an empty pool never starts the cast task");
        TestAssert.Equal(0, dryBot.MatchStatistics.SpellCasts.Sum(),
            "and so nothing is cast or approached");
    }

    // NOT RETAIL, owner decision 18 October 2026: the 15 retail bots without a champion TaskCastSpell (retail: the generic
    // script casts nothing) aim every slot from the spell's own data. The expected aim of each of their 60 spells.
    [Check("runtime.bots-retail-bots-without-a-cast-script-aim-from-spell-data")]
    public static void RetailBotsWithoutACastScriptAimEverySpellFromItsData()
    {
        var expected = new Dictionary<string, BotAim[]>
        {
            ["Alistar"] = [BotAim.SelfArea, BotAim.EnemyUnit, BotAim.Self, BotAim.Self],
            ["Ashe"] = [BotAim.Self, BotAim.Cone, BotAim.AllyPoint, BotAim.EnemyPoint],
            ["Chogath"] = [BotAim.EnemyPoint, BotAim.Cone, BotAim.Self, BotAim.EnemyUnit],
            ["Malphite"] = [BotAim.EnemyUnit, BotAim.Self, BotAim.SelfArea, BotAim.EnemyPoint],
            ["Maokai"] = [BotAim.EnemyPoint, BotAim.EnemyUnit, BotAim.AllyPoint, BotAim.AllyPoint],
            ["MasterYi"] = [BotAim.EnemyUnit, BotAim.Self, BotAim.Self, BotAim.Self],
            ["MissFortune"] = [BotAim.EnemyUnit, BotAim.Self, BotAim.EnemyPoint, BotAim.Cone],
            ["Nasus"] = [BotAim.Self, BotAim.EnemyUnit, BotAim.EnemyPoint, BotAim.Self],
            ["Nunu"] = [BotAim.EnemyUnit, BotAim.AllyUnit, BotAim.EnemyUnit, BotAim.Self],
            ["Renekton"] = [BotAim.SelfArea, BotAim.Self, BotAim.EnemyPoint, BotAim.Self],
            ["Ryze"] = [BotAim.EnemyUnit, BotAim.EnemyUnit, BotAim.EnemyUnit, BotAim.Self],
            ["Shen"] = [BotAim.EnemyUnit, BotAim.Self, BotAim.EnemyPoint, BotAim.AllyUnit],
            ["Taric"] = [BotAim.AllyUnit, BotAim.SelfArea, BotAim.EnemyUnit, BotAim.SelfArea],
            ["Trundle"] = [BotAim.Self, BotAim.EnemyPoint, BotAim.EnemyPoint, BotAim.EnemyUnit],
            ["Warwick"] = [BotAim.EnemyUnit, BotAim.Self, BotAim.Self, BotAim.EnemyUnit],
        };
        var data = GameData.Default;
        foreach (var (champion, aims) in expected)
        {
            TestAssert.True(data.ClientNamesBot(champion) &&
                    !File.Exists(data.Paths.Data($"Characters/{champion}/Scripts/TaskCastSpell.luaobj")),
                $"{champion} is a retail bot without a champion TaskCastSpell");
            var names = data.Champion(champion).SpellNames;
            for (var slot = 0; slot < 4; slot++)
                TestAssert.Equal(aims[slot], BotCast.Classify(data.Spell(names[slot])),
                    $"{champion} slot {slot} ({names[slot]}) aims by its data");
        }

        // Which of the playable champions cast by spell data: exactly the retail bots without their own script, i.e. the 15 above.
        // The list this line used to hold (Ryze, Taric, Alistar) was stale: Master Yi (batch 215), Miss Fortune (216) and Shen (224)
        // are playable retail bots without a TaskCastSpell too. The bot rule (BotController.CastModeOf) is unchanged.
        var runtime = Start(OneBot("Ryze", Team.Chaos), 391);
        foreach (var champion in ChampionCatalog.PlayableNames)
            TestAssert.Equal(expected.ContainsKey(champion), runtime.Bots!.CastsBySpellData(champion),
                $"{champion} casts by spell data only when it is a retail bot without a TaskCastSpell");
    }

    // NOT RETAIL, owner decision 22 October 2026: retail 2011 bots only some champions, and a
    // champion with no own TaskCastSpell.luaobj runs the shared DATA template whose BeginTask is a
    // bare RETURN — it casts nothing and is not a bot at all. Every roster champion is a bot here:
    // the shipped Bot.luaobj drives it, and the per-champion data the shipped files do not carry is
    // reconstructed (BotController.NativeReconstructModuleData) and marked NOT RETAIL there. There
    // is no C# fallback: a champion either runs its own shipped template or the reconstruction.
    [Check("runtime.bots-every-roster-champion-is-a-bot")]
    public static void EveryRosterChampionIsABotDrivenByItsCastTemplate()
    {
        var data = GameData.Default;
        var roster = ChampionCatalog.PlayableNames;
        // The only per-champion data the scripts read is Characters/<Name>/Scripts/TaskCastSpell.luaobj;
        // the Bot/Task scripts themselves are shared (DATA/Scripts). Every champion the shipped files
        // cover runs that file, and every other one is the reconstruction's.
        var withTemplate = roster.Where(champion =>
            File.Exists(data.Paths.Data($"Characters/{champion}/Scripts/TaskCastSpell.luaobj"))).ToArray();
        TestAssert.True(withTemplate.Length > 0 && withTemplate.Length < roster.Count,
            $"the roster splits into champions with their own TaskCastSpell ({withTemplate.Length}) and the rest");
        TestAssert.True(withTemplate.Contains("Annie"),
            "Annie ships the template the reconstruction is modelled on");
        TestAssert.False(withTemplate.Contains("Janna"),
            "Janna has no own template, so it is the reconstruction's");

        // Five champions at a time: Map1 supports at most five heroes per team, and the human
        // takes the one Order seat. Each gets a minion to hold as its target and a full pool and
        // spellbook, so its cast template's BeginTask draws CanUseSpell on the first update. The
        // human seat is kept at its own fountain, out of every bot's HERO_ENGAGE_DIST, so the cast
        // task is not outranked by a kill-hero one.
        var draws = new Dictionary<string, int>();
        var casts = new Dictionary<string, int>();
        for (var offset = 0; offset < roster.Count; offset += 5)
        {
            var group = roster.Skip(offset).Take(5).ToArray();
            var seat = new List<SessionParticipantDefinition>
            {
                new(1, 0, "Human", "Annie", Team.Order),
            };
            for (var index = 0; index < group.Length; index++)
                seat.Add(new SessionParticipantDefinition((ulong)index + 2, (uint)index + 1,
                    $"Cover {offset + index + 1}", group[index], Team.Chaos, IsBot: true));
            var world = Start(seat, 601 + offset, new AnnieChecks.FixedRandomSource(0.5));
            TestAssert.Equal(group.Length, world.Bots!.ControlledBotCount,
                "every playable champion is a controlled bot");
            world.Bots.Tick(0.0);
            for (var index = 0; index < group.Length; index++)
            {
                var hero = world.Session.Participants[index + 1].Hero!;
                hero.SkillPoints = 0;
                hero.SpellRanks = [1, 1, 1, 1];
                hero.SpellCooldowns = [0.0, 0.0, 0.0, 0.0];
                hero.Mana = hero.MaxMana;
                hero.Gold = 0.0;
                hero.Inventory = Enumerable.Repeat<InventorySlot?>(
                    new InventorySlot(1001), Inventory.SlotCount).ToArray();
                hero.Position = new Position2(QuietMid.X + index * 400.0, QuietMid.Z);
                hero.Waypoints.Clear();
                var minion = AddBotMinionTarget(world, hero, 30.0);
                hero.AttackTarget = minion.NetId;
            }
            Decide(world, 1.25);
            for (var index = 0; index < group.Length; index++)
            {
                var hero = world.Session.Participants[index + 1].Hero!;
                var state = StateOf(world, hero);
                TestAssert.True(state.Tasks.Count > 0,
                    $"{hero.ChampionName}: Bot.luaobj built its task list");
                draws[hero.ChampionName] = state.Measure.CastDraws.Sum();
                casts[hero.ChampionName] = hero.MatchStatistics.SpellCasts.Sum();
            }
        }

        foreach (var champion in roster)
        {
            TestAssert.True(BotController.SupportedChampions.Contains(champion),
                $"{champion} is a supported bot champion");
            TestAssert.True(draws.TryGetValue(champion, out var count) && count > 0,
                $"{champion}: its cast template (own shipped script or the non-retail reconstruction) " +
                $"ran BeginTask and drew CanUseSpell ({count})");
        }
        TestAssert.True(casts.Values.Sum() > 0,
            "the templates reach ServerCastSpell: the roster casts");
    }

    // Annie's TaskCastSpell.BeginTask exit test (20 < i) follows the draw and i starts at 0, so a
    // bot with no castable slot draws 22 times (0030-0034; W24 record, 19 October 2026). Janna
    // runs the reconstruction of that same shape, whose draw is the match's IRandomSource, so the
    // draws are countable.
    [Check("runtime.bots-cast-task-draws-22-slots-when-none-is-usable")]
    public static void CastTaskDrawsTwentyTwoTimesWhenNoSlotIsUsable()
    {
        var draws = new CountingSlotSource();
        var (runtime, _, _, state) = ArmedCastBot(draws, [0, 0, 0, 0], 400.0);
        Decide(runtime, 1.25);
        TestAssert.True(state.Active is { Kind: BotTaskKind.CastSpell }, "the cast task begins");
        TestAssert.Equal(22, BotScript.CastAttempts, "the draw loop runs as often as the bytecode one");
        TestAssert.Equal(BotScript.CastAttempts, draws.SlotDraws, "an unlearned bot draws until the loop gives up");
        TestAssert.Equal(BotScript.CastAttempts, state.Measure.CastDraws.Sum(), "the match report counts every draw");
        TestAssert.Equal(BotScript.CastAttempts, state.Measure.CastGate["Q:Rank"], "each draw met the unlearned slot");
    }

    private sealed class CountingSlotSource : IRandomSource
    {
        public int SlotDraws { get; private set; }
        public double NextDouble() => 0.0;
        public int NextIndex(int count)
        {
            if (count == BotScript.CastSlotCount) SlotDraws++;
            return 0;
        }
    }

    [Check("runtime.bots-cast-task-ends-at-the-first-usable-slot")]
    public static void FirstUsableSlotEndsTheTaskEvenWhenItsCastFails()
    {
        // Annie's template ends at the first usable slot with a RETURN right after ServerCastSpell
        // (0026-0029), whatever that call achieves. The first arm is a refusal: a recalling bot's
        // cast order is turned away by IsActionLocked although CanUseSpell has no recall gate of
        // its own (SpellSystem.CanUseSpell: rank, cast bit and the cannot-cast states), so W is
        // drawn, allowed and refused — and the second draw (E) never happens. A protected target
        // cannot carry this arm: the engine drops an untargetable held target before the update
        // (LuaAiService.ReportLostTarget against CanAttackIgnoringRange), so GetTarget() would be
        // nil and TaskCastSpell proto 0.0 0016 would score 0.
        var (runtime, bot, target, state) = ArmedCastBot(new SlotScript(1, 2), [0, 1, 1, 0], 400.0, minionTarget: true);
        // A real Recall through the production summoner module (the windup, then the channel at the cast frame): the bot holds its orders
        // for as long as the recall runs (batch 179 review). Its gate is SpellSystem.RecallRemaining, not a field on the hero.
        ChampionLiveRegressionChecks.StartRecallWindup(runtime, bot);
        Decide(runtime, 1.25);
        TestAssert.True(state.Active is { Kind: BotTaskKind.CastSpell },
            "the cast task begins | " + Diag(runtime, bot, state) +
            $" ranks=[{string.Join(",", bot.SpellRanks)}] targetAlive={target.Alive} " +
            $"heroNet={bot.NetId} tgtNet={target.NetId} armedTarget={bot.AttackTarget}");
        TestAssert.True(runtime.Spells.CanUseSpell(bot, 1, 1.25).Allowed,
            "W passes CanUseSpell although its cast order will be refused");
        TestAssert.Equal(0, bot.MatchStatistics.SpellCasts.Sum(),
            "the task ended at W: the second pick (E) is never tried");

        var (control, controlBot, _, _) = ArmedCastBot(new SlotScript(2), [0, 1, 1, 0], 400.0);
        Decide(control, 1.25);
        TestAssert.Equal(1, controlBot.MatchStatistics.SpellCasts[2],
            "control: Eye of the Storm, picked first, is cast while nothing refuses the order");
    }

    [Check("spells-can-use-spell-has-no-range-or-target-test")]
    public static void CanUseSpellKeepsStateGatesAndDropsGeometryGates()
    {
        var roster = CurrentTen.Select((champion, index) =>
            new SessionParticipantDefinition(
                (ulong)index + 1, (uint)index, $"Seat {index + 1}", champion,
                index < 5 ? Team.Order : Team.Chaos, IsBot: index != 9)).ToArray();
        var runtime = Start(roster, 101);
        var heroes = runtime.Session.Participants.Select(seat => seat.Hero!).ToArray();

        // Every module checks its target and range after the state gates, so a hero that is ready
        // can use every slot without any target (Vayne's W is the passive that cannot be cast).
        foreach (var hero in heroes)
        {
            hero.SpellRanks = [1, 1, 1, 1];
            hero.SpellCooldowns = [0.0, 0.0, 0.0, 0.0];
            hero.Mana = hero.MaxMana;
            for (var slot = 0; slot < 4; slot++)
            {
                var verdict = runtime.Spells.CanUseSpell(hero, slot, 1.0);
                var passive = hero.ChampionName == "Vayne" && slot == 1;
                TestAssert.True(verdict.Allowed != passive,
                    $"{hero.ChampionName} slot {slot}: ready spells pass without a target ({verdict.Reason})");
            }
        }

        var annie = heroes.Single(hero => hero.ChampionName == "Annie");
        annie.Position = QuietMid;
        var far = new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion,
            Team.Chaos, new Position2(annie.Position.X + 900.0, annie.Position.Z))
        { Alive = true, Created = true, Visible = true, MaxHealth = 1000.0, Health = 1000.0, Radius = 35.0 };
        runtime.World.Register(far);
        var plain = runtime.Spells.CanCast(annie, 0, far.NetId, new Position3(far.Position.X, 0.0, far.Position.Z), 1.0);
        TestAssert.True(plain.Failure == SpellCastFailure.Range && runtime.Spells.CanUseSpell(annie, 0, 1.0).Allowed,
            "the range gate refuses a cast that CanUseSpell accepts");

        void Expect(string label, SpellCastFailure expected, Action arrange, Action restore)
        {
            arrange();
            var verdict = runtime.Spells.CanUseSpell(annie, 0, 1.0);
            restore();
            TestAssert.True(!verdict.Allowed && verdict.Failure == expected,
                $"{label}: expected {expected}, got {verdict.Failure}");
        }
        Expect("rank 0", SpellCastFailure.Rank, () => annie.SpellRanks[0] = 0, () => annie.SpellRanks[0] = 1);
        Expect("cooldown", SpellCastFailure.Cooldown, () => annie.SpellCooldowns[0] = 3.0,
            () => annie.SpellCooldowns[0] = 0.0);
        Expect("mana", SpellCastFailure.Mana, () => annie.Mana = 0.0, () => annie.Mana = annie.MaxMana);
        var silence = new BuffState("FixtureSilence", new BuffPayload { Silence = true }, BuffType.Silence,
            expiresAt: 100.0);
        Expect("silence", SpellCastFailure.Disabled, () => annie.Buffs.Add(silence), () => annie.Buffs.Remove(silence));
        var stun = new BuffState("FixtureStun", new BuffPayload(), BuffType.Stun, expiresAt: 100.0);
        Expect("stun", SpellCastFailure.Disabled, () => annie.Buffs.Add(stun), () => annie.Buffs.Remove(stun));
        Expect("dead", SpellCastFailure.Dead, () => annie.Alive = false, () => annie.Alive = true);
        TestAssert.True(runtime.Spells.CanUseSpell(annie, 4, 1.0).Failure == SpellCastFailure.Slot,
            "a slot beyond the four champion spells is refused");
    }

    [Check("runtime.bots-level-up-spends-points-in-r-w-e-q-order")]
    public static void LevelUpSpendsEveryPointInScriptOrder()
    {
        var runtime = Start(OneBot("Annie", Team.Chaos), 351);
        var bot = runtime.Session.Participants.Single(participant => participant.IsBot).Hero!;
        runtime.Bots!.Tick(0.0);
        Quiet(bot);
        Decide(runtime, 1.25); // initialises the script
        bot.SpellRanks = [0, 0, 0, 0];
        bot.Level = 6;
        bot.SkillPoints = 3;
        runtime.Bots.Tick(0.0);

        // UpgradeSpellsInOrder(4, 2, 3, 1): R first, then W (twice, level 3 gate), never E or Q here.
        TestAssert.Equal(0, bot.SkillPoints, "OnLevelUp spends every available point immediately");
        TestAssert.True(bot.SpellRanks.SequenceEqual(new[] { 0, 2, 0, 1 }),
            $"points go to the first legal slot in R, W, E, Q order: {string.Join(',', bot.SpellRanks)}");
    }

    [Check("runtime.bots-push-lane-spread-is-the-script-formula")]
    public static void PushLanePriorityCountsTeammatesOnTheSameLaneAndNothingElseSpreadsTheBots()
    {
        // The A8 audit (18 October 2026) deleted the server's home-lane rule: the only spread is the script's
        // 0.04 * (heroes - others on this lane) / heroes term, the random term and the active-lane bonus.
        var roster = CurrentTen.Select((champion, index) =>
            new SessionParticipantDefinition((ulong)index + 1, (uint)index,
                $"Lane {index + 1}", champion, index < 5 ? Team.Order : Team.Chaos,
                IsBot: index != 5)).ToArray();
        var runtime = Start(roster, 361, new AnnieChecks.FixedRandomSource(0.5));
        var seats = runtime.Session.Participants.Where(seat => seat.IsBot).ToArray();
        runtime.Bots!.Tick(0.0);
        foreach (var seat in seats) Quiet(seat.Hero!);
        var minions = new Dictionary<(Team, int), Entity>();
        foreach (var team in new[] { Team.Order, Team.Chaos })
            for (var lane = 0; lane < 3; lane++)
                minions[(team, lane)] = AddFriendlyMinion(runtime, team, lane,
                    RoutePoint(runtime, team, lane, 6));

        // Batch 61: a hero order seats the hero on the client's 2-unit lattice point (up to 1.4 units), so the scan distance is the
        // one before the decision: the last bot's own order only runs after its scan.
        var scanDistances = new Dictionary<(Team, int), double>();
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            var scanner = seats.Where(seat => seat.Team == team).ToArray()[^1].Hero!;
            for (var lane = 0; lane < 3; lane++)
                scanDistances[(team, lane)] = Math.Sqrt(DistanceSquared(scanner.Position, minions[(team, lane)].Position));
        }
        Decide(runtime, 1.25);
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            // Bots decide one after the other, so the last bot of a team sees every teammate's final task.
            var teamSeats = seats.Where(seat => seat.Team == team).ToArray();
            var last = teamSeats[^1].Hero!;
            var lastState = StateOf(runtime, last);
            TestAssert.True(teamSeats.All(seat => StateOf(runtime, seat.Hero!).Active is { Kind: BotTaskKind.PushLane }),
                $"{team}: every bot takes a lane to push");
            for (var lane = 0; lane < 3; lane++)
            {
                var others = teamSeats.Take(teamSeats.Length - 1).Count(seat =>
                    StateOf(runtime, seat.Hero!).Active is { Kind: BotTaskKind.PushLane } active && active.Lane == lane);
                var distance = scanDistances[(team, lane)];
                // TaskPushLane proto 0.1: 0.06 * (1 - d / D) + 0.24 + 0.04 * (5 - others) / 5,
                // plus 0.05 while this lane is the active one (not yet) and math.random() * 0.03.
                // The random term is the script's own math.random (LuaMathLibrary's static
                // generator), not the match's IRandomSource, so the expected value is the band
                // [base, base + 0.03] the drawn term can only fall in.
                var withoutRandom = 0.06 * (1.0 - distance / BotController.MaxTravelDistance) +
                    0.24 + 0.04 * (5 - others) / 5.0;
                var priority = TaskOf(lastState, BotTaskKind.PushLane, lane).Priority;
                TestAssert.True(priority >= withoutRandom - 1e-9 && priority <= withoutRandom + 0.03 + 1e-9,
                    $"{team} lane {lane}: {others} teammate(s) on it, five team heroes, no active-lane bonus yet: " +
                    $"{priority} is outside [{withoutRandom}, {withoutRandom + 0.03}]");
            }
        }
    }

    [Check("runtime.bots-retreat-regroup-and-heal-near-the-fountain")]
    public static void RetreatWeighsMissingHealthAndFinishesHealingNearTheRegroupPoint()
    {
        var runtime = Start(OneBot("Orianna", Team.Chaos), 371);
        var seat = runtime.Session.Participants.Single(participant => participant.IsBot);
        var bot = seat.Hero!;
        runtime.Bots!.Tick(0.0);
        Quiet(bot);
        bot.MaxHealth = 1000.0;
        var route = runtime.Waves!.LaneRoute(Team.Chaos, "L");
        var midLane = route[route.Count / 2];
        bot.Position = midLane;
        AddFriendlyMinion(runtime, Team.Chaos, 0, route[Math.Min(route.Count - 1, route.Count / 2 + 4)]);

        // Far from the regroup point a slightly injured bot keeps pushing.
        bot.Health = 900.0;
        Decide(runtime, 1.25);
        var state = StateOf(runtime, bot);
        TestAssert.True(state.Active is { Kind: BotTaskKind.PushLane },
            "10 % missing health far from the fountain scores below the push task");

        // A badly injured bot leaves for the fountain with one order.
        bot.Health = 300.0;
        var before = runtime.Bots.OrdersIssued(seat.PlayerId);
        Decide(runtime, 1.5);
        TestAssert.True(state.Active is { Kind: BotTaskKind.Retreat }, "70 % missing health retreats");
        TestAssert.Equal<Position2?>(ChaosFountain, runtime.Movement.RetainedHeroGoal(bot),
            "the retreat order goes to the fountain");
        var afterRetreat = runtime.Bots.OrdersIssued(seat.PlayerId);
        TestAssert.True(afterRetreat > before);
        Decide(runtime, 1.75);
        Decide(runtime, 2.0);
        TestAssert.Equal(afterRetreat, runtime.Bots.OrdersIssued(seat.PlayerId),
            "the retreat task orders its walk once, in BeginTask; its Tick is empty");

        // Healed, the bot returns to pushing.
        bot.Health = bot.MaxHealth;
        Decide(runtime, 2.25);
        TestAssert.Near(0.0, TaskOf(state, BotTaskKind.Retreat).Priority, 1e-12, "no missing health, no retreat");
        TestAssert.True(state.Active is { Kind: BotTaskKind.PushLane }, "a healthy bot resumes the lane");

        // Within a tenth of the travel scale of the fountain a bot that is only
        // slightly hurt still goes back and finishes healing.
        bot.Position = new Position2(ChaosFountain.X - 600.0, ChaosFountain.Z - 600.0);
        bot.Health = 950.0;
        Decide(runtime, 2.5);
        TestAssert.True(state.Active is { Kind: BotTaskKind.Retreat },
            "an injured bot near the regroup point counts as fully injured and stays to heal");
        TestAssert.True(TaskOf(state, BotTaskKind.Retreat).Priority > 0.95,
            "near the regroup point the retreat priority is close to 1");
        bot.Health = bot.MaxHealth;
        Decide(runtime, 2.75);
        TestAssert.Near(0.0, TaskOf(state, BotTaskKind.Retreat).Priority, 1e-12, "healed: no retreat priority");
    }

    // Batch 581. TaskRetreat orders its walk once, in BeginTask (proto 0.1), and its Tick is empty (proto 0.2). A retreat that
    // begins while the bot is stunned must still walk home once the stun ends, not leave it standing with Retreat active (seed
    // 9801: Ezreal stood 24 s at low health after OrianaStun).
    [Check("runtime.bots-retreat-ordered-under-a-stun-walks-when-it-ends")]
    public static void ARetreatThatBeginsUnderAStunWalksWhenTheStunEnds()
    {
        var runtime = Start(OneBot("Orianna", Team.Chaos), 583);
        var bot = runtime.Session.Participants.Single(participant => participant.IsBot).Hero!;
        runtime.Bots!.Tick(0.0);
        Quiet(bot);
        bot.MaxHealth = 1000.0;
        bot.Health = 900.0;
        var route = runtime.Waves!.LaneRoute(Team.Chaos, "L");
        bot.Position = route[route.Count / 2];
        AddFriendlyMinion(runtime, Team.Chaos, 0, route[Math.Min(route.Count - 1, route.Count / 2 + 4)]);
        Decide(runtime, 1.25);
        var state = StateOf(runtime, bot);
        TestAssert.True(state.Active is { Kind: BotTaskKind.PushLane }, "the bot pushes first");

        var stun = new BuffState("FixtureStun", new BuffPayload(), BuffType.Stun, expiresAt: 100.0);
        bot.Buffs.Add(stun);
        ClearBotOrders(bot);
        bot.Health = 300.0;
        Decide(runtime, 1.5);
        TestAssert.True(state.Active is { Kind: BotTaskKind.Retreat }, "70 % missing health retreats");
        TestAssert.True(runtime.Movement.RetainedHeroGoal(bot) is null, "a stunned bot takes no walk");

        bot.Buffs.Remove(stun);
        Decide(runtime, 1.75);
        Decide(runtime, 2.0);
        TestAssert.True(state.Active is { Kind: BotTaskKind.Retreat }, "the retreat is still the task");
        TestAssert.Equal<Position2?>(ChaosFountain, runtime.Movement.RetainedHeroGoal(bot),
            "once the stun ends the walk BeginTask ordered goes to the fountain");
    }

    [Check("runtime.bots-no-stall-or-idle-guards")]
    public static void AStalledTargetIsKeptAndAnIdleBotIsNotSentAlongItsLane()
    {
        // The scripts have no stall or idle handling (the A8 audit, 18 October 2026, deleted the server guards
        // that ignored an unreachable target for ten seconds and sent a bot idle for fifteen along its lane).
        var runtime = Start(OneBot("Orianna", Team.Chaos), 381);
        var seat = runtime.Session.Participants.Single(participant => participant.IsBot);
        var bot = seat.Hero!;
        runtime.Bots!.Tick(0.0);
        Quiet(bot);
        bot.Mana = 0.0;
        bot.Position = QuietMid;
        var minion = AddBotMinionTarget(runtime, bot, 800.0, 500.0);
        Decide(runtime, 1.25);
        // TaskKillMinion proto 0.4 walks at the target until it is inside MIN_FIGHT_DIST
        // (GetAttackRange() + 50, module proto 0), so at 800 units the engagement is the route.
        TestAssert.Equal(minion.NetId, ChosenOf(bot, minion), "the minion is engaged first");
        for (var step = 0; step < 18; step++)
        {
            // The movement layer never gets the bot closer: no route, out of range.
            bot.Waypoints.Clear();
            bot.Target = null;
            Decide(runtime, 1.5 + 0.25 * step);
        }
        TestAssert.True(StateOf(runtime, bot).Active is { Kind: BotTaskKind.KillMinion },
            "a stalled chase is not dropped after four seconds: the scripts never give up a target");
        TestAssert.Near(0.45, TaskOf(StateOf(runtime, bot), BotTaskKind.KillMinion).Priority, 1e-12,
            "TaskKillMinion proto 0.1 0003-0008: and the minion keeps its 0.45");

        // A bot with nothing to do is not the push task's: TaskPushLane scores 0 whenever
        // FindNearestFriendlyMinion finds none (proto 0.1 0046-0049 writes Priority 0 and returns;
        // that helper wants a friendly minion on the lane and more than MIN_FOLLOW_DIST_SQ away,
        // proto 0.0 0016-0027), and no other task has anything to score either. Bot.txt proto 0.6
        // then starts its search for the best task at tasks[#tasks] (0000-0003) and only replaces
        // it on a strictly greater priority (0041), so a tie goes to the last entry of the list
        // proto 0.4 builds — the last kill-hero task — which is begun and ticked (0084-0090) and
        // keeps chasing an enemy hero it cannot reach. The old C# port waited fifteen seconds and
        // then sent the idle bot down its lane; that guard is deleted, and the script has neither
        // an idle timer nor a home-lane rule: nothing ever picks the bot up and sends it along a
        // lane, so the whole match is idle in that sense and not one second of it is push lane.
        var idleRuntime = Start(OneBot("Orianna", Team.Chaos), 382);
        var idleSeat = idleRuntime.Session.Participants.Single(participant => participant.IsBot);
        var idleBot = idleSeat.Hero!;
        idleRuntime.Bots!.Tick(0.0);
        Quiet(idleBot);
        idleBot.Position = QuietMid;
        Decide(idleRuntime, 1.25);
        var idleState = StateOf(idleRuntime, idleBot);
        TestAssert.True(idleState.Active is { Kind: BotTaskKind.KillHero },
            "with every task at 0 the arbiter keeps the last entry of Bot.luaobj's list | " +
            Diag(idleRuntime, idleBot, idleState));
        Decide(idleRuntime, 16.5);
        Decide(idleRuntime, 60.0);
        TestAssert.True(idleState.Active is { Kind: BotTaskKind.KillHero },
            "no stall timer, no idle timer and no home-lane rule ever join in | " +
            Diag(idleRuntime, idleBot, idleState));
        TestAssert.Near(0.0, idleState.Measure.PushLaneSeconds.Sum(), 1e-9,
            "and no guard ever sends it along its lane");
    }

    // W5, 18 October 2026 (NOT RETAIL, bot only): retail leaves a hero pressed against another body next to its destination pressing for
    // good (the client's actor update never lets it give up within radius + 75 of its end), and the scripts only end such a walk through a task
    // change or StopMove near a minion; the controller ends it after two seconds of unbroken contact frames.
    // The walk measured is the retreat walk, not the chase the old C# fixture left running: TaskRetreat.BeginTask orders
    // its walk to the regroup point once and its Tick is a bare RETURN (TaskRetreat.txt proto 0.2), so nothing replaces the
    // pressed route while the controller counts. TaskKillHero's Tick re-issues SetStateAndCloseToTarget(HeroID) on every
    // 250 ms update (TaskKillHero.txt proto 0.2 0020-0023) and the arbiter keeps that task as the last entry of its list
    // whenever everything scores 0 (Bot.txt proto 0.6 0000-0003/0041), so a pressed chase route is replaced and dropped
    // Retail has no controller release for a pressed route: only the solver's give-up or a task's re-order changes it. Restated from the
    // old "released" expectation (owner decision, 25 October 2026: bot walks are retail).
    [Check("runtime.bots-pinned-route-next-to-its-end-is-kept")]
    public static void ABotPressedAgainstABodyNextToItsDestinationKeepsTheWalk()
    {
        var runtime = Start(OneBot("Orianna", Team.Chaos), 383);
        var seat = runtime.Session.Participants.Single(participant => participant.IsBot);
        var bot = seat.Hero!;
        runtime.Bots!.Tick(0.0);
        Quiet(bot);
        bot.MaxHealth = 1000.0;
        bot.Health = 300.0;
        bot.Mana = 0.0;
        var route = runtime.Waves!.LaneRoute(Team.Chaos, "L");
        bot.Position = route[route.Count / 2];
        Decide(runtime, 1.25);
        var state = StateOf(runtime, bot);
        TestAssert.True(state.Active is { Kind: BotTaskKind.Retreat },
            "the fixture is walking to the regroup point, whose Tick never re-orders | " +
            Diag(runtime, bot, state));

        // Pressed for two and a half seconds with the end 40 units away: nothing ends the walk, the route is kept.
        bot.Waypoints.Clear();
        bot.Waypoints.Add(new Position2(bot.Position.X + 40.0, bot.Position.Z));
        bot.RepathAttempts = 4;
        bot.StuckElapsed = 2.5;
        Decide(runtime, 1.5);
        TestAssert.Equal(1, bot.Waypoints.Count, "a bot pressed next to its end keeps its walk (retail never ends such a walk)");

        // The same contact with the end 600 units away is a crowd on the way: the solver's give-up decides, not the controller.
        bot.Waypoints.Clear();
        bot.Waypoints.Add(new Position2(bot.Position.X + 600.0, bot.Position.Z));
        bot.RepathAttempts = 4;
        bot.StuckElapsed = 2.5;
        Decide(runtime, 1.75);
        TestAssert.Equal(1, bot.Waypoints.Count, "a route whose end is far away is kept");

        // One second of contact is not a deadlock yet.
        bot.Waypoints.Clear();
        bot.Waypoints.Add(new Position2(bot.Position.X + 40.0, bot.Position.Z));
        bot.RepathAttempts = 4;
        bot.StuckElapsed = 1.0;
        Decide(runtime, 2.0);
        TestAssert.Equal(1, bot.Waypoints.Count, "a short contact keeps the route");
    }

    [Check("runtime.bots-no-order-spam")]
    public static void IdenticalOrdersAreNotReissued()
    {
        var runtime = Start(OneBot("Orianna", Team.Chaos), 391);
        var seat = runtime.Session.Participants.Single(participant => participant.IsBot);
        var bot = seat.Hero!;
        runtime.Bots!.Tick(0.0);
        Quiet(bot);
        var route = runtime.Waves!.LaneRoute(Team.Chaos, "L");
        bot.Position = route[route.Count / 2];
        AddFriendlyMinion(runtime, Team.Chaos, 0, route[route.Count / 2 + 6]);

        Decide(runtime, 1.25);
        var orders = runtime.Bots.OrdersIssued(seat.PlayerId);
        TestAssert.Equal(1, orders, "the push task orders one route to the minion");
        for (var step = 0; step < 24; step++) Decide(runtime, 1.5 + 0.25 * step);
        TestAssert.Equal(orders, runtime.Bots.OrdersIssued(seat.PlayerId),
            "24 further decisions with the route in flight issue no order");

        // A lost order is retried, but never faster than once a second.
        bot.Waypoints.Clear();
        bot.Target = null;
        Decide(runtime, 8.0);
        TestAssert.Equal(orders + 1, runtime.Bots.OrdersIssued(seat.PlayerId), "the dropped order is re-issued once");
        bot.Waypoints.Clear();
        bot.Target = null;
        Decide(runtime, 8.25);
        TestAssert.Equal(orders + 1, runtime.Bots.OrdersIssued(seat.PlayerId),
            "an identical destination is not ordered again within a second");
        Decide(runtime, 9.5);
        TestAssert.Equal(orders + 2, runtime.Bots.OrdersIssued(seat.PlayerId),
            "and it is retried once the second has passed");
    }

    // The gate e94294c3 wrote measured the C# port of the bot scripts and asserted a steady-state
    // task update allocated under 64 bytes. Batch 160 deleted that port: the decision half is the
    // shipped Bot.luaobj and its Task*.luaobj on the Lua AI host, and a scripted step allocates by
    // construction — every Lua call takes a LuaFrame and its register and result arrays (the
    // LuaFrame constructor and the CALL and multret paths of LuaInterpreter) and every
    // GetMinions/GetPos native builds a fresh LuaTable (LuaAiInstance's ObjectList/PointValue).
    // Making that free is a VM project, not this batch's subject. What survives the replacement is
    // the engine half: its measurement pass still must allocate nothing, and the scripted step's
    // cost must stay bounded and grow at most linearly in the bodies its tasks walk
    // (TaskKillMinion's FindNearLowestHPMinion scans every enemy body: TaskKillMinion.txt proto 0.3).
    // DIAGNOSTICS: retail ran these scripts inside the 2011 game server against its own budget; the
    // ceilings below are this host's, measured 22 October 2026 (839815 B per bot update with nine
    // bot seats and 240 lane minions in the world, 408370 B with the world empty).
    [Check("runtime.bots-steady-state-update-budget")]
    public static void SteadyStateUpdatesStayWithinTheScriptedStepBudget()
    {
        const int decisions = 40;
        const long ScriptStepCeiling = 1 << 20;
        const long ScanCeilingPerBody = 4096;

        static (long ScriptStep, long EnginePass) Measure(int botSeats, int minionsPerLane,
            out int updates)
        {
            var roster = new List<SessionParticipantDefinition>
            {
                new(1, 0, "Human", "Annie", Team.Order),
            };
            for (var index = 0; index < botSeats; index++)
                roster.Add(new SessionParticipantDefinition((ulong)index + 2, (uint)index + 1,
                    $"Alloc {index + 1}", CurrentTen[index % CurrentTen.Length],
                    index % 2 == 0 ? Team.Chaos : Team.Order, IsBot: true));
            var runtime = Start(roster, 421, new AnnieChecks.FixedRandomSource(0.5));
            var seats = runtime.Session.Participants.Where(seat => seat.IsBot).ToArray();
            runtime.Bots!.Tick(0.0);
            foreach (var seat in seats) Quiet(seat.Hero!);
            // A late-game population: up to 240 lane minions spread over both teams and all lanes.
            foreach (var team in new[] { Team.Order, Team.Chaos })
                for (var lane = 0; lane < 3; lane++)
                    for (var index = 0; index < minionsPerLane; index++)
                        AddFriendlyMinion(runtime, team, lane, RoutePoint(runtime, team, lane, 20 + index));

            for (var step = 0; step < 8; step++)
            {
                Decide(runtime, 1.25 + 0.25 * step);
                runtime.PlaytestDiagnostics!.Tick();
            }
            var scriptStep = 0L;
            var enginePass = 0L;
            for (var step = 0; step < decisions; step++)
            {
                runtime.Combat.RestoreClock(3.25 + 0.25 * step);
                var before = GC.GetAllocatedBytesForCurrentThread();
                runtime.Bots.Tick(0.25);
                scriptStep += GC.GetAllocatedBytesForCurrentThread() - before;
                before = GC.GetAllocatedBytesForCurrentThread();
                runtime.PlaytestDiagnostics!.Tick();
                enginePass += GC.GetAllocatedBytesForCurrentThread() - before;
            }
            updates = decisions * seats.Length;
            return (scriptStep, enginePass);
        }

        var (fullStep, fullEngine) = Measure(9, 40, out var updates);
        var (emptyStep, _) = Measure(9, 0, out _);
        TestAssert.True(fullEngine < 64L * decisions,
            $"the engine's measurement pass allocated {fullEngine} bytes over {decisions} ticks");
        TestAssert.True(fullStep / updates <= ScriptStepCeiling,
            $"a scripted bot update allocated {fullStep / updates} B with 240 minions in the world " +
            $"(ceiling {ScriptStepCeiling} B); with the world empty it is {emptyStep / updates} B");
        var perBody = (fullStep - emptyStep) / (240L * updates);
        TestAssert.True(perBody <= ScanCeilingPerBody,
            $"240 bodies cost {perBody} B per body per update (ceiling {ScanCeilingPerBody} B): " +
            "the tasks' world scans must stay linear in what they walk");
    }

    // TaskKillHero.AntiKiteTimer (proto 0.3) is a two-second repeating timer BeginTask arms
    // (proto 0.1, 0014-0020). It sets the module's ReducePriority while a moving bot's target
    // lost at most 10 health and clears it otherwise. TimerHackDelayedInit loads the module once
    // and setmetatable's every kill-hero task table onto it (Bot.txt 0153), so the latch is one
    // global per bot, shared by every kill-hero task.
    [Check("runtime.bots-anti-kite-latch-is-shared-and-temporary")]
    public static void AntiKiteLatchIsSharedAndTheTimerClearsItWhileTheChaseIsIdle()
    {
        // TaskKillHero.AntiKiteTimer proto 0.3 0004-0011: a moving bot whose target lost at most
        // 10 health sets the script-wide latch.
        var runtime = Start(OneBot("Orianna", Team.Chaos), 221);
        var bot = runtime.Session.Participants.Single(seat => seat.IsBot).Hero!;
        runtime.Bots!.Tick(0.0);
        Quiet(bot);
        bot.Mana = 0.0;
        bot.Position = QuietMid;
        var target = AddBotTarget(runtime, bot, 800.0);
        var minion = AddBotMinionTarget(runtime, bot, 30.0);

        Decide(runtime, 1.25);
        var state = StateOf(runtime, bot);
        TestAssert.True(state.Active is { Kind: BotTaskKind.KillHero },
            "an engaged hero (0.5) outranks a minion (0.45)");
        TestAssert.Equal(target.NetId, bot.AttackTarget ?? 0u, "the bot starts a pursuit");
        TestAssert.True(bot.Waypoints.Count > 0, "the pursuit is moving, so anti-kite can apply");
        target.Health -= 10.0;

        Decide(runtime, 3.25);
        TestAssert.True(state.ReducePriority, "a moving pursuit that loses exactly ten health sets the latch");
        TestAssert.Equal(1, state.Measure.AntiKiteLatches, "and the latch is counted once");
        // AntiKiteTimer and TimerUpdate are the two timers of the same step, and the latch it
        // writes is only read by the next scoring pass (Bot.txt proto 0.6), so the switch to the
        // minion task lands on the following 250 ms update.
        Decide(runtime, 3.5);
        TestAssert.True(state.Active is { Kind: BotTaskKind.KillMinion },
            "every hero task now scores 0, so the minion task takes over");
        TestAssert.Equal(minion.NetId, bot.AttackTarget ?? 0u, "and the attack is retargeted to it");

        // The latch is the module's global, so it outlives the kill-hero task that set it. It
        // stays set: Bot.luaobj's own AntiKiteTimer handler (proto 0.5, 0000-0012) forwards to
        // the task only while activeTask.HeroID is set, and no kill-hero task is active any more,
        // so nothing ever clears it. Retail 2011 leaves a latched bot unable to fight a champion
        // for the rest of the match; the C# port used to clear it here (Non-retail, owner decision
        // 18 October 2026) and that override is gone with the port (owner, 22 October 2026).
        minion.Alive = false;
        Decide(runtime, 3.75);
        Decide(runtime, 4.0);
        TestAssert.True(state.ReducePriority, "the latch outlives the hero task");
        TestAssert.True(state.Tasks.Where(task => task.Kind == BotTaskKind.KillHero).All(task => task.Priority == 0.0),
            "a latched bot scores every hero at 0 even at 800 units");
        TestAssert.True(bot.AttackTarget is null, "the dead minion's order is dropped and the hero is not chased");

        // Two further timer periods change nothing: the forward is gated on activeTask.HeroID.
        Decide(runtime, 5.25);
        Decide(runtime, 7.25);
        TestAssert.True(state.ReducePriority,
            "Bot.luaobj proto 0.5 0000-0012: with no kill-hero task active the timer forwards nothing");
        TestAssert.Equal(1, state.Measure.AntiKiteLatches);
        TestAssert.True(state.Active is not { Kind: BotTaskKind.KillHero },
            "the latched bot never fights the champion again");

        // TaskKillHero proto 0.3 0004-0005: a loss above 10 health is outside the shipped
        // threshold and clears the latch instead of setting it.
        var largeLoss = Start(OneBot("Orianna", Team.Chaos), 231);
        var largeLossBot = largeLoss.Session.Participants.Single(seat => seat.IsBot).Hero!;
        largeLoss.Bots!.Tick(0.0);
        Quiet(largeLossBot);
        largeLossBot.Mana = 0.0;
        largeLossBot.Position = QuietMid;
        var largeLossTarget = AddBotTarget(largeLoss, largeLossBot, 800.0);
        Decide(largeLoss, 1.25);
        largeLossTarget.Health -= 11.0;
        Decide(largeLoss, 3.25);
        TestAssert.False(StateOf(largeLoss, largeLossBot).ReducePriority, "eleven health lost clears the latch");
        TestAssert.Equal(largeLossTarget.NetId, largeLossBot.AttackTarget ?? 0u,
            "the pursuit of a target that is being hurt continues");

        // TaskKillHero proto 0.3 0006-0011: the set branch is behind IsMoving(), so a stationary
        // attack takes the clear branch instead.
        var stationary = Start(OneBot("Orianna", Team.Chaos), 241);
        var stationaryBot = stationary.Session.Participants.Single(seat => seat.IsBot).Hero!;
        stationary.Bots!.Tick(0.0);
        Quiet(stationaryBot);
        stationaryBot.Mana = 0.0;
        stationaryBot.Position = QuietMid;
        var stationaryTarget = AddBotTarget(stationary, stationaryBot, 30.0);
        Decide(stationary, 1.25);
        TestAssert.Equal(0, stationaryBot.Waypoints.Count, "an in-range attack has no movement route");
        stationaryTarget.Health -= 10.0;
        Decide(stationary, 3.25);
        TestAssert.False(StateOf(stationary, stationaryBot).ReducePriority,
            "a bot that is not moving never sets the latch");
        TestAssert.Equal(stationaryTarget.NetId, stationaryBot.AttackTarget ?? 0u);

        // Bot.luaobj proto 0.5 0000-0012: the timer forwards to the active task only while it
        // carries a HeroID. A kill-hero task begins first (arming the timer), then a minion task
        // is the active one when the timer fires twice.
        var guarded = Start(OneBot("Orianna", Team.Chaos), 251);
        var guardedBot = guarded.Session.Participants.Single(seat => seat.IsBot).Hero!;
        guarded.Bots!.Tick(0.0);
        Quiet(guardedBot);
        guardedBot.Mana = 0.0;
        guardedBot.Position = QuietMid;
        var guardedHero = AddBotTarget(guarded, guardedBot, 800.0);
        Decide(guarded, 1.25);
        var guardedState = StateOf(guarded, guardedBot);
        TestAssert.True(guardedState.Active is { Kind: BotTaskKind.KillHero },
            "the kill-hero task begins first and arms AntiKiteTimer");
        guardedHero.Position = new Position2(QuietMid.X + 3000.0, QuietMid.Z);
        guardedBot.Waypoints.Clear();
        guardedBot.AttackTarget = null;
        AddBotMinionTarget(guarded, guardedBot, 600.0);
        for (var step = 0; step < 10; step++) Decide(guarded, 1.5 + 0.25 * step);
        TestAssert.True(guardedState.Active is { Kind: BotTaskKind.KillMinion });
        TestAssert.False(guardedState.ReducePriority,
            "Bot.luaobj proto 0.5: with a minion task active the timer never sets the latch");
        TestAssert.Equal(0, guardedState.Measure.AntiKiteLatches);
    }

    // Batch 581. TaskKillHero's BeginTask re-arms AntiKiteTimer (proto 0.1 0010-0014) from inside Bot.luaobj's TimerUpdate.
    // A re-engagement that lands on the old timer's fire step must not let the old timer fire: it would compare the
    // TargetHealth BeginTask has just read with the same health, and a bot closing on its target latched ReducePriority for
    // the rest of the match (seed 9801 on master: Nidalee at 132.27 s and Janna at 438.27 s, each on a re-engagement step).
    [Check("runtime.bots-kill-hero-re-engagement-re-arms-the-anti-kite-timer")]
    public static void AReEngagementOnTheOldTimersStepDoesNotLatch()
    {
        var runtime = Start(OneBot("Orianna", Team.Chaos), 581);
        var bot = runtime.Session.Participants.Single(seat => seat.IsBot).Hero!;
        runtime.Bots!.Tick(0.0);
        Quiet(bot);
        bot.Mana = 0.0;
        bot.Position = QuietMid;
        var hero = AddBotTarget(runtime, bot, 800.0);
        AddBotMinionTarget(runtime, bot, 30.0);

        Decide(runtime, 1.25);
        var state = StateOf(runtime, bot);
        TestAssert.True(state.Active is { Kind: BotTaskKind.KillHero },
            "the hero task begins at 1.25 s and arms AntiKiteTimer for 3.25 s");

        hero.Position = new Position2(bot.Position.X + 3000.0, bot.Position.Z);
        Decide(runtime, 2.0);
        TestAssert.True(state.Active is { Kind: BotTaskKind.KillMinion },
            "with the hero beyond twice the engage distance the minion task takes over");

        Decide(runtime, 3.0);
        hero.Position = new Position2(bot.Position.X + 800.0, bot.Position.Z);
        Decide(runtime, 3.25);
        TestAssert.True(state.Active is { Kind: BotTaskKind.KillHero },
            "the hero is back inside the engage distance on the old timer's step");
        TestAssert.True(bot.Waypoints.Count > 0, "the bot is closing on it, so a stale fire would latch");
        TestAssert.False(state.ReducePriority,
            "the replaced timer does not fire: BeginTask's InitTimer re-armed AntiKiteTimer for 5.25 s");
        TestAssert.Equal(0, state.Measure.AntiKiteLatches);
    }

    // Batch 581. TaskPushLane walks with SetStateAndMove(AI_SOFTATTACK, ...) (proto 0.2 0004-0009); TaskKillTower's BeginTask
    // sets no state (proto 0.2) and its Tick acts only on its own AI_FOLLOW 1 and AI_ATTACK 0 (proto 0.4 0014-0047). A bot
    // that reaches an enemy tower behind its push must still close on it and swing, not stand beside it.
    [Check("runtime.bots-kill-tower-after-push-lane-attacks-the-tower")]
    public static void ATowerTaskThatFollowsThePushTaskAttacksTheTower()
    {
        var runtime = Start(OneBot("Orianna", Team.Chaos), 582);
        var bot = runtime.Session.Participants.Single(seat => seat.IsBot).Hero!;
        runtime.Bots!.Tick(0.0);
        Quiet(bot);
        bot.Mana = 0.0;
        bot.Position = QuietMid;
        var turret = runtime.World.Entities.Values.Single(entity => entity.Name == "Turret_T1_C_05");
        TestAssert.True(runtime.Combat.IsTargetable(turret, bot), "the outer mid turret is attackable");
        TestAssert.True(DistanceSquared(QuietMid, turret.Position) > Math.Pow(bot.AttackRange + 800.0, 2),
            "the push starts outside TaskKillTower's attack range + 800");
        var friend = AddFriendlyMinion(runtime, Team.Chaos, 1, new Position2(QuietMid.X - 250.0, QuietMid.Z - 170.0));

        Decide(runtime, 1.25);
        var state = StateOf(runtime, bot);
        TestAssert.True(state.Active is { Kind: BotTaskKind.PushLane, Lane: 1 },
            "the bot follows its friendly mid minion (TaskPushLane BeginTask, AI_SOFTATTACK)");

        var toCenter = new Position2(QuietMid.X - turret.Position.X, QuietMid.Z - turret.Position.Z);
        var length = Math.Sqrt(toCenter.X * toCenter.X + toCenter.Z * toCenter.Z);
        friend.Alive = false;
        ClearBotOrders(bot);
        bot.Position = new Position2(turret.Position.X + toCenter.X / length * 650.0,
            turret.Position.Z + toCenter.Z / length * 650.0);
        Decide(runtime, 1.5);
        TestAssert.True(state.Active is { Kind: BotTaskKind.KillTower },
            "with the friendly minion gone the tower in reach is the task");
        Decide(runtime, 2.0);
        TestAssert.Equal(turret.NetId, ChosenOf(bot, turret),
            "TaskKillTower's Tick closes on the tower and attacks it instead of standing still");
    }

    // TaskKillHero.UpdatePriority (proto 0.0) tests ReducePriority and IsDead(self.HeroID) and
    // nothing else: 0006-0030 never ask whether the hero can be seen. A hidden hero therefore
    // keeps its 0.5; the engine's own DropLostTarget still drops the pursuit, since the task
    // scripts have no sight gate to drop it for them.
    [Check("runtime.bots-kill-hero-hidden-target-is-not-chased")]
    public static void AHiddenHeroKeepsItsPriorityButItsPursuitIsDropped()
    {
        var runtime = Start(OneBot("Orianna", Team.Chaos), 261);
        var bot = runtime.Session.Participants.Single(seat => seat.IsBot).Hero!;
        runtime.Bots!.Tick(0.0);
        Quiet(bot);
        bot.Mana = 0.0;
        bot.Position = QuietMid;
        var hero = AddBotTarget(runtime, bot, 400.0);
        Decide(runtime, 1.25);
        var state = StateOf(runtime, bot);
        TestAssert.Equal(hero.NetId, bot.AttackTarget ?? 0u, "a visible hero in range is engaged");
        var task = state.Tasks.Single(entry =>
            entry.Kind == BotTaskKind.KillHero && entry.SubjectNetId == hero.NetId);
        TestAssert.Near(0.5, task.Priority, 1e-12, "an engaged hero scores 0.5");

        hero.StealthHidden = true;
        TestAssert.False(runtime.Spells.CanSeeTarget(bot, hero), "the real team visibility hides the hero");
        Decide(runtime, 1.5);
        TestAssert.True(bot.AttackTarget is null && bot.HeroPursuit is null,
            "a hero the team no longer sees is dropped instead of chased to its last position");
        TestAssert.Near(0.5, task.Priority, 1e-12,
            "TaskKillHero proto 0.0 0006-0030 has no sight test, so the hidden hero still scores 0.5");
    }

    [Check("runtime.bots-current-ten-mixed-world-soak")]
    public static void CurrentTenRunNaturalWavesCampsAndOwnedPetInput()
    {
        const long connection = 101;
        const double horizon = 200.0;
        var runes = new RuneCatalog();
        var masteries = new MasteryCatalog();
        var firstRunes = RunePage.Create(runes, [5001, 5014, 5025],
            summonerLevel: 30);
        var firstMasteries = MasteryPage.Create(masteries,
            new Dictionary<int, int> { [103] = 3, [106] = 3, [136] = 1, [126] = 1,
                [102] = 4, [107] = 1 }, 30);
        var secondRunes = RunePage.Create(runes, [5002], summonerLevel: 30);
        var secondMasteries = MasteryPage.Create(masteries,
            new Dictionary<int, int> { [133] = 1, [110] = 3, [129] = 1,
                [109] = 3, [112] = 1 }, 30);
        var roster = CurrentTen.Select((champion, index) =>
            new SessionParticipantDefinition((ulong)index + 1, (uint)index,
                $"Soak {index + 1}", champion,
                index < 5 ? Team.Order : Team.Chaos,
                Runes: index == 0 ? firstRunes : index == 5 ? secondRunes : null,
                Masteries: index == 0 ? firstMasteries : index == 5 ? secondMasteries : null,
                IsBot: index != 5)).ToArray();
        using var runtime = Start(roster, connection,
            new AnnieChecks.FixedRandomSource(0.5));
        var heroes = runtime.Session.Participants.Select(seat => seat.Hero!).ToArray();
        var shaco = heroes[5];
        TestAssert.Equal(9, runtime.Bots!.ControlledBotCount,
            "the natural-world match retains nine server bots and human Shaco");
        var baseAnnie = runtime.GameData.StatsAtLevel(
            runtime.GameData.Champion("Annie"), heroes[0].Level);
        TestAssert.True(heroes[0].AttackDamage > baseAnnie.AttackDamage,
            "Annie's configured page changes an actual production stat");
        TestAssert.True(runtime.Session.Participants[5].Runes.WireIds.SequenceEqual(
                secondRunes.WireIds) &&
            runtime.Session.Participants[5].Masteries.TotalPoints ==
                secondMasteries.TotalPoints,
            "the human Shaco seat retains its second validated account page");

        foreach (var hero in heroes)
        {
            var experience = Math.Max(0.0,
                ProgressionRules.ExperienceThreshold(6) - hero.Experience);
            if (experience > 0.0)
                TestAssert.True(runtime.Combat.AwardHeroProgress(hero, experience: experience),
                    $"{hero.ChampionName} receives legal level-six progression");
            TestAssert.True(hero.Level >= 6,
                $"{hero.ChampionName} reaches the level-six spell gate");
        }
        TestAssert.True(runtime.Spells.RankUp(shaco, 3),
            "human Shaco learns Hallucinate through the spell service");
        shaco.Mana = shaco.MaxMana;
        shaco.Position = new Position2(7000, 7000);
        runtime.Delivery.Flush();
        for (var step = 0; step < 20; step++) runtime.Tick(0.1);

        TestAssert.True(SendMixedCast(runtime, connection, shaco, 3),
            "authenticated human Hallucinate cast is accepted");
        Entity? clone = null;
        for (var step = 0; step < 12 && clone is null; step++)
        {
            runtime.Tick(0.1);
            clone = runtime.World.Entities.Values.SingleOrDefault(entity =>
                entity.Kind == EntityKind.ShacoClone && entity.OwnerNetId == shaco.NetId);
        }
        TestAssert.True(clone is not null && clone.Alive && clone.PetState is not null,
            "Hallucinate creates the owned commandable clone in the production world");
        // Pet.lua holds the order state now, in its own AI state: an undelivered order is the script
        // running on its own (any state outside the hard ones), the hard move is AI_PET_HARDMOVE and
        // the hard return AI_PET_HARDRETURN — the states OnOrder leaves behind for wire 6 and 9.
        TestAssert.False(PetLuaAiEngine.IsHardState(runtime.Pets.StateOf(clone!) ?? PetLuaAiEngine.StateHalted),
            "new clone begins in autonomous follow");
        TestAssert.True(SendMixedOrder(runtime, connection, shaco, 2,
                new Position2(7150, 7000)),
            "ordinary authenticated right-click moves the hero");
        TestAssert.False(PetLuaAiEngine.IsHardState(runtime.Pets.StateOf(clone!) ?? PetLuaAiEngine.StateHalted),
            "ordinary hero right-click leaves clone autonomous");
        TestAssert.True(SendMixedOrder(runtime, connection, clone!, 6,
                new Position2(7040, 7130)),
            "explicit Alt pet move reaches the owned clone");
        TestAssert.Equal(PetLuaAiEngine.StatePetHardMove, runtime.Pets.StateOf(clone!)!.Value,
            "hard pet move replaces autonomous follow");
        TestAssert.True(SendMixedOrder(runtime, connection, clone!, 9, shaco.Position),
            "explicit Alt return restores follow");
        TestAssert.Equal(PetLuaAiEngine.StatePetHardReturn, runtime.Pets.StateOf(clone!)!.Value);
        TestAssert.True(shaco.MatchStatistics.SpellCasts[3] > 0,
            "human input committed a real champion cast");

        var minionWaves = new HashSet<(Team Team, int Wave)>();
        var neutralNames = new HashSet<string>(StringComparer.Ordinal);
        var botActed = false;
        var lifecycleViolations = 0;
        var nextSampleAt = runtime.Combat.CurrentTime + 1.0;
        for (var step = 0; step < 2100 &&
            runtime.Combat.CurrentTime < horizon - 1e-9; step++)
        {
            runtime.Tick(Math.Min(0.1, horizon - runtime.Combat.CurrentTime));
            foreach (var entity in runtime.World.Entities.Values)
            {
                if (entity.Kind == EntityKind.Minion)
                    minionWaves.Add((entity.Team, 1 + (int)Math.Round(
                        (entity.SpawnScheduledAt - 90.0) / 30.0)));
                else if (entity.Kind == EntityKind.Neutral)
                    neutralNames.Add(entity.Name);
            }
            botActed |= heroes.Where(hero => hero.NetId != shaco.NetId)
                .Any(hero => hero.MatchStatistics.SpellCasts.Sum() > 0 ||
                    hero.MatchStatistics.TotalDamageDealt > 0.0);
            if (runtime.Combat.CurrentTime + 1e-9 < nextSampleAt) continue;
            nextSampleAt += 1.0;
            lifecycleViolations += runtime.Lifecycle.Check(
                runtime.Combat.CurrentTime, force: true).Count;
            foreach (var entity in runtime.World.Entities.Values)
            {
                TestAssert.True(double.IsFinite(entity.Position.X) &&
                    double.IsFinite(entity.Position.Z) &&
                    double.IsFinite(entity.Health) && double.IsFinite(entity.Mana) &&
                    entity.Waypoints.All(point => double.IsFinite(point.X) &&
                        double.IsFinite(point.Z)),
                    $"finite state at {runtime.Combat.CurrentTime:F1}s: {entity.NetId}");
            }
        }
        foreach (var team in new[] { Team.Order, Team.Chaos })
            TestAssert.True(minionWaves.Contains((team, 1)) &&
                minionWaves.Contains((team, 2)),
                $"{team} spawned natural first and second lane waves");
        TestAssert.True(neutralNames.Any(name => name.StartsWith("wolf", StringComparison.OrdinalIgnoreCase)) &&
            neutralNames.Any(name => name.StartsWith("AncientGolem", StringComparison.Ordinal)),
            "ordinary and buff camps both spawned on the natural jungle schedule");
        TestAssert.True(botActed,
            "at least one bot reaches an actual spell or damage action in the mixed world");
        TestAssert.True(runtime.Combat.CurrentTime >= horizon - 1e-6,
            $"the bounded production loop reaches {horizon:F0} s: {runtime.Combat.CurrentTime:R}");
        TestAssert.True(heroes.Where(hero => hero.NetId != shaco.NetId)
                .All(hero => hero.SpellRanks.Sum() > 0),
            "all nine bots spend a legal skill point through the normal decisions");
        TestAssert.True(heroes.Where(hero => hero.NetId != shaco.NetId)
                .Any(hero => hero.Inventory.Any(item => item is not null)),
            "a real bot bought a basic shop item during the natural match");
        TestAssert.False(runtime.World.IsAlive(clone!.NetId),
            "the scripted clone has retired by the end of the natural-world soak");
        TestAssert.Equal(0, lifecycleViolations,
            "long-running production state has no lifecycle violations");
    }

    [Check("runtime.bots-idle-sampling-counts-completed-short-route")]
    public static void IdleSamplingCountsMovementWhoseRouteFinishedBetweenSamples()
    {
        using var runtime = Start(OneBot("LeeSin", Team.Order), 402);
        var hero = runtime.Session.Participants.Single(seat => seat.IsBot).Hero!;
        runtime.Bots!.Tick(0.0);
        Quiet(hero);
        hero.Position = new Position2(11559.559050364602, 1698.35248493423);
        hero.Speed = 375.0;
        Decide(runtime, 1.25);
        var previous = hero.Position;
        TestAssert.True(IsIdleSinceSample(hero, previous), "a stationary bot away from home is idle");

        runtime.Movement.SetMoveOrder(hero.NetId, new Position2(11907.0, 1613.0));
        TestAssert.True(hero.Waypoints.Count > 0, "the production planner accepts the short route");
        TestAssert.False(IsIdleSinceSample(hero, previous), "an unfinished route is activity");
        for (var step = 0; step < 30; step++) runtime.Movement.Tick(1.0 / 30.0);
        TestAssert.True(hero.Waypoints.Count == 0 && hero.Target is null && hero.AttackTarget is null,
            "the short route finishes before the next one-second sample");
        TestAssert.True(DistanceSquared(previous, hero.Position) > 300.0 * 300.0,
            "the bot made substantial progress between samples");
        TestAssert.False(IsIdleSinceSample(hero, previous), "completed movement resets the idle interval");
        TestAssert.True(IsIdleSinceSample(hero, hero.Position), "a subsequent motionless interval still counts as idle");
        hero.Position = OrderFountain;
        TestAssert.False(IsIdleSinceSample(hero, hero.Position), "standing at the fountain remains exempt");
    }

    [Check("runtime.bots-forty-minute-nine-bot-soak")]
    public static void NineBotsAndOneHumanHoldUpForAFortyMinuteGame()
    {
        // 40 simulated minutes if the host is fast enough, otherwise as long as
        // the wall-clock budget allows (never less than 5 minutes of game time).
        const double targetSeconds = 40.0 * 60.0;
        const double minimumSeconds = 300.0;
        const double wallBudgetSeconds = 90.0;
        // The server's fifteen-second idle fallback is gone (A8 audit): the scripts never move an idle bot, so a bot
        // waiting for a route, or standing beside a stationary friendly minion, can idle for minutes; the longest in
        // the seeded 60-minute matches was 246 s.
        const double idleLimitSeconds = 300.0;
        const int maximumOrdersPerMinute = 150;
        var roster = CurrentTen.Select((champion, index) =>
            new SessionParticipantDefinition((ulong)index + 1, (uint)index,
                $"Long {index + 1}", champion, index < 5 ? Team.Order : Team.Chaos,
                IsBot: index != 5)).ToArray();
        using var runtime = Start(roster, 401, new SeededRandomSource(20260929));
        // Keep the match alive if unattended lane combat reaches a Nexus.
        foreach (var nexus in runtime.World.Entities.Values.Where(entity => entity.Kind == EntityKind.Nexus))
            nexus.GodMode = true;
        var seats = runtime.Session.Participants.Where(seat => seat.IsBot).ToArray();
        TestAssert.Equal(9, seats.Length);
        var ordersAtWindowStart = seats.Select(_ => 0).ToArray();
        var damageAtWindowStart = seats.Select(_ => 0.0).ToArray();
        var deadInWindow = seats.Select(_ => false).ToArray();
        var idleSeconds = seats.Select(_ => 0.0).ToArray();
        var previousPositions = seats.Select(seat => seat.Hero!.Position).ToArray();
        var windowStart = 0.0;
        var nextSample = 1.0;
        var watch = Stopwatch.StartNew();
        const double step = 0.25;

        while (runtime.Combat.CurrentTime < targetSeconds - 1e-9 &&
               watch.Elapsed.TotalSeconds < wallBudgetSeconds && !runtime.Combat.GameOver)
        {
            runtime.Tick(step);
            var now = runtime.Combat.CurrentTime;
            if (now + 1e-9 < nextSample) continue;
            nextSample += 1.0;
            for (var index = 0; index < seats.Length; index++)
            {
                var hero = seats[index].Hero!;
                if (!hero.Alive)
                {
                    idleSeconds[index] = 0.0;
                    previousPositions[index] = hero.Position;
                    deadInWindow[index] = true;
                    continue;
                }
                // A short route can start and finish between samples.
                // Empty waypoints at both ends do not mean the bot stood still.
                var idle = IsIdleSinceSample(hero, previousPositions[index]);
                previousPositions[index] = hero.Position;
                idleSeconds[index] = idle ? idleSeconds[index] + 1.0 : 0.0;
                TestAssert.True(idleSeconds[index] <= idleLimitSeconds,
                    $"{hero.ChampionName} has been idle away from its fountain for {idleSeconds[index]:F0} s at {now:F0} s");
            }

            if (now + 1e-9 < windowStart + 60.0) continue;
            for (var index = 0; index < seats.Length; index++)
            {
                var hero = seats[index].Hero!;
                var orders = runtime.Bots!.OrdersIssued(seats[index].PlayerId) - ordersAtWindowStart[index];
                TestAssert.True(orders <= maximumOrdersPerMinute,
                    $"{hero.ChampionName} issued {orders} orders in the minute from {windowStart:F0} s");
                // A standing attack order keeps attacking without new orders, so
                // dealing damage counts as activity as well.
                var active = orders > 0 ||
                    hero.MatchStatistics.TotalDamageDealt > damageAtWindowStart[index];
                if (windowStart >= 120.0 && !deadInWindow[index])
                    TestAssert.True(active,
                        $"{hero.ChampionName} neither ordered nor attacked in the minute from {windowStart:F0} s");
                ordersAtWindowStart[index] = runtime.Bots!.OrdersIssued(seats[index].PlayerId);
                damageAtWindowStart[index] = hero.MatchStatistics.TotalDamageDealt;
                deadInWindow[index] = !hero.Alive;
            }
            windowStart += 60.0;
        }

        var reached = runtime.Combat.CurrentTime;
        TestAssert.True(reached >= minimumSeconds,
            $"the soak covered {reached:F0} simulated seconds in {watch.Elapsed.TotalSeconds:F0} s of wall time");
        TestAssert.True(runtime.Waves!.WaveIndex >= (int)((reached - 90.0) / 30.0) - 1 &&
                        runtime.Waves.WaveIndex >= 6,
            $"the game reached later waves (wave {runtime.Waves.WaveIndex} at {reached:F0} s)");
        TestAssert.False(runtime.Combat.GameOver, "the fixture keeps both Nexuses alive");
        TestAssert.True(seats.All(seat => seat.Hero!.SpellRanks.Sum() > 0),
            "every bot spent skill points");
        Console.WriteLine($"Soak covered {reached:F2} simulated seconds in {watch.Elapsed.TotalSeconds:F2} wall seconds.");
    }

    // ------------------------------------------------------------------
    // Existing scenarios, re-based on the script cadence
    // ------------------------------------------------------------------

    [Check("runtime.bots-current-ten-production-loop")]
    public static void CurrentTenUseProductionLoopAndLoadoutServices()
    {
        var roster = CurrentTen.Select((champion, index) =>
            new SessionParticipantDefinition(
                (ulong)index + 1, (uint)index, $"Seat {index + 1}", champion,
                index < 5 ? Team.Order : Team.Chaos, IsBot: index != 9)).ToArray();
        var runtime = Start(roster, 101);

        TestAssert.Equal(9, runtime.Bots!.ControlledBotCount,
            "the controller covers all nine bot seats in this current-ten permutation");
        var seats = runtime.Session.Participants.Where(seat => seat.IsBot).ToArray();
        var bots = seats.Select(seat => seat.Hero!).ToArray();
        TestAssert.True(bots.Any(hero => hero.ChampionName == "Annie"),
            "the current-ten permutation exercises Annie's bot seat");

        // The scripts wait one second, then update every 250 ms. Before the
        // first lane wave every bot buys and levels at the fountain and holds.
        for (var step = 0; step < 25; step++) runtime.Tick(0.1);
        TestAssert.True(bots.All(hero => hero.SpellRanks.Sum() > 0),
            "each bot spends a legal initial skill point through SpellSystem");
        TestAssert.True(bots.All(hero => Inventory.HasItem(hero.Inventory, 1001)),
            "each bot at the fountain buys the first list item through ShopService");
        TestAssert.True(bots.All(hero => hero.Waypoints.Count == 0),
            "with no lane wave yet the bots hold at the fountain, as the scripts do");

        // A friendly minion on its lane sends every bot out on maintained lane navigation.
        var initial = bots.ToDictionary(hero => hero.NetId, hero => hero.Position);
        foreach (var seat in seats)
        {
            var lane = Array.IndexOf(bots, seat.Hero!) % 3;
            AddFriendlyMinion(runtime, seat.Team, lane, RoutePoint(runtime, seat.Team, lane, 8));
        }
        for (var step = 0; step < 25; step++) runtime.Tick(0.1);
        TestAssert.True(bots.All(hero => hero.Waypoints.Count > 0 ||
                                          hero.Position != initial[hero.NetId]),
            "each current-ten bot follows its lane minion on maintained lane navigation");

        var retreating = bots[0];
        retreating.Position = MapDefinitions.LanePaths["C"][9];
        retreating.Health = retreating.MaxHealth * 0.20;
        runtime.Combat.RestoreClock(runtime.Combat.CurrentTime + 0.3);
        runtime.Bots.Tick(0.25);
        // Other departing bots can temporarily block this return. A waiting
        // route must still retain the fountain goal, never the old lane goal.
        // movement.hero-blocked-order-retries-after-wave-opens covers release.
        TestAssert.Equal<Position2?>(new Position2(
                MapDefinitions.HeroSpawn.X, MapDefinitions.HeroSpawn.Z),
            runtime.Movement.RetainedHeroGoal(retreating),
            "a retreat replaces the retained lane destination with the fountain");
        retreating.Health = retreating.MaxHealth;

        var fullInventory = bots[1];
        fullInventory.Inventory = Enumerable.Repeat<InventorySlot?>(
            new InventorySlot(1001), Inventory.SlotCount).ToArray();
        fullInventory.Gold = 10000.0;
        runtime.Combat.RestoreClock(runtime.Combat.CurrentTime + 0.3);
        runtime.Bots.Tick(0.25);
        TestAssert.Near(0.0, TaskOf(StateOf(runtime, fullInventory), BotTaskKind.Buy).Priority, 1e-12,
            "a full inventory removes the shop task's priority");
        TestAssert.False(StateOf(runtime, fullInventory).Active is { Kind: BotTaskKind.Buy },
            "a full inventory leaves the shop task in favor of lane movement");

        // Use the authoritative progression path to give every seat enough
        // levels for a legal spell decision.
        foreach (var bot in bots)
        {
            var experience = Math.Max(0.0,
                ProgressionRules.ExperienceThreshold(6) - bot.Experience);
            TestAssert.True(runtime.Combat.AwardHeroProgress(bot, experience: experience),
                $"authoritative XP grants a level-up path for {bot.ChampionName}");
        }

        // The targets cannot stand next to a shopping bot: a fountain shrine shoots every enemy
        // minion inside its 2250 range (the shipped shrine AttackRange, batch 204) for 1000 damage, so a fixture
        // placed on the fountain platform dies before the bot's next
        // update and the seat is left with nothing to cast at or swing at — the fixture note
        // runtime.bots-blitzcrank-production-seat already carries for one bot. One quiet point of
        // the left lane per seat, two path points apart (1700+ units, past the engage distance
        // GetAttackRange() + 500 of every champion here), keeps each target its own and every
        // enemy hero out of TaskKillHero proto 0.0's 0.5 band, which would outrank the 0.45 of
        // TaskKillMinion and the 0.48 ceiling of TaskCastSpell. Batch 213: Janna's point (18) stands 2084 from the Chaos
        // shrine, inside its 2250 range since batch 204; the shrine's 999-damage shot killed that fixture at 0.2 s, before
        // the bot acted (the same run with the shrine at its old 1250 passes). MoveOutOfShrineRange moves it out.
        var fixtures = new Dictionary<Entity, Entity>();
        for (var index = 0; index < bots.Length; index++)
        {
            var bot = bots[index];
            bot.Position = MapDefinitions.LanePaths["L"][2 * index + 2];
            ClearBotOrders(bot);
            fixtures[bot] = AddBotMinionTarget(runtime, bot);
            MoveOutOfShrineRange(runtime, fixtures[bot]);
        }

        for (var step = 0; step < 45; step++) runtime.Tick(0.1);

        TestAssert.True(bots.All(hero => hero.SpellRanks.Sum() > 1),
            "level-up spends every new point through SpellSystem in script order");
        TestAssert.True(bots.All(hero => hero.MatchStatistics.SpellCasts.Sum() > 0 ||
            hero.MatchStatistics.TotalDamageDealt > 0.0),
            "each current-ten bot casts or damages its legal visible target | " +
            string.Join("; ", bots.Select(hero =>
            {
                var state = StateOf(runtime, hero);
                var measure = state.Measure;
                var near = runtime.World.Entities.Values.Where(unit =>
                    unit.Alive && unit.NetId != hero.NetId &&
                    DistanceSquared(unit.Position, hero.Position) < 600.0 * 600.0).ToArray();
                var fixture = fixtures[hero];
                return $"{hero.ChampionName} casts={hero.MatchStatistics.SpellCasts.Sum()}" +
                       $" dmg={hero.MatchStatistics.TotalDamageDealt:0} draws={measure.CastDraws.Sum()}" +
                       $" ordered={measure.CastOrdered.Sum()} pos={hero.Position.X:0},{hero.Position.Z:0}" +
                       $" fixture alive={fixture.Alive} hp={fixture.Health:0} at={fixture.Position.X:0},{fixture.Position.Z:0}" +
                       $" nearMinions={near.Count(unit => unit.Kind == EntityKind.Minion && unit.Team != hero.Team)}" +
                       $" nearHeroes={near.Count(unit => unit.UnitType == UnitType.Hero && unit.Team != hero.Team)}" +
                       $" killHero={state.Tasks.Where(t => t.Kind == BotTaskKind.KillHero).Select(t => t.Priority).DefaultIfEmpty().Max()}" +
                       " " + Diag(runtime, hero, state);
            })));
        TestAssert.True(bots.All(hero => hero.AttackTarget is null ||
            runtime.World.Get(hero.AttackTarget.Value) is { } target &&
            runtime.Spells.CanSeeTarget(hero, target)),
            "bot attack targets remain visibility-safe");
    }

    [Check("runtime.bots-blitzcrank-production-seat")]
    public static void BlitzcrankIsCoveredWhenItsSeatIsTheBot()
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Human", "Annie", Team.Order),
            new SessionParticipantDefinition(2, 1, "Bot", "Blitzcrank", Team.Chaos, IsBot: true),
        };
        var runtime = Start(roster, 131);
        var bot = runtime.Session.Participants.Single(seat => seat.IsBot).Hero!;
        for (var step = 0; step < 25; step++) runtime.Tick(0.1);
        TestAssert.True(Inventory.HasItem(bot.Inventory, 1001) && bot.SpellRanks.Sum() > 0,
            "the Blitzcrank bot buys and levels through the production tasks");
        var initial = bot.Position;
        AddFriendlyMinion(runtime, Team.Chaos, 0, RoutePoint(runtime, Team.Chaos, 0, 8));
        for (var step = 0; step < 25; step++) runtime.Tick(0.1);
        TestAssert.True(bot.Waypoints.Count > 0 || bot.Position != initial,
            "the Blitzcrank bot uses the production movement task");
        // The bot is still inside its fountain shrine's range, and the shrine kills any
        // enemy minion with a 1000-damage shot within a fraction of a second, before a
        // melee attack can land. The combat part of the check runs on the open mid lane.
        bot.Position = QuietMid;
        ClearBotOrders(bot);
        AddBotMinionTarget(runtime, bot);

        for (var step = 0; step < 45; step++) runtime.Tick(0.1);

        TestAssert.True(bot.MatchStatistics.SpellCasts.Sum() > 0 ||
                        bot.MatchStatistics.TotalDamageDealt > 0.0,
            "the Blitzcrank bot reaches a legal cast or damage action");
    }

    [Check("runtime.bots-self-target-fallback")]
    public static void BotUsesAuthoritativeAllyFallbackForSupportSpell()
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Human", "Annie", Team.Order),
            new SessionParticipantDefinition(2, 1, "Bot", "Janna", Team.Chaos, IsBot: true),
        };
        var runtime = Start(roster, 151, new AnnieChecks.FixedRandomSource(0.5));
        var bot = runtime.Session.Participants.Single(seat => seat.IsBot).Hero!;
        runtime.Bots!.Tick(0.0);
        Quiet(bot);
        bot.SpellRanks = [0, 0, 1, 0];
        bot.SkillPoints = 0;
        bot.Mana = bot.MaxMana;
        AddBotMinionTarget(runtime, bot);

        Decide(runtime, 1.25);
        Decide(runtime, 1.5);

        TestAssert.True(bot.MatchStatistics.SpellCasts[2] > 0,
            "a bot retries an ally-targeted spell against itself after enemy rejection");
        TestAssert.True(bot.Buffs.Any(buff => buff.Name == "EyeOfTheStorm"),
            "the self-target fallback reaches Janna's authoritative shield path");
    }

    [Check("runtime.bots-pause-death-respawn-and-fog")]
    public static void BotStateFreezesAndResetsAcrossLifecycle()
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Human", "Annie", Team.Order),
            new SessionParticipantDefinition(2, 1, "Bot", "Orianna", Team.Chaos, IsBot: true),
        };
        var runtime = Start(roster, 201);
        var bot = runtime.Session.Participants.Single(seat => seat.IsBot).Hero!;
        for (var step = 0; step < 16; step++) runtime.Tick(0.1);

        var combatAtPause = runtime.Combat.CurrentTime;
        var positionAtPause = bot.Position;
        var ordersAtPause = runtime.Bots!.OrdersIssued(runtime.Session.Participants.Single(seat => seat.IsBot).PlayerId);
        TestAssert.True(runtime.Session.Receive(201, 1,
            NativeRequestWire.Encrypt(runtime.Cipher, PausePackets.Pause(0, 60).Plaintext.Span)).Count > 0);
        TestAssert.True(runtime.Session.Paused, "a production pause enters the session gate");
        runtime.Tick(1.0);
        TestAssert.Equal(combatAtPause, runtime.Combat.CurrentTime,
            "paused production ticks do not advance combat time");
        TestAssert.Equal(positionAtPause, bot.Position,
            "paused production ticks do not run bot movement");
        TestAssert.Equal(ordersAtPause,
            runtime.Bots.OrdersIssued(runtime.Session.Participants.Single(seat => seat.IsBot).PlayerId),
            "paused production ticks issue no bot order");

        runtime.Session.Receive(201, 1,
            NativeRequestWire.Encrypt(runtime.Cipher, PausePackets.Resume(uint.MaxValue, delayed: false).Plaintext.Span));
        runtime.World.Clock.SetVirtual(runtime.World.Clock.Monotonic() + 6.0);
        runtime.Session.RunSchedulers();
        TestAssert.False(runtime.Session.Paused, "the delayed resume reopens production ticks");

        // Leave both fountain true-sight providers before testing stealth. The
        // mid-lane center is outside the reviewed 1350-unit turret/fountain
        // detection radii, while the bot's own terrain vision still sees the
        // target before StealthHidden is latched.
        bot.Position = QuietMid;
        ClearBotOrders(bot);
        var hidden = new Entity(0x70000001, EntityKind.Hero, UnitType.Hero,
            Team.Order, new Position2(QuietMid.X + 80.0, QuietMid.Z))
        {
            ChampionName = "Annie", Alive = true, Created = true, Visible = true,
            MaxHealth = 1000.0, Health = 1000.0, AttackRange = 500.0,
        };
        runtime.World.Register(hidden);
        TestAssert.True(runtime.Spells.CanSeeTarget(bot, hidden),
            "the target starts visible to the bot team");
        bot.AttackTarget = hidden.NetId;
        bot.HeroPursuit = new HeroPursuitState(hidden.NetId, hidden.Position);
        hidden.StealthHidden = true;
        TestAssert.False(runtime.Spells.CanSeeTarget(bot, hidden),
            "the real team visibility path hides the stealthed target");
        runtime.Combat.RestoreClock(runtime.Combat.CurrentTime + 0.3);
        runtime.Bots!.Tick(0.25);
        TestAssert.True(bot.AttackTarget is null && bot.HeroPursuit is null,
            "a hidden lost target is cleared instead of chased by last-known position");

        var killer = runtime.Session.Participants.Single(seat => !seat.IsBot).Hero!;
        TestAssert.True(runtime.Combat.ApplyHit(killer.NetId, bot.NetId,
            bot.Health + 1.0, DamageType.True, DamageSourceKind.Spell),
            "bot enters the ordinary lethal-hit death path");
        TestAssert.False(bot.Alive);
        runtime.Bots.Tick(0.25);
        TestAssert.True(bot.Waypoints.Count == 0 && bot.AttackTarget is null &&
                        bot.HeroPursuit is null,
            "a dead bot clears its movement and attack state");
        TestAssert.True(StateOf(runtime, bot).Active is null, "a dead bot drops its active task");
        var respawnAt = bot.RespawnAt;
        runtime.Combat.RestoreClock(respawnAt + 0.1);
        TestAssert.True(runtime.Combat.ProcessRespawns() > 0,
            "bot respawns through the existing CombatService lifecycle");
        TestAssert.True(bot.Alive && bot.AttackTarget is null && bot.HeroPursuit is null,
            "respawn keeps the same seat and has no stale bot order");
        runtime.Tick(1.0);
        runtime.Tick(0.3);
        TestAssert.True(StateOf(runtime, bot).Active is not null,
            "the bot's task updates resume after respawn");
        TestAssert.True(bot.SpellRanks.Sum() > 0,
            "the existing bot seat resumes after respawn without a new peer");
    }

    [Check("runtime.bots-retreat-two-second-health-sample")]
    public static void BotRetreatUsesPerSeatTwoSecondHealthSample()
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Human", "Annie", Team.Order),
            new SessionParticipantDefinition(2, 1, "Exact", "Orianna", Team.Chaos,
                IsBot: true),
            new SessionParticipantDefinition(3, 2, "Over", "Vayne", Team.Chaos,
                IsBot: true),
        };
        var runtime = Start(roster, 251, new AnnieChecks.FixedRandomSource(0.5));
        var bots = runtime.Session.Participants.Where(seat => seat.IsBot)
            .Select(seat => seat.Hero!).ToArray();
        runtime.Bots!.Tick(0.0);
        foreach (var bot in bots)
        {
            Quiet(bot);
            // 90 % health keeps the ordinary missing-health priority well below
            // the push task, so only the >110 burst rule can force a retreat.
            bot.MaxHealth = 1000.0;
            bot.Health = 900.0;
        }
        var exact = bots[0];
        var over = bots[1];

        // Start the seats mid-lane, each with a friendly minion on its own home lane.
        var exactRoute = runtime.Waves!.LaneRoute(Team.Chaos, "L");
        var overRoute = runtime.Waves.LaneRoute(Team.Chaos, "C");
        exact.Position = exactRoute[exactRoute.Count / 2];
        over.Position = overRoute[overRoute.Count / 2];
        AddFriendlyMinion(runtime, Team.Chaos, 0, exactRoute[exactRoute.Count / 2 + 4]);
        AddFriendlyMinion(runtime, Team.Chaos, 1, overRoute[overRoute.Count / 2 + 4]);
        TestAssert.True(DistanceSquared(exact.Position, ChaosFountain) > 120.0 * 120.0 &&
                        DistanceSquared(over.Position, ChaosFountain) > 120.0 * 120.0,
            "the burst fixture starts away from the chaos fountain");

        // The first update at 1.25 s records the integer-second-1 baseline.
        Decide(runtime, 1.25);
        var baseline = 900.0;
        var routeCapture = new PacketCaptureTransport();
        runtime.Delivery.AddObserver(new ReplicationObserver(
            252, Team.Chaos, over.NetId, routeCapture));
        runtime.Delivery.Flush();

        // The source compares against the sample exactly two integer seconds
        // old and uses a strict >110 test. Keep the exact-boundary seat at
        // 110 and the second seat one point over it.
        exact.Health = baseline - 110.0;
        over.Health = baseline - 100.0;
        Decide(runtime, 2.25);

        // The shipped same-second history keeps its first value. A later
        // change in this same integer second must not overwrite the -100
        // sample with -120.
        over.Health = baseline - 120.0;
        Decide(runtime, 2.5);
        runtime.Delivery.Flush();

        var exactLaneRoute = exact.Waypoints.ToArray();
        routeCapture.Clear();
        Decide(runtime, 3.25);
        runtime.Delivery.Flush();
        TestAssert.True(exact.Waypoints.SequenceEqual(exactLaneRoute) && !HasFountainRoute(exact),
            "a loss of exactly 110 does not force the retreat task");
        TestAssert.True(HasFountainRoute(over),
            "a loss greater than 110 forces the fountain retreat task");
        var retreatWaypointPackets = routeCapture.Packets.Count(packet =>
            packet.Plaintext.Span.Length > 0 &&
            packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode &&
            packet.EntityReferences.Any(reference => reference.NetId == over.NetId));
        TestAssert.Equal(1, retreatWaypointPackets,
            "the first retreat decision emits one waypoint order");

        // Repeated decisions during the same source sample retain the active
        // route instead of clearing and re-emitting an equivalent order.
        routeCapture.Clear();
        Decide(runtime, 3.5);
        runtime.Delivery.Flush();
        var repeatedRetreatWaypointPackets = routeCapture.Packets.Count(packet =>
            packet.Plaintext.Span.Length > 0 &&
            packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode &&
            packet.EntityReferences.Any(reference => reference.NetId == over.NetId));
        TestAssert.Equal(0, repeatedRetreatWaypointPackets,
            "the active retreat does not churn its committed route");

        // At the next comparison key, the first sample from second 2 is still
        // authoritative: -100 versus -220 is another strict >110 escalation.
        over.Health = baseline - 220.0;
        routeCapture.Clear();
        Decide(runtime, 4.25);
        runtime.Delivery.Flush();
        TestAssert.True(HasFountainRoute(over),
            "the first same-second sample remains authoritative at expiry");

        // Restoring health clears the burst at the following sample key and
        // resumes the lane task; this is sample expiry, not a rolling window.
        over.Health = baseline;
        routeCapture.Clear();
        Decide(runtime, 5.25);
        runtime.Delivery.Flush();
        TestAssert.False(HasFountainRoute(over),
            "health recovery after the two-second sample resumes lane movement");

        // Death resets the per-seat history; respawn starts a new baseline and
        // does not replay the earlier retreat burst.
        var killer = runtime.Session.Participants.Single(seat => !seat.IsBot).Hero!;
        TestAssert.True(runtime.Combat.ApplyHit(killer.NetId, over.NetId,
            over.Health + 1.0, DamageType.True, DamageSourceKind.Spell),
            "the bot enters the ordinary lethal-hit path");
        TestAssert.False(over.Alive);
        runtime.Bots.Tick(0.25);
        var respawnAt = over.RespawnAt;
        runtime.Combat.RestoreClock(respawnAt + 0.1);
        TestAssert.True(runtime.Combat.ProcessRespawns() > 0,
            "the bot respawns through CombatService");
        runtime.Bots.Tick(0.25); // lifecycle reset; no decision yet
        runtime.Combat.RestoreClock(runtime.Combat.CurrentTime + 0.3);
        runtime.Bots.Tick(0.25);
        TestAssert.True(over.Alive && !HasFountainRoute(over),
            "respawn starts a fresh sample and resumes the lane task");
    }

    [Check("runtime.bots-defend-structure-and-push-lane")]
    public static void BotStructureDamageAndLanePushBoundaries()
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Human", "Annie", Team.Order),
            new SessionParticipantDefinition(2, 1, "Bot", "Orianna", Team.Chaos,
                IsBot: true),
        };
        var runtime = Start(roster, 271);
        var bot = runtime.Session.Participants.Single(seat => seat.IsBot).Hero!;
        var attacker = runtime.Session.Participants.Single(seat => !seat.IsBot).Hero!;
        var structureDefinition = MapDefinitions.Inhibitors.Single(definition =>
            definition.Name == "Barracks_T2_C1");
        var structure = runtime.World.Get(structureDefinition.NetId) ??
            throw new InvalidOperationException("production fixture did not create the inhibitor");
        // Keep the threshold probe on the known open floor used by the
        // production fixture. The inhibitor itself remains at its original
        // position for the defend-goal and later damage checks.
        var thresholdBotPosition = new Position2(9800.0, 10500.0);
        bot.Position = thresholdBotPosition;
        Quiet(bot);

        // This case isolates DefendStructure from higher-priority PushLane and
        // KillTower candidates. Friendly lane members or nearby enemy structures
        // created by the production fixture would otherwise mask the
        // structure-recency decision being checked below.
        foreach (var minion in runtime.World.Entities.Values.Where(entity =>
                     entity.Kind == EntityKind.Minion && entity.Team == bot.Team))
            minion.Alive = false;
        foreach (var enemyStructure in runtime.World.Entities.Values.Where(entity =>
                     entity.Team != bot.Team &&
                     entity.Kind is (EntityKind.Turret or EntityKind.Inhibitor or EntityKind.Nexus)))
            enemyStructure.Alive = false;

        // A real positive post-mitigation hit starts the 20-second defend
        // window. The bot then routes to its own damaged structure.
        runtime.Bots!.Tick(0.0);
        runtime.Combat.RestoreClock(0.5);
        var healthBefore = structure.Health;
        structure.LastDamageAt = double.NegativeInfinity;
        TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, structure.NetId,
            25.0, DamageType.Physical, DamageSourceKind.Basic),
            "a live structure accepts the fixture damage");
        TestAssert.True(structure.Health < healthBefore &&
                        double.IsFinite(structure.LastDamageAt),
            "positive structure damage records a finite damage timestamp");
        Decide(runtime, 1.25);
        var state = StateOf(runtime, bot);
        // The controller rebuilds the mirrored task list every 250 ms step, so the defend entry is
        // re-read whenever the clock has moved (a handle kept from an earlier step is frozen).
        BotTask Defend() => StateOf(runtime, bot).Tasks.Single(task =>
            task.Kind == BotTaskKind.Defend && task.SubjectNetId == structure.NetId);
        var defend = Defend();
        TestAssert.True(defend.Priority > 0.0 && state.Active is { Kind: BotTaskKind.Defend } active &&
                        active.SubjectNetId == structure.NetId,
            "the recently damaged friendly structure becomes the active defend task");
        TestAssert.True(bot.Waypoints.Count > 0 &&
                        DistanceSquared(bot.Waypoints[^1], structure.Position) <=
                        Math.Pow(structure.Radius + bot.Radius + 50.0, 2),
            $"recently damaged friendly structure becomes the bot defend goal: bot={bot.Position}, structure={structure.Position}, route={string.Join(';', bot.Waypoints)}");

        var defendOrigin = structure.Position;
        // Move this synthetic threshold target along the open south corridor;
        // diagonal placement intersects terrain/body detours unrelated to AI.
        var defendDirection = new Position2(0.0, -1.0);
        bot.Position = thresholdBotPosition;
        var defendTestOrigin = new Position2(
            bot.Position.X + defendDirection.X * 100.0,
            bot.Position.Z + defendDirection.Z * 100.0);
        var clientBounds = new NavigationBounds(runtime.Navigation.MinX,
            runtime.Navigation.MaxX, runtime.Navigation.MinZ,
            runtime.Navigation.MaxZ);
        var wireBotPosition = Geometry.ClientWaypoints([bot.Position],
            clientBounds)[0];
        TestAssert.True(runtime.Navigation.IsMovementWalkable(bot.Position) &&
                        runtime.Navigation.IsMovementWalkable(wireBotPosition),
            $"defend threshold starts from movement floor: bot={bot.Position}, wire={wireBotPosition}");
        structure.Position = defendTestOrigin;
        ClearBotOrders(bot);
        Decide(runtime, 1.5);
        TestAssert.Equal(0, bot.Waypoints.Count,
            "defend movement stays stopped inside the 150-unit structure radius");
        structure.Position = new Position2(defendTestOrigin.X + defendDirection.X * 150.0,
            defendTestOrigin.Z + defendDirection.Z * 150.0);
        Decide(runtime, 1.75);
        TestAssert.Equal(0, bot.Waypoints.Count,
            "a moving structure at 250 units remains stopped inside 750");
        structure.Position = new Position2(defendTestOrigin.X + defendDirection.X * 700.0,
            defendTestOrigin.Z + defendDirection.Z * 700.0);
        TestAssert.True(runtime.Navigation.IsMovementWalkable(structure.Position),
            "the resumed defend target remains on movement floor");
        Decide(runtime, 2.0);
        TestAssert.True(bot.Waypoints.Count > 1,
            "defend movement resumes after the 750-unit threshold");
        structure.Position = defendOrigin;

        // A shielded hit consumes shield only and must not refresh the defend
        // clock. The same applies to an invulnerable or dead structure.
        structure.LastDamageAt = double.NegativeInfinity;
        var shieldedHealth = structure.Health;
        structure.Buffs.Add(new BuffState("BotFixtureShield", new BuffPayload
        {
            DamageShield = 100.0,
        }, BuffType.CombatEnhancer, expiresAt: 20.0,
            casterNetId: attacker.NetId));
        runtime.Combat.RestoreClock(1.0);
        TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, structure.NetId,
            25.0, DamageType.Physical, DamageSourceKind.Basic),
            "a shielded structure still processes the hit");
        TestAssert.Near(shieldedHealth, structure.Health, 1e-9,
            "a fully shielded structure takes no health damage");
        TestAssert.True(double.IsNegativeInfinity(structure.LastDamageAt),
            "shield-only damage does not start the defend window");
        structure.Buffs.Clear();
        structure.Protected = true;
        runtime.Combat.RestoreClock(2.0);
        TestAssert.False(runtime.Combat.ApplyHit(attacker.NetId, structure.NetId,
            25.0, DamageType.Physical, DamageSourceKind.Basic),
            "an invulnerable structure rejects damage");
        TestAssert.True(double.IsNegativeInfinity(structure.LastDamageAt),
            "rejected invulnerable damage leaves the defend clock untouched");
        structure.Protected = false;

        // Outside the twenty-second window the task scores 0. (Its Tick still
        // belongs to the active task, as in the script, until another task wins.)
        structure.LastDamageAt = runtime.Combat.CurrentTime - 21.0;
        Decide(runtime, 3.0);
        TestAssert.Near(0.0, Defend().Priority, 1e-12,
            "a structure older than the 20-second window is not defended");

        // TaskDefendStructure proto 0.0 0000-0007 tests GetObject(self.StructureID) == nil and
        // nothing else: there is no IsDead in its scoring, and GetObject is a world lookup. A
        // destroyed structure whose object is still registered therefore keeps its score and stays
        // the defend target; only one that has left the world marks the task Done. The old C# port
        // dropped a dead structure here — that test is not in the script, so the script wins.
        structure.LastDamageAt = runtime.Combat.CurrentTime;
        structure.Alive = false;
        Decide(runtime, 4.0);
        TestAssert.True(Defend().Priority > 0.0,
            "TaskDefendStructure proto 0.0 0000-0007: a dead structure is still scored, only a gone object is not");
        TestAssert.False(Defend().Done, "a destroyed structure is not a finished task while the object exists");

        // Use the production lane route and wave spawn path. The midpoint of
        // a long route leg specifically guards the point-to-segment lane test;
        // a vertex-only check would incorrectly reject this ally.
        var pushRuntime = Start(roster, 272);
        var pushBot = pushRuntime.Session.Participants.Single(seat => seat.IsBot).Hero!;
        Quiet(pushBot);
        pushRuntime.Bots!.Tick(0.0);
        var lane = pushRuntime.Waves!.LaneRoute(Team.Chaos, "L");
        var segment = Enumerable.Range(1, lane.Count - 1)
            .Select(index => (Start: lane[index - 1], End: lane[index]))
            .OrderByDescending(pair => DistanceSquared(pair.Start, pair.End))
            .First();
        var dx = segment.End.X - segment.Start.X;
        var dz = segment.End.Z - segment.Start.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        TestAssert.True(length > 500.0, "the lane fixture has a long production route leg");
        var direction = new Position2(dx / length, dz / length);
        var allyPosition = new Position2((segment.Start.X + segment.End.X) / 2.0,
            (segment.Start.Z + segment.End.Z) / 2.0);
        var ally = AddFriendlyMinion(pushRuntime, Team.Chaos, 0, allyPosition);
        pushBot.Position = new Position2(allyPosition.X - direction.X * 400.0,
            allyPosition.Z - direction.Z * 400.0);
        Decide(pushRuntime, 1.25);
        TestAssert.True(pushBot.Waypoints.Count > 0 &&
                        EndsAt(pushBot, ally.Position),
            "a same-lane live ally in the 75-range range receives a push route");

        // Stop inside 150, retain the current route through the 300-unit
        // hysteresis band, and begin again when initially beyond that band.
        pushBot.Position = new Position2(allyPosition.X - direction.X * 100.0,
            allyPosition.Z - direction.Z * 100.0);
        ClearBotOrders(pushBot);
        Decide(pushRuntime, 1.5);
        TestAssert.Equal(0, pushBot.Waypoints.Count,
            "push movement stays stopped inside the 150-unit follow radius");
        ally.Position = new Position2(allyPosition.X + direction.X * 150.0,
            allyPosition.Z + direction.Z * 150.0);
        Decide(pushRuntime, 1.75);
        TestAssert.Equal(0, pushBot.Waypoints.Count,
            "a moving minion at 250 units remains stopped inside the 300-unit band");
        ally.Position = new Position2(allyPosition.X + direction.X * 250.0,
            allyPosition.Z + direction.Z * 250.0);
        Decide(pushRuntime, 3.0);
        var retainedRoute = pushBot.Waypoints.ToArray();
        TestAssert.True(retainedRoute.Length > 0,
            "push movement resumes after the 300-unit threshold");
        ally.Position = new Position2(allyPosition.X + direction.X * 150.0,
            allyPosition.Z + direction.Z * 150.0);
        Decide(pushRuntime, 3.25);
        TestAssert.True(pushBot.Waypoints.SequenceEqual(retainedRoute),
            "push movement retains its route inside the 300-unit hysteresis band");
    }

    [Check("spells-bot-request-validation")]
    public static void TypedCastRejectsMalformedGeometryBeforeMutation()
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, readySettleSeconds: 0.0);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.CreateWorld();
        var hero = runtime.Session.Participants[0].Hero!;
        var pending = new PendingCastState(
            new SpellCastRequest(0, false, new Position3(1, 0, 1),
                new Position3(1, 0, 1), 0), 4.0);
        hero.PendingCast = pending;
        var malformed = new SpellCastRequest(0, false,
            new Position3(double.NaN, 0, 1), new Position3(1, 0, 1), 0);

        TestAssert.False(runtime.Spells.HandleCastRequest(hero, malformed, 0.0),
            "typed bot requests reject non-finite geometry");
        TestAssert.True(ReferenceEquals(pending, hero.PendingCast),
            "rejected typed geometry does not cancel an existing order");

        var malformedEnd = new SpellCastRequest(0, false,
            new Position3(1, 0, 1), new Position3(double.NaN, 0, 1), 0);
        TestAssert.False(runtime.Spells.HandleCastRequest(hero, malformedEnd, 0.0),
            "typed bot requests reject non-finite end geometry");
        TestAssert.True(ReferenceEquals(pending, hero.PendingCast),
            "rejected end geometry also preserves an existing order");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static bool IsIdleSinceSample(Entity hero, Position2 previous) =>
        hero.Waypoints.Count == 0 && hero.Target is null && hero.AttackTarget is null &&
        DistanceSquared(previous, hero.Position) <= 1.0 &&
        DistanceSquared(hero.Position, hero.Team == Team.Order ? OrderFountain : ChaosFountain) >
            CombatService.FountainHealRadius * CombatService.FountainHealRadius;

    private static SessionParticipantDefinition[] OneBot(string champion, Team botTeam) =>
    [
        new SessionParticipantDefinition(1, 0, "Human", "Annie",
            botTeam == Team.Chaos ? Team.Order : Team.Chaos),
        new SessionParticipantDefinition(2, 1, "Bot", champion, botTeam, IsBot: true),
    ];

    /// <summary>Restores the combat clock and runs one bot tick (one due task update).</summary>
    private static void Decide(MatchRuntime runtime, double time)
    {
        // Bot.luaobj owns the cadence now: OnInit arms TimerHackDelayedInit one second later
        // (proto 0.3, InitTimer('TimerHackDelayedInit', 1, false)) and only that handler builds
        // the task list and arms TimerUpdate (proto 0.4, InitTimer('TimerUpdate', 0.25, true)).
        // A check that jumped the clock from 0 straight to its first decision skipped the second
        // and saw a bot with no tasks at all, so the clock is walked in the 250 ms steps a match
        // produces and the assertion still lands on the decision it names.
        for (var now = runtime.Combat.CurrentTime; now + Epsilon < time;)
        {
            now = Math.Min(now + BotScript.UpdateIntervalSeconds, time);
            runtime.Combat.RestoreClock(now);
            runtime.Bots!.Tick(0.25);
        }
    }

    private const double Epsilon = 1e-9;

    private static BotState StateOf(MatchRuntime runtime, Entity bot) =>
        runtime.Bots!.StateOf(runtime.Session.Participants
            .Single(seat => ReferenceEquals(seat.Hero, bot)).PlayerId) ??
        throw new InvalidOperationException("the bot has no script state yet");

    private static BotTask TaskOf(BotState state, BotTaskKind kind, int lane = -1) =>
        state.Tasks.First(task => task.Kind == kind && (lane < 0 || task.Lane == lane));

    /// <summary>
    /// Whether two mirrored entries are the same script task. The controller rebuilds
    /// the <see cref="BotTask"/> list every step, so only the fields the script holds are stable.
    /// </summary>
    private static bool SameTask(BotTask? one, BotTask? other) =>
        one is not null && other is not null && one.Kind == other.Kind && one.Lane == other.Lane &&
        one.SubjectNetId == other.SubjectNetId;

    /// <summary>Removes every optional task: no skill point, a full inventory, no gold, no orders.</summary>
    private static void Quiet(Entity bot)
    {
        bot.SkillPoints = 0;
        bot.Inventory = Enumerable.Repeat<InventorySlot?>(
            new InventorySlot(1001), Inventory.SlotCount).ToArray();
        bot.Gold = 0.0;
        ClearBotOrders(bot);
    }

    /// <summary>Empties one inventory slot (the list is read-only, so the array is rebuilt).</summary>
    private static void FreeSlot(Entity bot, int index)
    {
        var slots = bot.Inventory.ToArray();
        slots[index] = null;
        bot.Inventory = slots;
    }

    private static string Diag(MatchRuntime runtime, Entity bot, BotState state)
    {
        var items = string.Join(",", bot.Inventory.Select(slot => slot?.ItemId.ToString() ?? "-"));
        return $"[active={state.Active?.Kind} tasks={state.Tasks.Count} " +
               $"buy={TaskOf(state, BotTaskKind.Buy).Priority} " +
               $"cast={TaskOf(state, BotTaskKind.CastSpell).Priority} " +
               $"killMinion={TaskOf(state, BotTaskKind.KillMinion).Priority} " +
               $"push={string.Join("/", state.Tasks.Where(t => t.Kind == BotTaskKind.PushLane).Select(t => t.Priority))} " +
               $"free={bot.Inventory.Count(slot => slot is null)} gold={bot.Gold} inv={items} " +
               $"attempts={state.Measure.PurchaseAttempts} buys={state.Measure.Purchases} " +
               $"target={bot.AttackTarget} orders={state.OrdersIssued}]";
    }

    private static Position2 RoutePoint(MatchRuntime runtime, Team team, int lane, int index)
    {
        var route = runtime.Waves!.LaneRoute(team, MapDefinitions.Lanes[lane]);
        return route[Math.Min(index, route.Count - 1)];
    }

    private static Entity AddFriendlyMinion(MatchRuntime runtime, Team team, int lane,
        Position2 position)
    {
        var minion = runtime.Waves!.SpawnFixture(team, MinionType.Melee, 0,
            MapDefinitions.Lanes[lane], 0.0, 0.0);
        minion.Created = true;
        minion.Visible = true;
        minion.Position = position;
        minion.Waypoints.Clear();
        return minion;
    }

    private static bool SendMixedOrder(MatchRuntime runtime, long connection,
        Entity actor, byte order, Position2 destination)
    {
        var request = new byte[22];
        request[0] = 0x75;
        request[5] = order;
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), actor.NetId);
        BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(6), (float)destination.X);
        BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(14), (float)destination.Z);
        var accepted = false;
        runtime.Session.Receive(connection, 1, runtime.Cipher.Encrypt(request),
            context => accepted = runtime.Dependencies.Gameplay(context));
        runtime.Delivery.Flush();
        return accepted;
    }

    private static bool SendMixedCast(MatchRuntime runtime, long connection,
        Entity actor, int slot)
    {
        var request = new byte[34];
        request[0] = 0xA2;
        request[5] = (byte)(slot << 1);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), actor.NetId);
        foreach (var offset in new[] { 6, 18 })
        {
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset), (float)actor.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset + 8), (float)actor.Position.Z);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(30), actor.NetId);
        var accepted = false;
        runtime.Session.Receive(connection, 1, runtime.Cipher.Encrypt(request),
            context => accepted = runtime.Dependencies.Gameplay(context));
        runtime.Delivery.Flush();
        return accepted;
    }

    private static Entity AddBotTarget(MatchRuntime runtime, Entity bot,
        double distance = 30.0)
    {
        var target = new Entity(runtime.World.Allocate(UnitType.Hero), EntityKind.Hero,
            UnitType.Hero, bot.Team == Team.Order ? Team.Chaos : Team.Order,
            new Position2(bot.Position.X + distance, bot.Position.Z))
        {
            ChampionName = "Annie",
            Name = $"{bot.ChampionName} target",
            Alive = true,
            Created = true,
            Visible = true,
            MaxHealth = 100000.0,
            Health = 100000.0,
            Radius = 65.0,
            AttackRange = 0.0,
            Speed = 0.0,
        };
        runtime.World.Register(target);
        return target;
    }

    /// <summary>
    /// Moves a fixture straight out from each enemy fountain shrine whose AttackRange (the shipped 2250, batch 204) reaches it, to
    /// just past that range, as batch 204 moved the siege exhibit. A shrine shoots every enemy in range for 999 damage, so a fixture
    /// inside it dies before its bot acts. Test seam for placement only; the shrine's own range is read, not copied.
    /// </summary>
    private static void MoveOutOfShrineRange(MatchRuntime runtime, Entity fixture)
    {
        foreach (var shrine in runtime.World.Entities.Values.Where(unit => unit.UnitType == UnitType.Turret &&
                     unit.Team != fixture.Team && MapDefinitions.TurretTiers.GetValueOrDefault(unit.Name) == TurretTier.Fountain))
        {
            var dx = fixture.Position.X - shrine.Position.X;
            var dz = fixture.Position.Z - shrine.Position.Z;
            var distance = Math.Sqrt(dx * dx + dz * dz);
            var limit = shrine.AttackRange + 50.0;
            if (distance >= limit) continue;
            fixture.Position = new Position2(shrine.Position.X + dx / distance * limit, shrine.Position.Z + dz / distance * limit);
        }
    }

    private static Entity AddBotMinionTarget(MatchRuntime runtime, Entity bot,
        double distance = 30.0, double health = 1000.0)
    {
        var target = new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
            UnitType.Minion, bot.Team == Team.Order ? Team.Chaos : Team.Order,
            new Position2(bot.Position.X + distance, bot.Position.Z))
        {
            Name = $"{bot.ChampionName} minion target",
            Alive = true,
            Created = true,
            Visible = true,
            MaxHealth = Math.Max(1000.0, health),
            Health = health,
            Radius = 35.0,
            AttackRange = 0.0,
            Speed = 0.0,
        };
        runtime.World.Register(target);
        return target;
    }

    /// <summary>
    /// A Janna bot alone on the quiet mid point whose attack target is a dummy enemy minion at
    /// <paramref name="distance"/>. Janna is the champion on purpose: it has no own
    /// TaskCastSpell.luaobj, so the reconstructed BeginTask draws through the match's
    /// <see cref="IRandomSource"/> and a fixture can name the slot. The target is a minion because
    /// TaskKillHero scores 0.5 for any enemy hero inside GetAttackRange() + 500, which outranks
    /// the cast task's ceiling of 0.48 (TaskCastSpell proto 0.0); the minion keeps that task at 0
    /// and leaves KillMinion's 0.45 just under the cast task.
    /// </summary>
    private static (MatchRuntime Runtime, Entity Bot, Entity Target, BotState State) ArmedCastBot(
        IRandomSource random, int[] ranks, double distance, Action<Entity>? arrange = null,
        bool minionTarget = true)
    {
        var runtime = Start(OneBot("Janna", Team.Chaos), 351, random);
        var bot = runtime.Session.Participants.Single(participant => participant.IsBot).Hero!;
        runtime.Bots!.Tick(0.0);
        var state = StateOf(runtime, bot);
        Quiet(bot);
        bot.SpellRanks = [.. ranks];
        bot.Mana = bot.MaxMana;
        bot.Position = QuietMid;
        var target = minionTarget ? AddBotMinionTarget(runtime, bot, distance)
            : AddBotTarget(runtime, bot, distance);
        bot.AttackTarget = target.NetId;
        arrange?.Invoke(bot);
        return (runtime, bot, target, state);
    }

    /// <summary>Scripts the slot TaskCastSpell draws with <c>math.random(0, 3)</c>; the last slot repeats.</summary>
    private sealed class SlotScript(params int[] slots) : IRandomSource
    {
        private int _next;
        public double NextDouble() => 0.5;
        public int NextIndex(int count) => count == BotScript.CastSlotCount
            ? slots[Math.Min(_next++, slots.Length - 1)]
            : Math.Clamp((int)Math.Floor(0.5 * count), 0, count - 1);
    }

    private static MatchRuntime Start(
        IReadOnlyList<SessionParticipantDefinition> roster, long humanConnection,
        IRandomSource? random = null)
    {
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, readySettleSeconds: 0.0, random: random, roster: roster);
        runtime.World.Clock.SetVirtual(0.0);
        var session = runtime.Session;
        session.CreateWorld();
        var human = roster.Single(definition => !definition.IsBot);
        TestAssert.True(session.Authenticate(humanConnection,
            session.BuildKeyCheck(human.PlayerId)) is not null);
        TestAssert.True(session.Spawn(humanConnection).Count > 0);
        TestAssert.True(session.MarkReady(humanConnection));
        session.BuildMapReadyPackets(humanConnection);
        TestAssert.True(session.Start(), "the human-ready production roster starts");
        return runtime;
    }

    private static double DistanceSquared(Position2 first, Position2 second) =>
        (first.X - second.X) * (first.X - second.X) +
        (first.Z - second.Z) * (first.Z - second.Z);

    private static bool EndsAt(Entity hero, Position2 goal) => hero.Waypoints.Count > 0 &&
        DistanceSquared(hero.Waypoints[^1], goal) <= 120.0 * 120.0;

    private static void ClearBotOrders(Entity hero)
    {
        hero.Waypoints.Clear();
        hero.Target = null;
        hero.AttackTarget = null;
        hero.AttackMoveGoal = null;
        hero.HeroPursuit = null;
        hero.HeroAutoAcquire = false;
    }

    private static bool HasFountainRoute(Entity hero)
    {
        var fountain = hero.Team == Team.Order ? OrderFountain : ChaosFountain;
        return hero.Waypoints.Count > 0 &&
               DistanceSquared(hero.Waypoints[^1], fountain) <= 120.0 * 120.0;
    }

    private sealed class SeededRandomSource(int seed) : IRandomSource
    {
        private readonly Random _random = new(seed);
        public double NextDouble() => _random.NextDouble();
    }

    private sealed class PacketCaptureTransport : IReplicationTransport
    {
        private readonly List<PlaintextPacket> _packets = new();

        public IReadOnlyList<PlaintextPacket> Packets => _packets;

        public void Send(PlaintextPacket packet) => _packets.Add(packet);

        public void Clear() => _packets.Clear();
    }
}
