using System;
using System.Collections.Generic;

namespace PlayerData.Unity.Editor
{
    /// <summary>
    /// Plain-language labels for the Data Viewer. Keeps technical enum names and FQCNs out of the
    /// UI. Pure functions for EditMode tests.
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

        /// <summary>Prefer property name, then type name, then storage key for list rows.</summary>
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
        /// Short type names for dropdowns; duplicates get a numeric suffix (Foo (1), Foo (2)).
        /// Order matches <paramref name="types"/>.
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
        /// Builds a default JSON object for an entity type (empty instance). Used when creating
        /// document assets. Fails with a reason when the type cannot be constructed or serialized.
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
                // Ensure the template can be fed back through the same pipeline as Apply.
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
