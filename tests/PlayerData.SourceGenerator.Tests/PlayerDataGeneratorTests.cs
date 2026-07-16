using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using PlayerData.SourceGenerator;

namespace PlayerData.SourceGenerator.Tests;

public class PlayerDataGeneratorTests
{
    [Test]
    public void Session_WithSingleAndCollection_GeneratesSessionSurface()
    {
        const string source = """
            using MemoryPack;
            using PlayerData;

            namespace Sample;

            [PlayerDataSession]
            [PlayerDataSingle(typeof(PlayerProfile), "Profile", Default = "NewGame")]
            [PlayerDataCollection(typeof(InventoryItem), "Inventory")]
            public partial class GameSave
            {
            }

            [MemoryPackable(GenerateType.VersionTolerant)]
            public partial class PlayerProfile
            {
                [MemoryPackOrder(0)] public int Level { get; set; }
                public static PlayerProfile NewGame() => new() { Level = 1 };
            }

            [MemoryPackable(GenerateType.VersionTolerant)]
            public partial class InventoryItem
            {
                [PlayerDataKey]
                [MemoryPackOrder(0)] public string ItemId { get; set; } = "";
                [MemoryPackOrder(1)] public int Count { get; set; }
            }
            """;

        var (diagnostics, generatedTrees) = RunGenerator(source);

        Assert.That(diagnostics, Is.Empty);
        var generated = generatedTrees.SingleOrDefault(t => t.FilePath.EndsWith("GameSave.PlayerDataSession.g.cs"));
        Assert.That(generated, Is.Not.Null);
        var text = generated!.GetText().ToString();
        Assert.That(text, Does.Contain("namespace Sample"));
        Assert.That(text, Does.Contain("partial class GameSave : global::PlayerData.ISaveSession"));
        Assert.That(text, Does.Contain("IDoc<global::Sample.PlayerProfile> Profile"));
        Assert.That(text, Does.Contain("IBag<string, global::Sample.InventoryItem> Inventory"));
        Assert.That(text, Does.Not.Contain("partial global::PlayerData.IDoc"));
        Assert.That(text, Does.Not.Contain("partial global::PlayerData.IBag"));
        Assert.That(text, Does.Contain("AddDocument(\"Profile\""));
        Assert.That(text, Does.Contain("AddCollection<string, global::Sample.InventoryItem>(\"Inventory\""));
        Assert.That(text, Does.Contain("OpenAsync"));
        Assert.That(text, Does.Contain("PlayerProfile.NewGame()"));
        Assert.That(text, Does.Contain("SuppressNotifications()"));
        Assert.That(text, Does.Contain("AddValidator"));
    }

    [Test]
    public void PropertyName_OmittedDefaultsToTypeName()
    {
        const string source = """
            using MemoryPack;
            using PlayerData;

            namespace Sample;

            [PlayerDataSession]
            [PlayerDataSingle(typeof(PlayerProfile))]
            public partial class GameSave
            {
            }

            [MemoryPackable(GenerateType.VersionTolerant)]
            public partial class PlayerProfile
            {
                [MemoryPackOrder(0)] public int Level { get; set; }
            }
            """;

        var (diagnostics, generatedTrees) = RunGenerator(source);

        Assert.That(diagnostics, Is.Empty);
        var text = generatedTrees.Single(t => t.FilePath.EndsWith("GameSave.PlayerDataSession.g.cs")).GetText().ToString();
        Assert.That(text, Does.Contain("IDoc<global::Sample.PlayerProfile> PlayerProfile"));
        Assert.That(text, Does.Contain("AddDocument(\"PlayerProfile\""));
    }

