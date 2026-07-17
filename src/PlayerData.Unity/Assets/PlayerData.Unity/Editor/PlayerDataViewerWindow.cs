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

        [MenuItem(PlayerDataEditorMenu.WindowMenuPath)]
        public static void Open()
        {
            PlayerDataViewerWindow window = GetWindow<PlayerDataViewerWindow>();
            window.titleContent = new GUIContent("PlayerData Viewer");
        }

        public void CreateGUI()
        {
            ViewerUI.BuildInto(rootVisualElement, _controller, Application.persistentDataPath);
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            ViewerUI.UpdatePlayModeWarning(rootVisualElement);
        }
    }

    // UI construction is separated from the EditorWindow lifecycle so EditMode tests can build
    // and inspect the hierarchy without relying on window layout callbacks (which are not
    // guaranteed to run in batch mode).
    internal static class ViewerUI
    {
        internal const string PlayModeWarningName = "playmode-warning";
        internal const string SessionDropdownName = "session-dropdown";
        internal const string RootPathName = "root-path";
        internal const string ScanButtonName = "scan-button";
        internal const string ReloadButtonName = "reload-button";
        internal const string SaveDropdownName = "save-dropdown";
        internal const string SchemaDiagnosticsName = "schema-diagnostics";
        internal const string LoadErrorName = "load-error";
        internal const string DocumentsListName = "documents-list";
        internal const string DocumentInfoName = "document-info";
        internal const string DocumentJsonName = "document-json";
        internal const string ApplyButtonName = "apply-button";
        internal const string RevertButtonName = "revert-button";
        internal const string ApplyErrorName = "apply-error";

        internal static ViewerPanel BuildInto(VisualElement root, PlayerDataViewerController controller, string defaultRootPath)
        {
            return new ViewerPanel(root, controller, defaultRootPath);
        }

        internal static void UpdatePlayModeWarning(VisualElement root)
        {
            HelpBox warning = root.Q<HelpBox>(PlayModeWarningName);
            if (warning is not null)
            {
                warning.style.display = EditorApplication.isPlayingOrWillChangePlaymode
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;
            }
        }
    }

    internal sealed class ViewerPanel
    {
        private readonly PlayerDataViewerController _controller;
        private readonly DropdownField _sessionDropdown;
        private readonly TextField _rootPath;
        private readonly DropdownField _saveDropdown;
        private readonly HelpBox _schemaDiagnostics;
        private readonly HelpBox _loadError;
        private readonly ListView _documentsList;
        private readonly Label _documentInfo;
        private readonly TextField _documentJson;
        private readonly Button _applyButton;
        private readonly Button _revertButton;
        private readonly HelpBox _applyError;
        private readonly List<DocumentEntry> _documents = new List<DocumentEntry>();
        private string? _selectedStorageKey;

        internal ViewerPanel(VisualElement root, PlayerDataViewerController controller, string defaultRootPath)
        {
            _controller = controller;
            controller.RefreshSessionTypes();
            root.Clear();

            HelpBox playModeWarning = new HelpBox(
                "Play mode is active: a live session's next commit can overwrite what is shown here.",
                HelpBoxMessageType.Warning);
            playModeWarning.name = ViewerUI.PlayModeWarningName;
            root.Add(playModeWarning);

            List<string> sessionNames = new List<string>();
            foreach (Type type in controller.SessionTypes)
                sessionNames.Add(type.FullName);
            _sessionDropdown = new DropdownField("Session", sessionNames, -1);
            _sessionDropdown.name = ViewerUI.SessionDropdownName;
            _sessionDropdown.RegisterValueChangedCallback(_ => OnSessionChanged());
            root.Add(_sessionDropdown);

            _rootPath = new TextField("Root path") { value = defaultRootPath };
            _rootPath.name = ViewerUI.RootPathName;
            root.Add(_rootPath);

            VisualElement buttonRow = new VisualElement();
            buttonRow.style.flexDirection = FlexDirection.Row;
            Button scanButton = new Button(OnScan) { text = "Scan" };
            scanButton.name = ViewerUI.ScanButtonName;
            buttonRow.Add(scanButton);
            Button reloadButton = new Button(OnReload) { text = "Reload" };
            reloadButton.name = ViewerUI.ReloadButtonName;
            buttonRow.Add(reloadButton);
            root.Add(buttonRow);

            _saveDropdown = new DropdownField("Save", new List<string>(), -1);
            _saveDropdown.name = ViewerUI.SaveDropdownName;
            _saveDropdown.RegisterValueChangedCallback(_ => OnSaveChanged());
            root.Add(_saveDropdown);

            _schemaDiagnostics = new HelpBox(string.Empty, HelpBoxMessageType.Warning);
            _schemaDiagnostics.name = ViewerUI.SchemaDiagnosticsName;
            _schemaDiagnostics.style.display = DisplayStyle.None;
            root.Add(_schemaDiagnostics);

            _loadError = new HelpBox(string.Empty, HelpBoxMessageType.Error);
            _loadError.name = ViewerUI.LoadErrorName;
            _loadError.style.display = DisplayStyle.None;
            root.Add(_loadError);

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
            root.Add(_documentsList);

            _documentInfo = new Label();
            _documentInfo.name = ViewerUI.DocumentInfoName;
            root.Add(_documentInfo);

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

            ScrollView jsonScroll = new ScrollView();
            jsonScroll.style.flexGrow = 1;
            _documentJson = new TextField { multiline = true, isReadOnly = true };
            _documentJson.name = ViewerUI.DocumentJsonName;
            jsonScroll.Add(_documentJson);
            root.Add(jsonScroll);

            ViewerUI.UpdatePlayModeWarning(root);
        }

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
            if (_selectedStorageKey is null)
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
            if (_controller.ApplyJson(storageKey, _documentJson.value, out string? error))
            {
                HideApplyError();
                RefreshDocuments(storageKey);
            }
            else
            {
                _applyError.text = error ?? "Apply failed.";
                _applyError.style.display = DisplayStyle.Flex;
            }
        }

        private void OnRevert()
        {
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
            _applyButton.SetEnabled(false);
            _revertButton.SetEnabled(false);

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
            _applyButton.SetEnabled(view.CanEdit);
            _revertButton.SetEnabled(view.CanEdit);
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
