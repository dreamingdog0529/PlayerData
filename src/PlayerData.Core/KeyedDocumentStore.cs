using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace PlayerData;

// Backed by ConcurrentDictionary<TKey,T>: reads (TryGet/Contains/Snapshot enumeration) are
// lock-free; writes take a short per-bucket lock instead of retrying a whole-tree CAS. Every
// conditional/read-modify-write operation goes through AddOrUpdate/GetOrAdd so the update
// delegate always observes the truly-live current value. ConcurrentDictionary.TryUpdate(key,
// newValue, comparisonValue) is deliberately never used: its comparison is
// EqualityComparer<T>.Default, which is structural equality for record T, so a different
// instance that happens to compare equal to a stale read would make TryUpdate report success
// against that stale base and silently discard whatever produced the "equal" instance (a real
// ABA hazard, reproduced empirically for record-typed T - see PlayerData plan
// 202607131429-playerdata-extreme-perf.plan.md, Assumption 2).
public sealed class KeyedDocumentStore<TKey, T> : IBag<TKey, T>
    where TKey : notnull
    where T : class
{
    private readonly Func<T, TKey> _keySelector;
    // Called exactly once per mutation (not once for the dirty-notification path and again for
    // the Changed-coalescing check, as the two separate delegates this replaced did). The
    // callback both forwards the mutation to the owning SaveSession and returns whether
    // notifications are currently suppressed, so the store can reuse that single answer for its
    // own RaiseChanged decision below instead of querying suppression state a second time.
    private readonly Func<bool>? _onMutated;
    private readonly object _pendingGate = new();
    // Not readonly: ReplaceFromLoad swaps in the freshly deserialized dictionary wholesale
    // (a single volatile reference write) instead of clearing and re-inserting item by item.
    // Reference reads are atomic, so a concurrent reader sees either the old or the new
    // dictionary in full, never a half-populated one - strictly better than the old
    // Clear-then-copy, whose intermediate states (empty, partially filled) were observable.
    private ConcurrentDictionary<TKey, T> _items = new();
    private long _version;
    private long _cleanVersion;
    private List<BagChange<TKey, T>>? _pendingChanges;
    // The previous suppress cycle's (cleared) list, recycled by RaiseChanged so steady-state
    // suppress/flush cycles allocate no new list once its capacity has warmed up. Guarded by
    // _pendingGate like _pendingChanges itself.
    private List<BagChange<TKey, T>>? _pendingChangesSpare;

    public KeyedDocumentStore(
        Func<T, TKey> keySelector,
        Func<bool>? onMutated = null)
    {
        _keySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
        _onMutated = onMutated;
        _version = 0;
        _cleanVersion = 0;
    }

    public int Count => _items.Count;

    // Weakly consistent as of the redesign in plan 202607131429: this returns the live
    // ConcurrentDictionary instance (not a frozen point-in-time copy). Enumerating it while
    // another thread writes may observe some but not all of that write's effects; individual
    // key reads are always atomic. See SnapshotItems below for why this is safe for
    // SaveSession's serialize/validate paths.
    public IReadOnlyDictionary<TKey, T> Snapshot => _items;

    // Concretely-typed accessor for in-assembly consumers (SaveSession serialization) that need
    // the ConcurrentDictionary-specific enumerator to avoid the ReadOnlyCollection allocation
    // that ConcurrentDictionary.Values would otherwise incur.
    internal ConcurrentDictionary<TKey, T> SnapshotItems => _items;

    public event Action<BagChange<TKey, T>>? Changed;

    internal bool IsDirty
    {
        get
        {
            var version = Volatile.Read(ref _version);
            return version != Volatile.Read(ref _cleanVersion);
        }
    }

    internal long Version => Volatile.Read(ref _version);

    public bool Contains(TKey key) => _items.ContainsKey(key);

    public bool TryGet(TKey key, out T value) => _items.TryGetValue(key, out value!);

    public T Get(TKey key)
    {
        if (TryGet(key, out var value)) return value;
        throw new KeyNotFoundException($"No item with key '{key}' exists in the bag.");
    }

    public T Upsert(T entity)
    {
        if (entity is null) throw new ArgumentNullException(nameof(entity));
        var key = _keySelector(entity);
        return SetCore(key, entity, validateKeyMatch: false);
    }

    public T Set(TKey key, T entity)
    {
        if (entity is null) throw new ArgumentNullException(nameof(entity));
        return SetCore(key, entity, validateKeyMatch: true);
    }

    public bool TryUpdate(TKey key, Func<T, T> updater, out T updated)
    {
        if (updater is null) throw new ArgumentNullException(nameof(updater));
        return TryUpdate(key, updater, static (u, before) => u(before), out updated);
    }

    public bool TryUpdate<TState>(TKey key, TState state, Func<TState, T, T> updater, out T updated)
    {
        if (updater is null) throw new ArgumentNullException(nameof(updater));

        // Fast path: definitely-missing key costs nothing beyond this lookup. `previous` here is
        // a best-effort peek for the Changed event only (see UpdateContext below) - the actual
        // "does the key still exist" correctness gate is AddOrUpdate's own addValueFactory below,
        // which re-checks on every internal retry, not this one-time peek.
        if (!_items.TryGetValue(key, out var previous))
        {
            updated = null!;
            return false;
        }

        T result;
        try
        {
            result = _items.AddOrUpdate(
                key,
                addValueFactory: static (_, _) => throw KeyMissingSignal.Instance,
                updateValueFactory: static (k, existing, ctx) =>
                {
                    var next = ctx.Updater(ctx.State, existing) ?? throw new InvalidOperationException("updater must not return null.");
                    ctx.Self.EnsureKeyMatches(k, next);
                    return next;
                },
                new UpdateContext<TState>(state, updater, this));
        }
        catch (KeyMissingSignal)
        {
            updated = null!;
            return false;
        }

        updated = result;
        // `previous` is the pre-write peek, not necessarily the exact value this call's AddOrUpdate
        // replaced - under concurrent Upsert/TryUpdate on the SAME key from multiple threads, it
        // can trail by one generation. The store's own state is unaffected (still exactly
        // correct); only the informational Previous field on the Changed event could be stale in
        // that narrow window. Traded deliberately: capturing the exact replaced value here would
        // require a per-call closure (see git history prior to this commit for the closure-based
        // version, and its higher Upsert_ExistingKey allocation in PlayerData.Benchmarks).
        BumpAndNotify(key, PlayerDataChangeKind.Upserted, previous, updated);
        return true;
    }

    // Bundles TryUpdate's per-call state into one value-type argument so both AddOrUpdate
    // factories can be `static` (no closure/delegate allocation per call).
    private readonly struct UpdateContext<TState>
    {
        public UpdateContext(TState state, Func<TState, T, T> updater, KeyedDocumentStore<TKey, T> self)
        {
            State = state;
            Updater = updater;
            Self = self;
        }

        public TState State { get; }
        public Func<TState, T, T> Updater { get; }
        public KeyedDocumentStore<TKey, T> Self { get; }
    }

    public T Update(TKey key, Func<T, T> updater)
    {
        if (!TryUpdate(key, updater, out var updated))
            throw new KeyNotFoundException($"No item with key '{key}' exists in the bag.");
        return updated;
    }

    public T Update<TState>(TKey key, TState state, Func<TState, T, T> updater)
    {
        if (!TryUpdate(key, state, updater, out var updated))
            throw new KeyNotFoundException($"No item with key '{key}' exists in the bag.");
        return updated;
    }

    public T GetOrAdd(TKey key, Func<TKey, T> factory)
    {
        if (factory is null) throw new ArgumentNullException(nameof(factory));
        return GetOrAdd(key, factory, static (k, f) => f(k));
    }

    public T GetOrAdd<TState>(TKey key, TState state, Func<TKey, TState, T> factory)
    {
        if (factory is null) throw new ArgumentNullException(nameof(factory));

        // Peek-then-TryAdd instead of ConcurrentDictionary.GetOrAdd(key, factory): the factory
        // overload can invoke factory concurrently on multiple threads racing for the same
        // missing key, and offers no way to tell which invocation actually won without an extra
        // allocation to carry that flag out. TryAdd's own return value already tells us that for
        // free. Looping on a lost race (rather than trusting our discarded candidate) matches the
        // old CAS-loop's guarantee of never returning a value that isn't actually stored.
        while (true)
        {
            if (_items.TryGetValue(key, out var existing))
                return existing;

            var created = factory(key, state) ?? throw new InvalidOperationException("factory must not return null.");
            EnsureKeyMatches(key, created);

            if (_items.TryAdd(key, created))
            {
                BumpAndNotify(key, PlayerDataChangeKind.Upserted, null, created);
                return created;
            }
        }
    }

    public bool Remove(TKey key)
    {
        if (!_items.TryRemove(key, out var previous)) return false;
        BumpAndNotify(key, PlayerDataChangeKind.Removed, previous, null);
        return true;
    }

    public void Clear()
    {
        if (_items.IsEmpty) return;
        _items.Clear();

        Interlocked.Increment(ref _version);
        var suppressed = _onMutated?.Invoke() ?? false;
        RaiseChanged(new BagChange<TKey, T>(default!, PlayerDataChangeKind.Cleared, null, null, DataChangeCause.UserWrite), suppressed);
    }

    internal void ReplaceFromLoad(ConcurrentDictionary<TKey, T> items)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        Volatile.Write(ref _items, items);

        var v = Volatile.Read(ref _version);
        Volatile.Write(ref _cleanVersion, v);
        lock (_pendingGate)
        {
            if (_pendingChanges is { } dropped)
            {
                dropped.Clear();
                _pendingChangesSpare ??= dropped;
                _pendingChanges = null;
            }
        }
    }

    internal void MarkCleanAt(long version)
    {
        if (Volatile.Read(ref _version) == version)
            Volatile.Write(ref _cleanVersion, version);
    }

    internal void MarkClean()
    {
        Volatile.Write(ref _cleanVersion, Volatile.Read(ref _version));
    }

    internal void FlushPendingNotifications()
    {
        List<BagChange<TKey, T>>? pending;
        lock (_pendingGate)
        {
            pending = _pendingChanges;
            _pendingChanges = null;
        }

        if (pending is null) return;
        foreach (var change in pending)
            Changed?.Invoke(change);

        // Hand the drained list back for the next suppress cycle. Skipped when a handler above
        // threw (this line never runs), which only costs the recycling, not correctness.
        pending.Clear();
        lock (_pendingGate)
            _pendingChangesSpare ??= pending;
    }

    private T SetCore(TKey key, T entity, bool validateKeyMatch)
    {
        if (validateKeyMatch)
            EnsureKeyMatches(key, entity);

        // Existing-key fast path (the common Upsert case in a game loop): TryGetValue + indexer
        // assignment avoids ConcurrentDictionary.AddOrUpdate's dual-factory machinery and the
        // ThreadStatic capture it needed for Previous. Previous is a best-effort pre-write peek
        // for the Changed event only - same deliberate trade-off as TryUpdate (exact replaced
        // value under same-key races would require a per-call closure). ConcurrentDictionary's
        // indexer set replaces without comparing T by structural equality, so the record-typed
        // ABA hazard that bars TryUpdate(key, new, comparison) does not apply here.
        if (_items.TryGetValue(key, out var previous))
        {
            _items[key] = entity;
            BumpAndNotify(key, PlayerDataChangeKind.Upserted, previous, entity);
            return entity;
        }

        if (_items.TryAdd(key, entity))
        {
            BumpAndNotify(key, PlayerDataChangeKind.Upserted, null, entity);
            return entity;
        }

        // Lost the add race: another thread inserted the key. Fall through to update; Previous
        // may still be null if a concurrent Remove won between TryAdd and this peek - the store
        // ends with entity either way, and Changed's informational Previous trails in that window.
        _items.TryGetValue(key, out previous);
        _items[key] = entity;
        BumpAndNotify(key, PlayerDataChangeKind.Upserted, previous, entity);
        return entity;
    }

    private void EnsureKeyMatches(TKey key, T entity)
    {
        var entityKey = _keySelector(entity);
        if (!EqualityComparer<TKey>.Default.Equals(key, entityKey))
            throw new InvalidOperationException(
                $"Entity key '{entityKey}' does not match the provided key '{key}'.");
    }

    private void BumpAndNotify(TKey key, PlayerDataChangeKind kind, T? previous, T? value)
    {
        Interlocked.Increment(ref _version);
        var suppressed = _onMutated?.Invoke() ?? false;
        RaiseChanged(new BagChange<TKey, T>(key, kind, previous, value, DataChangeCause.UserWrite), suppressed);
    }

    private void RaiseChanged(BagChange<TKey, T> change, bool suppressed)
    {
        if (suppressed)
        {
            lock (_pendingGate)
            {
                if (_pendingChanges is null)
                {
                    _pendingChanges = _pendingChangesSpare ?? new List<BagChange<TKey, T>>();
                    _pendingChangesSpare = null;
                }

                // Coalesce consecutive same-key Upserts in place (first Previous, latest Value),
                // matching DocumentStore's pending DocChange policy. The common suppress pattern
                // - bulk-load or repeated writes of the same entity - otherwise appends one entry
                // per mutation and pays O(n) list growth + an O(n) flush of redundant events.
                // Only the trailing entry is considered so multi-key bulk inserts stay O(1) per
                // call; non-adjacent same-key updates still append (rare under real suppress use).
                if (change.Kind == PlayerDataChangeKind.Upserted && _pendingChanges.Count > 0)
                {
                    var lastIndex = _pendingChanges.Count - 1;
                    var last = _pendingChanges[lastIndex];
                    if (last.Kind == PlayerDataChangeKind.Upserted
                        && EqualityComparer<TKey>.Default.Equals(last.Key, change.Key))
                    {
                        _pendingChanges[lastIndex] = new BagChange<TKey, T>(
                            change.Key, PlayerDataChangeKind.Upserted, last.Previous, change.Value, change.Cause);
                        return;
                    }
                }

                _pendingChanges.Add(change);
            }
            return;
        }

        Changed?.Invoke(change);
    }

    // Control-flow-only sentinel thrown from AddOrUpdate's addValueFactory to signal "the key
    // was missing" back out through TryUpdate, distinguishing it from a real add. A cached
    // singleton avoids allocating on this (rare - only hit when a concurrent Remove interleaves
    // with TryUpdate) path.
    private sealed class KeyMissingSignal : Exception
    {
        public static readonly KeyMissingSignal Instance = new();
        private KeyMissingSignal() { }
    }
}
