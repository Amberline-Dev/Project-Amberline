using System.Buffers.Binary;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 897 (18 November 2026, owner decision "Fix in its own batch"; batch 892's open item): <c>CharScriptShyvana.UpdateSelfBuffActions</c>
/// calls <c>BBSetPARColorOverride</c> on every run, and below 100 Fury <c>BBClearPARColorOverride</c> after it; each call is one
/// ChangePARColorOverride (0x9A on 132; one packet per call is the native's, batch 241). The hook also owns the +1 Fury
/// <c>BBExecutePeriodically</c> (1.5 s), and before this batch a self hook that owned a periodic ran on every 33 ms step: 30 packets a
/// second at full Fury and 60 below, to every client in view, all game. The character script now runs on the hero's 0.25 s self-update
/// line like every other self poll (<c>BbSelfUpdate</c>): 4 a second at full Fury and 8 below; the 1.5 s Fury tick lands on that line.
/// </summary>
public static class BbB897ShyvanaFuryChecks
{
    [Check("scripting.p132-shyvana-fury-bar-colour-is-sent-on-the-quarter-second-self-update-line")]
    public static void FuryBarColourFollowsTheSelfUpdateLine()
    {
        using var match = new Match("Shyvana");
        var hero = match.Hero;
        match.Learn(1); // the +1 Fury tick needs a rank in R (GetSlotSpellLevel 3 >= 1)
        var observer = Observe(match, Amberline.Core.Team.Order, match.Ally);
        int Colours()
        {
            match.Runtime.Delivery.Flush();
            var count = observer.Packets.Count(row => row.Packet.Plaintext.Span is var bytes && bytes.Length >= 18 && bytes[0] == 0x9A &&
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..]) == hero.NetId);
            observer.Packets.Clear();
            return count;
        }
        void Steps(int count) { for (var step = 0; step < count; step++) match.Runtime.Tick(Step); }

        // Below 100 Fury: a set and its clear per run.
        hero.Mana = 50.0;
        Steps(15);
        Colours();
        Steps(60);
        var below = Colours();
        Console.WriteLine($"shyvana: {below} 0x9A in 2.0 s at {hero.Mana:0} Fury");
        TestAssert.Equal(16, below, "below 100 Fury: a set and a clear per 0.25 s run, 16 in 2.0 s (was 120 on the step cadence)");
        TestAssert.True(hero.ParColorOverride is null, "below 100 Fury the clear is the last word");

        // The +1 Fury tick (1.5 s) still lands: twice in 3.0 s whatever the phase.
        var before = hero.Mana;
        Steps(90);
        Colours();
        TestAssert.Equal(2.0, hero.Mana - before, $"+1 Fury per 1.5 s out of dragon form ({before} -> {hero.Mana})");

        // At 100 Fury: the set alone.
        hero.Mana = hero.MaxMana;
        Steps(15);
        Colours();
        Steps(60);
        var full = Colours();
        Console.WriteLine($"shyvana: {full} 0x9A in 2.0 s at {hero.Mana:0} Fury");
        TestAssert.Equal(8, full, "at 100 Fury: one set per 0.25 s run, 8 in 2.0 s (was 60 on the step cadence)");
        TestAssert.True(hero.ParColorOverride is not null, "at 100 Fury the bar keeps its red override");
        TestAssert.Equal("", string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)), "no script failure");
    }
}
