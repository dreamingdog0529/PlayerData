using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace PlayerData;

// Wraps an ISaveBackend to compress each document's bytes with Deflate (raw DEFLATE, no
// GZip/zlib header). This is a size optimization only: it provides no confidentiality or
// integrity. Stack with EncryptedSaveBackend when those are required, and compress first so
// the ciphertext is not fed into the compressor (e.g. EncryptedSaveBackend(Compressed(...))).
//
// Payload layout per document: [FormatTag(1)] [UncompressedLength(LE u32)] [deflate body].
// Storing the original length lets Decompress allocate the exact output buffer once.
public sealed class CompressedSaveBackend : ISaveBackend
{
    private const byte FormatTag = 0x01;
    private const int HeaderSize = 1 + 4; // FormatTag + UncompressedLength
    private const int MaxUncompressedLength = 512 * 1024 * 1024; // 512 MiB safety cap
    private const int MaxInitialCompressionBufferSize = 64 * 1024;

    private readonly ISaveBackend _inner;
    private readonly CompressionLevel _compressionLevel;

    // Write-path transformed-document dictionary, recycled between writes via the same
    // Interlocked.Exchange handoff the other wrapper backends use: at most one in-flight
    // write holds it, a concurrent write just allocates fresh, and it is only recycled after
    // the inner WriteAsync completed. Dropped, not recycled, when the write throws.
    private Dictionary<string, byte[]>? _writeScratch;

    public CompressedSaveBackend(ISaveBackend inner)
        : this(inner, CompressionLevel.Optimal)
    {
    }

