using System;
using System.Collections.Generic;

namespace PlayerData;

public interface IBag<TKey, T>
    where TKey : notnull
    where T : class
{
    int Count { get; }

    // Weakly consistent: enumerating this while another thread writes may observe some but not
    // all of that write's effects (no exception is thrown and no entry is ever torn - each
    // key's value is always read atomically). Does not represent a single frozen point in time
    // the way a snapshot of an immutable collection would.
    IReadOnlyDictionary<TKey, T> Snapshot { get; }

    bool Contains(TKey key);

    bool TryGet(TKey key, out T value);

    T Get(TKey key);

    // key = keySelector(entity). Throws if entity is null.
    T Upsert(T entity);

    // Throws if key does not equal keySelector(entity).
    T Set(TKey key, T entity);

    bool TryUpdate(TKey key, Func<T, T> updater, out T updated);

    // Throws KeyNotFoundException when the key is missing.
    T Update(TKey key, Func<T, T> updater);

    T GetOrAdd(TKey key, Func<TKey, T> factory);

    // State-threading variants of the above - avoid a capturing closure at the call site when
    // the updater/factory needs external data. updater/factory may run more than once under CAS
    // contention - it must be pure (no side effects).
    bool TryUpdate<TState>(TKey key, TState state, Func<TState, T, T> updater, out T updated);

    // Throws KeyNotFoundException when the key is missing.
    T Update<TState>(TKey key, TState state, Func<TState, T, T> updater);

    T GetOrAdd<TState>(TKey key, TState state, Func<TKey, TState, T> factory);

    bool Remove(TKey key);

    void Clear();

    // Fires on user writes only. Load does not raise this.
    event Action<BagChange<TKey, T>>? Changed;
}
