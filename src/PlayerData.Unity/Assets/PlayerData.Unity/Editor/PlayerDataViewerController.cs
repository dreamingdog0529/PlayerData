using System;
using System.Collections.Generic;
using System.Reflection;

namespace PlayerData.Unity.Editor
{
    /// <summary>View state of one document, prepared for display.</summary>
    public sealed class DocumentView
    {
        public DocumentView(DocumentEntry entry, string? json, string? jsonError)
        {
            Entry = entry;
            Json = json;
            JsonError = jsonError;
        }

        public DocumentEntry Entry { get; }

        /// <summary>Pretty-printed JSON; null when the payload cannot be rendered.</summary>
        public string? Json { get; }

        public string? JsonError { get; }

        public bool CanEdit => Entry.State == DocumentState.Editable;
    }

    /// <summary>
    /// UI-independent state behind PlayerDataViewerWindow, kept separate so EditMode tests can
    /// drive the full session → scan → load → view flow without a window.
    /// </summary>
    public sealed class PlayerDataViewerController
    {
        public IReadOnlyList<Type> SessionTypes { get; private set; } = Array.Empty<Type>();

        public SessionSchema? Schema { get; private set; }

        public IReadOnlyList<SaveLocation> Saves { get; private set; } = Array.Empty<SaveLocation>();

        public SaveLocation? SelectedSave { get; private set; }

        public LoadedSave? CurrentSave { get; private set; }

        public string? LoadError { get; private set; }

        public void RefreshSessionTypes()
        {
            List<Type> discovered = SessionSchemaResolver.FindSessionTypes();
            List<Type> result = new List<Type>(discovered.Count);
            foreach (Type type in discovered)
            {
                // Test-only session fixtures (compiled under UNITY_INCLUDE_TESTS) would otherwise
                // clutter this package's own dev-project dropdown. A package consumer never sees
                // them — their test assembly is not compiled — so this only affects development.
                if (!IsFromTestAssembly(type))
                    result.Add(type);
            }

            SessionTypes = result;
        }

        // Every Unity EditMode/PlayMode test assembly references the NUnit framework and no
        // production assembly does, which makes it a reliable "is a test type" marker.
        private static bool IsFromTestAssembly(Type type)
        {
            foreach (AssemblyName referenced in type.Assembly.GetReferencedAssemblies())
            {
                if (string.Equals(referenced.Name, "nunit.framework", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        public void SelectSession(Type sessionType)
        {
            if (sessionType is null) throw new ArgumentNullException(nameof(sessionType));

            Schema = SessionSchemaResolver.Resolve(sessionType);

            // Classification depends on the schema, so an already-selected save is re-read.
            if (SelectedSave is not null)
                LoadSelected();
        }

        public void Scan(string rootPath)
        {
            Saves = SaveDataStore.FindSaves(rootPath);
            SelectedSave = null;
            CurrentSave = null;
            LoadError = null;
        }

        /// <summary>
        /// Loads every scanned save with the current schema, for the tree view. Locations whose
        /// manifest cannot be read are omitted; without a schema no save can be classified, so
        /// the result is empty. Returns a fresh list on every call.
        /// </summary>
        public IReadOnlyList<LoadedSave> LoadScannedSaves()
        {
            List<LoadedSave> result = new List<LoadedSave>();
            if (Schema is null)
                return result;

            foreach (SaveLocation location in Saves)
            {
                LoadedSave? save = SaveDataStore.TryLoad(location, Schema, out _);
                if (save is not null)
                    result.Add(save);
            }

            return result;
        }

        public void SelectSave(SaveLocation location)
        {
            SelectedSave = location ?? throw new ArgumentNullException(nameof(location));
            LoadSelected();
        }

        public void Reload()
        {
            if (SelectedSave is not null)
                LoadSelected();
        }

        public DocumentView? GetDocumentView(string storageKey)
        {
            if (CurrentSave is null)
                return null;

            foreach (DocumentEntry entry in CurrentSave.Documents)
            {
                if (!string.Equals(entry.StorageKey, storageKey, StringComparison.Ordinal))
                    continue;

                if (entry.Descriptor is null || entry.State == DocumentState.Unreadable)
                    return new DocumentView(entry, json: null, jsonError: null);

                try
                {
                    string json = MemoryPackJsonConverter.ToJson(entry.Bytes, entry.Descriptor.PayloadType);
                    return new DocumentView(entry, json, jsonError: null);
                }
                catch (Exception ex)
                {
                    return new DocumentView(entry, json: null, jsonError: ex.Message);
                }
            }

            return null;
        }

        /// <summary>
        /// Parses the edited JSON, writes the document back and reloads the save. Returns false
        /// with an error (and without touching the disk) when the JSON is invalid or the document
        /// is not editable.
        /// </summary>
        public bool ApplyJson(string storageKey, string json, out string? error)
        {
            error = null;
            if (CurrentSave is null || Schema is null)
            {
                error = "No save is loaded.";
                return false;
            }

            DocumentEntry? target = null;
            foreach (DocumentEntry entry in CurrentSave.Documents)
            {
                if (string.Equals(entry.StorageKey, storageKey, StringComparison.Ordinal))
                {
                    target = entry;
                    break;
                }
            }

            if (target is null)
            {
                error = $"Document '{storageKey}' does not exist in this save.";
                return false;
            }

            if (target.State != DocumentState.Editable)
            {
                error = $"Document '{storageKey}' is not editable ({target.State}).";
                return false;
            }

            byte[] newBytes;
            try
            {
                newBytes = MemoryPackJsonConverter.FromJson(json, target.Descriptor!.PayloadType);
            }
            catch (Exception ex)
            {
                error = $"JSON error: {ex.Message}";
                return false;
            }

            try
            {
                CurrentSave = SaveDataStore.WriteDocument(CurrentSave, storageKey, newBytes, Schema);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            return true;
        }

        private void LoadSelected()
        {
            if (Schema is null)
            {
                CurrentSave = null;
                LoadError = "Select a session type first.";
                return;
            }

            CurrentSave = SaveDataStore.TryLoad(SelectedSave!, Schema, out string? error);
            LoadError = CurrentSave is null ? (error ?? "No save found (manifest.bin is missing).") : null;
        }
    }
}
