namespace PlayerData;

public enum DataChangeCause
{
    UserWrite,
    Loaded,
}

public enum PlayerDataChangeKind
{
    Upserted,
    Removed,
    Cleared,
}

public readonly record struct DocChange<T>(T? Previous, T Current, DataChangeCause Cause)
    where T : class;

public readonly record struct BagChange<TKey, T>(TKey Key, PlayerDataChangeKind Kind, T? Previous, T? Value, DataChangeCause Cause)
    where TKey : notnull
    where T : class;

// Retained alias for adapter packages that previously used PlayerDataChange.
public readonly record struct PlayerDataChange<TKey, T>(TKey Key, PlayerDataChangeKind Kind, T? Value)
    where TKey : notnull
    where T : class
{
    public static PlayerDataChange<TKey, T> From(BagChange<TKey, T> change) =>
        new(change.Key, change.Kind, change.Value);
}