    [Test]
    public void Single_ExplicitKey_UsesConfiguredKey()
    {
        const string source = """
            using MemoryPack;
            using PlayerData;

            namespace Sample;

            [PlayerDataSession]
            [PlayerDataSingle(typeof(PlayerProfile), "Profile", Key = "custom_key")]
            public partial class GameSave
            {
            }

            [MemoryPackable(GenerateType.VersionTolerant)]
            public partial class PlayerProfile
            {
                [MemoryPackOrder(0)] public int Level { get; set; }
            }
            """;

        var (diagnostics, generatedTrees) = RunGenerator(source);

        Assert.That(diagnostics, Is.Empty);
        var text = generatedTrees.Single(t => t.FilePath.EndsWith("GameSave.PlayerDataSession.g.cs")).GetText().ToString();
        Assert.That(text, Does.Contain("\"custom_key\""));
        Assert.That(text, Does.Not.Contain("AddDocument(\"Profile\""));
    }

    [Test]
    public void Single_MissingVersionTolerantMemoryPackable_ReportsPD0001()
    {
        const string source = """
            using PlayerData;

            namespace Sample;

            [PlayerDataSession]
            [PlayerDataSingle(typeof(PlayerProfile))]
            public partial class GameSave
            {
            }

            public partial class PlayerProfile
            {
                public int Level { get; set; }
            }
            """;

        var (diagnostics, _) = RunGenerator(source);

        Assert.That(diagnostics.Select(d => d.Id), Does.Contain("PD0001"));
    }

    [Test]
    public void Collection_NoKeyMember_ReportsPD0002()
    {
        const string source = """
            using MemoryPack;
            using PlayerData;

            namespace Sample;

            [PlayerDataSession]
            [PlayerDataCollection(typeof(InventoryItem))]
            public partial class GameSave
            {
            }

            [MemoryPackable(GenerateType.VersionTolerant)]
            public partial class InventoryItem
            {
                [MemoryPackOrder(0)] public string ItemId { get; set; } = "";
            }
            """;

        var (diagnostics, generatedTrees) = RunGenerator(source);

        Assert.That(diagnostics.Select(d => d.Id), Does.Contain("PD0002"));
        var session = generatedTrees.SingleOrDefault(t => t.FilePath.EndsWith("GameSave.PlayerDataSession.g.cs"));
        if (session is not null)
            Assert.That(session.GetText().ToString(), Does.Not.Contain("InventoryItem"));
    }

    [Test]
    public void MultipleSessions_AreAllowed()
    {
        const string source = """
            using MemoryPack;
            using PlayerData;

            namespace Sample;

            [PlayerDataSession]
            [PlayerDataSingle(typeof(PlayerProfile))]
            public partial class GameSaveA
            {
            }

            [PlayerDataSession]
            [PlayerDataSingle(typeof(PlayerProfile))]
            public partial class GameSaveB
            {
            }

            [MemoryPackable(GenerateType.VersionTolerant)]
            public partial class PlayerProfile
            {
                [MemoryPackOrder(0)] public int Level { get; set; }
            }
            """;

        var (diagnostics, generatedTrees) = RunGenerator(source);

        Assert.That(diagnostics, Is.Empty);
        Assert.That(generatedTrees.Count(t => t.FilePath.EndsWith("PlayerDataSession.g.cs")), Is.EqualTo(2));
    }

    [Test]
    public void UnmarkedType_GeneratesNothing()
    {
        const string source = """
            namespace Sample;

            public class PlainClass
            {
                public int Value { get; set; }
            }
            """;

        var (_, generatedTrees) = RunGenerator(source);

        Assert.That(generatedTrees, Is.Empty);
    }

    [Test]
    public void Session_WithNoDocuments_GeneratesEmptySession()
    {
        const string source = """
            using PlayerData;

            namespace Sample;

            [PlayerDataSession]
            public partial class GameSave { }
            """;

        var (diagnostics, generatedTrees) = RunGenerator(source);

        Assert.That(diagnostics, Is.Empty);
        var text = generatedTrees.Single(t => t.FilePath.EndsWith("GameSave.PlayerDataSession.g.cs")).GetText().ToString();
        Assert.That(text, Does.Contain("partial class GameSave : global::PlayerData.ISaveSession"));
        Assert.That(text, Does.Contain("public GameSave(global::PlayerData.ISaveBackend backend"));
        Assert.That(text, Does.Contain("OpenAsync"));
    }

