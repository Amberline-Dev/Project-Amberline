using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;

namespace Amberline.Simulation.BehaviorTrees;

/// <summary>
/// The facing the Dominion props send for raw 22. Production sends <see cref="Z"/> on the Crystal Scar; the other two are the DIAGNOSTICS
/// seam of <see cref="DominionProps.EnvironmentVariable"/>.
/// </summary>
public enum DominionPropFacing
{
    /// <summary>No props: every map but the Crystal Scar, and the seam's values other than <c>z</c> and <c>0</c> (DIAGNOSTICS).</summary>
    Off,

    /// <summary>Production: raw 22 = (0, 0, 1).</summary>
    Z,

    /// <summary>DIAGNOSTICS: the seam's value <c>0</c>, raw 22 = (0, 0, 0).</summary>
    Zero,
}

/// <summary>
/// The Dominion (map 8) stairs and nexus crystals, their clips, the lasers and lit cores between them, and the nexus fall. They are
/// production: on by default on the Crystal Scar since 29 October 2026 (the owner plays them). The stairs' creates are sent once per
/// client with the spawn, by name at their scene spawner; the clips and the release's raise follow the opening ceremony. Batch 491 made the
/// two nexus crystals props again (<see cref="SpawnPackets"/>), each with its gem particle (<see cref="CrystalStandPackets"/>). Batch 523
/// made the lasers team-coloured per viewer. Reconstruction: the packet layouts and clip timings are inferred from the shipped scene and
/// clips, and the batch records hold the evidence.
/// </summary>
public static class DominionProps
{
    /// <summary>
    /// DIAGNOSTICS: the environment seam of the props (<c>AMBERLINE_ODIN_PROPS</c>). Unset (or <c>z</c>) is production. <c>0</c> probes the
    /// facing (raw 22 = (0, 0, 0)), and any other value turns the props off. It is kept because the check
    /// <c>network.odin-props-default-on-and-the-off-switch</c> pins the parse.
    /// </summary>
    public const string EnvironmentVariable = "AMBERLINE_ODIN_PROPS";

    /// <summary>The team id of the stairs (neutral; the client masks it with 0x1ff).</summary>
    public const uint NeutralTeam = 300;

    /// <summary>Our map node id (the 0x80 spawn and the map objects send it, <c>SessionSpawnService.MapNetNodeId</c>).</summary>
    public const byte NodeId = 0x40;

    /// <summary>The stairs' scene spawners. The client compares the names case-insensitively.</summary>
    public const string OrderStairsName = "LevelProp_Odin_SoG_Order";
    public const string ChaosStairsName = "LevelProp_Odin_SoG_Chaos";
    public const string OrderStairsProp = "Odin_SoG_Order";
    public const string ChaosStairsProp = "Odin_SoG_Chaos";

    /// <summary>The stairs' scene centre points (<c>LEVELS/Map8/Scene/LevelProp_Odin_SoG_{Order,Chaos}.sco</c>, CentralPoint).</summary>
    public static readonly Position3 OrderStairsPosition = new(844.5499, -104.5178, 4281.7783);
    public static readonly Position3 ChaosStairsPosition = new(13045.8652, -104.5178, 4306.8452);

    /// <summary>
    /// The fresh netids of the stairs (the 0xD9 header netid and the 0xDA key). The seed strings are hashed into the netid, so they keep
    /// their batch 477 spelling: changing one would change the wire.
    /// </summary>
    public static readonly uint OrderStairsNetId = MapDefinitions.BuildingNetId("DIAGNOSTICS_OdinExperiment_OrderStairs");
    public static readonly uint ChaosStairsNetId = MapDefinitions.BuildingNetId("DIAGNOSTICS_OdinExperiment_ChaosStairs");

    /// <summary>The animation the stairs play at the release (raw 5 of 0xDA).</summary>
    public const string ReleaseAnimation = "Close1";

    /// <summary>
    /// DIAGNOSTICS: parses the seam's value. Unset, empty or <c>z</c> is production's facing (0, 0, 1); <c>0</c> is (0, 0, 0); anything else
    /// is off. Production's default has been on since 29 October (two native runs: the client created the props and raised no assert).
    /// </summary>
    public static DominionPropFacing FromValue(string? value) => value switch
    {
        null or "" or "z" => DominionPropFacing.Z,
        "0" => DominionPropFacing.Zero,
        _ => DominionPropFacing.Off,
    };

    /// <summary>
    /// The facing of the running process, read at each call by the production hooks. Off on every map but the Crystal Scar; on the Crystal
    /// Scar it is <see cref="FromValue"/> of the seam (unset, so production's <see cref="DominionPropFacing.Z"/>).
    /// </summary>
    public static DominionPropFacing FromEnvironment() => MapDefinitions.OnCrystalScar
        ? FromValue(Environment.GetEnvironmentVariable(EnvironmentVariable))
        : DominionPropFacing.Off;

    /// <summary>The raw 22 vector for a mode.</summary>
    public static Position3 FacingVector(DominionPropFacing mode) => mode switch
    {
        DominionPropFacing.Z => new Position3(0.0, 0.0, 1.0),
        _ => new Position3(0.0, 0.0, 0.0),
    };

