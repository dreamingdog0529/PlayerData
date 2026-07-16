using System;
using System.Threading;
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

        return new Subscription(store, observer);
    }

    // Holds the target store and observer as fields and uses its own instance method as the
    // Changed handler, so subscribing allocates exactly one delegate (shared by += and -=)
    // instead of a local-function Handler closure plus the closure Disposable.Create itself
    // wraps into a second ephemeral object. The target field is Interlocked.Exchange'd to null
    // before unsubscribing so a double Dispose() only detaches once.
    private sealed class Subscription : IDisposable
    {
        private IDoc<T>? _store;
        private readonly Observer<T> _observer;

        public Subscription(IDoc<T> store, Observer<T> observer)
        {
            _store = store;
            _observer = observer;
            store.Changed += OnChanged;
        }

        private void OnChanged(DocChange<T> change) => _observer.OnNext(change.Current);

        public void Dispose()
        {
            var store = Interlocked.Exchange(ref _store, null);
            if (store is not null)
                store.Changed -= OnChanged;
        }
    }
}

internal sealed class PlayerDataStoreChangeObservable<T>(IDoc<T> store, bool replayCurrent) : Observable<DocChange<T>> where T : class
{
    protected override IDisposable SubscribeCore(Observer<DocChange<T>> observer)
    {
        if (replayCurrent)
            observer.OnNext(new DocChange<T>(null, store.Value, DataChangeCause.UserWrite));

        return new Subscription(store, observer);
    }

    private sealed class Subscription : IDisposable
    {
        private IDoc<T>? _store;
        private readonly Observer<DocChange<T>> _observer;

        public Subscription(IDoc<T> store, Observer<DocChange<T>> observer)
        {
            _store = store;
            _observer = observer;
            store.Changed += OnChanged;
        }

        private void OnChanged(DocChange<T> change) => _observer.OnNext(change);

        public void Dispose()
        {
            var store = Interlocked.Exchange(ref _store, null);
            if (store is not null)
                store.Changed -= OnChanged;
        }
    }
}
