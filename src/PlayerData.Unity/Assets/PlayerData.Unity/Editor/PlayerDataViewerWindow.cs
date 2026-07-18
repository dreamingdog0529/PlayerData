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
        internal const string RootMenuName = "root-menu";
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
        private readonly ToolbarMenu _rootMenu;
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
        private readonly Dictionary<string, LiveSessionView> _liveViews =
            new Dictionary<string, LiveSessionView>(StringComparer.Ordinal);
        private readonly Action _registryChangedHandler;
        private readonly Action<PlayModeStateChange> _playModeChangedHandler;
        private bool _disposed;
        private string _rootPath;
        private string _filter = string.Empty;
        private IReadOnlyList<SaveTreeNode> _treeRoots = Array.Empty<SaveTreeNode>();
        private SaveTreeNode? _selectedNode;
        private Type? _payloadType;
        private bool _isCollection;
        private bool _editable;
        private bool _jsonViewActive;
        private bool _preferredJsonView;
        private string? _originalJson;
        private FieldEditorModel? _fieldsModel;
        private FieldEditorView? _fieldsView;

        internal ViewerPanel(VisualElement root, PlayerDataViewerController controller, string initialRootPath)
        {
            _controller = controller;
            _rootPath = initialRootPath;
            _preferredJsonView = EditorPrefs.GetBool(ViewerUI.JsonViewPrefsKey, false);
            controller.RefreshSessionTypes();
            root.Clear();

            // Located by search, not a literal path, so it resolves both when the package lives
            // under Assets/ (this repo) and when consumed from Packages/ via UPM.
            string[] styleGuids = AssetDatabase.FindAssets("PlayerDataViewerWindow t:StyleSheet");
            if (styleGuids.Length > 0)
                root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(styleGuids[0])));
            else
                Debug.LogWarning("PlayerDataViewerWindow.uss was not found; the split divider will render unstyled.");

            Toolbar toolbar = new Toolbar();
            root.Add(toolbar);

            _rootMenu = new ToolbarMenu();
            _rootMenu.name = ViewerUI.RootMenuName;
            // Long custom paths must not push the other toolbar controls out of view.
            _rootMenu.style.flexShrink = 1;
            _rootMenu.style.overflow = Overflow.Hidden;
            toolbar.Add(_rootMenu);
            UpdateRootMenu();

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

            // Plain flex row + a hand-rolled drag handle instead of TwoPaneSplitView: in a
            // code-built editor window that control collapsed its fixed pane to 0 width on the
            // first zero-size layout pass and its dragger stopped resizing the panes, so the
            // split is laid out and dragged explicitly here.
            VisualElement splitView = new VisualElement();
            splitView.name = ViewerUI.SplitViewName;
            splitView.style.flexGrow = 1;
            splitView.style.flexDirection = FlexDirection.Row;
            root.Add(splitView);

            _treeView = new TreeView
            {
                fixedItemHeight = 18,
                selectionType = SelectionType.Single,
                autoExpand = true,
            };
            _treeView.name = ViewerUI.TreeViewName;
            // flexBasis, not width: TreeView's own stylesheet sets flex-grow/flex-basis, which
            // wins over an inline width on the flex main axis (both panes end up ~equal halves).
            _treeView.style.flexBasis = 260;
            _treeView.style.flexGrow = 0;
            _treeView.style.flexShrink = 0;
            _treeView.style.minWidth = 120;
            _treeView.makeItem = static () => new Label();
            _treeView.bindItem = (element, index) =>
                ((Label)element).text = _treeView.GetItemDataForIndex<SaveTreeNode>(index).DisplayName;
            _treeView.selectionChanged += OnTreeSelectionChanged;
            splitView.Add(_treeView);

            splitView.Add(BuildSplitDivider(splitView));

            VisualElement detailPane = new VisualElement();
            detailPane.name = ViewerUI.DetailPaneName;
            detailPane.style.flexGrow = 1;
            detailPane.style.minWidth = 160;
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

            // Apply/Revert live in this top bar (right-aligned) so they stay reachable without
            // scrolling past a long document.
            VisualElement viewModeSpacer = new VisualElement();
            viewModeSpacer.style.flexGrow = 1;
            viewModeBar.Add(viewModeSpacer);
            _applyButton = new Button(OnApply) { text = ViewerDisplayNames.ApplyLabel };
            _applyButton.name = ViewerUI.ApplyButtonName;
            _applyButton.SetEnabled(false);
            viewModeBar.Add(_applyButton);
            _revertButton = new Button(OnRevert) { text = ViewerDisplayNames.RevertLabel };
            _revertButton.name = ViewerUI.RevertButtonName;
            _revertButton.SetEnabled(false);
            viewModeBar.Add(_revertButton);
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
            _jsonField.RegisterValueChangedCallback(_ => UpdateApplyState());
            _jsonScroll.Add(_jsonField);
            _detailContent.Add(_jsonScroll);

            // Outside the content block so load failures are visible while the pane is empty.
            _errorBox = new HelpBox(string.Empty, HelpBoxMessageType.Error);
            _errorBox.name = ViewerUI.ErrorBoxName;
            _errorBox.style.display = DisplayStyle.None;
            detailPane.Add(_errorBox);

            _registryChangedHandler = OnRegistryChanged;
            LiveSessionRegistry.Changed += _registryChangedHandler;
            _playModeChangedHandler = OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += _playModeChangedHandler;

            if (controller.SessionTypes.Count > 0)
                controller.SelectSession(controller.SessionTypes[0]);
            Rescan();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            LiveSessionRegistry.Changed -= _registryChangedHandler;
            EditorApplication.playModeStateChanged -= _playModeChangedHandler;
            DisposeLiveViews();
        }

        private void OnRegistryChanged()
        {
            if (_disposed)
                return;
            Rescan();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // Entered* only: during Exiting* the scene is mid-teardown, and the registry raises
            // Changed itself when it clears its entries on play-mode exit.
            if (change == PlayModeStateChange.EnteredPlayMode || change == PlayModeStateChange.EnteredEditMode)
                Rescan();
        }

        // Dragging the divider resizes the tree; the detail pane flexes to take the rest.
        // Look and hover cursor come from PlayerDataViewerWindow.uss (inline styles cannot set
        // the built-in resize cursor).
        private VisualElement BuildSplitDivider(VisualElement splitContainer)
        {
            VisualElement divider = new VisualElement();
            divider.AddToClassList("playerdata-viewer__split-divider");
            VisualElement line = new VisualElement();
            line.AddToClassList("playerdata-viewer__split-divider-line");
            divider.Add(line);
            float dragStartX = 0f;
            float dragStartWidth = 0f;
            divider.RegisterCallback<PointerDownEvent>(evt =>
            {
                divider.CapturePointer(evt.pointerId);
                dragStartX = evt.position.x;
                dragStartWidth = _treeView.resolvedStyle.width;
                evt.StopPropagation();
            });
            divider.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (!divider.HasPointerCapture(evt.pointerId))
                    return;
                // Keeps the detail pane's minWidth (160) plus the divider itself (5) reachable.
                float max = Mathf.Max(120f, splitContainer.resolvedStyle.width - 165f);
                _treeView.style.flexBasis = Mathf.Clamp(dragStartWidth + evt.position.x - dragStartX, 120f, max);
            });
            divider.RegisterCallback<PointerUpEvent>(evt => divider.ReleasePointer(evt.pointerId));
            return divider;
        }

        private void OnChooseFolder()
        {
            string picked = EditorUtility.OpenFolderPanel("Select save data folder", _rootPath, string.Empty);
            if (string.IsNullOrEmpty(picked))
                return;
            SetRoot(picked, persist: true);
        }

        private void SetRoot(string rootPath, bool persist)
        {
            _rootPath = rootPath;
            UpdateRootMenu();
            if (persist)
                EditorPrefs.SetString(ViewerUI.RootPathPrefsKey, rootPath);
            Rescan();
        }

        // The dropdown's face answers "which folder am I looking at?" without exposing a raw
        // path in the common case: the game's own save folder reads as a plain-language label,
        // anything else shows the path itself. The tooltip always carries the resolved path.
        private void UpdateRootMenu()
        {
            bool isDefault = string.Equals(
                NormalizePathForComparison(_rootPath),
                NormalizePathForComparison(Application.persistentDataPath),
                StringComparison.Ordinal);

            _rootMenu.text = isDefault ? ViewerDisplayNames.DefaultRootLabel : _rootPath;
            _rootMenu.tooltip = _rootPath;

            _rootMenu.menu.MenuItems().Clear();
            _rootMenu.menu.AppendAction(
                ViewerDisplayNames.DefaultRootLabel,
                _ => SetRoot(Application.persistentDataPath, persist: true),
                isDefault ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            if (!isDefault)
            {
                // DropdownMenu treats '/' in an item name as a submenu separator, so the
                // active custom path is shown with backslashes to stay a single item.
                _rootMenu.menu.AppendAction(
                    _rootPath.Replace('/', '\\'),
                    _ => { }, // already active; selecting it keeps the current root
                    DropdownMenuAction.Status.Checked);
            }

            _rootMenu.menu.AppendAction(
                ViewerDisplayNames.ChooseFolderLabel,
                _ => OnChooseFolder(),
                DropdownMenuAction.Status.Normal);
        }

        // Forward slashes, no trailing separator: "C:\X\" and "C:/X" name the same folder.
        private static string NormalizePathForComparison(string path) =>
            path.Replace('\\', '/').TrimEnd('/');

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
            SaveTreeNode? previous = _selectedNode;
            _controller.Scan(_rootPath);
            _treeRoots = SaveTreeModel.Build(
                _rootPath, _controller.LoadScannedSaves(), CollectLiveSessions());
            RebuildTree();
            // Scanning reloads every save (and recreates the live views), so the shown document
            // is re-resolved by identity: still present → re-shown freshly loaded (pending edits
            // are discarded, same as selecting another node), gone → the detail stays cleared.
            ClearDetail();
            if (previous is not null && TryFindSameDocument(_visibleItems, previous, out SaveTreeNode match))
            {
                _treeView.SetSelectionByIdWithoutNotify(new[] { match.Id });
                ShowNode(match);
            }
        }

        // Identity, not tree id: ids are positional and shift when saves appear or disappear.
        private static bool TryFindSameDocument(
            IEnumerable<TreeViewItemData<SaveTreeNode>> items, SaveTreeNode previous, out SaveTreeNode match)
        {
            foreach (TreeViewItemData<SaveTreeNode> item in items)
            {
                SaveTreeNode node = item.data;
                if (node.Kind == previous.Kind && node.IsSelectableForDetail && IsSameDocument(node, previous))
                {
                    match = node;
                    return true;
                }

                if (TryFindSameDocument(item.children, previous, out match))
                    return true;
            }

            match = null!;
            return false;
        }

        private static bool IsSameDocument(SaveTreeNode left, SaveTreeNode right)
        {
            if (left.Kind == SaveTreeNodeKind.Document)
                return left.Location is not null && right.Location is not null
                    && string.Equals(left.Location.Directory, right.Location.Directory, StringComparison.Ordinal)
                    && string.Equals(left.StorageKey, right.StorageKey, StringComparison.Ordinal);

            return string.Equals(left.SessionName, right.SessionName, StringComparison.Ordinal)
                && string.Equals(left.StorageKey, right.StorageKey, StringComparison.Ordinal)
                && string.Equals(left.PropertyName, right.PropertyName, StringComparison.Ordinal);
        }

        // Recreates the live views from the registry. Session display names are the tree's
        // lookup key, so duplicates get an index suffix (same rule as the session dropdown).
        private IReadOnlyList<SaveTreeLiveSession> CollectLiveSessions()
        {
            DisposeLiveViews();

            IReadOnlyList<LiveSessionEntry> entries = LiveSessionRegistry.Entries;
            if (entries.Count == 0)
                return Array.Empty<SaveTreeLiveSession>();

            Dictionary<string, int> totals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (LiveSessionEntry entry in entries)
                totals[entry.Name] = totals.TryGetValue(entry.Name, out int count) ? count + 1 : 1;

            List<SaveTreeLiveSession> sessions = new List<SaveTreeLiveSession>(entries.Count);
            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (LiveSessionEntry entry in entries)
            {
                seen[entry.Name] = seen.TryGetValue(entry.Name, out int occurrence) ? occurrence + 1 : 1;
                string name = totals[entry.Name] > 1 ? $"{entry.Name} ({seen[entry.Name]})" : entry.Name;
                LiveSessionView view = new LiveSessionView(entry.Session);
                _liveViews.Add(name, view);
                sessions.Add(new SaveTreeLiveSession(name, view.Documents));
            }

            return sessions;
        }

        private void DisposeLiveViews()
        {
            foreach (KeyValuePair<string, LiveSessionView> pair in _liveViews)
                pair.Value.Dispose();
            _liveViews.Clear();
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
            if (node is null)
                return;

            if (node.Kind == SaveTreeNodeKind.Document && node.Location is not null && node.StorageKey is not null)
            {
                _controller.SelectSave(node.Location);
                _selectedNode = node;
                PopulateDetail();
            }
            else if (node.Kind == SaveTreeNodeKind.LiveDocument && node.SessionName is not null && node.PropertyName is not null)
            {
                _selectedNode = node;
                PopulateDetail();
            }
        }

        private void PopulateDetail()
        {
            if (_selectedNode!.Kind == SaveTreeNodeKind.LiveDocument)
                PopulateLiveDetail();
            else
                PopulateDiskDetail();
        }

        private void PopulateDiskDetail()
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

            PresentDocument(view.Json, view.JsonError, FieldsBlockReason(entry, view, stateReason));
        }

        private void PopulateLiveDetail()
        {
            SaveTreeNode node = _selectedNode!;
            ClearFieldsSurface();
            HideError();

            if (!_liveViews.TryGetValue(node.SessionName!, out LiveSessionView view))
            {
                ClearDetail();
                ShowError($"Live session '{node.SessionName}' is gone. Refresh the tree.");
                return;
            }

            LiveDocumentDescriptor? descriptor = null;
            foreach (LiveDocumentDescriptor candidate in view.Documents)
            {
                if (string.Equals(candidate.PropertyName, node.PropertyName, StringComparison.Ordinal))
                {
                    descriptor = candidate;
                    break;
                }
            }

            if (descriptor is null)
            {
                ClearDetail();
                ShowError($"Live document '{node.PropertyName}' is gone. Refresh the tree.");
                return;
            }

            string? json = null;
            string? jsonError = null;
            try
            {
                json = view.GetJson(descriptor.PropertyName);
            }
            catch (Exception ex)
            {
                jsonError = ex.Message;
            }

            string? editBlockReason = null;
            bool editable = json is not null && view.CanEdit(descriptor.PropertyName, out editBlockReason);

            _detailContent.style.display = DisplayStyle.Flex;
            _detailHeader.text = $"{descriptor.PropertyName} ({node.SessionName})";
            // Live docs have no on-disk DocumentState; the live round-trip gate maps onto the
            // same two labels the disk pane uses.
            _detailStateLabel.text = ViewerDisplayNames.StateLabel(
                editable ? DocumentState.Editable : DocumentState.ReadOnlyRoundTrip);
            _detailStateLabel.tooltip = editBlockReason ?? jsonError ?? string.Empty;
            _detailPathLabel.text = SaveTreeModel.PlayingNowLabel;

            _editable = editable;
            _isCollection = descriptor.IsCollection;
            _payloadType = descriptor.IsCollection ? null : descriptor.EntityType;

            string? fieldsBlockReason = null;
            if (json is null)
                fieldsBlockReason = jsonError ?? "The current value cannot be shown.";
            else if (!editable)
                fieldsBlockReason = editBlockReason ?? "This value can't be edited safely in the viewer.";

            PresentDocument(json, jsonError, fieldsBlockReason);
        }

        // Shared tail of the disk and live populate paths: fills the JSON field, applies the
        // Fields gate and restores the preferred view mode.
        private void PresentDocument(string? json, string? jsonError, string? fieldsBlockReason)
        {
            _originalJson = json;
            _jsonField.SetValueWithoutNotify(json ?? string.Empty);
            _jsonField.isReadOnly = !_editable;

            bool fieldsAvailable = fieldsBlockReason is null;
            _fieldsToggle.SetEnabled(fieldsAvailable);
            _fieldsToggle.tooltip = fieldsBlockReason ?? string.Empty;

            _jsonViewActive = _preferredJsonView || !fieldsAvailable;
            if (!_jsonViewActive && !TryBuildFieldsSurface(json ?? string.Empty))
                _jsonViewActive = true;

            SyncViewToggles();
            UpdateEditSurfaceVisibility();
            UpdateApplyState();

            if (jsonError is not null)
                ShowError($"Could not show values: {jsonError}");
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
            SaveTreeNode? node = _selectedNode;
            if (node is null)
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

            bool applied;
            string? error;
            if (node.Kind == SaveTreeNodeKind.LiveDocument)
            {
                if (!_liveViews.TryGetValue(node.SessionName!, out LiveSessionView view))
                {
                    ShowError($"Live session '{node.SessionName}' is gone. Refresh the tree.");
                    return;
                }

                applied = view.ApplyJson(node.PropertyName!, payload, out error);
            }
            else
            {
                if (node.StorageKey is null)
                    return;
                applied = _controller.ApplyJson(node.StorageKey, payload, out error);
            }

            if (applied)
            {
                HideError();
                // Disk: ApplyJson reloaded the save; live: the session was mutated in place.
                // Repopulating shows the state the target actually holds now.
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
            // Live docs revert by re-reading the live snapshot; disk docs reload from disk.
            if (_selectedNode.Kind != SaveTreeNodeKind.LiveDocument)
                _controller.Reload();
            PopulateDetail();
        }

        private void UpdateApplyState()
        {
            bool editable = _selectedNode is not null && _editable;
            bool dirty = editable && IsDirty();
            bool canApply = dirty;
            if (canApply && !_jsonViewActive && _fieldsModel is not null && _fieldsModel.HasInvalid)
                canApply = false;
            _applyButton.SetEnabled(canApply);
            _revertButton.SetEnabled(dirty);
        }

        // "Changed from the original" is compared on canonical (parse + compact re-serialize)
        // JSON so formatting drift between the converter output, the field editor's working
        // copy and hand-typed text never counts as an edit. Unparseable typed JSON counts as
        // dirty (Apply will surface the parse error), as do invalid field values (Revert must
        // stay available to escape them).
        private bool IsDirty()
        {
            if (_originalJson is null)
                return false;

            if (!_jsonViewActive && _fieldsModel is not null)
                return _fieldsModel.HasInvalid
                    || !string.Equals(CanonicalJson(_fieldsModel.ToJson()), CanonicalJson(_originalJson), StringComparison.Ordinal);

            return !string.Equals(CanonicalJson(_jsonField.value), CanonicalJson(_originalJson), StringComparison.Ordinal);
        }

        private static string CanonicalJson(string json)
        {
            try
            {
                return Newtonsoft.Json.Linq.JToken.Parse(json).ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return json;
            }
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
            _originalJson = null;
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

        internal void SetJsonTextForTests(string json)
        {
            // Mirrors the runtime path (typing raises a change event → UpdateApplyState); the
            // event is not dispatched reliably on an unattached panel, so the update is direct.
            _jsonField.SetValueWithoutNotify(json);
            UpdateApplyState();
        }

        internal void ApplyForTests() => OnApply();

        internal void RevertForTests() => OnRevert();

        internal bool IsJsonViewActiveForTests => _jsonViewActive;

        internal FieldEditorModel? FieldsModelForTests => _fieldsModel;

        internal FieldEditorView? FieldsViewForTests => _fieldsView;
    }
}
