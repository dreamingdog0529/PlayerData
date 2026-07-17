using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MemoryPack;

namespace PlayerData.Unity.Editor
{
    /// <summary>A directory containing a manifest.bin (one save).</summary>
    public sealed class SaveLocation
    {
        public SaveLocation(string directory, int? slot)
        {
            Directory = directory;
            Slot = slot;
        }

        public string Directory { get; }

        /// <summary>Slot number when the directory is a SlotSaveBackend "slot_{n}" folder.</summary>
        public int? Slot { get; }
    }

    public enum DocumentState
    {
        Editable,

        /// <summary>JSON cannot represent the payload losslessly; viewing only.</summary>
        ReadOnlyRoundTrip,

        /// <summary>Save FormatVersion differs from SaveSession.CurrentFormatVersion; viewing only.</summary>
        ReadOnlyFormatVersion,

        /// <summary>Key is not declared on the selected session type; preserved verbatim on write-back.</summary>
        UnknownKey,

        /// <summary>MemoryPack deserialization failed (encrypted, obfuscated or corrupt payload).</summary>
        Unreadable,
    }

    public sealed class DocumentEntry
    {
        public DocumentEntry(string storageKey, DocumentDescriptor? descriptor, byte[] bytes, DocumentState state, string? stateReason)
        {
            StorageKey = storageKey;
            Descriptor = descriptor;
            Bytes = bytes;
            State = state;
            StateReason = stateReason;
        }

        public string StorageKey { get; }

        /// <summary>Schema descriptor; null for unknown keys.</summary>
        public DocumentDescriptor? Descriptor { get; }

        public byte[] Bytes { get; }

        public DocumentState State { get; }

        public string? StateReason { get; }

        public int SizeBytes => Bytes.Length;
    }

    public sealed class LoadedSave
    {
        public LoadedSave(SaveLocation location, int formatVersion, IReadOnlyList<DocumentEntry> documents)
        {
            Location = location;
            FormatVersion = formatVersion;
            Documents = documents;
        }

        public SaveLocation Location { get; }

        public int FormatVersion { get; }

        public IReadOnlyList<DocumentEntry> Documents { get; }

        public bool IsFormatVersionCurrent => FormatVersion == SaveSession.CurrentFormatVersion;
    }

    /// <summary>
    /// Discovers, loads, classifies and writes back saves in the DirectorySaveBackend layout
    /// ({root}/manifest.bin + {root}/docs/*.bin, slots as {root}/slot_{n}/...). Write-back always
    /// replaces exactly one editable document and keeps every other document (unknown keys
    /// included) plus the FormatVersion intact, because DirectorySaveBackend deletes doc files
    /// that are missing from the written bundle.
    /// </summary>
    public static class SaveDataStore
    {
        private const string ManifestFileName = "manifest.bin";

        /// <summary>Finds all directories containing a manifest.bin under root (root included).</summary>
        public static List<SaveLocation> FindSaves(string rootDirectory, int maxDepth = 3)
        {
            List<SaveLocation> found = new List<SaveLocation>();
            if (string.IsNullOrEmpty(rootDirectory) || !System.IO.Directory.Exists(rootDirectory))
                return found;

            Scan(rootDirectory, 0, maxDepth, found);
            found.Sort(static (left, right) => string.CompareOrdinal(left.Directory, right.Directory));
            return found;
        }

        /// <summary>
        /// Loads and classifies a save against the session schema. Returns null with error = null
        /// when no manifest exists, and null with a non-null error when the save cannot be read
        /// (corrupt or encrypted manifest, missing document files).
        /// </summary>
        public static LoadedSave? TryLoad(SaveLocation location, SessionSchema schema, out string? error)
        {
            if (location is null) throw new ArgumentNullException(nameof(location));
            if (schema is null) throw new ArgumentNullException(nameof(schema));

            error = null;
            SaveBundle? bundle;
            try
            {
                bundle = new DirectorySaveBackend(location.Directory).ReadAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }

            if (bundle is null)
                return null;

            bool formatCurrent = bundle.FormatVersion == SaveSession.CurrentFormatVersion;
            List<DocumentEntry> documents = new List<DocumentEntry>();
            foreach (KeyValuePair<string, byte[]> pair in bundle.Documents)
                documents.Add(Classify(pair.Key, pair.Value, schema, formatCurrent));

            return new LoadedSave(location, bundle.FormatVersion, documents);
        }

