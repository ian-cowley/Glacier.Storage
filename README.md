![Glacier.Storage Banner](assets/banner.jpg)

# Glacier.Storage

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Native AOT](https://img.shields.io/badge/Native%20AOT-Ready-brightgreen.svg)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
[![Website](https://img.shields.io/badge/Website-glaciersoftware.dev-66fcf1.svg)](https://glaciersoftware.dev/)

`Glacier.Storage` is a managed embedded storage kernel for .NET 10 with no native C dependencies (`e_sqlite3.dll` is not required). It pairs a multi-tier unmanaged buffer pool and group-commit write-ahead log (`WAL`) with slotted pages, lossless ALP float compression, FastLanes SIMD bit-unpacking, and transactional `TableStore` exports to Arrow IPC and Parquet via `Glacier.Formats`.

It is built for embedded analytical tables, local checkpoints, and column stores that need predictable unmanaged page caching, crash recovery, and Native AOT builds in pure C#.

## Install

```powershell
dotnet add package Glacier.Storage
```

## Quick Start

```csharp
using Glacier.Formats.Arrow;
using Glacier.Formats.Parquet;
using Glacier.Storage;

var schema = new ArrowSchema(new[]
{
    new ArrowField("id", ArrowType.Int32),
    new ArrowField("metric", ArrowType.Double)
});

using var store = new TableStore("./my_table", schema);
TxId tx = store.BeginTransaction();
store.AppendBatch(tx, recordBatch);
await store.CommitTransactionAsync(tx);

store.ExportToParquet("data.parquet", CompressionCodec.Snappy);
```

## What You Can Do

- **Commit & export batches (`TableStore`):** Append `ArrowRecordBatch` data under WAL transactions (`BeginTransaction`, `AppendBatch`, `CommitTransactionAsync`), replay committed logs automatically on open, and stream to Arrow IPC (`ExportToArrowIpc`) or Parquet (`ExportToParquet`).
- **Manage unmanaged pages (`BufferPool` & `SlottedPage`):** Pin 4 KB (`PageTier.Small`), 64 KB (`PageTier.Medium`), and 2 MB (`PageTier.Huge`) 64-byte aligned slabs with `ClockProPolicy` or `TwoQueuePolicy`, atomic shared/exclusive `FrameLatch` locks, and variable-length tuple storage in `SlottedPage`.
- **Guarantee crash durability (`WalTransactionEngine` & `WalRecovery`):** Log `TxId` records verified by hardware `Crc32c`, flush via lock-split double buffering, and recover cleanly past torn tails.
- **Compress floating-point columns (`AlpCompressor` & `FastLanesBitUnpacker`):** Encode lossless IEEE-754 `float` and `double` blocks (up to `65,535` values) using standard ALP / `ALP_RD` modes with 64-bit zone-map bounds and SIMD bit-unpacking.

## Platform & Compatibility

`Glacier.Storage` targets `.NET 10` (`net10.0`) with Native AOT readiness, relying strictly on managed `Glacier.Formats` without external native SQLite or C++ runtimes.

## Benchmarks

Measured on this workstation (`.NET 10.0.15` Release x64, `AMD Ryzen AI 9 HX 370`):

| Subsystem | Workload | Measured Result |
| :--- | :--- | ---: |
| `VectorizedBitUnpacker.Unpack` (4-bit) | 100,000,000 values | 16 ms (6,008.6 M values/s) |
| `SnappyCodec` Compress / Decompress | 5 x 10 MB | 21,820.7 MB/s / 3,690.5 MB/s |
| `ArrowStreamWriter` / `ArrowStreamReader` | 500,000 rows | 4 ms write / 9 ms read |
| `BufferPool.PinPage` / Dispose | 1,000,000 pins | 155 ms (6.43 M ops/s) |
| `WalTransactionEngine` Group Commit | 50,000 transactions | 18 ms (2,740.9 K tx/s) |
| `AlpCompressor` Float32 / Float64 | 1,000,000 values | 78.6 M / 117.1 M vals/s compress; 197.4 M / 183.7 M vals/s decompress |

See [docs/benchmarks.md](docs/benchmarks.md) for full details and reproduction commands.

## Documentation

- [User Manual](docs/manual.md) — `TableStore`, `BufferPool`, `SlottedPage`, `WalTransactionEngine`, `WalRecovery`, `AlpCompressor`, and `FastLanesBitUnpacker`
- [Architecture](docs/architecture.md) — Aligned slab tiers, `ClockProPolicy` / `TwoQueuePolicy` state machines, lock-split WAL flush, and ALP layout
- [Benchmarks](docs/benchmarks.md) — Complete `--run-direct` benchmark table

## License & Credits

Licensed under the [MIT License](LICENSE).

Developed by Ian Cowley, Antigravity (Google DeepMind) and Claude.
