using BenchmarkDotNet.Attributes;

namespace PlayerData.Benchmarks;

// Compares the capturing-closure call shape (pre-existing API) against the TState overloads
// added to eliminate per-call closure allocations on DocumentStore<T>.
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
public class DocumentStoreBenchmarks
{
    private const int GoldGain = 10;

    private DocumentStore<PlayerProfile> _store = null!;
    private PlayerProfile _replacement = null!;

    // Dedicated Clean/Dirty stores for IsDirty (round-2 perf plan ST-2): isolates the
    // Volatile.Read(_version)/Volatile.Read(_cleanVersion) comparison itself from the
    // Update/Replace call path the other benchmarks in this class exercise.
    private DocumentStore<PlayerProfile> _cleanStore = null!;
    private DocumentStore<PlayerProfile> _dirtyStore = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _store = new DocumentStore<PlayerProfile>(() => new PlayerProfile(1, "Hero", 0));
        _replacement = new PlayerProfile(2, "Hero", 100);

        _cleanStore = new DocumentStore<PlayerProfile>(() => new PlayerProfile(1, "Hero", 0));

        _dirtyStore = new DocumentStore<PlayerProfile>(() => new PlayerProfile(1, "Hero", 0));
        _dirtyStore.Replace(new PlayerProfile(1, "Hero", 1));
    }

    [Benchmark(Baseline = true)]
    public PlayerProfile Update_ClosureCapture()
    {
        var gain = GoldGain;
        return _store.Update(current => current with { Gold = current.Gold + gain });
    }

    [Benchmark]
    public PlayerProfile Update_WithState()
    {
        return _store.Update(GoldGain, static (gain, current) => current with { Gold = current.Gold + gain });
    }

    // Isolates DocumentStore<T>.Update's own cost from the `with` expression's allocation: the
    // updater here returns the already-allocated `_replacement` unchanged, so it performs zero
    // allocation of its own. If this benchmark's Allocated is ~0B (matching Value_Read), that
    // proves Update_WithState's 40B comes entirely from PlayerProfile's `with` expression
    // creating a new record instance, not from DocumentStore<T> itself (see plan ST-3).
    [Benchmark]
    public PlayerProfile Update_WithState_UpdaterAllocatesNothing()
    {
        return _store.Update(_replacement, static (value, _) => value);
    }

    [Benchmark]
    public PlayerProfile Replace_OldPattern_ClosureCapture()
    {
        var value = _replacement;
        return _store.Update(current => value);
    }

    [Benchmark]
    public PlayerProfile Replace_New()
    {
        return _store.Replace(_replacement);
    }

    [Benchmark]
    public PlayerProfile Value_Read()
    {
        return _store.Value;
    }

    [Benchmark]
    public bool IsDirty_Clean()
    {
        return _cleanStore.IsDirty;
    }

    [Benchmark]
    public bool IsDirty_Dirty()
    {
        return _dirtyStore.IsDirty;
    }
}
