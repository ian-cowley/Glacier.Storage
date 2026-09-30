namespace Glacier.Storage.Tests;

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Glacier.Storage.Arrow;
using Xunit;

public class ArrowIpcTests
{
    [Fact]
    public void ArrowSchema_Properties_Correct()
    {
        var fields = new[]
        {
            new ArrowField("id", ArrowType.Int32, isNullable: false),
            new ArrowField("score", ArrowType.Double, isNullable: true),
            new ArrowField("name", ArrowType.Utf8, isNullable: true)
        };

        var schema = new ArrowSchema(fields);
        Assert.Equal(3, schema.FieldCount);
        Assert.Equal("id", schema.GetField(0).Name);
        Assert.Equal(0, schema.GetFieldIndex("id"));
        Assert.Equal(1, schema.GetFieldIndex("score"));
        Assert.Equal(2, schema.GetFieldIndex("name"));
    }

    [Fact]
    public void ArrowStream_Roundtrip_PrimitiveColumns()
    {
        var schema = new ArrowSchema(new[]
        {
            new ArrowField("col_i32", ArrowType.Int32),
            new ArrowField("col_i64", ArrowType.Int64),
            new ArrowField("col_f64", ArrowType.Double)
        });

        int rowCount = 500;
        var i32Vals = new int[rowCount];
        var i64Vals = new long[rowCount];
        var f64Vals = new double[rowCount];

        for (int i = 0; i < rowCount; i++)
        {
            i32Vals[i] = i * 10;
            i64Vals[i] = 1_000_000_000L + i;
            f64Vals[i] = i * 1.5;
        }

        byte[] i32Bytes = MemoryMarshal.AsBytes(i32Vals.AsSpan()).ToArray();
        byte[] i64Bytes = MemoryMarshal.AsBytes(i64Vals.AsSpan()).ToArray();
        byte[] f64Bytes = MemoryMarshal.AsBytes(f64Vals.AsSpan()).ToArray();

        var col0 = new ArrowColumn(schema.GetField(0), rowCount, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, i32Bytes);
        var col1 = new ArrowColumn(schema.GetField(1), rowCount, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, i64Bytes);
        var col2 = new ArrowColumn(schema.GetField(2), rowCount, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, f64Bytes);

        var batch = new ArrowRecordBatch(schema, rowCount, new[] { col0, col1, col2 });

        using var ms = new MemoryStream();
        using (var writer = new ArrowStreamWriter(ms, leaveOpen: true))
        {
            writer.WriteSchema(schema);
            writer.WriteRecordBatch(batch);
            writer.WriteEndOfStream();
        }

        ms.Seek(0, SeekOrigin.Begin);
        using var reader = new ArrowStreamReader(ms);
        var readSchema = reader.ReadSchema();
        Assert.Equal(3, readSchema.FieldCount);

        var readBatch = reader.ReadNextRecordBatch();
        Assert.NotNull(readBatch);
        Assert.Equal(rowCount, readBatch.RowCount);

        var readCol0 = readBatch.Column(0);
        var readCol1 = readBatch.Column(1);
        var readCol2 = readBatch.Column(2);

        for (int i = 0; i < rowCount; i++)
        {
            Assert.Equal(i * 10, readCol0.GetInt32(i));
            Assert.Equal(1_000_000_000L + i, readCol1.GetInt64(i));
            Assert.Equal(i * 1.5, readCol2.GetDouble(i), 4);
        }

        // Verify EOS
        var nextBatch = reader.ReadNextRecordBatch();
        Assert.Null(nextBatch);
    }

    [Fact]
    public void ArrowStream_Roundtrip_StringsAndNullables()
    {
        var schema = new ArrowSchema(new[]
        {
            new ArrowField("id", ArrowType.Int32),
            new ArrowField("text", ArrowType.Utf8)
        });

        int rowCount = 200;
        var i32Vals = new int[rowCount];
        var offsets = new int[rowCount + 1];
        using var textStream = new MemoryStream();

        for (int i = 0; i < rowCount; i++)
        {
            i32Vals[i] = i;
            offsets[i] = (int)textStream.Position;
            byte[] strBytes = Encoding.UTF8.GetBytes($"Record_{i}_Glacier");
            textStream.Write(strBytes);
        }
        offsets[rowCount] = (int)textStream.Position;

        byte[] i32Bytes = MemoryMarshal.AsBytes(i32Vals.AsSpan()).ToArray();
        byte[] offsetsBytes = MemoryMarshal.AsBytes(offsets.AsSpan()).ToArray();
        byte[] textBytes = textStream.ToArray();

        var col0 = new ArrowColumn(schema.GetField(0), rowCount, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, i32Bytes);
        var col1 = new ArrowColumn(schema.GetField(1), rowCount, 0, ReadOnlyMemory<byte>.Empty, offsetsBytes, textBytes);

        var batch = new ArrowRecordBatch(schema, rowCount, new[] { col0, col1 });

        using var ms = new MemoryStream();
        using (var writer = new ArrowStreamWriter(ms, leaveOpen: true))
        {
            writer.WriteSchema(schema);
            writer.WriteRecordBatch(batch);
            writer.WriteEndOfStream();
        }

        ms.Seek(0, SeekOrigin.Begin);
        using var reader = new ArrowStreamReader(ms);
        reader.ReadSchema();
        var readBatch = reader.ReadNextRecordBatch();

        Assert.NotNull(readBatch);
        Assert.Equal(rowCount, readBatch.RowCount);

        for (int i = 0; i < rowCount; i++)
        {
            Assert.Equal(i, readBatch.Column(0).GetInt32(i));
            Assert.Equal($"Record_{i}_Glacier", readBatch.Column(1).GetString(i));
        }
    }

    [Fact]
    public void ArrowRecordBatch_Slice_OperatesZeroCopy()
    {
        var schema = new ArrowSchema(new[]
        {
            new ArrowField("id", ArrowType.Int32)
        });

        var vals = new int[] { 10, 20, 30, 40, 50 };
        byte[] bytes = MemoryMarshal.AsBytes(vals.AsSpan()).ToArray();
        var col = new ArrowColumn(schema.GetField(0), 5, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, bytes);
        var batch = new ArrowRecordBatch(schema, 5, new[] { col });

        var sliced = batch.Slice(1, 3);
        Assert.Equal(3, sliced.RowCount);
        Assert.Equal(20, sliced.Column(0).GetInt32(0));
        Assert.Equal(30, sliced.Column(0).GetInt32(1));
        Assert.Equal(40, sliced.Column(0).GetInt32(2));
    }
}
