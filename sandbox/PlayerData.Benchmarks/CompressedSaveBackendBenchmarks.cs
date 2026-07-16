using System;
using System.IO.Compression;
using BenchmarkDotNet.Attributes;

namespace PlayerData.Benchmarks;

// Prices the CompressedSaveBackend.Compress/Decompress kernels directly (via InternalsVisibleTo),
// isolated from the per-call Dictionary/SaveBundle machinery the way EncryptedSaveBackendBenchmarks
// prices Protect/Unprotect. Two payload shapes bracket the real range: Compressible=true is a
// 3-symbol repeating pattern (deflate shrinks it hard, exercising the growth-free fast path);
// Compressible=false is PRNG noise (deflate expands slightly, exercising buffer growth and the
// worst-case CPU cost). The inner backend is never touched - the kernels take/return byte[] arrays.
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
