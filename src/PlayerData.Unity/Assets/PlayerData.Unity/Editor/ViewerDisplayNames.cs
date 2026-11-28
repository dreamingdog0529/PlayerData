using System;
using System.Collections.Generic;
using UnityEngine;

namespace PlayerData.Unity.Editor
{
    /// <summary>
    /// Data Viewer 向けの、わかりやすい言葉のラベルをまとめています。技術的な enum 名や FQCN が
    /// UI に出てこないようにしています。EditMode テストで使えるよう、純粋な関数だけにしています。
    /// </summary>
    public static class ViewerDisplayNames
    {
        public const string SavedFilesLabel = "Saved files";
        public const string DefaultRootLabel = "This game's save folder";
        public const string ChooseFolderLabel = "Choose folder...";
        public const string ApplyLabel = "Apply";
        public const string RevertLabel = "Revert";
        public const string FieldsTabLabel = "Fields";
        public const string JsonTabLabel = "JSON";

        public const string JsonOnlyHint = "Edit via the JSON view";

        public const string EmptyCollectionLabel = "This collection has no entries.";

        public const string AddEntryLabel = "Add entry";

        public const string RemoveEntryLabel = "Remove";

        public const string DuplicateKeyWarning = "Duplicate key — each entry needs a unique key.";

        public const string NoDocumentSelectedTitle = "No document selected";

        public const string NoDocumentSelectedHint = "Pick one from the list to view and edit its saved values.";

        public const string OpenFileLabel = "Open file";

        /// <summary>
        /// 「入っているフォルダーを開く」ボタンのラベルです。ホスト OS のファイルブラウザーに合わせた言い方にしています
        /// （Explorer / Finder / ファイルマネージャー）。テストしやすいよう、プラットフォームを受け取る純粋な関数にしています。
        /// </summary>
        public static string RevealLabel(RuntimePlatform platform)
        {
            switch (platform)
            {
                case RuntimePlatform.OSXEditor:
                    return "Reveal in Finder";
                case RuntimePlatform.LinuxEditor:
                    return "Show in file manager";
                default:
                    return "Show in Explorer";
            }
        }

        public static string StateLabel(DocumentState state)
        {
            switch (state)
            {
                case DocumentState.Editable:
                    return "Editable";
                case DocumentState.ReadOnlyRoundTrip:
                    return "View only";
                case DocumentState.ReadOnlyFormatVersion:
                    return "View only (old format)";
                case DocumentState.UnknownKey:
                    return "Unknown data";
                case DocumentState.Unreadable:
                    return "Can't read";
                default:
                    return "Unknown";
            }
        }

        public static string StateDescription(DocumentState state)
        {
            switch (state)
            {
                case DocumentState.Editable:
                    return string.Empty;
                case DocumentState.ReadOnlyRoundTrip:
                    return "This value can't be edited safely in the viewer.";
                case DocumentState.ReadOnlyFormatVersion:
                    return "Open the game once so it can upgrade this save.";
                case DocumentState.UnknownKey:
                    return "Not part of the selected save type; kept as-is when you save.";
                case DocumentState.Unreadable:
                    return "Encrypted, scrambled, or damaged.";
                default:
                    return string.Empty;
            }
        }

        public static string ShortTypeName(Type type)
        {
            if (type is null) throw new ArgumentNullException(nameof(type));
            return type.Name;
        }

        /// <summary>リストの行では、プロパティ名、型名、ストレージキーの順に優先して使います。</summary>
        public static string DocumentDisplayName(string storageKey, string? propertyName, string? typeName)
        {
            if (!string.IsNullOrEmpty(propertyName))
                return propertyName!;
            if (!string.IsNullOrEmpty(typeName))
                return typeName!;
            return storageKey ?? string.Empty;
        }

        public static string FormatDocumentLine(DocumentEntry entry, bool includeTechnicalDetails)
        {
            if (entry is null) throw new ArgumentNullException(nameof(entry));

            string name = DocumentDisplayName(
                entry.StorageKey,
                entry.Descriptor?.PropertyName,
                entry.Descriptor?.DocumentType.Name);
            string state = StateLabel(entry.State);
            if (!includeTechnicalDetails)
                return $"{name}  ·  {state}";

            string typeName = entry.Descriptor?.DocumentType.Name ?? "?";
            return $"{name}  ·  {state}  ·  {typeName}  ·  {entry.StorageKey}  ·  {entry.SizeBytes} B";
        }

        /// <summary>
        /// ドロップダウン向けの短い型名を作ります。重複したときは数字の suffix を付けます（Foo (1)、Foo (2) のような感じです）。
        /// 並び順は <paramref name="types"/> と同じです。
        /// </summary>
        public static List<string> DisambiguatedShortNames(IReadOnlyList<Type> types)
        {
            if (types is null) throw new ArgumentNullException(nameof(types));

            Dictionary<string, int> totals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Type type in types)
            {
                string shortName = ShortTypeName(type);
                totals[shortName] = totals.TryGetValue(shortName, out int count) ? count + 1 : 1;
            }

            List<string> labels = new List<string>(types.Count);
            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Type type in types)
            {
                string shortName = ShortTypeName(type);
                if (totals[shortName] == 1)
                {
                    labels.Add(shortName);
                    continue;
                }

                seen[shortName] = seen.TryGetValue(shortName, out int occurrence) ? occurrence + 1 : 1;
                labels.Add($"{shortName} ({seen[shortName]})");
            }

            return labels;
        }

        /// <summary>
        /// entity 型のデフォルトの JSON オブジェクト（空のインスタンス）を作ります。ドキュメントアセットを
        /// 作るときに使います。その型を構築できない、またはシリアライズできないときは、理由を添えて失敗します。
        /// </summary>
        public static bool TryCreateDefaultJson(Type entityType, out string json, out string? error)
        {
            if (entityType is null) throw new ArgumentNullException(nameof(entityType));

            object? instance;
            try
            {
                instance = Activator.CreateInstance(entityType);
            }
            catch (Exception ex)
            {
                json = string.Empty;
                error = $"Cannot create a default {ShortTypeName(entityType)}: {ex.Message}";
                return false;
            }

            if (instance is null)
            {
                json = string.Empty;
                error = $"Cannot create a default {ShortTypeName(entityType)}.";
                return false;
            }

            try
            {
                json = MemoryPackJsonConverter.ToJson(instance, entityType);
                // テンプレートが Apply と同じパイプラインにそのまま流せることを確かめています。
                _ = MemoryPackJsonConverter.ObjectFromJson(json, entityType);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                json = string.Empty;
                error = $"Cannot prepare a default {ShortTypeName(entityType)}: {ex.Message}";
                return false;
            }
        }
    }
}
