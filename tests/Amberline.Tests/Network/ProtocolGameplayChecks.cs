using System.Text.Json;
using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Tests.Network;

public static class ProtocolGameplayChecks
{


    [Check("protocol.gameplay-metadata")]
    public static void MetadataAndNumericBoundaries()
    {
        var info = new SpellCastInfo(1, 2, 1, 3, 4,
            new Position3(1, 2, 3), new Position3(1, 2, 3),
            new[] { new SpellCastTarget(5, new Position3(1, 2, 3), 0) },
            1, 2, 0, new Position3(1, 2, 3));
        var missile = SpellPackets.MissileReplication(3,
            new Position3(1, 2, 3), new Position3(1, 2, 3), new Position3(1, 0, 0),
            new Position3(1, 0, 0), new Position3(1, 2, 3), new Position3(1, 2, 3),
            new Position3(1, 2, 3), 1, 0, 0, info);
        TestAssert.Equal(new LeaguePacketEntityReference(LeaguePacketEntityRole.Hero, 3), missile.EntityReferences[0]);
        TestAssert.Equal(new LeaguePacketEntityReference(LeaguePacketEntityRole.Create, 4), missile.EntityReferences[1]);
        TestAssert.Equal(new LeaguePacketEntityReference(LeaguePacketEntityRole.Target, 5), missile.EntityReferences[2]);

        var fx = FxPackets.CreateGroup(1, 4, new Position2(10, 20),
            new NavigationBounds(0, 0, 0, 0), (_, _) => 3,
            targetNetId: 5, bindNetId: 6, headerNetId: 7);
        TestAssert.True(fx.EntityReferences.Contains(new(LeaguePacketEntityRole.World, 7)));
        TestAssert.True(fx.EntityReferences.Contains(new(LeaguePacketEntityRole.Target, 5)));
        TestAssert.True(fx.EntityReferences.Contains(new(LeaguePacketEntityRole.Create, 4)));
        TestAssert.True(fx.EntityReferences.Contains(new(LeaguePacketEntityRole.Binding, 6)));
        TestAssert.True(FxPackets.Kill(4).EntityReferences.Contains(new(LeaguePacketEntityRole.Effect, 4)));

        var baseZero = CombatPackets.HeroStats(1,
            new HeroStatsSnapshot(AttackDamage: 10, BaseAttackDamage: 0, MaxHp: 1, MaxMp: 1, MoveSpeed: 1), 1);
        TestAssert.Equal(0.0f, BitConverter.ToSingle(baseZero.Plaintext.Span.Slice(71, 4)));
        var rounded = CombatPackets.BasicAttack(1, 2, 3, extraTimeSeconds: 0.575d);
        TestAssert.Equal((byte)0xB9, rounded.Plaintext.Span[9]);
        TestAssert.Throws<ArgumentOutOfRangeException>(
            () => VisionPackets.SetCameraPosition(1, new Position3(double.PositiveInfinity, 0, 0)));
    }

