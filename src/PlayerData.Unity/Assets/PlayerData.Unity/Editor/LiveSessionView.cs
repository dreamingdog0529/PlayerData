using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using MemoryPack;

namespace PlayerData.Unity.Editor
{
    /// <summary>One IDoc&lt;&gt;/IBag&lt;,&gt; property discovered on a live session instance.</summary>
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

        /// <summary>T of IDoc&lt;T&gt;, or the element T of IBag&lt;TKey, T&gt;.</summary>
        public Type EntityType { get; }

        /// <summary>TKey of IBag&lt;TKey, T&gt;; null for single documents.</summary>
        public Type? KeyType { get; }
    }

    /// <summary>
    /// UI-independent adapter over one live ISaveSession instance: enumerates its generated
    /// IDoc&lt;&gt;/IBag&lt;,&gt; properties, reads values as pretty JSON and applies JSON edits
    /// exclusively through the runtime APIs (Replace/Set/Upsert/Remove), so the game observes
    /// edits via its existing Changed subscriptions. Never writes entity members directly.
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

        // ---- Single documents ----

        /// <summary>Current Value of a single document as pretty JSON.</summary>
        public string GetJson(string propertyName) => GetSingle(propertyName).GetJson();

        /// <summary>
        /// Editability gate: the current value must survive bytes → JSON → bytes losslessly,
        /// same philosophy as the on-disk viewer. When false the document is view-only and
        /// <paramref name="failureReason"/> says why.
        /// </summary>
        public bool CanEdit(string propertyName, out string? failureReason) =>
            GetSingle(propertyName).CanEdit(out failureReason);

        /// <summary>
        /// Parses JSON and applies it via IDoc.Replace. Returns false with the error message
        /// (parse error, unknown property name, or view-only reason) without mutating the session.
        /// </summary>
        public bool ApplyJson(string propertyName, string json, out string? error)
        {
            if (json is null) throw new ArgumentNullException(nameof(json));

            SingleHandleBase handle = GetSingle(propertyName);
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
                // Deserialization runs before Replace, so a failure here never mutated the doc.
                error = ex.Message;
                return false;
            }
        }

        // ---- Collections ----

        /// <summary>Keys currently present in the bag's Snapshot (boxed).</summary>
        public IReadOnlyList<object> GetEntryKeys(string propertyName) =>
            GetCollection(propertyName).GetEntryKeys();

        /// <summary>One entry as pretty JSON. Throws KeyNotFoundException when the key is missing.</summary>
        public string GetEntryJson(string propertyName, object key) =>
            GetCollection(propertyName).GetEntryJson(key);

        /// <summary>
        /// Parses JSON and applies it via IBag.Set(key, entity). Returns false with the error
        /// message without mutating the bag — including when the payload's key member differs
        /// from <paramref name="key"/> (the key of an existing entry cannot be changed).
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
                // Set validates the key before touching the dictionary, so nothing was mutated.
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Parses JSON and adds/replaces the entry via IBag.Upsert (key comes from the payload).</summary>
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

        /// <summary>Removes the entry via IBag.Remove. False when the key was not present.</summary>
        public bool RemoveEntry(string propertyName, object key) =>
            GetCollection(propertyName).RemoveEntry(key);

        // ---- Change flag ----

        /// <summary>
        /// True when any subscribed document/bag/session event fired since the last call.
        /// Intended for throttled UI refresh: poll this instead of repainting per event.
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

        // The generic handle classes give us statically typed access to Value/Replace/Set/Upsert/
        // Remove and to the Changed event (whose delegate type is generic); only this one
        // construction step needs reflection.
        private static Handle CreateHandle(Type handleType, ISaveSession session, PropertyInfo property, Action notify)
        {
            object store = property.GetValue(session);
            if (store is null)
                throw new InvalidOperationException($"Live document property '{property.Name}' returned null.");
            return (Handle)Activator.CreateInstance(handleType, store, notify);
        }

        private SingleHandleBase GetSingle(string propertyName)
        {
            if (GetHandle(propertyName) is SingleHandleBase single)
                return single;
            throw new InvalidOperationException($"Document '{propertyName}' is a collection; use the entry APIs.");
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
            public abstract void Dispose();
        }

        private abstract class SingleHandleBase : Handle
        {
            public abstract string GetJson();
            public abstract bool CanEdit(out string? failureReason);
            public abstract void ApplyJson(string json);
        }

        private abstract class CollectionHandleBase : Handle
        {
            public abstract IReadOnlyList<object> GetEntryKeys();
            public abstract string GetEntryJson(object key);
            public abstract void ApplyEntryJson(object key, string json);
            public abstract void AddEntryJson(string json);
            public abstract bool RemoveEntry(object key);
        }

        private sealed class SingleHandle<T> : SingleHandleBase where T : class
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

            private void OnChanged(BagChange<TKey, T> change) => _notify();
        }
    }
}
