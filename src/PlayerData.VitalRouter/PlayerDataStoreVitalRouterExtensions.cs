using System;
using System.Threading;
using global::VitalRouter;
using PlayerData.Documents;

namespace PlayerData.VitalRouter;

public static class PlayerDataStoreVitalRouterExtensions
{
    public static IDisposable PublishChangesTo<T>(this IDoc<T> store, ICommandPublisher publisher) where T : class =>
        new Subscription<T>(store, publisher);

    // 対象のストアと publisher をフィールドに持って、自分のインスタンスメソッドを
    // Changed ハンドラとして使います。こうすると、購読のときに確保されるデリゲートは
    // ちょうど1つだけ（+= と -= で共有されます）で済みます。ローカル関数の Handler クロージャや、
    // ActionDisposable で包むもう1つのラムダのクロージャは要りません。
    // 対象のフィールドは、購読を外す前に Interlocked.Exchange で null にしておくので、
    // Dispose() を2回呼んでも、外す処理は1回だけ走ります。
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
