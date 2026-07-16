using MemoryPack;

namespace PlayerData.Core.Tests;

[MemoryPackable(GenerateType.VersionTolerant)]
public partial record SamplePlayerData(
    [property: MemoryPackOrder(0)] int Level,
    [property: MemoryPackOrder(1)] string Name);

[MemoryPackable(GenerateType.VersionTolerant)]
public partial record SampleItem(
    [property: MemoryPackOrder(0)] string ItemId,
    [property: MemoryPackOrder(1)] int Count);
