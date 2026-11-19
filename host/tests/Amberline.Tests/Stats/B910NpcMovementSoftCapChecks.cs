using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 910 (owner decision, 19 November 2026): every unit that moves takes the movement soft caps heroes take
/// (<c>HeroStatCalculator.ApplyMovementSoftCaps</c>), from its spawn and on every recompute. Retail footage (qj9gFmeR_yo, 30 November 2011)
/// shows an unslowed Ancient Golem at 200 in the target panel, which reads the replicated mMoveSpeed; its shipped Data*MoveSpeed is 180.
/// A unit with no speed (turrets, wards, buildings) keeps 0. docs/maintenance/2026-11-19-batch-910-ms-softcap-all.md.
/// </summary>
public static class B910NpcMovementSoftCapChecks
{
    [Check("stats.b910-every-moving-unit-takes-the-movement-soft-caps")]
    public static void EveryMovingUnitTakesTheSoftCaps()
    {
        using var match = new Match("Annie");
        match.Runtime.Waves!.AutomaticSpawningEnabled = false;
        var failures = new List<string>();
        void Expect(string what, double expected, double actual)
        {
            if (Math.Abs(expected - actual) > 1e-6) failures.Add($"{what}: {actual:0.###}, expected {expected:0.###}");
        }

        for (var tick = 0; tick < 800 && Camp("AncientGolem") is null; tick++) match.Runtime.Tick(0.25);
        var golem = Camp("AncientGolem") ?? throw new InvalidOperationException("no Ancient Golem spawned");
        var wolf = Camp("GiantWolf") ?? throw new InvalidOperationException("no Giant Wolf spawned");
        Expect("unslowed Ancient Golem (shipped 180, 110 + 180 / 2)", 200.0, golem.Speed);
        Expect("unslowed Giant Wolf (shipped 560, 560 * 0.5 + 230)", 510.0, wolf.Speed);
        var minion = LaneMinion(match, match.Hero.Position + new Position2(400, 0));
        Expect("lane minion (shipped 325, inside 220..415)", 325.0, minion.Speed);

        // The golem's replicated mMoveSpeed (what the target panel shows): the full stats block a client gets when the golem comes into view.
        var wire = Observe(match, Team.Order, match.Hero);
        match.Hero.Position = golem.Position + new Position2(-200, 0);
        match.Hero.Waypoints.Clear();
        match.Advance(0.2);
        // The minion block's movement-speed property (id 0x04) and its float are its last eight bytes (NeutralObservationChecks).
        var sent = wire.Packets.Select(item => item.Packet).Where(packet =>
                packet.Plaintext.Length >= 100 && BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[^8..]) == 0x04u &&
                packet.EntityReferences.Any(reference => reference.NetId == golem.NetId))
            .Select(packet => (double)BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[^4..])).ToList();
        if (sent.Count == 0) failures.Add("the golem's full stats block never reached the client");
        foreach (var speed in sent) Expect("the golem's replicated movement speed", 200.0, speed);

        // On every recompute, from the raw baseline: a 50 percent slow takes the golem's raw 180 to 90, which reads 110 + 45; without it, 200 again.
        var slow = match.Runtime.SpellBuffs.Add(golem, "B910Slow", BuffType.Slow, 10.0, match.Hero.NetId, new BuffPayload { MoveSpeedMult = -0.5 })
                   ?? throw new InvalidOperationException("the slow was not added");
        Expect("Ancient Golem slowed 50 percent", 155.0, golem.Speed);
        match.Runtime.SpellBuffs.Remove(golem, slow.Slot);
        Expect("Ancient Golem after the slow", 200.0, golem.Speed);

        // A full slow (rank-5 Majestic Roar gives a non-hero -1.0) leaves the lower cap's 110, so the unit still walks.
        match.Runtime.SpellBuffs.Add(minion, "B910Roar", BuffType.Slow, 10.0, match.Hero.NetId,
            new BuffPayload { MoveSpeedPercent = -1.0, MoveSpeedMult = -1.0 });
        Expect("lane minion under a 100 percent slow", 110.0, minion.Speed);

        // No speed, no cap: a turret and a ward stay at 0 under the same slow.
        var turret = match.ChaosLaneTurret;
        var turretSpeed = turret.Speed;
        match.Runtime.SpellBuffs.Add(turret, "B910Slow", BuffType.Slow, 10.0, match.Hero.NetId, new BuffPayload { MoveSpeedMult = -0.5 });
        Expect("a slowed turret", turretSpeed, turret.Speed);
        Expect("a turret's speed", 0.0, turret.Speed);
        var ward = match.Runtime.World.Register(new Entity(match.Runtime.World.Allocate(UnitType.Monster), EntityKind.Ward, UnitType.Monster,
            Team.Chaos, match.Hero.Position + new Position2(0, 300))
        {
            Name = "SightWard", IsWard = true, Health = 1.0, MaxHealth = 1.0, Speed = 0.0, AttackRange = 0.0, Created = true, Visible = true,
        });
        match.Runtime.SpellBuffs.Add(ward, "B910Slow", BuffType.Slow, 10.0, match.Hero.NetId, new BuffPayload { MoveSpeedMult = -0.5 });
        Expect("a slowed ward", 0.0, ward.Speed);

        // A snapshot clone reads its source's (capped) speed at birth and takes the cap once more only from the raw speed that came from:
        // 200 is raw 180 (slowed 50 percent: 90, so 155), 300 stays 300 (150, so 185), 450 is raw 458.75 (229.375), 500 is raw 540 (270).
        foreach (var (source, slowed) in new[] { (200.0, 155.0), (300.0, 185.0), (450.0, 229.375), (500.0, 270.0) })
        {
            match.Hero.Speed = source;
            var clone = match.Runtime.Clones.Spawn(match.Hero, match.Hero, match.Hero.Position + new Position2(150, 0),
                Amberline.Simulation.Pets.CloneKind.ShacoClone, 500.0, "Hallucinate", 30.0, "Hallucinate");
            Expect($"a clone of a {source} source at birth", source, clone.Speed);
            match.Runtime.SpellBuffs.Add(clone, "B910Slow", BuffType.Slow, 10.0, match.Hero.NetId, new BuffPayload { MoveSpeedMult = -0.5 });
            Expect($"a clone of a {source} source slowed 50 percent", slowed, clone.Speed);
        }

        // Units that never walk keep their record's speed: the Orianna ball (a placemarker, 475) and Teemo's mushroom (Idle.luaobj; no
        // Data*MoveSpeed, so the loader's 100). The foe Orianna's Q puts her ball on the ground (the unit lives there; on her it is not one).
        var orianna = match.Foe;
        orianna.SpellRanks[0] = 1;
        orianna.SpellCooldowns[0] = 0.0;
        orianna.Mana = orianna.MaxMana;
        if (!match.Cast(0, caster: orianna, at: orianna.Position + new Position2(0, 600))) failures.Add("Orianna's Q was not cast");
        Entity? ball = null;
        for (var tick = 0; tick < 90 && (ball = match.Runtime.World.Entities.Values.FirstOrDefault(unit =>
                 unit.Alive && unit.ChampionName == "OriannaBall")) is null; tick++) match.Runtime.Tick(1.0 / 30.0);
        if (ball is null) failures.Add("Orianna's Q left no ball");
        else Expect("the Orianna ball (a placemarker)", 475.0, ball.Speed);
        using (var teemo = new Match("Teemo"))
        {
            var experience = ProgressionRules.ExperienceThreshold(6) - teemo.Hero.Experience;
            if (experience > 0.0) teemo.Runtime.Combat.AwardHeroProgress(teemo.Hero, experience: experience);
            teemo.Hero.SpellRanks[3] = 0;
            if (!teemo.Runtime.Spells.RankUp(teemo.Hero, 3)) failures.Add("Teemo's R was not trained");
            teemo.Module.Buffs!.Add(new BbBuffRequest(teemo.Hero, teemo.Hero, "TeemoMushrooms", (int)BbBuffAddType.BUFF_STACKS_AND_RENEWS,
                (int)BbBuffType.BUFF_Aura, 3, 25000.0, 0.0, false, new BbTable(), 1, true));
            teemo.Refill();
            if (!teemo.Cast(3, at: teemo.Hero.Position + new Position2(150, 0))) failures.Add("Teemo's R was not cast");
            teemo.Advance(0.6);
            var trap = teemo.Runtime.World.Entities.Values.FirstOrDefault(unit => unit.Alive && unit.Name == "TeemoMushroom");
            if (trap is null) failures.Add("no Teemo mushroom stands");
            else Expect("Teemo's mushroom (Idle.luaobj)", 100.0, trap.Speed);
        }

        TestAssert.True(failures.Count == 0, string.Join(" | ", failures));

        Entity? Camp(string name) => match.Runtime.World.Entities.Values.FirstOrDefault(unit =>
            unit.Kind == EntityKind.Neutral && unit.Alive && unit.Name == name);
    }
}
