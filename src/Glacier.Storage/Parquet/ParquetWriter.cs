namespace Glacier.Storage.Parquet;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Glacier.Storage.Arrow;

public static class ParquetWriter
{
    private static readonly byte[] Magic = "PAR1"u8.ToArray();

    public static void WriteFile(string filePath, ArrowRecordBatch batch, CompressionCodec codec = CompressionCodec.Snappy)
    {
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        WriteStream(fs, batch, codec);
    }

    public static byte[] WriteToBytes(ArrowRecordBatch batch, CompressionCodec codec = CompressionCodec.Snappy)
    {
        using var ms = new MemoryStream();
        WriteStream(ms, batch, codec);
        return ms.ToArray();
    }

    public static void WriteStream(Stream stream, ArrowRecordBatch batch, CompressionCodec codec = CompressionCodec.Snappy)
    {
        // 1. Write PAR1 magic header
        stream.Write(Magic);

        var schemaElements = new List<SchemaElement>
        {
            new SchemaElement { Name = "schema", NumChildren = batch.Columns.Count }
        };

        var columnChunks = new List<ColumnChunk>();
        long totalRowGroupBytes = 0;

        for (int i = 0; i < batch.Columns.Count; i++)
        {
            var col = batch.Columns[i];
            ParquetType pType = MapArrowTypeToParquet(col.Field.DataType.Id);

            schemaElements.Add(new SchemaElement
            {
                Name = col.Field.Name,
                Type = pType,
                RepetitionType = col.Field.IsNullable ? FieldRepetitionType.Optional : FieldRepetitionType.Required
            });

            long chunkOffset = stream.Position;

            // Encode column data
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

            // Write Data Page Header
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

        var rowGroup = new RowGroup
        {
            Columns = columnChunks,
            NumRows = batch.RowCount,
            TotalByteSize = totalRowGroupBytes
        };

        var fileMetadata = new FileMetaData
        {
            Version = 1,
            Schema = schemaElements,
            NumRows = batch.RowCount,
            RowGroups = [rowGroup],
            CreatedBy = "Glacier.Storage 1.0"
        };

        // Serialize FileMetaData with Thrift
        long metaOffset = stream.Position;
        using (var metaMs = new MemoryStream())
        {
            var metaWriter = new ThriftCompactWriter(metaMs);
            ParquetThriftCodec.WriteFileMetaData(metaWriter, fileMetadata);
            byte[] metaBytes = metaMs.ToArray();
            stream.Write(metaBytes);

            // Write metadata length (4 bytes uint32 LE)
            Span<byte> lenBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(lenBytes, (uint)metaBytes.Length);
            stream.Write(lenBytes);
        }

        // Write PAR1 magic footer
        stream.Write(Magic);
        stream.Flush();
    }

    private static byte[] EncodeColumnData(ArrowColumn col)
    {
        if (col.Field.DataType.Id == ArrowTypeId.Utf8 || col.Field.DataType.Id == ArrowTypeId.Binary)
        {
            // PLAIN encoding for ByteArray: 4 bytes length prefix followed by utf8 bytes
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
            // Direct memory blit
            return col.DataBuffer.ToArray();
        }
    }

    private static ParquetType MapArrowTypeToParquet(ArrowTypeId typeId) => typeId switch
    {
        ArrowTypeId.Boolean => ParquetType.Boolean,
        ArrowTypeId.Int8 or ArrowTypeId.UInt8 or ArrowTypeId.Int16 or ArrowTypeId.UInt16 or ArrowTypeId.Int32 or ArrowTypeId.UInt32 => ParquetType.Int32,
        ArrowTypeId.Int64 or ArrowTypeId.UInt64 => ParquetType.Int64,
        ArrowTypeId.Float => ParquetType.Float,
        ArrowTypeId.Double => ParquetType.Double,
        ArrowTypeId.Utf8 or ArrowTypeId.Binary => ParquetType.ByteArray,
        _ => ParquetType.ByteArray
    };
}
