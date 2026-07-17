using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
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
            _panel = ViewerUI.BuildInto(rootVisualElement, _controller, Application.persistentDataPath);
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.update -= OnEditorUpdate;
            _panel?.Dispose();
            _panel = null;
        }

        private void OnEditorUpdate()
        {
            _panel?.Tick(EditorApplication.timeSinceStartup);
        }

        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            _panel?.RefreshPlayModeBanner();
        }
    }

    // UI construction is separated from the EditorWindow lifecycle so EditMode tests can build
    // and inspect the hierarchy without relying on window layout callbacks (which are not
    // guaranteed to run in batch mode).
    internal static class ViewerUI
    {
        internal const string PlayModeWarningName = "playmode-warning";
        internal const string LiveIndicatorName = "live-indicator";
        internal const string SourceDropdownName = "source-dropdown";
        internal const string DiskSectionName = "disk-section";
        internal const string SessionDropdownName = "session-dropdown";
        internal const string RootPathName = "root-path";
        internal const string ScanButtonName = "scan-button";
        internal const string ReloadButtonName = "reload-button";
        internal const string SaveDropdownName = "save-dropdown";
        internal const string SchemaDiagnosticsName = "schema-diagnostics";
        internal const string LoadErrorName = "load-error";
        internal const string DocumentsListName = "documents-list";
        internal const string LiveSectionName = "live-section";
        internal const string LiveDocumentsListName = "live-documents-list";
        internal const string LiveEntrySectionName = "live-entry-section";
        internal const string LiveEntriesListName = "live-entries-list";
        internal const string RemoveEntryButtonName = "remove-entry-button";
        internal const string AddEntryJsonName = "add-entry-json";
        internal const string AddEntryButtonName = "add-entry-button";
        internal const string DocumentInfoName = "document-info";
        internal const string StaleHintName = "stale-hint";
        internal const string DocumentJsonName = "document-json";
        internal const string ApplyButtonName = "apply-button";
        internal const string RevertButtonName = "revert-button";
        internal const string ApplyErrorName = "apply-error";
        internal const string FieldsTabButtonName = "fields-tab-button";
        internal const string JsonTabButtonName = "json-tab-button";
        internal const string FieldsScrollName = "fields-scroll";
        internal const string JsonScrollName = "json-scroll";
        internal const string FieldsSectionName = "fields-section";
        internal const string FieldsHintName = "fields-hint";

        internal const string DiskSourceLabel = "Disk";

        internal static ViewerPanel BuildInto(VisualElement root, PlayerDataViewerController controller, string defaultRootPath)
        {
            return new ViewerPanel(root, controller, defaultRootPath);
        }
    }

    internal sealed class ViewerPanel : IDisposable
    {
        // ~400ms between live refreshes keeps repaints cheap while the game mutates data every frame.
        private const double LiveRefreshIntervalSeconds = 0.4;

        private readonly PlayerDataViewerController _controller;
        private readonly HelpBox _playModeWarning;
        private readonly Label _liveIndicator;
        private readonly DropdownField _sourceDropdown;
        private readonly VisualElement _diskSection;
        private readonly DropdownField _sessionDropdown;
        private readonly TextField _rootPath;
        private readonly DropdownField _saveDropdown;
        private readonly HelpBox _schemaDiagnostics;
        private readonly HelpBox _loadError;
        private readonly ListView _documentsList;
        private readonly VisualElement _liveSection;
        private readonly ListView _liveDocumentsList;
        private readonly VisualElement _liveEntrySection;
        private readonly ListView _liveEntriesList;
        private readonly Button _removeEntryButton;
        private readonly TextField _addEntryJson;
        private readonly Label _documentInfo;
        private readonly Label _staleHint;
        private readonly TextField _documentJson;
        private readonly Button _applyButton;
        private readonly Button _revertButton;
        private readonly HelpBox _applyError;
        private readonly Button _fieldsTabButton;
        private readonly Button _jsonTabButton;
        private readonly ScrollView _fieldsScroll;
        private readonly ScrollView _jsonScroll;
        private readonly VisualElement _fieldsSection;
        private readonly Label _fieldsHint;
        private readonly List<DocumentEntry> _documents = new List<DocumentEntry>();
        private readonly List<LiveDocumentDescriptor> _liveDocuments = new List<LiveDocumentDescriptor>();
        private readonly List<object> _liveEntryKeys = new List<object>();
        private readonly List<LiveSessionEntry> _liveSources = new List<LiveSessionEntry>();
        private readonly Action _registryChangedHandler;
        private string? _selectedStorageKey;
        private string? _diskSelectedStorageKey;
        private LiveSessionView? _liveView;
        private ISaveSession? _liveSession;
        private string? _liveSelectedProperty;
        private object? _liveSelectedEntryKey;
        private string? _lastLoadedLiveJson;
        private FieldEditorModel? _fieldsModel;
        private FieldEditorView? _fieldsView;
        private Type? _fieldsDocType;
        private bool _fieldsJsonOnly;
        private bool _surfaceEditable;
        private bool _fieldsTabActive;
        private bool _pendingLiveChange;
        private double _lastLiveRefreshTime = double.NegativeInfinity;
        private bool _disposed;

        internal ViewerPanel(VisualElement root, PlayerDataViewerController controller, string defaultRootPath)
        {
            _controller = controller;
            controller.RefreshSessionTypes();
            root.Clear();

            _playModeWarning = new HelpBox(
                "Play mode is active: a live session's next commit can overwrite what is shown here.",
                HelpBoxMessageType.Warning);
            _playModeWarning.name = ViewerUI.PlayModeWarningName;
            root.Add(_playModeWarning);

            _liveIndicator = new Label("Live session: edits apply directly to the running game.");
            _liveIndicator.name = ViewerUI.LiveIndicatorName;
            _liveIndicator.style.display = DisplayStyle.None;
            root.Add(_liveIndicator);

            _sourceDropdown = new DropdownField("Source", new List<string> { ViewerUI.DiskSourceLabel }, 0);
            _sourceDropdown.name = ViewerUI.SourceDropdownName;
            _sourceDropdown.RegisterValueChangedCallback(_ => OnSourceChanged());
            root.Add(_sourceDropdown);

            _diskSection = new VisualElement();
            _diskSection.name = ViewerUI.DiskSectionName;
            root.Add(_diskSection);

            List<string> sessionNames = new List<string>();
            foreach (Type type in controller.SessionTypes)
                sessionNames.Add(type.FullName);
            _sessionDropdown = new DropdownField("Session", sessionNames, -1);
            _sessionDropdown.name = ViewerUI.SessionDropdownName;
            _sessionDropdown.RegisterValueChangedCallback(_ => OnSessionChanged());
            _diskSection.Add(_sessionDropdown);

            _rootPath = new TextField("Root path") { value = defaultRootPath };
            _rootPath.name = ViewerUI.RootPathName;
            _diskSection.Add(_rootPath);

            VisualElement buttonRow = new VisualElement();
            buttonRow.style.flexDirection = FlexDirection.Row;
            Button scanButton = new Button(OnScan) { text = "Scan" };
            scanButton.name = ViewerUI.ScanButtonName;
            buttonRow.Add(scanButton);
            Button reloadButton = new Button(OnReload) { text = "Reload" };
            reloadButton.name = ViewerUI.ReloadButtonName;
            buttonRow.Add(reloadButton);
            _diskSection.Add(buttonRow);

            _saveDropdown = new DropdownField("Save", new List<string>(), -1);
            _saveDropdown.name = ViewerUI.SaveDropdownName;
            _saveDropdown.RegisterValueChangedCallback(_ => OnSaveChanged());
            _diskSection.Add(_saveDropdown);

            _schemaDiagnostics = new HelpBox(string.Empty, HelpBoxMessageType.Warning);
            _schemaDiagnostics.name = ViewerUI.SchemaDiagnosticsName;
            _schemaDiagnostics.style.display = DisplayStyle.None;
            _diskSection.Add(_schemaDiagnostics);

            _loadError = new HelpBox(string.Empty, HelpBoxMessageType.Error);
            _loadError.name = ViewerUI.LoadErrorName;
            _loadError.style.display = DisplayStyle.None;
            _diskSection.Add(_loadError);

            _documentsList = new ListView
            {
                fixedItemHeight = 20,
                selectionType = SelectionType.Single,
                itemsSource = _documents,
            };
            _documentsList.name = ViewerUI.DocumentsListName;
            _documentsList.makeItem = static () => new Label();
            _documentsList.bindItem = (element, index) => ((Label)element).text = DescribeDocument(_documents[index]);
            _documentsList.selectionChanged += _ => ShowSelectedDocument();
            _documentsList.style.minHeight = 120;
            _diskSection.Add(_documentsList);

            _liveSection = new VisualElement();
            _liveSection.name = ViewerUI.LiveSectionName;
            _liveSection.style.display = DisplayStyle.None;
            root.Add(_liveSection);

            _liveDocumentsList = new ListView
            {
                fixedItemHeight = 20,
                selectionType = SelectionType.Single,
                itemsSource = _liveDocuments,
            };
            _liveDocumentsList.name = ViewerUI.LiveDocumentsListName;
            _liveDocumentsList.makeItem = static () => new Label();
            _liveDocumentsList.bindItem = (element, index) => ((Label)element).text = DescribeLiveDocument(_liveDocuments[index]);
            _liveDocumentsList.selectionChanged += _ => ShowSelectedLiveDocument();
            _liveDocumentsList.style.minHeight = 120;
            _liveSection.Add(_liveDocumentsList);

            _liveEntrySection = new VisualElement();
            _liveEntrySection.name = ViewerUI.LiveEntrySectionName;
            _liveEntrySection.style.display = DisplayStyle.None;
            _liveSection.Add(_liveEntrySection);

            _liveEntrySection.Add(new Label("Entries"));

            _liveEntriesList = new ListView
            {
                fixedItemHeight = 20,
                selectionType = SelectionType.Single,
                itemsSource = _liveEntryKeys,
            };
            _liveEntriesList.name = ViewerUI.LiveEntriesListName;
            _liveEntriesList.makeItem = static () => new Label();
            _liveEntriesList.bindItem = (element, index) => ((Label)element).text = _liveEntryKeys[index].ToString();
            _liveEntriesList.selectionChanged += _ => ShowSelectedLiveEntry();
            _liveEntriesList.style.minHeight = 80;
            _liveEntrySection.Add(_liveEntriesList);

            _removeEntryButton = new Button(OnRemoveEntry) { text = "Remove Entry" };
            _removeEntryButton.name = ViewerUI.RemoveEntryButtonName;
            _removeEntryButton.SetEnabled(false);
            _liveEntrySection.Add(_removeEntryButton);

            VisualElement addEntryRow = new VisualElement();
            addEntryRow.style.flexDirection = FlexDirection.Row;
            _addEntryJson = new TextField { multiline = true };
            _addEntryJson.name = ViewerUI.AddEntryJsonName;
            _addEntryJson.style.flexGrow = 1;
            addEntryRow.Add(_addEntryJson);
            Button addEntryButton = new Button(OnAddEntry) { text = "Add Entry" };
            addEntryButton.name = ViewerUI.AddEntryButtonName;
            addEntryRow.Add(addEntryButton);
            _liveEntrySection.Add(addEntryRow);

            _documentInfo = new Label();
            _documentInfo.name = ViewerUI.DocumentInfoName;
            root.Add(_documentInfo);

            _staleHint = new Label("Live data changed. Unapplied edits are kept; Apply or Revert to sync.");
            _staleHint.name = ViewerUI.StaleHintName;
            _staleHint.style.display = DisplayStyle.None;
            root.Add(_staleHint);

            VisualElement editRow = new VisualElement();
            editRow.style.flexDirection = FlexDirection.Row;
            _applyButton = new Button(OnApply) { text = "Apply" };
            _applyButton.name = ViewerUI.ApplyButtonName;
            _applyButton.SetEnabled(false);
            editRow.Add(_applyButton);
            _revertButton = new Button(OnRevert) { text = "Revert" };
            _revertButton.name = ViewerUI.RevertButtonName;
            _revertButton.SetEnabled(false);
            editRow.Add(_revertButton);
            root.Add(editRow);

            _applyError = new HelpBox(string.Empty, HelpBoxMessageType.Error);
            _applyError.name = ViewerUI.ApplyErrorName;
            _applyError.style.display = DisplayStyle.None;
            root.Add(_applyError);

            VisualElement tabRow = new VisualElement();
            tabRow.style.flexDirection = FlexDirection.Row;
            _fieldsTabButton = new Button(() => SetActiveTab(showFields: true)) { text = "Fields" };
            _fieldsTabButton.name = ViewerUI.FieldsTabButtonName;
            tabRow.Add(_fieldsTabButton);
            _jsonTabButton = new Button(() => SetActiveTab(showFields: false)) { text = "JSON" };
            _jsonTabButton.name = ViewerUI.JsonTabButtonName;
            tabRow.Add(_jsonTabButton);
            root.Add(tabRow);

            _fieldsScroll = new ScrollView();
            _fieldsScroll.name = ViewerUI.FieldsScrollName;
            _fieldsScroll.style.flexGrow = 1;
            _fieldsHint = new Label();
            _fieldsHint.name = ViewerUI.FieldsHintName;
            _fieldsHint.style.display = DisplayStyle.None;
            _fieldsScroll.Add(_fieldsHint);
            _fieldsSection = new VisualElement();
            _fieldsSection.name = ViewerUI.FieldsSectionName;
            _fieldsScroll.Add(_fieldsSection);
            root.Add(_fieldsScroll);

            _jsonScroll = new ScrollView();
            _jsonScroll.name = ViewerUI.JsonScrollName;
            _jsonScroll.style.flexGrow = 1;
            _documentJson = new TextField { multiline = true, isReadOnly = true };
            _documentJson.name = ViewerUI.DocumentJsonName;
            _jsonScroll.Add(_documentJson);
            root.Add(_jsonScroll);

            // Fields is the default tab: typed editors with validated input are the safer surface.
            SetActiveTab(showFields: true);

            _registryChangedHandler = OnRegistryChanged;
            LiveSessionRegistry.Changed += _registryChangedHandler;
            RebuildSources();
            RefreshPlayModeBanner();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            LiveSessionRegistry.Changed -= _registryChangedHandler;
            DisposeLiveView();
        }

        internal void RefreshPlayModeBanner()
        {
            bool playing = EditorApplication.isPlayingOrWillChangePlaymode;
            bool live = _liveView is not null;
            // In live mode edits go through the session's own APIs, so the disk-overwrite warning
            // does not apply; a lightweight indicator replaces it.
            _playModeWarning.style.display = playing && !live ? DisplayStyle.Flex : DisplayStyle.None;
            _liveIndicator.style.display = live ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // ---- Fields / JSON tabs ----

        // Each tab keeps its own unapplied edits across switches (nothing is discarded or merged);
        // Apply always materializes the active tab's content.
        private void SetActiveTab(bool showFields)
        {
            _fieldsTabActive = showFields;
            // The active tab's button is disabled: it doubles as the "current tab" indicator and
            // makes re-clicking it a no-op.
            _fieldsTabButton.SetEnabled(!showFields);
            _jsonTabButton.SetEnabled(showFields);
            _fieldsScroll.style.display = showFields ? DisplayStyle.Flex : DisplayStyle.None;
            _jsonScroll.style.display = showFields ? DisplayStyle.None : DisplayStyle.Flex;
            UpdateApplyEnabled();
        }

        private void SetSurfaceEditable(bool editable)
        {
            _surfaceEditable = editable;
            _revertButton.SetEnabled(editable);
            UpdateApplyEnabled();
        }

        private void UpdateApplyEnabled()
        {
            bool enabled = _surfaceEditable;
            if (_fieldsTabActive)
                enabled = enabled && _fieldsModel is not null && !_fieldsModel.HasInvalid;
            _applyButton.SetEnabled(enabled);
        }

        private void RebuildFields(string? json, Type? documentType, bool jsonOnly, bool editable)
        {
            _fieldsSection.Clear();
            _fieldsModel = null;
            _fieldsView = null;
            _fieldsDocType = jsonOnly ? null : documentType;
            _fieldsJsonOnly = jsonOnly;

            if (jsonOnly)
            {
                ShowFieldsHint($"Collection payload — {FieldEditorModel.JsonOnlyHint}.");
            }
            else if (string.IsNullOrEmpty(json) || documentType is null)
            {
                HideFieldsHint();
            }
            else
            {
                try
                {
                    _fieldsModel = FieldEditorModel.Create(json, documentType);
                }
                catch (Exception ex)
                {
                    ShowFieldsHint($"Cannot build field editors ({ex.Message}) — {FieldEditorModel.JsonOnlyHint}.");
                }

                if (_fieldsModel is not null)
                {
                    HideFieldsHint();
                    _fieldsView = new FieldEditorView(_fieldsModel);
                    _fieldsView.Changed += OnFieldsEdited;
                    _fieldsSection.Add(_fieldsView.Root);
                    _fieldsSection.SetEnabled(editable);
                }
            }

            UpdateApplyEnabled();
        }

        private void OnFieldsEdited() => UpdateApplyEnabled();

        private bool FieldsHasUnappliedEdits => _fieldsModel is not null && _fieldsModel.IsDirty;

        // False only when the Fields tab is active without an applicable model (no document,
        // collection payload, or invalid numeric input) — Apply is disabled in those states too.
        private bool TryGetEditedPayload(out string payload)
        {
            if (_fieldsTabActive)
            {
                if (_fieldsModel is null || _fieldsModel.HasInvalid)
                {
                    payload = string.Empty;
                    return false;
                }

                payload = _fieldsModel.ToJson();
                return true;
            }

            payload = _documentJson.value;
            return true;
        }

        private void ShowFieldsHint(string message)
        {
            _fieldsHint.text = message;
            _fieldsHint.style.display = DisplayStyle.Flex;
        }

        private void HideFieldsHint()
        {
            _fieldsHint.style.display = DisplayStyle.None;
        }

        // ---- Throttled live refresh ----

        // `now` is injected (EditorApplication.timeSinceStartup in production) so tests can drive
        // the throttle without waiting on wall-clock time.
        internal void Tick(double now)
        {
            if (_disposed || _liveView is null)
                return;

            if (_liveView.ConsumeChangeFlag())
                _pendingLiveChange = true;
            if (!_pendingLiveChange || now - _lastLiveRefreshTime < LiveRefreshIntervalSeconds)
                return;

            _pendingLiveChange = false;
            _lastLiveRefreshTime = now;
            RefreshLiveData();
        }

        // ---- Source selection ----

        private void OnSourceChanged()
        {
            int index = _sourceDropdown.index;
            if (index <= 0)
                SwitchToDisk();
            else if (index - 1 < _liveSources.Count)
                SwitchToLive(_liveSources[index - 1]);
        }

        private void OnRegistryChanged()
        {
            if (_disposed)
                return;
            RebuildSources();
        }

        private void RebuildSources()
        {
            IReadOnlyList<LiveSessionEntry> entries = LiveSessionRegistry.Entries;
            _liveSources.Clear();

            // Occurrence-count first so duplicate names (and a clash with "Disk") get an index suffix.
            Dictionary<string, int> totals = new Dictionary<string, int>(StringComparer.Ordinal);
            totals[ViewerUI.DiskSourceLabel] = 1;
            foreach (LiveSessionEntry entry in entries)
                totals[entry.Name] = totals.TryGetValue(entry.Name, out int count) ? count + 1 : 1;

            List<string> choices = new List<string> { ViewerUI.DiskSourceLabel };
            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (LiveSessionEntry entry in entries)
            {
                _liveSources.Add(entry);
                seen[entry.Name] = seen.TryGetValue(entry.Name, out int occurrence) ? occurrence + 1 : 1;
                choices.Add(totals[entry.Name] > 1 ? $"{entry.Name} ({seen[entry.Name]})" : entry.Name);
            }

            int newIndex = 0;
            if (_liveSession is not null)
            {
                for (int i = 0; i < _liveSources.Count; i++)
                {
                    if (ReferenceEquals(_liveSources[i].Session, _liveSession))
                    {
                        newIndex = i + 1;
                        break;
                    }
                }
            }

            _sourceDropdown.choices = choices;
            _sourceDropdown.SetValueWithoutNotify(choices[newIndex]);
            if (newIndex == 0 && _liveView is not null)
                SwitchToDisk();
        }

        private void SwitchToDisk()
        {
            DisposeLiveView();
            _liveSection.style.display = DisplayStyle.None;
            _diskSection.style.display = DisplayStyle.Flex;
            HideStaleHint();
            RefreshDocuments(_diskSelectedStorageKey);
            _diskSelectedStorageKey = null;
            RefreshPlayModeBanner();
        }

        private void SwitchToLive(LiveSessionEntry entry)
        {
            if (_liveView is not null && ReferenceEquals(_liveSession, entry.Session))
                return;

            if (_liveView is null)
                _diskSelectedStorageKey = _selectedStorageKey;
            DisposeLiveView();

            _liveView = new LiveSessionView(entry.Session);
            _liveSession = entry.Session;
            _diskSection.style.display = DisplayStyle.None;
            _liveSection.style.display = DisplayStyle.Flex;

            _liveDocuments.Clear();
            _liveDocuments.AddRange(_liveView.Documents);
            _liveDocumentsList.ClearSelection();
            _liveDocumentsList.RefreshItems();

            _liveEntrySection.style.display = DisplayStyle.None;
            _addEntryJson.SetValueWithoutNotify(string.Empty);
            _documentInfo.text = string.Empty;
            HideApplyError();
            HideStaleHint();
            ClearJsonEditor();
            RefreshPlayModeBanner();
        }

        private void DisposeLiveView()
        {
            _liveView?.Dispose();
            _liveView = null;
            _liveSession = null;
            _liveSelectedProperty = null;
            _liveSelectedEntryKey = null;
            _lastLoadedLiveJson = null;
            _pendingLiveChange = false;
            _lastLiveRefreshTime = double.NegativeInfinity;
        }

        // ---- Live mode ----

        private void ShowSelectedLiveDocument()
        {
            if (_liveView is null)
                return;
            int index = _liveDocumentsList.selectedIndex;
            if (index < 0 || index >= _liveDocuments.Count)
                return;

            LiveDocumentDescriptor descriptor = _liveDocuments[index];
            _liveSelectedProperty = descriptor.PropertyName;
            _liveSelectedEntryKey = null;
            HideApplyError();
            HideStaleHint();

            if (descriptor.IsCollection)
            {
                _liveEntrySection.style.display = DisplayStyle.Flex;
                _documentInfo.text = $"{descriptor.PropertyName} — collection of {descriptor.EntityType.Name} (key: {descriptor.KeyType!.Name})";
                ClearJsonEditor();
                RefreshLiveEntryKeys(preserveKey: null);
                return;
            }

            _liveEntrySection.style.display = DisplayStyle.None;
            bool canEdit = _liveView.CanEdit(descriptor.PropertyName, out string? reason);
            string info = $"{descriptor.PropertyName} — {descriptor.EntityType.Name}";
            if (!canEdit)
                info += $" — view-only: {reason}";

            string? json = null;
            try
            {
                json = _liveView.GetJson(descriptor.PropertyName);
            }
            catch (Exception ex)
            {
                info += $" — JSON error: {ex.Message}";
                canEdit = false;
            }

            _documentInfo.text = info;
            SetLiveJson(json ?? string.Empty, canEdit, descriptor.EntityType);
        }

        private void ShowSelectedLiveEntry()
        {
            if (_liveView is null || _liveSelectedProperty is null)
                return;
            int index = _liveEntriesList.selectedIndex;
            if (index < 0 || index >= _liveEntryKeys.Count)
                return;

            _liveSelectedEntryKey = _liveEntryKeys[index];
            HideApplyError();
            HideStaleHint();

            string json;
            try
            {
                json = _liveView.GetEntryJson(_liveSelectedProperty, _liveSelectedEntryKey);
            }
            catch (KeyNotFoundException)
            {
                // The game removed the entry between the last list refresh and this click.
                _liveSelectedEntryKey = null;
                RefreshLiveEntryKeys(preserveKey: null);
                ClearJsonEditor();
                return;
            }

            _removeEntryButton.SetEnabled(true);
            _documentInfo.text = $"{_liveSelectedProperty}[{_liveSelectedEntryKey}]";
            SetLiveJson(json, editable: true, FindLiveDescriptor(_liveSelectedProperty)?.EntityType);
        }

        private void OnAddEntry()
        {
            if (_liveView is null || _liveSelectedProperty is null)
                return;

            if (_liveView.AddEntryJson(_liveSelectedProperty, _addEntryJson.value, out string? error))
            {
                HideApplyError();
                _addEntryJson.SetValueWithoutNotify(string.Empty);
                RefreshLiveEntryKeys(_liveSelectedEntryKey);
            }
            else
            {
                ShowApplyError(error);
            }
        }

        private void OnRemoveEntry()
        {
            if (_liveView is null || _liveSelectedProperty is null || _liveSelectedEntryKey is null)
                return;

            // False means the game already removed it; the list refresh below covers both cases.
            _liveView.RemoveEntry(_liveSelectedProperty, _liveSelectedEntryKey);
            _liveSelectedEntryKey = null;
            HideApplyError();
            HideStaleHint();
            RefreshLiveEntryKeys(preserveKey: null);
            ClearJsonEditor();
        }

        private void RefreshLiveEntryKeys(object? preserveKey)
        {
            if (_liveView is null || _liveSelectedProperty is null)
                return;

            _liveEntryKeys.Clear();
            _liveEntryKeys.AddRange(_liveView.GetEntryKeys(_liveSelectedProperty));
            _liveEntriesList.RefreshItems();

            int preservedIndex = preserveKey is null ? -1 : _liveEntryKeys.IndexOf(preserveKey);
            if (preservedIndex >= 0)
            {
                // Without-notify: re-selecting must not reload the JSON field (it may hold edits).
                _liveEntriesList.SetSelectionWithoutNotify(new[] { preservedIndex });
            }
            else
            {
                _liveEntriesList.ClearSelection();
                _removeEntryButton.SetEnabled(false);
            }
        }

        private void RefreshLiveData()
        {
            if (_liveView is null || _liveSelectedProperty is null)
                return;
            LiveDocumentDescriptor? descriptor = FindLiveDescriptor(_liveSelectedProperty);
            if (descriptor is null)
                return;

            bool hasUnappliedEdits = (!_documentJson.isReadOnly
                && !string.Equals(_documentJson.value, _lastLoadedLiveJson ?? string.Empty, StringComparison.Ordinal))
                || FieldsHasUnappliedEdits;

            if (descriptor.IsCollection)
            {
                RefreshLiveEntryKeys(_liveSelectedEntryKey);
                if (_liveSelectedEntryKey is null)
                    return;

                if (!_liveEntryKeys.Contains(_liveSelectedEntryKey))
                {
                    if (hasUnappliedEdits)
                    {
                        ShowStaleHint();
                        return;
                    }

                    _liveSelectedEntryKey = null;
                    ClearJsonEditor();
                    return;
                }

                ApplyRefreshedJson(_liveView.GetEntryJson(_liveSelectedProperty, _liveSelectedEntryKey), hasUnappliedEdits);
                return;
            }

            ApplyRefreshedJson(_liveView.GetJson(_liveSelectedProperty), hasUnappliedEdits);
        }

        private void ApplyRefreshedJson(string json, bool hasUnappliedEdits)
        {
            if (string.Equals(json, _lastLoadedLiveJson, StringComparison.Ordinal))
                return; // The visible document did not change (the flag covers the whole session).

            if (hasUnappliedEdits)
            {
                ShowStaleHint();
                return;
            }

            _lastLoadedLiveJson = json;
            _documentJson.SetValueWithoutNotify(json);
            RebuildFields(json, _fieldsDocType, _fieldsJsonOnly, _surfaceEditable);
            HideStaleHint();
        }

        private void OnApplyLive()
        {
            if (_liveView is null || _liveSelectedProperty is null)
                return;
            LiveDocumentDescriptor? descriptor = FindLiveDescriptor(_liveSelectedProperty);
            if (descriptor is null)
                return;
            if (!TryGetEditedPayload(out string payload))
                return;

            bool applied;
            string? error;
            if (descriptor.IsCollection)
            {
                if (_liveSelectedEntryKey is null)
                    return;
                applied = _liveView.ApplyEntryJson(_liveSelectedProperty, _liveSelectedEntryKey, payload, out error);
            }
            else
            {
                applied = _liveView.ApplyJson(_liveSelectedProperty, payload, out error);
            }

            if (applied)
            {
                HideApplyError();
                HideStaleHint();
                ReloadLiveJson();
            }
            else
            {
                ShowApplyError(error);
            }
        }

        private void ReloadLiveJson()
        {
            if (_liveView is null || _liveSelectedProperty is null)
                return;
            LiveDocumentDescriptor? descriptor = FindLiveDescriptor(_liveSelectedProperty);
            if (descriptor is null)
                return;

            string json;
            if (descriptor.IsCollection)
            {
                if (_liveSelectedEntryKey is null)
                {
                    ClearJsonEditor();
                    return;
                }

                try
                {
                    json = _liveView.GetEntryJson(_liveSelectedProperty, _liveSelectedEntryKey);
                }
                catch (KeyNotFoundException)
                {
                    _liveSelectedEntryKey = null;
                    RefreshLiveEntryKeys(preserveKey: null);
                    ClearJsonEditor();
                    return;
                }
            }
            else
            {
                json = _liveView.GetJson(_liveSelectedProperty);
            }

            // Reload is only reachable from Apply/Revert, both gated on an editable document.
            SetLiveJson(json, editable: true, descriptor.EntityType);
        }

        private void SetLiveJson(string json, bool editable, Type? documentType)
        {
            _lastLoadedLiveJson = json;
            _documentJson.SetValueWithoutNotify(json);
            _documentJson.isReadOnly = !editable;
            SetSurfaceEditable(editable);
            RebuildFields(json, documentType, jsonOnly: false, editable);
        }

        private void ClearJsonEditor()
        {
            _lastLoadedLiveJson = null;
            _documentJson.SetValueWithoutNotify(string.Empty);
            _documentJson.isReadOnly = true;
            SetSurfaceEditable(false);
            RebuildFields(json: null, documentType: null, jsonOnly: false, editable: false);
        }

        private LiveDocumentDescriptor? FindLiveDescriptor(string propertyName)
        {
            foreach (LiveDocumentDescriptor descriptor in _liveDocuments)
            {
                if (string.Equals(descriptor.PropertyName, propertyName, StringComparison.Ordinal))
                    return descriptor;
            }

            return null;
        }

        private string DescribeLiveDocument(LiveDocumentDescriptor descriptor)
        {
            string kind = descriptor.IsCollection ? "collection" : "doc";
            bool editable = descriptor.IsCollection
                || (_liveView is not null && _liveView.CanEdit(descriptor.PropertyName, out _));
            return $"{descriptor.PropertyName}  [{kind}]  {descriptor.EntityType.Name}  {(editable ? "editable" : "view-only")}";
        }

        private void ShowStaleHint()
        {
            _staleHint.style.display = DisplayStyle.Flex;
        }

        private void HideStaleHint()
        {
            _staleHint.style.display = DisplayStyle.None;
        }

        private void ShowApplyError(string? error)
        {
            _applyError.text = error ?? "Apply failed.";
            _applyError.style.display = DisplayStyle.Flex;
        }

        // ---- Test hooks ----
        // Detached elements (no panel) do not dispatch ChangeEvent/ClickEvent, so EditMode tests
        // drive the same handlers the controls are wired to.

        internal void SelectSourceForTests(int index)
        {
            _sourceDropdown.index = index;
            OnSourceChanged();
        }

        internal void SelectLiveDocumentForTests(int index)
        {
            _liveDocumentsList.SetSelection(index);
            ShowSelectedLiveDocument();
        }

        internal void SelectLiveEntryForTests(int index)
        {
            _liveEntriesList.SetSelection(index);
            ShowSelectedLiveEntry();
        }

        internal void SelectSessionForTests(int index)
        {
            _sessionDropdown.index = index;
            OnSessionChanged();
        }

        internal void ScanForTests(string rootPath)
        {
            _rootPath.SetValueWithoutNotify(rootPath);
            OnScan();
        }

        internal void SelectSaveForTests(int index)
        {
            _saveDropdown.index = index;
            OnSaveChanged();
        }

        internal void SelectDocumentForTests(int index)
        {
            _documentsList.SetSelection(index);
            ShowSelectedDocument();
        }

        internal void SelectTabForTests(bool showFields) => SetActiveTab(showFields);

        internal void ApplyForTests() => OnApply();

        internal FieldEditorView? FieldsViewForTests => _fieldsView;

        internal FieldEditorModel? FieldsModelForTests => _fieldsModel;

        // ---- Disk mode ----

        private void OnSessionChanged()
        {
            int index = _sessionDropdown.index;
            if (index < 0 || index >= _controller.SessionTypes.Count)
                return;

            _controller.SelectSession(_controller.SessionTypes[index]);
            RefreshSchemaDiagnostics();
            RefreshDocuments();
        }

        private void OnScan()
        {
            _controller.Scan(_rootPath.value);

            List<string> labels = new List<string>();
            foreach (SaveLocation location in _controller.Saves)
                labels.Add(SaveLabel(location));
            _saveDropdown.choices = labels;
            _saveDropdown.SetValueWithoutNotify(string.Empty);

            RefreshDocuments();
        }

        private void OnReload()
        {
            _controller.Reload();
            RefreshDocuments();
        }

        private void OnSaveChanged()
        {
            int index = _saveDropdown.index;
            if (index < 0 || index >= _controller.Saves.Count)
                return;

            _controller.SelectSave(_controller.Saves[index]);
            RefreshDocuments();
        }

        private void RefreshSchemaDiagnostics()
        {
            IReadOnlyList<string> diagnostics = _controller.Schema?.Diagnostics ?? Array.Empty<string>();
            if (diagnostics.Count == 0)
            {
                _schemaDiagnostics.style.display = DisplayStyle.None;
                return;
            }

            StringBuilder text = new StringBuilder("Skipped document declarations:");
            foreach (string diagnostic in diagnostics)
                text.Append('\n').Append("- ").Append(diagnostic);
            _schemaDiagnostics.text = text.ToString();
            _schemaDiagnostics.style.display = DisplayStyle.Flex;
        }

        private void OnApply()
        {
            if (_liveView is not null)
            {
                // Live edits go through the session's own APIs; the disk-overwrite dialog below
                // only concerns writing files behind a running game's back.
                OnApplyLive();
                return;
            }

            if (_selectedStorageKey is null)
                return;
            if (!TryGetEditedPayload(out string payload))
                return;

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                bool proceed = EditorUtility.DisplayDialog(
                    "Apply during play mode?",
                    "Play mode is active. A live session's next commit can overwrite this edit (last write wins). Apply anyway?",
                    "Apply",
                    "Cancel");
                if (!proceed)
                    return;
            }

            string storageKey = _selectedStorageKey;
            if (_controller.ApplyJson(storageKey, payload, out string? error))
            {
                HideApplyError();
                RefreshDocuments(storageKey);
            }
            else
            {
                ShowApplyError(error);
            }
        }

        private void OnRevert()
        {
            if (_liveView is not null)
            {
                HideApplyError();
                HideStaleHint();
                ReloadLiveJson();
                return;
            }

            HideApplyError();
            ShowSelectedDocument();
        }

        private void HideApplyError()
        {
            _applyError.text = string.Empty;
            _applyError.style.display = DisplayStyle.None;
        }

        private void RefreshDocuments(string? preserveSelectedKey = null)
        {
            _documents.Clear();
            if (_controller.CurrentSave is not null)
                _documents.AddRange(_controller.CurrentSave.Documents);

            string? loadError = _controller.LoadError;
            _loadError.text = loadError ?? string.Empty;
            _loadError.style.display = loadError is null ? DisplayStyle.None : DisplayStyle.Flex;

            HideApplyError();
            _documentsList.ClearSelection();
            _documentsList.RefreshItems();
            _selectedStorageKey = null;
            _documentInfo.text = string.Empty;
            _documentJson.SetValueWithoutNotify(string.Empty);
            _documentJson.isReadOnly = true;
            SetSurfaceEditable(false);
            RebuildFields(json: null, documentType: null, jsonOnly: false, editable: false);

            if (preserveSelectedKey is not null)
            {
                for (int i = 0; i < _documents.Count; i++)
                {
                    if (string.Equals(_documents[i].StorageKey, preserveSelectedKey, StringComparison.Ordinal))
                    {
                        _documentsList.SetSelection(i);
                        break;
                    }
                }
            }
        }

        private void ShowSelectedDocument()
        {
            int index = _documentsList.selectedIndex;
            if (index < 0 || index >= _documents.Count)
                return;

            DocumentView? view = _controller.GetDocumentView(_documents[index].StorageKey);
            if (view is null)
                return;

            _selectedStorageKey = view.Entry.StorageKey;
            HideApplyError();

            string info = $"{view.Entry.StorageKey} — {view.Entry.State}";
            if (view.Entry.StateReason is not null)
                info += $" — {view.Entry.StateReason}";
            if (view.JsonError is not null)
                info += $" — JSON error: {view.JsonError}";
            _documentInfo.text = info;

            _documentJson.SetValueWithoutNotify(view.Json ?? string.Empty);
            _documentJson.isReadOnly = !view.CanEdit;
            SetSurfaceEditable(view.CanEdit);
            // Disk collection payloads (dictionaries) have no top-level members to edit;
            // the Fields tab shows the JSON-only hint for them instead.
            bool isCollection = view.Entry.Descriptor?.IsCollection == true;
            RebuildFields(view.Json, isCollection ? null : view.Entry.Descriptor?.PayloadType, jsonOnly: isCollection, editable: view.CanEdit);
        }

        private static string SaveLabel(SaveLocation location) =>
            location.Slot is int slot ? $"{location.Directory} (slot {slot})" : location.Directory;

        private static string DescribeDocument(DocumentEntry entry)
        {
            string typeName = entry.Descriptor?.DocumentType.Name ?? "?";
            return $"{entry.StorageKey}  [{entry.State}]  {typeName}  {entry.SizeBytes} B";
        }
    }
}
