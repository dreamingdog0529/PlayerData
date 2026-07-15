using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayerData;

// Layout:
//   {root}/manifest.bin
//   {root}/docs/{sanitized-key}.bin
// Writes stage under {root}/.staging then promote docs and finally replace the manifest so a
// crash mid-write leaves the previous manifest (and its doc files) as the recoverable state.
public sealed class DirectorySaveBackend : ISaveBackend
{
    private readonly string _root;

    public DirectorySaveBackend(string rootDirectory)
    {
        _root = rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory));
    }

    public async ValueTask<SaveBundle?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var manifestPath = ManifestPath();
        if (!File.Exists(manifestPath)) return null;

        int formatVersion;
        string[] keys;

        // The manifest byte[] is fully transient - consumed by ParseManifest and then discarded
        // - unlike document bytes below, which SaveSession retains as its dirty-tracking cache
        // (see CollectionParticipant/DocumentParticipant.LoadBytes). Only a transient buffer is
        // safe to pool: an ArrayPool rental that outlived this method would risk another Rent()
        // handing the same backing array to unrelated code while SaveSession still held it.
        var (manifestBuffer, manifestLength) = await ReadPooledAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        try
        {
            (formatVersion, keys) = ParseManifest(manifestBuffer.AsSpan(0, manifestLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(manifestBuffer);
        }

        // Existence is checked up front for every key, then all document reads are issued
        // concurrently: each file is independent, so total read latency is the slowest single
        // file instead of the sum of all files.
        var readTasks = new Task<byte[]>[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            var path = DocumentPath(keys[i]);
            if (!File.Exists(path))
                throw new InvalidDataException($"Manifest lists document '{keys[i]}' but file '{path}' is missing.");

            readTasks[i] = ReadAllBytesAsync(path, cancellationToken).AsTask();
        }

        await Task.WhenAll(readTasks).ConfigureAwait(false);

        var documents = new Dictionary<string, byte[]>(keys.Length);
        for (var i = 0; i < keys.Length; i++)
            documents[keys[i]] = readTasks[i].Result;

        return new SaveBundle(formatVersion, documents);
    }

    public async ValueTask WriteAsync(SaveBundle bundle, CancellationToken cancellationToken = default)
    {
        if (bundle is null) throw new ArgumentNullException(nameof(bundle));

        Directory.CreateDirectory(_root);
        var staging = Path.Combine(_root, ".staging");
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);

        var stagingDocs = Path.Combine(staging, "docs");
        Directory.CreateDirectory(stagingDocs);

        // ToFileName is computed exactly once per key (it used to run again for promotion and
        // once per key per existing file in the cleanup scan below), and all staged document
        // writes plus the manifest write are issued concurrently - they target distinct files,
        // so total staging latency is the slowest single write instead of the sum.
        var count = bundle.Documents.Count;
        var keys = new string[count];
        var fileNames = new string[count];
        var writeTasks = new Task[count + 1];
        var index = 0;
        foreach (var pair in bundle.Documents)
        {
            var fileName = ToFileName(pair.Key);
            keys[index] = pair.Key;
            fileNames[index] = fileName;
            writeTasks[index] = WriteAllBytesAsync(Path.Combine(stagingDocs, fileName), pair.Value, cancellationToken).AsTask();
            index++;
        }

        var stagingManifest = Path.Combine(staging, "manifest.bin");
        writeTasks[count] = WriteAllBytesAsync(stagingManifest, BuildManifest(bundle.FormatVersion, keys), cancellationToken)
            .AsTask();

        await Task.WhenAll(writeTasks).ConfigureAwait(false);

        var docsDir = Path.Combine(_root, "docs");
        Directory.CreateDirectory(docsDir);

        for (var i = 0; i < count; i++)
        {
            var dest = Path.Combine(docsDir, fileNames[i]);
            var src = Path.Combine(stagingDocs, fileNames[i]);
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(src, dest);
        }

        // O(existing + keys) via a set lookup, replacing the old O(existing x keys) scan that
        // also re-ran ToFileName for every comparison.
        var liveNames = new HashSet<string>(fileNames, StringComparer.Ordinal);
        foreach (var existing in Directory.GetFiles(docsDir, "*.bin"))
        {
            if (!liveNames.Contains(Path.GetFileName(existing)))
                File.Delete(existing);
        }

        var manifestDest = ManifestPath();
        if (File.Exists(manifestDest)) File.Delete(manifestDest);
        File.Move(stagingManifest, manifestDest);

        Directory.Delete(staging, recursive: true);
    }

    private string ManifestPath() => Path.Combine(_root, "manifest.bin");

    private string DocumentPath(string key) => Path.Combine(_root, "docs", ToFileName(key));

    // Path.GetInvalidFileNameChars() allocates a fresh array on every call and was linearly
    // scanned per character; every invalid char it returns is ASCII on all supported platforms
    // ('\0' and '/' on Unix; control chars plus <>:"/\|?* on Windows), so a 128-entry bitmap
    // (with '.' folded in, per the escaping rule below) answers each character in O(1).
    private static readonly bool[] InvalidFileNameCharMap = BuildInvalidFileNameCharMap();

    private static bool[] BuildInvalidFileNameCharMap()
    {
        var map = new bool[128];
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            if (c < 128) map[c] = true;
        }
        map['.'] = true;
        return map;
    }

    // Keep keys readable when they are type names; escape anything unsafe for file names.
    // Clean keys (the overwhelmingly common case - keys are usually type names) take a scan-only
    // fast path with a single string.Concat allocation; keys that need escaping build the result
    // in-place via string.Create instead of ToCharArray + new string + concat (three allocations).
    internal static string ToFileName(string key)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key must be non-empty.", nameof(key));

        var needsEscape = false;
        foreach (var c in key)
        {
            if (c < 128 && InvalidFileNameCharMap[c])
            {
                needsEscape = true;
                break;
            }
        }

        if (!needsEscape) return string.Concat(key, ".bin");

        return string.Create(key.Length + 4, key, static (span, k) =>
        {
            for (var i = 0; i < k.Length; i++)
            {
                var c = k[i];
                span[i] = c < 128 && InvalidFileNameCharMap[c] ? '_' : c;
            }
            ".bin".AsSpan().CopyTo(span.Slice(k.Length));
        });
    }

    // Manifest binary layout (little-endian):
    // int32 formatVersion | int32 keyCount | repeated: int32 utf8Length + utf8 bytes
    //
    // Built directly into a single pre-sized byte[] through a fixed pointer instead of
    // MemoryStream + BinaryWriter, which cost at least one internal buffer allocation (often
    // several, as it grows to fit), one Encoding.UTF8.GetBytes(string) byte[] allocation per
    // key, and a final ms.ToArray() copy. BinaryPrimitives.WriteInt32LittleEndian keeps the
    // encoding explicitly little-endian (matching BinaryWriter's historical behavior) rather
    // than relying on the host CPU happening to be little-endian.
    // GetByteCount(key) used to run once per key while sizing the buffer and again per key while
    // writing into it. Each key's count is computed once and held in `byteCounts` (stack-allocated
    // up to MaxStackKeys, pooled beyond that - manifest key counts come from a session's
    // registered documents/collections, which is normally a handful) and reused for the write pass.
    private const int MaxStackKeys = 64;

    internal static unsafe byte[] BuildManifest(int formatVersion, string[] keys)
    {
        int[]? rentedByteCounts = null;
        try
        {
            Span<int> byteCounts = keys.Length <= MaxStackKeys
                ? stackalloc int[keys.Length]
                : (rentedByteCounts = ArrayPool<int>.Shared.Rent(keys.Length));

            var totalSize = sizeof(int) + sizeof(int);
            for (var i = 0; i < keys.Length; i++)
            {
                var byteCount = Encoding.UTF8.GetByteCount(keys[i]);
                byteCounts[i] = byteCount;
                totalSize += sizeof(int) + byteCount;
            }

            var result = new byte[totalSize];
            fixed (byte* basePtr = result)
            {
                var ptr = basePtr;
                BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(ptr, sizeof(int)), formatVersion);
                ptr += sizeof(int);
                BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(ptr, sizeof(int)), keys.Length);
                ptr += sizeof(int);

                for (var i = 0; i < keys.Length; i++)
                {
                    var byteCount = byteCounts[i];
                    BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(ptr, sizeof(int)), byteCount);
                    ptr += sizeof(int);
                    var written = Encoding.UTF8.GetBytes(keys[i].AsSpan(), new Span<byte>(ptr, byteCount));
                    ptr += written;
                }
            }
            return result;
        }
        finally
        {
            if (rentedByteCounts is not null) ArrayPool<int>.Shared.Return(rentedByteCounts);
        }
    }

    internal static unsafe (int FormatVersion, string[] Keys) ParseManifest(ReadOnlySpan<byte> data)
    {
        fixed (byte* basePtr = data)
        {
            var length = (long)data.Length;
            var offset = 0L;

            var formatVersion = ReadInt32(basePtr, length, ref offset);
            var count = ReadInt32(basePtr, length, ref offset);
            if (count < 0) throw new InvalidDataException("Manifest key count is negative.");

            var keys = new string[count];
            for (var i = 0; i < count; i++)
            {
                var len = ReadInt32(basePtr, length, ref offset);
                if (len < 0) throw new InvalidDataException("Manifest key length is negative.");
                if (offset + len > length) throw new EndOfStreamException("Unexpected end of manifest while reading a key.");
                keys[i] = Encoding.UTF8.GetString(basePtr + offset, len);
                offset += len;
            }
            return (formatVersion, keys);
        }
    }

    // Bounds-checks against `length` using long arithmetic before touching the pointer, so a
    // corrupted/adversarial length prefix can never push `basePtr + offset` out of the buffer.
    private static unsafe int ReadInt32(byte* basePtr, long length, ref long offset)
    {
        if (offset + sizeof(int) > length) throw new EndOfStreamException("Unexpected end of manifest.");
        var value = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(basePtr + offset, sizeof(int)));
        offset += sizeof(int);
        return value;
    }

    // bufferSize: 1 disables FileStream's internal 4 KB buffer (the same trick the BCL's own
    // File.ReadAllBytesAsync/WriteAllBytesAsync use): every read here is a single full-length
    // read into an exact-size destination and every write is one full-payload write, so the
    // intermediate buffer would only add an allocation plus a memcpy for payloads smaller than
    // it. SequentialScan hints the OS cache that each file is read front-to-back exactly once.
    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async ValueTask ReadExactAsync(FileStream stream, string path, byte[] buffer, int length, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer, offset, length - offset, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException($"Unexpected end of stream while reading '{path}'.");
            offset += read;
        }
    }

    private static async ValueTask<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
    {
        using var stream = OpenRead(path);
        var buffer = new byte[stream.Length];
        await ReadExactAsync(stream, path, buffer, buffer.Length, cancellationToken).ConfigureAwait(false);
        return buffer;
    }

    private static async ValueTask<(byte[] Buffer, int Length)> ReadPooledAsync(string path, CancellationToken cancellationToken)
    {
        using var stream = OpenRead(path);
        var length = checked((int)stream.Length);
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        await ReadExactAsync(stream, path, buffer, length, cancellationToken).ConfigureAwait(false);
        return (buffer, length);
    }

    private static async ValueTask WriteAllBytesAsync(string path, byte[] data, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1,
            FileOptions.Asynchronous);
        await stream.WriteAsync(data, 0, data.Length, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
