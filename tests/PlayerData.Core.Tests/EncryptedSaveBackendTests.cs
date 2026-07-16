using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace PlayerData.Core.Tests;

public class EncryptedSaveBackendTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] OtherKey = Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray();

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
    [TestCase(15)]
    [TestCase(16)]
    [TestCase(17)]
    [TestCase(32)]
    [TestCase(100)]
    public async Task WriteAsync_ThenReadAsync_RoundTripsExactBytes(int length)
    {
        var plaintext = MakePlaintext(length);
        var backend = new EncryptedSaveBackend(new DirectorySaveBackend(_directory), Key);

        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]> { ["doc"] = plaintext }));
        var result = await backend.ReadAsync();

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Documents["doc"], Is.EqualTo(plaintext));
    }

    [Test]
    public async Task WriteAsync_StoresCiphertextDifferentFromPlaintext()
    {
        var plaintext = MakePlaintext(64);
        var backend = new EncryptedSaveBackend(new DirectorySaveBackend(_directory), Key);
        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]> { ["doc"] = plaintext }));

        var raw = await File.ReadAllBytesAsync(Path.Combine(_directory, "docs", "doc.bin"));

        Assert.That(raw, Is.Not.EqualTo(plaintext));
        Assert.That(raw.Length, Is.GreaterThanOrEqualTo(1 + 16 + 32));
    }

    [Test]
    public async Task ReadAsync_TamperedPayload_ThrowsSaveTamperDetectedException()
    {
        var plaintext = MakePlaintext(64);
        var backend = new EncryptedSaveBackend(new DirectorySaveBackend(_directory), Key);
        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]> { ["doc"] = plaintext }));

        var path = Path.Combine(_directory, "docs", "doc.bin");
        var raw = await File.ReadAllBytesAsync(path);
        raw[raw.Length - 1] ^= 0xFF; // flip a byte inside the HMAC tag
        await File.WriteAllBytesAsync(path, raw);

        var reader = new EncryptedSaveBackend(new DirectorySaveBackend(_directory), Key);
        Assert.ThrowsAsync<SaveTamperDetectedException>(async () => await reader.ReadAsync());
    }

    [Test]
    public async Task ReadAsync_WrongKey_ThrowsSaveTamperDetectedException()
    {
        var plaintext = MakePlaintext(64);
        var writer = new EncryptedSaveBackend(new DirectorySaveBackend(_directory), Key);
        await writer.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]> { ["doc"] = plaintext }));

        var reader = new EncryptedSaveBackend(new DirectorySaveBackend(_directory), OtherKey);
        Assert.ThrowsAsync<SaveTamperDetectedException>(async () => await reader.ReadAsync());
    }

    [Test]
    public async Task ReadAsync_TooShortPayload_ThrowsSaveTamperDetectedException()
    {
        var plaintext = MakePlaintext(10);
        var backend = new EncryptedSaveBackend(new DirectorySaveBackend(_directory), Key);
        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]> { ["doc"] = plaintext }));

        var path = Path.Combine(_directory, "docs", "doc.bin");
        await File.WriteAllBytesAsync(path, new byte[10]); // shorter than the minimum 49-byte payload

        var reader = new EncryptedSaveBackend(new DirectorySaveBackend(_directory), Key);
        Assert.ThrowsAsync<SaveTamperDetectedException>(async () => await reader.ReadAsync());
    }

    [Test]
    public async Task ReadAsync_SwappedDocumentCiphertexts_ThrowsSaveTamperDetectedException()
    {
        // Guards against the document-substitution attack flagged in the plan's Decided D2:
        // swapping which key an encrypted payload is stored under must be detected, since the
        // document key is bound into the HMAC as associated data.
        var plaintextA = MakePlaintext(20);
        var plaintextB = MakePlaintext(21);
        var backend = new EncryptedSaveBackend(new DirectorySaveBackend(_directory), Key);
        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]>
        {
            ["gold"] = plaintextA,
            ["score"] = plaintextB,
        }));

        var goldPath = Path.Combine(_directory, "docs", "gold.bin");
        var scorePath = Path.Combine(_directory, "docs", "score.bin");
        var goldBytes = await File.ReadAllBytesAsync(goldPath);
        var scoreBytes = await File.ReadAllBytesAsync(scorePath);
        await File.WriteAllBytesAsync(goldPath, scoreBytes);
        await File.WriteAllBytesAsync(scorePath, goldBytes);

        var reader = new EncryptedSaveBackend(new DirectorySaveBackend(_directory), Key);
        Assert.ThrowsAsync<SaveTamperDetectedException>(async () => await reader.ReadAsync());
    }

    [Test]
    public async Task PassphraseConstructor_RoundTripsExactBytes()
    {
        var plaintext = MakePlaintext(50);
        var writer = new EncryptedSaveBackend(new DirectorySaveBackend(_directory), "correct horse battery staple");
        await writer.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]> { ["doc"] = plaintext }));

        var reader = new EncryptedSaveBackend(new DirectorySaveBackend(_directory), "correct horse battery staple");
        var result = await reader.ReadAsync();

        Assert.That(result!.Documents["doc"], Is.EqualTo(plaintext));
    }

    [Test]
    public void Constructor_NullInner_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new EncryptedSaveBackend(null!, Key));
    }

    [Test]
    public void Constructor_EmptyKey_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new EncryptedSaveBackend(new DirectorySaveBackend(_directory), Array.Empty<byte>()));
    }

    [Test]
    public void Constructor_EmptyPassphrase_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new EncryptedSaveBackend(new DirectorySaveBackend(_directory), string.Empty));
    }

    [Test]
    public void Constructor_NullKey_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new EncryptedSaveBackend(new DirectorySaveBackend(_directory), (byte[])null!));
    }

    [Test]
    public void Constructor_NullPassphrase_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new EncryptedSaveBackend(new DirectorySaveBackend(_directory), (string)null!));
    }

    [Test]
    public async Task ReadAsync_NoSave_ReturnsNull()
    {
        using var backend = new EncryptedSaveBackend(new DirectorySaveBackend(_directory), Key);
        Assert.That(await backend.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task WriteAsync_NullBundle_Throws()
    {
        using var backend = new EncryptedSaveBackend(new DirectorySaveBackend(_directory), Key);
        Assert.ThrowsAsync<ArgumentNullException>(async () => await backend.WriteAsync(null!));
    }

    [Test]
    public async Task ReadAsync_FormatVersionTamper_ThrowsSaveTamperDetectedException()
    {
        // HMAC binds FormatVersion as associated data. Changing only the manifest's version
        // must fail authentication even when ciphertext bytes are untouched.
        using (var writer = new EncryptedSaveBackend(new DirectorySaveBackend(_directory), Key))
        {
            await writer.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]>
            {
                ["doc"] = MakePlaintext(32),
            }));
        }

        // Rewrite manifest with a different format version while keeping document ciphertext.
        var docs = await File.ReadAllBytesAsync(Path.Combine(_directory, "docs", "doc.bin"));
        var inner = new DirectorySaveBackend(_directory);
        await inner.WriteAsync(new SaveBundle(2, new Dictionary<string, byte[]> { ["doc"] = docs }));

        using var reader = new EncryptedSaveBackend(new DirectorySaveBackend(_directory), Key);
        Assert.ThrowsAsync<SaveTamperDetectedException>(async () => await reader.ReadAsync());
    }

    [Test]
    public async Task MultiDocument_RoundTripsIndependently()
    {
        using var backend = new EncryptedSaveBackend(new DirectorySaveBackend(_directory), Key);
        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]>
        {
            ["a"] = MakePlaintext(10),
            ["b"] = MakePlaintext(20),
            ["c"] = MakePlaintext(0),
        }));

        var result = await backend.ReadAsync();
        Assert.That(result!.Documents["a"], Is.EqualTo(MakePlaintext(10)));
        Assert.That(result.Documents["b"], Is.EqualTo(MakePlaintext(20)));
        Assert.That(result.Documents["c"], Is.EqualTo(MakePlaintext(0)));
    }

    [Test]
    public async Task PassphraseConstructor_WrongPassphrase_ThrowsSaveTamperDetectedException()
    {
        using (var writer = new EncryptedSaveBackend(new DirectorySaveBackend(_directory), "correct horse battery staple"))
        {
            await writer.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]>
            {
                ["doc"] = MakePlaintext(16),
            }));
        }

        using var reader = new EncryptedSaveBackend(new DirectorySaveBackend(_directory), "wrong passphrase");
        Assert.ThrowsAsync<SaveTamperDetectedException>(async () => await reader.ReadAsync());
    }
}
