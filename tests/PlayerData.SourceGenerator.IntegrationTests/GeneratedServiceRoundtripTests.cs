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

    [Test]
    public async Task GeneratedSession_OpenAsync_NoSave_UsesDefaultsAndIsLoaded()
    {
        var save = await GameSave.OpenAsync(new DirectorySaveBackend(_directory));

        Assert.That(save.IsLoaded, Is.True);
        Assert.That(save.IsDirty, Is.False);
        Assert.That(save.Profile.Value.Level, Is.EqualTo(1));
        Assert.That(save.Profile.Value.Name, Is.EqualTo("New"));
        Assert.That(save.Inventory.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task GeneratedSession_SuppressNotifications_CoalescesProfileChanges()
    {
        var save = new GameSave(new DirectorySaveBackend(_directory));
        var changes = 0;
        save.Profile.Changed += _ => changes++;

        using (save.SuppressNotifications())
        {
            save.Profile.Update(_ => new PlayerProfile { Level = 2, Name = "A" });
            save.Profile.Update(_ => new PlayerProfile { Level = 3, Name = "B" });
            Assert.That(changes, Is.EqualTo(0));
        }

        Assert.That(changes, Is.EqualTo(1));
        Assert.That(save.Profile.Value.Level, Is.EqualTo(3));
    }

    [Test]
    public async Task GeneratedSession_ValidatorFailure_PreventsCommit()
    {
        var save = new GameSave(new DirectorySaveBackend(_directory));
        save.AddValidator(_ => throw new SaveValidationException("blocked"));
        save.Profile.Update(_ => new PlayerProfile { Level = 9, Name = "X" });

        Assert.ThrowsAsync<SaveValidationException>(async () => await save.CommitAsync());
        Assert.That(System.IO.File.Exists(System.IO.Path.Combine(_directory, "manifest.bin")), Is.False);
        Assert.That(save.IsDirty, Is.True);
    }

    [Test]
    public async Task GeneratedSession_CollectionOnlyMutation_RoundTrips()
    {
        var writer = new GameSave(new DirectorySaveBackend(_directory));
        writer.Inventory.Upsert(new InventoryItem { ItemId = "potion", Count = 5 });
        await writer.CommitAsync();

        var reader = await GameSave.OpenAsync(new DirectorySaveBackend(_directory));
        Assert.That(reader.Inventory.Get("potion").Count, Is.EqualTo(5));
        Assert.That(reader.Profile.Value.Level, Is.EqualTo(1)); // default factory value
    }

    [Test]
    public async Task GeneratedSession_DirtyChanged_FiresOnMutationAndCommit()
    {
        var save = new GameSave(new DirectorySaveBackend(_directory));
        var flags = new System.Collections.Generic.List<bool>();
        save.DirtyChanged += flags.Add;

        save.Profile.Update(_ => new PlayerProfile { Level = 2, Name = "Z" });
        Assert.That(flags, Is.EqualTo(new[] { true }));

        await save.CommitAsync();
        Assert.That(flags, Is.EqualTo(new[] { true, false }));
    }
}