    /// <summary>
    /// The crystals' scene height: the Y of each crystal's centre, the <c>.sco</c> CentralPoint Y (-218.6098) plus the ObjectCFG
    /// <c>Move</c> Y (277.7778 Order, 222.2222 Chaos), i.e. 59.1680 and 3.6124. Those are the nexus centres of the batch 447 notes.
    /// </summary>
    public static double CrystalSceneHeight(Team team) => team == Team.Order ? 59.1680 : 3.6124;

    /// <summary>
    /// The spawn-time creates for one client, all branch 1 (by name at their scene spawner): the two stairs, then the two nexus crystals.
    /// Empty when the mode is off. The caller passes the mode from <see cref="FromEnvironment"/>, which is off on every map but the Crystal Scar.
    /// Batch 491 (Reconstruction): the crystals are props again, as the scene names them (<c>LEVELS/Map8/Scene/LevelProp_Odin_SOG_*_Crystal.sco</c>
    /// with their ObjectCFG Move and Rot, the same kind of spawner as the stairs). Branch 1 makes a LevelPropGameObject at the spawner and
    /// raises no assert (owner games of batches 483 to 486). The crystal character's skin is only the dark roots and four buried shards
    /// (batch 491 decode: the skeleton's 13-entry influence list maps the skin to shards 10, 11, 12, 35 and roots 1 to 10), so the prop
    /// alone shows no crystal; the gem is a particle (<see cref="CrystalStandPackets"/>).
    /// </summary>
    public static IReadOnlyList<PlaintextPacket> SpawnPackets(DominionPropFacing mode)
    {
        if (mode == DominionPropFacing.Off) return Array.Empty<PlaintextPacket>();
        var facing = FacingVector(mode);
        var zero = new Position3(0.0, 0.0, 0.0);
        var packets = new List<PlaintextPacket>(4)
        {
            LevelPropPackets.Create(OrderStairsNetId, NodeId, OrderStairsPosition, facing, zero, NeutralTeam, 1, 0,
                OrderStairsName, OrderStairsProp),
            LevelPropPackets.Create(ChaosStairsNetId, NodeId, ChaosStairsPosition, facing, zero, NeutralTeam, 1, 0,
                ChaosStairsName, ChaosStairsProp),
        };
        // Raw 10 is only the fallback when no spawner of that name exists (the spawner's base plus its Move is the same point, batch 486).
        foreach (var nexus in MapDefinitions.CrystalScarNexusList)
            packets.Add(LevelPropPackets.Create(CrystalNetId(nexus.Team), NodeId,
                new Position3(nexus.Position.X, CrystalSceneHeight(nexus.Team), nexus.Position.Z), facing, zero,
                (uint)TeamMapping.ToNative(nexus.Team), 1, 0, nexus.Name, CrystalSkin(nexus.Team)));
        return packets;
    }

    /// <summary>The crystal prop's character (the 0xD9 PropName): the shipped folder <c>DATA/Characters/Odin_SOG_{Order,Chaos}_Crystal</c>.</summary>
    public static string CrystalSkin(Team team) => team == Team.Order ? "Odin_SOG_Order_Crystal" : "Odin_SOG_Chaos_Crystal";

    /// <summary>
    /// Batch 491: the crystal props' own netids (the 0xD9 header netid, the 0xDA key and the gem's bind). Not the nexus building's netid, so the
    /// nexus stats (0xCC) and its death (0x8E) never name a level prop.
    /// </summary>
    public static readonly uint OrderCrystalNetId = MapDefinitions.BuildingNetId("OdinNexusCrystal_Order");
    public static readonly uint ChaosCrystalNetId = MapDefinitions.BuildingNetId("OdinNexusCrystal_Chaos");
    public static uint CrystalNetId(Team team) => team == Team.Order ? OrderCrystalNetId : ChaosCrystalNetId;

    /// <summary>Batch 491: the netids of the two gem effects (kept so the nexus fall can kill them).</summary>
    public static uint CrystalGemNetId(Team team) => MapDefinitions.BuildingNetId(team == Team.Order ? "OdinNexusGem_Order" : "OdinNexusGem_Chaos");

    /// <summary>
    /// Batch 491: the gem particle. <c>DATA/Particles/Odin_Crystal_blue.troybin</c> and <c>Odin_Crystal_purple.troybin</c> draw the crystal
    /// itself: emitters gem, gem_low, gem_refract and gem_sparkle, mesh <c>Nexus_Crystal.sco</c> (388 x 1095 x 386 units) with the
    /// <c>Dominion_Crystal_Blue32</c> / <c>Purple32</c> texture, life 100000 s, spinning 30 degrees a second. Blue is Order, purple is Chaos
    /// (the crystals' own AnimEffects use the same colours: Odin_Crystal_Cracked_blue on Order, _purple on Chaos).
    /// </summary>
    public static string CrystalGemEffect(Team team) => team == Team.Order ? "Odin_Crystal_blue" : "Odin_Crystal_purple";

