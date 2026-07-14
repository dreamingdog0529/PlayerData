using BenchmarkDotNet.Attributes;
using PlayerData.R3;
using R3;

namespace PlayerData.Benchmarks;

// Measures a single subscribe-then-dispose round trip through PlayerDataStoreR3Extensions /
// PlayerDataCollectionR3Extensions (round-2 perf plan ST-3, baseline for ST-5). Each [Benchmark]
// call is the unit of work being priced: SubscribeCore allocates a local-function Handler closure
// plus a Disposable.Create-wrapping lambda closure, on every call. replayCurrent:false isolates
// that subscribe-mechanism cost from the extra OnNext(store.Value) replay call.
[MemoryDiagnoser]
[SimpleJob(launchCount: 3, warmupCount: 5, iterationCount: 10)]
public class R3AdapterBenchmarks
{
    private DocumentStore<PlayerProfile> _store = null!;
    private KeyedDocumentStore<string, InventoryItem> _collection = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _store = new DocumentStore<PlayerProfile>(() => new PlayerProfile(1, "Hero", 0));
        _collection = new KeyedDocumentStore<string, InventoryItem>(i => i.ItemId);
    }

    [Benchmark(Baseline = true)]
    public void DocumentSubscribeUnsubscribe()
    {
        using var subscription = _store.AsObservable(replayCurrent: false).Subscribe(_ => { });
    }

    [Benchmark]
    public void DocumentChangeSubscribeUnsubscribe()
    {
        using var subscription = _store.AsChangeObservable(replayCurrent: false).Subscribe(_ => { });
    }

    [Benchmark]
    public void CollectionSubscribeUnsubscribe()
    {
        using var subscription = _collection.AsObservable().Subscribe(_ => { });
    }
}
