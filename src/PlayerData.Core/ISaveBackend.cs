using System.Threading;
using System.Threading.Tasks;

namespace PlayerData;

public interface ISaveBackend
{
    // null = no save present.
    ValueTask<SaveBundle?> ReadAsync(CancellationToken cancellationToken = default);

    ValueTask WriteAsync(SaveBundle bundle, CancellationToken cancellationToken = default);
}
