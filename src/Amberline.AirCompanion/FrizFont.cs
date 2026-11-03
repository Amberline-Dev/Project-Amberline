using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Amberline.AirCompanion;

/// <summary>One outline point in SWF font units (20480 per em, y down).</summary>
public readonly record struct SwfPoint(int X, int Y, bool OnCurve);

/// <summary>One glyph of a DefineFont3 tag: its code point, advance and closed contours (quadratic splines).</summary>
public sealed record SwfGlyph(int Code, int Advance, IReadOnlyList<SwfPoint[]> Contours);

/// <summary>One parsed DefineFont3 tag (with the name and copyright of its DefineFontName tag, when there is one).</summary>
public sealed record SwfFontFace(int FontId, string Name, string InternalName, string? Copyright, bool Bold, bool Italic,
    bool ShiftJis, int Ascent, int Descent, int Leading, int KerningPairs, IReadOnlyList<SwfGlyph> Glyphs);

/// <summary>
/// Local converter from the embedded fonts of the AIR client's <c>css/fonts.swf</c> (DefineFont3, regular and bold
/// "ITC Friz Quadrata") to plain TrueType (glyf, quadratic) files. The companion runs it on each PC against that PC's
/// own client files; the result is cached locally and served on loopback only (<see cref="FrizFontServer"/>), never by
/// the Platform and never to another machine. Output is deterministic. Any malformed input ends in
/// <see cref="InvalidDataException"/> (or a <c>false</c> from <see cref="TryConvert(string, out byte[], out byte[], out string)"/>).
/// Kerning is parsed (counted) but not written: the shipped font carries none.
/// </summary>
public static class FrizFont
{
    public const string Family = "ITC Friz Quadrata";

    /// <summary>Part of the cache file names; bump it when the converter output changes.</summary>
    public const int ConverterVersion = 1;

    private const int SwfEm = 20480;
    private const int TtEm = 2048;
    private const long MaxSwfFileBytes = 32L * 1024 * 1024;
    private const int MaxUncompressedBytes = 64 * 1024 * 1024;
    private const int MaxTotalShapeRecords = 6_000_000;
    private const int MaxGlyphPoints = 20_000;
    private const int MaxCoordinate = 300_000;

    public static bool TryConvert(string swfPath, out byte[] regular, out byte[] bold, out string report)
    {
        regular = bold = [];
        try
        {
            if (!File.Exists(swfPath)) { report = "fonts.swf not found"; return false; }
            if (new FileInfo(swfPath).Length > MaxSwfFileBytes) { report = "fonts.swf is too large"; return false; }
            return TryConvert(File.ReadAllBytes(swfPath), out regular, out bold, out report);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            report = "fonts.swf cannot be read (" + exception.GetType().Name + ")";
            return false;
        }
    }

    /// <summary>Converts the Friz faces of a SWF. <paramref name="bold"/> is empty when the SWF has no bold Friz face.</summary>
    public static bool TryConvert(byte[] swf, out byte[] regular, out byte[] bold, out string report)
    {
        regular = bold = [];
        try
        {
            var friz = ReadFonts(swf).Where(face => face.Name.Contains("Friz", StringComparison.OrdinalIgnoreCase) && !face.Italic).ToList();
            var regularFace = friz.FirstOrDefault(face => !face.Bold);
            if (regularFace is null) { report = "no regular ITC Friz Quadrata face in fonts.swf"; return false; }
            var boldFace = friz.FirstOrDefault(face => face.Bold);
            regular = BuildTrueType(regularFace);
            if (boldFace is not null) bold = BuildTrueType(boldFace);
            var faces = boldFace is null ? 1 : 2;
            var glyphs = regularFace.Glyphs.Count + (boldFace?.Glyphs.Count ?? 0);
            report = string.Create(CultureInfo.InvariantCulture, $"converted {faces} faces, {glyphs} glyphs");
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or OverflowException
            or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            regular = bold = [];
            report = exception is InvalidDataException ? exception.Message : "malformed fonts.swf (" + exception.GetType().Name + ")";
            return false;
        }
    }

    // ---------------------------------------------------------------- SWF reading

