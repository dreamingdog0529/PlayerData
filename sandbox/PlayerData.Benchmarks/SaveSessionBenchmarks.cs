using BenchmarkDotNet.Attributes;
using PlayerData.Documents;
using PlayerData.Sessions;

namespace PlayerData.Benchmarks;

// SaveSession.CommitAsync を、メモリ上のバックエンドで測ります（入出力は除きます）。Tier 2 で整理した、
// 汚れているかの確認とスナップショットの処理だけを切り出します（Any(...) の LINQ によるボクシングから
// 手書きのループへ、ToDictionary から SnapshotItems へのコピーへ変えた部分です）。
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
public class SaveSessionBenchmarks
{
    private const int SeedCount = 50;

    private SaveSession _dirtySession = null!;
    private IDoc<PlayerProfile> _dirtyProfile = null!;
    private int _counter;

    private SaveSession _cleanSession = null!;

    private SaveSession _nullBackendSession = null!;
    private IDoc<PlayerProfile> _nullBackendProfile = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _dirtySession = new SaveSession(new InMemorySaveBackend());
        _dirtyProfile = _dirtySession.AddDocument("profile", () => new PlayerProfile(1, "Hero", 0));
        var dirtyItems = _dirtySession.AddCollection<string, InventoryItem>("items", i => i.ItemId);
        for (var i = 0; i < SeedCount; i++)
            dirtyItems.Upsert(new InventoryItem("item" + i, i));

        _cleanSession = new SaveSession(new InMemorySaveBackend());
        var cleanProfile = _cleanSession.AddDocument("profile", () => new PlayerProfile(1, "Hero", 0));
        var cleanItems = _cleanSession.AddCollection<string, InventoryItem>("items", i => i.ItemId);
        for (var i = 0; i < SeedCount; i++)
            cleanItems.Upsert(new InventoryItem("item" + i, i));
        _cleanSession.CommitAsync().AsTask().GetAwaiter().GetResult();
        _ = cleanProfile;

        _nullBackendSession = new SaveSession(new NullSaveBackend());
        _nullBackendProfile = _nullBackendSession.AddDocument("profile", () => new PlayerProfile(1, "Hero", 0));
        var nullItems = _nullBackendSession.AddCollection<string, InventoryItem>("items", i => i.ItemId);
        for (var i = 0; i < SeedCount; i++)
            nullItems.Upsert(new InventoryItem("item" + i, i));
        _nullBackendSession.CommitAsync().AsTask().GetAwaiter().GetResult();
    }

    // 呼び出しのたびにプロフィールのドキュメント自体を汚すので、BenchmarkDotNet が何回分を
    // 1回の計測にまとめようと、代表的な形のままでいられます。
    [Benchmark(Baseline = true)]
    public async System.Threading.Tasks.Task CommitAsync_Dirty()
    {
        _dirtyProfile.Replace(_dirtyProfile.Value with { Gold = ++_counter });
        await _dirtySession.CommitAsync();
    }

    // _cleanSession は、GlobalSetup での最初のコミットのあと二度と変更されません。そのため、呼び出しのたびに
    // IsDirty が false の、何もしない速い経路を通ります（定期的な自動セーブの確認では、よくある形です）。
    [Benchmark]
    public async System.Threading.Tasks.Task CommitAsync_NoOp()
    {
        await _cleanSession.CommitAsync();
    }

    // CommitAsync_Dirty と同じ定常状態の形ですが、保持も確保もしないバックエンドを使います。そのため
    // Allocated の列には、セッション自身のコミット経路（スナップショット、ドキュメントのディクショナリ、
    // バンドル、シリアライズ）の分だけが載ります。
    [Benchmark]
    public async System.Threading.Tasks.Task CommitAsync_Dirty_NullBackend()
    {
        _nullBackendProfile.Replace(_nullBackendProfile.Value with { Gold = ++_counter });
        await _nullBackendSession.CommitAsync();
    }
}
