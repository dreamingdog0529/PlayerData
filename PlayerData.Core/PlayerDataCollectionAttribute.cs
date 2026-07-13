using System;

namespace PlayerData;

// Declares a keyed collection document on a [PlayerDataSession] class (stack one instance per
// document). documentType must have exactly one [PlayerDataKey] member; its type becomes
// IBag<TKey, T>'s key. propertyName defaults to documentType.Name when omitted and must be a
// valid C# identifier. The generator writes the property (IBag<TKey, T>) fully - do not declare
// it yourself. Storage key defaults to the resolved property name.
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
