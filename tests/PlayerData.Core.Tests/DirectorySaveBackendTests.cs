using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;

namespace PlayerData.Core.Tests;

public class DirectorySaveBackendTests
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
    public async Task ReadAsync_NoSave_ReturnsNull()
    {
        var backend = new DirectorySaveBackend(_directory);
        var result = await backend.ReadAsync();
        Assert.That(result, Is.Null);
    }

    [Test]
    public void Constructor_NullRoot_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DirectorySaveBackend(null!));
    }

    [Test]
    public async Task WriteAsync_NullBundle_Throws()
    {
        var backend = new DirectorySaveBackend(_directory);
        Assert.ThrowsAsync<ArgumentNullException>(async () => await backend.WriteAsync(null!));
    }

    [Test]
    public async Task WriteAsync_ThenReadAsync_RoundTripsMultipleDocuments()
    {
        var backend = new DirectorySaveBackend(_directory);
        var documents = new Dictionary<string, byte[]>
        {
            ["a"] = new byte[] { 1, 2 },
            ["b"] = new byte[] { 3, 4, 5 },
        };
        await backend.WriteAsync(new SaveBundle(2, documents));

        var result = await backend.ReadAsync();

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.FormatVersion, Is.EqualTo(2));
        Assert.That(result.Documents["a"], Is.EqualTo(new byte[] { 1, 2 }));
        Assert.That(result.Documents["b"], Is.EqualTo(new byte[] { 3, 4, 5 }));
    }

    [Test]
    public async Task WriteAsync_RemovesOrphanedDocumentFiles()
    {
        var backend = new DirectorySaveBackend(_directory);
        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]>
        {
            ["keep"] = new byte[] { 1 },
            ["drop"] = new byte[] { 2 },
        }));

        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]>
        {
            ["keep"] = new byte[] { 9 },
        }));

        Assert.That(File.Exists(Path.Combine(_directory, "docs", "keep.bin")), Is.True);
        Assert.That(File.Exists(Path.Combine(_directory, "docs", "drop.bin")), Is.False);

        var result = await backend.ReadAsync();
        Assert.That(result!.Documents.Keys, Is.EquivalentTo(new[] { "keep" }));
        Assert.That(result.Documents["keep"], Is.EqualTo(new byte[] { 9 }));
    }

    [Test]
    public async Task WriteAsync_EscapesPathSeparatorsInKeys()
    {
        // Keys containing path separators must not create nested directories under docs/.
        var backend = new DirectorySaveBackend(_directory);
        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]>
        {
            ["player/profile"] = new byte[] { 1, 2, 3 },
            ["nested\\key"] = new byte[] { 4 },
        }));

        var docsDir = Path.Combine(_directory, "docs");
        Assert.That(Directory.GetDirectories(docsDir), Is.Empty);
        var files = Directory.GetFiles(docsDir, "*.bin");
        Assert.That(files, Has.Length.EqualTo(2));
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            Assert.That(name, Does.Not.Contain("/"));
            Assert.That(name, Does.Not.Contain("\\"));
        }

        var result = await backend.ReadAsync();
        Assert.That(result!.Documents["player/profile"], Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(result.Documents["nested\\key"], Is.EqualTo(new byte[] { 4 }));
    }

    [Test]
    public async Task ReadAsync_ManifestListsMissingDocumentFile_Throws()
    {
        var backend = new DirectorySaveBackend(_directory);
        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]>
        {
            ["player"] = new byte[] { 1 },
            ["items"] = new byte[] { 2 },
        }));

        File.Delete(Path.Combine(_directory, "docs", "items.bin"));

        Assert.ThrowsAsync<InvalidDataException>(async () => await backend.ReadAsync());
    }

    [Test]
    public async Task ReadAsync_CorruptManifest_Throws()
    {
        Directory.CreateDirectory(_directory);
        // Truncated after format version int32 - ParseManifest should fail cleanly.
        await File.WriteAllBytesAsync(Path.Combine(_directory, "manifest.bin"), new byte[] { 1, 0, 0, 0 });

        var backend = new DirectorySaveBackend(_directory);
        Assert.ThrowsAsync<EndOfStreamException>(async () => await backend.ReadAsync());
    }

    [Test]
    public async Task WriteAsync_OverwriteExisting_ReplacesContents()
    {
        var backend = new DirectorySaveBackend(_directory);
        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]>
        {
            ["doc"] = new byte[] { 1, 1, 1 },
        }));
        await backend.WriteAsync(new SaveBundle(3, new Dictionary<string, byte[]>
        {
            ["doc"] = new byte[] { 9, 9 },
        }));

        var result = await backend.ReadAsync();
        Assert.That(result!.FormatVersion, Is.EqualTo(3));
        Assert.That(result.Documents["doc"], Is.EqualTo(new byte[] { 9, 9 }));
    }

    [Test]
    public async Task WriteAsync_EmptyDocumentPayload_RoundTrips()
    {
        var backend = new DirectorySaveBackend(_directory);
        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]>
        {
            ["empty"] = Array.Empty<byte>(),
        }));

        var result = await backend.ReadAsync();
        Assert.That(result!.Documents["empty"], Is.EqualTo(Array.Empty<byte>()));
    }
}
