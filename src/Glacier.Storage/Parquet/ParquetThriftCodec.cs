namespace Glacier.Storage.Parquet;

using System;
using System.Collections.Generic;
using System.IO;

public static class ParquetThriftCodec
{
    public static void WritePageHeader(ThriftCompactWriter writer, PageHeader header)
    {
        writer.WriteStructBegin();

        writer.WriteFieldBegin(1, ThriftType.I32);
        writer.WriteI32((int)header.Type);

        writer.WriteFieldBegin(2, ThriftType.I32);
        writer.WriteI32(header.UncompressedPageSize);

        writer.WriteFieldBegin(3, ThriftType.I32);
        writer.WriteI32(header.CompressedPageSize);

        if (header.DataPageHeader != null)
        {
            writer.WriteFieldBegin(5, ThriftType.Struct);
            writer.WriteStructBegin();

            writer.WriteFieldBegin(1, ThriftType.I32);
            writer.WriteI32(header.DataPageHeader.NumValues);

            writer.WriteFieldBegin(2, ThriftType.I32);
            writer.WriteI32((int)header.DataPageHeader.Encoding);

            writer.WriteFieldBegin(3, ThriftType.I32);
            writer.WriteI32((int)header.DataPageHeader.DefinitionLevelEncoding);

            writer.WriteFieldBegin(4, ThriftType.I32);
            writer.WriteI32((int)header.DataPageHeader.RepetitionLevelEncoding);

            writer.WriteStructEnd();
        }
        else if (header.DictionaryPageHeader != null)
        {
            writer.WriteFieldBegin(7, ThriftType.Struct);
            writer.WriteStructBegin();

            writer.WriteFieldBegin(1, ThriftType.I32);
            writer.WriteI32(header.DictionaryPageHeader.NumValues);

            writer.WriteFieldBegin(2, ThriftType.I32);
            writer.WriteI32((int)header.DictionaryPageHeader.Encoding);

            writer.WriteStructEnd();
        }

        writer.WriteStructEnd();
    }

