using BenchmarkDotNet.Attributes;

namespace PlayerData.Benchmarks;

// Proves the library's steady-state hot paths are allocation-free (Allocated = 0 B/op, or "-" in
// the BenchmarkDotNet table). Unlike the other benchmark classes, every mutation here swaps in a
// PRE-allocated instance instead of `new`-ing or `with`-ing one per call, so the measured
// allocation is the store/session path's own - not the caller's payload construction, which is
// the caller's choice and unavoidable for immutable data anyway.
//
// Covered paths and why they matter per-frame in a game loop:
//   - Document Replace / collection Upsert-TryUpdate-GetOrAdd on a session-attached store: the
//     per-mutation pipeline (CAS or ConcurrentDictionary in-place update -> version bump ->
//     OnMutated suppression query -> DirtyChanged/Changed raise with struct payloads).
//   - Value/TryGet reads and IsDirty: polled by autosave and UI binding every frame.
//   - CommitAsync on a clean session: the autosave-tick no-op, which must not pay for the
//     save it doesn't perform.
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 3, printSource: true)]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
public class ZeroAllocationBenchmarks
{
    private SaveSession _session = null!;
    private IDoc<PlayerProfile> _profile = null!;
    private IBag<string, InventoryItem> _items = null!;

    private PlayerProfile _profileA = null!;
    private PlayerProfile _profileB = null!;
    private InventoryItem _itemA = null!;
    private InventoryItem _itemB = null!;
    private int _flip;

    private SaveSession _cleanSession = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _session = new SaveSession(new InMemorySaveBackend());
        _session.DirtyChanged += static _ => { };
        _profile = _session.AddDocument("profile", () => new PlayerProfile(1, "Hero", 0));
        _profile.Changed += static _ => { };
        _items = _session.AddCollection<string, InventoryItem>("items", i => i.ItemId);
        _items.Changed += static _ => { };

        _profileA = new PlayerProfile(1, "Hero", 100);
        _profileB = new PlayerProfile(1, "Hero", 200);
        _itemA = new InventoryItem("item0", 1);
        _itemB = new InventoryItem("item0", 2);
        _items.Upsert(_itemA);
        _items.Upsert(new InventoryItem("item1", 1));

        _cleanSession = new SaveSession(new InMemorySaveBackend());
        var cleanProfile = _cleanSession.AddDocument("profile", () => new PlayerProfile(1, "Hero", 0));
        cleanProfile.Replace(new PlayerProfile(1, "Hero", 1));
        _cleanSession.CommitAsync().AsTask().GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true)]
    public PlayerProfile DocumentReplace()
    {
        return _profile.Replace((++_flip & 1) == 0 ? _profileA : _profileB);
    }

    [Benchmark]
    public InventoryItem CollectionUpsert_ExistingKey()
    {
        return _items.Upsert((++_flip & 1) == 0 ? _itemA : _itemB);
    }

    [Benchmark]
    public bool CollectionTryUpdate_ExistingKey()
    {
        // The updater swaps in the pre-allocated other instance, so the store's TryUpdate
        // machinery (AddOrUpdate + UpdateContext + notification) is the only thing priced.
        return _items.TryUpdate(
            "item0",
            (++_flip & 1) == 0 ? _itemA : _itemB,
            static (replacement, _) => replacement,
            out _);
    }

    [Benchmark]
    public InventoryItem CollectionGetOrAdd_ExistingKey()
    {
        return _items.GetOrAdd("item1", static id => new InventoryItem(id, 1));
    }

    [Benchmark]
    public PlayerProfile DocumentValue_Read()
    {
        return _profile.Value;
    }

    [Benchmark]
    public bool CollectionTryGet()
    {
        return _items.TryGet("item1", out _);
    }

    [Benchmark]
    public bool SessionIsDirty()
    {
        return _session.IsDirty;
    }

    [Benchmark]
    public async System.Threading.Tasks.Task CommitAsync_CleanNoOp()
    {
        await _cleanSession.CommitAsync();
    }
}
