using Microsoft.CodeAnalysis;

namespace PlayerData.SourceGenerator;

internal static class Diagnostics
{
    // PD0003 (property cannot be marked with both [PlayerDataSingle] and [PlayerDataCollection]),
    // PD0004 (document property type must be IDoc<T>/IBag<TKey,T>) and PD0007 (document property
    // must be partial) were retired when document declarations moved from partial properties to
    // class-level attributes (Unity's bundled Roslyn caps at C# 12 and cannot parse C# 13 partial
    // properties). The IDs are not reused for different diagnostics.

    public static readonly DiagnosticDescriptor MissingVersionTolerantMemoryPackable = new(
        id: "PD0001",
        title: "Missing [MemoryPackable(GenerateType.VersionTolerant)]",
        messageFormat: "Document type '{0}' is not annotated with [MemoryPackable(GenerateType.VersionTolerant)], which is required for safe schema evolution",
        category: "PlayerData.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidPlayerDataKeyCount = new(
        id: "PD0002",
        title: "Collection entity type must have exactly one [PlayerDataKey] member",
        messageFormat: "Type '{0}' is used as an IBag entity but has {1} member(s) marked [PlayerDataKey]; exactly one is required",
        category: "PlayerData.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingDefaultFactory = new(
        id: "PD0005",
        title: "Cannot resolve document default factory",
        messageFormat: "Document '{0}' of type '{1}' needs [PlayerDataSingle(..., Default = \"MethodName\")] pointing to a public static method returning '{1}', or a public parameterless constructor",
        category: "PlayerData.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateDocumentKey = new(
        id: "PD0006",
        title: "Duplicate document storage key in the same session",
        messageFormat: "Multiple documents in session '{0}' share the storage key '{1}'",
        category: "PlayerData.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidPropertyNameIdentifier = new(
        id: "PD0008",
        title: "Document property name is not a valid C# identifier",
        messageFormat: "Session '{0}' declares a document with property name '{1}', which is not a valid C# identifier",
        category: "PlayerData.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicatePropertyName = new(
        id: "PD0009",
        title: "Duplicate document property name in the same session",
        messageFormat: "Multiple documents in session '{0}' resolve to the same generated property name '{1}'",
        category: "PlayerData.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ReservedPropertyName = new(
        id: "PD0010",
        title: "Document property name collides with a generated session member",
        messageFormat: "Session '{0}' declares a document with property name '{1}', which collides with a member the generator always emits",
        category: "PlayerData.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidEntityType = new(
        id: "PD0011",
        title: "Invalid document entity type",
        messageFormat: "Type '{0}' cannot be used as a [PlayerDataSingle]/[PlayerDataCollection] document; it must be a concrete, closed class type",
        category: "PlayerData.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor SessionClassNotPartial = new(
        id: "PD0012",
        title: "Session class must be partial",
        messageFormat: "Class '{0}' is marked with [PlayerDataSession] but is not declared partial",
        category: "PlayerData.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
