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
        internal const string DetailContentName = "detail-content";
        internal const string DetailHeaderName = "detail-header";
        internal const string DetailStateLabelName = "detail-state-label";
        internal const string DetailPathLabelName = "detail-path-label";
        internal const string FieldsToggleName = "fields-toggle";
        internal const string JsonToggleName = "json-toggle";
        internal const string FieldsSectionName = "fields-section";
        internal const string FieldsHintName = "fields-hint";
        internal const string JsonTextFieldName = "json-text-field";
        internal const string ApplyButtonName = "apply-button";
        internal const string RevertButtonName = "revert-button";
        internal const string ErrorBoxName = "error-box";

        internal const string CollectionFieldsHint =
            "This is a collection. Switch to the JSON view to edit its entries.";

        // Per-project key so projects sharing this machine keep independent viewer roots.
        internal static string RootPathPrefsKey => "PlayerData.Viewer.RootPath." + PlayerSettings.productGUID;

        // One viewer-wide preference (not per document): true = JSON view.
        internal static string JsonViewPrefsKey => "PlayerData.Viewer.JsonView." + PlayerSettings.productGUID;

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
        private readonly VisualElement _detailContent;
        private readonly Label _detailHeader;
        private readonly Label _detailStateLabel;
        private readonly Label _detailPathLabel;
        private readonly ToolbarToggle _fieldsToggle;
        private readonly ToolbarToggle _jsonToggle;
        private readonly ScrollView _fieldsScroll;
        private readonly VisualElement _fieldsSection;
        private readonly Label _fieldsHint;
        private readonly ScrollView _jsonScroll;
        private readonly TextField _jsonField;
        private readonly Button _applyButton;
        private readonly Button _revertButton;
        private readonly HelpBox _errorBox;
        private readonly List<TreeViewItemData<SaveTreeNode>> _visibleItems = new List<TreeViewItemData<SaveTreeNode>>();
        private string _rootPath;
        private string _filter = string.Empty;
        private IReadOnlyList<SaveTreeNode> _treeRoots = Array.Empty<SaveTreeNode>();
        private SaveTreeNode? _selectedNode;
        private Type? _payloadType;
        private bool _isCollection;
        private bool _editable;
        private bool _jsonViewActive;
        private bool _preferredJsonView;
        private FieldEditorModel? _fieldsModel;
        private FieldEditorView? _fieldsView;

        internal ViewerPanel(VisualElement root, PlayerDataViewerController controller, string initialRootPath)
        {
            _controller = controller;
            _rootPath = initialRootPath;
            _preferredJsonView = EditorPrefs.GetBool(ViewerUI.JsonViewPrefsKey, false);
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
            _treeView.selectionChanged += OnTreeSelectionChanged;
            splitView.Add(_treeView);

            VisualElement detailPane = new VisualElement();
            detailPane.name = ViewerUI.DetailPaneName;
            detailPane.style.flexGrow = 1;
            splitView.Add(detailPane);

            _detailContent = new VisualElement();
            _detailContent.name = ViewerUI.DetailContentName;
            _detailContent.style.flexGrow = 1;
            _detailContent.style.display = DisplayStyle.None;
            detailPane.Add(_detailContent);

            _detailHeader = new Label();
            _detailHeader.name = ViewerUI.DetailHeaderName;
            _detailHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            _detailContent.Add(_detailHeader);

            _detailStateLabel = new Label();
            _detailStateLabel.name = ViewerUI.DetailStateLabelName;
            _detailContent.Add(_detailStateLabel);

            _detailPathLabel = new Label();
            _detailPathLabel.name = ViewerUI.DetailPathLabelName;
            _detailPathLabel.style.overflow = Overflow.Hidden;
            _detailContent.Add(_detailPathLabel);

            Toolbar viewModeBar = new Toolbar();
            _fieldsToggle = new ToolbarToggle { text = ViewerDisplayNames.FieldsTabLabel };
            _fieldsToggle.name = ViewerUI.FieldsToggleName;
            _fieldsToggle.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue)
                    SetViewMode(json: false, persist: true);
                else
                    SyncViewToggles(); // clicking the active toggle is a no-op
            });
            viewModeBar.Add(_fieldsToggle);
            _jsonToggle = new ToolbarToggle { text = ViewerDisplayNames.JsonTabLabel };
            _jsonToggle.name = ViewerUI.JsonToggleName;
            _jsonToggle.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue)
                    SetViewMode(json: true, persist: true);
                else
                    SyncViewToggles();
            });
            viewModeBar.Add(_jsonToggle);
            _detailContent.Add(viewModeBar);

            _fieldsScroll = new ScrollView();
            _fieldsScroll.style.flexGrow = 1;
            _fieldsHint = new Label();
            _fieldsHint.name = ViewerUI.FieldsHintName;
            _fieldsHint.style.unityFontStyleAndWeight = FontStyle.Italic;
            _fieldsHint.style.display = DisplayStyle.None;
            _fieldsScroll.Add(_fieldsHint);
            _fieldsSection = new VisualElement();
            _fieldsSection.name = ViewerUI.FieldsSectionName;
            _fieldsScroll.Add(_fieldsSection);
            _detailContent.Add(_fieldsScroll);

            _jsonScroll = new ScrollView();
            _jsonScroll.style.flexGrow = 1;
            _jsonField = new TextField { multiline = true, isReadOnly = true };
            _jsonField.name = ViewerUI.JsonTextFieldName;
            _jsonScroll.Add(_jsonField);
            _detailContent.Add(_jsonScroll);

            VisualElement buttonRow = new VisualElement();
            buttonRow.style.flexDirection = FlexDirection.Row;
            _applyButton = new Button(OnApply) { text = ViewerDisplayNames.ApplyLabel };
            _applyButton.name = ViewerUI.ApplyButtonName;
            _applyButton.SetEnabled(false);
            buttonRow.Add(_applyButton);
            _revertButton = new Button(OnRevert) { text = ViewerDisplayNames.RevertLabel };
            _revertButton.name = ViewerUI.RevertButtonName;
            _revertButton.SetEnabled(false);
            buttonRow.Add(_revertButton);
            _detailContent.Add(buttonRow);

            // Outside the content block so load failures are visible while the pane is empty.
            _errorBox = new HelpBox(string.Empty, HelpBoxMessageType.Error);
            _errorBox.name = ViewerUI.ErrorBoxName;
            _errorBox.style.display = DisplayStyle.None;
            detailPane.Add(_errorBox);

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
            // Scanning reloads every save, so a previously shown document may be gone or stale.
            ClearDetail();
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

        // ---- Detail pane ----

        private void OnTreeSelectionChanged(IEnumerable<object> selection)
        {
            SaveTreeNode? node = null;
            foreach (object item in selection)
            {
                node = item as SaveTreeNode;
                break;
            }

            ShowNode(node);
        }

        // Changing selection discards unapplied edits on purpose: this UI has no confirmation
        // dialogs, and the rule is pinned by
        // SwitchingDocumentSelection_DiscardsUnappliedEdits_ByDesign.
        private void ShowNode(SaveTreeNode? node)
        {
            ClearDetail();
            // Live documents get wired up with the live-session integration step; until then
            // they leave the pane empty like any other non-document node.
            if (node is null || node.Kind != SaveTreeNodeKind.Document
                || node.Location is null || node.StorageKey is null)
            {
                return;
            }

            _controller.SelectSave(node.Location);
            _selectedNode = node;
            PopulateDetail();
        }

        private void PopulateDetail()
        {
            SaveTreeNode node = _selectedNode!;
            ClearFieldsSurface();
            HideError();

            DocumentView? view = _controller.GetDocumentView(node.StorageKey!);
            if (view is null)
            {
                string error = _controller.LoadError ?? $"Document '{node.StorageKey}' no longer exists. Refresh the tree.";
                ClearDetail();
                ShowError(error);
                return;
            }

            DocumentEntry entry = view.Entry;
            _detailContent.style.display = DisplayStyle.Flex;
            _detailHeader.text = ViewerDisplayNames.DocumentDisplayName(
                entry.StorageKey, entry.Descriptor?.PropertyName, entry.Descriptor?.DocumentType.Name);
            _detailStateLabel.text = ViewerDisplayNames.StateLabel(entry.State);
            string stateReason = ViewerDisplayNames.StateDescription(entry.State);
            if (string.IsNullOrEmpty(stateReason))
                stateReason = entry.StateReason ?? string.Empty;
            _detailStateLabel.tooltip = stateReason;
            _detailPathLabel.text = node.Location!.Directory;

            _editable = view.CanEdit;
            _isCollection = entry.Descriptor?.IsCollection == true;
            _payloadType = entry.Descriptor?.PayloadType;
            _jsonField.SetValueWithoutNotify(view.Json ?? string.Empty);
            _jsonField.isReadOnly = !_editable;

            string? fieldsBlockReason = FieldsBlockReason(entry, view, stateReason);
            bool fieldsAvailable = fieldsBlockReason is null;
            _fieldsToggle.SetEnabled(fieldsAvailable);
            _fieldsToggle.tooltip = fieldsBlockReason ?? string.Empty;

            _jsonViewActive = _preferredJsonView || !fieldsAvailable;
            if (!_jsonViewActive && !TryBuildFieldsSurface(view.Json ?? string.Empty))
                _jsonViewActive = true;

            SyncViewToggles();
            UpdateEditSurfaceVisibility();
            UpdateApplyState();

            if (view.JsonError is not null)
                ShowError($"Could not show values: {view.JsonError}");
        }

        // Fields need a resolved schema, a renderable JSON payload and a JSON round-trip that
        // preserves the bytes; anything else is JSON-view-only (read-only unless Editable).
        private static string? FieldsBlockReason(DocumentEntry entry, DocumentView view, string stateReason)
        {
            if (entry.Descriptor is null)
                return string.IsNullOrEmpty(stateReason) ? "Not part of the selected save type." : stateReason;
            if (view.Json is null)
                return view.JsonError ?? (string.IsNullOrEmpty(stateReason) ? "The stored values cannot be shown." : stateReason);
            if (entry.State == DocumentState.ReadOnlyRoundTrip)
                return string.IsNullOrEmpty(stateReason) ? entry.StateReason ?? "View only" : stateReason;
            return null;
        }

        private void SetViewMode(bool json, bool persist)
        {
            _preferredJsonView = json;
            if (persist)
                EditorPrefs.SetBool(ViewerUI.JsonViewPrefsKey, json);

            if (_selectedNode is null || json == _jsonViewActive)
            {
                SyncViewToggles();
                return;
            }

            if (!json && !_fieldsToggle.enabledSelf)
            {
                SyncViewToggles();
                return;
            }

            if (json)
            {
                // Fields -> JSON: the shared working copy is serialized into the text field,
                // so nothing edited mid-switch is lost.
                if (_fieldsModel is not null)
                    _jsonField.SetValueWithoutNotify(_fieldsModel.ToJson());
                ClearFieldsSurface();
                HideError();
                _jsonViewActive = true;
            }
            else
            {
                // JSON -> Fields: parse the text back into the model; on error stay on JSON
                // with the error shown (no silent fallback).
                if (!TryBuildFieldsSurface(_jsonField.value))
                {
                    SyncViewToggles();
                    UpdateApplyState();
                    return;
                }

                _jsonViewActive = false;
            }

            SyncViewToggles();
            UpdateEditSurfaceVisibility();
            UpdateApplyState();
        }

        private bool TryBuildFieldsSurface(string json)
        {
            ClearFieldsSurface();
            if (_isCollection)
            {
                _fieldsHint.text = ViewerUI.CollectionFieldsHint;
                _fieldsHint.style.display = DisplayStyle.Flex;
                HideError();
                return true;
            }

            if (_payloadType is null)
                return false; // unreachable while the Fields toggle gating holds

            try
            {
                _fieldsModel = FieldEditorModel.Create(json, _payloadType);
            }
            catch (Exception ex)
            {
                _fieldsModel = null;
                ShowError($"JSON error: {ex.Message}");
                return false;
            }

            HideError();
            _fieldsView = new FieldEditorView(_fieldsModel);
            _fieldsView.Changed += UpdateApplyState;
            _fieldsSection.Add(_fieldsView.Root);
            _fieldsSection.SetEnabled(_editable);
            return true;
        }

        private void OnApply()
        {
            if (_selectedNode?.StorageKey is null)
                return;

            string payload;
            if (!_jsonViewActive && _fieldsModel is not null)
            {
                if (_fieldsModel.HasInvalid)
                {
                    ShowError("Fix the highlighted fields before applying.");
                    return;
                }

                payload = _fieldsModel.ToJson();
            }
            else
            {
                payload = _jsonField.value;
            }

            if (_controller.ApplyJson(_selectedNode.StorageKey, payload, out string? error))
            {
                HideError();
                // ApplyJson reloaded the save from disk; repopulating shows the persisted state.
                PopulateDetail();
            }
            else
            {
                ShowError(error ?? "Apply failed.");
            }
        }

        private void OnRevert()
        {
            if (_selectedNode is null)
                return;

            HideError();
            _controller.Reload();
            PopulateDetail();
        }

        private void UpdateApplyState()
        {
            bool hasDocument = _selectedNode is not null;
            bool canApply = hasDocument && _editable;
            if (canApply && !_jsonViewActive && _fieldsModel is not null && _fieldsModel.HasInvalid)
                canApply = false;
            _applyButton.SetEnabled(canApply);
            _revertButton.SetEnabled(hasDocument && _editable);
        }

        private void UpdateEditSurfaceVisibility()
        {
            _fieldsScroll.style.display = _jsonViewActive ? DisplayStyle.None : DisplayStyle.Flex;
            _jsonScroll.style.display = _jsonViewActive ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void SyncViewToggles()
        {
            _fieldsToggle.SetValueWithoutNotify(!_jsonViewActive);
            _jsonToggle.SetValueWithoutNotify(_jsonViewActive);
        }

        private void ClearDetail()
        {
            _selectedNode = null;
            _payloadType = null;
            _isCollection = false;
            _editable = false;
            _jsonViewActive = _preferredJsonView;
            _detailContent.style.display = DisplayStyle.None;
            ClearFieldsSurface();
            _jsonField.SetValueWithoutNotify(string.Empty);
            _jsonField.isReadOnly = true;
            HideError();
            UpdateApplyState();
        }

        private void ClearFieldsSurface()
        {
            _fieldsSection.Clear();
            _fieldsModel = null;
            _fieldsView = null;
            _fieldsHint.text = string.Empty;
            _fieldsHint.style.display = DisplayStyle.None;
        }

        private void ShowError(string message)
        {
            _errorBox.text = message;
            _errorBox.style.display = DisplayStyle.Flex;
        }

        private void HideError()
        {
            _errorBox.text = string.Empty;
            _errorBox.style.display = DisplayStyle.None;
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

        internal void SelectTreeNodeForTests(SaveTreeNode? node) => ShowNode(node);

        internal void SetViewModeForTests(bool json) => SetViewMode(json, persist: false);

        internal void SetJsonTextForTests(string json) => _jsonField.SetValueWithoutNotify(json);

        internal void ApplyForTests() => OnApply();

        internal void RevertForTests() => OnRevert();

        internal bool IsJsonViewActiveForTests => _jsonViewActive;

        internal FieldEditorModel? FieldsModelForTests => _fieldsModel;

        internal FieldEditorView? FieldsViewForTests => _fieldsView;
    }
}
