namespace Glacier.Storage.Parquet;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
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
            return new ArrowRecordBatch(new ArrowSchema([]), 0, []);
        }

        // 4. Construct ArrowSchema from Parquet SchemaElements
        var fields = new List<ArrowField>(fileMeta.Schema.Count - 1);
        for (int i = 1; i < fileMeta.Schema.Count; i++)
        {
            var elem = fileMeta.Schema[i];
            var arrowType = MapParquetTypeToArrow(elem.Type ?? ParquetType.ByteArray);
            bool isNullable = elem.RepetitionType == FieldRepetitionType.Optional;
            fields.Add(new ArrowField(elem.Name, arrowType, isNullable));
        }

        var schema = new ArrowSchema(fields);
        int totalRows = checked((int)fileMeta.NumRows);

        // Fast path: single RowGroup
        if (fileMeta.RowGroups.Count == 1)
        {
            var rg = fileMeta.RowGroups[0];
            int rgRows = checked((int)rg.NumRows);
            var singleCols = new List<ArrowColumn>(fields.Count);

            for (int colIdx = 0; colIdx < rg.Columns.Count && colIdx < fields.Count; colIdx++)
            {
                byte[] uncompressedBytes = ReadUncompressedPageBytes(stream, rg.Columns[colIdx]);
                singleCols.Add(DecodeColumnData(fields[colIdx], rgRows, uncompressedBytes));
            }

            return new ArrowRecordBatch(schema, rgRows, singleCols);
        }

        // Multi-RowGroup path: decode all RowGroups so multi-batch exports never drop rows
        var columns = new List<ArrowColumn>(fields.Count);
        for (int colIdx = 0; colIdx < fields.Count; colIdx++)
        {
            var field = fields[colIdx];
            if (field.DataType.Id is ArrowTypeId.Utf8 or ArrowTypeId.Binary)
            {
                var offsets = new int[totalRows + 1];
                using var dataMs = new MemoryStream();
                int rowCursor = 0;

                foreach (var rg in fileMeta.RowGroups)
                {
                    int rgRows = checked((int)rg.NumRows);
                    byte[] pageBytes = ReadUncompressedPageBytes(stream, rg.Columns[colIdx]);
                    AppendPlainByteArrayPage(pageBytes, rgRows, offsets, ref rowCursor, dataMs);
                }
                offsets[totalRows] = (int)dataMs.Position;

                byte[] offsetsBytes = MemoryMarshal.AsBytes(offsets.AsSpan()).ToArray();
                columns.Add(new ArrowColumn(field, totalRows, 0, ReadOnlyMemory<byte>.Empty, offsetsBytes, dataMs.ToArray()));
            }
            else if (field.DataType.Id == ArrowTypeId.Boolean)
            {
                byte[] combinedBits = new byte[(totalRows + 7) / 8];
                int rowCursor = 0;

                foreach (var rg in fileMeta.RowGroups)
                {
                    int rgRows = checked((int)rg.NumRows);
                    byte[] pageBytes = ReadUncompressedPageBytes(stream, rg.Columns[colIdx]);
                    int neededBytes = (rgRows + 7) / 8;
                    if (pageBytes.Length < neededBytes)
                    {
                        throw new InvalidDataException("Truncated Parquet boolean data page.");
                    }

                    for (int r = 0; r < rgRows; r++)
                    {
                        bool bit = (pageBytes[r >> 3] & (1 << (r & 7))) != 0;
                        if (bit)
                        {
                            combinedBits[rowCursor >> 3] |= (byte)(1 << (rowCursor & 7));
                        }
                        rowCursor++;
                    }
                }

                columns.Add(new ArrowColumn(field, totalRows, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, combinedBits));
            }
            else
            {
                int elemWidth = field.DataType.ByteWidth;
                byte[] combined = new byte[checked(totalRows * elemWidth)];
                int dstByteOffset = 0;

                foreach (var rg in fileMeta.RowGroups)
                {
                    int rgRows = checked((int)rg.NumRows);
                    int byteCount = checked(rgRows * elemWidth);
                    byte[] pageBytes = ReadUncompressedPageBytes(stream, rg.Columns[colIdx]);
                    if (pageBytes.Length < byteCount)
                    {
                        throw new InvalidDataException("Truncated Parquet fixed-width data page.");
                    }

                    Buffer.BlockCopy(pageBytes, 0, combined, dstByteOffset, byteCount);
                    dstByteOffset += byteCount;
                }

                columns.Add(new ArrowColumn(field, totalRows, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, combined));
            }
        }

        return new ArrowRecordBatch(schema, totalRows, columns);
    }

    private static byte[] ReadUncompressedPageBytes(Stream stream, ColumnChunk chunk)
    {
        var colMeta = chunk.MetaData ?? throw new InvalidDataException("Missing Parquet ColumnMetaData.");
        if (colMeta.DataPageOffset < 4 || colMeta.DataPageOffset >= stream.Length)
        {
            throw new InvalidDataException("Invalid Parquet DataPageOffset.");
        }

        stream.Seek(colMeta.DataPageOffset, SeekOrigin.Begin);
        var pageHeader = ParquetThriftCodec.ReadPageHeader(new ThriftCompactReader(stream));
        if (pageHeader.CompressedPageSize < 0 || pageHeader.UncompressedPageSize < 0)
        {
            throw new InvalidDataException("Invalid negative Parquet page size.");
        }

        byte[] pageBytes = new byte[pageHeader.CompressedPageSize];
        stream.ReadExactly(pageBytes);

        if (colMeta.Codec == CompressionCodec.Snappy)
        {
            byte[] uncompressedBytes = new byte[pageHeader.UncompressedPageSize];
            int decompressedLen = SnappyCodec.Decompress(pageBytes, uncompressedBytes);
            if (decompressedLen != pageHeader.UncompressedPageSize)
            {
                throw new InvalidDataException(
                    $"Parquet Snappy page size mismatch: expected {pageHeader.UncompressedPageSize} bytes, decoded {decompressedLen} bytes.");
            }
            return uncompressedBytes;
        }

        if (colMeta.Codec == CompressionCodec.Uncompressed)
        {
            if (pageBytes.Length != pageHeader.UncompressedPageSize)
            {
                throw new InvalidDataException("Uncompressed Parquet page size mismatch.");
            }
            return pageBytes;
        }

        throw new NotSupportedException($"Unsupported Parquet compression codec '{colMeta.Codec}'.");
    }

    private static void AppendPlainByteArrayPage(byte[] uncompressedBytes, int numValues, int[] offsets, ref int rowCursor, MemoryStream dataMs)
    {
        int srcOffset = 0;
        for (int i = 0; i < numValues; i++)
        {
            if (srcOffset + 4 > uncompressedBytes.Length)
            {
                throw new InvalidDataException("Truncated PLAIN ByteArray length prefix in Parquet page.");
            }

            int len = BinaryPrimitives.ReadInt32LittleEndian(uncompressedBytes.AsSpan(srcOffset, 4));
            srcOffset += 4;
            if (len < 0 || srcOffset + len > uncompressedBytes.Length)
            {
                throw new InvalidDataException("Invalid PLAIN ByteArray element length in Parquet page.");
            }

            offsets[rowCursor++] = (int)dataMs.Position;
            if (len > 0)
            {
                dataMs.Write(uncompressedBytes, srcOffset, len);
                srcOffset += len;
            }
        }
    }

    private static ArrowColumn DecodeColumnData(ArrowField field, int numValues, byte[] uncompressedBytes)
    {
        if (field.DataType.Id == ArrowTypeId.Utf8 || field.DataType.Id == ArrowTypeId.Binary)
        {
            var offsets = new int[numValues + 1];
            using var dataMs = new MemoryStream();
            int rowCursor = 0;
            AppendPlainByteArrayPage(uncompressedBytes, numValues, offsets, ref rowCursor, dataMs);
            offsets[numValues] = (int)dataMs.Position;

            byte[] offsetsBytes = MemoryMarshal.AsBytes(offsets.AsSpan()).ToArray();
            byte[] dataBytes = dataMs.ToArray();

            return new ArrowColumn(field, numValues, 0, ReadOnlyMemory<byte>.Empty, offsetsBytes, dataBytes);
        }
        else if (field.DataType.Id == ArrowTypeId.Boolean)
        {
            int neededBytes = (numValues + 7) / 8;
            if (uncompressedBytes.Length < neededBytes)
            {
                throw new InvalidDataException("Truncated Parquet boolean column page.");
            }
            return new ArrowColumn(field, numValues, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, uncompressedBytes);
        }
        else
        {
            int neededBytes = checked(numValues * field.DataType.ByteWidth);
            if (uncompressedBytes.Length < neededBytes)
            {
                throw new InvalidDataException("Truncated Parquet fixed-width column page.");
            }
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
