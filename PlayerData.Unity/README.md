# PlayerData for Unity

[English](README.md) | [日本語](README_ja.md)

> Unity integration layer for [PlayerData](https://github.com/dreamingdog0529/PlayerData): `Application.persistentDataPath` backend and auto-save helper.

For VContainer registration helpers, install the separate package **[PlayerData.Unity.VContainer](../PlayerData.Unity.VContainer)**.

## Requirements

NuGet DLLs are not resolved by UPM; install them via [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity) (or equivalent) **before** adding this package:

- `PlayerData.Core` 0.1.0+
- `MemoryPack` 1.21.4+ (transitive of Core)
- `System.Collections.Immutable` (transitive of Core)

Optional add-ons:

- [PlayerData.Unity.VContainer](../PlayerData.Unity.VContainer) + [VContainer](https://github.com/hadashiA/VContainer)
- [R3](https://github.com/Cysharp/R3) / VitalRouter / MessagePipe — via their PlayerData adapter NuGet packages

## Install

Add to `Packages/manifest.json` (adjust path / git URL as needed):

```json
"com.dreamingdog0529.playerdata": "https://github.com/dreamingdog0529/PlayerData.git?path=PlayerData.Unity"
```

Ensure NuGetForUnity has copied `PlayerData.Core.dll` (and dependencies) into a folder referenced by the asmdef `precompiledReferences`.

## Runtime API

### `UnitySaveBackend`

```csharp
var backend = UnitySaveBackend.Create();                    // persistentDataPath/PlayerData
var slot1   = UnitySaveBackend.Create(slot: 1);             // .../PlayerData/slot_1
var custom  = UnitySaveBackend.Create("MyGame/Saves", 0);

await using var save = await GameSave.OpenAsync(backend);
```

### `PlayerDataAutoSave`

```csharp
// On a bootstrap GameObject:
var auto = gameObject.AddComponent<PlayerDataAutoSave>();
auto.IntervalSeconds = 30f;   // 0 = interval disabled
auto.CommitOnPause = true;
auto.CommitOnQuit = true;
auto.Bind(save);
```

Commits only when `IsDirty`. Concurrent commits are gated; failures are logged via `Debug.LogException`.

## Notes

- Core APIs (`IDoc` / `IBag` / `SuppressNotifications` / validators) live in `PlayerData.Core`; this package only adds Unity-specific hosting.
- `ValueTask` from `LoadAsync` / `CommitAsync` can be awaited directly, or converted with UniTask if you already depend on it.