    /// <summary>Every DefineFont3 tag of a SWF (FWS or CWS; LZMA-compressed ZWS is rejected).</summary>
    public static IReadOnlyList<SwfFontFace> ReadFonts(byte[] swf)
    {
        var body = Inflate(swf);
        if (body.Length < 1) throw new InvalidDataException("SWF is empty");
        var rectBytes = (5 + 4 * (body[0] >> 3) + 7) / 8;
        var position = rectBytes + 4; // frame rate (2) + frame count (2)
        var raw = new List<SwfFontFace>();
        var names = new Dictionary<int, (string Name, string? Copyright)>();
        var budget = new ShapeBudget();
        while (position + 2 <= body.Length)
        {
            var header = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(position));
            position += 2;
            var code = header >> 6;
            long length = header & 0x3F;
            if (length == 0x3F)
            {
                if (position + 4 > body.Length) throw new InvalidDataException("SWF tag header is truncated");
                length = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(position));
                position += 4;
            }
            if (length > body.Length - position) throw new InvalidDataException("SWF tag overruns the file");
            var end = position + (int)length;
            if (code == 0) break;
            if (code == 75) raw.Add(ReadDefineFont3(new Cursor(body, position, end), budget));
            else if (code == 88 && length >= 3)
            {
                var cursor = new Cursor(body, position, end);
                var id = cursor.U16();
                var name = cursor.CString();
                names[id] = (name, cursor.Remaining > 0 ? cursor.CString() : null);
            }
            position = end;
        }
        return raw.Select(face => names.TryGetValue(face.FontId, out var named)
            ? face with { Name = named.Name, Copyright = named.Copyright }
            : face).ToList();
    }

    private static byte[] Inflate(byte[] swf)
    {
        if (swf is null || swf.Length < 12) throw new InvalidDataException("not a SWF file");
        if (swf[1] != 'W' || swf[2] != 'S') throw new InvalidDataException("not a SWF file");
        switch (swf[0])
        {
            case (byte)'F': return swf[8..];
            case (byte)'Z': throw new InvalidDataException("LZMA-compressed SWF is not supported");
            case (byte)'C':
                try
                {
                    using var input = new MemoryStream(swf, 8, swf.Length - 8);
                    using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    var buffer = new byte[16 * 1024];
                    int read;
                    while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + read > MaxUncompressedBytes) throw new InvalidDataException("SWF expands beyond the size limit");
                        output.Write(buffer, 0, read);
                    }
                    return output.ToArray();
                }
                catch (InvalidDataException) { throw; }
                catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
                { throw new InvalidDataException("SWF zlib stream is corrupt"); }
            default: throw new InvalidDataException("not a SWF file");
        }
    }

    private sealed class ShapeBudget { public int Records; }

    private static SwfFontFace ReadDefineFont3(Cursor tag, ShapeBudget budget)
    {
        var fontId = tag.U16();
        var flags = tag.U8();
        var hasLayout = (flags & 0x80) != 0;
        var shiftJis = (flags & 0x40) != 0;
        var wideOffsets = (flags & 0x08) != 0;
        var wideCodes = (flags & 0x04) != 0;
        var italic = (flags & 0x02) != 0;
        var bold = (flags & 0x01) != 0;
        tag.U8(); // language code
        var internalName = tag.FixedString(tag.U8());
        var count = tag.U16();
        if (!hasLayout) throw new InvalidDataException("DefineFont3 without layout is not supported");

        var tableStart = tag.Position; // glyph and code offsets count from here
        var offsets = new long[count];
        for (var i = 0; i < count; i++) offsets[i] = wideOffsets ? tag.U32() : tag.U16();
        var codeTableOffset = wideOffsets ? tag.U32() : tag.U16();

        var shapes = new List<List<SwfPoint[]>>(count);
        for (var i = 0; i < count; i++) shapes.Add(ReadShape(tag, tableStart, offsets[i], budget));

        var codes = new int[count];
        tag.Seek(tableStart, codeTableOffset);
        for (var i = 0; i < count; i++) codes[i] = wideCodes ? tag.U16() : tag.U8();

        var ascent = tag.I16();
        var descent = tag.I16();
        var leading = tag.I16();
        var advances = new int[count];
        for (var i = 0; i < count; i++) advances[i] = tag.I16();
        for (var i = 0; i < count; i++) tag.SkipRect();
        var kerningPairs = tag.U16();

        var result = new SwfGlyph[count];
        for (var i = 0; i < count; i++) result[i] = new SwfGlyph(codes[i], advances[i], shapes[i]);
        return new SwfFontFace(fontId, internalName, internalName, null, bold, italic, shiftJis, ascent, descent, leading, kerningPairs, result);
    }

    /// <summary>
    /// SHAPE of one glyph: style changes (move, fill 0/1), straight and quadratic edges. A contour whose only fill is
    /// style 0 (fill on the left) is reversed so every contour has its fill on the same side.
    /// </summary>
    private static List<SwfPoint[]> ReadShape(Cursor tag, int tableStart, long offset, ShapeBudget budget)
    {
        tag.Seek(tableStart, offset);
        var fillBits = tag.Bits(4);
        var lineBits = tag.Bits(4);
        var contours = new List<SwfPoint[]>();
        var points = new List<SwfPoint>();
        var reverse = false;
        var fill0 = 0;
        var fill1 = 0;
        long x = 0, y = 0;

        void Close()
        {
            if (points.Count >= 2 && points[^1].OnCurve && points[^1].X == points[0].X && points[^1].Y == points[0].Y)
                points.RemoveAt(points.Count - 1);
            if (points.Count >= 3)
            {
                var array = points.ToArray();
                if (reverse) Array.Reverse(array);
                contours.Add(array);
            }
            points.Clear();
        }

        void Add(long px, long py, bool onCurve)
        {
            if (Math.Abs(px) > MaxCoordinate || Math.Abs(py) > MaxCoordinate)
                throw new InvalidDataException("glyph coordinate is out of range");
            if (points.Count >= MaxGlyphPoints) throw new InvalidDataException("glyph has too many points");
            points.Add(new SwfPoint((int)px, (int)py, onCurve));
        }

        void EnsureStarted()
        {
            if (points.Count == 0)
            {
                reverse = fill0 != 0 && fill1 == 0;
                Add(x, y, true);
            }
        }

        while (true)
        {
            if (++budget.Records > MaxTotalShapeRecords) throw new InvalidDataException("fonts.swf has too many shape records");
            if (tag.Bits(1) == 0)
            {
                var change = tag.Bits(5);
                if (change == 0) break;
                if ((change & 0x10) != 0) throw new InvalidDataException("glyph shape defines new styles");
                if ((change & 0x01) != 0)
                {
                    var moveBits = tag.Bits(5);
                    var moveX = tag.SignedBits(moveBits);
                    var moveY = tag.SignedBits(moveBits);
                    Close();
                    x = moveX;
                    y = moveY;
                }
                if ((change & 0x02) != 0) fill0 = tag.Bits(fillBits);
                if ((change & 0x04) != 0) fill1 = tag.Bits(fillBits);
                if ((change & 0x08) != 0) tag.Bits(lineBits);
                if (points.Count == 0 && (change & 0x01) != 0) EnsureStarted();
                continue;
            }
            EnsureStarted();
            if (tag.Bits(1) == 1)
            {
                var bits = tag.Bits(4) + 2;
                long dx = 0, dy = 0;
                if (tag.Bits(1) == 1) { dx = tag.SignedBits(bits); dy = tag.SignedBits(bits); }
                else if (tag.Bits(1) == 1) dy = tag.SignedBits(bits);
                else dx = tag.SignedBits(bits);
                x += dx;
                y += dy;
                Add(x, y, true);
            }
            else
            {
                var bits = tag.Bits(4) + 2;
                var controlX = x + tag.SignedBits(bits);
                var controlY = y + tag.SignedBits(bits);
                x = controlX + tag.SignedBits(bits);
                y = controlY + tag.SignedBits(bits);
                Add(controlX, controlY, false);
                Add(x, y, true);
            }
        }
        Close();
        return contours;
    }

    private sealed class Cursor
    {
        private readonly byte[] data;
        private readonly int end;
        private int bitPosition; // absolute, in bits

        public Cursor(byte[] data, int start, int end)
        {
            this.data = data;
            this.end = end;
            bitPosition = start * 8;
        }

        public int Position => (bitPosition + 7) / 8;
        public int Remaining => end - Position;

        public void Seek(int tableStart, long offset)
        {
            if (offset < 0 || tableStart + offset >= end) throw new InvalidDataException("DefineFont3 offset is out of range");
            bitPosition = (int)(tableStart + offset) * 8;
        }

        private int Need(int bytes)
        {
            var at = Position;
            if (bytes < 0 || at + bytes > end) throw new InvalidDataException("DefineFont3 is truncated");
            bitPosition = at * 8;
            return at;
        }

        public int U8() { var at = Need(1); bitPosition += 8; return data[at]; }
        public int U16() { var at = Need(2); bitPosition += 16; return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)); }
        public int I16() { var at = Need(2); bitPosition += 16; return BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(at)); }
        public long U32() { var at = Need(4); bitPosition += 32; return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)); }

        public string FixedString(int length)
        {
            var at = Need(length);
            bitPosition += length * 8;
            return Encoding.UTF8.GetString(data, at, length).TrimEnd('\0');
        }

        public string CString()
        {
            var at = Need(0);
            var stop = Array.IndexOf(data, (byte)0, at, end - at);
            var length = stop < 0 ? end - at : stop - at;
            bitPosition = (at + length + (stop < 0 ? 0 : 1)) * 8;
            return Encoding.UTF8.GetString(data, at, length);
        }

        public int Bits(int count)
        {
            if (count < 0 || count > 31) throw new InvalidDataException("bit field is too wide");
            if (bitPosition + count > end * 8) throw new InvalidDataException("DefineFont3 is truncated");
            var value = 0;
            for (var i = 0; i < count; i++, bitPosition++)
                value = (value << 1) | ((data[bitPosition >> 3] >> (7 - (bitPosition & 7))) & 1);
            return value;
        }

        public long SignedBits(int count)
        {
            if (count == 0) return 0;
            long value = Bits(count);
            return (value & (1L << (count - 1))) != 0 ? value - (1L << count) : value;
        }

        /// <summary>A RECT of the bounds table (byte aligned); the shipped tables are all zero, so the values are ignored.</summary>
        public void SkipRect()
        {
            bitPosition = Need(0) * 8;
            var bits = Bits(5);
            for (var i = 0; i < 4; i++) Bits(bits);
            bitPosition = Position * 8;
        }
    }

    // ---------------------------------------------------------------- TrueType writing

    private readonly record struct TtPoint(int X, int Y, bool On);

    private sealed class TtGlyph
    {
        public List<TtPoint[]> Contours { get; } = [];
        public int Advance { get; set; }
        public int XMin { get; set; }
        public int YMin { get; set; }
        public int XMax { get; set; }
        public int YMax { get; set; }
        public int PointCount => Contours.Sum(contour => contour.Length);
    }

    private static int Div10(int value) => value >= 0 ? (value + 5) / 10 : -((-value + 5) / 10);

    private static long SignedArea(TtPoint[] contour)
    {
        long area = 0;
        for (var i = 0; i < contour.Length; i++)
        {
            var a = contour[i];
            var b = contour[(i + 1) % contour.Length];
            area += (long)a.X * b.Y - (long)b.X * a.Y;
        }
        return area;
    }

    /// <summary>Scales a SWF glyph to the 2048 em square, flips y up and gives outer contours the TrueType clockwise winding.</summary>
    private static TtGlyph ToTrueType(SwfGlyph glyph)
    {
        var result = new TtGlyph { Advance = Math.Clamp(Div10(glyph.Advance), 0, 65535) };
        foreach (var contour in glyph.Contours)
        {
            var points = new List<TtPoint>(contour.Length);
            foreach (var point in contour)
            {
                var scaled = new TtPoint(Div10(point.X), -Div10(point.Y), point.OnCurve);
                // +-16383 keeps every point delta inside the 16-bit range of the glyf coordinate arrays.
                if (Math.Abs(scaled.X) > 16383 || Math.Abs(scaled.Y) > 16383)
                    throw new InvalidDataException("glyph coordinate is out of range");
                if (points.Count == 0 || points[^1] != scaled) points.Add(scaled);
            }
            if (points.Count > 1 && points[^1] == points[0]) points.RemoveAt(points.Count - 1);
            if (points.Count >= 3) result.Contours.Add(points.ToArray());
        }
        if (result.Contours.Count == 0) return result;
        // The largest contour is an outer one; TrueType wants outers clockwise (negative area with y up).
        var largest = result.Contours.MaxBy(contour => Math.Abs(SignedArea(contour)))!;
        if (SignedArea(largest) > 0)
            for (var i = 0; i < result.Contours.Count; i++) Array.Reverse(result.Contours[i]);
        var all = result.Contours.SelectMany(contour => contour).ToList();
        result.XMin = all.Min(p => p.X);
        result.YMin = all.Min(p => p.Y);
        result.XMax = all.Max(p => p.X);
        result.YMax = all.Max(p => p.Y);
        return result;
    }

    private static TtGlyph NotDef()
    {
        var glyph = new TtGlyph { Advance = 1024, XMin = 128, YMin = 0, XMax = 896, YMax = 1400 };
        glyph.Contours.Add([new(128, 0, true), new(128, 1400, true), new(896, 1400, true), new(896, 0, true)]);
        glyph.Contours.Add([new(256, 128, true), new(768, 128, true), new(768, 1272, true), new(256, 1272, true)]);
        return glyph;
    }

    private sealed class Be
    {
        private readonly MemoryStream stream = new();
        public int Length => (int)stream.Length;
        public void U8(int value) => stream.WriteByte((byte)value);
        public void U16(int value) { U8(value >> 8); U8(value); }
        public void U32(uint value) { U16((int)(value >> 16)); U16((int)(value & 0xFFFF)); }
        public void Bytes(ReadOnlySpan<byte> bytes) => stream.Write(bytes);
        public void Pad4() { while (stream.Length % 4 != 0) stream.WriteByte(0); }
        public byte[] ToArray() => stream.ToArray();
    }

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (var j = 0; j < 4; j++) word = (word << 8) | (i + j < data.Length ? data[i + j] : (uint)0);
            sum += word;
        }
        return sum;
    }

    public static byte[] BuildTrueType(SwfFontFace face)
    {
        if (face.ShiftJis) throw new InvalidDataException("ShiftJIS font codes are not supported");
        var glyphs = new List<TtGlyph> { NotDef() };
        glyphs.AddRange(face.Glyphs.Select(ToTrueType));
        if (glyphs.Count > 65535) throw new InvalidDataException("too many glyphs");

        // cmap: first glyph wins a duplicated code; the BMP terminator, NUL and surrogates are never mapped.
        var map = new SortedDictionary<int, int>();
        for (var i = 0; i < face.Glyphs.Count; i++)
        {
            var code = face.Glyphs[i].Code;
            if (code is 0 or 0xFFFF or (>= 0xD800 and <= 0xDFFF)) continue;
            map.TryAdd(code, i + 1);
        }

        // glyf + loca
        var glyf = new Be();
        var offsets = new List<int>();
        foreach (var glyph in glyphs)
        {
            offsets.Add(glyf.Length);
            if (glyph.Contours.Count == 0) continue;
            WriteGlyph(glyf, glyph);
            glyf.Pad4();
        }
        offsets.Add(glyf.Length);
        var shortLoca = glyf.Length / 2 <= 0xFFFF;
        var loca = new Be();
        foreach (var offset in offsets)
        {
            if (shortLoca) loca.U16(offset / 2);
            else loca.U32((uint)offset);
        }

        var outlined = glyphs.Where(glyph => glyph.Contours.Count > 0).ToList();
        int xMin = outlined.Min(g => g.XMin), yMin = outlined.Min(g => g.YMin), xMax = outlined.Max(g => g.XMax), yMax = outlined.Max(g => g.YMax);
        var ascent = Math.Clamp(Div10(face.Ascent), 0, short.MaxValue);
        var descent = Math.Clamp(Div10(face.Descent), 0, short.MaxValue);
        var lineGap = Math.Clamp(Div10(face.Leading), 0, short.MaxValue);
        var advanceMax = glyphs.Max(g => g.Advance);

        var head = new Be();
        head.U32(0x00010000); head.U32(0x00010000); // version 1.0, revision 1.0
        head.U32(0); head.U32(0x5F0F3CF5);          // checkSumAdjustment (patched later), magic
        head.U16(0x000B); head.U16(TtEm);           // flags: y0 baseline, lsb at x=0, rounded ppem; unitsPerEm
        var stamp = (long)(new DateTime(2011, 1, 1, 0, 0, 0, DateTimeKind.Utc) - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        head.U32((uint)(stamp >> 32)); head.U32((uint)stamp); head.U32((uint)(stamp >> 32)); head.U32((uint)stamp);
        head.U16(xMin); head.U16(yMin); head.U16(xMax); head.U16(yMax);
        head.U16((face.Bold ? 1 : 0) | (face.Italic ? 2 : 0)); // macStyle
        head.U16(8); head.U16(2);                              // lowestRecPPEM, fontDirectionHint
        head.U16(shortLoca ? 0 : 1); head.U16(0);              // indexToLocFormat, glyphDataFormat

        var hmtx = new Be();
        foreach (var glyph in glyphs)
        {
            hmtx.U16(glyph.Advance);
            hmtx.U16(glyph.Contours.Count == 0 ? 0 : glyph.XMin);
        }

        var hhea = new Be();
        hhea.U32(0x00010000); hhea.U16(ascent); hhea.U16(-descent); hhea.U16(lineGap);
        hhea.U16(advanceMax);
        hhea.U16(outlined.Min(g => g.XMin));
        hhea.U16(outlined.Min(g => g.Advance - g.XMax));
        hhea.U16(xMax);
        hhea.U16(1); hhea.U16(0); hhea.U16(0);
        for (var i = 0; i < 4; i++) hhea.U16(0);
        hhea.U16(0); hhea.U16(glyphs.Count);

        var maxp = new Be();
        maxp.U32(0x00010000); maxp.U16(glyphs.Count);
        maxp.U16(glyphs.Max(g => g.PointCount)); maxp.U16(glyphs.Max(g => g.Contours.Count));
        maxp.U16(0); maxp.U16(0); maxp.U16(2);
        for (var i = 0; i < 8; i++) maxp.U16(0);

        var post = new Be();
        post.U32(0x00030000); post.U32(0);
        post.U16(-205); post.U16(102);
        for (var i = 0; i < 5; i++) post.U32(0);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["OS/2"] = BuildOs2(face, glyphs, map, ascent, descent, lineGap),
            ["cmap"] = BuildCmap(map),
            ["glyf"] = glyf.ToArray(),
            ["head"] = head.ToArray(),
            ["hhea"] = hhea.ToArray(),
            ["hmtx"] = hmtx.ToArray(),
            ["loca"] = loca.ToArray(),
            ["maxp"] = maxp.ToArray(),
            ["name"] = BuildName(face),
            ["post"] = post.ToArray(),
        };
        return Assemble(tables);
    }

    private static void WriteGlyph(Be output, TtGlyph glyph)
    {
        output.U16(glyph.Contours.Count);
        output.U16(glyph.XMin); output.U16(glyph.YMin); output.U16(glyph.XMax); output.U16(glyph.YMax);
        var end = -1;
        foreach (var contour in glyph.Contours)
        {
            end += contour.Length;
            output.U16(end);
        }
        output.U16(0); // no instructions
        var all = glyph.Contours.SelectMany(contour => contour).ToArray();

        var flags = new byte[all.Length];
        int previousX = 0, previousY = 0;
        var xData = new Be();
        var yData = new Be();
        for (var i = 0; i < all.Length; i++)
        {
            var dx = all[i].X - previousX;
            var dy = all[i].Y - previousY;
            previousX = all[i].X;
            previousY = all[i].Y;
            var flag = all[i].On ? 1 : 0;
            if (dx == 0) flag |= 0x10;
            else if (Math.Abs(dx) <= 255) { flag |= 0x02 | (dx > 0 ? 0x10 : 0); xData.U8(Math.Abs(dx)); }
            else xData.U16(dx);
            if (dy == 0) flag |= 0x20;
            else if (Math.Abs(dy) <= 255) { flag |= 0x04 | (dy > 0 ? 0x20 : 0); yData.U8(Math.Abs(dy)); }
            else yData.U16(dy);
            flags[i] = (byte)flag;
        }
        for (var i = 0; i < flags.Length;)
        {
            var run = 1;
            while (i + run < flags.Length && flags[i + run] == flags[i] && run < 256) run++;
            if (run > 1) { output.U8(flags[i] | 0x08); output.U8(run - 1); }
            else output.U8(flags[i]);
            i += run;
        }
        output.Bytes(xData.ToArray());
        output.Bytes(yData.ToArray());
    }

    private static byte[] BuildCmap(SortedDictionary<int, int> map)
    {
        // Runs of consecutive codes with consecutive glyph ids become one idDelta segment.
        var segments = new List<(int Start, int End, int Glyph)>();
        foreach (var (code, glyph) in map)
        {
            if (segments.Count > 0)
            {
                var last = segments[^1];
                if (code == last.End + 1 && glyph == last.Glyph + (code - last.Start))
                {
                    segments[^1] = last with { End = code };
                    continue;
                }
            }
            segments.Add((code, code, glyph));
        }
        segments.Add((0xFFFF, 0xFFFF, 0));
        var count = segments.Count;
        var length = 16 + count * 8;
        if (length > 0xFFFF) throw new InvalidDataException("cmap is too large");
        var power = 1;
        var selector = 0;
        while (power * 2 <= count) { power *= 2; selector++; }

        var cmap = new Be();
        cmap.U16(0); cmap.U16(2);                       // version, two encoding records
        cmap.U16(0); cmap.U16(3); cmap.U32(20);         // Unicode BMP
        cmap.U16(3); cmap.U16(1); cmap.U32(20);         // Windows Unicode BMP
        cmap.U16(4); cmap.U16(length); cmap.U16(0);     // format 4, length, language
        cmap.U16(count * 2); cmap.U16(power * 2); cmap.U16(selector); cmap.U16(count * 2 - power * 2);
        foreach (var segment in segments) cmap.U16(segment.End);
        cmap.U16(0);
        foreach (var segment in segments) cmap.U16(segment.Start);
        foreach (var segment in segments) cmap.U16((segment.Glyph - segment.Start) & 0xFFFF);
        foreach (var _ in segments) cmap.U16(0);
        return cmap.ToArray();
    }

    private static byte[] BuildName(SwfFontFace face)
    {
        var style = (face.Bold, face.Italic) switch
        {
            (false, false) => "Regular",
            (true, false) => "Bold",
            (false, true) => "Italic",
            _ => "Bold Italic",
        };
        // name strings carry 16-bit lengths; a hostile SWF must not be able to overflow them.
        var family = string.IsNullOrWhiteSpace(face.Name) ? Family : face.Name[..Math.Min(face.Name.Length, 128)];
        var full = style == "Regular" ? family : family + " " + style;
        var postScript = new string((family + "-" + style.Replace(" ", string.Empty)).Where(c => c is > ' ' and < '\u007F' && "[](){}<>/%".IndexOf(c) < 0).ToArray());
        var strings = new List<(int Id, string Text)>
        {
            (1, family), (2, style), (3, "Amberline local conversion v" + ConverterVersion.ToString(CultureInfo.InvariantCulture) + "; " + postScript),
            (4, full), (5, "Version 1.0"), (6, postScript),
        };
        if (!string.IsNullOrWhiteSpace(face.Copyright)) strings.Insert(0, (0, face.Copyright[..Math.Min(face.Copyright.Length, 1000)]));

        // Records are sorted by platform, encoding, language, name id: Macintosh Roman first, then Windows Unicode.
        var records = new List<(int Platform, int Encoding, int Language, int Id, byte[] Text)>();
        foreach (var (id, text) in strings)
        {
            var ascii = new string(text.Select(c => c is >= ' ' and < '\u007F' ? c : '?').ToArray());
            records.Add((1, 0, 0, id, Encoding.ASCII.GetBytes(ascii)));
        }
        foreach (var (id, text) in strings) records.Add((3, 1, 0x409, id, Encoding.BigEndianUnicode.GetBytes(text)));

        var name = new Be();
        name.U16(0); name.U16(records.Count); name.U16(6 + records.Count * 12);
        var offset = 0;
        foreach (var record in records)
        {
            name.U16(record.Platform); name.U16(record.Encoding); name.U16(record.Language); name.U16(record.Id);
            name.U16(record.Text.Length); name.U16(offset);
            offset += record.Text.Length;
        }
        foreach (var record in records) name.Bytes(record.Text);
        return name.ToArray();
    }

    private static readonly (int Bit, int From, int To)[] UnicodeRanges =
    [
        (0, 0x0020, 0x007F), (1, 0x0080, 0x00FF), (2, 0x0100, 0x017F), (3, 0x0180, 0x024F), (4, 0x0250, 0x02AF),
        (5, 0x02B0, 0x02FF), (6, 0x0300, 0x036F), (7, 0x0370, 0x03FF), (31, 0x2000, 0x206F), (32, 0x2070, 0x209F),
        (33, 0x20A0, 0x20CF), (35, 0x2100, 0x214F), (36, 0x2150, 0x218F), (37, 0x2190, 0x21FF), (38, 0x2200, 0x22FF),
        (60, 0xE000, 0xF8FF), (62, 0xFB00, 0xFB4F),
    ];

    private static byte[] BuildOs2(SwfFontFace face, List<TtGlyph> glyphs, SortedDictionary<int, int> map,
        int ascent, int descent, int lineGap)
    {
        var mapped = map.Values.Select(glyph => glyphs[glyph]).Where(glyph => glyph.Advance > 0).ToList();
        var average = mapped.Count == 0 ? 0 : (int)Math.Round(mapped.Average(glyph => glyph.Advance));
        var ranges = new uint[4];
        foreach (var (bit, from, to) in UnicodeRanges)
            if (map.Keys.Any(code => code >= from && code <= to)) ranges[bit / 32] |= 1u << (bit % 32);
        uint codePages = 0;
        if (map.ContainsKey(0x00E9)) codePages |= 1u;      // 1252 Latin 1
        if (map.ContainsKey(0x0141)) codePages |= 2u;      // 1250 Latin 2
        int HeightOf(char c) => map.TryGetValue(c, out var glyph) ? glyphs[glyph].YMax : 0;

        var os2 = new Be();
        os2.U16(2); os2.U16(average); os2.U16(face.Bold ? 700 : 400); os2.U16(5); os2.U16(0);
        os2.U16(TtEm * 65 / 100); os2.U16(TtEm * 60 / 100); os2.U16(0); os2.U16(TtEm * 14 / 100);   // subscript
        os2.U16(TtEm * 65 / 100); os2.U16(TtEm * 60 / 100); os2.U16(0); os2.U16(TtEm * 48 / 100);   // superscript
        os2.U16(TtEm / 20); os2.U16(TtEm * 26 / 100);                                               // strikeout
        os2.U16(0);
        for (var i = 0; i < 10; i++) os2.U8(0);
        foreach (var range in ranges) os2.U32(range);
        os2.Bytes("LEMU"u8);
        os2.U16((face.Italic ? 0x01 : 0) | (face.Bold ? 0x20 : 0) | (!face.Bold && !face.Italic ? 0x40 : 0)); // fsSelection
        os2.U16(map.Count == 0 ? 0 : Math.Min(map.Keys.Min(), 0xFFFF)); os2.U16(map.Count == 0 ? 0 : Math.Min(map.Keys.Max(), 0xFFFF));
        os2.U16(ascent); os2.U16(-descent); os2.U16(lineGap);
        // Win metrics are the SWF layout ascent/descent (the original font's own cell), not the extremes of its rare accented glyphs.
        os2.U16(ascent); os2.U16(descent);
        os2.U32(codePages); os2.U32(0);
        os2.U16(HeightOf('x')); os2.U16(HeightOf('H'));
        os2.U16(0); os2.U16(0x20); os2.U16(0);
        return os2.ToArray();
    }

    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        var count = tables.Count;
        var power = 1;
        var selector = 0;
        while (power * 2 <= count) { power *= 2; selector++; }

        var font = new Be();
        font.U32(0x00010000); font.U16(count); font.U16(power * 16); font.U16(selector); font.U16(count * 16 - power * 16);
        var offset = 12 + count * 16;
        foreach (var (tag, data) in tables)
        {
            font.Bytes(Encoding.ASCII.GetBytes(tag));
            font.U32(Checksum(data));
            font.U32((uint)offset);
            font.U32((uint)data.Length);
            offset += (data.Length + 3) & ~3;
        }
        var headOffset = 0;
        foreach (var (tag, data) in tables)
        {
            if (tag == "head") headOffset = font.Length;
            font.Bytes(data);
            font.Pad4();
        }
        var bytes = font.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(headOffset + 8), 0xB1B0AFBAu - Checksum(bytes));
        return bytes;
    }
}
