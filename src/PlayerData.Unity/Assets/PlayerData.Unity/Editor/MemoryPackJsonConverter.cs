using System;
using System.Collections.Generic;
using System.Reflection;
using MemoryPack;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace PlayerData.Unity.Editor
{
    /// <summary>
    /// Converts MemoryPack document payloads to and from editable JSON.
    /// JSON is only an editing surface — MemoryPack bytes remain the storage format.
    /// Callers must gate editing behind <see cref="CanRoundTrip"/> so documents that JSON
    /// cannot represent losslessly are never written back.
    /// </summary>
    public static class MemoryPackJsonConverter
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new MemoryPackContractResolver(),
            Formatting = Formatting.Indented,
            // Keep string members verbatim; date-looking strings must not be re-formatted.
            DateParseHandling = DateParseHandling.None,
            // Fail loudly on property-name typos instead of silently dropping the edit.
            MissingMemberHandling = MissingMemberHandling.Error,
            // Replace pre-initialized members instead of appending to their default contents.
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

        /// <summary>
        /// True when bytes → object → JSON → object → bytes reproduces the payload exactly,
        /// i.e. JSON editing cannot silently lose data for this document.
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
        /// Mirrors MemoryPack member selection for Json.NET: include non-public members marked
        /// [MemoryPackInclude], exclude members marked [MemoryPackIgnore].
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
