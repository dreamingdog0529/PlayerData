using BenchmarkDotNet.Attributes;

namespace PlayerData.Benchmarks;

// Measures one whole SuppressNotifications cycle (open scope -> mutate -> Dispose/flush) as a
// single op, pricing the pieces the per-mutation benchmarks can't see: the scope instance
// itself and the pending-change list the collection store coalesces into while suppressed.
// Mutations swap pre-allocated instances (same technique as ZeroAllocationBenchmarks) so caller
// payload construction stays out of the measurement, and handlers are subscribed so the flush
// actually invokes them, like a UI-bound session would.
// These cycles sit in the tens-of-nanoseconds-to-microsecond range, where single-process JIT
// tiering variance can swamp the effect; launchCount 3 averages across separate processes.
[MemoryDiagnoser]
[SimpleJob(launchCount: 3, warmupCount: 5, iterationCount: 10)]
public class SuppressionCycleBenchmarks
{
    private const int BurstSize = 8;

    private SaveSession _session = null!;
    private IBag<string, InventoryItem> _items = null!;
    private InventoryItem _itemA = null!;
    private InventoryItem _itemB = null!;
    private int _flip;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _session = new SaveSession(new NullSaveBackend());
        _session.DirtyChanged += static _ => { };
        _items = _session.AddCollection<string, InventoryItem>("items", i => i.ItemId);
        _items.Changed += static _ => { };
        _itemA = new InventoryItem("item0", 1);
        _itemB = new InventoryItem("item0", 2);
        _items.Upsert(_itemA);
    }

    // Open + immediately dispose: isolates the cost of the scope itself.
    [Benchmark(Baseline = true)]
    public void SuppressAndFlush_Empty()
    {
        using (_session.SuppressNotifications())
        {
        }
    }

    // A representative batch mutation: BurstSize coalesced collection changes flushed on dispose.
    [Benchmark]
    public void SuppressAndFlush_CollectionBurst()
    {
        using (_session.SuppressNotifications())
        {
            for (var i = 0; i < BurstSize; i++)
                _items.Upsert((++_flip & 1) == 0 ? _itemA : _itemB);
        }
    }
}
