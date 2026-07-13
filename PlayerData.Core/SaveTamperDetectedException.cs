using System;

namespace PlayerData;

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
