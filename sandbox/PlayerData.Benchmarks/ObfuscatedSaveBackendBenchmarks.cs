using System;
using BenchmarkDotNet.Attributes;
using PlayerData.Persistence;

namespace PlayerData.Benchmarks;

// 現在の ObfuscatedSaveBackend.Transform（System.Numerics.Vector<byte> による SIMD の XOR です。
// PlayerData.Core 側と、計画 202607141500-playerdata-core-perf-tuning の ST-6 をご覧ください）と、ST-6 より
// 前の、バイトを1つずつ処理するスカラーのループを凍結したコピーを比べます。古いコードは、比べるための基準として
// ここに置いているだけです。本番のコードではなく、この比較の外では使われません。PayloadSize には 33 も含めています
// （32バイトのマスクの周期を1つ過ぎたところです）。小さいサイズでも、スカラーの残り処理の経路が通るようにするためです。
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

    // --- ここから凍結した基準です。PlayerData.Core/ObfuscatedSaveBackend.cs の ST-6 より前のものを、そのままコピーしています ---

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
