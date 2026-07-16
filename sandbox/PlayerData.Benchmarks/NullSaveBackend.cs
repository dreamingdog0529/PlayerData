using System.Threading;
using System.Threading.Tasks;

namespace PlayerData.Benchmarks;

// Removes the backend from the measurement entirely: writes are discarded and reads report no
// existing save. Unlike InMemorySaveBackend it retains nothing and allocates nothing, so commit
// benchmarks that target it price the session's own path with zero backend contribution.
internal sealed class NullSaveBackend : ISaveBackend
{
    public ValueTask<SaveBundle?> ReadAsync(CancellationToken cancellationToken = default) => new((SaveBundle?)null);

    public ValueTask WriteAsync(SaveBundle bundle, CancellationToken cancellationToken = default) => default;
}
