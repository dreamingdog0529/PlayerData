using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace PlayerData.Unity.Editor
{
    public sealed class PlayerDataViewerWindow : EditorWindow
    {
        private readonly PlayerDataViewerController _controller = new PlayerDataViewerController();
        private ViewerPanel? _panel;

        [MenuItem(PlayerDataEditorMenu.WindowMenuPath)]
        public static void Open()
        {
            PlayerDataViewerWindow window = GetWindow<PlayerDataViewerWindow>();
            window.titleContent = new GUIContent("PlayerData Viewer");
        }

        public void CreateGUI()
        {
            string rootPath = EditorPrefs.GetString(ViewerUI.RootPathPrefsKey, Application.persistentDataPath);
            _panel = ViewerUI.BuildInto(rootVisualElement, _controller, rootPath);
        }

        private void OnDisable()
        {
            _panel?.Dispose();
            _panel = null;
        }
    }

    // UI construction is separated from the EditorWindow lifecycle so EditMode tests can build
    // and inspect the hierarchy without relying on window layout callbacks (which are not
    // guaranteed to run in batch mode).
    internal static class ViewerUI
    {
        internal const string RootPathLabelName = "root-path-label";
        internal const string BrowseButtonName = "browse-button";
        internal const string RefreshButtonName = "refresh-button";
        internal const string SessionDropdownName = "session-dropdown";
        internal const string SearchFieldName = "search-field";
        internal const string SplitViewName = "split-view";
        internal const string TreeViewName = "save-tree";
        internal const string DetailPaneName = "detail-pane";

        // Per-project key so projects sharing this machine keep independent viewer roots.
        internal static string RootPathPrefsKey => "PlayerData.Viewer.RootPath." + PlayerSettings.productGUID;

        internal static ViewerPanel BuildInto(VisualElement root, PlayerDataViewerController controller, string initialRootPath)
        {
            return new ViewerPanel(root, controller, initialRootPath);
        }
    }

    internal sealed class ViewerPanel : IDisposable
    {
        private readonly PlayerDataViewerController _controller;
        private readonly Label _rootPathLabel;
        private readonly DropdownField _sessionDropdown;
        private readonly ToolbarSearchField _searchField;
        private readonly TreeView _treeView;
        private readonly List<TreeViewItemData<SaveTreeNode>> _visibleItems = new List<TreeViewItemData<SaveTreeNode>>();
        private string _rootPath;
        private string _filter = string.Empty;
        private IReadOnlyList<SaveTreeNode> _treeRoots = Array.Empty<SaveTreeNode>();

        internal ViewerPanel(VisualElement root, PlayerDataViewerController controller, string initialRootPath)
        {
            _controller = controller;
            _rootPath = initialRootPath;
            controller.RefreshSessionTypes();
            root.Clear();

            Toolbar toolbar = new Toolbar();
            root.Add(toolbar);

            _rootPathLabel = new Label(initialRootPath);
            _rootPathLabel.name = ViewerUI.RootPathLabelName;
            _rootPathLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            _rootPathLabel.style.flexShrink = 1;
            _rootPathLabel.style.overflow = Overflow.Hidden;
            toolbar.Add(_rootPathLabel);

            ToolbarButton browseButton = new ToolbarButton(OnBrowse) { text = "Browse..." };
            browseButton.name = ViewerUI.BrowseButtonName;
            toolbar.Add(browseButton);

            ToolbarButton refreshButton = new ToolbarButton(Rescan) { text = "Refresh" };
            refreshButton.name = ViewerUI.RefreshButtonName;
            toolbar.Add(refreshButton);

            List<string> sessionNames = ViewerDisplayNames.DisambiguatedShortNames(controller.SessionTypes);
            _sessionDropdown = new DropdownField(sessionNames, controller.SessionTypes.Count > 0 ? 0 : -1);
            _sessionDropdown.name = ViewerUI.SessionDropdownName;
            _sessionDropdown.style.minWidth = 120;
            _sessionDropdown.RegisterValueChangedCallback(_ => OnSessionChanged());
            toolbar.Add(_sessionDropdown);

            _searchField = new ToolbarSearchField();
            _searchField.name = ViewerUI.SearchFieldName;
            _searchField.style.flexGrow = 1;
            _searchField.RegisterValueChangedCallback(evt => OnSearchChanged(evt.newValue));
            toolbar.Add(_searchField);

            TwoPaneSplitView splitView = new TwoPaneSplitView(0, 260f, TwoPaneSplitViewOrientation.Horizontal);
            splitView.name = ViewerUI.SplitViewName;
            splitView.style.flexGrow = 1;
            root.Add(splitView);

            _treeView = new TreeView
            {
                fixedItemHeight = 18,
                selectionType = SelectionType.Single,
                autoExpand = true,
            };
            _treeView.name = ViewerUI.TreeViewName;
            _treeView.makeItem = static () => new Label();
            _treeView.bindItem = (element, index) =>
                ((Label)element).text = _treeView.GetItemDataForIndex<SaveTreeNode>(index).DisplayName;
            splitView.Add(_treeView);

            // Document editing arrives with the detail pane; until then the right side is an
            // intentionally empty, named placeholder.
            VisualElement detailPane = new VisualElement();
            detailPane.name = ViewerUI.DetailPaneName;
            splitView.Add(detailPane);

            if (controller.SessionTypes.Count > 0)
                controller.SelectSession(controller.SessionTypes[0]);
            Rescan();
        }

        public void Dispose()
        {
            // Nothing to release yet; kept so the window's teardown contract survives the
            // live-session integration that will subscribe to external events.
        }

        private void OnBrowse()
        {
            string picked = EditorUtility.OpenFolderPanel("Select save data folder", _rootPath, string.Empty);
            if (string.IsNullOrEmpty(picked))
                return;
            SetRoot(picked, persist: true);
        }

        private void SetRoot(string rootPath, bool persist)
        {
            _rootPath = rootPath;
            _rootPathLabel.text = rootPath;
            if (persist)
                EditorPrefs.SetString(ViewerUI.RootPathPrefsKey, rootPath);
            Rescan();
        }

        private void OnSessionChanged()
        {
            int index = _sessionDropdown.index;
            if (index < 0 || index >= _controller.SessionTypes.Count)
                return;

            _controller.SelectSession(_controller.SessionTypes[index]);
            Rescan();
        }

        private void Rescan()
        {
            _controller.Scan(_rootPath);
            // Live sessions join the tree in a later step; the tree is disk-only until then.
            _treeRoots = SaveTreeModel.Build(
                _rootPath, _controller.LoadScannedSaves(), Array.Empty<SaveTreeLiveSession>());
            RebuildTree();
        }

        private void OnSearchChanged(string filter)
        {
            _filter = filter ?? string.Empty;
            RebuildTree();
        }

        private void RebuildTree()
        {
            _visibleItems.Clear();
            foreach (SaveTreeNode node in _treeRoots)
            {
                if (TryBuildItem(node, out TreeViewItemData<SaveTreeNode> item))
                    _visibleItems.Add(item);
            }

            _treeView.SetRootItems(_visibleItems);
            _treeView.Rebuild();
        }

        // The filter matches document leaves by display-name substring; every ancestor of a
        // match stays visible so the leaf keeps its context.
        private bool TryBuildItem(SaveTreeNode node, out TreeViewItemData<SaveTreeNode> item)
        {
            List<TreeViewItemData<SaveTreeNode>> children = new List<TreeViewItemData<SaveTreeNode>>();
            foreach (SaveTreeNode child in node.Children)
            {
                if (TryBuildItem(child, out TreeViewItemData<SaveTreeNode> childItem))
                    children.Add(childItem);
            }

            bool keep;
            if (string.IsNullOrEmpty(_filter))
                keep = true;
            else if (node.IsSelectableForDetail)
                keep = node.DisplayName.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;
            else
                keep = children.Count > 0;

            item = keep ? new TreeViewItemData<SaveTreeNode>(node.Id, node, children) : default;
            return keep;
        }

        // ---- Test hooks ----
        // Detached elements (no panel) do not dispatch ChangeEvent/ClickEvent, so EditMode tests
        // drive the same handlers the controls are wired to.

        internal IReadOnlyList<TreeViewItemData<SaveTreeNode>> VisibleTreeItemsForTests => _visibleItems;

        internal void SelectSessionForTests(int index)
        {
            _sessionDropdown.index = index;
            OnSessionChanged();
        }

        internal void SetRootForTests(string rootPath) => SetRoot(rootPath, persist: false);

        internal void RefreshForTests() => Rescan();

        internal void SetSearchFilterForTests(string filter)
        {
            _searchField.SetValueWithoutNotify(filter);
            OnSearchChanged(filter);
        }
    }
}
