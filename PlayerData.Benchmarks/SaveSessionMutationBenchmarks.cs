using BenchmarkDotNet.Attributes;

namespace PlayerData.Benchmarks;

// Measures the SaveSession-attached DocumentStore/KeyedDocumentStore mutation path in isolation
// (Update/Upsert only, no CommitAsync/backend I-O), to isolate the onMutated/suppression-query
// consolidation (round-2 perf plan ST-1) from the CommitAsync-level costs SaveSessionBenchmarks
// already covers. Both the unsuppressed path (SaveSession.OnMutated queries suppression once and
// drives PublishDirty) and the SuppressNotifications-active path (RaiseChanged coalesces into
// _pendingChange/_pendingChanges) are measured, since both delegates the consolidation replaced
// queried suppression state on every mutation regardless of which branch ran.
// These operations run in the tens-of-nanoseconds range, where single-process JIT tiering
// variance can swamp a genuine few-ns effect; launchCount averages across separate processes to
// keep that noise from being mistaken for a real regression or improvement.
[MemoryDiagnoser]
[SimpleJob(launchCount: 3, warmupCount: 5, iterationCount: 10)]
public class SaveSessionMutationBenchmarks
{
    private SaveSession _session = null!;
    private IDoc<PlayerProfile> _profile = null!;
    private IBag<string, InventoryItem> _items = null!;
    private int _counter;

    private SaveSession _suppressedSession = null!;
    private IDoc<PlayerProfile> _suppressedProfile = null!;
    private IBag<string, InventoryItem> _suppressedItems = null!;
    private System.IDisposable _suppressScope = null!;
    private int _suppressedCounter;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _session = new SaveSession(new InMemorySaveBackend());
        _session.DirtyChanged += static _ => { };
        _profile = _session.AddDocument("profile", () => new PlayerProfile(1, "Hero", 0));
        _items = _session.AddCollection<string, InventoryItem>("items", i => i.ItemId);
        _items.Upsert(new InventoryItem("item0", 0));

        _suppressedSession = new SaveSession(new InMemorySaveBackend());
        _suppressedSession.DirtyChanged += static _ => { };
        _suppressedProfile = _suppressedSession.AddDocument("profile", () => new PlayerProfile(1, "Hero", 0));
        _suppressedItems = _suppressedSession.AddCollection<string, InventoryItem>("items", i => i.ItemId);
        _suppressedItems.Upsert(new InventoryItem("item0", 0));
        _suppressScope = _suppressedSession.SuppressNotifications();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _suppressScope.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void DocumentUpdate_Unsuppressed()
    {
        _profile.Replace(_profile.Value with { Gold = ++_counter });
    }

    [Benchmark]
    public void CollectionUpsert_Unsuppressed()
    {
        _items.Upsert(new InventoryItem("item0", ++_counter));
    }

    [Benchmark]
    public void DocumentUpdate_Suppressed()
    {
        _suppressedProfile.Replace(_suppressedProfile.Value with { Gold = ++_suppressedCounter });
    }

    [Benchmark]
    public void CollectionUpsert_Suppressed()
    {
        _suppressedItems.Upsert(new InventoryItem("item0", ++_suppressedCounter));
    }
}
