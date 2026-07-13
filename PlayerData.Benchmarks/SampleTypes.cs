using MemoryPack;

namespace PlayerData.Benchmarks;

[MemoryPackable(GenerateType.VersionTolerant)]
public partial record PlayerProfile(
    [property: MemoryPackOrder(0)] int Level,
    [property: MemoryPackOrder(1)] string Name,
    [property: MemoryPackOrder(2)] long Gold);

[MemoryPackable(GenerateType.VersionTolerant)]
public partial record InventoryItem(
    [property: MemoryPackOrder(0)] string ItemId,
    [property: MemoryPackOrder(1)] int Count);
