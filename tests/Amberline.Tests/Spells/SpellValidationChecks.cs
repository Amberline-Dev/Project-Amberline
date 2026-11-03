using System.Text.Json;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

/// <summary>Shared parser, rank-gate, and cast-validation oracle checks.</summary>
public static class SpellValidationChecks
{


    private static void CheckParsers(JsonElement cases)
    {
        foreach (var testCase in cases.EnumerateArray())
        {
            var hex = testCase.GetProperty("hex").GetString() ?? string.Empty;
            var actual = SpellSystem.ParseCastRequest(
                hex.Length == 0 ? ReadOnlySpan<byte>.Empty : Convert.FromHexString(hex));
            var expected = testCase.GetProperty("expected");
            if (expected.ValueKind == JsonValueKind.Null)
            {
                TestAssert.True(actual is null, $"parser {hex} must reject");
                continue;
            }

            TestAssert.True(actual is not null, $"parser {hex} must decode");
            var request = actual!.Value;
            TestAssert.Equal(expected.GetProperty("slot").GetInt32(), request.Slot,
                $"parser {hex} slot");
            TestAssert.Equal(expected.GetProperty("is_summoner").GetBoolean(), request.IsSummoner,
                $"parser {hex} summoner");
            ComparePoint(expected.GetProperty("pos"), request.Position,
                $"parser {hex} position");
            ComparePoint(expected.GetProperty("end_pos"), request.EndPosition,
                $"parser {hex} end position");
            TestAssert.Equal(expected.GetProperty("target_netid").GetUInt32(), request.TargetNetId,
                $"parser {hex} target");
        }
    }

    private static void CheckRankGates(JsonElement cases)
    {
        using var runtime = new ValidationRuntime("Annie");
        var hero = runtime.Hero;
        foreach (var testCase in cases.EnumerateArray())
        {
            var slot = testCase.GetProperty("slot").GetInt32();
            hero.Level = testCase.GetProperty("level").GetInt32();
            hero.SkillPoints = testCase.GetProperty("points").GetInt32();
            hero.SpellRanks = [0, 0, 0, 0];
            if ((uint)slot < hero.SpellRanks.Count)
                hero.SpellRanks[slot] = testCase.GetProperty("current").GetInt32();

            var actual = runtime.Spells.CanRankUp(hero, slot);
            TestAssert.Equal(testCase.GetProperty("allowed").GetBoolean(), actual.Allowed,
                $"rank slot={slot} level={hero.Level} current={hero.SpellRanks.ElementAtOrDefault(Math.Max(slot, 0))}");
            TestAssert.Equal(testCase.GetProperty("reason").GetString() ?? string.Empty,
                actual.Reason, $"rank slot={slot} reason");
        }
    }

