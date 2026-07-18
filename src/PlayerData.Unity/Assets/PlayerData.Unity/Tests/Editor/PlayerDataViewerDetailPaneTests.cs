using System;
using System.Collections.Generic;
using System.IO;
using MemoryPack;
using NUnit.Framework;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace PlayerData.Unity.Editor.Tests
{
    public sealed class PlayerDataViewerDetailPaneTests
    {
        private string _root;
        private PlayerDataViewerController _controller;
        private VisualElement _rootElement;
        private ViewerPanel _panel;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "PlayerDataEditorTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _controller = new PlayerDataViewerController();
            _rootElement = new VisualElement();
            _panel = ViewerUI.BuildInto(_rootElement, _controller, _root);
        }

        [TearDown]
        public void TearDown()
        {
            _panel.Dispose();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        // ---- element access ----

        private VisualElement DetailContent => _rootElement.Q<VisualElement>(ViewerUI.DetailContentName);
        private Label Header => _rootElement.Q<Label>(ViewerUI.DetailHeaderName);
        private Label StateLabel => _rootElement.Q<Label>(ViewerUI.DetailStateLabelName);
        private Label PathLabel => _rootElement.Q<Label>(ViewerUI.DetailPathLabelName);
        private ToolbarToggle FieldsToggle => _rootElement.Q<ToolbarToggle>(ViewerUI.FieldsToggleName);
        private ToolbarToggle JsonToggle => _rootElement.Q<ToolbarToggle>(ViewerUI.JsonToggleName);
        private TextField JsonField => _rootElement.Q<TextField>(ViewerUI.JsonTextFieldName);
        private Button ApplyButton => _rootElement.Q<Button>(ViewerUI.ApplyButtonName);
        private Button RevertButton => _rootElement.Q<Button>(ViewerUI.RevertButtonName);
        private HelpBox ErrorBox => _rootElement.Q<HelpBox>(ViewerUI.ErrorBoxName);

        private static bool IsShown(VisualElement element) => element.style.display.value == DisplayStyle.Flex;

        // ---- fixtures ----

        private string ShowSampleSaves()
        {
            string saveRoot = SampleSaveMenu.Create(_root);
            SelectSampleSession();
            _panel.SetRootForTests(saveRoot);
            return saveRoot;
        }

        // SampleEditorSession is a test fixture and is filtered out of the dropdown, so it is
        // selected directly rather than by dropdown index.
        private void SelectSampleSession() => _panel.SelectSessionByTypeForTests(typeof(SampleEditorSession));

        private static void Flatten(IEnumerable<TreeViewItemData<SaveTreeNode>> items, List<SaveTreeNode> into)
        {
            foreach (TreeViewItemData<SaveTreeNode> item in items)
            {
                into.Add(item.data);
                Flatten(item.children, into);
            }
        }

        private List<SaveTreeNode> AllNodes()
        {
            List<SaveTreeNode> nodes = new List<SaveTreeNode>();
            Flatten(_panel.VisibleTreeItemsForTests, nodes);
            return nodes;
        }

        private SaveTreeNode FindDocument(string displayNamePrefix)
        {
            foreach (SaveTreeNode node in AllNodes())
            {
                if (node.Kind == SaveTreeNodeKind.Document
                    && node.DisplayName.StartsWith(displayNamePrefix, StringComparison.Ordinal))
                {
                    return node;
                }
            }

            Assert.Fail($"No document node starting with '{displayNamePrefix}' in the tree.");
            throw new InvalidOperationException("unreachable");
        }

        private SaveTreeNode FindGroup()
        {
            foreach (SaveTreeNode node in AllNodes())
            {
                if (node.Kind == SaveTreeNodeKind.Group)
                    return node;
            }

            Assert.Fail("No group node in the tree.");
            throw new InvalidOperationException("unreachable");
        }

        private SaveTreeNode SelectProfileInFieldsView()
        {
            SaveTreeNode node = FindDocument("SampleProfile");
            _panel.SelectTreeNodeForTests(node);
            _panel.SetViewModeForTests(json: false);
            return node;
        }

        private static void WriteBundle(string directory, int formatVersion, Dictionary<string, byte[]> documents)
        {
            new DirectorySaveBackend(directory)
                .WriteAsync(new SaveBundle(formatVersion, documents))
                .AsTask().GetAwaiter().GetResult();
        }

        private static SampleProfile ReadProfileFromDisk(SaveLocation location)
        {
            SessionSchema schema = SessionSchemaResolver.Resolve(typeof(SampleEditorSession));
            LoadedSave? save = SaveDataStore.TryLoad(location, schema, out string? error);
            Assert.That(save, Is.Not.Null, error);
            foreach (DocumentEntry entry in save!.Documents)
            {
                if (entry.StorageKey == "SampleProfile")
                    return MemoryPackSerializer.Deserialize<SampleProfile>(entry.Bytes)!;
            }

            Assert.Fail("SampleProfile document not found on disk.");
            throw new InvalidOperationException("unreachable");
        }

        private static System.Collections.Concurrent.ConcurrentDictionary<string, SampleItem> ReadItemsFromDisk(SaveLocation location)
        {
            SessionSchema schema = SessionSchemaResolver.Resolve(typeof(SampleEditorSession));
            LoadedSave? save = SaveDataStore.TryLoad(location, schema, out string? error);
            Assert.That(save, Is.Not.Null, error);
            foreach (DocumentEntry entry in save!.Documents)
            {
                if (entry.StorageKey == "items-v1")
                {
                    return MemoryPackSerializer.Deserialize<System.Collections.Concurrent.ConcurrentDictionary<string, SampleItem>>(entry.Bytes)!;
                }
            }

            Assert.Fail("Items document not found on disk.");
            throw new InvalidOperationException("unreachable");
        }

        // ---- header / selection ----

        [Test]
        public void SelectDocumentLeaf_ShowsHeaderStateAndPath()
        {
            ShowSampleSaves();
            SaveTreeNode node = FindDocument("SampleProfile");

            _panel.SelectTreeNodeForTests(node);

            Assert.That(IsShown(DetailContent), Is.True);
            Assert.That(Header.text, Is.EqualTo("SampleProfile"));
            Assert.That(StateLabel.text, Is.EqualTo(ViewerDisplayNames.StateLabel(DocumentState.Editable)));
            Assert.That(PathLabel.text, Is.EqualTo(node.Location!.Directory));
            // Editable, but untouched: both buttons wait for an actual change.
            Assert.That(ApplyButton.enabledSelf, Is.False);
            Assert.That(RevertButton.enabledSelf, Is.False);
        }

        [Test]
        public void ApplyAndRevert_EnableOnEdit_AndDisableAgainAfterRevert()
        {
            ShowSampleSaves();
            _panel.SelectTreeNodeForTests(FindDocument("SampleProfile"));
            _panel.SetViewModeForTests(json: true);
            Assert.That(ApplyButton.enabledSelf, Is.False);
            Assert.That(RevertButton.enabledSelf, Is.False);

            _panel.SetJsonTextForTests(JsonField.value.Replace("\"Level\": 5", "\"Level\": 9"));
            Assert.That(ApplyButton.enabledSelf, Is.True);
            Assert.That(RevertButton.enabledSelf, Is.True);

            _panel.RevertForTests();
            Assert.That(ApplyButton.enabledSelf, Is.False);
            Assert.That(RevertButton.enabledSelf, Is.False);
        }

        [Test]
        public void Refresh_KeepsSelectedDocumentShown()
        {
            ShowSampleSaves();
            SaveTreeNode node = FindDocument("SampleProfile");
            _panel.SelectTreeNodeForTests(node);
            Assert.That(IsShown(DetailContent), Is.True);

            _panel.RefreshForTests();

            Assert.That(IsShown(DetailContent), Is.True, "Refresh must keep the selected document shown.");
            Assert.That(Header.text, Is.EqualTo("SampleProfile"));
            Assert.That(PathLabel.text, Is.EqualTo(node.Location!.Directory));
        }

        [Test]
        public void SelectGroupNodeOrClearSelection_EmptiesDetailPane()
        {
            ShowSampleSaves();
            _panel.SelectTreeNodeForTests(FindDocument("SampleProfile"));
            Assert.That(IsShown(DetailContent), Is.True);

            _panel.SelectTreeNodeForTests(FindGroup());
            Assert.That(IsShown(DetailContent), Is.False);
            Assert.That(ApplyButton.enabledSelf, Is.False);

            _panel.SelectTreeNodeForTests(FindDocument("SampleProfile"));
            _panel.SelectTreeNodeForTests(null);
            Assert.That(IsShown(DetailContent), Is.False);
        }

        // ---- shared working copy across view switches ----

        [Test]
        public void FieldsEdit_SwitchToJsonView_KeepsTheEditedValue()
        {
            ShowSampleSaves();
            SelectProfileInFieldsView();

            _panel.FieldsViewForTests!.SetTextForTests("Name", "renamed-in-fields");
            _panel.SetViewModeForTests(json: true);

            Assert.That(_panel.IsJsonViewActiveForTests, Is.True);
            Assert.That(JsonField.value, Does.Contain("renamed-in-fields"));
        }

        [Test]
        public void JsonEdit_SwitchToFieldsView_KeepsTheEditedValue()
        {
            ShowSampleSaves();
            SaveTreeNode node = FindDocument("SampleProfile");
            _panel.SelectTreeNodeForTests(node);
            _panel.SetViewModeForTests(json: true);

            _panel.SetJsonTextForTests(JsonField.value.Replace("hero", "edited-in-json"));
            _panel.SetViewModeForTests(json: false);

            Assert.That(_panel.IsJsonViewActiveForTests, Is.False);
            Assert.That(_panel.FieldsModelForTests!.ToJson(), Does.Contain("edited-in-json"));
        }

        [Test]
        public void SwitchToFields_WithInvalidJson_StaysOnJsonViewAndShowsError()
        {
            ShowSampleSaves();
            _panel.SelectTreeNodeForTests(FindDocument("SampleProfile"));
            _panel.SetViewModeForTests(json: true);

            _panel.SetJsonTextForTests("{ this is not json");
            _panel.SetViewModeForTests(json: false);

            Assert.That(_panel.IsJsonViewActiveForTests, Is.True, "must stay on JSON, not silently fall back");
            Assert.That(_panel.FieldsModelForTests, Is.Null);
            Assert.That(IsShown(ErrorBox), Is.True);
            Assert.That(ErrorBox.text, Does.StartWith("JSON error"));
        }

        // ---- Apply / Revert ----

        [Test]
        public void Apply_FromFieldsView_WritesToDisk_AndPaneShowsPersistedState()
        {
            ShowSampleSaves();
            SaveTreeNode node = SelectProfileInFieldsView();

            _panel.FieldsViewForTests!.SetTextForTests("Level", "99");
            _panel.ApplyForTests();

            Assert.That(IsShown(ErrorBox), Is.False, ErrorBox.text);
            Assert.That(ReadProfileFromDisk(node.Location!).Level, Is.EqualTo(99));
            // The pane reloaded the persisted document into a fresh working copy.
            Assert.That(_panel.FieldsModelForTests!.ToJson(), Does.Contain("99"));
            Assert.That(_panel.FieldsModelForTests.IsDirty, Is.False);
        }

        [Test]
        public void Apply_FromJsonView_WritesToDisk()
        {
            ShowSampleSaves();
            SaveTreeNode node = FindDocument("SampleProfile");
            _panel.SelectTreeNodeForTests(node);
            _panel.SetViewModeForTests(json: true);

            _panel.SetJsonTextForTests(JsonField.value.Replace("hero", "json-applied"));
            _panel.ApplyForTests();

            Assert.That(IsShown(ErrorBox), Is.False, ErrorBox.text);
            Assert.That(ReadProfileFromDisk(node.Location!).Name, Is.EqualTo("json-applied"));
        }

        [Test]
        public void Revert_DiscardsUnappliedEdits_AndReloadsFromDisk()
        {
            ShowSampleSaves();
            SaveTreeNode node = SelectProfileInFieldsView();

            _panel.FieldsViewForTests!.SetTextForTests("Name", "temp-name");
            _panel.RevertForTests();

            string json = _panel.FieldsModelForTests!.ToJson();
            Assert.That(json, Does.Contain("hero"));
            Assert.That(json, Does.Not.Contain("temp-name"));
            Assert.That(ReadProfileFromDisk(node.Location!).Name, Is.EqualTo("hero"));
        }

        [Test]
        public void SwitchingDocumentSelection_DiscardsUnappliedEdits_ByDesign()
        {
            // Deliberate rule: no confirmation dialogs exist in this UI, so changing the tree
            // selection silently drops unapplied edits of the previous document.
            ShowSampleSaves();
            SelectProfileInFieldsView();
            _panel.FieldsViewForTests!.SetTextForTests("Name", "lost-on-switch");

            _panel.SelectTreeNodeForTests(FindDocument("Items"));
            SelectProfileInFieldsView();

            string json = _panel.FieldsModelForTests!.ToJson();
            Assert.That(json, Does.Contain("hero"));
            Assert.That(json, Does.Not.Contain("lost-on-switch"));
        }

        [Test]
        public void InvalidNumericFieldValue_DisablesApply_UntilFixed()
        {
            ShowSampleSaves();
            SelectProfileInFieldsView();

            _panel.FieldsViewForTests!.SetTextForTests("Level", "not-a-number");
            Assert.That(ApplyButton.enabledSelf, Is.False);

            _panel.FieldsViewForTests.SetTextForTests("Level", "7");
            Assert.That(ApplyButton.enabledSelf, Is.True);
        }

        // ---- DocumentState gating ----

        [Test]
        public void UnknownKeyAndUnreadableDocuments_AreReadOnly_WithApplyDisabled()
        {
            ShowSampleSaves();

            _panel.SelectTreeNodeForTests(FindDocument("mystery"));
            Assert.That(StateLabel.text, Is.EqualTo(ViewerDisplayNames.StateLabel(DocumentState.UnknownKey)));
            Assert.That(ApplyButton.enabledSelf, Is.False);
            Assert.That(RevertButton.enabledSelf, Is.False);
            Assert.That(JsonField.isReadOnly, Is.True);
            Assert.That(FieldsToggle.enabledSelf, Is.False);
            Assert.That(FieldsToggle.tooltip, Is.Not.Empty, "disabled Fields toggle must carry a reason");
            Assert.That(_panel.IsJsonViewActiveForTests, Is.True);

            _panel.SelectTreeNodeForTests(FindDocument("Stats"));
            Assert.That(StateLabel.text, Is.EqualTo(ViewerDisplayNames.StateLabel(DocumentState.Unreadable)));
            Assert.That(ApplyButton.enabledSelf, Is.False);
            Assert.That(JsonField.isReadOnly, Is.True);
            Assert.That(FieldsToggle.enabledSelf, Is.False);
            Assert.That(FieldsToggle.tooltip, Is.Not.Empty);
        }

        [Test]
        public void OldFormatVersionSave_IsReadOnly_FieldEditorsDisabled_ApplyDisabled()
        {
            string dir = Path.Combine(_root, "old-format");
            WriteBundle(dir, SaveSession.CurrentFormatVersion + 1, new Dictionary<string, byte[]>
            {
                ["SampleProfile"] = MemoryPackSerializer.Serialize(new SampleProfile { Name = "old", Level = 1 }),
            });
            SelectSampleSession();
            _panel.SetRootForTests(dir);

            _panel.SelectTreeNodeForTests(FindDocument("SampleProfile"));

            Assert.That(StateLabel.text, Is.EqualTo(ViewerDisplayNames.StateLabel(DocumentState.ReadOnlyFormatVersion)));
            Assert.That(ApplyButton.enabledSelf, Is.False);
            Assert.That(JsonField.isReadOnly, Is.True);
            // Round-trippable, so the Fields toggle stays usable; the editors themselves are disabled.
            Assert.That(FieldsToggle.enabledSelf, Is.True);
            _panel.SetViewModeForTests(json: false);
            Assert.That(_panel.FieldsViewForTests, Is.Not.Null);
            Assert.That(_rootElement.Q<VisualElement>(ViewerUI.FieldsSectionName).enabledSelf, Is.False);
        }

        [Test]
        public void NonRoundTrippableDocument_DisablesFieldsToggle_JsonViewReadOnly()
        {
            string dir = Path.Combine(_root, "wide");
            WriteBundle(dir, SaveSession.CurrentFormatVersion, new Dictionary<string, byte[]>
            {
                ["Wide"] = MemoryPackSerializer.Serialize(new SampleWideDocV2 { A = 1, B = 2, C = 3 }),
            });
            _controller.SelectSession(typeof(SessionWithWideDoc));
            _panel.SetRootForTests(dir);

            _panel.SelectTreeNodeForTests(FindDocument("Wide"));

            Assert.That(StateLabel.text, Is.EqualTo(ViewerDisplayNames.StateLabel(DocumentState.ReadOnlyRoundTrip)));
            Assert.That(FieldsToggle.enabledSelf, Is.False);
            Assert.That(FieldsToggle.tooltip, Is.Not.Empty);
            Assert.That(_panel.IsJsonViewActiveForTests, Is.True);
            Assert.That(JsonField.isReadOnly, Is.True);
            Assert.That(ApplyButton.enabledSelf, Is.False);

            // The toggle gate holds even when a switch to Fields is forced.
            _panel.SetViewModeForTests(json: false);
            Assert.That(_panel.IsJsonViewActiveForTests, Is.True);
        }

        // ---- collections ----

        [Test]
        public void CollectionDocument_FieldsView_ShowsOneSubFormPerEntry()
        {
            ShowSampleSaves();
            _panel.SelectTreeNodeForTests(FindDocument("Items"));
            _panel.SetViewModeForTests(json: false);

            CollectionFieldsEditor collection = _panel.CollectionFieldsForTests;
            Assert.That(collection, Is.Not.Null, "collection documents get one field sub-form per entry");
            Assert.That(collection!.EntryKeysForTests, Has.Member("potion"));
            Assert.That(collection.EntryKeysForTests, Has.Member("sword"));
            // Each entry exposes the entity's own members through the same editor kinds.
            Assert.That(collection.EntryModelForTests("potion").Rows.Count, Is.EqualTo(2));
            // Untouched, so Apply waits for an actual edit.
            Assert.That(ApplyButton.enabledSelf, Is.False);
        }

        [Test]
        public void CollectionDocument_FieldsEdit_Apply_WritesTheEntryToDisk()
        {
            ShowSampleSaves();
            SaveTreeNode node = FindDocument("Items");
            _panel.SelectTreeNodeForTests(node);
            _panel.SetViewModeForTests(json: false);

            _panel.CollectionFieldsForTests!.EntryViewForTests("potion").SetTextForTests("Count", "42");
            Assert.That(ApplyButton.enabledSelf, Is.True);
            _panel.ApplyForTests();

            Assert.That(IsShown(ErrorBox), Is.False, ErrorBox.text);
            System.Collections.Concurrent.ConcurrentDictionary<string, SampleItem> items = ReadItemsFromDisk(node.Location!);
            Assert.That(items["potion"].Count, Is.EqualTo(42));
            Assert.That(items["sword"].Count, Is.EqualTo(1), "untouched entry must be preserved");
        }

        [Test]
        public void CollectionDocument_FieldsEdit_SwitchToJsonView_KeepsTheEditedEntry()
        {
            ShowSampleSaves();
            _panel.SelectTreeNodeForTests(FindDocument("Items"));
            _panel.SetViewModeForTests(json: false);

            _panel.CollectionFieldsForTests!.EntryViewForTests("sword").SetTextForTests("Count", "7");
            _panel.SetViewModeForTests(json: true);

            Assert.That(_panel.IsJsonViewActiveForTests, Is.True);
            Assert.That(JsonField.isReadOnly, Is.False);
            Assert.That(JsonField.value, Does.Contain("\"Count\": 7"));
        }

        [Test]
        public void CollectionDocument_FieldsAddEntry_Apply_WritesNewEntryToDisk()
        {
            ShowSampleSaves();
            SaveTreeNode node = FindDocument("Items");
            _panel.SelectTreeNodeForTests(node);
            _panel.SetViewModeForTests(json: false);

            _panel.CollectionFieldsForTests!.AddEntryForTests();
            Assert.That(ApplyButton.enabledSelf, Is.True);
            _panel.ApplyForTests();

            Assert.That(IsShown(ErrorBox), Is.False, ErrorBox.text);
            System.Collections.Concurrent.ConcurrentDictionary<string, SampleItem> items = ReadItemsFromDisk(node.Location!);
            Assert.That(items.Count, Is.EqualTo(3));
            Assert.That(items.ContainsKey("newEntry"), Is.True);
            Assert.That(items.ContainsKey("potion"), Is.True, "existing entries must be preserved");
        }

        [Test]
        public void CollectionDocument_FieldsRemoveEntry_Apply_DeletesFromDisk()
        {
            ShowSampleSaves();
            SaveTreeNode node = FindDocument("Items");
            _panel.SelectTreeNodeForTests(node);
            _panel.SetViewModeForTests(json: false);

            _panel.CollectionFieldsForTests!.RemoveEntryForTests("sword");
            Assert.That(ApplyButton.enabledSelf, Is.True);
            _panel.ApplyForTests();

            Assert.That(IsShown(ErrorBox), Is.False, ErrorBox.text);
            System.Collections.Concurrent.ConcurrentDictionary<string, SampleItem> items = ReadItemsFromDisk(node.Location!);
            Assert.That(items.Count, Is.EqualTo(1));
            Assert.That(items.ContainsKey("sword"), Is.False);
            Assert.That(items.ContainsKey("potion"), Is.True);
        }

        [Test]
        public void CollectionDocument_DuplicateKey_BlocksApply()
        {
            ShowSampleSaves();
            _panel.SelectTreeNodeForTests(FindDocument("Items"));
            _panel.SetViewModeForTests(json: false);

            // Rename sword's key member onto potion's key.
            _panel.CollectionFieldsForTests!.EntryViewForTests("sword").SetTextForTests("ItemId", "potion");

            Assert.That(ApplyButton.enabledSelf, Is.False, "a duplicate key must block Apply");
        }

        // ---- viewer-wide view mode preference ----

        [Test]
        public void ViewModePreference_IsViewerWide_AndAppliesAcrossSelections()
        {
            ShowSampleSaves();

            _panel.SetViewModeForTests(json: true);
            _panel.SelectTreeNodeForTests(FindDocument("SampleProfile"));
            Assert.That(_panel.IsJsonViewActiveForTests, Is.True);
            Assert.That(JsonToggle.value, Is.True);
            _panel.SelectTreeNodeForTests(FindDocument("Items"));
            Assert.That(_panel.IsJsonViewActiveForTests, Is.True);

            _panel.SetViewModeForTests(json: false);
            _panel.SelectTreeNodeForTests(FindDocument("SampleProfile"));
            Assert.That(_panel.IsJsonViewActiveForTests, Is.False);
            Assert.That(FieldsToggle.value, Is.True);
        }
    }
}
