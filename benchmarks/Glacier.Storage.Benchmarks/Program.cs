namespace Glacier.Storage.Benchmarks;

using System;
using BenchmarkDotNet.Running;

public static class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("Glacier.Storage (Pillar 12) High-Performance Columnar Storage Microbenchmarks");
        Console.WriteLine("Pure C# .NET 10 Zero-Alloc Arrow IPC, Vectorized Parquet, Clock-Pro BufferPool, WAL, .gdoc");
        Console.WriteLine("================================================================================");

        if (args.Length > 0 && args[0].Equals("--run-direct", StringComparison.OrdinalIgnoreCase))
        {
            DirectBenchmarkRunner.RunAll();
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
