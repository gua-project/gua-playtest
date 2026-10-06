# Issue起票案と依存関係

これは元の作業分割案を保持する履歴である。現在の実Issue対応はtraceability.jsonのactual_issue_mappingを正本とする。既存機能の全件差分監査でもない。実起票時は既存実装・PR・命名に合わせ、追加ではなく接続/修正/回帰試験で足りる部分を重複実装しない。

## 親Issueと既存Issue

**Playtest管理用親Issue案:** 「Gua Playtest v0.1の合意仕様と統合リリースを追跡する」。実装の重複依頼ではなく要件・子作業・実行証拠の集約先。実GitHub番号は未発行。

**Gua側の親:** 既存#109は承認済み改訂本文へ更新する案。T-01〜T-06を子作業とし、全連携・Viewer・配布検証まで親の受け入れ条件を閉じない。[S-109]

| 既存項目 | 方針 |
|---|---|
| Gua #106 | Lintを再利用し、Traceへの添付をT-06で接続する。[S-106] |
| Gua #107 | Locator/actionability/完了確認を再利用する。別のauto-waitを作らない。[S-107] |
| Gua #108 | baseline比較を再利用する。Traceに同じ既定除外を適用しない。[S-108] |
| Gua #109 | 元本文を改訂する。Playtestに第二のTraceを作らない。[S-109] |
| 既存InputAction/Recording | 既存のdescription・操作形式等は再利用し、差分だけを実装する。[S-INPUT][S-REC] |

## 依存の読み方

「着手に必要」は合意されたschema/API/意味規則。「統合完了に必要」は相手側の実装・実接続の確認。「リリースに必要」は全採用要件の実行証拠。下記の依存は統合のための保守的な計画であり、着手を全て直列にする指示ではない。

Fake・骨格・配布起動smoke・CLI外枠は契約が決まった時点で進められる。Guaの全client実装やViewerが完了するまで、Runnerの純粋な条件評価を待つ必要はない。逆にFakeだけでは統合完了にしない。

## Gua側の起票一覧

| 仮ID | タイトル | 統合依存 |
|---|---|---|
| G-01 | 共通Value schemaと型・比較用データを定義する | なし |
| G-02 | Object/UI ObserveとWorld Propertyの登録・寿命・通知を実装する | G-01 |
| G-03 | 追加観測値をbinding・adapter・bridge・既存クライアントへ接続する | G-02 |
| G-04 | InputActionの値説明schema・入力例と互換性を追加する | なし |
| G-05 | ReplayのTimed Segment・複合入力・時間契約を実装する | GH-107 |
| G-06 | Playtestから再利用できるGua配布契約を整える | G-03, G-04, G-05, T-06 |
| T-01 | #109子作業：Trace schema・writer/reader・保存方針を実装する | G-01 |
| T-02 | #109子作業：操作・Assertion・native lifecycleを相関する | T-01, GH-107 |
| T-03 | #109子作業：Snapshot・Change・観測区間と欠損を保存する | T-01, G-03 |
| T-04 | #109子作業：外部Runner向けTrace記録APIを提供する | T-01, T-02, T-03 |
| T-05 | #109子作業：共通Trace Viewerと静的reportを提供する | T-02, T-03, T-04 |
| T-06 | #109子作業：lint・baseline・既存diagnosticsと全体を統合する | T-05, GH-106, GH-108 |

## Playtest側の起票一覧

| 仮ID | タイトル | 統合依存 |
|---|---|---|
| P-01 | 仕様台帳・設定schema・Planner契約と静的validatorを整える | なし |
| P-02 | 五つのプロジェクト・依存境界・Fake/テスト基盤を作る | なし |
| P-03 | 型付きAssertion・Operator・三値論理を実装する | P-01, P-02, G-01 |
| P-04 | 時間条件・論理group・対象数・観測連続性を実装する | P-03 |
| P-05 | Run状態機械・予算・結果確定・cleanupを実装する | P-04 |
| P-06 | launch/attach・Setup・開始前提・Running境界を実装する | P-05, P-07 |
| P-07 | Gua観測・操作の統合と公開範囲の境界を実装する | P-02, G-03, G-04, GH-107 |
| P-08 | Run成果物とGua Trace・Recordingの関連付けを実装する | P-05, P-07, T-04 |
| P-09 | Replay Planの解決・Gua再生・中間検証を実装する | P-06, P-08, G-05 |
| P-10 | PlannerInput/Decision・採用ゲート・feedbackを実装する | P-01, P-02, P-05 |
| P-11 | Explore・進捗・停滞検知と有限の回復を実装する | P-06, P-07, P-10 |
| P-12 | Codex App Server接続と権限制限を実装する | P-10 |
| P-13 | CLI・Environment解決・終了コード・report入口を実装する | P-01, P-06, P-08, P-09, P-11, P-12, P-14, T-05 |
| P-14 | RecordingからPlan候補を生成し、内容固定・試験・採用を管理する | P-01, P-08, P-09 |
| P-15 | 共通fixture・故障注入・独立oracleを用意する | P-02, G-03, G-04 |
| P-16 | 配布物・互換性・v0.1全体E2Eを検証する | P-13, P-14, P-15, P-17, G-06 |
| P-17 | 利用文書・正常サンプル・互換性表と責務説明を整える | P-13 |

