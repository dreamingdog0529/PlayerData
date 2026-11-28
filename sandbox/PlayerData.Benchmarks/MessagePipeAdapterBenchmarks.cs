using BenchmarkDotNet.Attributes;
using MessagePipe;
using Microsoft.Extensions.DependencyInjection;
using PlayerData.MessagePipe;
using PlayerData.Documents;

namespace PlayerData.Benchmarks;

// PlayerDataStoreMessagePipeExtensions / PlayerDataCollectionMessagePipeExtensions.PublishChangesTo 経由で、
// 購読してから破棄するまでの往復を1回分だけ測ります（ラウンド2の性能計画 ST-3 で、ST-4 の基準に
// なるものです）。[Benchmark] の呼び出し1回が、測りたい仕事の単位です。PublishChangesTo は呼び出しの
// たびに、ローカル関数の Handler クロージャと、ActionDisposable で包むラムダのクロージャを確保します。
[MemoryDiagnoser]
[SimpleJob(launchCount: 3, warmupCount: 5, iterationCount: 10)]
public class MessagePipeAdapterBenchmarks
{
    private ServiceProvider _provider = null!;
    private IPublisher<PlayerProfile> _profilePublisher = null!;
    private IPublisher<BagChange<string, InventoryItem>> _itemsPublisher = null!;
    private DocumentStore<PlayerProfile> _store = null!;
    private KeyedDocumentStore<string, InventoryItem> _collection = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var services = new ServiceCollection();
        services.AddMessagePipe();
        _provider = services.BuildServiceProvider();

        _profilePublisher = _provider.GetRequiredService<IPublisher<PlayerProfile>>();
        _itemsPublisher = _provider.GetRequiredService<IPublisher<BagChange<string, InventoryItem>>>();

        _store = new DocumentStore<PlayerProfile>(() => new PlayerProfile(1, "Hero", 0));
        _collection = new KeyedDocumentStore<string, InventoryItem>(i => i.ItemId);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _provider.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void DocumentSubscribeUnsubscribe()
    {
        using var subscription = _store.PublishChangesTo(_profilePublisher);
    }

    [Benchmark]
    public void CollectionSubscribeUnsubscribe()
    {
        using var subscription = _collection.PublishChangesTo(_itemsPublisher);
    }
}
