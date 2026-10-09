using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace CoreScope.Core;

public sealed record BenchmarkResult(
    double CpuSingle,
    double CpuMulti,
    double MemoryCopyGBs,
    double MemoryLatencyNs,
    double DiskSeqWriteMBs,
    double DiskSeqReadMBs,
    double Disk4kReadMBs,
    DateTime When);

/// <summary>
/// Short, repeatable benchmarks. CPU scores are "CoreScope points": iterations of a fixed mixed
/// integer/floating-point kernel per second, normalised so a Ryzen 5 7520U core scores about 1000.
/// Disk tests bypass the Windows cache (FILE_FLAG_NO_BUFFERING) so they measure the drive, not RAM.
/// </summary>
public static class Benchmark
{
    /// <summary>Kernel iterations per second that map to 1000 points (calibrated on a Ryzen 5 7520U).</summary>
    public static double CalibrationIterationsPerSecond = 950_000_000;

    // ───────────── CPU ─────────────

    /// <summary>A mix of integer hashing, branches and FP math that compilers can't fold away.</summary>
    private static long Kernel(CancellationToken stop)
    {
        long iterations = 0;
        ulong h = 1469598103934665603UL;
        double x = 1.000001;
        while (!stop.IsCancellationRequested)
        {
            for (int i = 0; i < 4096; i++)
            {
                h ^= (ulong)i;
                h *= 1099511628211UL;
                if ((h & 7) == 3) x = x * 1.0000001 + 0.5 / (x + i);
                else x = Math.Sqrt(x * x + 1e-9);
            }
            iterations += 4096;
        }
        GC.KeepAlive(h);
        GC.KeepAlive(x);
        return iterations;
    }

    private static double Points(long iterations, TimeSpan elapsed) =>
        iterations / elapsed.TotalSeconds / CalibrationIterationsPerSecond * 1000.0;

    public static double CpuSingle(TimeSpan duration)
    {
        using var stop = new CancellationTokenSource(duration);
        var w = Stopwatch.StartNew();
        long it = Kernel(stop.Token);
        return Points(it, w.Elapsed);
    }

