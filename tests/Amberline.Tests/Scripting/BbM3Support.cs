using System.Buffers.Binary;
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
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbAnnieDifferentialChecks;

namespace Amberline.Tests;

/// <summary>
/// Shared harness of the BuildingBlocks M3 champion checks (Ryze, Taric, Kayle, Sivir): a production <c>MatchRuntime</c> with the
/// script-driven hero, an ally, an enemy hero and a far enemy, proxies recording what the module asks of the spell services,
/// and readers for the shipped script tables. The expected numbers of a check are read from the shipped files (script tables
/// through <see cref="BbScriptLoader"/>, spell inibins through <see cref="GameData"/>) and pinned a second time as literals.
/// </summary>
internal static class BbM3
{
    public const double Step = 1.0 / 30.0;

    /// <summary>
    /// A value the script's damage, ratio or percent parameters produce, compared with one computed from their decimals. The
    /// client's number getter returns a float32, so 0.2 reads 0.2f; the engine's <c>BbNativeHandlers.Sum</c> follows it
    /// (Batch 64 evidence, 18 October 2026) and the result is off from the decimal arithmetic by float32 rounding, under 2e-7 relative
    /// (the tolerance the data checks use for byte/10 values). A wrong ratio or table entry is off by whole points.
    /// </summary>
    public static void NearF32(double expected, double actual, string message) =>
        TestAssert.Near(expected, actual, Math.Max(1e-6, Math.Abs(expected) * 2e-7), message);

    internal sealed class Match : IDisposable
    {
        /// <summary>Batch 469: <paramref name="navigation"/> is the map's grid; null is map 1's (the default for every champion check).</summary>
        public Match(string champion, string allyChampion = "Annie", string foeChampion = "Orianna", Team team = Team.Order,
            NavigationGrid? navigation = null)
        {
            Champion = champion;
            var other = team == Team.Order ? Team.Chaos : Team.Order;
            var roster = new List<SessionParticipantDefinition>
            {
                new(1, 0, "Hero", champion, team), new(2, 1, "Ally", allyChampion, team),
                new(3, 2, "Enemy", foeChampion, other), new(4, 3, "FarEnemy", "Ezreal", other),
            };
            Runtime = MatchRuntime.CreateProduction(navigation ?? NavigationGrid.LoadDefault(), GameData.Default, readySettleSeconds: 0, roster: roster);
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
            Hero = Runtime.Session.Participants[0].Hero!;
            Ally = Runtime.Session.Participants[1].Hero!;
            Foe = Runtime.Session.Participants[2].Hero!;
            FarFoe = Runtime.Session.Participants[3].Hero!;
            Hero.Position = new Position2(7000, 7000);
            Ally.Position = new Position2(7150, 7000);
            Foe.Position = new Position2(7300, 7000);
            FarFoe.Position = new Position2(12000, 12000);
            Log = new Trace(Runtime.World);
            Log.Name(Hero, champion);
            Log.Name(Ally, "Ally");
            Log.Name(Foe, "Foe");
            Log.Name(FarFoe, "FarFoe");
            Module = (BbChampionModule)Runtime.Dependencies.ChampionDispatcher.FindModule(champion)!;
            Intercept();
        }

        public string Champion { get; }
        public MatchRuntime Runtime { get; }
        public Entity Hero { get; }
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

        /// <summary>The Chaos fountain: the production map's own shrine, which the Order champions' scenarios place themselves beside (batch 227).</summary>
        public Entity ChaosFountain => Runtime.World.Entities.Values.Single(entity =>
            entity.Team == Team.Chaos && TurretRules.IsFountainTurret(entity));

        /// <summary>A Chaos lane turret of the production map, the negative control of the fountain's immunity rule (batch 227).</summary>
        public Entity ChaosLaneTurret => Runtime.World.Entities.Values
            .Where(entity => entity.Team == Team.Chaos && TurretRules.IsLaneTurret(entity))
            .OrderBy(entity => entity.Name, StringComparer.Ordinal).First();

        public bool Cast(int slot, Entity? target = null, Entity? caster = null, Position2? at = null)
        {
            caster ??= Hero;
            var point = at ?? target?.Position ?? caster.Position;
            var position = new Position3(point.X, 0, point.Z);
            return Runtime.Spells.HandleCastRequest(caster, new SpellCastRequest(slot, false, position, position, target?.NetId ?? 0), Now);
        }

        public SpellValidation CanCast(int slot, Entity? target = null)
        {
            var point = target?.Position ?? Hero.Position;
            return Runtime.Spells.CanCast(Hero, slot, target?.NetId ?? 0, new Position3(point.X, 0, point.Z), Now);
        }

