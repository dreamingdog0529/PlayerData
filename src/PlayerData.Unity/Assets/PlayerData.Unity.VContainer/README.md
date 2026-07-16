# PlayerData VContainer

[English](README.md) | [日本語](README_ja.md)

> Optional [VContainer](https://github.com/hadashiA/VContainer) integration for [PlayerData.Unity](../PlayerData.Unity).

## Requirements

- UPM: `com.dreamingdog0529.playerdata` (PlayerData.Unity) 0.1.0+
- UPM: [VContainer](https://github.com/hadashiA/VContainer) (`jp.hadashikick.vcontainer`)
- NuGet (via NuGetForUnity, same as PlayerData.Unity): `PlayerData.Core` 0.1.0+

## Install

Add **both** packages to `Packages/manifest.json` (adjust URLs):

```json
"com.dreamingdog0529.playerdata": "https://github.com/dreamingdog0529/PlayerData.git?path=src/PlayerData.Unity/Assets/PlayerData.Unity",
"com.dreamingdog0529.playerdata.vcontainer": "https://github.com/dreamingdog0529/PlayerData.git?path=src/PlayerData.Unity/Assets/PlayerData.Unity.VContainer",
"jp.hadashikick.vcontainer": "https://github.com/hadashiA/VContainer.git?path=VContainer/Assets/VContainer#1.16.8"
```

If you do not use VContainer, install only `PlayerData.Unity` — do not add this package.

## Usage

```csharp
using PlayerData.Unity;
using VContainer;

// In LifetimeScope.Configure:
builder.RegisterPlayerDataSession<GameSave>(relativeFolder: "PlayerData", slot: 0);
// Registers ISaveBackend (UnitySaveBackend) + GameSave singleton, then LoadAsync on IAsyncStartable
```
