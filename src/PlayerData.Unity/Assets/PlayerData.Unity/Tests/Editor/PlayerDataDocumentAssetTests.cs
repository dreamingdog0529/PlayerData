using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace PlayerData.Unity.Editor.Tests
{
    public sealed class PlayerDataDocumentAssetTests
    {
        private string _exportRoot = null!;

        [SetUp]
        public void SetUp()
        {
            _exportRoot = Path.Combine(Path.GetTempPath(), "PlayerDataDocumentAsset_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_exportRoot);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_exportRoot))
                Directory.Delete(_exportRoot, recursive: true);
        }

        private static PlayerDataDocumentAsset CreateAsset(string sessionTypeName, string storageKey, string json)
        {
            PlayerDataDocumentAsset asset = ScriptableObject.CreateInstance<PlayerDataDocumentAsset>();
            asset.SessionTypeName = sessionTypeName;
            asset.StorageKey = storageKey;
            asset.Json = json;
            return asset;
        }

        [Test]
        public void TryResolve_ValidSampleSession_ResolvesDescriptor()
        {
            PlayerDataDocumentAsset asset = CreateAsset(
                typeof(SampleEditorSession).FullName!,
                "SampleProfile",
                "{}");

            bool ok = PlayerDataDocumentAssetUtility.TryResolve(
                asset, out Type sessionType, out DocumentDescriptor descriptor, out string? error);

            Assert.That(ok, Is.True, error);
            Assert.That(sessionType, Is.EqualTo(typeof(SampleEditorSession)));
            Assert.That(descriptor.PropertyName, Is.EqualTo("SampleProfile"));
            Assert.That(descriptor.IsCollection, Is.False);
        }

        [Test]
        public void TryResolve_UnknownSession_FailsWithReason()
        {
            PlayerDataDocumentAsset asset = CreateAsset("No.Such.Session", "SampleProfile", "{}");

            bool ok = PlayerDataDocumentAssetUtility.TryResolve(asset, out _, out _, out string? error);

            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("not found"));
        }

        [Test]
        public void TryResolve_UnknownDocumentKey_FailsWithReason()
        {
            PlayerDataDocumentAsset asset = CreateAsset(
                typeof(SampleEditorSession).FullName!,
                "missing-key",
                "{}");

            bool ok = PlayerDataDocumentAssetUtility.TryResolve(asset, out _, out _, out string? error);

            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("missing-key"));
        }

        [Test]
        public void TryValidate_ValidProfileJson_Passes()
        {
            string json = MemoryPackJsonConverter.ToJson(
                new SampleProfile { Name = "hero", Level = 3 },
                typeof(SampleProfile));
            PlayerDataDocumentAsset asset = CreateAsset(
                typeof(SampleEditorSession).FullName!,
                "SampleProfile",
                json);

            bool ok = PlayerDataDocumentAssetUtility.TryValidate(asset, out DocumentDescriptor descriptor, out string? error);

            Assert.That(ok, Is.True, error);
            Assert.That(descriptor.StorageKey, Is.EqualTo("SampleProfile"));
        }

        [Test]
        public void TryValidate_MalformedJson_FailsWithoutMutatingAsset()
        {
            PlayerDataDocumentAsset asset = CreateAsset(
                typeof(SampleEditorSession).FullName!,
                "SampleProfile",
                "{ not json");
            string original = asset.Json;

            bool ok = PlayerDataDocumentAssetUtility.TryValidate(asset, out _, out string? error);

            Assert.That(ok, Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
            Assert.That(asset.Json, Is.EqualTo(original));
        }

        [Test]
        public void TrySetJson_ValidEdit_UpdatesAsset()
        {
            PlayerDataDocumentAsset asset = CreateAsset(
                typeof(SampleEditorSession).FullName!,
                "SampleProfile",
                "{}");
            string json = MemoryPackJsonConverter.ToJson(
                new SampleProfile { Name = "edited", Level = 9 },
                typeof(SampleProfile));

            bool ok = PlayerDataDocumentAssetUtility.TrySetJson(asset, json, out string? error);

            Assert.That(ok, Is.True, error);
            Assert.That(asset.Json, Does.Contain("edited"));
            Assert.That(asset.Json, Does.Contain("9"));
        }

        [Test]
        public void TrySetJson_UnknownProperty_FailsAndLeavesAssetUnchanged()
        {
            string original = MemoryPackJsonConverter.ToJson(
                new SampleProfile { Name = "keep", Level = 1 },
                typeof(SampleProfile));
            PlayerDataDocumentAsset asset = CreateAsset(
                typeof(SampleEditorSession).FullName!,
                "SampleProfile",
                original);

            bool ok = PlayerDataDocumentAssetUtility.TrySetJson(
                asset, "{ \"Name\": \"x\", \"Level\": 1, \"NoSuch\": 1 }", out string? error);

            Assert.That(ok, Is.False);
            Assert.That(error, Is.Not.Null);
            Assert.That(asset.Json, Is.EqualTo(original));
        }

        [Test]
        public void TryCreateDefaultJson_SingleAndCollection_Succeed()
        {
            SessionSchema schema = SessionSchemaResolver.Resolve(typeof(SampleEditorSession));
            DocumentDescriptor profile = null!;
            DocumentDescriptor items = null!;
            foreach (DocumentDescriptor document in schema.Documents)
            {
                if (document.StorageKey == "SampleProfile") profile = document;
                if (document.StorageKey == "items-v1") items = document;
            }

            Assert.That(
                PlayerDataDocumentAssetUtility.TryCreateDefaultJson(profile, out string profileJson, out string? e1),
                Is.True,
                e1);
            Assert.That(profileJson, Does.Contain("Name"));

            Assert.That(
                PlayerDataDocumentAssetUtility.TryCreateDefaultJson(items, out string itemsJson, out string? e2),
                Is.True,
                e2);
            Assert.That(itemsJson, Is.Not.Empty);
        }

        [Test]
        public void TryExportToFolder_CreatesReadableSave_AndMergesSecondDocument()
        {
            string profileJson = MemoryPackJsonConverter.ToJson(
                new SampleProfile { Name = "export-hero", Level = 7 },
                typeof(SampleProfile));
            PlayerDataDocumentAsset profileAsset = CreateAsset(
                typeof(SampleEditorSession).FullName!,
                "SampleProfile",
                profileJson);

            Assert.That(
                PlayerDataDocumentAssetUtility.TryExportToFolder(profileAsset, _exportRoot, out string? error1),
                Is.True,
                error1);

            string statsJson = MemoryPackJsonConverter.ToJson(
                new SampleStats { Hp = 42 },
                typeof(SampleStats));
            PlayerDataDocumentAsset statsAsset = CreateAsset(
                typeof(SampleEditorSession).FullName!,
                "Stats",
                statsJson);

            Assert.That(
                PlayerDataDocumentAssetUtility.TryExportToFolder(statsAsset, _exportRoot, out string? error2),
                Is.True,
                error2);

            SessionSchema schema = SessionSchemaResolver.Resolve(typeof(SampleEditorSession));
            LoadedSave? save = SaveDataStore.TryLoad(new SaveLocation(_exportRoot, slot: null), schema, out string? loadError);
            Assert.That(save, Is.Not.Null, loadError);
            Assert.That(save!.Documents.Count, Is.EqualTo(2));

            DocumentEntry? profileEntry = null;
            DocumentEntry? statsEntry = null;
            foreach (DocumentEntry entry in save.Documents)
            {
                if (entry.StorageKey == "SampleProfile") profileEntry = entry;
                if (entry.StorageKey == "Stats") statsEntry = entry;
            }

            Assert.That(profileEntry, Is.Not.Null);
            Assert.That(statsEntry, Is.Not.Null);
            Assert.That(profileEntry!.State, Is.EqualTo(DocumentState.Editable));
            Assert.That(statsEntry!.State, Is.EqualTo(DocumentState.Editable));

            SampleProfile loadedProfile = MemoryPack.MemoryPackSerializer.Deserialize<SampleProfile>(profileEntry.Bytes)!;
            Assert.That(loadedProfile.Name, Is.EqualTo("export-hero"));
            Assert.That(loadedProfile.Level, Is.EqualTo(7));
        }

        [Test]
        public void DocumentKeyChoices_ReturnsSampleKeys()
        {
            var keys = PlayerDataDocumentAssetUtility.DocumentKeyChoices(typeof(SampleEditorSession).FullName!);
            Assert.That(keys, Is.EquivalentTo(new[] { "SampleProfile", "Stats", "items-v1" }));
        }

        [Test]
        public void SessionTypeChoices_IncludesSampleEditorSession()
        {
            var choices = PlayerDataDocumentAssetUtility.SessionTypeChoices();
            Assert.That(choices, Has.Member(typeof(SampleEditorSession).FullName));
        }
    }
}
