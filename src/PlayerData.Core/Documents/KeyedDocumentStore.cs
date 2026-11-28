using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using PlayerData.Sessions;

namespace PlayerData.Documents;

// 土台は ConcurrentDictionary<TKey,T> です：読み取り（TryGet / Contains / Snapshot の列挙）は
// ロックなしで、書き込みはツリー全体の CAS をリトライする代わりにバケットごとの短いロックを取ります。
// 条件付きの操作や読み取り・変更・書き込みの操作はすべて AddOrUpdate / GetOrAdd を通しますので、
// 更新デリゲートは常に本当に現在の値を目にできます。ConcurrentDictionary.TryUpdate(key,
// newValue, comparisonValue) は使わないことにしています：その比較は EqualityComparer<T>.Default で、
// record 型の T では構造的な等価性になります。そのため、古い読み取り値とたまたま等しくなる別の
// インスタンスがあると、TryUpdate はその古い値を土台に成功を報告してしまい、「等しい」インスタンスを
// 作った側の変更を黙って捨ててしまうおそれがあります（実際に起こりうる ABA の危険で、record 型の
// T では実測でも再現しています）。そうした事故を防ぐための選択です。
public sealed class KeyedDocumentStore<TKey, T> : IBag<TKey, T>
    where TKey : notnull
    where T : class
{
    private readonly Func<T, TKey> _keySelector;
    // 変更のたびにちょうど1回だけ呼ばれるコールバックです。変更を所有者の SaveSession へ伝える
    // 役割と、通知が今抑えられているかどうかを返す役割を1つにまとめてあります。おかげでストアは、
    // 抑制状態をもう一度問い合わせずに、その答えを下の RaiseChanged の判断に使い回せます。
    private readonly Func<bool>? _onMutated;
    private readonly object _pendingGate = new();
    // readonly にしていないのには理由があります：ReplaceFromLoad は、クリアして1件ずつ入れ直す
    // のではなく、デシリアライズしたばかりのディクショナリをまるごと入れ替えるためです（volatile な
    // 参照の書き込み1回です）。参照の読み取りは原子的ですので、同時に読んでいる側には古い
    // ディクショナリか新しいディクショナリのどちらかが丸ごと見え、空の状態や半分だけ入った状態の
    // ような途中経過が見えてしまうことはありません。
    private ConcurrentDictionary<TKey, T> _items = new();
    private long _version;
    private long _cleanVersion;
    private List<BagChange<TKey, T>>? _pendingChanges;
    // 前の抑制サイクルで使い終えた（クリア済みの）リストで、RaiseChanged が使い回すためのものです。
    // 容量が温まってくれば、定常状態の抑制／フラッシュのサイクルで新しいリストを作らずに済みます。
    // _pendingChanges 自体と同じく _pendingGate で守られています。
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

    // 弱い整合性です：これは生きている ConcurrentDictionary のインスタンスそのものを返します
    // （ある一時点を切り取ったコピーではありません）。別のスレッドが書き込んでいる最中に列挙すると、
    // その書き込みの影響の一部だけが見えることがありますが、個々のキーの読み取りは常に原子的です。
    // これが SaveSession のシリアライズ／検証の経路で安全な理由は、下の SnapshotItems をご覧ください。
    public IReadOnlyDictionary<TKey, T> Snapshot => _items;

    // アセンブリ内の利用者（SaveSession のシリアライズ）向けの、具象型のアクセサーです。
    // ConcurrentDictionary 専用の列挙子が必要なときに使うためのもので、ConcurrentDictionary.Values が
    // 引き起こしてしまう ReadOnlyCollection のアロケーションを避けられます。
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

        // 高速な経路です：キーが確実にない場合は、この検索以上のコストはかかりません。ここでの
        // `previous` は Changed イベント用にできるだけ覗いたものにすぎません（下の UpdateContext を
        // ご覧ください）。「キーがまだ存在するか」という正確さの関門は、この一度きりの覗き見ではなく、
        // 下にある AddOrUpdate 自身の addValueFactory が担っていて、内部のリトライのたびに
        // 確認し直してくれます。
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
        // `previous` は書き込み前に覗いた値で、この呼び出しの AddOrUpdate が実際に置き換えた値と
        // 必ずしも一致しません。同じキーに対して複数のスレッドから Upsert / TryUpdate が同時に走ると、
        // 1世代遅れることがあります。ストア自体の状態には影響しません（そちらは正確なままです）。
        // その狭い範囲で古くなりうるのは、Changed イベントの参考情報である Previous フィールドだけです。
        // これは意図的な取捨選択です：ここで正確に置き換えた値を捕らえようとすると呼び出しごとの
        // クロージャが必要になり、アロケーションも増えてしまいます。
        BumpAndNotify(key, PlayerDataChangeKind.Upserted, previous, updated);
        return true;
    }

    // TryUpdate の呼び出しごとの状態を1つの値型の引数にまとめたものです。おかげで AddOrUpdate の
    // 両方の factory を `static` にでき、呼び出しごとのクロージャやデリゲートのアロケーションが要りません。
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

        // ConcurrentDictionary.GetOrAdd(key, factory) ではなく、覗いてから TryAdd する形にしています：
        // factory のオーバーロードは、同じ不足キーを奪い合う複数のスレッドで factory を同時に呼ぶことが
        // あり、どの呼び出しが実際に勝ったかを知るには、その印を持ち出すための余分なアロケーションが
        // 必要になってしまいます。TryAdd 自身の戻り値なら、それをただで教えてくれます。競合に負けたら
        // （捨てた候補を信じずに）ループする形にすることで、実際に保存されていない値は決して返さない
        // という保証を保っています。
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
        // ロックなしの早期退出です：EndSuppress は抑制サイクルの終わりごとにすべての参加者に対して
        // これを呼びますので、サイクル中に変更がなかったストアが、流すものがないと知るためだけに
        // Monitor の入退出の組を払わずに済むようにするためのものです。この読み取りと競合した変更
        // （null を観測した直後に追加されたもの）は次のサイクルのものとして扱います。排出のあとに
        // 届いた追加は次のサイクルで扱う、という範囲の決め方です。
        if (Volatile.Read(ref _pendingChanges) is null) return;

        List<BagChange<TKey, T>>? pending;
        lock (_pendingGate)
        {
            pending = _pendingChanges;
            _pendingChanges = null;
        }

        if (pending is null) return;
        foreach (var change in pending)
            Changed?.Invoke(change);

        // 排出したリストを、次の抑制サイクルで使い回せるように返しておきます。上のハンドラーが例外を
        // 投げたときはこの行は実行されませんが、失われるのは再利用の機会だけで、正確さには影響しません。
        pending.Clear();
        lock (_pendingGate)
            _pendingChangesSpare ??= pending;
    }

    // スレッドごとのスロットで、AddOrUpdate の updateValueFactory が既存の値を下の SetCore へ
    // 渡すために使います。AddOrUpdate の前に別の TryGetValue を走らせずに済みますので、呼び出し
    // ごとのディクショナリ検索は2回ではなく1回で済みます。通常の static フィールドではなく
    // [ThreadStatic] であることが要です：異なるキーで SetCore を競合している2つのスレッドが、
    // お互いの捕らえた値を目にすることは決してあってはいけません。別のスレッドの書き込みが CAS の
    // 間に割り込むと、AddOrUpdate はこの同じスレッドで updateValueFactory を複数回呼ぶことが
    // ありますが、SetCore がスロットを読み戻す前に走るのは成功した CAS の直前の最後の呼び出しだけ
    // ですので、常に実際に置き換えた値を反映します。さらに SetCore は、BumpAndNotify が同じ
    // スレッドでの再入呼び出しを引き起こす前に、値をローカルへ吸い出してスロットをクリアします。
    [ThreadStatic]
    private static T? _capturedPrevious;

    private T SetCore(TKey key, T entity, bool validateKeyMatch)
    {
        if (validateKeyMatch)
            EnsureKeyMatches(key, entity);

        _capturedPrevious = null;
        _items.AddOrUpdate(
            key,
            addValueFactory: static (_, e) => e,
            updateValueFactory: static (_, existing, e) =>
            {
                _capturedPrevious = existing;
                return e;
            },
            entity);

        var previous = _capturedPrevious;
        _capturedPrevious = null;

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

                // 連続する同じキーの Upsert はその場でまとめます（Previous は最初のもの、Value は
                // 最新のもの）。DocumentStore の保留中の DocChange の方針に合わせたものです。よくある
                // 抑制のパターン（一括読み込みや、同じエンティティへの繰り返しの書き込み）で変更のたびに
                // エントリを増やしていくと、O(n) のリストの成長と、冗長なイベントの O(n) のフラッシュを
                // 払うことになってしまいます。末尾のエントリだけを見るようにしてありますので、複数キーの
                // 一括挿入でも呼び出しごとに O(1) のままです。隣り合わない同じキーの更新はそのまま
                // 追加されます（実際の抑制の使い方ではまれです）。
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

    // 制御フロー専用の番兵で、AddOrUpdate の addValueFactory から投げて、「キーがなかった」ことを
    // TryUpdate へ伝え、本物の追加と区別するためのものです。キャッシュしたシングルトンにしてあります
    // ので、この経路（まれで、同時の Remove が TryUpdate の間に割り込んだときにだけ通ります）で
    // アロケーションせずに済みます。
    private sealed class KeyMissingSignal : Exception
    {
        public static readonly KeyMissingSignal Instance = new();
        private KeyMissingSignal() { }
    }
}
