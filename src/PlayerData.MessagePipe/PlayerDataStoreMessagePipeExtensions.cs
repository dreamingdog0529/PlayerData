using System;
using System.Threading;
using global::MessagePipe;
using PlayerData.Documents;

namespace PlayerData.MessagePipe;

public static class PlayerDataStoreMessagePipeExtensions
{
    public static IDisposable PublishChangesTo<T>(this IDoc<T> store, IPublisher<T> publisher) where T : class =>
        new ValueSubscription<T>(store, publisher);

    public static IDisposable PublishChangesTo<T>(this IDoc<T> store, IPublisher<DocChange<T>> publisher) where T : class =>
        new ChangeSubscription<T>(store, publisher);

    // 対象のストアと publisher をフィールドに持って、自分のインスタンスメソッドを
    // Changed ハンドラとして使います。こうすると、購読のときに確保されるデリゲートは
    // ちょうど1つだけ（+= と -= で共有されます）で済みます。ローカル関数の Handler クロージャや、
    // ActionDisposable で包むもう1つのラムダのクロージャは要りません。
    // 対象のフィールドは、購読を外す前に Interlocked.Exchange で null にしておくので、
    // Dispose() を2回呼んでも、外す処理は1回だけ走ります。
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
