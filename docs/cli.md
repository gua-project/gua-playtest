# Gua Playtest の CLI

`gua-playtest --help` でコマンドを確認できます。CLI は対話入力を求めません。`--json` はどの位置でも一度指定でき、指定時は標準出力に最終結果の JSON を一件出します。通常は短いテキストを出します。診断は標準エラー出力へ送り、例外、入力文書の本文、認証情報、環境変数の一覧は出しません。

## コマンドを選ぶ

| コマンド | ゲームへの接続と入力 | 結果の読み方 |
| --- | --- | --- |
| `validate` | なし | 文書やスキーマを検証します。終了 0 は検証の成功です。 |
| `doctor --native` | なし | インストール済みの Gua SDK のネイティブ配布物を検査します。 |
| `report` | なし | 保存済み Trace を読み、必要に応じて HTML を出力します。終了 0 はレポート処理の成功です。Run の合否は保存した結果で確認します。 |
| `dashboard` | なし | 保存済み Run の一覧をローカルで表示します。 |
| `plan create` / `plan accept` | なし | Plan 候補を作り、保存された試行の証拠に基づいて明示採用します。 |
| `explore` / `replay` | なし。入力の検証と上限の解決まで | 有効な入力でも終了 2 の `ProductionRuntimeUnavailable` を返します。 |
| [`observe-run`](observation-runtime.md) | Windows でゲームを起動・接続し、観測のみ | 実行ファイルと接続先の所有権を確かめ、Scenario を評価します。 |
| [`click-run`](single-ui-runtime.md) | Windows で明示した UI を最大１回クリック | 開始時に Goal が成立していればクリックを省きます。クリックした場合は操作の完了と Goal の成立を別々に確かめます。 |
| [`scripted-explore-run`](scripted-explore-runtime.md) | Windows で Player に接続し、台本型 Planner で明示した UI を最大１回クリック | 外部モデルや任意のスクリプトを使いません。開始時に Goal が成立していれば、提案もクリックも行いません。 |
| [`replay-run`](replay-runtime.md) | Windows で保存した固定１クリック Plan を再生 | `conditionSynchronized` に対応します。採用前の候補を試すには `--allow-candidate` が必要です。 |

## 引数

Windows でゲームを起動するコマンドの引数は、上の表から各手順を参照してください。

```text
validate --allow-root <existing-directory> <json-or-yaml-file>
validate --gua-schema <packaged-schema-name> <json-file>
doctor --native
dashboard --registry <file> --artifact-root <existing-directory> --allow-root <existing-directory> --port <1024..65535>
plan create --managed-root <private-root> --allow-root <input-root> --scenario <file> --recording <file> --timing <recorded|conditionSynchronized>
plan accept --managed-root <private-root> --plan <managed-plan.json> --trial <trial-id> --approve --approve-expectations
report --allow-root <existing-directory> --trace <saved-trace-directory> [--output <new-html>]
explore --allow-root <existing-directory> --scenario <file> --environment <file>
replay --allow-root <existing-directory> --plan <file> --environment <file>
```

パスと許可範囲は明示します。パスの自動探索、親の設定の取り込み、任意の `--set`、Goal の上書き、Replay から Explore への自動切り替えはありません。

## 保存済みの Trace と Run を読む

`report` は `GuaTraceReader` で確定済みの Trace を読みます。読み取りエラーや未確定の Trace は拒否します。HTML は同梱の `GuaTraceReport` Viewer で生成します。出力先の親ディレクトリは存在し、許可範囲に入っている必要があります。Trace 内への出力は、大文字・小文字だけが異なる別名経由でも拒否します。既存ファイルへの上書きも拒否します。Trace の詳細形式は Gua が定義します。

`dashboard` は指定したループバックのポートで、保存済み Run の索引を読み取り専用で提供します。レジストリ、成果物のディレクトリ、許可範囲が必要です。成果物のルートや許可範囲を複数渡すには、それぞれのフラグを繰り返します。レジストリから成果物のルートを自動で追加することはありません。準備完了時の URL は標準エラー出力に表示します。中断時はサーバーを停止してから、終了 4 の最終結果を出します。

## パスの許可範囲

