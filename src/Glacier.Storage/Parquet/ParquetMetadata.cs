namespace Glacier.Storage.Parquet;

using System;
using System.Collections.Generic;

public enum ParquetType
{
    Boolean = 0,
    Int32 = 1,
    Int64 = 2,
    Int96 = 3,
    Float = 4,
    Double = 5,
    ByteArray = 6,
    FixedLenByteArray = 7
}

public enum FieldRepetitionType
{
    Required = 0,
    Optional = 1,
    Repeated = 2
}

public enum CompressionCodec
{
    Uncompressed = 0,
    Snappy = 1,
    Gzip = 2,
    Lzo = 3,
    Brotli = 4,
    Lz4 = 5,
    Zstd = 6
}

public enum ParquetEncoding
{
    Plain = 0,
    PlainDictionary = 2,
    Rle = 3,
    BitPacked = 4,
    DeltaBinaryPacked = 5,
    DeltaLengthByteArray = 6,
    DeltaByteArray = 7,
    RleDictionary = 8
}

public enum PageType
{
    DataPage = 0,
    IndexPage = 1,
    DictionaryPage = 2,
    DataPageV2 = 3
}

public sealed class SchemaElement
{
    public ParquetType? Type { get; set; }
    public FieldRepetitionType? RepetitionType { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? NumChildren { get; set; }
}

public sealed class ColumnMetaData
{
    public ParquetType Type { get; set; }
    public List<ParquetEncoding> Encodings { get; set; } = new();
    public List<string> PathInSchema { get; set; } = new();
    public CompressionCodec Codec { get; set; }
    public long NumValues { get; set; }
    public long TotalUncompressedSize { get; set; }
    public long TotalCompressedSize { get; set; }
    public long DataPageOffset { get; set; }
    public long? DictionaryPageOffset { get; set; }
}

public sealed class ColumnChunk
{
    public long FileOffset { get; set; }
    public ColumnMetaData? MetaData { get; set; }
}

public sealed class RowGroup
{
    public List<ColumnChunk> Columns { get; set; } = new();
    public long TotalByteSize { get; set; }
    public long NumRows { get; set; }
}

public sealed class FileMetaData
{
    public int Version { get; set; } = 1;
    public List<SchemaElement> Schema { get; set; } = new();
    public long NumRows { get; set; }
    public List<RowGroup> RowGroups { get; set; } = new();
    public string CreatedBy { get; set; } = "Glacier.Storage";
}

public sealed class DataPageHeader
{
    public int NumValues { get; set; }
    public ParquetEncoding Encoding { get; set; }
    public ParquetEncoding DefinitionLevelEncoding { get; set; }
    public ParquetEncoding RepetitionLevelEncoding { get; set; }
}

public sealed class DictionaryPageHeader
{
    public int NumValues { get; set; }
    public ParquetEncoding Encoding { get; set; }
}

public sealed class PageHeader
{
    public PageType Type { get; set; }
    public int UncompressedPageSize { get; set; }
    public int CompressedPageSize { get; set; }
    public DataPageHeader? DataPageHeader { get; set; }
    public DictionaryPageHeader? DictionaryPageHeader { get; set; }
}
