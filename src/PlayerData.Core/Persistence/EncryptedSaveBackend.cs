using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayerData.Persistence;

// ISaveBackend を包んで、各文書のバイト列を AES-256-CBC で暗号化し、HMAC-SHA256 で認証する
// ためのものです（Encrypt-then-MAC です）。AesGcm は使わないことにしています：どの .NET の
// モニカーでも [UnsupportedOSPlatform("browser")] が付いていて、Unity IL2CPP の下で
// PlatformNotSupportedException が報告されることがあるためです。一方、Aes / HMACSHA256 には
// netstandard2.1 でそうした制限がありません。
//
// 文書ごとのペイロードの並びは [FormatTag(1)] [IV(16)] [ciphertext(N, PKCS7)] [HMACTag(32)] です。
// HMAC はさらに、文書のキーとバンドルの FormatVersion を関連データとして（ペイロード自体には
// 保存せずに）認証しますので、暗号文を文書の間で入れ替えたり、バージョンの違うバンドルから
// 流用したりしても、検知されずに済むことはありません。
public sealed class EncryptedSaveBackend : ISaveBackend, IDisposable
{
    private const byte FormatTag = 0x01;
    private const int IvSize = 16;
    private const int TagSize = 32; // HMACSHA256 の出力サイズです
    private const int MinPayloadSize = 1 + IvSize + TagSize;
    private const int Pbkdf2Iterations = 210_000; // PBKDF2-HMAC-SHA256 についての OWASP 2023 の推奨値です

    private static readonly byte[] Pbkdf2Salt = Encoding.UTF8.GetBytes("PlayerData.Core.EncryptedSaveBackend.v1");
    private static readonly byte[] AesInfo = Encoding.UTF8.GetBytes("PlayerData.Core.v1.AES");
    private static readonly byte[] HmacInfo = Encoding.UTF8.GetBytes("PlayerData.Core.v1.HMAC");

    private readonly ISaveBackend _inner;
    private readonly byte[] _aesKey;
    private readonly byte[] _hmacKey;

    // Aes.Create()（プラットフォーム実装の探索とオブジェクトグラフ）と IncrementalHash.CreateHMAC
    // （HMAC の鍵スケジュール：詰めた鍵に対する SHA-256 ブロックの処理2回）は、どちらも構築時に
    // 決まるフィールドを鍵にしています。そのため、Protect / Unprotect の呼び出しのたびに文書ごとに
    // 作り直さず、キャッシュしたインスタンスを1つずつ使い回します。どちらの型もスレッドセーフでは
    // ないため、アクセスは _cryptoGate で直列化します（競合のないロックのコストは、呼び出しごとの
    // 構築に比べればごくわずかですし、Read / Write はもともと文書を順番に処理します）。
    private readonly object _cryptoGate = new();
    private readonly Aes _aes;
    private readonly IncrementalHash _hmac;
    private Dictionary<string, byte[]>? _writeScratch;
    // CreateEncryptor / CreateDecryptor へ渡す作業用の IV です。そちらは変換を作る前に内部で
    // 複製しますので、バックエンドごとに（_cryptoGate の下で）1つのバッファを使い回しても安全で、
    // 文書ごとに 16 バイトのアロケーションを省けます。netstandard2.1 には span を取る
    // オーバーロードがありません。
    private readonly byte[] _ivScratch = new byte[IvSize];

