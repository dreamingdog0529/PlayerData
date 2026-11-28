using R3;
using PlayerData.Documents;

namespace PlayerData.R3;

public static class PlayerDataStoreR3Extensions
{
    // replayCurrent: true（既定値）のときは、Subscribe した直後に Value を流して、その後の書き込みも流します。
    public static Observable<T> AsObservable<T>(this IDoc<T> store, bool replayCurrent = true) where T : class =>
        new PlayerDataStoreObservable<T>(store, replayCurrent);

    public static Observable<DocChange<T>> AsChangeObservable<T>(this IDoc<T> store, bool replayCurrent = false) where T : class =>
        new PlayerDataStoreChangeObservable<T>(store, replayCurrent);
}
