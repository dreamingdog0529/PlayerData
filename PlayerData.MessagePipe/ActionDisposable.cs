using System;

namespace PlayerData.MessagePipe;

internal sealed class ActionDisposable(Action action) : IDisposable
{
    public void Dispose() => action();
}
