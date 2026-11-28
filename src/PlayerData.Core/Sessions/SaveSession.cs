using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;
using PlayerData.Persistence;
using PlayerData.Migrations;
using PlayerData.Validation;
using PlayerData.Documents;

namespace PlayerData.Sessions;

public sealed class SaveSession : ISaveSession
{
    public const int CurrentFormatVersion = 1;

    private readonly ISaveBackend _backend;
    private readonly IReadOnlyList<ISaveMigration> _migrations;
    private readonly object _gate = new();
    private int _suppressDepth;
    private bool _isLoaded;
    private bool _lastDirtyNotified;

    // List<T> とキャッシュしたスナップショットの組み合わせではなく、追加時にコピーする配列を
    // 使います：AddDocument / AddCollection / AddValidator（まれで、セッションの準備時にだけ
    // 呼ばれます）が _gate の下で配列を作り直し、volatile な書き込みで公開します。読み取りの経路
    // （AnyDirty 経由の IsDirty、LoadAsync、CommitAsync、EndSuppress）はすべて、現在の配列を
    // ロックなしの Volatile.Read で取ります。おかげで IsDirty からロックが完全に外れます。
    // IsDirty は OnMutated / PublishDirty がストアの変更のたびに触る、このライブラリでいちばん
    // 熱い読み取りです。公開された配列がその場で書き換えられることは決してありません（入れ替わる
    // のはフィールドの参照だけです）ので、ロックの外で列挙しても安全です。
    private ISessionParticipant[] _participants = Array.Empty<ISessionParticipant>();
    private ISaveValidator[] _validators = Array.Empty<ISaveValidator>();

    // CommitAsync のスナップショット用配列で、コミットの合間に使い回すためのものです。定常状態の
    // コミットの固定の負担に、毎回の再確保が含まれないようにするためです。Interlocked.Exchange は
    // この配列を同時に進行中のコミットの最大で1つにだけ渡します。競合したコミット（や参加者の数が
    // 変わった場合）は素直に新しい配列を確保しますので、正しさがキャッシュの有無に依存することは
    // ありません。
    private (ISessionParticipant Participant, long Version, byte[] Bytes)[]? _snapshotScratch;

    // CommitAsync の文書ペイロード用ディクショナリで、_snapshotScratch と同じ
    // Interlocked.Exchange の受け渡しでコミットの合間に使い回します。このディクショナリは
    // バックエンドへ渡す SaveBundle の中に入って外へ出ますが、使い回しても安全です：
    // ISaveBackend の所有権の約束ごとによりバンドルは WriteAsync の間だけ有効ですので、次の
    // コミットがこのインスタンスを再利用するころには、約束を守るバックエンドはどれも保持して
    // いません。
    private Dictionary<string, byte[]>? _documentsScratch;

    public SaveSession(ISaveBackend backend, IEnumerable<ISaveMigration>? migrations = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _migrations = NormalizeMigrations(migrations);
    }

    public bool IsDirty => AnyDirty(Volatile.Read(ref _participants));

    public bool IsLoaded => _isLoaded;

    public event Action? Loaded;
    public event Action? Committed;
    public event Action<bool>? DirtyChanged;

    public IDoc<T> AddDocument<T>(string key, Func<T> initialValueFactory) where T : class
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Document key must be non-empty.", nameof(key));
        if (initialValueFactory is null) throw new ArgumentNullException(nameof(initialValueFactory));

