using MemoryPack;

namespace PlayerData.Unity.Editor.Tests
{
    // Collection entity with a single [PlayerDataKey] member.
    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class SampleItem
    {
        [PlayerDataKey]
        [MemoryPackOrder(0)]
        public string ItemId { get; set; } = string.Empty;

        [MemoryPackOrder(1)]
        public int Count { get; set; }
    }

    // Valid session covering all three storage-key resolution rules. The source generator runs
    // on this class inside Unity, so it also proves generated session code compiles here.
    [PlayerDataSession]
    [PlayerDataSingle(typeof(SampleProfile))]
    [PlayerDataSingle(typeof(SampleStats), "Stats")]
    [PlayerDataCollection(typeof(SampleItem), "Items", Key = "items-v1")]
    public sealed partial class SampleEditorSession
    {
    }

    // The declarations below are intentionally invalid. They deliberately omit
    // [PlayerDataSession] so the source generator (whose diagnostics are compile errors) never
    // sees them; SessionSchemaResolver is pointed at these types directly in tests.

    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class SampleKeyless
    {
        [MemoryPackOrder(0)]
        public int Value { get; set; }
    }

    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class SampleDoubleKeyed
    {
        [PlayerDataKey]
        [MemoryPackOrder(0)]
        public string A { get; set; } = string.Empty;

        [PlayerDataKey]
        [MemoryPackOrder(1)]
        public string B { get; set; } = string.Empty;
    }

    public class SamplePlainDoc
    {
        public int Value { get; set; }
    }

    [PlayerDataCollection(typeof(SampleKeyless))]
    public sealed class SessionWithKeylessCollection
    {
    }

    [PlayerDataCollection(typeof(SampleDoubleKeyed))]
    public sealed class SessionWithDoubleKeyedCollection
    {
    }

    [PlayerDataSingle(typeof(SampleProfile), "First", Key = "dup")]
    [PlayerDataSingle(typeof(SampleStats), "Second", Key = "dup")]
    public sealed class SessionWithDuplicateStorageKey
    {
    }

    [PlayerDataSingle(typeof(SamplePlainDoc))]
    public sealed class SessionWithNonMemoryPackableDocument
    {
    }

    // "Session" maps to the generator-emitted "_session" backing field; "IsDirty" is a reserved
    // generated member name. The generator rejects both.
    [PlayerDataSingle(typeof(SampleProfile), "Session")]
    [PlayerDataSingle(typeof(SampleStats), "IsDirty")]
    public sealed class SessionWithReservedNames
    {
    }

    // Schema-only fixture (valid declaration): lets tests load a payload written by the "newer"
    // SampleWideDocV2 under a key declared as SampleWideDocV1, which must classify as
    // ReadOnlyRoundTrip.
    [PlayerDataSingle(typeof(SampleWideDocV1), "Wide")]
    public sealed class SessionWithWideDoc
    {
    }
}
