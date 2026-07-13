using System;
using System.Threading;

namespace PlayerData;

// Lock-free reads (Volatile.Read) with a CAS retry loop on writes.
// Persistence is owned by SaveSession - this type is memory-only.
public sealed class DocumentStore<T> : IDoc<T> where T : class
{
    private readonly Action? _onDirty;
    private readonly Func<bool>? _isNotificationSuppressed;
    private T _current;
    private long _version;
    private long _cleanVersion;
    private DocChange<T>? _pendingChange;

    public DocumentStore(
        Func<T> initialValueFactory,
        Action? onDirty = null,
        Func<bool>? isNotificationSuppressed = null)
    {
        if (initialValueFactory is null) throw new ArgumentNullException(nameof(initialValueFactory));
        _current = initialValueFactory() ?? throw new InvalidOperationException("initialValueFactory must not return null.");
        _onDirty = onDirty;
        _isNotificationSuppressed = isNotificationSuppressed;
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
        _onDirty?.Invoke();
        RaiseChanged(new DocChange<T>(before, after, DataChangeCause.UserWrite));
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

    private void RaiseChanged(DocChange<T> change)
    {
        if (_isNotificationSuppressed?.Invoke() == true)
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
