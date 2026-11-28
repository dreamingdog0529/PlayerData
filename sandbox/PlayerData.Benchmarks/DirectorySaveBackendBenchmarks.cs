using System.IO;
using System.Text;
using BenchmarkDotNet.Attributes;
using PlayerData.Persistence;
using PlayerData.Sessions;

namespace PlayerData.Benchmarks;

// 現在の DirectorySaveBackend.BuildManifest / ParseManifest（固定ポインタと BinaryPrimitives を使う形です。
// PlayerData.Core 側と、計画 202607131429-playerdata-extreme-perf の ST-2 をご覧ください）と、最適化前の
// 実装を凍結したコピー（MemoryStream と BinaryWriter / BinaryReader を使う形で、コミット e982877 から
// そのまま持ってきたものです）を比べます。古いコードは、比べるための基準としてここに置いているだけです。
// 本番のコードではなく、この比較の外では使われません。
[MemoryDiagnoser]
[SimpleJob(launchCount: 3, warmupCount: 3, iterationCount: 5)]
public class DirectorySaveBackendBenchmarks
{
    // ふだんの SaveSession の代表的な形です。登録されたドキュメントやコレクションが数個だけあります
    // （コレクションの項目ごとのエントリではありません。それらは、各ドキュメント自身の MemoryPack のバイト列の中に入っています）。
    private static readonly string[] Keys = { "profile", "items", "deck", "currency", "settings" };

    private byte[] _manifestBytes = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _manifestBytes = DirectorySaveBackend.BuildManifest(1, Keys);
    }

    [Benchmark(Baseline = true)]
    public byte[] BuildManifest_Old_MemoryStreamBinaryWriter() => OldBuildManifest(1, Keys);

    [Benchmark]
    public byte[] BuildManifest_New_UnsafeBinaryPrimitives() => DirectorySaveBackend.BuildManifest(1, Keys);

    [Benchmark]
    public (int FormatVersion, string[] Keys) ParseManifest_Old_MemoryStreamBinaryReader() => OldParseManifest(_manifestBytes);

    [Benchmark]
    public (int FormatVersion, string[] Keys) ParseManifest_New_UnsafeBinaryPrimitives() => DirectorySaveBackend.ParseManifest(_manifestBytes);

    // --- ここから凍結した基準です。PlayerData.Core/DirectorySaveBackend.cs の e982877 時点のものを、そのままコピーしています ---

    private static byte[] OldBuildManifest(int formatVersion, string[] keys)
    {
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(formatVersion);
            writer.Write(keys.Length);
            foreach (var key in keys)
            {
                var bytes = Encoding.UTF8.GetBytes(key);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
        }
        return ms.ToArray();
    }

    private static (int FormatVersion, string[] Keys) OldParseManifest(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var reader = new BinaryReader(ms, Encoding.UTF8);
        var formatVersion = reader.ReadInt32();
        var count = reader.ReadInt32();
        if (count < 0) throw new InvalidDataException("Manifest key count is negative.");
        var keys = new string[count];
        for (var i = 0; i < count; i++)
        {
            var len = reader.ReadInt32();
            if (len < 0) throw new InvalidDataException("Manifest key length is negative.");
            var bytes = reader.ReadBytes(len);
            if (bytes.Length != len) throw new EndOfStreamException("Unexpected end of manifest while reading a key.");
            keys[i] = Encoding.UTF8.GetString(bytes);
        }
        return (formatVersion, keys);
    }
}