    private static IReadOnlyList<PlaintextPacket> BuildCase(JsonElement testCase, NavigationBounds bounds)
    {
        var function = testCase.GetProperty("function").GetString()!;
        var args = testCase.GetProperty("args").EnumerateArray().ToArray();
        var kwargs = testCase.GetProperty("kwargs");
        switch (function)
        {
            case "send_start_spawn": return One(SpawnPackets.StartSpawn());
            case "send_end_spawn": return One(SpawnPackets.EndSpawn());
            case "send_start_game": return One(SpawnPackets.StartGame());
            case "send_game_clock": return One(SpawnPackets.GameClock(args.Length == 0 ? 0 : args[0].GetSingle()));
            case "send_switch_nexuses_to_idle_particles": return One(SpawnPackets.SwitchNexusesToIdleParticles());
            case "send_toggle_fow": return One(SpawnPackets.ToggleFow());
            case "send_create_hero": return One(SpawnPackets.CreateHero(args[0].GetUInt32(), args[1].GetUInt64(), args[2].GetString()!, args[3].GetString()!, args[4].GetBoolean(), (int)UInt32(kwargs, "skin_id"), (byte)UInt32(kwargs, "skill_level", 1), (byte)UInt32(kwargs, "spawn_pos_index"), (byte)UInt32(kwargs, "net_node_id", 1)));
            case "send_create_turret": return One(SpawnPackets.CreateTurret(args[0].GetUInt32(), (byte)args[1].GetInt32(), args[2].GetString()!));
            case "send_all_turrets": return SpawnPackets.AllTurrets(args[0].EnumerateArray().Select(item => item.GetUInt32()).ToArray());
            case "send_spawn_minion":
            {
                 var minionPosition = Position2Of(args[2]);
                 return One(SpawnPackets.SpawnMinion(new SpawnMinionSnapshot(args[0].GetUInt32(), (byte)args[1].GetInt32(), minionPosition, args[3].GetInt32(), args[4].GetString()!, args[5].GetString()!, Bool(kwargs, "is_ward"), Bool(kwargs, "ignore_collision")), KnownHeight(minionPosition.X, minionPosition.Z)));
            }
            case "send_spawn_pet":
            {
                 var petPosition = Position2Of(args[3]);
                 var pet = new SpawnPetSnapshot(args[0].GetUInt32(), args[1].GetUInt32(), (byte)args[2].GetInt32(), petPosition, args[4].GetString()!, args[5].GetString()!, UInt32(kwargs, "clone_netid"), Int32(kwargs, "skin_id"), Int32(kwargs, "rank", 1), Single(kwargs, "duration"), Int32(kwargs, "damage_bonus"), Int32(kwargs, "health_bonus"), String(kwargs, "buff_name"), Bool(kwargs, "copy_inventory", true), Bool(kwargs, "clear_focus_target", true), String(kwargs, "ai_script", "Pet.lua"), Bool(kwargs, "show_minimap_icon"));
                 return One(SpawnPackets.SpawnPet(pet, KnownHeight(petPosition.X, petPosition.Z)));
            }
            case "send_create_neutral": return One(SpawnPackets.CreateNeutral(new NeutralSpawnSnapshot(args[0].GetUInt32(), (byte)args[1].GetInt32(), Position3Of(args[2]), Position3Of(args[3]), Position3Of(args[4]), args[5].GetString()!, args[6].GetString()!, args[7].GetString()!, args[8].GetString()!, args[9].GetInt32(), args[10].GetInt32(), args[11].GetInt32(), args[12].GetInt32(), args[13].GetInt32(), Bool(kwargs, "behavior_tree"))));
            case "send_neutral_camp_empty": return One(SpawnPackets.NeutralCampEmpty(args[0].GetUInt32(), args[1].GetInt32(), args[2].GetBoolean()));
            case "send_add_perception_bubble": return One(SpawnPackets.AddPerceptionBubble(new PerceptionBubble(args[0].GetUInt32(), Position3Of(args[1]), args[2].GetSingle(), args[3].GetSingle(), args[4].GetUInt32(), UInt32(kwargs, "client_netid"), UInt32(kwargs, "flags"))));
            case "send_add_unit_perception_bubble": return One(SpawnPackets.AddUnitPerceptionBubble(args[0].GetUInt32(), args[1].GetSingle(), args[2].GetSingle(), args[3].GetUInt32(), UInt32(kwargs, "bubble_type"), UInt32(kwargs, "client_netid"), UInt32(kwargs, "flags")));
            case "send_remove_perception_bubble": return One(SpawnPackets.RemovePerceptionBubble(args[0].GetUInt32()));
            case "send_set_local_client_id": return One(SpawnPackets.SetLocalClientId(args[0].GetUInt32()));

            case "send_set_camera_position": { var camera = Position2Of(args[1]); return One(VisionPackets.SetCameraPosition(args[0].GetUInt32(), new Position3(camera.X, 0.0, camera.Z))); }
            case "send_change_character_data": return One(VisionPackets.ChangeCharacterData(args[0].GetUInt32(), args[1].GetString()!, args[2].GetBoolean(), args[3].GetUInt32()));
            case "send_avatar_info": return One(VisionPackets.AvatarInfo(args[0].GetUInt32()));
            case "send_waypoint_list": return One(VisionPackets.WaypointList(args[0].GetUInt32(), args[1].EnumerateArray().Select(Position2Of).ToArray(), UInt32(kwargs, "sync_id", 1)));
            case "send_dash": return One(VisionPackets.Dash(args[0].GetUInt32(), Position2Of(args[1]), Position2Of(args[2]), args[3].GetSingle(), bounds, UInt32(kwargs, "sync_id", 1), UInt32(kwargs, "follow_netid"), Single(kwargs, "follow_distance"), Single(kwargs, "gravity")));
            case "send_teleport": return One(VisionPackets.Teleport(args[0].GetUInt32(), Position2Of(args[1]), args[2].GetInt32(), bounds, Int32(kwargs, "sync_id", 1), kwargs.TryGetProperty("staging", out var staging) ? Position2Of(staging) : null));
            case "send_enter_local_visibility": return One(VisionPackets.EnterLocalVisibility(args[0].GetUInt32(), args[1].GetSingle(), args[2].GetSingle()));
            case "send_leave_local_visibility": return One(VisionPackets.LeaveLocalVisibility(args[0].GetUInt32()));
             case "send_enter_visibility_client": return One(VisionPackets.EnterVisibilityClient(args[0].GetUInt32(), String(kwargs, "skin_name"), (int)UInt32(kwargs, "skin_id"), Single(kwargs, "max_health"), Single(kwargs, "health"), kwargs.TryGetProperty("pos", out var clientPosition) ? Position2Of(clientPosition) : null, kwargs.TryGetProperty("facing", out var clientFacing) ? Position2Of(clientFacing) : null, null, Bool(kwargs, "is_hero")));
            case "send_enter_visibility": return VisionPackets.EnterVisibility(args[0].GetUInt32(), args[1].GetString()!, args[2].GetInt32(), args[3].GetSingle(), args[4].GetSingle(), kwargs.TryGetProperty("pos", out var enterPosition) ? Position2Of(enterPosition) : null, kwargs.TryGetProperty("facing", out var enterFacing) ? Position2Of(enterFacing) : null);
            case "send_leave_visibility": return VisionPackets.LeaveVisibility(args[0].GetUInt32());
            case "send_enter_team_visibility": return One(VisionPackets.EnterTeamVisibility(args[0].GetUInt32(), (byte)args[1].GetInt32()));
            case "send_leave_team_visibility": return One(VisionPackets.LeaveTeamVisibility(args[0].GetUInt32(), (byte)args[1].GetInt32()));

            case "send_character_fade": return One(SpellPackets.CharacterFade(args[0].GetUInt32(), args[1].GetInt16(), args.Length > 2 ? args[2].GetSingle() : null, args.Length > 3 ? args[3].GetSingle() : 0));
            case "send_face_direction": return One(SpellPackets.FaceDirection(args[0].GetUInt32(), Position3Of(args[1])));
            case "send_push_character_data": return One(SpellPackets.PushCharacterData(args[0].GetUInt32(), args[1].GetUInt32(), args[2].GetString()!, Bool(kwargs, "use_spells")));
            case "send_pop_character_data": return One(SpellPackets.PopCharacterData(args[0].GetUInt32(), args[1].GetUInt32()));
            case "send_spell_offset_target": return One(SpellPackets.SpellOffsetTarget(args[0].GetUInt32(), (byte)args[1].GetInt32(), args[2].GetUInt32(), Bool(kwargs, "summoner")));
            case "send_pause_animation": return One(SpellPackets.PauseAnimation(args[0].GetUInt32(), args[1].GetBoolean()));
            case "send_spell_tooltip_vars": return One(SpellPackets.SpellTooltipVariables(args[1].EnumerateArray().Select(item => new SpellTooltipEntry((byte)item[0].GetInt32(), item[1][0].GetSingle(), item[1][1].GetSingle(), item[1][2].GetSingle())).ToArray(), args[0].GetUInt32()));
            case "send_cast_spell_ans": return One(SpellPackets.CastSpellAnswer(args[0].GetUInt32(), Hex(args[1], "hex"), Int32(kwargs, "caster_pos_sync_id")));
            case "send_missile_replication": return One(SpellPackets.MissileReplication(args[0].GetUInt32(), Position3Of(args[1]), Position3Of(args[2]), Position3Of(args[3]), Position3Of(args[4]), Position3Of(args[5]), Position3Of(args[6]), Position3Of(args[7]), args[8].GetSingle(), args[9].GetSingle(), (byte)args[10].GetInt32(), Hex(args[11], "hex")));
            case "send_upgrade_spell_ans": return One(SpellPackets.UpgradeSpellAnswer(args[0].GetUInt32(), (byte)args[1].GetInt32(), (byte)args[2].GetInt32(), (byte)args[3].GetInt32()));
            case "send_set_cooldown": return One(SpellPackets.SetCooldown(args[0].GetUInt32(), (byte)args[1].GetInt32(), args[2].GetSingle(), args[3].GetBoolean()));
            case "send_set_spell_data": return One(SpellPackets.SetSpellData(args[0].GetUInt32(), args[1].GetUInt32(), (byte)args[2].GetInt32()));
            case "send_destroy_missile": return One(SpellPackets.DestroyMissile(args[0].GetUInt32()));
            case "send_modify_shield": return One(SpellPackets.ModifyShield(args[0].GetUInt32(), args[1].GetSingle(), Bool(kwargs, "physical", true), Bool(kwargs, "magic", true), Bool(kwargs, "no_fade")));
            case "send_buff_add": return One(SpellPackets.AddBuff(new BuffAdd(args[0].GetUInt32(), (byte)args[1].GetInt32(), (byte)args[2].GetInt32(), args[3].GetUInt32(), args[4].GetSingle(), args[5].GetUInt32(), (byte)UInt32(kwargs, "count", 1), (byte)UInt32(kwargs, "is_hidden"), Single(kwargs, "running_time"))));
            case "send_buff_remove": return One(SpellPackets.RemoveBuff(args[0].GetUInt32(), (byte)args[1].GetInt32(), args[2].GetUInt32()));

            case "send_basic_attack": return One(CombatPackets.BasicAttack(args[0].GetUInt32(), args[1].GetUInt32(), args[2].GetUInt32(), (byte)UInt32(kwargs, "slot"), Double(kwargs, "extra_time_secs")));
            // The oracle vector carries the flags byte 0x12 as an arbitrary value: the builder writes whatever it is given.
            case "send_npc_instant_stop_attack": return One(CombatPackets.NpcInstantStopAttack(args[0].GetUInt32(), 0x12));
            case "send_apply_damage": return One(CombatPackets.ApplyDamage(args[0].GetUInt32(), args[1].GetUInt32(), args[2].GetSingle(), (byte)UInt32(kwargs, "type_byte", 0x84)));
            case "send_unit_add_gold": return One(CombatPackets.UnitAddGold(args[0].GetUInt32(), args[1].GetUInt32(), args[2].GetSingle()));
            case "send_npc_level_up": return One(CombatPackets.NpcLevelUp(args[0].GetUInt32(), (byte)args[1].GetInt32(), (byte)args[2].GetInt32()));
            case "send_replication_hp": return One(CombatPackets.ReplicationHp(args[0].GetUInt32(), args[1].GetSingle(), 1, Bool(kwargs, "is_hero"), Bool(kwargs, "is_turret")));
            case "send_hero_gold_exp": return One(CombatPackets.HeroGoldExp(args[0].GetUInt32(), args[1].GetSingle(), args[2].GetSingle()));
            case "send_hero_stats": return One(CombatPackets.HeroStats(args[0].GetUInt32(), HeroStats(args[1]), 1, NullableSingle(kwargs, "current_hp"), NullableSingle(kwargs, "current_mp"), UInt32(kwargs, "level", 1), Single(kwargs, "gold"), Single(kwargs, "exp"), kwargs.TryGetProperty("mana_costs", out var mana) ? mana.EnumerateArray().Select(item => item.GetSingle()).ToArray() : null));
            case "send_minion_stats": return One(CombatPackets.MinionStats(args[0].GetUInt32(), MinionStats(args[1]), 1));
             case "send_turret_stats": return One(CombatPackets.TurretStats(args[0].GetUInt32(), args[1].GetSingle(), args[2].GetSingle(), NullableSingle(kwargs, "max_hp"), Single(kwargs, "armor", 30), Single(kwargs, "spell_block", 30), Single(kwargs, "hp_regen"), Bool(kwargs, "protected"), 1));
            case "send_turret_protection_flags": return One(CombatPackets.TurretProtectionFlags(args[0].GetUInt32(), args[1].GetUInt32(), 1, UInt32(kwargs, "invulnerable"), UInt32(kwargs, "targetable", 1)));
            case "send_building_stats": return One(CombatPackets.BuildingStats(args[0].GetUInt32(), args[1].GetSingle(), args[2].GetUInt32(), UInt32(kwargs, "invulnerable"), UInt32(kwargs, "targetable", 1), 1));
            case "send_dampener_state": return One(CombatPackets.DampenerState(args[0].GetUInt32(), args[1].GetSingle(), args[2].GetBoolean()));
            case "send_ai_set_target": return One(CombatPackets.AiSetTarget(args[0].GetUInt32(), args[1].GetUInt32()));
            case "send_ai_set_target_hero": return One(CombatPackets.AiSetTargetHero(args[0].GetUInt32(), args[1].GetUInt32()));
            case "send_npc_die": return One(CombatPackets.NpcDie(args[0].GetUInt32(), args[1].GetUInt32(), Single(kwargs, "death_duration", 8), (byte)UInt32(kwargs, "become_zombie"), (byte)UInt32(kwargs, "dtype"), (byte)UInt32(kwargs, "stype"), (byte)UInt32(kwargs, "opcode", 0xA6)));
            case "send_force_dead": return One(CombatPackets.ForceDead(args[0].GetUInt32()));
            case "send_building_die": return One(CombatPackets.BuildingDie(args[0].GetUInt32(), args[1].GetUInt32()));
            case "send_on_event_world": return One(CombatPackets.OnEventWorld((byte)args[0].GetInt32(), args[1].GetUInt32(), args.Length > 2 ? args[2].GetUInt32() : 0, kwargs.TryGetProperty("map_number", out var map) ? map.GetInt32() : null));
            case "send_hero_reincarnate": { var respawn = Position2Of(args[1]); return One(CombatPackets.HeroReincarnate(args[0].GetUInt32(), new Position3(respawn.X, 0.0, respawn.Z))); }
            case "send_move_camera_to_point": return One(CombatPackets.MoveCameraToPoint(args[0].GetUInt32(), Position3Of(args[1]), args[2].GetSingle()));
            case "send_disable_hud_for_end_of_game": return One(CombatPackets.DisableHudForEndOfGame());
            case "send_end_of_game_event": return One(CombatPackets.EndOfGameEvent(args[0].GetBoolean()));
            case "send_end_game": return One(CombatPackets.EndGame(args[0].GetBoolean(), args[1].GetBoolean()));
            default: throw new InvalidOperationException("Fixture function is not dispatched: " + function);
        }
    }

