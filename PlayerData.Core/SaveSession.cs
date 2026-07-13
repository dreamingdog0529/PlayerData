using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;

namespace PlayerData;

public sealed class SaveSession : ISaveSession
{
    public const int CurrentFormatVersion = 1;

    private readonly ISaveBackend _backend;
    private readonly IReadOnlyList<ISaveMigration> _migrations;
    private readonly List<ISessionParticipant> _participants = new();
    private readonly List<ISaveValidator> _validators = new();
    private readonly object _gate = new();
    private int _suppressDepth;
    private bool _isLoaded;
    private bool _lastDirtyNotified;

    public SaveSession(ISaveBackend backend, IEnumerable<ISaveMigration>? migrations = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _migrations = NormalizeMigrations(migrations);
    }

    public bool IsDirty
    {
        get
        {
            lock (_gate)
                return AnyDirty(_participants);
        }
    }

    public bool IsLoaded => _isLoaded;

    public event Action? Loaded;
    public event Action? Committed;
    public event Action<bool>? DirtyChanged;

    public IDoc<T> AddDocument<T>(string key, Func<T> initialValueFactory) where T : class
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Document key must be non-empty.", nameof(key));
        if (initialValueFactory is null) throw new ArgumentNullException(nameof(initialValueFactory));

        var store = new DocumentStore<T>(initialValueFactory, NotifyDirty, IsNotificationSuppressed);
        var participant = new DocumentParticipant<T>(key, store);
        lock (_gate)
        {
            EnsureKeyUnique(key);
            _participants.Add(participant);
        }
        return store;
    }

    public IBag<TKey, T> AddCollection<TKey, T>(string key, Func<T, TKey> keySelector)
        where TKey : notnull
        where T : class
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Document key must be non-empty.", nameof(key));
        if (keySelector is null) throw new ArgumentNullException(nameof(keySelector));

        var store = new KeyedDocumentStore<TKey, T>(keySelector, NotifyDirty, IsNotificationSuppressed);
        var participant = new CollectionParticipant<TKey, T>(key, store);
        lock (_gate)
        {
            EnsureKeyUnique(key);
            _participants.Add(participant);
        }
        return store;
    }

    public IDisposable SuppressNotifications()
    {
        Interlocked.Increment(ref _suppressDepth);
        return new SuppressScope(this);
    }

    public void AddValidator(ISaveValidator validator)
    {
        if (validator is null) throw new ArgumentNullException(nameof(validator));
        lock (_gate)
            _validators.Add(validator);
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
            lock (_gate)
            {
                foreach (var p in _participants)
                    p.MarkClean();
            }
            _isLoaded = true;
            Loaded?.Invoke();
            PublishDirty(force: true);
            return LoadResult.NotFound;
        }

        bundle = ApplyMigrations(bundle);

        List<ISessionParticipant> participants;
        lock (_gate)
        {
            participants = _participants.ToList();
        }

        foreach (var participant in participants)
        {
            if (bundle.Documents.TryGetValue(participant.Key, out var bytes))
                participant.LoadBytes(bytes);
            else
                participant.MarkClean();
        }

        // Participants present on disk but not registered are ignored (forward-compatible).

        _isLoaded = true;
        Loaded?.Invoke();
        PublishDirty(force: true);
        return new LoadResult(Found: true, FormatVersion: bundle.FormatVersion);
    }

    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        List<(ISessionParticipant Participant, long Version, byte[] Bytes)> snapshot;
        Dictionary<string, byte[]> allDocuments;
        List<ISessionParticipant> participants;
        List<ISaveValidator> validators;

        lock (_gate)
        {
            if (!AnyDirty(_participants))
                return;

            participants = _participants.ToList();
            validators = _validators.ToList();
        }

        // Fail-fast validation before any I/O or serialization snapshot.
        foreach (var participant in participants)
            participant.Validate();
        foreach (var validator in validators)
            validator.Validate(this);

        lock (_gate)
        {
            // Re-check dirty under lock after validation (validation is pure by contract).
            if (!AnyDirty(_participants))
                return;

            snapshot = new List<(ISessionParticipant, long, byte[])>(_participants.Count);
            allDocuments = new Dictionary<string, byte[]>(_participants.Count);
            foreach (var p in _participants)
            {
                var version = p.Version;
                // Dirty documents re-serialize; clean ones reuse cached bytes (dirty-only serialize).
                var bytes = p.GetBytes();
                snapshot.Add((p, version, bytes));
                allDocuments[p.Key] = bytes;
            }
        }

        var bundle = new SaveBundle(CurrentFormatVersion, allDocuments);
        await _backend.WriteAsync(bundle, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            foreach (var (participant, version, bytes) in snapshot)
                participant.MarkCleanAt(version, bytes);
        }

        Committed?.Invoke();
        PublishDirty(force: true);
    }

    public ValueTask DisposeAsync() => default;

    private bool IsNotificationSuppressed() => Volatile.Read(ref _suppressDepth) > 0;

    private void EndSuppress()
    {
        var depth = Interlocked.Decrement(ref _suppressDepth);
        if (depth < 0)
        {
            Interlocked.Exchange(ref _suppressDepth, 0);
            throw new InvalidOperationException("SuppressNotifications dispose called too many times.");
        }

        if (depth != 0) return;

        List<ISessionParticipant> participants;
        lock (_gate)
            participants = _participants.ToList();

        foreach (var p in participants)
            p.FlushPendingNotifications();

        PublishDirty(force: true);
    }

    private void NotifyDirty()
    {
        if (IsNotificationSuppressed()) return;
        PublishDirty(force: false);
    }

    private void PublishDirty(bool force)
    {
        if (!force && IsNotificationSuppressed()) return;

        var dirty = IsDirty;
        if (!force && dirty == _lastDirtyNotified) return;
        _lastDirtyNotified = dirty;
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

    private static bool AnyDirty(List<ISessionParticipant> participants)
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

    private sealed class SuppressScope : IDisposable
    {
        private SaveSession? _owner;

        public SuppressScope(SaveSession owner) => _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.EndSuppress();
        }
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

            // MemoryPack's ConcurrentDictionaryFormatter writes the same collection-header-plus-
            // KeyValuePairs wire layout as Dictionary<TKey,TValue> (and ImmutableDictionary before
            // it), so this serializes the store's live backing dictionary directly instead of
            // copying it into a Dictionary first. Verified byte-identical by round-trip test.
            var bytes = MemoryPackSerializer.Serialize(_store.SnapshotItems);
            _cachedBytes = bytes;
            return bytes;
        }

        public void LoadBytes(byte[] bytes)
        {
            // Deserializing straight into ConcurrentDictionary<TKey,T> avoids a Dictionary
            // round-trip; it reads Dictionary/ImmutableDictionary-formatted bytes from older saves
            // identically, since all three formatters share the same wire layout (verified by
            // round-trip test).
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
            // Enumerates key-value pairs directly instead of SnapshotItems.Values, which would
            // allocate a ReadOnlyCollection<T> wrapper on ConcurrentDictionary. Weakly consistent
            // (see KeyedDocumentStore.Snapshot doc) - acceptable here since each item's own
            // internal validation doesn't depend on the other items in the collection.
            foreach (var kvp in _store.SnapshotItems)
            {
                if (kvp.Value is IValidatable validatable)
                    validatable.Validate();
            }
        }

        public void FlushPendingNotifications() => _store.FlushPendingNotifications();
    }
}
