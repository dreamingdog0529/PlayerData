using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using PlayerData.Persistence;

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

    // UI の構築は EditorWindow のライフサイクルから分けてあります。EditMode テストが、ウィンドウのレイアウト用コールバックに頼らずに
    // 階層を作って確認できるようにするためです（そのコールバックは、バッチモードでは
    // 動く保証がありません）。
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
        internal const string JsonTextFieldName = "json-text-field";
        internal const string ApplyButtonName = "apply-button";
        internal const string RevertButtonName = "revert-button";
        internal const string ErrorBoxName = "error-box";
        internal const string EmptyStateName = "empty-state";

        // プロジェクトごとのキーです。このマシンを共有するプロジェクト同士で、ビューワーのルートが独立したままになるようにしています。
        internal static string RootPathPrefsKey => "PlayerData.Viewer.RootPath." + PlayerSettings.productGUID;

        // ビューワー全体で 1 つの設定です（ドキュメントごとではありません）。true なら JSON ビューです。
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
        private readonly ScrollView _jsonScroll;
        private readonly TextField _jsonField;
        private readonly Button _applyButton;
        private readonly Button _revertButton;
        private readonly HelpBox _errorBox;
        private readonly VisualElement _emptyState;
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
        private Type? _entityType;
        private Type? _keyType;
        private bool _isCollection;
        private bool _editable;
        private bool _jsonViewActive;
        private bool _preferredJsonView;
        private string? _originalJson;
        private IFieldsEditor? _fields;

        internal ViewerPanel(VisualElement root, PlayerDataViewerController controller, string initialRootPath)
        {
            _controller = controller;
            _rootPath = initialRootPath;
            _preferredJsonView = EditorPrefs.GetBool(ViewerUI.JsonViewPrefsKey, false);
            controller.RefreshSessionTypes();
            root.Clear();

            // リテラルのパスではなく検索で見つけています。パッケージが Assets/ の下にあるとき（このリポジトリです）も、
            // UPM 経由で Packages/ から使うときも、どちらでも解決できるようにするためです。
            string[] styleGuids = AssetDatabase.FindAssets("PlayerDataViewerWindow t:StyleSheet");
            if (styleGuids.Length > 0)
                root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(styleGuids[0])));
            else
                Debug.LogWarning("PlayerDataViewerWindow.uss was not found; the split divider will render unstyled.");

            Toolbar toolbar = new Toolbar();
            root.Add(toolbar);

            _rootMenu = new ToolbarMenu();
            _rootMenu.name = ViewerUI.RootMenuName;
            // 長いカスタムパスが、ほかのツールバーのコントロールを見えないところへ押し出してしまわないようにします。
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

            // TwoPaneSplitView ではなく、素の flex の行と手作りのドラッグハンドルを使っています。コードで組み立てた
            // エディターウィンドウでは、そのコントロールはサイズゼロの最初のレイアウトパスで、固定ペインを幅 0 に潰してしまい、
            // ドラッガーでペインのサイズを変えられなくなってしまったためです。なので、分割は
            // ここで明示的にレイアウトし、ドラッグも扱っています。
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
            // width ではなく flexBasis です。TreeView 自身のスタイルシートが flex-grow/flex-basis を設定していて、そちらのほうが
            // flex の主軸ではインラインの width より優先されてしまいます（両方のペインがほぼ半々になってしまいます）。
            _treeView.style.flexBasis = 260;
            _treeView.style.flexGrow = 0;
            _treeView.style.flexShrink = 0;
            _treeView.style.minWidth = 120;
            // 行は、アイコン + 名前 + ステータスドットです。アイコンでノードの種類がわかり、ドットで
            // ドキュメントの状態がひと目でわかります（ツールチップに状態のテキストが入っています）。
            _treeView.makeItem = static () =>
            {
                VisualElement row = new VisualElement();
                row.AddToClassList("playerdata-viewer__tree-row");

                VisualElement icon = new VisualElement { name = "tree-icon" };
                icon.AddToClassList("playerdata-viewer__tree-icon");
                row.Add(icon);

                Label label = new Label { name = "tree-label" };
                label.AddToClassList("playerdata-viewer__tree-label");
                row.Add(label);

                VisualElement dot = new VisualElement { name = "tree-dot" };
                dot.AddToClassList("playerdata-viewer__tree-dot");
                row.Add(dot);

                // フォルダーやファイルの行を右クリックすると、その場所を開けます。メニューは、行の userData から
                // バインドされたノードを読みます（バインドのたびに更新されます）。
                row.AddManipulator(new ContextualMenuManipulator(PopulateTreeContextMenu));
                return row;
            };
            _treeView.bindItem = (element, index) =>
            {
                SaveTreeNode node = _treeView.GetItemDataForIndex<SaveTreeNode>(index);
                element.userData = node;
                element.Q<Label>("tree-label").text = node.DisplayName;
                BindTreeIcon(element.Q<VisualElement>("tree-icon"), node.Kind);
                BindTreeDot(element.Q<VisualElement>("tree-dot"), node);
            };
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
            _detailContent.AddToClassList("playerdata-viewer__detail");
            _detailContent.style.flexGrow = 1;
            _detailContent.style.display = DisplayStyle.None;
            detailPane.Add(_detailContent);

            // ヘッダーの行です。ドキュメント名と、そのステータスバッジを並べて固定しています。
            VisualElement headerRow = new VisualElement();
            headerRow.AddToClassList("playerdata-viewer__detail-headerrow");
            _detailHeader = new Label();
            _detailHeader.name = ViewerUI.DetailHeaderName;
            _detailHeader.AddToClassList("playerdata-viewer__detail-header");
            headerRow.Add(_detailHeader);

            _detailStateLabel = new Label();
            _detailStateLabel.name = ViewerUI.DetailStateLabelName;
            _detailStateLabel.AddToClassList("playerdata-viewer__status-badge");
            headerRow.Add(_detailStateLabel);
            _detailContent.Add(headerRow);

            _detailPathLabel = new Label();
            _detailPathLabel.name = ViewerUI.DetailPathLabelName;
            _detailPathLabel.AddToClassList("playerdata-viewer__path");
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
                    SyncViewToggles(); // 有効になっているトグルをクリックしても何もしません
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

            // Apply/Revert はこの上のバー（右寄せ）に置いてあります。長いドキュメントをスクロールしなくても
            // 届くようにするためです。
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
            _fieldsScroll.AddToClassList("playerdata-viewer__edit-surface");
            _fieldsScroll.style.flexGrow = 1;
            _fieldsSection = new VisualElement();
            _fieldsSection.name = ViewerUI.FieldsSectionName;
            _fieldsScroll.Add(_fieldsSection);
            _detailContent.Add(_fieldsScroll);

            _jsonScroll = new ScrollView();
            _jsonScroll.AddToClassList("playerdata-viewer__edit-surface");
            _jsonScroll.style.flexGrow = 1;
            _jsonField = new TextField { multiline = true, isReadOnly = true };
            _jsonField.name = ViewerUI.JsonTextFieldName;
            _jsonField.RegisterValueChangedCallback(_ => UpdateApplyState());
            _jsonScroll.Add(_jsonField);
            _detailContent.Add(_jsonScroll);

            // コンテンツブロックの外に置いてあります。ペインが空のときでも、読み込みの失敗が見えるようにするためです。
            _errorBox = new HelpBox(string.Empty, HelpBoxMessageType.Error);
            _errorBox.name = ViewerUI.ErrorBoxName;
            _errorBox.style.display = DisplayStyle.None;
            detailPane.Add(_errorBox);

            // 何も選ばれていない（エラーも出ていない）ときはペインを埋めて、白紙のまま放っておくのではなく、
            // 案内が出るようにします。
            _emptyState = new VisualElement();
            _emptyState.name = ViewerUI.EmptyStateName;
            _emptyState.AddToClassList("playerdata-viewer__empty");
            Label emptyTitle = new Label(ViewerDisplayNames.NoDocumentSelectedTitle);
            emptyTitle.AddToClassList("playerdata-viewer__empty-title");
            _emptyState.Add(emptyTitle);
            Label emptyHint = new Label(ViewerDisplayNames.NoDocumentSelectedHint);
            emptyHint.AddToClassList("playerdata-viewer__empty-hint");
            _emptyState.Add(emptyHint);
            detailPane.Add(_emptyState);

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
            // Entered* のときだけです。Exiting* の間はシーンが撤収の途中ですし、レジストリはプレイモードを抜けるときに
            // エントリを消す際に自分で Changed を上げます。
            if (change == PlayModeStateChange.EnteredPlayMode || change == PlayModeStateChange.EnteredEditMode)
                Rescan();
        }

        // 仕切りをドラッグするとツリーのサイズが変わり、詳細ペインが伸びて残りを受け持ちます。
        // 見た目とホバー時のカーソルは PlayerDataViewerWindow.uss から来ます（インラインスタイルでは、組み込みの
        // リサイズ用カーソルを設定できないためです）。
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
                // 詳細ペインの minWidth（160）と仕切りそのもの（5）に届く状態を保ちます。
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

        // ドロップダウンの表示面は、「今見ているのはどのフォルダーか」に、生のパスを見せずに答えます。
        // よくあるケースではそうです。ゲーム自身のセーブフォルダーはわかりやすい言葉のラベルとして読め、
        // それ以外ではパスそのものを表示します。ツールチップには、いつも解決済みのパスが入っています。
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
                // DropdownMenu は、項目名の中の '/' をサブメニューの区切りとして扱います。なので、
                // 有効なカスタムパスは、1 つの項目のままになるよう、バックスラッシュで表示します。
                _rootMenu.menu.AppendAction(
                    _rootPath.Replace('/', '\\'),
                    _ => { }, // すでに有効です。選んでも現在のルートのままです
                    DropdownMenuAction.Status.Checked);
            }

            _rootMenu.menu.AppendAction(
                ViewerDisplayNames.ChooseFolderLabel,
                _ => OnChooseFolder(),
                DropdownMenuAction.Status.Normal);
        }

        // スラッシュで統一し、末尾の区切りは付けません。もとの表記が違っても、同じフォルダーとして比べます。
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
            // スキャンすると、すべてのセーブを読み直し（ライブビューも作り直し）ます。なので、表示中のドキュメントは
            // 同一性で解決し直します。まだあれば、新しく読み込んで再表示し（未適用の編集は、ほかのノードを選んだときと
            // 同じように破棄されます）、なければ、詳細は消えたままにします。
            ClearDetail();
            if (previous is not null && TryFindSameDocument(_visibleItems, previous, out SaveTreeNode match))
            {
                _treeView.SetSelectionByIdWithoutNotify(new[] { match.Id });
                ShowNode(match);
            }
        }

        // ツリーの id ではなく、同一性で見ます。id は位置によるものなので、セーブが現れたり消えたりするとずれてしまいます。
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

        // レジストリからライブビューを作り直します。セッションの表示名がツリーの検索キーなので、
        // 重複したときはインデックスの suffix を付けます（セッションのドロップダウンと同じルールです）。
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

        // フィルターは、表示名の部分一致でドキュメントのリーフに一致します。一致したものの祖先は
        // すべて見えるままにして、リーフが文脈を失わないようにします。
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

        // ---- 詳細ペイン ----

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

        // 選択を変えると、未適用の編集はわざと破棄されます。この UI には確認ダイアログがなく、そのルールは
        // 次のテストで固定されています。
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
            ShowDetailContent();
            _detailHeader.text = ViewerDisplayNames.DocumentDisplayName(
                entry.StorageKey, entry.Descriptor?.PropertyName, entry.Descriptor?.DocumentType.Name);
            string stateReason = ViewerDisplayNames.StateDescription(entry.State);
            if (string.IsNullOrEmpty(stateReason))
                stateReason = entry.StateReason ?? string.Empty;
            SetStatusBadge(entry.State, stateReason);
            _detailPathLabel.text = node.Location!.Directory;

            _editable = view.CanEdit;
            _isCollection = entry.Descriptor?.IsCollection == true;
            _payloadType = entry.Descriptor?.PayloadType;
            // コレクションの entity の T（各エントリの値の型）です。ペイロードそのものは ConcurrentDictionary の
            // ラッパーで、コレクションの Fields 画面がリフレクションで見ることはありません。
            _entityType = entry.Descriptor?.DocumentType;
            _keyType = entry.Descriptor?.KeyType;

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

            ShowDetailContent();
            _detailHeader.text = $"{descriptor.PropertyName} ({node.SessionName})";
            // ライブのドキュメントには、ディスク上の DocumentState がありません。ライブの往復のガードは、ディスク側のペインが
            // 使うのと同じ 2 つの状態（と色）に対応付けます。
            SetStatusBadge(
                editable ? DocumentState.Editable : DocumentState.ReadOnlyRoundTrip,
                editBlockReason ?? jsonError ?? string.Empty);
            _detailPathLabel.text = SaveTreeModel.PlayingNowLabel;

            _editable = editable;
            _isCollection = descriptor.IsCollection;
            _payloadType = descriptor.IsCollection ? null : descriptor.EntityType;
            _entityType = descriptor.EntityType;
            _keyType = descriptor.KeyType;

            string? fieldsBlockReason = null;
            if (json is null)
                fieldsBlockReason = jsonError ?? "The current value cannot be shown.";
            else if (!editable)
                fieldsBlockReason = editBlockReason ?? "This value can't be edited safely in the viewer.";

            PresentDocument(json, jsonError, fieldsBlockReason);
        }

        // ディスクとライブの、データを入れる経路に共通する最後の部分です。JSON フィールドに入力し、Fields の
        // ガードを適用して、好みのビューモードに戻します。
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

        // Fields には、解決済みのスキーマと、表示できる JSON ペイロードと、バイト列を保持する JSON の往復が必要です。
        // それ以外は JSON ビューだけになります（Editable でない限り読み取り専用です）。
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
                // Fields → JSON：共有の作業用コピーをテキストフィールドへシリアライズするので、
                // 切り替えの途中で編集したものが失われることはありません。
                if (_fields is not null)
                    _jsonField.SetValueWithoutNotify(_fields.ToJson());
                ClearFieldsSurface();
                HideError();
                _jsonViewActive = true;
            }
            else
            {
                // JSON → Fields：テキストをモデルへパースし直します。エラーが出たら JSON のままにして、
                // エラーを表示します（静かにフォールバックはしません）。
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

            // コレクションは、エントリごとにその entity の T をリフレクションで見ます。単一ドキュメントは、そのペイロードの
            // 型を見ます。どちらも解決済みの型が必要です。型がないのに Fields のトグルに届いてしまったということは本来ないはずなので、
            // これは防御的なガードです。
            Type? surfaceType = _isCollection ? _entityType : _payloadType;
            if (surfaceType is null)
                return false;

            try
            {
                _fields = _isCollection
                    ? new CollectionFieldsEditor(json, surfaceType, _keyType)
                    : (IFieldsEditor)new FieldEditorView(FieldEditorModel.Create(json, surfaceType));
            }
            catch (Exception ex)
            {
                _fields = null;
                ShowError($"JSON error: {ex.Message}");
                return false;
            }

            HideError();
            _fields.Changed += UpdateApplyState;
            _fieldsSection.Add(_fields.Root);
            _fieldsSection.SetEnabled(_editable);
            return true;
        }

        private void OnApply()
        {
            SaveTreeNode? node = _selectedNode;
            if (node is null)
                return;

            string payload;
            if (!_jsonViewActive && _fields is not null)
            {
                if (_fields.HasInvalid)
                {
                    ShowError("Fix the highlighted fields before applying.");
                    return;
                }

                payload = _fields.ToJson();
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
                // ディスクの場合：ApplyJson がセーブを読み直しました。ライブの場合：セッションがその場で変更されました。
                // データを入れ直すことで、対象が今実際に持っている状態を表示します。
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
            // ライブのドキュメントは、ライブのスナップショットを読み直して revert します。ディスクのドキュメントは、ディスクから読み直します。
            if (_selectedNode.Kind != SaveTreeNodeKind.LiveDocument)
                _controller.Reload();
            PopulateDetail();
        }

        private void UpdateApplyState()
        {
            bool editable = _selectedNode is not null && _editable;
            bool dirty = editable && IsDirty();
            bool canApply = dirty;
            if (canApply && !_jsonViewActive && _fields is not null && _fields.HasInvalid)
                canApply = false;
            _applyButton.SetEnabled(canApply);
            _revertButton.SetEnabled(dirty);
        }

        // 「元の内容から変わったか」は、正規化した（パースして、コンパクトに再シリアライズした）JSON で比べます。
        // コンバーターの出力、フィールドエディターの作業用コピー、手で打ったテキストの間の書式のずれが、編集として数えられないようにするためです。
        // パースできない入力済み JSON は dirty として数えます（Apply でパースエラーが出ます）。無効なフィールドの値も同様です
        // （そこから抜け出すために Revert が使えるままでないといけません）。
        // 
        private bool IsDirty()
        {
            if (_originalJson is null)
                return false;

            if (!_jsonViewActive && _fields is not null)
                return _fields.HasInvalid
                    || !string.Equals(CanonicalJson(_fields.ToJson()), CanonicalJson(_originalJson), StringComparison.Ordinal);

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
            _entityType = null;
            _keyType = null;
            _isCollection = false;
            _editable = false;
            _jsonViewActive = _preferredJsonView;
            _detailContent.style.display = DisplayStyle.None;
            ClearCardAccent();
            _emptyState.style.display = DisplayStyle.Flex;
            ClearFieldsSurface();
            _jsonField.SetValueWithoutNotify(string.Empty);
            _jsonField.isReadOnly = true;
            HideError();
            UpdateApplyState();
        }

        private void ClearFieldsSurface()
        {
            _fieldsSection.Clear();
            _fields = null;
        }

        private void ShowError(string message)
        {
            _emptyState.style.display = DisplayStyle.None;
            _errorBox.text = message;
            _errorBox.style.display = DisplayStyle.Flex;
        }

        private void HideError()
        {
            _errorBox.text = string.Empty;
            _errorBox.style.display = DisplayStyle.None;
        }

        // ツリーの行の右クリックメニューです。ディスクのノードのフォルダーを OS のファイルブラウザー
        // （Explorer / Finder / ファイルマネージャー）で表示し、ドキュメントについては、そのファイルをデフォルトのアプリで開きます。
        // グループとライブのノードにはディスクの裏付けがないので、項目は出しません。
        private static void PopulateTreeContextMenu(ContextualMenuPopulateEvent evt)
        {
            VisualElement element = evt.target as VisualElement;
            while (element != null && element.userData is not SaveTreeNode)
                element = element.parent;

            if (element?.userData is not SaveTreeNode node)
                return;

            string directory = RevealableDirectory(node);
            if (!string.IsNullOrEmpty(directory))
            {
                evt.menu.AppendAction(
                    ViewerDisplayNames.RevealLabel(Application.platform),
                    _ => EditorUtility.RevealInFinder(directory));
            }

            string filePath = DocumentFilePath(node);
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                evt.menu.AppendAction(
                    ViewerDisplayNames.OpenFileLabel,
                    _ => EditorUtility.OpenWithDefaultApp(filePath));
            }
        }

        // ノードについて表示すべきディスク上のフォルダーです。ディスク上の場所がないノード
        // （グループとライブのセッション／ドキュメント）では null です。
        internal static string RevealableDirectory(SaveTreeNode node) => node?.Location?.Directory;

        // ディスクのドキュメント自身のファイルです。ディスクのドキュメントでないものは null です。パスは
        // DirectorySaveBackend のレイアウトに従うので、名前が書き込まれたものと合います。
        internal static string DocumentFilePath(SaveTreeNode node)
        {
            if (node is null || node.Kind != SaveTreeNodeKind.Document
                || node.Location is null || string.IsNullOrEmpty(node.StorageKey))
                return null;

            return DirectorySaveBackend.DocumentFilePath(node.Location.Directory, node.StorageKey);
        }

        // ドキュメントの詳細を表示し、空状態のプレースホルダーを取り下げます。
        private void ShowDetailContent()
        {
            _detailContent.style.display = DisplayStyle.Flex;
            _emptyState.style.display = DisplayStyle.None;
        }

        // 共有のステータスパレットから、ステータスバッジとカードの左側のアクセントを塗ります。ドキュメントの
        // 状態が、ツリーの中のドットと同じようにここでも読めるようにするためです。
        private void SetStatusBadge(DocumentState state, string tooltip)
        {
            Color accent = ViewerStatusVisuals.AccentColor(state);
            _detailStateLabel.text = ViewerDisplayNames.StateLabel(state);
            _detailStateLabel.tooltip = tooltip ?? string.Empty;
            _detailStateLabel.style.color = accent;
            _detailStateLabel.style.backgroundColor = ViewerStatusVisuals.BadgeBackground(accent);
            SetBadgeBorderColor(_detailStateLabel, ViewerStatusVisuals.BadgeBorder(accent));
            _detailContent.style.borderLeftWidth = 3;
            _detailContent.style.borderLeftColor = accent;
        }

        private void ClearCardAccent()
        {
            _detailContent.style.borderLeftWidth = 0;
            _detailContent.style.borderLeftColor = StyleKeyword.Null;
        }

        private static void SetBadgeBorderColor(VisualElement badge, Color color)
        {
            badge.style.borderTopColor = color;
            badge.style.borderBottomColor = color;
            badge.style.borderLeftColor = color;
            badge.style.borderRightColor = color;
        }

        private static void BindTreeIcon(VisualElement icon, SaveTreeNodeKind kind)
        {
            Texture2D texture = TreeIcon(kind);
            if (texture is null)
            {
                icon.style.display = DisplayStyle.None;
                return;
            }

            icon.style.display = DisplayStyle.Flex;
            icon.style.backgroundImage = new StyleBackground(texture);
        }

        // ドットが付くのは、注意が必要な状態だけです。健全な Editable のドキュメントは静かにしておいて、
        // もう一度見るべきものが目立つようにします。ライブのリーフには「live」のドットが付きます（そのディスク上の状態は、
        // 開くまで定まりません）。ドットのツールチップが状態を説明するので、ホバーすれば
        // 「このドキュメントはどうなっているの？」に答えられます。
        private static void BindTreeDot(VisualElement dot, SaveTreeNode node)
        {
            if (node.Kind == SaveTreeNodeKind.Document && node.State.HasValue
                && node.State.Value != DocumentState.Editable)
            {
                DocumentState state = node.State.Value;
                dot.style.display = DisplayStyle.Flex;
                dot.style.backgroundColor = ViewerStatusVisuals.AccentColor(state);
                dot.tooltip = DotTooltip(state);
            }
            else if (node.Kind == SaveTreeNodeKind.LiveDocument)
            {
                dot.style.display = DisplayStyle.Flex;
                dot.style.backgroundColor = ViewerStatusVisuals.LiveAccentColor();
                dot.tooltip = SaveTreeModel.PlayingNowLabel;
            }
            else
            {
                dot.style.display = DisplayStyle.None;
                dot.tooltip = string.Empty;
            }
        }

        // 短い状態ラベルと、そのわかりやすい説明です。たとえば、
        // "View only — This value can't be edited safely in the viewer."
        private static string DotTooltip(DocumentState state)
        {
            string label = ViewerDisplayNames.StateLabel(state);
            string description = ViewerDisplayNames.StateDescription(state);
            return string.IsNullOrEmpty(description) ? label : $"{label} — {description}";
        }

        private static Texture2D TreeIcon(SaveTreeNodeKind kind)
        {
            string iconName;
            switch (kind)
            {
                case SaveTreeNodeKind.Save:
                case SaveTreeNodeKind.LiveSession:
                    iconName = "Folder Icon";
                    break;
                case SaveTreeNodeKind.Document:
                case SaveTreeNodeKind.LiveDocument:
                    iconName = "TextAsset Icon";
                    break;
                default:
                    return null; // グループの行は、foldout の矢印だけに頼ります。
            }

            return EditorGUIUtility.IconContent(iconName)?.image as Texture2D;
        }

        // ---- テスト用の入口 ----
        // 切り離された要素（パネルがないもの）は ChangeEvent/ClickEvent を送らないので、EditMode テストでは、
        // コントロールにつながっているのと同じハンドラーを直接動かします。

        internal IReadOnlyList<TreeViewItemData<SaveTreeNode>> VisibleTreeItemsForTests => _visibleItems;

        internal void SelectSessionForTests(int index)
        {
            _sessionDropdown.index = index;
            OnSessionChanged();
        }

        // ドロップダウンに出ていないセッションを選びます（セッションの一覧から除外された、テスト専用のフィクスチャなどです）。
        // ドロップダウンを経由せずに、OnSessionChanged を再現します。
        internal void SelectSessionByTypeForTests(Type sessionType)
        {
            _controller.SelectSession(sessionType);
            Rescan();
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
            // ランタイムの経路を再現しています（入力すると変更イベントが上がり、UpdateApplyState へ進みます）。
            // 付いていないパネルではイベントが確実には送られないので、更新は直接行います。
            _jsonField.SetValueWithoutNotify(json);
            UpdateApplyState();
        }

        internal void ApplyForTests() => OnApply();

        internal void RevertForTests() => OnRevert();

        internal bool IsJsonViewActiveForTests => _jsonViewActive;

        internal FieldEditorModel? FieldsModelForTests => (_fields as FieldEditorView)?.Model;

        internal FieldEditorView? FieldsViewForTests => _fields as FieldEditorView;

        internal CollectionFieldsEditor? CollectionFieldsForTests => _fields as CollectionFieldsEditor;
    }
}
