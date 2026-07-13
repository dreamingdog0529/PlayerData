using System;

namespace PlayerData;

// Marks the member whose value is the bag key for an entity type used in IBag<TKey, T>.
// Exactly one member per entity type must carry this attribute.
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
public sealed class PlayerDataKeyAttribute : Attribute
{
}
