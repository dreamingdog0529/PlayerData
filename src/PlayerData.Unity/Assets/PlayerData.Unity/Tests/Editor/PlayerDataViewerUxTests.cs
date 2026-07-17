using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace PlayerData.Unity.Editor.Tests
{
    // List UX behaviors of the viewer window: the search filter, the unapplied-change
    // indicator (Apply/Revert enablement + "*" in the info label) and the Open Folder button.
    public sealed class PlayerDataViewerUxTests
    {
        private string _root;
        private SampleEditorSession _session;
        private PlayerDataViewerController _controller;
        private VisualElement _rootElement;
        private ViewerPanel _panel;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "PlayerDataEditorTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            LiveSessionRegistry.ClearForTests();
            _session = new SampleEditorSession(new DirectorySaveBackend(_root));
            _controller = new PlayerDataViewerController();
            _rootElement = new VisualElement();
            _panel = ViewerUI.BuildInto(_rootElement, _controller, _root);
        }

        [TearDown]
        public void TearDown()
        {
            _panel.Dispose();
            LiveSessionRegistry.ClearForTests();
            _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private TextField SearchField => _rootElement.Q<TextField>(ViewerUI.SearchFieldName);

        private Button OpenFolderButton => _rootElement.Q<Button>(ViewerUI.OpenFolderButtonName);

        private VisualElement DiskSection => _rootElement.Q<VisualElement>(ViewerUI.DiskSectionName);

        private ListView DocumentsList => _rootElement.Q<ListView>(ViewerUI.DocumentsListName);

        private ListView LiveDocumentsList => _rootElement.Q<ListView>(ViewerUI.LiveDocumentsListName);

        private Label Info => _rootElement.Q<Label>(ViewerUI.DocumentInfoName);

        private Button ApplyButton => _rootElement.Q<Button>(ViewerUI.ApplyButtonName);

        private Button RevertButton => _rootElement.Q<Button>(ViewerUI.RevertButtonName);

        private TextField Json => _rootElement.Q<TextField>(ViewerUI.DocumentJsonName);

        private int SessionTypeIndex(Type sessionType)
        {
            for (int i = 0; i < _controller.SessionTypes.Count; i++)
            {
                if (_controller.SessionTypes[i] == sessionType)
                    return i;
            }

            Assert.Fail($"Session type '{sessionType.Name}' not found.");
            return -1;
        }

        private int DocumentIndex(string storageKey)
        {
            IList items = DocumentsList.itemsSource;
            for (int i = 0; i < items.Count; i++)
            {
                if (((DocumentEntry)items[i]).StorageKey == storageKey)
                    return i;
            }

            Assert.Fail($"Document '{storageKey}' not found.");
            return -1;
        }

        private int LiveDocumentIndex(string propertyName)
        {
            IList items = LiveDocumentsList.itemsSource;
            for (int i = 0; i < items.Count; i++)
            {
                if (((LiveDocumentDescriptor)items[i]).PropertyName == propertyName)
                    return i;
            }

            Assert.Fail($"Live document '{propertyName}' not found.");
            return -1;
        }

        private void SelectDiskSave()
        {
            string saveRoot = SampleSaveMenu.Create(_root);
            _panel.SelectSessionForTests(SessionTypeIndex(typeof(SampleEditorSession)));
            _panel.ScanForTests(saveRoot);
            _panel.SelectSaveForTests(0);
        }

        private void SelectDiskDocument(string storageKey)
        {
            SelectDiskSave();
            _panel.SelectDocumentForTests(DocumentIndex(storageKey));
        }

        // ---- Filter predicate (pure) ----

        [Test]
        public void MatchesFilter_EmptyFilter_MatchesEverything()
        {
            Assert.That(PlayerDataViewerController.MatchesFilter(null, "SampleProfile", null), Is.True);
            Assert.That(PlayerDataViewerController.MatchesFilter(string.Empty, "SampleProfile", "SampleProfile"), Is.True);
        }

        [Test]
        public void MatchesFilter_CaseInsensitivePartial_MatchesNameOrTypeName()
        {
            Assert.That(PlayerDataViewerController.MatchesFilter("PROFILE", "SampleProfile", null), Is.True);
            Assert.That(PlayerDataViewerController.MatchesFilter("item", "items-v1", null), Is.True);
            Assert.That(PlayerDataViewerController.MatchesFilter("sampleitem", "items-v1", "SampleItem"), Is.True);
        }

        [Test]
        public void MatchesFilter_NoMatch_ReturnsFalse()
        {
            Assert.That(PlayerDataViewerController.MatchesFilter("zzz", "SampleProfile", "SampleProfile"), Is.False);
            Assert.That(PlayerDataViewerController.MatchesFilter("SampleItem", "mystery", null), Is.False);
        }

        // ---- Open-folder path resolution (pure) ----

        [Test]
        public void ResolveRevealPath_NoSaveSelected_ReturnsRootPath()
        {
            Assert.That(PlayerDataViewerController.ResolveRevealPath(_root, null), Is.EqualTo(_root));
        }

        [Test]
        public void ResolveRevealPath_SaveSelected_ReturnsSaveDirectory()
        {
            string saveRoot = SampleSaveMenu.Create(_root);
            _controller.Scan(saveRoot);
            Assert.That(_controller.Saves.Count, Is.EqualTo(2));

            string resolved = PlayerDataViewerController.ResolveRevealPath(saveRoot, _controller.Saves[1]);

            Assert.That(resolved, Is.EqualTo(_controller.Saves[1].Directory));
            Assert.That(resolved, Is.Not.EqualTo(saveRoot));
        }

        // ---- New elements ----

        [Test]
        public void BuildInto_CreatesSearchFieldAndOpenFolderButton()
        {
            Assert.That(SearchField, Is.Not.Null);
            Assert.That(SearchField.value, Is.EqualTo(string.Empty), "filter starts empty");
            Assert.That(OpenFolderButton, Is.Not.Null);
            Assert.That(DiskSection.Contains(OpenFolderButton), Is.True,
                "the button lives in the disk section so live mode hides it");
        }

        [Test]
        public void OpenFolderButton_LiveMode_IsHiddenWithDiskSection()
        {
            using IDisposable token = LiveSessionRegistry.Register("game", _session);

            _panel.SelectSourceForTests(1);

            Assert.That(DiskSection.style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(DiskSection.Contains(OpenFolderButton), Is.True);
        }

        // ---- Filter behavior ----

        [Test]
        public void Filter_DiskMode_FiltersByKeyAndTypeName_AndClearingRestores()
        {
            SelectDiskSave();
            Assert.That(DocumentsList.itemsSource.Count, Is.EqualTo(4));

            _panel.SetSearchFilterForTests("sampleitem");
            Assert.That(DocumentsList.itemsSource.Count, Is.EqualTo(1), "type name must match");
            Assert.That(((DocumentEntry)DocumentsList.itemsSource[0]).StorageKey, Is.EqualTo("items-v1"));

            _panel.SetSearchFilterForTests("MYSTERY");
            Assert.That(DocumentsList.itemsSource.Count, Is.EqualTo(1), "storage key must match case-insensitively");

            _panel.SetSearchFilterForTests("zzz");
            Assert.That(DocumentsList.itemsSource.Count, Is.EqualTo(0));

            _panel.SetSearchFilterForTests(string.Empty);
            Assert.That(DocumentsList.itemsSource.Count, Is.EqualTo(4));
        }

        [Test]
        public void Filter_DiskMode_SelectionSurvivesWhenStillVisible()
        {
            SelectDiskDocument("SampleProfile");

            _panel.SetSearchFilterForTests("profile");

            Assert.That(DocumentsList.itemsSource.Count, Is.EqualTo(1));
            Assert.That(DocumentsList.selectedIndex, Is.EqualTo(0));
            Assert.That(Info.text, Does.StartWith("SampleProfile"));
        }

        [Test]
        public void Filter_DiskMode_SelectedDocumentFilteredOut_ClearsEditSurface()
        {
            SelectDiskDocument("SampleProfile");
            Assert.That(Info.text, Does.StartWith("SampleProfile"));

            _panel.SetSearchFilterForTests("stats");

            Assert.That(DocumentsList.itemsSource.Count, Is.EqualTo(1));
            Assert.That(DocumentsList.selectedIndex, Is.EqualTo(-1));
            Assert.That(Info.text, Is.EqualTo(string.Empty));
            Assert.That(Json.value, Is.EqualTo(string.Empty));
            Assert.That(Json.isReadOnly, Is.True);
            Assert.That(ApplyButton.enabledSelf, Is.False);
            Assert.That(RevertButton.enabledSelf, Is.False);

            _panel.SetSearchFilterForTests(string.Empty);
            Assert.That(DocumentsList.itemsSource.Count, Is.EqualTo(4), "clearing the filter restores the list");
            Assert.That(DocumentsList.selectedIndex, Is.EqualTo(-1), "the cleared selection stays cleared");
        }

        [Test]
        public void Filter_LiveMode_FiltersDocuments_AndClearsFilteredOutSelection()
        {
            using IDisposable token = LiveSessionRegistry.Register("game", _session);
            _panel.SelectSourceForTests(1);
            Assert.That(LiveDocumentsList.itemsSource.Count, Is.EqualTo(3));
            _panel.SelectLiveDocumentForTests(LiveDocumentIndex("Stats"));
            Assert.That(Json.isReadOnly, Is.False);

            _panel.SetSearchFilterForTests("profile");

            Assert.That(LiveDocumentsList.itemsSource.Count, Is.EqualTo(1));
            Assert.That(((LiveDocumentDescriptor)LiveDocumentsList.itemsSource[0]).PropertyName, Is.EqualTo("SampleProfile"));
            Assert.That(LiveDocumentsList.selectedIndex, Is.EqualTo(-1));
            Assert.That(Info.text, Is.EqualTo(string.Empty));
            Assert.That(Json.value, Is.EqualTo(string.Empty));
            Assert.That(Json.isReadOnly, Is.True);

            _panel.SetSearchFilterForTests(string.Empty);
            Assert.That(LiveDocumentsList.itemsSource.Count, Is.EqualTo(3));
        }

        // ---- Unapplied-change indicator ----

        [Test]
        public void Indicator_JsonEdit_EnablesButtonsAndStar_RevertResets()
        {
            SelectDiskDocument("SampleProfile");
            _panel.SelectTabForTests(showFields: false);
            string loaded = Json.value;
            Assert.That(ApplyButton.enabledSelf, Is.False, "clean after selection");
            Assert.That(RevertButton.enabledSelf, Is.False, "clean after selection");
            Assert.That(Info.text, Does.Not.EndWith(" *"));

            _panel.SetJsonTextForTests(loaded + " ");

            Assert.That(ApplyButton.enabledSelf, Is.True);
            Assert.That(RevertButton.enabledSelf, Is.True);
            Assert.That(Info.text, Does.EndWith(" *"));

            _panel.RevertForTests();

            Assert.That(Json.value, Is.EqualTo(loaded));
            Assert.That(ApplyButton.enabledSelf, Is.False);
            Assert.That(RevertButton.enabledSelf, Is.False);
            Assert.That(Info.text, Does.Not.EndWith(" *"));
        }

        [Test]
        public void Indicator_FieldsEdit_EnablesButtonsAndStar_RevertResets()
        {
            SelectDiskDocument("SampleProfile");
            Assert.That(ApplyButton.enabledSelf, Is.False);
            Assert.That(RevertButton.enabledSelf, Is.False);
            Assert.That(Info.text, Does.Not.EndWith(" *"));

            _panel.FieldsViewForTests.SetTextForTests("Name", "renamed");

            Assert.That(ApplyButton.enabledSelf, Is.True);
            Assert.That(RevertButton.enabledSelf, Is.True);
            Assert.That(Info.text, Does.EndWith(" *"));

            _panel.RevertForTests();

            Assert.That(ApplyButton.enabledSelf, Is.False);
            Assert.That(RevertButton.enabledSelf, Is.False);
            Assert.That(Info.text, Does.Not.EndWith(" *"));
        }

        [Test]
        public void Indicator_InvalidFieldInput_KeepsApplyBlockedButAllowsRevert()
        {
            SelectDiskDocument("SampleProfile");

            _panel.FieldsViewForTests.SetTextForTests("Level", "abc");

            Assert.That(ApplyButton.enabledSelf, Is.False, "invalid input must keep Apply blocked");
            Assert.That(RevertButton.enabledSelf, Is.True, "Revert is the way out of invalid input");
            Assert.That(Info.text, Does.EndWith(" *"));
        }

        [Test]
        public void Indicator_LiveDocument_JsonEditAndRevert()
        {
            using IDisposable token = LiveSessionRegistry.Register("game", _session);
            _panel.SelectSourceForTests(1);
            _panel.SelectLiveDocumentForTests(LiveDocumentIndex("Stats"));
            _panel.SelectTabForTests(showFields: false);
            string loaded = Json.value;
            Assert.That(ApplyButton.enabledSelf, Is.False);
            Assert.That(RevertButton.enabledSelf, Is.False);

            _panel.SetJsonTextForTests(loaded + " ");
            Assert.That(ApplyButton.enabledSelf, Is.True);
            Assert.That(RevertButton.enabledSelf, Is.True);
            Assert.That(Info.text, Does.EndWith(" *"));

            _panel.RevertForTests();
            Assert.That(Json.value, Is.EqualTo(loaded));
            Assert.That(ApplyButton.enabledSelf, Is.False);
            Assert.That(Info.text, Does.Not.EndWith(" *"));
        }
    }
}
