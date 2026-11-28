using System;
using System.Collections.Generic;
using System.Reflection;
using MemoryPack;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace PlayerData.Unity.Editor
{
    /// <summary>
    /// MemoryPack のドキュメントのペイロードと、編集できる JSON とを相互に変換します。
    /// JSON はあくまで編集用の姿で、保存形式は MemoryPack のバイト列のままです。
    /// 呼び出し側では、編集を <see cref="CanRoundTrip"/> でガードしてください。JSON では
    /// 無損失に表せないドキュメントを、決して書き戻さないためです。
    /// </summary>
    public static class MemoryPackJsonConverter
    {
        // 共有インスタンスです。DefaultContractResolver はインスタンスごとにコントラクトをキャッシュしますし、
        // 下の ResolveObjectContract では、まったく同じメンバー選択ルールを見ないといけないためです。
        private static readonly MemoryPackContractResolver Resolver = new MemoryPackContractResolver();

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = Resolver,
            Formatting = Formatting.Indented,
            // 文字列メンバーはそのまま保ちます。日付っぽく見える文字列も、書式を変えないでください。
            DateParseHandling = DateParseHandling.None,
            // プロパティ名のタイプミスでは、編集を静かに捨てず、はっきり失敗させます。
            MissingMemberHandling = MissingMemberHandling.Error,
            // あらかじめ初期化されたメンバーは、デフォルトの中身に追加するのではなく、置き換えます。
            ObjectCreationHandling = ObjectCreationHandling.Replace,
        };

        public static string ToJson(byte[] bytes, Type documentType)
        {
            if (bytes is null) throw new ArgumentNullException(nameof(bytes));
            if (documentType is null) throw new ArgumentNullException(nameof(documentType));

            object? value = MemoryPackSerializer.Deserialize(documentType, bytes);
            return JsonConvert.SerializeObject(value, documentType, Settings);
        }

        public static byte[] FromJson(string json, Type documentType)
        {
            if (json is null) throw new ArgumentNullException(nameof(json));
            if (documentType is null) throw new ArgumentNullException(nameof(documentType));

            object? value = JsonConvert.DeserializeObject(json, documentType, Settings);
            return MemoryPackSerializer.Serialize(documentType, value);
        }

        /// <summary>バイト列レベルの API と同じ設定で、ライブオブジェクトをシリアライズします。</summary>
        public static string ToJson(object value, Type documentType)
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            if (documentType is null) throw new ArgumentNullException(nameof(documentType));

            return JsonConvert.SerializeObject(value, documentType, Settings);
        }

        /// <summary>バイト列レベルの API と同じ設定で、JSON をライブオブジェクトにデシリアライズします。</summary>
        public static object ObjectFromJson(string json, Type documentType)
        {
            if (json is null) throw new ArgumentNullException(nameof(json));
            if (documentType is null) throw new ArgumentNullException(nameof(documentType));

            object? value = JsonConvert.DeserializeObject(json, documentType, Settings);
            if (value is null)
                throw new JsonSerializationException($"JSON deserialized to null for document type '{documentType.Name}'.");
            return value;
        }

        /// <summary>
        /// バイト列 → オブジェクト → JSON → オブジェクト → バイト列と変換してもペイロードがそのまま再現できるときに true です。
        /// つまり、このドキュメントでは JSON 編集でデータが静かに失われることはありません。
        /// </summary>
        public static bool CanRoundTrip(byte[] bytes, Type documentType, out string? failureReason)
        {
            try
            {
                string json = ToJson(bytes, documentType);
                byte[] roundTripped = FromJson(json, documentType);
                if (bytes.AsSpan().SequenceEqual(roundTripped))
                {
                    failureReason = null;
                    return true;
                }

                failureReason = "Re-serialized bytes differ from the stored payload; JSON cannot represent this document losslessly (e.g. the payload was written by a newer schema).";
                return false;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// フィールドエディター向けに、メンバー選択ルールを解決済みのコントラクトとして返します。ここから公開しているのは、
        /// ルールを再実装せずに済ませるためで、JSON 編集とフィールドごとの編集とで、ドキュメントが公開する
        /// メンバーがずれてしまわないようにするためです。
        /// </summary>
        internal static JsonObjectContract ResolveObjectContract(Type documentType)
        {
            if (documentType is null) throw new ArgumentNullException(nameof(documentType));

            if (Resolver.ResolveContract(documentType) is JsonObjectContract contract)
                return contract;
            throw new InvalidOperationException(
                $"Type '{documentType.Name}' does not resolve to an object contract; per-field editing applies to document objects only.");
        }

        /// <summary>
        /// Json.NET 向けに MemoryPack のメンバー選択を再現しています。[MemoryPackInclude] が付いた非公開メンバーを含め、
        /// [MemoryPackIgnore] が付いたメンバーを除外します。
        /// </summary>
        private sealed class MemoryPackContractResolver : DefaultContractResolver
        {
            protected override List<MemberInfo> GetSerializableMembers(Type objectType)
            {
                List<MemberInfo> members = base.GetSerializableMembers(objectType);

                const BindingFlags nonPublicInstance = BindingFlags.Instance | BindingFlags.NonPublic;
                foreach (PropertyInfo property in objectType.GetProperties(nonPublicInstance))
                {
                    if (property.IsDefined(typeof(MemoryPackIncludeAttribute), inherit: true) && !members.Contains(property))
                        members.Add(property);
                }

                foreach (FieldInfo field in objectType.GetFields(nonPublicInstance))
                {
                    if (field.IsDefined(typeof(MemoryPackIncludeAttribute), inherit: true) && !members.Contains(field))
                        members.Add(field);
                }

                members.RemoveAll(static member => member.IsDefined(typeof(MemoryPackIgnoreAttribute), inherit: true));
                return members;
            }

            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
            {
                JsonProperty property = base.CreateProperty(member, memberSerialization);

                if (member.IsDefined(typeof(MemoryPackIncludeAttribute), inherit: true))
                {
                    switch (member)
                    {
                        case PropertyInfo propertyInfo:
                            property.Readable = propertyInfo.GetMethod is not null;
                            property.Writable = propertyInfo.SetMethod is not null;
                            break;
                        case FieldInfo:
                            property.Readable = true;
                            property.Writable = true;
                            break;
                    }
                }

                return property;
            }
        }
    }
}
