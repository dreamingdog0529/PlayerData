using System;

namespace PlayerData.Documents;

public interface IDoc<T> where T : class
{
    T Value { get; }

    // updater は CAS の競合時に複数回実行されることがありますので、純粋に（副作用なしで）書いてください。
    T Update(Func<T, T> updater);

    // Update(Func<T,T>) と同じものですが、キャプチャするクロージャに頼る代わりに状態を引き回します。
    // 呼び出しごとのデータがある場合でも、アロケーションなしに更新できます。
    T Update<TState>(TState state, Func<TState, T, T> updater);

    T Replace(T value);

    // 既定ではユーザーの書き込みのときだけ発火します。読み込みでは発火しません。
    event Action<DocChange<T>>? Changed;
}
