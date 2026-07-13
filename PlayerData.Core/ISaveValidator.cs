namespace PlayerData;

// Session-level commit gate. Registered via SaveSession.AddValidator / ISaveSession.AddValidator.
public interface ISaveValidator
{
    // Throw on failure (fail-fast; exceptions are not swallowed).
    void Validate(ISaveSession session);
}
