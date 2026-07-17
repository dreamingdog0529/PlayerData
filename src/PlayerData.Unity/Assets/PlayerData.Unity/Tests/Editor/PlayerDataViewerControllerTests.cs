using System;
using System.IO;
using NUnit.Framework;

namespace PlayerData.Unity.Editor.Tests
{
    public sealed class PlayerDataViewerControllerTests
    {
        private string _root;
        private string _saveRoot;
        private PlayerDataViewerController _controller;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "PlayerDataEditorTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _saveRoot = SampleSaveMenu.Create(_root);

            _controller = new PlayerDataViewerController();
            _controller.RefreshSessionTypes();
            _controller.SelectSession(typeof(SampleEditorSession));
            _controller.Scan(_saveRoot);
            _controller.SelectSave(_controller.Saves[0]);
            Assert.That(_controller.CurrentSave, Is.Not.Null, _controller.LoadError);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private string ProfileJson()
        {
            DocumentView? view = _controller.GetDocumentView("SampleProfile");
            Assert.That(view, Is.Not.Null);
            Assert.That(view.Json, Is.Not.Null);
            return view.Json;
        }

        private DateTime ManifestStampUtc() =>
            File.GetLastWriteTimeUtc(Path.Combine(_saveRoot, "manifest.bin"));

        [Test]
        public void ApplyJson_EditableDocument_PersistsNewValue()
        {
            string editedJson = ProfileJson().Replace("\"hero\"", "\"edited\"");

            bool applied = _controller.ApplyJson("SampleProfile", editedJson, out string? error);

            Assert.That(applied, Is.True, error);
            Assert.That(error, Is.Null);
            Assert.That(ProfileJson(), Does.Contain("edited"));

            // A fresh controller reading from disk sees the persisted value, and the other
            // documents (unknown key included) are intact.
            PlayerDataViewerController fresh = new PlayerDataViewerController();
            fresh.SelectSession(typeof(SampleEditorSession));
            fresh.Scan(_saveRoot);
            fresh.SelectSave(fresh.Saves[0]);
            DocumentView? reloaded = fresh.GetDocumentView("SampleProfile");
            Assert.That(reloaded, Is.Not.Null);
            Assert.That(reloaded.Json, Does.Contain("edited"));
            DocumentView? mystery = fresh.GetDocumentView("mystery");
            Assert.That(mystery, Is.Not.Null);
            Assert.That(mystery.Entry.Bytes, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
        }

        [Test]
        public void ApplyJson_MalformedJson_FailsWithoutDiskChange()
        {
            DateTime stampBefore = ManifestStampUtc();

            bool applied = _controller.ApplyJson("SampleProfile", "{ not valid json", out string? error);

            Assert.That(applied, Is.False);
            Assert.That(error, Does.Contain("JSON"));
            Assert.That(ManifestStampUtc(), Is.EqualTo(stampBefore));
            Assert.That(ProfileJson(), Does.Contain("hero"));
        }

        [Test]
        public void ApplyJson_UnknownPropertyName_FailsWithoutDiskChange()
        {
            DateTime stampBefore = ManifestStampUtc();

            bool applied = _controller.ApplyJson("SampleProfile", "{ \"NoSuchProperty\": 1 }", out string? error);

            Assert.That(applied, Is.False);
            Assert.That(error, Does.Contain("JSON"));
            Assert.That(ManifestStampUtc(), Is.EqualTo(stampBefore));
        }

        [Test]
        public void ApplyJson_NonEditableDocuments_Fail()
        {
            Assert.That(_controller.ApplyJson("mystery", "{}", out string? unknownError), Is.False);
            Assert.That(unknownError, Does.Contain("not editable"));
            Assert.That(_controller.ApplyJson("Stats", "{}", out string? unreadableError), Is.False);
            Assert.That(unreadableError, Does.Contain("not editable"));
            Assert.That(_controller.ApplyJson("no-such-key", "{}", out string? missingError), Is.False);
            Assert.That(missingError, Does.Contain("does not exist"));
        }
    }
}
