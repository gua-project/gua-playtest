# Issue #3 基盤の検証境界

対象: BOUND-001 / PACK-001 / PACK-002 / DELIVERY-002、協力 FIX-001 / DELIVERY-001。元 Issue #3、main `96b72d23a4203a8051db296e0cd507f6cd688ec8` の docs-only 状態から独立 clone/branch で実装する。Gua 自体は変更しない。

| AT | 実装・独立期待値 | 証拠 / 残件 |
| --- | --- | --- |
| AT-BOUND-001 | 五つの product project、Core は package 依存なし、Runner は Core のみ、GuaIntegration は公開 Gua 1.1.1。外部 ProjectReference/source import を拒否 | `scripts/check-boundaries.ps1`、csproj/lock。Gua 1.1.1 source `88f5dca4aa97c5d5187ab66ea4416377f3affc96` distribution contract を read-only 確認。Gua→Playtest を追加しない |
| AT-PACK-001 | CLI と library が同じ ValidationRunner/validator を使用。不正 selector `id:42` は exit2、正常 `id:{value:ready}` は exit0 | `CliAndLibraryShareRealPackagedValidator`。採点/Run 終端規則は #4–#6、CLI の全規則は #15。Planner 認証を配置しない |
| AT-PACK-002 | `../gua` なしで restore/build/test、package-local `$ref`、native 削除後も validate、Trace は Gua API のみ | `PackageLocalReferencesRejectInvalidNestedValue`、`GuaTraceIsPackageOnlyAndRedactsBeforeSaving`、archive/tool smoke。実 Run の Trace 接続は #9 |
| AT-DELIVERY-002 | provider fault が実発火、事前/途中 cancellation、秘密文言/パスを出さない、1 MiB/UTF-8 拒否、Trace redaction/既存 HTML 非上書き、owned cleanup Fake | tests と smoke JSON/TRX。native あり/なしを別 process で実行して不足を実検出。実 host 所有権/cleanup は #6–#8 |
| AT-FIX-001（協力） | Fake は tests のみ。public managed/native package の証拠を Fake と区別する | 本 PR は型/境界と package 層。実 bridge/入力は #7/#16、Godot/Unity と同成果物 E2E は #18。未実行を成功にしない |
| AT-DELIVERY-001（協力） | 契約/基盤を並行開始し、接続先のコード所有を分離する | #2 は model/schema、#3 は projects/props/CLI/CI。#2 が `IStaticValidator` を実装して ValidationRunner に接続。最終配布は #18 |

## #2 の接続点

`src/Gua.Playtest.Core` は namespace `Gua.Playtest.Core`。`IStaticValidator.ValidateAsync(string document, CancellationToken)` → `ValueTask<ValidationResult>`。status は Valid/Invalid/Unavailable/Interrupted、code は秘密値を含まない固定診断識別子。Scenario/path/reference の正式 model をここで仮定しない。許可 root 等の設定は validator の constructor で受け取れる。

`src/Gua.Playtest.Runner/ValidationRunner.cs` は provider 例外を Unavailable へ分離し、明示 cancellation を Interrupted へする。CLI はこの結果を表示する。#2 統合時に Scenario validator の組立と roots を CLI へ追加し、project/依存 graph 検査へ schema-only package を反映する。推奨 fixture は `fixtures/contracts/`、test project は `tests/Gua.Playtest.Contracts.Tests/`。

clock/observation/planner/host ports は generic payload の抽象境界のみ。Goal/Value/Decision/result wire format、状態機械、予算、採点、App Server protocol/隔離を確定しない。`CodexPlanner` は未実装を明示して throw し、成功 Decision を捏造しない。

## OPEN と未実行範囲

本 PR は OPEN の Scenario fields/採点を解決しない。OPEN-11 の package 依存は公開 Gua 1.1.1/上記 commit へ固定するが、engine/他経路の対応表の確定は #18。OPEN-12 の foundation namespace と ports は本 PR の接続点として示す。正式 schema/名称は #2、正式 CLI/result は #15。

Scenario validator・実 game Run・bridge・Replay・Codex・browser QA・ライセンス閉包・全 E2E は本 PR の合格証拠に含めない。統合 acceptance が残る間は #3 を自動 close しない。全 issue 完了は親 #1 と接続先の証拠で判定する。

## 実行記録

local: Windows x64 / .NET SDK 10.0.401、公開 Gua 1.1.1。`dotnet test --no-restore -c Release --logger 'trx;LogFileName=foundation.trx' --results-directory artifacts/tests` は 15 passed / 0 failed / 0 skipped。`scripts/check-boundaries.ps1` は五 project の固定依存を検査する。PR の最終 HEAD と real CI/review の結果は PR 本文で保持する。

CI: 四 RID の job を PR/push で実行し、各 job が build/tests、archive extraction、Tool install、正常/不正 schema、native 実 load/identity、不足の実発火を必須にする。`artifacts/tests/*.trx`、`artifacts/smoke-*/evidence.json`（head/SDK/package commit/archive SHA256/実 exit）、zip/nupkg/locks を upload する。CI 未完了・review 無応答は merge 成功ではない。
