namespace Glacier.Storage.Parquet;

using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

public enum ThriftType : byte
{
    Stop = 0,
    BooleanTrue = 1,
    BooleanFalse = 2,
    Byte = 3,
    I16 = 4,
    I32 = 5,
    I64 = 6,
    Double = 7,
    Binary = 8,
    List = 9,
    Set = 10,
    Map = 11,
    Struct = 12
}

public sealed class ThriftCompactWriter
{
    private readonly Stream _stream;
    private short _lastFieldId;

    public ThriftCompactWriter(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public void WriteStructBegin()
    {
        _lastFieldId = 0;
    }

    public void WriteStructEnd()
    {
        _stream.WriteByte((byte)ThriftType.Stop);
    }

    public void WriteFieldBegin(short fieldId, ThriftType type)
    {
        short delta = (short)(fieldId - _lastFieldId);
        if (delta > 0 && delta <= 15)
        {
            byte header = (byte)((delta << 4) | (byte)type);
            _stream.WriteByte(header);
        }
        else
        {
            _stream.WriteByte((byte)type);
            WriteI16(fieldId);
        }
        _lastFieldId = fieldId;
    }

    public void WriteFieldStop()
    {
        _stream.WriteByte((byte)ThriftType.Stop);
    }

    public void WriteBool(short fieldId, bool value)
    {
        var type = value ? ThriftType.BooleanTrue : ThriftType.BooleanFalse;
        WriteFieldBegin(fieldId, type);
    }

    public void WriteByte(byte value)
    {
        _stream.WriteByte(value);
    }

    public void WriteI16(short value)
    {
        WriteVarint32((uint)ZigZagEncode32(value));
    }

    public void WriteI32(int value)
    {
        WriteVarint32((uint)ZigZagEncode32(value));
    }

    public void WriteI64(long value)
    {
        WriteVarint64((ulong)ZigZagEncode64(value));
    }

    public void WriteDouble(double value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    public void WriteBinary(ReadOnlySpan<byte> bytes)
    {
        WriteVarint32((uint)bytes.Length);
        _stream.Write(bytes);
    }

    public void WriteString(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteBinary(bytes);
    }

    public void WriteListBegin(ThriftType elemType, int size)
    {
        if (size <= 14)
        {
            byte header = (byte)((size << 4) | (byte)elemType);
            _stream.WriteByte(header);
        }
        else
        {
            byte header = (byte)(0xF0 | (byte)elemType);
            _stream.WriteByte(header);
            WriteVarint32((uint)size);
        }
    }

    private static int ZigZagEncode32(int n) => (n << 1) ^ (n >> 31);
    private static long ZigZagEncode64(long n) => (n << 1) ^ (n >> 63);

    private void WriteVarint32(uint n)
    {
        while ((n & ~0x7Fu) != 0)
        {
            _stream.WriteByte((byte)((n & 0x7F) | 0x80));
            n >>= 7;
        }
        _stream.WriteByte((byte)n);
    }

    private void WriteVarint64(ulong n)
    {
        while ((n & ~0x7FUL) != 0)
        {
            _stream.WriteByte((byte)((n & 0x7F) | 0x80));
            n >>= 7;
        }
        _stream.WriteByte((byte)n);
    }
}

public sealed class ThriftCompactReader
{
    private readonly Stream _stream;
    private short _lastFieldId;

    public ThriftCompactReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public void ReadStructBegin()
    {
        _lastFieldId = 0;
    }

    public bool ReadFieldBegin(out short fieldId, out ThriftType type)
    {
        int b = _stream.ReadByte();
        if (b <= 0)
        {
            fieldId = 0;
            type = ThriftType.Stop;
            return false;
        }

        byte header = (byte)b;
        type = (ThriftType)(header & 0x0F);
        if (type == ThriftType.Stop)
        {
            fieldId = 0;
            return false;
        }

        short delta = (short)((header >> 4) & 0x0F);
        if (delta != 0)
        {
            fieldId = (short)(_lastFieldId + delta);
        }
        else
        {
            fieldId = ReadI16();
        }

        _lastFieldId = fieldId;
        return true;
    }

    public byte ReadByte()
    {
        int b = _stream.ReadByte();
        if (b < 0) throw new EndOfStreamException();
        return (byte)b;
    }

    public short ReadI16()
    {
        return (short)ZigZagDecode32((int)ReadVarint32());
    }

    public int ReadI32()
    {
        return ZigZagDecode32((int)ReadVarint32());
    }

    public long ReadI64()
    {
        return ZigZagDecode64((long)ReadVarint64());
    }

    public double ReadDouble()
    {
        Span<byte> bytes = stackalloc byte[8];
        ReadExact(bytes);
        return BinaryPrimitives.ReadDoubleLittleEndian(bytes);
    }

    public byte[] ReadBinary()
    {
        int length = (int)ReadVarint32();
        var bytes = new byte[length];
        ReadExact(bytes);
        return bytes;
    }

    public string ReadString()
    {
        byte[] bytes = ReadBinary();
        return Encoding.UTF8.GetString(bytes);
    }

    public void ReadListBegin(out ThriftType elemType, out int size)
    {
        byte header = ReadByte();
        elemType = (ThriftType)(header & 0x0F);
        int s = (header >> 4) & 0x0F;
        if (s == 15)
        {
            size = (int)ReadVarint32();
        }
        else
        {
            size = s;
        }
    }

    public void Skip(ThriftType type)
    {
        switch (type)
        {
            case ThriftType.BooleanTrue:
            case ThriftType.BooleanFalse:
                break;
            case ThriftType.Byte:
                ReadByte();
                break;
            case ThriftType.I16:
                ReadI16();
                break;
            case ThriftType.I32:
                ReadI32();
                break;
            case ThriftType.I64:
                ReadI64();
                break;
            case ThriftType.Double:
                ReadDouble();
                break;
            case ThriftType.Binary:
                ReadBinary();
                break;
            case ThriftType.List:
            case ThriftType.Set:
                ReadListBegin(out var elemType, out int listSize);
                for (int i = 0; i < listSize; i++) Skip(elemType);
                break;
            case ThriftType.Struct:
                ReadStructBegin();
                while (ReadFieldBegin(out _, out var fType)) Skip(fType);
                break;
        }
    }

    private static int ZigZagDecode32(int n) => (int)((uint)n >> 1) ^ -(n & 1);
    private static long ZigZagDecode64(long n) => (long)((ulong)n >> 1) ^ -(n & 1);

    private uint ReadVarint32()
    {
        uint result = 0;
        int shift = 0;
        while (shift < 32)
        {
            int b = _stream.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
        return result;
    }

    private ulong ReadVarint64()
    {
        ulong result = 0;
        int shift = 0;
        while (shift < 64)
        {
            int b = _stream.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
        return result;
    }

    private void ReadExact(Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int r = _stream.Read(destination[total..]);
            if (r == 0) throw new EndOfStreamException();
            total += r;
        }
    }
}
