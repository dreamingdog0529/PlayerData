using System;
using PlayerData.Documents;

namespace PlayerData.Schema;

// [PlayerDataSession] を付けたクラスに、キー付きのコレクション文書を宣言するためのものです
// （文書1つにつき1つずつ重ねて付けます）。documentType には [PlayerDataKey] を付けたメンバーが
// ちょうど1つ必要で、その型が IBag<TKey, T> のキーになります。propertyName を省略すると
// documentType.Name が使われますが、指定する場合は C# の識別子として正しい名前でお願いします。
// プロパティ（IBag<TKey, T>）はジェネレーターがまるごと書きますので、ご自分では宣言しないで
// くださいね。保存用のキーは、省略すると解決されたプロパティ名がそのまま使われます。
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class PlayerDataCollectionAttribute : Attribute
{
    public PlayerDataCollectionAttribute(Type documentType, string? propertyName = null)
    {
        DocumentType = documentType;
        PropertyName = propertyName;
    }

    public Type DocumentType { get; }

    public string? PropertyName { get; }

    public string? Key { get; set; }
}
