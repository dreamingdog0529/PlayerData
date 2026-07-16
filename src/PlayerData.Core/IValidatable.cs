namespace PlayerData;

// Optional seam for document / entity types. SaveSession.CommitAsync invokes Validate() on
// every IValidatable value in dirty (and clean) participants before writing.
public interface IValidatable
{
    // Throw on failure (fail-fast; exceptions are not swallowed).
    void Validate();
}
