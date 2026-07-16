using MemoryPack;

namespace PlayerData.VitalRouter.Tests;

[MemoryPackable]
public partial record SampleData(
    [property: MemoryPackOrder(0)] string Id,
    [property: MemoryPackOrder(1)] int Value);
