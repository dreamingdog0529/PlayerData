using System;
using System.Threading;
using global::VitalRouter;

namespace PlayerData.VitalRouter;

public static class PlayerDataCollectionVitalRouterExtensions
{
    public static IDisposable PublishChangesTo<TKey, T>(this IBag<TKey, T> collection, ICommandPublisher publisher)
        where TKey : notnull
        where T : class =>
        new Subscription<TKey, T>(collection, publisher);

    // Holds the target collection and publisher as fields and uses its own instance method as
    // the Changed handler, so subscribing allocates exactly one delegate (shared by += and -=)
    // instead of a local-function Handler closure plus a second ActionDisposable-wrapping lambda
    // closure. The target field is Interlocked.Exchange'd to null before unsubscribing so a
    // double Dispose() only detaches once.
    private sealed class Subscription<TKey, T> : IDisposable
        where TKey : notnull
        where T : class
    {
        private IBag<TKey, T>? _collection;
        private readonly ICommandPublisher _publisher;

        public Subscription(IBag<TKey, T> collection, ICommandPublisher publisher)
        {
            _collection = collection;
            _publisher = publisher;
            collection.Changed += OnChanged;
        }

        private void OnChanged(BagChange<TKey, T> change) =>
            _publisher.PublishAsync(new PlayerDataCollectionChangedCommand<TKey, T>(change));

        public void Dispose()
        {
            var collection = Interlocked.Exchange(ref _collection, null);
            if (collection is not null)
                collection.Changed -= OnChanged;
        }
    }
}
