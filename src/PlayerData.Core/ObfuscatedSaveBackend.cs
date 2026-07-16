using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
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

    // Write-path transformed-document dictionary, recycled between writes via the same
    // Interlocked.Exchange handoff SaveSession's commit scratch uses: at most one in-flight
    // write holds it, a concurrent write just allocates fresh, and it is only recycled after
    // the inner WriteAsync completed (the ownership contract bars the inner backend from
    // holding the bundle past that point). Dropped, not recycled, when the write throws.
    private Dictionary<string, byte[]>? _writeScratch;

    public ObfuscatedSaveBackend(ISaveBackend inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async ValueTask<SaveBundle?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var bundle = await _inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (bundle is null) return null;

        // The inner backend transfers ownership of the returned bundle (see ISaveBackend), so
        // each masked payload is unmasked in place and the same bundle instance is returned -
        // no per-document copy, no new dictionary, no new bundle.
        foreach (var pair in bundle.Documents)
            TransformInPlace(pair.Value);

        return bundle;
    }

    public async ValueTask WriteAsync(SaveBundle bundle, CancellationToken cancellationToken = default)
    {
        if (bundle is null) throw new ArgumentNullException(nameof(bundle));

        // The input arrays are the caller's (SaveSession caches them as clean-state bytes), so
        // the write path must transform into fresh arrays - only the dictionary is recycled.
        var documents = Interlocked.Exchange(ref _writeScratch, null)
            ?? new Dictionary<string, byte[]>(bundle.Documents.Count);
        foreach (var pair in bundle.Documents)
            documents[pair.Key] = Transform(pair.Value);

        await _inner.WriteAsync(new SaveBundle(bundle.FormatVersion, documents), cancellationToken).ConfigureAwait(false);

        documents.Clear();
        Volatile.Write(ref _writeScratch, documents);
    }

    // Mask.Length (32) generally doesn't divide Vector<byte>.Count evenly on every platform (16
    // on SSE2/NEON, 32 on AVX2, 64 on AVX-512), so TiledMask repeats Mask out to their least
    // common multiple: every Vector<byte>.Count-aligned offset into TiledMask then lines up with
    // the correct phase of the 32-byte mask, letting the same precomputed vector be reused for
    // every chunk at that offset (mod TiledMask.Length) instead of re-deriving it per call.
    // System.Numerics.Vector<T> (not a hardware intrinsic type) is used deliberately: it falls
    // back to a software (non-accelerated) implementation wherever SIMD isn't available, so this
    // stays correct - if slower - on platforms/backends without vector hardware support.
    private static readonly byte[] TiledMask = BuildTiledMask();

    private static byte[] BuildTiledMask()
    {
        var tileLength = Lcm(Vector<byte>.Count, Mask.Length);
        var tiled = new byte[tileLength];
        for (var i = 0; i < tileLength; i++)
            tiled[i] = Mask[i % Mask.Length];
        return tiled;
    }

    private static int Lcm(int a, int b) => a / Gcd(a, b) * b;

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }

    internal static unsafe byte[] Transform(byte[] data)
    {
        var length = data.Length;
        var result = new byte[length];
        if (length == 0) return result;

        fixed (byte* src = data, dst = result, mask = TiledMask)
            TransformCore(src, dst, length, mask);

        return result;
    }

    // Self-XOR variant for buffers this backend owns (the read path, where the inner backend
    // transferred ownership): dst == src is safe in TransformCore because every location is
    // fully read before it is written.
    internal static unsafe void TransformInPlace(byte[] data)
    {
        if (data.Length == 0) return;

        fixed (byte* buffer = data, mask = TiledMask)
            TransformCore(buffer, buffer, data.Length, mask);
    }

    // Raw-pointer loop over the previous span/indexer version: pinning src/dst/mask once
    // removes the per-chunk bounds checks the Vector<byte>(array, index) constructor and
    // CopyTo(array, index) performed, Unsafe.Read/WriteUnaligned compiles to single unaligned
    // vector moves, and the mask offset is tracked as a running counter reset at the tile
    // boundary instead of recomputing `i % tileLength` (an integer division) per chunk.
    // TiledMask.Length is an exact multiple of Vector<byte>.Count (LCM, see BuildTiledMask), so
    // `maskOffset + vectorSize <= tileLength` always holds when the counter is in range and the
    // unaligned mask read never runs off the end of the tile.
    private static unsafe void TransformCore(byte* src, byte* dst, int length, byte* mask)
    {
        var vectorSize = Vector<byte>.Count;
        var tileLength = TiledMask.Length;

        var i = 0;
        var maskOffset = 0;
        for (; i + vectorSize <= length; i += vectorSize)
        {
            var dataVector = Unsafe.ReadUnaligned<Vector<byte>>(src + i);
            var maskVector = Unsafe.ReadUnaligned<Vector<byte>>(mask + maskOffset);
            Unsafe.WriteUnaligned(dst + i, dataVector ^ maskVector);
            maskOffset += vectorSize;
            if (maskOffset == tileLength) maskOffset = 0;
        }

        for (; i < length; i++)
        {
            dst[i] = (byte)(src[i] ^ mask[maskOffset]);
            if (++maskOffset == tileLength) maskOffset = 0;
        }
    }
}
