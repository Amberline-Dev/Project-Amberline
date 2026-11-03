using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Network.Transport;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network;

/// <summary>
/// Production-path death recap checks.  Codec-only invariants live in
/// DeathRecapChecks; these checks exercise CombatService and ReplicationDelivery
/// with the same graph used by the server.
/// </summary>
public static class DeathRecapLiveChecks
{
    /// <summary>
    /// The recap hash of Dragon's Rage. Lee Sin runs from his shipped scripts, so his R origin is the production
    /// <c>DeathRecapOrigin.ChampionSpell(lee, 3, rank)</c> (slot 3's spell name, <c>BlindMonkRKick</c>); the former
    /// hand-written constant <c>LeeSinBlindMonkRKickHash</c> (0x082DA94B) is gone.
    /// </summary>
    private static readonly uint LeeSinBlindMonkRKickHash = ProtocolEncoding.HashString("BlindMonkRKick");

    /// <summary>
    /// The recap hashes of the spells these checks name. Batch 164 moved the remaining hand-written death-recap rows
    /// onto what the scripts' damage carries: a production row is <c>DeathRecapOrigin.ChampionSpell(caster, slot, rank)</c>
    /// (<c>BbSpellHost.RecapOrigin</c> reads the SpellSlot/SpellRank the running script's frame carries), and its hash is
    /// the shipped spell name of that slot. The hand-written rows are deleted with it: <c>AnnieDirectSpell</c> /
    /// <c>BlitzcrankDirectSpell</c> (with <c>DirectSpell</c>) and the fixed per-champion hash constants, as Lee Sin's
    /// alias was in batch 105.
    /// </summary>
    private static readonly uint EzrealEssenceFluxHash = ProtocolEncoding.HashString("EzrealEssenceFlux");
    private static readonly uint EzrealTrueshotBarrageHash = ProtocolEncoding.HashString("EzrealTrueshotBarrage");
    private static readonly uint BlitzcrankStaticFieldHash = ProtocolEncoding.HashString("StaticField");
    private static readonly uint OriannaDetonateCommandHash = ProtocolEncoding.HashString("OrianaDetonateCommand");
    private static readonly uint AnnieInfernalGuardianHash = ProtocolEncoding.HashString("InfernalGuardian");

    [Check("death-recap.live-b4-baron-four-producers")]
    public static void BaronRecapIncludesScriptedAttackSweepPropelAndLethalWrath()
    {
        using var match = new LiveMatch(2, "Vayne");
        var runtime = match.Runtime;
        var victim = match.Hero(0);
        // playtest B4 / retail sweep, 27 Sept 2026: control/debuff stat refreshes
        // must not erase the health pool needed to survive all four producers.
        runtime.Buffs.AddBuff(victim, "BaronRecapHealth", BuffType.CombatEnhancer,
            30, victim.NetId, new BuffPayload { MaxHp = 5000 });
        victim.Health = victim.MaxHealth;
        match.Hero(1).Position = new Position2(10000, 10000);
        var grid = runtime.Navigation;
        var sink = new ProtocolNeutralPacketSink(packet => runtime.Delivery.Enqueue(packet),
            runtime.SyncIds, runtime.World.Allocate,
            new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ),
            (x, z) => grid.HeightAt(new Position2(x, z)));
        var scheduler = new JungleScheduler(runtime.World, runtime.GameData, sink, [12], grid);
        // Batch 174b: production binds every camp member to its character script (ProductionMatchComposition); the Baron's rotation, and so
        // its WormAttack, exists only when the Worm is bound. Without this line the Baron has no script and never casts.
        scheduler.BindUnitCharacter = (unit, character) => runtime.Spells.UnitScripts!.BindUnit(unit, character);
        scheduler.Start(0.0);
        scheduler.Tick(NeutralDefinitions.InitialSpawnSeconds + NeutralDefinitions.Camps[12].InitialDelay);
        var baron = scheduler.MembersForCamp(12).Single(member => member.Name == "Worm").Entity;
        baron.Position = victim.Position + new Position2(150, 0);
        baron.Created = true;
        var ai = new NeutralAiService(runtime.World, scheduler, runtime.Movement,
            runtime.Combat, sink, runtime.Effects, runtime.SpellBuffs,
            runtime.GameData, new RecapRandom());
        foreach (var seat in runtime.Session.Participants)
        {
            seat.View.Known.Add(baron.NetId);
            seat.View.Visible.Add(baron.NetId);
        }
        runtime.Delivery.Flush();
        match.Clear();
        var before = victim.MatchStatistics.TotalDamageTaken;

        // Drive the production owners separately to isolate damage from regen,
        // and retain an exactly eleven-second-old real Sweep contribution.
        // Batch 174b: the victim's own hit wakes the Baron, and its rotation queues the WormAttack while the victim is still in reach. The
        // WormAttack is a scripted missile (not a Combat.PendingHits entry), so the window waits for the missile in flight; only then do the
        // Sweep and the move out of reach for the Propel follow.
        TestAssert.True(runtime.Combat.ApplyHit(victim.NetId, baron.NetId, 1.0, DamageType.Physical, DamageSourceKind.Basic),
            "the victim's hit wakes the Baron");
        // The missile is in flight for about a tenth of a second, so the window samples every .02 s (a .1 s sample can fall between two flights).
        for (var step = 0; step < 200 && !WormAttackInFlight(runtime, baron); step++)
        {
            runtime.World.Clock.Advance(.02);
            runtime.Spells.Tick(.02);
            foreach (var unit in runtime.World.Entities.Values.ToArray()) runtime.Buffs.TickBuffs(unit, .02);
            ai.Tick(.02);
            runtime.Combat.CombatTick(.02, regenerate: false);
        }
        TestAssert.True(WormAttackInFlight(runtime, baron), "the Baron's WormAttack is launched while the victim is in reach");
        // Batch 192: the Sweep's record lands 0.38 s after the trigger (the 0.397 s cast, read on the step after the trigger, measured). The combat
        // clock is set to 0.22 so that record is stamped 0.60 s, and the lethal sits 11.0 s later: the age the assertion below names.
        runtime.Combat.RestoreClock(0.22);
        TestAssert.True(NeutralWormScriptView.Trigger(runtime.Spells.UnitScripts!, runtime.Buffs, baron, NeutralWormAction.Sweep));
        // Batch 192: the Sweep lands on the spell clock (its cast takes 0.397 s) while the victim is still in reach. The full tick runs until then;
        // Movement.Tick alone let the cast land after the victim had been moved out of reach, so the Sweep damage was never recorded.
        for (var step = 0; step < 30; step++) StepFull(runtime, ai, .02);
        runtime.Movement.Tick(.6);
        victim.Position = baron.Position + new Position2(300, 0);
        TestAssert.True(NeutralWormScriptView.Trigger(runtime.Spells.UnitScripts!, runtime.Buffs, baron, NeutralWormAction.Propel));
        // Batch 174b: the geyser's delay (2.1 s, a quarter second more for the other team's grid) runs on the spell and buff clocks, which the
        // rotation's own window used to advance; the window now steps them for 2.5 s.
        for (var step = 0; step < 125; step++)
        {
            runtime.World.Clock.Advance(.02);
            runtime.Spells.Tick(.02);
            foreach (var unit in runtime.World.Entities.Values.ToArray()) runtime.Buffs.TickBuffs(unit, .02);
            ai.Tick(.02);
            runtime.Combat.CombatTick(.02, regenerate: false);
        }
        TestAssert.True(victim.ForcedMove is { Effect: "Propel" }, "the geyser starts the real move");
        // Batch 174b: the displacement carries no recap origin in this engine (Entity.Movement.cs RecapOrigin is never set on a Propel move).
        // The Propel's origin reaches the recap through its landing hit, which the recap loop below checks.
        // Batch 174b: the displacement runs to its end on the full tick (movement, spell system, buffs, combat), within five seconds.
        for (var step = 0; step < 100 && victim.ForcedMove is not null; step++)
        {
            runtime.Movement.Tick(.05);
            runtime.World.Clock.Advance(.05);
            runtime.Spells.Tick(.05);
            foreach (var unit in runtime.World.Entities.Values.ToArray()) runtime.Buffs.TickBuffs(unit, .05);
            runtime.Combat.CombatTick(.05, regenerate: false);
        }
        TestAssert.True(victim.ForcedMove is null, "Propel completed before the lethal hit");
        runtime.Combat.CombatTick(1.0, regenerate: false);
        runtime.Combat.CombatTick(1.0, regenerate: false);
        TestAssert.False(WormAttackInFlight(runtime, baron), "the Baron's WormAttack has landed");

        // Batch 192: WrathoftheAncients casts with its derived cast time (0.397 s: GameData.CastTime of the shipped spell, no override, not
        // instant), and its selection runs every 0.4 s (TimeBetweenExecutions) over the Wrath's 800-unit area (Range 800.0). So the first marker
        // is due about 0.8 s after the trigger. The 200 of the Sweep's scan (SweepTimer, Range 200.0) is not the Wrath's area. The victim stands
        // at 100 units, inside both. The bound Baron's own rotation can leave a Wrath running (a Sweep with nothing within 200 casts it), so
        // the window first lets any such Wrath end; the explicit trigger is then the only Wrath, and the window steps until its marker is due.
        victim.Position = baron.Position + new Position2(100, 0);
        // Batch 192: the bound Baron's own rotation can leave a Wrath running (a Sweep with nothing within 200 casts it). Its selection is ended
        // here and its last marker is let expire, so the explicit trigger below is the only Wrath in the window.
        var naturalWrath = baron.Buffs.FirstOrDefault(buff => buff.Name == "WrathoftheAncients");
        if (naturalWrath is not null) runtime.Buffs.RemoveBuff(baron, naturalWrath.Slot);
        // Batch 259: the rotation's own geysers are let land too. PropelTimer places them at random points
        // (BBForEachUnitInTargetAreaRandom, BBGetRandomPointInAreaUnit; the match's unseeded random), so about one run in twenty left one
        // that landed on the victim in the lethal window (a Propel row at 11.0 s and no WrathDamage row; 7 of 150 runs in one worker).
        // A geyser started after this point lands 2.1 s later, beyond the marker and lethal windows.
        var settleSteps = 0;
        while (settleSteps < 150 && NeutralWormScriptView.Snapshot(runtime.World, baron) is var settle &&
               (settle.PendingWrath.Count > 0 || settle.Geysers.Count > 0))
        {
            StepFull(runtime, ai, .02);
            settleSteps++;
        }
        runtime.Combat.RestoreClock(10.2);
        TestAssert.True(victim.Alive, "Sweep, Propel and WormAttack remain nonlethal");
        TestAssert.True(settleSteps < 150 && NeutralWormScriptView.Snapshot(runtime.World, baron) is { PendingWrath.Count: 0, Geysers.Count: 0 },
            "the rotation's own Wrath marker and geysers have ended before the explicit trigger");
        TestAssert.True(NeutralWormScriptView.Trigger(runtime.Spells.UnitScripts!, runtime.Buffs, baron, NeutralWormAction.Wrath));
        runtime.Combat.RestoreClock(10.6);
        // Batch 174c: the Wrath selection is a periodic buff action, so this window runs the spell and buff clocks too (ai.Tick alone does not).
        var markerSteps = 0;
        while (NeutralWormScriptView.Snapshot(runtime.World, baron).PendingWrath.Count == 0 && markerSteps < 60)
        {
            StepFull(runtime, ai, .02);
            markerSteps++;
        }
        TestAssert.True(NeutralWormScriptView.Snapshot(runtime.World, baron).PendingWrath.Count == 1,
            $"the Wrath selection attaches its marker within 1.2 s (baron buffs [{string.Join(",", baron.Buffs.Select(buff => buff.Name))}], " +
            $"victim buffs [{string.Join(",", victim.Buffs.Select(buff => buff.Name))}])");
        TestAssert.True(markerSteps * .02 >= .79 && markerSteps * .02 <= .9,
            $"the marker lands on the shipped timing (cast 0.397 s, then a 0.4 s selection), not at {markerSteps * .02:F2} s");
        victim.Health = 1.0;
        runtime.Combat.RestoreClock(11.0);
        for (var step = 0; step < 30; step++) StepFull(runtime, ai, .02);
        TestAssert.False(victim.Alive, "delayed Wrath is the lethal producer");
        var snapshot = runtime.Combat.GetLastDeathRecap(victim.NetId)
            ?? throw new InvalidOperationException("Baron did not create a recap");
        TestAssert.Equal(15.0, snapshot.TimeWindow);
        TestAssert.Equal(baron.NetId, snapshot.KillerNetId);
        TestAssert.Equal(DeathRecapSourceType.Minion, snapshot.KillerSourceType);
        // Batch 174b: the Sweep row is found by its origin (the WormAttack that woke the Baron is an earlier row now).
        // Batch 192: the Sweep's recap row is the SweepingBlow origin (a shipped spell with no client buff; the recap catalogue names it since
        // the gate fix in DeathRecap.cs). Its hash is HashString("SweepingBlow"), not the WormAttack hash the earlier row lookup misread.
        // Batch 192: two records carry the Sweep origin; the age below names the first, the Sweep's landing on the victim in reach.
        var sweepRow = snapshot.Records.First(row => row.Origin == DeathRecapOrigin.WorldScript(baron, "SweepingBlow"));
        // Batch 192: the age is 11.0 s to float precision (the clock steps accumulate rounding); the expected value is unchanged.
        TestAssert.Near(11.0, runtime.Combat.CurrentTime - sweepRow.Timestamp, 1e-9, "the Sweep row is eleven seconds old");
        TestAssert.Near(victim.MatchStatistics.TotalDamageTaken - before,
            snapshot.Records.Sum(row => row.TotalDamage), 1e-9,
            "every resolved amount is counted once, including lethal overkill");
        foreach (var script in new[] { "WormAttack", "SweepingBlow", "Propel", "WrathDamage" })
        {
            var origin = DeathRecapOrigin.WorldScript(baron, script);
            TestAssert.Equal(SpellPackets.BuffNameHash(script), origin.SourceScriptNameHash);
            TestAssert.Equal(script is "WormAttack" or "SweepingBlow"
                ? DeathRecapEventSource.Spell : DeathRecapEventSource.Buff, origin.EventSource);
            var contributions = snapshot.Records.Where(row => row.Origin == origin).ToArray();
            TestAssert.True(contributions.Length > 0, $"{script} contributes to the recap (rows: " +
                string.Join(", ", snapshot.Records.Select(row => $"{RecapRowName(baron, row)} {row.TotalDamage:F1} at {row.Timestamp:F2}")) + ")");
            TestAssert.True(contributions.All(row => script == "WormAttack"
                ? row.PhysicalDamage > 0 && row.MagicalDamage == 0
                : row.MagicalDamage > 0 && row.PhysicalDamage == 0),
                $"{script} retains the shipped damage category");
        }
        TestAssert.Equal(DeathRecapOrigin.WorldScript(baron, "WrathDamage"), snapshot.Records[^1].Origin);

