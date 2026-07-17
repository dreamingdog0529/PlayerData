using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MemoryPack;
using NUnit.Framework;

namespace PlayerData.Unity.Editor.Tests
{
    public sealed class SaveDataStoreTests
    {
        private string _root;
        private SessionSchema _schema;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "PlayerDataEditorTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _schema = SessionSchemaResolver.Resolve(typeof(SampleEditorSession));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private static byte[] ProfileBytes(string name, int level) =>
            MemoryPackSerializer.Serialize(new SampleProfile { Name = name, Level = level });

        private static byte[] ItemsBytes(string itemId, int count)
        {
            ConcurrentDictionary<string, SampleItem> bag = new ConcurrentDictionary<string, SampleItem>();
            bag[itemId] = new SampleItem { ItemId = itemId, Count = count };
            return MemoryPackSerializer.Serialize(bag);
        }

        private static void WriteSave(string directory, int formatVersion, Dictionary<string, byte[]> documents)
        {
            new DirectorySaveBackend(directory)
                .WriteAsync(new SaveBundle(formatVersion, documents))
                .AsTask().GetAwaiter().GetResult();
        }

        private static Dictionary<string, byte[]> DefaultDocuments() => new Dictionary<string, byte[]>
        {
            ["SampleProfile"] = ProfileBytes("hero", 5),
            ["items-v1"] = ItemsBytes("potion", 3),
            ["mystery"] = new byte[] { 1, 2, 3, 4 },
            ["Stats"] = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
        };

        private LoadedSave LoadOrFail(string directory)
        {
            LoadedSave? save = SaveDataStore.TryLoad(new SaveLocation(directory, null), _schema, out string? error);
            Assert.That(save, Is.Not.Null, error);
            return save;
        }

        [Test]
        public void FindSaves_DirectAndSlotLayouts_FindsBothWithSlotNumbers()
        {
            WriteSave(Path.Combine(_root, "saveA"), SaveSession.CurrentFormatVersion, DefaultDocuments());
            new SlotSaveBackend(Path.Combine(_root, "saveB"), slot: 2)
                .WriteAsync(new SaveBundle(SaveSession.CurrentFormatVersion, DefaultDocuments()))
                .AsTask().GetAwaiter().GetResult();

            List<SaveLocation> found = SaveDataStore.FindSaves(_root);

            Assert.That(found, Has.Count.EqualTo(2));
            Assert.That(found[0].Directory, Does.EndWith("saveA"));
            Assert.That(found[0].Slot, Is.Null);
            Assert.That(found[1].Directory, Does.EndWith("slot_2"));
            Assert.That(found[1].Slot, Is.EqualTo(2));
        }

        [Test]
        public void TryLoad_MixedDocuments_ClassifiesEachState()
        {
            string dir = Path.Combine(_root, "save");
            WriteSave(dir, SaveSession.CurrentFormatVersion, DefaultDocuments());

            LoadedSave save = LoadOrFail(dir);

            Assert.That(save.IsFormatVersionCurrent, Is.True);
            Dictionary<string, DocumentState> states = save.Documents.ToDictionary(d => d.StorageKey, d => d.State);
            Assert.That(states["SampleProfile"], Is.EqualTo(DocumentState.Editable));
            Assert.That(states["items-v1"], Is.EqualTo(DocumentState.Editable));
            Assert.That(states["mystery"], Is.EqualTo(DocumentState.UnknownKey));
            Assert.That(states["Stats"], Is.EqualTo(DocumentState.Unreadable));
        }

        [Test]
        public void TryLoad_NoManifest_ReturnsNullWithoutError()
        {
            LoadedSave? save = SaveDataStore.TryLoad(new SaveLocation(_root, null), _schema, out string? error);

            Assert.That(save, Is.Null);
            Assert.That(error, Is.Null);
        }

        [Test]
        public void TryLoad_CorruptManifest_ReturnsNullWithError()
        {
            File.WriteAllBytes(Path.Combine(_root, "manifest.bin"), new byte[] { 0xFF });

            LoadedSave? save = SaveDataStore.TryLoad(new SaveLocation(_root, null), _schema, out string? error);

            Assert.That(save, Is.Null);
            Assert.That(error, Is.Not.Null);
        }

