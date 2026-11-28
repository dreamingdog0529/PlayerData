<a id="readme-top"></a>

<div align="center">

<h1>PlayerData</h1>

<p><em>.NET と Unity のための、セッションを単位にしたプレイヤーのセーブデータです。型付きの <code>IDoc</code> / <code>IBag</code> ドキュメントと <a href="https://github.com/Cysharp/MemoryPack">MemoryPack</a> による保存を使って、複数のドキュメントを 1 つのわかりやすいセーブの境界の中でまとめてコミットできます。</em></p>

[![CI](https://github.com/dreamingdog0529/PlayerData/actions/workflows/ci.yml/badge.svg)](https://github.com/dreamingdog0529/PlayerData/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/dreamingdog0529/PlayerData?include_prereleases&sort=semver)](https://github.com/dreamingdog0529/PlayerData/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![OpenSSF Scorecard](https://api.securityscorecards.dev/projects/github.com/dreamingdog0529/PlayerData/badge)](https://securityscorecards.dev/viewer/?uri=github.com/dreamingdog0529/PlayerData)

<p>
  <a href="docs/development.md"><strong>ドキュメントを見る »</strong></a>
  <br /><br />
  <a href="https://github.com/dreamingdog0529/PlayerData/issues/new?template=bug_report.yml">バグの報告</a>
  ·
  <a href="https://github.com/dreamingdog0529/PlayerData/issues/new?template=feature_request.yml">機能リクエスト</a>
  ·
  <a href="https://github.com/dreamingdog0529/PlayerData/discussions">ディスカッション</a>
</p>

</div>

> [!WARNING]
> **まだベータ版（0.x）です。** API やパッケージの表面、生成されるコードは、マイナー／パッチリリースのあいだでも **破壊的に変わる** ことがあります。本番で使うときはバージョンをぴったり固定して、1.0 までは移行の手間がかかることもあると思っておいてください。

## 目的

PlayerData は、プレイヤーが書き換えていく進行状況のような **プレイヤーのセーブデータを、型付きで安全に読み書きするためのライブラリ** です。読み取り専用のマスターテーブルは対象にしていません（そちらは [MasterSheet](https://github.com/dreamingdog0529/MasterSheet) のほうをご覧ください）。

セーブデータは、メモリの上ではこまめに書き換え、ディスクへはたまに、まとめて一貫した形で書きたいものです。PlayerData はそのために、役割を次のように分けています。

* **形は型が持ちます。** 単一の値を入れる `IDoc<T>` と、キー付きのコレクション `IBag<TKey,T>` でドキュメントを表します。
* **境界はセッションが持ちます。** ドキュメントは 1 つの `ISaveSession` にまとめ、読み込みもコミットもセッション単位で行います。
* **ディスク上のバイトはバックエンドが持ちます。** 保存先や暗号化などは `ISaveBackend` が担当するので、セッション側のコードにパスを直接書く必要はありません。

メモリの上での変更はまだ下書きの段階で、`CommitAsync` が検証をして、変更のあった（dirty な）ドキュメントだけを書き込みます。検証は書き込みの前に行うので、コミットがうまくいかなかったときも、直前のセーブはそのまま残ります。

```
セッション（例: GameSave）
├── Profile    → IDoc<T>     単一の値
├── Settings   → IDoc<T>
└── Inventory  → IBag<K,T>   キー付きコレクション
```

## 特徴

- **定型部分は生成におまかせです。** Roslyn のソースジェネレータが、属性から `OpenAsync` や型付きプロパティ、`ISaveSession` の実装を作ってくれます。
- **バックエンドは差し替えられます。** ディレクトリやスロット、Unity のパスのほか、暗号化／難読化／圧縮のラッパーも用意しています。
- **マイグレーションも用意しています。** ディスク上の形式のバージョンが古いときは、読み込みのときに順番に適用していきます。
- **Unity との連携もあります。** `UnitySaveBackend` や `PlayerDataAutoSave`、エディタの中で使える Data Viewer、任意の VContainer 対応が入っています。
- **リアクティブなアダプターは任意です。** R3／VitalRouter／MessagePipe は、それぞれ別のパッケージで用意しています。Core 本体は、依存をできるだけ軽く保つようにしています。

## 前提条件

| 項目 | 要件 |
| --- | --- |
| ライブラリ | .NET Standard 2.1 |
| MemoryPack | 1.21.4+ |
| C# | `partial` クラス |
| Unity（任意） | Unity 6+ の UPM です。Core は [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity) などで **先に** 入れてください |

## インストール

```bash
dotnet add package PlayerData.Core
# お好みで使うアダプター
dotnet add package PlayerData.R3
dotnet add package PlayerData.VitalRouter
dotnet add package PlayerData.MessagePipe
```

Unity の場合は、まず NuGetForUnity などで `PlayerData.Core`（と MemoryPack）を入れてから、git の URL で UPM パッケージを足してください。

```
https://github.com/dreamingdog0529/PlayerData.git?path=src/PlayerData.Unity/Assets/PlayerData.Unity
```

## クイックスタート

まず、ドキュメントの型に MemoryPack の指定を付けます。

```csharp
using MemoryPack;
using PlayerData;
using PlayerData.Persistence;
using PlayerData.Schema;

[MemoryPackable(GenerateType.VersionTolerant)]
public partial record PlayerProfile(
    [property: MemoryPackOrder(0)] int Level,
    [property: MemoryPackOrder(1)] string Name)
{
    public static PlayerProfile NewGame() => new(1, "Hero");
}

[MemoryPackable(GenerateType.VersionTolerant)]
public partial record InventoryItem(
    [property: MemoryPackOrder(0), PlayerDataKey] string ItemId,
    [property: MemoryPackOrder(1)] int Count);
```

* ドキュメントは、version-tolerant な class 型にしてください
* コレクションの要素には、`[PlayerDataKey]` を **ちょうど 1 つだけ** 付けてください

次に、セッションを宣言します。ドキュメントのプロパティは手で書かなくて大丈夫です。ソースジェネレータが作ってくれます。

```csharp
[PlayerDataSession]
[PlayerDataSingle(typeof(PlayerProfile), "Profile", Default = nameof(PlayerProfile.NewGame))]
[PlayerDataCollection(typeof(InventoryItem), "Inventory")]
public partial class GameSave { }
// → Profile: IDoc<PlayerProfile>, Inventory: IBag<string, InventoryItem>
```

あとは、開いて、書き換えて、コミットするだけです。

```csharp
await using var save = await GameSave.OpenAsync(new DirectorySaveBackend(path));

using (save.SuppressNotifications())
{
    save.Profile.Update(p => p with { Level = p.Level + 1 });
    save.Inventory.Upsert(new InventoryItem("potion", 1));
}

await save.CommitAsync();
```

| API | 挙動 |
| --- | --- |
| `OpenAsync` | 作ってから、`LoadAsync` を 1 回行います |
| `Update` など | メモリの上だけです。ファイルには触りません |
| `CommitAsync` | 検証してから書き込みます。うまくいかなかったときはディスクは変わらず、dirty のまま残ります |

使い方の要点は、これだけです。

- `Update` の updater は **純粋なもの** にしてください（CAS の都合で再実行されることがあります）。
- ストアの `Changed` はユーザーの書き込みのときだけ起きます。読み込みの完了は、セッションの `Loaded` のほうで見てください。
- 保存先を変えたいときは `ISaveBackend` を差し替えます。スロット保存の `SlotSaveBackend` や Unity 用の `UnitySaveBackend` のほか、暗号化／難読化／圧縮のラッパーも同じ形で重ねられます。
- 属性だけでは表しきれない場合は、`SaveSession` を直接使う手動セッションも使えます。

## Unity

対応している Unity の一番古いバージョンは 6000.0 です。保存先には `UnitySaveBackend`、定期的な保存には `PlayerDataAutoSave` が使えます。

```csharp
var backend = UnitySaveBackend.Create();
var slot1   = UnitySaveBackend.Create(slot: 1);

await using var save = await GameSave.OpenAsync(backend);

var auto = gameObject.AddComponent<PlayerDataAutoSave>();
auto.IntervalSeconds = 30f;
auto.CommitOnPause = auto.CommitOnQuit = true;
auto.Bind(save); // dirty のときだけです。同時にコミットが重ならないようにしています
```

セーブの中身は、エディタの `Window > PlayerData > Data Viewer` から見たり編集したりできます。VContainer との連携も `PlayerData.Unity` の中に入っていて、VContainer が入っているときに自動で有効になります。

## 詳しいドキュメント

README では入口だけに絞っています。詳しいことは、こちらをご覧ください。

| 文書 | 内容 |
|------|------|
| [docs/development.md](docs/development.md) | 開発・ビルド・テストの手順 |
| [CONTRIBUTING.md](.github/CONTRIBUTING.md) | コントリビュートの進め方 |
| [ROADMAP.md](ROADMAP.md) | これからの方向性 |
| [CHANGELOG.md](CHANGELOG.md) | 変更履歴 |
| [SECURITY.md](.github/SECURITY.md) | 脆弱性を、公開せずに知らせる方法 |

そのほかの詳しい仕様（各バックエンドの変換の内容、ソースジェネレータの診断コード、Data Viewer の編集ルールなど）は、ソースコードと上記のドキュメントを参照してください。

## コントリビュート

コントリビュートは、いつでも歓迎しています。進め方については **[CONTRIBUTING.md](.github/CONTRIBUTING.md)** を、コミュニティの決まりごとについては [行動規範](.github/CODE_OF_CONDUCT.md) をご覧ください。

## ライセンス

MIT ライセンスのもとで配布しています。詳しくは [LICENSE](LICENSE) をご覧ください。

MIT © 2026 dreamingdog0529

## 謝辞

- [MemoryPack](https://github.com/Cysharp/MemoryPack) — バージョンの違いに強いバイナリシリアライズです
- [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity) — Unity で NuGet パッケージを使えるようにしてくれます
