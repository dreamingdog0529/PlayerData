using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using PlayerData;
using Sample;

namespace PlayerData.SourceGenerator.IntegrationTests;

// Exercises the generated GameSave session (compiled from PlayerData.Core's embedded analyzer
// via PackageReference) - not an in-memory Roslyn-driver text assertion.
public class GeneratedServiceRoundtripTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "PlayerData.SG.IntegrationTests_" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public async Task GeneratedSession_UpdateCommitLoad_RoundtripsThroughSeparateInstance()
    {
        var writer = new GameSave(new DirectorySaveBackend(_directory));
        writer.Profile.Update(_ => new PlayerProfile { Level = 42, Name = "Saved" });
        writer.Inventory.Upsert(new InventoryItem { ItemId = "sword", Count = 1 });
        writer.Inventory.Upsert(new InventoryItem { ItemId = "shield", Count = 2 });
        await writer.CommitAsync();

        var reader = await GameSave.OpenAsync(new DirectorySaveBackend(_directory));

        Assert.That(reader.IsLoaded, Is.True);
        Assert.That(reader.Profile.Value.Level, Is.EqualTo(42));
        Assert.That(reader.Profile.Value.Name, Is.EqualTo("Saved"));
        Assert.That(reader.Inventory.Snapshot.Count, Is.EqualTo(2));
        Assert.That(reader.Inventory.TryGet("sword", out var sword), Is.True);
        Assert.That(sword!.Count, Is.EqualTo(1));
    }

    [Test]
    public void GeneratedSession_ImplementsISaveSession()
    {
        ISaveSession session = new GameSave(new DirectorySaveBackend(_directory));
        Assert.That(session.IsDirty, Is.False);
    }

    [Test]
    public void GeneratedSession_DocumentUpdate_MarksDirty()
    {
        var save = new GameSave(new DirectorySaveBackend(_directory));
        save.Profile.Update(p => new PlayerProfile { Level = 5, Name = p.Name });
        Assert.That(save.IsDirty, Is.True);
        Assert.That(save.Profile.Value.Level, Is.EqualTo(5));
    }
}