## 各作業の起票用要点

### G-01 共通Value schemaと型・比較用データを定義する

**起票先:** `gua`（提案）

**実装・接続範囲:** bool/integer/number/string/enum/list/set、型情報、enum型定義、禁止値、言語間の正常・異常データを定義する。

**着手前の契約:** 既存wire契約との差分、OPEN-01/02を先に解決する。

**統合確認の依存:** なし

**対象外:** PlaytestのGoal、ゲーム固有の固定状態モデル。

**完了条件:** schema・生成データ・各実装の読み取りを照合し、既存Gua値を無断で破壊しない。

**主担当要件:** VALUE-001, VALUE-002, VALUE-003, VALUE-004, VALUE-005

**協力・統合する要件:** ASSERT-003

**具体化が必要な残件:** OPEN-01, OPEN-02

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### G-02 Object/UI ObserveとWorld Propertyの登録・寿命・通知を実装する

**起票先:** `gua`（提案）

**実装・接続範囲:** 読み取り専用の登録、重複名、追加・変更・削除、Objectの寿命、購読境界、取得不能を扱う。

**着手前の契約:** OPEN-03の検知境界・寿命・失敗契約を固定する。

**統合確認の依存:** G-01

**対象外:** 状態書換え、GuaからのGoal評価、ゲーム内部全状態の同期。

**完了条件:** 同種の敵を複数登録し、破棄・再生成・取得例外・中間変化を契約どおり検証する。

**主担当要件:** OBS-001, OBS-003, OBS-004

**協力・統合する要件:** VALUE-002, TIME-002

**具体化が必要な残件:** OPEN-03, OPEN-04

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### G-03 追加観測値をbinding・adapter・bridge・既存クライアントへ接続する

**起票先:** `gua`（提案）

**実装・接続範囲:** C ABI、C++/.NET、Godot/Unity、wire、Inspector等の公開・取得経路を接続する。既存のWorld stateとの重複を解消する。

**着手前の契約:** G-01/G-02の契約と公開profile、対象クライアントの対応表。

**統合確認の依存:** G-02

**対象外:** 新しいPlaytest専用Game Stateレジストリ。

**完了条件:** 対応表で宣言した経路ごとに、実際の生成・通知・読取・秘密値非混入を確認する。

**主担当要件:** OBS-002

**協力・統合する要件:** VALUE-003, OBS-001, OBS-004, INPUT-002

**具体化が必要な残件:** OPEN-01, OPEN-03

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### G-04 InputActionの値説明schema・入力例と互換性を追加する

**起票先:** `gua`（提案）

**実装・接続範囲:** valueSchema・examplesの定義、登録、検索結果、Inspector/MCP等への伝達と入力検証を整える。

**着手前の契約:** 既存description/valueType/rangeの再利用、OPEN-01の互換性とschema方言。

**統合確認の依存:** なし

**対象外:** descriptionの重複新設、任意ゲーム内部コマンド、Playtest独自Capability。

**完了条件:** 既存button/axis1d/vector2/textを壊さず、入力例とschemaが一致し、旧クライアントとの扱いが明示される。

**主担当要件:** INPUT-001, INPUT-002, INPUT-003

**協力・統合する要件:** なし

**具体化が必要な残件:** OPEN-01

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### G-05 ReplayのTimed Segment・複合入力・時間契約を実装する

**起票先:** `gua`（提案）

**実装・接続範囲:** 区間開始＋offsetによる予定、結果待ちとの分離、順序、保持解除、遅延停止、clock能力、記録参照を扱う。

**着手前の契約:** CLOCK要件、OPEN-07、既存Recordingの時刻の意味を固定する。

