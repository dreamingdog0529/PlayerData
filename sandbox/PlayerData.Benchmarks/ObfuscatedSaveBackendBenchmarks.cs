using System;
using BenchmarkDotNet.Attributes;

namespace PlayerData.Benchmarks;

// Compares the current ObfuscatedSaveBackend.Transform (System.Numerics.Vector<byte> SIMD XOR,
// see PlayerData.Core, ST-6 of plan 202607141500-playerdata-core-perf-tuning) against a frozen
// copy of the pre-ST-6 scalar byte-at-a-time loop. The old code is kept here only as a benchmark
// baseline, not as production code - it is dead outside this comparison. PayloadSize includes 33
// (one past the 32-byte mask period) to keep the scalar tail-loop path exercised even at small sizes.
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
public class ObfuscatedSaveBackendBenchmarks
{
    [Params(33, 100, 1024, 10 * 1024)]
    public int PayloadSize { get; set; }

    private byte[] _data = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _data = new byte[PayloadSize];
        new Random(1).NextBytes(_data);
    }

    [Benchmark(Baseline = true)]
    public byte[] Transform_Old_ScalarLoop() => OldTransform(_data);

    [Benchmark]
    public byte[] Transform_New_SimdVector() => ObfuscatedSaveBackend.Transform(_data);

    // --- Frozen baseline, copied verbatim from PlayerData.Core/ObfuscatedSaveBackend.cs before ST-6 ---

    private static readonly byte[] OldMask =
    {
        0x5A, 0xE3, 0x1C, 0x7F, 0x92, 0x08, 0xB6, 0xD4,
        0x3E, 0xC1, 0x77, 0x0A, 0xF9, 0x64, 0x2D, 0x8B,
        0x11, 0x9C, 0x45, 0xE0, 0x6F, 0x82, 0x1A, 0xD7,
        0x53, 0xC8, 0x3F, 0x96, 0x0D, 0xB1, 0x7A, 0x29,
    };

    private static byte[] OldTransform(byte[] data)
    {
        var result = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
            result[i] = (byte)(data[i] ^ OldMask[i % OldMask.Length]);
        return result;
    }
}