    /// <summary>
    /// Batch 491: the bone that carries the gem. No skin vertex is bound to it; it is the bone the crystal's own AnimEffects put the cracked
    /// gem on at the Death (<c>crystal_core</c> for Order, <c>chaos_root</c> for Chaos), and it is the only bone the Raise moves
    /// (117.7 lowered, 312.1 in Raised_Idle frame 0, model Y). The client hashes bone names case-insensitively (the AnimEffects writes
    /// <c>chaos_root</c> for the skeleton's <c>Chaos_root</c>).
    /// </summary>
    public static string CrystalGemBone(Team team) => team == Team.Order ? "crystal_core" : "Chaos_root";

    /// <summary>Batch 491: <see cref="CrystalGemBone"/>'s model height in <c>Crystal_nexus_idle</c> frame 0 (both crystals).</summary>
    public const double RaisedCoreHeight = 312.1;

    /// <summary>
    /// Reconstruction (batch 491): what stands a crystal up, sent once per client right after <see cref="SpawnPackets"/> (same channel, so
    /// each prop exists first). For each crystal:
    /// <list type="number">
    /// <item><c>Raised_Idle</c> held from the spawn (batch 486's hold: start <see cref="SpawnHoldStart"/>, duration <see cref="HoldSeconds"/>,
    /// int speed 0). The prop is created in Idle1, the lowered pose.</item>
    /// <item>The gem effect (0x8C) bound to the prop's netid and its gem bone, so it rides the bone. The client finds the bind object in the
    /// object manager by its netid (the bind lookup matches the object's netid field; branch 1 sets it through the spawn helper and
    /// the second helper). No target netid: the client moves a target object to the packet's owner position (its move routine,
    /// its position slot). The packet position is the raised gem point, which the client uses if the bind fails.</item>
    /// </list>
    /// Which retail code spawned the gem is not recovered: no shipped script or inibin names <c>Odin_Crystal_blue</c> (only the stairs'
    /// AnimEffects, commented out, on their <c>center_crystal</c> bone at Close1 with k:0). The stand from the spawn follows the owner's
    /// retail picture (a big crystal in each base from the start).
    /// Batch 725: a crystal whose nexus has fallen (<paramref name="fallen"/>, a client that returns after the fall) gets neither: its gem was
    /// killed at the fall, and its fallen clip follows in the reconnect burst (BehaviorTreeQuestHost.ReconnectState).
    /// </summary>
    public static IReadOnlyList<PlaintextPacket> CrystalStandPackets(NavigationBounds bounds, Func<Team, bool> fallen)
    {
        var packets = new List<PlaintextPacket>(4);
        foreach (var nexus in MapDefinitions.CrystalScarNexusList.Where(nexus => !fallen(nexus.Team)))
        {
            var team = nexus.Team;
            var gemHeight = CrystalSceneHeight(team) + RaisedCoreHeight;
            packets.Add(LevelPropPackets.Animation(CrystalNetId(team), "Raised_Idle", (float)SpawnHoldStart, (float)HoldSeconds, ClipFlags));
            packets.Add(FxPackets.CreateGroup(SpellPackets.BuffNameHash(CrystalGemEffect(team)), CrystalGemNetId(team), nexus.Position,
                bounds, (_, _) => gemHeight, bindNetId: CrystalNetId(team), boneHash: SpellPackets.BuffNameHash(CrystalGemBone(team))));
        }
        return packets;
    }

    /// <summary>
    /// Batch 507: the stairs' lasers into the crystal, one stage per Close clip. Each row is the shipped line's clip, start frame and the two
    /// stair pieces it fires from (<c>DATA/Characters/Odin_SoG_Order/AnimEffects.list:20-22, 31-33, 42-44, 53-55</c>, commented out:
    /// <c>Close1 Crystal_r_4_aim Odin_crystal_beam_green.troy center_crystal s:270 l:0 k:0</c>, the same for <c>Crystal_l_4_aim</c>, and
    /// <c>Close1 center_crystal Odin_Crystal_beam_hit_green.troy s:270 l:0 k:0</c>; Close2 s:280 with the _3 pieces, Close3 s:285 with _2,
    /// Close4 s:290 with _1). The frame is the latch: the clip moves that stage's piece (<c>Crystal_r_4</c> in Close1, <c>Crystal_l_1</c> in
    /// Close4, .anm tracks) and slams its dust at 260/270. The bones are Order's; the Chaos stairs start from their own set (<see cref="StairBone"/>).
    /// </summary>
    public static readonly (int Stage, int Frame, string RightBone, string LeftBone)[] BeamStages =
    [
        (0, 270, "Crystal_r_4_aim", "Crystal_l_4_aim"),
        (1, 280, "Crystal_r_3_aim", "Crystal_l_3_aim"),
        (2, 285, "Crystal_r_2_aim", "Crystal_l_2_aim"),
        (3, 290, "Crystal_r_1_aim", "Crystal_l_1_aim"),
    ];

