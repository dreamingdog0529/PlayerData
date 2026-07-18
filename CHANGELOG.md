# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

This file is maintained automatically by [Release Please](https://github.com/googleapis/release-please)
from Conventional Commits. Add user-facing notes under `[Unreleased]` if you want to
call something out before the next release.

## [0.1.2](https://github.com/dreamingdog0529/PlayerData/compare/v0.1.1...v0.1.2) (2026-07-18)


### Features

* add CompressedSaveBackend (Deflate wrapper) ([d99d8a8](https://github.com/dreamingdog0529/PlayerData/commit/d99d8a8cb0792ae5d6d29fd933b01a6813a902cb))
* **core:** add DocumentFilePath and bump packages to 0.1.1 ([781d142](https://github.com/dreamingdog0529/PlayerData/commit/781d1422fe19c6df5a7f14b5ccf502325a4aeee7))
* **unity:** overhaul Data Viewer UI/UX with status colors and row actions ([7745478](https://github.com/dreamingdog0529/PlayerData/commit/7745478ee9ed993feae953bc871001b63a8f16c6))
* **unity:** VContainer統合をPlayerData.Unity内に同梱し導入時に自動有効化 ([eaf23b4](https://github.com/dreamingdog0529/PlayerData/commit/eaf23b44d67a2a3e3d3a1f57ae07da10f3dd885d))
* **unity:** エディタ用セーブデータビューアを追加 ([4fb4c69](https://github.com/dreamingdog0529/PlayerData/commit/4fb4c69cda656efb11d0ab5fedc32a3514623c70))
* **unity:** サンプル/QAフィクスチャ用データアセット(PlayerDataDocumentAsset)を追加し0.5.0に更新 ([cf33c22](https://github.com/dreamingdog0529/PlayerData/commit/cf33c22292f0292deab8a5c1fec0200dc544eb92))
* **unity:** データビューアに再生中セッションのライブ閲覧・編集モードを追加 ([92c68f4](https://github.com/dreamingdog0529/PlayerData/commit/92c68f4d324cce575f3ac78c412e02ab014da6ce))
* **unity:** データビューアに型認識のFieldsタブ編集を追加 ([4fd9ffa](https://github.com/dreamingdog0529/PlayerData/commit/4fd9ffa9c7d562562f0c4b81de4dcde1ab563416))
* **unity:** データビューアに検索フィルタ・変更インジケータ・Open Folderを追加 ([044e36a](https://github.com/dreamingdog0529/PlayerData/commit/044e36a99b23c6543fdb74029a0300d4dc423781))
* **unity:** テスト用セッションをビューアのセッション選択から除外 ([5abb813](https://github.com/dreamingdog0529/PlayerData/commit/5abb81334b6efe6c9a17579f4c73703d3a987fc0))
* **unity:** ビューアでコレクション文書のフィールド表示に対応 ([bacaa0a](https://github.com/dreamingdog0529/PlayerData/commit/bacaa0aec0c19ea86e72913695985fb28b23964c))
* **unity:** ビューアのコレクションFieldsで要素の追加/削除に対応 ([6f75051](https://github.com/dreamingdog0529/PlayerData/commit/6f75051ecd292078c5d146e3c7e202bf9b5c8c83))
* **unity:** ビューアを非エンジニア向けUXに改善(平易ラベル・段階的開示・FieldsテンプレートAdd Entry) ([67f6e1f](https://github.com/dreamingdog0529/PlayerData/commit/67f6e1fbf7132a056289dc3058c78005812909f8))
* **unity:** ビューア刷新ST-1 セーブツリーモデルをUI非依存で追加 ([ae23321](https://github.com/dreamingdog0529/PlayerData/commit/ae23321a741bf7ab49651c8f249b866275c9a974))
* **unity:** ビューア刷新ST-2 2ペインスケルトンへ書き換え(旧段階フロー削除) ([793bb9e](https://github.com/dreamingdog0529/PlayerData/commit/793bb9e04ac8a1f3878a5bd287b699bf545b8587))
* **unity:** ビューア刷新ST-4 ライブセッションをPlaying nowグループとしてツリー統合 ([6db2cd9](https://github.com/dreamingdog0529/PlayerData/commit/6db2cd944283ac8719242fe11185b5ee738fda4c))
* **unity:** ビューア刷新ST-7 ルート選択を場所ドロップダウン化 ([084fe21](https://github.com/dreamingdog0529/PlayerData/commit/084fe219e46462ecead42cb98ca7fa1f6fd3d61f))
* **unity:** ビューア詳細ペインの操作性を改善(Refresh選択維持/Apply上部/変更時のみ活性) ([be44b19](https://github.com/dreamingdog0529/PlayerData/commit/be44b19db9e0b9466ea5602637808e7662251933))
* **unity:** ライブセッションの列挙とJSON読み書きを担うLiveSessionViewを追加 ([c16b606](https://github.com/dreamingdog0529/PlayerData/commit/c16b60635981b2846ffba8a6b14ad314d7fbf401))
* **unity:** ライブセッションをエディタツールへ公開するLiveSessionRegistryを追加 ([388810a](https://github.com/dreamingdog0529/PlayerData/commit/388810af7c3a92ac40f5b2f9ca22e9f82a3bf345))


### Bug Fixes

* **core:** clear pooled compression buffers and fix empty-path comment ([ba89740](https://github.com/dreamingdog0529/PlayerData/commit/ba897404096620c1f510a6c9361c7b3b9b65f3b6))
* **core:** escape path separators portably in DirectorySaveBackend ([9b655eb](https://github.com/dreamingdog0529/PlayerData/commit/9b655ebaf0387ea430e6d9e20eeadeafdce902a0))
* **unity:** NuGet ソースジェネレータの Microsoft.CodeAnalysis 参照検証エラーを抑止 ([6eacacf](https://github.com/dreamingdog0529/PlayerData/commit/6eacacf790d89585218d08f1e3a0c3f8e7643676))
* **unity:** RuntimeコードをUnityのC#バージョンで通るnamespace構文に修正 ([4afc8f4](https://github.com/dreamingdog0529/PlayerData/commit/4afc8f46cba2cc6dce3db2454f88b9364180e623))
* **unity:** ビューア左ペインが幅0に潰れる問題を修正しスプリッタを自前実装へ置換 ([31780bf](https://github.com/dreamingdog0529/PlayerData/commit/31780bf8658055e10dfcd760da38a6797642a32a))


### Performance Improvements

* **core:** default CompressedSaveBackend to Fastest compression ([2b24e6d](https://github.com/dreamingdog0529/PlayerData/commit/2b24e6d528e287495f7424c017aba18ac714e61e))
* **core:** DirectorySaveBackendのI/O並列化とToFileName高速化 ([dd6f73d](https://github.com/dreamingdog0529/PlayerData/commit/dd6f73df2297bd17e965c8b0c810a8b9ab99b85c))
* **core:** DocumentStore/KeyedDocumentStoreのIsDirty読み取りを局所変数化 ([897e2b7](https://github.com/dreamingdog0529/PlayerData/commit/897e2b7c415004845cc3e8b8968ae1f35462aed4))
* **core:** DocumentStore/KeyedDocumentStoreの抑制状態問い合わせを1回に統合 ([a292925](https://github.com/dreamingdog0529/PlayerData/commit/a2929251031f9f8d2bfcd098a1b465850f6a9ee8))
* **core:** EncryptedSaveBackendのMAC/暗号化バッファを直接書込み方式に変更 ([4fd78cc](https://github.com/dreamingdog0529/PlayerData/commit/4fd78cceec24f3b169b7f371b63f97b0a24c9f23))
* **core:** KeyedDocumentStore.SetCoreの二重ディクショナリ参照を排除 ([ba9b5ab](https://github.com/dreamingdog0529/PlayerData/commit/ba9b5abf59e805284642df70590216854ffbecc4))
* **core:** ObfuscatedSaveBackend.TransformをVector&lt;byte&gt;によるSIMD化 ([8d820a2](https://github.com/dreamingdog0529/PlayerData/commit/8d820a2cd644be7cbb9612bcb139329d1897c73c))
* **core:** pool compression output buffer in CompressedSaveBackend ([b095b48](https://github.com/dreamingdog0529/PlayerData/commit/b095b487bcc1eff4d827d4b68aebe1276613be4d))
* **core:** ReplaceFromLoadを辞書の参照スワップに置換 ([0b88243](https://github.com/dreamingdog0529/PlayerData/commit/0b88243f6587c8c201c39f348bd613b6e1a7c1c7))
* **core:** SaveSessionの参加者/バリデータ配列生成をバージョンキャッシュ化 ([8ac3c18](https://github.com/dreamingdog0529/PlayerData/commit/8ac3c18ede337512dc679f4501f1e753a222d349))
* **core:** SaveSession参加者をvolatile配列化しIsDirtyをロックフリー化 ([56abe3e](https://github.com/dreamingdog0529/PlayerData/commit/56abe3e7a19d705ed5b9970b7e24890661e35145))
* **core:** SaveSession参加者をvolatile配列化しIsDirtyをロックフリー化 ([3629b53](https://github.com/dreamingdog0529/PlayerData/commit/3629b53809727386e7559d9ecdff3d64b058f5ee))
* **core:** zero-allocation suppress scope and scratch recycling ([d3bb59d](https://github.com/dreamingdog0529/PlayerData/commit/d3bb59db03d537ec86ddf68f081c7dc23525d61b))
* **core:** クリーン時CommitAsyncとsuppress経路を高速化、SetCore変更を計測に基づき差し戻し ([d7afbcf](https://github.com/dreamingdog0529/PlayerData/commit/d7afbcf19b7979ab0fb8078a219aec2a2f196d8f))
* **core:** 暗号プリミティブをインスタンスキャッシュ化 ([1a5e590](https://github.com/dreamingdog0529/PlayerData/commit/1a5e590ba7a7e3f047114f07ad29eb18a57e0760))
* **core:** 書き込みホットパスのアロケーションと通知コストを削減 ([65064e4](https://github.com/dreamingdog0529/PlayerData/commit/65064e410ddc3bdf6479bcbd8cd4e6bc722f1532))
* **messagepipe,vitalrouter:** 購読をトークンclassに置き換えデリゲート割当を削減 ([4dd6411](https://github.com/dreamingdog0529/PlayerData/commit/4dd6411529d32f5e2809e0ca0f6baf8661f740a8))
* **r3:** 購読をトークンclassに置き換えデリゲート割当を削減 ([93eb5a9](https://github.com/dreamingdog0529/PlayerData/commit/93eb5a96656b6d5a072a7a62707a704b9fdfe2d9))


### Documentation

* README言語切替で現在表示中の言語をリンクにしないよう修正 ([9f6cf7a](https://github.com/dreamingdog0529/PlayerData/commit/9f6cf7a4acdad856fdc347594c8212a52e0b0fc6))
* relocate community health files to .github/ ([61b3ef4](https://github.com/dreamingdog0529/PlayerData/commit/61b3ef415a1b9e2b3b0db75c8cc9943a152a08c1))
* trim acknowledgments to core dependencies ([0b8b05f](https://github.com/dreamingdog0529/PlayerData/commit/0b8b05ff7069491fcbc84d583488cad8422be59f))
* VContainerパッケージのREADMEをルートREADMEに統合 ([1ca3f65](https://github.com/dreamingdog0529/PlayerData/commit/1ca3f65b97455c67e37b0014ec5fd7af7cca2f72))
* セーブデータビューアの2ペイン刷新をREADMEに反映し0.6.0に更新 ([8f4bf44](https://github.com/dreamingdog0529/PlayerData/commit/8f4bf44be12717c65ed151d321cee472be3c7913))
* セーブデータビューアのライブ編集・Fieldsタブ・検索フィルタを追記し0.3.0に更新 ([5b4aaa1](https://github.com/dreamingdog0529/PlayerData/commit/5b4aaa1ebbc2ca384359aee7bc5837e915ac5bf8))


### Miscellaneous

* adopt shared repository template structure ([1e88984](https://github.com/dreamingdog0529/PlayerData/commit/1e8898487b6991de3ed4557c95299516ef9e5735))
* adopt shared repository template structure ([035eaa3](https://github.com/dreamingdog0529/PlayerData/commit/035eaa387ea8956fe26c9ebc8a8c7c220021e9c1))
* **deps:** bump actions/setup-dotnet from 4.3.1 to 6.0.0 ([#3](https://github.com/dreamingdog0529/PlayerData/issues/3)) ([8d04c10](https://github.com/dreamingdog0529/PlayerData/commit/8d04c10a4d07c238e1a79f73aece87f501020ffa))
* **deps:** bump actions/upload-artifact from 4.6.2 to 7.0.1 ([#4](https://github.com/dreamingdog0529/PlayerData/issues/4)) ([dbe3c58](https://github.com/dreamingdog0529/PlayerData/commit/dbe3c580c6fbf87b4356a81255957fc7fc62dec9))
* retarget tests and benchmarks from net9.0 to net10.0 ([5c66f9c](https://github.com/dreamingdog0529/PlayerData/commit/5c66f9cec9bbfd4d04f55d01e67785889d673ee8))
* **unity:** PlayerDataトップレベルメニューを削除 ([756c381](https://github.com/dreamingdog0529/PlayerData/commit/756c3810dd62c698a8e711241024043f343b0587))
* **unity:** RoslynAnalyzerImportFixer.cs に UTF-8 BOM を付与 ([5dcc373](https://github.com/dreamingdog0529/PlayerData/commit/5dcc3730ee7fef8df6331b87a20edf06cc721081))
* **unity:** set up NuGet-based package restore and editor test scaffold ([5ade476](https://github.com/dreamingdog0529/PlayerData/commit/5ade4760a6c2a9ea525b5e6d70c5e654f4bad7f9))
* **unity:** エディタ起動で生成されたパッケージ設定スタブを追加 ([ebaf91f](https://github.com/dreamingdog0529/PlayerData/commit/ebaf91f518eb1bd5ac17fd3ee11d8a2d4a2ba37a))
* **unity:** サンドボックスをAssets/Demoへ移設しデモ用の命名に統一 ([ce268e1](https://github.com/dreamingdog0529/PlayerData/commit/ce268e13ff3465932a9b80cca3dffefff8db4879))
* **unity:** サンプルセーブ削除メニューを追加 ([ca729c2](https://github.com/dreamingdog0529/PlayerData/commit/ca729c2ace82293cb52959e235bd0cb6f7d0d839))
* **unity:** デモシーンのGameObject名をDemoに変更 ([668f1da](https://github.com/dreamingdog0529/PlayerData/commit/668f1da08819767ffb1cc857f47a2f7cd157f409))
* **unity:** ビューア刷新ST-5 旧フロー残骸の掃除 ([5d3739c](https://github.com/dreamingdog0529/PlayerData/commit/5d3739cf5525123dc01b80e7eb678a00c9fd5e29))
* **unity:** ライブ編集の手動確認用サンドボックスを追加 ([b9d3c0a](https://github.com/dreamingdog0529/PlayerData/commit/b9d3c0aad84f2b638b42d9383499a2b17c1d936c))
* プロジェクト構成をsrc/tests/sandboxレイアウトに変更 ([fe2a365](https://github.com/dreamingdog0529/PlayerData/commit/fe2a36510a0b310f2de022c2d0ee83b3500b9bfe))
* 初回コミット (v0.1.0) ([8b01416](https://github.com/dreamingdog0529/PlayerData/commit/8b014164d228dc263cf5aaa99f2cf8a2a349c8b0))
* 計画ファイル配置ディレクトリをgit管理対象外にする ([65c94e8](https://github.com/dreamingdog0529/PlayerData/commit/65c94e8ba003bb9a7dd5920854d99261d3ea6e43))


### Code Refactoring

* move PlayerData.Unity.VContainer under Assets/External ([5336773](https://github.com/dreamingdog0529/PlayerData/commit/5336773a19ee0b6894c8eb42e5150773b3d62d69))


### Tests

* **bench:** MessagePipe/R3/VitalRouterアダプター向けベンチマークを追加 ([d33a8db](https://github.com/dreamingdog0529/PlayerData/commit/d33a8db15b6c8af39754aa506cd0d08631d26323))
* expand coverage for session, backends, adapters, and generator ([ba7f329](https://github.com/dreamingdog0529/PlayerData/commit/ba7f32908c5aac7b8058553f75a30ad694aa6820))
* 無効ディレクトリのコミット失敗テストをOS間の例外型差異に対応 ([e623f07](https://github.com/dreamingdog0529/PlayerData/commit/e623f07288b379a5a46bafb9aa1ce7e6663cf626))

## [Unreleased]
