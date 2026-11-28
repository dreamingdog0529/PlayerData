using BenchmarkDotNet.Attributes;
using PlayerData.Documents;
using PlayerData.Sessions;

namespace PlayerData.Benchmarks;

// SaveSession.CollectionParticipant のシリアライズとデシリアライズの経路（GetBytes / LoadBytes）を、
// SaveSessionBenchmarks がすでにカバーしている単一ドキュメントの経路から切り離して測ります。
// KeyedDocumentStore の汚れの管理は項目ごとではなくストア単位なので、既存のキーを1つ差し替えるだけでも、
// 次のコミットでは GetBytes() が、仕込んだ項目をすべてシリアライズし直します。ここが、
// ImmutableDictionary を直接シリアライズする最適化の対象になっている経路です。
//
// このプロジェクトのほかの場所で使う launchCount:1 ではなく、launchCount:3 を使っています。
// 200項目の ImmutableDictionary への書き込み経路で launchCount:1 / iterationCount:5 のジョブを使うと、
// 平均が3倍振れたり、きれいな実行同士でも順位が逆転したりしました（KeyedDocumentStoreBenchmarks を続けて
// 2回実行して確認しています）。ImmutableDictionary.SetItem が確保するノードの数はツリーの形で決まり、
// 試運転の段階でのウォームアップの変更が、その形を実行ごとに違うふうに乱してしまうためです。
// プロセスを複数回起動して平均を取ることで、それをならしています。
[MemoryDiagnoser]
[SimpleJob(launchCount: 3, warmupCount: 3, iterationCount: 5)]
public class SaveSessionCollectionBenchmarks
{
    private const int SeedCountSmall = 50;
    private const int SeedCountLarge = 200;

    private SaveSession _session50 = null!;
    private IBag<string, InventoryItem> _items50 = null!;
    private int _counter50;

    private SaveSession _session200 = null!;
    private IBag<string, InventoryItem> _items200 = null!;
    private int _counter200;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _session50 = new SaveSession(new InMemorySaveBackend());
        _items50 = _session50.AddCollection<string, InventoryItem>("items", i => i.ItemId);
        for (var i = 0; i < SeedCountSmall; i++)
            _items50.Upsert(new InventoryItem("item" + i, i));

        _session200 = new SaveSession(new InMemorySaveBackend());
        _items200 = _session200.AddCollection<string, InventoryItem>("items", i => i.ItemId);
        for (var i = 0; i < SeedCountLarge; i++)
            _items200.Upsert(new InventoryItem("item" + i, i));
    }

    [Benchmark(Baseline = true)]
    public async System.Threading.Tasks.Task CommitAsync_DirtyCollection_Seed50()
    {
        _items50.Upsert(new InventoryItem("item0", ++_counter50));
        await _session50.CommitAsync();
    }

    [Benchmark]
    public async System.Threading.Tasks.Task CommitAsync_DirtyCollection_Seed200()
    {
        _items200.Upsert(new InventoryItem("item0", ++_counter200));
        await _session200.CommitAsync();
    }
}