**統合確認の依存:** GH-107

**対象外:** AIによる経路修復、エンジン全体の決定論の無条件保証。

**完了条件:** 遅い応答で解除予定を延ばさず、許容外遅延でまとめ打ちせず、未対応の時間保証を拒否する。

**主担当要件:** ACTION-005, CLOCK-001, CLOCK-003, CLOCK-004, CLOCK-005, CLOCK-006, CLOCK-007

**協力・統合する要件:** BUDGET-002, REPLAY-002, CLOCK-002

**具体化が必要な残件:** OPEN-07

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### G-06 Playtestから再利用できるGua配布契約を整える

**起票先:** `gua`（提案）

**実装・接続範囲:** 既存パッケージの不足分だけを整備し、schema/native/生成済みViewerを版固定して再利用可能にする。

**着手前の契約:** 利用するGuaパッケージ、schema、native、Viewerの配布方式と版の契約。

**統合確認の依存:** G-03, G-04, G-05, T-06

**対象外:** PlaytestへGuaのソースやViewer実装を複製すること。

**完了条件:** Guaソースcheckoutなしの利用側テストで、各対応RIDと表示資産を検証する。

**主担当要件:** 主担当は各基盤要件。下記協力要件を接続する。

**協力・統合する要件:** BOUND-001, FILE-002, PACK-002, PACK-003

**具体化が必要な残件:** OPEN-11

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### T-01 #109子作業：Trace schema・writer/reader・保存方針を実装する

**起票先:** `gua`（提案）

**実装・接続範囲:** Session/Step/Event、recent/streaming、onFailure/always、有限容量、追記保存、部分読取、manifestを実装する。

**着手前の契約:** 承認済み#109改訂案、OPEN-01/10。

**統合確認の依存:** G-01

**対象外:** Goal採点、全frameの無制限保存、秘密値保護の後回し。

**完了条件:** 保持範囲・欠損・中断を区別し、容量上限と不完全な末尾を正常な完全記録と偽らない。

**主担当要件:** TRACE-003, TRACE-007, TRACE-008, TRACE-009

**協力・統合する要件:** TRACE-002

**具体化が必要な残件:** OPEN-01, OPEN-10

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### T-02 #109子作業：操作・Assertion・native lifecycleを相関する

**起票先:** `gua`（提案）

**実装・接続範囲:** client stepとnative処理段階、UI/Game Input/Raw Input、保持開始解除、source locationを関連付ける。

**着手前の契約:** request/source/epoch識別とホスト事実の取得範囲。

**統合確認の依存:** T-01, GH-107

**対象外:** 同じ要求の二重計上、結果キューの先取り、未観測の適用時刻の推定。

**完了条件:** 同一requestIdが別接続・epochにあっても混ざらず、結果待ちクライアントを妨げない。

**主担当要件:** TRACE-002, TRACE-010

**協力・統合する要件:** OBS-005, ACTION-001, ACTION-005, TRACE-003, CLOCK-004

**具体化が必要な残件:** 担当要件の受け入れ例・命名を起票時に確認する。

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### T-03 #109子作業：Snapshot・Change・観測区間と欠損を保存する

**起票先:** `gua`（提案）

**実装・接続範囲:** redaction後のcontent hash、観測時点分離、前後と中間Change、欠損・不在・古い値を保存する。

**着手前の契約:** G-02の購読境界、OPEN-03/04。

**統合確認の依存:** T-01, G-03

**対象外:** baseline正規化の無条件適用、因果関係の自動断定。

**完了条件:** 同じ内容の別時点観測とFirst→Second→Thirdを保持し、欠損後のSnapshotで履歴を捏造しない。

**主担当要件:** TRACE-004, TRACE-005, TRACE-006

**協力・統合する要件:** OBS-003, OBS-004, OBS-006, ACTION-002, TRACE-009

**具体化が必要な残件:** OPEN-03

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### T-04 #109子作業：外部Runner向けTrace記録APIを提供する

**起票先:** `gua`（提案）

**実装・接続範囲:** framework非依存のStep/mark/評価経過/添付/flush、名前空間付きmetadataと型付き値を提供する。

**着手前の契約:** 汎用注釈・attachment・相関APIの契約。

**統合確認の依存:** T-01, T-02, T-03

**対象外:** GuaがPlaytestを参照すること、Goal/Planner型の導入。

**完了条件:** Guaだけに依存する小さな外部writerで、未知の注釈を含むTraceを保存・読取できる。

