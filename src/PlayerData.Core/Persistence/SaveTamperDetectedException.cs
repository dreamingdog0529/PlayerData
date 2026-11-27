using System;

namespace PlayerData.Persistence;

public sealed class SaveTamperDetectedException : Exception
{
    public SaveTamperDetectedException(string message) : base(message)
    {
    }

    public SaveTamperDetectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