    // key は AES / HMAC の鍵として直接使うのではなく、HKDF の入力鍵素材（IKM）として使います：
    // 1つの生の鍵を2つのプリミティブで使い回すのは鍵の再利用というよくない形ですので、AES と
    // HMAC のサブ鍵は別々に導出します（DeriveKeys をご覧ください）。
    public EncryptedSaveBackend(ISaveBackend inner, byte[] key)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (key is null) throw new ArgumentNullException(nameof(key));
        if (key.Length == 0) throw new ArgumentException("Key must not be empty.", nameof(key));
        (_aesKey, _hmacKey) = DeriveKeys(key);
        (_aes, _hmac) = CreateCryptoPrimitives(_aesKey, _hmacKey);
    }

    public EncryptedSaveBackend(ISaveBackend inner, string passphrase)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (passphrase is null) throw new ArgumentNullException(nameof(passphrase));
        if (passphrase.Length == 0) throw new ArgumentException("Passphrase must not be empty.", nameof(passphrase));

        using var pbkdf2 = new Rfc2898DeriveBytes(passphrase, Pbkdf2Salt, Pbkdf2Iterations, HashAlgorithmName.SHA256);
        var ikm = pbkdf2.GetBytes(32);
        (_aesKey, _hmacKey) = DeriveKeys(ikm);
        (_aes, _hmac) = CreateCryptoPrimitives(_aesKey, _hmacKey);
    }

    private static (Aes Aes, IncrementalHash Hmac) CreateCryptoPrimitives(byte[] aesKey, byte[] hmacKey)
    {
        var aes = Aes.Create();
        aes.Key = aesKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return (aes, IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, hmacKey));
    }

    public void Dispose()
    {
        _aes.Dispose();
        _hmac.Dispose();
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

    public async ValueTask WriteAsync(SaveBundle bundle, CancellationToken cancellationToken = default)
    {
        if (bundle is null) throw new ArgumentNullException(nameof(bundle));

        // 使い回すディクショナリです（ObfuscatedSaveBackend と同じ Interlocked.Exchange の
        // 受け渡し）：同時に進行中の書き込みで取るのは最大で1つだけで、戻すのは内側の WriteAsync が
        // 完了したあとだけです（所有権の約束ごとにより、内側のバックエンドがその時点を越えてバンドルを
        // 保持することはありません）。書き込みが例外を投げたときは捨てます。暗号化したペイロード自体は
        // 呼び出しごとに新しいもので、再利用するのはディクショナリだけです。
        var documents = Interlocked.Exchange(ref _writeScratch, null)
            ?? new Dictionary<string, byte[]>(bundle.Documents.Count);
        foreach (var pair in bundle.Documents)
            documents[pair.Key] = Protect(bundle.FormatVersion, pair.Key, pair.Value);

        await _inner.WriteAsync(new SaveBundle(bundle.FormatVersion, documents), cancellationToken).ConfigureAwait(false);

        documents.Clear();
        Volatile.Write(ref _writeScratch, documents);
    }

    // 最終的なペイロードのバッファ（[FormatTag][IV][ciphertext][HMACTag]）を1回で組み立て、
    // そこへ直接暗号化します。途中で暗号文の配列や ivAndCiphertext の配列、MAC 入力の配列を
    // 別々に確保しないための形です。PKCS7 は入力がすでにブロック境界に揃っていても必ず丸ごとの
    // パディングブロックを付けますので、暗号文の長さは常に平文を次の 16 バイト境界へ切り上げた
    // 大きさになり、最終バッファの大きさを最初に決められます。TransformBlock は完全なブロックを
    // ペイロードの正しいオフセットへ直接書きます。最後のブロック（すべてパディングのこともあります）
    // だけは TransformFinalBlock から新しい固定 16 バイトの配列として返ってきます。
    // ICryptoTransform には netstandard2.1 で span やオフセットへ書くオーバーロードがないためです。
    internal byte[] Protect(int formatVersion, string documentKey, byte[] plaintext)
    {
        // IV は最終的な置き場所であるペイロードの中へ直接生成します。コピーは、CreateEncryptor が
        // byte[] として必要とする使い回しの作業バッファへの1回だけです。aes.GenerateIV() と
        // aes.IV の getter を使う形にしないのは、呼び出しごとに内部の配列と防御的なコピーが
        // 確保されてしまうためです。
        var ciphertextLength = (plaintext.Length / 16 + 1) * 16;
        var payload = new byte[1 + IvSize + ciphertextLength + TagSize];
        payload[0] = FormatTag;
        RandomNumberGenerator.Fill(payload.AsSpan(1, IvSize));

        var ciphertextOffset = 1 + IvSize;
        lock (_cryptoGate)
        {
            payload.AsSpan(1, IvSize).CopyTo(_ivScratch);
            using (var encryptor = _aes.CreateEncryptor(_aesKey, _ivScratch))
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
        }
        return payload;
    }

    // 暗号文（や IV）を専用の配列へ先にコピーするのではなく、入力の `payload` 配列に対して
    // オフセットで操作します：ICryptoTransform.TransformFinalBlock は（配列、オフセット、個数）の
    // 組を受け取りますので、呼び出し側のバッファから直接復号できます。専用の配列が要るのは
    // 16 バイトの IV だけで、SymmetricAlgorithm.IV の setter が netstandard2.1 では byte[] を
    // 取るためです（ReadOnlySpan<byte> のオーバーロードがありません）。
    internal byte[] Unprotect(int formatVersion, string documentKey, byte[] payload)
    {
        if (payload.Length < MinPayloadSize)
            throw new SaveTamperDetectedException(
                $"Encrypted document '{documentKey}' payload is too short ({payload.Length} bytes, minimum {MinPayloadSize}).");
        if (payload[0] != FormatTag)
            throw new SaveTamperDetectedException(
                $"Encrypted document '{documentKey}' has an unrecognized format tag (0x{payload[0]:X2}).");

        var ivAndCiphertextLength = payload.Length - 1 - TagSize;
        var ciphertextOffset = 1 + IvSize;
        var ciphertextLength = ivAndCiphertextLength - IvSize;

        lock (_cryptoGate)
        {
            var expectedTag = ComputeMac(formatVersion, documentKey, payload.AsSpan(1, ivAndCiphertextLength));
            if (!CryptographicOperations.FixedTimeEquals(payload.AsSpan(1 + ivAndCiphertextLength, TagSize), expectedTag))
                throw new SaveTamperDetectedException(
                    $"Encrypted document '{documentKey}' failed integrity verification (HMAC mismatch).");

            payload.AsSpan(1, IvSize).CopyTo(_ivScratch);

            try
            {
                using var decryptor = _aes.CreateDecryptor(_aesKey, _ivScratch);
                return decryptor.TransformFinalBlock(payload, ciphertextOffset, ciphertextLength);
            }
            catch (CryptographicException ex)
            {
                // ここでパディングが失敗するのは、上の HMAC は通ったのに平文がそれでも壊れている
                // ということで、鍵が壊れているような場合でもない限り起きないはずです。より詳しい
                // 別のエラーの経路を表に出さず、同じ例外にまとめて扱います。
                throw new SaveTamperDetectedException(
                    $"Encrypted document '{documentKey}' failed to decrypt after passing integrity verification.", ex);
            }
        }
    }

    // HMAC の入力は FormatTag(1) || LE-Int32(formatVersion) || LE-Int32(keyByteLength) ||
    // UTF8(documentKey) || ivAndCiphertext です。documentKey と formatVersion は関連データで、
    // 認証はされますがペイロードには保存されません。各タグが、それが書かれた特定の文書の場所と
    // バンドルのバージョンに結びつくようにするためです。
    //
    // すべてを1つの macInput 配列へ先に連結するのではなく、IncrementalHash.AppendData で
    // 3つの span に分けて組み立てます。HMAC はストリーミングの構成ですので、同じバイト列を同じ
    // 順番で追加すれば同じタグになります。documentKey の UTF8 バイトは、呼び出しごとのヒープの
    // 配列ではなく、スタック上（MaxStackKeyBytes を超えたらプール）のバッファへエンコードします。
    // GetHashAndReset() は netstandard2.1 で使える唯一のオーバーロードですので（Span<byte> へ
    // 書くものは net5.0 以降です）、32 バイトのタグ自体は新しいアロケーションになります。
    private const int MaxStackKeyBytes = 256;

    // 呼び出し側は _cryptoGate を保持している必要があります：共有の _hmac インスタンスは、
    // AppendData から GetHashAndReset までの間、計算ごとの状態を持ちます（GetHashAndReset は
    // リセットして、次の MAC の準備ができた状態で残してくれます）。
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

            _hmac.AppendData(header);
            _hmac.AppendData(keyBuffer.Slice(0, keyByteLength));
            _hmac.AppendData(ivAndCiphertext);
            return _hmac.GetHashAndReset();
        }
        finally
        {
            if (rentedKeyBuffer is not null) ArrayPool<byte>.Shared.Return(rentedKeyBuffer);
        }
    }

    // RFC 5869 の HKDF（Extract-then-Expand）で、HMACSHA256 だけで組み立ててあります：
    // netstandard2.1 には System.Security.Cryptography.HKDF がありません（そのクラスは
    // .NET 5 以降だけです）。SHA-256 は各サブ鍵が必要とする 32 バイトをすでに生みますので、
    // Expand の各ステップには HMAC ブロックが1つで足ります：
    // T(1) = HMAC(PRK, info || counterByte) で、T(0) は空、counterByte は 0x01 です。
    private static (byte[] AesKey, byte[] HmacKey) DeriveKeys(byte[] ikm)
    {
        var prk = Hmac(new byte[32], ikm); // Extract です：HMAC(key=zero-salt, message=IKM)
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