    /// <summary>
    /// Batch 725 (owner, 9 November 2026: the Chaos lasers were off the walls): the bone a laser starts from on a team's stairs. The two
    /// stairs are one model mirrored across X, and each skeleton (<c>DATA/Characters/Odin_SoG_{Order,Chaos}/*.skl</c>, 126 bones) holds two
    /// sets of the eight pieces and their aim bones: the plain <c>Crystal_{r,l}_{n}</c> (each aim a child of its piece) and the <c>chaos_</c>
    /// set (root bones). Each skin draws one set (the .skl influence list): Order's the plain pieces, Chaos's the <c>chaos_</c> pieces, and the
    /// Chaos AnimEffects names its own set too (<c>chaos_dust{n}</c>, <c>chaos_LAYOUT</c>). In the Chaos clips the plain pieces' rotations are
    /// lost (their quaternions have length 0.707), so each plain aim bone sits 88 to 197 units off the drawn piece in every latched pose,
    /// while each <c>chaos_</c> aim bone is the X-mirror of Order's (<c>.artifacts/b725/aims.py</c>, record of batch 725). Reconstruction: the
    /// shipped laser lines (commented out) exist only in the Order list.
    /// </summary>
    public static string StairBone(Team team, string bone) => team == Team.Chaos ? "chaos_" + bone : bone;

    /// <summary>
    /// Batch 507: the laser (<c>DATA/Particles/odin_crystal_beam_green.troybin</c>: emitters flare1, flare2, beam-green, beam-green-refract,
    /// beam-white, beam-white-refract, texture nexus_ray, sound <c>BOG/Lasers/buffactivate</c>). Batch 523 (owner, 31 October 2026): team-coloured
    /// per viewer, green at the viewer's own base and red (<c>odin_crystal_beam_red.troybin</c>: beam-red, beam-red-refract, Flare-Rainbow_red_01,
    /// color-redfade, the same emitters and sound) at the enemy's. The shipped line names green; the owner's retail picture has green beams into
    /// the purple Chaos crystal, so it was taken from the Chaos side.
    /// </summary>
    public const string OwnBeamEffect = "odin_crystal_beam_green";
    public const string EnemyBeamEffect = "odin_crystal_beam_red";

    /// <summary>
    /// Batch 523: the lit core where the lasers meet, for the crystal's own team and for the other team. The core is the crystal's colour, not a
    /// team colour: the three shipped files differ only in the inner-glow texture and the loop sound (key diff of the .troybin):
    /// <c>Odin_crystal_beam_hit_green</c> and <c>_blue</c> both draw <c>crystal_inner_glow_blue</c> (sounds <c>Crystal/loop</c> and
    /// <c>Crystal/loopblue</c>; <c>_blue</c> also has the <c>High</c> key that <c>_purple</c> has), <c>_purple</c> draws
    /// <c>crystal_inner_glow_purple</c> (<c>Crystal/looppurple</c>). There is no red core. So the blue Order crystal shows <c>_green</c> (the shipped
    /// line) to Order and <c>_blue</c> to Chaos, the same glow; the purple Chaos crystal shows <c>_purple</c> to everyone (a green core is the blue
    /// glow, which would light the purple crystal blue, and the owner's retail picture shows its own Chaos crystal purple and white at the core).
    /// </summary>
    public static (string Own, string Enemy) BeamHitEffects(Team team) => team == Team.Order
        ? ("Odin_crystal_beam_hit_green", "Odin_crystal_beam_hit_blue")
        : ("Odin_crystal_beam_hit_purple", "Odin_crystal_beam_hit_purple");

    /// <summary>Batch 507: the netid of a laser (one per team and stair bone) and of a stage's lit core. Both variants of an effect share it.</summary>
    public static uint BeamNetId(Team team, string bone) => MapDefinitions.BuildingNetId($"OdinNexusBeam_{team}_{bone}");
    public static uint BeamHitNetId(Team team, int stage) => MapDefinitions.BuildingNetId($"OdinNexusBeamHit_{team}_{stage}");

    /// <summary>Batch 507: every laser and lit-core netid of a team (the nexus fall removes them).</summary>
    public static IEnumerable<uint> BeamEffectNetIds(Team team) => BeamStages.SelectMany(row =>
        new[] { BeamNetId(team, row.RightBone), BeamNetId(team, row.LeftBone), BeamHitNetId(team, row.Stage) });

    /// <summary>The teams every viewer is on: the lasers are drawn wherever a viewer looks (the props are scene props, never fogged).</summary>
    private static readonly IReadOnlySet<Team> EveryViewer = new HashSet<Team> { Team.Order, Team.Chaos };

