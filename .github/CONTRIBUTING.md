# PlayerData へのコントリビュートについて

参加してくださってありがとうございます。このプロジェクトは、次の形で進めています。

- 変更はすべてプルリクエスト経由で、スカッシュマージだけにします（1 つの PR につき 1 コミットです）
- PR のタイトルとコミットは Conventional Commits（`type: description`）にします
- すべてのコミットに DCO サインオフ（`git commit -s`）をお願いします
- リリースは Release Please で自動化し、push の前に Lefthook / Task / Git フックで確認します

参加されるときは [行動規範](CODE_OF_CONDUCT.md) を守ってください。困ったときは [SUPPORT.md](SUPPORT.md)、ロードマップは [ROADMAP.md](../ROADMAP.md) をご覧ください。

## 開発環境

必要なツールチェーンは [docs/development.md](../docs/development.md) にまとめています。任意のツールは [typos](https://github.com/crate-ci/typos)、[lefthook](https://github.com/evilmartians/lefthook)、[Task](https://taskfile.dev/) で、Git フックは `pwsh ./scripts/install-git-hooks.ps1` か `task setup` で入れられます。

```sh
dotnet build PlayerData.slnx -c Release
dotnet test PlayerData.slnx
```

```bash
task build
task test
task check
```

### プロジェクトの構成

```
src/       アプリケーション / ライブラリのソース
tests/     テスト一式
docs/      ドキュメント
scripts/   開発用のユーティリティや補助スクリプト
.github/   ワークフロー、Issue / PR のテンプレート、設定
```

## Issue やリクエスト、アイデア

[GitHub Issues](https://github.com/dreamingdog0529/PlayerData/issues) で、YAML フォームを使ってください。バグ報告、機能リクエスト、大きめの設計変更の提案を受け付けています。`bug`、`enhancement`、`proposal`、`needs-triage` といったラベルはフォーム側で付けられます。

## プルリクエストの作り方

1. Issue を検索して、必要なら先に Issue を開いてください。
2. フォーク / クローンして、内容を絞ったブランチを作ってください（フォークした場合は、PR を開く前に同期しておいてください）。
3. Conventional Commits と DCO を付けてコミットしてください。

   ```bash
   git commit -s -m "fix: correct off-by-one in parser"
   ```

4. push して、デフォルトブランチに向けた PR を開き、PR テンプレートへの記入をお願いします。
5. CI がすべて通っているか確認してください（ビルド / テスト、PR タイトル、スペルチェック、依存関係のレビューなどです）。

### コントリビュートのチェックリスト

- [ ] きれいでシンプルで、スタイルのそろったコードになっている
- [ ] Conventional Commits（`type: description`）になっていて、関連する Issue を番号で参照している
- [ ] すべてのコミットに DCO サインオフが付いている（`git commit -s`）
- [ ] テストが通る（`dotnet test PlayerData.slnx` / `task test`）
- [ ] 依存関係はできるだけ少なくし、Apache-2.0、BSD-3、MIT、ISC、MPL を優先する
- [ ] シークレットやビルド成果物（`bin/`、`obj/`、`dist/`、`build/`）が含まれていない

### コミットメッセージの形

```
<type>[optional scope]: <description>

[optional body]

Signed-off-by: Name <email>
```

**種類:** `feat`、`fix`、`docs`、`style`、`refactor`、`perf`、`test`、`build`、`ci`、`chore`、`revert`

バージョンへの影響は、`feat:` がマイナー、`fix:` やその他の多くがパッチ、`feat!:` / `fix!:` / `BREAKING CHANGE:` がメジャーです。`1.0.0` より前では、`feat:` でもマイナーが上がります（`bump-minor-pre-major`）。

スカッシュマージでは **PR のタイトル** がそのままデフォルトブランチ上のコミットになるので、形は `type(optional-scope): description` を守ってください（説明は必須で、できれば小文字から始めます。CI は `pr-title.yml` です）。

対象外のものもあります。ボットの PR（Dependabot の `chore(deps):`、Release Please の `chore(main): release X.Y.Z` など）、リリース用の件名（`Release 1.2.3` など）、マージコミットは、PR タイトルやローカルのフックのチェック対象外です。

### ローカルのフック

```powershell
pwsh ./scripts/install-git-hooks.ps1
# または: task setup
```

- **pre-commit**（lefthook）: ステージされた Markdown / YAML のスペルチェックをします
- **commit-msg**: Conventional Commits と DCO を確認します

## DCO

このプロジェクトでは [Developer Certificate of Origin](https://developercertificate.org/) を使っています。すべてのコミットに `-s` でサインオフをお願いします。メンテナーは [DCO-2](https://github.com/apps/dco-2) GitHub App を有効にすることもできます（`.github/dco.yml` をご覧ください）。

## 自動化とリリース

CI では、ビルドとテスト（`ci.yml`）、PR タイトル（`pr-title.yml`）、スペルチェック（`spellcheck.yml`）、依存関係のレビュー（`dependency-review.yml`）、OpenSSF Scorecard（`scorecard.yml`）などが動いています。ラベル付けや初めての方への歓迎メッセージ、リポジトリ設定の適用もワークフローで行っています。

リリースはメンテナーが次の流れで進めます。

1. Conventional Commits で PR をデフォルトブランチにマージします（スカッシュです）。
2. Release Please がリリース PR を開くか、更新します。
3. リリース PR をマージすると、タグと GitHub Release が作られます。

最初に一度だけ、Actions の読み書き権限と、Actions が PR を作れるようにする設定が必要です。任意で、`settings.yml` をすべて適用するための `SETTINGS_TOKEN`（リポジトリの PAT）や、PR での DCO チェックのための DCO-2 のインストールもあります。

## セキュリティ

脆弱性については、公開の Issue には書かないでください。[SECURITY.md](SECURITY.md) をご覧ください。

## ライセンス

コントリビュートしていただくことで、あなたのコントリビューションがプロジェクトの [MIT License](../LICENSE) でライセンスされることと、DCO のもとで提出する権利があなたにあることに同意していただいたものとします。