**主担当要件:** TRACE-001

**協力・統合する要件:** RUN-003

**具体化が必要な残件:** 担当要件の受け入れ例・命名を起票時に確認する。

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### T-05 #109子作業：共通Trace Viewerと静的reportを提供する

**起票先:** `gua`（提案）

**実装・接続範囲:** Inspector部品の共有、Timeline、保持解除、差分、欠損、成功Run、静的HTMLと配布アセットを整える。

**着手前の契約:** Trace readerと未知の注釈・添付の安全な表示契約。

**統合確認の依存:** T-02, T-03, T-04

**対象外:** Viewerからゲーム操作・Replay実行、Playtest専用Viewer。

**完了条件:** 悪意ある文字列や添付パスが実行・外部取得されず、未知metadataも汎用表示される。

**主担当要件:** TRACE-011

**協力・統合する要件:** TRACE-009

**具体化が必要な残件:** 担当要件の受け入れ例・命名を起票時に確認する。

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### T-06 #109子作業：lint・baseline・既存diagnosticsと全体を統合する

**起票先:** `gua`（提案）

**実装・接続範囲:** 既存診断、lint、snapshot comparison、Recording参照、並列分離、保存失敗と元例外の分離を通す。

**着手前の契約:** 各機能の独立した公開契約とartifact参照。

**統合確認の依存:** T-05, GH-106, GH-108

**対象外:** lint自動実行、baseline自動更新、Traceによるテスト成否決定。

**完了条件:** 成功・失敗・中断fixtureと静的reportで改訂#109の受け入れ条件を通す。

**主担当要件:** 主担当は各基盤要件。下記協力要件を接続する。

**協力・統合する要件:** TRACE-005, TRACE-009, TRACE-010, TRACE-011, FIX-006

**具体化が必要な残件:** 担当要件の受け入れ例・命名を起票時に確認する。

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### P-01 仕様台帳・設定schema・Planner契約と静的validatorを整える

**起票先:** `gua-playtest`（提案）

**実装・接続範囲:** Scenario/Environment/Plan/Input/Decision/Run/Result、型とschemaの対応、静的解析、正常異常データを管理する。

**着手前の契約:** 本台帳とOPEN一覧。Gua共通型はG-01等の契約に合わせる。

**統合確認の依存:** なし

**対象外:** ゲーム起動、外部コードの実行、暗黙の最新schema取得。

**完了条件:** 形式検証と実行時確認項目を区別し、秘密値や任意設定上書きの入口を作らない。

**主担当要件:** BOUND-005, GOAL-001, FILE-001, FILE-002, FILE-003

**協力・統合する要件:** BOUND-002, VALUE-005, ASSERT-001, RUN-001, REPLAY-001, REPLAY-006, PLANNER-004, FILE-004, DELIVERY-001

**具体化が必要な残件:** OPEN-01, OPEN-04, OPEN-08, OPEN-10, OPEN-12

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### P-02 五つのプロジェクト・依存境界・Fake/テスト基盤を作る

**起票先:** `gua-playtest`（提案）

**実装・接続範囲:** 一方向の依存、clock/observation/planner/hostの窓口、Fake、ビルド、配布物の早期起動試験を整える。

**着手前の契約:** C#/.NET 10、Core/Runner/GuaIntegration/Planners.Codex/Cliの合意。

**統合確認の依存:** なし

**対象外:** 二系統のRunner、Fakeの製品代替、動的な未承認pluginロード。

**完了条件:** Coreにエンジン・Codex依存がなく、CLIへ成否規則を埋め込まずテストできる。

**主担当要件:** BOUND-001, PACK-001, PACK-002, DELIVERY-002

**協力・統合する要件:** FIX-001, DELIVERY-001

**具体化が必要な残件:** 担当要件の受け入れ例・命名を起票時に確認する。

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### P-03 型付きAssertion・Operator・三値論理を実装する

**起票先:** `gua-playtest`（提案）

**実装・接続範囲:** Target/Value/Operator、scalar/collection演算、enumType、True/False/Unknown、不正と取得不能を区別する。

**着手前の契約:** VALUE/ASSERT要件、OPEN-02/04/05。

**統合確認の依存:** P-01, P-02, G-01

**対象外:** 独自Selector言語、未知をfalseに変換すること。

**完了条件:** 正常・不正・不明を独立した期待値で検証し、別のany分岐で不正条件を隠さない。

**主担当要件:** ASSERT-001, ASSERT-002, ASSERT-003, ASSERT-006

