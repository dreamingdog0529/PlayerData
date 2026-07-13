using System;

namespace PlayerData;

public sealed class SaveValidationException : Exception
{
    public SaveValidationException(string message) : base(message)
    {
    }

    public SaveValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
