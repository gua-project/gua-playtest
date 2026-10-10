# Gua Playtest を読む開発者のためのガイド

Gua Playtest は、ゲームの外で動き、公開された操作を使って目的の達成を確かめる C#/.NET の Runner です。Gua が提供する観測・入力・Recording・Trace を使い、Playtest が目的、条件判定、実行期限、操作予算、結果を管理します。ゲーム内部の関数を直接呼んで所持品を増やすような経路は、プレイによる達成の検証になりません。

このガイドは全体の流れとコードへの入口を説明します。正確なフィールド、境界時刻、終了理由の優先順位は各契約書を参照してください。[docs の索引](README.md)から、現在の契約と起票当時の資料を読み分けられます。

## まず「一度購入する」を追う

例えば「店で品物を一つ購入する」を試す場合、次の役割を分けます。これは仕組みを説明する例であり、完成したゲーム実行CLIの例ではありません。

1. **Scenario** に目的を記述します。人間や Planner に伝える `goal.objective` と、機械が判定する `goal.success` は別です。購入の合否には、信頼できる観測で購入数が増えたことなど、ゲームに対応した条件が必要です。
2. **Environment** に実行条件を置きます。接続先、許可する入力、fixture、時計、有限の予算などです。fixture は承認された開始状態の準備で、Planner が任意のコードを実行する入口にはしません。
3. **静的検証** で形、型、明示参照、ファイルの SHA-256 を調べます。文書が正しくても、店のボタンが存在することやゲームが起動できることはまだ分かりません。
4. **Preparing** で接続・ゲームの識別・開始状態・観測の同期を確認します。launch なら自分が起動したプロセスを管理し、attach なら他者のプロセスを所有したことにはしません。
5. **Running** では観測と判定を続けながら入力します。Planner は公開観測から次の操作を提案します。Runner は古い提案、権限不足、予算超過を拒否し、送信直前にも現在の対象と許可を確認します。
6. **入力の完了と Goal を別々に確認** します。ボタン操作の応答だけでは購入成功とは限りません。反対に、応答が失われたからといって同じ購入を再送すると二重購入の恐れがあります。送信後不明を「未送信」に戻すことはできません。
7. **結果を確定し、後処理と保存を行います。** 合否を一度確定した後、入力の解放、接続・所有プロセスの終了、必須成果物の保存を別に記録します。Goal が成立しても、必須後処理が未完了なら通常の成功終了にはなりません。

Explore は次の操作を提案しながら進め、Replay は固定された Recording を順番どおりに再生する設計です。Replay が途中で別の経路を考えて Plan を修復することはありません。両者の Goal 判定は同じ機械条件を使います。各経路の実装・接続状況は、対応する契約と受け入れ条件を確認してください。

## 用語が担っている役割

| 用語 | 何を表すか | 購入例での読み方 |
| --- | --- | --- |
| Goal | 一つの目的と任意の成功・失敗条件 | 「購入する」と「購入数が増えた」の区別 |
| Assertion / Condition | 一つの比較／比較・対象数・時間を組み合わせた条件 | 金額比較、ボタン一件一致、一定時間の状態保持 |
| Target / read | どの対象の、どの領域の値を読むか | UI selector と `state.checked` などの標準field、別登録の Observe |
| Observation | 値に加えて収集時刻、対象寿命、完全性を持つ証拠 | 値が同じでも対象差替えや gap があれば連続した観測とは限らない |
| PlannerDecision | 一件の完了した提案 | execute / observe / wait / finish。合否や同意は決めない |
| Permit / ApprovedDecision | Runner が有限期間・予算内で管理する許可 | 提案を採用したことと、現在送信してよいことは別の確認 |
| Run / Result | 一実行の状態／確定した一次結果と後処理 | `Completing` は終了処理中。`Passed` だけで保存完了とは言えない |
| Recording / Replay Plan | Gua の記録／Scenario と記録を固定参照する再生条件 | 記録した購入操作を削除・挿入せず再生する |
| Trace / ArtifactReceipt | Gua の時系列／取得・保存状況とファイル参照 | 失敗を調べる材料。存在しない画像を空ファイルで補わない |
| fixture | 承認された開始状態・独立検証のためのテスト環境 | 所持金や店の初期状態を準備する。合否を Planner に漏らさない |

