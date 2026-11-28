using BenchmarkDotNet.Attributes;
using PlayerData.Documents;
using PlayerData.Sessions;

namespace PlayerData.Benchmarks;

// SuppressNotifications の周期全体（スコープを開く、変更する、Dispose して流し切るまで）を1つの処理として
// 測ります。変更ごとのベンチマークでは見えない部分、つまりスコープのインスタンス自身と、抑制されている間に
// コレクションのストアがため込んでまとめる、保留中の変更のリストに値段を付けます。
// 変更では、あらかじめ確保しておいたインスタンスへ差し替えます（ZeroAllocationBenchmarks と同じやり方です）。
// これで、呼び出し側がデータを組み立てる分は計測から外れます。また、ハンドラを購読しておくことで、
// UI につながったセッションと同じように、流し切るときに実際にハンドラが呼ばれます。
// これらの周期は数十ナノ秒からマイクロ秒の範囲にあるので、1プロセスだけだと JIT の段階的な最適化の
// ばらつきに効果が埋もれてしまうことがあります。launchCount 3 で、別プロセスの平均を取ります。
[MemoryDiagnoser]
[SimpleJob(launchCount: 3, warmupCount: 5, iterationCount: 10)]
public class SuppressionCycleBenchmarks
{
    private const int BurstSize = 8;

    private SaveSession _session = null!;
    private IBag<string, InventoryItem> _items = null!;
    private InventoryItem _itemA = null!;
    private InventoryItem _itemB = null!;
    private int _flip;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _session = new SaveSession(new NullSaveBackend());
        _session.DirtyChanged += static _ => { };
        _items = _session.AddCollection<string, InventoryItem>("items", i => i.ItemId);
        _items.Changed += static _ => { };
        _itemA = new InventoryItem("item0", 1);
        _itemB = new InventoryItem("item0", 2);
        _items.Upsert(_itemA);
    }

    // 開いてすぐに破棄します。スコープ自身のコストだけを切り出します。
    [Benchmark(Baseline = true)]
    public void SuppressAndFlush_Empty()
    {
        using (_session.SuppressNotifications())
        {
        }
    }

    // 代表的な、まとめての変更です。BurstSize 件の、まとめられたコレクションの変更が、破棄のときに流し切られます。
    [Benchmark]
    public void SuppressAndFlush_CollectionBurst()
    {
        using (_session.SuppressNotifications())
        {
            for (var i = 0; i < BurstSize; i++)
                _items.Upsert((++_flip & 1) == 0 ? _itemA : _itemB);
        }
    }
}
