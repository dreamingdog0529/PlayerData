using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using MemoryPack;
using PlayerData.Documents;
using PlayerData.Sessions;
using PlayerData.Schema;

namespace PlayerData.Unity.Editor
{
    /// <summary>ライブセッションのインスタンス上で見つけた IDoc&lt;&gt;/IBag&lt;,&gt; プロパティ 1 件です。</summary>
    public sealed class LiveDocumentDescriptor
    {
        public LiveDocumentDescriptor(string propertyName, bool isCollection, Type entityType, Type? keyType)
        {
            PropertyName = propertyName;
            IsCollection = isCollection;
            EntityType = entityType;
            KeyType = keyType;
        }

        public string PropertyName { get; }

        public bool IsCollection { get; }

        /// <summary>IDoc&lt;T&gt; の T、または IBag&lt;TKey, T&gt; の要素の T です。</summary>
        public Type EntityType { get; }

        /// <summary>IBag&lt;TKey, T&gt; の TKey です。単一ドキュメントでは null になります。</summary>
        public Type? KeyType { get; }
    }

    /// <summary>
    /// ライブの ISaveSession インスタンス 1 件に対する、UI に依存しないアダプターです。生成された
    /// IDoc&lt;&gt;/IBag&lt;,&gt; プロパティを列挙し、値を整えた JSON として読み、JSON の編集は
    /// ランタイム API（Replace/Set/Upsert/Remove）を通してだけ適用します。なので、ゲーム側では
    /// 既存の Changed の購読を通して編集に気づけます。entity のメンバーを直接書くことは決してしません。
    /// </summary>
    public sealed class LiveSessionView : IDisposable
    {
        private readonly ISaveSession _session;
        private readonly List<LiveDocumentDescriptor> _documents = new List<LiveDocumentDescriptor>();
        private readonly Dictionary<string, Handle> _handles = new Dictionary<string, Handle>(StringComparer.Ordinal);
        private readonly Action _sessionLoadedHandler;
        private readonly Action _sessionCommittedHandler;
        private readonly Action<bool> _sessionDirtyChangedHandler;
        private int _changeFlag;
        private bool _disposed;

        public LiveSessionView(ISaveSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));

            Action notify = SetChangeFlag;
            foreach (PropertyInfo property in session.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Type propertyType = property.PropertyType;
                if (!propertyType.IsGenericType)
                    continue;

                Type definition = propertyType.GetGenericTypeDefinition();
                Type[] arguments = propertyType.GetGenericArguments();
                Handle handle;
                LiveDocumentDescriptor descriptor;
                if (definition == typeof(IDoc<>))
                {
                    descriptor = new LiveDocumentDescriptor(property.Name, isCollection: false, arguments[0], keyType: null);
                    handle = CreateHandle(typeof(SingleHandle<>).MakeGenericType(arguments), session, property, notify);
                }
                else if (definition == typeof(IBag<,>))
                {
                    descriptor = new LiveDocumentDescriptor(property.Name, isCollection: true, arguments[1], arguments[0]);
                    handle = CreateHandle(typeof(CollectionHandle<,>).MakeGenericType(arguments), session, property, notify);
                }
                else
                {
                    continue;
                }

                _documents.Add(descriptor);
                _handles.Add(property.Name, handle);
            }

