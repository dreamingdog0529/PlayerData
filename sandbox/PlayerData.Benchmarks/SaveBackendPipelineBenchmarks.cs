using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace PlayerData.Benchmarks;

// Measures the FULL ObfuscatedSaveBackend/EncryptedSaveBackend ReadAsync/WriteAsync pipeline -
// including the per-call transformed-document Dictionary and SaveBundle - unlike
// ObfuscatedSaveBackendBenchmarks/EncryptedSaveBackendBenchmarks, which price only the
// per-document Transform/Protect/Unprotect kernels. The inner backends are benchmark-local and
// deliberately behavior-stable across the whole measurement campaign: WriteAsync targets
// NullSaveBackend (zero inner contribution) and ReadAsync draws from FreshCloneReadBackend,
// which materializes a deep clone per call the way a disk-backed backend returns freshly read
// arrays - its clone cost is constant in before/after runs, so any Allocated delta is the
// wrapper's own.
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
public class SaveBackendPipelineBenchmarks
{
    private const int DocumentCount = 4;
    private const int PayloadSize = 4096;

    private ObfuscatedSaveBackend _obfuscatedWrite = null!;
    private EncryptedSaveBackend _encryptedWrite = null!;
    private ObfuscatedSaveBackend _obfuscatedRead = null!;
    private EncryptedSaveBackend _encryptedRead = null!;
    private SaveBundle _plainBundle = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var documents = new Dictionary<string, byte[]>(DocumentCount);
        var seed = 12345;
        for (var i = 0; i < DocumentCount; i++)
        {
            var payload = new byte[PayloadSize];
            for (var j = 0; j < payload.Length; j++)
                payload[j] = (byte)(seed = unchecked(seed * 31 + j));
            documents["doc" + i] = payload;
        }
        _plainBundle = new SaveBundle(1, documents);

        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(i * 7 + 3);

        _obfuscatedWrite = new ObfuscatedSaveBackend(new NullSaveBackend());
        _encryptedWrite = new EncryptedSaveBackend(new NullSaveBackend(), key);

        // The protected read templates are produced through the public write path so this
        // benchmark never touches the internal Transform/Protect helpers.
        var obfuscatedCapture = new CaptureSaveBackend();
        new ObfuscatedSaveBackend(obfuscatedCapture).WriteAsync(_plainBundle).AsTask().GetAwaiter().GetResult();
        _obfuscatedRead = new ObfuscatedSaveBackend(new FreshCloneReadBackend(obfuscatedCapture.Bundle!));

        var encryptedCapture = new CaptureSaveBackend();
        using (var protector = new EncryptedSaveBackend(encryptedCapture, key))
            protector.WriteAsync(_plainBundle).AsTask().GetAwaiter().GetResult();
        _encryptedRead = new EncryptedSaveBackend(new FreshCloneReadBackend(encryptedCapture.Bundle!), key);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _encryptedWrite.Dispose();
        _encryptedRead.Dispose();
    }

    [Benchmark(Baseline = true)]
    public async Task Obfuscated_WriteAsync()
    {
        await _obfuscatedWrite.WriteAsync(_plainBundle);
    }

    [Benchmark]
    public async Task<SaveBundle?> Obfuscated_ReadAsync()
    {
        return await _obfuscatedRead.ReadAsync();
    }

    [Benchmark]
    public async Task Encrypted_WriteAsync()
    {
        await _encryptedWrite.WriteAsync(_plainBundle);
    }

    [Benchmark]
    public async Task<SaveBundle?> Encrypted_ReadAsync()
    {
        return await _encryptedRead.ReadAsync();
    }

    // Setup-only sink that grabs the protected bundle a wrapper produces through its public
    // write path. Deep-copies because a writing wrapper is free to reuse the bundle's dictionary
    // and arrays once the call returns.
    private sealed class CaptureSaveBackend : ISaveBackend
    {
        public SaveBundle? Bundle { get; private set; }

        public ValueTask<SaveBundle?> ReadAsync(CancellationToken cancellationToken = default) => new(Bundle);

        public ValueTask WriteAsync(SaveBundle bundle, CancellationToken cancellationToken = default)
        {
            Bundle = DeepClone(bundle);
            return default;
        }
    }

    // Materializes a deep clone of the template per read, the way a disk-backed backend returns
    // fresh arrays; the returned bundle is the caller's to do with as it pleases.
    private sealed class FreshCloneReadBackend : ISaveBackend
    {
        private readonly SaveBundle _template;

        public FreshCloneReadBackend(SaveBundle template) => _template = template;

        public ValueTask<SaveBundle?> ReadAsync(CancellationToken cancellationToken = default) =>
            new(DeepClone(_template));

        public ValueTask WriteAsync(SaveBundle bundle, CancellationToken cancellationToken = default) => default;
    }

    private static SaveBundle DeepClone(SaveBundle bundle)
    {
        var documents = new Dictionary<string, byte[]>(bundle.Documents.Count);
        foreach (var pair in bundle.Documents)
        {
            var copy = new byte[pair.Value.Length];
            Buffer.BlockCopy(pair.Value, 0, copy, 0, pair.Value.Length);
            documents[pair.Key] = copy;
        }
        return new SaveBundle(bundle.FormatVersion, documents);
    }
}
