# Glacier.Storage Benchmarks

All measurements below were collected on the reference workstation in `Release` x64 configuration during this audit.

## Test Workstation

- **CPU:** AMD Ryzen AI 9 HX 370 (12 cores / 24 logical threads, AVX-512)
- **Memory & OS:** 31 GB RAM, Windows 11 (`10.0.26300.0`)
- **Runtime:** `.NET 10.0.15` (`.NET SDK 11.0.100`, Release x64)

## Commands

```powershell
# Direct microbenchmark suite across all storage subsystems
dotnet run --project benchmarks/Glacier.Storage.Benchmarks/Glacier.Storage.Benchmarks.csproj -c Release -- --run-direct

# Full BenchmarkDotNet suite (optional)
dotnet run --project benchmarks/Glacier.Storage.Benchmarks/Glacier.Storage.Benchmarks.csproj -c Release -- --filter "*"
```

## Measured Subsystem Benchmarks (`--run-direct`)

| Subsystem / Operation | Workload | Measured Timing / Rate | Notes |
| :--- | :--- | ---: | :--- |
| `VectorizedBitUnpacker.Unpack` (4-bit) | 100,000,000 values (10 x 10M) | 16 ms / 6,008.6 M values/s | SIMD bit-unpacking |
| `SnappyCodec.Compress` | 5 x 10 MB | 21,820.7 MB/s | 21.33x ratio on repeating fixture |
| `SnappyCodec.Decompress` | 5 x 10 MB | 3,690.5 MB/s | SIMD decompression |
| `ArrowStreamWriter` / `ArrowStreamReader` | 500,000 rows | 4 ms write / 9 ms read | Zero-copy Arrow IPC |
| `BufferPool.PinPage` / Dispose | 1,000,000 pins | 155 ms / 6.43 M ops/s | Clock-Pro shared pins |
| `WalTransactionEngine` Group Commit | 50,000 transactions | 18 ms / 2,740.9 K tx/s | In-memory stream group commit |
| `.gdoc` SIMD Cosine Search (768-D) | 200 queries x 1,000 nodes | 72 ms / 2,767 queries/s | Contiguous memory-mapped index |
| `AlpCompressor` Float32 | 1,000,000 values (16 x 62,500) | 78.6 M vals/s compress / 197.4 M vals/s decompress | Lossless IEEE-754 block codec |
| `AlpCompressor` Float64 | 1,000,000 values (16 x 62,500) | 117.1 M vals/s compress / 183.7 M vals/s decompress | Lossless IEEE-754 block codec |
