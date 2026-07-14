using System;
using System.Threading;
using global::VitalRouter;

namespace PlayerData.VitalRouter;

public static class PlayerDataStoreVitalRouterExtensions
{
    public static IDisposable PublishChangesTo<T>(this IDoc<T> store, ICommandPublisher publisher) where T : class =>
        new Subscription<T>(store, publisher);

    // Holds the target store and publisher as fields and uses its own instance method as the
    // Changed handler, so subscribing allocates exactly one delegate (shared by += and -=)
    // instead of a local-function Handler closure plus a second ActionDisposable-wrapping lambda
    // closure. The target field is Interlocked.Exchange'd to null before unsubscribing so a
    // double Dispose() only detaches once.
    private sealed class Subscription<T> : IDisposable where T : class
    {
        private IDoc<T>? _store;
        private readonly ICommandPublisher _publisher;

        public Subscription(IDoc<T> store, ICommandPublisher publisher)
        {
            _store = store;
            _publisher = publisher;
            store.Changed += OnChanged;
        }

        private void OnChanged(DocChange<T> change) =>
            _publisher.PublishAsync(new PlayerDataChangedCommand<T>(change.Current, change.Previous));

        public void Dispose()
        {
            var store = Interlocked.Exchange(ref _store, null);
            if (store is not null)
                store.Changed -= OnChanged;
        }
    }
}
