using R3;

namespace PlayerData.R3;

public static class PlayerDataStoreR3Extensions
{
    // replayCurrent: when true (default), emits Value immediately on Subscribe, then subsequent writes.
    public static Observable<T> AsObservable<T>(this IDoc<T> store, bool replayCurrent = true) where T : class =>
        new PlayerDataStoreObservable<T>(store, replayCurrent);

    public static Observable<DocChange<T>> AsChangeObservable<T>(this IDoc<T> store, bool replayCurrent = false) where T : class =>
        new PlayerDataStoreChangeObservable<T>(store, replayCurrent);
}
