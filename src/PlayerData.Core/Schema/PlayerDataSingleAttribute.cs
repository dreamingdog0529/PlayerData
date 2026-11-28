using System;
using PlayerData.Documents;

namespace PlayerData.Schema;

// [PlayerDataSession] を付けたクラスに、単一の（常に存在する）文書を宣言するためのものです
// （文書1つにつき1つずつ重ねて付けます）。propertyName を省略すると documentType.Name が
// 使われますが、指定する場合は C# の識別子として正しい名前でお願いします。プロパティ
// （IDoc<T>）はジェネレーターがまるごと書きますので、ご自分では宣言しないでくださいね。
// 保存用のキーは、省略すると解決されたプロパティ名がそのまま使われます。
// Default: documentType を返す、documentType 上の public static メソッドの名前です
// （たとえば nameof(PlayerProfile.NewGame) のように指定します）。省略した場合は、
// documentType に public な引数なしコンストラクターが必要です。
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class PlayerDataSingleAttribute : Attribute
{
    public PlayerDataSingleAttribute(Type documentType, string? propertyName = null)
    {
        DocumentType = documentType;
        PropertyName = propertyName;
    }

    public Type DocumentType { get; }

    public string? PropertyName { get; }

    public string? Key { get; set; }

    public string? Default { get; set; }
}
