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

    // enum や nullable、list、dictionary のメンバーを扱う、変更できるクラスです。
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

    // 別のレコードの中に入っている、変更できないレコードです。
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

    // [MemoryPackIgnore] でペイロードから外している、計算で求めるメンバーです。
    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class SampleWithIgnored
    {
        [MemoryPackOrder(0)]
        public int BaseValue { get; set; }

        [MemoryPackIgnore]
        public int Doubled => BaseValue * 2;
    }

    // [MemoryPackInclude] でペイロードに含めている、非公開のメンバーです。
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

    // 「新しい」スキーマ（追加メンバー C があります）が書いた、同じドキュメントキーです。そのペイロードを
    // SampleWideDocV1 として読むと C が落ちてしまうので、JSON の往復ではバイト列を再現できません。
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
