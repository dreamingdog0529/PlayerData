using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PlayerData;

// Wraps DirectorySaveBackend under {root}/slot_{n}/ so multiple save slots share one root path.
public sealed class SlotSaveBackend : ISaveBackend
{
    private readonly DirectorySaveBackend _inner;

    public SlotSaveBackend(string rootDirectory, int slot)
    {
        if (rootDirectory is null) throw new ArgumentNullException(nameof(rootDirectory));
        if (slot < 0) throw new ArgumentOutOfRangeException(nameof(slot), slot, "Slot must be non-negative.");
        Slot = slot;
        var path = Path.Combine(rootDirectory, "slot_" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _inner = new DirectorySaveBackend(path);
    }

    public int Slot { get; }

    public ValueTask<SaveBundle?> ReadAsync(CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(cancellationToken);

    public ValueTask WriteAsync(SaveBundle bundle, CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(bundle, cancellationToken);
}
