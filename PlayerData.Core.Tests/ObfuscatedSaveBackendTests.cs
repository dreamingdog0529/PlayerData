using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;

namespace PlayerData.Core.Tests;

public class ObfuscatedSaveBackendTests
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

    private static byte[] MakePlaintext(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++) data[i] = (byte)(i * 37 + 11);
        return data;
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(31)]
    [TestCase(32)] // mask period boundary
    [TestCase(33)]
    [TestCase(100)]
    public async Task WriteAsync_ThenReadAsync_RoundTripsExactBytes(int length)
    {
        var plaintext = MakePlaintext(length);
        var backend = new ObfuscatedSaveBackend(new DirectorySaveBackend(_directory));

        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]> { ["doc"] = plaintext }));
        var result = await backend.ReadAsync();

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Documents["doc"], Is.EqualTo(plaintext));
    }

    [Test]
    public async Task WriteAsync_StoresBytesDifferentFromPlaintext()
    {
        var plaintext = MakePlaintext(64);
        var backend = new ObfuscatedSaveBackend(new DirectorySaveBackend(_directory));
        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]> { ["doc"] = plaintext }));

        var raw = await File.ReadAllBytesAsync(Path.Combine(_directory, "docs", "doc.bin"));

        Assert.That(raw, Is.Not.EqualTo(plaintext));
        Assert.That(raw.Length, Is.EqualTo(plaintext.Length)); // obfuscation does not change length
    }

    [Test]
    public void Constructor_HasNoKeyOrPassphraseParameter()
    {
        var ctors = typeof(ObfuscatedSaveBackend).GetConstructors();
        Assert.That(ctors, Has.Length.EqualTo(1));
        var parameters = ctors[0].GetParameters();
        Assert.That(parameters, Has.Length.EqualTo(1));
        Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(ISaveBackend)));
    }

    [Test]
    public void Constructor_NullInner_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ObfuscatedSaveBackend(null!));
    }
}
