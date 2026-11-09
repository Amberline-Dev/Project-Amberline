using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Pets;
using Amberline.Network.Replication;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    // Reconstruction (19 October 2026): the hand-written port refused a Bushwhack at a wall cell; the shipped script spawns the trap at the cast point,
    // and the engine's summon floor (the owner decision for summons, now also for BBSpawnMinion) moves a blocked aim to the closest legal floor.
    [Check("spells-live-nidalee-trap-wall-placement-is-projected")]
    public static void BushwhackAtAWallCellPlacesTheTrapOnTheClosestFloor()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Nidalee", team);
            var runtime = match.Runtime;
            var hero = match.Hero(0);
            hero.Position = new Position2(6913, 6635);
            var grid = runtime.Navigation;
            IEnumerable<Entity> Traps() => runtime.World.Entities.Values.Where(unit =>
                unit.Kind == EntityKind.NidaleeTrap && unit.OwnerNetId == hero.NetId);
            TestAssert.True(grid.IsMovementWalkable(hero.Position));
            var range = GameData.Default.Spell("Bushwhack").CastRange;
            var blocked = Enumerable.Range(0, grid.CountX * grid.CountY)
                .Select(index => grid.CellToWorld(new NavCell(index % grid.CountX, index / grid.CountX)))
                .First(point =>
                {
                    var dx = point.X - hero.Position.X;
                    var dz = point.Z - hero.Position.Z;
                    return dx * dx + dz * dz < range * range && !grid.IsMovementWalkable(point);
                });
            var mana = hero.Mana;
            match.CastGround(0, blocked, slot: 1);
            runtime.Tick(.5);
            TestAssert.True(hero.Mana < mana, $"{team}: the cast is paid");
            TestAssert.Equal(1, Traps().Count(), $"{team}: one trap");
            TestAssert.True(grid.IsMovementWalkable(Traps().Single().Position), $"{team}: the trap stands on walkable ground");
        }
    }

    [Check("spells-live-nidalee-trap-percent-modifier-wire")]
    public static void BushwhackPercentModifierIsAdditive()
    {
        var packet = CombatPackets.MinionVisionPercentModifier(0x40030000, -1);
        TestAssert.Equal((byte)0xCC, packet.Plaintext.Span[0]);
        TestAssert.Equal((byte)8, packet.Plaintext.Span[10]);
        TestAssert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[15..]));
        TestAssert.Equal(-1f, BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[19..]));
        TestAssert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(
            CombatPackets.MinionVisionPercentModifier(0x40030000, 0).Plaintext.Span[19..]));
        foreach (var invalid in new[] { -1.01f, float.NaN, float.NegativeInfinity, float.PositiveInfinity })
            TestAssert.Throws<ArgumentOutOfRangeException>(
                () => CombatPackets.MinionVisionPercentModifier(0x40030000, invalid));
    }

    [Check("spells-live-nidalee-placement-vision-flash")]
    public static void BushwhackPlacementMakesNoVisionFlash()
    {
        // Batch 222 (restated after batch 221): Bushwhack.luaobj's BBSpawnMinion passes VisibilitySize 0.0 and no sight spell (batch 221,
        // .artifacts/b221/bushwhack-dis.txt), so the trap's placement makes no timed vision bubble, no team sight and no wire add. The flash
        // this check used to read was the hand-written module's; the shipped script places none.
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            using var match = new Match("Nidalee", team, ally: true);
            var runtime = match.Runtime;
            var caster = match.Hero(0);
            // Every map turret's own perception bubble is the map's, not the cast's (see batch 159 step 2).
            var mapBubbles = runtime.World.TimedVisionBubbles.Select(bubble => bubble.BubbleId).ToHashSet();
            caster.Position = new Position2(6913, 6635);
            match.Cast(0, 1, caster);
            for (var step = 0; step < 400 && !runtime.World.Entities.Values.Any(unit => unit.Kind == EntityKind.NidaleeTrap && unit.OwnerNetId == caster.NetId); step++)
                runtime.Spells.Tick(.001);
            runtime.Delivery.Flush();
            var trap = runtime.World.Entities.Values.Single(unit =>
                unit.Kind == EntityKind.NidaleeTrap && unit.OwnerNetId == caster.NetId);
            var bubbleId = trap.NetId ^ 0x2000_0000u;
            TestAssert.Equal(0, runtime.World.TimedVisionBubbles.Count(bubble => !mapBubbles.Contains(bubble.BubbleId)),
                "the trap's placement makes no timed vision bubble (VisibilitySize 0.0)");
            TestAssert.Equal(0.0, VisibilityRules.TerrainVisionRange(runtime.World.Get(trap.NetId)!));
            TestAssert.False(runtime.Dependencies.AdditionalProviders(team).Any(provider => provider.Position == trap.Position),
                "the placement contributes no team sight");
            runtime.Spells.Tick(.25);
            runtime.Delivery.Flush();
            foreach (var capture in match.Captures)
                TestAssert.False(capture.Packets.Any(packet => Opcode(packet) == 0x26 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[25..]) == bubbleId),
                    "no seat receives a placement bubble");
        }
    }

    // Batch 729 (owner report, 9 November 2026: "stepping into Nidalee trap doesn't produce any sound"). A springing trap creates
    // nidalee_bushwhack_trigger_01.troy and nidalee_bushwhack_trigger_02.troy at its own point (the trap is their target), adds BushwhackDebuff to
    // the enemy and removes its own Bushwhack buff. The only sound among them is trigger_02's OnCreate LoL_Audio/Spells/Bushwhack/hit, which the
    // shipped bank cannot resolve (BushwhackTriggerSoundNamesAnEventTheBankLacks). This check records what each seat is sent at the trigger.
    // Batch 729 follow-up (owner's batch 113 request, "stepping on one shows it"): the enemy stands in sight of the trap's spot the whole time, yet
    // nothing that names the armed trap reaches it before the spring; at the spring it is sent the trap, both creates that name it, and the death.
    [Check("spells-live-nidalee-trap-trigger-wire-by-seat")]
    public static void BushwhackTriggerWireBySeat()
    {
        using var match = new Match("Nidalee", Team.Order);
        var runtime = match.Runtime;
        var trapper = match.Hero(0);
        var foe = match.Hero(1);
        IEnumerable<Entity> Traps() => runtime.World.Entities.Values.Where(unit =>
            unit.Kind == EntityKind.NidaleeTrap && unit.OwnerNetId == trapper.NetId);
        match.CastGround(0, new Position2(7600, 7000), slot: 1);
        for (var step = 0; step < 60 && !Traps().Any(); step++) runtime.Tick(.05);
        TestAssert.Equal(1, Traps().Count(), "the trap is placed");
        var trap = Traps().Single();
        var trapSlot = trap.Buffs.Single(buff => buff.Name == "Bushwhack").Slot;
        runtime.Delivery.Flush();
        match.Clear();
        // The enemy stands 50 units from the trap: inside its 150 range and in sight of its spot. The trap arms 0.9 s after it is placed, and
        // until it springs the enemy must not be sent anything that names it (batch 113: hidden from the enemy).
        foe.Position = new Position2(7650, 7000);
        for (var step = 0; step < 120 && foe.Buffs.All(buff => buff.Name != "BushwhackDebuff"); step++)
        {
            runtime.Tick(.05);
            if (foe.Buffs.All(buff => buff.Name != "BushwhackDebuff"))
                TestAssert.Equal(0, match.Captures[1].Packets.Count(packet => CarriesNetId(packet, trap.NetId)),
                    "before the spring the enemy is sent nothing that names the armed, hidden trap");
        }
        TestAssert.True(foe.Buffs.Any(buff => buff.Name == "BushwhackDebuff"), "the trap springs on the enemy within 150");
        for (var step = 0; step < 4; step++) runtime.Tick(.05);
        runtime.Delivery.Flush();

        int Creates(int seat, string stem) => match.Captures[seat].Packets.Count(packet => Opcode(packet) == FxPackets.CreateGroupOpcode &&
            packet.Plaintext.Length >= 33 && BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == SpellPackets.BuffNameHash(stem));
        TestAssert.Equal(1, Creates(0, "nidalee_bushwhack_trigger_02"), "the trapper's seat is sent trigger_02 once (its OnCreate sound is the trap's)");
        TestAssert.Equal(1, Creates(0, "nidalee_bushwhack_trigger_01"), "the trapper's seat is sent trigger_01 once (no sound)");
        TestAssert.Equal(1, Creates(1, "nidalee_bushwhack_trigger_02"), "at the spring the enemy's seat is sent trigger_02 (it sees the trap's spot)");
        TestAssert.Equal(1, Creates(1, "nidalee_bushwhack_trigger_01"), "at the spring the enemy's seat is sent trigger_01 too");
        var enemy = match.Captures[1].Packets;
        var reveal = enemy.FindIndex(packet => CarriesNetId(packet, trap.NetId));
        var sound = enemy.FindIndex(packet => Opcode(packet) == FxPackets.CreateGroupOpcode && packet.Plaintext.Length >= 33 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == SpellPackets.BuffNameHash("nidalee_bushwhack_trigger_02"));
        TestAssert.True(reveal >= 0 && reveal < sound, "the enemy is sent the trap itself before the create that names it");
        TestAssert.Equal(1, enemy.Count(packet => Opcode(packet) == 0xA6 && BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == trap.NetId),
            "the enemy is sent the trap's death at the spring");
        var debuffSlot = foe.Buffs.Single(buff => buff.Name == "BushwhackDebuff").Slot;
        for (var seat = 0; seat < 2; seat++)
            TestAssert.Equal(1, WireBuffCount(match, seat, SpellPackets.BuffAddOpcode, foe, debuffSlot, "BushwhackDebuff", 9),
                $"seat {seat}: one add of BushwhackDebuff on the enemy at slot {debuffSlot}");
        TestAssert.Equal(1, WireBuffCount(match, 0, 0x7F, trap, trapSlot, "Bushwhack", 6),
            "the trapper's seat is sent the removal of the trap's Bushwhack buff once");
    }

    // A plaintext packet that carries the unit's net id as a field (a spawn, a visibility, a buff, a death or an effect's target or bind).
    private static bool CarriesNetId(PlaintextPacket packet, uint netId)
    {
        Span<byte> pattern = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(pattern, netId);
        return packet.Plaintext.Span.IndexOf(pattern) >= 0;
    }

    // Batch 729: the trap's trigger sound is a failing troy string, so the stock client plays nothing for it (retail 1.0.0.126 too: the same data
    // and EXE). The troy names LoL_Audio/Spells/Bushwhack/hit; the bank's event is /Spells-Champions/bushwhack/hit. No group named Spells exists in
    // LoL_Audio.fev, so every LoL_Audio/Spells/... name misses: the owner's own client logs show the same miss for Nidalee's Swipe and Ezreal's loop.
    [Check("spells-live-nidalee-trap-trigger-sound-names-an-event-the-bank-lacks")]
    public static void BushwhackTriggerSoundNamesAnEventTheBankLacks()
    {
        var troy = InibinReader.Load(GameData.Default.Paths.Data("Particles/nidalee_bushwhack_trigger_02.troybin"), raw: true);
        TestAssert.True(troy.TryGetValue(InibinReader.KeyHash("System*SoundOnCreate"), out var sound) &&
            sound.AsString() == "LoL_Audio/Spells/Bushwhack/hit", "the trigger troy's OnCreate sound is LoL_Audio/Spells/Bushwhack/hit");
        var bank = File.ReadAllBytes(GameData.Default.Paths.Data("Sounds/FMOD/LoL_Audio.fev"));
        TestAssert.True(bank.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes("/Spells-Champions/bushwhack/hit")) >= 0,
            "the bank holds the trap's event under Spells-Champions");
        TestAssert.True(bank.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes("/Spells/")) < 0,
            "the bank has no group named Spells, so the troy's path cannot resolve");
    }
}
