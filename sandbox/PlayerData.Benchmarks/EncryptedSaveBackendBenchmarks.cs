using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using BenchmarkDotNet.Attributes;

namespace PlayerData.Benchmarks;

// Compares the current EncryptedSaveBackend.Protect/Unprotect (buffer-direct AES writes +
// IncrementalHash-streamed MAC, see PlayerData.Core, ST-2 of plan
// 202607141500-playerdata-core-perf-tuning) against a frozen copy of the pre-ST-2 implementation
// (separate ciphertext / ivAndCiphertext / macInput arrays, taken verbatim before that change).
// The old code is kept here only as a benchmark baseline, not as production code - it is dead
// outside this comparison. The two sides use independently generated keys: performance and
// allocation cost do not depend on the key's contents, only on its length, so there is no need
// for Old and New to agree on the same key value.
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
public class EncryptedSaveBackendBenchmarks
{
    private const int FormatVersion = 1;
    private const string DocumentKey = "profile";

    [Params(100, 1024, 10 * 1024)]
    public int PayloadSize { get; set; }

    private byte[] _plaintext = null!;
    private byte[] _oldAesKey = null!;
    private byte[] _oldHmacKey = null!;
    private byte[] _oldPayload = null!;
    private EncryptedSaveBackend _backend = null!;
    private byte[] _newPayload = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _plaintext = new byte[PayloadSize];
        new Random(1).NextBytes(_plaintext);

        _oldAesKey = new byte[32];
        _oldHmacKey = new byte[32];
        new Random(2).NextBytes(_oldAesKey);
        new Random(3).NextBytes(_oldHmacKey);
        _oldPayload = OldProtect(_oldAesKey, _oldHmacKey, FormatVersion, DocumentKey, _plaintext);

        var newKey = new byte[32];
        new Random(4).NextBytes(newKey);
        _backend = new EncryptedSaveBackend(new InMemorySaveBackend(), newKey);
        _newPayload = _backend.Protect(FormatVersion, DocumentKey, _plaintext);
    }

    [Benchmark(Baseline = true)]
    public byte[] Protect_Old_SeparateArrays() => OldProtect(_oldAesKey, _oldHmacKey, FormatVersion, DocumentKey, _plaintext);

    [Benchmark]
    public byte[] Protect_New_DirectBuffer() => _backend.Protect(FormatVersion, DocumentKey, _plaintext);

    [Benchmark]
    public byte[] Unprotect_Old_SeparateArrays() => OldUnprotect(_oldAesKey, _oldHmacKey, FormatVersion, DocumentKey, _oldPayload);

    [Benchmark]
    public byte[] Unprotect_New_DirectBuffer() => _backend.Unprotect(FormatVersion, DocumentKey, _newPayload);

    // --- Frozen baseline, copied verbatim from PlayerData.Core/EncryptedSaveBackend.cs before ST-2 ---

    private const int OldIvSize = 16;
    private const int OldTagSize = 32;
    private const byte OldFormatTag = 0x01;

    private static byte[] OldProtect(byte[] aesKey, byte[] hmacKey, int formatVersion, string documentKey, byte[] plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateIV();

        byte[] ciphertext;
        using (var encryptor = aes.CreateEncryptor())
            ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

        var ivAndCiphertext = new byte[OldIvSize + ciphertext.Length];
        aes.IV.CopyTo(ivAndCiphertext, 0);
        ciphertext.CopyTo(ivAndCiphertext, OldIvSize);

        var tag = OldComputeMac(hmacKey, formatVersion, documentKey, ivAndCiphertext);

        var payload = new byte[1 + ivAndCiphertext.Length + OldTagSize];
        payload[0] = OldFormatTag;
        ivAndCiphertext.CopyTo(payload, 1);
        tag.CopyTo(payload, 1 + ivAndCiphertext.Length);
        return payload;
    }

    private static byte[] OldUnprotect(byte[] aesKey, byte[] hmacKey, int formatVersion, string documentKey, byte[] payload)
    {
        var ivAndCiphertextLength = payload.Length - 1 - OldTagSize;
        var ivAndCiphertext = payload.AsSpan(1, ivAndCiphertextLength);
        var storedTag = payload.AsSpan(1 + ivAndCiphertextLength, OldTagSize);

        var expectedTag = OldComputeMac(hmacKey, formatVersion, documentKey, ivAndCiphertext);
        if (!CryptographicOperations.FixedTimeEquals(storedTag, expectedTag))
            throw new SaveTamperDetectedException("Benchmark baseline: unexpected HMAC mismatch.");

        var iv = ivAndCiphertext.Slice(0, OldIvSize).ToArray();
        var ciphertext = ivAndCiphertext.Slice(OldIvSize).ToArray();

        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }

    private static byte[] OldComputeMac(byte[] hmacKey, int formatVersion, string documentKey, ReadOnlySpan<byte> ivAndCiphertext)
    {
        var keyBytes = Encoding.UTF8.GetBytes(documentKey);
        var macInput = new byte[1 + 4 + 4 + keyBytes.Length + ivAndCiphertext.Length];
        var offset = 0;

        macInput[offset] = OldFormatTag;
        offset += 1;
        BinaryPrimitives.WriteInt32LittleEndian(macInput.AsSpan(offset, 4), formatVersion);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(macInput.AsSpan(offset, 4), keyBytes.Length);
        offset += 4;
        keyBytes.AsSpan().CopyTo(macInput.AsSpan(offset));
        offset += keyBytes.Length;
        ivAndCiphertext.CopyTo(macInput.AsSpan(offset));

        using var hmac = new HMACSHA256(hmacKey);
        return hmac.ComputeHash(macInput);
    }
}