**協力・統合する要件:** VALUE-001, VALUE-004, OBS-005, ACTION-002, STALL-002, FIX-007

**具体化が必要な残件:** OPEN-02, OPEN-04, OPEN-05

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### P-04 時間条件・論理group・対象数・観測連続性を実装する

**起票先:** `gua-playtest`（提案）

**実装・接続範囲:** within/for、通常allと時間成立履歴、quantifier、0件・打切り、各分岐の期限、評価タイマーを実装する。

**着手前の契約:** 直近の整合性レビューとOPEN-05の詳細真理値表。

**統合確認の依存:** P-03

**対象外:** pollingで消えた中間状態の復元、全分岐一律Timeout失敗。

**完了条件:** 別時点A/B、any片方期限切れ、failure期限切れ、空all、for欠損を検証する。

**主担当要件:** ASSERT-004, ASSERT-005, ASSERT-007, ASSERT-008, ASSERT-009, ASSERT-010, TIME-001, TIME-002, TIME-003

**協力・統合する要件:** ASSERT-006, TIME-004, BUDGET-003, CLOCK-002, START-005, FIX-007

**具体化が必要な残件:** OPEN-05

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### P-05 Run状態機械・予算・結果確定・cleanupを実装する

**起票先:** `gua-playtest`（提案）

**実装・接続範囲:** CreatedからFinished、一度だけの主結果確定、同一評価単位の優先、予算予約、後処理を扱う。

**着手前の契約:** RUN/ACTION/BUDGET要件、OPEN-05/06。

**統合確認の依存:** P-04

**対象外:** CompletingのCancelによる主結果上書き、送信不明操作の自動再送。

**完了条件:** 競合結果、最後の操作、区間予算不足、必須cleanup失敗とexit 11を確認する。

**主担当要件:** GOAL-002, TIME-004, RUN-001, RUN-002, RUN-003, RUN-005, RUN-006, BUDGET-001, BUDGET-002, BUDGET-003, CLOCK-002

**協力・統合する要件:** GOAL-001, ASSERT-009, RUN-004, ACTION-003, ACTION-005, REPLAY-004, CLOCK-006, PLANNER-007, STALL-004, START-002, START-006, FILE-005, CLI-005, FIX-007

**具体化が必要な残件:** OPEN-05, OPEN-06, OPEN-10

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### P-06 launch/attach・Setup・開始前提・Running境界を実装する

**起票先:** `gua-playtest`（提案）

**実装・接続範囲:** 承認済みEnvironment、専有・所有、Setupとreset分離、準備期限、初期観測と監視開始を整える。

**着手前の契約:** START要件とGuaホストの既存機能・不足確認。

**統合確認の依存:** P-05, P-07

**対象外:** 任意shell、PlannerへのSetup公開、attach先の無断reset/kill。

**完了条件:** 接続だけでは開始せず、初期success/failureと古いepoch・Setup失敗を正しく扱う。

**主担当要件:** GOAL-003, START-001, START-002, START-003, START-004, START-005, START-006, START-007

**協力・統合する要件:** REPLAY-003

**具体化が必要な残件:** 担当要件の受け入れ例・命名を起票時に確認する。

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### P-07 Gua観測・操作の統合と公開範囲の境界を実装する

**起票先:** `gua-playtest`（提案）

**実装・接続範囲:** 観測集約、Selector再解決、既存操作の送信と相関、保持owner、公開profile、未確認状態を扱う。

**着手前の契約:** Guaの既存clientとsource/ui/object/world、OPEN-01/03/04。

**統合確認の依存:** P-02, G-03, G-04, GH-107

**対象外:** 別のGua機能再実装、Worldへの内部コマンド、万能command tunnel。

**完了条件:** 実bridgeで取得/操作/公開制限と要求非二重化を確認する。

**主担当要件:** BOUND-003, OBS-005, OBS-006, ACTION-001, ACTION-002, ACTION-003

**協力・統合する要件:** VALUE-005, OBS-002, ASSERT-004, ACTION-005, ACTION-006, PLANNER-001, PLANNER-003, PLANNER-005, START-004, START-005, PACK-002, FIX-002, INPUT-001

**具体化が必要な残件:** OPEN-04

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### P-08 Run成果物とGua Trace・Recordingの関連付けを実装する

**起票先:** `gua-playtest`（提案）

**実装・接続範囲:** Preparingからの記録、run/result、設定・入力hash、観測/操作/判断参照、保存状態分離を実装する。

