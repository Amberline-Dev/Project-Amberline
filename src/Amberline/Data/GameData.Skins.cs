using System.Globalization;
using System.Text.RegularExpressions;

namespace Amberline.Data;

public sealed record ChampionSkin(int Id, string DisplayName);

public sealed partial class GameData
{
    private readonly Dictionary<string, IReadOnlyList<ChampionSkin>> _skins = new(StringComparer.Ordinal);
    private Dictionary<string, string>? _labels;

    /// <summary>
    /// Character, skin and bot names the client localizes, read from the shipped fontconfig. NOT RETAIL: a game folder without its
    /// language project (<c>lol_game_client_en_us</c>) has no fontconfig, which retail's deploy always composed into the game. Then
    /// there are no labels: the names fall back to the champions' internal names ("MonkeyKing Bot", "Skin 1") and no hero gets the
    /// bot flag, as the client could not name it either, so the champion list and the host's login still work (batch 657). The empty
    /// answer is not kept: a fontconfig copied in later is read then.
    /// </summary>
    private Dictionary<string, string> Labels
    {
        get
        {
            if (_labels is not null) return _labels;
            var path = Paths.Data("Menu/fontconfig_en_US.txt");
            if (!File.Exists(path)) return new();
            return _labels = Regex.Matches(File.ReadAllText(path),
                    "(?m)^tr\\s+\"(game_(?:character_(?:skin_)?displayname|bot)_[^\"]+)\"\\s*=\\s*\"([^\"]*)\"")
                .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value, StringComparer.Ordinal);
        }
    }

    /// <summary>Local MeshSkin indices, not the global ChampionSkinID catalog numbers.</summary>
    public IReadOnlyList<ChampionSkin> Skins(string champion)
    {
        if (_skins.TryGetValue(champion, out var cached)) return cached;
        var raw = Champion(champion).Raw;
        var skins = new List<ChampionSkin> { new(0, "Default") };
        // The shipped ten champions have contiguous MeshSkin1..N sections.
        for (var id = 1; raw.TryGetValue(InibinReader.KeyHash($"MeshSkin{id}*SimpleSkin"), out _); id++)
        {
            if (new[] { "SimpleSkin", "Skeleton", "Texture" }.Any(field =>
                !raw.TryGetValue(InibinReader.KeyHash($"MeshSkin{id}*{field}"), out var asset) ||
                asset.Kind != InibinValueKind.String ||
                !File.Exists(Paths.Data($"Characters/{champion}/{asset.AsString()}")))) continue;
            var label = $"game_character_skin_displayname_{champion}_{id.ToString(CultureInfo.InvariantCulture)}";
            skins.Add(new ChampionSkin(id, Labels.GetValueOrDefault(label, $"Skin {id}")));
        }
        return _skins[champion] = skins.AsReadOnly();
    }

    public void ValidateSkin(string champion, int skinId)
    {
        if (skinId < 0 || skinId > 0 && !Skins(champion).Any(skin => skin.Id == skinId))
            throw new ArgumentException($"{champion} has no installed skin with ID {skinId}.", nameof(skinId));
    }

    private readonly Dictionary<(string, int), double> _skinHalfWidths = new();

    /// <summary>
    /// Half the ground width of a unit's bounding box at model scale 1 (batch 256): the client's bounding-box recalculation makes
    /// <c>GameObject.BBox.Size.x</c> and <c>.z</c> both <c>SkinScale * mScaleSkinCoef * (meshX + meshZ) / 2</c>, where meshX and meshZ are the
    /// skin's vertex extents (the mesh box filled from the SKN asset's minimum and maximum), or 25 when the character has no
    /// skin mesh. This is half of that size without the coefficient: <c>SkinScale * (meshX + meshZ) / 4</c>, 12.5 without a mesh. The skin is
    /// the inibin section <c>MeshSkin</c> (skin 0) or <c>MeshSkin{id}</c> (its <c>SimpleSkin</c> file and <c>SkinScale</c>, default 1); a
    /// skin id the character has no section for reads skin 0. (The same extents give the Nexus' attack radius, MapDefinitions.NexusRadius.)
    /// </summary>
    public double SkinHalfWidth(string character, int skinId = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(character);
        if (_skinHalfWidths.TryGetValue((character, skinId), out var cached)) return cached;
        var half = 12.5;
        if (HasCharacterData(character))
        {
            var raw = Champion(character).Raw;
            var section = skinId > 0 && raw.TryGetValue(InibinReader.KeyHash($"MeshSkin{skinId}*SimpleSkin"), out _) ? $"MeshSkin{skinId}" : "MeshSkin";
            if (raw.TryGetValue(InibinReader.KeyHash($"{section}*SimpleSkin"), out var skin) && skin.Kind == InibinValueKind.String &&
                File.Exists(Paths.Data($"Characters/{character}/{skin.AsString()}")))
            {
                var (extentX, extentZ) = SknGroundExtents(Paths.Data($"Characters/{character}/{skin.AsString()}"));
                half = Number(raw, $"{section}*SkinScale", 1.0) * (extentX + extentZ) / 4.0;
            }
        }
        return _skinHalfWidths[(character, skinId)] = half;
    }

    /// <summary>
    /// Batch 582: the bind-pose height of a joint above the unit's ground point at model scale 1: <c>SkinScale</c> times the joint's model-space
    /// origin in the skin's skeleton (<c>MeshSkin</c> or <c>MeshSkin{id}</c> <c>Skeleton</c>, <see cref="SkeletonFile.BindOrigin"/>). Null when the
    /// character, its skeleton or the joint is absent. Ezreal's <c>root</c> is 86.3 up in his skeleton, 94.9 at his SkinScale 1.1.
    /// </summary>
    public double? JointHeight(string character, int skinId, string bone)
    {
        if (string.IsNullOrWhiteSpace(character) || string.IsNullOrWhiteSpace(bone) || !HasCharacterData(character)) return null;
        var raw = Champion(character).Raw;
        var section = skinId > 0 && raw.TryGetValue(InibinReader.KeyHash($"MeshSkin{skinId}*Skeleton"), out _) ? $"MeshSkin{skinId}" : "MeshSkin";
        if (!raw.TryGetValue(InibinReader.KeyHash($"{section}*Skeleton"), out var skeleton) || skeleton.Kind != InibinValueKind.String) return null;
        return SkeletonFile.BindOrigin(Paths.Data($"Characters/{character}/{skeleton.AsString()}"), bone)?.Y * Number(raw, $"{section}*SkinScale", 1.0);
    }

    /// <summary>The X and Z vertex extents of an SKN mesh: the magic bytes 33 22 11 00, version, object count; from version 1 a material table (80 bytes
    /// each); the index and vertex counts, 16-bit indices, then 52-byte vertices whose first 12 bytes are the position.</summary>
    private static (double X, double Z) SknGroundExtents(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.ReadUInt32() != 0x00112233) throw new InvalidDataException($"{path} is not an SKN mesh");
        var version = reader.ReadUInt16();
        reader.ReadUInt16();
        if (version >= 1) reader.BaseStream.Seek(reader.ReadUInt32() * 80L, SeekOrigin.Current);
        var indices = reader.ReadUInt32();
        var vertices = reader.ReadUInt32();
        reader.BaseStream.Seek(indices * 2L, SeekOrigin.Current);
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        for (var vertex = 0; vertex < vertices; vertex++)
        {
            var x = reader.ReadSingle();
            reader.ReadSingle();
            var z = reader.ReadSingle();
            reader.BaseStream.Seek(40, SeekOrigin.Current);
            (minX, maxX, minZ, maxZ) = (Math.Min(minX, x), Math.Max(maxX, x), Math.Min(minZ, z), Math.Max(maxZ, z));
        }
        return vertices == 0 ? (0.0, 0.0) : (Math.Abs(maxX - minX), Math.Abs(maxZ - minZ));
    }
}
