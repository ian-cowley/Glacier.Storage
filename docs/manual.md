# Glacier.Storage User Manual

## 1. Transactional Tables (`TableStore`)

`TableStore` (`Glacier.Storage`) coordinates an `IBufferPool`, an `IWalTransactionEngine`, and an `ArrowSchema` inside a table directory (`table.wal`):

```csharp
using Glacier.Formats.Arrow;
using Glacier.Formats.Parquet;
using Glacier.Storage;

var schema = new ArrowSchema(new[]
{
    new ArrowField("id", ArrowType.Int32),
    new ArrowField("metric", ArrowType.Double)
});

// Creates an internal 64-frame Medium pool and opens ./my_table/table.wal (replaying committed WAL batches)
using var store = new TableStore("./my_table", schema);

TxId tx = store.BeginTransaction();
store.AppendBatch(tx, batch);
await store.CommitTransactionAsync(tx);

// Export committed batches directly from buffer pool pages
store.ExportToParquet("data.parquet", CompressionCodec.Snappy);
```

To share an external pool or WAL across tables, pass `bufferPool`, `wal`, `leaveBufferPoolOpen`, and `leaveWalOpen` explicitly.

---

## 2. Unmanaged Buffer Pool & Slotted Pages (`BufferPool`, `SlottedPage`)

Initialize fixed-capacity 64-byte aligned native slabs across `PageTier.Small` (4 KB), `PageTier.Medium` (64 KB), or `PageTier.Huge` (2 MB), choosing `ClockProPolicy` (default) or `TwoQueuePolicy`:

```csharp
using Glacier.Storage;
using Glacier.Storage.BufferPool;

using var pool = new BufferPool(
    poolSize: 128,
    tier: PageTier.Small,
    policy: new ClockProPolicy(128));

PageId pageId = pool.AllocatePage();

using (PagePin pin = pool.PinPage(pageId, LockMode.Exclusive))
{
    SlottedPage.Initialize(pin.Data, pageId);
    SlottedPage.TryInsertRecord(pin.Data, "RecordPayload"u8, out ushort slot);
    pin.MarkDirty();
} // Releases FrameLatch and unpins deterministically
```

`SlottedPage` supports pages up to `65,535` bytes (`PageTier.Small`). Pass a `fileStream` to `BufferPool` when persisting resident frames to disk via `RandomAccess` I/O instead of in-memory backing.

---

## 3. Group-Commit WAL & Recovery (`WalTransactionEngine`, `WalRecovery`)

```csharp
using Glacier.Storage;
using Glacier.Storage.Wal;

using var logStream = new FileStream(
    "transactions.wal",
    FileMode.OpenOrCreate,
    FileAccess.ReadWrite,
    FileShare.ReadWrite);

using var wal = new WalTransactionEngine(
    logStream,
    bufferCapacity: 65536,
    groupCommitIntervalMs: 5,
    leaveOpen: false);

TxId tx = wal.BeginTransaction();
wal.LogRecord(tx, "UPDATE inventory SET stock = 42"u8);
await wal.CommitAsync(tx);
```

On restart, scan and replay an existing WAL stream without full-file allocations using `WalRecovery`, which stops cleanly at torn tails and verifies hardware `Crc32c` checksums.

---

## 4. Floating-Point & Bit-Packed Codecs (`AlpCompressor`, `FastLanesBitUnpacker`)

`AlpCompressor` compresses lossless blocks of up to `65,535` `float` or `double` values into standard ALP, `ALP_RD` (for high-precision doubles), or constant frames with embedded 64-bit zone-map bounds:

```csharp
using Glacier.Storage.Encoding;

float[] values = [10.25f, 10.50f, 10.75f, 11.00f];
byte[] compressed = new byte[AlpCompressor.GetMaxCompressedLength(values.Length, sizeof(float))];
int written = AlpCompressor.CompressFloat(values, compressed);

float[] decoded = new float[values.Length];
int count = AlpCompressor.DecompressFloat(compressed.AsSpan(0, written), decoded);
```

An instance adapter (`AlpCompressionCodec : IAlpCompressionCodec`) and `IFastLanesBitUnpacker` are also exposed for dependency injection.
