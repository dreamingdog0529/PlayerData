using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using PlayerData.Sessions;

namespace PlayerData.Persistence;

// ISaveBackend を包んで、各文書のバイト列を Deflate で圧縮するためのものです（生の DEFLATE で、
// GZip / zlib のヘッダーは付きません）。これはサイズを小さくするためだけのもので、秘匿性や
// 完全性は提供しません。そうしたものが必要なときは EncryptedSaveBackend と重ねて使い、
// 暗号文を圧縮器に渡さないよう、先に圧縮する順番にしてください
// （たとえば EncryptedSaveBackend(Compressed(...)) のようにします）。
//
// 文書ごとのペイロードの並びは [FormatTag(1)] [UncompressedLength(LE u32)] [deflate body] です。
// 元の長さを保存しておくことで、Decompress が出力バッファをちょうどの大きさで1回だけ確保できます。
public sealed class CompressedSaveBackend : ISaveBackend
{
    private const byte FormatTag = 0x01;
    private const int HeaderSize = 1 + 4; // FormatTag + UncompressedLength
    private const int MaxUncompressedLength = 512 * 1024 * 1024; // 512 MiB の安全上限です
    private const int MaxInitialCompressionBufferSize = 64 * 1024;

    private readonly ISaveBackend _inner;
    private readonly CompressionLevel _compressionLevel;

    // 書き込み経路で変換後の文書を入れるディクショナリで、ほかのラッパーバックエンドと同じ
    // Interlocked.Exchange の受け渡しで書き込みの合間に使い回します：同時に進行中の書き込みで
    // これを持つのは最大で1つだけで、競合した書き込みは新しく確保します。再利用するのは内側の
    // WriteAsync が完了したあとだけです。書き込みが例外を投げたときは、使い回さずに捨てます。
    private Dictionary<string, byte[]>? _writeScratch;

    // 既定は Optimal ではなく Fastest です：現実的な（圧縮の効く）セーブのペイロードでは、Fastest は
    // サイズの差がごくわずかで読み取りの不利もなく、書き込みがおよそ30〜40%速くなりますし、処理全体の
    // 支配的なコストは deflater の CPU 時間です。書き込みの遅さと引き換えにペイロードを小さくしたい
    // ときは、CompressionLevel.Optimal を明示的に渡してください。保存形式はレベルに依存しませんので、
    // どちらのレベルで書いたセーブも、あとから読む側がどちらのレベルで作られていても往復できます。
    public CompressedSaveBackend(ISaveBackend inner)
        : this(inner, CompressionLevel.Fastest)
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

        // 入力の配列は呼び出し側のものです（SaveSession が clean な状態のバイト列としてキャッシュして
        // います）ので、書き込み経路では新しい配列へ圧縮します。使い回すのはディクショナリだけです。
        var documents = Interlocked.Exchange(ref _writeScratch, null)
            ?? new Dictionary<string, byte[]>(bundle.Documents.Count);
        foreach (var pair in bundle.Documents)
            documents[pair.Key] = Compress(pair.Value);

        await _inner.WriteAsync(new SaveBundle(bundle.FormatVersion, documents), cancellationToken).ConfigureAwait(false);

        documents.Clear();
        Volatile.Write(ref _writeScratch, documents);
    }

    // [FormatTag][UncompressedLength][deflate body] を組み立てます。空の平文ではヘッダーだけの
    // ペイロード（FormatTag + 長さ 0 の LE で、deflate の本体はありません）になります。これは、
    // 宣言された長さが 0 のときに末尾の余分なバイトを拒む Decompress の扱いと揃えてあります。
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
            // 長さ 0 のペイロードに deflate の本体が付いていたら壊れているものとして扱います。
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

        // ストリームを最後まで読み切ったかを確認します：膨らんだバイトが余るようなら、保存された
        // 長さが間違っているか、ペイロードに余計なデータがくっついているということです。
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
                ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
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
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
            _buffer = newBuffer;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PooledWriteStream));
        }
    }
}
