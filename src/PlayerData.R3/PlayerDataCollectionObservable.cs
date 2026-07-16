using System;
using System.Threading;
using R3;

namespace PlayerData.R3;

internal sealed class PlayerDataCollectionObservable<TKey, T>(IBag<TKey, T> collection) : Observable<BagChange<TKey, T>>
    where TKey : notnull
    where T : class
{
    protected override IDisposable SubscribeCore(Observer<BagChange<TKey, T>> observer)
    {
        return new Subscription(collection, observer);
    }

    // Holds the target collection and observer as fields and uses its own instance method as the
    // Changed handler, so subscribing allocates exactly one delegate (shared by += and -=)
    // instead of a local-function Handler closure plus the closure Disposable.Create itself
    // wraps into a second ephemeral object. The target field is Interlocked.Exchange'd to null
    // before unsubscribing so a double Dispose() only detaches once.
    private sealed class Subscription : IDisposable
    {
        private IBag<TKey, T>? _collection;
        private readonly Observer<BagChange<TKey, T>> _observer;

        public Subscription(IBag<TKey, T> collection, Observer<BagChange<TKey, T>> observer)
        {
            _collection = collection;
            _observer = observer;
            collection.Changed += OnChanged;
        }

        private void OnChanged(BagChange<TKey, T> change) => _observer.OnNext(change);

        public void Dispose()
        {
            var collection = Interlocked.Exchange(ref _collection, null);
            if (collection is not null)
                collection.Changed -= OnChanged;
        }
    }
}
