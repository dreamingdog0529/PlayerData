using MemoryPack;
using PlayerData.Schema;

namespace PlayerData.Unity.Editor.Tests
{
    // [PlayerDataKey] メンバーを1つだけ持つ、コレクションのエンティティです。
    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class SampleItem
    {
        [PlayerDataKey]
        [MemoryPackOrder(0)]
        public string ItemId { get; set; } = string.Empty;

        [MemoryPackOrder(1)]
        public int Count { get; set; }
    }

    // ストレージキーの解決ルール3つすべてを扱う、有効なセッションです。ソースジェネレータは Unity の中で
    // このクラスに対して動くので、生成されたセッションコードがここでコンパイルできることの確認にもなります。
    [PlayerDataSession]
    [PlayerDataSingle(typeof(SampleProfile))]
    [PlayerDataSingle(typeof(SampleStats), "Stats")]
    [PlayerDataCollection(typeof(SampleItem), "Items", Key = "items-v1")]
    public sealed partial class SampleEditorSession
    {
    }

    // 以下の宣言は、わざと無効なものにしています。あえて [PlayerDataSession] を付けないことで、
    // ソースジェネレータ（診断がコンパイルエラーになります）に見せないようにしています。
    // SessionSchemaResolver は、テストの中でこれらの型を直接指すようにしています。

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

    // "Session" はジェネレータが出力する "_session" バッキングフィールドに対応します。"IsDirty" は予約された
    // 生成メンバー名です。ジェネレータはどちらも拒否します。
    [PlayerDataSingle(typeof(SampleProfile), "Session")]
    [PlayerDataSingle(typeof(SampleStats), "IsDirty")]
    public sealed class SessionWithReservedNames
    {
    }

    // スキーマ専用のフィクスチャ（有効な宣言）です。「新しい」SampleWideDocV2 が書いたペイロードを、
    // SampleWideDocV1 として宣言されたキーの下でテストが読み込めるようにするもので、
    // ReadOnlyRoundTrip に分類される必要があります。
    [PlayerDataSingle(typeof(SampleWideDocV1), "Wide")]
    public sealed class SessionWithWideDoc
    {
    }
}
