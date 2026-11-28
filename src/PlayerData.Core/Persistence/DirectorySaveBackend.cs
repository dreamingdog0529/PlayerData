using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PlayerData.Sessions;

namespace PlayerData.Persistence;

// 配置はこうなっています：
//   {root}/manifest.bin
//   {root}/docs/{sanitized-key}.bin
// 書き込みはまず {root}/.staging の下に用意してから、文書を昇格させ、最後に manifest を
// 置き換えます。書き込みの途中で落ちても、前の manifest（とその文書ファイル）が復旧できる
// 状態として残るようにするためです。
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

        // manifest の byte[] は完全に一時的なもので、ParseManifest に消費されて捨てられます。
        // 下の文書のバイト列とは違うところで、そちらは SaveSession が dirty 追跡のキャッシュとして
        // 保持します（CollectionParticipant / DocumentParticipant の LoadBytes をご覧ください）。
        // プールして安全なのは一時的なバッファだけです：このメソッドを越えて生き残った ArrayPool の
        // 貸し出しがあると、SaveSession がまだ保持している間に、別の Rent() が同じ土台の配列を
        // 関係のないコードへ渡してしまうおそれがあります。
        var (manifestBuffer, manifestLength) = await ReadPooledAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        try
        {
            (formatVersion, keys) = ParseManifest(manifestBuffer.AsSpan(0, manifestLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(manifestBuffer);
        }

        // すべてのキーについて存在を先に確認してから、文書の読み取りをまとめて同時に発行します：
        // 各ファイルは独立していますので、読み取り全体の待ち時間は全ファイルの合計ではなく、
        // いちばん遅い1ファイル分で済みます。
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

        // ToFileName はキーごとにちょうど1回だけ計算します（昇格や、下の後片付けの走査で何度も
        // 計算し直さないためのものです）。ステージングした文書の書き込みと manifest の書き込みは
        // すべて同時に発行します。対象のファイルがそれぞれ別ですので、ステージング全体の待ち時間は
        // 合計ではなく、いちばん遅い1回の書き込み分で済みます。
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

        // 集合での検索にして O(existing + keys) に収めます。キー全体との総当たりで比較する形に
        // しないためのものです。
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

    /// <summary>
    /// セーブディレクトリの中にある、1つの文書のファイルのディスク上のパスです。このバックエンドの
    /// 配置（{root}/docs/{sanitized-key}.bin）に従います。ツール側が、キーから名前を導き直さなくても
    /// 1つの文書のファイルを見つけられるようにするためのものです。
    /// </summary>
    public static string DocumentFilePath(string rootDirectory, string key)
    {
        if (rootDirectory is null) throw new ArgumentNullException(nameof(rootDirectory));
        return Path.Combine(rootDirectory, "docs", ToFileName(key));
    }

    // 持ち運びできる、ファイル名に使えない文字の集合です（Unix と Windows の
    // Path.GetInvalidFileNameChars の和集合）で、キーが下の ".bin" 拡張子の規則と衝突しないよう
    // '.' も含めてあります。ホストの API は OS ごとに違います。Unix は '\0' と '/' しか報告しない
    // ため、そのままではバックスラッシュや Windows で予約された文字がファイル名に残ってしまい、
    // ディスク上の配置が OS 間で持ち運べなくなってしまいます。128エントリのビットマップにしておく
    // ことで、キーごとに Path.GetInvalidFileNameChars() を確保したり走査したりせずに、各文字へ
    // O(1) で答えられます。
    private static readonly bool[] InvalidFileNameCharMap = BuildInvalidFileNameCharMap();

    private static bool[] BuildInvalidFileNameCharMap()
    {
        var map = new bool[128];
        // C0 の制御文字は Windows で使えず、その中には '\0'（どこでも使えません）も含まれます。
        for (var c = 0; c < 32; c++)
            map[c] = true;
        // パスの区切り文字は両方とも（バックスラッシュは Unix では正当なファイル名の文字ですが）と、
        // Windows で予約された残りの文字です。生成される名前が、対応するどのホストでも安全になる
        // ようにするためです。
        foreach (var c in "<>:\"/\\|?*")
            map[c] = true;
        map['.'] = true;
        return map;
    }

    // キーが型名のときはそのまま読める形を保ち、ファイル名として安全でないものはエスケープします。
    // きれいなキー（圧倒的に多いケースで、キーはたいてい型名です）は走査だけの高速な経路を通り、
    // string.Concat のアロケーション1回で済みます。エスケープが必要なキーは、ToCharArray +
    // new string + concat（アロケーション3回）ではなく、string.Create でその場に結果を組み立てます。
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

    // manifest のバイナリの並び（リトルエンディアン）：
    // int32 formatVersion | int32 keyCount | 繰り返し： int32 utf8Length + utf8 bytes
    //
    // MemoryStream + BinaryWriter を使う形ではなく、大きさを先に決めた1つの byte[] へ fixed
    // ポインター経由で直接組み立てます。その形にしないためのもので、MemoryStream + BinaryWriter
    // では内部バッファのアロケーション（収まるまで伸びるので複数回になることもあります）や、
    // キーごとの Encoding.UTF8.GetBytes(string) の byte[] アロケーション、最後の ms.ToArray() の
    // コピーがかかってしまいます。BinaryPrimitives.WriteInt32LittleEndian を使うことで、ホストの
    // CPU がたまたまリトルエンディアンであることに頼らず、エンコーディングを明示的に
    // リトルエンディアンに保ちます。
    // 各キーのバイト数は1回だけ計算して `byteCounts` に持たせ（MaxStackKeys まではスタック上に、
    // それを超えたらプールから。manifest のキー数はセッションに登録された文書／コレクションの数で、
    // ふつうは handful 程度です）、書き込みの回で使い回します。
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
                    if ((uint)written > (uint)byteCount)
                    {
                        throw new InvalidOperationException("Encoding produced an invalid byte count.");
                    }
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

    // ポインターに触る前に、long の計算で `length` に対する範囲チェックをします。壊れた、あるいは
    // 悪意のある長さの接頭辞があっても、`basePtr + offset` がバッファの外へ出ないようにするためです。
    private static unsafe int ReadInt32(byte* basePtr, long length, ref long offset)
    {
        if (offset + sizeof(int) > length) throw new EndOfStreamException("Unexpected end of manifest.");
        var value = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(basePtr + offset, sizeof(int)));
        offset += sizeof(int);
        return value;
    }

    // bufferSize: 1 にすると FileStream の内部の 4 KB バッファが無効になります（BCL 自身の
    // File.ReadAllBytesAsync / WriteAllBytesAsync も使うやり方です）：ここでの読み取りはすべて、
    // ちょうどの大きさの宛先への全長1回の読み取りで、書き込みもペイロード全体を1回で書きますので、
    // 中間のバッファは、それより小さいペイロードに対してアロケーションと memcpy を増やすだけに
    // なってしまいます。SequentialScan は、各ファイルが先頭から末尾までちょうど1回読まれることを
    // OS のキャッシュへ伝えるためのものです。
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
