using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using MemoryPack;
using UnityEditor;
using PlayerData.Documents;
using PlayerData.Sessions;
using PlayerData.Schema;

namespace PlayerData.Unity.Editor
{
    /// <summary>[PlayerDataSession] クラスで宣言されたドキュメント 1 件の、エディター側のスキーマです。</summary>
    public sealed class DocumentDescriptor
    {
        public DocumentDescriptor(string storageKey, Type documentType, Type payloadType, Type? keyType, string propertyName)
        {
            StorageKey = storageKey;
            DocumentType = documentType;
            PayloadType = payloadType;
            KeyType = keyType;
            PropertyName = propertyName;
        }

        public string StorageKey { get; }

        /// <summary>
        /// 生成されるプロパティ名です（属性の PropertyName、省略したときは DocumentType.Name です）。
        /// わかりやすいリストのラベルに使います。ストレージキーの解決では、相変わらず Key ?? propertyName を使います。
        /// </summary>
        public string PropertyName { get; }

        /// <summary>宣言された entity 型です（IDoc&lt;T&gt; / IBag&lt;TKey, T&gt; の T です）。</summary>
        public Type DocumentType { get; }

        /// <summary>
        /// 保存されたペイロードがデシリアライズされる型です。単一の場合は <see cref="DocumentType"/>、
        /// コレクションの場合は ConcurrentDictionary&lt;TKey, T&gt; です（SaveSession のワイヤ形式です）。
        /// </summary>
        public Type PayloadType { get; }

        /// <summary>コレクション向けの [PlayerDataKey] メンバーの型です。単一ドキュメントでは null になります。</summary>
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

        /// <summary>スキップされたドキュメント宣言について、人間が読める理由をまとめています。</summary>
        public IReadOnlyList<string> Diagnostics { get; }
    }

    /// <summary>
    /// セッション型のドキュメントスキーマを、そのランタイム属性から再構築します。ソースジェネレーターの
    /// 解決ルールを再現しています（ストレージキーは Key ?? propertyName ?? DocumentType.Name です）。
    /// この再現は SessionSchemaResolverTests で固定しています。ジェネレーターのルールが変わったら、それらの
    /// テストも歩調を合わせて更新してください。
    /// </summary>
    public static class SessionSchemaResolver
    {
        // PlayerDataGenerator.ReservedPropertyNames と同期を保ってください。
        private static readonly HashSet<string> ReservedPropertyNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "IsDirty", "IsLoaded", "Loaded", "Committed", "DirtyChanged",
            "LoadAsync", "CommitAsync", "SuppressNotifications", "AddValidator", "DisposeAsync", "OpenAsync",
        };

        /// <summary>プロジェクト内のすべての [PlayerDataSession] 型です。UI の一覧が安定するよう、並び替えてあります。</summary>
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
            // 「_session」はあらかじめ種として入れてあります。ジェネレーターはいつも `SaveSession _session` という
            // バッキングフィールドを出すためです（PlayerDataGenerator も、その seenFieldNames に同じように種を入れています）。
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
                    documents.Add(new DocumentDescriptor(storageKey, documentType, payloadType, keyType, propertyName));
                }
                else
                {
                    // ジェネレーターは、ここでドキュメントのデフォルトファクトリも追加で検証しますが、
                    // エディターではデフォルトを構築することはないので、そのチェックは再現していません。
                    if (!seenStorageKeys.Add(storageKey))
                    {
                        diagnostics.Add($"Multiple documents share the storage key '{storageKey}'.");
                        continue;
                    }

                    documents.Add(new DocumentDescriptor(storageKey, documentType, documentType, keyType: null, propertyName));
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
            // ジェネレーターは、宣言されたメンバーだけを見ます（INamedTypeSymbol.GetMembers() です）。
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

        // 字句レベルの識別子チェックです。ジェネレーターの SyntaxFacts.IsValidIdentifier を、診断に十分な程度に
        // 近く再現しています（キーワードは、使う側のコンパイルですでに弾かれます）。
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