**着手前の契約:** TRACE/FILE要件、Guaが所有する保存形式。

**統合確認の依存:** P-05, P-07, T-04

**対象外:** 独自Trace writer/Viewer、秘密値入り設定の丸ごと複製。

**完了条件:** 成功Trace省略と未実行を区別し、中断・記録失敗でも元の結果を維持する。

**主担当要件:** FILE-007

**協力・統合する要件:** RUN-002, RUN-005, RUN-006, TRACE-001, TRACE-006, TRACE-007, REPLAY-001, PLANNER-009, STALL-006, START-007, FILE-006, CLI-004

**具体化が必要な残件:** 担当要件の受け入れ例・命名を起票時に確認する。

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### P-09 Replay Planの解決・Gua再生・中間検証を実装する

**起票先:** `gua-playtest`（提案）

**実装・接続範囲:** 参照固定、区間、条件同期/記録時刻、初期/中間/Goal、onGoal/afterPlanを接続する。

**着手前の契約:** REPLAY/CLOCK要件、OPEN-07/08。

**統合確認の依存:** P-06, P-08, G-05

**対象外:** AI修復、Trace Snapshot書戻し、操作完走だけでPASS。

**完了条件:** 遅延、部分実行、hash変更、Goal先行、Plan未完了をAIなしで検証する。

**主担当要件:** RUN-004, REPLAY-001, REPLAY-002, REPLAY-003, REPLAY-004

**協力・統合する要件:** BOUND-004, TIME-003, ACTION-004, REPLAY-005, CLOCK-001, CLOCK-007, INPUT-003

**具体化が必要な残件:** OPEN-07, OPEN-08

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### P-10 PlannerInput/Decision・採用ゲート・feedbackを実装する

**起票先:** `gua-playtest`（提案）

**実装・接続範囲:** 一要求一Decision、execute/observe/wait/finish、stale/重複排除、時計・権限・予算再検証を実装する。

**着手前の契約:** PLANNER要件と公開方針、OPEN-09。

**統合確認の依存:** P-01, P-02, P-05

**対象外:** AIへの採点権、confirmed/ownerの自己申告、区間外保持、任意ファイル問い合わせ。

**完了条件:** 台本型Plannerで未送信拒否/送信不明/部分実行のfeedbackが分離される。

**主担当要件:** ACTION-004, ACTION-006, PLANNER-001, PLANNER-002, PLANNER-003, PLANNER-004, PLANNER-005, PLANNER-006, PLANNER-007, PLANNER-010

**協力・統合する要件:** BOUND-003, OBS-006, GOAL-003, ASSERT-010, CLOCK-005, PLANNER-009, INPUT-001, INPUT-003

**具体化が必要な残件:** OPEN-09

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### P-11 Explore・進捗・停滞検知と有限の回復を実装する

**起票先:** `gua-playtest`（提案）

**実装・接続範囲:** Milestone/Metric、最良値、比較可能な反復、回復判断予算、終了理由のoriginを実装する。

**着手前の契約:** STALL要件、初期値案とOPEN-10。

**統合確認の依存:** P-06, P-07, P-10

**対象外:** ゲーム攻略不能の断定、Replay中の回復、自動再起動・内部状態書換え。

**完了条件:** 正常な攻撃反復/往復、観測不明、回復予算非復活、秘密進捗の非漏えいを検証する。

**主担当要件:** STALL-001, STALL-002, STALL-003, STALL-004, STALL-005, STALL-006

**協力・統合する要件:** GOAL-002, ACTION-004, BUDGET-001, PLANNER-010

**具体化が必要な残件:** OPEN-10

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### P-12 Codex App Server接続と権限制限を実装する

**起票先:** `gua-playtest`（提案）

**実装・接続範囲:** stdio、thread/turn、構造化完了応答、中断、利用量、隔離、認証参照を専用moduleへ実装する。

**着手前の契約:** 対応版の公式契約とOPEN-09/11を実装時に確認する。

**統合確認の依存:** P-10

**対象外:** Codexの自動更新・認証同梱、開発権限の丸渡し、隠れた思考全文の必須保存。

**完了条件:** 遅延/切断/無効応答と実接続で、Runner迂回や元データ・非公開値へのアクセスを防ぐ。

**主担当要件:** PLANNER-008, PLANNER-009

**協力・統合する要件:** BOUND-004, CLOCK-001, PLANNER-007, PLANNER-010, START-007, FILE-006, PACK-004, FIX-006

