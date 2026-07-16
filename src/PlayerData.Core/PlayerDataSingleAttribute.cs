using System;

namespace PlayerData;

// Declares a single (always-present) document on a [PlayerDataSession] class (stack one
// instance per document). propertyName defaults to documentType.Name when omitted and must be
// a valid C# identifier. The generator writes the property (IDoc<T>) fully - do not declare it
// yourself. Storage key defaults to the resolved property name.
// Default: name of a public static method on documentType that returns documentType
// (e.g. nameof(PlayerProfile.NewGame)). When omitted, documentType must have a public
// parameterless constructor.
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
