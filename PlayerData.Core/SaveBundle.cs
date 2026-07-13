using System;
using System.Collections.Generic;

namespace PlayerData;

public sealed class SaveBundle
{
    public SaveBundle(int formatVersion, IReadOnlyDictionary<string, byte[]> documents)
    {
        if (formatVersion < 0) throw new ArgumentOutOfRangeException(nameof(formatVersion));
        FormatVersion = formatVersion;
        Documents = documents ?? throw new ArgumentNullException(nameof(documents));
    }

    public int FormatVersion { get; }

    public IReadOnlyDictionary<string, byte[]> Documents { get; }
}