    /// <summary>
    /// Reconstruction (batch 507): one stage's two lasers and its lit core at one team's base. Retail's sender is not recovered: the shipped lines are
    /// commented out (the Chaos list has none) and no script, inibin or the client EXE names the beam.
    /// <list type="bullet">
    /// <item>Laser: bound to the stairs' netid on the stair piece's aim bone (the base's own set, <see cref="StairBone"/>), aimed at the crystal prop's netid on its gem bone
    /// (<see cref="CrystalGemBone"/>), so it ends where the gem is drawn. The shipped line aims at the stairs' own <c>center_crystal</c>, which
    /// sits 220 units below the held gem until the Raise. The 0x8C handler moves a
    /// target object that is not a unit (its unit test is false) to the packet's third position; that position is the crystal's own
    /// scene point (the nexus centre at <see cref="CrystalSceneHeight"/>, where branch 1 put it), so the crystal stays put (within the 2-unit
    /// grid step).</item>
    /// <item>Lit core: bound to the crystal prop on its gem bone, no target, like the gem.</item>
    /// </list>
    /// Batch 523: each is one persistent effect create with the base's team as its allied team and the other colour as its enemy packet, the
    /// delivery's per-viewer rule for team-coloured effects (<c>ReplicationDelivery.Route</c>: an observer off the allied team gets the enemy
    /// packet; the BuildingBlocks' <c>EffectNameForOtherTeam</c> goes the same way). Persistent, so a late joiner or a reconnecting client gets each
    /// live one once it holds the props (the spawn burst marks them known), and the fall's destroy retires it. Every viewer's team is a creation
    /// team, so it reaches both teams wherever they look, as the gem does.
    /// </summary>
    public static IReadOnlyList<EffectCreateReplicationMessage> BeamMessages(int stage, Team team, NavigationBounds bounds)
    {
        var row = BeamStages[stage];
        var nexus = MapDefinitions.CrystalScarNexusList.First(candidate => candidate.Team == team);
        var height = CrystalSceneHeight(team);
        var stairs = team == Team.Order ? OrderStairsNetId : ChaosStairsNetId;
        var gemBone = SpellPackets.BuffNameHash(CrystalGemBone(team));
        PlaintextPacket Beam(string effect, string bone) => FxPackets.CreateGroup(SpellPackets.BuffNameHash(effect),
            BeamNetId(team, bone), nexus.Position, bounds, (_, _) => height, targetPosition: nexus.Position, ownerPosition: nexus.Position,
            targetNetId: CrystalNetId(team), bindNetId: stairs, targetBoneHash: gemBone, boneHash: SpellPackets.BuffNameHash(StairBone(team, bone)));
        PlaintextPacket Core(string effect) => FxPackets.CreateGroup(SpellPackets.BuffNameHash(effect), BeamHitNetId(team, stage),
            nexus.Position, bounds, (_, _) => height + RaisedCoreHeight, bindNetId: CrystalNetId(team), boneHash: gemBone);
        EffectCreateReplicationMessage Message(PlaintextPacket own, PlaintextPacket enemy, uint effectNetId) =>
            new(own, effectNetId, AlliedTeam: team, EnemyPacket: enemy, Persistent: true, CreationVisibleTeams: EveryViewer);
        var (ownCore, enemyCore) = BeamHitEffects(team);
        return
        [
            Message(Beam(OwnBeamEffect, row.RightBone), Beam(EnemyBeamEffect, row.RightBone), BeamNetId(team, row.RightBone)),
            Message(Beam(OwnBeamEffect, row.LeftBone), Beam(EnemyBeamEffect, row.LeftBone), BeamNetId(team, row.LeftBone)),
            Message(Core(ownCore), Core(enemyCore), BeamHitNetId(team, stage)),
        ];
    }

    /// <summary>
    /// Batch 507: each stage's lasers go out once, at the stage's latch frame of its Close clip. Batch 715: the clip start plus frame * duration /
    /// frames, as the 0xDA time-syncs each clip over its stage (<see cref="StairClips"/>): 19.16, 38.60, 58.94 and 79.27 s with the 80 s release. A team whose nexus has
    /// fallen gets no more. The lasers stay on (k:0) until the fall. Batch 523: the fall's removal is <see cref="Removals"/> (the delivery's
    /// effect destroy, which also drops the persistent create, so no later viewer gets it).
    /// </summary>
    public sealed class CrystalBeams
    {
        private readonly bool[] _sent = new bool[BeamStages.Length];
        private readonly HashSet<Team> _removed = new();

        public IReadOnlyList<EffectCreateReplicationMessage> Due(double now, DominionPropFacing mode, double release, NavigationBounds bounds,
            Func<Team, bool> nexusFallen)
        {
            ArgumentNullException.ThrowIfNull(nexusFallen);
            if (mode == DominionPropFacing.Off) return Array.Empty<EffectCreateReplicationMessage>();
            var stages = StairStages(release);
            var messages = new List<EffectCreateReplicationMessage>();
            foreach (var row in BeamStages)
            {
                var (_, start, duration) = stages[row.Stage];
                if (_sent[row.Stage] || now < start + row.Frame * duration / StairClips[row.Stage].Frames) continue;
                _sent[row.Stage] = true;
                foreach (var team in new[] { Team.Order, Team.Chaos })
                    if (!nexusFallen(team)) messages.AddRange(BeamMessages(row.Stage, team, bounds));
            }
            return messages;
        }

