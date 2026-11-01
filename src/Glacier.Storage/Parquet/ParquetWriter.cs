namespace Glacier.Storage.Parquet;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Glacier.Storage.Arrow;

public static class ParquetWriter
{
    private static readonly byte[] Magic = "PAR1"u8.ToArray();

    public static void WriteFile(string filePath, ArrowRecordBatch batch, CompressionCodec codec = CompressionCodec.Snappy)
    {
        ArgumentNullException.ThrowIfNull(batch);
        WriteFile(filePath, [batch], codec);
    }

    public static void WriteFile(string filePath, IReadOnlyList<ArrowRecordBatch> batches, CompressionCodec codec = CompressionCodec.Snappy)
    {
        ArgumentNullException.ThrowIfNull(batches);
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        WriteStream(fs, batches, codec);
    }

    public static byte[] WriteToBytes(ArrowRecordBatch batch, CompressionCodec codec = CompressionCodec.Snappy)
    {
        ArgumentNullException.ThrowIfNull(batch);
        using var ms = new MemoryStream();
        WriteStream(ms, [batch], codec);
        return ms.ToArray();
    }

    public static void WriteStream(Stream stream, ArrowRecordBatch batch, CompressionCodec codec = CompressionCodec.Snappy)
    {
        ArgumentNullException.ThrowIfNull(batch);
        WriteStream(stream, [batch], codec);
    }

    public static void WriteStream(Stream stream, IReadOnlyList<ArrowRecordBatch> batches, CompressionCodec codec = CompressionCodec.Snappy)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(batches);

        // 1. Write PAR1 magic header
        stream.Write(Magic);

        if (batches.Count == 0)
        {
            var emptyMeta = new FileMetaData
            {
                Version = 1,
                Schema = [new SchemaElement { Name = "schema", NumChildren = 0 }],
                NumRows = 0,
                RowGroups = [],
                CreatedBy = "Glacier.Storage 1.0"
            };
            WriteFooter(stream, emptyMeta);
            return;
        }

        ArrowRecordBatch firstBatch = batches[0];
        var schemaElements = new List<SchemaElement>(firstBatch.Columns.Count + 1)
        {
            new SchemaElement { Name = "schema", NumChildren = firstBatch.Columns.Count }
        };

        for (int i = 0; i < firstBatch.Columns.Count; i++)
        {
            var col = firstBatch.Columns[i];
            ParquetType pType = MapArrowTypeToParquet(col.Field.DataType.Id);
            schemaElements.Add(new SchemaElement
            {
                Name = col.Field.Name,
                Type = pType,
                RepetitionType = col.Field.IsNullable ? FieldRepetitionType.Optional : FieldRepetitionType.Required
            });
        }

        var rowGroups = new List<RowGroup>(batches.Count);
        long totalRows = 0;