    public CompressedSaveBackend(ISaveBackend inner, CompressionLevel compressionLevel)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (compressionLevel is < CompressionLevel.Optimal or > CompressionLevel.NoCompression)
            throw new ArgumentOutOfRangeException(nameof(compressionLevel));
        _compressionLevel = compressionLevel;
    }

    public async ValueTask<SaveBundle?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var bundle = await _inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (bundle is null) return null;

        var documents = new Dictionary<string, byte[]>(bundle.Documents.Count);
        foreach (var pair in bundle.Documents)
            documents[pair.Key] = Decompress(pair.Key, pair.Value);

        return new SaveBundle(bundle.FormatVersion, documents);
    }

    public async ValueTask WriteAsync(SaveBundle bundle, CancellationToken cancellationToken = default)
    {
        if (bundle is null) throw new ArgumentNullException(nameof(bundle));

        // The input arrays are the caller's (SaveSession caches them as clean-state bytes), so
        // the write path must compress into fresh arrays - only the dictionary is recycled.
        var documents = Interlocked.Exchange(ref _writeScratch, null)
            ?? new Dictionary<string, byte[]>(bundle.Documents.Count);
        foreach (var pair in bundle.Documents)
            documents[pair.Key] = Compress(pair.Value);

        await _inner.WriteAsync(new SaveBundle(bundle.FormatVersion, documents), cancellationToken).ConfigureAwait(false);

        documents.Clear();
        Volatile.Write(ref _writeScratch, documents);
    }

    // Builds [FormatTag][UncompressedLength][deflate body]. Empty input still gets a valid
    // header with length 0 and an empty deflate body (DeflateStream with no writes produces
    // no trailer data when left incomplete... we flush via Dispose of the stream).
    internal byte[] Compress(byte[] plaintext)
    {
        if (plaintext is null) throw new ArgumentNullException(nameof(plaintext));
        if ((uint)plaintext.Length > MaxUncompressedLength)
            throw new ArgumentOutOfRangeException(nameof(plaintext), plaintext.Length,
                $"Plaintext length must be between 0 and {MaxUncompressedLength} bytes.");

        if (plaintext.Length == 0)
        {
            var empty = new byte[HeaderSize];
            empty[0] = FormatTag;
            return empty;
        }

        using var output = new PooledWriteStream(InitialCompressionBufferSize(plaintext.Length));
        output.WriteByte(FormatTag);
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes, (uint)plaintext.Length);
        output.Write(lengthBytes);

        using (var deflate = new DeflateStream(output, _compressionLevel, leaveOpen: true))
            deflate.Write(plaintext, 0, plaintext.Length);

        return output.ToArray();
    }

    internal static byte[] Decompress(string documentKey, byte[] payload)
    {
        if (payload is null) throw new ArgumentNullException(nameof(payload));
        if (payload.Length < HeaderSize)
            throw new InvalidDataException(
                $"Compressed document '{documentKey}' payload is too short ({payload.Length} bytes, minimum {HeaderSize}).");
        if (payload[0] != FormatTag)
            throw new InvalidDataException(
                $"Compressed document '{documentKey}' has an unrecognized format tag (0x{payload[0]:X2}).");

        var uncompressedLength = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1, 4));
        if (uncompressedLength > MaxUncompressedLength)
            throw new InvalidDataException(
                $"Compressed document '{documentKey}' claims an uncompressed length of {uncompressedLength} bytes, which exceeds the {MaxUncompressedLength}-byte safety cap.");

        var result = new byte[uncompressedLength];
        if (uncompressedLength == 0)
        {
            // Length 0 payloads must not carry a deflate body; anything else is corrupt.
            if (payload.Length != HeaderSize)
                throw new InvalidDataException(
                    $"Compressed document '{documentKey}' has uncompressed length 0 but carries {payload.Length - HeaderSize} trailing bytes.");
            return result;
        }

        using var input = new MemoryStream(payload, HeaderSize, payload.Length - HeaderSize, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        var totalRead = 0;
        while (totalRead < result.Length)
        {
            var n = deflate.Read(result, totalRead, result.Length - totalRead);
            if (n == 0) break;
            totalRead += n;
        }

        if (totalRead != result.Length)
            throw new InvalidDataException(
                $"Compressed document '{documentKey}' inflated to {totalRead} bytes, expected {result.Length}.");

        // Ensure the stream is fully consumed: extra inflated bytes would mean the stored
        // length was wrong or the payload was concatenated with foreign data.
        Span<byte> overflow = stackalloc byte[1];
        if (deflate.Read(overflow) != 0)
            throw new InvalidDataException(
                $"Compressed document '{documentKey}' produced more than the declared {result.Length} uncompressed bytes.");

        return result;
    }

    private static int InitialCompressionBufferSize(int plaintextLength)
    {
        var estimatedCompressedLength = Math.Min(plaintextLength, MaxInitialCompressionBufferSize);
        return HeaderSize + Math.Max(estimatedCompressedLength, 16);
    }

    private sealed class PooledWriteStream : Stream
    {
        private byte[] _buffer;
        private int _length;
        private bool _disposed;

        public PooledWriteStream(int initialCapacity)
        {
            _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => !_disposed;

        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public byte[] ToArray()
        {
            ThrowIfDisposed();
            var result = new byte[_length];
            Buffer.BlockCopy(_buffer, 0, result, 0, _length);
            return result;
        }

        public override void Flush()
        {
            ThrowIfDisposed();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer is null) throw new ArgumentNullException(nameof(buffer));
            if ((uint)offset > (uint)buffer.Length || (uint)count > (uint)(buffer.Length - offset))
                throw new ArgumentOutOfRangeException(nameof(offset));

            ThrowIfDisposed();
            EnsureCapacity(count);
            Buffer.BlockCopy(buffer, offset, _buffer, _length, count);
            _length += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ThrowIfDisposed();
            EnsureCapacity(buffer.Length);
            buffer.CopyTo(_buffer.AsSpan(_length));
            _length += buffer.Length;
        }

        public override void WriteByte(byte value)
        {
            ThrowIfDisposed();
            EnsureCapacity(1);
            _buffer[_length++] = value;
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = Array.Empty<byte>();
                _length = 0;
                _disposed = true;
            }

            base.Dispose(disposing);
        }

        private void EnsureCapacity(int additionalLength)
        {
            var required = _length + additionalLength;
            if ((uint)required <= (uint)_buffer.Length) return;

            var newCapacity = Math.Max(required, _buffer.Length * 2);
            var newBuffer = ArrayPool<byte>.Shared.Rent(newCapacity);
            Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _length);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = newBuffer;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PooledWriteStream));
        }
    }
}
