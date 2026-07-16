using BenchmarkDotNet.Attributes;

namespace PlayerData.Benchmarks;

// Compares the capturing-closure call shape (pre-existing API) against the TState overloads
// added to KeyedDocumentStore<TKey,T>, and reports the ImmutableDictionary write baseline
// (Upsert/Set/Remove) which is unchanged by this pass - the lock-free CAS design was kept, so
// O(log n) node allocation on every write is an accepted trade-off, not a regression.
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
public class KeyedDocumentStoreBenchmarks
{
    private const int SeedCount = 200;
    private const int Amount = 3;

    private KeyedDocumentStore<string, InventoryItem> _store = null!;

    // Dedicated Clean/Dirty stores for IsDirty (round-2 perf plan ST-2): isolates the
    // Volatile.Read(_version)/Volatile.Read(_cleanVersion) comparison itself from the
    // Upsert/TryUpdate/GetOrAdd call paths the other benchmarks in this class exercise.
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

    // Baseline: ImmutableDictionary.SetItem allocates O(log n) tree nodes per write regardless
    // of closure usage - this is the accepted lock-free trade-off, not something this pass changes.
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
