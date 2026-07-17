using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace PlayerData.Unity.Editor.Tests
{
    public sealed class PlayerDataViewerFriendlyUxTests
    {
        private string _root = null!;
        private VisualElement _rootElement = null!;
        private ViewerPanel _panel = null!;
        private PlayerDataViewerController _controller = null!;
        private SampleEditorSession? _session;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "PlayerDataViewerFriendlyUx_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _controller = new PlayerDataViewerController();
            _rootElement = new VisualElement();
            _panel = ViewerUI.BuildInto(_rootElement, _controller, _root);
            LiveSessionRegistry.ClearForTests();
        }

        [TearDown]
        public async Task TearDown()
        {
            _panel.Dispose();
            if (_session is not null)
                await _session.DisposeAsync();
            LiveSessionRegistry.ClearForTests();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private DropdownField Session => _rootElement.Q<DropdownField>(ViewerUI.SessionDropdownName);
        private ListView DocumentsList => _rootElement.Q<ListView>(ViewerUI.DocumentsListName);
        private Foldout Advanced => _rootElement.Q<Foldout>(ViewerUI.AdvancedFoldoutName);
        private HelpBox Guide => _rootElement.Q<HelpBox>(ViewerUI.GuideBoxName);
        private ScrollView JsonScroll => _rootElement.Q<ScrollView>(ViewerUI.JsonScrollName);
        private TextField Json => _rootElement.Q<TextField>(ViewerUI.DocumentJsonName);
        private Label DocumentInfo => _rootElement.Q<Label>(ViewerUI.DocumentInfoName);

        private int SessionIndexOf(Type sessionType)
        {
            for (int i = 0; i < _controller.SessionTypes.Count; i++)
            {
                if (_controller.SessionTypes[i] == sessionType)
                    return i;
            }

            return -1;
        }

        private void SelectDiskDocument(string storageKey)
        {
            string saveRoot = SampleSaveMenu.Create(_root);
            int sessionIndex = SessionIndexOf(typeof(SampleEditorSession));
            Assert.That(sessionIndex, Is.GreaterThanOrEqualTo(0));
            _panel.SelectSessionForTests(sessionIndex);
            _panel.ScanForTests(saveRoot);
            _panel.SelectSaveForTests(0);

            IList items = DocumentsList.itemsSource;
            for (int i = 0; i < items.Count; i++)
            {
                DocumentEntry entry = (DocumentEntry)items[i]!;
                if (string.Equals(entry.StorageKey, storageKey, StringComparison.Ordinal))
                {
                    _panel.SelectDocumentForTests(i);
                    return;
                }
            }

            Assert.Fail("document not found: " + storageKey);
        }

        [Test]
        public void SessionDropdown_UsesShortTypeNames_NotFqcn()
        {
            Assert.That(Session.choices, Has.Member("SampleEditorSession"));
            foreach (string choice in Session.choices)
                Assert.That(choice, Does.Not.Contain("PlayerData.Unity.Editor.Tests.SampleEditorSession"));
        }

        [Test]
        public void SourceChoice_IsSavedFiles_NotDisk()
        {
            DropdownField source = _rootElement.Q<DropdownField>(ViewerUI.SourceDropdownName);
            Assert.That(source.choices[0], Is.EqualTo(ViewerDisplayNames.SavedFilesLabel));
            Assert.That(source.choices[0], Is.Not.EqualTo("Disk"));
        }

        [Test]
        public void DiskDocumentList_ShowsPlainState_NotTechnicalEnumNames()
        {
            string saveRoot = SampleSaveMenu.Create(_root);
            int sessionIndex = SessionIndexOf(typeof(SampleEditorSession));
            _panel.SelectSessionForTests(sessionIndex);
            _panel.ScanForTests(saveRoot);
            _panel.SelectSaveForTests(0);

            IList items = DocumentsList.itemsSource;
            Assert.That(items.Count, Is.GreaterThan(0));
            foreach (DocumentEntry entry in items)
            {
                string line = ViewerDisplayNames.FormatDocumentLine(entry, includeTechnicalDetails: false);
                Assert.That(line, Does.Not.Contain("ReadOnlyRoundTrip"));
                Assert.That(line, Does.Not.Contain("ReadOnlyFormatVersion"));
                Assert.That(line, Does.Not.Contain("UnknownKey"));
                Assert.That(line, Does.Not.Contain("Unreadable"));
                if (entry.StorageKey == "items-v1")
                {
                    Assert.That(line, Does.Contain("Items"));
                    Assert.That(line, Does.Not.Contain("items-v1"));
                }
            }
        }

        [Test]
        public void Advanced_DefaultsClosed_AndHidesJsonUntilOpened()
        {
            Assert.That(Advanced, Is.Not.Null);
            Assert.That(Advanced.value, Is.False);
            Assert.That(Guide, Is.Not.Null);
            Assert.That(Guide.style.display.value, Is.EqualTo(DisplayStyle.Flex));

            Assert.That(Json, Is.Not.Null);
            Assert.That(JsonScroll.style.display.value, Is.EqualTo(DisplayStyle.None));
        }

        [Test]
        public void SelectingJsonTab_OpensAdvanced()
        {
            SelectDiskDocument("SampleProfile");
            _panel.SelectTabForTests(showFields: false);

            Assert.That(_panel.AdvancedOpenForTests, Is.True);
            Assert.That(JsonScroll.style.display.value, Is.EqualTo(DisplayStyle.Flex));
        }

        [Test]
        public void DocumentInfo_UsesPlainLanguage_NotEnumName()
        {
            SelectDiskDocument("SampleProfile");

            Assert.That(DocumentInfo.text, Does.Contain("SampleProfile"));
            Assert.That(DocumentInfo.text, Does.Contain("Editable"));
            Assert.That(DocumentInfo.text, Does.Not.Contain("DocumentState"));
        }

        [Test]
        public void LiveAddEntry_UsesFieldsTemplate_WithoutHandWrittenJson()
        {
            _session = new SampleEditorSession(new DirectorySaveBackend(_root));
            using IDisposable token = LiveSessionRegistry.Register("game", _session);
            _panel.SelectSourceForTests(1);

            IList docs = _rootElement.Q<ListView>(ViewerUI.LiveDocumentsListName).itemsSource;
            int itemsIndex = -1;
            for (int i = 0; i < docs.Count; i++)
            {
                if (((LiveDocumentDescriptor)docs[i]!).PropertyName == "Items")
                {
                    itemsIndex = i;
                    break;
                }
            }

            Assert.That(itemsIndex, Is.GreaterThanOrEqualTo(0));
            _panel.SelectLiveDocumentForTests(itemsIndex);

            FieldEditorModel? model = _panel.AddEntryModelForTests;
            Assert.That(model, Is.Not.Null, "Add Entry should build a Fields template");
            model!.SetString("ItemId", "potion");
            Assert.That(model.TrySetNumber("Count", "3"), Is.True);

            int before = _session.Items.Snapshot.Count;
            _panel.AddEntryForTests();

            Assert.That(_session.Items.Snapshot.Count, Is.EqualTo(before + 1));
            Assert.That(_session.Items.Snapshot.ContainsKey("potion"), Is.True);
            Assert.That(_session.Items.Snapshot["potion"].Count, Is.EqualTo(3));
        }
    }
}
