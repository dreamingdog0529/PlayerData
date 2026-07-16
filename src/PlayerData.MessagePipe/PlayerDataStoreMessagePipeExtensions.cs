using System;
using System.Threading;
using global::MessagePipe;

namespace PlayerData.MessagePipe;

public static class PlayerDataStoreMessagePipeExtensions
{
    public static IDisposable PublishChangesTo<T>(this IDoc<T> store, IPublisher<T> publisher) where T : class =>
        new ValueSubscription<T>(store, publisher);

    public static IDisposable PublishChangesTo<T>(this IDoc<T> store, IPublisher<DocChange<T>> publisher) where T : class =>
        new ChangeSubscription<T>(store, publisher);

    // Holds the target store and publisher as fields and uses its own instance method as the
    // Changed handler, so subscribing allocates exactly one delegate (shared by += and -=)
    // instead of a local-function Handler closure plus a second ActionDisposable-wrapping lambda
    // closure. The target field is Interlocked.Exchange'd to null before unsubscribing so a
    // double Dispose() only detaches once.
    private sealed class ValueSubscription<T> : IDisposable where T : class
    {
        private IDoc<T>? _store;
        private readonly IPublisher<T> _publisher;

        public ValueSubscription(IDoc<T> store, IPublisher<T> publisher)
        {
            _store = store;
            _publisher = publisher;
            store.Changed += OnChanged;
        }

        private void OnChanged(DocChange<T> change) => _publisher.Publish(change.Current);

        public void Dispose()
        {
            var store = Interlocked.Exchange(ref _store, null);
            if (store is not null)
                store.Changed -= OnChanged;
        }
    }

    private sealed class ChangeSubscription<T> : IDisposable where T : class
    {
        private IDoc<T>? _store;
        private readonly IPublisher<DocChange<T>> _publisher;

        public ChangeSubscription(IDoc<T> store, IPublisher<DocChange<T>> publisher)
        {
            _store = store;
            _publisher = publisher;
            store.Changed += OnChanged;
        }

        private void OnChanged(DocChange<T> change) => _publisher.Publish(change);

        public void Dispose()
        {
            var store = Interlocked.Exchange(ref _store, null);
            if (store is not null)
                store.Changed -= OnChanged;
        }
    }
}