    private static SpellCastInfo CastInfo(JsonElement[] args, JsonElement kwargs, SpellCastTarget[] targets)
        => new(args[0].GetUInt32(), args[1].GetUInt32(), args[2].GetInt32(), args[3].GetUInt32(), args[4].GetUInt32(), Position3Of(args[5]), Position3Of(args[6]), targets, args[8].GetSingle(), args[9].GetSingle(), args[10].GetByte(), Position3Of(args[11]), Single(kwargs, "attack_speed_modifier", 1), Single(kwargs, "designer_cast_time"), Single(kwargs, "extra_cast_time"), Single(kwargs, "designer_total_time"), Single(kwargs, "start_cast_time"), (byte)UInt32(kwargs, "flags"));

    private static HeroStatsSnapshot HeroStats(JsonElement value)
        => new(Float(value, "attack_damage"), Float(value, "armor"), Float(value, "spell_block"), Float(value, "hp_regen"), Float(value, "mp_regen"), Float(value, "attack_range"), Float(value, "attack_speed_mod"), Float(value, "max_hp"), Float(value, "max_mp"), Float(value, "move_speed"), value.TryGetProperty("base_attack_damage", out var baseAttack) ? baseAttack.GetSingle() : null, Float(value, "dodge"), Float(value, "crit_chance"), Float(value, "bonus_attack_damage"), Float(value, "ability_power"), Float(value, "cooldown_mod"), Float(value, "armor_pen_flat"), Float(value, "armor_pen_percent"), Float(value, "magic_pen_flat"), Float(value, "magic_pen_percent"), Float(value, "life_steal"), Float(value, "spell_vamp"), UInt32(value, "action_state", 7), UInt32(value, "is_targetable", 1), UInt32(value, "can_cast_bits1", 0xFFFFFFFF), UInt32(value, "can_cast_bits2", 0xFFFFFFFF));

