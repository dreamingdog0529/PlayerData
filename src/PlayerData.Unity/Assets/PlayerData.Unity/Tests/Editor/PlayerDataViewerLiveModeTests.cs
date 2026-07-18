using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace PlayerData.Unity.Editor.Tests
{
    // Live sessions in the two-pane viewer: registering makes a "Playing now" group appear in
    // the tree, live document leaves open the same detail pane, Apply flows through
    // LiveSessionView into the running session, and unregistering clears both tree and pane.
    public sealed class PlayerDataViewerLiveModeTests
    {
        private string _root;
        private SampleEditorSession _session;
        private VisualElement _rootElement;
        private ViewerPanel _panel;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "PlayerDataEditorTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            LiveSessionRegistry.ClearForTests();
            _session = new SampleEditorSession(new DirectorySaveBackend(_root));
            _rootElement = new VisualElement();
            _panel = ViewerUI.BuildInto(_rootElement, new PlayerDataViewerController(), _root);
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

        // ---- element access ----

        private VisualElement DetailContent => _rootElement.Q<VisualElement>(ViewerUI.DetailContentName);
        private Label Header => _rootElement.Q<Label>(ViewerUI.DetailHeaderName);
        private Label StateLabel => _rootElement.Q<Label>(ViewerUI.DetailStateLabelName);
        private Label PathLabel => _rootElement.Q<Label>(ViewerUI.DetailPathLabelName);
        private ToolbarToggle FieldsToggle => _rootElement.Q<ToolbarToggle>(ViewerUI.FieldsToggleName);
        private TextField JsonField => _rootElement.Q<TextField>(ViewerUI.JsonTextFieldName);
        private Button ApplyButton => _rootElement.Q<Button>(ViewerUI.ApplyButtonName);
        private HelpBox ErrorBox => _rootElement.Q<HelpBox>(ViewerUI.ErrorBoxName);

        private static bool IsShown(VisualElement element) => element.style.display.value == DisplayStyle.Flex;

        // ---- tree access ----

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

        private SaveTreeNode? TryFindNode(SaveTreeNodeKind kind, string displayName)
        {
            foreach (SaveTreeNode node in AllNodes())
            {
                if (node.Kind == kind && node.DisplayName == displayName)
                    return node;
            }

            return null;
        }

        private SaveTreeNode FindNode(SaveTreeNodeKind kind, string displayName)
        {
            SaveTreeNode? node = TryFindNode(kind, displayName);
            Assert.That(node, Is.Not.Null, $"No {kind} node named '{displayName}' in the tree.");
            return node!;
        }

        private SaveTreeNode SelectLiveDocument(string propertyName)
        {
            SaveTreeNode node = FindNode(SaveTreeNodeKind.LiveDocument, propertyName);
            _panel.SelectTreeNodeForTests(node);
            return node;
        }

        // ---- tree integration ----

        [Test]
        public void Register_ShowsPlayingNowGroup_WithSessionAndDocumentLeaves()
        {
            Assert.That(TryFindNode(SaveTreeNodeKind.Group, SaveTreeModel.PlayingNowLabel), Is.Null);

            using (LiveSessionRegistry.Register("game", _session))
            {
                FindNode(SaveTreeNodeKind.Group, SaveTreeModel.PlayingNowLabel);
                FindNode(SaveTreeNodeKind.LiveSession, "game");
                FindNode(SaveTreeNodeKind.LiveDocument, "SampleProfile");
                FindNode(SaveTreeNodeKind.LiveDocument, "Stats");
                FindNode(SaveTreeNodeKind.LiveDocument, "Items");
            }
        }

        [Test]
        public void Register_DuplicateNames_GetIndexSuffixes()
        {
            using (LiveSessionRegistry.Register("game", _session))
            using (LiveSessionRegistry.Register("game", _session))
            {
                FindNode(SaveTreeNodeKind.LiveSession, "game (1)");
                FindNode(SaveTreeNodeKind.LiveSession, "game (2)");
            }
        }

        [Test]
        public void Unregister_RemovesGroup_AndClearsDetailWithoutThrowing()
        {
            IDisposable token = LiveSessionRegistry.Register("game", _session);
            SelectLiveDocument("Stats");
            Assert.That(IsShown(DetailContent), Is.True);

            token.Dispose();

            Assert.That(TryFindNode(SaveTreeNodeKind.Group, SaveTreeModel.PlayingNowLabel), Is.Null);
            Assert.That(IsShown(DetailContent), Is.False);
            Assert.That(ApplyButton.enabledSelf, Is.False);
        }

        // ---- detail pane ----

        [Test]
        public void SelectLiveDocument_ShowsHeaderStateAndValues()
        {
            using (LiveSessionRegistry.Register("game", _session))
            {
                _session.Stats.Replace(new SampleStats { Hp = 12 });
                SelectLiveDocument("Stats");
                _panel.SetViewModeForTests(json: true);

                Assert.That(IsShown(DetailContent), Is.True);
                Assert.That(Header.text, Is.EqualTo("Stats (game)"));
                Assert.That(StateLabel.text, Is.EqualTo(ViewerDisplayNames.StateLabel(DocumentState.Editable)));
                Assert.That(PathLabel.text, Is.EqualTo(SaveTreeModel.PlayingNowLabel));
                Assert.That(JsonField.value, Does.Contain("\"Hp\": 12"));
                Assert.That(JsonField.isReadOnly, Is.False);
                Assert.That(ApplyButton.enabledSelf, Is.False, "untouched document is not applyable");
            }
        }

        [Test]
        public void JsonEdit_Apply_UpdatesRunningSession_AndPaneShowsLiveState()
        {
            using (LiveSessionRegistry.Register("game", _session))
            {
                SelectLiveDocument("Stats");
                _panel.SetViewModeForTests(json: true);

                _panel.SetJsonTextForTests(JsonField.value.Replace("\"Hp\": 0", "\"Hp\": 42"));
                _panel.ApplyForTests();

                Assert.That(IsShown(ErrorBox), Is.False, ErrorBox.text);
                Assert.That(_session.Stats.Value.Hp, Is.EqualTo(42));
                // The pane repopulated from the live snapshot after Apply.
                Assert.That(JsonField.value, Does.Contain("\"Hp\": 42"));
            }
        }

        [Test]
        public void FieldsEdit_Apply_UpdatesRunningSession()
        {
            using (LiveSessionRegistry.Register("game", _session))
            {
                SelectLiveDocument("Stats");
                _panel.SetViewModeForTests(json: false);
                Assert.That(_panel.FieldsViewForTests, Is.Not.Null);

                _panel.FieldsViewForTests!.SetTextForTests("Hp", "55");
                _panel.ApplyForTests();

                Assert.That(IsShown(ErrorBox), Is.False, ErrorBox.text);
                Assert.That(_session.Stats.Value.Hp, Is.EqualTo(55));
            }
        }

        [Test]
        public void Revert_RereadsTheLiveSnapshot()
        {
            using (LiveSessionRegistry.Register("game", _session))
            {
                SelectLiveDocument("Stats");
                _panel.SetViewModeForTests(json: true);
                _panel.SetJsonTextForTests("{ \"Hp\": 999 }");

                _session.Stats.Replace(new SampleStats { Hp = 5 });
                _panel.RevertForTests();

                Assert.That(JsonField.value, Does.Contain("\"Hp\": 5"));
                Assert.That(JsonField.value, Does.Not.Contain("999"));
                Assert.That(_session.Stats.Value.Hp, Is.EqualTo(5));
            }
        }

        // ---- collections ----

        [Test]
        public void LiveCollection_FieldsView_ShowsOneSubFormPerEntry()
        {
            using (LiveSessionRegistry.Register("game", _session))
            {
                _session.Items.Upsert(new SampleItem { ItemId = "potion", Count = 3 });
                SelectLiveDocument("Items");
                _panel.SetViewModeForTests(json: false);

                Assert.That(FieldsToggle.enabledSelf, Is.True);
                CollectionFieldsEditor collection = _panel.CollectionFieldsForTests;
                Assert.That(collection, Is.Not.Null, "live collections get one field sub-form per entry");
                Assert.That(collection!.EntryKeysForTests, Has.Member("potion"));
            }
        }

        [Test]
        public void LiveCollection_FieldsEdit_Apply_UpdatesRunningSession()
        {
            using (LiveSessionRegistry.Register("game", _session))
            {
                _session.Items.Upsert(new SampleItem { ItemId = "potion", Count = 3 });
                SelectLiveDocument("Items");
                _panel.SetViewModeForTests(json: false);

                _panel.CollectionFieldsForTests!.EntryViewForTests("potion").SetTextForTests("Count", "9");
                _panel.ApplyForTests();

                Assert.That(IsShown(ErrorBox), Is.False, ErrorBox.text);
                Assert.That(_session.Items.Snapshot["potion"].Count, Is.EqualTo(9));
            }
        }

        [Test]
        public void LiveCollection_JsonEdit_Apply_UpsertsAndUpdatesEntries()
        {
            using (LiveSessionRegistry.Register("game", _session))
            {
                _session.Items.Upsert(new SampleItem { ItemId = "potion", Count = 3 });
                SelectLiveDocument("Items");
                _panel.SetViewModeForTests(json: true);
                Assert.That(JsonField.value, Does.Contain("potion"));

                _panel.SetJsonTextForTests(
                    "{ \"potion\": { \"ItemId\": \"potion\", \"Count\": 9 }," +
                    " \"elixir\": { \"ItemId\": \"elixir\", \"Count\": 1 } }");
                _panel.ApplyForTests();

                Assert.That(IsShown(ErrorBox), Is.False, ErrorBox.text);
                Assert.That(_session.Items.Snapshot["potion"].Count, Is.EqualTo(9));
                Assert.That(_session.Items.Snapshot["elixir"].Count, Is.EqualTo(1));
                // Round trip: the pane re-read the applied snapshot.
                Assert.That(JsonField.value, Does.Contain("elixir"));
            }
        }

        [Test]
        public void LiveCollection_JsonEdit_Apply_RemovesEntriesMissingFromJson()
        {
            using (LiveSessionRegistry.Register("game", _session))
            {
                _session.Items.Upsert(new SampleItem { ItemId = "potion", Count = 3 });
                _session.Items.Upsert(new SampleItem { ItemId = "elixir", Count = 1 });
                SelectLiveDocument("Items");
                _panel.SetViewModeForTests(json: true);

                _panel.SetJsonTextForTests("{ \"potion\": { \"ItemId\": \"potion\", \"Count\": 3 } }");
                _panel.ApplyForTests();

                Assert.That(IsShown(ErrorBox), Is.False, ErrorBox.text);
                Assert.That(_session.Items.Contains("elixir"), Is.False);
                Assert.That(_session.Items.Count, Is.EqualTo(1));
            }
        }

        [Test]
        public void LiveCollection_JsonKeyDisagreeingWithPayloadKey_ErrorsAndApplyFails()
        {
            using (LiveSessionRegistry.Register("game", _session))
            {
                SelectLiveDocument("Items");
                _panel.SetViewModeForTests(json: true);

                _panel.SetJsonTextForTests("{ \"potion\": { \"ItemId\": \"elixir\", \"Count\": 1 } }");
                _panel.ApplyForTests();

                Assert.That(IsShown(ErrorBox), Is.True);
                Assert.That(_session.Items.Count, Is.EqualTo(0));
            }
        }
    }
}
