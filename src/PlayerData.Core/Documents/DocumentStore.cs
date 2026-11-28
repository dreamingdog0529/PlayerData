using System;
using System.Threading;
using PlayerData.Sessions;

namespace PlayerData.Documents;

// 読み取りはロックなし（Volatile.Read）で、書き込みは CAS のリトライループで回しています。
// 永続化は SaveSession が担当しますので、この型はメモリ上だけのものです。
public sealed class DocumentStore<T> : IDoc<T> where T : class
{
    // 変更のたびにちょうど1回だけ呼ばれるコールバックです。変更を所有者の SaveSession へ伝える
    // 役割と、通知が今抑えられているかどうかを返す役割を1つにまとめてあります。おかげでストアは、
    // 抑制状態をもう一度問い合わせずに、その答えを下の RaiseChanged の判断に使い回せます。
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

    internal bool IsDirty
    {
        get
        {
            var version = Volatile.Read(ref _version);
            return version != Volatile.Read(ref _cleanVersion);
        }
    }

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

        // 無条件の入れ替えです：Replace には、読み取り・変更・書き込みを行う Update が使うような
        // CAS のリトライループは要りません。Interlocked.Exchange が新しい参照を公開しつつ、
        // Changed イベント用の前の参照を、1回の原子的なステップで返してくれます。
        var before = Interlocked.Exchange(ref _current, value)!;
        Interlocked.Increment(ref _version);
        var suppressed = _onMutated?.Invoke() ?? false;
        RaiseChanged(new DocChange<T>(before, value, DataChangeCause.UserWrite), suppressed);
        return value;
    }

    // SaveSession.LoadAsync が使うものです。Changed は発火させず、ストアを clean な状態にします。
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
        // コミットのスナップショットを取ったあとに新しいユーザー更新が来ていたら、dirty は消さないようにします。
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
            // まとめます：Previous は最初のものを残し、Current は最新のものにします。
            if (_pendingChange is { } existing)
                _pendingChange = new DocChange<T>(existing.Previous, change.Current, change.Cause);
            else
                _pendingChange = change;
            return;
        }

        Changed?.Invoke(change);
    }
}
