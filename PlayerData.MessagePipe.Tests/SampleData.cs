using MemoryPack;

namespace PlayerData.MessagePipe.Tests;

[MemoryPackable]
public partial record SampleData(
    [property: MemoryPackOrder(0)] string Id,
    [property: MemoryPackOrder(1)] int Value);
