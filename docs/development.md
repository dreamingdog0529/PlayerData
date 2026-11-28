# PlayerData の開発について

## 用意していただきたいもの

- .NET SDK 9.0 以上
- Unity 6000.0 以上（`src/PlayerData.Unity` を触るときだけ。任意です）
- [NuGetForUnity CLI](https://github.com/GlitchEnzo/NuGetForUnity)（Unity プロジェクトを触るときだけ）

あると便利なツールです（CI やローカルのフックで使っています）。

- [typos](https://github.com/crate-ci/typos) — スペルチェック用です
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
task check   # スペルチェック + コミットメッセージのチェック + DCO チェック + ビルド + テスト
```

## Git フック

ローカルのフックを入れておくと、Conventional Commits や DCO のサインオフを忘れにくくなります。

```powershell
pwsh ./scripts/install-git-hooks.ps1
# または
task setup
```

- **pre-commit**: ステージされた Markdown / YAML のスペルチェックをします（lefthook 経由です）
- **commit-msg**: Conventional Commits と DCO の `Signed-off-by` が付いているか確認します

## コミットと PR の約束ごと

- [Conventional Commits](https://www.conventionalcommits.org/) で書いてください（`type: description` の形です）
- すべてのコミットにサインオフをお願いします（`git commit -s` です）
- マージはスカッシュマージだけです。PR のタイトルが、そのままデフォルトブランチ上のコミットになります

全体の流れは [CONTRIBUTING.md](../.github/CONTRIBUTING.md) にまとめています。

## リリース

リリースは [Release Please](https://github.com/googleapis/release-please) で自動化しています。Conventional Commits のコミットをデフォルトブランチにマージするとリリース PR が開くか更新され、それをマージするとタグと GitHub Release が作られます。そのあと `release-assets.yml` が、リリースしたバージョンで NuGet パッケージ（`PlayerData.Core`、`PlayerData.MessagePipe`、`PlayerData.R3`、`PlayerData.VitalRouter`）をパックして、`.nupkg` ファイルを GitHub Release に添付します。

NuGet.org にも公開したいときは、`NUGET_KEY` シークレットを追加して、`.github/workflows/release-assets.yml` の push ステップのコメントアウトを外してください。
