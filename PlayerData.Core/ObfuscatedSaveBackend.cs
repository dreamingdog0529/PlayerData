using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayerData;

// Wraps an ISaveBackend to obscure each document's bytes with a fixed, position-dependent XOR
// mask. This is not a security feature: the mask is a compile-time constant with no external
// key, so anyone who decompiles or reverse-engineers this library can trivially recover it. Its
// only purpose is to keep save files unreadable in a plain hex/text editor, deterring casual
// tampering. Use EncryptedSaveBackend instead when real confidentiality or tamper detection is
// required.
public sealed class ObfuscatedSaveBackend : ISaveBackend
{
    // Arbitrary fixed bytes; XOR is its own inverse, so the same mask both obscures and restores
    // a document's bytes (data[i] ^ Mask[i % Mask.Length], applied twice, is the identity).
    private static readonly byte[] Mask =
    {
        0x5A, 0xE3, 0x1C, 0x7F, 0x92, 0x08, 0xB6, 0xD4,
        0x3E, 0xC1, 0x77, 0x0A, 0xF9, 0x64, 0x2D, 0x8B,
        0x11, 0x9C, 0x45, 0xE0, 0x6F, 0x82, 0x1A, 0xD7,
        0x53, 0xC8, 0x3F, 0x96, 0x0D, 0xB1, 0x7A, 0x29,
    };

    private readonly ISaveBackend _inner;

    public ObfuscatedSaveBackend(ISaveBackend inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async ValueTask<SaveBundle?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var bundle = await _inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (bundle is null) return null;

        var documents = new Dictionary<string, byte[]>(bundle.Documents.Count);
        foreach (var pair in bundle.Documents)
            documents[pair.Key] = Transform(pair.Value);

        return new SaveBundle(bundle.FormatVersion, documents);
    }

    public ValueTask WriteAsync(SaveBundle bundle, CancellationToken cancellationToken = default)
    {
        if (bundle is null) throw new ArgumentNullException(nameof(bundle));

        var documents = new Dictionary<string, byte[]>(bundle.Documents.Count);
        foreach (var pair in bundle.Documents)
            documents[pair.Key] = Transform(pair.Value);

        return _inner.WriteAsync(new SaveBundle(bundle.FormatVersion, documents), cancellationToken);
    }

    private static byte[] Transform(byte[] data)
    {
        var result = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
            result[i] = (byte)(data[i] ^ Mask[i % Mask.Length]);
        return result;
    }
}
