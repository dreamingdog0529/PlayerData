using System;

namespace PlayerData.Sessions;

// ISaveSession.SuppressNotifications が返すものです。struct にしてありますので、抑制スコープを
// 開いても何もアロケーションしません。インターフェースがこの具象型を返すのは意図的なもので、
// IDisposable を返す形にすると struct がボックス化されてしまい、この型がなくそうとしている
// アロケーションが戻ってきてしまいます。一方、具象型に対する `using` であれば、ボックス化なしに
// Dispose が呼べます。
// 同じコピーを2回破棄しても冪等です（owner フィールドがクリアされるため）が、まだ破棄されて
// いないスコープのコピーはそれぞれが owner への参照を持っていますので、2つのコピーを破棄すると
// セッションの抑制の深さを閉じすぎてしまいます。その場合は SaveSession.EndSuppress が検知して、
// カウンターを黙って壊してしまう代わりに例外を投げます。スコープはちょうど1回だけ破棄するもの
// として扱ってください。`using` の形を使えば自然にそうなります。なお、
// default(SuppressionScope) の破棄は何もしないものとして扱われます。
public struct SuppressionScope : IDisposable
{
    private SaveSession? _owner;

    internal SuppressionScope(SaveSession owner) => _owner = owner;

    public void Dispose()
    {
        var owner = _owner;
        _owner = null;
        owner?.EndSuppress();
    }
}
