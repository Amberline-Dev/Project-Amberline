using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("spells-area-effects-respect-ward-eligibility")]
    public static void AreaEffectsRespectWardEligibility()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            foreach (var champion in new[] { "Janna", "Orianna", "Annie", "Blitzcrank" })
            {
                var random = new AreaWardRandom();
                using var match = new Match(champion, team, random: random);
                var runtime = match.Runtime;
                var hero = match.Hero(0);
                hero.AbilityPower = 0;
                var wards = new List<Entity>();
                // Batch 279: the wards' point is kept, since a windup cast's stop seats the hero on its anchor and moves it.
                var wardPoint = hero.Position + new Position2(150, 0);
                foreach (var seat in new[] { 0, 1 })
                foreach (var item in new[] { 2044, 2043, 3154 })
                    wards.Add(PlaceAreaWard(match, seat, item, wardPoint));
                var controls = new List<Entity>();
                foreach (var controlTeam in new[] { team, match.Hero(1).Team })
                foreach (var kind in new[] { EntityKind.Minion, EntityKind.Neutral,
                             EntityKind.ShacoBox, EntityKind.ShacoClone, EntityKind.YorickRevenant })
                {
                    var type = kind == EntityKind.Minion ? UnitType.Minion : UnitType.Monster;
                    controls.Add(runtime.World.Register(new Entity(runtime.World.Allocate(type), kind,
                        type, controlTeam, hero.Position + new Position2(200, 0))
                    {
                        Name = kind == EntityKind.ShacoBox ? "JackInTheBox" : "Minion_T200Basic",
                        Health = 1000, MaxHealth = 2000, Created = true, Visible = true,
                        Radius = 1, Speed = 325,
                        OwnerNetId = kind is EntityKind.ShacoBox or EntityKind.ShacoClone or EntityKind.YorickRevenant
                            ? (controlTeam == team ? hero.NetId : match.Hero(1).NetId) : 0,
                    }));
                }
                var neutral = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Monster),
                    EntityKind.Neutral, UnitType.Monster, Team.Neutral, hero.Position + new Position2(200, 0))
                    { Name = "YoungLizard", Health = 1000, MaxHealth = 2000, Created = true, Visible = true });
                var enemies = controls.Where(unit => unit.Team != team).Append(neutral).ToArray();
                foreach (var unit in controls.Append(neutral))
                    TestAssert.True(ChampionSpellSupport.IsAreaEffectTarget(unit), "pets and monsters remain eligible");
                runtime.Delivery.Flush(); match.Clear();

                if (champion == "Janna")
                {
                    // The first pulse (the heals and the knockback) comes when the 0.25 s windup ends, the next heal 0.5 s after it.
                    match.Cast(0, 3, hero);
                    Advance(match, .3);
                    runtime.Delivery.Flush();
                    TestAssert.True(match.Captures[0].Packets.Any(packet => Opcode(packet) == 0x67),
                        "eligible enemies still receive Monsoon dash packets");
                    foreach (var enemy in enemies.Where(unit => unit.Team != Team.Neutral))
                        TestAssert.Equal(enemy.Kind != EntityKind.ShacoBox, enemy.ForcedMove is not null,
                            "Monsoon moves ordinary enemies and clones; boxes retain their own immunity");
                    TestAssert.True(neutral.ForcedMove is null, "Monsoon retains its neutral exclusion");
                    TestAssert.Near(1000, neutral.Health, 0);
                    Advance(match, .6);
                    foreach (var ally in controls.Where(unit => unit.Team == team))
                        TestAssert.Near(1070, ally.Health, 1e-6, "initial and periodic Monsoon heals still include allied pets");
                }
                else if (champion == "Orianna")
                {
                    match.Cast(0, 1, hero);
                    SpellStep(match, .5);
                    foreach (var ally in controls.Where(unit => unit.Team == team))
                        TestAssert.True(ally.Buffs.Any(buff => buff.Name == "OrianaHaste"));
                    foreach (var enemy in enemies)
                    {
                        TestAssert.True(enemy.Health < 1000, "Dissonance still damages eligible non-wards");
                        TestAssert.Equal(enemy.Kind != EntityKind.ShacoBox,
                            enemy.Buffs.Any(buff => buff.Name == "OrianaSlow"), "box slow immunity stays separate");
                    }
                }
                else if (champion == "Annie")
                {
                    match.CastDirected(0, 1, hero.Position, hero.Position + new Position2(400, 0));
                    foreach (var capture in match.Captures)
                    {
                        var cast = Decode(capture.Packets.Single(packet => Opcode(packet) == 0xBD));
                        var targets = cast.GetProperty("cast_info").GetProperty("targets").EnumerateArray()
                            .Select(target => target.GetProperty("netid").GetUInt32()).ToArray();
                        TestAssert.False(wards.Any(ward => targets.Contains(ward.NetId)), "W cast targets exclude wards");
                        TestAssert.True(enemies.All(enemy => targets.Contains(enemy.NetId)), "W retains minion, monster and pet targets");
                    }
                    SpellStep(match, .3);
                    foreach (var enemy in enemies) TestAssert.True(enemy.Health < 1000, "W cone still lands");
                    runtime.Delivery.Flush();
                    foreach (var enemy in enemies)
                        TestAssert.True(match.Captures[0].Packets.Any(packet => IsNamedVisual(packet, "Incinerate_buf") &&
                            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[21..]) == enemy.NetId),
                            "W still creates victim FX on ordinary minions, monsters and pets");
                    var beforeBurst = enemies.Select(unit => unit.Health).ToArray();
                    match.CastGround(0, hero.Position + new Position2(200, 0), 3);
                    SpellStep(match, .3);
                    for (var i = 0; i < enemies.Length; i++)
                        TestAssert.True(enemies[i].Health < beforeBurst[i], "R burst still includes pets and neutral monsters");
                    var tibbers = runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.Pet && unit.OwnerNetId == hero.NetId);
                    TestAssert.True(runtime.Pets.Command(hero, 11, tibbers.Position), "hold Tibbers to isolate aura damage");
                    var beforeAura = enemies.Select(unit => unit.Health).ToArray();
                    // The aura is InfernalGuardianBurning's one-second BBExecutePeriodically (batch 86), not TibbersAiSystem.PulseAura: the first tick
                    // comes one second after the landing, so the clock has to run past it.
                    Advance(match, 1.2);
                    for (var i = 0; i < enemies.Length; i++)
                        TestAssert.True(enemies[i].Health < beforeAura[i], "Tibbers aura still includes eligible units");
                }
                else
                {
                    // Static Field's passive is a script buff (batch 100): its immediate strike runs on the buff clock, not on a bare spell tick.
                    Advance(match, .1);
                    TestAssert.Equal(enemies.Length + 1, random.LastCount,
                        $"passive selection includes enemy hero, minions, monsters and pets, but no wards (draw over {random.LastCount})");
                    TestAssert.True(neutral.Health < 1000, "the selected ordinary target receives the strike");
                    var before = enemies.Select(unit => unit.Health).ToArray();
                    match.Cast(0, 3, hero);
                    SpellStep(match, .3);
                    for (var i = 0; i < enemies.Length; i++)
                        TestAssert.True(enemies[i].Health < before[i], "active R retains eligible targets");
                }

                runtime.Delivery.Flush();
                if (champion != "Janna")
                    foreach (var ally in controls.Where(unit => unit.Team == team))
                        TestAssert.Near(1000, ally.Health, 0, "area damage retains its team filter");
                foreach (var ward in wards)
                {
                    TestAssert.Near(2, ward.Health, 0, $"{champion}: injured wards receive neither healing nor spell damage");
                    TestAssert.True(ward.ForcedMove is null);
                    TestAssert.Equal(wardPoint, ward.Position);
                    TestAssert.False(ward.Buffs.Any(buff => buff.Name is "MoveAway" or "OrianaSlow" or "OrianaHaste" or "Silence"));
                    foreach (var capture in match.Captures)
                    {
                        TestAssert.False(capture.Packets.Any(packet => Opcode(packet) == 0x67 &&
                            Decode(packet).GetProperty("entries").EnumerateArray().Any(entry =>
                                entry.GetProperty("unit_netid").GetUInt32() == ward.NetId)), "no ward dash");
                        TestAssert.False(capture.Packets.Any(packet =>
                            (IsNamedVisual(packet, "Incinerate_buf") || IsNamedVisual(packet, "StaticField_tar") ||
                             IsNamedVisual(packet, "StaticField_hit")) &&
                            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[21..]) == ward.NetId), "no ward victim FX");
                    }
                }
                if (champion == "Janna") AssertWardBuffAdmission(match, wards);
            }

            foreach (var item in new[] { 2044, 2043, 3154 })
            {
                using var match = new Match("LeeSin", team);
                var lee = match.Hero(0);
                var ward = PlaceAreaWard(match, 0, item, lee.Position + new Position2(300, 0));
                match.Cast(0, 1, ward);
                SpellStep(match, .3);
                TestAssert.True(lee.ForcedMove is { Effect: "BlindMonkWOneDash" }, "Safeguard still accepts every allied ward type");
                match.Runtime.Movement.Tick(.3);
                SpellStep(match, 0);
                TestAssert.True(lee.ForcedMove is null && Distance(lee.Position, ward.Position) < 100,
                    "the ward jump completes");
            }
        }
    }

    private static Entity PlaceAreaWard(Match match, int seat, int item, Position2 point)
    {
        var hero = match.Hero(seat);
        hero.Inventory = [new InventorySlot(item), null, null, null, null, null];
        hero.ItemCooldowns[0] = 0;
        var existing = match.Runtime.World.Entities.Keys.ToHashSet();
        match.CastGround(seat, point, 4);
        var ward = match.Runtime.World.Entities.Values.Single(unit => unit.IsWard && !existing.Contains(unit.NetId));
        ward.Health = 2;
        hero.Inventory = [null, null, null, null, null, null];
        return ward;
    }

    private static void AssertWardBuffAdmission(Match match, IEnumerable<Entity> wards)
    {
        var buffs = match.Runtime.Buffs;
        foreach (var ward in wards)
        {
            var friend = match.Hero(0).Team == ward.Team ? match.Hero(0) : match.Hero(1);
            var enemy = match.Hero(0).Team != ward.Team ? match.Hero(0) : match.Hero(1);
            foreach (var type in new[] { BuffType.Fear, BuffType.Net, BuffType.Silence, BuffType.Sleep,
                         BuffType.Slow, BuffType.Snare, BuffType.Stun, BuffType.Taunt, BuffType.Blind,
                         BuffType.Suppression, BuffType.CombatDehancer })
            {
                TestAssert.True(buffs.AddBuff(ward, "WardControlProbe", type, 3, enemy.NetId) is null);
                TestAssert.True(buffs.AddStackingBuff(ward, "WardControlProbe", type, 3,
                    enemy.NetId, 3, new BuffPayload()) is null, "initial stacking admission rejects hostile control");
                var friendly = buffs.AddBuff(ward, "WardControlProbe", type, 2, friend.NetId)
                    ?? throw new InvalidOperationException("the ward veto must be hostile-only");
                TestAssert.True(buffs.AddBuff(ward, "WardControlProbe", type, 9, enemy.NetId) is null,
                    "hostile replacement cannot remove an existing buff");
                foreach (var replacePayload in new[] { false, true })
                    TestAssert.True(buffs.AddStackingBuff(ward, "WardControlProbe", type, 9,
                        enemy.NetId, 3, new BuffPayload { MoveSpeedMult = -.5 }, replacePayload) is null,
                        "hostile stacking and renewal cannot mutate a friendly buff");
                TestAssert.True(ward.Buffs.Contains(friendly));
                TestAssert.Equal(friend.NetId, friendly.CasterNetId);
                TestAssert.Equal(1, friendly.Stacks);
                TestAssert.Near(2, friendly.ExpiresAt, 0);
                TestAssert.Near(0, friendly.Payload.MoveSpeedMult, 0);
                buffs.RemoveBuff(ward, friendly.Slot);
            }
            var dot = buffs.AddBuff(ward, "WardDotProbe", BuffType.DamageOverTime, 1, enemy.NetId);
            TestAssert.True(dot is not null, "the veto is the shipped type list, not every hostile buff");
            buffs.RemoveBuff(ward, dot!.Slot);
        }
    }

    private sealed class AreaWardRandom : IRandomSource
    {
        public int LastCount { get; private set; }
        public double NextDouble() => .99;
        public int NextIndex(int count) { LastCount = count; return count - 1; }
    }
}