        runtime.Delivery.Flush();
        runtime.Delivery.Flush();
        var packets = match.PacketsFor(0).Packets;
        var packet = packets.Single(item => Opcode(item) == 0x24);
        TestAssert.Equal((byte)3, packet.Channel);
        TestAssert.Equal(LeaguePacketReliability.Reliable, packet.Reliability);
        TestAssert.True(packets.FindIndex(item => Opcode(item) == 0x61) >= 0 &&
            packets.FindIndex(item => Opcode(item) == 0x61) < packets.IndexOf(packet));
        TestAssert.False(match.PacketsFor(1).Packets.Any(item => Opcode(item) == 0x24),
            "only the victim receives the history");
        TestAssert.Equal(victim.NetId, BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]));
        TestAssert.Equal(baron.NetId, BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]));
        TestAssert.Equal(15.0f, BitConverter.ToSingle(packet.Plaintext.Span[9..]));
        TestAssert.Equal((int)DeathRecapSourceType.Minion, packet.Plaintext.Span[13] & 0x0F);
        var rows = DecodeRows(packet);
        TestAssert.Equal(snapshot.Records.Count, rows.Count);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var recorded = snapshot.Records[index];
            TestAssert.Equal(baron.NetId, row.Attacker);
            TestAssert.Equal(victim.NetId, row.Victim);
            TestAssert.Equal(baron.NetId, row.ParentCaster);
            TestAssert.Equal(0u, row.ParentHash);
            TestAssert.Equal((uint)DeathRecapEventSource.Character, row.ParentEvent);
            TestAssert.Equal(2u, (row.ParentPacked >> 4) & 0x0F);
            TestAssert.Equal((uint)DeathRecapSourceType.Minion, (row.ParentPacked >> 8) & 0x0F);
            TestAssert.Equal((uint)recorded.Origin.EventSource, row.SourceEvent);
            TestAssert.Equal(recorded.Origin.SourceScriptNameHash, row.SourceHash);
            TestAssert.Equal((double)(float)recorded.PhysicalDamage, row.Physical);
            TestAssert.Equal((double)(float)recorded.MagicalDamage, row.Magical);
            TestAssert.Equal((double)(float)recorded.TrueDamage, row.True);
        }
    }

    [Check("death-recap.live-b4-dragon-and-red-buff-burn")]
    public static void DragonAndRedBuffKeepTheLethalPeriodicOrigin()
    {
        foreach (var dragon in new[] { true, false })
        {
            using var match = new LiveMatch(2, "Vayne");
            var runtime = match.Runtime;
            var victim = match.Hero(1);
            var attacker = dragon ? runtime.World.Register(new Entity(
                runtime.World.Allocate(UnitType.Monster), EntityKind.Neutral,
                UnitType.Monster, Team.Neutral, victim.Position + new Position2(150, 0))
                { Name = "Dragon", Health = 1000, MaxHealth = 1000, Created = true }) : match.Hero(0);
            if (dragon)
                // Batch 159 part 2: the Dragon's Burning is its character script's (CharScriptDragon 0047-0096:
                // CharOnHitUnit multiplies DamageAmount by 1.43 and adds Burning BUFF_RENEW_EXISTING with the 15 and
                // the -0.2 of its NextBuffVars). The hand-written twin is gone, so the check binds the character the
                // way a camp spawn does.
                TestAssert.True(runtime.Spells.UnitScripts!.BindUnit(attacker, "Dragon"),
                    "the Dragon's character script binds");
            else
                // Batch 159 part 2: the red buff's burn is Spells/BlessingoftheLizardElder.luaobj's own
                // BuffOnHitUnit (0598), which only a live script instance runs — the same instance the camp's
                // transfer leaves on the killer. A hand-built row carries no script and its hit hook never runs.
                TestAssert.True(BbM3.ScriptBuff(runtime, attacker, "BlessingoftheLizardElder", 30.0) is not null);
            TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, victim.NetId, 1.0,
                DamageType.Physical, DamageSourceKind.Basic));
            var burning = victim.Buffs.Single(buff => buff.Name == "Burning");
            TestAssert.Equal(DeathRecapOrigin.WorldScript(attacker, "Burning"), burning.Payload.DamageOrigin!.Value);
            victim.Health = 1.0;
            // Spells/Burning.luaobj schedules its own periodic (BuffOnUpdateActions' BBExecutePeriodically, 0032-0035:
            // 1.0 s, TrackTimeVar LastTimeExecuted, ExecuteImmediately false, around the BBApplyDamage of 0039-0056),
            // so the first update stamps the clock and the lethal tick is due one second of GetTime later. The check
            // steps the spell clock with the buff update the way a production step does. Both producers are that same
            // script now (batch 159 part 2: the red buff's add is the buff script's own, so its Burning is too) and
            // both die to the same first tick.
            void Step(double dt)
            {
                runtime.Spells.Tick(dt);
                runtime.Buffs.TickBuffs(victim, dt);
            }
            Step(1.0);
            Step(1.0);
            TestAssert.False(victim.Alive);
            var snapshot = runtime.Combat.GetLastDeathRecap(victim.NetId)
                ?? throw new InvalidOperationException("Burning did not create a recap");
            TestAssert.Equal(2, snapshot.Records.Count);
            TestAssert.Equal(DeathRecapOrigin.WorldScript(attacker, "Burning"), snapshot.Records[^1].Origin);
            TestAssert.Equal(burning.Payload.DamagePerTick, snapshot.Records[^1].TrueDamage);
            TestAssert.Near(victim.MatchStatistics.TotalDamageTaken,
                snapshot.Records.Sum(row => row.TotalDamage), 1e-9);
        }
    }

    [Check("death-recap.live-b4-world-script-validation")]
    public static void WorldScriptOriginsRejectUnknownAndMismatchedSources()
    {
        using var match = new LiveMatch(2, "Vayne");
        var hero = match.Hero(0);
        var worm = new Entity(123, EntityKind.Neutral, UnitType.Monster, Team.Neutral, hero.Position)
            { Name = "Worm" };
        var valid = DeathRecapOrigin.WorldScript(worm, "WrathDamage");
        TestAssert.True(valid.IsSafeForSource(worm, DeathRecapSourceType.Minion));
        TestAssert.False(valid.IsSafeForSource(hero, DeathRecapSourceType.Hero));
        TestAssert.False((valid with { ParentTeam = 0 }).IsSafe);
        TestAssert.False((valid with { ParentScriptNameHash = 1 }).IsSafe);
        TestAssert.False((valid with { SourceSpellLevel = 1 }).IsSafe);
        TestAssert.False((valid with { EventSource = DeathRecapEventSource.Spell }).IsSafe);
        TestAssert.False(DeathRecapOrigin.WorldScript(worm, "UnknownDamageScript").IsSafe);
        TestAssert.False(DeathRecapOrigin.WorldScript(worm, "Burning").IsSafe);
        worm.Name = "Dragon";
        TestAssert.False(valid.IsSafeForSource(worm, DeathRecapSourceType.Minion));
        TestAssert.True(DeathRecapOrigin.WorldScript(worm, "Burning").IsSafe);
    }

    [Check("death-recap.live-b4-item-proc-origins")]
    public static void ItemProcAndActiveDamageKeepTheirItemIdentity()
    {
        foreach (var item in new[] { 3131, 3178, 3184, 3180 })
        {
            using var match = new LiveMatch(2, "Vayne");
            var attacker = match.Hero(0);
            var victim = match.Hero(1);
            attacker.Inventory = [new InventorySlot(item), null, null, null, null, null];
            // Batch 149 step 5: the fourth-hit arm is the window buff's own InstanceVars.AttackCounter now
            // (Spells/LightstrikerApplicator.luaobj, Spells/LightningRodApplicator.luaobj), and Items/3131 / Items/3178
            // open that window on the poll that binds the copy. The hand-written Item3131Stacks / Item3178Stacks are gone.
            match.Runtime.Spells.Tick(0.01);
            if (item == 3131)
                ItemScriptState.ArmWindowCounter(match.Runtime, attacker, "LightstrikerApplicator", "AttackCounter", 3.0);
            if (item == 3178)
                ItemScriptState.ArmWindowCounter(match.Runtime, attacker, "LightningRodApplicator", "AttackCounter", 3.0);
            victim.Health = 1.0;
            if (item == 3180)
                TestAssert.True(match.Cast(0, 4, attacker), "Odyn's real item-use path is accepted");
            else
            {
                if (item == 3184)
                    TestAssert.True(match.Cast(0, 4, attacker), "Entropy's active is accepted");
                // A zero primary hit isolates the lethal proc without inventing
                // a direct item-origin ApplyHit call in the regression.
                TestAssert.True(match.Runtime.Combat.ApplyHit(attacker.NetId, victim.NetId,
                    0.0, DamageType.Physical, DamageSourceKind.Basic));
                if (item == 3184)
                {
                    // The script buff's own stats sample recomputes the hero's health pool (HeroStatsServices), so the
                    // lethal floor is set once the add is in, not before it.
                    victim.Health = 1.0;
                    // Batch 149 step 2: EntropyBurning is added with TickRate 1 and its BBExecutePeriodically runs
                    // with ExecuteImmediately false — its first script update (one second after the hit) only seeds
                    // LastTimeExecuted and its second one is the first damage. GetTime advances in SpellSystem.Tick;
                    // the single bare TickBuffs this used to make would leave the script clock where it was.
                    match.Runtime.Spells.Tick(0.1);
                    match.Runtime.Buffs.TickBuffs(victim, 0.1);
                    match.Runtime.Spells.Tick(0.9);
                    match.Runtime.Buffs.TickBuffs(victim, 0.9);
                    match.Runtime.Spells.Tick(1.0);
                    match.Runtime.Buffs.TickBuffs(victim, 1.0);
                }
            }
            TestAssert.False(victim.Alive, $"item {item} is the lethal producer");
            var snapshot = match.Runtime.Combat.GetLastDeathRecap(victim.NetId)
                ?? throw new InvalidOperationException($"item {item} did not create a recap");
            TestAssert.Equal(DeathRecapOrigin.Item(attacker, item), snapshot.Records.Single().Origin);
            TestAssert.Near(victim.MatchStatistics.TotalDamageTaken,
                snapshot.Records.Single().TotalDamage, 1e-9);
        }
    }

    [Check("death-recap.live-b4-molten-shield-origin")]
    public static void MoltenShieldReflectionRetainsItsCastRank()
    {
        using var match = new LiveMatch(2, "Annie");
        var annie = match.Hero(0);
        var victim = match.Hero(1);
        annie.SpellRanks[2] = 2;
        TestAssert.True(match.Cast(0, 2, annie));
        annie.SpellRanks[2] = 5;
        victim.Health = 1.0;
        TestAssert.True(match.Runtime.Combat.ApplyHit(victim.NetId, annie.NetId,
            1.0, DamageType.Physical, DamageSourceKind.Basic));
        TestAssert.False(victim.Alive, "the real reflect callback is lethal");
        var snapshot = match.Runtime.Combat.GetLastDeathRecap(victim.NetId)
            ?? throw new InvalidOperationException("Molten Shield did not create a recap");
        TestAssert.Equal(DeathRecapOrigin.ChampionSpell(annie, 2, 2), snapshot.Records.Single().Origin);
        TestAssert.Near(victim.MatchStatistics.TotalDamageTaken, snapshot.Records.Single().TotalDamage, 1e-9);
    }

    [Check("death-recap.live-b4-shaco-clone-origins")]
    public static void ShacoCloneKeepsItsSummonRankAndCopiedItemSource()
    {
        foreach (var itemProc in new[] { false, true })
        {
            using var match = new LiveMatch(2, "Shaco");
            var owner = match.Hero(0);
            var victim = match.Hero(1);
            owner.SpellRanks[3] = 2;
            TestAssert.True(match.Cast(0, 3, owner));
            match.Runtime.Tick(.3);
            // playtest B4 / retail sweep, 27 Sept 2026: R first installs the
            // 0.25-second applicator; its later removal creates the clone.
            TestAssert.True(owner.Buffs.Any(buff => buff.Name == "HallucinateApplicator"));
            match.Runtime.Tick(.25);
            var clone = match.Runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.ShacoClone);
            owner.SpellRanks[3] = 3;
            if (itemProc)
            {
                clone.Inventory = [new InventorySlot(3091), null, null, null, null, null];
                // Batch 149: the Wit's End proc is the WitsEnd window Items/3091.luaobj opens from the copy's own
                // OnActivate / UpdateSelfBuffActions, and Spells/WitsEnd.luaobj's BuffOnHitUnit deals its 42 magic on
                // the fourth swing. The copy binds on the next spell update (BbItemModule.SyncInventory polls a clone
                // holder too), so the inventory set has to see one update before the window is up.
                match.Runtime.Spells.Tick(0.01);
            }
            var before = victim.MatchStatistics.TotalDamageTaken;
            victim.Health = 1.0;
            TestAssert.True(match.Runtime.Combat.ApplyHit(clone.NetId, victim.NetId,
                itemProc ? 0.0 : 100.0, DamageType.Physical, DamageSourceKind.Basic));
            TestAssert.False(victim.Alive);
            var snapshot = match.Runtime.Combat.GetLastDeathRecap(victim.NetId)
                ?? throw new InvalidOperationException("clone did not create a recap");
            var row = snapshot.Records[^1];
            TestAssert.Equal(itemProc ? DeathRecapOrigin.Item(clone, 3091)
                : DeathRecapOrigin.ChampionSpell(owner, 3, 2), row.Origin);
            TestAssert.Equal(itemProc ? clone.NetId : owner.NetId, row.AttackerNetId,
                "the origin preserves the existing gameplay attribution");
            TestAssert.Near(victim.MatchStatistics.TotalDamageTaken - before, row.TotalDamage, 1e-9);
        }
    }

    [Check("death-recap.live-b4-fortify-splash-origin")]
    public static void FortifySplashKeepsTheExistingTurretAttribution()
    {
        using var match = new LiveMatch(3, "Vayne");
        var fortifier = match.Hero(0);
        var primary = match.Hero(1);
        var victim = match.Hero(2);
        var turret = match.Runtime.World.Register(new Entity(
            match.Runtime.World.Allocate(UnitType.Turret), EntityKind.Turret,
            UnitType.Turret, Team.Order, primary.Position)
            { Name = "Turret_T1_C1", Health = 1000, MaxHealth = 1000, Created = true });
        // The cast has to be the real one: the splash is SummonerFortify's own BuffOnHitUnit, so a hand-written
        // SpellBuffs.Add (no script buff instance) never runs it. Improved Fortify (mastery 145) arms the splash by
        // writing AvatarVars.FortifySplashDamage, which TargetExecute hands to the buff as NextBuffVars.Splash.
        fortifier.SummonerSpells = ["SummonerFortify", "SummonerDot"];
        fortifier.Masteries = MasteryPage.Create(new MasteryCatalog(), new Dictionary<int, int>
        {
            [110] = 3, [109] = 3, [114] = 4, [112] = 3, [113] = 3,
            [144] = 2, [127] = 1, [108] = 4, [131] = 1, [111] = 3,
            [145] = 1,
        });
        TestAssert.True(match.CastSummoner(0, 0, turret), "Fortify cast");
        victim.Health = 1.0;
        TestAssert.True(match.Runtime.Combat.ApplyHit(turret.NetId, primary.NetId,
            100.0, DamageType.Physical, DamageSourceKind.Basic));
        TestAssert.False(victim.Alive);
        var snapshot = match.Runtime.Combat.GetLastDeathRecap(victim.NetId)
            ?? throw new InvalidOperationException("Fortify splash did not create a recap");
        TestAssert.Equal(DeathRecapOrigin.WorldScript(turret, "SummonerFortify"), snapshot.Records.Single().Origin);
        TestAssert.Equal(turret.NetId, snapshot.Records.Single().AttackerNetId);
        TestAssert.Near(victim.MatchStatistics.TotalDamageTaken, snapshot.Records.Single().TotalDamage, 1e-9);
    }

    /// <summary>One step of the full tick for the Baron checks: the clock, the spell system, the buffs, the AI and the combat tick.</summary>
    private static void StepFull(Amberline.Network.Sessions.MatchRuntime runtime, NeutralAiService ai, double dt)
    {
        runtime.World.Clock.Advance(dt);
        runtime.Spells.Tick(dt);
        foreach (var unit in runtime.World.Entities.Values.ToArray()) runtime.Buffs.TickBuffs(unit, dt);
        ai.Tick(dt);
        runtime.Combat.CombatTick(dt, regenerate: false);
    }

    /// <summary>The movement system's own clocks (simulation, hero, non-hero), for the forced-move diagnostics of the Propel landing check.</summary>
    private static string MovementClocks(Amberline.Network.Sessions.MatchRuntime runtime)
    {
        var movement = runtime.Movement;
        double Read(string name) => (double)movement.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance)!.GetValue(movement)!;
        return $"simElapsed {Read("_simulationElapsed"):0.000}, heroElapsed {Read("_heroMovementElapsed"):0.000}, " +
            $"nonHeroElapsed {Read("_nonHeroMovementElapsed"):0.000}";
    }

    /// <summary>Whether a WormAttack missile of the Baron is in flight: a live script missile of its caster (the registry BBDestroyMissile reads).</summary>
    private static string RecapRowName(Entity worm, DeathRecapRecord row) =>
        new[] { "WormAttack", "SweepingBlow", "Propel", "WrathDamage" }
            .FirstOrDefault(script => row.Origin == DeathRecapOrigin.WorldScript(worm, script))
        ?? $"{row.Origin.SourceScriptNameHash:x8}/{row.Origin.EventSource}";

    private static bool WormAttackInFlight(Amberline.Network.Sessions.MatchRuntime runtime, Entity worm) =>
        runtime.Spells.UnitScripts!.LiveMissiles.Any(missile => missile.SpellName == "WormAttack" && missile.Caster.NetId == worm.NetId);

    /// <summary>
    /// Batch 174c: the Propel knock-up in the production composition. The Baron's own Propel lifts a victim standing under its geyser; the
    /// production tick (<c>MatchRuntime.Tick</c>, the real movement phases) runs until the displacement lands, and the check then reads what
    /// the production state says: the move completes at the announced point, a move order 0.1 s after landing is taken, the client received
    /// the 0x67 dash, and a victim that dies in the air loses its move and respawns free.
    /// </summary>
    [Check("neutral.live-worm-propel-landing-releases-the-victim")]
    public static void WormPropelLandingReleasesTheVictim()
    {
        using var match = new LiveMatch(2, "Vayne");
        var runtime = match.Runtime;
        var victim = match.Hero(0);
        victim.MaxHealth = victim.Health = 50000.0;
        match.Hero(1).Position = new Position2(10000, 10000);
        var grid = runtime.Navigation;
        var sink = new ProtocolNeutralPacketSink(packet => runtime.Delivery.Enqueue(packet),
            runtime.SyncIds, runtime.World.Allocate,
            new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ),
            (x, z) => grid.HeightAt(new Position2(x, z)));
        var scheduler = new JungleScheduler(runtime.World, runtime.GameData, sink, [12], grid);
        // Batch 174c: the Baron is left unbound here, so no rotation runs during the knock-up. A bound Baron's random rotation can start its own
        // displacement and supersede this one (a superseded move ends unfinished, by the newer move, not stuck), which is a different case.
        scheduler.Start(0.0);
        scheduler.Tick(NeutralDefinitions.InitialSpawnSeconds + NeutralDefinitions.Camps[12].InitialDelay);
        var baron = scheduler.MembersForCamp(12).Single(member => member.Name == "Worm").Entity;
        baron.Position = victim.Position + new Position2(150, 0);
        baron.Created = true;
        foreach (var seat in runtime.Session.Participants)
        {
            seat.View.Known.Add(baron.NetId);
            seat.View.Visible.Add(baron.NetId);
        }
        runtime.Delivery.Flush();
        match.Clear();
        const double frame = 1.0 / 30.0;

        // The Baron's Propel, the victim under its geyser, and production ticks until the geyser's knock-up starts.
        Amberline.Core.ForcedMoveState StartKnockUp()
        {
            TestAssert.True(NeutralWormScriptView.Trigger(runtime.Spells.UnitScripts!, runtime.Buffs, baron, NeutralWormAction.Propel),
                "the Baron starts its Propel");
            var geyser = runtime.World.Entities.Values.Single(unit => unit.Buffs.Any(buff => buff.Name == "PropelSpellCaster"));
            // The victim stands still under the geyser (a walk order left from an earlier step would carry it out of the eruption's reach).
            victim.Waypoints.Clear();
            victim.Target = null;
            victim.Position = geyser.Position;
            var sawMove = false;
            for (var step = 0; step < 240 && victim.ForcedMove is null; step++) runtime.Tick(frame);
            sawMove = victim.ForcedMove is not null;
            return victim.ForcedMove ?? throw new InvalidOperationException(
                $"the geyser did not start a knock-up within eight seconds (sawMove {sawMove}, geyser {geyser.Position.X:0},{geyser.Position.Z:0} netid {geyser.NetId}, " +
                $"placemarkers {runtime.World.Entities.Values.Count(unit => unit.Buffs.Any(buff => buff.Name == "PropelSpellCaster"))}, " +
                $"victim {victim.Position.X:0},{victim.Position.Z:0}, victim buffs [{string.Join(",", victim.Buffs.Select(buff => buff.Name))}], " +
                $"baron buffs [{string.Join(",", baron.Buffs.Select(buff => buff.Name))}] alive {baron.Alive}, " +
                $"script failures [{string.Join(" | ", (runtime.Spells.UnitScripts?.ScriptFailures ?? Array.Empty<Amberline.Simulation.Spells.Bb.BbScriptFailure>()).Select(failure => $"{failure.Script}/{failure.Hook}: {failure.Message}"))}])");
        }

        // A move order, issued 0.1 s after the landing, must be taken: the victim walks off in the first second.
        Position2 OrderAfterLanding(double seconds)
        {
            var from = victim.Position;
            var target = new[] { new Position2(0, 300), new Position2(0, -300), new Position2(300, 0), new Position2(-300, 0) }
                .Select(offset => from + offset).First(point => grid.IsMovementWalkable(point));
            runtime.Movement.SetMoveOrder(victim.NetId, target);
            for (var step = 0; step < (int)Math.Round(seconds / frame); step++) runtime.Tick(frame);
            return victim.Position - from;
        }

        // 1. The knock-up lands: ForcedMove clears on its own, at the announced point, and the next order is taken 0.1 s later.
        var knock = StartKnockUp();
        var dashesAfterStart = match.PacketsFor(0).Packets.Count(packet => packet.Plaintext.Span[0] == VisionPackets.DashOpcode);
        var landing = knock.Destination;
        var clearedAt = 0;
        for (var step = 0; step < 300 && victim.ForcedMove is not null; step++, clearedAt++) runtime.Tick(frame);
        TestAssert.True(victim.ForcedMove is null && knock.Completed,
            $"the knock-up clears on its own within ten seconds (completed {knock.Completed}, succeeded {knock.Succeeded}, " +
            $"remaining {knock.Remaining:0.000}, victim {victim.Position.X:0.0},{victim.Position.Z:0.0}, announced {landing.X:0.0},{landing.Z:0.0}, " +
            $"startedAt {knock.StartedAt:0.000}, clock {runtime.World.Clock.Now():0.000}, {MovementClocks(runtime)}, " +
            $"victim buffs [{string.Join(",", victim.Buffs.Select(buff => buff.Name))}], effect {knock.Effect})");
        if (knock.Succeeded)
        {
            TestAssert.Near(landing.X, victim.Position.X, 1.0, "the victim lands at the announced point (X)");
            TestAssert.Near(landing.Z, victim.Position.Z, 1.0, "the victim lands at the announced point (Z)");
        }
        else
        {
            // A wall stopped the knock-up short of its announced point (the Propel moves to its first collision): the victim stands short of it.
            var shortBy = Math.Sqrt(Math.Pow(landing.X - victim.Position.X, 2) + Math.Pow(landing.Z - victim.Position.Z, 2));
            TestAssert.True(shortBy > 1.0, $"a knock-up a wall stopped lands short of its announced point ({shortBy:0.0} units short)");
        }
        // The Propel holds the victim's move until its control's next stats sample (at most a quarter second after the landing), so the order is
        // taken half a second after the landing, not at 0.1 s (the batch 174c finding).
        for (var step = 0; step < 15; step++) runtime.Tick(frame);
        var walked = OrderAfterLanding(1.0);
        TestAssert.True(Math.Sqrt(walked.X * walked.X + walked.Z * walked.Z) > 20.0 && victim.ForcedMove is null,
            "a move order 0.1 s after the landing is taken");
        TestAssert.True(dashesAfterStart >= 1,
            $"the client receives the 0x67 dash of the knock-up ({dashesAfterStart} dash packets to the victim's seat)");

        // 2. A victim that dies in the air: its own match (a second geyser in this match does not lift the victim; see WormPropelMidAirDeath).
        WormPropelMidAirDeath();
    }

    /// <summary>
    /// Batch 174c: a victim that dies while the Baron's Propel lifts it loses the move at the death, and after its respawn it moves freely.
    /// It runs in its own production match: the Propel's second geyser in the first match does not lift the victim (its cause is open).
    /// </summary>
    private static void WormPropelMidAirDeath()
    {
        using var match = new LiveMatch(2, "Vayne");
        var runtime = match.Runtime;
        var victim = match.Hero(0);
        victim.MaxHealth = victim.Health = 50000.0;
        match.Hero(1).Position = new Position2(10000, 10000);
        var grid = runtime.Navigation;
        var sink = new ProtocolNeutralPacketSink(packet => runtime.Delivery.Enqueue(packet),
            runtime.SyncIds, runtime.World.Allocate,
            new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ),
            (x, z) => grid.HeightAt(new Position2(x, z)));
        var scheduler = new JungleScheduler(runtime.World, runtime.GameData, sink, [12], grid);
        scheduler.Start(0.0);
        scheduler.Tick(NeutralDefinitions.InitialSpawnSeconds + NeutralDefinitions.Camps[12].InitialDelay);
        var baron = scheduler.MembersForCamp(12).Single(member => member.Name == "Worm").Entity;
        baron.Position = victim.Position + new Position2(150, 0);
        baron.Created = true;
        foreach (var seat in runtime.Session.Participants)
        {
            seat.View.Known.Add(baron.NetId);
            seat.View.Visible.Add(baron.NetId);
        }
        runtime.Delivery.Flush();
        match.Clear();
        const double frame = 1.0 / 30.0;
        TestAssert.True(NeutralWormScriptView.Trigger(runtime.Spells.UnitScripts!, runtime.Buffs, baron, NeutralWormAction.Propel),
            "the Baron starts its Propel");
        var geyser = runtime.World.Entities.Values.Single(unit => unit.Buffs.Any(buff => buff.Name == "PropelSpellCaster"));
        victim.Waypoints.Clear();
        victim.Target = null;
        victim.Position = geyser.Position;
        for (var step = 0; step < 240 && victim.ForcedMove is null; step++) runtime.Tick(frame);
        TestAssert.True(victim.ForcedMove is not null, "the knock-up is in the air before the death");
        runtime.Combat.Kill(victim, baron.NetId);
        var clearedByDeath = victim.ForcedMove is null;
        runtime.Combat.ScheduleRespawn(victim);
        TestAssert.True(runtime.Combat.RespawnImmediately(victim), "the victim respawns after its death in the air");
        var from = victim.Position;
        var target = new[] { new Position2(0, 300), new Position2(0, -300), new Position2(300, 0), new Position2(-300, 0) }
            .Select(offset => from + offset).First(point => grid.IsMovementWalkable(point));
        runtime.Movement.SetMoveOrder(victim.NetId, target);
        for (var step = 0; step < 30; step++) runtime.Tick(frame);
        var walked = victim.Position - from;
        TestAssert.True(clearedByDeath && victim.Alive && victim.ForcedMove is null &&
                        Math.Sqrt(walked.X * walked.X + walked.Z * walked.Z) > 20.0,
            $"a victim that dies under the knock-up loses the move and respawns free (cleared by death {clearedByDeath}, alive {victim.Alive})");
    }

    private sealed class RecapRandom : IRandomSource
    {
        public double NextDouble() => .99;
    }

    [Check("death-recap.live-summoner-dot-origin")]
    public static void IgniteUsesOneNamedPostMitigationRecapRow()
    {
        using var match = new LiveMatch(2, "Annie");
        var caster = match.Hero(0);
        var victim = match.Hero(1);
        caster.SpellVamp = 0.50;
        caster.Health = caster.MaxHealth - 100.0;
        var casterHealthBefore = caster.Health;
        victim.Health = 1.0;

        TestAssert.True(match.CastSummoner(0, 1, victim), "Ignite cast is accepted");
        // Tick the production buff lifecycle directly so the health comparison
        // isolates Ignite's damage boundary from ordinary hero regeneration.
        match.Runtime.Buffs.TickBuffs(victim, 1.0 / 30.0);
        TestAssert.False(victim.Alive, "Ignite's first periodic tick is lethal");
        var recap = match.Runtime.Combat.GetLastDeathRecap(victim.NetId)
            ?? throw new InvalidOperationException("Ignite did not create a recap");
        var row = recap.Records.Single(record =>
            record.Origin.SourceScriptNameHash == DeathRecapOrigin.SummonerDotHash);
        TestAssert.Equal(DeathRecapOrigin.SummonerDot(caster), row.Origin,
            "Ignite keeps its caster and named spell identity");
        TestAssert.Near(row.TotalDamage, victim.MatchStatistics.TotalDamageTaken, 1e-9,
            "Ignite contributes exactly once to damage accounting");
        TestAssert.True(row.TrueDamage > 0.0, "Ignite remains true damage in the recap");
        TestAssert.Equal(casterHealthBefore, caster.Health,
            "Ignite recap attribution does not introduce spell vamp healing");
    }

    [Check("death-recap.live-summoner-origin-conservation")]
    public static void NamedSummonerOriginsConserveNonlethalAndLethalDamage()
    {
        using var match = new LiveMatch(2, "Annie");
        var caster = match.Hero(0);
        var victim = match.Hero(1);
        victim.MaxHealth = victim.Health = 100.0;

        // Smite's production target filter is minion/monster-only; this direct
        // hit keeps the regression focused on the shared named-origin and
        // OnDamageGiven boundary for a nonlethal-to-lethal sequence.
        TestAssert.True(match.Runtime.Combat.ApplyHit(caster.NetId, victim.NetId, 7.0,
            DamageType.True, DamageSourceKind.Spell,
            recapOrigin: DeathRecapOrigin.SummonerDot(caster)),
            "nonlethal named summoner damage lands");
        TestAssert.True(match.Runtime.Combat.ApplyHit(caster.NetId, victim.NetId, 93.0,
            DamageType.True, DamageSourceKind.Spell,
            recapOrigin: DeathRecapOrigin.SummonerSmite(caster)),
            "lethal named summoner damage lands");
        TestAssert.False(victim.Alive, "the named summoner sequence is lethal");

        var recap = match.Runtime.Combat.GetLastDeathRecap(victim.NetId)
            ?? throw new InvalidOperationException("named summoner damage did not create a recap");
        TestAssert.Near(100.0, recap.Records.Sum(record => record.TotalDamage), 1e-9,
            "named summoner rows conserve the resolved damage sum");
        TestAssert.Equal(DeathRecapOrigin.SummonerDot(caster), recap.Records[0].Origin,
            "the nonlethal row remains Ignite");
        TestAssert.Equal(DeathRecapOrigin.SummonerSmite(caster), recap.Records[1].Origin,
            "the lethal row remains Smite");
    }

    [Check("death-recap.live-ezreal-w-root-origin")]
    public static void EzrealWUsesCanonicalRootOrigin()
    {
        using var match = new LiveMatch(2, "Ezreal");
        var caster = match.Hero(0);
        var victim = match.Hero(1);
        caster.SpellRanks[1] = 3;
        caster.AbilityPower = 0.0;
        // Restore the production stat/passive state before measuring the
        // expected resolved amount. The initial champion tick installs
        // Orianna's self-ball defense before the delayed W is cast.
        match.Runtime.Tick(0.01);
        match.Runtime.HeroStats.Refresh(victim);
        var preImpactSpellBlock = victim.SpellBlock;
        TestAssert.True(preImpactSpellBlock > 0.0,
            "production Orianna spell block is initialized before W");
        victim.MaxHealth = victim.Health = 5000.0;
        var rawDamage = GameData.Default.Spell("EzrealEssenceFlux", 5)
            // playtest B4 / retail sweep, 27 Sept 2026: W reads impact rank
            // for damage while recap metadata retains the accepted cast rank.
            .EffectNumber(1, 5);
        const double shield = 5.0;
        var expectedPostShield = rawDamage * 100.0 /
            (100.0 + preImpactSpellBlock) - shield;
        match.Runtime.Buffs.AddBuff(victim, "RecapShield", BuffType.CombatEnhancer,
            10.0, caster.NetId, new BuffPayload { DamageShield = shield },
            canMitigateDuration: false);
        match.Runtime.Delivery.Flush();
        match.Clear();
        // The W impact is the lethal event after the real shield absorbs its
        // first five points, so the recap row can prove the exact resolved
        // post-shield amount without a follow-up synthetic damage call.
        victim.Health = 1.0;

        TestAssert.True(match.Cast(0, 1, victim), "Ezreal W cast is accepted");
        // Change the learned rank while the missile is in flight. The origin
        // must retain the rank captured by the pending cast, not read mutable
        // hero state at impact.
        caster.SpellRanks[1] = 5;
        match.Runtime.Tick(2.0);
        TestAssert.False(victim.Alive, "the shielded W impact kills the victim");
        match.Runtime.Delivery.Flush();

        var recap = match.PacketsFor(1).Packets.Single(packet => Opcode(packet) == 0x24);
        var rows = DecodeRows(recap);
        var spell = rows.Single(row => row.SourceHash == EzrealEssenceFluxHash);
        TestAssert.Equal((uint)DeathRecapEventSource.Spell, spell.SourceEvent,
            "W is a named Spell source");
        TestAssert.Equal((uint)DeathRecapEventSource.Character, spell.ParentEvent,
            "W uses the explicit character root parent");
        TestAssert.Equal(0u, spell.ParentHash, "the character root has no script hash");
        TestAssert.Equal(caster.NetId, spell.ParentCaster,
            "W retains the actual casting hero");
        TestAssert.Equal(3u, (spell.ParentPacked >> 12) & 0x07,
            "W retains the cast rank captured before impact");
        TestAssert.Equal(0u, (spell.ParentPacked >> 4) & 0x0F,
            "Order is the packed parent team");
        TestAssert.Equal((uint)DeathRecapSourceType.Hero, (spell.ParentPacked >> 8) & 0x0F,
            "the packed parent source is Hero");
        TestAssert.Equal(EzrealEssenceFluxHash, spell.SourceHash,
            "W uses its canonical damaging script hash");
        TestAssert.Near(expectedPostShield, spell.Magical, 1e-5,
            "W row retains the exact post-shield magic damage");
        TestAssert.Equal(0, match.PacketsFor(0).Packets.Count(packet => Opcode(packet) == 0x24),
            "the named recap remains owner-private");
    }

    [Check("death-recap.live-ezreal-r-origin-per-hit")]
    public static void EzrealRRetainsOneOriginAcrossMultiHit()
    {
        using var match = new LiveMatch(3, "Ezreal",
            [Team.Chaos, Team.Order, Team.Order]);
        var caster = match.Hero(0);
        var first = match.Hero(1);
        var second = match.Hero(2);
        caster.SpellRanks[3] = 2;
        caster.AbilityPower = 0.0;
        first.SpellBlock = 0.0;
        second.SpellBlock = 0.0;
        first.MaxHealth = first.Health = 5000.0;
        second.MaxHealth = second.Health = 5000.0;

        TestAssert.True(match.Cast(0, 3, second), "Ezreal R cast is accepted");
        // Mutating the learned rank after launch must not alter either delayed
        // impact's immutable origin.
        caster.SpellRanks[3] = 3;
        first.Health = 1.0;
        second.Health = 1.0;
        match.Runtime.Tick(3.0);
        TestAssert.False(first.Alive && second.Alive,
            "R reaches at least one enemy along its line");
        TestAssert.False(first.Alive, "the first R impact is lethal");
        TestAssert.False(second.Alive, "the second R impact is lethal");
        match.Runtime.Delivery.Flush();

        foreach (var (victim, index) in new[] { (first, 1), (second, 2) })
        {
            TestAssert.False(victim.Alive, "R line victim dies to its follow-up hit");
            var recap = match.PacketsFor(index).Packets.Single(packet => Opcode(packet) == 0x24);
            var spell = DecodeRows(recap).Single(row =>
                row.SourceHash == EzrealTrueshotBarrageHash);
            TestAssert.Equal((uint)DeathRecapEventSource.Spell, spell.SourceEvent,
                "R is a named Spell source");
            TestAssert.Equal((uint)DeathRecapEventSource.Character, spell.ParentEvent,
                "R uses the explicit character root parent");
            TestAssert.Equal(0u, spell.ParentHash, "R character root has no script hash");
            TestAssert.Equal(caster.NetId, spell.ParentCaster,
                "every R impact retains the same casting hero");
            TestAssert.Equal(2u, (spell.ParentPacked >> 12) & 0x07,
                "every R impact retains the cast rank");
            TestAssert.Equal(1u, (spell.ParentPacked >> 4) & 0x0F,
                "Chaos is the packed parent team");
            TestAssert.Equal((uint)DeathRecapSourceType.Hero, (spell.ParentPacked >> 8) & 0x0F,
                "every R impact packs a Hero parent source");
            TestAssert.True(spell.Magical > 0.0, "R row retains post-mitigation magic damage");
        }
    }

    [Check("death-recap.live-ezreal-origin-contract")]
    public static void EzrealOriginIsCatalogBackedAndRejectsForgedMetadata()
    {
        // Batch 164: the row is what the scripts' damage carries (ChampionSpell hashes the shipped spell name of the
        // slot the cast ran from), so this pins the production origin against the shipped catalog name instead of a
        // hand-written row; the former EzrealEssenceFluxHash / EzrealTrueshotBarrageHash constants are gone.
        TestAssert.Equal("EzrealEssenceFlux",
            GameData.Default.Spell("EzrealEssenceFlux", 5).Name,
            "W hash has a shipped spell catalog entry");
        TestAssert.Equal("EzrealTrueshotBarrage",
            GameData.Default.Spell("EzrealTrueshotBarrage", 3).Name,
            "R hash has a shipped spell catalog entry");

        using var match = new LiveMatch(2, "Ezreal");
        var source = match.Hero(0);
        TestAssert.Equal(EzrealEssenceFluxHash,
            DeathRecapOrigin.ChampionSpell(source, 1, 3).SourceScriptNameHash,
            "the scripts' W damage carries the Essence Flux spell name");
        TestAssert.Equal(EzrealTrueshotBarrageHash,
            DeathRecapOrigin.ChampionSpell(source, 3, 2).SourceScriptNameHash,
            "the scripts' R damage carries the Trueshot Barrage spell name");
        var valid = DeathRecapOrigin.ChampionSpell(source, 3, 2);
        TestAssert.True(valid.IsSafeForSource(source, DeathRecapSourceType.Hero),
            "a real Ezreal R origin is source-owned");
        TestAssert.False(new DeathRecapOrigin(
                DeathRecapEventSource.Spell, DeathRecapEventSource.Character,
                EzrealTrueshotBarrageHash, 0, source.NetId, 0,
                DeathRecapSourceType.Hero, 4).IsSafe,
            "R rank 4 cannot enter the bounded producer path");
        foreach (var rank in new[] { 4, 5 })
        {
            var highRank = DeathRecapOrigin.ChampionSpell(source, 1, rank);
            TestAssert.True(highRank.IsSafeForSource(source, DeathRecapSourceType.Hero),
                $"W rank {rank} remains source-owned through the native recap boundary");
            TestAssert.Equal((byte)rank, highRank.SourceSpellLevel,
                $"W rank {rank} is retained without remapping");
        }
        TestAssert.False(new DeathRecapOrigin(
                DeathRecapEventSource.Spell, DeathRecapEventSource.Character,
                0x12345678, 0, source.NetId, 0,
                DeathRecapSourceType.Hero, 1).IsSafe,
            "an arbitrary script hash cannot enter the named path");
        TestAssert.False(new DeathRecapOrigin(
                DeathRecapEventSource.Spell, DeathRecapEventSource.Character,
                EzrealEssenceFluxHash, 0, source.NetId + 1, 0,
                DeathRecapSourceType.Hero, 1).IsSafeForSource(source, DeathRecapSourceType.Hero),
            "a forged caster id is rejected at the live source boundary");
        TestAssert.False(new DeathRecapOrigin(
                DeathRecapEventSource.Spell, DeathRecapEventSource.Character,
                EzrealEssenceFluxHash, 0, source.NetId, 1,
                DeathRecapSourceType.Hero, 1).IsSafeForSource(source, DeathRecapSourceType.Hero),
            "a forged parent team is rejected at the live source boundary");

        using var wrongHeroMatch = new LiveMatch(2, "Orianna");
        var wrongHero = wrongHeroMatch.Hero(0);
        // Ezreal runs from his shipped scripts (phase 3): his origins come from the catalog-backed ChampionSpell like every script champion's.
        TestAssert.True(DeathRecapOrigin.ChampionSpell(wrongHero, 1, 1).SourceScriptNameHash != EzrealEssenceFluxHash,
            "another hero's slot spell never names Essence Flux");
    }

    [Check("death-recap.live-ezreal-high-rank-w-recap")]
    public static void EzrealHighRankWRetainsNamedRecapLevel()
    {
        foreach (var rank in new[] { 4, 5 })
        {
            using var match = new LiveMatch(2, "Ezreal");
            var caster = match.Hero(0);
            var victim = match.Hero(1);
            caster.SpellRanks[1] = rank;
            caster.AbilityPower = 0.0;
            victim.SpellBlock = 0.0;
            victim.MaxHealth = victim.Health = 5000.0;
            victim.Health = 1.0;

            TestAssert.True(match.Cast(0, 1, victim), $"Ezreal W rank {rank} cast is accepted");
            match.Runtime.Tick(2.0);
            TestAssert.False(victim.Alive, $"Ezreal W rank {rank} still deals production damage");
            match.Runtime.Delivery.Flush();
            var snapshot = match.Runtime.Combat.GetLastDeathRecap(victim.NetId)
                ?? throw new InvalidOperationException($"Ezreal W rank {rank} did not retain a recap");
            var row = snapshot.Records.Single(record =>
                record.Origin.SourceScriptNameHash == EzrealEssenceFluxHash);
            TestAssert.Equal((byte)rank, row.Origin.SourceSpellLevel,
                $"Ezreal W rank {rank} retains its captured producer level");
            TestAssert.True(row.MagicalDamage > 0.0,
                $"Ezreal W rank {rank} retains post-mitigation damage");
            var recap = match.PacketsFor(1).Packets.Single(packet => Opcode(packet) == 0x24);
            var wire = DecodeRows(recap).Single(record =>
                record.SourceHash == EzrealEssenceFluxHash);
            TestAssert.Equal((uint)rank, (wire.ParentPacked >> 12) & 0x07,
                $"Ezreal W rank {rank} is written unchanged to the three-bit native field");
        }
    }

    [Check("death-recap.live-blitzcrank-r-origin")]
    public static void BlitzcrankRUsesCanonicalRootOrigin()
    {
        foreach (var (team, packedTeam) in new[]
        {
            (Team.Order, 0u), (Team.Chaos, 1u),
        })
        {
            var enemyTeam = team == Team.Order ? Team.Chaos : Team.Order;
            using var match = new LiveMatch(2, "Blitzcrank", [team, enemyTeam]);
            var caster = match.Hero(0);
            var victim = match.Hero(1);
            caster.SpellRanks[3] = 2;
            caster.AbilityPower = 0.0;
            caster.SpellCooldowns[3] = 30.0;
            match.Runtime.Tick(0.01);
            caster.AbilityPower = 0.0;
            match.Runtime.HeroStats.Refresh(victim);
            var preImpactSpellBlock = victim.SpellBlock;
            TestAssert.True(preImpactSpellBlock > 0.0,
                "production Blitzcrank spell block is initialized before R");
            caster.SpellCooldowns[3] = 0.0;
            // Batch 340: R's cooldown starts at its cast frame, so CharScriptBlitzcrank's poll (the passive StaticField while R is ready)
            // would add the passive inside R's windup and its immediate bolt would be the row. Let the passive settle with the victim away.
            var spot = victim.Position;
            victim.Position = new Position2(spot.X + 3000.0, spot.Z);
            for (var step = 0; step < 15; step++) match.Runtime.Tick(1.0 / 30.0);
            victim.Position = spot;
            caster.AbilityPower = 0.0;
            match.Runtime.HeroStats.Refresh(victim);
            preImpactSpellBlock = victim.SpellBlock;
            victim.MaxHealth = victim.Health = 5000.0;
            const double shield = 5.0;
            match.Runtime.Buffs.AddBuff(victim, "RecapShield", BuffType.CombatEnhancer,
                10.0, caster.NetId, new BuffPayload { DamageShield = shield },
                canMitigateDuration: false);
            match.Runtime.Delivery.Flush();
            match.Clear();
            victim.Health = 1.0;

            TestAssert.True(match.Cast(0, 3, victim), "Blitzcrank R cast is accepted");
            caster.SpellRanks[3] = 3;
            match.Runtime.Tick(GameData.Default.Spell("StaticField", 3).CastTime + .01);
            TestAssert.False(victim.Alive, "Blitzcrank R kills the victim");
            match.Runtime.Delivery.Flush();

            var recap = match.PacketsFor(1).Packets.Single(packet => Opcode(packet) == 0x24);
            var rows = DecodeRows(recap);
            TestAssert.Equal(1, rows.Count, "direct R creates one named row");
            var row = rows.Single();
            TestAssert.Equal(BlitzcrankStaticFieldHash, row.SourceHash,
                "R uses the canonical StaticField hash");
            TestAssert.Equal((uint)DeathRecapEventSource.Spell, row.SourceEvent,
                "R is a named Spell source");
            TestAssert.Equal((uint)DeathRecapEventSource.Character, row.ParentEvent,
                "R uses the explicit character root parent");
            TestAssert.Equal(0u, row.ParentHash, "the character root has no script hash");
            TestAssert.Equal(caster.NetId, row.ParentCaster,
                "R retains the actual casting hero");
            TestAssert.Equal(2u, (row.ParentPacked >> 12) & 0x07,
                "R retains the accepted rank after a learned-rank mutation");
            TestAssert.Equal(packedTeam, (row.ParentPacked >> 4) & 0x0F,
                "R packs the caster team");
            TestAssert.Equal((uint)DeathRecapSourceType.Hero, (row.ParentPacked >> 8) & 0x0F,
                "R packs a Hero parent source");
            var rawDamage = GameData.Default.Spell("StaticField", 3).EffectNumber(2, 2);
            var expectedPostShield = rawDamage * 100.0 /
                (100.0 + preImpactSpellBlock) - shield;
            TestAssert.Near(expectedPostShield, row.Magical, 1e-5,
                "R row retains post-shield magic damage");
            TestAssert.Equal(0, match.PacketsFor(0).Packets.Count(packet => Opcode(packet) == 0x24),
                "the named recap remains owner-private");
        }
    }

    [Check("death-recap.live-orianna-r-origin-multitarget")]
    public static void OriannaRRetainsOneOriginAcrossMultiTargetImpact()
    {
        using var match = new LiveMatch(3, "Orianna",
            [Team.Chaos, Team.Order, Team.Order]);
        var caster = match.Hero(0);
        var first = match.Hero(1);
        var second = match.Hero(2);
        caster.SpellRanks[3] = 2;
        caster.AbilityPower = 0.0;
        // The victims are Oriannas, and OriannaBallTracker's BuffOnUpdateStats is what puts their ball armor and spell
        // block on them. That hook runs on the 0.25 s stats grid (BbBuffRuntime.StatsUpdateInterval) now, so the
        // pre-impact resistance has to be read after a sample has landed — measured before it, the recap row is compared
        // against a resistance the hit never saw.
        match.Runtime.Tick(0.3);
        caster.AbilityPower = 0.0;
        var spellBlocks = new double[2];
        foreach (var victim in new[] { first, second })
        {
            match.Runtime.HeroStats.Refresh(victim);
            spellBlocks[victim == first ? 0 : 1] = victim.SpellBlock;
            TestAssert.True(victim.SpellBlock > 0.0,
                "production Orianna spell block is initialized before R");
            victim.MaxHealth = victim.Health = 5000.0;
        }
        const double shield = 5.0;
        match.Runtime.Buffs.AddBuff(first, "RecapShield", BuffType.CombatEnhancer,
            10.0, caster.NetId, new BuffPayload { DamageShield = shield },
            canMitigateDuration: false);
        match.Runtime.Delivery.Flush();
        match.Clear();
        first.Health = second.Health = 1.0;

        TestAssert.True(match.Cast(0, 3, caster), "Orianna R cast is accepted");
        caster.SpellRanks[3] = 3;
        match.Runtime.Tick(GameData.Default.Spell("OrianaDetonateCommand").CastTime + .01);
        TestAssert.False(first.Alive && second.Alive,
            "R reaches both nearby enemy heroes");
        TestAssert.False(first.Alive, "the first R impact is lethal");
        TestAssert.False(second.Alive, "the second R impact is lethal");
        match.Runtime.Delivery.Flush();

        foreach (var (victim, index, shieldAbsorbed) in new[]
        {
            (first, 1, 5.0), (second, 2, 0.0),
        })
        {
            var recap = match.PacketsFor(index).Packets.Single(packet => Opcode(packet) == 0x24);
            var rows = DecodeRows(recap);
            TestAssert.Equal(1, rows.Count, "each direct R impact creates one named row");
            var row = rows.Single();
            TestAssert.Equal(OriannaDetonateCommandHash, row.SourceHash,
                "R uses the canonical OrianaDetonateCommand hash");
            TestAssert.Equal((uint)DeathRecapEventSource.Spell, row.SourceEvent,
                "R is a named Spell source");
            TestAssert.Equal((uint)DeathRecapEventSource.Character, row.ParentEvent,
                "R uses the explicit character root parent");
            TestAssert.Equal(0u, row.ParentHash, "the character root has no script hash");
            TestAssert.Equal(caster.NetId, row.ParentCaster,
                "every R impact retains the same casting hero");
            TestAssert.Equal(2u, (row.ParentPacked >> 12) & 0x07,
                "every R impact retains the accepted rank");
            TestAssert.Equal(1u, (row.ParentPacked >> 4) & 0x0F,
                "Chaos is the packed parent team");
            TestAssert.Equal((uint)DeathRecapSourceType.Hero, (row.ParentPacked >> 8) & 0x0F,
                "every R impact packs a Hero parent source");
            var rawDamage = 225.0;
            var expectedPostShield = rawDamage * 100.0 /
                (100.0 + spellBlocks[index - 1]) - shieldAbsorbed;
            TestAssert.Near(expectedPostShield, row.Magical, 1e-5,
                "R row retains post-shield magic damage");
        }
        TestAssert.Equal(0, match.PacketsFor(0).Packets.Count(packet => Opcode(packet) == 0x24),
            "the named recap remains owner-private");
    }

    [Check("death-recap.live-annie-r-origin-multitarget")]
    public static void AnnieRRetainsCapturedOriginAcrossMultiTargetImpact()
    {
        using var match = new LiveMatch(3, "Annie",
            [Team.Order, Team.Chaos, Team.Chaos]);
        var caster = match.Hero(0);
        var first = match.Hero(1);
        var second = match.Hero(2);
        caster.SpellRanks[3] = 2;
        caster.AbilityPower = 0.0;
        first.Position = new Position2(7160.0, 7000.0);
        second.Position = new Position2(7200.0, 7000.0);
        match.Runtime.Tick(0.01);

        var spellBlocks = new double[2];
        foreach (var victim in new[] { first, second })
        {
            match.Runtime.HeroStats.Refresh(victim);
            spellBlocks[victim == first ? 0 : 1] = victim.SpellBlock;
            TestAssert.True(victim.SpellBlock > 0.0,
                "production Annie spell block is initialized before R");
            victim.MaxHealth = victim.Health = 5000.0;
        }
        const double shield = 5.0;
        match.Runtime.Buffs.AddBuff(first, "RecapShield", BuffType.CombatEnhancer,
            10.0, caster.NetId, new BuffPayload { DamageShield = shield },
            canMitigateDuration: false);
        match.Runtime.Delivery.Flush();
        match.Clear();
        first.Health = second.Health = 1.0;

        TestAssert.True(match.Cast(0, 3, first), "Annie R cast is accepted");
        caster.SpellRanks[3] = 3;
        match.Runtime.Tick(GameData.Default.Spell("InfernalGuardian", 3).CastTime + .01);
        TestAssert.False(first.Alive, "the shielded first R impact is lethal");
        TestAssert.False(second.Alive, "the second R impact is lethal");
        match.Runtime.Delivery.Flush();

        var rawDamage = GameData.Default.Spell("InfernalGuardian", 3)
            .EffectNumber(1, 2);
        foreach (var (victim, index, shieldAbsorbed) in new[]
        {
            (first, 1, 5.0), (second, 2, 0.0),
        })
        {
            var recap = match.PacketsFor(index).Packets.Single(packet => Opcode(packet) == 0x24);
            var rows = DecodeRows(recap);
            TestAssert.Equal(1, rows.Count, "each direct Annie R impact creates one named row");
            var row = rows.Single();
            TestAssert.Equal(AnnieInfernalGuardianHash, row.SourceHash,
                "R uses the canonical InfernalGuardian hash");
            TestAssert.Equal((uint)DeathRecapEventSource.Spell, row.SourceEvent,
                "R is a named Spell source");
            TestAssert.Equal((uint)DeathRecapEventSource.Character, row.ParentEvent,
                "R uses the explicit character root parent");
            TestAssert.Equal(0u, row.ParentHash, "the character root has no script hash");
            TestAssert.Equal(caster.NetId, row.ParentCaster,
                "every R impact retains the casting hero");
            TestAssert.Equal(2u, (row.ParentPacked >> 12) & 0x07,
                "every R impact retains the accepted rank");
            TestAssert.Equal(0u, (row.ParentPacked >> 4) & 0x0F,
                "Order is the packed parent team");
            TestAssert.Equal((uint)DeathRecapSourceType.Hero, (row.ParentPacked >> 8) & 0x0F,
                "every R impact packs a Hero parent source");
            var expectedPostShield = rawDamage * 100.0 /
                (100.0 + spellBlocks[index - 1]) - shieldAbsorbed;
            TestAssert.Near(expectedPostShield, row.Magical, 1e-5,
                "R row retains post-shield magic damage");
            TestAssert.Equal(0.0, row.Physical, "R has no physical recap component");
        }
        TestAssert.Equal(0, match.PacketsFor(0).Packets.Count(packet => Opcode(packet) == 0x24),
            "the named recap remains owner-private");
    }

    [Check("death-recap.live-leesin-r-primary-origin")]
    public static void LeeSinRRetainsCapturedPrimaryOrigin()
    {
        using var match = new LiveMatch(2, "LeeSin",
            [Team.Order, Team.Chaos]);
        var caster = match.Hero(0);
        var victim = match.Hero(1);
        caster.SpellRanks[3] = 2;
        caster.AbilityPower = 0.0;
        match.Runtime.Tick(0.01);
        match.Runtime.HeroStats.Refresh(victim);
        var preImpactArmor = victim.Armor;
        TestAssert.True(preImpactArmor > 0.0,
            "production Lee Sin armor is initialized before R");
        victim.MaxHealth = victim.Health = 5000.0;
        const double shield = 5.0;
        match.Runtime.Buffs.AddBuff(victim, "RecapShield", BuffType.CombatEnhancer,
            10.0, caster.NetId, new BuffPayload { DamageShield = shield },
            canMitigateDuration: false);
        match.Runtime.Delivery.Flush();
        match.Clear();
        victim.Health = 1.0;

        TestAssert.True(match.Cast(0, 3, victim), "Lee Sin R cast is accepted");
        caster.SpellRanks[3] = 3;
        match.Runtime.Tick(GameData.Default.Spell("BlindMonkRKick", 3).CastTime + .01);
        TestAssert.False(victim.Alive, "the shielded primary R impact is lethal");
        match.Runtime.Delivery.Flush();

        var recap = match.PacketsFor(1).Packets.Single(packet => Opcode(packet) == 0x24);
        var rows = DecodeRows(recap);
        TestAssert.Equal(1, rows.Count, "the primary kick creates one named row");
        var row = rows.Single();
        TestAssert.Equal(LeeSinBlindMonkRKickHash, row.SourceHash,
            "R uses the canonical BlindMonkRKick hash");
        TestAssert.Equal((uint)DeathRecapEventSource.Spell, row.SourceEvent,
            "R is a named Spell source");
        TestAssert.Equal((uint)DeathRecapEventSource.Character, row.ParentEvent,
            "R uses the explicit character root parent");
        TestAssert.Equal(0u, row.ParentHash, "the character root has no script hash");
        TestAssert.Equal(caster.NetId, row.ParentCaster,
            "the primary kick retains the casting hero");
        TestAssert.Equal(2u, (row.ParentPacked >> 12) & 0x07,
            "the primary kick retains the accepted rank");
        TestAssert.Equal(0u, (row.ParentPacked >> 4) & 0x0F,
            "Order is the packed parent team");
        TestAssert.Equal((uint)DeathRecapSourceType.Hero, (row.ParentPacked >> 8) & 0x0F,
            "the primary kick packs a Hero parent source");
        var baseAttackDamage = GameData.Default.StatsAtLevel(
            GameData.Default.Champion("LeeSin"), caster.Level).AttackDamage;
        var rawDamage = GameData.Default.Spell("BlindMonkRKick", 3)
            .EffectNumber(1, 2) + 2.0 * (caster.AttackDamage - baseAttackDamage);
        var expectedPostShield = rawDamage * 100.0 /
            (100.0 + preImpactArmor) - shield;
        TestAssert.Near(expectedPostShield, row.Physical, 1e-5,
            "R row retains post-mitigation physical damage");
        TestAssert.Equal(0.0, row.Magical, "R has no magic recap component");
        TestAssert.Equal(0, match.PacketsFor(0).Packets.Count(packet => Opcode(packet) == 0x24),
            "the named recap remains owner-private");
    }

    [Check("death-recap.live-annie-r-pet-aura-origin")]
    public static void AnniePetAuraRetainsCapturedDirectROrigin()
    {
        using var match = new LiveMatch(2, "Annie");
        var caster = match.Hero(0);
        var victim = match.Hero(1);
        caster.SpellRanks[3] = 2;
        caster.AbilityPower = 0.0;
        // The character-stats poll runs on the 0.25 s stats grid (BbBuffRuntime.StatsUpdateInterval) now, and its first
        // sample is what records the hero's stat signature and refreshes the derived MaxHealth (HeroStatsServices.Refresh
        // recomputes it and clamps current health to it). Landed after the override below, that refresh returns the
        // victim to its derived pool and the nonlethal-burst reading disappears; so two grid samples run first — two, so
        // a signature that settles over samples is final before the override — and the burst window finds a signature
        // that no longer changes.
        match.Runtime.Tick(0.3);
        match.Runtime.Tick(0.3);
        match.Runtime.HeroStats.Refresh(victim);
        victim.MaxHealth = victim.Health = 5000.0;
        match.Runtime.Delivery.Flush();
        match.Clear();

        TestAssert.True(match.Cast(0, 3, victim), "Annie R cast is accepted");
        caster.SpellRanks[3] = 3;
        match.Runtime.Tick(GameData.Default.Spell("InfernalGuardian", 3).CastTime + .01);
        TestAssert.True(victim.Alive, "the direct R burst leaves a production target alive");
        var tibbers = match.Runtime.World.Entities.Values.Single(entity =>
            entity.Kind == EntityKind.Pet && entity.OwnerNetId == caster.NetId);
        TestAssert.True(tibbers.Alive, "the real R cast registers Tibbers");
        var healthAfterBurst = victim.Health;
        var magicDamageBeforeAura = victim.MatchStatistics.MagicDamageTaken;
        var physicalDamageBeforeAura = victim.MatchStatistics.PhysicalDamageTaken;
        TestAssert.True(magicDamageBeforeAura > 0.0 && healthAfterBurst < victim.MaxHealth,
            "the direct R burst dealt production magic damage");

        // Leave enough health for one real aura pulse to be nonlethal, then
        // provide the owner as the independent native death-header killer.
        // This keeps the pet row in history while avoiding a queued Tibbers
        // basic attack before the header-preserving execution.
        TestAssert.True(victim.SpellBlock > 0.0,
            "the aura target retains production spell resistance");
        victim.Health = 1000.0;
        tibbers.AttackCooldown = 999.0;
        var victimObserver = match.Runtime.Delivery.Observers.Single(observer =>
            observer.HeroNetId == victim.NetId);
        // Runtime.Tick flushes delivery after the aura callback. Hold
        // the owner-private recap until the pet has retired so this check
        // exercises the real lifecycle projection path.
        victimObserver.DeathRecapReady = false;
        match.Runtime.Tick(1.01);
        TestAssert.True(victim.Alive, "the isolated Tibbers aura pulse is nonlethal");
        TestAssert.True(victim.MatchStatistics.MagicDamageTaken > magicDamageBeforeAura,
            "the aura contributes real magic damage");
        TestAssert.Equal(physicalDamageBeforeAura, victim.MatchStatistics.PhysicalDamageTaken,
            "the aura phase adds no pet basic-attack damage");

        TestAssert.True(match.Runtime.Combat.Kill(victim, caster.NetId),
            "the owner supplies a valid native recap header killer after the aura row");

        var snapshot = match.Runtime.Combat.GetLastDeathRecap(victim.NetId)
            ?? throw new InvalidOperationException("the direct burst did not retain a death recap");
        TestAssert.Equal(2, snapshot.Records.Count,
            "the direct burst and real Tibbers aura retain separate recap records");
        // The direct burst and periodic burn both have Annie as their actual
        // attacker. History retains their damage order, so distinguish the
        // two named spell rows by the earlier burst and later aura timestamp.
        var direct = snapshot.Records[0];
        var aura = snapshot.Records[1];
        TestAssert.True(direct.Timestamp < aura.Timestamp &&
            direct.MagicalDamage > 0.0 && aura.MagicalDamage > 0.0,
            "direct R and the later burn remain distinct magic damage records");
        TestAssert.Equal(caster.NetId, direct.AttackerNetId,
            "the direct R burst is attacked by Annie");
        TestAssert.Equal(AnnieInfernalGuardianHash,
            direct.Origin.SourceScriptNameHash,
            "the direct Annie R burst retains its canonical root");
        TestAssert.Equal(caster.NetId, direct.Origin.ParentCasterNetId,
            "the direct record retains Annie as caster");
        TestAssert.Equal((byte)2, direct.Origin.SourceSpellLevel,
            "the direct record retains the accepted R rank");
        TestAssert.Equal(caster.NetId, aura.AttackerNetId,
            "the periodic burn uses Annie as its actual attacker");
        TestAssert.Equal(AnnieInfernalGuardianHash,
            aura.Origin.SourceScriptNameHash,
            "the Tibbers aura uses the captured InfernalGuardian root");
        TestAssert.Equal(DeathRecapEventSource.Spell, aura.Origin.EventSource,
            "the Tibbers aura remains a spell-origin recap");
        TestAssert.Equal(caster.NetId, aura.Origin.ParentCasterNetId,
            "the Tibbers aura retains Annie as the captured spell owner");
        TestAssert.Equal((byte)2, aura.Origin.SourceSpellLevel,
            "the Tibbers aura retains the accepted R rank after Annie learns rank three");
        TestAssert.Equal(0, match.PacketsFor(1).Packets.Count(packet => Opcode(packet) == 0x24),
            "the owner-private recap remains queued while rehydration is incomplete");
        // The periodic burn remains a named Annie spell row after Tibbers
        // retires; the independent owner kill keeps the native header valid.
        TestAssert.True(match.Runtime.World.Remove(tibbers.NetId),
            "the pet may retire before recap delivery");
        victimObserver.DeathRecapReady = true;
        match.Runtime.Delivery.Flush();
        var wireRows = DecodeRows(match.PacketsFor(1).Packets.Single(packet => Opcode(packet) == 0x24));
        TestAssert.Equal(2, wireRows.Count,
            "pet retirement does not drop the named aura row");
        TestAssert.True(wireRows.All(row => row.Attacker == caster.NetId),
            "both named rows retain their actual Annie attacker on the wire");
        TestAssert.Equal(1, match.PacketsFor(1).Packets.Count(packet => Opcode(packet) == 0x24),
            "the owner receives one recap packet containing both rows");
    }

    [Check("death-recap.live-leesin-r-secondary-origin")]
    public static void LeeSinSecondaryCollisionRetainsCapturedROrigin()
    {
        using var match = new LiveMatch(3, "LeeSin",
            [Team.Order, Team.Chaos, Team.Chaos]);
        var caster = match.Hero(0);
        var primary = match.Hero(1);
        var secondary = match.Hero(2);
        // This is the production collision geometry already covered by the
        // kick movement regression; it gives the primary a clear kick path
        // through the second enemy without manufacturing a collision handle.
        caster.Position = new Position2(7032.0, 6560.0);
        primary.Position = new Position2(6999.0, 6757.0);
        secondary.Position = new Position2(6941.0, 7102.0);
        caster.SpellRanks[3] = 2;
        match.Runtime.Tick(0.01);
        match.Runtime.HeroStats.Refresh(primary);
        match.Runtime.HeroStats.Refresh(secondary);
        primary.MaxHealth = primary.Health = 5000.0;
        secondary.Health = 1.0;
        match.Runtime.Delivery.Flush();
        match.Clear();

        TestAssert.True(match.Cast(0, 3, primary), "Lee Sin R cast is accepted");
        caster.SpellRanks[3] = 3;
        match.Runtime.Tick(0.27);
        TestAssert.True(primary.Alive && primary.ForcedMove is not null,
            "the primary target survives and starts the production kick");
        // The swept collision is processed during the established travel
        // step, after the primary kick has begun.
        match.Runtime.Tick(1.0);
        TestAssert.False(secondary.Alive, "the swept secondary collision is lethal");
        TestAssert.True(secondary.MatchStatistics.PhysicalDamageTaken > 0.0,
            "the secondary receives real physical collision damage");
        match.Runtime.Delivery.Flush();

        var snapshot = match.Runtime.Combat.GetLastDeathRecap(secondary.NetId)
            ?? throw new InvalidOperationException("the secondary collision did not retain a recap");
        var row = snapshot.Records.Single();
        TestAssert.Equal(LeeSinBlindMonkRKickHash,
            row.Origin.SourceScriptNameHash,
            "the secondary collision uses the canonical BlindMonkRKick root");
        TestAssert.Equal(DeathRecapEventSource.Spell, row.Origin.EventSource,
            "the secondary collision remains a spell-origin recap");
        TestAssert.Equal(caster.NetId, row.AttackerNetId,
            "the secondary collision retains Lee Sin as the damage attacker");
        TestAssert.Equal(caster.NetId, row.Origin.ParentCasterNetId,
            "the secondary collision retains the original R caster");
        TestAssert.Equal((byte)2, row.Origin.SourceSpellLevel,
            "the secondary collision retains the accepted R rank after a learned-rank mutation");
        TestAssert.Equal((byte)0, row.Origin.ParentTeam,
            "the secondary collision packs Lee Sin's team");
        TestAssert.Equal(1, match.PacketsFor(2).Packets.Count(packet => Opcode(packet) == 0x24),
            "the secondary owner receives one named recap packet");
    }

    [Check("death-recap.live-r-origin-contract")]
    public static void RankThreeOriginsAreCatalogBackedAndRejectForgedMetadata()
    {
        // Batch 164: the retired direct-spell aliases and fixed per-champion hashes are gone; the rows these checks
        // hold are what the scripts' damage carries (ChampionSpell of the cast's slot and rank), pinned below against
        // the shipped spell names. The names themselves stay pinned here.
        TestAssert.Equal("StaticField", GameData.Default.Spell("StaticField", 3).Name,
            "Blitz R hash has a shipped spell catalog entry");
        TestAssert.Equal("OrianaDetonateCommand",
            GameData.Default.Spell("OrianaDetonateCommand", 3).Name,
            "Orianna R hash has a shipped spell catalog entry");
        TestAssert.Equal(0x082DA94Bu, LeeSinBlindMonkRKickHash,
            "Lee Sin R hash is the protocol canonical-name hash of BlindMonkRKick (the retired constant's value)");
        TestAssert.Equal("InfernalGuardian",
            GameData.Default.Spell("InfernalGuardian", 3).Name,
            "Annie R hash has a shipped spell catalog entry");
        TestAssert.Equal("BlindMonkRKick",
            GameData.Default.Spell("BlindMonkRKick", 3).Name,
            "Lee Sin R hash has a shipped spell catalog entry");

        using var blitzMatch = new LiveMatch(2, "Blitzcrank");
        var blitz = blitzMatch.Hero(0);
        var validBlitz = DeathRecapOrigin.ChampionSpell(blitz, 3, 2);
        TestAssert.Equal(BlitzcrankStaticFieldHash, validBlitz.SourceScriptNameHash,
            "the scripts' R damage carries the Static Field spell name");
        TestAssert.True(validBlitz.IsSafeForSource(blitz, DeathRecapSourceType.Hero),
            "a real Blitzcrank R origin is source-owned");
        var highRankBlitzQ = DeathRecapOrigin.ChampionSpell(blitz, 0, 4);
        TestAssert.True(highRankBlitzQ.IsSafeForSource(blitz, DeathRecapSourceType.Hero),
            "a learned rank-4 Blitzcrank Q remains source-owned");
        TestAssert.Equal((byte)4, highRankBlitzQ.SourceSpellLevel,
            "a learned rank-4 Blitzcrank Q retains its native level");
        TestAssert.False(new DeathRecapOrigin(
                DeathRecapEventSource.Spell, DeathRecapEventSource.Spell,
                BlitzcrankStaticFieldHash, 0, blitz.NetId, 0,
                DeathRecapSourceType.Hero, 2).IsSafe,
            "a non-character parent is rejected");
        TestAssert.False(new DeathRecapOrigin(
                DeathRecapEventSource.Spell, DeathRecapEventSource.Character,
                BlitzcrankStaticFieldHash, 0x12345678, blitz.NetId, 0,
                DeathRecapSourceType.Hero, 2).IsSafe,
            "a forged parent hash is rejected");
        TestAssert.False(new DeathRecapOrigin(
                DeathRecapEventSource.Spell, DeathRecapEventSource.Character,
                BlitzcrankStaticFieldHash, 0, blitz.NetId + 1, 0,
                DeathRecapSourceType.Hero, 2).IsSafeForSource(blitz, DeathRecapSourceType.Hero),
            "a forged caster id is rejected");
        TestAssert.False(new DeathRecapOrigin(
                DeathRecapEventSource.Spell, DeathRecapEventSource.Character,
                BlitzcrankStaticFieldHash, 0, blitz.NetId, 1,
                DeathRecapSourceType.Hero, 2).IsSafeForSource(blitz, DeathRecapSourceType.Hero),
            "a forged parent team is rejected");
        TestAssert.False(new DeathRecapOrigin(
                DeathRecapEventSource.Spell, DeathRecapEventSource.Character,
                BlitzcrankStaticFieldHash, 0, blitz.NetId, 0,
                DeathRecapSourceType.Clone, 2).IsSafe,
            "a clone parent source is rejected");

        using var oriannaMatch = new LiveMatch(2, "Orianna");
        var orianna = oriannaMatch.Hero(0);
        var validOrianna = DeathRecapOrigin.ChampionSpell(orianna, 3, 3);
        TestAssert.Equal(OriannaDetonateCommandHash, validOrianna.SourceScriptNameHash,
            "the scripts' R damage carries the Detonate Command spell name");
        TestAssert.True(validOrianna.IsSafeForSource(orianna, DeathRecapSourceType.Hero),
            "a real Orianna R origin is source-owned");
        TestAssert.False(new DeathRecapOrigin(
                DeathRecapEventSource.Spell, DeathRecapEventSource.Character,
                BlitzcrankStaticFieldHash, 0, orianna.NetId, 0,
                DeathRecapSourceType.Hero, 2).IsSafeForSource(orianna, DeathRecapSourceType.Hero),
            "a Blitz hash cannot be owned by Orianna");
        var highRankOriannaQ = DeathRecapOrigin.ChampionSpell(orianna, 0, 4);
        TestAssert.True(highRankOriannaQ.IsSafeForSource(orianna, DeathRecapSourceType.Hero),
            "a learned rank-4 Orianna Q remains source-owned");
        var oriannaPassive = DeathRecapOrigin.BuffProducer(orianna, "OrianaSpellSword")!.Value;
        TestAssert.True(oriannaPassive.IsSafeForSource(orianna, DeathRecapSourceType.Hero),
            "the shipped OrianaSpellSword proc has a catalog-backed Buff origin");
        TestAssert.Equal(DeathRecapEventSource.Buff, oriannaPassive.EventSource,
            "Clockwork Windup remains a Buff event rather than a guessed cast");
        // The hand-written Blitz factory's champion argument is gone with the factory (batch 164): a row is what the
        // scripts' damage carries, so Orianna's slot-3 row is her own R and the Blitz hash is never hers (the catalog
        // gate refuses it above).
        TestAssert.Equal(OriannaDetonateCommandHash,
            DeathRecapOrigin.ChampionSpell(orianna, 3, 2).SourceScriptNameHash,
            "a slot-3 row for Orianna names Detonate Command, never Static Field");

        using var annieMatch = new LiveMatch(2, "Annie");
        var annie = annieMatch.Hero(0);
        var validAnnie = DeathRecapOrigin.ChampionSpell(annie, 3, 2);
        TestAssert.Equal(AnnieInfernalGuardianHash, validAnnie.SourceScriptNameHash,
            "the scripts' R damage carries the Infernal Guardian spell name");
        TestAssert.True(validAnnie.IsSafeForSource(annie, DeathRecapSourceType.Hero),
            "a real Annie R origin is source-owned");
        foreach (var rank in new[] { 1, 2, 3 })
            TestAssert.True(DeathRecapOrigin.ChampionSpell(annie, 3, rank)
                .IsSafeForSource(annie, DeathRecapSourceType.Hero),
                $"Annie R rank {rank} remains representable");
        var highRankAnnieQ = DeathRecapOrigin.ChampionSpell(annie, 0, 4);
        TestAssert.True(highRankAnnieQ.IsSafeForSource(annie, DeathRecapSourceType.Hero),
            "a learned rank-4 Annie Q remains source-owned");
        TestAssert.False(new DeathRecapOrigin(
                DeathRecapEventSource.Spell, DeathRecapEventSource.Character,
                LeeSinBlindMonkRKickHash, 0, annie.NetId, 0,
                DeathRecapSourceType.Hero, 2).IsSafeForSource(annie, DeathRecapSourceType.Hero),
            "a Lee Sin R hash cannot be owned by Annie");
        TestAssert.False(new DeathRecapOrigin(
                DeathRecapEventSource.Spell, DeathRecapEventSource.Character,
                AnnieInfernalGuardianHash, 0, annie.NetId + 1, 0,
                DeathRecapSourceType.Hero, 2).IsSafeForSource(annie, DeathRecapSourceType.Hero),
            "an Annie R origin with a forged caster id is rejected");

        using var leeMatch = new LiveMatch(2, "LeeSin");
        var lee = leeMatch.Hero(0);
        var validLee = DeathRecapOrigin.ChampionSpell(lee, 3, 3);
        TestAssert.True(validLee.IsSafeForSource(lee, DeathRecapSourceType.Hero),
            "a real Lee Sin R origin is source-owned");
        TestAssert.Equal(LeeSinBlindMonkRKickHash, validLee.SourceScriptNameHash,
            "the production slot-3 origin of Lee Sin names BlindMonkRKick");
        foreach (var rank in new[] { 1, 2, 3 })
            TestAssert.True(DeathRecapOrigin.ChampionSpell(lee, 3, rank)
                .IsSafeForSource(lee, DeathRecapSourceType.Hero),
                $"Lee Sin R rank {rank} remains representable");
        var highRankLeeQ = DeathRecapOrigin.ChampionSpell(lee, 0, 4);
        TestAssert.True(highRankLeeQ.IsSafeForSource(lee, DeathRecapSourceType.Hero),
            "a learned rank-4 Lee Sin Q remains source-owned");
        TestAssert.False(new DeathRecapOrigin(
                DeathRecapEventSource.Spell, DeathRecapEventSource.Character,
                AnnieInfernalGuardianHash, 0, lee.NetId, 0,
                DeathRecapSourceType.Hero, 2).IsSafeForSource(lee, DeathRecapSourceType.Hero),
            "an Annie R hash cannot be owned by Lee Sin");
        TestAssert.False(new DeathRecapOrigin(
                DeathRecapEventSource.Spell, DeathRecapEventSource.Character,
                LeeSinBlindMonkRKickHash, 0, lee.NetId + 1, 0,
                DeathRecapSourceType.Hero, 2).IsSafeForSource(lee, DeathRecapSourceType.Hero),
            "a Lee Sin R origin with a forged caster id is rejected");

        foreach (var (champion, index) in new[]
        {
            ("Annie", 0), ("Blitzcrank", 1), ("Ezreal", 2), ("Janna", 3),
            ("LeeSin", 4), ("Nidalee", 5), ("Orianna", 6), ("Shaco", 7),
            ("Vayne", 8), ("Yorick", 9),
        })
        {
            var source = new Entity((uint)(0x5000 + index), EntityKind.Hero,
                UnitType.Hero, Team.Order, new Position2(0.0, 0.0))
            { ChampionName = champion };
            var validR = DeathRecapOrigin.ChampionSpell(source, 3, 3);
            TestAssert.True(validR.IsSafeForSource(source, DeathRecapSourceType.Hero),
                $"{champion} R rank three is catalog-backed");
            TestAssert.False((validR with { SourceSpellLevel = 4 }).IsSafe,
                $"a forged {champion} R rank four is rejected");
            TestAssert.Equal(DeathRecapOrigin.Unclassified,
                DeathRecapOrigin.ChampionSpell(source, 3, 4),
                $"{champion} R rank four is rejected by the producer factory");
            foreach (var slot in new[] { 0, 1, 2 })
            foreach (var rank in new[] { 4, 5 })
            {
                var highRank = DeathRecapOrigin.ChampionSpell(source, slot, rank);
                TestAssert.True(highRank.IsSafeForSource(source, DeathRecapSourceType.Hero),
                    $"{champion} slot {slot} rank {rank} remains source-owned");
                TestAssert.Equal((byte)rank, highRank.SourceSpellLevel,
                    $"{champion} slot {slot} rank {rank} is retained unchanged");
            }
        }
    }

    [Check("death-recap.live-blitzcrank-passive-recap")]
    public static void BlitzcrankStaticFieldPassiveRetainsNamedRecap()
    {
        using var match = new LiveMatch(2, "Blitzcrank");
        var caster = match.Hero(0);
        var victim = match.Hero(1);
        caster.Position = new Position2(7000.0, 7000.0);
        victim.Position = new Position2(7100.0, 7000.0);
        victim.SpellBlock = 0.0;
        TestAssert.True(match.Runtime.Navigation.HasVisionLos(caster.Position, victim.Position),
            "the passive target has production line of sight");

        match.Runtime.Tick(0.01);
        TestAssert.True(victim.Alive && victim.MaxHealth > 1.0,
            "the warmup preserves the production victim health pool");
        victim.Health = 1.0;
        match.Runtime.Tick(2.5);
        TestAssert.False(victim.Alive, "the periodic Static Field pulse kills the victim");
        match.Runtime.Delivery.Flush();
        var snapshot = match.Runtime.Combat.GetLastDeathRecap(victim.NetId)
            ?? throw new InvalidOperationException("the Static Field pulse did not retain a recap");
        var passiveRows = snapshot.Records.Where(record =>
            record.Origin.SourceScriptNameHash == BlitzcrankStaticFieldHash)
            .ToArray();
        TestAssert.Equal(2, passiveRows.Length,
            "the 0.01 warmup pulse and 2.5-second lethal pulse remain separate history rows");
        TestAssert.True(passiveRows.All(record => record.MagicalDamage > 0.0),
            "every Static Field pulse retains positive post-mitigation magic damage");
        TestAssert.True(passiveRows.All(record => record.Origin.SourceSpellLevel == 1),
            "every passive pulse captures Blitzcrank's learned Static Field rank");
        var recap = match.PacketsFor(1).Packets.Single(packet => Opcode(packet) == 0x24);
        var wireRows = DecodeRows(recap).Where(record =>
            record.SourceHash == BlitzcrankStaticFieldHash).ToArray();
        TestAssert.Equal(passiveRows.Length, wireRows.Length,
            "the wire preserves each positive Static Field history row");
        TestAssert.True(wireRows.All(record => record.Magical > 0.0),
            "every named Static Field wire row retains positive magic damage");
        TestAssert.True(wireRows.All(record => ((record.ParentPacked >> 12) & 0x07) == 1),
            "every passive wire row writes its captured rank into the native field");
    }

    [Check("death-recap.live-production-damage-owner-only")]
    public static void ProductionDamageIsPostShieldAndOwnerOnly()
    {
        using var match = new LiveMatch(2);
        var source = match.Hero(0);
        var victim = match.Hero(1);
        victim.Armor = 0.0;
        victim.SpellBlock = 0.0;
        victim.MaxHealth = victim.Health = 1000.0;

        // A rejected hit and an unresolved attacker must not create recap rows.
        victim.Protected = true;
        TestAssert.False(match.Runtime.Combat.ApplyHit(source.NetId, victim.NetId,
            10.0, DamageType.True, DamageSourceKind.Basic), "protected hit is rejected");
        victim.Protected = false;
        TestAssert.True(match.Runtime.Combat.ApplyHit(0xDEAD_BEEF, victim.NetId,
            10.0, DamageType.True, DamageSourceKind.Basic),
            "unknown attackers may damage but cannot enter the recap history");

        match.Runtime.Buffs.AddBuff(victim, "RecapShield", BuffType.CombatEnhancer,
            10.0, source.NetId, new BuffPayload { DamageShield = 5.0 },
            canMitigateDuration: false);
        match.Runtime.Delivery.Flush();
        match.Clear();

        TestAssert.True(match.Runtime.Combat.ApplyHit(source.NetId, victim.NetId,
            10.0, DamageType.Physical, DamageSourceKind.Basic), "physical hit");
        TestAssert.True(match.Runtime.Combat.ApplyHit(source.NetId, victim.NetId,
            7.0, DamageType.Magic, DamageSourceKind.Basic), "magic hit");
        TestAssert.True(match.Runtime.Combat.ApplyHit(source.NetId, victim.NetId,
            3.0, DamageType.True, DamageSourceKind.Basic), "true hit");
        TestAssert.True(match.Runtime.Combat.ApplyHit(source.NetId, victim.NetId,
            2000.0, DamageType.True, DamageSourceKind.Basic), "lethal hit");
        TestAssert.False(victim.Alive, "lethal production hit kills the victim");

        match.Runtime.Delivery.Flush();
        var ownerPackets = match.PacketsFor(1).Packets;
        var otherPackets = match.PacketsFor(0).Packets;
        var deathIndex = ownerPackets.FindIndex(packet => Opcode(packet) == 0x61);
        var recapIndex = ownerPackets.FindIndex(packet => Opcode(packet) == 0x24);
        TestAssert.True(deathIndex >= 0 && recapIndex > deathIndex,
            "owner receives death before its recap");
        TestAssert.Equal(0, otherPackets.Count(packet => Opcode(packet) == 0x24),
            "death recap remains owner-private");

        var recap = ownerPackets.Single(packet => Opcode(packet) == 0x24);
        var rows = DecodeRows(recap);
        TestAssert.Equal(4, rows.Count,
            "rejected and unknown-attacker hits are absent from the history");
        TestAssert.Near(5.0, rows.Sum(row => row.Physical), 1e-6,
            "physical recap damage is post-shield");
        TestAssert.Near(7.0, rows.Sum(row => row.Magical), 1e-6,
            "magic recap damage is post-mitigation");
        TestAssert.Near(2003.0, rows.Sum(row => row.True), 1e-6,
            "true recap damage includes the small hit and lethal hit");
        TestAssert.True(rows.All(row => row.Attacker == source.NetId && row.Victim == victim.NetId),
            "all delivered rows retain their authoritative source and victim");

        match.Runtime.Delivery.Flush();
        TestAssert.Equal(1, match.PacketsFor(1).Packets.Count(packet => Opcode(packet) == 0x24),
            "one generation is delivered once per observer view");
    }

    [Check("death-recap.live-attacker-owner-boundary")]
    public static void BasicPetUsesActualAttackerAndUnrelatedOwnerCannotForgeNamedRow()
    {
        using var match = new LiveMatch(4, "Annie");
        var owner = match.Hero(0);
        var unrelatedOwner = match.Hero(1);
        var basicVictim = match.Hero(2);
        var forgedVictim = match.Hero(3);
        var pet = match.Runtime.World.Register(new Entity(
            match.Runtime.World.Allocate(UnitType.Monster), EntityKind.Pet,
            UnitType.Monster, owner.Team, owner.Position)
        {
            OwnerNetId = owner.NetId,
            Health = 100.0,
            MaxHealth = 100.0,
            Created = true,
            Visible = true,
        });
        basicVictim.MaxHealth = basicVictim.Health = 100.0;
        forgedVictim.MaxHealth = forgedVictim.Health = 100.0;

        TestAssert.True(match.Runtime.Combat.ApplyHit(pet.NetId, basicVictim.NetId,
            200.0, DamageType.True, DamageSourceKind.Basic),
            "a real pet basic hit follows the ordinary damage path");
        var basicSnapshot = match.Runtime.Combat.GetLastDeathRecap(basicVictim.NetId)
            ?? throw new InvalidOperationException("pet basic hit did not retain a recap");
        var basicRow = basicSnapshot.Records.Single();
        TestAssert.Equal(pet.NetId, basicRow.AttackerNetId,
            "basic recap retains the pet as the actual attacker");
        TestAssert.Equal(DeathRecapEventSource.BasicAttack, basicRow.Origin.EventSource,
            "pet basic damage remains a BasicAttack origin");
        TestAssert.Equal(DeathRecapSourceType.Pet, basicRow.Origin.ParentSourceType,
            "pet basic damage retains the Pet source type");
        TestAssert.Equal(0u, basicRow.Origin.ParentCasterNetId,
            "basic damage has no forged hero caster");

        TestAssert.True(match.Runtime.Combat.ApplyHit(pet.NetId, forgedVictim.NetId,
            200.0, DamageType.True, DamageSourceKind.Spell,
            spellOwnerNetId: unrelatedOwner.NetId,
            // The forged row is the scripts' own slot-3 identity (batch 164: the hand-written Annie direct row is
            // gone), and the unrelated spell owner cannot stand behind it.
            recapOrigin: DeathRecapOrigin.ChampionSpell(owner, 3, 1)),
            "an unrelated owner may still deal damage through the live hit path");
        TestAssert.True(match.Runtime.Combat.GetLastDeathRecap(forgedVictim.NetId) is null,
            "an unrelated spell owner cannot forge a named recap row");
    }

    [Check("pets-live-shaco-box-impact-rank-recap")]
    public static void ShacoBoxReadsCurrentRankForDamageAndRecap()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var inFlight in new[] { false, true })
        foreach (var ownerDies in new[] { false, true })
        {
            var enemyTeam = team == Team.Order ? Team.Chaos : Team.Order;
            using var match = new LiveMatch(2, "Shaco", [team, enemyTeam]);
            var runtime = match.Runtime;
            var owner = match.Hero(0);
            var victim = match.Hero(1);
            owner.SpellRanks[1] = 1;
            // The shot reads the AP of Shaco's stat block (ShacoBoxSpell: SpellDamageRatio 0.25), which every refresh recomputes: a Needlessly Large Rod gives the 80.
            owner.Inventory = [new InventorySlot(1058), null, null, null, null, null];
            runtime.HeroStats.Refresh(owner);
            TestAssert.Near(80.0, owner.AbilityPower, 1e-9, "the fixture owner has 80 AP");
            victim.Position = owner.Position + new Position2(200.0, 0.0);

            TestAssert.True(match.Cast(0, 1, victim), "Shaco W placement is accepted");
            runtime.Tick(0.5);
            var box = runtime.World.Entities.Values.Single(entity =>
                entity.Kind == EntityKind.ShacoBox);
            TestAssert.Equal(1, owner.SpellRanks[1], "the fixture places the box at rank one");
            TestAssert.True(box.Buffs.Any(buff => buff.Name == "JackInTheBox"), "the shipped script's box buff drives the unit");
            match.Clear();

            victim.Position = box.Position + new Position2(250.0, 0.0);
            if (inFlight)
            {
                for (var tick = 0; tick < 120 && !HasLaunchedBoxShot(); tick++)
                    runtime.Tick(1.0 / 30.0);
                TestAssert.True(HasLaunchedBoxShot(),
                    "rank-up fixture must observe the box missile in flight");
                owner.SpellRanks[1] = 3;
            }
            else
            {
                victim.Position = box.Position + new Position2(600.0, 0.0);
                runtime.Tick(1.0);
                owner.SpellRanks[1] = 3;
                victim.Position = box.Position + new Position2(250.0, 0.0);
                for (var tick = 0; tick < 120 && !HasLaunchedBoxShot(); tick++)
                    runtime.Tick(1.0 / 30.0);
                TestAssert.True(HasLaunchedBoxShot(),
                    "dormant rank-up fixture must observe the box missile in flight");
            }

            if (ownerDies)
            {
                runtime.Combat.Kill(owner, victim.NetId);
                runtime.Delivery.Flush();
                match.Clear();
            }

            // Fear refreshes the natural stats, including Orianna's attached
            // rank-one E defense: 30 base MR plus OrianaGhostSelf's 10 MR.
            runtime.HeroStats.Refresh(victim);
            TestAssert.Equal(1, victim.Level, "the rank fixture keeps Orianna at level one");
            var expectedSpellBlock = GameData.Default.StatsAtLevel(
                GameData.Default.Champion(victim.ChampionName), victim.Level).SpellBlock;
            TestAssert.Near(30.0, expectedSpellBlock, 1e-9,
                "shipped level-one Orianna supplies the expected 30 MR");
            TestAssert.Equal(1, victim.SpellRanks[2], "attached-ball defense uses rank-one E");
            TestAssert.Near(10.0, victim.Buffs.Single(buff => buff.Name == "OrianaGhostSelf")
                .Payload.SpellBlock, 1e-9, "rank-one attached-ball defense adds 10 MR");
            TestAssert.Near(40.0, victim.SpellBlock, 1e-9, "the full natural defense stays stable through fear");
            victim.Health = 1.0;

            for (var tick = 0; tick < 120 && victim.Alive; tick++)
                runtime.Tick(1.0 / 30.0);
            TestAssert.False(victim.Alive,
                $"rank-three box hit resolves in {(inFlight ? "flight" : "dormant")} fixture");
            runtime.Delivery.Flush();

            // 75 base + 0.25 * 80 AP, mitigated by 30 base + 10 ball MR.
            const double expectedDamage = 95.0 * 100.0 / 140.0;
            foreach (var observer in new[] { 0, 1 })
            {
                var damage = match.PacketsFor(observer).Packets
                    .Where(packet => Opcode(packet) == 0x68)
                    .Select(packet => WirePacketDecoder.Decode(
                        packet.Plaintext.Span, packet.Channel))
                    .Where(decoded =>
                        decoded.Fields.TryGetValue("source_netid", out var source) &&
                        source is uint sourceId && sourceId == owner.NetId &&
                        decoded.Fields.TryGetValue("target_netid", out var target) &&
                        target is uint targetId && targetId == victim.NetId)
                    .ToArray();
                TestAssert.Equal(1, damage.Length,
                    "each observer receives the one owner-attributed box hit");
                TestAssert.Near(expectedDamage,
                    Convert.ToDouble(damage[0].Fields["damage"]), 1e-4,
                    "damage packet uses the impact-time rank-three W value");
            }

            var victimPackets = match.PacketsFor(1).Packets;
            TestAssert.Equal(1, victimPackets.Count(packet => Opcode(packet) == 0x24),
                "the victim observer receives one death recap");
            TestAssert.Equal(0, match.PacketsFor(0).Packets.Count(packet => Opcode(packet) == 0x24),
                "the owner observer does not receive the victim-private recap");
            var recap = victimPackets.Single(packet => Opcode(packet) == 0x24);
            var row = DecodeRows(recap).Single();
            if (ownerDies)
                TestAssert.False(owner.Alive, "owner-dead box recap retains the dead Shaco source");
            TestAssert.Equal(owner.NetId, row.Attacker,
                "decoded recap retains Shaco as the owner source");
            TestAssert.Equal(victim.NetId, row.Victim,
                "decoded recap retains the victim net id");
            TestAssert.Near(expectedDamage, row.Magical, 1e-4,
                "decoded recap stores the post-mitigation box damage");
            TestAssert.Equal((uint)DeathRecapEventSource.Spell, row.SourceEvent,
                "box damage is a named spell recap row");
            TestAssert.Equal(ProtocolEncoding.HashString("JackInTheBox"), row.SourceHash,
                "box recap uses the shipped W script hash");
            TestAssert.Equal(owner.NetId, row.ParentCaster,
                "box recap keeps the Shaco owner caster after delayed impact");
            TestAssert.Equal(3u, (row.ParentPacked >> 12) & 0x07,
                "decoded recap uses the impact-time rank-three W value");
            TestAssert.Equal(team == Team.Order ? 0u : 1u,
                (row.ParentPacked >> 4) & 0x0F,
                "decoded recap keeps the owner's native parent team");
            TestAssert.Equal((uint)DeathRecapSourceType.Hero,
                (row.ParentPacked >> 8) & 0x0F,
                "box recap identifies the owner as a hero source");

            bool HasLaunchedBoxShot() => runtime.Spells.SharedProjectiles.Any(projectile =>
                projectile.Snapshot is { SpellName: "ShacoBoxSpell", Launched: true });
        }
    }

    [Check("death-recap.live-spell-basic-kind-keeps-origin")]
    public static void SpellBasicKindDoesNotMasqueradeAsAutoAttack()
    {
        foreach (var rank in new[] { 1, 5 })
        {
            using var match = new LiveMatch(2, "Ezreal");
            var caster = match.Hero(0);
            var victim = match.Hero(1);
            caster.SpellRanks[0] = rank;
            victim.MaxHealth = victim.Health = 5000.0;
            var healthBeforeQ = victim.Health;

            TestAssert.True(match.Cast(0, 0, victim), "Ezreal Q cast is accepted");
            match.Runtime.Tick(1.0);
            TestAssert.True(victim.Health < healthBeforeQ,
                "Ezreal Q reaches the victim before the lethal auto attack");

            // Leave the real auto-attack producer on the normal QueueBasicAttack
            // path. Its pending hit has no explicit origin and may infer
            // BasicAttack. Q supplies its own Spell origin at every learned
            // rank representable by the native three-bit field.
            caster.AttackTarget = victim.NetId;
            victim.Health = 1.0;
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(caster, victim),
                "a genuine auto attack is queued");
            match.Runtime.Tick(1.0);
            TestAssert.False(victim.Alive, "the auto attack kills the victim");

            var snapshot = match.Runtime.Combat.GetLastDeathRecap(victim.NetId);
            TestAssert.True(snapshot is not null, "the lethal auto attack creates a recap");
            TestAssert.Equal(2, snapshot!.Records.Count,
                "both the spell and genuine auto attack enter the recap");
            var basic = snapshot.Records.Single(row => row.Origin.EventSource == DeathRecapEventSource.BasicAttack);
            TestAssert.Equal(caster.NetId, basic.AttackerNetId,
                "the basic record retains its attacker");
            var spell = snapshot.Records.Single(row => row.Origin.EventSource == DeathRecapEventSource.Spell);
            TestAssert.Equal(ProtocolEncoding.HashString("EzrealMysticShot"), spell.Origin.SourceScriptNameHash);
            TestAssert.Equal((byte)rank, spell.Origin.SourceSpellLevel);
        }
    }

    [Check("death-recap.live-yorick-qwe-direct-and-basic-origins")]
    public static void YorickQweDirectAndBasicOriginsStayTyped()
    {
        using var match = new LiveMatch(3, "Yorick", [Team.Order, Team.Chaos, Team.Chaos]);
        var owner = match.Hero(0);
        var victim = match.Hero(1);
        var killer = match.Hero(2);
        victim.Position = owner.Position + new Position2(160.0, 0.0);
        victim.MaxHealth = victim.Health = 10000.0;
        owner.SpellRanks[0] = 2;
        owner.SpellRanks[1] = 3;
        owner.SpellRanks[2] = 4;

        TestAssert.True(match.Cast(0, 0, owner), "Yorick Q arms");
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(owner, victim,
            new AttackRolls(1.0, 1.0, 1.0)), "Yorick Q attack queues");
        match.Runtime.Tick(1.0);
        RetireYorickPets(match.Runtime, owner.NetId);

        TestAssert.True(match.Cast(0, 1, victim), "Yorick W casts");
        match.Runtime.Tick(0.3);
        RetireYorickPets(match.Runtime, owner.NetId);
        TestAssert.True(match.Cast(0, 2, victim), "Yorick E casts");
        match.Runtime.Tick(0.3);
        RetireYorickPets(match.Runtime, owner.NetId);

        TestAssert.True(match.Runtime.Combat.ApplyHit(killer.NetId, victim.NetId,
            victim.Health + 1.0, DamageType.True, DamageSourceKind.Basic),
            "Yorick victim receives a later lethal hit");
        var recap = match.Runtime.Combat.GetLastDeathRecap(victim.NetId)
            ?? throw new InvalidOperationException("Yorick Q/W/E did not produce a recap");
        TestAssert.True(recap.Records.Any(row => row.Origin.EventSource ==
            DeathRecapEventSource.BasicAttack), "Yorick Q remains a basic attack row");
        foreach (var (slot, rank) in new[] { (1, 3), (2, 4) })
        {
            var expected = DeathRecapOrigin.ChampionSpell(owner, slot, rank);
            TestAssert.True(recap.Records.Any(row => row.Origin.EventSource ==
                DeathRecapEventSource.Spell &&
                row.Origin.SourceScriptNameHash == expected.SourceScriptNameHash &&
                row.Origin.SourceSpellLevel == expected.SourceSpellLevel),
                $"Yorick slot {slot} keeps its direct spell origin and rank");
        }

        static void RetireYorickPets(MatchRuntime runtime, uint ownerNetId)
        {
            foreach (var pet in runtime.World.Entities.Values.Where(entity =>
                         entity.OwnerNetId == ownerNetId &&
                         entity.Kind is EntityKind.YorickGhoul or EntityKind.YorickRevenant &&
                         entity.Alive).ToArray())
                runtime.Combat.Kill(pet, 0);
        }
    }

    [Check("death-recap.live-yorick-ghoul-origin-retains-owner-rank")]
    public static void YorickGhoulOriginRetainsOwnerRankAfterPetRetirement()
    {
        foreach (var (kind, slot, rank) in new[]
        {
            ("Spectral", 0, 2), ("Decayed", 1, 3), ("Ravenous", 2, 4),
        })
        {
            using var match = new LiveMatch(3, "Yorick", [Team.Order, Team.Chaos, Team.Chaos]);
            var owner = match.Hero(0);
            var victim = match.Hero(1);
            var killer = match.Hero(2);
            owner.SpellRanks[slot] = rank;
            owner.ArmorPenetrationFlat = 20.0;
            victim.MaxHealth = victim.Health = 5000.0;
            victim.Armor = 100.0;
            // 19 October 2026 (batch 109): the ghoul's hit is Yorick's PROC dealt by the Logic buff, whose recap origin and rank are those of the
            // spell chain that added it. So the ghoul comes from the real cast: Q armed and spent on a swing, W at the owner, E at the killer (the
            // victim stands out of reach of the W burst and the cloud).
            owner.Level = 11;
            owner.Mana = owner.MaxMana;
            victim.Position = new Position2(8200.0, 7000.0);
            switch (slot)
            {
                case 0:
                    TestAssert.True(match.Cast(0, 0, owner), $"{kind}: Q armed");
                    TestAssert.True(match.Runtime.Combat.ApplyHit(owner.NetId, killer.NetId, owner.AttackDamage,
                        sourceKind: DamageSourceKind.Basic), "the armed swing lands on the killer");
                    break;
                case 1:
                    TestAssert.True(match.Cast(0, 1, owner), $"{kind}: W cast");
                    match.Runtime.Tick(0.3);
                    break;
                default:
                    TestAssert.True(match.Cast(0, 2, killer), $"{kind}: E cast");
                    match.Runtime.Tick(0.3);
                    break;
            }
            var ghoul = match.Runtime.World.Entities.Values.Single(entity =>
                entity.Kind == EntityKind.YorickGhoul && entity.OwnerNetId == owner.NetId && entity.Alive);
            // A cast refreshes the heroes' stats, which rebuilds the penetration, the health and the armor from level, runes and items.
            owner.ArmorPenetrationFlat = 20.0;
            victim.MaxHealth = victim.Health = 5000.0;
            victim.Armor = 100.0;
            TestAssert.True(match.Runtime.Combat.ApplyHit(ghoul.NetId, victim.NetId,
                100.0, DamageType.Physical, DamageSourceKind.Basic),
                $"{kind} ghoul hit lands");
            // PreDamage delegates this attack to the existing owner Proc
            // path before the Basic-only passive multiplier can run.
            var expectedDamage = 100.0 * CombatService.ResistanceMultiplier(80.0);
            TestAssert.Near(5000.0 - expectedDamage, victim.Health, 1e-9,
                $"{kind} keeps owner armor penetration while recording recap metadata");
            TestAssert.True(match.Runtime.Combat.Kill(ghoul, 0),
                $"{kind} ghoul can retire before the victim dies");

            TestAssert.True(match.Runtime.Combat.ApplyHit(killer.NetId, victim.NetId,
                victim.Health + 1.0, DamageType.True, DamageSourceKind.Basic),
                $"{kind} victim receives the later lethal hit");
            var recap = match.Runtime.Combat.GetLastDeathRecap(victim.NetId)
                ?? throw new InvalidOperationException($"{kind} did not produce a recap");
            var row = recap.Records.Single(record => record.Origin.EventSource ==
                DeathRecapEventSource.Spell);
            var expectedOrigin = DeathRecapOrigin.ChampionSpell(owner, slot, rank);
            TestAssert.Equal(owner.NetId, row.AttackerNetId,
                $"{kind} preserves the existing owner-attributed attacker");
            TestAssert.Equal(expectedOrigin.SourceScriptNameHash,
                row.Origin.SourceScriptNameHash, $"{kind} recap script hash");
            TestAssert.Equal((byte)rank, row.Origin.SourceSpellLevel,
                $"{kind} recap keeps the ghoul's captured rank");
        }
    }

    [Check("death-recap.live-yorick-revenant-origin-retains-rank")]
    public static void YorickRevenantOriginRetainsCapturedRRankAfterRetirement()
    {
        using var match = new LiveMatch(4, "Yorick",
            [Team.Order, Team.Order, Team.Chaos, Team.Chaos]);
        var owner = match.Hero(0);
        var ally = match.Hero(1);
        var victim = match.Hero(2);
        var killer = match.Hero(3);
        const int rank = 2;
        owner.SpellRanks[3] = rank;
        owner.Level = 11;
        ally.SpellRanks[3] = 1;
        victim.MaxHealth = victim.Health = 5000.0;
        TestAssert.True(match.Cast(0, 3, ally), "Yorick R creates the revenant");
        match.Runtime.Tick(0.3);
        var clone = match.Runtime.World.Entities.Values.Single(entity =>
            entity.Kind == EntityKind.YorickRevenant && entity.Alive);
        // 19 October 2026 (batch 109): the revenant is cloned by the child YorickRARevive, which YorickReviveAlly.TargetExecute casts with
        // OverrideForceLevel = 1 (YorickReviveAlly.luaobj), and the buffs it adds, the pet buff whose hit hook deals the revenant's damage among
        // them, carry that level. The damage ratio still follows Yorick's learned rank (BbYorickChecks); the recap row names the spell chain's level.
        const int recapLevel = 1;
        TestAssert.True(match.Runtime.Combat.ApplyHit(clone.NetId, victim.NetId,
            100.0, DamageType.Physical, DamageSourceKind.Basic),
            "revenant hit lands");
        TestAssert.True(match.Runtime.Combat.Kill(clone, 0),
            "revenant can retire before the victim dies");
        TestAssert.True(match.Runtime.Combat.ApplyHit(killer.NetId, victim.NetId,
            victim.Health + 1.0, DamageType.True, DamageSourceKind.Basic),
            "victim receives the later lethal hit");
        var recap = match.Runtime.Combat.GetLastDeathRecap(victim.NetId)
            ?? throw new InvalidOperationException("revenant did not produce a recap");
        var row = recap.Records.Single(record => record.Origin.EventSource ==
            DeathRecapEventSource.Spell);
        var expectedOrigin = DeathRecapOrigin.ChampionSpell(owner, 3, recapLevel);
        TestAssert.Equal(owner.NetId, row.AttackerNetId,
            "revenant keeps the existing owner-attributed attacker");
        TestAssert.Equal(expectedOrigin.SourceScriptNameHash,
            row.Origin.SourceScriptNameHash, "revenant recap script hash");
        TestAssert.Equal((byte)recapLevel, row.Origin.SourceSpellLevel,
            "revenant recap keeps the level its child cast ran at");
    }

    [Check("death-recap.live-filter-respawn-end-and-revival")]
    public static void ProductionFilteringAndNewLifeCleanup()
    {
        using (var match = new LiveMatch(3))
        {
            var victim = match.Hero(0);
            var removedAttacker = match.Hero(1);
            var killer = match.Hero(2);
            victim.MaxHealth = victim.Health = 1000.0;

            TestAssert.True(match.Runtime.Combat.ApplyHit(removedAttacker.NetId, victim.NetId,
                10.0, DamageType.Physical, DamageSourceKind.Basic));
            TestAssert.True(match.Runtime.World.Remove(removedAttacker.NetId),
                "the first attacker is removed before the lethal event");
            TestAssert.True(match.Runtime.Combat.ApplyHit(0xDEAD_BEEF, victim.NetId,
                10.0, DamageType.True, DamageSourceKind.Basic), "unknown hit is accepted");
            TestAssert.True(match.Runtime.Combat.ApplyHit(killer.NetId, victim.NetId,
                2000.0, DamageType.True, DamageSourceKind.Basic));
            match.Runtime.Delivery.Flush();

            var recap = match.PacketsFor(0).Packets.Single(packet => Opcode(packet) == 0x24);
            var rows = DecodeRows(recap);
            // The killer is Orianna, whose passive (her script since batch 112) adds a spell row for the swing, so the live killer owns
            // two rows: the passive's magic damage and the lethal hit itself. What the filter removes is every row of the other attackers.
            TestAssert.True(rows.Count > 0 && rows.All(row => row.Attacker == killer.NetId),
                "removed and unknown attackers are filtered: every row left is the live killer's");
            TestAssert.Equal(1, rows.Count(row => row.True > 1999.0), "the live killer row remains available");

            match.PacketsFor(0).Packets.Clear();
            TestAssert.True(match.Runtime.Combat.RespawnImmediately(victim),
                "ordinary respawn starts a new life");
            TestAssert.True(victim.Alive && match.Runtime.Combat.GetLastDeathRecap(victim.NetId) is null,
                "respawn removes the prior recap generation");
            match.Runtime.Delivery.Flush();
            TestAssert.False(match.PacketsFor(0).Packets.Any(packet => Opcode(packet) == 0x24),
                "a new life does not replay the old recap");

            TestAssert.True(match.Runtime.Combat.ApplyHit(killer.NetId, victim.NetId,
                victim.Health + 1.0, DamageType.True, DamageSourceKind.Basic));
            TestAssert.True(match.Runtime.Combat.GetLastDeathRecap(victim.NetId) is not null,
                "a later life gets an independent recap");
            TestAssert.True(match.Runtime.Combat.Surrender(victim.Team),
                "end-game path succeeds after the recap");
            TestAssert.True(match.Runtime.Combat.GetLastDeathRecap(victim.NetId) is null,
                "end-game cleanup removes retained recaps");
        }

        using (var unknownKiller = new LiveMatch(2))
        {
            var source = unknownKiller.Hero(0);
            var victim = unknownKiller.Hero(1);
            TestAssert.True(unknownKiller.Runtime.Combat.ApplyHit(source.NetId, victim.NetId,
                10.0, DamageType.True, DamageSourceKind.Basic));
            TestAssert.True(unknownKiller.Runtime.World.Remove(source.NetId));
            TestAssert.True(unknownKiller.Runtime.Combat.Kill(victim, source.NetId),
                "death with an unresolved killer still follows ordinary death");
            unknownKiller.Runtime.Delivery.Flush();
            TestAssert.False(unknownKiller.PacketsFor(1).Packets.Any(packet => Opcode(packet) == 0x24),
                "an unresolved killer cannot open a recap");
        }

        using (var guardian = new LiveMatch(2))
        {
            var source = guardian.Hero(0);
            var victim = guardian.Hero(1);
            victim.Inventory = [new InventorySlot(3026), null, null, null, null, null];
            // Batch 149: the revival is Spells/WillRevive.luaobj's own BuffOnPreDamage and its trigger is the ready
            // marker Items/3026.luaobj's UpdateSelfBuffActions keeps alive, not the inventory item the hand-written
            // revive read. The copy binds on the next spell update (BbItemModule.SyncInventory), so the inventory set
            // has to see one update before the marker is up — the same latency items-live-guardian-capacity-lethal-or-revival
            // and items-live-targeted-slow-does-not-cross-yorick-first-death already account for.
            guardian.Runtime.Tick(0.01);
            guardian.Clear();
            TestAssert.True(guardian.Runtime.Combat.ApplyHit(source.NetId, victim.NetId,
                victim.Health + 1.0, DamageType.True, DamageSourceKind.Basic));
            guardian.Runtime.Delivery.Flush();
            TestAssert.True(victim.Alive && victim.Buffs.Any(buff => buff.Name == "GuardianAngel"),
                "Guardian Angel prevents the ordinary death lifecycle");
            TestAssert.False(guardian.PacketsFor(1).Packets.Any(packet => Opcode(packet) is 0x61 or 0x24),
                "Guardian Angel emits neither death nor recap");
        }

        using (var yorick = new LiveMatch(3, "Yorick", [Team.Order, Team.Order, Team.Chaos]))
        {
            var caster = yorick.Hero(0);
            var victim = yorick.Hero(1);
            var killer = yorick.Hero(2);
            caster.Level = 6;
            yorick.Runtime.HeroStats.Refresh(caster);
            TestAssert.True(yorick.Cast(0, 3, victim), "Yorick R targets a same-team ally");
            yorick.Runtime.Tick(0.3);
            TestAssert.True(victim.Buffs.Any(buff => buff.Name == "YorickReviveAllySelf"),
                "production R installs the canonical revival marker");
            yorick.Clear();
            victim.Masteries = MasteryPage.Create(new MasteryCatalog(),
                new Dictionary<int, int> { [134] = 1 });
            yorick.Runtime.HeroStats.Refresh(victim);
            TestAssert.True(victim.LoadoutRespawnTimePercent > 0.0,
                "Good Hands contributes a positive respawn reduction");
            var killsBefore = killer.ChampionKills;
            var experienceBefore = killer.Experience;
            TestAssert.True(yorick.Runtime.Combat.ApplyHit(killer.NetId, victim.NetId,
                victim.Health + 1.0, DamageType.True, DamageSourceKind.Basic));
            TestAssert.True(victim.Alive && victim.YorickRevival is not null,
                "Yorick's marked ally enters the temporary revival life");
            yorick.Runtime.Delivery.Flush();
            TestAssert.True(yorick.Runtime.Combat.GetLastDeathRecap(victim.NetId) is not null,
                "the first counted death snapshot is retained while the temporary life is alive");
            TestAssert.False(yorick.PacketsFor(1).Packets.Any(packet => Opcode(packet) == 0x24),
                "the retained recap stays suppressed while the temporary life is alive");
            TestAssert.Equal(killsBefore + 1, killer.ChampionKills,
                "the first temporary-revival death awards exactly one kill");
            TestAssert.True(killer.Experience > experienceBefore,
                "the first temporary-revival death awards its normal experience");
            var experienceAfterFirst = killer.Experience;
            var originalRespawn = victim.RespawnAt;
            var unmodifiedRespawn = yorick.Runtime.Combat.CurrentTime +
                ProgressionRules.DeathTime(victim.Level, yorick.Runtime.Combat.CurrentTime);
            TestAssert.True(originalRespawn < unmodifiedRespawn,
                "Good Hands reduces the authoritative respawn deadline");
            var firstDeath = yorick.PacketsFor(1).Packets.Single(packet => Opcode(packet) == 0x61);
            TestAssert.Near(originalRespawn - yorick.Runtime.Combat.CurrentTime,
                BitConverter.ToSingle(firstDeath.Plaintext.Span[11..]), 1e-6,
                "the first death packet uses the loadout-reduced respawn deadline");

            yorick.Runtime.Tick(1.1);
            TestAssert.True(victim.YorickRevival is not null && victim.Buffs.Any(buff => buff.Name == "YorickRAZombie"),
                "the temporary revival reaches its zombie phase");
            yorick.Runtime.Tick(10.0);
            yorick.Runtime.Delivery.Flush();
            TestAssert.False(victim.Alive,
                "the temporary life ends through the production ForceDead path");
            TestAssert.True(victim.YorickRevival is null,
                "ForceDead clears the temporary revival state");
            TestAssert.Equal(originalRespawn, victim.RespawnAt,
                "ForceDead preserves the original respawn deadline");
            TestAssert.Equal(killsBefore + 1, killer.ChampionKills,
                "temporary-life expiry grants no second kill");
            TestAssert.Equal(experienceAfterFirst, killer.Experience,
                "temporary-life expiry does not change the first reward");
            var packets = yorick.PacketsFor(1).Packets;
            var forceDead = packets.FindIndex(packet => Opcode(packet) == 0x1E);
            var finalRecap = packets.FindIndex(packet => Opcode(packet) == 0x24);
            TestAssert.True(forceDead >= 0 && finalRecap > forceDead,
                "ForceDead precedes the retained recap when the temporary life ends");

            yorick.Runtime.Tick(Math.Max(0.1, originalRespawn - yorick.Runtime.Combat.CurrentTime + 0.1));
            TestAssert.True(victim.Alive && victim.YorickRevival is null,
                "ordinary respawn starts the next life");
            TestAssert.True(yorick.Runtime.Combat.GetLastDeathRecap(victim.NetId) is null,
                "fountain respawn clears the retained recap");
            TestAssert.Equal(killsBefore + 1, killer.ChampionKills,
                "respawn does not alter the one counted reward");
        }
    }

    [Check("death-recap.live-reconnect-orders-dead-state", Serial = true)]
    public static void DeadReconnectSendsStateBeforeOwnerRecap()
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Victim", "Annie", Team.Order),
            new SessionParticipantDefinition(2, 1, "Killer", "Ezreal", Team.Chaos, IsBot: true),
        };
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, readySettleSeconds: 0.0, roster: roster);
        using var network = MatchRuntimeNetwork.CreateEphemeral(runtime, peerLimit: 2, channelLimit: 8);
        using var client = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
        var events = new List<LeagueEnetEvent>();
        var peer = ConnectAndStart(network, client, runtime, events);
        var serverConnectionId = network.ConnectionIds.Single();
        var victim = runtime.Session.Participant(serverConnectionId)?.Hero
            ?? throw new InvalidOperationException("victim did not spawn");
        var killer = runtime.Session.Participants.Single(seat => seat.IsBot).Hero
            ?? throw new InvalidOperationException("bot killer did not spawn");
        victim.Inventory = [new InventorySlot(1001), null, null, null, null, null];
        victim.MaxHealth = victim.Health = 1000.0;
        // Keep the victim's retained observer knowledge explicit. The combat
        // test does not depend on the native fountain LOS layout, while the
        // delivery contract still requires a known killer for recap output.
        runtime.Session.Participant(serverConnectionId)!.View.Known.Add(killer.NetId);
        runtime.Session.Participant(serverConnectionId)!.View.Visible.Add(killer.NetId);

        // Detach while alive, then kill through the production combat path.
        peer.DisconnectNow();
        client.Flush();
        PumpUntil(network, client, events, () => network.ConnectionIds.Count == 0);
        TestAssert.True(runtime.Combat.ApplyHit(killer.NetId, victim.NetId,
            10.0, DamageType.Magic, DamageSourceKind.Spell,
            recapOrigin: DeathRecapOrigin.ChampionSpell(killer, 1, 1)),
            "the named Ezreal producer is retained before disconnect");
        TestAssert.True(runtime.Combat.ApplyHit(killer.NetId, victim.NetId,
            2000.0, DamageType.True, DamageSourceKind.Basic));
        TestAssert.False(victim.Alive);

        using var reconnectClient = new LeagueEnetHost(0, 1, 8, bindHost: "127.0.0.1");
        var reconnectEvents = new List<LeagueEnetEvent>();
        var reconnectPeer = reconnectClient.Connect("127.0.0.1", network.Port, 8);
        PumpUntil(network, reconnectClient, reconnectEvents,
            () => network.ConnectionIds.Count == 1);
        var reconnectServerConnectionId = network.ConnectionIds.Single();
        reconnectPeer.Send(0, runtime.Session.BuildKeyCheck(1), LeagueEnetPacketFlags.Reliable);
        reconnectClient.Flush();
        PumpUntil(network, reconnectClient, reconnectEvents,
            () => runtime.Session.Participant(reconnectServerConnectionId) is not null);
        reconnectPeer.Send(6, [1], LeagueEnetPacketFlags.Reliable);
        reconnectPeer.Send(1, [0xC5], LeagueEnetPacketFlags.Reliable);
        reconnectPeer.Send(1, [0xC6, 0, 0, 0, 0], LeagueEnetPacketFlags.Reliable);
        reconnectClient.Flush();
        PumpUntil(network, reconnectClient, reconnectEvents,
            () => runtime.Session.Participant(reconnectServerConnectionId) is { Loaded: true });
        PumpUntil(network, reconnectClient, reconnectEvents,
            () => runtime.Session.Participant(reconnectServerConnectionId)?.Spawned == true);
        reconnectPeer.Send(1, [0xC6, 0, 0, 0, 0], LeagueEnetPacketFlags.Reliable);
        reconnectClient.Flush();
        PumpUntil(network, reconnectClient, reconnectEvents,
            () => runtime.Session.Participant(reconnectServerConnectionId)?.Loaded == true);
        // One additional pump gives the reconnect observer's first delivery
        // pass a chance to emit the retained owner-private recap.
        PumpUntil(network, reconnectClient, reconnectEvents,
            () => reconnectEvents.Any(item => WireOpcode(item, runtime.Cipher) == 0x24));

        var opcodes = reconnectEvents.Select(item => WireOpcode(item, runtime.Cipher)).ToArray();
        var recap = Array.IndexOf(opcodes, (byte)0x24);
        TestAssert.True(recap >= 0, "reconnected dead owner receives its recap");
        var recapPayload = WirePayload(reconnectEvents[recap], runtime.Cipher);
        var rowCount = BinaryPrimitives.ReadInt32LittleEndian(recapPayload.AsSpan(0x15));
        var namedRow = false;
        for (var row = 0; row < rowCount; row++)
        {
            var offset = 0x19 + row * 0x2F;
            var sourceEvent = BinaryPrimitives.ReadUInt32LittleEndian(recapPayload.AsSpan(offset + 27));
            var sourceHash = BinaryPrimitives.ReadUInt32LittleEndian(recapPayload.AsSpan(offset + 31));
            var parentHash = BinaryPrimitives.ReadUInt32LittleEndian(recapPayload.AsSpan(offset + 35));
            var parentCaster = BinaryPrimitives.ReadUInt32LittleEndian(recapPayload.AsSpan(offset + 39));
            var packed = BinaryPrimitives.ReadUInt32LittleEndian(recapPayload.AsSpan(offset + 43));
            namedRow |= sourceEvent == (uint)DeathRecapEventSource.Spell &&
                sourceHash == EzrealEssenceFluxHash && parentHash == 0 &&
                parentCaster == killer.NetId && (packed & 0x0F) == (uint)DeathRecapEventSource.Character &&
                ((packed >> 4) & 0x0F) == 1 && ((packed >> 8) & 0x0F) == (uint)DeathRecapSourceType.Hero &&
                ((packed >> 12) & 0x07) == 1;
        }
        TestAssert.True(namedRow,
            "reconnect decodes the named Ezreal origin in the owner snapshot");
        foreach (var required in new byte[] { 0x61, 0x56, 0x73, 0x18 })
        {
            var index = Array.IndexOf(opcodes, required);
            TestAssert.True(index >= 0 && index < recap,
                $"reconnect opcode 0x{required:X2} precedes the recap");
        }
    }

    private static LeagueEnetPeer ConnectAndStart(
        MatchRuntimeNetwork network, LeagueEnetHost client, MatchRuntime runtime,
        List<LeagueEnetEvent> events)
    {
        var peer = client.Connect("127.0.0.1", network.Port, 8);
        PumpUntil(network, client, events, () => network.ConnectionIds.Count == 1);
        peer.Send(0, runtime.Session.BuildKeyCheck(1), LeagueEnetPacketFlags.Reliable);
        client.Flush();
        var serverConnectionId = network.ConnectionIds.Single();
        PumpUntil(network, client, events,
            () => runtime.Session.Participant(serverConnectionId) is not null);
        peer.Send(6, [1], LeagueEnetPacketFlags.Reliable);
        peer.Send(1, [0xC5], LeagueEnetPacketFlags.Reliable);
        peer.Send(1, [0xC6, 0, 0, 0, 0], LeagueEnetPacketFlags.Reliable);
        client.Flush();
        PumpUntil(network, client, events,
            () => runtime.Session.Participant(serverConnectionId) is { Loaded: true });
        PumpUntil(network, client, events,
            () => runtime.Session.Participant(serverConnectionId)?.Spawned == true);
        peer.Send(1, [0xC6, 0, 0, 0, 0], LeagueEnetPacketFlags.Reliable);
        client.Flush();
        PumpUntil(network, client, events,
            () => runtime.Session.Participant(serverConnectionId)?.Loaded == true && runtime.Session.Started);
        return peer;
    }

    private static void PumpUntil(MatchRuntimeNetwork network, LeagueEnetHost client,
        List<LeagueEnetEvent> events, Func<bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!done())
        {
            network.Pump(1, 32, flushDelivery: true);
            Drain(client, events);
            if (DateTime.UtcNow >= deadline)
                throw new InvalidOperationException("production death-recap network did not reach its boundary");
        }
    }

    private static void Drain(LeagueEnetHost client, List<LeagueEnetEvent> events)
    {
        while (true)
        {
            var item = client.Service(0);
            if (item.Type == LeagueEnetEventType.None) return;
            events.Add(item);
        }
    }

    private static byte? WireOpcode(LeagueEnetEvent item, LegacyBlowfishCipher cipher)
    {
        var plain = WirePayload(item, cipher);
        return plain.Length == 0 ? null : plain[0];
    }

    private static byte[] WirePayload(LeagueEnetEvent item, LegacyBlowfishCipher cipher)
    {
        if (item.Type != LeagueEnetEventType.Receive || item.Payload.Length == 0)
            return [];
        return item.Payload.Length >= LegacyBlowfishCipher.BlockSize &&
               item.Channel is 1 or 3 or 5
            ? cipher.Decrypt(item.Payload) : item.Payload;
    }

    private static byte Opcode(PlaintextPacket packet) =>
        packet.Plaintext.Length == 0 ? (byte)0 : packet.Plaintext.Span[0];

    private static IReadOnlyList<(uint Attacker, uint Victim, double Physical,
        double Magical, double True, uint SourceEvent, uint SourceHash,
        uint ParentHash, uint ParentCaster, uint ParentEvent, uint ParentPacked)> DecodeRows(
        PlaintextPacket packet)
    {
        var bytes = packet.Plaintext.Span;
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes[0x15..]);
        var rows = new List<(uint, uint, double, double, double, uint, uint, uint, uint, uint, uint)>(count);
        for (var index = 0; index < count; index++)
        {
            var offset = 0x19 + index * 0x2F;
            rows.Add((
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 7)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 11)..]),
                BitConverter.ToSingle(bytes[(offset + 15)..]),
                BitConverter.ToSingle(bytes[(offset + 19)..]),
                BitConverter.ToSingle(bytes[(offset + 23)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 27)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 31)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 35)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 39)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 43)..]) & 0x0F,
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 43)..])));
        }
        return rows;
    }

    private sealed class LiveMatch : IDisposable
    {
        private readonly List<PacketCapture> _captures = [];

        public LiveMatch(int seats, string champion = "Annie", IReadOnlyList<Team>? teams = null)
        {
            if (teams is not null && teams.Count != seats)
                throw new ArgumentException("Team fixture must cover every seat.", nameof(teams));
            var roster = Enumerable.Range(0, seats)
                .Select(index => new SessionParticipantDefinition(
                    (ulong)index + 1, (uint)index, $"Seat {index + 1}",
                    index == 0 ? champion : "Orianna",
                    teams?[index] ?? (index == 0 ? Team.Order : Team.Chaos)))
                .ToArray();
            Runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
                GameData.Default, readySettleSeconds: 0.0, roster: roster);
            Runtime.World.Clock.SetVirtual(0.0);
            Runtime.Session.CreateWorld();
            for (var index = 0; index < roster.Length; index++)
            {
                var connection = index + 1;
                TestAssert.True(Runtime.Session.Authenticate(connection,
                    Runtime.Session.BuildKeyCheck((ulong)connection)) is not null,
                    "production test seat authenticates");
                Runtime.Session.Spawn(connection);
                TestAssert.True(Runtime.Session.MarkReady(connection), "production test seat ready");
                var participant = Runtime.Session.Participants[index];
                participant.Hero!.Position = new Position2(7000.0 + index * 160.0, 7000.0);
                participant.Hero.MaxHealth = participant.Hero.Health = 5000.0;
                participant.Hero.HeroAutoAcquire = false;
                for (var slot = 0; slot < 4; slot++) participant.Hero.SpellRanks[slot] = 1;
                var capture = new PacketCapture();
                _captures.Add(capture);
                Runtime.Delivery.AddObserver(new ReplicationObserver(
                    participant.PlayerId, participant.Team, participant.Hero.NetId,
                    capture, ready: true, view: participant.View));
            }
            TestAssert.True(Runtime.Session.Start(), "production test match starts");
            Runtime.Delivery.Flush();
            Clear();
        }

        public MatchRuntime Runtime { get; }
        public Entity Hero(int index) => Runtime.Session.Participants[index].Hero!;
        public PacketCapture PacketsFor(int index) => _captures[index];
        public bool Cast(int seat, int slot, Entity target)
        {
            var request = new byte[34];
            request[0] = 0xA2;
            request[5] = checked((byte)(slot << 1));
            var source = Hero(seat);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), source.NetId);
            foreach (var offset in new[] { 6, 18 })
            {
                BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset), (float)target.Position.X);
                BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset + 8), (float)target.Position.Z);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(30), target.NetId);
            var accepted = false;
            Runtime.Session.Receive(seat + 1, 1, Runtime.Cipher.Encrypt(request), context =>
                accepted = Runtime.Dependencies.Gameplay(context));
            Runtime.Delivery.Flush();
            return accepted;
        }

        public bool CastSummoner(int seat, int slot, Entity target)
        {
            var request = new byte[34];
            request[0] = 0xA2;
            request[5] = checked((byte)((slot << 1) | 1));
            var source = Hero(seat);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), source.NetId);
            foreach (var offset in new[] { 6, 18 })
            {
                BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset), (float)target.Position.X);
                BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset + 8), (float)target.Position.Z);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(30), target.NetId);
            var accepted = false;
            Runtime.Session.Receive(seat + 1, 1, Runtime.Cipher.Encrypt(request), context =>
                accepted = Runtime.Dependencies.Gameplay(context));
            Runtime.Delivery.Flush();
            return accepted;
        }
        public void Clear() { foreach (var capture in _captures) capture.Packets.Clear(); }
        public void Dispose() => Runtime.World.Clock.UseReal();
    }

    private sealed class PacketCapture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];
        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }
}