        public void Advance(double seconds)
        {
            for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += Step) Runtime.Tick(Step);
        }

        public void Refill()
        {
            for (var slot = 0; slot < 4; slot++) Hero.SpellCooldowns[slot] = 0;
            Hero.Mana = Hero.MaxMana;
        }

        /// <summary>Batch 340: the seconds from a press of the hero's slot to the cast frame that pays it (<see cref="BbM3.CastFrame"/>).</summary>
        public double CastFrameOf(int slot) => CastFrame(Module.SpellName(Hero, slot));

        public void Learn(int rank)
        {
            for (var slot = 0; slot < 4; slot++) Hero.SpellRanks[slot] = Math.Min(rank, slot == 3 ? 3 : 5);
            Runtime.HeroStats.Refresh(Hero, push: false);
        }

        public void GiveAbilityPower()
        {
            Hero.Inventory = [new InventorySlot(1058), null, null, null, null, null];
            Runtime.HeroStats.Refresh(Hero, push: false);
            TestAssert.True(Hero.AbilityPower > 50, "Needlessly Large Rod raises ability power");
        }

        public void Dispose() => Runtime.World.Clock.UseReal();
    }

    // ------------------------------------------------------------------------------------------------------------
    // Shipped data readers
    // ------------------------------------------------------------------------------------------------------------

    public static BbScript Script(string name) => BbScriptLoader.Load(GameData.Default.Paths, $"Spells/{name}.luaobj").Script!;

    public static BbScript CharScript(string champion) =>
        BbScriptLoader.Load(GameData.Default.Paths, $"Characters/{champion}/Scripts/CharScript{champion}.luaobj").Script!;

    /// <summary>The <c>SrcValueByLevel</c> table of the first block that sets <paramref name="variable"/> in a hook.</summary>
    public static double[] ByLevel(string script, string hook, string variable)
    {
        var block = Script(script).Event(hook)!.Blocks.SelectMany(Flatten)
            .First(item => item.Function == "BBSetVarInTable" && item.Text("DestVar") == variable);
        return Numbers(block.Param("SrcValueByLevel")!);
    }

    public static IEnumerable<BbBlock> Flatten(BbBlock block) => new[] { block }.Concat(block.SubBlocks.SelectMany(Flatten));

    public static IEnumerable<BbBlock> Blocks(BbScript script, string hook) => script.Event(hook)!.Blocks.SelectMany(Flatten);

    public static double[] Numbers(BbValue table) => ((BbValue.Table)table).Items.Select(item => ((BbValue.Number)item).Value).ToArray();

    public static double[] Table(BbBlock block, string key) => Numbers(block.Param(key)!);

    public static string R(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>
    /// Batch 340: the seconds from a press to the cast frame that spends the cast's mana and starts its cooldown (<c>DoCast_ClientAndServer</c>
    /// -> <c>ApplyCostsOfSpell</c>); 0 for an instacast, which pays at the press.
    /// </summary>
    public static double CastFrame(string spell) => GameData.Default.Spell(spell, 5).CastDelay;

    // ------------------------------------------------------------------------------------------------------------
    // Trace readers
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Committed hits on a named unit: time, final amount after mitigation, raw amount before it, damage category.</summary>
    public static List<(double At, double Dealt, double Raw, string Kind)> HitsOn(Match match, string victim) =>
        match.Log.Hits.Where(line => line.Contains("->" + victim + " ", StringComparison.Ordinal)).Select(line =>
        {
            var parts = line.Split(' ');
            return (double.Parse(parts[0], CultureInfo.InvariantCulture),
                double.Parse(parts.First(part => part.StartsWith("dealt=", StringComparison.Ordinal))[6..], CultureInfo.InvariantCulture),
                double.Parse(parts.First(part => part.StartsWith("raw=", StringComparison.Ordinal))[4..], CultureInfo.InvariantCulture),
                parts[4]);
        }).ToList();

    public static BuffState? Buff(Entity unit, string name) =>
        unit.Buffs.FirstOrDefault(buff => string.Equals(buff.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Adds a shipped spell's buff to <paramref name="holder"/> as a live script instance of the unit character module,
    /// the way a camp's transferred buff or a character script's own add is a live instance: its hooks
    /// (<c>BuffOnHitUnit</c>, <c>BuffOnUpdateStats</c> ...) run for the unit instead of the buff being a hand-built row.
    /// Batch 159 part 2: the red buff's burn and slow are Spells/BlessingoftheLizardElder.luaobj's own
    /// <c>BuffOnHitUnit</c>, so a check that wants them has to add the buff this way — a plain
    /// <c>ISpellBuffService.Add</c> row carries no script at all.
    /// </summary>
    public static BuffState? ScriptBuff(MatchRuntime runtime, Entity holder, string name, double duration) =>
        runtime.Spells.UnitScripts!.Buffs?.Add(new BbBuffRequest(holder, holder, name,
            (int)BbBuffAddType.BUFF_REPLACE_EXISTING, (int)Amberline.Core.Buffs.BuffType.CombatEnhancer, 1,
            duration, 0.0, false, new BbTable()));

    public static string Names(Entity unit) =>
        string.Join(",", unit.Buffs.Select(buff => $"{buff.Name}x{buff.Stacks}"));

    public static double Mitigated(double raw, double resistance) => raw * 100.0 / (100.0 + resistance);

    /// <summary>The packets one client receives, with the simulation time of the flush that delivered each.</summary>
    internal sealed class WireCapture(Func<double> now) : Amberline.Network.Replication.IReplicationTransport
    {
        public List<(double At, PlaintextPacket Packet)> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add((now(), packet));
    }

    /// <summary>Adds a ready client of <paramref name="team"/> that sees the match (its own hero is <paramref name="hero"/>); a second client needs its own <paramref name="id"/>.</summary>
    public static WireCapture Observe(Match match, Team team, Entity hero, ulong id = 9001)
    {
        var capture = new WireCapture(() => match.Now);
        match.Runtime.Delivery.AddObserver(new Amberline.Network.Replication.ReplicationObserver(id, team, hero.NetId, capture, ready: true));
        return capture;
    }

    /// <summary>Batch 582: the spell hash and missile net id a 0xBD cast answer (cast info at 9) or a 0x41 missile packet (at 98) names; null otherwise.</summary>
    public static (uint Spell, uint Missile)? MissileOf(PlaintextPacket packet)
    {
        var span = packet.Plaintext.Span;
        var info = span[0] switch { SpellPackets.CastSpellAnswerOpcode => 9, 0x41 => 98, _ => -1 };
        if (info < 0 || span.Length < info + 23) return null;
        return (BinaryPrimitives.ReadUInt32LittleEndian(span[(info + 2)..]), BinaryPrimitives.ReadUInt32LittleEndian(span[(info + 19)..]));
    }

    /// <summary>A 0x8C effect create whose name is one of <paramref name="stems"/> (the particle's name without its extension, hashed as the client hashes it).</summary>
    public static bool EffectNamed(PlaintextPacket packet, params string[] stems) =>
        packet.Plaintext.Span[0] == FxPackets.CreateGroupOpcode &&
        stems.Any(stem => BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == SpellPackets.BuffNameHash(stem));

    /// <summary>
    /// The 0x8C position block at <paramref name="from"/> minus the block at <paramref name="to"/>, in units. The blocks are at 33 (source), 41 (target) and
    /// 49 (third, the target object's). The grid packs each coordinate as a short two units apart (VisionPackets.GridCoordinate), so the difference is
    /// exact to two units and needs no origin.
    /// </summary>
    public static (double X, double Z) EffectDelta(PlaintextPacket packet, int from, int to)
    {
        var span = packet.Plaintext.Span;
        return ((BinaryPrimitives.ReadInt16LittleEndian(span[from..]) - BinaryPrimitives.ReadInt16LittleEndian(span[to..])) * 2.0,
            (BinaryPrimitives.ReadInt16LittleEndian(span[(from + 6)..]) - BinaryPrimitives.ReadInt16LittleEndian(span[(to + 6)..])) * 2.0);
    }

    /// <summary>
    /// A production lane minion (the wave scheduler's own spawn, with its inibin attack: damage, range, windup) parked at <paramref name="position"/>
    /// with no route, so a check can queue its basic attack through <c>CombatService.QueueBasicAttack</c> (batch 309: the harness had no attacking
    /// minion, so a script's minion branch, such as Teemo's passive, was not checked).
    /// </summary>
    public static Entity LaneMinion(Match match, Position2 position, Team team = Team.Chaos, MinionType type = MinionType.Melee)
    {
        var minion = match.Runtime.Waves!.SpawnFixture(team, type, 0, "C", 0, 0);
        minion.Position = position;
        minion.Waypoints.Clear();
        minion.Target = null;
        match.Log.Name(minion, "Minion");
        return minion;
    }

    public static Entity Enemy(Match match, string name, Position2 position, UnitType type = UnitType.Minion, Team? team = null)
    {
        var unit = new Entity(match.Runtime.World.Allocate(type), EntityKind.Minion, type, team ?? Team.Chaos, position)
        {
            Name = name, Health = 2000, MaxHealth = 2000, Armor = 20, SpellBlock = 20, Created = true, Visible = true,
        };
        match.Runtime.World.Register(unit);
        match.Log.Name(unit, name);
        return unit;
    }
}