        /// <summary>
        /// Replaces exactly one editable document, writes the whole bundle back and returns the
        /// reloaded save. Throws when the target document does not exist or is not editable.
        /// </summary>
        public static LoadedSave WriteDocument(LoadedSave save, string storageKey, byte[] newBytes, SessionSchema schema)
        {
            if (save is null) throw new ArgumentNullException(nameof(save));
            if (storageKey is null) throw new ArgumentNullException(nameof(storageKey));
            if (newBytes is null) throw new ArgumentNullException(nameof(newBytes));
            if (schema is null) throw new ArgumentNullException(nameof(schema));

            DocumentEntry? target = null;
            foreach (DocumentEntry entry in save.Documents)
            {
                if (string.Equals(entry.StorageKey, storageKey, StringComparison.Ordinal))
                {
                    target = entry;
                    break;
                }
            }

            if (target is null)
                throw new InvalidOperationException($"Document '{storageKey}' does not exist in this save.");
            if (target.State != DocumentState.Editable)
                throw new InvalidOperationException(
                    $"Document '{storageKey}' is not editable ({target.State}): {target.StateReason}");

            Dictionary<string, byte[]> documents = new Dictionary<string, byte[]>(save.Documents.Count, StringComparer.Ordinal);
            foreach (DocumentEntry entry in save.Documents)
                documents[entry.StorageKey] = string.Equals(entry.StorageKey, storageKey, StringComparison.Ordinal) ? newBytes : entry.Bytes;

            SaveBundle bundle = new SaveBundle(save.FormatVersion, documents);
            new DirectorySaveBackend(save.Location.Directory).WriteAsync(bundle).AsTask().GetAwaiter().GetResult();

            LoadedSave? reloaded = TryLoad(save.Location, schema, out string? reloadError);
            if (reloaded is null)
                throw new InvalidOperationException($"Reload after write failed: {reloadError ?? "save disappeared"}");
            return reloaded;
        }

        private static void Scan(string directory, int depth, int maxDepth, List<SaveLocation> found)
        {
            if (File.Exists(Path.Combine(directory, ManifestFileName)))
                found.Add(new SaveLocation(directory, ParseSlot(directory)));

            if (depth >= maxDepth)
                return;

            foreach (string child in System.IO.Directory.GetDirectories(directory))
            {
                string name = Path.GetFileName(child);
                if (name is ".staging" or "docs")
                    continue;
                Scan(child, depth + 1, maxDepth, found);
            }
        }

        private static int? ParseSlot(string directory)
        {
            string name = Path.GetFileName(directory);
            if (name.StartsWith("slot_", StringComparison.Ordinal) &&
                int.TryParse(name.Substring(5), NumberStyles.None, CultureInfo.InvariantCulture, out int slot))
            {
                return slot;
            }

            return null;
        }

        private static DocumentEntry Classify(string storageKey, byte[] bytes, SessionSchema schema, bool formatCurrent)
        {
            DocumentDescriptor? descriptor = null;
            foreach (DocumentDescriptor candidate in schema.Documents)
            {
                if (string.Equals(candidate.StorageKey, storageKey, StringComparison.Ordinal))
                {
                    descriptor = candidate;
                    break;
                }
            }

            if (descriptor is null)
            {
                return new DocumentEntry(storageKey, null, bytes, DocumentState.UnknownKey,
                    "Key is not declared on the selected session type.");
            }

            try
            {
                MemoryPackSerializer.Deserialize(descriptor.PayloadType, bytes);
            }
            catch (Exception ex)
            {
                return new DocumentEntry(storageKey, descriptor, bytes, DocumentState.Unreadable,
                    $"MemoryPack deserialization failed (encrypted, obfuscated or corrupt): {ex.Message}");
            }

            if (!formatCurrent)
            {
                return new DocumentEntry(storageKey, descriptor, bytes, DocumentState.ReadOnlyFormatVersion,
                    $"Save FormatVersion differs from SaveSession.CurrentFormatVersion ({SaveSession.CurrentFormatVersion}); run migrations in game code first.");
            }

            if (!MemoryPackJsonConverter.CanRoundTrip(bytes, descriptor.PayloadType, out string? reason))
                return new DocumentEntry(storageKey, descriptor, bytes, DocumentState.ReadOnlyRoundTrip, reason);

            return new DocumentEntry(storageKey, descriptor, bytes, DocumentState.Editable, null);
        }
    }
}
