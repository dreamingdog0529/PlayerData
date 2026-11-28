# セキュリティを固めるために

サプライチェーンを守るための、メンテナー向けの運用ガイドです。CI/CD パイプラインや依存関係、AI エージェントまわりのツールを対象にしています。

PlayerData 自体の脆弱性の報告については、[SECURITY.md](../.github/SECURITY.md) をご覧ください。

## なぜ大切なのか

サプライチェーン攻撃は、開発者が当たり前に信頼している部品を狙います。狙われやすいのは、パッケージレジストリ（タイポスクワッティングや依存関係の混乱）、CI/CD パイプライン（侵害された GitHub Actions から強いシークレットが抜かれる）、AI エージェントのスキル / MCP サーバー（乗っ取りやプロンプトインジェクション）の 3 つです。

共通点は、名前やタグ、URL が気づかないうちに別のものを指すようになることです。ピン留めと監査、最小権限で、その暗黙の信頼を減らしていきます。

## 最初から入っているもの

| 対策 | 場所 |
|---------|-------|
| GitHub Actions をコミット SHA でピン留め | `.github/workflows/*.yml` |
| アクション / 依存関係の更新 PR の自動作成 | `.github/dependabot.yml` |
| PR での依存関係レビュー（`fail-on-severity: high`） | `.github/workflows/dependency-review.yml` |
| OpenSSF Scorecard による分析 | `.github/workflows/scorecard.yml` |
| シークレットスキャンと push 保護、Dependabot のセキュリティ更新 | `.github/settings.yml` |
| すべてのワークフローでの最小権限の `permissions:` | `.github/workflows/*.yml` |
| DCO サインオフと Conventional Commits（来歴の確認用） | `lefthook.yml`、`.githooks/` |

## 1. GitHub Actions をコミット SHA でピン留めする

タグ（`@v4`）やブランチは動かせてしまう参照で、所有者や攻撃者が指す先を変えられます。40 文字のコミット SHA は変わりません。アクションを足すときも、SHA でピン留めして末尾にバージョンのコメントを書いてください。

```yaml
- uses: actions/checkout@9c091bb21b7c1c1d1991bb908d89e4e9dddfe3e0 # v7
```

### タグから SHA を調べる

```bash
# 軽量タグ: object.sha がコミットで、type は "commit" です。
gh api repos/<owner>/<repo>/git/ref/tags/<tag> --jq '.object'

# 注釈付きタグ (type が "tag"): もとになるコミットまでたどってください。
gh api repos/<owner>/<repo>/git/tags/<tag-sha> --jq '.object.sha'
```

コミットする前に、その SHA が意図したタグを指しているかを必ず確認してください。

**注意 — アクションによっては、メジャーのラインがタグではなくブランチで配られていることがあります。** たとえば `actions/dependency-review-action@v5` は `refs/heads/v5` という動くブランチに解決されます。代わりに、変わらないリリースのコミット（`vX.Y.Z` タグの後ろにある SHA）をピン留めしてください。

