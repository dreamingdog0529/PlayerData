using System;
using System.Collections.Generic;
using System.IO;
using PlayerData.Persistence;
using PlayerData.Sessions;
using PlayerData.Schema;

namespace PlayerData.Unity.Editor
{
    /// <summary>
    /// <see cref="PlayerDataDocumentAsset"/> に対する、UI に依存しない操作です。セッションのスキーマを
    /// 解決し、ドキュメントのペイロード型に対して JSON を検証し、DirectorySaveBackend のレイアウトへ
    /// エクスポートします。EditMode テストで使えるくらい、純粋な作りにしてあります。
    /// </summary>
    public static class PlayerDataDocumentAssetUtility
    {
        public static Type? FindSessionType(string sessionTypeName)
        {
            if (string.IsNullOrWhiteSpace(sessionTypeName))
                return null;

            foreach (Type type in SessionSchemaResolver.FindSessionTypes())
            {
                if (string.Equals(type.FullName, sessionTypeName, StringComparison.Ordinal)
                    || string.Equals(type.AssemblyQualifiedName, sessionTypeName, StringComparison.Ordinal)
                    || string.Equals(type.Name, sessionTypeName, StringComparison.Ordinal))
                {
                    return type;
                }
            }

            return null;
        }

        public static bool TryResolve(
            PlayerDataDocumentAsset asset,
            out Type sessionType,
            out DocumentDescriptor descriptor,
            out string? error)
        {
            sessionType = null!;
            descriptor = null!;
            if (asset is null) throw new ArgumentNullException(nameof(asset));

            Type? found = FindSessionType(asset.SessionTypeName);
            if (found is null)
            {
                error = string.IsNullOrWhiteSpace(asset.SessionTypeName)
                    ? "Choose a save type (session)."
                    : $"Save type '{asset.SessionTypeName}' was not found. It must be a [PlayerDataSession] class in the project.";
                return false;
            }

            sessionType = found;
            SessionSchema schema = SessionSchemaResolver.Resolve(sessionType);
            if (string.IsNullOrWhiteSpace(asset.StorageKey))
            {
                error = "Choose a document (storage key).";
                return false;
            }

            foreach (DocumentDescriptor candidate in schema.Documents)
            {
                if (string.Equals(candidate.StorageKey, asset.StorageKey, StringComparison.Ordinal))
                {
                    descriptor = candidate;
                    error = null;
                    return true;
                }
            }

            error = $"Document key '{asset.StorageKey}' is not declared on {ViewerDisplayNames.ShortTypeName(sessionType)}.";
            return false;
        }

        /// <summary>
        /// アセットの JSON がドキュメントのペイロード型にデシリアライズできて、MemoryPack の往復にも
        /// 耐えられるときに true です（Data Viewer と同じガードです）。
        /// </summary>
        public static bool TryValidate(PlayerDataDocumentAsset asset, out DocumentDescriptor descriptor, out string? error)
        {
            if (!TryResolve(asset, out _, out descriptor, out error))
                return false;

            return TryValidateJson(asset.Json, descriptor, out error);
        }