`#6` などは実 GitHub Issue、`P-05` などは起票前の作業案、`AT-*` は受け入れ確認の識別子です。実Issue対応は [traceability.json](traceability.json)、要件の意味は [01-spec-ledger.md](01-spec-ledger.md)を参照します。IDや試験項目の存在は合格実績ではありません。

## コードを読む順番

まず [CliApplication.ExecuteAsync](../src/Gua.Playtest.Cli/CliApplication.cs)で、公開コマンドからどの処理へ入るかを確認します。その後、関心に応じて次へ進みます。

| 知りたいこと | 実在するコードの入口 | 具体的な試験例の入口 |
| --- | --- | --- |
| ファイルの読み込みと固定参照 | [StaticContractValidator.ValidateFileAsync](../src/Gua.Playtest.Core/Contracts/StaticContractValidator.cs)、[ContractDecoder](../src/Gua.Playtest.Core/Contracts/ContractDecoder.cs) | [StaticContractsTests](../tests/Gua.Playtest.Contracts.Tests/StaticContractsTests.cs) |
| 元の Value をどう比較するか | [PreparedAssertion.EvaluateJson](../src/Gua.Playtest.Core/Assertions/PreparedAssertion.cs)、[EvaluationResult / TruthLogic](../src/Gua.Playtest.Core/Assertions/EvaluationResult.cs) | [AssertionTests](../tests/Gua.Playtest.Contracts.Tests/AssertionTests.cs)、[TruthLogicTests](../tests/Gua.Playtest.Contracts.Tests/TruthLogicTests.cs) |
| 条件木と時間の履歴 | [PreparedCondition.Create / Start](../src/Gua.Playtest.Runner/Conditions/PreparedCondition.cs)、[ConditionSession.EvaluateAt](../src/Gua.Playtest.Runner/Conditions/ConditionSession.cs) | [ConditionTests](../tests/Gua.Playtest.Foundation.Tests/ConditionTests.cs) |
| 終了判断と後処理 | [RunSession.Evaluate](../src/Gua.Playtest.Runner/Execution/RunSession.cs)、[RunMonitor.AwaitAsync](../src/Gua.Playtest.Runner/Execution/RunMonitor.cs)、[OwnedCleanup](../src/Gua.Playtest.Runner/Execution/OwnedCleanup.cs) | [RunTests](../tests/Gua.Playtest.Foundation.Tests/RunTests.cs)、[RunReviewTests](../tests/Gua.Playtest.Foundation.Tests/RunReviewTests.cs) |
| 起動・接続・開始状態 | [HostPreparation.PrepareAsync](../src/Gua.Playtest.Runner/Preparation/HostPreparation.cs)、[SystemProcessLauncher](../src/Gua.Playtest.Runner/Preparation/SystemProcessLauncher.cs) | [PreparationTests](../tests/Gua.Playtest.Foundation.Tests/PreparationTests.cs) |
| Guaからの観測と入力結果 | [BridgeObservations.ReadBatch](../src/Gua.Playtest.GuaIntegration/BridgeObservations.cs)、[BridgeUiActions](../src/Gua.Playtest.GuaIntegration/BridgeUiActions.cs)、[OwnedGameInput](../src/Gua.Playtest.GuaIntegration/OwnedGameInput.cs) | [Bridge.Tests](../tests/Gua.Playtest.Bridge.Tests) |
| 提案の採用と再確認 | [PlannerGate.Begin / Adopt](../src/Gua.Playtest.Runner/Planning/PlannerGate.cs)、[PlannerTurn.AwaitAsync](../src/Gua.Playtest.Runner/Planning/PlannerTurn.cs) | [PlannerGateTests](../tests/Gua.Playtest.Foundation.Tests/PlannerGateTests.cs) |
| 保存と読み戻し | [RunArtifactStore](../src/Gua.Playtest.Runner/Persistence/RunArtifactStore.cs)、[RunArtifactReader](../src/Gua.Playtest.Runner/Persistence/RunArtifactReader.cs) | [ArtifactTests](../tests/Gua.Playtest.Foundation.Tests/ArtifactTests.cs) |

Core はファイル形式、純粋比較、抽象窓口を持ち、nativeを読み込みません。Runner は条件の履歴、予算、判定、許可、後処理を組み合わせます。GuaIntegration が Gua の具体APIを扱い、Cli が利用入口を組み立てます。Planners.Codex は Planner 実装を置く境界です。依存関係は [check-boundaries.ps1](../scripts/check-boundaries.ps1)で検査します。

## ローカルで最初に確認する

