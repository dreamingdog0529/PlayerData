using System.Threading;
using System.Threading.Tasks;
using PlayerData.Persistence;

namespace PlayerData.Benchmarks;

// バックエンドを計測から完全に取り除きます。書き込みは捨てられ、読み取りでは既存のセーブはないものとして
// 返します。InMemorySaveBackend と違って何も保持せず何も確保しないので、これを向き先にしたコミットの
// ベンチマークでは、セッション自身の経路だけに値段が付きます。
internal sealed class NullSaveBackend : ISaveBackend
{
    public ValueTask<SaveBundle?> ReadAsync(CancellationToken cancellationToken = default) => new((SaveBundle?)null);

    public ValueTask WriteAsync(SaveBundle bundle, CancellationToken cancellationToken = default) => default;
}
