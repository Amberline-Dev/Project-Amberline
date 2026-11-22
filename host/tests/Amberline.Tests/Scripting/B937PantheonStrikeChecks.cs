using Amberline.Core;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 937 (players, 22 November 2026: "The 2nd strike of Pantheon E occurs at the same time as the 3rd, but should be halfway through the
/// channel"). Heartseeker Strike is the same script on both lines: <c>Pantheon_Heartseeker</c>'s <c>ChannelingStart</c> adds the buff
/// <c>Pantheon_Heartseeker</c> (<c>Duration 0.75</c>, <c>TickRate 0.25</c>); its <c>OnBuffActivate</c> casts the first strike
/// (<c>Pantheon_HeartseekerChannel</c>, extra slot 0) and sets <c>ticksRemaining</c> 2, its <c>BuffOnUpdateActions</c> casts one more on each
/// <c>BBExecutePeriodically</c> of 0.25 s (not immediate) while one remains, and its <c>OnBuffDeactivate</c> casts a remaining one when the buff
/// ran out (<c>Expired</c>).
/// </summary>
public static class B937PantheonStrikeChecks
{
    /// <summary>
    /// The three strikes land a quarter second apart from the channel's start: the first at once, the second 0.25 s later, the third 0.5 s
    /// after the first (each within one 1/30 s step), and each strike's cast reaches the client in its own step. Before batch 937 the second
    /// and the third landed in the same step at the channel's end.
    /// </summary>
    [Check("scripting.b937-pantheon-heartseeker-strikes-a-quarter-second-apart")]
    public static void HeartseekerStrikesAQuarterSecondApart()
    {
        using var match = new Match("Pantheon", foeChampion: "Ezreal");
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7150, 7000);
        match.Ally.Position = new Position2(6500, 6500);
        match.FarFoe.Position = new Position2(12000, 12000);
        match.Foe.MaxHealth = match.Foe.Health = 50000.0;
        match.Refill();
        var own = Observe(match, Team.Order, match.Hero);
        match.Advance(0.5);
        var pressed = match.Now;
        TestAssert.True(match.Cast(2, at: new Position2(7200, 7000)), "the strike is accepted");
        double? channelAt = null;
        for (var elapsed = 0.0; elapsed < 2.0; elapsed += 1.0 / 30.0)
        {
            match.Advance(1.0 / 30.0);
            if (channelAt is null && Buff(match.Hero, "Pantheon_Heartseeker") is not null) channelAt = match.Now;
        }
        TestAssert.True(channelAt is not null, "the channel starts");
        var hits = HitsOn(match, "Foe").Where(hit => hit.Kind is not ("Basic" or "none")).Select(hit => hit.At).ToArray();
        var hash = SpellPackets.BuffNameHash("Pantheon_HeartseekerChannel");
        var casts = own.Packets.Where(entry => entry.At > pressed && MissileOf(entry.Packet) is { } m && m.Spell == hash)
            .Select(entry => entry.At).Distinct().ToArray();
        string Times(IEnumerable<double> times) => string.Join(",", times.Select(at => (at - channelAt!.Value).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)));
        var report = $"hits at {Times(hits)}, strike casts at {Times(casts)} (seconds from the channel's start)";
        TestAssert.Equal(3, hits.Length, "three strikes: " + report);
        var step = 1.0 / 30.0 + 1e-6;
        TestAssert.True(Math.Abs(hits[1] - hits[0] - 0.25) <= step, "the second strike lands 0.25 s after the first: " + report);
        TestAssert.True(Math.Abs(hits[2] - hits[0] - 0.5) <= step, "the third strike lands 0.5 s after the first: " + report);
        TestAssert.Equal(3, casts.Length, "each strike's cast reaches the caster's client in its own step: " + report);
        Console.WriteLine("b937 Heartseeker: " + report);
    }

    /// <summary>The foe's health drops after one landed swing of a hero who holds <paramref name="window"/> (a live script instance): time, amount.</summary>
    private static List<(double At, double Amount)> PulsesAfterOneSwing(string window, double seconds)
    {
        using var match = new Match("Annie", foeChampion: "Ezreal");
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7100, 7000);
        match.Foe.MaxHealth = match.Foe.Health = 50000.0;
        match.Foe.Armor = match.Foe.SpellBlock = 0.0;
        match.Advance(0.5);
        TestAssert.True(ScriptBuff(match.Runtime, match.Hero, window, 25.0) is not null, $"the hero holds {window}");
        TestAssert.True(match.Runtime.Combat.ApplyHit(match.Hero.NetId, match.Foe.NetId, 1.0, DamageType.Physical, DamageSourceKind.Basic),
            "the swing lands");
        var hit = match.Now;
        match.Advance(seconds);
        return HitsOn(match, "Foe").Where(entry => entry.Kind is not ("Basic" or "none")).Select(entry => (entry.At - hit, entry.Dealt)).ToList();
    }

    private static string Show(IEnumerable<(double At, double Amount)> pulses) => string.Join(", ", pulses.Select(pulse =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{pulse.Amount:0.##} at {pulse.At:0.000}")));

    /// <summary>
    /// The other shipped TickRate adds whose update hook is a periodic (batch 937's list): their first update seeds the periodic, so the pulses
    /// come one period after the add. Entropy (item 3184) is pinned by its shipped tooltip, "80 True Damage over 2.5 seconds": EntropyBurning
    /// (TickRate 1, Duration 2.5, 40 a pulse, not immediate) pulses at 1 and 2 s. Before batch 937 its first update came one TickRate late and
    /// it pulsed once (40). The red buff's Burning (TickRate 1, Duration 3) is reported, not pinned: no shipped text gives its total.
    /// </summary>
    [Check("scripting.b937-tickrate-periodic-buffs-pulse-one-period-after-the-add")]
    public static void TickRatePeriodicBuffsPulseOnePeriodAfterTheAdd()
    {
        var entropy = PulsesAfterOneSwing("OdinEntropicClaymore", 3.5);
        TestAssert.Equal(2, entropy.Count, "Entropy pulses twice: " + Show(entropy));
        TestAssert.Near(80.0, entropy.Sum(pulse => pulse.Amount), 1e-6, "80 true damage over 2.5 seconds: " + Show(entropy));
        TestAssert.True(Math.Abs(entropy[0].At - 1.0) <= Step + 1e-6 && Math.Abs(entropy[1].At - 2.0) <= Step + 1e-6,
            "at 1 and 2 s after the hit: " + Show(entropy));
        var red = PulsesAfterOneSwing("BlessingoftheLizardElder", 4.5);
        TestAssert.True(red.Count >= 1, "the red buff's burn pulses: " + Show(red));
        Console.WriteLine("b937 Entropy: " + Show(entropy) + " | red buff Burning: " + Show(red));
    }

    /// <summary>
    /// Cassiopeia's Miasma (batch 937's list): the cloud keeps CassiopeiaMiasmaPoison (TickRate 1, an immediate 1 s periodic) on the foe standing in
    /// it, and the poison ticks once a second, the first at its first update. A re-add that made a new instance would tick on every re-add.
    /// </summary>
    [Check("scripting.b937-miasma-poison-ticks-once-a-second-from-its-add")]
    public static void MiasmaPoisonTicksOnceASecond()
    {
        using var match = new Match("Cassiopeia", foeChampion: "Ezreal");
        match.Learn(1);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7250, 7000);
        match.Foe.MaxHealth = match.Foe.Health = 50000.0;
        match.Refill();
        match.Advance(0.5);
        TestAssert.True(match.Cast(1, at: match.Foe.Position), "Miasma is cast on the foe");
        double? poisoned = null;
        for (var elapsed = 0.0; elapsed < 4.0; elapsed += Step)
        {
            match.Advance(Step);
            if (poisoned is null && Buff(match.Foe, "CassiopeiaMiasmaPoison") is not null) poisoned = match.Now;
        }
        TestAssert.True(poisoned is not null, "the foe in the cloud is poisoned");
        var ticks = HitsOn(match, "Foe").Where(hit => hit.Kind is not ("Basic" or "none")).Select(hit => hit.At - poisoned!.Value).ToArray();
        var shown = string.Join(",", ticks.Select(at => at.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)));
        TestAssert.True(ticks.Length >= 3, $"the poison ticks while the foe stays in the cloud ({shown} s from the poison's add)");
        TestAssert.True(Math.Abs(ticks[0]) <= Step + 1e-6, $"the first tick is at the poison's add ({shown})");
        for (var index = 1; index < ticks.Length; index++)
            TestAssert.True(Math.Abs(ticks[index] - ticks[index - 1] - 1.0) <= Step + 1e-6, $"one tick a second ({shown})");
        Console.WriteLine("b937 Miasma: ticks at " + shown);
    }

    /// <summary>A rank-one cast of <paramref name="slot"/> with the foe 400 in front; the hits on the foe from the moment <paramref name="buff"/> appears.</summary>
    private static List<(double At, double Raw)> HitsFromBuff(string champion, int slot, string buff, double seconds, Func<Match, bool> cast)
    {
        using var match = new Match(champion, foeChampion: "Ezreal");
        match.Learn(1);
        if (slot == 3) match.Hero.Level = 6;
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7400, 7000);
        match.Foe.MaxHealth = match.Foe.Health = 50000.0;
        match.Refill();
        match.Advance(0.5);
        TestAssert.True(cast(match), $"{champion}: the cast is accepted");
        double? added = null;
        for (var elapsed = 0.0; elapsed < seconds; elapsed += Step)
        {
            match.Advance(Step);
            if (added is null && (Buff(match.Hero, buff) ?? Buff(match.Foe, buff)) is not null) added = match.Now;
        }
        TestAssert.True(added is not null, $"{champion}: {buff} is added");
        return HitsOn(match, "Foe").Where(hit => hit.Kind is not ("Basic" or "none")).Select(hit => (hit.At - added!.Value, hit.Raw)).ToList();
    }

    private static string Shown(IEnumerable<(double At, double Raw)> hits) => string.Join(", ", hits.Select(hit =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{hit.Raw:0.#}@{hit.At:0.000}")));

    /// <summary>
    /// Batch 937, part 2 (owner, 22 November 2026, "make them consistent"; not applied, see the record): a TickRate hook with no periodic of its
    /// own keeps its first run one TickRate after the add, as the shipped numbers of three such buffs need. Death Lotus (DeathLotus, TickRate
    /// 0.25, the 2.65 s channel) throws 10 daggers (the wiki's V1.0.0.75 "Daggers increased to 10 from 9", the tooltip's "10 daggers"); a
    /// first run at the add throws 11. Explosive Shot (ExplosiveShotDebuff, TickRate 1, 5 s, dotdmg 22 at rank one) deals its Effect3 110
    /// over 5 seconds as five ticks at 1 to 5 s; at the add it deals six (132). Time Bomb (TickRate 1, 4 s, TickDamage 3 counting down) shows
    /// 3, 2, 1 at 1, 2 and 3 s and blasts at 4 s; at the add the count runs 0, 1, 2 and the last second is silent.
    /// </summary>
    [Check("scripting.b937-bare-tickrate-hooks-first-run-one-period-after-the-add")]
    public static void BareTickRateHooksFirstRunOnePeriodAfterTheAdd()
    {
        var daggers = HitsFromBuff("Katarina", 3, "DeathLotus", 3.5, match => match.Runtime.Spells.HandleCastRequest(match.Hero,
            new SpellCastRequest(3, false, new Position3(7000, 0, 7000), new Position3(7000, 0, 7000), match.Hero.NetId),
            match.Now));
        TestAssert.Equal(10, daggers.Count, "Death Lotus throws 10 daggers at the foe: " + Shown(daggers));
        var shot = HitsFromBuff("Tristana", 2, "ExplosiveShotDebuff", 6.5, match => match.Cast(2, match.Foe));
        TestAssert.Equal(5, shot.Count, "Explosive Shot ticks five times: " + Shown(shot));
        TestAssert.Near(110.0, shot.Sum(hit => hit.Raw), 1e-6, "Effect3 110 over 5 seconds: " + Shown(shot));
        for (var tick = 0; tick < 5; tick++)
            TestAssert.True(Math.Abs(shot[tick].At - (tick + 1)) <= Step + 1e-6, $"tick {tick + 1} at {tick + 1} s: " + Shown(shot));
        var bomb = HitsFromBuff("Zilean", 0, "TimeBomb", 5.0, match => match.Cast(0, match.Foe));
        TestAssert.Equal(4, bomb.Count, "Time Bomb counts three and blasts: " + Shown(bomb));
        for (var count = 0; count < 3; count++)
        {
            TestAssert.Near(3 - count, bomb[count].Raw, 1e-6, $"the count shows {3 - count}: " + Shown(bomb));
            TestAssert.True(Math.Abs(bomb[count].At - (count + 1)) <= Step + 1e-6, $"{3 - count} at {count + 1} s: " + Shown(bomb));
        }
        TestAssert.True(Math.Abs(bomb[3].At - 4.0) <= Step + 1e-6, "the blast at 4 s: " + Shown(bomb));
        Console.WriteLine($"b937 bare: Death Lotus {Shown(daggers)} | Explosive Shot {Shown(shot)} | Time Bomb {Shown(bomb)}");
    }
}
