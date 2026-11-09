using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;
using Amberline.Simulation.Buffs;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    // Batch 737 (owner report, 9 November 2026: "Kogmaw health bar can be missing ( passive bug ? )": an enemy Kog'Maw fought under a turret a
    // minute after his Icathian Surprise with his name plate and no health bar). The zombie lives hide their holder's bar while they last and
    // show it again when they end: KogMawIcathianSurprise.luaobj (OnBuffActivate BBShowHealthBar Show=false; OnBuffDeactivate BBForceDead, then
    // BBShowHealthBar Show=true) and Karthus' DeathDefiedBuff.luaobj (the show before its BBForceDead). Each is ShowHealthBar (0xD7):
    // the client stores show == false in the unit's hide flag and draws no champion bar while it is set; nothing else clears it (the AntiBot
    // build's the client, the stock client's champion bar draw, the client). The production
    // composition (the nested Match): the hero dies (CombatService.Kill, the zombie life from his shipped scripts), ends it, respawns on the
    // match's own timer, and the enemy sees him alive again; his own client and an ally see him throughout, the enemy as the case says.
    // What each client keeps is its last 0xD7 for him.

    private enum ZombieSight { Throughout, LostBeforeTheEnd, RevealedThenLost, Never }

    [Check("spells-zombie-life-health-bar-comes-back-for-every-client")]
    public static void ZombieLifeHealthBarComesBackForEveryClient()
    {
        // Every case runs; the failures are reported together.
        var failures = new List<string>();
        foreach (var (champion, zombie) in new[] { ("KogMaw", "KogMawIcathianSurprise"), ("Karthus", "DeathDefiedBuff") })
        foreach (var sight in Enum.GetValues<ZombieSight>())
        {
            try { ZombieLifeBar(champion, zombie, sight); }
            catch (Exception failure) { failures.Add(failure.Message); }
        }
        TestAssert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static void ZombieLifeBar(string champion, string zombie, ZombieSight sight)
    {
        using var match = new Match(champion, Team.Order, ally: true);
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        var enemyView = match.Runtime.Session.Participants[1].View;
        var clients = new[] { ("own", match.Captures[0]), ("enemy", match.Captures[1]), ("ally", match.Captures[2]) };
        var context = $"{champion} ({sight})";
        // Out of the zombie's sight and of every Order turret's reach; the near spot is in his sight and outside his explosion's 400 units.
        var far = new Position2(9000, 9500);
        Position2 Near() => hero.Position + new Position2(600, 0);
        match.Runtime.Tick(1.1); // the character script's passive buff (CharOnActivate, UpdateSelfBuffActions)
        enemy.Position = sight is ZombieSight.Throughout or ZombieSight.LostBeforeTheEnd ? Near() : far;
        match.Runtime.Tick(0.2);
        match.Clear();

        var timeline = new List<string>();
        var start = match.Runtime.World.Clock.Now();
        var counts = clients.Select(_ => 0).ToArray();
        void Record(string moment)
        {
            var now = match.Runtime.World.Clock.Now() - start;
            for (var index = 0; index < clients.Length; index++)
            {
                var (who, capture) = clients[index];
                foreach (var packet in capture.Packets.Skip(counts[index]))
                    if (ZombieBarLine(packet, hero.NetId) is { } line) timeline.Add($"{now,6:F2} {who,-5} {line}");
                counts[index] = capture.Packets.Count;
            }
            if (moment.Length > 0)
                timeline.Add($"{now,6:F2} server {moment}: alive={hero.Alive} zombie={hero.IsZombie} hidden={hero.HealthBarHidden} " +
                    $"word=0x{CharacterState.ActionWord(hero):X} enemy sees={enemyView.Visible.Contains(hero.NetId)}");
        }

        TestAssert.True(match.Runtime.Combat.Kill(hero, enemy.NetId), $"{context}: the death");
        Record("death");
        TestAssert.True(hero.Alive && hero.Buffs.Any(buff => buff.Name == zombie), $"{context}: the death starts the zombie life ({zombie})");
        var moves = sight switch
        {
            ZombieSight.LostBeforeTheEnd => new[] { (2.0, far) },
            ZombieSight.RevealedThenLost => new[] { (1.5, Near()), (3.0, far) },
            _ => Array.Empty<(double, Position2)>(),
        };
        var elapsed = 0.0;
        while (hero.Alive && elapsed < 20.0)
        {
            foreach (var (at, to) in moves)
                if (elapsed < at && elapsed + 0.1 >= at) enemy.Position = to;
            match.Runtime.Tick(0.1);
            elapsed += 0.1;
            Record("");
        }
        Record("the zombie life ended");
        TestAssert.False(hero.Alive, $"{context}: the zombie life ends in a death");
        TestAssert.False(hero.HealthBarHidden, $"{context}: the server shows the bar again at the end");
        while (!hero.Alive && elapsed < 120.0)
        {
            match.Runtime.Tick(0.5);
            elapsed += 0.5;
            Record("");
        }
        TestAssert.True(hero.Alive, $"{context}: the respawn");
        Record("respawn");
        // He fights in the open: the enemy sees him alive, then a minute passes.
        hero.Position = new Position2(7000, 7000);
        enemy.Position = new Position2(7300, 7000);
        match.Runtime.Tick(1.0);
        Record("seen alive");
        TestAssert.True(enemyView.Visible.Contains(hero.NetId), $"{context}: the enemy sees him after the respawn");
        for (var second = 0; second < 60; second++) match.Runtime.Tick(1.0);
        Record("a minute after the respawn");
        Console.WriteLine($"B737 {context}:{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", timeline));

        foreach (var (who, capture) in clients)
        {
            var bars = capture.Packets.Where(packet => packet.Plaintext.Length == 6 && packet.Plaintext.Span[0] == 0xD7 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == hero.NetId).Select(packet => packet.Plaintext.Span[5] != 0).ToList();
            var story = $"{context}, {who} client, 0xD7 shows [{string.Join(",", bars)}]";
            TestAssert.True(bars.Count == 0 || bars[^1], $"{story}: the bar is shown after the respawn (the last 0xD7 hid it)");
            // PolicyBroadcast: every client that holds the hero is told the hide at the death and the show at the end, in sight or not (a
            // reveal in between repeats the hide).
            TestAssert.True(bars.Count >= 2 && !bars[0] && bars[^1], $"{story}: hidden at the death, shown at the end");
        }
    }

    // A packet about the unit worth a line in the record: the bar, the buffs, the death, the visibility (null for the rest).
    private static string? ZombieBarLine(PlaintextPacket packet, uint unit)
    {
        var bytes = packet.Plaintext.Span;
        if (bytes.Length < 5 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]) != unit) return null;
        var name = WirePacketDecoder.LogName(bytes[0], packet.Channel);
        if (bytes[0] == 0xD7) return $"0xD7 ShowHealthBar show={bytes[5]}";
        if (bytes[0] == 0x1E) return "0x1E ForceDead";
        if (SpellPackets.TryReadBuffAdd(bytes, out var add))
            return $"0x{bytes[0]:X2} {name} {ZombieBuffName(add.BuffNameHash)} {add.Duration:F2} s";
        if (bytes[0] == 0x7F && bytes.Length >= 10)
            return $"0x7F {name} {ZombieBuffName(BinaryPrimitives.ReadUInt32LittleEndian(bytes[6..]))}";
        return name.Contains("Die", StringComparison.Ordinal) || name.Contains("Death", StringComparison.Ordinal) ||
               name.Contains("Visibility", StringComparison.Ordinal) || name.Contains("Respawn", StringComparison.Ordinal) ||
               name.Contains("Targetable", StringComparison.Ordinal) || name.Contains("Reincarnat", StringComparison.Ordinal)
            ? $"0x{bytes[0]:X2} {name}" : null;
    }

    private static readonly string[] ZombieBuffNames =
    [
        "KogMawIcathianSurprise", "KogMawIcathianSurpriseReady", "KogMawIcathianSurpriseSound", "Untargetable", "UnlockAnimation",
        "DeathDefied", "DeathDefiedBuff",
    ];

    private static string ZombieBuffName(uint hash) =>
        ZombieBuffNames.FirstOrDefault(name => SpellPackets.BuffNameHash(name) == hash) ?? $"0x{hash:X8}";
}
