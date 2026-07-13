using R3;

namespace PlayerData.R3;

public static class PlayerDataCollectionR3Extensions
{
    public static Observable<BagChange<TKey, T>> AsObservable<TKey, T>(this IBag<TKey, T> collection)
        where TKey : notnull
        where T : class =>
        new PlayerDataCollectionObservable<TKey, T>(collection);
}
