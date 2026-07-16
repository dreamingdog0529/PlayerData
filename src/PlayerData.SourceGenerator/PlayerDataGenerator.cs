using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace PlayerData.SourceGenerator;

[Generator]
public sealed class PlayerDataGenerator : IIncrementalGenerator
{
    private const string SingleAttributeMetadataName = "PlayerData.PlayerDataSingleAttribute";
    private const string CollectionAttributeMetadataName = "PlayerData.PlayerDataCollectionAttribute";
    private const string SessionAttributeMetadataName = "PlayerData.PlayerDataSessionAttribute";
    private const string KeyAttributeMetadataName = "PlayerData.PlayerDataKeyAttribute";
    private const string MemoryPackableAttributeMetadataName = "MemoryPack.MemoryPackableAttribute";

    // Fixed members every generated session always emits; a document property name must not
    // collide with any of them.
    private static readonly HashSet<string> ReservedPropertyNames = new(StringComparer.Ordinal)
    {
        "IsDirty", "IsLoaded", "Loaded", "Committed", "DirtyChanged",
        "LoadAsync", "CommitAsync", "SuppressNotifications", "AddValidator", "DisposeAsync", "OpenAsync",
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var sessionTargets = context.SyntaxProvider.ForAttributeWithMetadataName(
            SessionAttributeMetadataName,
            predicate: static (node, _) => node is ClassDeclarationSyntax,
            transform: static (ctx, _) => ctx);

        context.RegisterSourceOutput(sessionTargets.Collect(), static (spc, sessions) => Emit(spc, sessions));
    }

    private static void Emit(SourceProductionContext context, ImmutableArray<GeneratorAttributeSyntaxContext> sessions)
    {
        foreach (var sessionCtx in sessions)
        {
            var sessionType = (INamedTypeSymbol)sessionCtx.TargetSymbol;

            if (sessionCtx.TargetNode is not ClassDeclarationSyntax classSyntax ||
                !classSyntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.SessionClassNotPartial,
                    sessionType.Locations.FirstOrDefault(),
                    sessionType.Name));
                continue;
            }

