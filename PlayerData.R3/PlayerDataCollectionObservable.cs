using System;
using R3;

namespace PlayerData.R3;

internal sealed class PlayerDataCollectionObservable<TKey, T>(IBag<TKey, T> collection) : Observable<BagChange<TKey, T>>
    where TKey : notnull
    where T : class
{
    protected override IDisposable SubscribeCore(Observer<BagChange<TKey, T>> observer)
    {
        void Handler(BagChange<TKey, T> change) => observer.OnNext(change);
        collection.Changed += Handler;
        return Disposable.Create(() => collection.Changed -= Handler);
    }
}