            _sessionLoadedHandler = SetChangeFlag;
            _sessionCommittedHandler = SetChangeFlag;
            _sessionDirtyChangedHandler = OnSessionDirtyChanged;
            _session.Loaded += _sessionLoadedHandler;
            _session.Committed += _sessionCommittedHandler;
            _session.DirtyChanged += _sessionDirtyChangedHandler;
        }

        public IReadOnlyList<LiveDocumentDescriptor> Documents => _documents;

        // ---- ドキュメント全体（単一とコレクション） ----

        /// <summary>
        /// 現在の値を整えた JSON で返します。単一ドキュメントではその Value、コレクションでは
        /// bag 全体を 1 つの JSON オブジェクト（キー → entity）として返し、ディスク上のコレクションの形に合わせます。
        /// </summary>
        public string GetJson(string propertyName) => GetHandle(propertyName).GetJson();

        /// <summary>
        /// 編集できるかどうかのガードです。現在の値が、バイト列 → JSON → バイト列と変換しても無損失で残らないといけません。
        /// ディスク上のビューワーと同じ考え方です。false のとき、そのドキュメントは表示だけになり、
        /// <paramref name="failureReason"/> にその理由が入ります。
        /// </summary>
        public bool CanEdit(string propertyName, out string? failureReason) =>
            GetHandle(propertyName).CanEdit(out failureReason);

        /// <summary>
        /// JSON をパースして、IDoc.Replace（単一の場合）または IBag の Set/Remove の差分（コレクションの場合）で適用します。
        /// 失敗したときはエラーメッセージとともに false を返します（パースエラー、未知のプロパティ名、
        /// 表示だけである理由などです）。パースやガードで失敗したときは、セッションを決して変更しません。
        /// </summary>
        public bool ApplyJson(string propertyName, string json, out string? error)
        {
            if (json is null) throw new ArgumentNullException(nameof(json));

            Handle handle = GetHandle(propertyName);
            if (!handle.CanEdit(out string? reason))
            {
                error = reason;
                return false;
            }

            try
            {
                handle.ApplyJson(json);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                // デシリアライズは Replace の前に動くので、単一ドキュメントでここで失敗しても、そのドキュメントが
                // 変更されることはありません。コレクションの差分は、キーが合わないところで途中で止まることがあります。
                // 呼び出し側では、スナップショットを読み直して実際の状態を表示します。
                error = ex.Message;
                return false;
            }
        }

        // ---- コレクション ----

        /// <summary>bag の Snapshot に現在あるキーです（ボックス化してあります）。</summary>
        public IReadOnlyList<object> GetEntryKeys(string propertyName) =>
            GetCollection(propertyName).GetEntryKeys();

        /// <summary>エントリ 1 件を整えた JSON で返します。キーがないときは KeyNotFoundException を投げます。</summary>
        public string GetEntryJson(string propertyName, object key) =>
            GetCollection(propertyName).GetEntryJson(key);

        /// <summary>
        /// JSON をパースして、IBag.Set(key, entity) で適用します。失敗したときはエラーメッセージとともに false を返し、bag は変更しません。
        /// ペイロードのキーメンバーが <paramref name="key"/> と違うとき（既存エントリのキーは変えられません）も含みます。
        /// 
        /// </summary>
        public bool ApplyEntryJson(string propertyName, object key, string json, out string? error)
        {
            if (json is null) throw new ArgumentNullException(nameof(json));

            CollectionHandleBase handle = GetCollection(propertyName);
            try
            {
                handle.ApplyEntryJson(key, json);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                // Set は辞書に触る前にキーを検証するので、何も変更されていません。
                error = ex.Message;
                return false;
            }
        }

        /// <summary>JSON をパースして、IBag.Upsert でエントリを追加または置き換えます（キーはペイロードから来ます）。</summary>
        public bool AddEntryJson(string propertyName, string json, out string? error)
        {
            if (json is null) throw new ArgumentNullException(nameof(json));

            CollectionHandleBase handle = GetCollection(propertyName);
            try
            {
                handle.AddEntryJson(json);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>IBag.Remove でエントリを削除します。キーがなかったときは false です。</summary>
        public bool RemoveEntry(string propertyName, object key) =>
            GetCollection(propertyName).RemoveEntry(key);

        // ---- 変更フラグ ----

        /// <summary>
        /// 前回の呼び出し以降、購読しているドキュメント、bag、セッションのどれかのイベントが発火したら true です。
        /// UI の更新を間引くためのものです。イベントごとに再描画するのではなく、これをポーリングしてください。
        /// </summary>
        public bool ConsumeChangeFlag() => Interlocked.Exchange(ref _changeFlag, 0) != 0;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _session.Loaded -= _sessionLoadedHandler;
            _session.Committed -= _sessionCommittedHandler;
            _session.DirtyChanged -= _sessionDirtyChangedHandler;
            foreach (KeyValuePair<string, Handle> pair in _handles)
                pair.Value.Dispose();
        }

        private void SetChangeFlag() => Interlocked.Exchange(ref _changeFlag, 1);

        private void OnSessionDirtyChanged(bool dirty) => SetChangeFlag();

        // ジェネリックなハンドルクラスがあるおかげで、Value/Replace/Set/Upsert/
        // Remove と Changed イベント（デリゲート型がジェネリックです）に、静的に型付けされた形でアクセスできます。リフレクションが
        // 必要なのは、この構築のステップだけです。
        private static Handle CreateHandle(Type handleType, ISaveSession session, PropertyInfo property, Action notify)
        {
            object store = property.GetValue(session);
            if (store is null)
                throw new InvalidOperationException($"Live document property '{property.Name}' returned null.");
            return (Handle)Activator.CreateInstance(handleType, store, notify);
        }

        private CollectionHandleBase GetCollection(string propertyName)
        {
            if (GetHandle(propertyName) is CollectionHandleBase collection)
                return collection;
            throw new InvalidOperationException($"Document '{propertyName}' is a single document, not a collection.");
        }

        private Handle GetHandle(string propertyName)
        {
            if (propertyName is null) throw new ArgumentNullException(nameof(propertyName));
            if (_handles.TryGetValue(propertyName, out Handle handle))
                return handle;
            throw new KeyNotFoundException($"No live document property named '{propertyName}' exists on this session.");
        }

        private abstract class Handle : IDisposable
        {
            public abstract string GetJson();
            public abstract bool CanEdit(out string? failureReason);
            public abstract void ApplyJson(string json);
            public abstract void Dispose();
        }

        private abstract class CollectionHandleBase : Handle
        {
            public abstract IReadOnlyList<object> GetEntryKeys();
            public abstract string GetEntryJson(object key);
            public abstract void ApplyEntryJson(object key, string json);
            public abstract void AddEntryJson(string json);
            public abstract bool RemoveEntry(object key);
        }

        private sealed class SingleHandle<T> : Handle where T : class
        {
            private readonly IDoc<T> _doc;
            private readonly Action _notify;

            public SingleHandle(IDoc<T> doc, Action notify)
            {
                _doc = doc;
                _notify = notify;
                _doc.Changed += OnChanged;
            }

            public override string GetJson() => MemoryPackJsonConverter.ToJson(_doc.Value, typeof(T));

            public override bool CanEdit(out string? failureReason)
            {
                byte[] bytes;
                try
                {
                    bytes = MemoryPackSerializer.Serialize(typeof(T), _doc.Value);
                }
                catch (Exception ex)
                {
                    failureReason = ex.Message;
                    return false;
                }

                return MemoryPackJsonConverter.CanRoundTrip(bytes, typeof(T), out failureReason);
            }

            public override void ApplyJson(string json)
            {
                T value = (T)MemoryPackJsonConverter.ObjectFromJson(json, typeof(T));
                _doc.Replace(value);
            }

            public override void Dispose() => _doc.Changed -= OnChanged;

            private void OnChanged(DocChange<T> change) => _notify();
        }

        private sealed class CollectionHandle<TKey, T> : CollectionHandleBase
            where TKey : notnull
            where T : class
        {
            private readonly IBag<TKey, T> _bag;
            private readonly Action _notify;

            public CollectionHandle(IBag<TKey, T> bag, Action notify)
            {
                _bag = bag;
                _notify = notify;
                _bag.Changed += OnChanged;
            }

            public override string GetJson() =>
                MemoryPackJsonConverter.ToJson(CopySnapshot(), typeof(Dictionary<TKey, T>));

            public override bool CanEdit(out string? failureReason)
            {
                byte[] bytes;
                try
                {
                    bytes = MemoryPackSerializer.Serialize(typeof(Dictionary<TKey, T>), CopySnapshot());
                }
                catch (Exception ex)
                {
                    failureReason = ex.Message;
                    return false;
                }

                return MemoryPackJsonConverter.CanRoundTrip(bytes, typeof(Dictionary<TKey, T>), out failureReason);
            }

            public override void ApplyJson(string json)
            {
                // まず全体をパースして、JSON エラーでは何も変更されないようにします。Set（Upsert ではなく）を使うので、JSON の
                // キーがペイロードの [PlayerDataKey] メンバーと食い違うときは、違うキーの下に静かに入ってしまうのではなく、
                // はっきり失敗します。
                Dictionary<TKey, T>? desired =
                    (Dictionary<TKey, T>?)MemoryPackJsonConverter.ObjectFromJson(json, typeof(Dictionary<TKey, T>));
                if (desired is null)
                    throw new InvalidOperationException("The JSON must be an object with one entry per key.");

                foreach (KeyValuePair<TKey, T> pair in desired)
                    _bag.Set(pair.Key, pair.Value);

                List<TKey> removed = new List<TKey>();
                foreach (KeyValuePair<TKey, T> pair in _bag.Snapshot)
                {
                    if (!desired.ContainsKey(pair.Key))
                        removed.Add(pair.Key);
                }

                foreach (TKey key in removed)
                    _bag.Remove(key);
            }

            public override IReadOnlyList<object> GetEntryKeys()
            {
                List<object> keys = new List<object>(_bag.Count);
                foreach (KeyValuePair<TKey, T> pair in _bag.Snapshot)
                    keys.Add(pair.Key);
                return keys;
            }

            public override string GetEntryJson(object key) =>
                MemoryPackJsonConverter.ToJson(_bag.Get((TKey)key), typeof(T));

            public override void ApplyEntryJson(object key, string json)
            {
                T entity = (T)MemoryPackJsonConverter.ObjectFromJson(json, typeof(T));
                _bag.Set((TKey)key, entity);
            }

            public override void AddEntryJson(string json)
            {
                T entity = (T)MemoryPackJsonConverter.ObjectFromJson(json, typeof(T));
                _bag.Upsert(entity);
            }

            public override bool RemoveEntry(object key) => _bag.Remove((TKey)key);

            public override void Dispose() => _bag.Changed -= OnChanged;

            private Dictionary<TKey, T> CopySnapshot()
            {
                Dictionary<TKey, T> copy = new Dictionary<TKey, T>(_bag.Count);
                foreach (KeyValuePair<TKey, T> pair in _bag.Snapshot)
                    copy[pair.Key] = pair.Value;
                return copy;
            }

            private void OnChanged(BagChange<TKey, T> change) => _notify();
        }
    }
}