            var autoCommit = GetAutoCommitOnDispose(sessionCtx.Attributes);
            var documents = new List<DocumentInfo>();
            var seenPropertyNames = new HashSet<string>(StringComparer.Ordinal);
            var seenFieldNames = new HashSet<string>(StringComparer.Ordinal) { "_session" };
            var seenStorageKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var attribute in sessionType.GetAttributes())
            {
                var metadataName = attribute.AttributeClass?.ToDisplayString();
                var isCollection = metadataName == CollectionAttributeMetadataName;
                if (!isCollection && metadataName != SingleAttributeMetadataName) continue;

                var location = GetAttributeLocation(attribute) ?? sessionType.Locations.FirstOrDefault();

                if (attribute.ConstructorArguments.Length < 1 ||
                    attribute.ConstructorArguments[0].Value is not ITypeSymbol entityTypeRaw ||
                    !IsValidEntityType(entityTypeRaw))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.InvalidEntityType,
                        location,
                        attribute.ConstructorArguments.Length > 0
                            ? attribute.ConstructorArguments[0].Value?.ToString() ?? "?"
                            : "?"));
                    continue;
                }

                var entityType = (INamedTypeSymbol)entityTypeRaw;

                var rawPropertyName = attribute.ConstructorArguments.Length > 1
                    ? attribute.ConstructorArguments[1].Value as string
                    : null;
                var propertyName = string.IsNullOrEmpty(rawPropertyName) ? entityType.Name : rawPropertyName!;

                if (!SyntaxFacts.IsValidIdentifier(propertyName))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.InvalidPropertyNameIdentifier,
                        location,
                        sessionType.Name,
                        propertyName));
                    continue;
                }

                if (!seenPropertyNames.Add(propertyName))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.DuplicatePropertyName,
                        location,
                        sessionType.Name,
                        propertyName));
                    continue;
                }

                if (ReservedPropertyNames.Contains(propertyName) || !seenFieldNames.Add(ToFieldName(propertyName)))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.ReservedPropertyName,
                        location,
                        sessionType.Name,
                        propertyName));
                    continue;
                }

                if (!HasVersionTolerantMemoryPackable(entityType))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.MissingVersionTolerantMemoryPackable,
                        entityType.Locations.FirstOrDefault() ?? location,
                        entityType.Name));
                    continue;
                }

                var storageKey = GetNamedArgument(attribute, "Key") ?? propertyName;

                if (isCollection)
                {
                    if (!TryGetKeyMember(entityType, out var keyMember, out var keyType))
                    {
                        var count = CountKeyMembers(entityType);
                        context.ReportDiagnostic(Diagnostic.Create(
                            Diagnostics.InvalidPlayerDataKeyCount,
                            entityType.Locations.FirstOrDefault() ?? location,
                            entityType.Name,
                            count));
                        continue;
                    }

                    if (!seenStorageKeys.Add(storageKey))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            Diagnostics.DuplicateDocumentKey,
                            location,
                            sessionType.Name,
                            storageKey));
                        continue;
                    }

                    documents.Add(DocumentInfo.Collection(propertyName, entityType, keyType, keyMember, storageKey));
                }
                else
                {
                    if (!TryResolveDefaultFactory(entityType, attribute, out var defaultExpr))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            Diagnostics.MissingDefaultFactory,
                            location,
                            propertyName,
                            entityType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                        continue;
                    }

                    if (!seenStorageKeys.Add(storageKey))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            Diagnostics.DuplicateDocumentKey,
                            location,
                            sessionType.Name,
                            storageKey));
                        continue;
                    }

                    documents.Add(DocumentInfo.Single(propertyName, entityType, storageKey, defaultExpr));
                }
            }

            var source = GenerateSession(sessionType, documents, autoCommit);
            context.AddSource($"{sessionType.Name}.PlayerDataSession.g.cs", SourceText.From(source, Encoding.UTF8));
        }
    }

    private static string GenerateSession(INamedTypeSymbol session, List<DocumentInfo> documents, bool autoCommit)
    {
        var sessionNs = session.ContainingNamespace.IsGlobalNamespace
            ? null
            : session.ContainingNamespace.ToDisplayString();
        var indent = sessionNs is null ? "" : "    ";

        var fields = new List<string>();
        var props = new List<string>();
        var ctorBody = new List<string>
        {
            "if (backend is null) throw new global::System.ArgumentNullException(nameof(backend));",
            "_session = new global::PlayerData.SaveSession(backend, migrations);",
        };

        foreach (var doc in documents)
        {
            var propName = doc.PropertyName;
            var fieldName = ToFieldName(propName);
            var keyLiteral = ToLiteral(doc.StorageKey);

            if (doc.IsCollection)
            {
                var keyRef = doc.KeyType!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var typeRef = doc.EntityType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var keyAccess = FormatKeyAccess(doc.KeyMember!);
                fields.Add($"private readonly global::PlayerData.IBag<{keyRef}, {typeRef}> {fieldName};");
                props.Add($"public global::PlayerData.IBag<{keyRef}, {typeRef}> {propName} => {fieldName};");
                ctorBody.Add($"{fieldName} = _session.AddCollection<{keyRef}, {typeRef}>({keyLiteral}, static e => e{keyAccess});");
            }
            else
            {
                var typeRef = doc.EntityType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                fields.Add($"private readonly global::PlayerData.IDoc<{typeRef}> {fieldName};");
                props.Add($"public global::PlayerData.IDoc<{typeRef}> {propName} => {fieldName};");
                ctorBody.Add($"{fieldName} = _session.AddDocument({keyLiteral}, {doc.DefaultFactoryExpression});");
            }
        }

        var accessibility = session.DeclaredAccessibility switch
        {
            Accessibility.Public => "public ",
            Accessibility.Internal => "internal ",
            Accessibility.ProtectedAndInternal => "private protected ",
            Accessibility.ProtectedOrInternal => "protected internal ",
            Accessibility.Protected => "protected ",
            Accessibility.Private => "private ",
            _ => "",
        };

        var sb = new StringBuilder();
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        if (sessionNs is not null)
        {
            sb.AppendLine($"namespace {sessionNs}");
            sb.AppendLine("{");
        }

        sb.AppendLine($"{indent}{accessibility}partial class {session.Name} : global::PlayerData.ISaveSession");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    private readonly global::PlayerData.SaveSession _session;");
        foreach (var f in fields)
            sb.AppendLine($"{indent}    {f}");
        sb.AppendLine();
        sb.AppendLine($"{indent}    public {session.Name}(global::PlayerData.ISaveBackend backend, global::System.Collections.Generic.IEnumerable<global::PlayerData.ISaveMigration>? migrations = null)");
        sb.AppendLine($"{indent}    {{");
        foreach (var line in ctorBody)
            sb.AppendLine($"{indent}        {line}");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine();
        sb.AppendLine($"{indent}    public static async global::System.Threading.Tasks.ValueTask<{session.Name}> OpenAsync(global::PlayerData.ISaveBackend backend, global::System.Threading.CancellationToken cancellationToken = default, global::System.Collections.Generic.IEnumerable<global::PlayerData.ISaveMigration>? migrations = null)");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        var session = new {session.Name}(backend, migrations);");
        sb.AppendLine($"{indent}        await session.LoadAsync(cancellationToken).ConfigureAwait(false);");
        sb.AppendLine($"{indent}        return session;");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine();
        foreach (var p in props)
            sb.AppendLine($"{indent}    {p}");
        sb.AppendLine();
        sb.AppendLine($"{indent}    public bool IsDirty => _session.IsDirty;");
        sb.AppendLine($"{indent}    public bool IsLoaded => _session.IsLoaded;");
        sb.AppendLine();
        sb.AppendLine($"{indent}    public event global::System.Action? Loaded");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        add => _session.Loaded += value;");
        sb.AppendLine($"{indent}        remove => _session.Loaded -= value;");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine();
        sb.AppendLine($"{indent}    public event global::System.Action? Committed");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        add => _session.Committed += value;");
        sb.AppendLine($"{indent}        remove => _session.Committed -= value;");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine();
        sb.AppendLine($"{indent}    public event global::System.Action<bool>? DirtyChanged");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        add => _session.DirtyChanged += value;");
        sb.AppendLine($"{indent}        remove => _session.DirtyChanged -= value;");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine();
        sb.AppendLine($"{indent}    public global::System.Threading.Tasks.ValueTask<global::PlayerData.LoadResult> LoadAsync(global::System.Threading.CancellationToken cancellationToken = default) => _session.LoadAsync(cancellationToken);");
        sb.AppendLine();
        sb.AppendLine($"{indent}    public global::System.Threading.Tasks.ValueTask CommitAsync(global::System.Threading.CancellationToken cancellationToken = default) => _session.CommitAsync(cancellationToken);");
        sb.AppendLine();
        sb.AppendLine($"{indent}    public global::PlayerData.SuppressionScope SuppressNotifications() => _session.SuppressNotifications();");
        sb.AppendLine();
        sb.AppendLine($"{indent}    public void AddValidator(global::PlayerData.ISaveValidator validator) => _session.AddValidator(validator);");
        sb.AppendLine();
        sb.AppendLine($"{indent}    public void AddValidator(global::System.Action<global::PlayerData.ISaveSession> validate) => _session.AddValidator(validate);");
        sb.AppendLine();
        if (autoCommit)
        {
            sb.AppendLine($"{indent}    public async global::System.Threading.Tasks.ValueTask DisposeAsync()");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        if (IsDirty)");
            sb.AppendLine($"{indent}            await CommitAsync().ConfigureAwait(false);");
            sb.AppendLine($"{indent}    }}");
        }
        else
        {
            sb.AppendLine($"{indent}    public global::System.Threading.Tasks.ValueTask DisposeAsync() => _session.DisposeAsync();");
        }

        sb.AppendLine($"{indent}}}");

        if (sessionNs is not null)
            sb.AppendLine("}");

        return sb.ToString();
    }

    private static bool IsValidEntityType(ITypeSymbol type) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false, IsUnboundGenericType: false };

    private static string ToFieldName(string propertyName) =>
        "_" + char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);

    private static Location? GetAttributeLocation(AttributeData attribute) =>
        attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation();

    private static bool TryResolveDefaultFactory(INamedTypeSymbol docType, AttributeData attribute, out string expression)
    {
        expression = null!;
        var defaultName = GetNamedArgument(attribute, "Default");
        var typeRef = docType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        if (!string.IsNullOrEmpty(defaultName))
        {
            var method = docType.GetMembers(defaultName!)
                .OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.IsStatic && m.Parameters.Length == 0 &&
                                     SymbolEqualityComparer.Default.Equals(m.ReturnType, docType));
            if (method is null) return false;
            expression = $"static () => {typeRef}.{method.Name}()";
            return true;
        }

        var ctor = docType.InstanceConstructors.FirstOrDefault(c =>
            c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public);
        if (ctor is not null)
        {
            expression = $"static () => new {typeRef}()";
            return true;
        }

        return false;
    }

    private static bool TryGetKeyMember(INamedTypeSymbol entityType, out ISymbol keyMember, out ITypeSymbol keyType)
    {
        keyMember = null!;
        keyType = null!;
        var keyMembers = entityType.GetMembers()
            .Where(m => m is IPropertySymbol or IFieldSymbol)
            .Where(m => HasAttribute(m, KeyAttributeMetadataName))
            .ToList();

        if (keyMembers.Count != 1) return false;
        keyMember = keyMembers[0];
        keyType = keyMember switch
        {
            IPropertySymbol p => p.Type,
            IFieldSymbol f => f.Type,
            _ => throw new InvalidOperationException("unreachable"),
        };
        return true;
    }

    private static int CountKeyMembers(INamedTypeSymbol entityType) =>
        entityType.GetMembers()
            .Where(m => m is IPropertySymbol or IFieldSymbol)
            .Count(m => HasAttribute(m, KeyAttributeMetadataName));

    private static string FormatKeyAccess(ISymbol keyMember) =>
        keyMember switch
        {
            IPropertySymbol p => "." + p.Name,
            IFieldSymbol f => "." + f.Name,
            _ => throw new InvalidOperationException("unreachable"),
        };

    private static bool HasAttribute(ISymbol symbol, string metadataName) =>
        symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == metadataName);

    private static bool HasVersionTolerantMemoryPackable(INamedTypeSymbol type)
    {
        var attribute = type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == MemoryPackableAttributeMetadataName);
        if (attribute is null) return false;
        if (attribute.ConstructorArguments.Length != 1) return false;

        var arg = attribute.ConstructorArguments[0];
        if (arg.Type is not { } enumType) return false;

        var versionTolerantMember = enumType.GetMembers()
            .OfType<IFieldSymbol>()
            .FirstOrDefault(f => f.Name == "VersionTolerant" && f.HasConstantValue);

        return versionTolerantMember is not null && Equals(versionTolerantMember.ConstantValue, arg.Value);
    }

    private static bool GetAutoCommitOnDispose(ImmutableArray<AttributeData> attributes)
    {
        var attribute = attributes.FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == SessionAttributeMetadataName);
        var arg = attribute?.NamedArguments.FirstOrDefault(kv => kv.Key == "AutoCommitOnDispose");
        return arg?.Value.Value is true;
    }

    private static string? GetNamedArgument(AttributeData attribute, string name)
    {
        var arg = attribute.NamedArguments.FirstOrDefault(kv => kv.Key == name);
        return arg.Value.Value as string;
    }

    private static string ToLiteral(string value) =>
        Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Literal(value).ToString();

    private sealed class DocumentInfo
    {
        private DocumentInfo(
            string propertyName,
            INamedTypeSymbol entityType,
            string storageKey,
            bool isCollection,
            ITypeSymbol? keyType,
            ISymbol? keyMember,
            string? defaultFactoryExpression)
        {
            PropertyName = propertyName;
            EntityType = entityType;
            StorageKey = storageKey;
            IsCollection = isCollection;
            KeyType = keyType;
            KeyMember = keyMember;
            DefaultFactoryExpression = defaultFactoryExpression;
        }

        public string PropertyName { get; }
        public INamedTypeSymbol EntityType { get; }
        public string StorageKey { get; }
        public bool IsCollection { get; }
        public ITypeSymbol? KeyType { get; }
        public ISymbol? KeyMember { get; }
        public string? DefaultFactoryExpression { get; }

        public static DocumentInfo Single(
            string propertyName,
            INamedTypeSymbol entityType,
            string storageKey,
            string defaultFactoryExpression) =>
            new(propertyName, entityType, storageKey, isCollection: false, keyType: null, keyMember: null, defaultFactoryExpression);

        public static DocumentInfo Collection(
            string propertyName,
            INamedTypeSymbol entityType,
            ITypeSymbol keyType,
            ISymbol keyMember,
            string storageKey) =>
            new(propertyName, entityType, storageKey, isCollection: true, keyType, keyMember, defaultFactoryExpression: null);
    }
}
