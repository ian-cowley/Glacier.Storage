namespace Glacier.Storage.Arrow;

using System;
using System.Collections.Generic;

public enum ArrowTypeId : byte
{
    Null = 0,
    Boolean = 1,
    Int8 = 2,
    UInt8 = 3,
    Int16 = 4,
    UInt16 = 5,
    Int32 = 6,
    UInt32 = 7,
    Int64 = 8,
    UInt64 = 9,
    Float = 10,
    Double = 11,
    Utf8 = 12,
    Binary = 13,
    Date32 = 14,
    Date64 = 15,
    Timestamp = 16,
    Duration = 17,
    Decimal128 = 18,
    Time64 = 19
}

public readonly record struct ArrowType(ArrowTypeId Id)
{
    public static readonly ArrowType Null = new(ArrowTypeId.Null);
    public static readonly ArrowType Boolean = new(ArrowTypeId.Boolean);
    public static readonly ArrowType Int8 = new(ArrowTypeId.Int8);
    public static readonly ArrowType UInt8 = new(ArrowTypeId.UInt8);
    public static readonly ArrowType Int16 = new(ArrowTypeId.Int16);
    public static readonly ArrowType UInt16 = new(ArrowTypeId.UInt16);
    public static readonly ArrowType Int32 = new(ArrowTypeId.Int32);
    public static readonly ArrowType UInt32 = new(ArrowTypeId.UInt32);
    public static readonly ArrowType Int64 = new(ArrowTypeId.Int64);
    public static readonly ArrowType UInt64 = new(ArrowTypeId.UInt64);
    public static readonly ArrowType Float = new(ArrowTypeId.Float);
    public static readonly ArrowType Double = new(ArrowTypeId.Double);
    public static readonly ArrowType Utf8 = new(ArrowTypeId.Utf8);
    public static readonly ArrowType Binary = new(ArrowTypeId.Binary);
    public static readonly ArrowType Date32 = new(ArrowTypeId.Date32);
    public static readonly ArrowType Date64 = new(ArrowTypeId.Date64);
    public static readonly ArrowType Timestamp = new(ArrowTypeId.Timestamp);
    public static readonly ArrowType Duration = new(ArrowTypeId.Duration);
    public static readonly ArrowType Decimal128 = new(ArrowTypeId.Decimal128);
    public static readonly ArrowType Time64 = new(ArrowTypeId.Time64);

    public int ByteWidth => Id switch
    {
        ArrowTypeId.Boolean or ArrowTypeId.Int8 or ArrowTypeId.UInt8 => 1,
        ArrowTypeId.Int16 or ArrowTypeId.UInt16 => 2,
        ArrowTypeId.Int32 or ArrowTypeId.UInt32 or ArrowTypeId.Float or ArrowTypeId.Date32 => 4,
        ArrowTypeId.Int64 or ArrowTypeId.UInt64 or ArrowTypeId.Double or ArrowTypeId.Date64 or ArrowTypeId.Timestamp or ArrowTypeId.Duration or ArrowTypeId.Time64 => 8,
        ArrowTypeId.Decimal128 => 16,
        _ => 0
    };
}

public sealed class ArrowField
{
    public string Name { get; }
    public ArrowType DataType { get; }
    public bool IsNullable { get; }

    public ArrowField(string name, ArrowType dataType, bool isNullable = true)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        DataType = dataType;
        IsNullable = isNullable;
    }

    public override string ToString() => $"{Name}: {DataType.Id}{(IsNullable ? "?" : "")}";
}

public sealed class ArrowSchema
{
    public IReadOnlyList<ArrowField> Fields { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }

    public ArrowSchema(IEnumerable<ArrowField> fields, IDictionary<string, string>? metadata = null)
    {
        Fields = new List<ArrowField>(fields);
        Metadata = metadata != null ? new Dictionary<string, string>(metadata) : new Dictionary<string, string>();
    }

    public int FieldCount => Fields.Count;

    public ArrowField GetField(int index) => Fields[index];
    public ArrowField GetFieldByIndex(int index) => Fields[index];

    public int GetFieldIndex(string name)
    {
        for (int i = 0; i < Fields.Count; i++)
        {
            if (string.Equals(Fields[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}

public readonly record struct ArrowBuffer(long Offset, long Length);

public readonly record struct ArrowFieldNode(long Length, long NullCount);

public enum ArrowMessageType : byte
{
    None = 0,
    Schema = 1,
    DictionaryBatch = 2,
    RecordBatch = 3
}
