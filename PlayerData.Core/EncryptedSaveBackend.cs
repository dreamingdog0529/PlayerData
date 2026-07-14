using System;
using System.Buffers;
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

    // Builds the final payload buffer once ([FormatTag][IV][ciphertext][HMACTag]) and encrypts
    // directly into it instead of allocating a separate ciphertext array, an ivAndCiphertext
    // array, and a MAC-input array along the way (the pre-ST-2 shape of this method). PKCS7
    // always appends a full padding block even when the input is already block-aligned, so the
    // ciphertext length is always plaintext rounded up to the next 16-byte boundary - this lets
    // the final buffer be sized up front. TransformBlock writes full blocks straight into the
    // payload at the right offset; only the last (possibly all-padding) block still comes back
    // as a fresh, fixed 16-byte array from TransformFinalBlock, since ICryptoTransform has no
    // span/offset-writing overload for it on netstandard2.1.
    internal byte[] Protect(int formatVersion, string documentKey, byte[] plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = _aesKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateIV();

        var ciphertextLength = (plaintext.Length / 16 + 1) * 16;
        var payload = new byte[1 + IvSize + ciphertextLength + TagSize];
        payload[0] = FormatTag;
        aes.IV.CopyTo(payload, 1);

        var ciphertextOffset = 1 + IvSize;
        using (var encryptor = aes.CreateEncryptor())
        {
            var fullBlockBytes = (plaintext.Length / 16) * 16;
            var written = fullBlockBytes > 0
                ? encryptor.TransformBlock(plaintext, 0, fullBlockBytes, payload, ciphertextOffset)
                : 0;
            var finalBlock = encryptor.TransformFinalBlock(plaintext, fullBlockBytes, plaintext.Length - fullBlockBytes);
            finalBlock.CopyTo(payload.AsSpan(ciphertextOffset + written));
        }

        var ivAndCiphertext = payload.AsSpan(1, IvSize + ciphertextLength);
        var tag = ComputeMac(formatVersion, documentKey, ivAndCiphertext);
        tag.CopyTo(payload.AsSpan(1 + IvSize + ciphertextLength));
        return payload;
    }

    // Operates on the input `payload` array by offset instead of first copying the ciphertext
    // (and, previously, the IV) out into their own arrays: ICryptoTransform.TransformFinalBlock
    // accepts an (array, offset, count) triple, so it can decrypt straight out of the caller's
    // buffer. Only the 16-byte IV still needs a dedicated array, since SymmetricAlgorithm.IV's
    // setter takes byte[] on netstandard2.1 (no ReadOnlySpan<byte> overload).
    internal byte[] Unprotect(int formatVersion, string documentKey, byte[] payload)
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

        var iv = payload.AsSpan(1, IvSize).ToArray();
        var ciphertextOffset = 1 + IvSize;
        var ciphertextLength = ivAndCiphertextLength - IvSize;

        using var aes = Aes.Create();
        aes.Key = _aesKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.IV = iv;

        try
        {
            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(payload, ciphertextOffset, ciphertextLength);
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
    //
    // Built via IncrementalHash.AppendData across three spans instead of concatenating
    // everything into one macInput array first - HMAC is a streaming construction, so appending
    // the same bytes in the same order produces an identical tag. documentKey's UTF8 bytes are
    // encoded into a stack (or, past MaxStackKeyBytes, pooled) buffer rather than a per-call
    // heap array; GetHashAndReset() is netstandard2.1's only overload (the Span<byte>-writing one
    // is net5.0+), so the 32-byte tag itself is still a fresh allocation.
    private const int MaxStackKeyBytes = 256;

    private byte[] ComputeMac(int formatVersion, string documentKey, ReadOnlySpan<byte> ivAndCiphertext)
    {
        var maxKeyByteCount = Encoding.UTF8.GetMaxByteCount(documentKey.Length);
        byte[]? rentedKeyBuffer = null;
        try
        {
            Span<byte> keyBuffer = maxKeyByteCount <= MaxStackKeyBytes
                ? stackalloc byte[maxKeyByteCount]
                : (rentedKeyBuffer = ArrayPool<byte>.Shared.Rent(maxKeyByteCount));
            var keyByteLength = Encoding.UTF8.GetBytes(documentKey, keyBuffer);

            Span<byte> header = stackalloc byte[1 + 4 + 4];
            header[0] = FormatTag;
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(1, 4), formatVersion);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(5, 4), keyByteLength);

            using var incrementalHash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, _hmacKey);
            incrementalHash.AppendData(header);
            incrementalHash.AppendData(keyBuffer.Slice(0, keyByteLength));
            incrementalHash.AppendData(ivAndCiphertext);
            return incrementalHash.GetHashAndReset();
        }
        finally
        {
            if (rentedKeyBuffer is not null) ArrayPool<byte>.Shared.Return(rentedKeyBuffer);
        }
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