        [Test]
        public void WriteDocument_ReplacesTarget_PreservesOthersAndFormatVersion()
        {
            string dir = Path.Combine(_root, "save");
            WriteSave(dir, SaveSession.CurrentFormatVersion, DefaultDocuments());
            LoadedSave save = LoadOrFail(dir);
            byte[] newBytes = ProfileBytes("edited", 99);

            LoadedSave reloaded = SaveDataStore.WriteDocument(save, "SampleProfile", newBytes, _schema);

            Assert.That(reloaded.FormatVersion, Is.EqualTo(SaveSession.CurrentFormatVersion));
            Dictionary<string, byte[]> bytesByKey = reloaded.Documents.ToDictionary(d => d.StorageKey, d => d.Bytes);
            Assert.That(bytesByKey["SampleProfile"], Is.EqualTo(newBytes));
            Assert.That(bytesByKey["mystery"], Is.EqualTo(new byte[] { 1, 2, 3, 4 }), "unknown key must survive write-back");
            Assert.That(bytesByKey["Stats"], Is.EqualTo(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }), "unreadable doc must survive write-back");

            SampleProfile? edited = (SampleProfile?)MemoryPackSerializer.Deserialize(typeof(SampleProfile), bytesByKey["SampleProfile"]);
            Assert.That(edited, Is.Not.Null);
            Assert.That(edited.Name, Is.EqualTo("edited"));
            Assert.That(edited.Level, Is.EqualTo(99));
        }

        [Test]
        public void WriteDocument_NonEditableTargets_ThrowWithoutTouchingDisk()
        {
            string dir = Path.Combine(_root, "save");
            WriteSave(dir, SaveSession.CurrentFormatVersion, DefaultDocuments());
            LoadedSave save = LoadOrFail(dir);
            DateTime manifestStampBefore = File.GetLastWriteTimeUtc(Path.Combine(dir, "manifest.bin"));

            Assert.That(
                () => SaveDataStore.WriteDocument(save, "mystery", new byte[] { 9 }, _schema),
                Throws.InvalidOperationException);
            Assert.That(
                () => SaveDataStore.WriteDocument(save, "Stats", new byte[] { 9 }, _schema),
                Throws.InvalidOperationException);
            Assert.That(
                () => SaveDataStore.WriteDocument(save, "no-such-key", new byte[] { 9 }, _schema),
                Throws.InvalidOperationException);
            Assert.That(File.GetLastWriteTimeUtc(Path.Combine(dir, "manifest.bin")), Is.EqualTo(manifestStampBefore));
        }

        [Test]
        public void TryLoad_PayloadWrittenByNewerSchema_MarksDocumentReadOnlyRoundTrip()
        {
            SessionSchema wideSchema = SessionSchemaResolver.Resolve(typeof(SessionWithWideDoc));
            string dir = Path.Combine(_root, "save");
            WriteSave(dir, SaveSession.CurrentFormatVersion, new Dictionary<string, byte[]>
            {
                ["Wide"] = MemoryPackSerializer.Serialize(new SampleWideDocV2 { A = 1, B = 2, C = 3 }),
            });

            LoadedSave? save = SaveDataStore.TryLoad(new SaveLocation(dir, null), wideSchema, out string? error);

            Assert.That(save, Is.Not.Null, error);
            DocumentEntry entry = save.Documents.Single();
            Assert.That(entry.State, Is.EqualTo(DocumentState.ReadOnlyRoundTrip));
            Assert.That(entry.StateReason, Is.Not.Null);
            Assert.That(
                () => SaveDataStore.WriteDocument(save, "Wide", new byte[] { 1 }, wideSchema),
                Throws.InvalidOperationException);
        }

        [Test]
        public void TryLoad_FormatVersionMismatch_MarksKnownDocumentsReadOnly()
        {
            string dir = Path.Combine(_root, "save");
            WriteSave(dir, 999, new Dictionary<string, byte[]>
            {
                ["SampleProfile"] = ProfileBytes("hero", 5),
            });
            LoadedSave save = LoadOrFail(dir);

            Assert.That(save.IsFormatVersionCurrent, Is.False);
            Assert.That(save.Documents.Single().State, Is.EqualTo(DocumentState.ReadOnlyFormatVersion));
            Assert.That(
                () => SaveDataStore.WriteDocument(save, "SampleProfile", ProfileBytes("x", 1), _schema),
                Throws.InvalidOperationException);
        }
    }
}