        /// <summary>Once per team, at its nexus fall: the destroy (0x3D) of each of its lasers and lit cores.</summary>
        public IReadOnlyList<EffectDestroyReplicationMessage> Removals(DominionPropFacing mode, Func<Team, bool> nexusFallen)
        {
            ArgumentNullException.ThrowIfNull(nexusFallen);
            if (mode == DominionPropFacing.Off) return Array.Empty<EffectDestroyReplicationMessage>();
            return new[] { Team.Order, Team.Chaos }
                .Where(team => nexusFallen(team) && _removed.Add(team))
                .SelectMany(BeamEffectNetIds)
                .Select(effect => new EffectDestroyReplicationMessage(FxPackets.Kill(effect), effect))
                .ToArray();
        }
    }

    /// <summary>
    /// Reconstruction (batch 486): the mission time the crystals' spawn hold starts at. It must be above 0.00001 for the client to time-sync the
    /// clip. The spawn's mission clock is about zero, so the seek (clock minus start) clamps to frame 0.
    /// </summary>
    public const double SpawnHoldStart = 1.0;

    /// <summary>
    /// The stairs' closing clips in order, with their frame counts from each .anm header (Odin_SoG_{Order,Chaos}_Close_1..4: 286 frames for
    /// Close1, 301 for Close2 to Close4; the header's fps is 30) and the seconds each stage is time-synced over.
    /// Reconstruction (batch 715): 15 s for Close1 and 20 s for Close2 to Close4, from the closing sounds. Each clip fires its sound at frame 0
    /// (<c>Odin-SOG_Stair{n}_OBA_Sound.troy</c>, <c>BOG/Stair{n}/buffactivate</c>: the waves <c>odin-bog_stair{n}_extend_01/02</c>, 18 to 24 s), and
    /// each wave ends in a thump: its end event starts 13.0 to 13.6 s in for Stair1 and 17.5 to 17.9 s in for Stair2 to Stair4 (both waves of
    /// each, decoded from <c>LoL_SFX_odin.fsb</c>). The slam is the slamming dust's frame (AnimEffects s:260 on Close1, s:270 on the others; the
    /// stair piece's fastest frame, .anm). Over 15 and 20 s the slam comes 13.64 and 17.94 s in, on the thump; at the clips' own 30 fps it came
    /// at 8.67 and 9.0 s, so the thump played 4.4 to 8.9 s after the slam, in the next stage (batch 715 record). Retail's sender is not
    /// recovered; these are the round stage lengths the four sounds agree on.
    /// </summary>
    public static readonly (string Name, int Frames, double Seconds)[] StairClips =
    [
        ("Close1", 286, 15.0), ("Close2", 301, 20.0), ("Close3", 301, 20.0), ("Close4", 301, 20.0),
    ];

    /// <summary>
    /// Reconstruction (batch 485): the duration of a held idle (Raised_Idle). The client sets the playback speed to frames / duration
    /// (batch 509: a float, not an int). With this
    /// duration a 65 or 89 frame idle advances under 1e-5 frames a second: it stays on the frame it seeks to (frame 0 at the send) and never
    /// reaches its end, so it does not revert to Idle1 (the lowered crystal, or Open_Idle on the stairs). Holds the formed pose; it does not loop.
    /// </summary>
    public const double HoldSeconds = 10_000_000.0;

    /// <summary>
    /// Reconstruction (batch 485): the 0xDA duration for a clip of <paramref name="seconds"/>, shortened by 1e-6. Batch 485 took the speed
    /// for an int that truncates 29.99999 to 29; batch 509 found it is a float (see <see cref="HoldSeconds"/>), so the shortening changes
    /// nothing visible. It is kept as it is harmless.
    /// </summary>
    public static float PacketSeconds(double seconds) => (float)(seconds * (1.0 - 1e-6));

    /// <summary>
    /// Batch 509: the play flags (raw 81) of every prop clip. The prop's listener ORs in 0x20, which drops the clip that was playing
    /// (the prop's listener), and passes them to the animation's play and update:
    /// <list type="bullet">
    /// <item>0x4 holds the last frame: at the clip's end the update clamps the frame and keeps the clip. Without it, a clip that ends fades out over 0.4 s into the prop's default <c>idle1</c> (the animation setup
    /// idle1, blend 0.4): Open_Idle on the stairs, the lowered roots on the crystal. That
    /// was the shake at each Close stage (the next 0xDA lands a tick after the end and snaps the pose back) and the pre-game look after the
    /// nexus fall. The clips chain exactly: each Close clip's last frame is the next one's frame 0, as are Close4 to Raise and Raise to
    /// Raised_Idle (all 126 bones).</item>
    /// <item>0x200 starts the clip at full weight (no 0.2 s fade-in).</item>
    /// </list>
    /// 0x20 and 0x100 (bundles 31 and 30) were no-ops: 0x100 only matters with 0x2, 0x8 or 0x10.
    /// </summary>
    public const uint ClipFlags = 0x4 | 0x200;

