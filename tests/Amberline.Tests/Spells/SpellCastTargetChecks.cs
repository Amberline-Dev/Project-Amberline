using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

/// <summary>
/// Cast target lists and the reveals that follow them (SpellCastTargets.cs). The stock client reads a
/// cast's target list in the reveal routine (first resolvable unit),
/// the spell manager's cast step and the cast-and-serve routine (one missile per listed unit
/// for CastType 1). The expected radii come from the shipped inibins and scripts (docs/champions/
/// spell-geometry.md), the 4.5 s timer from Map1 Constants.var, and the flags from the spell inibins,
/// not from the code under test.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    private const byte CastSpellAnswerOpcode = 0xBD;

    // 0xBD: opcode, caster id, sync id, then the cast blob: length(2), hash(4), spell net id(4), rank(1),
    // attack speed(4), caster(4), missile(4), two positions(24), target count(1), 17-byte entries.
    private static IReadOnlyList<IReadOnlyList<uint>> CastTargetLists(Capture capture, string spellName)
    {
        var hash = SpellSystem.ProtocolHash(spellName);
        var lists = new List<IReadOnlyList<uint>>();
        foreach (var packet in capture.Packets)
        {
            var bytes = packet.Plaintext.Span;
            if (bytes.Length < 57 || bytes[0] != CastSpellAnswerOpcode ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[11..]) != hash) continue;
            var ids = new uint[bytes[56]];
            for (var i = 0; i < ids.Length; i++)
                ids[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(57 + 17 * i)..]);
            lists.Add(ids);
        }
        return lists;
    }

    [Check("spells-live-annie-w-lists-its-cone-units-and-reveals-to-the-first")]
    public static void AnnieWListsTheUnitsInItsConeInWorldOrder()
    {
        var data = GameData.Default;
        // Incinerate.inibin: TargettingType 3 (cone), CastConeDistance 625, CastConeAngle 24.76.
        TestAssert.Equal(3, (int)data.Spell("Incinerate").Raw[InibinReader.KeyHash("SpellData*TargettingType")].AsDouble(),
            "Incinerate is a cone");
        foreach (var (name, heroInCone, monsterInCone, expectChaos, expectNeutral) in new[]
                 {
                     ("hero only", true, false, true, false),
                     ("monster only", false, true, false, true),
                     // The monster registers after every hero, so the hero is the first listed unit.
                     ("hero before monster", true, true, true, false),
                     ("nothing in the cone", false, false, false, false),
                 })
        {
            using var match = new Match("Annie", Team.Order, extraEnemy: true,
                enemyChampion: "Yorick", extraEnemyChampion: "Vayne");
            var annie = match.Hero(0); var inCone = match.Hero(1); var outside = match.Hero(2);
            // 24.76 degrees at 300 units is 138 units of lateral room: 250 is outside, 100 inside.
            inCone.Position = heroInCone ? annie.Position + new Position2(300, 100) : annie.Position + new Position2(300, 450);
            outside.Position = annie.Position + new Position2(300, 250);
            var expected = new List<uint>();
            if (heroInCone) expected.Add(inCone.NetId);
            if (monsterInCone)
                expected.Add(AddMonster(match, annie.Position + new Position2(500, 20)).NetId);
            match.Clear();

            // Batch 324: an aim 625 away is beyond the client's cone test (L = 521.3, Cone.Fire) and walks first; 500 fires where she stands, and the
            // cone (CastConeDistance 625 from Annie along the aim) is the same.
            match.CastDirected(0, 1, annie.Position, annie.Position + new Position2(500, 0));
            TestAssert.Near(expectChaos ? RevealSeconds : 0.0, annie.AttackRevealRemaining[(int)Team.Chaos], 1e-9,
                $"{name}: the enemy team sees Annie only through the first listed unit");
            TestAssert.Near(expectNeutral ? RevealSeconds : 0.0, annie.AttackRevealRemaining[(int)Team.Neutral], 1e-9,
                $"{name}: the camps see Annie only when they are the first listed unit");
            var wire = CastTargetLists(match.Captures[1], "Incinerate");
            TestAssert.Equal(1, wire.Count, $"{name}: one Incinerate acknowledgement reaches the enemy seat");
            TestAssert.Equal(string.Join(',', expected), string.Join(',', wire[0]),
                $"{name}: the acknowledgement lists the units inside the cone, in world order, and not the hero outside");
        }
    }

    [Check("spells-live-area-cast-lists-its-victims-and-reveals-the-caster-to-their-team")]
    public static void InstantAreaCastsListTheEnemiesTheyStrikeAndRevealTheCaster()
    {
        var data = GameData.Default;
        // Shipped radii: the CastRadius of each spell's inibin. The list is the server's own (a self-area script spell lists the enemies within
        // that radius, BbChampionModule.CastSelfArea, a Reconstruction). Orianna's W and R left this list when she moved to her scripts (19 October
        // 2026, batch 112): they are TTYPE_Self spells whose script scans 225 and 415 around the ball, so the cast announces no target and reveals
        // nobody (a Reconstruction; retail's list for a self-cast is unknown).
        // The three spells keep SpellRevealsChampion on. (Janna's Monsoon left this list when she moved to her scripts, 19 October 2026: a
        // TTYPE_SelfAOE script spell announces an empty target list, see BbChampionModule.CastSelfArea.)
        // 19 October 2026: Lee Sin's Tempest is a script spell; its cast list is the inibin CastRadius (350), although
        // BlindMonkEOne's script strikes 450 (the hand-written Tempest listed 450).
        foreach (var (champion, slot, spell, radius, aimAhead) in new[]
                 {
                     ("Annie", 3, "InfernalGuardian", data.Spell("InfernalGuardian").CastRadius, 400.0),
                     ("Blitzcrank", 3, "StaticField", data.Spell("StaticField").CastRadius, 0.0),
                     ("LeeSin", 2, "BlindMonkEOne", data.Spell("BlindMonkEOne").CastRadius, 0.0),

                 })
        foreach (var inside in new[] { true, false })
        {
            TestAssert.True(data.Spell(spell).RevealsChampion, $"{spell} reveals its caster");
            using var match = new Match(champion, Team.Order, enemyChampion: "Vayne");
            var hero = match.Hero(0); var victim = match.Hero(1);
            var aim = hero.Position + new Position2(aimAhead, 0);
            victim.Position = aim + new Position2(radius + (inside ? -20 : 20), 0);
            match.Clear();

            TestAssert.True(match.TryCastGround(0, slot, aim), $"{spell} is accepted");
            var bubble = hero.NetId ^ 0x2000_0000u;
            var who = $"{spell} / victim {(inside ? "inside" : "outside")} {radius:0.#}";
            var lists = CastTargetLists(match.Captures[1], spell);
            TestAssert.Equal(1, lists.Count, $"{who}: the enemy seat receives one acknowledgement");
            TestAssert.Equal(inside ? new[] { victim.NetId } : Array.Empty<uint>(), lists[0].ToArray(),
                $"{who}: the cast lists the enemy it strikes");
            TestAssert.Near(inside ? RevealSeconds : 0.0, hero.AttackRevealRemaining[(int)Team.Chaos], 1e-9,
                $"{who}: the hit team sees the caster for ca_RevealAttackerTimeOut");
            if (inside)
                TestAssert.Equal(hero.Position, hero.AttackRevealPosition[(int)Team.Chaos], $"{who}: at the cast point");
            // Batch 52 (79d621b): the client adds the 400-unit bubble itself in its cast frame; no packet is sent.
            TestAssert.Equal(0, match.Captures[1].Packets.Count(packet => IsAttackBubble(packet, bubble)),
                $"{who}: the victim's client is sent no bubble packet");
            TestAssert.Equal(0, match.Captures[0].Packets.Count(packet => IsAttackBubble(packet, bubble)),
                $"{who}: the caster's own team never does");
        }
    }

    [Check("spells-live-line-missile-hit-reveals-the-caster-by-the-missile-spells-flag")]
    public static void LineMissileHitsRevealTheCasterOnlyWhenTheChildSpellSetsTheFlag()
    {
        var data = GameData.Default;
        // The cast of a line spell lists nobody (a listed unit would make the client spawn one more
        // missile for it), so the reveal waits for the hit. Flags are explicit in the missile spells'
        // inibins: True on the first four, False on EzrealEssenceFluxMissile and JavelinToss. (Janna's
        // HowlingGaleSpell, absent = True, left this list when she moved to her scripts, 19 October 2026: the
        // wave is cast by the script's TestCube, so its hit reveals the cube's position, not Janna's.)
        foreach (var (champion, slot, child, reveals) in new[]
                 {
                     ("Ezreal", 0, "EzrealMysticShotMissile", true),
                     ("Ezreal", 1, "EzrealEssenceFluxMissile", false),
                     ("Blitzcrank", 0, "RocketGrabMissile", true),
                     ("LeeSin", 0, "BlindMonkQOne", true),
                     ("Orianna", 0, "OrianaIzuna", true),
                     ("Nidalee", 0, "JavelinToss", false),
                 })
        {
            TestAssert.Equal(reveals, data.Spell(child).RevealsChampion, $"{child}: shipped flag");
            using var match = new Match(champion, Team.Order, enemyChampion: "Vayne");
            var hero = match.Hero(0); var victim = match.Hero(1);
            victim.Position = hero.Position + new Position2(400, 0);
            // The first runtime step replaces the scenario's 5000 health with level-1 stats.
            match.Runtime.Tick(.02);
            var health = victim.Health;
            match.Clear();

            match.CastGround(0, victim.Position, slot);
            var bubble = hero.NetId ^ 0x2000_0000u;
            TestAssert.Near(0.0, hero.AttackRevealRemaining[(int)Team.Chaos], 0, $"{child}: the cast reveals nothing");
            for (var elapsed = 0.0; elapsed < 3.0 && victim.Health >= health; elapsed += .02)
                match.Runtime.Tick(.02);
            TestAssert.True(victim.Health < health, $"{child}: the missile hits the enemy");
            if (reveals)
            {
                var remaining = hero.AttackRevealRemaining[(int)Team.Chaos];
                TestAssert.True(remaining > RevealSeconds - .1 && remaining <= RevealSeconds,
                    $"{child}: the hit shows the caster to the hit unit's team for 4.5 s, got {remaining}");
                // Batch 52 (79d621b): the client adds the bubble itself in its cast frame; the server sends no packet.
                TestAssert.Equal(0, match.Captures[1].Packets.Count(packet => IsAttackBubble(packet, bubble)),
                    $"{child}: the victim's client is sent no bubble packet");
            }
            else
            {
                TestAssert.Near(0.0, hero.AttackRevealRemaining[(int)Team.Chaos], 0, $"{child}: flag False reveals nothing");
                TestAssert.Equal(0, match.Captures[1].Packets.Count(packet => IsAttackBubble(packet, bubble)),
                    $"{child}: no bubble is sent");
            }
        }
    }

    [Check("spells-live-homing-child-cast-reveals-the-caster-at-launch")]
    public static void InstantHomingChildListsItsTargetAndRevealsTheCasterAtLaunch()
    {
        // EzrealArcaneShiftMissile.inibin: Instacast (flag bit 4), unit-target missile, SpellRevealsChampion
        // = 1. Its own cast lists the bolt's target, so the reveal is at launch, in the instant-cast call path,
        // after the blink has moved Ezreal.
        var child = GameData.Default.Spell("EzrealArcaneShiftMissile");
        TestAssert.True(child.IsInstant && child.TargetsUnit && child.RevealsChampion, "shipped child data");
        using var match = new Match("Ezreal", Team.Order, enemyChampion: "Vayne");
        var hero = match.Hero(0); var victim = match.Hero(1);
        victim.Position = hero.Position + new Position2(300, 0);
        match.Runtime.Tick(.02);
        match.Clear();

        // Blink 300 units away from the enemy: it is still within the 750-unit bolt acquisition range.
        match.CastGround(0, hero.Position + new Position2(-300, 0), 2);
        TestAssert.Near(0.0, hero.AttackRevealRemaining[(int)Team.Chaos], 0, "the parent cast reveals nothing");
        for (var elapsed = 0.0; elapsed < 1.0 && EzrealFlights.InFlight(match.Runtime.Spells, hero).Length == 0; elapsed += .02)
            match.Runtime.Tick(.02);
        TestAssert.Equal(1, EzrealFlights.InFlight(match.Runtime.Spells, hero).Length, "the bolt launches at the enemy");
        TestAssert.True(hero.AttackRevealRemaining[(int)Team.Chaos] > RevealSeconds - .1,
            "the launch shows the caster to the bolt target's team");
        TestAssert.Equal(hero.Position, hero.AttackRevealPosition[(int)Team.Chaos], "at the post-blink position");
    }

    [Check("combat.basic-attack-reveal-follows-the-attack-spells-flag")]
    public static void BasicAttackRevealHonoursSpellRevealsChampion()
    {
        var data = GameData.Default;
        // The reveal routine serves attacks too. Only the Malzahar voidling attacks ship False.
        foreach (var (character, reveals) in new[]
                 {
                     ("Annie", true), ("Yorick", true), ("Wolf", true), ("Blue_Minion_Basic", true),
                     ("UnitWithNoAttackSpell", true), ("MalzaharVoidling", false),
                 })
            TestAssert.Equal(reveals, data.BasicAttackRevealsChampion(character), $"{character}: attack flag");

        foreach (var (character, expectReveal) in new[] { ("Annie", true), ("MalzaharVoidling", false) })
        {
            using var match = new Match("Annie", Team.Order, enemyChampion: "Yorick");
            var attacker = match.Hero(0); var victim = match.Hero(1);
            // The attack path reads the attacker's character; no playable champion ships a False attack.
            attacker.ChampionName = character;
            victim.Position = attacker.Position + new Position2(200, 0);
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(attacker, victim), $"{character}: the attack starts");
            // Batch 49 (1ccf9f7): the reveal runs at the cast frame, the end of the windup, not when the swing starts.
            TestAssert.True(attacker.AttackWindup > 0.0, $"{character}: the swing has a windup");
            TestAssert.Near(0.0, attacker.AttackRevealRemaining[(int)Team.Chaos], 0,
                $"{character}: a swing still in its windup reveals nothing");
            match.Runtime.Combat.CombatTick(attacker.AttackWindup, regenerate: false);
            TestAssert.Near(expectReveal ? RevealSeconds : 0.0, attacker.AttackRevealRemaining[(int)Team.Chaos], 1e-9,
                $"{character}: basic attack reveal");
        }
    }
}