        var store = new DocumentStore<T>(initialValueFactory, OnMutated);
        var participant = new DocumentParticipant<T>(key, store);
        lock (_gate)
        {
            EnsureKeyUnique(key);
            AppendParticipant(participant);
        }
        return store;
    }

    public IBag<TKey, T> AddCollection<TKey, T>(string key, Func<T, TKey> keySelector)
        where TKey : notnull
        where T : class
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Document key must be non-empty.", nameof(key));
        if (keySelector is null) throw new ArgumentNullException(nameof(keySelector));

        var store = new KeyedDocumentStore<TKey, T>(keySelector, OnMutated);
        var participant = new CollectionParticipant<TKey, T>(key, store);
        lock (_gate)
        {
            EnsureKeyUnique(key);
            AppendParticipant(participant);
        }
        return store;
    }

    public SuppressionScope SuppressNotifications()
    {
        Interlocked.Increment(ref _suppressDepth);
        return new SuppressionScope(this);
    }

    public void AddValidator(ISaveValidator validator)
    {
        if (validator is null) throw new ArgumentNullException(nameof(validator));
        lock (_gate)
        {
            var current = _validators;
            var next = new ISaveValidator[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[current.Length] = validator;
            Volatile.Write(ref _validators, next);
        }
    }

    public void AddValidator(Action<ISaveSession> validate)
    {
        if (validate is null) throw new ArgumentNullException(nameof(validate));
        AddValidator(new DelegateSaveValidator(validate));
    }

    public async ValueTask<LoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        var bundle = await _backend.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (bundle is null)
        {
            foreach (var p in Volatile.Read(ref _participants))
                p.MarkClean();
            _isLoaded = true;
            Loaded?.Invoke();
            PublishDirty(force: true);
            return LoadResult.NotFound;
        }

        bundle = ApplyMigrations(bundle);

        var participants = Volatile.Read(ref _participants);

        if (participants.Length <= 1)
        {
            foreach (var participant in participants)
            {
                if (bundle.Documents.TryGetValue(participant.Key, out var bytes))
                    participant.LoadBytes(bytes);
                else
                    participant.MarkClean();
            }
        }
        else
        {
            // 各参加者の MemoryPack のデシリアライズは CPU 主体で、その参加者自身のストアにしか
            // 触りません。ですので、文書が複数登録されているときは同時にデシリアライズでき、読み込み
            // 全体の CPU 時間は合計ではなく、いちばん大きい1つの文書分で済みます。TryGetValue は
            // どのタスクが始まるよりも前に呼び出し側のスレッドで走ります。広がるのは LoadBytes
            // 自体だけです。
            var tasks = new List<Task>(participants.Length);
            foreach (var participant in participants)
            {
                if (bundle.Documents.TryGetValue(participant.Key, out var bytes))
                {
                    var (p, b) = (participant, bytes);
                    tasks.Add(Task.Run(() => p.LoadBytes(b)));
                }
                else
                {
                    participant.MarkClean();
                }
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        // ディスク上にあっても登録されていない参加者は無視します（前方互換のためです）。

        _isLoaded = true;
        Loaded?.Invoke();
        PublishDirty(force: true);
        return new LoadResult(Found: true, FormatVersion: bundle.FormatVersion);
    }

    public ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        // 自動セーブの刻みでの何もしない経路です：clean なセッションは、下の非同期ステートマシンに
        // 入ることなく完了済みの ValueTask を返します。ですので、フレームごとに CommitAsync を
        // 確認しても、かかるのは参加者の dirty 走査（数 ns）だけで、ステートマシンの準備
        // （約10 ns）はかかりません。正しさの関門は、CommitCoreAsync の中（検証のあと）にある
        // dirty の再確認が担います。ここの確認は純粋に早期退出のためのもので、その間にストアが
        // clean / dirty に切り替わる競合があっても、結果はそちらの再確認で決まります。
        if (!AnyDirty(Volatile.Read(ref _participants)))
            return default;

        return CommitCoreAsync(cancellationToken);
    }

    private async ValueTask CommitCoreAsync(CancellationToken cancellationToken)
    {
        (ISessionParticipant Participant, long Version, byte[] Bytes)[] snapshot;
        Dictionary<string, byte[]> allDocuments;

        var participants = Volatile.Read(ref _participants);
        if (!AnyDirty(participants))
            return;

        // I/O やシリアライズのスナップショットより前に、fail-fast の検証を行います。
        foreach (var participant in participants)
            participant.Validate();
        foreach (var validator in Volatile.Read(ref _validators))
            validator.Validate(this);

        {
            // `participants` を使い回さずに読み直します：上の検証が走っている間に、
            // AddDocument / AddCollection で新しい参加者が登録されているかもしれないためです。
            var current = Volatile.Read(ref _participants);

            // 検証のあとに dirty を確認し直します（検証は約束ごととして純粋なものです）。
            if (!AnyDirty(current))
                return;

            var scratch = Interlocked.Exchange(ref _snapshotScratch, null);
            snapshot = scratch is not null && scratch.Length == current.Length
                ? scratch
                : new (ISessionParticipant, long, byte[])[current.Length];
            allDocuments = Interlocked.Exchange(ref _documentsScratch, null)
                ?? new Dictionary<string, byte[]>(current.Length);

            var dirtyCount = 0;
            foreach (var p in current)
            {
                if (p.IsDirty) dirtyCount++;
            }

            if (dirtyCount <= 1)
            {
                for (var i = 0; i < current.Length; i++)
                {
                    var p = current[i];
                    var version = p.Version;
                    // dirty な文書はシリアライズし直し、clean なものはキャッシュしたバイト列を使い回します（dirty なものだけをシリアライズします）。
                    var bytes = p.GetBytes();
                    snapshot[i] = (p, version, bytes);
                    allDocuments[p.Key] = bytes;
                }
            }
            else
            {
                // dirty な文書が2つ以上あるときは、それぞれの MemoryPack のシリアライズ（参加者
                // ごとに独立した CPU の仕事です）を広げて同時に行いますので、シリアライズ全体の時間は
                // 合計ではなく、いちばん大きい1つの文書分で済みます。clean な参加者はその場に残します：
                // その GetBytes はキャッシュしたバイト列を返すだけで、タスクにする価値はありません。
                // dirtyCount の関門により、圧倒的に多い「dirty な文書が1つ」のコミットは、タスクの
                // 負担がゼロの逐次経路に保たれます。各タスクの中では GetBytes の前に Version を
                // 捕らえますので、逐次経路の順序の保証が保たれます：シリアライズと競合した変更は
                // バージョンを捕らえた値より先へ進めますので、下の MarkCleanAt はそのストアを次の
                // コミットのために dirty のまま残し、新しいバイト列を clean とはしません。（上の数え
                // 上げとこのループの間で IsDirty が切り替わることもありますが、それで仕事がタスクの
                // 経路とその場の経路の間で移るだけで、どちらも正しいものです。）
                var tasks = new List<Task<(int Index, long Version, byte[] Bytes)>>(dirtyCount);
                for (var i = 0; i < current.Length; i++)
                {
                    var p = current[i];
                    if (p.IsDirty)
                    {
                        var index = i;
                        tasks.Add(Task.Run(() =>
                        {
                            var version = p.Version;
                            return (index, version, p.GetBytes());
                        }));
                    }
                    else
                    {
                        var version = p.Version;
                        var bytes = p.GetBytes();
                        snapshot[i] = (p, version, bytes);
                        allDocuments[p.Key] = bytes;
                    }
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);

                foreach (var task in tasks)
                {
                    var (index, version, bytes) = task.Result;
                    var p = current[index];
                    snapshot[index] = (p, version, bytes);
                    allDocuments[p.Key] = bytes;
                }
            }
        }

        var bundle = new SaveBundle(CurrentFormatVersion, allDocuments);
        await _backend.WriteAsync(bundle, cancellationToken).ConfigureAwait(false);

        // ロックは要りません：MarkCleanAt はストアごとにバージョンで判定します（スナップショットより
        // 先へバージョンを進めた同時の変更があると、そのストアは dirty のまま残ります）し、
        // `snapshot` はローカルなものだからです。
        foreach (var (participant, version, bytes) in snapshot)
            participant.MarkCleanAt(version, bytes);

        // スナップショット用配列を、次のコミットで使い回せるように戻しておきます。先にクリアするのは、
        // あとで登録が変わるセッションの参加者や byte[] への参照を掴んだままにしないためです。
        Array.Clear(snapshot, 0, snapshot.Length);
        Volatile.Write(ref _snapshotScratch, snapshot);

        // 文書のディクショナリも使い回します：上の WriteAsync は完了していて、所有権の約束ごとにより
        // バックエンドがその時点を越えてバンドルを保持することはありません。クリアするのは、参加者
        // 自身がキャッシュしている分を越えて byte[] のペイロードを掴んだままにしないためです。
        allDocuments.Clear();
        Volatile.Write(ref _documentsScratch, allDocuments);

        Committed?.Invoke();
        PublishDirty(force: true);
    }

    public ValueTask DisposeAsync() => default;

    private bool IsNotificationSuppressed() => Volatile.Read(ref _suppressDepth) > 0;

    internal void EndSuppress()
    {
        var depth = Interlocked.Decrement(ref _suppressDepth);
        if (depth < 0)
        {
            Interlocked.Exchange(ref _suppressDepth, 0);
            throw new InvalidOperationException("SuppressNotifications dispose called too many times.");
        }

        if (depth != 0) return;

        foreach (var p in Volatile.Read(ref _participants))
            p.FlushPendingNotifications();

        PublishDirty(force: true);
    }

    // DocumentStore / KeyedDocumentStore へ、その唯一の onMutated コールバックとして渡すものです。
    // 変更のたびに抑制状態をちょうど1回だけ問い合わせて、その答えをストアへ返します（ストアは
    // それを、別に問い合わせ直す代わりに、自分の Changed をまとめるかどうかの判断に使い回します）。
    // あわせて、抑えられていないときは dirty 通知の経路もここで進めます。
    //
    // 変更は常にそのストア自身を dirty にしますので、このメソッドが返った時点でセッションは必ず
    // dirty です。おかげで、すべての参加者を走査する（PublishDirty / IsDirty）ことなく、
    // clean から dirty への境目で DirtyChanged を通知できます。自動セーブの合間に変更が入る
    // 使い方では、セッションがすでに dirty な状態が定常ですので、変更ごとの負担を抑えるための
    // 形です。clean への移行（Load / Commit / EndSuppress）は PublishDirty を
    // force:true で通し、参加者から IsDirty を計算し直します。
    private bool OnMutated()
    {
        var suppressed = IsNotificationSuppressed();
        if (!suppressed && !Volatile.Read(ref _lastDirtyNotified))
        {
            Volatile.Write(ref _lastDirtyNotified, true);
            DirtyChanged?.Invoke(true);
        }
        return suppressed;
    }

    private void PublishDirty(bool force)
    {
        var dirty = IsDirty;
        if (!force && dirty == Volatile.Read(ref _lastDirtyNotified)) return;
        Volatile.Write(ref _lastDirtyNotified, dirty);
        DirtyChanged?.Invoke(dirty);
    }

    private void EnsureKeyUnique(string key)
    {
        foreach (var p in _participants)
        {
            if (string.Equals(p.Key, key, StringComparison.Ordinal))
                throw new InvalidOperationException($"A document with key '{key}' is already registered on this session.");
        }
    }

    // 呼び出し側は _gate を保持している必要があります。
    private void AppendParticipant(ISessionParticipant participant)
    {
        var current = _participants;
        var next = new ISessionParticipant[current.Length + 1];
        Array.Copy(current, next, current.Length);
        next[current.Length] = participant;
        Volatile.Write(ref _participants, next);
    }

    private static bool AnyDirty(ISessionParticipant[] participants)
    {
        foreach (var p in participants)
        {
            if (p.IsDirty) return true;
        }
        return false;
    }

    private SaveBundle ApplyMigrations(SaveBundle bundle)
    {
        var version = bundle.FormatVersion;
        if (version > CurrentFormatVersion)
            throw new InvalidDataException(
                $"Save format version {version} is newer than supported version {CurrentFormatVersion}.");

        while (version < CurrentFormatVersion)
        {
            ISaveMigration? migration = null;
            foreach (var m in _migrations)
            {
                if (m.FromVersion != version) continue;
                migration = m;
                break;
            }

            if (migration is null)
                throw new InvalidDataException(
                    $"No migration registered from save format version {version} to {CurrentFormatVersion}.");

            bundle = migration.Migrate(bundle)
                ?? throw new InvalidOperationException(
                    $"Migration {migration.FromVersion}->{migration.ToVersion} returned null.");

            if (bundle.FormatVersion != migration.ToVersion)
                throw new InvalidOperationException(
                    $"Migration {migration.FromVersion}->{migration.ToVersion} produced format version {bundle.FormatVersion}.");

            version = bundle.FormatVersion;
        }

        return bundle;
    }

    private static IReadOnlyList<ISaveMigration> NormalizeMigrations(IEnumerable<ISaveMigration>? migrations)
    {
        if (migrations is null) return Array.Empty<ISaveMigration>();
        return migrations.OrderBy(m => m.FromVersion).ToList();
    }

    private sealed class DelegateSaveValidator : ISaveValidator
    {
        private readonly Action<ISaveSession> _validate;

        public DelegateSaveValidator(Action<ISaveSession> validate) => _validate = validate;

        public void Validate(ISaveSession session) => _validate(session);
    }

    private interface ISessionParticipant
    {
        string Key { get; }
        bool IsDirty { get; }
        long Version { get; }
        byte[] GetBytes();
        void LoadBytes(byte[] bytes);
        void MarkClean();
        void MarkCleanAt(long version, byte[] committedBytes);
        void Validate();
        void FlushPendingNotifications();
    }

    private sealed class DocumentParticipant<T> : ISessionParticipant where T : class
    {
        private readonly DocumentStore<T> _store;
        private byte[]? _cachedBytes;

        public DocumentParticipant(string key, DocumentStore<T> store)
        {
            Key = key;
            _store = store;
        }

        public string Key { get; }
        public bool IsDirty => _store.IsDirty;
        public long Version => _store.Version;

        public byte[] GetBytes()
        {
            if (!IsDirty && _cachedBytes is not null)
                return _cachedBytes;

            var bytes = MemoryPackSerializer.Serialize(_store.Value);
            _cachedBytes = bytes;
            return bytes;
        }

        public void LoadBytes(byte[] bytes)
        {
            var loaded = MemoryPackSerializer.Deserialize<T>(bytes)
                ?? throw new InvalidDataException($"Save data for key '{Key}' deserialized to null.");
            _store.ReplaceFromLoad(loaded);
            _cachedBytes = bytes;
        }

        public void MarkClean() => _store.MarkClean();

        public void MarkCleanAt(long version, byte[] committedBytes)
        {
            _store.MarkCleanAt(version);
            if (!IsDirty)
                _cachedBytes = committedBytes;
        }

        public void Validate()
        {
            if (_store.Value is IValidatable validatable)
                validatable.Validate();
        }

        public void FlushPendingNotifications() => _store.FlushPendingNotifications();
    }

    private sealed class CollectionParticipant<TKey, T> : ISessionParticipant
        where TKey : notnull
        where T : class
    {
        private readonly KeyedDocumentStore<TKey, T> _store;
        private byte[]? _cachedBytes;

        public CollectionParticipant(string key, KeyedDocumentStore<TKey, T> store)
        {
            Key = key;
            _store = store;
        }

        public string Key { get; }
        public bool IsDirty => _store.IsDirty;
        public long Version => _store.Version;

        public byte[] GetBytes()
        {
            if (!IsDirty && _cachedBytes is not null)
                return _cachedBytes;

            // MemoryPack の ConcurrentDictionaryFormatter は、Dictionary<TKey,TValue> と同じ
            // 「コレクションのヘッダー＋ KeyValuePairs」というワイヤ上の並びで書きます（ImmutableDictionary
            // も同じです）。ですので、まず Dictionary へコピーするのではなく、ストアの生きている土台の
            // ディクショナリを直接シリアライズできます。往復テストでバイト単位に同一であることを
            // 確認してあります。
            var bytes = MemoryPackSerializer.Serialize(_store.SnapshotItems);
            _cachedBytes = bytes;
            return bytes;
        }

        public void LoadBytes(byte[] bytes)
        {
            // ConcurrentDictionary<TKey,T> へ直接デシリアライズすることで、Dictionary を経由する
            // 往復を避けられます。3つのフォーマッターはすべて同じワイヤ上の並びを共有していますので、
            // Dictionary / ImmutableDictionary の形式で書かれたバイト列も同じように読めます
            // （往復テストで確認してあります）。
            var loaded = MemoryPackSerializer.Deserialize<ConcurrentDictionary<TKey, T>>(bytes)
                ?? throw new InvalidDataException($"Save data for key '{Key}' deserialized to null.");
            _store.ReplaceFromLoad(loaded);
            _cachedBytes = bytes;
        }

        public void MarkClean() => _store.MarkClean();

        public void MarkCleanAt(long version, byte[] committedBytes)
        {
            _store.MarkCleanAt(version);
            if (!IsDirty)
                _cachedBytes = committedBytes;
        }

        public void Validate()
        {
            // SnapshotItems.Values ではなく、キーと値の組を直接列挙します。Values のほうは
            // ConcurrentDictionary の上に ReadOnlyCollection<T> のラッパーを確保してしまうためです。
            // 弱い整合性です（KeyedDocumentStore.Snapshot の説明をご覧ください）が、ここでは問題に
            // なりません。各項目自身の内部の検証は、コレクション内のほかの項目に依存しないためです。
            foreach (var kvp in _store.SnapshotItems)
            {
                if (kvp.Value is IValidatable validatable)
                    validatable.Validate();
            }
        }

        public void FlushPendingNotifications() => _store.FlushPendingNotifications();
    }
}
