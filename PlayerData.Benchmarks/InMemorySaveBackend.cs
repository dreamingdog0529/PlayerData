using System.Threading;
using System.Threading.Tasks;

namespace PlayerData.Benchmarks;

// Isolates SaveSession benchmarks from disk I/O so measured time/allocations reflect only
// SaveSession's own commit/load logic.
internal sealed class InMemorySaveBackend : ISaveBackend
{
    private SaveBundle? _bundle;

    public ValueTask<SaveBundle?> ReadAsync(CancellationToken cancellationToken = default) => new(_bundle);

    public ValueTask WriteAsync(SaveBundle bundle, CancellationToken cancellationToken = default)
    {
        _bundle = bundle;
        return default;
    }
}
