using System.IO;
using System.Text;
using BenchmarkDotNet.Attributes;

namespace PlayerData.Benchmarks;

// Compares the current DirectorySaveBackend.BuildManifest/ParseManifest (fixed pointer +
// BinaryPrimitives, see PlayerData.Core, ST-2 of plan 202607131429-playerdata-extreme-perf) against
// a frozen copy of the pre-optimization implementation (MemoryStream + BinaryWriter/BinaryReader,
// taken verbatim from commit e982877). The old code is kept here only as a benchmark baseline, not
// as production code - it is dead outside this comparison.
[MemoryDiagnoser]
[SimpleJob(launchCount: 3, warmupCount: 3, iterationCount: 5)]
public class DirectorySaveBackendBenchmarks
{
    // Representative of a typical SaveSession: a handful of registered documents/collections
    // (not per-item collection entries - those live inside each document's own MemoryPack bytes).
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

    // --- Frozen baseline, copied verbatim from PlayerData.Core/DirectorySaveBackend.cs @ e982877 ---

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
