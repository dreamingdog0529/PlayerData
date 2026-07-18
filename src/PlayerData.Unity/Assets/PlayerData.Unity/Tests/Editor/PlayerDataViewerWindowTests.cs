using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MemoryPack;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace PlayerData.Unity.Editor.Tests
{
    public sealed class PlayerDataViewerWindowTests
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

        private TreeView Tree => _rootElement.Q<TreeView>(ViewerUI.TreeViewName);

        private void SelectSampleSession()
        {
            for (int i = 0; i < _controller.SessionTypes.Count; i++)
            {
                if (_controller.SessionTypes[i] == typeof(SampleEditorSession))
                {
                    _panel.SelectSessionForTests(i);
                    return;
                }
            }

            Assert.Fail($"Session type '{nameof(SampleEditorSession)}' not found.");
        }

        private static void Flatten(IEnumerable<TreeViewItemData<SaveTreeNode>> items, List<SaveTreeNode> into)
        {
            foreach (TreeViewItemData<SaveTreeNode> item in items)
            {
                into.Add(item.data);
                Flatten(item.children, into);
            }
        }

        private List<string> VisibleNames()
        {
            List<SaveTreeNode> nodes = new List<SaveTreeNode>();
            Flatten(_panel.VisibleTreeItemsForTests, nodes);
            return nodes.Select(static n => n.DisplayName).ToList();
        }

        private static void WriteExtraSave(string directory)
        {
            ConcurrentDictionary<string, SampleItem> items = new ConcurrentDictionary<string, SampleItem>();
            items["potion"] = new SampleItem { ItemId = "potion", Count = 1 };
            new DirectorySaveBackend(directory)
                .WriteAsync(new SaveBundle(SaveSession.CurrentFormatVersion, new Dictionary<string, byte[]>
                {
                    ["SampleProfile"] = MemoryPackSerializer.Serialize(new SampleProfile { Name = "extra", Level = 1 }),
                    ["items-v1"] = MemoryPackSerializer.Serialize(items),
                }))
                .AsTask().GetAwaiter().GetResult();
        }

        [Test]
        public void Window_OpenAndClose_DoesNotThrow()
        {
            // GetWindow can emit a graphics-device error under -batchmode -nographics; the
            // window lifecycle itself must still complete without an exception.
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            try
            {
                Assert.DoesNotThrow(static () =>
                {
                    PlayerDataViewerWindow window = EditorWindow.GetWindow<PlayerDataViewerWindow>();
                    window.Close();
                });
            }
            finally
            {
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
            }
        }

        [Test]
        public void BuildInto_CreatesTwoPaneSkeletonElements()
        {
            Assert.That(_rootElement.Q<ToolbarMenu>(ViewerUI.RootMenuName), Is.Not.Null);
            Assert.That(_rootElement.Q<ToolbarButton>(ViewerUI.RefreshButtonName), Is.Not.Null);
            Assert.That(_rootElement.Q<DropdownField>(ViewerUI.SessionDropdownName), Is.Not.Null);
            Assert.That(_rootElement.Q<ToolbarSearchField>(ViewerUI.SearchFieldName), Is.Not.Null);
            Assert.That(_rootElement.Q<VisualElement>(ViewerUI.SplitViewName), Is.Not.Null);
            Assert.That(Tree, Is.Not.Null);
            Assert.That(_rootElement.Q<VisualElement>(ViewerUI.DetailPaneName), Is.Not.Null);
            Assert.That(_controller.SessionTypes, Has.Member(typeof(SampleEditorSession)));
        }

        [Test]
        public void BuildInto_EmptyRoot_TreeShowsOnlySavedFilesGroup()
        {
            Assert.That(VisibleNames(), Is.EqualTo(new[] { ViewerDisplayNames.SavedFilesLabel }));

            // The TreeView control itself carries the same data (not just the test-hook list).
            List<int> rootIds = Tree.GetRootIds().ToList();
            Assert.That(rootIds, Has.Count.EqualTo(1));
            Assert.That(
                Tree.GetItemDataForId<SaveTreeNode>(rootIds[0]).DisplayName,
                Is.EqualTo(ViewerDisplayNames.SavedFilesLabel));
        }

        [Test]
        public void SetRoot_FixtureSaves_TreeShowsSaveAndDocumentNodes()
        {
            string saveRoot = SampleSaveMenu.Create(_root);
            SelectSampleSession();

            _panel.SetRootForTests(saveRoot);

            ToolbarMenu rootMenu = _rootElement.Q<ToolbarMenu>(ViewerUI.RootMenuName);
            Assert.That(rootMenu.text, Is.EqualTo(saveRoot), "a custom root shows its path");
            Assert.That(rootMenu.tooltip, Is.EqualTo(saveRoot));
            List<string> names = VisibleNames();
            Assert.That(names, Has.Member(ViewerDisplayNames.SavedFilesLabel));
            Assert.That(names, Has.Member(SaveTreeModel.RootSaveLabel));
            Assert.That(names, Has.Member("slot_1"));
            Assert.That(names, Has.Some.StartsWith("SampleProfile"));
            Assert.That(names, Has.Some.StartsWith("Items"));
            Assert.That(names, Has.Some.StartsWith("mystery"));
        }

        [Test]
        public void SetRoot_PersistentDataPath_MenuShowsDefaultLabelWithPathTooltip()
        {
            _panel.SetRootForTests(UnityEngine.Application.persistentDataPath);

            ToolbarMenu rootMenu = _rootElement.Q<ToolbarMenu>(ViewerUI.RootMenuName);
            Assert.That(rootMenu.text, Is.EqualTo(ViewerDisplayNames.DefaultRootLabel));
            Assert.That(rootMenu.tooltip, Is.EqualTo(UnityEngine.Application.persistentDataPath));
        }

        [Test]
        public void Refresh_AfterNewSaveWritten_AddsItsNode()
        {
            string saveRoot = SampleSaveMenu.Create(_root);
            SelectSampleSession();
            _panel.SetRootForTests(saveRoot);
            Assert.That(VisibleNames(), Has.No.Member("extra"));

            WriteExtraSave(Path.Combine(saveRoot, "extra"));
            _panel.RefreshForTests();

            Assert.That(VisibleNames(), Has.Member("extra"));
        }

        [Test]
        public void SearchFilter_NarrowsDocumentLeaves_AndKeepsTheirAncestors()
        {
            string saveRoot = SampleSaveMenu.Create(_root);
            SelectSampleSession();
            _panel.SetRootForTests(saveRoot);

            _panel.SetSearchFilterForTests("SampleProfile");

            List<string> names = VisibleNames();
            Assert.That(names, Has.Some.StartsWith("SampleProfile"));
            Assert.That(names, Has.Member(ViewerDisplayNames.SavedFilesLabel), "ancestors of matches stay visible");
            Assert.That(names, Has.Member(SaveTreeModel.RootSaveLabel));
            Assert.That(names, Has.Member("slot_1"));
            Assert.That(names, Has.None.StartsWith("Items"));
            Assert.That(names, Has.None.StartsWith("mystery"));

            _panel.SetSearchFilterForTests("zzz-no-such-document");
            Assert.That(VisibleNames(), Is.Empty);

            _panel.SetSearchFilterForTests(string.Empty);
            Assert.That(VisibleNames(), Has.Some.StartsWith("Items"));
        }

        [Test]
        public void OldStagedFlowElements_DoNotExist()
        {
            // Old-flow element names are hardcoded on purpose: their ViewerUI constants were
            // deleted together with the staged flow, and this test pins their absence.
            string[] oldNames =
            {
                "guide-box",
                "advanced-foldout",
                "source-dropdown",
                "scan-button",
                "save-dropdown",
                "documents-list",
                "disk-section",
                "live-section",
                "root-path-label",
                "browse-button",
            };

            foreach (string oldName in oldNames)
                Assert.That(_rootElement.Q<VisualElement>(oldName), Is.Null, $"'{oldName}' must not exist in the new UI");
        }
    }
}
