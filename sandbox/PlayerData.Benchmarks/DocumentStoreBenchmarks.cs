using BenchmarkDotNet.Attributes;
using PlayerData.Documents;

namespace PlayerData.Benchmarks;

// キャプチャするクロージャを使う呼び出し方（もともとの API）と、DocumentStore<T> への呼び出しごとの
// クロージャ確保をなくすために追加した、TState ありのオーバーロードを比べます。
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
public class DocumentStoreBenchmarks
{
    private const int GoldGain = 10;

    private DocumentStore<PlayerProfile> _store = null!;
    private PlayerProfile _replacement = null!;

    // IsDirty 専用の、きれいな状態と汚れた状態のストアです（ラウンド2の性能計画 ST-2）。Volatile.Read(_version) と
    // Volatile.Read(_cleanVersion) の比較そのものを、このクラスのほかのベンチマークが使う
    // Update / Replace の呼び出し経路から切り離して測ります。
    private DocumentStore<PlayerProfile> _cleanStore = null!;
    private DocumentStore<PlayerProfile> _dirtyStore = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _store = new DocumentStore<PlayerProfile>(() => new PlayerProfile(1, "Hero", 0));
        _replacement = new PlayerProfile(2, "Hero", 100);

        _cleanStore = new DocumentStore<PlayerProfile>(() => new PlayerProfile(1, "Hero", 0));

        _dirtyStore = new DocumentStore<PlayerProfile>(() => new PlayerProfile(1, "Hero", 0));
        _dirtyStore.Replace(new PlayerProfile(1, "Hero", 1));
    }

    [Benchmark(Baseline = true)]
    public PlayerProfile Update_ClosureCapture()
    {
        var gain = GoldGain;
        return _store.Update(current => current with { Gold = current.Gold + gain });
    }

    [Benchmark]
    public PlayerProfile Update_WithState()
    {
        return _store.Update(GoldGain, static (gain, current) => current with { Gold = current.Gold + gain });
    }

    // DocumentStore<T>.Update 自身のコストを、`with` 式の確保から切り離して測ります。
    // ここでの updater は、確保済みの `_replacement` をそのまま返すので、自分では何も確保しません。
    // このベンチマークの Allocated がだいたい 0B（Value_Read と同じくらい）なら、Update_WithState の
    // 40B はすべて、PlayerProfile の `with` 式が新しいレコードのインスタンスを作る分であり、
    // DocumentStore<T> 自身の分ではないことが分かります（計画の ST-3 をご覧ください）。
    [Benchmark]
    public PlayerProfile Update_WithState_UpdaterAllocatesNothing()
    {
        return _store.Update(_replacement, static (value, _) => value);
    }

    [Benchmark]
    public PlayerProfile Replace_OldPattern_ClosureCapture()
    {
        var value = _replacement;
        return _store.Update(current => value);
    }

    [Benchmark]
    public PlayerProfile Replace_New()
    {
        return _store.Replace(_replacement);
    }

    [Benchmark]
    public PlayerProfile Value_Read()
    {
        return _store.Value;
    }

    [Benchmark]
    public bool IsDirty_Clean()
    {
        return _cleanStore.IsDirty;
    }

    [Benchmark]
    public bool IsDirty_Dirty()
    {
        return _dirtyStore.IsDirty;
    }
}
