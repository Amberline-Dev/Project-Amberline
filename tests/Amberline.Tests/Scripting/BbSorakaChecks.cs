using System.Globalization;
using System.Reflection;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;
using static Amberline.Tests.BbAnnieDifferentialChecks;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine M2: Soraka plays from her shipped scripts in a production <c>MatchRuntime</c>. Every expected number is
/// read from the shipped files (the script tables through <see cref="BbScriptLoader"/>, the spell inibins through
/// <see cref="GameData"/>) and pinned a second time as a literal where the two are independent evidence of each other.
/// </summary>
public static class BbSorakaChecks
{
    private const double Step = 1.0 / 30.0;

    // Batch 340: the seconds from a press to the cast frame that pays it (0 for an instacast).
    private static double CastDelay(string spell) => GameData.Default.Spell(spell, 5).CastDelay;

    // ------------------------------------------------------------------------------------------------------------
    // Harness: Soraka and an ally on Order, an enemy hero and a far enemy on Chaos, the production graph.
    // ------------------------------------------------------------------------------------------------------------

    internal sealed class Match : IDisposable
    {
        public Match(string allyChampion = "Annie", string foeChampion = "Orianna")
        {
            var roster = new List<SessionParticipantDefinition>
            {
                new(1, 0, "Support", "Soraka", Team.Order), new(2, 1, "Ally", allyChampion, Team.Order),
                new(3, 2, "Enemy", foeChampion, Team.Chaos), new(4, 3, "FarEnemy", "Ezreal", Team.Chaos),
            };
            Runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default, readySettleSeconds: 0, roster: roster);
            Runtime.World.Clock.SetVirtual(0);
            Runtime.Session.CreateWorld();
            for (var i = 0; i < roster.Count; i++)
            {
                var id = i + 1;
                TestAssert.True(Runtime.Session.Authenticate(id, Runtime.Session.BuildKeyCheck((ulong)id)) is not null, "authenticate");
                Runtime.Session.Spawn(id);
                TestAssert.True(Runtime.Session.MarkReady(id), "ready");
                var hero = Runtime.Session.Participants[i].Hero!;
                hero.HeroAutoAcquire = false;
                hero.MaxHealth = hero.Health = 5000;
                hero.Mana = hero.MaxMana;
            }
            TestAssert.True(Runtime.Session.Start(), "start");
            Soraka = Runtime.Session.Participants[0].Hero!;
            Ally = Runtime.Session.Participants[1].Hero!;
            Foe = Runtime.Session.Participants[2].Hero!;
            FarFoe = Runtime.Session.Participants[3].Hero!;
            Soraka.Position = new Position2(7000, 7000);
            Ally.Position = new Position2(7150, 7000);
            Foe.Position = new Position2(7300, 7000);
            FarFoe.Position = new Position2(12000, 12000);
            Log = new Trace(Runtime.World);
            Log.Name(Soraka, "Soraka");
            Log.Name(Ally, "Ally");
            Log.Name(Foe, "Foe");
            Log.Name(FarFoe, "FarFoe");
            Module = (BbChampionModule)Runtime.Dependencies.ChampionDispatcher.FindModule("Soraka")!;
            Intercept();
        }

        public MatchRuntime Runtime { get; }
        public Entity Soraka { get; }
        public Entity Ally { get; }
        public Entity Foe { get; }
        public Entity FarFoe { get; }
        public Trace Log { get; }
        public BbChampionModule Module { get; }

