using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Protocol;
using Amberline.Simulation;
using Amberline.Simulation.Spells.Champions;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

// Schema adapters for the spell corpora. FixtureRecorder alone owns file output.
internal sealed class SpellReplayRecording : IDisposable
{
    private static SpellReplayRecording? current;
    private readonly string fixture;
    private readonly JsonNode root;
    private readonly Dictionary<JsonElement, JsonNode> nodes = new();
    public JsonDocument Document { get; }

    [Check("spells-replay-recording-schema")]
    public static void SnapshotSchemaRetainsEntitiesTagsAndNumberText()
    {
        using var layout = JsonDocument.Parse("[{\"netid\":1,\"etype\":\"hero\",\"position\":[0.0,0.0],\"hp\":100.0}]");
        var world = new WorldState();
        world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(10, 20)) { Health = 50 });
        world.Register(new Entity(2, EntityKind.NidaleeTrap, UnitType.Monster, Team.Order, new(30, 40)) { Health = 1 });
        var entities = Entities(layout.RootElement, world);
        TestAssert.Equal(2, entities.Count, "trap registry output is not filtered");
        TestAssert.Equal(50.0, entities[0]!["hp"]!.GetValue<double>());
        TestAssert.Equal("nidalee_trap", entities[1]!["etype"]!.GetValue<string>());
        TestAssert.Equal("ab01", Value(new byte[] { 0xab, 1 })!["bytes"]!.GetValue<string>());
        TestAssert.Equal(1u, Value(world.Get(1))!["entity"]!.GetValue<uint>());
        var merged = Merge(JsonNode.Parse("{\"n\":1.0,\"changed\":2}"), JsonNode.Parse("{\"n\":1,\"changed\":3}"));
        TestAssert.Equal("{\"n\":1.0,\"changed\":3}", merged!.ToJsonString());
    }

    public SpellReplayRecording(string name)
    {
        fixture = name;
        var text = File.ReadAllText(AnnieChecks.Fixture(name));
        Document = JsonDocument.Parse(text);
        root = JsonNode.Parse(text)!;
        if (FixtureRecorder.Enabled)
        {
            TestAssert.True(current is null, "one spell recording at a time");
            current = this;
            Index(Document.RootElement, root);
        }
    }

    private void Index(JsonElement source, JsonNode? node, string fieldName = "")
    {
        if (node is JsonObject obj)
        {
            // Only cases and snapshots are recording destinations. Indexing every
            // nested value makes JsonElement's document-based hash collide heavily.
            if (obj.ContainsKey("entities") || obj.ContainsKey("initial") || obj.ContainsKey("initialState"))
                nodes.Add(source, obj);
            foreach (var field in source.EnumerateObject())
                if (field.Name != "entities") Index(field.Value, obj[field.Name],
                    fieldName == "expectedPeerPackets" ? "packets" : field.Name);
        }
        else if (node is JsonArray array)
        {
            if (fieldName == "packets") { nodes.Add(source, array); return; }
            for (var i = 0; i < array.Count; i++) Index(source[i], array[i]);
        }
    }


    public void Dispose()
    {
        if (ReferenceEquals(current, this)) current = null;
        Document.Dispose();
    }

    // Keep unchanged JsonValues, including their original numeric spelling.
    private static JsonNode? Merge(JsonNode? old, JsonNode? value)
    {
        if (JsonNode.DeepEquals(old, value)) return old?.DeepClone();
        if (old is JsonValue ov && value is JsonValue nv &&
            ov.TryGetValue<double>(out var od) && nv.TryGetValue<double>(out var nd) && od == nd)
            return old.DeepClone();
        if (value is JsonObject obj)
        {
            var result = new JsonObject();
            if (old is JsonObject prior)
                foreach (var field in prior)
                    if (obj.ContainsKey(field.Key)) result[field.Key] = Merge(field.Value, obj[field.Key]);
            foreach (var field in obj)
                if (!result.ContainsKey(field.Key)) result[field.Key] = field.Value?.DeepClone();
            return result;
        }
        if (value is JsonArray array)
            return new JsonArray(array.Select((v, i) => Merge(old is JsonArray before && i < before.Count ? before[i] : null, v)).ToArray());
        return value?.DeepClone();
    }

    public static void Fields(JsonElement expected, JsonObject actual)
    {
        var destination = current!.nodes[expected].AsObject();
        foreach (var field in actual) destination[field.Key] = Merge(destination[field.Key], field.Value);
    }

    /// <summary>
    /// Replaces one row of a case's RNG tape. The tape is a supply, not an output; its kinds and ranges follow the code that draws from it,
    /// so a consumer that draws another kind than the recorded one (a script engine's <c>random</c> where the hand-written module drew a
    /// <c>uniform</c>) gets a default row of the kind it draws (random 0.5, uniform fraction 0.35, choice index 0), as <see cref="AppendRandom"/> does.
    /// </summary>
    public static void ReplaceRandom(JsonElement testCase, int index, JsonObject call)
    {
        var node = current!.nodes[testCase].AsObject();
        node["randomCalls"]!.AsArray()[index] = call;
        node["randomExtensionNote"] = "28 Sept 2026: additional calls use random=0.5, uniform fraction=0.35, choice index=0, supplied at the random boundary independently of expected outputs. 19 Oct 2026: a row whose kind or range the current consumer does not draw is replaced the same way.";
    }

    public static void AppendRandom(JsonElement testCase, JsonObject call)
    {
        var node = current!.nodes[testCase].AsObject();
        if (node["randomCalls"] is not JsonArray) node["randomCalls"] = new JsonArray();
        node["randomCalls"]!.AsArray().Add(call);
        node["randomExtensionNote"] = "28 Sept 2026: additional calls use random=0.5, uniform fraction=0.35, choice index=0, supplied at the random boundary independently of expected outputs.";
    }

    public static void Packets(JsonElement expected, IReadOnlyList<PlaintextPacket> packets)
    {
        var cipher = LegacyBlowfish.CreateDefault();
        static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
        var actual = new JsonArray();
        foreach (var packet in packets)
        {
            var wire = packet.ToWireBytes(cipher);
            actual.Add(new JsonObject
            {
                ["channel"] = packet.Channel, ["flags"] = (byte)packet.Reliability,
                ["plaintext"] = Hex(cipher.Decrypt(wire)), ["wire"] = Hex(wire),
            });
        }
        var destination = current!.nodes[expected].AsArray();
        var merged = Merge(destination, actual)!.AsArray();
        destination.Clear();
        foreach (var row in merged) destination.Add(row?.DeepClone());
    }

    public static JsonArray Entities(JsonElement layout, WorldState world) => new(
        world.Entities.Values.Select(entity => EntitySnapshot(entity, layout.EnumerateArray()
            .FirstOrDefault(row => row.GetProperty("netid").GetUInt32() == entity.NetId), layout[0])).ToArray());

    private static JsonNode EntitySnapshot(Entity entity, JsonElement row, JsonElement fallback)
    {
        var template = row.ValueKind == JsonValueKind.Object ? row : fallback;
        var result = new JsonObject();
        foreach (var field in template.EnumerateObject())
        {
            object? value = field.Name switch
            {
                "etype" => entity.Kind.ToSourceType(),
                "item_counters" or "itemCounters" => entity.ItemState,
                // 19 October 2026: Orianna has no champion state (her ball is a script-spawned unit); a recorded orianna_ball column reads as absent.
                "orianna_ball" => null,
                // 19 October 2026: Lee Sin's state left the entity with the hand-written module (his scripts keep theirs in buffs); the recorded
                // column stays in the layouts as a constant.
                "leesin_state" => null,
                // 19 October 2026: Nidalee runs from her shipped scripts; a recorded hand-written state column reads as absent.
                "nidalee_state" => null,
                // 19 October 2026: Ezreal runs from his shipped scripts; a recorded hand-written state column reads as absent.
                "ezreal_state" => null,
                // 19 October 2026: Vayne and Shaco run from their shipped scripts; a recorded hand-written state column reads as absent.
                "vayne_state" => null,
                "shaco_state" => null,
                // 25 October 2026 (batch 191, record path only): Recall left the entity with batch 179 (SummonersRecall holds the channel), and the
                // summoner corpus no longer compares or restores these columns (batch 179's record). The recorded columns read as absent, as above.
                "recall_remaining" or "recall_fx_netid" => null,
                // 19 October 2026: Janna and Yorick run from their scripts (charge, wave, channel, ghouls, revenant and zombie life are buffs and units); the recorded
                // columns stay null.
                "yorick_state" => null,
                "janna_state" => null,
                "blitzcrank_state" => entity.ChampionState?.GetType().Name == "BlitzcrankState" ? entity.ChampionState : null,
                _ => Property(entity, field.Name),
            };
            result[field.Name] = Value(value, field.Value);
        }
        return result;
    }

    public static JsonArray Hits(IEnumerable<PendingHit> hits, bool full = true) => new(hits.Select(hit =>
    {
        var row = new JsonArray(hit.WindupRemaining, hit.ProjectilePosition.X, hit.ProjectilePosition.Z,
            hit.SourceNetId, hit.TargetNetId, hit.Damage, hit.MissileSpeed);
        if (full)
        {
            row.Add(hit.SourceKind is { } kind ? JsonNamingPolicy.SnakeCaseLower.ConvertName(kind.ToString()) : null);
            row.Add(hit.MissileNetId);
        }
        return (JsonNode)row;
    }).ToArray());

    public static JsonObject Map<T>(IEnumerable<KeyValuePair<uint, T>> pairs) => new(
        pairs.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key.ToString(CultureInfo.InvariantCulture), Value(pair.Value))));

    public static JsonArray Pending(SpellSystem spells, bool compact) => new(spells.SharedProjectiles.Select(projectile =>
    {
        var p = projectile.Snapshot;
        var metadata = new JsonObject { ["spell"] = p.SpellName };
        switch (p.SpellName)
        {
            case "Incinerate" or "InfernalGuardian":
                metadata["origin"] = Value(p.Origin);
                metadata["direction"] = Value(p.Direction);
                break;
            // 19 October 2026: Shaco's Hallucinate is a script cast with a windup and no missile, queued under its own spell name.
            case "Disintegrate" or "HallucinateFull":
                metadata["cast_remaining"] = p.CastWindupRemaining;
                metadata["pos"] = Value(p.Position);
                metadata["speed"] = p.Speed;
                break;
            case "TwoShivPoison":
                metadata["rank"] = p.Rank;
                metadata["pos"] = Value(p.Position);
                metadata["speed"] = p.Speed;
                break;
            // 19 October 2026: Shaco runs from his shipped scripts, so Jack in the Box flies under its own spell name (ShacoBoxSpell was the hand-written flight's).
            case "ShacoBoxSpell" or "JackInTheBox":
                metadata["pos"] = Value(p.Position);
                metadata["speed"] = p.Speed;
                metadata["cast_info"] = Value(p.CastInfo);
                metadata["launched"] = p.Launched;
                break;
            // 19 October 2026: every other queued flight is a BuildingBlocks script flight (the hand-written flights are gone): its windup, rank,
            // position, speed and, when it has one, its cast packet are what the replay compares.
            default:
                metadata["rank"] = p.Rank;
                metadata["cast_remaining"] = p.CastWindupRemaining;
                metadata["pos"] = Value(p.Position);
                metadata["speed"] = p.Speed;
                if (p.CastInfo is not null) metadata["cast_info"] = Value(p.CastInfo);
                break;
        }
        if (!compact) return (JsonNode)new JsonArray(p.CastRemaining, p.CasterNetId, p.TargetNetId,
            p.Damage, p.ManaCost, p.Slot, p.PyromaniaArmed, p.MissileNetId, metadata);
        return new JsonObject { ["caster"] = p.CasterNetId, ["target"] = p.TargetNetId,
            ["slot"] = p.Slot, ["remaining"] = p.CastRemaining, ["damage"] = p.Damage,
            ["manaCost"] = p.ManaCost, ["pyromania"] = p.PyromaniaArmed,
            ["missile"] = p.MissileNetId, ["metadata"] = metadata };
    }).ToArray());

    // 19 October 2026: the hand-written Annie's private Q death markers are gone (the script's Disintegrate mark is an ordinary buff).
    public static JsonArray Markers(SpellSystem spells) => new();

    /// <summary>The numeric form of a recorded value: a plain number, or the round-trip text <see cref="Value"/> writes for a non-finite double.</summary>
    public static double Number(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? double.Parse(value.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture)
        : value.GetDouble();

    public static JsonNode? Value(object? value, JsonElement layout = default)
    {
        if (value is null) return null;
        if (value is Position2 point) return layout.ValueKind == JsonValueKind.Array && layout.GetArrayLength() == 3
            ? new JsonArray(point.X, 0, point.Z) : new JsonArray(point.X, point.Z);
        if (value is Position3 point3) return new JsonArray(point3.X, point3.Y, point3.Z);
        if (value is Entity entity) return new JsonObject { ["entity"] = entity.NetId };
        if (value is SpellCastInfo cast) return new JsonObject { ["bytes"] = Convert.ToHexString(SpellPackets.BuildCastInfo(cast)).ToLowerInvariant() };
        if (value is byte[] bytes) return new JsonObject { ["bytes"] = Convert.ToHexString(bytes).ToLowerInvariant() };
        if (value is Enum enumeration) return JsonValue.Create(Convert.ToInt32(enumeration));
        if (value is string text) return JsonValue.Create(text);
        if (value is HashSet<uint> ids) return new JsonArray(ids.Order().Select(id => JsonValue.Create(id)).ToArray());
        if (value is IDictionary dictionary)
        {
            var mapped = new JsonObject();
            foreach (DictionaryEntry pair in dictionary) mapped[Convert.ToString(pair.Key, CultureInfo.InvariantCulture)!] = Value(pair.Value);
            return mapped;
        }
        if (value is IEnumerable sequence)
        {
            var items = sequence.Cast<object?>().ToArray();
            return new JsonArray(items.Select((item, i) => Value(item, item is BuffState buff
                ? BuffLayout(layout, items, i, buff)
                : layout.ValueKind == JsonValueKind.Array && i < layout.GetArrayLength() ? layout[i] : default)).ToArray());
        }
        // A double JSON cannot spell. Entity.ExpiresAt is +Infinity for a pet whose life is its script buff's, not a
        // clock of its own (BBSpawnMinion's pet branch, batch 146; Yorick's ghouls take that branch since batch 164's
        // YorickSummon* spawn). The field is written as its round-trip text ("Infinity") and read back through Number().
        if (value is double nonFinite && !double.IsFinite(nonFinite))
            return JsonValue.Create(nonFinite.ToString("R", CultureInfo.InvariantCulture));
        if (value is float nonFiniteFloat && !float.IsFinite(nonFiniteFloat))
            return JsonValue.Create(((double)nonFiniteFloat).ToString("R", CultureInfo.InvariantCulture));
        if (value.GetType().IsPrimitive || value is decimal) return JsonSerializer.SerializeToNode(value);
        if (value is HeroItemState counters)
        {
            var result = new JsonObject();
            foreach (var property in typeof(HeroItemState).GetProperties())
            {
                var counter = property.GetValue(counters);
                if (counter is true || counter is int n && n != 0 || counter is double d && d != 0)
                    result[JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name)] = Value(counter);
            }
            return result;
        }
        if (value is BuffPayload payload)
        {
            var result = new JsonObject();
            var keys = layout.ValueKind == JsonValueKind.Object ? layout.EnumerateObject().Select(p => p.Name).ToList() : new List<string>();
            foreach (var field in Enum.GetValues<BuffPayloadField>())
                if (payload.Has(field)) keys.Add(JsonNamingPolicy.SnakeCaseLower.ConvertName(field.ToString()));
            // 19 October 2026: a recorded payload key that names a field of a hand-written module (janna_r_channel, vayne_r, yorick_ghoul_netid ...)
            // has no property any more; the scripts keep that state in buffs, so the column is dropped instead of recorded.
            foreach (var key in keys.Distinct())
                if (!MissingProperty(Property(payload, key, tolerant: true)))
                    result[key] = Value(Property(payload, key));
            return result;
        }
        var schema = layout.ValueKind == JsonValueKind.Object && layout.EnumerateObject().Any()
            ? layout.EnumerateObject().Select(field => field.Name).ToArray() : Schema(value.GetType().Name).Split(',');
        var obj = new JsonObject();
        foreach (var key in schema)
        {
            // Python-era bookkeeping with no current state member; no comparer reads it.
            if (UnreadLegacyColumn(value, key)) continue;
            var old = layout.ValueKind == JsonValueKind.Object && layout.TryGetProperty(key, out var found) ? found : default;
            var field = Property(value, key);
            obj[key] = Value(field, old);
        }
        return obj;
    }

    // Batch 208: a buff takes the recorded row of the same name (the same occurrence of that name), not the row at its position.
    // Batch 201 took the carriers out of the buff lists, so positions moved and the recorded key sets followed the wrong buffs.
    private static JsonElement BuffLayout(JsonElement layout, object?[] items, int index, BuffState buff)
    {
        if (layout.ValueKind != JsonValueKind.Array) return default;
        var occurrence = items.Take(index).Count(item => item is BuffState earlier && earlier.Name == buff.Name);
        foreach (var row in layout.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("name", out var name) || name.GetString() != buff.Name) continue;
            if (occurrence-- == 0) return row;
        }
        return default;
    }

    private static bool UnreadLegacyColumn(object value, string key) =>
        false;

    private static readonly object Missing = new();

    private static bool MissingProperty(object? value) => ReferenceEquals(value, Missing);

    private static object? Property(object value, string key, bool tolerant = false)
    {
        // 19 October 2026: Annie's Pyromania counter and pending summon left the entity (her scripts keep the passive in buffs and the summon
        // in the cast windup); the recorded columns stay in the layouts as constants.
        if (value is Entity && key == "pyro_stacks") return 0;
        // 19 October 2026: Orianna's passive target left the entity with her hand-written module (her scripts keep it in buffs); the recorded
        // column stays in the layouts as the constant 0 it always held.
        if (value is Entity && key == "orianna_passive_target") return 0u;
        if (value is Entity && key is "pendingSummon" or "pending_summon") return null;
        if (value.GetType().Name == "BlitzMissileState" && key == "spell") return "RocketGrabMissile";
        if (value.GetType().Name == "BlitzMissileState" && key == "slot") return 0;
        // 19 October 2026: Yorick runs from his shipped scripts; the zombie-life token has only a deadline (the phase is the YorickRADelay /
        // YorickRAZombie buff on the unit, the caster is the marker's). The recorded columns read as absent (re-record at the next full run).
        if (value is YorickRevivalState && key is "stage" or "caster") return null;
        var name = key switch
        {
            "hp" => "Health", "max_hp" when value is Entity => "MaxHealth", "max_mana" => "MaxMana", "atk_damage" => "AttackDamage",
            "atk_range" => "AttackRange", "atk_period" => "AttackPeriod", "windup" => "AttackWindup", "attack_cd" => "AttackCooldown",
            "exp" => "Experience", "hp_regen" when value is Entity => "HealthRegen", "mp_regen" when value is Entity => "ManaRegen",
            "xp_reward" => "ExperienceReward", "champion" or "champion_name" => "ChampionName",
            "pos" => "Position", "unitType" => "UnitType", "netid" => "NetId", "owner_netid" => "OwnerNetId",
            "pendingSummon" => "PendingSummon", "armor_pen_flat" when value is Entity => "ArmorPenetrationFlat",
            "armor_pen_percent" when value is Entity => "ArmorPenetrationPercent", "magic_pen_flat" when value is Entity => "MagicPenetrationFlat",
            "magic_pen_percent" when value is Entity => "MagicPenetrationPercent", "caster" => "CasterNetId",
            "target" when value is not Entity => "TargetNetId", "missile_id" => "MissileNetId", "model_id" => "ModelNetId",
            "object_id" => "ObjectNetId", "carrier_fx" => "CarrierEffectNetId", "fx_carrier" => "CarrierEffectTarget",
            "fx" => "EffectNetId", "q_missile_id" => "QMissileNetId", "pending_missile_id" => "PendingMissileNetId",
            "caster_pos" => "CasterPosition", "spawn_pos" => "SpawnPosition", "last" or "last_pos" => "LastPosition",
            "pyro_armed" => "PyromaniaArmed", "spell" => "SpellName", "amount" => "Damage",
            "initialized" => "PassiveInitialized",
            "silver_caster" => "SilverCasterNetId", "deceive_pos" => "DeceivePosition",
            "cast_info" when value.GetType().Name == "BlitzPendingCast" => "CommandCastInfo",
            _ => string.Concat(key.Split('_').Select(part => char.ToUpperInvariant(part[0]) + part[1..])),
        };
        if (value is BuffPayload buff && key == "spell_shield") return buff.MagicShield > 0;
        // Retired field of the recorded pet follow delay; production never set it.
        if (key == "pet_follow_since") return null;
        // Batch 149 step 4: the Odyn's Veil store left the entity (the window keeps it in the shipped
        // Spells/OdynsVeil.luaobj's own CharVars.StoredDamage); the recorded column stays in the layouts as 0.
        // The tibbers fixture spells the archived Python-era column in camelCase.
        if (value is Entity && key is "odyn_stored_damage" or "odynStoredDamage") return 0.0;
        var property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (property is null)
            return tolerant ? Missing : throw new InvalidDataException($"missing spell snapshot mapping {value.GetType().Name}.{key}");
        var result = property.GetValue(value);
        return (key is "stage" or "source_kind" or "damage_type") && result is Enum e
            ? JsonNamingPolicy.SnakeCaseLower.ConvertName(e.ToString()) : result;
    }

    private static string Schema(string type) => type switch
    {
        "BuffState" => "name,slot,type,expires_at,caster,payload,max_stack,stacks",
        "YorickRevivalState" => "stage,deadline,caster",
        "PendingCastState" => "request,deadline,last_move_point,next_move_retry_at",
        "SpellCastRequest" => "slot,is_summoner,position,end_position,target_netid",
        "ForcedMoveState" => "destination,speed,remaining,source_netid,effect,first_collision,completed",
        "OriannaBallState" => "pos,attached,destination,rank,hit,multiplier,carrier,return_target,defended,command_lock,wave,shockwave,model_id,object_id,rendered,offset_target,teleport_id,carrier_fx,fx_carrier,return_remaining,missile_id",
        "OriannaWaveState" => "pos,remaining,elapsed,fx",
        "OriannaShockwaveState" => "remaining,rank,center,kind",
        "BlitzPendingCast" => "remaining,slot,rank,aim,origin,missile_id,cast_info",
        "BlitzcrankState" => "pending,missiles,passive_elapsed,aura_active,initialized",
        "BlitzMissileState" => "netid,rank,pos,end,direction,speed,width,cast_info,caster_pos,hit_ids",
        _ => throw new InvalidDataException("missing spell snapshot schema " + type),
    };
}
