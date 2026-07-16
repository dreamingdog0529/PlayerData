using System;

namespace PlayerData;

public interface IDoc<T> where T : class
{
    T Value { get; }

    // updater may run more than once under CAS contention - it must be pure (no side effects).
    T Update(Func<T, T> updater);

    // Same as Update(Func<T,T>) but threads state through instead of relying on a capturing
    // closure, so callers with per-call data can update without allocating.
    T Update<TState>(TState state, Func<TState, T, T> updater);

    T Replace(T value);

    // Fires on user writes only by default. Load does not raise this.
    event Action<DocChange<T>>? Changed;
}
