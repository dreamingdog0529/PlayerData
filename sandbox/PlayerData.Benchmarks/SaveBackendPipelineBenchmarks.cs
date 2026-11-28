using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using PlayerData.Persistence;

namespace PlayerData.Benchmarks;

// ObfuscatedSaveBackend / EncryptedSaveBackend の ReadAsync / WriteAsync のパイプライン全体を測ります。
// 呼び出しのたびに変換されるドキュメントの Dictionary や SaveBundle も含みます。ドキュメントごとの
// Transform / Protect / Unprotect の核だけを測る ObfuscatedSaveBackendBenchmarks /
// EncryptedSaveBackendBenchmarks とは違うところです。内側のバックエンドはこのベンチマーク専用のもので、
// 計測期間を通して動きが変わらないように、わざとそうしています。WriteAsync の向き先は
// NullSaveBackend（内側の寄与はゼロ）で、ReadAsync は FreshCloneReadBackend から読みます。こちらは、
// ディスクを使うバックエンドが読みたての配列を返すのと同じように、呼び出しのたびに深いコピーを作ります。
// そのコピーのコストは前後の実行で一定なので、Allocated に差が出たら、それはラッパー自身の分です。
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

        // 保護された読み取り用のひな形は、公開されている書き込み経路を通して作ります。
        // このベンチマークが、内部の Transform / Protect ヘルパーに触ることはありません。
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

    // セットアップ専用の受け皿です。ラッパーが公開されている書き込み経路を通して作った、保護済みのバンドルを受け取ります。
    // 深いコピーをしています。書き込むラッパーは、呼び出しが終わったあとにバンドルのディクショナリや
    // 配列を再利用してもよいことになっているためです。
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

    // ひな形の深いコピーを、読み取りのたびに作ります。ディスクを使うバックエンドが読みたての配列を返すのと同じやり方です。
    // 返されたバンドルは、呼び出し側が自由に扱ってかまいません。
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
