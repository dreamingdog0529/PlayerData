using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using MemoryPack;
using UnityEditor;

namespace PlayerData.Unity.Editor
{
    /// <summary>Editor-side schema of one document declared on a [PlayerDataSession] class.</summary>
    public sealed class DocumentDescriptor
    {
        public DocumentDescriptor(string storageKey, Type documentType, Type payloadType, Type? keyType)
        {
            StorageKey = storageKey;
            DocumentType = documentType;
            PayloadType = payloadType;
            KeyType = keyType;
        }

        public string StorageKey { get; }

        /// <summary>Declared entity type (the T of IDoc&lt;T&gt; / IBag&lt;TKey, T&gt;).</summary>
        public Type DocumentType { get; }

        /// <summary>
        /// Type the stored payload deserializes to: <see cref="DocumentType"/> for singles,
        /// ConcurrentDictionary&lt;TKey, T&gt; for collections (SaveSession's wire format).
        /// </summary>
        public Type PayloadType { get; }

        /// <summary>[PlayerDataKey] member type for collections; null for single documents.</summary>
        public Type? KeyType { get; }

        public bool IsCollection => KeyType is not null;
    }

    public sealed class SessionSchema
    {
        public SessionSchema(Type sessionType, IReadOnlyList<DocumentDescriptor> documents, IReadOnlyList<string> diagnostics)
        {
            SessionType = sessionType;
            Documents = documents;
            Diagnostics = diagnostics;
        }

        public Type SessionType { get; }

        public IReadOnlyList<DocumentDescriptor> Documents { get; }

        /// <summary>Human-readable reasons for document declarations that were skipped.</summary>
        public IReadOnlyList<string> Diagnostics { get; }
    }

    /// <summary>
    /// Rebuilds the document schema of a session type from its runtime attributes, mirroring the
    /// source generator's resolution rules (storage key = Key ?? propertyName ?? DocumentType.Name).
    /// The mirror is locked by SessionSchemaResolverTests; if the generator's rules change, those
    /// tests must be updated in lockstep.
    /// </summary>
    public static class SessionSchemaResolver
    {
        // Keep in sync with PlayerDataGenerator.ReservedPropertyNames.
        private static readonly HashSet<string> ReservedPropertyNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "IsDirty", "IsLoaded", "Loaded", "Committed", "DirtyChanged",
            "LoadAsync", "CommitAsync", "SuppressNotifications", "AddValidator", "DisposeAsync", "OpenAsync",
        };

        /// <summary>All [PlayerDataSession] types in the project, sorted for stable UI listing.</summary>
        public static List<Type> FindSessionTypes()
        {
            List<Type> types = new List<Type>();
            foreach (Type type in TypeCache.GetTypesWithAttribute<PlayerDataSessionAttribute>())
                types.Add(type);
            types.Sort(static (left, right) => string.CompareOrdinal(left.FullName, right.FullName));
            return types;
        }