    public static double CpuMulti(TimeSpan duration)
    {
        int threads = Environment.ProcessorCount;
        long total = 0;
        using var stop = new CancellationTokenSource(duration);
        var w = Stopwatch.StartNew();
        var workers = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            workers[t] = new Thread(() => Interlocked.Add(ref total, Kernel(stop.Token))) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            workers[t].Start();
        }
        foreach (var worker in workers) worker.Join();
        return Points(total, w.Elapsed);
    }

    // ───────────── Memory ─────────────

    /// <summary>
    /// Multi-threaded copy: one core can't saturate a modern memory controller, so each core copies its own
    /// slice (like AIDA64/STREAM). Reads + writes are both counted.
    /// </summary>
    public static double MemoryCopyGBs()
    {
        const int size = 256 * 1024 * 1024;
        int threads = Math.Clamp(Environment.ProcessorCount, 1, 16);
        int slice = size / threads / 4096 * 4096;
        var src = GC.AllocateUninitializedArray<byte>(size);
        var dst = GC.AllocateUninitializedArray<byte>(size);
        new Random(1).NextBytes(src.AsSpan(0, 4096));

        void CopyAll()
        {
            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads },
                t => Buffer.BlockCopy(src, t * slice, dst, t * slice, slice));
        }

        CopyAll(); // warm-up / page-in
        const int passes = 8;
        var w = Stopwatch.StartNew();
        for (int i = 0; i < passes; i++) CopyAll();
        return 2.0 * (double)slice * threads * passes / w.Elapsed.TotalSeconds / 1e9;
    }

    /// <summary>Random pointer chase through 128 MB — defeats caches and prefetchers.</summary>
    public static double MemoryLatencyNs()
    {
        const int count = 16 * 1024 * 1024; // 16M × 8 bytes = 128 MB
        var next = new int[count];
        var order = new int[count];
        for (int i = 0; i < count; i++) order[i] = i;
        var rng = new Random(42);
        for (int i = count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        for (int i = 0; i < count - 1; i++) next[order[i]] = order[i + 1];
        next[order[count - 1]] = order[0];

        const int steps = 20_000_000;
        int p = 0;
        var w = Stopwatch.StartNew();
        for (int i = 0; i < steps; i++) p = next[p];
        var ns = w.Elapsed.TotalMilliseconds * 1e6 / steps;
        GC.KeepAlive(p);
        return ns;
    }

    // ───────────── Disk ─────────────

    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    private const int QueueDepth = 8;

    /// <summary>
    /// Sequential 1 MB writes/reads with 8 requests in flight (NVMe drives need parallel requests to reach
    /// their rated speed — single-request numbers would understate them by ~40%), plus 4 KB random reads at
    /// queue depth 1 (the latency-bound case that dominates everyday snappiness). Uncached; file always deleted.
    /// </summary>
    public static unsafe (double WriteMBs, double ReadMBs, double Random4kMBs) Disk(string folder, long sizeBytes = 512L * 1024 * 1024)
    {
        const int block = 1024 * 1024;
        var path = Path.Combine(folder, $"corescope-bench-{Guid.NewGuid():N}.tmp");
        long blocks = sizeBytes / block;
        // One aligned buffer per in-flight request.
        var buffers = new IntPtr[QueueDepth];
        for (int q = 0; q < QueueDepth; q++)
        {
            buffers[q] = (IntPtr)NativeMemory.AlignedAlloc(block, 4096);
            new Random(7 + q).NextBytes(new Span<byte>((void*)buffers[q], block)); // incompressible: SSD controllers can't cheat
        }

        double Parallelised(Microsoft.Win32.SafeHandles.SafeFileHandle h, bool write)
        {
            long next = -1;
            var w = Stopwatch.StartNew();
            Parallel.For(0, QueueDepth, new ParallelOptions { MaxDegreeOfParallelism = QueueDepth }, q =>
            {
                var span = new Span<byte>((void*)buffers[q], block);
                long i;
                while ((i = Interlocked.Increment(ref next)) < blocks)
                {
                    if (write) RandomAccess.Write(h, span, i * block);
                    else RandomAccess.Read(h, span, i * block);
                }
            });
            return blocks * (double)block / w.Elapsed.TotalSeconds / 1e6;
        }

        try
        {
            double write, read, random;
            using (var h = File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.WriteThrough | NoBuffering, sizeBytes))
                write = Parallelised(h, write: true);
            using (var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.None, NoBuffering))
            {
                read = Parallelised(h, write: false);

                var small = new Span<byte>((void*)buffers[0], 4096);
                var w = Stopwatch.StartNew();
                var rng = new Random(3);
                long blocks4k = sizeBytes / 4096, done = 0;
                w.Restart();
                while (w.ElapsedMilliseconds < 3000)
                {
                    RandomAccess.Read(h, small, rng.NextInt64(blocks4k) * 4096);
                    done++;
                }
                random = done * 4096 / w.Elapsed.TotalSeconds / 1e6;
            }
            return (write, read, random);
        }
        finally
        {
            foreach (var b in buffers) NativeMemory.AlignedFree((void*)b);
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Runs everything (~25 s). Progress strings are reported for the UI.</summary>
    public static async Task<BenchmarkResult> RunAllAsync(string diskFolder, IProgress<string>? progress = null)
    {
        progress?.Report("CPU single-core…");
        var single = await Task.Run(() => CpuSingle(TimeSpan.FromSeconds(4)));
        progress?.Report("CPU all cores…");
        var multi = await Task.Run(() => CpuMulti(TimeSpan.FromSeconds(5)));
        progress?.Report("Memory bandwidth…");
        var copy = await Task.Run(MemoryCopyGBs);
        progress?.Report("Memory latency…");
        var latency = await Task.Run(MemoryLatencyNs);
        progress?.Report("Disk (writes a 512 MB temporary file)…");
        var disk = await Task.Run(() => Disk(diskFolder));
        var result = new BenchmarkResult(single, multi, copy, latency, disk.WriteMBs, disk.ReadMBs, disk.Random4kMBs, DateTime.Now);
        Log.Info($"Benchmark: {result}");
        return result;
    }
}
