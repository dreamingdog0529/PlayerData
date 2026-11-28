using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MemoryPack;
using PlayerData.Persistence;
using PlayerData.Sessions;

namespace PlayerData.Unity.Editor
{
    /// <summary>manifest.bin を含むディレクトリです（セーブ 1 件分です）。</summary>
    public sealed class SaveLocation
    {
        public SaveLocation(string directory, int? slot)
        {
            Directory = directory;
            Slot = slot;
        }

        public string Directory { get; }

        /// <summary>ディレクトリが SlotSaveBackend の「slot_{n}」フォルダーのときのスロット番号です。</summary>
        public int? Slot { get; }
    }

    public enum DocumentState
    {
        Editable,

        /// <summary>JSON ではペイロードを無損失に表せないので、表示だけです。</summary>
        ReadOnlyRoundTrip,

        /// <summary>セーブの FormatVersion が SaveSession.CurrentFormatVersion と違うので、表示だけです。</summary>
        ReadOnlyFormatVersion,

        /// <summary>選択中のセッション型で宣言されていないキーです。書き戻すときはそのまま保持されます。</summary>
        UnknownKey,

        /// <summary>MemoryPack のデシリアライズに失敗しました（暗号化、難読化、またはペイロードの破損が考えられます）。</summary>
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

        /// <summary>スキーマの記述です。未知のキーでは null になります。</summary>
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
    /// DirectorySaveBackend のレイアウトで、セーブを見つけて、読み込んで、分類して、書き戻します
    /// （{root}/manifest.bin + {root}/docs/*.bin、スロットは {root}/slot_{n}/... です）。書き戻すときは、いつも
    /// 編集できるドキュメントをちょうど 1 件だけ置き換え、それ以外のドキュメント（未知のキーを含みます）と
    /// FormatVersion はそのまま保ちます。DirectorySaveBackend は、書き込んだバンドルに含まれていない doc ファイルを
    /// 削除してしまうためです。
    /// </summary>
    public static class SaveDataStore
    {
        private const string ManifestFileName = "manifest.bin";

        /// <summary>root の下（root 自身も含みます）で、manifest.bin を含むディレクトリをすべて探します。</summary>
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
        /// セッションのスキーマに照らして、セーブを読み込んで分類します。マニフェストがないときは null を error = null で返し、
        /// セーブを読めないとき（マニフェストの破損や暗号化、ドキュメントファイルの不足など）は、null を null でない error とともに返します。
        ///
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
        /// 編集できるドキュメントをちょうど 1 件だけ置き換え、バンドル全体を書き戻して、読み直した
        /// セーブを返します。対象のドキュメントがない、または編集できないときは例外を投げます。
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
