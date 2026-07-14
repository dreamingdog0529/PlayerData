using BenchmarkDotNet.Attributes;
using MessagePipe;
using Microsoft.Extensions.DependencyInjection;
using PlayerData.MessagePipe;

namespace PlayerData.Benchmarks;

// Measures a single subscribe-then-dispose round trip through PlayerDataStoreMessagePipeExtensions
// / PlayerDataCollectionMessagePipeExtensions.PublishChangesTo (round-2 perf plan ST-3, baseline
// for ST-4). Each [Benchmark] call is the unit of work being priced: PublishChangesTo allocates a
// local-function Handler closure plus an ActionDisposable-wrapping lambda closure, on every call.
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
