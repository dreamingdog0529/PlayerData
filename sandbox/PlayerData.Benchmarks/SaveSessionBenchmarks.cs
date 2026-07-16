using BenchmarkDotNet.Attributes;

namespace PlayerData.Benchmarks;

// Measures SaveSession.CommitAsync with an in-memory backend (I/O excluded) to isolate the
// dirty-check / snapshot logic that Tier 2 cleaned up (Any(...) LINQ boxing -> manual loop,
// ToDictionary -> SnapshotItems copy).
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
public class SaveSessionBenchmarks
{
    private const int SeedCount = 50;

    private SaveSession _dirtySession = null!;
    private IDoc<PlayerProfile> _dirtyProfile = null!;
    private int _counter;

    private SaveSession _cleanSession = null!;

    private SaveSession _nullBackendSession = null!;
    private IDoc<PlayerProfile> _nullBackendProfile = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _dirtySession = new SaveSession(new InMemorySaveBackend());
        _dirtyProfile = _dirtySession.AddDocument("profile", () => new PlayerProfile(1, "Hero", 0));
        var dirtyItems = _dirtySession.AddCollection<string, InventoryItem>("items", i => i.ItemId);
        for (var i = 0; i < SeedCount; i++)
            dirtyItems.Upsert(new InventoryItem("item" + i, i));

        _cleanSession = new SaveSession(new InMemorySaveBackend());
        var cleanProfile = _cleanSession.AddDocument("profile", () => new PlayerProfile(1, "Hero", 0));
        var cleanItems = _cleanSession.AddCollection<string, InventoryItem>("items", i => i.ItemId);
        for (var i = 0; i < SeedCount; i++)
            cleanItems.Upsert(new InventoryItem("item" + i, i));
        _cleanSession.CommitAsync().AsTask().GetAwaiter().GetResult();
        _ = cleanProfile;

        _nullBackendSession = new SaveSession(new NullSaveBackend());
        _nullBackendProfile = _nullBackendSession.AddDocument("profile", () => new PlayerProfile(1, "Hero", 0));
        var nullItems = _nullBackendSession.AddCollection<string, InventoryItem>("items", i => i.ItemId);
        for (var i = 0; i < SeedCount; i++)
            nullItems.Upsert(new InventoryItem("item" + i, i));
        _nullBackendSession.CommitAsync().AsTask().GetAwaiter().GetResult();
    }

    // Every invocation dirties the profile doc itself, so this stays representative regardless
    // of how many invocations BenchmarkDotNet batches into one measured iteration.
    [Benchmark(Baseline = true)]
    public async System.Threading.Tasks.Task CommitAsync_Dirty()
    {
        _dirtyProfile.Replace(_dirtyProfile.Value with { Gold = ++_counter });
        await _dirtySession.CommitAsync();
    }

    // _cleanSession is never mutated after the initial commit in GlobalSetup, so every call
    // exercises the IsDirty-false no-op fast path (the common case for periodic autosave polling).
    [Benchmark]
    public async System.Threading.Tasks.Task CommitAsync_NoOp()
    {
        await _cleanSession.CommitAsync();
    }

    // Same steady-state shape as CommitAsync_Dirty but against a backend that neither stores nor
    // allocates, so the Allocated column is purely the session's own commit path (snapshot,
    // document dictionary, bundle, serialize).
    [Benchmark]
    public async System.Threading.Tasks.Task CommitAsync_Dirty_NullBackend()
    {
        _nullBackendProfile.Replace(_nullBackendProfile.Value with { Gold = ++_counter });
        await _nullBackendSession.CommitAsync();
    }
}
