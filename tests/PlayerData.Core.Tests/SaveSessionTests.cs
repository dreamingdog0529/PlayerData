using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;
using NUnit.Framework;

namespace PlayerData.Core.Tests;

public class SaveSessionTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "PlayerData.Core.Tests_" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private SaveSession CreateSession(out IDoc<SamplePlayerData> player, out IBag<string, SampleItem> items)
    {
        var session = new SaveSession(new DirectorySaveBackend(_directory));
        player = session.AddDocument("player", () => new SamplePlayerData(1, "New"));
        items = session.AddCollection<string, SampleItem>("items", i => i.ItemId);
        return session;
    }

    [Test]
    public async Task LoadAsync_NoSave_ReturnsNotFoundAndKeepsInitialValues()
    {
        var session = CreateSession(out var player, out var items);

        var result = await session.LoadAsync();

        Assert.That(result.Found, Is.False);
        Assert.That(session.IsLoaded, Is.True);
        Assert.That(player.Value.Level, Is.EqualTo(1));
        Assert.That(items.Snapshot.Count, Is.EqualTo(0));
        Assert.That(session.IsDirty, Is.False);
    }

    [Test]
    public async Task CommitAsync_ThenLoadAsync_OnSeparateSession_RestoresBothDocuments()
    {
        var writer = CreateSession(out var player, out var items);
        player.Update(_ => new SamplePlayerData(42, "Saved"));
        items.Upsert(new SampleItem("sword", 3));
        Assert.That(writer.IsDirty, Is.True);
        await writer.CommitAsync();
        Assert.That(writer.IsDirty, Is.False);

        var reader = CreateSession(out var player2, out var items2);
        var result = await reader.LoadAsync();

        Assert.That(result.Found, Is.True);
        Assert.That(player2.Value, Is.EqualTo(new SamplePlayerData(42, "Saved")));
        Assert.That(items2.TryGet("sword", out var sword), Is.True);
        Assert.That(sword, Is.EqualTo(new SampleItem("sword", 3)));
        Assert.That(reader.IsDirty, Is.False);
    }

    [Test]
    public async Task LoadAsync_ReadsCollectionSerializedInLegacyDictionaryFormat()
    {
        // CollectionParticipant now serializes ImmutableDictionary<TKey,T> directly instead of
        // copying into Dictionary<TKey,T> first (SaveSession.cs GetBytes/LoadBytes). MemoryPack's
        // Dictionary and ImmutableDictionary formatters share the same collection-header-plus-
        // KeyValuePairs wire layout, so saves written by the old Dictionary-based path must still
        // load correctly under the new code.
        var backend = new DirectorySaveBackend(_directory);
        var legacyItems = new Dictionary<string, SampleItem>
        {
            ["sword"] = new SampleItem("sword", 3),
            ["shield"] = new SampleItem("shield", 1),
        };
        var bundle = new SaveBundle(SaveSession.CurrentFormatVersion, new Dictionary<string, byte[]>
        {
            ["player"] = MemoryPackSerializer.Serialize(new SamplePlayerData(1, "Legacy")),
            ["items"] = MemoryPackSerializer.Serialize(legacyItems),
        });
        await backend.WriteAsync(bundle);

        var reader = CreateSession(out var player, out var items);
        var result = await reader.LoadAsync();

        Assert.That(result.Found, Is.True);
        Assert.That(player.Value, Is.EqualTo(new SamplePlayerData(1, "Legacy")));
        Assert.That(items.Snapshot.Count, Is.EqualTo(2));
        Assert.That(items.TryGet("sword", out var sword), Is.True);
        Assert.That(sword, Is.EqualTo(new SampleItem("sword", 3)));
        Assert.That(items.TryGet("shield", out var shield), Is.True);
        Assert.That(shield, Is.EqualTo(new SampleItem("shield", 1)));
    }

    [Test]
    public async Task CommitAsync_WhenNotDirty_IsNoOp()
    {
        var session = CreateSession(out _, out _);
        await session.CommitAsync();
        Assert.That(File.Exists(Path.Combine(_directory, "manifest.bin")), Is.False);
    }

    [Test]
    public async Task LoadAsync_DoesNotRaiseDocumentChanged()
    {
        var writer = CreateSession(out var player, out var items);
        player.Update(_ => new SamplePlayerData(5, "X"));
        items.Upsert(new SampleItem("a", 1));
        await writer.CommitAsync();

        var reader = CreateSession(out var player2, out var items2);
        var playerChanges = 0;
        var itemChanges = 0;
        player2.Changed += _ => playerChanges++;
        items2.Changed += _ => itemChanges++;
        var loadedEvents = 0;
        reader.Loaded += () => loadedEvents++;

        await reader.LoadAsync();

        Assert.That(playerChanges, Is.EqualTo(0));
        Assert.That(itemChanges, Is.EqualTo(0));
        Assert.That(loadedEvents, Is.EqualTo(1));
        Assert.That(player2.Value.Level, Is.EqualTo(5));
    }

    [Test]
    public async Task CommitAsync_RaisesCommitted()
    {
        var session = CreateSession(out var player, out _);
        var committed = 0;
        session.Committed += () => committed++;
        player.Update(p => p with { Level = 2 });
        await session.CommitAsync();
        Assert.That(committed, Is.EqualTo(1));
    }

    [Test]
    public async Task DirtyChanged_FiresOnUpdateAndCommit()
    {
        var session = CreateSession(out var player, out _);
        var flags = new System.Collections.Generic.List<bool>();
        session.DirtyChanged += flags.Add;

        player.Update(p => p with { Level = 2 });
        Assert.That(flags, Is.EqualTo(new[] { true }));

        await session.CommitAsync();
        Assert.That(flags, Is.EqualTo(new[] { true, false }));
    }

    [Test]
    public async Task CommitAsync_InvalidDirectory_ThrowsInsteadOfSwallowing()
    {
        var blockerPath = Path.Combine(Path.GetTempPath(), "PlayerData.Core.Tests_blocker_" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(blockerPath, new byte[] { 0 });
        try
        {
            var session = new SaveSession(new DirectorySaveBackend(Path.Combine(blockerPath, "sub")));
            var store = session.AddDocument("player", () => new SamplePlayerData(1, "X"));
            store.Update(p => p with { Level = 2 });

            // Windows surfaces the blocking file as IOException, Linux as its subclass
            // DirectoryNotFoundException (ENOTDIR) - accept the whole IOException family.
            Assert.CatchAsync<IOException>(async () => await session.CommitAsync());
        }
        finally
        {
            File.Delete(blockerPath);
        }
    }

    [Test]
    public void AddDocument_DuplicateKey_Throws()
    {
        var session = new SaveSession(new DirectorySaveBackend(_directory));
        session.AddDocument("player", () => new SamplePlayerData(1, "A"));
        Assert.Throws<InvalidOperationException>(() =>
            session.AddDocument("player", () => new SamplePlayerData(1, "B")));
    }

    [Test]
    public void Constructor_NullBackend_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SaveSession(null!));
    }

    [Test]
    public void AddDocument_EmptyKey_Throws()
    {
        var session = new SaveSession(new DirectorySaveBackend(_directory));
        Assert.Throws<ArgumentException>(() => session.AddDocument("", () => new SamplePlayerData(1, "A")));
        Assert.Throws<ArgumentException>(() => session.AddDocument(null!, () => new SamplePlayerData(1, "A")));
    }

    [Test]
    public void AddDocument_NullFactory_Throws()
    {
        var session = new SaveSession(new DirectorySaveBackend(_directory));
        Assert.Throws<ArgumentNullException>(() => session.AddDocument<SamplePlayerData>("player", null!));
    }

    [Test]
    public void AddCollection_NullKeySelector_Throws()
    {
        var session = new SaveSession(new DirectorySaveBackend(_directory));
        Assert.Throws<ArgumentNullException>(() => session.AddCollection<string, SampleItem>("items", null!));
    }

    [Test]
    public void AddCollection_DuplicateKey_Throws()
    {
        var session = new SaveSession(new DirectorySaveBackend(_directory));
        session.AddDocument("shared", () => new SamplePlayerData(1, "A"));
        Assert.Throws<InvalidOperationException>(() =>
            session.AddCollection<string, SampleItem>("shared", i => i.ItemId));
    }

    [Test]
    public void AddValidator_Null_Throws()
    {
        var session = new SaveSession(new DirectorySaveBackend(_directory));
        Assert.Throws<ArgumentNullException>(() => session.AddValidator((ISaveValidator)null!));
        Assert.Throws<ArgumentNullException>(() => session.AddValidator((Action<ISaveSession>)null!));
    }

    [Test]
    public async Task CommitAsync_UpdateDuringCommit_RemainsDirty()
    {
        var session = CreateSession(out var player, out _);
        player.Update(p => p with { Level = 2 });

        await session.CommitAsync();
        player.Update(p => p with { Level = 3 });
        Assert.That(session.IsDirty, Is.True);
    }

    [Test]
    public async Task SlotSaveBackend_IsolatesSlots()
    {
        var slot0 = new SaveSession(new SlotSaveBackend(_directory, 0));
        var p0 = slot0.AddDocument("player", () => new SamplePlayerData(1, "A"));
        p0.Update(_ => new SamplePlayerData(10, "Slot0"));
        await slot0.CommitAsync();

        var slot1 = new SaveSession(new SlotSaveBackend(_directory, 1));
        var p1 = slot1.AddDocument("player", () => new SamplePlayerData(1, "B"));
        p1.Update(_ => new SamplePlayerData(20, "Slot1"));
        await slot1.CommitAsync();

        var read0 = new SaveSession(new SlotSaveBackend(_directory, 0));
        var r0 = read0.AddDocument("player", () => new SamplePlayerData(0, ""));
        await read0.LoadAsync();
        Assert.That(r0.Value.Level, Is.EqualTo(10));

        var read1 = new SaveSession(new SlotSaveBackend(_directory, 1));
        var r1 = read1.AddDocument("player", () => new SamplePlayerData(0, ""));
        await read1.LoadAsync();
        Assert.That(r1.Value.Level, Is.EqualTo(20));
    }

    [Test]
    public async Task CommitAsync_DirtyOnlySerialize_CleanDocumentReusesCache()
    {
        // Spy: second commit after touching only one doc should still restore both.
        var writer = CreateSession(out var player, out var items);
        player.Update(_ => new SamplePlayerData(1, "A"));
        items.Upsert(new SampleItem("sword", 1));
        await writer.CommitAsync();

        player.Update(p => p with { Level = 99 });
        await writer.CommitAsync();

        var reader = CreateSession(out var player2, out var items2);
        await reader.LoadAsync();
        Assert.That(player2.Value.Level, Is.EqualTo(99));
        Assert.That(items2.Get("sword").Count, Is.EqualTo(1));
    }

    [Test]
    public void SuppressNotifications_DefersChangedAndDirtyChanged_UntilDispose()
    {
        var session = CreateSession(out var player, out var items);
        var playerChanges = 0;
        var itemChanges = 0;
        var dirtyFlags = new System.Collections.Generic.List<bool>();
        player.Changed += _ => playerChanges++;
        items.Changed += _ => itemChanges++;
        session.DirtyChanged += dirtyFlags.Add;

        using (session.SuppressNotifications())
        {
            player.Update(p => p with { Level = 5 });
            player.Update(p => p with { Level = 6 });
            items.Upsert(new SampleItem("a", 1));
            Assert.That(playerChanges, Is.EqualTo(0));
            Assert.That(itemChanges, Is.EqualTo(0));
            Assert.That(dirtyFlags, Is.Empty);
            Assert.That(session.IsDirty, Is.True);
        }

        Assert.That(playerChanges, Is.EqualTo(1)); // coalesced
        Assert.That(itemChanges, Is.EqualTo(1));
        Assert.That(dirtyFlags, Is.EqualTo(new[] { true }));
        Assert.That(player.Value.Level, Is.EqualTo(6));
    }

    [Test]
    public void SuppressNotifications_Nested_OnlyFlushesOnOuterDispose()
    {
        var session = CreateSession(out var player, out _);
        var playerChanges = 0;
        player.Changed += _ => playerChanges++;

        using (session.SuppressNotifications())
        {
            player.Update(p => p with { Level = 2 });
            using (session.SuppressNotifications())
            {
                player.Update(p => p with { Level = 3 });
                Assert.That(playerChanges, Is.EqualTo(0));
            }
            // Inner dispose only drops depth; outer scope still suppresses.
            Assert.That(playerChanges, Is.EqualTo(0));
            player.Update(p => p with { Level = 4 });
        }

        Assert.That(playerChanges, Is.EqualTo(1));
        Assert.That(player.Value.Level, Is.EqualTo(4));
    }

    [Test]
    public void SuppressNotifications_DoubleDisposeOfSameScope_IsIdempotent()
    {
        var session = CreateSession(out var player, out _);
        var playerChanges = 0;
        player.Changed += _ => playerChanges++;

        var scope = session.SuppressNotifications();
        player.Update(p => p with { Level = 2 });
        scope.Dispose();
        Assert.That(playerChanges, Is.EqualTo(1));

        scope.Dispose(); // second dispose of the same copy is a no-op
        Assert.That(playerChanges, Is.EqualTo(1));
    }

    [Test]
    public void SuppressNotifications_DisposeOfCopiedScope_Throws()
    {
        var session = CreateSession(out _, out _);
        var scope = session.SuppressNotifications();
        var copy = scope;
        scope.Dispose();

        // Each copy owns the same suppress-depth token; disposing both over-closes the counter.
        Assert.Throws<InvalidOperationException>(() => copy.Dispose());
    }

    [Test]
    public void DefaultSuppressionScope_Dispose_IsNoOp()
    {
        Assert.DoesNotThrow(() => default(SuppressionScope).Dispose());
    }

    [Test]
    public void CommitAsync_ValidatorFailure_ThrowsAndDoesNotWrite()
    {
        var session = CreateSession(out var player, out _);
        session.AddValidator(_ => throw new SaveValidationException("nope"));
        player.Update(p => p with { Level = 2 });

        Assert.ThrowsAsync<SaveValidationException>(async () => await session.CommitAsync());
        Assert.That(File.Exists(Path.Combine(_directory, "manifest.bin")), Is.False);
        Assert.That(session.IsDirty, Is.True);
    }

    [Test]
    public void CommitAsync_IValidatableFailure_ThrowsAndDoesNotWrite()
    {
        var session = new SaveSession(new DirectorySaveBackend(_directory));
        var doc = session.AddDocument("guarded", () => new GuardedData(1));
        doc.Update(_ => new GuardedData(-1));

        Assert.ThrowsAsync<SaveValidationException>(async () => await session.CommitAsync());
        Assert.That(File.Exists(Path.Combine(_directory, "manifest.bin")), Is.False);
    }

    [Test]
    public void CommitAsync_CollectionIValidatableFailure_ThrowsAndDoesNotWrite()
    {
        var session = new SaveSession(new DirectorySaveBackend(_directory));
        var bag = session.AddCollection<string, GuardedItem>("guarded", g => g.Id);
        bag.Upsert(new GuardedItem("ok", 1));
        bag.Upsert(new GuardedItem("bad", -1));

        Assert.ThrowsAsync<SaveValidationException>(async () => await session.CommitAsync());
        Assert.That(File.Exists(Path.Combine(_directory, "manifest.bin")), Is.False);
        Assert.That(session.IsDirty, Is.True);
    }

    [Test]
    public async Task LoadAsync_MissingDocumentKeyInSave_KeepsInitialAndStaysClean()
    {
        var backend = new DirectorySaveBackend(_directory);
        var bundle = new SaveBundle(SaveSession.CurrentFormatVersion, new Dictionary<string, byte[]>
        {
            ["player"] = MemoryPackSerializer.Serialize(new SamplePlayerData(7, "OnlyPlayer")),
            // intentionally no "items" entry
        });
        await backend.WriteAsync(bundle);

        var session = CreateSession(out var player, out var items);
        var result = await session.LoadAsync();

        Assert.That(result.Found, Is.True);
        Assert.That(player.Value, Is.EqualTo(new SamplePlayerData(7, "OnlyPlayer")));
        Assert.That(items.Count, Is.EqualTo(0));
        Assert.That(session.IsDirty, Is.False);
    }

    [Test]
    public async Task LoadAsync_UnregisteredDocumentKeys_AreIgnored()
    {
        var backend = new DirectorySaveBackend(_directory);
        var bundle = new SaveBundle(SaveSession.CurrentFormatVersion, new Dictionary<string, byte[]>
        {
            ["player"] = MemoryPackSerializer.Serialize(new SamplePlayerData(3, "P")),
            ["items"] = MemoryPackSerializer.Serialize(new Dictionary<string, SampleItem>
            {
                ["sword"] = new SampleItem("sword", 1),
            }),
            ["future_feature"] = new byte[] { 1, 2, 3 },
        });
        await backend.WriteAsync(bundle);

        var reader = CreateSession(out var player, out var items);
        var result = await reader.LoadAsync();

        Assert.That(result.Found, Is.True);
        Assert.That(player.Value.Level, Is.EqualTo(3));
        Assert.That(items.TryGet("sword", out _), Is.True);
        Assert.That(reader.IsDirty, Is.False);
    }

    [Test]
    public async Task CommitAsync_MultipleDirtyDocuments_RoundTrips()
    {
        // Exercises the parallel dirty-serialize path (dirtyCount > 1).
        var writer = CreateSession(out var player, out var items);
        player.Update(_ => new SamplePlayerData(11, "Multi"));
        items.Upsert(new SampleItem("a", 1));
        items.Upsert(new SampleItem("b", 2));
        await writer.CommitAsync();

        var reader = CreateSession(out var player2, out var items2);
        await reader.LoadAsync();
        Assert.That(player2.Value, Is.EqualTo(new SamplePlayerData(11, "Multi")));
        Assert.That(items2.Snapshot.Count, Is.EqualTo(2));
        Assert.That(items2.Get("b").Count, Is.EqualTo(2));
    }

    [Test]
    public async Task LoadAsync_AppliesMigrationsInOrder()
    {
        var backend = new DirectorySaveBackend(_directory);
        // Version 0 save with a single document key the migration will rewrite.
        await backend.WriteAsync(new SaveBundle(0, new Dictionary<string, byte[]>
        {
            ["legacy_player"] = MemoryPackSerializer.Serialize(new SamplePlayerData(9, "Legacy")),
        }));

        var migrations = new ISaveMigration[]
        {
            new RenameDocumentMigration(fromVersion: 0, toVersion: 1, fromKey: "legacy_player", toKey: "player"),
        };
        var session = new SaveSession(backend, migrations);
        var player = session.AddDocument("player", () => new SamplePlayerData(1, "New"));

        var result = await session.LoadAsync();

        Assert.That(result.Found, Is.True);
        Assert.That(result.FormatVersion, Is.EqualTo(1));
        Assert.That(player.Value, Is.EqualTo(new SamplePlayerData(9, "Legacy")));
    }

    [Test]
    public async Task LoadAsync_FutureFormatVersion_Throws()
    {
        var backend = new DirectorySaveBackend(_directory);
        await backend.WriteAsync(new SaveBundle(SaveSession.CurrentFormatVersion + 1, new Dictionary<string, byte[]>
        {
            ["player"] = MemoryPackSerializer.Serialize(new SamplePlayerData(1, "X")),
        }));

        var session = CreateSession(out _, out _);
        Assert.ThrowsAsync<InvalidDataException>(async () => await session.LoadAsync());
    }

    [Test]
    public async Task LoadAsync_MissingMigration_Throws()
    {
        var backend = new DirectorySaveBackend(_directory);
        await backend.WriteAsync(new SaveBundle(0, new Dictionary<string, byte[]>
        {
            ["player"] = MemoryPackSerializer.Serialize(new SamplePlayerData(1, "X")),
        }));

        // No migrations registered for version 0 → 1.
        var session = CreateSession(out _, out _);
        Assert.ThrowsAsync<InvalidDataException>(async () => await session.LoadAsync());
    }

    [Test]
    public async Task LoadAsync_MigrationReturningNull_Throws()
    {
        var backend = new DirectorySaveBackend(_directory);
        await backend.WriteAsync(new SaveBundle(0, new Dictionary<string, byte[]>
        {
            ["player"] = MemoryPackSerializer.Serialize(new SamplePlayerData(1, "X")),
        }));

        var session = new SaveSession(backend, new[] { new NullMigration(0, 1) });
        session.AddDocument("player", () => new SamplePlayerData(1, "New"));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await session.LoadAsync());
    }

    [Test]
    public async Task LoadAsync_MigrationWrongToVersion_Throws()
    {
        var backend = new DirectorySaveBackend(_directory);
        await backend.WriteAsync(new SaveBundle(0, new Dictionary<string, byte[]>
        {
            ["player"] = MemoryPackSerializer.Serialize(new SamplePlayerData(1, "X")),
        }));

        var session = new SaveSession(backend, new[] { new WrongVersionMigration(0, 1) });
        session.AddDocument("player", () => new SamplePlayerData(1, "New"));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await session.LoadAsync());
    }

    [Test]
    public async Task CommitAsync_AfterLoad_ThenModify_OnlyNewChangesAreDirty()
    {
        var writer = CreateSession(out var player, out var items);
        player.Update(_ => new SamplePlayerData(5, "A"));
        items.Upsert(new SampleItem("sword", 1));
        await writer.CommitAsync();

        var session = CreateSession(out var player2, out var items2);
        await session.LoadAsync();
        Assert.That(session.IsDirty, Is.False);

        items2.Upsert(new SampleItem("sword", 9));
        Assert.That(session.IsDirty, Is.True);
        await session.CommitAsync();
        Assert.That(session.IsDirty, Is.False);

        var reader = CreateSession(out var player3, out var items3);
        await reader.LoadAsync();
        Assert.That(player3.Value.Level, Is.EqualTo(5));
        Assert.That(items3.Get("sword").Count, Is.EqualTo(9));
    }

    [Test]
    public async Task DisposeAsync_Completes()
    {
        var session = CreateSession(out _, out _);
        await session.DisposeAsync();
    }

    // Coverage gap identified when KeyedDocumentStore moved from ImmutableDictionary (frozen
    // point-in-time Snapshot) to ConcurrentDictionary (weakly consistent live Snapshot):
    // GetBytes()/Validate() now enumerate the same live dictionary a background thread may be
    // writing to. This must never throw or produce a save file that fails to load, no matter how
    // the writes and the serializing reads interleave.
    [Test]
    public async Task CommitAsync_ConcurrentCollectionWrites_DoesNotThrowOrCorrupt()
    {
        const int keyCount = 200;
        var writer = CreateSession(out _, out var items);
        for (var i = 0; i < keyCount; i++)
            items.Upsert(new SampleItem("item" + i, i));
        await writer.CommitAsync();

        using var cts = new CancellationTokenSource();
        var writerTask = Task.Run(() =>
        {
            var i = 0;
            while (!cts.IsCancellationRequested)
            {
                items.Upsert(new SampleItem("item" + (i % keyCount), i));
                i++;
            }
        });

        try
        {
            for (var c = 0; c < 20; c++)
                await writer.CommitAsync();
        }
        finally
        {
            cts.Cancel();
            await writerTask;
        }

        var reader = CreateSession(out _, out var readItems);
        var result = await reader.LoadAsync();
        Assert.That(result.Found, Is.True);
        Assert.That(readItems.Snapshot.Count, Is.EqualTo(keyCount));
    }

    // Boundary tests for DirectorySaveBackend.BuildManifest/ParseManifest, rewritten onto raw
    // pointers + BinaryPrimitives to drop the MemoryStream/BinaryWriter/BinaryReader allocations.
    // Both methods are `internal` with no InternalsVisibleTo, so these exercise them the only way
    // available from outside the assembly: a full WriteAsync/ReadAsync round trip.
    [Test]
    public async Task DirectorySaveBackend_EmptyManifest_RoundTrips()
    {
        var backend = new DirectorySaveBackend(_directory);
        var bundle = new SaveBundle(1, new Dictionary<string, byte[]>());
        await backend.WriteAsync(bundle);

        var result = await backend.ReadAsync();

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.FormatVersion, Is.EqualTo(1));
        Assert.That(result.Documents.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task DirectorySaveBackend_MultiByteUtf8Keys_RoundTrip()
    {
        var backend = new DirectorySaveBackend(_directory);
        var documents = new Dictionary<string, byte[]>
        {
            ["プレイヤー"] = new byte[] { 1, 2, 3 },
            ["アイテム袋"] = new byte[] { 4, 5, 6, 7 },
            ["🎮emoji"] = new byte[] { 8 }, // surrogate pair - stresses the UTF-8 byte-count/write boundary
        };
        var bundle = new SaveBundle(1, documents);
        await backend.WriteAsync(bundle);

        var result = await backend.ReadAsync();

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Documents.Count, Is.EqualTo(3));
        Assert.That(result.Documents["プレイヤー"], Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(result.Documents["アイテム袋"], Is.EqualTo(new byte[] { 4, 5, 6, 7 }));
        Assert.That(result.Documents["🎮emoji"], Is.EqualTo(new byte[] { 8 }));
    }

    private sealed class GuardedData : IValidatable
    {
        public GuardedData(int value) => Value = value;
        public int Value { get; }

        public void Validate()
        {
            if (Value < 0)
                throw new SaveValidationException("Value must be non-negative.");
        }
    }

    private sealed class GuardedItem : IValidatable
    {
        public GuardedItem(string id, int value)
        {
            Id = id;
            Value = value;
        }

        public string Id { get; }
        public int Value { get; }

        public void Validate()
        {
            if (Value < 0)
                throw new SaveValidationException("Item value must be non-negative.");
        }
    }

    private sealed class RenameDocumentMigration : ISaveMigration
    {
        private readonly string _fromKey;
        private readonly string _toKey;

        public RenameDocumentMigration(int fromVersion, int toVersion, string fromKey, string toKey)
        {
            FromVersion = fromVersion;
            ToVersion = toVersion;
            _fromKey = fromKey;
            _toKey = toKey;
        }

        public int FromVersion { get; }
        public int ToVersion { get; }

        public SaveBundle Migrate(SaveBundle bundle)
        {
            var docs = new Dictionary<string, byte[]>(bundle.Documents);
            if (docs.TryGetValue(_fromKey, out var bytes))
            {
                docs.Remove(_fromKey);
                docs[_toKey] = bytes;
            }
            return new SaveBundle(ToVersion, docs);
        }
    }

    private sealed class NullMigration : ISaveMigration
    {
        public NullMigration(int fromVersion, int toVersion)
        {
            FromVersion = fromVersion;
            ToVersion = toVersion;
        }

        public int FromVersion { get; }
        public int ToVersion { get; }
        public SaveBundle Migrate(SaveBundle bundle) => null!;
    }

    private sealed class WrongVersionMigration : ISaveMigration
    {
        public WrongVersionMigration(int fromVersion, int toVersion)
        {
            FromVersion = fromVersion;
            ToVersion = toVersion;
        }

        public int FromVersion { get; }
        public int ToVersion { get; }

        public SaveBundle Migrate(SaveBundle bundle) =>
            // Claims ToVersion but leaves the bundle's FormatVersion unchanged.
            new SaveBundle(FromVersion, new Dictionary<string, byte[]>(bundle.Documents));
    }
}