        for (int rgIdx = 0; rgIdx < batches.Count; rgIdx++)
        {
            var batch = batches[rgIdx];
            if (batch.Columns.Count != firstBatch.Columns.Count)
            {
                throw new ArgumentException($"Batch {rgIdx} column count ({batch.Columns.Count}) does not match schema ({firstBatch.Columns.Count}).", nameof(batches));
            }

            var columnChunks = new List<ColumnChunk>(batch.Columns.Count);
            long totalRowGroupBytes = 0;

            for (int i = 0; i < batch.Columns.Count; i++)
            {
                var col = batch.Columns[i];
                ParquetType pType = MapArrowTypeToParquet(col.Field.DataType.Id);
                long chunkOffset = stream.Position;

                byte[] uncompressedData = EncodeColumnData(col);
                byte[] pageData;

                if (codec == CompressionCodec.Snappy)
                {
                    byte[] compBuf = new byte[SnappyCodec.GetMaxCompressedLength(uncompressedData.Length)];
                    int compLen = SnappyCodec.Compress(uncompressedData, compBuf);
                    pageData = new byte[compLen];
                    Buffer.BlockCopy(compBuf, 0, pageData, 0, compLen);
                }
                else
                {
                    pageData = uncompressedData;
                }

                var pageHeader = new PageHeader
                {
                    Type = PageType.DataPage,
                    UncompressedPageSize = uncompressedData.Length,
                    CompressedPageSize = pageData.Length,
                    DataPageHeader = new DataPageHeader
                    {
                        NumValues = col.Length,
                        Encoding = ParquetEncoding.Plain,
                        DefinitionLevelEncoding = ParquetEncoding.Rle,
                        RepetitionLevelEncoding = ParquetEncoding.Rle
                    }
                };

                using var headerMs = new MemoryStream();
                var thriftWriter = new ThriftCompactWriter(headerMs);
                ParquetThriftCodec.WritePageHeader(thriftWriter, pageHeader);
                byte[] headerBytes = headerMs.ToArray();

                stream.Write(headerBytes);
                stream.Write(pageData);

                long chunkTotalBytes = headerBytes.Length + pageData.Length;
                totalRowGroupBytes += chunkTotalBytes;

                var colMeta = new ColumnMetaData
                {
                    Type = pType,
                    Encodings = [ParquetEncoding.Plain],
                    PathInSchema = [col.Field.Name],
                    Codec = codec,
                    NumValues = col.Length,
                    TotalUncompressedSize = headerBytes.Length + uncompressedData.Length,
                    TotalCompressedSize = chunkTotalBytes,
                    DataPageOffset = chunkOffset
                };

                columnChunks.Add(new ColumnChunk
                {
                    FileOffset = chunkOffset,
                    MetaData = colMeta
                });
            }

            rowGroups.Add(new RowGroup
            {
                Columns = columnChunks,
                NumRows = batch.RowCount,
                TotalByteSize = totalRowGroupBytes
            });
            totalRows += batch.RowCount;
        }

        var fileMetadata = new FileMetaData
        {
            Version = 1,
            Schema = schemaElements,
            NumRows = totalRows,
            RowGroups = rowGroups,
            CreatedBy = "Glacier.Storage 1.0"
        };

        WriteFooter(stream, fileMetadata);
    }

    private static void WriteFooter(Stream stream, FileMetaData fileMetadata)
    {
        using (var metaMs = new MemoryStream())
        {
            var metaWriter = new ThriftCompactWriter(metaMs);
            ParquetThriftCodec.WriteFileMetaData(metaWriter, fileMetadata);
            byte[] metaBytes = metaMs.ToArray();
            stream.Write(metaBytes);

            Span<byte> lenBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(lenBytes, (uint)metaBytes.Length);
            stream.Write(lenBytes);
        }

        stream.Write(Magic);
        stream.Flush();
    }

    private static byte[] EncodeColumnData(ArrowColumn col)
    {
        if (col.Field.DataType.Id == ArrowTypeId.Utf8 || col.Field.DataType.Id == ArrowTypeId.Binary)
        {
            using var ms = new MemoryStream();
            Span<byte> len = stackalloc byte[4];
            for (int i = 0; i < col.Length; i++)
            {
                var bytes = col.GetBytes(i);
                BinaryPrimitives.WriteInt32LittleEndian(len, bytes.Length);
                ms.Write(len);
                ms.Write(bytes);
            }
            return ms.ToArray();
        }
        else
        {
            return col.DataBuffer.ToArray();
        }
    }

    private static ParquetType MapArrowTypeToParquet(ArrowTypeId typeId) => typeId switch
    {
        ArrowTypeId.Boolean => ParquetType.Boolean,
        ArrowTypeId.Int8 or ArrowTypeId.UInt8 or ArrowTypeId.Int16 or ArrowTypeId.UInt16 or ArrowTypeId.Int32 or ArrowTypeId.UInt32 or ArrowTypeId.Date32 => ParquetType.Int32,
        ArrowTypeId.Int64 or ArrowTypeId.UInt64 or ArrowTypeId.Date64 or ArrowTypeId.Timestamp or ArrowTypeId.Duration => ParquetType.Int64,
        ArrowTypeId.Float => ParquetType.Float,
        ArrowTypeId.Double => ParquetType.Double,
        ArrowTypeId.Utf8 or ArrowTypeId.Binary => ParquetType.ByteArray,
        _ => ParquetType.ByteArray
    };
}
