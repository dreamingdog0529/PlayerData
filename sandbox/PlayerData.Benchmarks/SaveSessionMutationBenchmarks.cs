using BenchmarkDotNet.Attributes;
using PlayerData.Documents;
using PlayerData.Sessions;

namespace PlayerData.Benchmarks;

// SaveSession につながった DocumentStore / KeyedDocumentStore の変更まわりだけを切り出して測ります
// （Update / Upsert のみで、CommitAsync やバックエンドへの入出力は含みません）。onMutated や抑制状態の
// 問い合わせをまとめた変更（ラウンド2の性能計画 ST-1）の効果を、SaveSessionBenchmarks がすでに
// カバーしている CommitAsync 側のコストと分けて見るためです。抑制されていない経路
// （SaveSession.OnMutated が抑制状態を1回だけ問い合わせて PublishDirty を進める経路）も、
// SuppressNotifications が有効な経路（RaiseChanged が _pendingChange / _pendingChanges に
// まとめられる経路）も測っています。まとめる前のデリゲートは、どちらの分岐が走るときも、
// 変更のたびに抑制状態を問い合わせていたためです。
// これらの処理は数十ナノ秒の範囲で動くので、1プロセスだけだと JIT の段階的な最適化のばらつきに、
// 本当の数ナノ秒の差が埋もれてしまうことがあります。launchCount で別プロセスの平均を取ることで、
// そのノイズを、本当の悪化や改善と見間違えないようにしています。
[MemoryDiagnoser]
[SimpleJob(launchCount: 3, warmupCount: 5, iterationCount: 10)]
public class SaveSessionMutationBenchmarks
{
    private SaveSession _session = null!;
    private IDoc<PlayerProfile> _profile = null!;
    private IBag<string, InventoryItem> _items = null!;
    private int _counter;

    private SaveSession _suppressedSession = null!;
    private IDoc<PlayerProfile> _suppressedProfile = null!;
    private IBag<string, InventoryItem> _suppressedItems = null!;
    private SuppressionScope _suppressScope;
    private int _suppressedCounter;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _session = new SaveSession(new InMemorySaveBackend());
        _session.DirtyChanged += static _ => { };
        _profile = _session.AddDocument("profile", () => new PlayerProfile(1, "Hero", 0));
        _items = _session.AddCollection<string, InventoryItem>("items", i => i.ItemId);
        _items.Upsert(new InventoryItem("item0", 0));

        _suppressedSession = new SaveSession(new InMemorySaveBackend());
        _suppressedSession.DirtyChanged += static _ => { };
        _suppressedProfile = _suppressedSession.AddDocument("profile", () => new PlayerProfile(1, "Hero", 0));
        _suppressedItems = _suppressedSession.AddCollection<string, InventoryItem>("items", i => i.ItemId);
        _suppressedItems.Upsert(new InventoryItem("item0", 0));
        _suppressScope = _suppressedSession.SuppressNotifications();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _suppressScope.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void DocumentUpdate_Unsuppressed()
    {
        _profile.Replace(_profile.Value with { Gold = ++_counter });
    }

    [Benchmark]
    public void CollectionUpsert_Unsuppressed()
    {
        _items.Upsert(new InventoryItem("item0", ++_counter));
    }

    [Benchmark]
    public void DocumentUpdate_Suppressed()
    {
        _suppressedProfile.Replace(_suppressedProfile.Value with { Gold = ++_suppressedCounter });
    }

    [Benchmark]
    public void CollectionUpsert_Suppressed()
    {
        _suppressedItems.Upsert(new InventoryItem("item0", ++_suppressedCounter));
    }
}
