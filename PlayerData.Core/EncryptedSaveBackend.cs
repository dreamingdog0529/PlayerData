using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayerData;

// Wraps an ISaveBackend to encrypt each document's bytes with AES-256-CBC and authenticate them
// with HMAC-SHA256 (Encrypt-then-MAC). AesGcm was rejected for this library: it carries
// [UnsupportedOSPlatform("browser")] on every .NET moniker and has a history of
// PlatformNotSupportedException reports under Unity IL2CPP, while Aes/HMACSHA256 carry no such
// restriction on netstandard2.1.
//
// Payload layout per document: [FormatTag(1)] [IV(16)] [ciphertext(N, PKCS7)] [HMACTag(32)].
// The HMAC additionally authenticates the document key and the bundle's FormatVersion as
// associated data (not stored in the payload itself), so ciphertext cannot be swapped between
// documents or replayed from a differently-versioned bundle without detection.
public sealed class EncryptedSaveBackend : ISaveBackend
{
    private const byte FormatTag = 0x01;
    private const int IvSize = 16;
    private const int TagSize = 32; // HMACSHA256 output size
    private const int MinPayloadSize = 1 + IvSize + TagSize;
    private const int Pbkdf2Iterations = 210_000; // OWASP 2023 recommendation for PBKDF2-HMAC-SHA256

    private static readonly byte[] Pbkdf2Salt = Encoding.UTF8.GetBytes("PlayerData.Core.EncryptedSaveBackend.v1");
    private static readonly byte[] AesInfo = Encoding.UTF8.GetBytes("PlayerData.Core.v1.AES");
    private static readonly byte[] HmacInfo = Encoding.UTF8.GetBytes("PlayerData.Core.v1.HMAC");

    private readonly ISaveBackend _inner;
    private readonly byte[] _aesKey;
    private readonly byte[] _hmacKey;

