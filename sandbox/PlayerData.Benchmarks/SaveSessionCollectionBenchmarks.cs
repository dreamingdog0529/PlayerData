using BenchmarkDotNet.Attributes;

namespace PlayerData.Benchmarks;

// Isolates SaveSession.CollectionParticipant's serialize/deserialize path (GetBytes/LoadBytes) from
// the single-document path already covered by SaveSessionBenchmarks. Dirty tracking on
// KeyedDocumentStore is store-level, not per-item, so upserting one existing key forces
// GetBytes() to re-serialize every seeded item on the next commit - this is the path the
// ImmutableDictionary-direct-serialize optimization targets.
//
// Uses launchCount:3 instead of the launchCount:1 used elsewhere in this project: a
// launchCount:1/iterationCount:5 job on a 200-item ImmutableDictionary write path produced 3x
// Mean swings and inverted rankings between two otherwise-identical clean runs (confirmed by
// re-running KeyedDocumentStoreBenchmarks twice back to back), because ImmutableDictionary.SetItem's
// node-allocation count depends on tree shape, which the pilot phase's warmup mutations perturb
// differently run to run. Multiple process launches average that out.
[MemoryDiagnoser]
[SimpleJob(launchCount: 3, warmupCount: 3, iterationCount: 5)]
public class SaveSessionCollectionBenchmarks
{
    private const int SeedCountSmall = 50;
    private const int SeedCountLarge = 200;

    private SaveSession _session50 = null!;
    private IBag<string, InventoryItem> _items50 = null!;
    private int _counter50;

    private SaveSession _session200 = null!;
    private IBag<string, InventoryItem> _items200 = null!;
    private int _counter200;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _session50 = new SaveSession(new InMemorySaveBackend());
        _items50 = _session50.AddCollection<string, InventoryItem>("items", i => i.ItemId);
        for (var i = 0; i < SeedCountSmall; i++)
            _items50.Upsert(new InventoryItem("item" + i, i));

        _session200 = new SaveSession(new InMemorySaveBackend());
        _items200 = _session200.AddCollection<string, InventoryItem>("items", i => i.ItemId);
        for (var i = 0; i < SeedCountLarge; i++)
            _items200.Upsert(new InventoryItem("item" + i, i));
    }

    [Benchmark(Baseline = true)]
    public async System.Threading.Tasks.Task CommitAsync_DirtyCollection_Seed50()
    {
        _items50.Upsert(new InventoryItem("item0", ++_counter50));
        await _session50.CommitAsync();
    }

    [Benchmark]
    public async System.Threading.Tasks.Task CommitAsync_DirtyCollection_Seed200()
    {
        _items200.Upsert(new InventoryItem("item0", ++_counter200));
        await _session200.CommitAsync();
    }
}
