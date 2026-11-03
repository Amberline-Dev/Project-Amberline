using System.IO.Compression;
using System.Text;

namespace Amberline.Data;

/// <summary>
/// The champion rows of the player's own AIR client, read at runtime: <c>assets/swfs/AirGeneratedContent.swf</c> holds the
/// AIR class <c>ChampionGeneratedData</c>, whose <c>fillChampionData</c> calls <c>createChampion</c> once per champion with
/// constant arguments (id, skin name, four ranks, five <c>.dds</c> icon names, the search tags, the skin table). This reads the
/// SWF's ABC bytecode and replays only those constant pushes; nothing of the client is copied into the server.
/// </summary>
public static class AirChampionData
{
    public const string SwfRelativePath = "assets/swfs/AirGeneratedContent.swf";

    /// <summary>One <c>createChampion</c> call: the icon names without <c>.dds</c>, the skin count of its 1..N skin indices.</summary>
    public sealed record Row(int Id, string Name, int SkinCount, string[] Tags, string[] Icons);

    /// <summary>
    /// The AIR client's <see cref="SwfRelativePath"/>: under <c>AIR_CLIENT_ROOT</c> (the Platform sets it to its AIR folder),
    /// else the nearest <c>air</c> folder above the program, the working directory or the game data.
    /// </summary>
    public static string ResolveSwf()
    {
        var configured = Environment.GetEnvironmentVariable("AIR_CLIENT_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            return CaseInsensitivePaths.Resolve(Path.GetFullPath(Path.Combine(configured, SwfRelativePath)));
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory(), GameDataPaths.ResolveRoot() })
            for (var directory = new DirectoryInfo(Path.GetFullPath(start)); directory is not null; directory = directory.Parent)
                foreach (var air in new[] { "air" })
                {
                    var candidate = CaseInsensitivePaths.Resolve(Path.Combine(directory.FullName, air, SwfRelativePath));
                    if (File.Exists(candidate)) return candidate;
                }
        throw new FileNotFoundException(
            $"The AIR client's {SwfRelativePath} was not found; set AIR_CLIENT_ROOT to the AIR client folder (the one holding LolClient.swf).");
    }

    public static IReadOnlyList<Row> Read(string swfPath) => Champions(DoAbc(File.ReadAllBytes(swfPath)));

    /// <summary>The ABC block of the first DoABC tag (82, or 72 without the flags and name) of an FWS or zlib CWS movie.</summary>
    private static byte[] DoAbc(byte[] file)
    {
        if (file.Length < 8 || file[1] != 'W' || file[2] != 'S' || file[0] != 'F' && file[0] != 'C')
            throw new InvalidDataException("Not a SWF movie.");
        var body = file[8..];
        if (file[0] == 'C')
        {
            using var inflated = new MemoryStream();
            using (var zlib = new ZLibStream(new MemoryStream(body), CompressionMode.Decompress)) zlib.CopyTo(inflated);
            body = inflated.ToArray();
        }
        // The frame RECT (5-bit field width, then four fields), frame rate and frame count precede the tags.
        var pos = (5 + 4 * (body[0] >> 3) + 7) / 8 + 4;
        while (pos + 2 <= body.Length)
        {
            int header = BitConverter.ToUInt16(body, pos), code = header >> 6, length = header & 0x3F;
            pos += 2;
            if (length == 0x3F) { length = BitConverter.ToInt32(body, pos); pos += 4; }
            if (code == 82) return body[(Array.IndexOf(body, (byte)0, pos + 4) + 1)..(pos + length)];
            if (code == 72) return body[pos..(pos + length)];
            if (code == 0) break;
            pos += length;
        }
        throw new InvalidDataException("The SWF holds no DoABC tag.");
    }

    private sealed class Reader(byte[] data)
    {
        public int Pos;
        public int Length => data.Length;
        public byte U8() => data[Pos++];
        public uint U32()
        {
            uint value = 0;
            for (var shift = 0; shift < 35; shift += 7)
            {
                var b = U8();
                value |= (uint)(b & 0x7F) << shift;
                if (b < 0x80) break;
            }
            return value;
        }
        public int U30() => (int)U32();
        public void Skip(int count) { for (var i = 0; i < count; i++) U32(); }
        public byte[] Bytes(int count) { var bytes = data[Pos..(Pos + count)]; Pos += count; return bytes; }
    }

    /// <summary>Walks the ABC file (AVM2 overview, chapter 4) to the body of <c>fillChampionData</c> and replays it.</summary>
    private static IReadOnlyList<Row> Champions(byte[] abc)
    {
        var r = new Reader(abc) { Pos = 4 };
        var ints = new int[Math.Max(1, r.U30())];
        for (var i = 1; i < ints.Length; i++) ints[i] = (int)r.U32();
        r.Skip(Math.Max(0, r.U30() - 1));
        var doubles = r.U30();
        r.Pos += 8 * Math.Max(0, doubles - 1);
        var strings = new string[Math.Max(1, r.U30())];
        strings[0] = "";
        for (var i = 1; i < strings.Length; i++) strings[i] = Encoding.UTF8.GetString(r.Bytes(r.U30()));
        for (var i = r.U30() - 1; i > 0; i--) { r.U8(); r.U30(); }
        for (var i = r.U30() - 1; i > 0; i--) r.Skip(r.U30());
        // Each multiname's name (null for a runtime name) and how many runtime name parts an instruction pops for it.
        var names = new List<string?> { null };
        var runtimeParts = new List<int> { 0 };
        for (var i = r.U30() - 1; i > 0; i--)
        {
            var kind = r.U8();
            string? name = null;
            var parts = 0;
            switch (kind)
            {
                case 0x07 or 0x0D: r.U30(); name = strings[r.U30()]; break;   // QName: namespace, name
                case 0x0F or 0x10: name = strings[r.U30()]; parts = 1; break;  // RTQName: name, runtime namespace
                case 0x11 or 0x12: parts = 2; break;                            // RTQNameL: runtime namespace and name
                case 0x09 or 0x0E: name = strings[r.U30()]; r.U30(); break;    // Multiname: name, namespace set
                case 0x1B or 0x1C: r.U30(); parts = 1; break;                  // MultinameL: namespace set, runtime name
                case 0x1D: r.U30(); r.Skip(r.U30()); break;                    // TypeName: name, parameters
                default: throw new InvalidDataException($"Unknown ABC multiname kind 0x{kind:X2}.");
            }
            names.Add(name);
            runtimeParts.Add(parts);
        }
        for (var i = r.U30(); i > 0; i--)
        {
            var parameters = r.U30();
            r.Skip(2 + parameters);
            var flags = r.U8();
            if ((flags & 0x08) != 0) for (var o = r.U30(); o > 0; o--) { r.U30(); r.U8(); }
            if ((flags & 0x80) != 0) r.Skip(parameters);
        }
        for (var i = r.U30(); i > 0; i--) { r.U30(); r.Skip(2 * r.U30()); }
        var fill = -1;
        void Traits()
        {
            for (var t = r.U30(); t > 0; t--)
            {
                var name = names[r.U30()];
                var kind = r.U8();
                if ((kind & 0x0F) is 0 or 6) { r.Skip(2); if (r.U30() != 0) r.U8(); }
                else
                {
                    r.U30();
                    var method = r.U30();
                    if ((kind & 0x0F) == 1 && name == "fillChampionData") fill = method;
                }
                if ((kind & 0x40) != 0) r.Skip(r.U30());
            }
        }
        var classes = r.U30();
        for (var i = 0; i < classes; i++)
        {
            r.Skip(2);
            if ((r.U8() & 0x08) != 0) r.U30();
            r.Skip(r.U30());
            r.U30();
            Traits();
        }
        for (var i = 0; i < classes; i++) { r.U30(); Traits(); }
        for (var i = r.U30(); i > 0; i--) { r.U30(); Traits(); }
        for (var i = r.U30(); i > 0; i--)
        {
            var method = r.U30();
            r.Skip(4);
            var code = r.Bytes(r.U30());
            for (var e = r.U30(); e > 0; e--) r.Skip(5);
            Traits();
            if (method == fill) return Replay(code, ints, strings, names, runtimeParts);
        }
        throw new InvalidDataException("AirGeneratedContent holds no ChampionGeneratedData.fillChampionData body.");
    }

    /// <summary>
    /// Runs the constant-building subset of AVM2 that <c>fillChampionData</c> uses, keeping the arguments of every
    /// <c>createChampion</c> call. Any other opcode is refused, so a different client build fails loudly instead of reading wrong rows.
    /// </summary>
    private static IReadOnlyList<Row> Replay(byte[] code, int[] ints, string[] strings, List<string?> names, List<int> runtimeParts)
    {
        var stack = new List<object?>();
        var rows = new List<Row>();
        object? Pop() { var value = stack[^1]; stack.RemoveAt(stack.Count - 1); return value; }
        object?[] PopN(int count) { var values = stack[^count..].ToArray(); stack.RemoveRange(stack.Count - count, count); return values; }
        void PopName(int multiname) { for (var i = runtimeParts[multiname]; i > 0; i--) Pop(); }
        var r = new Reader(code);
        while (r.Pos < r.Length)
        {
            var op = r.U8();
            switch (op)
            {
                case 0xD0: stack.Add(null); break;                                   // getlocal0
                case 0x30: Pop(); break;                                              // pushscope
                case 0x2A: stack.Add(stack[^1]); break;                               // dup
                case 0x24: stack.Add((int)(sbyte)r.U8()); break;                      // pushbyte
                case 0x25: stack.Add((int)(short)r.U30()); break;                     // pushshort
                case 0x2D: stack.Add(ints[r.U30()]); break;                           // pushint
                case 0x2C: stack.Add(strings[r.U30()]); break;                        // pushstring
                case 0x56: stack.Add(PopN(r.U30())); break;                           // newarray
                case 0x55:                                                            // newobject
                    var pairs = PopN(2 * r.U30());
                    var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
                    for (var i = 0; i < pairs.Length; i += 2) obj[(string)pairs[i]!] = pairs[i + 1];
                    stack.Add(obj);
                    break;
                case 0x5D or 0x5E or 0x60: PopName(r.U30()); stack.Add(null); break;  // findpropstrict, findproperty, getlex
                case 0x61 or 0x68: { var mn = r.U30(); Pop(); PopName(mn); Pop(); break; } // setproperty, initproperty
                case 0x46 or 0x4A:                                                    // callproperty, constructprop
                {
                    var mn = r.U30();
                    var args = PopN(r.U30());
                    PopName(mn);
                    Pop();
                    if (op == 0x46 && names[mn] == "createChampion") rows.Add(ToRow(args));
                    stack.Add(null);
                    break;
                }
                case 0x47: return rows;                                               // returnvoid
                default: throw new InvalidDataException($"fillChampionData uses opcode 0x{op:X2}, which this reader does not replay.");
            }
        }
        throw new InvalidDataException("fillChampionData ended without returnvoid.");
    }

    /// <summary>createChampion(id, skinName, attack, defense, magic, difficulty, passive, Q, W, E, R icons, searchTags, skinInfo).</summary>
    private static Row ToRow(object?[] args)
    {
        if (args.Length != 13) throw new InvalidDataException($"createChampion takes 13 arguments, not {args.Length}.");
        var id = (int)args[0]!;
        var icons = args[6..11].Select(arg => arg is string icon && icon.EndsWith(".dds", StringComparison.Ordinal)
            ? icon[..^4] : throw new InvalidDataException($"Champion {id}: icon '{arg}' is not NAME.dds.")).ToArray();
        var skins = ((Dictionary<string, object?>)args[12]!).Values
            .Select(skin => (int)((Dictionary<string, object?>)skin!)["skinIndex"]!).Order().ToArray();
        // The global skin ids are Id * 1000 + 1 .. SkinCount (AirGameDtos.SkinIds), so the indices must be 1..N.
        if (skins.Length == 0 || !skins.SequenceEqual(Enumerable.Range(1, skins.Length)))
            throw new InvalidDataException($"Champion {id}: skin indices are not 1..N.");
        return new Row(id, (string)args[1]!, skins.Length, ((object?[])args[11]!).Cast<string>().ToArray(), icons);
    }
}