    private static void CheckCastValidation(JsonElement cases)
    {
        var failures = new List<string>();
        var grouped = cases.EnumerateArray()
            .GroupBy(testCase => testCase.GetProperty("champion").GetString() ?? "Annie",
                StringComparer.OrdinalIgnoreCase);
        foreach (var group in grouped)
        {
            using var runtime = new ValidationRuntime(group.Key);
            foreach (var testCase in group)
            {
                try { CheckCastCase(runtime, testCase); }
                catch (Exception exception)
                {
                    failures.Add($"{group.Key}/v{testCase.GetProperty("variant").GetInt32()}" +
                        $"/s{testCase.GetProperty("slot").GetInt32()}: {exception.Message}");
                }
            }
        }
        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"{failures.Count} validation cases failed" + Environment.NewLine +
                string.Join(Environment.NewLine, failures.Take(40)));
    }

    private static void CheckCastCase(ValidationRuntime runtime, JsonElement testCase)
    {
        var champion = testCase.GetProperty("champion").GetString() ?? "Annie";
        var heroId = testCase.GetProperty("hero").GetUInt32();
        var commandTargetId = testCase.GetProperty("target").GetUInt32();
        var targetEntityId = testCase.GetProperty("units")[1].GetProperty("netid").GetUInt32();
        var hero = runtime.World.Get(heroId)
            ?? throw new InvalidOperationException($"validation hero 0x{heroId:X8} missing");
        var target = runtime.World.Get(targetEntityId)
            ?? throw new InvalidOperationException($"validation target 0x{targetEntityId:X8} missing");
        ApplyUnit(testCase.GetProperty("units")[0], hero);
        ApplyUnit(testCase.GetProperty("units")[1], target);
        // This corpus isolates rank, resource, allegiance and range gates.
        // Fog at initial/executing casts is covered by the live approach checks.
        runtime.World.TimedVisionBubbles.Clear();
        runtime.World.TimedVisionBubbles.Add(new TimedVisionBubble(
            runtime.World.Allocate(UnitType.Missile), hero.Team, target.Position, 1, 1));

        var position = Point3(testCase.GetProperty("position"));
        var slot = testCase.GetProperty("slot").GetInt32();
        // The archived request has one point; it is the aim too (a cone's end, batch 324).
        var actual = runtime.Spells.CanCast(hero, slot, commandTargetId, position, 0.0, position);
        var expectedAllowed = testCase.GetProperty("allowed").GetBoolean();
        var expectedCause = testCase.GetProperty("cause").GetString();
        var expectedReason = testCase.GetProperty("reason").GetString() ?? string.Empty;
        var variant = testCase.GetProperty("variant").GetInt32();
        var context = $"cast {champion} slot={slot} variant={variant}";
        // Keep the archived requests intact. Variant 14 encodes the audited
        // bug: an incidental untargetable unit rejected every cast mode.
        // These are the shipped first-form/first-cast non-unit slots in this
        // corpus; the separate live check covers dynamic forms and recasts.
        var nonUnitSlot = champion switch
        {
            "Annie" => slot is 1 or 2 or 3,
            "Orianna" or "Nidalee" or "Shaco" or "Vayne" => slot is 0 or 1 or 3,
            "LeeSin" => slot is 0 or 2,
            "Yorick" => slot is 0 or 1,
            "Janna" => slot is 0 or 3,
            "Ezreal" or "Blitzcrank" => slot is >= 0 and <= 3,
            _ => false,
        };
        if (variant == 14 && nonUnitSlot)
        {
            TestAssert.False(expectedAllowed, context + " archived incidental-unit rejection");
            TestAssert.Equal("target", expectedCause, context + " archived failure cause");
            TestAssert.True(target.Buffs.Any(buff => buff.Payload.Untargetable));
            expectedAllowed = true; expectedCause = ""; expectedReason = "";
            if (champion == "Annie" && slot == 1)
            {
                expectedAllowed = false; expectedCause = "range";
                expectedReason = "Incinerate target is out of range (75)";
            }
            else if (champion == "Vayne" && slot == 1)
            {
                expectedAllowed = false; expectedCause = "disabled";
                expectedReason = "Silver Bolts is passive";
            }
        }
        // 19 October 2026: Janna runs from her scripts. Howling Gale is a TTYPE_Direction skillshot there: BbChampionModule.Validate gives such a
        // spell no cast point to reach (its CastRange is how far the wave flies), so the archived aim-out-of-range refusal of the hand-written
        // module no longer holds; the spell is accepted wherever it is aimed.
        if (champion == "Janna" && slot == 0 && variant == 7)
        {
            TestAssert.False(expectedAllowed, context + " archived aim-out-of-range refusal");
            TestAssert.Equal("range", expectedCause, context + " archived failure cause");
            expectedAllowed = true; expectedCause = ""; expectedReason = "";
        }
        // 19 October 2026: Orianna runs from her scripts. Izuna is a Location spell the script clamps (885 from the caster), but the module's cast gate
        // refuses an aim beyond the spell's CastRange (800) like every Location spell and the order walks into range (HandleCastRequest turns the
        // Range failure into an approach); the hand-written module accepted any aim.
        if (champion == "Orianna" && slot == 0 && variant == 7)
        {
            TestAssert.True(expectedAllowed, context + " archived far-aim acceptance");
            expectedAllowed = false; expectedCause = "range";
            expectedReason = "OrianaIzunaCommand target is out of range (800)";
        }
        if (champion == "Vayne" && slot == 0 && variant == 13)
        {
            TestAssert.True(expectedAllowed, context + " archived rooted-Tumble acceptance");
            TestAssert.True(hero.Buffs.Any(buff => buff.Type == BuffType.Snare));
            // 19 October 2026: VayneTumble.luaobj's CanCast refuses a unit that cannot move; the module words a script CanCast refusal as a target failure.
            expectedAllowed = false; expectedCause = "target";
            expectedReason = "VayneTumble has no valid target (script CanCast)";
        }
        // 19 October 2026: Silver Bolts is a passive slot the script seals (BBSealSpellSlot), and the sealed-slot gate answers before the rank, silence and
        // target gates. The seal outlives the rank a row sets (the rows of one champion share a runtime), so even the not-learned row meets it. The
        // hand-written module said "Silver Bolts is passive" for the same refusal, with the rank gate first.
        if (champion == "Vayne" && slot == 1 && variant == 2)
        {
            TestAssert.Equal("rank", expectedCause, context + " archived failure cause");
            expectedCause = "disabled"; expectedReason = "spell slot is sealed (BBSealSpellSlot)";
        }
        // Batch 53 (fd56898): a spell whose shipped inibin sets CantCastWhileRooted is refused for a unit that cannot move
        // (the unit's cast check), before any target gate. The archived rooted rows predate the flag.
        var rootedSpell = (champion, slot) switch
        {
            ("LeeSin", 1) => "BlindMonkWOne",
            ("Shaco", 0) => "Deceive",
            ("Ezreal", 2) => "EzrealArcaneShift",
            _ => null,
        };
        if (variant == 13 && rootedSpell is not null)
        {
            TestAssert.True(GameData.Default.Spell(rootedSpell).RawFlag("SpellData*CantCastWhileRooted"),
                context + " shipped CantCastWhileRooted flag");
            TestAssert.True(hero.Buffs.Any(buff => buff.Type == BuffType.Snare));
            expectedAllowed = false; expectedCause = "disabled";
            expectedReason = "caster cannot cast in its control state";
        }
        // Batch 324: Incinerate fires by the client's cone test (the cone targeter, BbChampionModule Cone.Fire), with
        // the archived point as the aim: an aim nearer than L = 446.3 + 75 = 521.3 fires (the targeter moves a nearer one out to 446.3), a farther one
        // walks. The archived refusals came from the old 75-unit test on the start.
        if (champion == "Annie" && slot == 1 && !expectedAllowed && expectedCause == "range")
        {
            var reach = Math.Sqrt(Math.Pow(position.X - hero.Position.X, 2) + Math.Pow(position.Z - hero.Position.Z, 2));
            if (reach <= 521.3) { expectedAllowed = true; expectedCause = ""; expectedReason = ""; }
            else expectedReason = "Incinerate target is out of range (cone)";
        }
        TestAssert.Equal(expectedAllowed, actual.Allowed,
            context + $" allowed expected={expectedAllowed} actual={actual.Allowed}");

        var expectedFailure = expectedCause switch
        {
            null or "" => SpellCastFailure.None,
            "dead" => SpellCastFailure.Dead,
            "rank" => SpellCastFailure.Rank,
            "cooldown" => SpellCastFailure.Cooldown,
            "mana" => SpellCastFailure.Mana,
            "target" => SpellCastFailure.Target,
            "range" => SpellCastFailure.Range,
            "disabled" => SpellCastFailure.Disabled,
            _ => throw new InvalidDataException($"unknown validation cause {expectedCause}"),
        };
        TestAssert.Equal(expectedFailure, actual.Failure,
            context + $" failure expected={expectedFailure} actual={actual.Failure}");
        // The source fixture records user-facing diagnostics.  Several
        // champion modules intentionally retain clearer C# diagnostic names
        // while preserving the source's accepted/rejected outcome and cause.
        // Keep this small compatibility table explicit so an unexpected
        // diagnostic change still fails the corpus check.
        // 19 October 2026: LeeSin is a script champion. His rows hold the hand-written module's own wording ("Safeguard needs a living
        // ally", "not enough energy", "Dragon Rage target out of range"); the script module words the same refusals from the spell's name
        // ("BlindMonkWOne cannot target an enemy"). Outcome and cause are still compared, and so is the wording of the shared gates (dead,
        // disabled, not targetable). Re-record the LeeSin rows (FixtureRecorder) to compare the module wording again.
        // The same holds for the other champions that run from their scripts (19 October 2026: Vayne, Blitzcrank, Shaco, Nidalee, Yorick and Orianna): their rows
        // hold the hand-written modules' wording ("Ravenous needs an enemy unit", "Primal Surge needs an allied champion", "box placement out of range")
        // and the script module words the same refusals from the spell's name; the shape of that wording is pinned in ScriptRefusalWording.
        var scriptWording = champion is "LeeSin" or "Vayne" or "Blitzcrank" or "Shaco" or "Nidalee" or "Yorick" or "Orianna" &&
            expectedReason != "target is not targetable" && ScriptRefusalWording(expectedFailure, actual.Reason);
        TestAssert.True(scriptWording || string.Equals(expectedReason, actual.Reason, StringComparison.Ordinal) ||
                        EquivalentDiagnostic(expectedReason, actual.Reason) ||
                        champion == "Janna" && JannaScriptDiagnostic(expectedReason, actual.Reason),
            context + $" reason expected={expectedReason} actual={actual.Reason}");
    }

    // Janna's rows were archived with the words of her hand-written module. BbChampionModule (19 October 2026) names the spell and its numbers
    // ("{spell} is on cooldown ({seconds}s left)", "not enough mana for {spell} (need {cost}, have {mana})", "{spell} target is out of range
    // ({range})") and words a target refusal "{spell} " + BbSpellTargeting.RejectTarget; the outcome and the cause are unchanged.
    private static bool JannaScriptDiagnostic(string expected, string actual) => expected switch
    {
        "not enough mana" => actual.StartsWith("not enough mana for ", StringComparison.Ordinal),
        "Zephyr target out of range" => actual.StartsWith("SowTheWind target is out of range (", StringComparison.Ordinal),
        "Zephyr needs a living enemy" => ScriptTargetRefusal("SowTheWind", actual),
        "Eye of the Storm needs an allied hero or turret" => ScriptTargetRefusal("EyeOfTheStorm", actual),
        _ => expected.EndsWith(" is on cooldown", StringComparison.Ordinal) &&
             actual.StartsWith(expected + " (", StringComparison.Ordinal) && actual.EndsWith("s left)", StringComparison.Ordinal),
    };

    /// <summary>
    /// The script module's wording of the data and target gates (<c>BbChampionModule.Validate</c>): "{spell} has no rank learned", "{spell} is on
    /// cooldown ({n}s left)", "not enough mana for {spell} (need {c}, have {m})", "{spell} target is out of range ({r})" and "{spell} " followed by
    /// the target refusal of <c>BbSpellTargeting.RejectTarget</c>.
    /// </summary>
    private static bool ScriptRefusalWording(SpellCastFailure failure, string actual) => failure switch
    {
        SpellCastFailure.Rank => actual.EndsWith(" has no rank learned", StringComparison.Ordinal),
        SpellCastFailure.Cooldown => actual.Contains(" is on cooldown (", StringComparison.Ordinal) && actual.EndsWith("s left)", StringComparison.Ordinal),
        SpellCastFailure.Mana => actual.StartsWith("not enough mana for ", StringComparison.Ordinal) && actual.Contains(" (need ", StringComparison.Ordinal),
        SpellCastFailure.Range => actual.Contains(" target is out of range (", StringComparison.Ordinal),
        SpellCastFailure.Target => actual.Split(' ', 2) is [_, var refusal] && refusal is "target is not alive" or "cannot target an ally" or
            "cannot target an enemy" or "cannot target that unit" or "cannot target a building" or "cannot target itself",
        _ => false,
    };

    private static bool ScriptTargetRefusal(string spell, string actual) =>
        actual.StartsWith(spell + " ", StringComparison.Ordinal) &&
        (actual[(spell.Length + 1)..] is "target is not alive" or "cannot target an ally" or "cannot target an enemy" or
            "cannot target that unit" or "cannot target a building" or "cannot target itself");

    private static bool EquivalentDiagnostic(string expected, string actual) =>
        (expected, actual) switch
        {
            // Batch 50/53 (fd56898): every cannot-cast state shares the OnCanUseSpell gate and its control-state reason.
            ("caster is silenced", "caster cannot cast in its control state") => true,
            // Batch 314: the stun too; the server's own action-disabled gate is gone (it refused even CanCastWhileDisabled spells).
            ("caster is action-disabled", "caster cannot cast in its control state") => true,
            // ... and a sealed slot (Vayne's Silver Bolts) answers first: the spellbook's cast check tests the cast bit before the owner callback.
            ("caster is action-disabled", "spell slot is sealed (BBSealSpellSlot)") => true,
            ("Command: Attack is not learned", "Orianna spell is not learned") => true,
            ("Command: Attack is on cooldown", "OrianaIzunaCommand is on cooldown") => true,
            ("Command: Attack is on cooldown", "OrianaDissonanceCommand is on cooldown") => true,
            ("Command: Attack is on cooldown", "OrianaRedactCommand is on cooldown") => true,
            ("Command: Attack is on cooldown", "OrianaDetonateCommand is on cooldown") => true,
            ("spell is not learned", "spell rank is outside the learned range") => true,
            ("spell is not learned", "spell is not learned") => true,
            ("Vayne spell is not learned", "spell is not learned") => true,
            ("Vayne spell is on cooldown", "spell is on cooldown") => true,
            ("Ravenous needs an enemy unit", "target is not alive") => true,
            ("ultimate needs an allied champion", "target is not alive") => true,
            // Ezreal runs from his shipped scripts (19 October 2026): BbChampionModule names the spell in the three data gates.
            ("spell is not learned", var learned) when learned.StartsWith("Ezreal", StringComparison.Ordinal) &&
                                                      learned.EndsWith(" has no rank learned", StringComparison.Ordinal) => true,
            ("spell is on cooldown", var cooling) when cooling.StartsWith("Ezreal", StringComparison.Ordinal) &&
                                                      cooling.Contains(" is on cooldown (", StringComparison.Ordinal) => true,
            ("not enough mana", var mana) when mana.StartsWith("not enough mana for Ezreal", StringComparison.Ordinal) => true,
            // Vayne's W is sealed by her script (BBSealSpellSlot), and that gate answers before the silence gate.
            ("Silver Bolts is passive" or "caster is silenced", "spell slot is sealed (BBSealSpellSlot)") => true,
            _ => false,
        };

    private static void ApplyUnit(JsonElement raw, Entity unit)
    {
        unit.Kind = EntityKindExtensions.FromSourceType(raw.GetProperty("etype").GetString());
        unit.UnitType = (UnitType)raw.GetProperty("unit_type").GetInt32();
        unit.Team = (Team)raw.GetProperty("team").GetInt32();
        unit.Position = Point2(raw.GetProperty("pos"));
        unit.ChampionName = raw.GetProperty("champion_name").GetString() ?? "Annie";
        unit.Alive = raw.GetProperty("alive").GetBoolean();
        unit.Created = true;
        unit.Visible = true;
        unit.MaxHealth = 10000.0;
        unit.Health = unit.Alive ? 10000.0 : 0.0;
        unit.MaxMana = 10000.0;
        unit.Mana = raw.GetProperty("mana").GetDouble();
        unit.Level = raw.GetProperty("level").GetInt32();
        unit.SpellRanks = raw.GetProperty("spell_ranks").EnumerateArray()
            .Select(value => value.GetInt32()).ToList();
        unit.SpellCooldowns = raw.GetProperty("spell_cooldowns").EnumerateArray()
            .Select(value => value.GetDouble()).ToList();
        unit.SummonerCooldowns = [0.0, 0.0];
        unit.ItemCooldowns = [0.0, 0.0, 0.0, 0.0, 0.0, 0.0];
        unit.Buffs.Clear();
        foreach (var buff in raw.GetProperty("buffs").EnumerateArray())
        {
            var payload = new BuffPayload();
            if (buff.TryGetProperty("payload", out var payloadJson) &&
                payloadJson.ValueKind == JsonValueKind.Object &&
                payloadJson.TryGetProperty("untargetable", out var untargetable))
                payload.Untargetable = untargetable.GetBoolean();
            unit.Buffs.Add(new BuffState(
                buff.GetProperty("name").GetString() ?? "CorpusBuff",
                payload,
                (BuffType)buff.GetProperty("type").GetInt32(),
                buff.GetProperty("slot").GetInt32(),
                buff.GetProperty("expires_at").GetDouble(),
                buff.GetProperty("caster").GetUInt32()));
        }
        unit.PendingCast = null;
        unit.ChampionState = null;
        unit.YorickRevival = null;
    }

    private static Position2 Point2(JsonElement value) =>
        new(value[0].GetDouble(), value.GetArrayLength() == 3 ? value[2].GetDouble() : value[1].GetDouble());

    private static Position3 Point3(JsonElement value) =>
        new(value[0].GetDouble(), value[1].GetDouble(), value[2].GetDouble());

    private static void ComparePoint(JsonElement expected, Position3 actual, string context)
    {
        TestAssert.Near(expected[0].GetDouble(), actual.X, 1e-12, context + " X");
        TestAssert.Near(expected[1].GetDouble(), actual.Y, 1e-12, context + " Y");
        TestAssert.Near(expected[2].GetDouble(), actual.Z, 1e-12, context + " Z");
    }

    private sealed class ValidationRuntime : IDisposable
    {
        private readonly MatchRuntime _runtime;

        public ValidationRuntime(string champion)
        {
            _runtime = MatchRuntime.CreateProduction(
                NavigationGrid.LoadDefault(), GameData.Default,
                orderChampion: champion, chaosChampion: "Annie",
                readySettleSeconds: 0.0);
            _runtime.World.Clock.SetVirtual(0.0);
            _runtime.Session.CreateWorld();
        }

        public WorldState World => _runtime.World;
        public SpellSystem Spells => _runtime.Spells;
        public Entity Hero => World.Get(0x40000001)
            ?? throw new InvalidOperationException("validation runtime hero missing");

        public void Dispose()
        {
            _runtime.World.Clock.UseReal();
        }
    }
}
