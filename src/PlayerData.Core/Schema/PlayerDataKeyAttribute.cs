using System;
using PlayerData.Documents;

namespace PlayerData.Schema;

// IBag<TKey, T> で使うエンティティ型について、その値がバッグのキーになるメンバーを示します。
// 1つのエンティティ型につき、この属性を付けるメンバーはちょうど1つだけにしてください。
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
public sealed class PlayerDataKeyAttribute : Attribute
{
}