    public static PageHeader ReadPageHeader(ThriftCompactReader reader)
    {
        var header = new PageHeader();
        reader.ReadStructBegin();

        while (reader.ReadFieldBegin(out short fieldId, out var type))
        {
            switch (fieldId)
            {
                case 1:
                    header.Type = (PageType)reader.ReadI32();
                    break;
                case 2:
                    header.UncompressedPageSize = reader.ReadI32();
                    break;
                case 3:
                    header.CompressedPageSize = reader.ReadI32();
                    break;
                case 5: // DataPageHeader
                    header.DataPageHeader = ReadDataPageHeader(reader);
                    break;
                case 7: // DictionaryPageHeader
                    header.DictionaryPageHeader = ReadDictionaryPageHeader(reader);
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        return header;
    }

    private static DataPageHeader ReadDataPageHeader(ThriftCompactReader reader)
    {
        var dph = new DataPageHeader();
        reader.ReadStructBegin();
        while (reader.ReadFieldBegin(out short fieldId, out var type))
        {
            switch (fieldId)
            {
                case 1:
                    dph.NumValues = reader.ReadI32();
                    break;
                case 2:
                    dph.Encoding = (ParquetEncoding)reader.ReadI32();
                    break;
                case 3:
                    dph.DefinitionLevelEncoding = (ParquetEncoding)reader.ReadI32();
                    break;
                case 4:
                    dph.RepetitionLevelEncoding = (ParquetEncoding)reader.ReadI32();
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }
        return dph;
    }

    private static DictionaryPageHeader ReadDictionaryPageHeader(ThriftCompactReader reader)
    {
        var dph = new DictionaryPageHeader();
        reader.ReadStructBegin();
        while (reader.ReadFieldBegin(out short fieldId, out var type))
        {
            switch (fieldId)
            {
                case 1:
                    dph.NumValues = reader.ReadI32();
                    break;
                case 2:
                    dph.Encoding = (ParquetEncoding)reader.ReadI32();
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }
        return dph;
    }

    public static void WriteFileMetaData(ThriftCompactWriter writer, FileMetaData meta)
    {
        writer.WriteStructBegin();

        // 1: version
        writer.WriteFieldBegin(1, ThriftType.I32);
        writer.WriteI32(meta.Version);

        // 2: schema
        writer.WriteFieldBegin(2, ThriftType.List);
        writer.WriteListBegin(ThriftType.Struct, meta.Schema.Count);
        foreach (var elem in meta.Schema)
        {
            writer.WriteStructBegin();
            if (elem.Type.HasValue)
            {
                writer.WriteFieldBegin(1, ThriftType.I32);
                writer.WriteI32((int)elem.Type.Value);
            }
            if (elem.RepetitionType.HasValue)
            {
                writer.WriteFieldBegin(3, ThriftType.I32);
                writer.WriteI32((int)elem.RepetitionType.Value);
            }
            writer.WriteFieldBegin(4, ThriftType.Binary);
            writer.WriteString(elem.Name);
            if (elem.NumChildren.HasValue)
            {
                writer.WriteFieldBegin(5, ThriftType.I32);
                writer.WriteI32(elem.NumChildren.Value);
            }
            writer.WriteStructEnd();
        }

        // 3: num_rows
        writer.WriteFieldBegin(3, ThriftType.I64);
        writer.WriteI64(meta.NumRows);

        // 4: row_groups
        writer.WriteFieldBegin(4, ThriftType.List);
        writer.WriteListBegin(ThriftType.Struct, meta.RowGroups.Count);
        foreach (var rg in meta.RowGroups)
        {
            writer.WriteStructBegin();

            // columns
            writer.WriteFieldBegin(1, ThriftType.List);
            writer.WriteListBegin(ThriftType.Struct, rg.Columns.Count);
            foreach (var col in rg.Columns)
            {
                writer.WriteStructBegin();
                writer.WriteFieldBegin(2, ThriftType.I64);
                writer.WriteI64(col.FileOffset);

                if (col.MetaData != null)
                {
                    writer.WriteFieldBegin(3, ThriftType.Struct);
                    writer.WriteStructBegin();

                    writer.WriteFieldBegin(1, ThriftType.I32);
                    writer.WriteI32((int)col.MetaData.Type);

                    // encodings
                    writer.WriteFieldBegin(2, ThriftType.List);
                    writer.WriteListBegin(ThriftType.I32, col.MetaData.Encodings.Count);
                    foreach (var enc in col.MetaData.Encodings) writer.WriteI32((int)enc);

                    // path
                    writer.WriteFieldBegin(3, ThriftType.List);
                    writer.WriteListBegin(ThriftType.Binary, col.MetaData.PathInSchema.Count);
                    foreach (var p in col.MetaData.PathInSchema) writer.WriteString(p);

                    // codec
                    writer.WriteFieldBegin(4, ThriftType.I32);
                    writer.WriteI32((int)col.MetaData.Codec);

                    // num_values
                    writer.WriteFieldBegin(5, ThriftType.I64);
                    writer.WriteI64(col.MetaData.NumValues);

                    // sizes
                    writer.WriteFieldBegin(6, ThriftType.I64);
                    writer.WriteI64(col.MetaData.TotalUncompressedSize);

                    writer.WriteFieldBegin(7, ThriftType.I64);
                    writer.WriteI64(col.MetaData.TotalCompressedSize);

                    // offsets
                    writer.WriteFieldBegin(8, ThriftType.I64);
                    writer.WriteI64(col.MetaData.DataPageOffset);

                    if (col.MetaData.DictionaryPageOffset.HasValue)
                    {
                        writer.WriteFieldBegin(10, ThriftType.I64);
                        writer.WriteI64(col.MetaData.DictionaryPageOffset.Value);
                    }

                    writer.WriteStructEnd();
                }

                writer.WriteStructEnd();
            }

            // total_byte_size
            writer.WriteFieldBegin(2, ThriftType.I64);
            writer.WriteI64(rg.TotalByteSize);

            // num_rows
            writer.WriteFieldBegin(3, ThriftType.I64);
            writer.WriteI64(rg.NumRows);

            writer.WriteStructEnd();
        }

        // 6: created_by
        writer.WriteFieldBegin(6, ThriftType.Binary);
        writer.WriteString(meta.CreatedBy);

        writer.WriteStructEnd();
    }

    public static FileMetaData ReadFileMetaData(ThriftCompactReader reader)
    {
        var meta = new FileMetaData();
        reader.ReadStructBegin();

        while (reader.ReadFieldBegin(out short fieldId, out var type))
        {
            switch (fieldId)
            {
                case 1:
                    meta.Version = reader.ReadI32();
                    break;
                case 2:
                    meta.Schema = ReadSchema(reader);
                    break;
                case 3:
                    meta.NumRows = reader.ReadI64();
                    break;
                case 4:
                    meta.RowGroups = ReadRowGroups(reader);
                    break;
                case 6:
                    meta.CreatedBy = reader.ReadString();
                    break;
                default:
                    reader.Skip(type);
                    break;
            }
        }

        return meta;
    }

    private static List<SchemaElement> ReadSchema(ThriftCompactReader reader)
    {
        var list = new List<SchemaElement>();
        reader.ReadListBegin(out _, out int size);
        for (int i = 0; i < size; i++)
        {
            var elem = new SchemaElement();
            reader.ReadStructBegin();
            while (reader.ReadFieldBegin(out short fId, out var fType))
            {
                switch (fId)
                {
                    case 1:
                        elem.Type = (ParquetType)reader.ReadI32();
                        break;
                    case 3:
                        elem.RepetitionType = (FieldRepetitionType)reader.ReadI32();
                        break;
                    case 4:
                        elem.Name = reader.ReadString();
                        break;
                    case 5:
                        elem.NumChildren = reader.ReadI32();
                        break;
                    default:
                        reader.Skip(fType);
                        break;
                }
            }
            list.Add(elem);
        }
        return list;
    }

    private static List<RowGroup> ReadRowGroups(ThriftCompactReader reader)
    {
        var rowGroups = new List<RowGroup>();
        reader.ReadListBegin(out _, out int size);
        for (int i = 0; i < size; i++)
        {
            var rg = new RowGroup();
            reader.ReadStructBegin();
            while (reader.ReadFieldBegin(out short fId, out var fType))
            {
                switch (fId)
                {
                    case 1:
                        rg.Columns = ReadColumns(reader);
                        break;
                    case 2:
                        rg.TotalByteSize = reader.ReadI64();
                        break;
                    case 3:
                        rg.NumRows = reader.ReadI64();
                        break;
                    default:
                        reader.Skip(fType);
                        break;
                }
            }
            rowGroups.Add(rg);
        }
        return rowGroups;
    }

    private static List<ColumnChunk> ReadColumns(ThriftCompactReader reader)
    {
        var cols = new List<ColumnChunk>();
        reader.ReadListBegin(out _, out int size);
        for (int i = 0; i < size; i++)
        {
            var chunk = new ColumnChunk();
            reader.ReadStructBegin();
            while (reader.ReadFieldBegin(out short fId, out var fType))
            {
                switch (fId)
                {
                    case 2:
                        chunk.FileOffset = reader.ReadI64();
                        break;
                    case 3:
                        chunk.MetaData = ReadColumnMetaData(reader);
                        break;
                    default:
                        reader.Skip(fType);
                        break;
                }
            }
            cols.Add(chunk);
        }
        return cols;
    }

    private static ColumnMetaData ReadColumnMetaData(ThriftCompactReader reader)
    {
        var meta = new ColumnMetaData();
        reader.ReadStructBegin();
        while (reader.ReadFieldBegin(out short fId, out var fType))
        {
            switch (fId)
            {
                case 1:
                    meta.Type = (ParquetType)reader.ReadI32();
                    break;
                case 2:
                    reader.ReadListBegin(out _, out int encCount);
                    for (int j = 0; j < encCount; j++) meta.Encodings.Add((ParquetEncoding)reader.ReadI32());
                    break;
                case 3:
                    reader.ReadListBegin(out _, out int pathCount);
                    for (int j = 0; j < pathCount; j++) meta.PathInSchema.Add(reader.ReadString());
                    break;
                case 4:
                    meta.Codec = (CompressionCodec)reader.ReadI32();
                    break;
                case 5:
                    meta.NumValues = reader.ReadI64();
                    break;
                case 6:
                    meta.TotalUncompressedSize = reader.ReadI64();
                    break;
                case 7:
                    meta.TotalCompressedSize = reader.ReadI64();
                    break;
                case 8:
                    meta.DataPageOffset = reader.ReadI64();
                    break;
                case 10:
                    meta.DictionaryPageOffset = reader.ReadI64();
                    break;
                default:
                    reader.Skip(fType);
                    break;
            }
        }
        return meta;
    }
}
