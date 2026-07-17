using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MemoryPack;
using Newtonsoft.Json;
using NUnit.Framework;

namespace PlayerData.Unity.Editor.Tests
{
    public sealed class MemoryPackJsonConverterTests
    {
        private static byte[] RoundTripThroughJson(byte[] bytes, Type documentType)
        {
            string json = MemoryPackJsonConverter.ToJson(bytes, documentType);
            return MemoryPackJsonConverter.FromJson(json, documentType);
        }

        [Test]
        public void FromJson_MutableClassPayload_ReproducesExactBytes()
        {
            SampleStats value = new SampleStats
            {
                Hp = 120,
                Shield = null,
                Rank = SampleRank.Gold,
                Titles = new List<string> { "hero", "2026-07-17T00:00:00Z" },
                Counters = new Dictionary<string, int> { ["kills"] = 3, ["deaths"] = 1 },
            };
            byte[] bytes = MemoryPackSerializer.Serialize(value);

            byte[] roundTripped = RoundTripThroughJson(bytes, typeof(SampleStats));

            Assert.That(roundTripped, Is.EqualTo(bytes));
        }

        [Test]
        public void FromJson_RecordWithNestedRecordPayload_ReproducesExactBytes()
        {
            SampleProfile value = new SampleProfile
            {
                Name = "wanwan",
                Level = 7,
                Spawn = new SamplePosition { X = 1.5f, Y = -2.25f },
            };
            byte[] bytes = MemoryPackSerializer.Serialize(value);

            byte[] roundTripped = RoundTripThroughJson(bytes, typeof(SampleProfile));

            Assert.That(roundTripped, Is.EqualTo(bytes));
        }

        [Test]
        public void FromJson_ConcurrentDictionaryPayload_ReproducesExactBytes()
        {
            ConcurrentDictionary<string, SampleProfile> value = new ConcurrentDictionary<string, SampleProfile>();
            value["a"] = new SampleProfile { Name = "alpha", Level = 1 };
            value["b"] = new SampleProfile { Name = "beta", Level = 2 };
            value["c"] = new SampleProfile { Name = "gamma", Level = 3 };
            byte[] bytes = MemoryPackSerializer.Serialize(value);

            byte[] roundTripped = RoundTripThroughJson(bytes, typeof(ConcurrentDictionary<string, SampleProfile>));

            Assert.That(roundTripped, Is.EqualTo(bytes));
        }

        [Test]
        public void ToJson_IgnoredMember_OmittedFromJson()
        {
            SampleWithIgnored value = new SampleWithIgnored { BaseValue = 21 };
            byte[] bytes = MemoryPackSerializer.Serialize(value);

            string json = MemoryPackJsonConverter.ToJson(bytes, typeof(SampleWithIgnored));

            Assert.That(json, Does.Contain("BaseValue"));
            Assert.That(json, Does.Not.Contain("Doubled"));
            Assert.That(RoundTripThroughJson(bytes, typeof(SampleWithIgnored)), Is.EqualTo(bytes));
        }

        [Test]
        public void FromJson_PrivateIncludedMember_PreservedExactly()
        {
            SampleWithPrivateIncluded value = new SampleWithPrivateIncluded { Secret = 999 };
            byte[] bytes = MemoryPackSerializer.Serialize(value);

            string json = MemoryPackJsonConverter.ToJson(bytes, typeof(SampleWithPrivateIncluded));
            byte[] roundTripped = MemoryPackJsonConverter.FromJson(json, typeof(SampleWithPrivateIncluded));

            Assert.That(json, Does.Contain("_secret"));
            Assert.That(roundTripped, Is.EqualTo(bytes));
        }

        [Test]
        public void CanRoundTrip_EditableSampleTypes_ReturnsTrue()
        {
            byte[] statsBytes = MemoryPackSerializer.Serialize(new SampleStats { Hp = 1 });
            byte[] profileBytes = MemoryPackSerializer.Serialize(new SampleProfile { Name = "x" });

            Assert.That(MemoryPackJsonConverter.CanRoundTrip(statsBytes, typeof(SampleStats), out string? statsReason), Is.True, statsReason);
            Assert.That(MemoryPackJsonConverter.CanRoundTrip(profileBytes, typeof(SampleProfile), out string? profileReason), Is.True, profileReason);
        }

        [Test]
        public void CanRoundTrip_PayloadWrittenByNewerSchema_ReturnsFalse()
        {
            byte[] v2Bytes = MemoryPackSerializer.Serialize(new SampleWideDocV2 { A = 1, B = 2, C = 3 });

            bool canRoundTrip = MemoryPackJsonConverter.CanRoundTrip(v2Bytes, typeof(SampleWideDocV1), out string? reason);

            Assert.That(canRoundTrip, Is.False);
            Assert.That(reason, Is.Not.Null);
        }

        [Test]
        public void FromJson_MalformedJson_Throws()
        {
            Assert.That(
                () => MemoryPackJsonConverter.FromJson("{ not valid json", typeof(SampleStats)),
                Throws.InstanceOf<JsonException>());
        }

        [Test]
        public void FromJson_UnknownPropertyName_Throws()
        {
            Assert.That(
                () => MemoryPackJsonConverter.FromJson("{ \"Hpp\": 5 }", typeof(SampleStats)),
                Throws.InstanceOf<JsonException>());
        }
    }
}
