using global::VitalRouter;
using PlayerData.Documents;

namespace PlayerData.VitalRouter;

// ユーザーのデータ型に ICommand を実装してもらう必要はありません。この型が包んであげることで、
// PlayerData.Core のデータモデルに、VitalRouter 固有の都合を持ち込まずに済みます。
public readonly record struct PlayerDataChangedCommand<T>(T Value, T? Previous) : ICommand where T : class
{
    public PlayerDataChangedCommand(T value) : this(value, null) { }
}

public readonly record struct PlayerDataCollectionChangedCommand<TKey, T>(BagChange<TKey, T> Change) : ICommand
    where TKey : notnull
    where T : class;
