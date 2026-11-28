using System;
using System.Threading;
using global::VitalRouter;
using PlayerData.Documents;

namespace PlayerData.VitalRouter;

public static class PlayerDataCollectionVitalRouterExtensions
{
    public static IDisposable PublishChangesTo<TKey, T>(this IBag<TKey, T> collection, ICommandPublisher publisher)
        where TKey : notnull
        where T : class =>
        new Subscription<TKey, T>(collection, publisher);

    // 対象のコレクションと publisher をフィールドに持って、自分のインスタンスメソッドを
    // Changed ハンドラとして使います。こうすると、購読のときに確保されるデリゲートは
    // ちょうど1つだけ（+= と -= で共有されます）で済みます。ローカル関数の Handler クロージャや、
    // ActionDisposable で包むもう1つのラムダのクロージャは要りません。
    // 対象のフィールドは、購読を外す前に Interlocked.Exchange で null にしておくので、
    // Dispose() を2回呼んでも、外す処理は1回だけ走ります。
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