    [Test]
    public void SessionClass_NotPartial_ReportsPD0012()
    {
        const string source = """
            using PlayerData;

            namespace Sample;

            [PlayerDataSession]
            public class GameSave { }
            """;

        var (diagnostics, generatedTrees) = RunGenerator(source);

        Assert.That(diagnostics.Select(d => d.Id), Does.Contain("PD0012"));
        Assert.That(generatedTrees, Is.Empty);
    }

    [Test]
    public void InvalidPropertyName_ReportsPD0008()
    {
        const string source = """
            using MemoryPack;
            using PlayerData;

            namespace Sample;

            [PlayerDataSession]
            [PlayerDataSingle(typeof(PlayerProfile), "not an identifier")]
            public partial class GameSave
            {
            }

            [MemoryPackable(GenerateType.VersionTolerant)]
            public partial class PlayerProfile
            {
                [MemoryPackOrder(0)] public int Level { get; set; }
            }
            """;

        var (diagnostics, _) = RunGenerator(source);

        Assert.That(diagnostics.Select(d => d.Id), Does.Contain("PD0008"));
    }

    [Test]
    public void DuplicatePropertyName_ReportsPD0009()
    {
        const string source = """
            using MemoryPack;
            using PlayerData;

            namespace Sample;

            [PlayerDataSession]
            [PlayerDataSingle(typeof(PlayerProfile), "Data")]
            [PlayerDataCollection(typeof(InventoryItem), "Data")]
            public partial class GameSave
            {
            }

            [MemoryPackable(GenerateType.VersionTolerant)]
            public partial class PlayerProfile
            {
                [MemoryPackOrder(0)] public int Level { get; set; }
            }

            [MemoryPackable(GenerateType.VersionTolerant)]
            public partial class InventoryItem
            {
                [PlayerDataKey]
                [MemoryPackOrder(0)] public string ItemId { get; set; } = "";
            }
            """;

        var (diagnostics, _) = RunGenerator(source);

        Assert.That(diagnostics.Count(d => d.Id == "PD0009"), Is.EqualTo(1));
    }

    [Test]
    public void ReservedPropertyName_ReportsPD0010()
    {
        const string source = """
            using MemoryPack;
            using PlayerData;

            namespace Sample;

            [PlayerDataSession]
            [PlayerDataSingle(typeof(PlayerProfile), "IsDirty")]
            public partial class GameSave
            {
            }

            [MemoryPackable(GenerateType.VersionTolerant)]
            public partial class PlayerProfile
            {
                [MemoryPackOrder(0)] public int Level { get; set; }
            }
            """;

        var (diagnostics, _) = RunGenerator(source);

        Assert.That(diagnostics.Select(d => d.Id), Does.Contain("PD0010"));
    }

    [Test]
    public void InvalidEntityType_ReportsPD0011()
    {
        const string source = """
            using PlayerData;

            namespace Sample;

            [PlayerDataSession]
            [PlayerDataSingle(typeof(int), "Level")]
            public partial class GameSave
            {
            }
            """;

        var (diagnostics, generatedTrees) = RunGenerator(source);

        Assert.That(diagnostics.Select(d => d.Id), Does.Contain("PD0011"));
        var session = generatedTrees.Single(t => t.FilePath.EndsWith("GameSave.PlayerDataSession.g.cs")).GetText().ToString();
        Assert.That(session, Does.Not.Contain("Level"));
    }

    private static (System.Collections.Immutable.ImmutableArray<Diagnostic> Diagnostics, System.Collections.Immutable.ImmutableArray<SyntaxTree> GeneratedTrees) RunGenerator(string additionalSource)
    {
        var references = Basic.Reference.Assemblies.Net90.References.All
            .Append(MetadataReference.CreateFromFile(typeof(PlayerData.PlayerDataSingleAttribute).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(MemoryPack.MemoryPackableAttribute).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            assemblyName: "PlayerData.SourceGenerator.Tests.Compilation",
            syntaxTrees: [CSharpSyntaxTree.ParseText(additionalSource)],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new PlayerDataGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

        var runResult = driver.GetRunResult();
        return (runResult.Diagnostics, runResult.GeneratedTrees);
    }
}
