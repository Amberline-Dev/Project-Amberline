using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-b11-child-cast-slots-use-native-spellbook", Serial = true)]
    public static void ChildCastSlotsMatchLocallySeededSpellbooks()
    {
        foreach (var champion in ChampionCatalog.PlayableNames)
        foreach (var reconnect in new[] { false, true })
        for (var inputSlot = 0; inputSlot < 4; inputSlot++)
        {
            if (champion == "Vayne" && inputSlot == 1) continue; // Learned passive, no cast.
            // Shen's Stand United (slot 3) is ally-only (AffectFriends and NotAffectSelf, the acceptance matrix's "ally" row): it needs
            // a second Order champion, seat 2, to target. Its cast refuses the caster ("cannot target itself").
            var allyTarget = champion == "Shen" && inputSlot == 3;
            using var match = new Match(champion, Team.Order, ally: allyTarget);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            var enemy = match.Hero(1);
            // A slot the champion's character script seals at activation (Kassadin's Force Pulse until his Nether Blade stacks) is not castable
            // from the start, by the script's design: the roster check derives it from the spellbook, not from a list of champions.
            if (hero.SpellSeals.IsSealed(false, inputSlot)) continue;
            var baselines = match.SpawnBursts.Select(burst => burst.ToList()).ToArray();
            foreach (var baseline in baselines) AssertB11NoExtraPublication(baseline);
            using var network = reconnect ? MatchRuntimeNetwork.CreateEphemeral(runtime) : null;
            if (reconnect)
            {
                // Fresh opposing client learns the roster while the caster
                // is still hidden; its first visible cast comes after loading.
                enemy.Position = new Position2(12000, 12000);
                runtime.Delivery.Flush();
                for (var viewer = 0; viewer < 2; viewer++)
                {
                    var id = viewer + 1;
                    var connection = 100 + id;
                    TestAssert.True(runtime.Session.Disconnect(id));
                    var participant = runtime.Session.AuthenticateReconnect(connection,
                        runtime.Session.BuildKeyCheck((ulong)id))!;
                    TestAssert.True(participant is not null);
                    runtime.Session.Receive(connection, 1, [0xC5]);
                    runtime.Session.Receive(connection, 1, [0xC6, 0, 0, 0, 0]);
                    runtime.World.Clock.SetVirtual(participant!.SpawnAt!.Value);
                    baselines[viewer] = runtime.Session.RunSchedulers()
                        .Where(row => row.ConnectionId == connection).Select(row => row.Packet).ToList();
                    runtime.Session.Receive(connection, 1, [0xC6, 0, 0, 0, 0]);
                    baselines[viewer].AddRange(runtime.Session.RunSchedulers()
                        .Where(row => row.ConnectionId == connection).Select(row => row.Packet));
                    TestAssert.True(participant.Ready);
                    baselines[viewer].AddRange(network!.BuildReconnectState(participant));
                }
                TestAssert.False(runtime.Session.Participants[1].View.Visible.Contains(hero.NetId),
                    "reconnect does not reveal the hidden caster");
            }

            foreach (var capture in match.Captures) AssertB11NoExtraPublication(capture.Packets);
            match.Clear();
            // 200 units out, or inside a shorter unit cast (Cho'Gath's Feast reaches 100).
            var reach = runtime.Spells.SpellDataOf(hero, inputSlot).CastRangeAt(1);
            enemy.Position = hero.Position + new Position2(reach is > 0.0 and < 200.0 ? reach : 200.0, 0);
            if (allyTarget) match.Hero(2).Position = hero.Position + new Position2(-200, 0);
            runtime.Delivery.Flush();
            // A spell that affects friends only (shipped SpellData Flags: Molten Shield, Safeguard, Primal Surge, Protect, Eye of the Storm, Omen of
            // Death, Astral Blessing, Imbue, Divine Blessing, Intervention) is cast at the caster; every other slot at the enemy.
            var slotFlags = BbSpellTargeting.Names(BbSpellTargeting.Flags(runtime.Spells.SpellDataOf(hero, inputSlot)));
            var target = allyTarget ? match.Hero(2)
                : slotFlags.Contains("AffectFriends") && !slotFlags.Contains("AffectEnemies") ? hero : enemy;
            // A unit spell that refuses champions (Nunu's Consume: minions and monsters only) has no target in this two-champion match;
            // its champion scenario casts it.
            if (target == enemy && BbChampionModule.Targeting(runtime.Spells.SpellDataOf(hero, inputSlot)) ==
                Amberline.Scripting.BuildingBlocks.Runtime.BbTargetType.TTYPE_Target && BbSpellTargeting.RejectTarget(hero, enemy, slotFlags) is not null) continue;
            var point = new Position3(target.Position.X,
                runtime.Navigation.HeightAt(target.Position), target.Position.Z);
            // Cone requests separate the casting position from the aim endpoint.
            // A distant Annie W start queues movement, which SpellStep does not run.
            // Miss Fortune's Bullet Time is a cone too (TTYPE_Cone): BbChampionModule measures a cone's cast point from the caster within 75
            // units, and the enemy 200 units away is out of it. Her start is the caster, as Annie W's is; the enemy stays the aim endpoint.
            // Every TTYPE_Cone slot (Bullet Time, Volley, Feral Scream, ...) starts at the caster the same way.
            var casterStart = (champion == "Annie" && inputSlot == 1)
                || BbChampionModule.Targeting(runtime.Spells.SpellDataOf(hero, inputSlot)) == Amberline.Scripting.BuildingBlocks.Runtime.BbTargetType.TTYPE_Cone;
            var start = casterStart
                ? new Position3(hero.Position.X, runtime.Navigation.HeightAt(hero.Position), hero.Position.Z)
                : point;
            var context = $"{champion} slot {inputSlot} reconnect={reconnect}";
            // An ammo spell casts only with charges (its CanCast): Akali's Shadow Dance needs two stacks, Corki's Missile Barrage one charge (the
            // stack count minus one, batch 255), Teemo's Noxious Trap a stored mushroom, Heimerdinger's H-28G a stocked turret. Seed them as champion-acceptance-matrix.json's ammo row does.
            var ammo = (champion, inputSlot) switch { ("Akali", 3) => ("AkaliShadowDance", 2), ("Corki", 3) => ("MissileBarrage", 3), ("Teemo", 3) => ("TeemoMushrooms", 3), ("Heimerdinger", 0) => ("HeimerdingerTurretReady", 1), _ => (null as string, 0) };
            if (ammo.Item1 is not null)
                for (var charge = 0; charge < ammo.Item2; charge++)
                    ((BbChampionModule)runtime.Dependencies.ChampionDispatcher.FindModule(champion)!).Buffs!.Add(new BbBuffRequest(hero, hero, ammo.Item1,
                        (int)Amberline.Scripting.BuildingBlocks.Runtime.BbBuffAddType.BUFF_STACKS_AND_RENEWS, (int)Amberline.Core.Buffs.BuffType.AmmoStack, 4, 25000.0, 0.0,
                        false, new Amberline.Scripting.BuildingBlocks.Runtime.BbTable(), 1, true));
            // Kennen's Electrical Surge casts only with a marked enemy within 800 (its CanCast): mark the enemy as the acceptance row does.
            if (champion == "Kennen" && inputSlot == 1)
                ((BbChampionModule)runtime.Dependencies.ChampionDispatcher.FindModule(champion)!).Buffs!.Add(new BbBuffRequest(enemy, hero, "KennenMarkofStorm",
                    (int)Amberline.Scripting.BuildingBlocks.Runtime.BbBuffAddType.BUFF_STACKS_AND_RENEWS, (int)Amberline.Core.Buffs.BuffType.CombatDehancer, 5, 8.0, 0.0,
                    false, new Amberline.Scripting.BuildingBlocks.Runtime.BbTable(), 1, true));
            var validation = runtime.Spells.CanCast(hero, inputSlot, target.NetId, start,
                runtime.Spells.CurrentTime);
            TestAssert.True(validation.Allowed, $"{context} ready to cast: {validation}");
            TestAssert.True(runtime.Spells.HandleCastRequest(hero,
                new SpellCastRequest(inputSlot, false, start, point, target.NetId),
                runtime.Spells.CurrentTime), context + " accepted");
            TestAssert.True(hero.PendingCast is null, context + " executes without an approach");
            TestAssert.Equal(1, hero.MatchStatistics.SpellCasts[inputSlot], context + " committed once");
            runtime.Delivery.Flush();
            SpellStep(match, .3);
            runtime.Delivery.Flush();
            foreach (var viewer in new[] { 0, 1 })
            {
                var baseline = baselines[viewer];
                var casts = match.Captures[viewer].Packets.Where(packet => Opcode(packet) == 0xBD &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId).ToArray();
                // Deceive may hide its caster in the same delivery flush;
                // require every owner cast and inspect every delivered enemy cast.
                if (viewer == 0) TestAssert.True(casts.Length > 0, context + " actually cast");
                var childHash = (champion, inputSlot) switch
                {
                    ("Yorick", 1) => 0x00556F54u,
                    ("Ezreal", 0) => 0x05FE9CE5u,
                    ("Ezreal", 1) => 0x0FA2B315u,
                    ("Blitzcrank", 0) => 0x00DD4395u,
                    _ => 0u,
                };
                if (childHash != 0)
                {
                    // Batch 393 (owner findings 16, 58, 84): a child that launches a missile reaches its caster's own client as the 0x41 missile
                    // alone, with no cast answer, so that client builds no SpellCaster and opens no cast bar. The other viewers keep the 0xBD, and
                    // a child with no missile keeps its cast answer for every viewer, unless it is a channel-less child that is not a container child
                    // (batch 394, Pantheon E's Heartseeker): that one reaches the owner as the 0x41 alone too, so the owner gets no cast answer for it.
                    var missileOnly = viewer == 0 && match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x41 && ChildSpellHash(packet) == childHash);
                    TestAssert.Equal(missileOnly ? 0 : 1, casts.Count(packet => ChildSpellHash(packet) == childHash),
                        "the normal child was emitted, not suppressed to avoid the assertion");
                }
                var packets = baseline.Concat(match.Captures[viewer].Packets).ToArray();
                AssertB11NoExtraPublication(packets);
                AssertB11SpellbookOrder(match, packets, $"{context} viewer {viewer}");
            }
        }
    }

    // Batch 393 (owner findings 16, 58): a child cast that launches a missile reaches its caster's own client as the 0x41 missile alone. A cast
    // answer (0xBD) there would build a SpellCaster when the spellbook is empty, and that SpellCaster opens the HUD cast bar; the missile packet
    // builds the object with no caster (the spell helper's cast routine). The other viewers keep their 0xBD.
    [Check("spells-b393-malzahar-q-child-missile-skips-the-casters-cast-answer")]
    public static void MalzaharQChildMissileSkipsTheCastersCastAnswer()
    {
        using var match = new Match("Malzahar", Team.Order);
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        // The portals of Call of the Void stand beyond a short aim (batch 391 placed its enemies 380 to 500 units out).
        enemy.Position = hero.Position + new Position2(450, 0);
        runtime.Delivery.Flush();
        match.Clear();
        var point = new Position3(enemy.Position.X, runtime.Navigation.HeightAt(enemy.Position), enemy.Position.Z);
        TestAssert.True(runtime.Spells.HandleCastRequest(hero, new SpellCastRequest(0, false, point, point, 0), runtime.Spells.CurrentTime),
            "Malzahar's Call of the Void is accepted");
        runtime.Delivery.Flush();
        RunSpellTenths(match, 30);
        AssertChildMissileSkipsTheCastersCastAnswer(match, "AlZaharCalloftheVoidMissile");
    }

    [Check("spells-b393-fiddlesticks-dark-wind-bounces-skip-the-casters-cast-answer")]
    public static void FiddlesticksDarkWindBouncesSkipTheCastersCastAnswer()
    {
        // A bounce needs a second enemy beside the target: the partner stands 400 beyond it, as the scripting check places it.
        using var match = new Match("FiddleSticks", Team.Order, extraEnemy: true);
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        enemy.Position = hero.Position + new Position2(300, 0);
        match.Hero(2).Position = enemy.Position + new Position2(400, 0);
        runtime.Delivery.Flush();
        match.Clear();
        var point = new Position3(enemy.Position.X, runtime.Navigation.HeightAt(enemy.Position), enemy.Position.Z);
        TestAssert.True(runtime.Spells.HandleCastRequest(hero, new SpellCastRequest(2, false, point, point, enemy.NetId), runtime.Spells.CurrentTime),
            "Fiddlesticks' Dark Wind is accepted");
        runtime.Delivery.Flush();
        RunSpellTenths(match, 40);
        AssertChildMissileSkipsTheCastersCastAnswer(match, "FiddleSticksDarkWindMissile");
    }

    // Batch 394 (Part 3, owner finding 84): Pantheon E's Heartseeker child is CastType 0 with no missile and no channel of its own. Its caster's own
    // client takes it as the 0x41 with MissileNetId 0 instead of the cast answer: the answer built a second SpellCaster and so a second HUD cast bar.
    // The other viewer keeps the cast answer.
    [Check("spells-b394-pantheon-e-child-without-missile-skips-the-casters-cast-answer")]
    public static void PantheonEChildWithoutMissileSkipsTheCastersCastAnswer()
    {
        using var match = new Match("Pantheon", Team.Order);
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        enemy.Position = hero.Position + new Position2(450, 0);
        runtime.Delivery.Flush();
        match.Clear();
        var point = new Position3(enemy.Position.X, runtime.Navigation.HeightAt(enemy.Position), enemy.Position.Z);
        TestAssert.True(runtime.Spells.HandleCastRequest(hero, new SpellCastRequest(2, false, point, point, enemy.NetId), runtime.Spells.CurrentTime),
            "Pantheon's Heartseeker Strike (E) is accepted");
        runtime.Delivery.Flush();
        RunSpellTenths(match, 40);
        var hash = Amberline.Simulation.Spells.SpellSystem.ProtocolHash("Pantheon_HeartseekerChannel");
        var owner = match.Captures[0].Packets.Where(packet => Opcode(packet) is 0xBD or 0x41 && ChildSpellHash(packet) == hash).ToArray();
        TestAssert.True(owner.Any(packet => Opcode(packet) == 0x41 && MissileNetId(packet) == 0),
            "the Heartseeker child reaches the caster's client as a 0x41 with no missile (MissileNetId 0)");
        TestAssert.Equal(0, owner.Count(packet => Opcode(packet) == 0xBD),
            "the Heartseeker child sends the caster's client no cast answer (0xBD), so its client builds no SpellCaster and opens no cast bar");
        TestAssert.True(match.Captures[1].Packets.Any(packet => Opcode(packet) == 0xBD && ChildSpellHash(packet) == hash),
            "the Heartseeker child keeps its cast answer for the other viewer");
    }

    // Batch 400 (owner finding, 27 October 2026: "unable to move when Fiddlesticks E exists, it bounces from target to target"). Dark Wind's crows are
    // child casts (FiddleSticksDarkWindMissile) that run after E's own cast frame (0.25 s). The move order is sent 0.5 s after that frame, while the
    // crows are still bouncing; the hero must walk at once, as retail does once E's own cast is over. The trace is per 0.05 s step.
    [Check("spells-b400-fiddlesticks-dark-wind-bounces-do-not-hold-the-move-order")]
    public static void FiddlesticksDarkWindBouncesDoNotHoldTheMoveOrder()
    {
        using var match = new Match("FiddleSticks", Team.Order, extraEnemy: true);
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        enemy.Position = hero.Position + new Position2(300, 0);
        // The partner stands 200 beyond the target: the crows then bounce about every 0.2 s, their windups overlap, and the stall runs
        // the whole chain (measured on master: the hero is still at the order's spot until the last crow's windup and settle end).
        match.Hero(2).Position = enemy.Position + new Position2(200, 0);
        runtime.Delivery.Flush();
        match.Clear();
        var point = new Position3(enemy.Position.X, runtime.Navigation.HeightAt(enemy.Position), enemy.Position.Z);
        TestAssert.True(runtime.Spells.HandleCastRequest(hero, new SpellCastRequest(2, false, point, point, enemy.NetId), runtime.Spells.CurrentTime),
            "Fiddlesticks' Dark Wind is accepted");
        var crow = Amberline.Simulation.Spells.SpellSystem.ProtocolHash("FiddleSticksDarkWindMissile");
        var order = new Position2(hero.Position.X - 600, hero.Position.Z);
        const double step = 0.05;
        const int orderStep = 15; // 0.75 s after the cast: E's cast frame (0.25 s) plus 0.5 s
        const int halfSecond = 10;
        var accepted = false;
        var seen = 0;
        var crowsAfterOrder = 0;
        var positions = new List<Position2> { hero.Position }; // positions[k]: the hero after k steps
        var trace = new List<string>();
        for (var n = 1; n <= orderStep + halfSecond; n++)
        {
            runtime.Tick(step);
            runtime.Delivery.Flush();
            var packets = match.Captures[0].Packets;
            var fresh = packets.Skip(seen).ToArray();
            seen = packets.Count;
            positions.Add(hero.Position);
            var crows = fresh.Count(packet => Opcode(packet) is 0x41 or 0xBD && ChildSpellHash(packet) == crow);
            if (n > orderStep) crowsAfterOrder += crows;
            var ops = string.Join(",", fresh.Select(Opcode).Where(code => code is 0x39 or 0x41 or 0xBD or 0xC1).Select(code => $"0x{code:X2}"));
            trace.Add($"t={n * step:0.00} x={hero.Position.X:0} z={hero.Position.Z:0} wp={hero.Waypoints.Count} " +
                      $"windup={hero.CastWindupRemaining:0.00} held={Amberline.Simulation.Spells.SpellWindup.IsWindingUp(hero)} " +
                      $"clip={hero.CastClipActive} canMove={Amberline.Simulation.Buffs.CharacterState.Of(hero).CanMove} crows={crows} ops=[{ops}]");
            if (n != orderStep) continue;
            var plaintext = ClientGameplayPackets.MoveOrder(hero.NetId, 2, order).Plaintext.ToArray();
            runtime.Session.Receive(1, 1, runtime.Cipher.Encrypt(plaintext),
                context => accepted = runtime.Dependencies.Gameplay(context));
        }
        var walked = Distance(positions[orderStep], positions[orderStep + halfSecond]);
        var bouncing = string.Join(" | ", trace);
        TestAssert.True(accepted, "the move order is accepted");
        TestAssert.True(crowsAfterOrder > 0, "the crows are still bouncing after the order: " + bouncing);
        TestAssert.True(walked >= 50.0,
            $"the hero walks while the crows bounce ({walked:0} units in the 0.5 s after the order): " + bouncing);

        static double Distance(Position2 a, Position2 b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));
    }

    // Steps of a tenth of a second through the whole match tick (the Scripting harness's Advance): the spell tick alone never runs a champion's
    // own timers, and Malzahar's Call of the Void portals fire from one. Delivered after each step, so a child cast fired mid-flight is seen.
    private static void RunSpellTenths(Match match, int tenths)
    {
        for (var step = 0; step < tenths; step++)
        {
            match.Runtime.Tick(0.1);
            match.Runtime.Delivery.Flush();
        }
    }

    private static uint ChildSpellHash(PlaintextPacket packet) =>
        Decode(packet).GetProperty("cast_info").GetProperty("spell_hash").GetUInt32();

    private static uint MissileNetId(PlaintextPacket packet) =>
        Decode(packet).GetProperty("cast_info").GetProperty("missile_netid").GetUInt32();

    private static void AssertChildMissileSkipsTheCastersCastAnswer(Match match, string spell)
    {
        var hash = Amberline.Simulation.Spells.SpellSystem.ProtocolHash(spell);
        var owner = match.Captures[0].Packets.Where(packet => Opcode(packet) is 0xBD or 0x41 && ChildSpellHash(packet) == hash).ToArray();
        TestAssert.True(owner.Any(packet => Opcode(packet) == 0x41), $"{spell} still reaches the caster's client as its missile (0x41)");
        TestAssert.Equal(0, owner.Count(packet => Opcode(packet) == 0xBD),
            $"{spell} sends the caster's client no cast answer (0xBD), so its client builds no SpellCaster and opens no cast bar");
        TestAssert.True(match.Captures[1].Packets.Any(packet => Opcode(packet) == 0xBD && ChildSpellHash(packet) == hash),
            $"{spell} keeps its cast answer for the other viewer");
    }

    // Batch 323: CharScriptNocturne's CharOnSpellCast renames slot 3 to NocturneParanoia2 and restores its cooldown when Paranoia is cast, inside the
    // cast's start (before the 0xBD is queued). The owner gets the cast while its slot still holds NocturneParanoia (the client takes the cast's
    // cooldown only then), then the 0x35, then the 0x8A that the 0x35's cooldown reset needs (ReplicationDelivery.AnnounceCastsBeforeTheirSlotRenames).
    [Check("spells-b323-cast-hook-slot-rename-follows-the-cast")]
    public static void CastHookSlotRenameFollowsTheCast()
    {
        using var match = new Match("Nocturne", Team.Order);
        var hero = match.Hero(0);
        match.Clear();
        match.Cast(0, 3, hero);
        match.Runtime.Delivery.Flush();
        var slotPackets = match.Captures[0].Packets.Where(packet => Opcode(packet) is 0xBD or 0x35 or 0x8A &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId).ToList();
        var cast = slotPackets.FindIndex(packet => Opcode(packet) == 0xBD &&
            Decode(packet).GetProperty("cast_info").GetProperty("spell_hash").GetUInt32() == ProtocolEncoding.HashString("NocturneParanoia"));
        var rename = slotPackets.FindIndex(packet => SpellPackets.TryReadChangeSlotSpellName(packet.Plaintext.Span, out var update) &&
            update is { Slot: 3, IsSummoner: false, SpellName: "NocturneParanoia2" });
        TestAssert.True(cast >= 0, "Paranoia was announced");
        TestAssert.True(rename > cast, $"the cast precedes its hook's rename (cast {cast}, rename {rename})");
        TestAssert.True(slotPackets.FindIndex(rename, packet => Opcode(packet) == 0x8A && packet.Plaintext.Span[5] == 3) > rename,
            "the hook's cooldown restore follows the rename");
        TestAssert.Equal("NocturneParanoia2", hero.ActiveSpellNames[3], "the slot holds the dash after the first press");
    }

    // Batch 325: a viewer that cannot see Nocturne when Paranoia renames his slot 3 still gets the 0x35. Retail's BBSetSpell sends
    // it through the visibility broadcast, and the client applies it to any unit it holds (its handler
    // has no visibility test; the reveal's 0xC2 carries no spellbook). So when he comes back into sight and casts the dash, the enemy's
    // book already holds NocturneParanoia2 in slot 3.
    [Check("spells-b325-slot-rename-reaches-a-viewer-in-fog")]
    public static void SlotRenameReachesAViewerInFog()
    {
        using var match = new Match("Nocturne", Team.Order);
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        var enemyView = runtime.Session.Participants[1].View;
        enemy.Position = new Position2(12000, 12000);
        runtime.Delivery.Flush();
        TestAssert.False(enemyView.Visible.Contains(hero.NetId), "the enemy cannot see Nocturne");
        match.Cast(0, 3, hero);
        runtime.Delivery.Flush();
        TestAssert.Equal("NocturneParanoia2", hero.ActiveSpellNames[3], "the first press renamed slot 3");
        TestAssert.False(enemyView.Visible.Contains(hero.NetId), "the rename happened while the enemy was blind");
        TestAssert.True(match.Captures[1].Packets.Any(packet => SpellPackets.TryReadChangeSlotSpellName(packet.Plaintext.Span, out var update) &&
            update is { Slot: 3, IsSummoner: false, SpellName: "NocturneParanoia2" } && update.NetId == hero.NetId),
            "the blind enemy got the rename (TryBroadcast)");

        enemy.Position = hero.Position + new Position2(150, 0);
        runtime.Delivery.Flush();
        TestAssert.True(enemyView.Visible.Contains(hero.NetId), "the enemy sees Nocturne again");
        SpellStep(match, .3);
        runtime.Delivery.Flush();
        match.Cast(0, 3, enemy);
        runtime.Delivery.Flush();
        var dash = match.Captures[1].Packets.Count(packet => Opcode(packet) == 0xBD &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId &&
            Decode(packet).GetProperty("cast_info").GetProperty("spell_hash").GetUInt32() == ProtocolEncoding.HashString("NocturneParanoia2"));
        TestAssert.Equal(1, dash, "the enemy sees the dash cast");
        AssertB11SpellbookOrder(match, match.SpawnBursts[1].Concat(match.Captures[1].Packets), "Nocturne slot 3, viewer 1 blind at the rename");
    }

    // Batch 329: a client that reconnects while Nocturne's slot 3 is renamed holds the rename. Its book of every other hero is rebuilt from the
    // character's defaults (CreateHero), so the reconnect burst replays each other hero's renamed slots: the 0x35 every client holding the unit
    // received when the script ran (TryBroadcast, batch 325).
    [Check("spells-b329-reconnect-holds-other-heroes-slot-renames")]
    public static void ReconnectHoldsOtherHeroesSlotRenames()
    {
        using var match = new Match("Nocturne", Team.Order);
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        match.Cast(0, 3, hero);
        runtime.Delivery.Flush();
        TestAssert.Equal("NocturneParanoia2", hero.ActiveSpellNames[3], "the first press renamed slot 3");
        var packets = MatchRuntimeNetwork.BuildReconnectState(runtime, runtime.Session.Participants[1]);
        TestAssert.True(packets.Any(packet => SpellPackets.TryReadChangeSlotSpellName(packet.Plaintext.Span, out var update) &&
            update is { Slot: 3, IsSummoner: false, SpellName: "NocturneParanoia2" } && update.NetId == hero.NetId),
            "the reconnecting enemy gets Nocturne's renamed slot");
        TestAssert.False(packets.Any(packet => SpellPackets.TryReadChangeSlotSpellName(packet.Plaintext.Span, out var update) &&
            update.NetId == hero.NetId && update is { Slot: < 3, IsSummoner: false }), "and no rename of a slot that holds its default");
    }

    [Check("spells-b11-attack-overrides-use-native-extra-slots")]
    public static void AttackOverridesUseNativeExtraSlots()
    {
        foreach (var (champion, ultimate, tumble, slot, name) in new[]
                 {
                     ("Nidalee", true, true, 42, "NidaleeTakedownAttack"),
                     ("Vayne", false, true, 44, "VayneTumbleAttack"),
                     ("Vayne", true, false, 46, "VayneUltAttack"),
                     ("Vayne", true, true, 47, "VayneTumbleUltAttack"),
                 })
        {
            using var match = new Match(champion, Team.Order);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            if (ultimate)
            {
                match.Cast(0, 3, hero);
                Advance(match, champion == "Nidalee" ? 6 : .3);
            }
            if (tumble)
            {
                match.CastGround(0, hero.Position + new Position2(0, 300));
                Advance(match, .4);
            }
            var model = champion == "Nidalee" ? "Nidalee_Cougar" : champion;
            TestAssert.Equal(name, B11ExtraSpellNames(match, model)[slot - 42],
                "override selects the active character's shipped extra spell");
            var activation = match.Captures[0].Packets.Last(packet => Opcode(packet) == 0x22 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId);
            TestAssert.Equal((byte)slot, activation.Plaintext.Span[5], "activation uses the native extra slot");

            // Fresh views use the same champion hydration as reconnect.
            var capture = new Capture();
            var observer = new ReplicationObserver(90, hero.Team, hero.NetId, capture);
            observer.View.Known.UnionWith(runtime.Session.Participants.Select(participant => participant.Hero!.NetId));
            runtime.Delivery.AddObserver(observer);
            runtime.Delivery.Flush();
            var restored = capture.Packets.Last(packet => Opcode(packet) == 0x22 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId);
            TestAssert.Equal((byte)slot, restored.Plaintext.Span[5], "fresh view restores the same native extra slot");
            AssertB11NoExtraPublication(capture.Packets);

            foreach (var buff in hero.Buffs.Where(buff => buff.Name is
                         "Takedown" or "VayneTumbleBonus" or "VayneInquisition").ToArray())
                runtime.SpellBuffs.Remove(hero, buff.Slot);
            runtime.Delivery.Flush();
            var reset = match.Captures[0].Packets.Last(packet => Opcode(packet) == 0x22 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId);
            TestAssert.Equal((byte)255, reset.Plaintext.Span[5], "removal restores the ordinary attack selector");
        }
    }

    private static void AssertB11NoExtraPublication(IEnumerable<PlaintextPacket> packets) =>
        TestAssert.False(packets.Any(packet => Opcode(packet) == 0x73 &&
            packet.Plaintext.Span[13] is >= 42 and <= 49),
            "hero spawn, reconnect and ordinary casts rely on locally seeded extra slots");

    private static string[] B11ExtraSpellNames(Match match, string champion)
    {
        var data = match.Runtime.GameData.Champion(champion).Raw;
        return Enumerable.Range(1, 8).Select(index =>
            data.TryGetValue(InibinReader.KeyHash($"Data*ExtraSpell{index}"), out var value) &&
            value.Kind == InibinValueKind.String ? value.AsString() : string.Empty).ToArray();
    }

    private static void AssertB11SpellbookOrder(Match match, IEnumerable<PlaintextPacket> packets, string context)
    {
        var known = new HashSet<uint>();
        var book = new Dictionary<(uint Hero, int Slot), uint>();
        foreach (var packet in packets)
        {
            var bytes = packet.Plaintext.Span;
            if (Opcode(packet) == 0x4F)
            {
                var id = BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..]);
                known.Add(id);
                // The native spellbook init seeds QWER and ExtraSpell1..8 from the
                // CreateHero character, without an extra-slot 0x73 baseline.
                var champion = match.Runtime.World.Get(id)!;
                var defaults = match.Runtime.GameData.Champion(champion.ChampionName).SpellNames;
                for (var slot = 0; slot < defaults.Count; slot++)
                    book[(id, slot)] = ProtocolEncoding.HashString(defaults[slot]);
                var extras = B11ExtraSpellNames(match, champion.ChampionName);
                for (var index = 0; index < extras.Length; index++)
                    if (!string.IsNullOrWhiteSpace(extras[index]))
                        book[(id, 42 + index)] = ProtocolEncoding.HashString(extras[index]);
            }
            else if (Opcode(packet) == 0x80)
            {
                // Batch 202: a script placemarker is announced by its spawn packet like a minion (Entity.Announced), and a spell a script gives it
                // is named after that spawn, so the spawn counts as its creation here.
                var id = BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..]);
                known.Add(id);
                // Batch 343: every unit seeds its spellbook from its character record when it is created, not only a hero:
                // the unit's network-ID hook calls the spellbook init with the unit's character data (the AI loader and the turret's init
                // call it first too). A script minion whose record lists its spell has it at 42 without a
                // 0x73: XerathArcaneBarrage spawns a XerathArcaneBarrageLauncher (BBSpawnMinion) and casts its ExtraSlots[0], the record's
                // ExtraSpell1 XerathArcaneBarrageMinion. The 0x80's skin is the record (byte 103, 64 bytes); TestCube lists no wave spell.
                var skin = System.Text.Encoding.ASCII.GetString(bytes.Slice(103, 64)).Split('\0')[0];
                if (skin.Length > 0 && match.Runtime.GameData.HasCharacterData(skin))
                {
                    var extras = B11ExtraSpellNames(match, skin);
                    for (var index = 0; index < extras.Length; index++)
                        if (!string.IsNullOrWhiteSpace(extras[index]))
                            book[(id, 42 + index)] = ProtocolEncoding.HashString(extras[index]);
                }
            }
            else if (Opcode(packet) == 0x73)
            {
                var id = BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]);
                TestAssert.True(known.Contains(id), "spell data follows hero creation");
                book[(id, bytes[13])] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[9..]);
            }
            else if (SpellPackets.TryReadChangeSlotSpellName(bytes, out var update) && !update.IsSummoner)
            {
                TestAssert.True(known.Contains(update.NetId), "spell name follows hero creation");
                book[(update.NetId, update.Slot)] = ProtocolEncoding.HashString(update.SpellName);
            }
            else if (Opcode(packet) == 0xBD)
            {
                var info = Decode(packet).GetProperty("cast_info");
                var id = BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]);
                var slot = info.GetProperty("slot").GetInt32();
                var castHash = info.GetProperty("spell_hash").GetUInt32();
                // QWER may duplicate an extra name (Hallucinate/BlindMonkRKick).
                // A cast outside the matching primary slot must use its native extra index.
                if (slot >= 4 || !book.TryGetValue((id, slot), out var primaryHash) || primaryHash != castHash)
                {
                    var caster = match.Runtime.World.Get(id)!.ChampionName;
                    TestAssert.True(slot is >= 42 and <= 49,
                        $"{context}: child casts use native extra slots ({caster} slot {slot} cast hash {castHash:X8}, recipient's slot holds " +
                        (book.TryGetValue((id, slot), out var held) ? $"{held:X8}" : "nothing") + ")");
                    // A script-spawned unit (Galio's Righteous Gust cubes, a TestCubeRender) casts its script's spell from an extra slot too, but has
                    // no champion inibin to list it: the ExtraSpell lookup applies to heroes.
                    if (match.Runtime.World.Get(id)!.UnitType == UnitType.Hero)
                    {
                        var extras = B11ExtraSpellNames(match, match.Runtime.World.Get(id)!.ChampionName);
                        var index = Array.FindIndex(extras, name => !string.IsNullOrWhiteSpace(name) &&
                            ProtocolEncoding.HashString(name) == castHash);
                        TestAssert.True(index >= 0, $"child spell exists in the champion inibin ({caster} slot {slot} cast hash {castHash:X8})");
                        TestAssert.Equal(42 + index, slot, "child slot follows shipped ExtraSpell1..8 order");
                    }
                }
                TestAssert.True(book.TryGetValue((id, slot), out var hash),
                    $"{context}: recipient has spell data before cast in slot {slot}");
                TestAssert.Equal(castHash, hash, "locally seeded slot matches the cast's spell data");
            }
        }
    }
}
