namespace PlayerData;

/// <summary>Migrates a save bundle from <see cref="FromVersion"/> to <see cref="ToVersion"/>.</summary>
/// <remarks>Ownership: the input bundle's ownership transfers to the migration - it may mutate
/// the documents in place or return the same instance. Ownership of the returned bundle
/// transfers back to the caller; the migration must not retain references to it.</remarks>
public interface ISaveMigration
{
    int FromVersion { get; }
    int ToVersion { get; }
    SaveBundle Migrate(SaveBundle bundle);
}
