using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;

namespace PlayerData.Core.Tests;

public class SlotSaveBackendTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "PlayerData.Core.Tests_" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void Constructor_NullRoot_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SlotSaveBackend(null!, 0));
    }

    [Test]
    public void Constructor_NegativeSlot_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlotSaveBackend(_directory, -1));
    }

    [Test]
    public void Slot_ExposesConfiguredIndex()
    {
        var backend = new SlotSaveBackend(_directory, 7);
        Assert.That(backend.Slot, Is.EqualTo(7));
    }

    [Test]
    public async Task WriteAsync_UsesSlotSubdirectory()
    {
        var backend = new SlotSaveBackend(_directory, 2);
        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]>
        {
            ["doc"] = new byte[] { 1, 2, 3 },
        }));

        Assert.That(File.Exists(Path.Combine(_directory, "slot_2", "manifest.bin")), Is.True);
        Assert.That(File.Exists(Path.Combine(_directory, "slot_2", "docs", "doc.bin")), Is.True);
        Assert.That(File.Exists(Path.Combine(_directory, "manifest.bin")), Is.False);
    }

    [Test]
    public async Task ReadAsync_NoSave_ReturnsNull()
    {
        var backend = new SlotSaveBackend(_directory, 0);
        Assert.That(await backend.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task RoundTrip_PreservesPayload()
    {
        var writer = new SlotSaveBackend(_directory, 1);
        await writer.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]>
        {
            ["player"] = new byte[] { 10, 20, 30 },
        }));

        var reader = new SlotSaveBackend(_directory, 1);
        var result = await reader.ReadAsync();
        Assert.That(result!.Documents["player"], Is.EqualTo(new byte[] { 10, 20, 30 }));
    }
}