**具体化が必要な残件:** OPEN-09, OPEN-11

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### P-13 CLI・Environment解決・終了コード・report入口を実装する

**起票先:** `gua-playtest`（提案）

**実装・接続範囲:** validate/explore/replay/plan/report、設定優先、非対話、JSON出力、Gua Viewer呼出しを実装する。

**着手前の契約:** FILE/CLIと最新exit 11契約。CLI骨組みはP-02から着手可能。

**統合確認の依存:** P-01, P-06, P-08, P-09, P-11, P-12, P-14, T-05

**対象外:** CLIへの成否実装重複、任意--set、ReplayからExploreへの自動切替。

**完了条件:** 各コマンドと終了コード、静的validate無起動、AIなしReplayを実配布に近い形で検証する。

**主担当要件:** BOUND-004, FILE-005, FILE-006, CLI-001, CLI-002, CLI-004, CLI-005

**協力・統合する要件:** GOAL-002, GOAL-003, RUN-005, START-001, FILE-001, FILE-003, FILE-007, CLI-003, DELIVERY-002

**具体化が必要な残件:** OPEN-06, OPEN-12

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### P-14 RecordingからPlan候補を生成し、内容固定・試験・採用を管理する

**起票先:** `gua-playtest`（提案）

**実装・接続範囲:** plan create/acceptの処理、管理先コピー、推定条件の区別、試験Runとの照合を実装する。

**着手前の契約:** OPEN-08のhash対象・採用証拠・範囲参照。

**統合確認の依存:** P-01, P-08, P-09

**対象外:** バグ状態を自動期待値化、無断上書き、候補実行による安全制限解除。

**完了条件:** 変更済みPlanの古い採用を拒否し、同じ内容の完走試験と明示採用を関連付ける。

**主担当要件:** REPLAY-005, REPLAY-006, FILE-004, CLI-003

**協力・統合する要件:** FILE-001, CLI-001, DELIVERY-003

**具体化が必要な残件:** OPEN-08

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### P-15 共通fixture・故障注入・独立oracleを用意する

**起票先:** `gua-playtest`（提案）

**実装・接続範囲:** ショップ・移動・敵の小場面、Godot/Unity変種、台本Planner、故障ハーネスとケース定義を用意する。

**着手前の契約:** FIX要件と故障発火・事実確認の独立契約。

**統合確認の依存:** P-02, G-03, G-04

**対象外:** 第二の製品Trace、Plannerに故障解除APIを公開、既存Gua低層テストの丸写し。

**完了条件:** 故障なしの正常性と故障の実発火を独立に確認する。全製品E2E完了はP-16で統合判定する。

**主担当要件:** FIX-001, FIX-002, FIX-003, FIX-004, FIX-005, FIX-006, FIX-007

**協力・統合する要件:** DELIVERY-003

**具体化が必要な残件:** 担当要件の受け入れ例・命名を起票時に確認する。

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### P-16 配布物・互換性・v0.1全体E2Eを検証する

**起票先:** `gua-playtest`（提案）

**実装・接続範囲:** self-contained/.NET Tool/NuGet、native/schema/Viewer、RID、AIあり探索→候補→採用→AIなし再生を検証する。

**着手前の契約:** 全実装と対応表。パッケージ起動smokeはP-02から継続する。

**統合確認の依存:** P-13, P-14, P-15, P-17, G-06

**対象外:** skipを成功と扱うこと、全AI結果を決定的テストへ混ぜること。

**完了条件:** 全要件に実行証拠があり、clean環境の配布物のみで一連の利用が成立する。

**主担当要件:** BOUND-002, PACK-003, PACK-004, PACK-005, DELIVERY-001, DELIVERY-003

**協力・統合する要件:** PACK-001, PACK-006, FIX-001, FIX-004, FIX-005, DELIVERY-002

**具体化が必要な残件:** OPEN-11

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

### P-17 利用文書・正常サンプル・互換性表と責務説明を整える

**起票先:** `gua-playtest`（提案）

**実装・接続範囲:** 導入、profile、Setup、失敗理由、採用、秘密値、保存、対応範囲とGuaとの境界を文書化する。

**着手前の契約:** 採用したAPI/CLIとOPEN-11/12の名称・版。

**統合確認の依存:** P-13

**対象外:** 未検証経路の対応済み表記、fault注入経路の通常サンプル公開。

**完了条件:** 正常サンプルの操作手順を実行し、動作前提と制限を配布物に添える。

**主担当要件:** PACK-006

