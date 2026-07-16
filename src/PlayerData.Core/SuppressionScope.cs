using System;

namespace PlayerData;

// Returned by ISaveSession.SuppressNotifications. A struct so opening a suppression scope
// allocates nothing; the interface returns this concrete type deliberately - an IDisposable
// return would box the struct and reintroduce the very allocation the type exists to remove,
// while `using` on the concrete type dispatches Dispose without boxing.
// Disposing the same copy twice is idempotent (the owner field is cleared), but each copy of a
// not-yet-disposed scope carries its own owner reference, so disposing two copies over-closes
// the session's suppression depth - SaveSession.EndSuppress detects that and throws rather than
// silently corrupting the counter. Treat a scope as dispose-exactly-once, as the `using`
// pattern naturally does. default(SuppressionScope) disposes as a no-op.
public struct SuppressionScope : IDisposable
{
    private SaveSession? _owner;

    internal SuppressionScope(SaveSession owner) => _owner = owner;

    public void Dispose()
    {
        var owner = _owner;
        _owner = null;
        owner?.EndSuppress();
    }
}
