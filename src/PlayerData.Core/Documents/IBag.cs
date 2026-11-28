using System;
using System.Collections.Generic;

namespace PlayerData.Documents;

public interface IBag<TKey, T>
    where TKey : notnull
    where T : class
{
    int Count { get; }

    // 弱い整合性です：別のスレッドが書き込んでいる最中に列挙すると、その書き込みの影響の一部だけが
    // 見えることがあります（例外は投げられませんし、エントリが中途半端な状態で見えることもありません。
    // 各キーの値は常に原子的に読めます）。イミュータブルなコレクションのスナップショットのように、
    // ある一時点を切り取ったものではない点にはご注意ください。
    IReadOnlyDictionary<TKey, T> Snapshot { get; }

    bool Contains(TKey key);

    bool TryGet(TKey key, out T value);

    T Get(TKey key);

    // キーは key = keySelector(entity) で決まります。entity が null のときは例外を投げます。
    T Upsert(T entity);

    // key が keySelector(entity) と一致しないときは例外を投げます。
    T Set(TKey key, T entity);

    bool TryUpdate(TKey key, Func<T, T> updater, out T updated);

    // キーがないときは KeyNotFoundException を投げます。
    T Update(TKey key, Func<T, T> updater);

    T GetOrAdd(TKey key, Func<TKey, T> factory);

    // 上のメソッドの、状態を引き回す版です。updater や factory が外のデータを必要とするときに、
    // 呼び出し側でキャプチャするクロージャを作らずに済みます。updater / factory は CAS の競合時に
    // 複数回実行されることがありますので、純粋に（副作用なしで）書いてください。
    bool TryUpdate<TState>(TKey key, TState state, Func<TState, T, T> updater, out T updated);

    // キーがないときは KeyNotFoundException を投げます。
    T Update<TState>(TKey key, TState state, Func<TState, T, T> updater);

    T GetOrAdd<TState>(TKey key, TState state, Func<TKey, TState, T> factory);

    bool Remove(TKey key);

    void Clear();

    // ユーザーの書き込みのときだけ発火します。読み込みでは発火しません。
    event Action<BagChange<TKey, T>>? Changed;
}
