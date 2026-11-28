using System;
using System.Threading;
using R3;
using PlayerData.Documents;

namespace PlayerData.R3;

internal sealed class PlayerDataCollectionObservable<TKey, T>(IBag<TKey, T> collection) : Observable<BagChange<TKey, T>>
    where TKey : notnull
    where T : class
{
    protected override IDisposable SubscribeCore(Observer<BagChange<TKey, T>> observer)
    {
        return new Subscription(collection, observer);
    }

    // 対象のコレクションと observer をフィールドに持って、自分のインスタンスメソッドを
    // Changed ハンドラとして使います。こうすると、購読のときに確保されるデリゲートは
    // ちょうど1つだけ（+= と -= で共有されます）で済みます。ローカル関数の Handler クロージャや、
    // Disposable.Create がそれを包んで作るもう1つの一時的なオブジェクトは要りません。
    // 対象のフィールドは、購読を外す前に Interlocked.Exchange で null にしておくので、
    // Dispose() を2回呼んでも、外す処理は1回だけ走ります。
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
