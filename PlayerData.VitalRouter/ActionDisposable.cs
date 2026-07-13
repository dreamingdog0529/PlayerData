using System;

namespace PlayerData.VitalRouter;

internal sealed class ActionDisposable(Action action) : IDisposable
{
    public void Dispose() => action();
}
