using System;
using System.IO.Compression;
using BenchmarkDotNet.Attributes;
using PlayerData.Persistence;

namespace PlayerData.Benchmarks;

// CompressedSaveBackend.Compress / Decompress の核を直接測ります（InternalsVisibleTo 経由です）。
// EncryptedSaveBackendBenchmarks が Protect / Unprotect を測るのと同じように、呼び出しごとの
// Dictionary / SaveBundle の仕組みからは切り離しています。2種類のデータの形で、実際の範囲を挟みます。
// Compressible=true は、3種類の記号が繰り返される形です（deflate で大きく縮み、伸びない速い経路を通ります）。
// Compressible=false は PRNG のノイズです（deflate で少し膨らみ、バッファの伸長と CPU コストの
// 最悪ケースを通ります）。内側のバックエンドには触りません。核は byte[] の配列を受け取って返すだけです。
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
public class CompressedSaveBackendBenchmarks
{
    private const string DocumentKey = "profile";

    [Params(1024, 10 * 1024, 100 * 1024)]
    public int PayloadSize { get; set; }

    [Params(true, false)]
    public bool Compressible { get; set; }

    [Params(CompressionLevel.Optimal, CompressionLevel.Fastest)]
    public CompressionLevel Level { get; set; }

    private CompressedSaveBackend _backend = null!;
    private byte[] _plaintext = null!;
    private byte[] _payload = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _backend = new CompressedSaveBackend(new InMemorySaveBackend(), Level);

        _plaintext = new byte[PayloadSize];
        if (Compressible)
            for (var i = 0; i < _plaintext.Length; i++) _plaintext[i] = (byte)('A' + (i % 3));
        else
            new Random(1).NextBytes(_plaintext);

        _payload = _backend.Compress(_plaintext);
    }

    [Benchmark]
    public byte[] Compress() => _backend.Compress(_plaintext);

    [Benchmark]
    public byte[] Decompress() => CompressedSaveBackend.Decompress(DocumentKey, _payload);
}