    // key is used as HKDF input key material (IKM), not directly as the AES/HMAC key: reusing
    // one raw key across two primitives is a key-reuse anti-pattern, so AES and HMAC subkeys are
    // derived separately (see DeriveKeys).
    public EncryptedSaveBackend(ISaveBackend inner, byte[] key)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (key is null) throw new ArgumentNullException(nameof(key));
        if (key.Length == 0) throw new ArgumentException("Key must not be empty.", nameof(key));
        (_aesKey, _hmacKey) = DeriveKeys(key);
    }

    public EncryptedSaveBackend(ISaveBackend inner, string passphrase)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (passphrase is null) throw new ArgumentNullException(nameof(passphrase));
        if (passphrase.Length == 0) throw new ArgumentException("Passphrase must not be empty.", nameof(passphrase));

        using var pbkdf2 = new Rfc2898DeriveBytes(passphrase, Pbkdf2Salt, Pbkdf2Iterations, HashAlgorithmName.SHA256);
        var ikm = pbkdf2.GetBytes(32);
        (_aesKey, _hmacKey) = DeriveKeys(ikm);
    }

    public async ValueTask<SaveBundle?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var bundle = await _inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (bundle is null) return null;

        var documents = new Dictionary<string, byte[]>(bundle.Documents.Count);
        foreach (var pair in bundle.Documents)
            documents[pair.Key] = Unprotect(bundle.FormatVersion, pair.Key, pair.Value);

        return new SaveBundle(bundle.FormatVersion, documents);
    }

    public ValueTask WriteAsync(SaveBundle bundle, CancellationToken cancellationToken = default)
    {
        if (bundle is null) throw new ArgumentNullException(nameof(bundle));

        var documents = new Dictionary<string, byte[]>(bundle.Documents.Count);
        foreach (var pair in bundle.Documents)
            documents[pair.Key] = Protect(bundle.FormatVersion, pair.Key, pair.Value);

        return _inner.WriteAsync(new SaveBundle(bundle.FormatVersion, documents), cancellationToken);
    }

    private byte[] Protect(int formatVersion, string documentKey, byte[] plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = _aesKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateIV();

        byte[] ciphertext;
        using (var encryptor = aes.CreateEncryptor())
            ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

        var ivAndCiphertext = new byte[IvSize + ciphertext.Length];
        aes.IV.CopyTo(ivAndCiphertext, 0);
        ciphertext.CopyTo(ivAndCiphertext, IvSize);

        var tag = ComputeMac(formatVersion, documentKey, ivAndCiphertext);

        var payload = new byte[1 + ivAndCiphertext.Length + TagSize];
        payload[0] = FormatTag;
        ivAndCiphertext.CopyTo(payload, 1);
        tag.CopyTo(payload, 1 + ivAndCiphertext.Length);
        return payload;
    }

    private byte[] Unprotect(int formatVersion, string documentKey, byte[] payload)
    {
        if (payload.Length < MinPayloadSize)
            throw new SaveTamperDetectedException(
                $"Encrypted document '{documentKey}' payload is too short ({payload.Length} bytes, minimum {MinPayloadSize}).");
        if (payload[0] != FormatTag)
            throw new SaveTamperDetectedException(
                $"Encrypted document '{documentKey}' has an unrecognized format tag (0x{payload[0]:X2}).");

        var ivAndCiphertextLength = payload.Length - 1 - TagSize;
        var ivAndCiphertext = payload.AsSpan(1, ivAndCiphertextLength);
        var storedTag = payload.AsSpan(1 + ivAndCiphertextLength, TagSize);

        var expectedTag = ComputeMac(formatVersion, documentKey, ivAndCiphertext);
        if (!CryptographicOperations.FixedTimeEquals(storedTag, expectedTag))
            throw new SaveTamperDetectedException(
                $"Encrypted document '{documentKey}' failed integrity verification (HMAC mismatch).");

        var iv = ivAndCiphertext.Slice(0, IvSize).ToArray();
        var ciphertext = ivAndCiphertext.Slice(IvSize).ToArray();

        using var aes = Aes.Create();
        aes.Key = _aesKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.IV = iv;

        try
        {
            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        }
        catch (CryptographicException ex)
        {
            // A padding failure here would mean the HMAC verified above but the plaintext is
            // still malformed, which should not happen for anything but a broken key. Fold it
            // into the same exception rather than exposing a second, more specific error path.
            throw new SaveTamperDetectedException(
                $"Encrypted document '{documentKey}' failed to decrypt after passing integrity verification.", ex);
        }
    }

    // HMAC input: FormatTag(1) || LE-Int32(formatVersion) || LE-Int32(keyByteLength) ||
    // UTF8(documentKey) || ivAndCiphertext. documentKey and formatVersion are associated data:
    // they are authenticated but never stored in the payload, binding each tag to the specific
    // document slot and bundle version it was written for.
    private byte[] ComputeMac(int formatVersion, string documentKey, ReadOnlySpan<byte> ivAndCiphertext)
    {
        var keyBytes = Encoding.UTF8.GetBytes(documentKey);
        var macInput = new byte[1 + 4 + 4 + keyBytes.Length + ivAndCiphertext.Length];
        var offset = 0;

        macInput[offset] = FormatTag;
        offset += 1;
        BinaryPrimitives.WriteInt32LittleEndian(macInput.AsSpan(offset, 4), formatVersion);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(macInput.AsSpan(offset, 4), keyBytes.Length);
        offset += 4;
        keyBytes.AsSpan().CopyTo(macInput.AsSpan(offset));
        offset += keyBytes.Length;
        ivAndCiphertext.CopyTo(macInput.AsSpan(offset));

        using var hmac = new HMACSHA256(_hmacKey);
        return hmac.ComputeHash(macInput);
    }

    // RFC 5869 HKDF (Extract-then-Expand), built from HMACSHA256 alone: netstandard2.1 has no
    // System.Security.Cryptography.HKDF (that class is .NET 5+ only). Each Expand step needs
    // only a single HMAC block since SHA-256 already produces the 32 bytes each subkey needs:
    // T(1) = HMAC(PRK, info || counterByte) with T(0) empty and counterByte = 0x01.
    private static (byte[] AesKey, byte[] HmacKey) DeriveKeys(byte[] ikm)
    {
        var prk = Hmac(new byte[32], ikm); // Extract: HMAC(key=zero-salt, message=IKM)
        var aesKey = Hmac(prk, ExpandBlockInput(AesInfo));
        var hmacKey = Hmac(prk, ExpandBlockInput(HmacInfo));
        return (aesKey, hmacKey);
    }

    private static byte[] ExpandBlockInput(byte[] info)
    {
        var input = new byte[info.Length + 1];
        info.AsSpan().CopyTo(input);
        input[info.Length] = 0x01;
        return input;
    }

    private static byte[] Hmac(byte[] key, byte[] message)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(message);
    }
}