**協力・統合する要件:** PACK-005

**具体化が必要な残件:** OPEN-11, OPEN-12

**検証計画:** 担当・協力要件に対応するAT-*を使用する。各PRで互換性・公開範囲・中断・秘密値・元失敗保持を確認し、文書と対応表を更新する。現時点で実行証拠はない。

## 開発の確認地点

| 段階 | 先に通すもの | 完了の証拠 |
|---|---|---|
| A | 台帳、schema、五project、Fake、validate外枠 | ゲーム/Codexなしの契約検証とRun状態遷移 |
| B | Gua Value/Observe/Input、#107、Trace基盤、Runner | 共通データが実Guaと同じ意味で往復 |
| C | 小ショップの実接続と台本Planner | UI入力→取引→状態判定→記録→cleanup |
| D | Gua時間制御とReplay Plan | 遅延・保持・中間検証・部分実行をAIなしで検証 |
| E | Decision gate、進捗、停滞、回復 | 台本Plannerで全終了分岐と制約を確認 |
| F | Codex接続と技術的隔離 | 実AIでもRunnerを迂回できず中断が可能 |
| G | 候補作成・採用・CLI・全配布経路 | Exploreから正式Replayまで同じ成果物を受け渡す |

全要件はv0.1の完了対象であり、この段階表を機能削減・無期限延期の口実にしない。

## 横断的な定義

安全性、秘密値、互換性、cleanup、Traceの非干渉は各作業の完了条件である。P-16やT-06だけに責任を押し込めない。

Guaの基盤要件はGua側で自己完結して試験できるようにする。Playtest固有のfixtureがなくてもGuaのreleaseが論理的に成立する依存関係を保つ。利用側のP-16は、公開Gua配布物との統合の最終確認を行う。

## 起票時の確認

G-/T-/P-IDを実Issue番号へ対応付ける。親子関係と統合依存を別欄にする。既存Issueのopen/closed、関連PR、移管先、既存コードを再取得して確認し、重複Issueを避ける。機能が既に存在すれば、不足契約・接続・試験・文書だけを起票する。

GitHubの本文/ラベル/PR/リポジトリは、この文書の作成では変更していない。

## 現在のIssue対応と追加依存

- P-01: https://github.com/gua-project/gua-playtest/issues/2
- P-02: https://github.com/gua-project/gua-playtest/issues/3
- P-03: https://github.com/gua-project/gua-playtest/issues/4
- P-04: https://github.com/gua-project/gua-playtest/issues/5
- P-05: https://github.com/gua-project/gua-playtest/issues/6
- P-06: https://github.com/gua-project/gua-playtest/issues/8
- P-07: https://github.com/gua-project/gua-playtest/issues/7
- P-08: https://github.com/gua-project/gua-playtest/issues/9
- P-09: https://github.com/gua-project/gua-playtest/issues/10
- P-10: https://github.com/gua-project/gua-playtest/issues/11
- P-11: https://github.com/gua-project/gua-playtest/issues/12
- P-12: https://github.com/gua-project/gua-playtest/issues/13
- P-13: https://github.com/gua-project/gua-playtest/issues/15
- P-14: https://github.com/gua-project/gua-playtest/issues/14
- P-15: https://github.com/gua-project/gua-playtest/issues/16
- P-16: https://github.com/gua-project/gua-playtest/issues/18
- P-17: https://github.com/gua-project/gua-playtest/issues/17
- G-01: https://github.com/gua-project/gua/issues/118
- G-02: https://github.com/gua-project/gua/issues/119
- G-03: https://github.com/gua-project/gua/issues/120
- G-04: https://github.com/gua-project/gua/issues/121
- G-05: https://github.com/gua-project/gua/issues/122
- G-06: https://github.com/gua-project/gua/issues/129
- T-01: https://github.com/gua-project/gua/issues/123
- T-02: https://github.com/gua-project/gua/issues/124
- T-03: https://github.com/gua-project/gua/issues/125
- T-04: https://github.com/gua-project/gua/issues/126
- T-05: https://github.com/gua-project/gua/issues/127
- T-06: https://github.com/gua-project/gua/issues/128
- P-26: https://github.com/gua-project/gua-playtest/issues/26

#26は#2の登録/識別契約、#9のRun/result、#15の入口、Gua#127の共通Viewerを統合する。#17へ説明、#18と親#1へ実Runner/browser/配布物証拠を追加する。#2のコード接続は#3基盤を取り込む。着手依存と統合/リリース証拠を区別する。
