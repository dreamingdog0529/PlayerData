using System;

namespace PlayerData.Schema;

// partial クラスをセーブセッションの表側として示すためのものです。文書はクラスレベルの
// [PlayerDataSingle] / [PlayerDataCollection] 属性で宣言します（文書1つにつき1つずつ重ねて
// 付けます）し、対応するプロパティはジェネレーターがまるごと書いてくれます。1回のコンパイルで
// 複数のセッションを作っても大丈夫です。
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class PlayerDataSessionAttribute : Attribute
{
    // true にすると、DisposeAsync のときに変更があればコミットしてくれます。既定は false で、その場合は破棄しても何もしません。
    public bool AutoCommitOnDispose { get; set; }
}
