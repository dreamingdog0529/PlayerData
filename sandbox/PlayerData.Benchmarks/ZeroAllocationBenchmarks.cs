using BenchmarkDotNet.Attributes;
using PlayerData.Documents;
using PlayerData.Sessions;

namespace PlayerData.Benchmarks;

// ライブラリの定常状態の速い経路では、確保が起きないことを確かめます（Allocated = 0 B/op、
// BenchmarkDotNet の表では "-" と出ます）。ほかのベンチマークのクラスと違って、ここでの変更はすべて、
// 呼び出しのたびに `new` や `with` で新しいものを作るのではなく、あらかじめ確保しておいたインスタンスへ
// 差し替える形にしています。そのため、測られる確保はストアやセッションの経路自身の分であり、
// 呼び出し側がデータを組み立てる分ではありません。そちらは呼び出し側の選択で、不変なデータでは避けようがないものです。
//
// カバーしている経路と、ゲームのループで毎フレーム効いてくる理由です。
//   - セッションにつながったストアでの、ドキュメントの Replace やコレクションの Upsert / TryUpdate / GetOrAdd:
//     変更ごとのパイプラインです（CAS や ConcurrentDictionary でのその場の更新から、バージョンの増加、
//     OnMutated での抑制状態の問い合わせ、構造体のデータを伴う DirtyChanged / Changed の発生まで）。
//   - Value / TryGet の読み取りと IsDirty: 自動セーブや UI のバインドが毎フレーム確認します。
//   - きれいな状態のセッションでの CommitAsync: 自動セーブの刻みの、何もしない回です。
//     実行しないセーブの分のコストを、払わされないようにしなければなりません。
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 3, printSource: true)]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
public class ZeroAllocationBenchmarks
{
    private SaveSession _session = null!;
    private IDoc<PlayerProfile> _profile = null!;
    private IBag<string, InventoryItem> _items = null!;

    private PlayerProfile _profileA = null!;
    private PlayerProfile _profileB = null!;
    private InventoryItem _itemA = null!;
    private InventoryItem _itemB = null!;
    private int _flip;

    private SaveSession _cleanSession = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _session = new SaveSession(new InMemorySaveBackend());
        _session.DirtyChanged += static _ => { };
        _profile = _session.AddDocument("profile", () => new PlayerProfile(1, "Hero", 0));
        _profile.Changed += static _ => { };
        _items = _session.AddCollection<string, InventoryItem>("items", i => i.ItemId);
        _items.Changed += static _ => { };

        _profileA = new PlayerProfile(1, "Hero", 100);
        _profileB = new PlayerProfile(1, "Hero", 200);
        _itemA = new InventoryItem("item0", 1);
        _itemB = new InventoryItem("item0", 2);
        _items.Upsert(_itemA);
        _items.Upsert(new InventoryItem("item1", 1));

        _cleanSession = new SaveSession(new InMemorySaveBackend());
        var cleanProfile = _cleanSession.AddDocument("profile", () => new PlayerProfile(1, "Hero", 0));
        cleanProfile.Replace(new PlayerProfile(1, "Hero", 1));
        _cleanSession.CommitAsync().AsTask().GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true)]
    public PlayerProfile DocumentReplace()
    {
        return _profile.Replace((++_flip & 1) == 0 ? _profileA : _profileB);
    }

    [Benchmark]
    public InventoryItem CollectionUpsert_ExistingKey()
    {
        return _items.Upsert((++_flip & 1) == 0 ? _itemA : _itemB);
    }

    [Benchmark]
    public bool CollectionTryUpdate_ExistingKey()
    {
        // updater は、あらかじめ確保しておいたもう一方のインスタンスへ差し替えます。そのため、ストアの TryUpdate の
        // 仕組み（AddOrUpdate と UpdateContext と通知）だけに値段が付きます。
        return _items.TryUpdate(
            "item0",
            (++_flip & 1) == 0 ? _itemA : _itemB,
            static (replacement, _) => replacement,
            out _);
    }

    [Benchmark]
    public InventoryItem CollectionGetOrAdd_ExistingKey()
    {
        return _items.GetOrAdd("item1", static id => new InventoryItem(id, 1));
    }

    [Benchmark]
    public PlayerProfile DocumentValue_Read()
    {
        return _profile.Value;
    }

    [Benchmark]
    public bool CollectionTryGet()
    {
        return _items.TryGet("item1", out _);
    }

    [Benchmark]
    public bool SessionIsDirty()
    {
        return _session.IsDirty;
    }

    [Benchmark]
    public async System.Threading.Tasks.Task CommitAsync_CleanNoOp()
    {
        await _cleanSession.CommitAsync();
    }
}