        public static SessionSchema Resolve(Type sessionType)
        {
            if (sessionType is null) throw new ArgumentNullException(nameof(sessionType));

            List<DocumentDescriptor> documents = new List<DocumentDescriptor>();
            List<string> diagnostics = new List<string>();
            HashSet<string> seenPropertyNames = new HashSet<string>(StringComparer.Ordinal);
            // "_session" is pre-seeded because the generator always emits a `SaveSession _session`
            // backing field (PlayerDataGenerator seeds its seenFieldNames identically).
            HashSet<string> seenFieldNames = new HashSet<string>(StringComparer.Ordinal) { "_session" };
            HashSet<string> seenStorageKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (Attribute attribute in sessionType.GetCustomAttributes(inherit: false))
            {
                Type documentType;
                string? rawPropertyName;
                string? storageKeyOverride;
                bool isCollection;
                switch (attribute)
                {
                    case PlayerDataSingleAttribute single:
                        documentType = single.DocumentType;
                        rawPropertyName = single.PropertyName;
                        storageKeyOverride = single.Key;
                        isCollection = false;
                        break;
                    case PlayerDataCollectionAttribute collection:
                        documentType = collection.DocumentType;
                        rawPropertyName = collection.PropertyName;
                        storageKeyOverride = collection.Key;
                        isCollection = true;
                        break;
                    default:
                        continue;
                }

                if (documentType is not { IsClass: true, IsAbstract: false } || documentType.ContainsGenericParameters)
                {
                    diagnostics.Add($"'{documentType?.Name ?? "?"}' cannot be used as a document; it must be a concrete, closed class type.");
                    continue;
                }

                string propertyName = string.IsNullOrEmpty(rawPropertyName) ? documentType.Name : rawPropertyName!;

                if (!IsValidIdentifier(propertyName))
                {
                    diagnostics.Add($"Property name '{propertyName}' is not a valid C# identifier.");
                    continue;
                }

                if (!seenPropertyNames.Add(propertyName))
                {
                    diagnostics.Add($"Multiple documents resolve to the same property name '{propertyName}'.");
                    continue;
                }

                if (ReservedPropertyNames.Contains(propertyName) || !seenFieldNames.Add(ToFieldName(propertyName)))
                {
                    diagnostics.Add($"Property name '{propertyName}' collides with a member the generator always emits.");
                    continue;
                }

                if (!HasVersionTolerantMemoryPackable(documentType))
                {
                    diagnostics.Add($"Document type '{documentType.Name}' is not annotated with [MemoryPackable(GenerateType.VersionTolerant)].");
                    continue;
                }

                string storageKey = storageKeyOverride ?? propertyName;

                if (isCollection)
                {
                    if (!TryGetKeyType(documentType, out Type? keyType, out int keyMemberCount))
                    {
                        diagnostics.Add($"Type '{documentType.Name}' is used as an IBag entity but has {keyMemberCount} member(s) marked [PlayerDataKey]; exactly one is required.");
                        continue;
                    }

                    if (!seenStorageKeys.Add(storageKey))
                    {
                        diagnostics.Add($"Multiple documents share the storage key '{storageKey}'.");
                        continue;
                    }

                    Type payloadType = typeof(ConcurrentDictionary<,>).MakeGenericType(keyType!, documentType);
                    documents.Add(new DocumentDescriptor(storageKey, documentType, payloadType, keyType));
                }
                else
                {
                    // The generator additionally validates the document's default factory here;
                    // the editor never constructs defaults, so that check is not mirrored.
                    if (!seenStorageKeys.Add(storageKey))
                    {
                        diagnostics.Add($"Multiple documents share the storage key '{storageKey}'.");
                        continue;
                    }

                    documents.Add(new DocumentDescriptor(storageKey, documentType, documentType, keyType: null));
                }
            }

            return new SessionSchema(sessionType, documents, diagnostics);
        }

        private static bool HasVersionTolerantMemoryPackable(Type documentType)
        {
            MemoryPackableAttribute? attribute = documentType.GetCustomAttribute<MemoryPackableAttribute>(inherit: false);
            return attribute is not null && attribute.GenerateType == GenerateType.VersionTolerant;
        }

        private static bool TryGetKeyType(Type documentType, out Type? keyType, out int keyMemberCount)
        {
            // The generator inspects declared members only (INamedTypeSymbol.GetMembers()).
            const BindingFlags declaredInstance =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            keyType = null;
            keyMemberCount = 0;

            foreach (PropertyInfo property in documentType.GetProperties(declaredInstance))
            {
                if (property.IsDefined(typeof(PlayerDataKeyAttribute), inherit: false))
                {
                    keyMemberCount++;
                    keyType = property.PropertyType;
                }
            }

            foreach (FieldInfo field in documentType.GetFields(declaredInstance))
            {
                if (field.IsDefined(typeof(PlayerDataKeyAttribute), inherit: false))
                {
                    keyMemberCount++;
                    keyType = field.FieldType;
                }
            }

            if (keyMemberCount != 1)
            {
                keyType = null;
                return false;
            }

            return true;
        }

        private static string ToFieldName(string propertyName) =>
            "_" + char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);

        // Lexical identifier check; mirrors the generator's SyntaxFacts.IsValidIdentifier closely
        // enough for diagnostics (keywords are already rejected by the consumer's compilation).
        private static bool IsValidIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!char.IsLetter(name[0]) && name[0] != '_') return false;
            for (int i = 1; i < name.Length; i++)
            {
                if (!char.IsLetterOrDigit(name[i]) && name[i] != '_') return false;
            }

            return true;
        }
    }
}
