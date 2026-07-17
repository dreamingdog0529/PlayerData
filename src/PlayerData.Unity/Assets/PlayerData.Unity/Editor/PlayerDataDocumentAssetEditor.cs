using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace PlayerData.Unity.Editor
{
    /// <summary>
    /// Inspector for <see cref="PlayerDataDocumentAsset"/>: session/document pickers, Fields
    /// editors (singles), JSON (collections / advanced), Apply to asset, Export to save folder.
    /// </summary>
    [CustomEditor(typeof(PlayerDataDocumentAsset))]
    public sealed class PlayerDataDocumentAssetEditor : UnityEditor.Editor
    {
        private const string RootName = "playerdata-document-asset-root";
        private const string StatusName = "playerdata-document-asset-status";
        private const string FieldsName = "playerdata-document-asset-fields";
        private const string JsonName = "playerdata-document-asset-json";
        private const string ExportPathName = "playerdata-document-asset-export-path";

        private PlayerDataDocumentAsset Asset => (PlayerDataDocumentAsset)target;

        private VisualElement _root = null!;
        private HelpBox _status = null!;
        private DropdownField _sessionDropdown = null!;
        private DropdownField _documentDropdown = null!;
        private VisualElement _fieldsSection = null!;
        private TextField _jsonField = null!;
        private TextField _exportPath = null!;
        private FieldEditorModel? _fieldsModel;
        private FieldEditorView? _fieldsView;
        private DocumentDescriptor? _descriptor;
        private bool _suppressCallbacks;

        public override VisualElement CreateInspectorGUI()
        {
            _root = new VisualElement { name = RootName };

            _status = new HelpBox(string.Empty, HelpBoxMessageType.Info) { name = StatusName };
            _root.Add(_status);

            _sessionDropdown = new DropdownField("Save type");
            _sessionDropdown.RegisterValueChangedCallback(OnSessionChanged);
            _root.Add(_sessionDropdown);

            _documentDropdown = new DropdownField("Document");
            _documentDropdown.RegisterValueChangedCallback(OnDocumentChanged);
            _root.Add(_documentDropdown);

            _fieldsSection = new VisualElement { name = FieldsName };
            _root.Add(_fieldsSection);

            _jsonField = new TextField("JSON") { multiline = true, name = JsonName };
            _jsonField.style.minHeight = 120;
            _jsonField.RegisterValueChangedCallback(_ => OnJsonEdited());
            _root.Add(_jsonField);

            VisualElement buttonRow = new VisualElement();
            buttonRow.style.flexDirection = FlexDirection.Row;
            Button applyButton = new Button(OnApply) { text = "Apply to Asset" };
            buttonRow.Add(applyButton);
            Button revertButton = new Button(OnRevert) { text = "Revert" };
            buttonRow.Add(revertButton);
            _root.Add(buttonRow);

            _exportPath = new TextField("Export folder") { name = ExportPathName, value = string.Empty };
            _root.Add(_exportPath);

            VisualElement exportRow = new VisualElement();
            exportRow.style.flexDirection = FlexDirection.Row;
            Button browseButton = new Button(OnBrowseExport) { text = "Browse…" };
            exportRow.Add(browseButton);
            Button exportButton = new Button(OnExport) { text = "Export to Save Folder" };
            exportRow.Add(exportButton);
            _root.Add(exportRow);

            // Keep serialized fields in sync if the user edits via the default property path.
            _root.TrackSerializedObjectValue(serializedObject, _ => RebuildFromAsset());

            RebuildFromAsset();
            return _root;
        }

        private void RebuildFromAsset()
        {
            _suppressCallbacks = true;
            try
            {
                List<string> sessions = PlayerDataDocumentAssetUtility.SessionTypeChoices();
                _sessionDropdown.choices = sessions;
                string session = Asset.SessionTypeName;
                if (!string.IsNullOrEmpty(session) && !sessions.Contains(session))
                {
                    // Keep a missing type visible so the user sees it failed to resolve.
                    sessions = new List<string>(sessions) { session };
                    _sessionDropdown.choices = sessions;
                }

                _sessionDropdown.SetValueWithoutNotify(
                    string.IsNullOrEmpty(session) && sessions.Count > 0 ? string.Empty : session);

                RefreshDocumentChoices();
                _jsonField.SetValueWithoutNotify(Asset.Json);
                RebuildFields();
                RefreshStatus();
            }
            finally
            {
                _suppressCallbacks = false;
            }
        }

        private void RefreshDocumentChoices()
        {
            List<string> keys = PlayerDataDocumentAssetUtility.DocumentKeyChoices(Asset.SessionTypeName);
            List<string> labels = new List<string>(keys.Count);
            Type? sessionType = PlayerDataDocumentAssetUtility.FindSessionType(Asset.SessionTypeName);
            Dictionary<string, string> labelToKey = new Dictionary<string, string>(StringComparer.Ordinal);
            if (sessionType is not null)
            {
                foreach (DocumentDescriptor document in SessionSchemaResolver.Resolve(sessionType).Documents)
                {
                    string label = PlayerDataDocumentAssetUtility.DocumentChoiceLabel(document);
                    labels.Add(label);
                    labelToKey[label] = document.StorageKey;
                }
            }
            else
            {
                foreach (string key in keys)
                {
                    labels.Add(key);
                    labelToKey[key] = key;
                }
            }

            _documentDropdown.userData = labelToKey;
            _documentDropdown.choices = labels;

            string selectedLabel = string.Empty;
            foreach (KeyValuePair<string, string> pair in labelToKey)
            {
                if (string.Equals(pair.Value, Asset.StorageKey, StringComparison.Ordinal))
                {
                    selectedLabel = pair.Key;
                    break;
                }
            }

            _documentDropdown.SetValueWithoutNotify(selectedLabel);
        }

        private void RebuildFields()
        {
            _fieldsSection.Clear();
            _fieldsModel = null;
            _fieldsView = null;
            _descriptor = null;

            if (!PlayerDataDocumentAssetUtility.TryResolve(Asset, out _, out DocumentDescriptor descriptor, out _))
                return;

            _descriptor = descriptor;
            if (descriptor.IsCollection)
            {
                _fieldsSection.Add(new HelpBox(
                    "This document is a list. Edit the whole payload as JSON below (Fields supports single documents).",
                    HelpBoxMessageType.Info));
                return;
            }

            try
            {
                string json = string.IsNullOrWhiteSpace(Asset.Json) ? "{}" : Asset.Json;
                _fieldsModel = FieldEditorModel.Create(json, descriptor.DocumentType);
                _fieldsView = new FieldEditorView(_fieldsModel);
                _fieldsView.Changed += OnFieldsChanged;
                _fieldsSection.Add(_fieldsView.Root);
            }
            catch (Exception ex)
            {
                _fieldsSection.Add(new HelpBox(
                    $"Fields unavailable ({ex.Message}). Edit JSON below.",
                    HelpBoxMessageType.Warning));
            }
        }

        private void RefreshStatus()
        {
            if (PlayerDataDocumentAssetUtility.TryValidate(Asset, out DocumentDescriptor descriptor, out string? error))
            {
                string kind = descriptor.IsCollection ? "list" : "item";
                _status.messageType = HelpBoxMessageType.Info;
                _status.text =
                    $"{descriptor.PropertyName}  ·  {kind}  ·  OK — Apply writes into this asset; Export writes a save folder.";
            }
            else
            {
                _status.messageType = HelpBoxMessageType.Warning;
                _status.text = error ?? "Not ready.";
            }
        }

        private void OnSessionChanged(ChangeEvent<string> evt)
        {
            if (_suppressCallbacks)
                return;

            Undo.RecordObject(Asset, "Change PlayerData session type");
            Asset.SessionTypeName = evt.newValue ?? string.Empty;
            Asset.StorageKey = string.Empty;
            EditorUtility.SetDirty(Asset);

            List<string> keys = PlayerDataDocumentAssetUtility.DocumentKeyChoices(Asset.SessionTypeName);
            if (keys.Count > 0)
            {
                Asset.StorageKey = keys[0];
                if (PlayerDataDocumentAssetUtility.TryResolve(Asset, out _, out DocumentDescriptor descriptor, out _)
                    && PlayerDataDocumentAssetUtility.TryCreateDefaultJson(descriptor, out string json, out _))
                {
                    Asset.Json = json;
                }
            }

            RebuildFromAsset();
        }

        private void OnDocumentChanged(ChangeEvent<string> evt)
        {
            if (_suppressCallbacks)
                return;

            string label = evt.newValue ?? string.Empty;
            string key = label;
            if (_documentDropdown.userData is Dictionary<string, string> map
                && map.TryGetValue(label, out string? mapped))
            {
                key = mapped;
            }

            Undo.RecordObject(Asset, "Change PlayerData document");
            Asset.StorageKey = key;
            if (PlayerDataDocumentAssetUtility.TryResolve(Asset, out _, out DocumentDescriptor descriptor, out _)
                && PlayerDataDocumentAssetUtility.TryCreateDefaultJson(descriptor, out string json, out _))
            {
                Asset.Json = json;
            }

            EditorUtility.SetDirty(Asset);
            RebuildFromAsset();
        }

        private void OnFieldsChanged()
        {
            if (_fieldsModel is null || _fieldsModel.HasInvalid)
                return;

            // Live-update the JSON field so Advanced and Fields stay aligned while editing.
            _jsonField.SetValueWithoutNotify(_fieldsModel.ToJson());
        }

        private void OnJsonEdited()
        {
            // JSON edits stay uncommitted until Apply (same as the Data Viewer).
        }

        private void OnApply()
        {
            string json = _jsonField.value;
            if (_fieldsModel is not null && !_fieldsModel.HasInvalid && _descriptor is not null && !_descriptor.IsCollection)
            {
                // Prefer Fields when they are valid — they are the non-engineer surface.
                // If the user only edited JSON, fields may be stale; when fields are dirty use them.
                if (_fieldsModel.IsDirty)
                    json = _fieldsModel.ToJson();
            }

            Undo.RecordObject(Asset, "Apply PlayerData document");
            if (PlayerDataDocumentAssetUtility.TrySetJson(Asset, json, out string? error))
            {
                EditorUtility.SetDirty(Asset);
                RebuildFromAsset();
                _status.messageType = HelpBoxMessageType.Info;
                _status.text = "Applied to asset.";
            }
            else
            {
                _status.messageType = HelpBoxMessageType.Error;
                _status.text = error ?? "Apply failed.";
            }
        }

        private void OnRevert()
        {
            RebuildFromAsset();
        }

        private void OnBrowseExport()
        {
            string path = EditorUtility.OpenFolderPanel("Export save folder", _exportPath.value, string.Empty);
            if (!string.IsNullOrEmpty(path))
                _exportPath.value = path;
        }

        private void OnExport()
        {
            // Commit current Fields/JSON into the asset first so export matches what the user sees.
            OnApply();
            if (!PlayerDataDocumentAssetUtility.TryValidate(Asset, out _, out _))
                return;

            if (PlayerDataDocumentAssetUtility.TryExportToFolder(Asset, _exportPath.value, out string? error))
            {
                _status.messageType = HelpBoxMessageType.Info;
                _status.text = $"Exported to {_exportPath.value}";
            }
            else
            {
                _status.messageType = HelpBoxMessageType.Error;
                _status.text = error ?? "Export failed.";
            }
        }
    }
}
