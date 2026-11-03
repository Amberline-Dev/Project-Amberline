using System.Buffers.Binary;

namespace Amberline.Network;

/// <summary>
/// A small bounded little-endian primitive packet reader/writer.
/// </summary>
public sealed class PacketBuffer
{
    private readonly byte[] _buffer;
    private readonly bool _readOnly;
    private int _length;
    private int _position;

    /// <summary>Creates a bounded writer with the given byte capacity.</summary>
    public PacketBuffer(int capacity = 256)
    {
        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _buffer = new byte[capacity];
    }

    /// <summary>Creates a read-only buffer over a copied packet.</summary>
    public PacketBuffer(byte[] data) : this(data.AsSpan())
    {
    }

    /// <summary>Creates a read-only buffer over a copied packet.</summary>
    public PacketBuffer(ReadOnlySpan<byte> data)
    {
        _buffer = data.ToArray();
        _length = _buffer.Length;
        _readOnly = true;
    }

    public int Capacity => _buffer.Length;

    public int Length => _length;

    public int Position
    {
        get => _position;
        set
        {
            if ((uint)value > (uint)_length)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _position = value;
        }
    }

    public int Remaining => _length - _position;

    public bool IsReadOnly => _readOnly;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    public byte[] ToArray() => WrittenSpan.ToArray();

    public void Rewind() => _position = 0;

    public void Clear()
    {
        EnsureWritable();
        _position = 0;
        _length = 0;
    }

    public void WriteByte(byte value)
    {
        EnsureWritable(1);
        _buffer[_position++] = value;
        UpdateLength();
    }

    public void WriteSByte(sbyte value) => WriteByte(unchecked((byte)value));

    public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteBytes(ReadOnlySpan<byte> values)
    {
        EnsureWritable(values.Length);
        values.CopyTo(_buffer.AsSpan(_position));
        _position += values.Length;
        UpdateLength();
    }

    public void WriteInt16(short value)
    {
        EnsureWritable(sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(_position), value);
        _position += sizeof(short);
        UpdateLength();
    }

    public void WriteUInt16(ushort value)
    {
        EnsureWritable(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_position), value);
        _position += sizeof(ushort);
        UpdateLength();
    }

    public void WriteInt32(int value)
    {
        EnsureWritable(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_position), value);
        _position += sizeof(int);
        UpdateLength();
    }

    public void WriteUInt32(uint value)
    {
        EnsureWritable(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_position), value);
        _position += sizeof(uint);
        UpdateLength();
    }

    public void WriteInt64(long value)
    {
        EnsureWritable(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_position), value);
        _position += sizeof(long);
        UpdateLength();
    }

    public void WriteUInt64(ulong value)
    {
        EnsureWritable(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_position), value);
        _position += sizeof(ulong);
        UpdateLength();
    }

    public void WriteSingle(float value)
    {
        WriteInt32(BitConverter.SingleToInt32Bits(value));
    }

    /// <summary>Explicitly narrows a C# double to the wire's IEEE-754 float32.</summary>
    public void WriteSingle(double value)
    {
        if (double.IsFinite(value) && (value > float.MaxValue || value < -float.MaxValue))
        {
            throw new OverflowException("The finite value does not fit in an IEEE-754 float32.");
        }

        WriteSingle((float)value);
    }

    public void WriteFloat(float value) => WriteSingle(value);

    /// <summary>Explicitly narrows a C# double to the wire's IEEE-754 float32.</summary>
    public void WriteFloat(double value) => WriteSingle(value);

    public void WriteDouble(double value)
    {
        WriteInt64(BitConverter.DoubleToInt64Bits(value));
    }

    public byte ReadByte()
    {
        EnsureReadable(1);
        return _buffer[_position++];
    }

    public sbyte ReadSByte() => unchecked((sbyte)ReadByte());

    public bool ReadBoolean() => ReadByte() != 0;

    public byte[] ReadBytes(int count)
    {
        EnsureReadable(count);
        var values = _buffer.AsSpan(_position, count).ToArray();
        _position += count;
        return values;
    }

    public short ReadInt16()
    {
        EnsureReadable(sizeof(short));
        var value = BinaryPrimitives.ReadInt16LittleEndian(_buffer.AsSpan(_position));
        _position += sizeof(short);
        return value;
    }

    public ushort ReadUInt16()
    {
        EnsureReadable(sizeof(ushort));
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(_position));
        _position += sizeof(ushort);
        return value;
    }

    public int ReadInt32()
    {
        EnsureReadable(sizeof(int));
        var value = BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(_position));
        _position += sizeof(int);
        return value;
    }

    public uint ReadUInt32()
    {
        EnsureReadable(sizeof(uint));
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(_position));
        _position += sizeof(uint);
        return value;
    }

    public long ReadInt64()
    {
        EnsureReadable(sizeof(long));
        var value = BinaryPrimitives.ReadInt64LittleEndian(_buffer.AsSpan(_position));
        _position += sizeof(long);
        return value;
    }

    public ulong ReadUInt64()
    {
        EnsureReadable(sizeof(ulong));
        var value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.AsSpan(_position));
        _position += sizeof(ulong);
        return value;
    }

    public float ReadSingle() => BitConverter.Int32BitsToSingle(ReadInt32());

    public float ReadFloat() => ReadSingle();

    public double ReadDouble() => BitConverter.Int64BitsToDouble(ReadInt64());

    private void UpdateLength()
    {
        if (_position > _length)
        {
            _length = _position;
        }
    }

    private void EnsureWritable(int count = 0)
    {
        if (_readOnly)
        {
            throw new InvalidOperationException("This PacketBuffer is read-only.");
        }

        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (count > _buffer.Length - _position)
        {
            throw new InvalidOperationException("The packet write exceeds the buffer bounds.");
        }
    }

    private void EnsureReadable(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (count > _length - _position)
        {
            throw new InvalidOperationException("The packet read exceeds the buffer bounds.");
        }
    }
}