    /// <summary>
    /// The stages with their mission start times: the four clips run back to back and the last one ends at <paramref name="release"/>
    /// (the owner's 1:20 start). Stage <c>i</c> starts when stage <c>i-1</c> has run its duration. Batch 715: each duration is the stage's
    /// <see cref="StairClips"/> seconds (15, 20, 20, 20), so with the 80 s release the stages start at 5, 20, 40 and 60 s.
    /// </summary>
    public static IReadOnlyList<(string Name, double Start, double Duration)> StairStages(double release)
    {
        var start = release - StairClips.Sum(clip => clip.Seconds);
        var stages = new (string Name, double Start, double Duration)[StairClips.Length];
        for (var index = 0; index < StairClips.Length; index++)
        {
            stages[index] = (StairClips[index].Name, start, StairClips[index].Seconds);
            start += StairClips[index].Seconds;
        }
        return stages;
    }

    /// <summary>
    /// DIAGNOSTICS (batch 477c; not sent since batch 486, kept for its check): the crystals' raise. A crystal is created in its <c>Idle1</c> (the create sets Idle1), which
    /// <c>DATA/Characters/Odin_SOG_*_Crystal/Animations.list</c> maps to <c>Crystal_nexus_idle_lower</c>: the lowered pose. At the release it
    /// plays <c>Raise</c> (<c>Crystal_nexus_idle_raise</c>, 39 frames at 30 fps), then <c>Raised_Idle</c> (<c>Crystal_nexus_idle</c>, an idle).
    /// Batch 485 (Reconstruction): both are time-synced. <c>Raise</c> starts at the release with its own length; <c>Raised_Idle</c> starts when
    /// Raise ends and holds (<see cref="HoldSeconds"/>). A start of 0 skips the time sync (the seek and speed are set only
    /// when start is above 0.00001), so the old 0/0 packets had no speed and the finite idle ended and reverted the crystal to its lowered pose.
    /// Batch 483 (Reconstruction): the crystals are now branch-1 LevelPropGameObjects, not LevelPropAI, so their clips no longer reach the
    /// navigation height assert that batch 477d saw on the LevelPropAI's Raise. Unproven until the owner's native game.
    /// Batch 486: no longer sent by the server. The crystals stood raised from the spawn, so a release Raise would drop them to frame 0 of the
    /// raise clip and back. Batch 491: still not sent (the crystals stand from the spawn, <see cref="CrystalStandPackets"/>); its netids are the
    /// nexus building's, not the props' (<see cref="CrystalNetId"/>). Kept for its check and the record.
    /// </summary>
    public sealed class CrystalRaiser
    {
        /// <summary>The length of <c>Raise</c>: 39 frames at 30 fps.</summary>
        public const double RaiseSeconds = 39 / 30.0;

        private int _stage;

        public IReadOnlyList<PlaintextPacket> Due(double now, DominionPropFacing mode, double release)
        {
            if (mode == DominionPropFacing.Off) return Array.Empty<PlaintextPacket>();
            var packets = new List<PlaintextPacket>();
            if (_stage == 0 && now >= release)
            {
                _stage = 1;
                foreach (var nexus in MapDefinitions.CrystalScarNexusList)
                    packets.Add(LevelPropPackets.Animation(nexus.NetId, "Raise", (float)release, PacketSeconds(RaiseSeconds)));
            }
            if (_stage == 1 && now >= release + RaiseSeconds)
            {
                _stage = 2;
                foreach (var nexus in MapDefinitions.CrystalScarNexusList)
                    packets.Add(LevelPropPackets.Animation(nexus.NetId, "Raised_Idle", (float)(release + RaiseSeconds), (float)HoldSeconds));
            }
            return packets;
        }
    }

    /// <summary>
    /// Reconstruction (batch 477d): the stairs' <c>Raise</c> then <c>Raised_Idle</c> after Close4. The <c>Odin_SoG_*</c> prop carries the nexus crystal
    /// clips (its Animations.list maps Raise to Crystal_Raise and Raised_Idle to Crystal_Idle): Crystal_Raise is 101 frames at 30 fps (3.367 s),
    /// Crystal_Idle is a 65-frame idle. Batch 485 (Reconstruction): both are time-synced like the Close clips. Raise starts at the release with
    /// its length; Raised_Idle starts when Raise ends and holds (<see cref="HoldSeconds"/>), so the stairs stay formed after 1:20.
    /// Reason (owner bundle 31, client log): the crystal's own prop asserts when its clip plays
    /// (<c>NavigationGrid.cpp:581</c>, <c>!"InvalidCell"</c>); its footprint cell is NotPassable with height 0 (map 8 AIPath.aimesh_ngrid).
    /// </summary>
    public sealed class StairRaiser
    {
        /// <summary>The length of <c>Raise</c>: 101 frames at 30 fps.</summary>
        public const double RaiseSeconds = 101 / 30.0;

        private int _stage;