- 大文字・小文字が異なるパスを指定のルートに対応付ける前に、ディレクトリとファイルが実体として同じかを確認します。名前やバイト列が一致する別のディレクトリに許可は及びません。保存済み Trace とレポート出力先の親もこの方法で確認します。
- 静的コマンドはシンボリックリンクとジャンクションを一段ずつ確認してたどり、最終的な参照先が許可範囲に入ることを確かめます。管理対象の Plan は親の項目を先に調べ、成果物へのリンクを拒否します。
- UNC と Windows デバイス名前空間は対象にアクセスする前に拒否します。管理用の保存ルートや `validate --gua-schema` の入力にも適用します。
- 開いた文書が参照するファイルや、レジストリが指定する各ディレクトリも個別に確認します。入口の文書に対する許可は参照先に引き継ぎません。

## Plan 候補の作成と採用

候補には検証済みの Scenario と Recording の元バイト列をハッシュとともに保持します。採用には２つの承認フラグと、`ReplayDriver` と保存処理を通じて AI なしで最後まで実行した試行が必要です。偽造・古い証拠、未完了や `onGoal` の試行は拒否します。証拠 JSON だけでは採用できません。詳しくは [Plan の採用契約](plan-adoption.md) を参照してください。

秘密情報を参照する場合は、どちらの Plan コマンドでも `--secret-env <secretKey>=<environment-variable-name>` を必要な数だけ指定できます。

- ホストプロセスは参照されたキーだけを解決し、環境変数を列挙しません。指定漏れ、重複、未参照の指定は拒否します。上限は 1024 キー、65536 文字です。
- `PlanManager` と共通の `PersistenceRedactor` が、解決済みの値と `password`、`token`、`authorization`、`secretValue` のフィールド名を検査します。既知の秘密と衝突する入力はバイト列を固定して公開する前に拒否します。Goal や Recording の意味を書き換えたり、秘密の参照キーを値に置き換えたりしません。
- CLI は秘密参照の確認に使った入力のハッシュを `PlanCommand` 経由で `PlanManager` に渡します。Scenario、Recording、Plan が途中で差し替わった場合は候補や採用結果の公開前に拒否します。管理用の保存ルートと採用の権限を証明するキーは、ホストで非公開にします。
- テキストでも JSON でも、診断コードや候補のパスを含む応答全体を公開前に検査します。別の指定が欠けている場合や中断・ポリシー構築の失敗時にも、解決済みの値を保護します。秘密と衝突する診断は出しません。通常のフィールド名から漏れる場合は内容を含まない JSON を返します。どの JSON 値でも漏れる場合は出力せず、候補や採用結果も公開しません。

## `explore` と `replay` が決める上限

両コマンドは複数の `--allow-root`、正の `--max-actions` と `--max-duration-ms`、複数の `--deny-action` を受け付けます。元の入力のバイト列とハッシュを保持し、Scenario、Environment、CLI の上限をそれぞれ示します。その最小値を適用するので、大きい値を CLI に渡しても既存の上限は上がりません。他の上限は Environment の明示値を使います。操作 ID は Scenario と Environment の両方が許すものに絞り、最後に `--deny-action` の指定を除きます。この計算では接続先の許可や実行時の識別情報を確認せず、認証情報も解決しません。

操作を実行する信頼済みの呼び出し側は、Environment v1 にない起動方式、実行ファイル、基準となる時計、認証ポリシーを明示し、実際のゲームビルドを確認します。Plan の時間規則（シミュレーションを含む）を守り、許可と資源上限を現在のホスト権限に合わせて絞ります。準備と保存に渡す秘密は必要なものに限ります。実行時に適用した秘密を含まない条件のハッシュと、入力 Environment のハッシュは個別に保持します。不足する入力を既定値で補うことはできません。

Gua.Testing 1.2.0 の API を使う経路でも、ホスト側で状態確認と操作受付を一体として行える必要があります。ローカルのロックや形式だけのリースではその保証を満たせません。一般の Explore を実ゲームへつなぐには、承認済みの隔離された実 Codex プロバイダーも必要です。Run や試行の成功は実際の実行結果から記録します。

## Run の終了コード

`CliApplication.WriteRunResultAsync` は `RunOutcome` と `PersistenceResult` に従って、一次結果と後処理を表示します。

| 終了コード | Run の結果 |
| --- | --- |
| 0 | Passed |
| 1 | Failed |
| 2 | Invalid |
| 3 | TimedOut |
| 4 | Aborted |
| 5 | Unverified |
| 10 | Runner エラー |
| 11 | 一次結果は Passed。必須の後処理または保存が未完了 |

一次結果が Passed 以外なら、後処理に失敗してもその終了コードを維持します。コマンド自体の中断は終了 4 です。子プロセスの標準出力と標準エラー出力は、そのまま蓄積・保存せずに読み捨てます。CLI の JSON や診断にも混ぜません。
