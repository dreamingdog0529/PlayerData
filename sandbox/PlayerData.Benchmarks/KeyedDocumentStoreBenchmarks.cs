using BenchmarkDotNet.Attributes;
using PlayerData.Documents;

namespace PlayerData.Benchmarks;

// キャプチャするクロージャを使う呼び出し方（もともとの API）と、KeyedDocumentStore<TKey,T> に追加した
// TState ありのオーバーロードを比べます。あわせて、ImmutableDictionary への書き込みの基準も報告します
// （Upsert / Set / Remove）。こちらは今回の変更では変わっていません。ロックなしの CAS の設計を
// そのままにしたので、書き込みのたびに O(log n) のノードが確保されるのは、悪化ではなく受け入れたトレードオフです。
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
public class KeyedDocumentStoreBenchmarks
{
    private const int SeedCount = 200;
    private const int Amount = 3;

    private KeyedDocumentStore<string, InventoryItem> _store = null!;

    // IsDirty 専用の、きれいな状態と汚れた状態のストアです（ラウンド2の性能計画 ST-2）。Volatile.Read(_version) と
    // Volatile.Read(_cleanVersion) の比較そのものを、このクラスのほかのベンチマークが使う
    // Upsert / TryUpdate / GetOrAdd の呼び出し経路から切り離して測ります。
    private KeyedDocumentStore<string, InventoryItem> _cleanStore = null!;
    private KeyedDocumentStore<string, InventoryItem> _dirtyStore = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _store = new KeyedDocumentStore<string, InventoryItem>(i => i.ItemId);
        for (var i = 0; i < SeedCount; i++)
            _store.Upsert(new InventoryItem("item" + i, 0));

        _cleanStore = new KeyedDocumentStore<string, InventoryItem>(i => i.ItemId);
        _cleanStore.Upsert(new InventoryItem("item0", 0));
        _cleanStore.MarkClean();

        _dirtyStore = new KeyedDocumentStore<string, InventoryItem>(i => i.ItemId);
        _dirtyStore.Upsert(new InventoryItem("item0", 1));
    }

    [Benchmark(Baseline = true)]
    public InventoryItem TryUpdate_ClosureCapture()
    {
        var amount = Amount;
        _store.TryUpdate("item0", existing => existing with { Count = existing.Count + amount }, out var updated);
        return updated;
    }

    [Benchmark]
    public InventoryItem TryUpdate_WithState()
    {
        _store.TryUpdate("item0", Amount, static (amount, existing) => existing with { Count = existing.Count + amount }, out var updated);
        return updated;
    }

    [Benchmark]
    public InventoryItem GetOrAdd_ExistingKey_ClosureCapture()
    {
        var seed = Amount;
        return _store.GetOrAdd("item1", id => new InventoryItem(id, seed));
    }

    [Benchmark]
    public InventoryItem GetOrAdd_ExistingKey_WithState()
    {
        return _store.GetOrAdd("item1", Amount, static (id, seed) => new InventoryItem(id, seed));
    }

    // 基準の話です。ImmutableDictionary.SetItem は、クロージャを使うかどうかに関係なく、書き込みのたびに
    // O(log n) のツリーのノードを確保します。これはロックなしで済ませるための受け入れたトレードオフで、今回の変更で変えるものではありません。
    [Benchmark]
    public InventoryItem Upsert_ExistingKey()
    {
        return _store.Upsert(new InventoryItem("item2", 1));
    }

    [Benchmark]
    public bool TryGet_ExistingKey()
    {
        return _store.TryGet("item3", out _);
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
