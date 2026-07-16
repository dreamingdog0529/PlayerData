using MemoryPack;
using NUnit.Framework;

namespace PlayerData.Unity.Editor.Tests
{
    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial record SmokeProfile
    {
        [MemoryPackOrder(0)]
        public string Name { get; init; } = string.Empty;

        [MemoryPackOrder(1)]
        public int Level { get; init; }
    }

    public sealed class SmokeTests
    {
        [Test]
        public void EditorTestAssembly_AlwaysPasses()
        {
            Assert.That(true, Is.True);
        }

        [Test]
        public void MemoryPackSerializer_RoundTrip_PreservesValues()
        {
            SmokeProfile original = new SmokeProfile { Name = "hero", Level = 42 };

            byte[] bytes = MemoryPackSerializer.Serialize(original);
            SmokeProfile? restored = MemoryPackSerializer.Deserialize<SmokeProfile>(bytes);

            Assert.That(restored, Is.Not.Null);
            Assert.That(restored, Is.EqualTo(original));
        }
    }
}
