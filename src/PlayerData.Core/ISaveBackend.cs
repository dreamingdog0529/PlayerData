using System.Threading;
using System.Threading.Tasks;

namespace PlayerData;

/// <summary>Persists and restores a <see cref="SaveBundle"/>.</summary>
/// <remarks>
/// Ownership contract:
/// <list type="bullet">
/// <item><description><see cref="ReadAsync"/>: ownership of the returned bundle - its Documents
/// dictionary and every byte[] value in it - transfers to the caller. The implementation must
/// not retain or reuse references to them after returning; the caller is free to mutate the
/// contents in place.</description></item>
/// <item><description><see cref="WriteAsync"/>: the bundle argument (including its Documents
/// dictionary and byte[] values) is read-only and only valid for the duration of the call. The
/// caller may recycle those objects as soon as the call completes, so an implementation that
/// wants to keep the data must copy it.</description></item>
/// </list>
/// </remarks>
public interface ISaveBackend
{
    /// <summary>Reads the persisted bundle, or null when no save is present. Ownership of the
    /// returned bundle transfers to the caller (see the ownership contract on this interface).</summary>
    ValueTask<SaveBundle?> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists the bundle. The bundle is only valid for the duration of the call
    /// (see the ownership contract on this interface).</summary>
    ValueTask WriteAsync(SaveBundle bundle, CancellationToken cancellationToken = default);
}
