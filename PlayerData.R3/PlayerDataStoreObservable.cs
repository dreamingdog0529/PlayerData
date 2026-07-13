using System;
using R3;

namespace PlayerData.R3;

// A plain Subject<T> wired to Changed would never detach (Changed holds a reference to it for the
// store's whole lifetime). Overriding SubscribeCore instead gives each individual .Subscribe()
// call its own attach/detach lifecycle.
internal sealed class PlayerDataStoreObservable<T>(IDoc<T> store, bool replayCurrent) : Observable<T> where T : class
{
    protected override IDisposable SubscribeCore(Observer<T> observer)
    {
        if (replayCurrent)
            observer.OnNext(store.Value);

        void Handler(DocChange<T> change) => observer.OnNext(change.Current);
        store.Changed += Handler;
        return Disposable.Create(() => store.Changed -= Handler);
    }
}

internal sealed class PlayerDataStoreChangeObservable<T>(IDoc<T> store, bool replayCurrent) : Observable<DocChange<T>> where T : class
{
    protected override IDisposable SubscribeCore(Observer<DocChange<T>> observer)
    {
        if (replayCurrent)
            observer.OnNext(new DocChange<T>(null, store.Value, DataChangeCause.UserWrite));

        void Handler(DocChange<T> change) => observer.OnNext(change);
        store.Changed += Handler;
        return Disposable.Create(() => store.Changed -= Handler);
    }
}
