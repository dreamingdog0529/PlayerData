using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PlayerData.Unity
{
    // ISaveBackend rooted under Application.persistentDataPath.
    // Optional slot maps to {root}/slot_{n}/ via SlotSaveBackend.
    public sealed class UnitySaveBackend : ISaveBackend
    {
        private readonly ISaveBackend _inner;

        public UnitySaveBackend(string relativeFolder = "PlayerData", int? slot = null)
        {
            if (string.IsNullOrWhiteSpace(relativeFolder))
                throw new ArgumentException("relativeFolder must be non-empty.", nameof(relativeFolder));

            RootDirectory = Path.Combine(Application.persistentDataPath, relativeFolder);
            Slot = slot;
            _inner = slot is int s
                ? new SlotSaveBackend(RootDirectory, s)
                : new DirectorySaveBackend(RootDirectory);
        }

        public string RootDirectory { get; }

        public int? Slot { get; }

        public static UnitySaveBackend Create(string relativeFolder = "PlayerData", int? slot = null) =>
            new(relativeFolder, slot);

        public ValueTask<SaveBundle?> ReadAsync(CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(cancellationToken);

        public ValueTask WriteAsync(SaveBundle bundle, CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(bundle, cancellationToken);
    }
}