SHA でピン留めすると上流の修正が自動では入らないので、Dependabot（`github-actions` は `.github/dependabot.yml` で有効）と組み合わせ、更新 PR はきちんと確認してください。既存のリポジトリをまとめて変換したいときは、StepSecurity の [secure-repo](https://github.com/step-security/secure-repo) が使えます。

## 2. ワークフローの権限は最小限に

すべてのワークフローで、必要な範囲に絞った `permissions:` を明示してください。1 つのジョブだけ強い権限が要るときは、ワークフロー全体ではなくジョブごとに引き上げます。デフォルトのトークンの範囲には頼らないでください。

```yaml
permissions:
  contents: read
```

## 3. 依存関係の監査

ソースとロックファイルが入ってきたら、次のことを行ってください。

1. 使うスタックに合わせて、`.github/dependabot.yml` で Dependabot のエコシステムを有効にします（npm、pip、gomod、cargo などの例がコメントで用意されています）。
2. CI で監査を実行します。

   ```yaml
   # Node.js
   - run: npm audit --audit-level=high

   # Python
   - run: pip install pip-audit && pip-audit -r requirements.txt
   ```

3. PR ではロックファイル（`package-lock.json` / `poetry.lock` / `go.sum` など）の予期しない変更を確認します。
4. インストール時に動くスクリプトは信用しすぎないでください。見慣れない npm パッケージは `npm install --ignore-scripts` で入れ、信頼できると判断してからビルドの手順を実行します。

依存関係レビューのワークフローは、依存関係グラフが有効なら、深刻度の高い勧告を持ち込む PR をすでに止めてくれます。

## 4. シークレットの管理とローテーション

- すべてのシークレットと、それが何を許可するのかを一覧で把握しておいてください（`gh secret list --repo <owner>/<repo>`）。
- 短命で範囲の狭い認証情報を優先してください。クラシック PAT より GitHub App トークンや細かく権限を絞った PAT を、長命なクラウドのキーより OIDC を選びます。
- 有効期限を設定するか定期的にローテーションし、使ったアクションが侵害されたおそれがある場合はすぐにローテーションしてください。
- シークレットをログに出力しないでください。ビルドログは攻撃者にも読まれうるものとして扱います。

補足です。任意の `SETTINGS_TOKEN`（`settings.yml` の適用用）は、設計上の制約でクラシック PAT になっています。対象を 1 つのリポジトリに絞って、他の認証情報と同じようにローテーションしてください。

## 5. AI エージェントのスキルや MCP サーバーを確かめる

サードパーティのスキルやプラグイン、MCP サーバーは依存関係と同じように扱ってください。スキルの説明文はモデルのプロンプトに直接入れられるので、悪意のあるものはエージェント自体を乗っ取ることがあります。入れる前に、次のことを確認してください。

- 配布元の所有者が実在して活動していること、アカウントが作られたばかりでないこと（`gh api users/<owner> --jq '{login,created_at,public_repos}'`）
- リポジトリの乗っ取りがないこと。URL でリダイレクトが起きる場合は、元の所有者が名前を変えたか削除して、別の人がその名前を取得した可能性があります（`curl -sI https://github.com/<owner>/<repo> | grep -i location`）
- ソースだけでなく、`name` や `description`、指示の中に隠れた命令や外部への通信がないこと
- 権限は最小限にし、MCP サーバーの接続先が意図したものか確かめること。できる場合は、署名や来歴のある配布元を、特定のリビジョンにピン留めして使うこと

## チェックリスト

**GitHub Actions / 依存関係**

- [ ] すべてのアクションが、バージョンのコメント付きでコミット SHA にピン留めされている
- [ ] Dependabot が `github-actions` と使うスタックの両方で有効になっている
- [ ] すべてのワークフローで、最小権限の `permissions:` が明示されている
- [ ] シークレットスキャンと push 保護が有効になっている
- [ ] 監査（`npm audit` / `pip-audit` など）が CI で動いていて、ロックファイルの変更が PR で確認されている

**シークレット / AI エージェント（使う場合）**

- [ ] シークレットの一覧があり、有効期限かローテーションの間隔が決まっている
- [ ] クラシック PAT ではなく、細かく権限を絞った PAT や GitHub App トークンが使われている
- [ ] ワークフローのログにシークレットが書かれていない
- [ ] サードパーティのスキル / MCP の配布元が採用前に確認され、必要な権限だけを求めている

## 参考資料

- [Security hardening for GitHub Actions](https://docs.github.com/en/actions/security-guides/security-hardening-for-github-actions)
- [Keeping your actions up to date with Dependabot](https://docs.github.com/en/code-security/dependabot/working-with-dependabot/keeping-your-actions-up-to-date-with-dependabot)
- [OpenSSF Scorecard](https://github.com/ossf/scorecard)
- [StepSecurity secure-repo](https://github.com/step-security/secure-repo)
