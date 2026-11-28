using System;
using System.Threading;
using R3;
using PlayerData.Documents;

namespace PlayerData.R3;

// ふつうの Subject<T> を Changed につないだだけだと、ずっと外れなくなってしまいます（Changed が、ストアの
// 生存期間ずっと参照を持ち続けるためです）。そこで SubscribeCore を上書きして、.Subscribe() の
// 呼び出しごとに、付けたり外したりできるようにしています。
internal sealed class PlayerDataStoreObservable<T>(IDoc<T> store, bool replayCurrent) : Observable<T> where T : class
{
    protected override IDisposable SubscribeCore(Observer<T> observer)
    {
        if (replayCurrent)
            observer.OnNext(store.Value);

        return new Subscription(store, observer);
    }

    // 対象のストアと observer をフィールドに持って、自分のインスタンスメソッドを
    // Changed ハンドラとして使います。こうすると、購読のときに確保されるデリゲートは
    // ちょうど1つだけ（+= と -= で共有されます）で済みます。ローカル関数の Handler クロージャや、
    // Disposable.Create がそれを包んで作るもう1つの一時的なオブジェクトは要りません。
    // 対象のフィールドは、購読を外す前に Interlocked.Exchange で null にしておくので、
    // Dispose() を2回呼んでも、外す処理は1回だけ走ります。
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
