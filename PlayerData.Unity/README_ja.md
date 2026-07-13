# PlayerData for Unity

[English](README.md) | [日本語](README_ja.md)

> [PlayerData](https://github.com/dreamingdog0529/PlayerData) の Unity 統合層: `Application.persistentDataPath` バックエンドとオートセーブ。

VContainer 連携は別パッケージ **[PlayerData.Unity.VContainer](../PlayerData.Unity.VContainer)** を入れてください。

## 必要条件

UPM は NuGet DLL を解決しません。[NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity) 等で、このパッケージ追加前に入れてください:

- `PlayerData.Core` 0.1.0+
- `MemoryPack` 1.21.4+（Core の推移依存）
- `System.Collections.Immutable`（Core の推移依存）

任意:

- [PlayerData.Unity.VContainer](../PlayerData.Unity.VContainer) + [VContainer](https://github.com/hadashiA/VContainer)
- R3 / VitalRouter / MessagePipe — 各 PlayerData アダプタ NuGet

## インストール

`Packages/manifest.json` に追加（URL はリポジトリに合わせて調整）:

```json
"com.dreamingdog0529.playerdata": "https://github.com/dreamingdog0529/PlayerData.git?path=PlayerData.Unity"
```

NuGetForUnity で `PlayerData.Core.dll` 等が asmdef の `precompiledReferences` から見える場所に入っていること。

## ランタイム API

### `UnitySaveBackend`

```csharp
var backend = UnitySaveBackend.Create();
await using var save = await GameSave.OpenAsync(backend);
```

### `PlayerDataAutoSave`

```csharp
var auto = gameObject.AddComponent<PlayerDataAutoSave>();
auto.IntervalSeconds = 30f;
auto.Bind(save);
```
