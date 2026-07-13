using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlayerData;

public readonly record struct LoadResult(bool Found, int FormatVersion)
{
    public static LoadResult NotFound { get; } = new(false, 0);
}

public interface ISaveSession : IAsyncDisposable
{
    bool IsDirty { get; }

    bool IsLoaded { get; }

    // Found = false means no save on disk; documents stay at their constructor initial values.
    ValueTask<LoadResult> LoadAsync(CancellationToken cancellationToken = default);

    // No-op when !IsDirty. Runs validators, then serializes dirty documents and writes via the backend.
    ValueTask CommitAsync(CancellationToken cancellationToken = default);

    // Suppress store Changed and DirtyChanged until dispose; then flush coalesced notifications.
    IDisposable SuppressNotifications();

    // Session-level commit validators (in addition to IValidatable on document values).
    void AddValidator(ISaveValidator validator);

    void AddValidator(Action<ISaveSession> validate);

    event Action? Loaded;
    event Action? Committed;

    // Fires when IsDirty transitions (true after first dirty write, false after a clean commit).
    event Action<bool>? DirtyChanged;
}
