using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using PlayerData.Sessions;

namespace PlayerData.Persistence;

// ISaveBackend を包んで、各文書のバイト列を、固定の位置依存の XOR マスクで分かりにくくする
// ためのものです。これはセキュリティ機能ではありません：マスクはコンパイル時の定数で外の鍵も
// ありませんので、このライブラリをデコンパイルしたりリバースエンジニアリングしたりできる人なら
// 簡単に復元できてしまいます。目的は、セーブファイルを普通の16進／テキストエディターでは読めなく
// して、気軽な改変を思いとどまらせることだけです。本当の秘匿性や改ざん検知が必要なときは、
// 代わりに EncryptedSaveBackend を使ってください。
public sealed class ObfuscatedSaveBackend : ISaveBackend
{
    // 任意に決めた固定のバイト列です。XOR は自身が逆演算ですので、同じマスクで文書のバイト列を
    // 分かりにくくもでき、元に戻すこともできます（data[i] ^ Mask[i % Mask.Length] を2回適用すると
    // 元に戻ります）。
    private static readonly byte[] Mask =
    {
        0x5A, 0xE3, 0x1C, 0x7F, 0x92, 0x08, 0xB6, 0xD4,
        0x3E, 0xC1, 0x77, 0x0A, 0xF9, 0x64, 0x2D, 0x8B,
        0x11, 0x9C, 0x45, 0xE0, 0x6F, 0x82, 0x1A, 0xD7,
        0x53, 0xC8, 0x3F, 0x96, 0x0D, 0xB1, 0x7A, 0x29,
    };

    private readonly ISaveBackend _inner;

    // 書き込み経路で変換後の文書を入れるディクショナリで、SaveSession のコミット用作業領域と
    // 同じ Interlocked.Exchange の受け渡しで書き込みの合間に使い回します：同時に進行中の
    // 書き込みでこれを持つのは最大で1つだけで、競合した書き込みは新しく確保します。再利用するのは
    // 内側の WriteAsync が完了したあとだけです（所有権の約束ごとにより、内側のバックエンドが
    // その時点を越えてバンドルを保持することはありません）。書き込みが例外を投げたときは、
    // 使い回さずに捨てます。
    private Dictionary<string, byte[]>? _writeScratch;

    public ObfuscatedSaveBackend(ISaveBackend inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async ValueTask<SaveBundle?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var bundle = await _inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (bundle is null) return null;

        // 内側のバックエンドは、返したバンドルの所有権を移してくれます（ISaveBackend をご覧
        // ください）ので、各マスク済みペイロードはその場でマスクを外し、同じバンドルのインスタンスを
        // 返します。文書ごとのコピーも、新しいディクショナリも、新しいバンドルも要りません。
        foreach (var pair in bundle.Documents)
            TransformInPlace(pair.Value);

        return bundle;
    }

    public async ValueTask WriteAsync(SaveBundle bundle, CancellationToken cancellationToken = default)
    {
        if (bundle is null) throw new ArgumentNullException(nameof(bundle));

        // 入力の配列は呼び出し側のものです（SaveSession が clean な状態のバイト列としてキャッシュして
        // います）ので、書き込み経路では新しい配列へ変換します。使い回すのはディクショナリだけです。
        var documents = Interlocked.Exchange(ref _writeScratch, null)
            ?? new Dictionary<string, byte[]>(bundle.Documents.Count);
        foreach (var pair in bundle.Documents)
            documents[pair.Key] = Transform(pair.Value);

        await _inner.WriteAsync(new SaveBundle(bundle.FormatVersion, documents), cancellationToken).ConfigureAwait(false);

        documents.Clear();
        Volatile.Write(ref _writeScratch, documents);
    }

    // Mask.Length（32）は、プラットフォームごとの Vector<byte>.Count（SSE2 / NEON では 16、
    // AVX2 では 32、AVX-512 では 64）を一般には割り切りません。そこで TiledMask は、Mask を
    // その最小公倍数まで繰り返したものです：TiledMask の中では Vector<byte>.Count に揃った
    // どのオフセットも 32 バイトのマスクの正しい位相と一致しますので、そのオフセット
    // （TiledMask.Length で割った余り）では、あらかじめ計算した同じベクターをすべてのチャンクで
    // 使い回せて、呼び出しごとに導き直さずに済みます。System.Numerics.Vector<T>（ハードウェア
    // 組み込みの型ではありません）を意図的に使っています：SIMD が使えない場所ではソフトウェアの
    // （加速されない）実装にフォールバックしますので、ベクターのハードウェア対応がない
    // プラットフォームやバックエンドでも、遅くはなっても正しさは保てます。
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

    // このバックエンドが所有するバッファ向けの、自分自身への XOR の版です（読み取り経路で、
    // 内側のバックエンドから所有権が移ったもの）：TransformCore ではどの場所も書かれる前に
    // 完全に読まれますので、dst == src でも安全です。
    internal static unsafe void TransformInPlace(byte[] data)
    {
        if (data.Length == 0) return;

        fixed (byte* buffer = data, mask = TiledMask)
            TransformCore(buffer, buffer, data.Length, mask);
    }

    // 生ポインターのループです。src / dst / mask を一度だけピン留めすることで、Vector<byte>
    // （array, index）のコンストラクターや CopyTo(array, index) が行うチャンクごとの範囲チェックを
    // なくせます。Unsafe.Read/WriteUnaligned は単一の非整列ベクター移動にコンパイルされ、マスクの
    // オフセットはチャンクごとに `i % tileLength`（整数除算）を計算し直すのではなく、タイルの
    // 境界でリセットされる進行カウンターで追います。TiledMask.Length は Vector<byte>.Count の
    // ちょうどの倍数ですので（最小公倍数です。BuildTiledMask をご覧ください）、カウンターが範囲内に
    // ある限り `maskOffset + vectorSize <= tileLength` が常に成り立ち、非整列のマスク読み取りが
    // タイルの末尾を越えることはありません。
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
