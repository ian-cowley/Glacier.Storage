namespace Glacier.Storage.Parquet;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Glacier.Storage.Arrow;

public static class ParquetReader
{
    private static readonly byte[] Magic = "PAR1"u8.ToArray();

    public static ArrowRecordBatch ReadFile(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadStream(fs);
    }

    public static ArrowRecordBatch ReadBytes(ReadOnlyMemory<byte> memory)
    {
        using var ms = new MemoryStream(memory.ToArray());
        return ReadStream(ms);
    }

    public static ArrowRecordBatch ReadStream(Stream stream)
    {
        if (stream.Length < 12)
        {
            throw new InvalidDataException("Stream too small to be a valid Parquet file.");
        }

        // 1. Verify header magic
        stream.Seek(0, SeekOrigin.Begin);
        Span<byte> headMagic = stackalloc byte[4];
        stream.ReadExactly(headMagic);
        if (!headMagic.SequenceEqual(Magic))
        {
            throw new InvalidDataException("Invalid Parquet file: header magic 'PAR1' mismatch.");
        }

        // 2. Read footer magic and metadata length
        stream.Seek(stream.Length - 8, SeekOrigin.Begin);
        Span<byte> footer = stackalloc byte[8];
        stream.ReadExactly(footer);

        if (!footer.Slice(4, 4).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Invalid Parquet file: footer magic 'PAR1' mismatch.");
        }

        uint metaLen = BinaryPrimitives.ReadUInt32LittleEndian(footer[..4]);
        long metaOffset = stream.Length - 8 - metaLen;
        if (metaOffset < 4)
        {
            throw new InvalidDataException("Invalid Parquet metadata length.");
        }

        // 3. Read FileMetaData
        stream.Seek(metaOffset, SeekOrigin.Begin);
        byte[] metaBytes = new byte[metaLen];
        stream.ReadExactly(metaBytes);

        using var metaMs = new MemoryStream(metaBytes);
        var thriftReader = new ThriftCompactReader(metaMs);
        FileMetaData fileMeta = ParquetThriftCodec.ReadFileMetaData(thriftReader);

        if (fileMeta.RowGroups.Count == 0 || fileMeta.Schema.Count <= 1)
        {
            // Empty schema/batch
            return new ArrowRecordBatch(new ArrowSchema([]), 0, []);
        }

        // 4. Construct ArrowSchema from Parquet SchemaElements
        var fields = new List<ArrowField>();
        // First element is root schema
        for (int i = 1; i < fileMeta.Schema.Count; i++)
        {
            var elem = fileMeta.Schema[i];
            var arrowType = MapParquetTypeToArrow(elem.Type ?? ParquetType.ByteArray);
            bool isNullable = elem.RepetitionType == FieldRepetitionType.Optional;
            fields.Add(new ArrowField(elem.Name, arrowType, isNullable));
        }

        var schema = new ArrowSchema(fields);
        int totalRows = (int)fileMeta.NumRows;

        // 5. Read row group columns
        var firstRg = fileMeta.RowGroups[0];
        var columns = new List<ArrowColumn>(fields.Count);

        for (int i = 0; i < firstRg.Columns.Count && i < fields.Count; i++)
        {
            var chunk = firstRg.Columns[i];
            var colMeta = chunk.MetaData;
            if (colMeta == null) continue;

            stream.Seek(colMeta.DataPageOffset, SeekOrigin.Begin);
            var pageHeader = ParquetThriftCodec.ReadPageHeader(new ThriftCompactReader(stream));

            byte[] pageBytes = new byte[pageHeader.CompressedPageSize];
            stream.ReadExactly(pageBytes);

            byte[] uncompressedBytes;
            if (colMeta.Codec == CompressionCodec.Snappy)
            {
                uncompressedBytes = new byte[pageHeader.UncompressedPageSize];
                SnappyCodec.Decompress(pageBytes, uncompressedBytes);
            }
            else
            {
                uncompressedBytes = pageBytes;
            }

            // Decode column data
            var field = fields[i];
            var col = DecodeColumnData(field, totalRows, uncompressedBytes);
            columns.Add(col);
        }

        return new ArrowRecordBatch(schema, totalRows, columns);
    }

    private static ArrowColumn DecodeColumnData(ArrowField field, int numValues, byte[] uncompressedBytes)
    {
        if (field.DataType.Id == ArrowTypeId.Utf8 || field.DataType.Id == ArrowTypeId.Binary)
        {
            // Parse PLAIN ByteArray: series of [length: int32] [utf8 bytes]
            var offsets = new int[numValues + 1];
            using var dataMs = new MemoryStream();
            int srcOffset = 0;

            for (int i = 0; i < numValues && srcOffset < uncompressedBytes.Length; i++)
            {
                int len = BinaryPrimitives.ReadInt32LittleEndian(uncompressedBytes.AsSpan(srcOffset, 4));
                srcOffset += 4;
                offsets[i] = (int)dataMs.Position;

                if (len > 0 && srcOffset + len <= uncompressedBytes.Length)
                {
                    dataMs.Write(uncompressedBytes, srcOffset, len);
                    srcOffset += len;
                }
            }
            offsets[numValues] = (int)dataMs.Position;

            byte[] offsetsBytes = MemoryMarshal.AsBytes(offsets.AsSpan()).ToArray();
            byte[] dataBytes = dataMs.ToArray();

            return new ArrowColumn(field, numValues, 0, ReadOnlyMemory<byte>.Empty, offsetsBytes, dataBytes);
        }
        else
        {
            // Direct memory blit
            return new ArrowColumn(field, numValues, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, uncompressedBytes);
        }
    }

    private static ArrowType MapParquetTypeToArrow(ParquetType pType) => pType switch
    {
        ParquetType.Boolean => ArrowType.Boolean,
        ParquetType.Int32 => ArrowType.Int32,
        ParquetType.Int64 => ArrowType.Int64,
        ParquetType.Float => ArrowType.Float,
        ParquetType.Double => ArrowType.Double,
        ParquetType.ByteArray => ArrowType.Utf8,
        _ => ArrowType.Utf8
    };
}
