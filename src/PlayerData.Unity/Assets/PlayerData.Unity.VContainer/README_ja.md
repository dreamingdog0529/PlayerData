# PlayerData VContainer

[English](README.md) | 日本語

> [PlayerData.Unity](../PlayerData.Unity) 向けの任意 [VContainer](https://github.com/hadashiA/VContainer) 統合。

## 必要条件

- UPM: `com.dreamingdog0529.playerdata`（PlayerData.Unity）0.1.0+
- UPM: [VContainer](https://github.com/hadashiA/VContainer)（`jp.hadashikick.vcontainer`）
- NuGet（PlayerData.Unity と同じ）: `PlayerData.Core` 0.1.0+

## インストール

`Packages/manifest.json` に **両方** 追加（URL は環境に合わせて調整）:

```json
"com.dreamingdog0529.playerdata": "https://github.com/dreamingdog0529/PlayerData.git?path=src/PlayerData.Unity/Assets/PlayerData.Unity",
"com.dreamingdog0529.playerdata.vcontainer": "https://github.com/dreamingdog0529/PlayerData.git?path=src/PlayerData.Unity/Assets/PlayerData.Unity.VContainer",
"jp.hadashikick.vcontainer": "https://github.com/hadashiA/VContainer.git?path=VContainer/Assets/VContainer#1.16.8"
```

VContainer を使わない場合はこのパッケージを入れないでください（`PlayerData.Unity` のみで可）。

## 使い方

```csharp
using PlayerData.Unity;
using VContainer;

builder.RegisterPlayerDataSession<GameSave>();
```
