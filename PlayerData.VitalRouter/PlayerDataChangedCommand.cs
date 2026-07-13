using global::VitalRouter;

namespace PlayerData.VitalRouter;

// The user's data type doesn't need to implement ICommand itself - this wraps it so
// PlayerData.Core's data model stays free of VitalRouter-specific requirements.
public readonly record struct PlayerDataChangedCommand<T>(T Value, T? Previous) : ICommand where T : class
{
    public PlayerDataChangedCommand(T value) : this(value, null) { }
}

public readonly record struct PlayerDataCollectionChangedCommand<TKey, T>(BagChange<TKey, T> Change) : ICommand
    where TKey : notnull
    where T : class;
