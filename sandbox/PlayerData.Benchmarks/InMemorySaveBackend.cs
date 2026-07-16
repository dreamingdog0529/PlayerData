using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayerData.Benchmarks;

// Isolates SaveSession benchmarks from disk I/O so measured time/allocations reflect only
// SaveSession's own commit/load logic, while honoring ISaveBackend's ownership contract:
// WriteAsync copies the dictionary it retains (the caller recycles the bundle's dictionary
// right after the call) and ReadAsync hands out a deep copy (ownership of the returned bundle
// transfers to the caller, who may mutate its arrays in place).
internal sealed class InMemorySaveBackend : ISaveBackend
{
    private SaveBundle? _bundle;

    public ValueTask<SaveBundle?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_bundle is null) return new((SaveBundle?)null);

        // byte[] contents are cloned too: a caller (e.g. an obfuscating wrapper) may transform
        // the returned arrays in place, which must not corrupt the stored copy.
        var documents = new Dictionary<string, byte[]>(_bundle.Documents.Count);
        foreach (var pair in _bundle.Documents)
        {
            var copy = new byte[pair.Value.Length];
            Buffer.BlockCopy(pair.Value, 0, copy, 0, pair.Value.Length);
            documents[pair.Key] = copy;
        }
        return new ValueTask<SaveBundle?>(new SaveBundle(_bundle.FormatVersion, documents));
    }

    public ValueTask WriteAsync(SaveBundle bundle, CancellationToken cancellationToken = default)
    {
        // Only the dictionary needs copying: the known writers (SaveSession, the wrapper
        // backends) recycle their dictionary between calls but never mutate the byte[] payloads
        // they handed over, so sharing the arrays keeps write benchmarks noise-free.
        var documents = new Dictionary<string, byte[]>(bundle.Documents.Count);
        foreach (var pair in bundle.Documents)
            documents[pair.Key] = pair.Value;
        _bundle = new SaveBundle(bundle.FormatVersion, documents);
        return default;
    }
}
