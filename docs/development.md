# PlayerData の開発について

## 用意していただきたいもの

- .NET SDK 9.0 以上
- Unity 6000.0 以上（`src/PlayerData.Unity` を触るときだけ。任意です）
- [NuGetForUnity CLI](https://github.com/GlitchEnzo/NuGetForUnity)（Unity プロジェクトを触るときだけ）

あると便利なツールです（ローカルのフックやタスクで使っています）。
- [lefthook](https://github.com/evilmartians/lefthook) — Git フック用です
- [Task](https://taskfile.dev/) — タスクランナーです

## ビルド・実行・テスト

```sh
dotnet build src/PlayerData.Core/PlayerData.Core.csproj -c Release  # Core をパックして ../.local-feed に置きます
dotnet build PlayerData.slnx -c Release
dotnet test PlayerData.slnx -c Release --no-build
```

`PlayerData.SourceGenerator.IntegrationTests` は、ローカルフィード（`../.local-feed`、`nuget.config` をご覧ください）にあるパック済みの `PlayerData.Core` の nupkg を使うので、ソリューション全体を復元する前に、まず Core を一度ビルドしておいてください。`task build` を使えばまとめて行われます。

```bash
task build
task test
task check   # コミットメッセージのチェック + ビルド + テスト
```

## Git フック

ローカルのフックを入れておくと、Conventional Commits の形を忘れにくくなります。

```powershell
pwsh ./scripts/install-git-hooks.ps1
# または
task setup
```

- **commit-msg**: Conventional Commits の形になっているか確認します

## コミットについて

- [Conventional Commits](https://www.conventionalcommits.org/) で書いてください（`type: description` の形です）。リリースの自動化がこの形を見ています
- 作業はそのまま main へ push して大丈夫です

## リリース

リリースは [Release Please](https://github.com/googleapis/release-please) で自動化しています。Conventional Commits のコミットをデフォルトブランチにマージするとリリース PR が開くか更新され、それをマージするとタグと GitHub Release が作られます。そのあと `release-assets.yml` が、リリースしたバージョンで NuGet パッケージ（`PlayerData.Core`、`PlayerData.MessagePipe`、`PlayerData.R3`、`PlayerData.VitalRouter`）をパックして、`.nupkg` ファイルを GitHub Release に添付します。

NuGet.org にも公開したいときは、`NUGET_KEY` シークレットを追加して、`.github/workflows/release-assets.yml` の push ステップのコメントアウトを外してください。
