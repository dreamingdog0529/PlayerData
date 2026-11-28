using BenchmarkDotNet.Attributes;
using PlayerData.R3;
using R3;
using PlayerData.Documents;

namespace PlayerData.Benchmarks;

// PlayerDataStoreR3Extensions / PlayerDataCollectionR3Extensions 経由で、購読してから破棄するまでの
// 往復を1回分だけ測ります（ラウンド2の性能計画 ST-3 で、ST-5 の基準になるものです）。[Benchmark] の
// 呼び出し1回が、測りたい仕事の単位です。SubscribeCore は呼び出しのたびに、ローカル関数の Handler
// クロージャと、それを Disposable.Create で包むラムダのクロージャを確保します。replayCurrent:false に
// することで、余分な OnNext(store.Value) の再生呼び出しではなく、購読の仕組み自体のコストを切り出せます。
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
