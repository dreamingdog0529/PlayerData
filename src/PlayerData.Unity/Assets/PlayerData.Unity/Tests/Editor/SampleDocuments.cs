using System.Collections.Generic;
using MemoryPack;

namespace PlayerData.Unity.Editor.Tests
{
    public enum SampleRank
    {
        Bronze = 0,
        Silver = 1,
        Gold = 2,
    }

    // Mutable class covering enum / nullable / list / dictionary members.
    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class SampleStats
    {
        [MemoryPackOrder(0)]
        public int Hp { get; set; }

        [MemoryPackOrder(1)]
        public int? Shield { get; set; }

        [MemoryPackOrder(2)]
        public SampleRank Rank { get; set; }

        [MemoryPackOrder(3)]
        public List<string> Titles { get; set; } = new List<string>();

        [MemoryPackOrder(4)]
        public Dictionary<string, int> Counters { get; set; } = new Dictionary<string, int>();
    }

    // Immutable record nested inside another record.
    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial record SamplePosition
    {
        [MemoryPackOrder(0)]
        public float X { get; init; }

        [MemoryPackOrder(1)]
        public float Y { get; init; }
    }

    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial record SampleProfile
    {
        [MemoryPackOrder(0)]
        public string Name { get; init; } = string.Empty;

        [MemoryPackOrder(1)]
        public int Level { get; init; }

        [MemoryPackOrder(2)]
        public SamplePosition Spawn { get; init; } = new SamplePosition();
    }

    // Computed member excluded from the payload via [MemoryPackIgnore].
    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class SampleWithIgnored
    {
        [MemoryPackOrder(0)]
        public int BaseValue { get; set; }

        [MemoryPackIgnore]
        public int Doubled => BaseValue * 2;
    }

    // Non-public member included in the payload via [MemoryPackInclude].
    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class SampleWithPrivateIncluded
    {
        [MemoryPackInclude]
        [MemoryPackOrder(0)]
        private int _secret;

        [MemoryPackIgnore]
        public int Secret
        {
            get => _secret;
            set => _secret = value;
        }
    }

    // Same document key written by a "newer" schema (extra member C): reading its payload
    // as SampleWideDocV1 drops C, so a JSON round-trip cannot reproduce the bytes.
    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial record SampleWideDocV2
    {
        [MemoryPackOrder(0)]
        public int A { get; init; }

        [MemoryPackOrder(1)]
        public int B { get; init; }

        [MemoryPackOrder(2)]
        public int C { get; init; }
    }

    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial record SampleWideDocV1
    {
        [MemoryPackOrder(0)]
        public int A { get; init; }

        [MemoryPackOrder(1)]
        public int B { get; init; }
    }
}