PowerShell と、[global.json](../global.json)に合う .NET SDK が必要です。SDK指定は 10.0.301、`rollForward` は `latestFeature` です。最初の restore では lock に固定された NuGet package を取得できるネットワーク／キャッシュが必要です。build と静的検証のために Gua checkout、ゲームエンジン、Codex認証を準備する必要はありません。

リポジトリルートで次を実行します。

```powershell
dotnet restore --locked-mode
dotnet build --no-restore -c Release
./scripts/check-boundaries.ps1
```

期待する結果は各コマンドの終了コード0です。restore が lock 不一致を報告した場合は、依存を勝手に更新せず、使用SDKと package取得状況を確認します。build成功だけでは実ゲームの入力や Planner 接続は保証されません。

次はリポジトリにある静的検証用の Scenario を読みます。

```powershell
dotnet run --no-build -c Release --project src/Gua.Playtest.Cli -- --help
dotnet run --no-build -c Release --project src/Gua.Playtest.Cli -- validate --allow-root ./fixtures/contracts ./fixtures/contracts/scenario.json
dotnet run --no-build -c Release --project src/Gua.Playtest.Cli -- validate --allow-root ./fixtures/contracts ./fixtures/contracts/bad-hash.json
```

`scenario.json` は終了0、stdout は `{"status":"Valid","code":"Valid"}` です。このfixtureの成功条件は checkbox の `state.checked == true` という形式の例であり、実店舗の購入証拠ではありません。`bad-hash.json` は終了2で固定参照の不一致を示します。期待するコード一覧は [expected.json](../fixtures/contracts/expected.json)にあります。`--allow-root` は呼出し側が与える読み取り範囲です。参照ファイルの相対パスは、その参照を持つ文書から解決されます。

判定の実装を追った後は、対応する既存テストを選べます。

```powershell
dotnet test tests/Gua.Playtest.Contracts.Tests -c Release --no-build --no-restore --filter FullyQualifiedName~StaticContractsTests
dotnet test tests/Gua.Playtest.Foundation.Tests -c Release --no-build --no-restore --filter FullyQualifiedName~ConditionTests
node --test tools/fixture-oracle.test.mjs
```

最後のコマンドには Node.js が必要です。これは独立検証器の人工データ試験です。製品の全projectを順番に試す入口は [test-projects.ps1](../scripts/test-projects.ps1)、配布配置の確認は [package-smoke.ps1](../scripts/package-smoke.ps1)、nativeを除いた静的検証の確認は [contract-smoke.ps1](../scripts/contract-smoke.ps1)です。これらは新しい `artifacts` 配下へ出力します。各試験の対象と成果物を、実ゲームを動かした証拠と区別してください。

## 保証を読むときの境界

**Unknown は False の別名ではありません。** 値を読めなかったときに「品物を持っていない」と断定すると、失敗条件や否定比較が誤成立します。観測の欠落、古いepoch、gap、型違反を、それぞれの契約に従って扱います。時間条件では、同じ値を二回読めただけでその間ずっと同じだったことにはできません。

**自然言語の達成報告は合否を決めません。** 成功条件のない探索は、Planner が finish を返しても Passed にできません。判定用の期待値・失敗条件と公開Planner観測は別に保持し、Debug接続の結果を後から間引くだけで公開profileの境界を作ったことにしません。

**公開packageの能力と接続経路の完成は別です。** Gua.Testing 1.1.1を使用します。送信直前チェックとホスト側enqueueを一体に守る guarded 経路や、実 Planner の隔離、実エンジンの受け入れ条件は [gua-bridge.md](gua-bridge.md)、[planning.md](planning.md)、[fixture-evidence.md](fixture-evidence.md)の制限を確認します。未公開APIを使える前提でfallbackを作らないでください。

**CLIの入口とライブラリの実装範囲は別です。** 公開CLIのコマンドは `--help` と `CliApplication` で確認してください。run/replay/report の正式なCLI compositionは #15 の統合作業です。ライブラリに準備・判定APIがあっても、それだけで `run` コマンドを使えることにはなりません。

結果の比較には同じ Scenario の定義・bytes hash、実ゲームの buildId、実効 Environment のidentityが必要です。Gua ライブラリの buildId をゲームの版番号として使うことはできません。過去の緑のCI、Fake試験、保存ファイルのschema適合は、それぞれが確認した範囲の証拠です。実ゲーム・実Planner・配布consumerの受け入れには各境界の独立した証拠が必要です。
