namespace PlayerData;

public interface ISaveMigration
{
    int FromVersion { get; }
    int ToVersion { get; }
    SaveBundle Migrate(SaveBundle bundle);
}
