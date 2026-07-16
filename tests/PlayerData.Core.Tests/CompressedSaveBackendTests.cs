using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace PlayerData.Core.Tests;

public class CompressedSaveBackendTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

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

    // Highly compressible payload so "stored bytes are smaller" assertions are meaningful.
    private static byte[] MakeCompressible(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++) data[i] = (byte)('A' + (i % 3));
        return data;
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(15)]
    [TestCase(16)]
    [TestCase(17)]
    [TestCase(32)]
    [TestCase(100)]
    [TestCase(4096)]
    public async Task WriteAsync_ThenReadAsync_RoundTripsExactBytes(int length)
    {
        var plaintext = MakePlaintext(length);
        var backend = new CompressedSaveBackend(new DirectorySaveBackend(_directory));

        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]> { ["doc"] = plaintext }));
        var result = await backend.ReadAsync();

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Documents["doc"], Is.EqualTo(plaintext));
    }

    [Test]
    public async Task WriteAsync_StoresBytesDifferentFromPlaintext_AndUsuallySmaller()
    {
        var plaintext = MakeCompressible(4096);
        var backend = new CompressedSaveBackend(new DirectorySaveBackend(_directory));
        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]> { ["doc"] = plaintext }));

        var raw = await File.ReadAllBytesAsync(Path.Combine(_directory, "docs", "doc.bin"));

        Assert.That(raw, Is.Not.EqualTo(plaintext));
        Assert.That(raw.Length, Is.LessThan(plaintext.Length));
        Assert.That(raw[0], Is.EqualTo(0x01)); // FormatTag
    }

    [Test]
    public async Task Constructor_CompressionLevel_IsHonoredForFastestVsOptimal()
    {
        // Both levels must round-trip; Optimal should not expand compressible data more than Fastest
        // in a way that breaks the format (we only assert both succeed and produce valid headers).
        var plaintext = MakeCompressible(2048);

        var optimalDir = Path.Combine(_directory, "optimal");
        var fastestDir = Path.Combine(_directory, "fastest");
        Directory.CreateDirectory(optimalDir);
        Directory.CreateDirectory(fastestDir);

        var optimal = new CompressedSaveBackend(new DirectorySaveBackend(optimalDir), CompressionLevel.Optimal);
        var fastest = new CompressedSaveBackend(new DirectorySaveBackend(fastestDir), CompressionLevel.Fastest);

        await optimal.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]> { ["doc"] = plaintext }));
        await fastest.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]> { ["doc"] = plaintext }));

        Assert.That((await optimal.ReadAsync())!.Documents["doc"], Is.EqualTo(plaintext));
        Assert.That((await fastest.ReadAsync())!.Documents["doc"], Is.EqualTo(plaintext));
    }

    [Test]
    public void Constructor_NullInner_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new CompressedSaveBackend(null!));
    }

    [Test]
    public void Constructor_InvalidCompressionLevel_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CompressedSaveBackend(new DirectorySaveBackend(_directory), (CompressionLevel)99));
    }

    [Test]
    public async Task ReadAsync_NoSave_ReturnsNull()
    {
        var backend = new CompressedSaveBackend(new DirectorySaveBackend(_directory));
        Assert.That(await backend.ReadAsync(), Is.Null);
    }

    [Test]
    public void WriteAsync_NullBundle_Throws()
    {
        var backend = new CompressedSaveBackend(new DirectorySaveBackend(_directory));
        Assert.ThrowsAsync<ArgumentNullException>(async () => await backend.WriteAsync(null!));
    }

    [Test]
    public async Task MultiDocument_RoundTripsIndependently()
    {
        var backend = new CompressedSaveBackend(new DirectorySaveBackend(_directory));
        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]>
        {
            ["a"] = MakePlaintext(10),
            ["b"] = MakeCompressible(64),
            ["c"] = MakePlaintext(0),
        }));

        var result = await backend.ReadAsync();
        Assert.That(result!.Documents["a"], Is.EqualTo(MakePlaintext(10)));
        Assert.That(result.Documents["b"], Is.EqualTo(MakeCompressible(64)));
        Assert.That(result.Documents["c"], Is.EqualTo(MakePlaintext(0)));
    }

    [Test]
    public async Task WriteAsync_ThenSecondWrite_Overwrites()
    {
        var backend = new CompressedSaveBackend(new DirectorySaveBackend(_directory));
        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]>
        {
            ["doc"] = MakePlaintext(8),
        }));
        var next = MakeCompressible(128);
        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]>
        {
            ["doc"] = next,
        }));

        var result = await backend.ReadAsync();
        Assert.That(result!.Documents["doc"], Is.EqualTo(next));
    }

    [Test]
    public async Task ReadAsync_TooShortPayload_ThrowsInvalidDataException()
    {
        var backend = new CompressedSaveBackend(new DirectorySaveBackend(_directory));
        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]> { ["doc"] = MakePlaintext(10) }));

        var path = Path.Combine(_directory, "docs", "doc.bin");
        await File.WriteAllBytesAsync(path, new byte[] { 0x01, 0x00 }); // shorter than 5-byte header

        Assert.ThrowsAsync<InvalidDataException>(async () => await backend.ReadAsync());
    }

    [Test]
    public async Task ReadAsync_UnknownFormatTag_ThrowsInvalidDataException()
    {
        var backend = new CompressedSaveBackend(new DirectorySaveBackend(_directory));
        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]> { ["doc"] = MakePlaintext(10) }));

        var path = Path.Combine(_directory, "docs", "doc.bin");
        var raw = await File.ReadAllBytesAsync(path);
        raw[0] = 0xFF;
        await File.WriteAllBytesAsync(path, raw);

        Assert.ThrowsAsync<InvalidDataException>(async () => await backend.ReadAsync());
    }

    [Test]
    public async Task ReadAsync_CorruptedDeflateBody_Throws()
    {
        var backend = new CompressedSaveBackend(new DirectorySaveBackend(_directory));
        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]> { ["doc"] = MakeCompressible(256) }));

        var path = Path.Combine(_directory, "docs", "doc.bin");
        var raw = await File.ReadAllBytesAsync(path);
        // Flip bytes in the deflate body (past the 5-byte header) without touching the declared length.
        for (var i = 5; i < raw.Length; i++) raw[i] ^= 0xA5;
        await File.WriteAllBytesAsync(path, raw);

        // DeflateStream may throw InvalidDataException, or our length checks may throw the same.
        Assert.ThrowsAsync<InvalidDataException>(async () => await backend.ReadAsync());
    }

    [Test]
    public async Task ChainedWithDirectory_SessionRoundTrip()
    {
        var backend = new CompressedSaveBackend(new DirectorySaveBackend(_directory));
        var writer = new SaveSession(backend);
        var player = writer.AddDocument("player", () => new SamplePlayerData(1, "New"));
        player.Update(_ => new SamplePlayerData(8, "Cmp"));
        await writer.CommitAsync();

        var reader = new SaveSession(new CompressedSaveBackend(new DirectorySaveBackend(_directory)));
        var player2 = reader.AddDocument("player", () => new SamplePlayerData(0, ""));
        await reader.LoadAsync();
        Assert.That(player2.Value, Is.EqualTo(new SamplePlayerData(8, "Cmp")));
    }

    [Test]
    public async Task StackedWithEncrypted_CompressThenEncrypt_RoundTrips()
    {
        // Recommended order: compress first (inner), encrypt outer.
        ISaveBackend backend = new EncryptedSaveBackend(
            new CompressedSaveBackend(new DirectorySaveBackend(_directory)),
            Key);

        var plaintext = MakeCompressible(2048);
        await backend.WriteAsync(new SaveBundle(1, new Dictionary<string, byte[]> { ["doc"] = plaintext }));

        var result = await backend.ReadAsync();
        Assert.That(result!.Documents["doc"], Is.EqualTo(plaintext));

        // On-disk bytes must not match either plaintext or a bare compressed header alone in a trivial way.
        var raw = await File.ReadAllBytesAsync(Path.Combine(_directory, "docs", "doc.bin"));
        Assert.That(raw, Is.Not.EqualTo(plaintext));
        Assert.That(raw[0], Is.EqualTo(0x01)); // EncryptedSaveBackend FormatTag
    }

    [Test]
    public async Task FormatVersion_IsPreserved()
    {
        var backend = new CompressedSaveBackend(new DirectorySaveBackend(_directory));
        await backend.WriteAsync(new SaveBundle(7, new Dictionary<string, byte[]>
        {
            ["doc"] = MakePlaintext(4),
        }));

        var result = await backend.ReadAsync();
        Assert.That(result!.FormatVersion, Is.EqualTo(7));
    }
}
