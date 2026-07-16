using MemoryPack;
using PlayerData;

namespace Sample;

[PlayerDataSession]
[PlayerDataSingle(typeof(PlayerProfile), "Profile", Default = nameof(PlayerProfile.NewGame))]
[PlayerDataCollection(typeof(InventoryItem), "Inventory")]
public partial class GameSave
{
}

[MemoryPackable(GenerateType.VersionTolerant)]
public partial class PlayerProfile
{
    [MemoryPackOrder(0)] public int Level { get; set; }
    [MemoryPackOrder(1)] public string Name { get; set; } = "";

    public static PlayerProfile NewGame() => new() { Level = 1, Name = "New" };
}

[MemoryPackable(GenerateType.VersionTolerant)]
public partial class InventoryItem
{
    [PlayerDataKey]
    [MemoryPackOrder(0)]
    public string ItemId { get; set; } = "";

    [MemoryPackOrder(1)] public int Count { get; set; }
}