        private void Intercept()
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            void Wrap<T>(string field) where T : class
            {
                var info = typeof(SpellSystem).GetField(field, flags)!;
                var proxy = DispatchProxy.Create<T, SpellServiceProxy>();
                var typed = (SpellServiceProxy)(object)proxy;
                typed.Target = info.GetValue(Runtime.Spells)!;
                typed.Log = Log;
                info.SetValue(Runtime.Spells, proxy);
            }
            Wrap<ISpellCombatService>("_combat");
            Wrap<ISpellBuffService>("_buffs");
            Wrap<ISpellEffectSink>("_effects");
            var hooks = typeof(CombatService).GetField("_hooks", flags)!;
            var hookProxy = DispatchProxy.Create<ICombatHooks, SpellServiceProxy>();
            var typedHook = (SpellServiceProxy)(object)hookProxy;
            typedHook.Target = hooks.GetValue(Runtime.Combat)!;
            typedHook.Log = Log;
            hooks.SetValue(Runtime.Combat, hookProxy);
        }

        public double Now => Runtime.World.Clock.Now();

        public bool Cast(int slot, Entity? target = null, Entity? caster = null)
        {
            caster ??= Soraka;
            var point = target?.Position ?? caster.Position;
            var at = new Position3(point.X, 0, point.Z);
            return Runtime.Spells.HandleCastRequest(caster, new SpellCastRequest(slot, false, at, at, target?.NetId ?? 0), Now);
        }

        public SpellValidation CanCast(int slot, Entity? target = null)
        {
            var point = target?.Position ?? Soraka.Position;
            return Runtime.Spells.CanCast(Soraka, slot, target?.NetId ?? 0, new Position3(point.X, 0, point.Z), Now);
        }

        public void Advance(double seconds)
        {
            for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += Step) Runtime.Tick(Step);
        }

        public void Refill()
        {
            for (var slot = 0; slot < 4; slot++) Soraka.SpellCooldowns[slot] = 0;
            Soraka.Mana = Soraka.MaxMana;
        }

        public void Learn(int rank)
        {
            for (var slot = 0; slot < 4; slot++) Soraka.SpellRanks[slot] = Math.Min(rank, slot == 3 ? 3 : 5);
        }

        public void GiveAbilityPower()
        {
            Soraka.Inventory = [new InventorySlot(1058), null, null, null, null, null];
            Runtime.HeroStats.Refresh(Soraka, push: false);
            TestAssert.True(Soraka.AbilityPower > 50, "Needlessly Large Rod raises ability power");
        }

        public void Dispose() => Runtime.World.Clock.UseReal();
    }

    // ------------------------------------------------------------------------------------------------------------
    // Shipped data readers
    // ------------------------------------------------------------------------------------------------------------

    private static BbScript Script(string name) => BbScriptLoader.Load(GameData.Default.Paths, $"Spells/{name}.luaobj").Script!;

    /// <summary>The <c>SrcValueByLevel</c> table of the first block that sets <paramref name="variable"/> in a hook.</summary>
    private static double[] ByLevel(string script, string hook, string variable)
    {
        var block = Script(script).Event(hook)!.Blocks.SelectMany(Flatten)
            .First(item => item.Function == "BBSetVarInTable" && item.Text("DestVar") == variable);
        return ((BbValue.Table)block.Param("SrcValueByLevel")!).Items.Select(item => ((BbValue.Number)item).Value).ToArray();
    }

    private static IEnumerable<BbBlock> Flatten(BbBlock block) => new[] { block }.Concat(block.SubBlocks.SelectMany(Flatten));

    private static double[] Table(BbBlock block, string key) =>
        ((BbValue.Table)block.Param(key)!).Items.Select(item => ((BbValue.Number)item).Value).ToArray();

    private static string R(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------------------------------------------------
    // Trace readers
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Committed hits on a named unit: time, final amount after mitigation, raw amount before it, damage category.</summary>
    private static List<(double At, double Dealt, double Raw, string Kind)> HitsOn(Match match, string victim) =>
        match.Log.Hits.Where(line => line.Contains("->" + victim + " ", StringComparison.Ordinal)).Select(line =>
        {
            var parts = line.Split(' ');
            return (double.Parse(parts[0], CultureInfo.InvariantCulture),
                double.Parse(parts.First(part => part.StartsWith("dealt=", StringComparison.Ordinal))[6..], CultureInfo.InvariantCulture),
                double.Parse(parts.First(part => part.StartsWith("raw=", StringComparison.Ordinal))[4..], CultureInfo.InvariantCulture),
                parts[4]);
        }).ToList();

    private static BuffState? Buff(Entity unit, string name) =>
        unit.Buffs.FirstOrDefault(buff => string.Equals(buff.Name, name, StringComparison.Ordinal));

    private static string Names(Entity unit) =>
        string.Join(",", unit.Buffs.Select(buff => $"{buff.Name}x{buff.Stacks}"));

    private static double Mitigated(double raw, double resistance) => raw * 100.0 / (100.0 + resistance);

    private static Entity Enemy(Match match, string name, Position2 position, UnitType type = UnitType.Minion)
    {
        var unit = new Entity(match.Runtime.World.Allocate(type), EntityKind.Minion, type, Team.Chaos, position)
        {
            Name = name, Health = 2000, MaxHealth = 2000, Armor = 20, SpellBlock = 20, Created = true, Visible = true,
        };
        match.Runtime.World.Register(unit);
        match.Log.Name(unit, name);
        return unit;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, packets
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-soraka-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndRunsEveryNativeOfItsHooks()
    {
        var module = new BbChampionModule("Soraka");
        TestAssert.Equal(16, module.ScriptNames.Count, "the Soraka closure is 16 scripts (audit bb-coverage)");
        var hero = new Entity { ChampionName = "Soraka" };
        TestAssert.Equal(["Starcall", "AstralBlessing", "Infuse", "Wish"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Soraka.inibin");
        // Infuse's ManaCost1..5 are stored as False in the shipped inibin; the ranks still come from the slot rule.
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "ranks");

        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Soraka");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly except the passive display name");
        var system = new[]
        {
            "APBonusDamageToTowers", "ChampionChampionDelta", "PositiveChampionDelta", "NegativeChampionDelta",
            "DisconnectTimer", "DisconnectTarget", "FortifyCheck", "FortifyBuff",
        };
        TestAssert.Equal(8, closure.ScriptNames.Count(name => system.Contains(name, StringComparer.OrdinalIgnoreCase)),
            "eight of the 16 scripts are the shared system buffs (batch 203: the engine runs APBonusDamageToTowers and DisconnectTarget from their shipped scripts)");
        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => !system.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native a Soraka hook runs is bound: " + string.Join(", ", unbound));
        // The natives M2 added for her, by the shipped scripts that call them.
        foreach (var native in new[] { "BBApplyAssistMarker", "BBApplySilence", "BBForEachUnitInTargetAreaRandom", "BBIncHealth" })
            TestAssert.True(BbNativeHandlers.IsBound(native), native + " is bound");
    }

    [Check("scripting.bb-soraka-production-installs-the-sink-only-for-script-heroes")]
    public static void ScriptHooksAreInstalledOnlyWhenASorakaIsInTheMatch()
    {
        // Batch 142: the "champion with no script module" this check used for its negative half is gone — Alistar runs from his
        // shipped scripts too (19 October 2026, W26d) and the Annie check pins that every registered champion does. A match
        // without a script-driven hero is therefore a match that creates no hero at all; the same roster then installs exactly
        // one sink the moment its heroes exist, and a champion outside it stays unbound.
        var plain = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default, "Alistar", "Alistar", readySettleSeconds: 0);
        try
        {
            plain.World.Clock.SetVirtual(0);
            TestAssert.True(((MatchGameplayHooks)plain.Dependencies.ChampionHooks).ScriptHooks is null,
                "no hero yet: no sink, the hooks forward nothing");
            TestAssert.False(((BbChampionModule)plain.Dependencies.ChampionDispatcher.FindModule("Soraka")!).IsBound, "Soraka's module stays unbound");
            plain.Session.CreateWorld();
            TestAssert.True(((MatchGameplayHooks)plain.Dependencies.ChampionHooks).ScriptHooks is not null,
                "a script-driven champion installs the sink when its hero is created");
            TestAssert.False(((BbChampionModule)plain.Dependencies.ChampionDispatcher.FindModule("Soraka")!).IsBound,
                "a champion outside the roster stays unbound");
        }
        finally { plain.World.Clock.UseReal(); }

        using var match = new Match();
        TestAssert.True(((MatchGameplayHooks)match.Runtime.Dependencies.ChampionHooks).ScriptHooks is not null,
            "a Soraka in the roster installs the sink when her hero is created");
        TestAssert.True(match.Module.IsBound, "the module is bound to the match");
        // session.HeroCreated ran SetVarsByLevel and CharOnActivate: the passive aura is on her before the match starts.
        TestAssert.True(Buff(match.Soraka, "ConsecrationAura") is { Type: BuffType.Aura }, "CharOnActivate added Consecration");
        TestAssert.Equal(1, match.Soraka.Buffs.Count(buff => buff.Name == "ChampionChampionDelta"),
            "the shared system buffs are applied once, natively: the engine skips the script's copies");
        TestAssert.Equal(1, match.Soraka.Buffs.Count(buff => buff.Name == "APBonusDamageToTowers"),
            "batch 203: the champion's shipped CharOnActivate add is the one tower buff; nothing refuses it any more");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-soraka-cast-packets-pass-the-four-client-gates")]
    public static void CastPacketsCarryNamesTimesCostsAndTargets()
    {
        using var match = new Match();
        match.Learn(1);
        var spells = GameData.Default.Champion("Soraka").SpellNames;
        TestAssert.Equal(["Starcall", "AstralBlessing", "Infuse", "Wish"], spells.ToArray(), "slot names");
        // Gate 4 (mCurManaCost) and gate 1 (mCanCastBits): what the 0xCC snapshot replicates before any cast.
        var snapshot = match.Runtime.HeroStats.Snapshot(match.Soraka);
        TestAssert.Equal([35.0, 90.0, 0.0, 100.0], snapshot.ManaCosts.ToArray(), "rank-one mana costs from the spell inibins (Infuse costs none)");
        TestAssert.True(snapshot.CanCastBits1 == uint.MaxValue && snapshot.CanCastBits2 == uint.MaxValue, "cast bits are open");
        var expectedCooldown = new[] { 2.5, 20.0, 10.0, 120.0 };
        var expectedCost = new[] { 35.0, 90.0, 0.0, 100.0 };
        var targets = new Entity?[] { null, match.Ally, match.Foe, null };
        for (var slot = 0; slot < 4; slot++)
        {
            match.Refill();
            var before = match.Log.Casts.Count;
            TestAssert.True(match.Cast(slot, targets[slot]), $"{spells[slot]} is accepted");
            TestAssert.Equal(before + 1, match.Log.Casts.Count, $"{spells[slot]} announces exactly one cast");
            var cast = match.Log.Casts[^1];
            var data = GameData.Default.Spell(spells[slot], slot == 3 ? 3 : 5);
            // Gate 2: the hash is the spell name the client resolves; gate 3: designer cast and total time are not zero.
            TestAssert.Equal(SpellSystem.ProtocolHash(spells[slot]), cast.SpellHash, $"{spells[slot]} hash");
            TestAssert.Equal((byte)slot, cast.Slot, $"{spells[slot]} slot");
            TestAssert.Equal(1, cast.Rank, $"{spells[slot]} rank");
            TestAssert.Equal(match.Soraka.NetId, cast.CasterNetId, $"{spells[slot]} caster");
            TestAssert.Near(expectedCost[slot], cast.ManaCost, 1e-6, $"{spells[slot]} mana cost");
            TestAssert.Near(expectedCooldown[slot], cast.Cooldown, 1e-6, $"{spells[slot]} cooldown");
            TestAssert.True(cast.DesignerCastTime > 0.0 && cast.DesignerTotalTime > 0.0,
                $"{spells[slot]} designer times {cast.DesignerCastTime}/{cast.DesignerTotalTime}");
            TestAssert.Near(data.CastDelay, cast.DesignerCastTime, 1e-6, $"{spells[slot]} designer cast time is the data's");
            TestAssert.Near(data.TotalTime, cast.DesignerTotalTime, 1e-6, $"{spells[slot]} designer total time is the data's");
            // A self-centred area cast lists the enemies its area holds (SpellSystem.AreaCastTargets, batch 111, the reveal rule): Starcall's
            // radius holds the foe 300 away, Wish's is the whole map; a unit-targeted cast names its target.
            var listed = targets[slot] is { } named ? new[] { named } : slot == 0 ? new[] { match.Foe } : slot == 3 ? new[] { match.Foe, match.FarFoe } : Array.Empty<Entity>();
            TestAssert.Equal(listed.Select(unit => unit.NetId).ToArray(), cast.Targets.Select(target => target.NetId).ToArray(), $"{spells[slot]} target list");
            TestAssert.Equal(0u, cast.MissileNetId, $"{spells[slot]} has no missile");
            match.Advance(0.6);
        }
        // The 0.5 s of the designer window: SpellDelayCastTime 0.5 scaled by DelayCastOffsetPercent -0.65 / -0.5.
        TestAssert.Near(0.175, GameData.Default.Spell("Starcall").CastDelay, 1e-6, "Starcall cast time 0.5 * (1 - 0.65)");
        TestAssert.Near(0.25, GameData.Default.Spell("AstralBlessing").CastDelay, 1e-6, "Astral Blessing cast time 0.5 * (1 - 0.5)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Q Starcall
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-soraka-q-starcall-damage-and-spell-block-shred")]
    public static void StarcallDamagesTheAreaAndShredsSpellBlockInStacks()
    {
        var damage = ByLevel("Starcall", "TargetExecute", "DamageToDeal");
        var shred = ByLevel("Starcall", "TargetExecute", "StarcallShred");
        var data = GameData.Default.Spell("Starcall", 5);
        TestAssert.Equal([60.0, 85.0, 110.0, 135.0, 160.0], damage, "Starcall.luaobj DamageToDeal");
        TestAssert.Equal([-8.0, -9.0, -10.0, -11.0, -12.0], shred, "Starcall.luaobj StarcallShred");
        for (var rank = 1; rank <= 5; rank++)
        {
            TestAssert.Equal(damage[rank - 1], data.EffectNumber(1, rank), $"Starcall.inibin Effect1 rank {rank} agrees with the script");
            TestAssert.Equal(-shred[rank - 1], data.EffectNumber(2, rank), $"Starcall.inibin Effect2 rank {rank} agrees with the script");
        }
        var ratio = ((BbValue.Number)Script("StarcallDamage").Event("OnBuffDeactivate")!.Blocks.SelectMany(Flatten)
            .First(block => block.Function == "BBApplyDamage").Param("SpellDamageRatio")!).Value;
        TestAssert.Near(0.4, ratio, 1e-12, "StarcallDamage.luaobj SpellDamageRatio");
        TestAssert.Near(0.4, data.Coefficient, 1e-6, "Starcall.inibin Coefficient");

        using var match = new Match();
        match.GiveAbilityPower();
        match.Learn(3);
        var ap = match.Soraka.AbilityPower;
        var minion = Enemy(match, "Minion", new Position2(7400, 7000));
        var far = Enemy(match, "FarMinion", new Position2(7800, 7000));        // 800 > 660
        var baseSpellBlock = match.Foe.SpellBlock;
        match.Refill();
        var mana = match.Soraka.Mana;
        TestAssert.True(match.CanCast(0).Allowed, "an enemy within 610 lets Starcall fire");
        var castAt = match.Now;
        TestAssert.True(match.Cast(0), "Q cast");
        match.Advance(0.12);
        TestAssert.Equal(0, HitsOn(match, "Foe").Count, "nothing lands during the 0.175 s windup");
        match.Advance(0.13);
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell) 0.175 s after the press, not the press.
        TestAssert.Near(mana - 45.0, match.Soraka.Mana, 0.5, "rank 3 costs 45 mana");
        TestAssert.Near(2.5, match.Soraka.SpellCooldowns[0] + 0.25 - CastDelay("Starcall"), Step + 1e-6, "cooldown 2.5 s");
        TestAssert.True(Buff(match.Foe, "StarcallDamage") is { Type: BuffType.Internal }, "the 0.4 s damage buff is on the target");
        TestAssert.Equal(0, HitsOn(match, "Foe").Count, "the damage waits for the buff to end");
        match.Advance(0.5);

        var raw = 110.0 + 0.4 * ap;
        foreach (var name in new[] { "Foe", "Minion" })
        {
            var hits = HitsOn(match, name);
            TestAssert.Equal(1, hits.Count, $"{name} is hit once");
            TestAssert.Near(raw, hits[0].Raw, 1e-6, $"{name}: rank 3 damage 110 + 0.4 AP");
            TestAssert.True(hits[0].Kind == "SpellAoe", "damage source SPELLAOE");
        }
        var foeHit = HitsOn(match, "Foe")[0];
        TestAssert.Near(castAt + 0.175 + 0.4, foeHit.At, Step + 1e-6, "damage lands 0.175 s windup + 0.4 s StarcallDamage after the cast");
        // The shred joins before the damage (the script adds the Starcall buff, then applies damage): resistance 30 - 10.
        TestAssert.Near(Mitigated(raw, baseSpellBlock - 10.0), foeHit.Dealt, 1e-6, "mitigated by the already shredded spell block");
        TestAssert.True(Buff(match.Foe, "Starcall") is { Type: BuffType.CombatDehancer, Stacks: 1 }, "one Starcall stack");
        TestAssert.Near(baseSpellBlock - 10.0, match.Foe.SpellBlock, 1e-6, "rank 3 shred is 10 spell block");
        TestAssert.Equal(0, HitsOn(match, "Ally").Count + HitsOn(match, "FarFoe").Count + HitsOn(match, "FarMinion").Count,
            "allies and units out of range are untouched");
        TestAssert.True(Buff(match.Ally, "Starcall") is null && Buff(match.Soraka, "Starcall") is null, "no shred on friends");

        // Stacks: every cast adds one, up to ten, and the shred scales with the stacks.
        for (var cast = 2; cast <= 12; cast++)
        {
            match.Refill();
            match.Foe.Health = match.Foe.MaxHealth;
            TestAssert.True(match.Cast(0), $"Q cast {cast}");
            match.Advance(0.6);
            var stacks = Math.Min(cast, 10);
            TestAssert.Equal(stacks, Buff(match.Foe, "Starcall")!.Stacks, $"stacks after cast {cast}");
            TestAssert.Near(baseSpellBlock - 10.0 * stacks, match.Foe.SpellBlock, 1e-6, $"spell block after cast {cast}");
        }
        TestAssert.Equal(1, match.Foe.Buffs.Count(buff => buff.Name == "Starcall"), "one buff state holds the stacks");
        // The buff lasts 8 s from the last stack, then the spell block comes back.
        match.Advance(7.0);
        TestAssert.True(Buff(match.Foe, "Starcall") is not null, "still shredded before the 8 s are up");
        match.Advance(1.2);
        TestAssert.True(Buff(match.Foe, "Starcall") is null, "the shred ends");
        TestAssert.Near(baseSpellBlock, match.Foe.SpellBlock, 1e-6, "spell block restored");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    /// <summary>
    /// Finding 32 (owner's hand test, 24 October 2026): "Soraka's Q isn't greyed out on the HUD when there are no valid targets nearby."
    /// The client greys a slot whose castable bit (<c>mCanCastBits1</c>) is clear. Starcall's CanCast gate
    /// clears its bit while nothing is within 610, and sets it again as soon as an enemy comes in reach (the stat snapshot reads the gate).
    /// </summary>
    [Check("scripting.bb-soraka-q-is-greyed-while-no-enemy-is-within-610")]
    public static void StarcallGreysItsSlotWhileNothingIsInReach()
    {
        using var match = new Match();
        match.Learn(1);
        match.Refill();
        match.Foe.Position = new Position2(7000 + 640, 7000);
        TestAssert.Equal(0xEu, match.Runtime.HeroStats.Snapshot(match.Soraka).CanCastBits1 & 0xFu,
            "no enemy inside 610: only the Q's castable bit is clear");
        match.Foe.Position = new Position2(7000 + 600, 7000);
        TestAssert.Equal(0xFu, match.Runtime.HeroStats.Snapshot(match.Soraka).CanCastBits1 & 0xFu,
            "an enemy inside 610: the Q's castable bit is set again");
        match.Foe.Position = new Position2(7000 + 900, 7000);
        TestAssert.Equal(0xEu, match.Runtime.HeroStats.Snapshot(match.Soraka).CanCastBits1 & 0xFu,
            "the enemy walks out again: the bit clears again");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-soraka-q-needs-an-enemy-within-610")]
    public static void StarcallCanCastRefusesWithoutAnEnemyNearby()
    {
        var canCast = Script("Starcall").Event("CanCast")!.Blocks.SelectMany(Flatten)
            .First(block => block.Function == "BBForEachUnitInTargetAreaRandom");
        TestAssert.Near(610.0, canCast.Number("Range")!.Value, 1e-12, "Starcall.luaobj CanCast range");
        TestAssert.Near(1.0, canCast.Number("MaximumUnitsToPick")!.Value, 1e-12, "it asks for a single unit");

        using var match = new Match();
        match.Learn(1);
        match.Refill();
        match.Foe.Position = new Position2(7000 + 640, 7000);
        var verdict = match.CanCast(0);
        TestAssert.False(verdict.Allowed, "no enemy inside 610: refused");
        TestAssert.True(verdict.Reason.Contains("CanCast", StringComparison.Ordinal), verdict.Reason);
        // A target-class refusal: the bot scripts' CanUseSpell(slot) == 0 (the spellbook's check, no script) still passes, so a bot draws
        // the slot and its ServerCastSpell fails here.
        TestAssert.True(verdict.Failure == SpellCastFailure.Target, "the script gate is refused as a target failure");
        TestAssert.True(match.Runtime.Spells.CanUseSpell(match.Soraka, 0, match.Now).Allowed,
            "CanUseSpell runs no spell script: the ready, learned slot is usable without an enemy in reach");
        var mana = match.Soraka.Mana;
        TestAssert.False(match.Cast(0), "the request is not accepted");
        TestAssert.Near(mana, match.Soraka.Mana, 1e-9, "no mana is spent");
        TestAssert.Near(0.0, match.Soraka.SpellCooldowns[0], 1e-9, "no cooldown starts");
        match.Foe.Position = new Position2(7000 + 600, 7000);
        TestAssert.True(match.CanCast(0).Allowed, "one enemy hero inside 610");
        match.Foe.Position = new Position2(7000 + 900, 7000);
        var minion = Enemy(match, "Minion", new Position2(7000, 7000 + 500));
        TestAssert.True(match.CanCast(0).Allowed, "an enemy minion counts as well (AffectMinions)");
        minion.Alive = false;
        TestAssert.False(match.CanCast(0).Allowed, "a dead minion does not");
        // The allied hero next to her never satisfies the gate.
        TestAssert.True(match.Ally.Position.X - match.Soraka.Position.X < 610.0, "the ally stands inside 610");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // W Astral Blessing
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-soraka-w-astral-blessing-heals-and-armors-an-ally")]
    public static void AstralBlessingHealsAndAddsArmor()
    {
        var heal = ByLevel("AstralBlessing", "TargetExecute", "HealthToRestore");
        var armor = ByLevel("AstralBlessing", "TargetExecute", "AstralArmor");
        var data = GameData.Default.Spell("AstralBlessing", 5);
        TestAssert.Equal([70.0, 140.0, 210.0, 280.0, 350.0], heal, "AstralBlessing.luaobj HealthToRestore");
        TestAssert.Equal([25.0, 50.0, 75.0, 100.0, 125.0], armor, "AstralBlessing.luaobj AstralArmor");
        for (var rank = 1; rank <= 5; rank++)
        {
            TestAssert.Equal(heal[rank - 1], data.EffectNumber(1, rank), $"AstralBlessing.inibin Effect1 rank {rank}");
            TestAssert.Equal(armor[rank - 1], data.EffectNumber(2, rank), $"AstralBlessing.inibin Effect2 rank {rank}");
        }
        var ratio = ((BbValue.Number)Script("AstralBlessing").Event("TargetExecute")!.Blocks.First(block =>
            block.Function == "BBMath" && block.Text("DestVar") == "HealingBonus").Param("Src2Value")!).Value;
        TestAssert.Near(0.45, ratio, 1e-12, "AstralBlessing.luaobj healing ratio");
        TestAssert.Near(0.45, data.Coefficient, 1e-6, "AstralBlessing.inibin Coefficient");

        using var match = new Match();
        match.GiveAbilityPower();
        match.Learn(2);
        var ap = match.Soraka.AbilityPower;
        var baseArmor = match.Ally.Armor;
        match.Ally.Health = 50.0;
        match.Refill();
        var mana = match.Soraka.Mana;
        TestAssert.True(match.CanCast(1, match.Ally).Allowed, "an ally in range is a legal target");
        TestAssert.False(match.CanCast(1, match.Foe).Allowed, "an enemy is not (the spell affects friends only)");
        TestAssert.True(match.CanCast(1, match.Foe).Failure == SpellCastFailure.Target, "refused as a target failure");
        TestAssert.True(match.Cast(1, match.Ally), "W cast");
        match.Advance(0.2);
        TestAssert.Near(50.0, match.Ally.Health, 1.0, "nothing lands before the 0.25 s windup ends (regeneration aside)");
        match.Advance(0.15);
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press.
        TestAssert.Near(mana - 110.0, match.Soraka.Mana, 0.5, "rank 2 costs 110 mana");
        TestAssert.Near(20.0, match.Soraka.SpellCooldowns[1] + 0.35 - CastDelay("AstralBlessing"), Step + 1e-6, "cooldown 20 s");
        var expectedHeal = 140.0 + 0.45 * ap;
        TestAssert.Near(Math.Min(match.Ally.MaxHealth, 50.0 + expectedHeal), match.Ally.Health, 1.0, "rank 2 heal 140 + 0.45 AP");
        TestAssert.True(Buff(match.Ally, "AstralBlessing") is { Type: BuffType.CombatEnhancer }, "the armor buff is on the ally");
        TestAssert.Near(5.0, Buff(match.Ally, "AstralBlessing")!.ExpiresAt, 0.2, "five seconds");
        TestAssert.Near(baseArmor + 50.0, match.Ally.Armor, 1e-6, "rank 2 armor +50");
        TestAssert.True(match.Log.Assists.Any(line => line.Contains("Soraka->Ally", StringComparison.Ordinal)), "the support is credited with an assist marker");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");

        // Casting again renews the live buff: five fresh seconds, still +50 (not stacked).
        match.Advance(3.0);
        match.Refill();
        TestAssert.True(match.Cast(1, match.Ally), "second W");
        match.Advance(0.4);
        TestAssert.Equal(1, match.Ally.Buffs.Count(buff => buff.Name == "AstralBlessing"), "one buff");
        TestAssert.Near(baseArmor + 50.0, match.Ally.Armor, 1e-6, "renewing does not stack the armor");
        TestAssert.True(Buff(match.Ally, "AstralBlessing")!.ExpiresAt > 4.5, "the renewal refreshed the duration");
        match.Advance(5.1);
        TestAssert.True(Buff(match.Ally, "AstralBlessing") is null, "the buff ends after five seconds");
        TestAssert.Near(baseArmor, match.Ally.Armor, 1e-6, "armor restored");

        // Self cast is legal (Flags carry no NotAffectSelf): she heals herself and takes the armor.
        match.Soraka.Health = 10.0;
        var selfArmor = match.Soraka.Armor;
        match.Refill();
        TestAssert.True(match.Cast(1, match.Soraka), "W on herself");
        match.Advance(0.4);
        TestAssert.Near(selfArmor + 50.0, match.Soraka.Armor, 1e-6, "self armor");
        TestAssert.True(match.Soraka.Health > 10.0 + 100.0, "self heal");

        // Losing the target during the windup refunds the cast (owner decision, 29 September 2026).
        match.Advance(5.5);
        match.Refill();
        var manaFull = match.Soraka.Mana;
        TestAssert.True(match.Cast(1, match.Ally), "W on an ally who then dies in the windup");
        match.Ally.Alive = false;
        match.Ally.Health = 0;
        match.Advance(0.5);
        TestAssert.Near(manaFull, match.Soraka.Mana, 1e-6, "mana refunded");
        TestAssert.Near(0.0, match.Soraka.SpellCooldowns[1], 1e-9, "cooldown cleared");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // E Infuse
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-soraka-e-infuse-restores-mana-or-damages-and-silences")]
    public static void InfuseRestoresManaToAlliesAndDamagesAndSilencesEnemies()
    {
        var infuse = Script("Infuse").Event("TargetExecute")!.Blocks.SelectMany(Flatten).ToArray();
        var mana = Table(infuse.First(block => block.Function == "BBIncPAR"), "DeltaByLevel");
        var damage = Table(infuse.First(block => block.Function == "BBApplyDamage"), "DamageByLevel");
        var silence = Table(infuse.First(block => block.Function == "BBApplySilence"), "DurationByLevel");
        var data = GameData.Default.Spell("Infuse", 5);
        TestAssert.Equal([25.0, 50.0, 75.0, 100.0, 125.0], mana, "Infuse.luaobj mana restored");
        TestAssert.Equal([50.0, 100.0, 150.0, 200.0, 250.0], damage, "Infuse.luaobj damage");
        TestAssert.Equal([1.5, 1.75, 2.0, 2.25, 2.5], silence, "Infuse.luaobj silence seconds");
        for (var rank = 1; rank <= 5; rank++)
        {
            TestAssert.Equal(mana[rank - 1], data.EffectNumber(1, rank), $"Infuse.inibin Effect1 rank {rank}");
            TestAssert.Equal(silence[rank - 1], data.EffectNumber(2, rank), $"Infuse.inibin Effect2 rank {rank}");
            TestAssert.Equal(damage[rank - 1], data.EffectNumber(3, rank), $"Infuse.inibin Effect3 rank {rank}");
        }
        TestAssert.Near(0.75, data.Coefficient, 1e-6, "Infuse.inibin Coefficient");
        TestAssert.Near(0.75, infuse.First(block => block.Function == "BBApplyDamage").Number("SpellDamageRatio")!.Value, 1e-12,
            "Infuse.luaobj SpellDamageRatio");

        using var match = new Match();
        match.GiveAbilityPower();
        match.Learn(4);
        var ap = match.Soraka.AbilityPower;

        // On an ally: mana to the ally and to herself, no mana cost for the cast.
        match.Ally.Mana = 0.0;
        match.Soraka.Mana = 50.0;
        match.Soraka.SpellCooldowns[2] = 0.0;
        TestAssert.True(match.CanCast(2, match.Ally).Allowed, "Infuse on an ally");
        TestAssert.True(match.Cast(2, match.Ally), "E on an ally");
        TestAssert.Near(50.0, match.Soraka.Mana, 1e-6, "Infuse costs no mana");
        match.Advance(0.1);
        TestAssert.Near(0.0, match.Ally.Mana, 1.0, "the 0.175 s windup has not ended");
        match.Advance(0.3);
        // Batch 340: the cooldown starts at the cast frame (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press.
        TestAssert.Near(10.0, match.Soraka.SpellCooldowns[2] + 0.4 - CastDelay("Infuse"), Step + 1e-6, "cooldown 10 s");
        TestAssert.Near(100.0, match.Ally.Mana, 1.0, "rank 4 gives the ally 100 mana");
        TestAssert.Near(150.0, match.Soraka.Mana, 1.0, "and Soraka herself 100 more");
        TestAssert.True(match.Log.Effects.Count(line => line.Contains("soraka_infuse_ally_tar.troy", StringComparison.Ordinal)) == 2,
            "the ally effect plays on both");
        TestAssert.True(match.Log.Assists.Any(line => line.Contains("Soraka->Ally", StringComparison.Ordinal)), "assist marker for the ally");

        // On herself: one portion only.
        match.Soraka.Mana = 50.0;
        match.Soraka.SpellCooldowns[2] = 0.0;
        TestAssert.True(match.Cast(2, match.Soraka), "E on herself");
        match.Advance(0.4);
        TestAssert.Near(150.0, match.Soraka.Mana, 1.0, "a self cast restores 100 once");

        // On an enemy: magic damage plus the global Silence.
        match.Advance(10.0);
        match.Soraka.SpellCooldowns[2] = 0.0;
        TestAssert.True(match.CanCast(2, match.Foe).Allowed, "Infuse on an enemy hero");
        TestAssert.True(match.Cast(2, match.Foe), "E on an enemy");
        match.Advance(0.1);
        TestAssert.True(Buff(match.Foe, "Silence") is null, "the silence waits for the windup");
        match.Advance(0.3);
        var hit = HitsOn(match, "Foe").Single(item => item.Kind == "Spell");
        TestAssert.Near(200.0 + 0.75 * ap, hit.Raw, 1e-6, "rank 4 damage 200 + 0.75 AP, DAMAGESOURCE_SPELL");
        TestAssert.Near(Mitigated(hit.Raw, match.Foe.SpellBlock), hit.Dealt, 1e-6, "magic damage is mitigated by spell block");
        var silenced = Buff(match.Foe, "Silence");
        TestAssert.True(silenced is { Type: BuffType.Silence }, "the Silence buff is on the enemy");
        TestAssert.True(match.Log.Buffs.Any(line => line.Contains(" add Foe Silence type=Silence dur=2.25 ", StringComparison.Ordinal)),
            "rank 4 silences for 2.25 s: " + string.Join(" | ", match.Log.Buffs));
        TestAssert.True(silenced!.ExpiresAt is > 1.9 and <= 2.25, "and the buff counts down from there");
        TestAssert.True(match.Foe.Buffs.Any(buff => buff.Payload.Silence), "silence payload");
        TestAssert.True(match.Log.Effects.Any(line => line.Contains("soraka_infuse_enemy_tar.troy", StringComparison.Ordinal)), "enemy effect");
        var verdict = match.Runtime.Spells.CanCast(match.Foe, 0, 0, new Position3(0, 0, 0), match.Now);
        // Batch 50/53 (fd56898): the refusal is the shared cast-check gate (SpellOwnerGate), whose reason is
        // its control-state text, not a silence-specific one.
        TestAssert.True(verdict.Failure == SpellCastFailure.Disabled &&
                verdict.Reason == "caster cannot cast in its control state",
            "the silenced enemy cannot cast: " + verdict.Reason);
        match.Advance(2.4);
        TestAssert.True(Buff(match.Foe, "Silence") is null, "the silence ends");

        // Range 725 and the flags: enemy minion and ally minion are legal, a building is not.
        match.Soraka.SpellCooldowns[2] = 0.0;
        match.Foe.Position = new Position2(7000 + 800, 7000);
        TestAssert.True(match.CanCast(2, match.Foe).Failure == SpellCastFailure.Range, "725 range");
        var enemyMinion = Enemy(match, "EnemyMinion", new Position2(7300, 7100));
        TestAssert.True(match.CanCast(2, enemyMinion).Allowed, "AffectMinions: an enemy minion is legal");
        var turret = Enemy(match, "Turret", new Position2(7300, 6900), UnitType.Turret);
        TestAssert.True(match.CanCast(2, turret).Failure == SpellCastFailure.Target, "AffectTurrets is not set");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // R Wish
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-soraka-r-wish-heals-every-allied-champion")]
    public static void WishHealsAlliedChampionsAnywhere()
    {
        var wish = Script("Wish").Event("TargetExecute")!.Blocks.SelectMany(Flatten).ToArray();
        var heal = ByLevel("Wish", "TargetExecute", "BaseHealthToHeal");
        var data = GameData.Default.Spell("Wish", 3);
        TestAssert.Equal([200.0, 320.0, 440.0], heal, "Wish.luaobj BaseHealthToHeal");
        for (var rank = 1; rank <= 3; rank++) TestAssert.Equal(heal[rank - 1], data.EffectNumber(1, rank), $"Wish.inibin Effect1 rank {rank}");
        TestAssert.Near(0.7, wish.First(block => block.Function == "BBMath" && block.Text("DestVar") == "SpellPowerBonus").Number("Src2Value")!.Value,
            1e-12, "Wish.luaobj healing ratio");
        TestAssert.Near(0.7, data.Coefficient, 1e-6, "Wish.inibin Coefficient");
        TestAssert.Equal([100.0, 175.0, 250.0], data.ManaCosts.ToArray(), "Wish mana costs");
        TestAssert.Equal([120.0, 110.0, 100.0], data.Cooldowns.ToArray(), "Wish cooldowns");
        TestAssert.Near(25000.0, data.CastRange, 1e-9, "Wish.inibin CastRange is global");

        using var match = new Match();
        match.GiveAbilityPower();
        match.Learn(2);
        var ap = match.Soraka.AbilityPower;
        // The ally is on the other side of the map; the heal does not care about distance. A low ally is still not full
        // after the heal (effect and assist marker), Soraka herself is only missing a few points (full after the heal).
        match.Ally.Position = new Position2(13000, 13000);
        match.Ally.Health = 10.0;
        match.Soraka.Health = match.Soraka.MaxHealth - 20.0;
        match.Foe.Health = 100.0;
        match.Refill();
        var mana = match.Soraka.Mana;
        TestAssert.True(match.CanCast(3).Allowed, "R has no target or range gate");
        TestAssert.True(match.Cast(3), "R cast");
        match.Advance(0.2);
        TestAssert.Near(10.0, match.Ally.Health, 1.0, "the 0.25 s windup has not ended");
        match.Advance(0.2);
        // Batch 340: the cast frame pays (DoCast_ClientAndServer -> ApplyCostsOfSpell), not the press.
        TestAssert.Near(mana - 175.0, match.Soraka.Mana, 0.5, "rank 2 costs 175 mana");
        TestAssert.Near(110.0, match.Soraka.SpellCooldowns[3] + 0.4 - CastDelay("Wish"), Step + 1e-6, "rank 2 cooldown 110 s");
        var amount = 320.0 + 0.7 * ap;
        TestAssert.Near(Math.Min(match.Ally.MaxHealth, 10.0 + amount), match.Ally.Health, 1.0, "the far ally gets 320 + 0.7 AP");
        TestAssert.Near(match.Soraka.MaxHealth, match.Soraka.Health, 1e-6, "Soraka is healed too (no NotAffectSelf)");
        TestAssert.Near(100.0, match.Foe.Health, 1.0, "enemies are not healed (regeneration aside)");
        TestAssert.True(match.Ally.Health < match.Ally.MaxHealth, "the ally is not full: the effect branch runs");
        TestAssert.Equal(1, match.Log.Effects.Count(line => line.Contains("Wish_tar.troy", StringComparison.OrdinalIgnoreCase)),
            "Wish_tar plays only on the champion still below full health after the heal");
        TestAssert.True(match.Log.Assists.Any(line => line.Contains("Soraka->Ally", StringComparison.Ordinal)), "assist marker on the healed ally");
        TestAssert.False(match.Log.Assists.Any(line => line.Contains("Soraka->Soraka", StringComparison.Ordinal)), "none on herself");

        // A dead ally is not an area target.
        match.Advance(110.0);
        match.Refill();
        match.Ally.Health = 0.0;
        match.Ally.Alive = false;
        TestAssert.True(match.Cast(3), "R with a dead ally");
        match.Advance(0.4);
        TestAssert.Near(0.0, match.Ally.Health, 1e-9, "the dead ally stays dead");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Passive: Consecration
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-soraka-passive-consecration-aura")]
    public static void ConsecrationGivesSpellBlockToHerAndNearbyAllies()
    {
        var character = BbScriptLoader.Load(GameData.Default.Paths, "Characters/Soraka/Scripts/CharScriptSoraka.luaobj").Script!;
        var scan = character.Event("UpdateSelfBuffActions")!.Blocks.SelectMany(Flatten).ToArray();
        TestAssert.Near(1000.0, scan.First(block => block.Function == "BBForEachUnitInTargetArea").Number("Range")!.Value, 1e-12, "aura range");
        TestAssert.Near(1.0, scan.First(block => block.Function == "BBExecutePeriodically").Number("TimeBetweenExecutions")!.Value, 1e-12, "refresh every second");
        TestAssert.Near(1.15, scan.First(block => block.Function == "BBSpellBuffAdd").Number("Duration")!.Value, 1e-12, "buff lasts 1.15 s");
        foreach (var name in new[] { "ConsecrationAura", "ConsecrationAuraNoParticle" })
        {
            var script = Script(name);
            TestAssert.Near(16.0, script.Event("OnBuffActivate")!.Blocks.First(block => block.Function == "BBIncPermanentStat").Number("Delta")!.Value, 1e-12, name + " adds 16");
            TestAssert.Near(-16.0, script.Event("OnBuffDeactivate")!.Blocks.First(block => block.Function == "BBIncPermanentStat").Number("Delta")!.Value, 1e-12, name + " takes it back");
        }
        var champion = GameData.Default.Champion("Soraka");

        using var match = new Match();
        // She carries the aura from creation: the character's own spell block (30, none per level) plus 16.
        TestAssert.Near(champion.SpellBlock + 16.0, match.Soraka.SpellBlock, 1e-9, "Soraka: base spell block 30 + 16");
        TestAssert.Near(16.0, match.Soraka.PermanentStatMods["FlatSpellBlockMod"], 1e-9, "the permanent modifier ledger");
        var allyBase = match.Ally.SpellBlock;
        TestAssert.True(Buff(match.Ally, "ConsecrationAuraNoParticle") is null, "allies get the aura at the first refresh, not at creation");
        match.Advance(1.2);
        TestAssert.True(Buff(match.Ally, "ConsecrationAuraNoParticle") is { Type: BuffType.Aura }, "an ally within 1000 holds the aura buff");
        TestAssert.Near(allyBase + 16.0, match.Ally.SpellBlock, 1e-9, "ally +16 spell block");
        TestAssert.True(Buff(match.Foe, "ConsecrationAuraNoParticle") is null && Buff(match.FarFoe, "ConsecrationAuraNoParticle") is null,
            "enemies are not covered");
        // Ten seconds of renewals: still +16, never more.
        match.Advance(10.0);
        TestAssert.Near(allyBase + 16.0, match.Ally.SpellBlock, 1e-9, "renewal does not stack the permanent modifier");
        TestAssert.Near(16.0, match.Ally.PermanentStatMods["FlatSpellBlockMod"], 1e-9, "ledger stays 16");
        TestAssert.Near(champion.SpellBlock + 16.0, match.Soraka.SpellBlock, 1e-9, "Soraka unchanged");

        // Leaving the range: the buff lapses within its 1.15 s and the ledger returns to zero.
        match.Ally.Position = new Position2(7000 + 1200, 7000);
        match.Advance(1.4);
        TestAssert.True(Buff(match.Ally, "ConsecrationAuraNoParticle") is null, "out of range the buff runs out");
        TestAssert.Near(allyBase, match.Ally.SpellBlock, 1e-9, "ally spell block restored");
        TestAssert.Near(0.0, match.Ally.PermanentStatMods.GetValueOrDefault("FlatSpellBlockMod"), 1e-9, "ledger back to zero");
        match.Ally.Position = new Position2(7000 + 900, 7000);
        match.Advance(1.2);
        TestAssert.Near(allyBase + 16.0, match.Ally.SpellBlock, 1e-9, "back in range: +16 again");

        // A dead Soraka stops refreshing (the script tests Owner IS_NOT_DEAD — owner decision 20 Oct 2026, "dead champions'
        // passives stop", retail footage of Zilean's passive leaving his allies while he is dead); her own aura persists
        // through death.
        match.Runtime.Combat.Kill(match.Soraka, match.Foe.NetId);
        TestAssert.False(match.Soraka.Alive, "Soraka died");
        TestAssert.True(Buff(match.Soraka, "ConsecrationAura") is not null, "Consecration persists through death");
        match.Advance(1.5);
        TestAssert.True(Buff(match.Ally, "ConsecrationAuraNoParticle") is null, "no refresh while she is dead");
        TestAssert.Near(allyBase, match.Ally.SpellBlock, 1e-9, "the ally loses the bonus");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Ranks, levels and cooldowns
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-soraka-ranks-levels-and-cooldowns")]
    public static void RankGatesLevelUpsAndCooldownsFollowTheData()
    {
        using var match = new Match();
        var soraka = match.Soraka;
        // Rank gates (the shared rule: Q/W/E at levels 1, 3, 5, 7, 9; R at 6, 11, 16).
        soraka.SkillPoints = 20;
        TestAssert.True(match.Runtime.Spells.RankUp(soraka, 0), "Q rank 1 at level 1");
        TestAssert.False(match.Runtime.Spells.RankUp(soraka, 0), "Q rank 2 needs level 3");
        TestAssert.False(match.Runtime.Spells.RankUp(soraka, 3), "R needs level 6");
        // Real level-ups through the combat service: SetVarsByLevel / the sink run on each, with the stat refresh.
        var manaBefore = soraka.MaxMana;
        TestAssert.True(match.Runtime.Combat.AwardHeroProgress(soraka, 0.0, 100000.0), "experience");
        TestAssert.Equal(18, soraka.Level, "enough experience reaches level 18");
        TestAssert.True(soraka.MaxMana > manaBefore, "the level-up refreshed her stats");
        var champion = GameData.Default.Champion("Soraka");
        TestAssert.Near(champion.SpellBlock + champion.SpellBlockPerLevel * 18 + 16.0, soraka.SpellBlock, 1e-6,
            "spell block follows the level with the aura on top");
        for (var slot = 0; slot < 4; slot++)
            while (match.Runtime.Spells.RankUp(soraka, slot)) { }
        TestAssert.Equal([5, 5, 5, 3], soraka.SpellRanks.ToArray(), "all ranks reachable at level 18");
        TestAssert.False(match.Runtime.Spells.RankUp(soraka, 3), "R stops at rank 3");

        var cooldowns = new[] { new[] { 2.5, 2.5, 2.5, 2.5, 2.5 }, new[] { 20.0, 20, 20, 20, 20 }, new[] { 10.0, 10, 10, 10, 10 }, new[] { 120.0, 110, 100 } };
        var manaCosts = new[] { new[] { 35.0, 40, 45, 50, 55 }, new[] { 90.0, 110, 130, 150, 170 }, new[] { 0.0, 0, 0, 0, 0 }, new[] { 100.0, 175, 250 } };
        for (var slot = 0; slot < 4; slot++)
        {
            var data = GameData.Default.Spell(GameData.Default.Champion("Soraka").SpellNames[slot], slot == 3 ? 3 : 5);
            TestAssert.Equal(cooldowns[slot], data.Cooldowns.ToArray(), $"slot {slot} cooldowns");
            TestAssert.Equal(manaCosts[slot], data.ManaCosts.ToArray(), $"slot {slot} mana costs");
        }
        // Cooldown reduction scales the cooldown of the cast (the shared BeginChampionCast rule), per rank.
        soraka.CooldownMod = -0.1;
        foreach (var slot in new[] { 0, 3 })
        {
            soraka.SpellRanks[slot] = slot == 3 ? 2 : 4;
            match.Refill();
            match.Soraka.Position = new Position2(7000, 7000);
            TestAssert.True(match.Cast(slot), $"slot {slot} cast");
            // Batch 340: a windup's cooldown starts at its cast frame (0 for an instacast).
            match.Advance(CastDelay(GameData.Default.Champion("Soraka").SpellNames[slot]));
            TestAssert.Near(cooldowns[slot][soraka.SpellRanks[slot] - 1] * 0.9, soraka.SpellCooldowns[slot], Step + 1e-6, $"slot {slot} cooldown with 10% reduction");
            match.Advance(0.5);
        }
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Production safety
    // ------------------------------------------------------------------------------------------------------------

    private sealed class CaptureLog : ISessionLogSink
    {
        public List<(string Category, string Level, string Message, IReadOnlyDictionary<string, object?>? Properties)> Records { get; } = [];

        public void Write(string category, string level, string message, IReadOnlyDictionary<string, object?>? properties = null) =>
            Records.Add((category, level, message, properties));
    }

    [Check("scripting.bb-soraka-script-exceptions-are-contained-and-logged")]
    public static void AFailingScriptSkipsItsHookAndNeverReachesTheMatch()
    {
        var log = new CaptureLog();
        var roster = new List<SessionParticipantDefinition>
        {
            new(1, 0, "Support", "Soraka", Team.Order), new(2, 1, "Enemy", "Orianna", Team.Chaos),
        };
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default, readySettleSeconds: 0, roster: roster, sessionLog: log);
        try
        {
            runtime.World.Clock.SetVirtual(0);
            runtime.Session.CreateWorld();
            var soraka = runtime.Session.Participants[0].Hero!;
            var module = (BbChampionModule)runtime.Dependencies.ChampionDispatcher.FindModule("Soraka")!;
            var router = module.Router!;

            // A hook whose second block calls a native the engine does not run: the run stops there, is reported with its block
            // path, and the simulation does not see the exception.
            var failing = new BbScript("Failing", "Spells/Failing.luaobj", [],
                [new BbEventTable("TargetExecute", [
                    new BbBlock("BBSetVarInTable", [new BbField("DestVar", new BbValue.Text("x")), new BbField("SrcValue", new BbValue.Number(1))], []),
                    new BbBlock("BBTeleportToKeyLocation", [], [])])], []);
            router.RunTargetExecute(failing, soraka, 0, 1, 0.0, new Position3(0, 0, 0), new Position3(0, 0, 0), soraka);
            TestAssert.Equal(1, module.ScriptFailures.Count, "one failure recorded");
            var failure = module.ScriptFailures[0];
            TestAssert.True(failure.Script == "Failing" && failure.Hook == "TargetExecute" && failure.BlockPath == ".2",
                $"script, hook and block path: {failure.Script}/{failure.Hook}/{failure.BlockPath}");
            TestAssert.True(failure.Message.Contains("BBTeleportToKeyLocation", StringComparison.Ordinal) ||
                            failure.Message.Contains("not bound", StringComparison.Ordinal), failure.Message);
            var line = log.Records.Single(record => record.Message == "script.exception");
            TestAssert.True(line.Level == "ERROR" && (string?)line.Properties!["hook"] == "TargetExecute" &&
                            (string?)line.Properties["script"] == "Failing" && (string?)line.Properties["block"] == ".2", "the ERROR log line");

            // The sink boundary: a bad argument cannot throw into the buff tick of the simulation.
            var buffTick = () => router.OnBuffTick(soraka, null!, 0.0);
            buffTick();
            TestAssert.Equal(2, module.ScriptFailures.Count, "the guarded forward reported once more");
            TestAssert.Equal("sink", module.ScriptFailures[1].Script, "named as a sink failure");
            TestAssert.Near(42.0, router.OnPreDamage(soraka, null, 42.0, DamageType.Magic, null), 0.0, "the damage amount passes through unchanged");

            // The host's frame stack is balanced after the failure: the next hook runs and the real Starcall gate answers
            // (no enemy within 610 of her at the fountain).
            var starcall = BbScriptLoader.Load(GameData.Default.Paths, "Spells/Starcall.luaobj").Script!;
            TestAssert.False(router.RunCanCast(starcall, soraka, 0, 1, 35.0), "a later hook still runs, and Starcall refuses an empty area");
            TestAssert.Equal(2, module.ScriptFailures.Count, "no further failure");
        }
        finally { runtime.World.Clock.UseReal(); }
    }

    // ------------------------------------------------------------------------------------------------------------
    // A Soraka bot in a headless match
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-soraka-bots-play-a-six-minute-headless-match", Serial = true)]
    public static void SorakaBotsPlayWithoutScriptExceptionsOrPacketViolations()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-soraka-match-" + Guid.NewGuid().ToString("N"));
        try
        {
            string[] roster = ["Soraka", "Annie", "Janna", "Ezreal", "Vayne", "Soraka", "Orianna", "Blitzcrank", "Nidalee", "Shaco"];
            var casts = new Dictionary<(uint Hero, int Slot), int>();
            var previous = new Dictionary<(uint Hero, int Slot), double>();
            var failures = new List<string>();
            var options = new Amberline.Tools.Diagnostics.HeadlessMatchOptions(20261016, Path.Combine(root, "m"), 6.0, roster)
            {
                OnStep = runtime =>
                {
                    foreach (var hero in runtime.World.Heroes().Where(hero => hero.ChampionName == "Soraka"))
                    {
                        for (var slot = 0; slot < 4; slot++)
                        {
                            var key = (hero.NetId, slot);
                            var now = hero.SpellCooldowns[slot];
                            if (previous.TryGetValue(key, out var before) && now > before + 0.5) casts[key] = casts.GetValueOrDefault(key) + 1;
                            previous[key] = now;
                        }
                    }
                    var module = (BbChampionModule)runtime.Dependencies.ChampionDispatcher.FindModule("Soraka")!;
                    foreach (var failure in module.ScriptFailures.Skip(failures.Count))
                        failures.Add($"{failure.Script}/{failure.Hook}/{failure.BlockPath}: {failure.Message}");
                },
            };
            var report = Amberline.Tools.Diagnostics.HeadlessMatch.Run(options);
            TestAssert.Equal("cap", report.Outcome, "six game minutes end at the cap, not in a crash");
            TestAssert.Equal(0L, report.PacketViolations, "the packet-stream checker finds no violation");
            TestAssert.Equal(0L, report.LifecycleViolations, "the lifecycle monitor stays quiet");
            TestAssert.Equal(0, failures.Count, "no script exception: " + string.Join(" | ", failures.Take(3)));
            var log = File.ReadAllText(Path.Combine(root, "m", "session.jsonl"));
            TestAssert.False(log.Contains("script.exception", StringComparison.Ordinal), "no script.exception line in the session log");
            TestAssert.True(casts.Values.Sum() > 0, "the Soraka bots cast their spells: " +
                string.Join(", ", casts.OrderBy(pair => pair.Key.Slot).Select(pair => $"slot {pair.Key.Slot} x{pair.Value}")));
            TestAssert.True(casts.Keys.Any(key => key.Slot is 1 or 2), "and use the support spells W or E on somebody");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
