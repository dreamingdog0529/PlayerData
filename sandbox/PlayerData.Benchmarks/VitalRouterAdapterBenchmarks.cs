using BenchmarkDotNet.Attributes;
using PlayerData.VitalRouter;
using VitalRouter;

namespace PlayerData.Benchmarks;

// Measures a single subscribe-then-dispose round trip through PlayerDataStoreVitalRouterExtensions
// / PlayerDataCollectionVitalRouterExtensions.PublishChangesTo (round-2 perf plan ST-3, baseline
// for ST-4). Each [Benchmark] call is the unit of work being priced: PublishChangesTo allocates a
// local-function Handler closure plus an ActionDisposable-wrapping lambda closure, on every call.
[MemoryDiagnoser]
[SimpleJob(launchCount: 3, warmupCount: 5, iterationCount: 10)]
public class VitalRouterAdapterBenchmarks
{
    private Router _router = null!;
    private DocumentStore<PlayerProfile> _store = null!;
    private KeyedDocumentStore<string, InventoryItem> _collection = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _router = new Router();
        _store = new DocumentStore<PlayerProfile>(() => new PlayerProfile(1, "Hero", 0));
        _collection = new KeyedDocumentStore<string, InventoryItem>(i => i.ItemId);
    }

    [Benchmark(Baseline = true)]
    public void DocumentSubscribeUnsubscribe()
    {
        using var subscription = _store.PublishChangesTo(_router);
    }

    [Benchmark]
    public void CollectionSubscribeUnsubscribe()
    {
        using var subscription = _collection.PublishChangesTo(_router);
    }
}
