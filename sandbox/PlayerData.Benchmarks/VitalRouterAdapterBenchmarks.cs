using BenchmarkDotNet.Attributes;
using PlayerData.VitalRouter;
using VitalRouter;
using PlayerData.Documents;

namespace PlayerData.Benchmarks;

// PlayerDataStoreVitalRouterExtensions / PlayerDataCollectionVitalRouterExtensions.PublishChangesTo 経由で、
// 購読してから破棄するまでの往復を1回分だけ測ります（ラウンド2の性能計画 ST-3 で、ST-4 の基準に
// なるものです）。[Benchmark] の呼び出し1回が、測りたい仕事の単位です。PublishChangesTo は呼び出しの
// たびに、ローカル関数の Handler クロージャと、ActionDisposable で包むラムダのクロージャを確保します。
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