        public IReadOnlyList<PlaintextPacket> Due(double now, DominionPropFacing mode, double release)
        {
            if (mode == DominionPropFacing.Off) return Array.Empty<PlaintextPacket>();
            var packets = new List<PlaintextPacket>();
            if (_stage == 0 && now >= release)
            {
                _stage = 1;
                packets.Add(LevelPropPackets.Animation(OrderStairsNetId, "Raise", (float)release, PacketSeconds(RaiseSeconds), ClipFlags));
                packets.Add(LevelPropPackets.Animation(ChaosStairsNetId, "Raise", (float)release, PacketSeconds(RaiseSeconds), ClipFlags));
            }
            if (_stage == 1 && now >= release + RaiseSeconds)
            {
                _stage = 2;
                packets.Add(LevelPropPackets.Animation(OrderStairsNetId, "Raised_Idle", (float)(release + RaiseSeconds), (float)HoldSeconds,
                    ClipFlags));
                packets.Add(LevelPropPackets.Animation(ChaosStairsNetId, "Raised_Idle", (float)(release + RaiseSeconds), (float)HoldSeconds,
                    ClipFlags));
            }
            return packets;
        }
    }

    /// <summary>
    /// Reconstruction (batches 477d and 483): a team's nexus death. Batch 491 (Reconstruction): every clip is time-synced from the fall (start
    /// = the fall, duration = the clip's length; a start of 0 left the speed unset, batch 485). At the fall, once per team:
    /// <list type="bullet">
    /// <item>the <c>Odin_SoG_*</c> stairs play <c>Death</c> (Odin_SoG_*_Shatter, 211 frames at 30 fps);</item>
    /// <item>the gem effect is killed (0x3D), and the crystal prop plays <c>Death</c> (Crystal_nexus_shattering, 121 frames), whose
    /// AnimEffects put the cracked gem (<c>Odin_Crystal_Cracked_*</c>) and the crack lights on the same bones;</item>
    /// <item>when Death ends, the crystal plays <c>Explode</c> (Crystal_nexus_shattering_explode, 61 frames, the
    /// <c>Odin_CrystalExplosion_*</c> burst). The order is the data's: Explode's first frame is Death's last pose (skinned bounds equal
    /// within 9 units, batch 491 decode).</item>
    /// </list>
    /// </summary>
    public sealed class NexusDeath
    {
        private const double StairShatterSeconds = 211 / 30.0;
        private const double CrystalDeathSeconds = 121 / 30.0;
        private const double CrystalExplodeSeconds = 61 / 30.0;
        private readonly Dictionary<Team, double> _fellAt = new();
        private readonly HashSet<Team> _exploded = new();

        public IReadOnlyList<PlaintextPacket> Due(double now, DominionPropFacing mode, Func<Team, bool> nexusFallen)
        {
            ArgumentNullException.ThrowIfNull(nexusFallen);
            if (mode == DominionPropFacing.Off) return Array.Empty<PlaintextPacket>();
            var packets = new List<PlaintextPacket>();
            foreach (var team in new[] { Team.Order, Team.Chaos })
            {
                if (!_fellAt.TryGetValue(team, out var fell))
                {
                    if (!nexusFallen(team)) continue;
                    _fellAt[team] = fell = now;
                    // Batch 509: every fall clip holds its last frame (ClipFlags), so the fallen props stay fallen.
                    packets.Add(LevelPropPackets.Animation(team == Team.Order ? OrderStairsNetId : ChaosStairsNetId, "Death",
                        (float)fell, PacketSeconds(StairShatterSeconds), ClipFlags));
                    packets.Add(FxPackets.Kill(CrystalGemNetId(team)));
                    packets.Add(LevelPropPackets.Animation(CrystalNetId(team), "Death", (float)fell, PacketSeconds(CrystalDeathSeconds),
                        ClipFlags));
                }
                if (now < fell + CrystalDeathSeconds || !_exploded.Add(team)) continue;
                packets.Add(LevelPropPackets.Animation(CrystalNetId(team), "Explode", (float)(fell + CrystalDeathSeconds),
                    PacketSeconds(CrystalExplodeSeconds), ClipFlags));
            }
            return packets;
        }
    }

    /// <summary>
    /// Sends each stage's 0xDA once, on the first tick at or after its start. The client seeks a clip to "now minus start", so a send
    /// at the start shows the clip from its first frame, and the frame-0 closing sound starts with it (batch 715). Each stage holds its last
    /// frame until the next one lands (<see cref="ClipFlags"/>).
    /// </summary>
    public sealed class StairAnimator
    {
        private readonly bool[] _sent = new bool[StairClips.Length];

        public IReadOnlyList<PlaintextPacket> Due(double now, DominionPropFacing mode, double release)
        {
            if (mode == DominionPropFacing.Off) return Array.Empty<PlaintextPacket>();
            var packets = new List<PlaintextPacket>();
            var stages = StairStages(release);
            for (var index = 0; index < stages.Count; index++)
            {
                if (_sent[index] || now < stages[index].Start) continue;
                _sent[index] = true;
                var (name, start, duration) = stages[index];
                packets.Add(LevelPropPackets.Animation(OrderStairsNetId, name, (float)start, PacketSeconds(duration), ClipFlags));
                packets.Add(LevelPropPackets.Animation(ChaosStairsNetId, name, (float)start, PacketSeconds(duration), ClipFlags));
            }
            return packets;
        }
    }
}
