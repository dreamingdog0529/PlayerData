using System;
using System.Threading;

namespace PlayerData;

// Lock-free reads (Volatile.Read) with a CAS retry loop on writes.
// Persistence is owned by SaveSession - this type is memory-only.
public sealed class DocumentStore<T> : IDoc<T> where T : class
{
    // Called exactly once per mutation (not once for the dirty-notification path and again for
    // the Changed-coalescing check, as the two separate delegates this replaced did). The
    // callback both forwards the mutation to the owning SaveSession and returns whether
    // notifications are currently suppressed, so the store can reuse that single answer for its
    // own RaiseChanged decision below instead of querying suppression state a second time.
    private readonly Func<bool>? _onMutated;
    private T _current;
    private long _version;
    private long _cleanVersion;
    private DocChange<T>? _pendingChange;

    public DocumentStore(
        Func<T> initialValueFactory,
        Func<bool>? onMutated = null)
    {
        if (initialValueFactory is null) throw new ArgumentNullException(nameof(initialValueFactory));
        _current = initialValueFactory() ?? throw new InvalidOperationException("initialValueFactory must not return null.");
        _onMutated = onMutated;
        _version = 0;
        _cleanVersion = 0;
    }

    public T Value => Volatile.Read(ref _current)!;

    public event Action<DocChange<T>>? Changed;

    internal bool IsDirty => Volatile.Read(ref _version) != Volatile.Read(ref _cleanVersion);

    internal long Version => Volatile.Read(ref _version);

    public T Update(Func<T, T> updater)
    {
        if (updater is null) throw new ArgumentNullException(nameof(updater));
        return Update(updater, static (u, before) => u(before));
    }

    public T Update<TState>(TState state, Func<TState, T, T> updater)
    {
        if (updater is null) throw new ArgumentNullException(nameof(updater));

        T before, after;
        do
        {
            before = Volatile.Read(ref _current)!;
            after = updater(state, before) ?? throw new InvalidOperationException("updater must not return null.");
        } while (Interlocked.CompareExchange(ref _current, after, before) != before);

        Interlocked.Increment(ref _version);
        var suppressed = _onMutated?.Invoke() ?? false;
        RaiseChanged(new DocChange<T>(before, after, DataChangeCause.UserWrite), suppressed);
        return after;
    }

    public T Replace(T value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        return Update(value, static (v, _) => v);
    }

    // Used by SaveSession.LoadAsync - does not raise Changed and marks the store clean.
    internal void ReplaceFromLoad(T value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        Volatile.Write(ref _current, value);
        var v = Volatile.Read(ref _version);
        Volatile.Write(ref _cleanVersion, v);
        _pendingChange = null;
    }

    internal void MarkCleanAt(long version)
    {
        // Only clear dirty if no newer user update landed after the commit snapshot was taken.
        if (Volatile.Read(ref _version) == version)
            Volatile.Write(ref _cleanVersion, version);
    }

    internal void MarkClean()
    {
        Volatile.Write(ref _cleanVersion, Volatile.Read(ref _version));
    }

    internal void FlushPendingNotifications()
    {
        var pending = _pendingChange;
        if (pending is null) return;
        _pendingChange = null;
        Changed?.Invoke(pending.Value);
    }

    private void RaiseChanged(DocChange<T> change, bool suppressed)
    {
        if (suppressed)
        {
            // Coalesce: keep the first Previous, latest Current.
            if (_pendingChange is { } existing)
                _pendingChange = new DocChange<T>(existing.Previous, change.Current, change.Cause);
            else
                _pendingChange = change;
            return;
        }

        Changed?.Invoke(change);
    }
}