    private static MinionStatsSnapshot MinionStats(JsonElement value)
        => new(Float(value, "hp"), Float(value, "atk_damage"), Float(value, "speed"), Float(value, "armor"), Float(value, "spell_block"), Float(value, "attack_speed_mod", 1), Float(value, "hp_regen"), UInt32(value, "action_state", 7), UInt32(value, "magic_immune"), UInt32(value, "invulnerable"), UInt32(value, "targetable", 1));

    private static IReadOnlyList<PlaintextPacket> One(PlaintextPacket packet) => new[] { packet };
    private static Position2 Position2Of(JsonElement value) => new(value[0].GetDouble(), value[1].GetDouble());
    private static Position3 Position3Of(JsonElement value) => new(value[0].GetDouble(), value[1].GetDouble(), value[2].GetDouble());
    private static byte[] Hex(JsonElement value, string property) => Convert.FromHexString(value.GetProperty(property).GetString()!);
    private static string String(JsonElement value, string property, string fallback = "") => value.TryGetProperty(property, out var result) ? result.GetString()! : fallback;
    private static bool Bool(JsonElement value, string property, bool fallback = false) => value.TryGetProperty(property, out var result) ? result.GetBoolean() : fallback;
    private static float Single(JsonElement value, string property, float fallback = 0) => value.TryGetProperty(property, out var result) ? result.GetSingle() : fallback;
    private static double Double(JsonElement value, string property, double fallback = 0) => value.TryGetProperty(property, out var result) ? result.GetDouble() : fallback;
    private static float? NullableSingle(JsonElement value, string property) => value.TryGetProperty(property, out var result) ? result.GetSingle() : null;
    private static int Int32(JsonElement value, string property, int fallback = 0) => value.TryGetProperty(property, out var result) ? result.GetInt32() : fallback;
    private static uint UInt32(JsonElement value, string property, uint fallback = 0) => value.TryGetProperty(property, out var result) ? result.GetUInt32() : fallback;
    private static float Float(JsonElement value, string property, float fallback = 0) => value.TryGetProperty(property, out var result) ? result.GetSingle() : fallback;

    private static double KnownHeight(double x, double z)
    {
        if (Math.Abs(x - 7300.125) < 1e-6 && Math.Abs(z - 7000.75) < 1e-6) return 45.43413162231445;
        if (Math.Abs(x - 7450.875) < 1e-6 && Math.Abs(z - 7160.25) < 1e-6) return 45.32799530029297;
        throw new InvalidOperationException("No fixture height is known for (" + x + ", " + z + ")");
    }

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