        public static bool TryValidateJson(string json, DocumentDescriptor descriptor, out string? error)
        {
            if (descriptor is null) throw new ArgumentNullException(nameof(descriptor));
            if (json is null)
            {
                error = "JSON is empty.";
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = MemoryPackJsonConverter.FromJson(json, descriptor.PayloadType);
            }
            catch (Exception ex)
            {
                error = $"Invalid JSON for {descriptor.PropertyName}: {ex.Message}";
                return false;
            }

            if (!MemoryPackJsonConverter.CanRoundTrip(bytes, descriptor.PayloadType, out string? reason))
            {
                error = reason ?? "This value cannot be edited safely (round-trip check failed).";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// 解決したペイロード型に対して検証してから、アセットの JSON を置き換えます。
        /// <see cref="UnityEditor.EditorUtility.SetDirty"/> は呼びません。それは呼び出し側や UI の役割です。
        /// </summary>
        public static bool TrySetJson(PlayerDataDocumentAsset asset, string json, out string? error)
        {
            if (!TryResolve(asset, out _, out DocumentDescriptor descriptor, out error))
                return false;
            if (!TryValidateJson(json, descriptor, out error))
                return false;

            asset.Json = json;
            error = null;
            return true;
        }

        /// <summary>
        /// entity のデフォルトインスタンス（単一の場合）、または空のコレクション（bag の場合）から、JSON の種を作ります。
        /// </summary>
        public static bool TryCreateDefaultJson(DocumentDescriptor descriptor, out string json, out string? error)
        {
            if (descriptor is null) throw new ArgumentNullException(nameof(descriptor));

            if (descriptor.IsCollection)
            {
                try
                {
                    object empty = Activator.CreateInstance(descriptor.PayloadType)
                        ?? throw new InvalidOperationException("Could not create an empty collection payload.");
                    json = MemoryPackJsonConverter.ToJson(empty, descriptor.PayloadType);
                    error = null;
                    return true;
                }
                catch (Exception ex)
                {
                    json = string.Empty;
                    error = ex.Message;
                    return false;
                }
            }

            return ViewerDisplayNames.TryCreateDefaultJson(descriptor.DocumentType, out json, out error);
        }

        /// <summary>
        /// このドキュメントを DirectorySaveBackend のフォルダーへ書き込み、またはマージします。そのセーブに
        /// すでにあるドキュメントは保持され、対象のストレージキーだけが置き換わります。
        /// </summary>
        public static bool TryExportToFolder(PlayerDataDocumentAsset asset, string directory, out string? error)
        {
            if (asset is null) throw new ArgumentNullException(nameof(asset));
            if (string.IsNullOrWhiteSpace(directory))
            {
                error = "Export folder is empty.";
                return false;
            }

            if (!TryResolve(asset, out _, out DocumentDescriptor descriptor, out error))
                return false;
            if (!TryValidateJson(asset.Json, descriptor, out error))
                return false;

            byte[] bytes;
            try
            {
                bytes = MemoryPackJsonConverter.FromJson(asset.Json, descriptor.PayloadType);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            try
            {
                Directory.CreateDirectory(directory);
                DirectorySaveBackend backend = new DirectorySaveBackend(directory);
                SaveBundle? existing = backend.ReadAsync().AsTask().GetAwaiter().GetResult();

                Dictionary<string, byte[]> documents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                int formatVersion = SaveSession.CurrentFormatVersion;
                if (existing is not null)
                {
                    formatVersion = existing.FormatVersion;
                    foreach (KeyValuePair<string, byte[]> pair in existing.Documents)
                        documents[pair.Key] = pair.Value;
                }

                documents[descriptor.StorageKey] = bytes;
                backend.WriteAsync(new SaveBundle(formatVersion, documents)).AsTask().GetAwaiter().GetResult();
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Export failed: {ex.Message}";
                return false;
            }
        }

        public static List<string> SessionTypeChoices()
        {
            List<Type> types = SessionSchemaResolver.FindSessionTypes();
            List<string> choices = new List<string>(types.Count);
            foreach (Type type in types)
                choices.Add(type.FullName ?? type.Name);
            return choices;
        }

        public static List<string> DocumentKeyChoices(string sessionTypeName)
        {
            Type? sessionType = FindSessionType(sessionTypeName);
            List<string> keys = new List<string>();
            if (sessionType is null)
                return keys;

            foreach (DocumentDescriptor document in SessionSchemaResolver.Resolve(sessionType).Documents)
                keys.Add(document.StorageKey);
            return keys;
        }

        public static string DocumentChoiceLabel(DocumentDescriptor descriptor) =>
            $"{descriptor.PropertyName}  ({descriptor.StorageKey})"
            + (descriptor.IsCollection ? "  ·  list" : string.Empty);
    }
}
