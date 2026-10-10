# 合意済み仕様台帳

## 要件から実装を探すために

この台帳は製品が守るべき意味と受け入れ条件を調べる入口です。例えば「購入応答がないとき再送しない」は、操作の責務・観測・結果・Traceの複数要件に関係します。まず関係する要件IDと `AT-*` を読み、[traceability.json](traceability.json)の実Issue対応から実装担当へ進んでください。

`P-*`／`G-*`／`T-*` は当初の作業案です。以下の未実行表記は台帳作成時の受け入れ計画で、現在の全実装が未着手という意味ではありません。現在のコードを読む入口は [開発者ガイド](developer-guide.ja.md)、詳細な形式・判定の正本は [contracts.md](contracts.md)と各契約書です。要件IDを持つテストでも、Fake・通信・エンジンのどの境界を確認したかを読み分けます。

この台帳の要件は会話上の設計合意を整理したもの。**実装状況は未監査、受け入れ確認は全件未実行**。G-/T-/P-IDは起票前の担当案である。未確定の詳細は03-open-decisions.mdへ切り離す。

## BOUND: 製品境界と作業方針

### BOUND-001 別製品・一方向依存

gua-playtestをGuaと別リポジトリにし、GuaはPlaytestを参照しない。観測・入力・汎用Replay・TraceはGua、目的・探索・実行判定はPlaytestが持つ。

**主担当案:** `P-02`　**協力:** `G-06`

**AT-BOUND-001（未実行）:** 依存グラフにGua→Playtestがなく、Gua側CIがPlaytestの完成を前提にしない。

### BOUND-002 v0.1の範囲

承認済みのlist/set、時間契約、Trace/Replay/Planner/CLI/配布等をv0.1で実装する。開発順序を分けても未対応機能を黙って削らない。

**主担当案:** `P-16`　**協力:** `P-01`

**AT-BOUND-002（未実行）:** 要件IDごとに実装・検証を確認し、Fakeのみやskipでリリース条件を満たしたことにしない。

### BOUND-003 プレイ経路を守る

Runningでは公開されたUI操作・Semantic Game Input・必要なRaw Inputを使う。内部ロジック直接呼出し、teleport、所持品の追加などで達成を飛び越えない。

**主担当案:** `P-07`　**協力:** `P-10`

**AT-BOUND-003（未実行）:** 同じゲーム内状態に内部APIで到達できても、そのAPIがPlannerの操作一覧・通路に存在しない。

### BOUND-004 非AI実行

Replay・静的検証・保存結果のreportにPlannerを必須としない。AIは経路の提案者であり採点者ではない。

**主担当案:** `P-13`　**協力:** `P-09` / `P-12`

**AT-BOUND-004（未実行）:** Codex未導入・未認証環境でvalidate/replay/reportの必要範囲が動く。

### BOUND-005 作業許可の履歴

2026-09-16の台帳は起票前の計画のみであり、起票が実装許可を意味しなかった。2026-10-06の明示依頼は全Issueの専用threadでの実装とdraft PRを許可する。mergeは最終headの実CI成功と実Codex GitHub review完了・actionable指摘ゼロが条件。release、credential/security変更は含まない。

**主担当案:** `P-01`　**協力:** なし

**AT-BOUND-005:** 起票前の制限と現在の明示実装許可・merge gateを別履歴として保持する。

## VALUE: 共通Value

### VALUE-001 型集合

bool/integer/number/string/enum/list/setを最初から扱い、wireはtypeとvalueを持つ共通表現を使う。

**主担当案:** `G-01`　**協力:** `P-03`

**AT-VALUE-001（未実行）:** 全型の正常例と空collectionが往復でき、numberとintegerの区別が残る。

### VALUE-002 禁止値と範囲

追加観測Valueにはnull、任意object、object参照、入れ子collection、BigIntを許さない。integerは±9007199254740991以内、numberは有限のみとする。

**主担当案:** `G-01`　**協力:** `G-02`

**AT-VALUE-002（未実行）:** 境界整数、NaN/Infinity、null、object、nested list/setを拒否し、理由を識別する。

### VALUE-003 enum表現

enumTypeと名前文字列をwireの意味とし、内部numeric valueを送らない。enum型の候補一覧をschema側で取得可能にする。名前の違うenumTypeは同じ文字列でも同じ型としない。

**主担当案:** `G-01`　**協力:** `G-03`

**AT-VALUE-003（未実行）:** BossPhase.Secondと別enumのSecondを混同せず、候補型情報が取得できる。

### VALUE-004 list/set

要素は同種scalar/enum。elementTypeとenumTypeを明示し空でも型を残す。listは順序あり、setは順序なし・重複禁止。

**主担当案:** `G-01`　**協力:** `P-03`

**AT-VALUE-004（未実行）:** 並べ替えたsetの等価とlistの非等価、重複setの拒否を確認する。

### VALUE-005 共通Valueの適用範囲

Observe/Property、関連Snapshot/Change、Assertionと注釈で共通Valueを再利用する。既存Gua InputのVector2 objectや既存のnullable fieldまで、この新規制限で書き換えない。

**主担当案:** `G-01`　**協力:** `P-01` / `P-07`

**AT-VALUE-005（未実行）:** 新規Valueのnull拒否と、既存形式の互換読取が両立する。

## OBS: 登録・観測・識別

### OBS-001 登録API

[object/ui].Observeは各ノード、world.PropertyはWorld全体へ読み取り専用の値を公開する。enemy1.phase等をグローバル文字列で管理しない。

**主担当案:** `G-02`　**協力:** `G-03`

**AT-OBS-001（未実行）:** 同じphase名を別の敵に登録でき、Ownerの寿命に従って消える。

### OBS-002 既存情報の再利用

UI TreeとWorld Object Treeを第一の観測源にし、位置などを二重登録しない。未公開のゲーム固有状態だけを追加観測値で補う。

**主担当案:** `G-03`　**協力:** `P-07`

**AT-OBS-002（未実行）:** 実ObjectのworldPositionと追加phaseを同じ観測へ関連付ける。

### OBS-003 Snapshot/Change

Snapshotは現在状態、Changeはbefore/after。追加はafter、削除はbeforeのみとし、観測不在や取得エラーのためにnullを捏造しない。

**主担当案:** `G-02`　**協力:** `T-03`

**AT-OBS-003（未実行）:** added/changed/removedと取得不能を別に保存・取得する。

### OBS-004 通知と連続性

公開された値の変化を通知し、中間変化・購読開始・revision/epochの連続性を扱う。具体的な変更検知境界はOPEN-03で固定し、getter登録だけで任意の内部変化を検知できると保証しない。

**主担当案:** `G-02`　**協力:** `G-03` / `T-03`

**AT-OBS-004（未実行）:** Snapshot取得と購読の隙間、欠損、遅い購読者で誤った継続保証をしない。

### OBS-005 参照方法

観測sourceはworld/ui/object。ScenarioはSemantic Selector＋observe/propertyを使い、内部でRuntime ID＋名称へ解決する。session/source/epochも含めて別実行と混ぜない。

**主担当案:** `P-07`　**協力:** `P-03` / `T-02`

**AT-OBS-005（未実行）:** 再生成された対象を再解決し、古いepochのIDを現状態に使わない。

### OBS-006 公開範囲・完全性

AIへ渡す範囲とRunnerの判定用範囲を分ける。検索scope/profile、truncated、省略、取得失敗、古い値を保持し、0件や空状態へ変換しない。

**主担当案:** `P-07`　**協力:** `P-10` / `T-03`

**AT-OBS-006（未実行）:** 非公開対象の存在をエラーや待機応答で漏らさず、打切りを完全検索と見なさない。

## GOAL: Scenarioの意味

### GOAL-001 単一Goal

1 Scenario = 1 Goal。自然言語objectiveと機械判定successを分け、failureは任意。Goalの中に操作手順・外部Goal参照・複数Goal workflowを持たせない。

**主担当案:** `P-01`　**協力:** `P-05`

**AT-GOAL-001（未実行）:** 同じScenarioをExploreとReplayで使用し、Plannerの主張だけでPassedにならない。

### GOAL-002 未検証探索

successを持たない自然言語のみの探索は許すが、終了してもUnverifiedでありCIのPassedにしない。successがあるのに未確認という理由でUnverifiedへ逃がさない。

**主担当案:** `P-05`　**協力:** `P-11` / `P-13`

**AT-GOAL-002（未実行）:** success有無ごとの終了statusと終了コードを検証する。

### GOAL-003 開始状態と制約

setupは望む開始状態、実現方法はEnvironmentの承認済みfixtureへ委譲。時間・操作数・権限制限は構造化し、objective内のお願いだけで代用しない。

**主担当案:** `P-06`　**協力:** `P-10` / `P-13`

**AT-GOAL-003（未実行）:** 禁止操作を自然言語に関係なくRunnerが止め、setup処理成功と開始前提成立を区別する。

## ASSERT: Assertionと整合性レビュー

### ASSERT-001 比較構造

Target/Operator/期待Valueを分離。Scalarのequals/notEquals、数値大小、numberのapproximatelyEqualsと許容差を扱う。型不一致はfalseでなく設定不正とする。

**主担当案:** `P-03`　**協力:** `P-01`

**AT-ASSERT-001（未実行）:** 型の不正、数値境界、近似値を独立期待値で検証する。

### ASSERT-002 文字列・collection演算

stringのcontains/startsWith/endsWith/matches、list/setのcontains/notContains/containsAll/containsAny/isEmpty/isNotEmpty/count系、listのsequence系を実装する。containsSequenceは連続部分列。

**主担当案:** `P-03`　**協力:** なし

**AT-ASSERT-002（未実行）:** 演算子×型表の有効・無効組合せを網羅し、regexはRunnerで評価する。細部はOPEN-02。

### ASSERT-003 等価・要素型

enumTypeを含む型一致を要求する。list/setのequalsで相互変換せず、containsの期待値は要素型、containsAll/Anyはcollectionとする。

**主担当案:** `P-03`　**協力:** `G-01`

**AT-ASSERT-003（未実行）:** `list<string>`対`set<string>`のequalsを拒否し、enum collectionの型を保持する。

### ASSERT-004 存在・対象数

exists/notExists/count系はSelector結果を評価。quantifierはone/any/all/noneでcountは含めない。複数を先頭一件へ暗黙縮約しない。

**主担当案:** `P-04`　**協力:** `P-07`

**AT-ASSERT-004（未実行）:** 複数一致のoneで未送信停止し、件数演算が要素比較と混ざらない。

### ASSERT-005 0件・打切り

完全検索0件でexists/any/allはfalse、notExists/noneはtrue。oneは比較成立させない。全件を必要とするall/none/件数判定では完全性を要求する。

**主担当案:** `P-04`　**協力:** なし

**AT-ASSERT-005（未実行）:** 空allが成功せず、部分結果だけで全体を確認済みにしない。

### ASSERT-006 三値評価

True/False/Unknownを値と別に扱う。取得不能なnotEqualsをtrueにしない。all(True,Unknown)=Unknown、all(False,Unknown)=False、any(True,Unknown)=True、any(False,Unknown)=Unknown。

**主担当案:** `P-03`　**協力:** `P-04`

**AT-ASSERT-006（未実行）:** 条件不成立・観測不能・不正設定・観測契約違反を分離する。

### ASSERT-007 通常allの評価時点

時間指定のないallは同じ評価に使用した観測の組合せで成立する。個々の通常条件を一度trueになっただけで永久成立にしない。

**主担当案:** `P-04`　**協力:** なし

**AT-ASSERT-007（未実行）:** A=true/B=falseの後A=false/B=trueとなっても通常allは成功しない。

### ASSERT-008 時間成立履歴

within/forを満たした条件の成立事実は保持する。条件ごとにwithinを置けば別時点成立を許す。group全体に時間指定を置けばgroupの成立・維持を検証する。

**主担当案:** `P-04`　**協力:** なし

**AT-ASSERT-008（未実行）:** 別々の時間条件とgroupに掛けた時間条件を区別する。

### ASSERT-009 ツリー全体の期限

個別withinの期限切れをScenario失敗へ直結しない。success.anyの他分岐は継続、必要allの不可能は全体へ伝播。failure条件が期限内に成立しないこと自体を失敗にしない。

**主担当案:** `P-04`　**協力:** `P-05`

**AT-ASSERT-009（未実行）:** any一枝期限切れ、failure期限切れ、Goal確認済み後の未使用分岐を検証する。

### ASSERT-010 評価の公開と必須情報

anyの別分岐がtrueでも不正設定を無視しない。必須failure監視の破損をPassedで隠さない。評価結果の公開もprofileを守る。

**主担当案:** `P-04`　**協力:** `P-10`

**AT-ASSERT-010（未実行）:** 無効な未評価枝と非公開条件のフィードバックを検査する。

## TIME: 条件の時間

### TIME-001 within/for

withinは監視開始から成立し始める期限、forは連続維持時間。within=5s/for=2sで4.5s開始→6.5s維持は成立、5sを過ぎた新しい維持開始は採用しない。

**主担当案:** `P-04`　**協力:** なし

**AT-TIME-001（未実行）:** 期限前開始・期限後維持完了・途中false・期限後再開始のケースを通す。

### TIME-002 評価契機

初回評価・変更通知・期限タイマーで進行する。for中のfalseは計測リセット。必要な観測連続性が失われた区間を維持成功と認めない。

**主担当案:** `P-04`　**協力:** `G-02`

**AT-TIME-002（未実行）:** 変更通知がなくてもfor期限で評価し、欠損をtrue維持へ変換しない。

### TIME-003 時間起点

Scenario success/failureはRunningから、Replay途中waitはその点到達から。検証点到達前の未監視時間をforへ加算しない。

**主担当案:** `P-04`　**協力:** `P-09`

**AT-TIME-003（未実行）:** 同じ条件を異なる配置で実行し起点が明示される。

### TIME-004 全体期限との関係

withinの成立開始は期限以下を認める。Scenario maxDurationはRunningからの実時間の到達で終了し、同一評価単位ではsuccessより優先。

**主担当案:** `P-05`　**協力:** `P-04`

**AT-TIME-004（未実行）:** within=5sと全体5sの境界で、条件事実とScenarioのTimedOutを分ける。

## RUN: Run・結果・競合

### RUN-001 状態機械

ExecutionStateはCreated/Preparing/Running/Completing/Finished、ResultStatusはPassed/Failed/TimedOut/Aborted/Invalid/Unverified。phase・origin・reason・messageを分離する。

**主担当案:** `P-05`　**協力:** `P-01`

**AT-RUN-001（未実行）:** 各正常異常経路とRun結果の出力が一貫する。

### RUN-002 主結果は一度だけ

Goal確認、主結果確定、後処理終了を別概念にする。Completingへの移行後は主結果を書換えず、Cancel/保存失敗/cleanup失敗を別に記録する。

**主担当案:** `P-05`　**協力:** `P-08`

**AT-RUN-002（未実行）:** report生成中CancelでFailedをAbortedへ書換えず、遅い成功応答も過去へ反映しない。

### RUN-003 競合処理

単一の実行制御で同じ評価単位の候補を扱う。優先は不正契約、確定failure/継続不能エラー、中断、全体期限、達成不能/終端予算、Passed。未採用原因も記録する。

**主担当案:** `P-05`　**協力:** `T-04`

**AT-RUN-003（未実行）:** failure/success/中断/期限の競合を再現し、callbackの書込競合で結果が変わらない。OPEN-06の終端述語を固定する。

### RUN-004 GoalとPlan完了

Goal success全体の成立事実は保持する。Explore onGoal、回帰Replay afterPlanを既定案とし、afterPlanはPlanと中間検証・必要解除・完了確認まで実行しfailure監視を継続する。

**主担当案:** `P-09`　**協力:** `P-05`

**AT-RUN-004（未実行）:** Goal先行時の未実行後半を区別し、Plan内解除失敗は確定前の実行失敗になる。

### RUN-005 後処理不完全

主結果Passedでも必須artifact/入力/資源の解放要件が未達なら通常成功終了にしない。CLI 11と後処理理由を返す。元がFailed等なら元のcode/statusを維持する。

**主担当案:** `P-05`　**協力:** `P-13` / `P-08`

**AT-RUN-005（未実行）:** Passed＋InputReleaseUnconfirmedとFailed＋保存失敗を区別する。

### RUN-006 取消と証拠

結果確定前は各段階で取消を扱い、確定後は有限の後処理中断として扱う。可能な診断を資源解放前に取り、元例外type/stackを維持する。

**主担当案:** `P-05`　**協力:** `P-08`

**AT-RUN-006（未実行）:** Setup途中CancelとCompleting中Cancelで必要な所有資源だけを解放する。

## ACTION: 操作の意味・失敗

### ACTION-001 操作単位・ID

一つのGua要求を一操作試行とし、runId、actionExecutionId、入力名actionId、Gua requestIdを区別する。Selectorと今回の解決IDを記録する。

**主担当案:** `P-07`　**協力:** `T-02`

**AT-ACTION-001（未実行）:** 同じmoveを繰り返しても履歴が一つに潰れず、別RunのrequestIdと混ざらない。

### ACTION-002 三つの事実

入力要求の処理結果、観測変化、Scenarioの検証結果を分離する。前後変化だけから操作との因果を断定しない。

**主担当案:** `P-07`　**協力:** `P-03` / `T-03`

**AT-ACTION-002（未実行）:** 攻撃入力成功でも空振りならHP変化・Goal成功を捏造しない。

### ACTION-003 Action分類

Succeeded/Failed/Rejected/TimedOut/AbortedはPlaytestの整理用。元Gua結果と確認済み段階を残す。Timeout/Abortedは未実行や巻戻しを意味しない。

**主担当案:** `P-07`　**協力:** `P-05`

**AT-ACTION-003（未実行）:** 処理済み購入の応答だけ欠落させ、未確認と未実行を区別する。

### ACTION-004 拒否と再判断

Exploreの未送信拒否は予算内で再判断可能。Replayは規定wait/再解決以外でPlan修復しない。ホスト失敗・送信後未確認は既定で停止/安全整理へ進む。

**主担当案:** `P-10`　**協力:** `P-09` / `P-11`

**AT-ACTION-004（未実行）:** 無効Decisionの再判断と、送信済み購入の再送禁止を別に検証する。

### ACTION-005 保持と後処理

開始要求成功と保持終了は別。通常解除は明示的な要求、leaseは保護期限。解除未確認を期限だけで解除済みにしない。cleanupは所有入力に限定する。

**主担当案:** `G-05`　**協力:** `P-07` / `P-05` / `T-02`

**AT-ACTION-005（未実行）:** 別ownerの保持を残し、自ownerの解除結果と未確認状態を照合する。

### ACTION-006 既存commandを限定利用

ui/semantic/rawの操作定義と値はGuaを利用する。Plannerへ全Guaコマンドを透過せず、reset/clock変更等はRunnerが管理する。

**主担当案:** `P-10`　**協力:** `P-07`

**AT-ACTION-006（未実行）:** 未許可type、自己申告confirmed、任意内部コマンドを送信前に拒否する。

## BUDGET: 予算

### BUDGET-001 カウンター分離

maxActionsは送信する各プレイ要求、判断回数はPlanner要求、停滞用は行動Decision、回復用は回復中判断を数える。回復判断は通常予算にも算入する。

**主担当案:** `P-05`　**協力:** `P-11`

**AT-BUDGET-001（未実行）:** 3操作Timed Segmentが操作3・行動1となり、回復予算を二重に無料化しない。

### BUDGET-002 区間予算の予約

Timed Segment全体分を開始前に確保する。途中未送信と送信済みを分ける。送信した要求は結果が失敗でも試行数に含める。

**主担当案:** `P-05`　**協力:** `G-05`

**AT-BUDGET-002（未実行）:** 残り2操作で開始/jump/解除の3操作を一部だけ始めない。

### BUDGET-003 最後の操作の結果

予算到達で新しい操作を止めても、送信済み結果と承認済みwait/判定は各期限内で継続する。cleanupを予算で省略せず、全体実時間は延長しない。

**主担当案:** `P-05`　**協力:** `P-04`

**AT-BUDGET-003（未実行）:** 最後の購入応答とsuccessを確認できる。無限の新規waitで延命しない。

## TRACE: Gua Trace #109改訂

### TRACE-001 記録と判断の分離

操作・Assertion・観測・lifecycleは共通Gua Traceへ集約する。Playtest独自Timeline/Viewerは作らず、Goal/判断/進捗は名前空間付き注釈・attachmentで結び付ける。

**主担当案:** `T-04`　**協力:** `P-08`

**AT-TRACE-001（未実行）:** Playtestを参照しない汎用writer/ViewerでPlaytest注釈を読める。

### TRACE-002 モデルと相関

Session/Step/Eventを分け、kindはaction/assertion/mark/lifecycle。source/epoch/request/step相関でnativeとclientを二重操作にせず、rawでsource locationがない場合も許す。

**主担当案:** `T-02`　**協力:** `T-01`

**AT-TRACE-002（未実行）:** 並列接続と自動/明示記録の併用で混同・二重計上しない。

### TRACE-003 時刻の区別

収集の単調時計・記録順と、ホストの発生時刻/frame/revisionを分離する。時計対応が不明なら未保証とする。

**主担当案:** `T-01`　**協力:** `T-02`

**AT-TRACE-003（未実行）:** 古い時刻の遅延結果を受信順と混ぜ、確定済み結果を書き換えない。

### TRACE-004 Snapshotの二層

redaction後の状態Blobをhashで重複排除し、別時点のObservation recordを残す。UI/Worldが別取得なら原子的同時Snapshotと称さない。

**主担当案:** `T-03`　**協力:** なし

**AT-TRACE-004（未実行）:** 同じ内容の複数時点が失われず、mask前の秘密値hashを出力しない。

### TRACE-005 Traceとbaseline

Traceは公開された位置/frame等の調査情報を保存する。#108のbaseline用既定除外をそのまま適用せず、比較結果は別添付とする。

**主担当案:** `T-03`　**協力:** `T-06`

**AT-TRACE-005（未実行）:** 位置だけ変わる入力でもTraceで変化を確認できる。

### TRACE-006 観測区間と中間変化

before/afterの取得契機、結果決定時とcleanup後を区別し、中間Changeを受信範囲で保存する。不在/欠損/古い値/取得失敗を区別する。

**主担当案:** `T-03`　**協力:** `P-08`

**AT-TRACE-006（未実行）:** Secondを経由する記録と欠損時の未保証がViewerに残る。

### TRACE-007 保存方針

captureMode recent/streaming、savePolicy onFailure/alwaysを独立実装。既定recent・100 step・onFailure、成功探索保存にはstreaming/alwaysを利用できる。

**主担当案:** `T-01`　**協力:** `P-08`

**AT-TRACE-007（未実行）:** 全組合せ、非成功/結果不明の異常終了、100step超を検証する。

### TRACE-008 容量・中断耐性

メモリ・キュー・全artifact・個別添付に有限上限を設け、上限・切詰めを明示する。末尾不完全レコードと未flush消失を完全保存と称さず、参照切れも隠さない。

**主担当案:** `T-01`　**協力:** なし

**AT-TRACE-008（未実行）:** 途中停止、ディスク失敗、上限到達を独立fixtureで読取る。

### TRACE-009 公開・安全性

保存用buffer/queue/hash前にredaction、profile維持、Screenshot別方針。文字列/添付をコードとして実行せず、外部URL自動取得やパストラバーサルを防ぐ。

**主担当案:** `T-01`　**協力:** `T-03` / `T-05` / `T-06`

**AT-TRACE-009（未実行）:** 秘密markerと悪意ある添付/HTMLをJSON・Snapshot・HTML・例外で検査する。

### TRACE-010 記録が動作を変えない

Traceは結果キューを横取りせず、clock/入力を操作せず、ゲームスレッドで保存I/Oを待たない。保存/Viewer失敗は主結果と別。

**主担当案:** `T-02`　**協力:** `T-06`

**AT-TRACE-010（未実行）:** Trace有無で相関結果の受信を妨げず、元失敗type/stackが残る。

### TRACE-011 表示と既存連携

Inspector/静的HTMLで同schema/componentを利用。成功/失敗/欠損、未知注釈を表示し、lint/comparison/Recordingへの参照を持つ。

**主担当案:** `T-05`　**協力:** `T-06`

**AT-TRACE-011（未実行）:** 未知namespaceでも閲覧でき、記録がない場合に空Traceを成功と表示しない。

## REPLAY: RecordingとReplay Plan

### REPLAY-001 三つの成果物

Traceは証拠、Gua Recordingは操作列、Replay PlanはScenario/Recording・時間方針・検証点の関連。Goalへ手順を追加しない。

**主担当案:** `P-09`　**協力:** `P-01` / `P-08`

**AT-REPLAY-001（未実行）:** 同じScenarioのExploreとReplayで成功条件の意味を維持する。

### REPLAY-002 二つの再生方針

記録タイミング重視と条件同期重視をv0.1から扱う。条件同期は待ち方と同一Selector再解決までで、別対象・別経路へ修復しない。

**主担当案:** `P-09`　**協力:** `G-05`

**AT-REPLAY-002（未実行）:** 見つからないUIを類似対象へ置換せず、Replay失敗をExploreで上書きしない。

### REPLAY-003 開始と中間検証

Scenario setup、操作定義・権限・秘密入力・時計の適合を確認し、開始/途中/Goalの検証を既存DSLで行う。Trace Snapshotをゲームへ書戻して復元しない。

**主担当案:** `P-09`　**協力:** `P-06`

**AT-REPLAY-003（未実行）:** 初期から所持済みケースと中間の経路破損を検証する。

### REPLAY-004 完走と成功の分離

操作列の完走だけでPassedにしない。列が尽きても承認済み条件を期限内で待てる。onGoalは省略後半を明示、afterPlanはPlan完了を要求する。

**主担当案:** `P-09`　**協力:** `P-05`

**AT-REPLAY-004（未実行）:** 操作全部SucceededでもGoal未達はPassedにならない。

### REPLAY-005 候補と採用

探索記録から候補作成、条件確認、AIなし試験、明示採用の順。観測したバグを期待値へ自動昇格しない。正式PlanのRecordingは一時Run領域に依存し続けない。

**主担当案:** `P-14`　**協力:** `P-09`

**AT-REPLAY-005（未実行）:** 観測の一致だけで回帰テストを合格させず、削除可能Runから管理先へ参照固定する。

### REPLAY-006 内容固定と証拠

Scenario/Recording/Planの内容変更を検知し、採用対象hashと試験Runを結び付ける。候補実行許可は安全制限解除ではない。hash範囲はOPEN-08で固定する。

**主担当案:** `P-14`　**協力:** `P-01`

**AT-REPLAY-006（未実行）:** 採用後の条件や時間変更へ古い合格証拠を流用しない。

## CLOCK: Replayの時間契約

### CLOCK-001 時計の明示

realtimeを既定とし、simulationは制御対象が明示された対応環境だけで使う。未対応を実時間へ自動降格しない。AI判断待ちを実時間予算から勝手に除外しない。

**主担当案:** `G-05`　**協力:** `P-09` / `P-12`

**AT-CLOCK-001（未実行）:** clockの存在だけでエンジン全体の決定論を保証しない。

### CLOCK-002 時間予算の領域

segment時刻は指定時計、条件は実行方針で固定した時計、maxDuration/通信/準備/cleanupの安全期限は実時間で動く。

**主担当案:** `P-05`　**協力:** `P-04` / `G-05`

**AT-CLOCK-002（未実行）:** simulation停止中でも接続・cleanupの期限が動く。

### CLOCK-003 共通起点

準備が整った区間の開始＋offsetで入力を予定する。前のAction結果待ち時間を次のoffsetへ加算しない。

**主担当案:** `G-05`　**協力:** なし

**AT-CLOCK-003（未実行）:** 結果応答を遅らせても開始・jump・解除の送信予定が後ろへずれない。

### CLOCK-004 順序と適用

同offsetは記録順。必要な順序保証がない経路は拒否する。送信時刻/ホスト適用時刻/結果受信を区別し、同tick同時適用は別の能力要件とする。

**主担当案:** `G-05`　**協力:** `T-02`

**AT-CLOCK-004（未実行）:** 送信順と実適用を照合し、取得不能な厳密適用時間を未確認とする。

### CLOCK-005 区間の境界

保持入力はTimed Segment内で開始・解除まで閉じる。条件waitは外側、境界で自owner入力の中立を確認。複合入力は同一区間で重ねられる。

**主担当案:** `G-05`　**協力:** `P-10`

**AT-CLOCK-005（未実行）:** 保持したままAIへ返るsingleやsegment内条件分岐を拒否する。

### CLOCK-006 解除とlease

開始応答の遅延を埋めるため解除を遅らせない。leaseは保護期限で通常終了は明示解除。時計が異なるleaseの余裕を検証し、勝手に延長/再入力しない。

**主担当案:** `G-05`　**協力:** `P-05`

**AT-CLOCK-006（未実行）:** 早いlease切れを正常時間再現とせず、解除未確認を記録する。

### CLOCK-007 許容遅延

有限maxLatenessを設定・記録し、超過時は遅れた操作をまとめ打ちせず未送信プレイを止め、必要な解除を試みる。20msは説明用例で共通固定値ではない。

**主担当案:** `G-05`　**協力:** `P-09`

**AT-CLOCK-007（未実行）:** 遅延復帰でjump/attackをまとめずReplayTimingViolation等の原因を残す。

## PLANNER: Planner契約・実行ゲート

### PLANNER-001 責務

AIは提案者。実行・制限・時間・成功判定はRunner、観測入力はGua。独自Capability定義やMCP経由の無制限操作を復活させない。

**主担当案:** `P-10`　**協力:** `P-07`

**AT-PLANNER-001（未実行）:** AIがゲームを書き換えて採点を通す経路を持たない。

### PLANNER-002 判断単位

Run内で有効な判断要求は原則一つ、一要求一Decision。runId/decisionRequestId/basedOnObservationIdを検証し、取消・終了・重複・stale応答を実行しない。

**主担当案:** `P-10`　**協力:** なし

**AT-PLANNER-002（未実行）:** 同じ応答を複数投入しても操作を二重送信しない。

### PLANNER-003 PlannerInput

目的、実制限、残量、取得範囲と時点を伴う観測、既存Gua操作定義、構造化feedbackを渡す。Scenario全文や接続秘密を丸渡ししない。

**主担当案:** `P-10`　**協力:** `P-07`

**AT-PLANNER-003（未実行）:** 差分基準が消えたら再同期し、取得不能/省略/0件を区別する。

### PLANNER-004 Decision分岐

execute/observe/wait/finishの一つ。executeはsingle/timed、observeは許可readのみ、waitは有限時間/既存条件、finishはgoalClaimed/stuck/cannotProceedの報告。

**主担当案:** `P-10`　**協力:** `P-01`

**AT-PLANNER-004（未実行）:** 複数kind、未知制御field、無制限区間、任意shell/URL/ファイル要求を拒否する。

### PLANNER-005 二段階検証

完了した構造化応答だけを検証し採用。schema適合と現在の権限/予算/Action定義/context/対象・時間能力の適合を別に検査する。

**主担当案:** `P-10`　**協力:** `P-07`

**AT-PLANNER-005（未実行）:** 部分JSONを実行せず、revision変化だけで永久却下せず関連前提を再確認する。

### PLANNER-006 AIが決めない項目

confirmed、owner、request ID、時計、制限、Goalの採点、任意profile昇格をAIへ与えない。提案waitを成功条件へ昇格しない。

**主担当案:** `P-10`　**協力:** なし

**AT-PLANNER-006（未実行）:** 自己申告承認や非公開値waitを未送信で拒否する。

### PLANNER-007 並行監視と中断

AI思考中もfailure/期限/停止監視を続ける。操作ゲートを閉じて必要解除を先に試し、その後Planner中断を要求する。通常の次判断は保持入力中立で行う。

**主担当案:** `P-10`　**協力:** `P-05` / `P-12`

**AT-PLANNER-007（未実行）:** AI無応答でも既定終了と解除ができ、遅延応答が実行されない。

### PLANNER-008 Codex backend

ローカルApp Serverとのstdio通信を専用moduleに置き、Runごと新session/thread、判断ごとturnを基本とする。対応版検証、取得可能な利用量、中断、認証参照を扱う。

**主担当案:** `P-12`　**協力:** なし

**AT-PLANNER-008（未実行）:** Runner schemaにthread IDを必須化せず、Codex未導入Replayを阻害しない。

### PLANNER-009 隔離・秘密・診断

標準開発権限を丸渡しせず、ソース/セーブの変更や非公開答えの読取、直接Gua操作を迂回できない実行制限を検証する。思考全文は必須ログにせず短い理由と根拠参照を残す。

**主担当案:** `P-12`　**協力:** `P-10` / `P-08`

**AT-PLANNER-009（未実行）:** ゲーム内文言をシステム指示へ昇格せず、送信前と保存前の両方でredactionする。

### PLANNER-010 Planner由来の失敗

無効提案は未送信で回数制限付き再判断、継続不能はPlannerOutputInvalid/PlannerTimeout等でoriginを分ける。AI障害をScenarioの形式不正にしない。

**主担当案:** `P-10`　**協力:** `P-12` / `P-11`

**AT-PLANNER-010（未実行）:** 無効応答・利用上限・接続障害とゲームのfailure条件を区別する。

## STALL: 進捗・停滞・探索終了

### STALL-001 進捗は任意

状態変化・新情報・進捗・成功は別。進捗定義がない場合は不明であり進捗ゼロとしない。任意Milestone/MetricはGoal成功を代替しない。

**主担当案:** `P-11`　**協力:** なし

**AT-STALL-001（未実行）:** アニメーション変化だけで進捗扱いせず、指標なしでも予算で終了を保証する。

### STALL-002 Milestone/Metric

Milestoneは実行中の初到達のみ。初期成立を前進として数えない。Metricは認定済み最良値からminImprovement以上の改善、反復回復やRuntime ID差替えで履歴を復活させない。

**主担当案:** `P-11`　**協力:** `P-03`

**AT-STALL-002（未実行）:** HP100→90→100→90を二度の改善とせず、小改善の累積を正しく扱う。

### STALL-003 停滞の兆候

定義済み進捗の未更新、比較可能な同状況＋同操作列反復、Planner申告を入口にする。操作名のみ/更新revisionのみで断定せず、欠損は判定不能。

**主担当案:** `P-11`　**協力:** なし

**AT-STALL-003（未実行）:** 正常な攻撃とHP改善を停滞にせず、位置往復と比較不可能を区別する。

### STALL-004 回復境界

区間/要求/解除/正当なwaitの途中へ停滞回復を割込ませない。Running内部のExploring/SuspectedStall/Recoveringで既存Decisionを使う。

**主担当案:** `P-11`　**協力:** `P-05`

**AT-STALL-004（未実行）:** 進行中waitを勝手に短縮せず、別経路の試行と再送を混同しない。

### STALL-005 有限回復

Run全体の回復判断予算は回復成功でも復活せず、通常予算も消費する。内部API/自動reset/再起動/条件改変を使わない。Replayでは回復しない。

**主担当案:** `P-11`　**協力:** なし

**AT-STALL-005（未実行）:** 再起動は別Run、Replay失敗は保存され、回復の無限延命ができない。

### STALL-006 終了と情報公開

stalledは今回の探索の限界でありゲーム攻略不能・バグの証明ではない。非公開Metricの詳細をPlannerのfeedbackへ漏らさない。8行動/3反復/3回復は調整可能な初期値案として扱う。

**主担当案:** `P-11`　**協力:** `P-08`

**AT-STALL-006（未実行）:** 結果originと終了理由、非公開進捗の漏えい、初期値の設定出所を確認する。

## START: 開始契約・Setup・所有権

### START-001 実行設定の分離

Scenarioの意味とEnvironmentの起動/接続/fixture/profile/Planner/準備・終了上限を分離し、既存Guaホストを再利用する。

**主担当案:** `P-06`　**協力:** `P-13`

**AT-START-001（未実行）:** 同じScenarioをlaunch/attachへ組合せられ、未指定の別portや実行ファイルを探して実行しない。

### START-002 launch/attach

launchはRunner作成資源を管理、attachは既存プロセスを無断終了/resetしない。原則一つの能動Runで専有し、owner分離をゲーム状態分離と称さない。

**主担当案:** `P-06`　**協力:** `P-05`

**AT-START-002（未実行）:** 並行操作と外部手動入力の保証限界を記録し、別owner/既存プロセスを巻き込まない。

### START-003 Setupとreset

望むcheckpoint/save/scene等を承認済み方法に対応付け、Setup経路をPlannerへ公開しない。Gua resetでゲーム状態を復元したと称さない。current開始は復元可能性を別に示す。

**主担当案:** `P-06`　**協力:** なし

**AT-START-003（未実行）:** Strict開始で残要求を消して隠さず、currentの記録だけで同じ状態へReplay可能と称さない。

### START-004 準備確認

接続先/buildの確認可能な識別、必要protocol/機能/profile/時計、開始前提を検証する。未来のBossやattackが開始画面にないだけで拒否しない。

**主担当案:** `P-06`　**協力:** `P-07`

**AT-START-004（未実行）:** port一致だけで期待buildと断定せず、厳密識別が必要な設定では不足を拒否する。

### START-005 初期境界

Setup後の観測と購読境界を同期し、同じ評価用観測で前提確認、Running境界、初回success/failureを処理してからAIへ渡す。全Tree静止を開始条件にしない。

**主担当案:** `P-06`　**協力:** `P-04` / `P-07`

**AT-START-005（未実行）:** 古いepoch・開始直後死亡・初期success・空UIの正常開始を検証する。

### START-006 準備期限と失敗

Preparingと各処理/cleanupに有限実時間上限。再接続で全体期限をリセットせず、結果不明の副作用Setupを無条件に再試行しない。Preparingの期限はFailed/PreparationTimeout。

**主担当案:** `P-06`　**協力:** `P-05`

**AT-START-006（未実行）:** 起動/接続/Setup/前提不成立をphase/reasonで区別し、再起動を新Runへする。

### START-007 準備中のPlannerとTrace

Exploreは権限付きPlanner環境をPreparingで確認するが判断はRunningから。ReplayはPlannerを要求しない。接続前の起動失敗も共通Traceに記録する。

**主担当案:** `P-06`　**協力:** `P-08` / `P-12`

**AT-START-007（未実行）:** CodexなしReplayと起動前失敗Trace、途中取得済み資源の解放を確認する。

## FILE: 設定ファイル・成果物

### FILE-001 三種類

Scenario/Environment/Replay Planに分離し、kind/schemaVersionを持つ。PlanはScenarioとRecordingを参照し、CLIで別Goalへ無断差替えしない。

**主担当案:** `P-01`　**協力:** `P-13` / `P-14`

**AT-FILE-001（未実行）:** 同じPlanのEnvironment変更と、Scenario変更の拒否を区別する。

### FILE-002 形式と参照

YAML/JSONは同一データモデル。重複キー/未知制御field/コードタグ/循環を拒否し、未対応版を実行しない。Gua schemaは版固定同梱しオンライン最新へ追従しない。

**主担当案:** `P-01`　**協力:** `G-06`

**AT-FILE-002（未実行）:** validateがネットワーク・外部コード・native起動なしで静的に処理できる。

### FILE-003 パス

ファイル内相対パスはそのファイルの場所、CLI引数はcwdを基準。暗黙探索や親設定の自動mergeをしない。許可領域は正規化/解決後のパスで確認する。

**主担当案:** `P-01`　**協力:** `P-13`

**AT-FILE-003（未実行）:** cwd変更でPlanの参照先が変わらず、許可外参照を拒否する。

### FILE-004 固定hash

Scenario/Recording参照はファイルbytesのSHA-256で固定し、実行前に読込んだ内容を保持する。書式/コメント変更でも一致しない。hashは安全性や発行者承認の証明ではない。

**主担当案:** `P-14`　**協力:** `P-01`

**AT-FILE-004（未実行）:** 実行途中差替え・採用後変更・自己参照hashの境界をOPEN-08に従い検査する。

### FILE-005 設定の解決

テストの意味はScenario/Plan、安全上限は厳しい方、許可は共通部分・禁止優先。CLIが変更できるのは承認した運用項目・より厳しい上限。実効値と出所を保存する。

**主担当案:** `P-13`　**協力:** `P-05`

**AT-FILE-005（未実行）:** 100/50/1000操作指定から50となり、simulationをrealtimeへ自動変換しない。

### FILE-006 秘密値

設定は秘密値の参照を持ち、必要分だけ実行時に解決する。全環境変数や平文をAI/Run/例外へ渡さない。保存入力は秘密値処理後の固定コピーとする。

**主担当案:** `P-13`　**協力:** `P-08` / `P-12`

**AT-FILE-006（未実行）:** secretKeyと供給可否を保存し、request/inputコピーに秘密markerが残らない。

### FILE-007 Run成果物

Runごとに一意のdirと最小run/resultサマリーを保存し、任意Trace/Recordingと関連付ける。成功時Trace省略と未実行/保存失敗を分離する。

**主担当案:** `P-08`　**協力:** `P-13`

**AT-FILE-007（未実行）:** 上書きなし、result不在の中断を最後の観測からPassedと補わない。

## CLI: CLIと候補採用

### CLI-001 入口

validate/explore/replay/plan create/plan accept/reportを一つのgua-playtestコマンドにする。実行Environmentは明示し、再生失敗から探索へ自動切替しない。

**主担当案:** `P-13`　**協力:** `P-14`

**AT-CLI-001（未実行）:** 明示した入力ファイル/Environmentのみに従って処理する。

### CLI-002 副作用の分離

validateは静的検証のみ、replayはCodex不要、reportは既存Trace読取表示だけ。通常CLIは非対話、承認不足を自動承認せず、対話は明示する。

**主担当案:** `P-13`　**協力:** なし

**AT-CLI-002（未実行）:** validate成功がScenarioPassedにならず、reportでゲームを再操作しない。

### CLI-003 候補採用手順

plan createは候補のみ。allow-candidateは候補試験を許すだけ。plan acceptは内容一致・同内容のPlan完走成功証拠・明示承認を検証し、変更後に旧採用を流用しない。

**主担当案:** `P-14`　**協力:** `P-13`

**AT-CLI-003（未実行）:** 古いhash、欠損Recording、推定期待値、未完走のonGoal証拠を正式採用へ流用しない。

### CLI-004 出力

通常表示と--jsonを分け、JSON stdoutは一つの最終結果、進捗・診断はstderr。詳細時系列はGua Trace、プロセス出力をJSONへ混ぜない。

**主担当案:** `P-13`　**協力:** `P-08`

**AT-CLI-004（未実行）:** ゲーム/Plannerがstdoutへ大量出力してもCLI結果JSONが壊れない。

### CLI-005 終了コード

実行commandの0 Passed/1 Failed/2 Invalid/3 TimedOut/4 Aborted/5 Unverified/10 Runner異常/11 Passedだが必須後処理未達。非実行commandの0はcommand成功でありテスト成功ではない。

**主担当案:** `P-13`　**協力:** `P-05`

**AT-CLI-005（未実行）:** 最新のexit 11定義と、元結果非Passed時の後処理追加エラーを検証する。

## PACK: 実装構成と配布

### PACK-001 C#単一実装

C#/.NET 10でCore/Runner/GuaIntegration/Planners.Codex/Cliを構成し、ゲーム外で実行する。TypeScript版RunnerとC#版Runnerの二系統は作らない。

**主担当案:** `P-02`　**協力:** `P-16`

**AT-PACK-001（未実行）:** CLIとライブラリが同じRunner規則を使い、ゲーム内へPlanner認証を配置しない。

### PACK-002 依存境界

Coreは純粋な契約・意味規則、Runnerは抽象窓口、具体連携はmodule、CLIは組立/表示。Gua実装/schemaは公開packageを利用し、別実装へコピーしない。

**主担当案:** `P-02`　**協力:** `G-06` / `P-07`

**AT-PACK-002（未実行）:** 正式buildが../guaに依存せず、native無しvalidateとGua-onlyのTrace利用が成立する。

### PACK-003 配布経路

同じCLIをself-contained archiveと.NET Toolにする。通常利用は一入口、ライブラリ利用はNuGet。npm版Runnerや必須のGUIビルドを追加しない。

**主担当案:** `P-16`　**協力:** `G-06`

**AT-PACK-003（未実行）:** clean環境でarchive/toolの同機能を実行し、schema/native/Viewerの不足を検出する。

### PACK-004 版と外部依存

Playtest内は同じ製品版、Guaは独立版を対応表で固定。Codex本体・認証は同梱せず自動更新しない。実際の版をRunへ記録する。

**主担当案:** `P-16`　**協力:** `P-12`

**AT-PACK-004（未実行）:** 対応外の機能を黙って降格せず、必要なら実行前に理由を返す。

### PACK-005 対応範囲

初期配布案はwin-x64/linux-x64/osx-x64/osx-arm64。CLI・native・engine launch/attach・render/headless・Planner制限を別軸で検証し、起動だけで全対応と称さない。

**主担当案:** `P-16`　**協力:** `P-17`

**AT-PACK-005（未実行）:** 各表明経路を配布物で確認し、skipを合格証拠にしない。

### PACK-006 利用者責任と文書

ゲームがGua対応していることが前提。起動/認証/profile/秘密値/保存/採用/失敗理由を文書化し、checksum/依存物/ライセンスを配布する。単一exeやAOTは必須要件にしない。

**主担当案:** `P-17`　**協力:** `P-16`

**AT-PACK-006（未実行）:** サンプル手順を配布物のみで実行し、未検証の宣伝をしない。

## FIX: fixture・故障注入・受け入れ証拠

### FIX-001 検証の三層

契約/RunnerのFake、実Gua接続、Godot/Unity実ゲームを分ける。既存Gua低層テストは再利用し、Fakeの成功で全統合済みとはしない。

**主担当案:** `P-15`　**協力:** `P-02` / `P-16`

**AT-FIX-001（未実行）:** 同じ要件に型・実通信・実入力それぞれの必要証拠が付く。

### FIX-002 三つの小場面

ショップ、移動コース（2D/3D変種）、複数敵の小部屋を用意する。プレイ経路を使い、内部ロジックAPIで近道しない。

**主担当案:** `P-15`　**協力:** `P-07`

**AT-FIX-002（未実行）:** 正常購入、複合入力・壁、phase変更・Object差替え・複数一致を確認する。

### FIX-003 故障の配置

ゲーム処理、Adapter/結果、観測、Planner、保存/終了へ責務別に故障を置き、開始前の承認済み設定で境界発火させる。

**主担当案:** `P-15`　**協力:** なし

**AT-FIX-003（未実行）:** 故障が実発火したことを確認し、Plannerへfault設定や解除操作を公開しない。

### FIX-004 独立oracle

fixture事実、Playtest結果、Trace、外側期待値を照合する。最小の専用カウンター等はテスト用のみ。検証対象の判定コード自身から期待結果を生成しない。

**主担当案:** `P-15`　**協力:** `P-16`

**AT-FIX-004（未実行）:** Replay afterPlanの購入後応答欠落で、一度だけの要求・取引・未確認Timeout・非再送を照合する。

### FIX-005 決定性と許容差

純粋規則は厳密、固定tickはtick、実時間は事前許容遅延、位置は明示許容差、実AIは制約/Goalで検証する。seedだけで全実行を固定したとしない。

**主担当案:** `P-15`　**協力:** `P-16`

**AT-FIX-005（未実行）:** 失敗後の閾値緩和を行わず、fixture版/build/seed/clock/fault/許容差を保存する。

### FIX-006 公開・所有の異常

テスト秘密marker、非公開情報wait、別owner/attachプロセス、capture失敗を検証する。Screenshotの安全性を文字のsensitive指定だけで保証しない。

**主担当案:** `P-15`　**協力:** `P-12` / `T-06`

**AT-FIX-006（未実行）:** 漏えいと他owner解除を外側から独立に検出する。

### FIX-007 仕様横断の競合

allの同時性、時間分岐、三値、空all、打切り、same-cycle failure、全体期限、Completing Cancel、予算区間予約をfixture/契約データへ追加する。

**主担当案:** `P-15`　**協力:** `P-03` / `P-04` / `P-05`

**AT-FIX-007（未実行）:** 直近の整合性レビューの期待結果を固定してから実装で照合する。

## DELIVERY: 実装順序・完了

### DELIVERY-001 依存と段階

仕様・データ→Fake Runner→実Guaと小ゲーム→Replay→探索→実Codex→全体配布確認を基本とする。契約済み領域は並行可能、完成証拠は実接続を要求する。

**主担当案:** `P-16`　**協力:** `P-01` / `P-02`

**AT-DELIVERY-001（未実行）:** 着手/統合/リリース依存を分け、根拠なく全工程を直列停止しない。

### DELIVERY-002 早期配布検証

CLI骨格・validate・配布物smokeは初期から作る。security/秘密値/cleanupテストを最後のIssueだけへ先送りしない。

**主担当案:** `P-02`　**協力:** `P-13` / `P-16`

**AT-DELIVERY-002（未実行）:** 各PRで担当境界の異常テストがあり、最後の梱包時だけnative不足を発見する進め方にしない。

### DELIVERY-003 リリース証拠

要件→Issue→実装→受け入れテスト→実行証拠を対応付ける。探索→Trace/Recording→候補→AIなし試験→明示採用→別Run再生を同じ成果物で一巡する。

**主担当案:** `P-16`　**協力:** `P-14` / `P-15`

**AT-DELIVERY-003（未実行）:** 閉じたIssue数でなく、採用要件の網羅・接続・配布・失敗時の正しさでv0.1を判定する。

## INPUT: InputActionの自己記述性

### INPUT-001 既存定義の再利用

InputActionのdescriptionは既存の必須フィールドを利用する。valueTypeのbutton/axis1d/vector2/text、range、holdable、active、risk、requiresConfirmation等の意味を再定義しない。

**主担当案:** `G-04`　**協力:** `P-07` / `P-10`

**AT-INPUT-001（未実行）:** 既存記述を二重に登録させず、Action discoveryで元の定義と同じ情報を返す。

### INPUT-002 追加metadata

valueSchemaで入力構造・範囲・各値の意味を機械可読にし、examplesを任意の典型入力例として提供する。説明はプレイヤー視点の動作と入力軸の意味を記述する。

**主担当案:** `G-04`　**協力:** `G-03`

**AT-INPUT-002（未実行）:** vector2の入力例がschemaを満たし、公開・登録・検索・利用側で一致する。

### INPUT-003 互換性と権限

新metadataの導入時は既存schema/ABI/clientの互換性を確認し、未知の拡張を無条件に追加しない。Action説明の存在を実行承認と解釈せず、現時点の公開定義を使う。

**主担当案:** `G-04`　**協力:** `P-10` / `P-09`

**AT-INPUT-003（未実行）:** 旧clientの扱いと入力型の不正を検証し、古いconfirmedを再生の承認に流用しない。

## 採用しない旧案・訂正履歴

| 旧案・曖昧な説明 | 現在の扱い |
|---|---|
| グローバルGua.Observe("enemy1.phase")の採用 | 各UI/ObjectのObserveとWorld.Propertyへ置換。 |
| Playtest専用のGame Stateレジストリを必須化 | GuaのTreeと追加観測値を再利用する。 |
| 任意metadataはGuaの責務外なので入れない | 汎用のread-only観測値としてGuaへ採用。ゲーム固有のGoal解釈は入れない。 |
| descriptionがないため新設する | 応答schemaに既存の必須descriptionあり。追加対象はvalueSchema/examples等。 |
| useItem(id)/attack(enemyId)/moveTo等を標準操作にする | 入力量・方向・現在の照準とUI選択を利用し、内部ロジックの直接指定で飛び越えない。 |
| Playtest独自Capability registry/Trace/Viewerを作る | Guaの操作、改訂#109、Gua Recordingへ一本化する。 |
| v0.1ではcollectionはlistだけ | list/setをv0.1から両方実装する。 |
| 各success項目は一度成立すれば常にall成立へ使える | 通常allは同じ評価状態。独立した時間条件とGoal全体の成立記録は別。 |
| 個別within失敗で常にScenario失敗 | 論理ツリーの達成可能性で判定。failure期限切れを失敗へ反転しない。 |
| Completing中Cancelで主結果をAbortedへ上書き | 主結果は一度だけ。後処理中断/失敗を別記録する。 |
| exit 11は必須artifactの失敗だけ | 主結果Passedだが入力/資源/保存など必須後処理未達の意味へ拡張する。 |
| どのAction拒否でも即Scenario失敗 | 未送信のExplore提案拒否は有限再判断、送信後失敗/不明は原則停止・安全整理。 |
| leaseMsを正確な押下時間や実解除の証拠とする | 明示解除予定と保護lease、送信/適用/結果受信を分離する。 |
| 成功した探索の観測値をそのまま正式な期待値にする | 候補→条件確認→AIなし試験→明示採用を経る。 |

旧案は上記の現仕様へ置換する。未実装TODOとして復活させない。

## DASH local dashboard追加要件

#26によりv0.1へ追加した4要件。元126要件は削減しない。契約は[contracts.md](contracts.md)、実Runner/browser/配布物の受け入れは#26/#18。

### DASH-001 明示Scenario登録母集団

登録Scenarioを明示一覧から読み、Runがない項目も未実行として表示する。

**主担当:** #26 **協力:** #2/#9/#15/#17/#18

**AT-DASH-001（未実行）:** 登録Scenarioを明示一覧から読み、Runがない項目も未実行として表示する。

### DASH-002 ScenarioとbuildとEnvironment識別

stableID、定義版、bytes hash、実buildID、実効Environment識別で照合し、識別不明旧Runを実績に混ぜない。

**主担当:** #26 **協力:** #2/#9/#15/#17/#18

**AT-DASH-002（未実行）:** stableID、定義版、bytes hash、実buildID、実効Environment識別で照合し、識別不明旧Runを実績に混ぜない。

### DASH-003 結果と履歴の忠実な表示

全status、未確定、読取失敗、詳細省略/欠損/保存失敗、exit11、最新失敗と過去成功を区別する。

**主担当:** #26 **協力:** #2/#9/#15/#17/#18

**AT-DASH-003（未実行）:** 全status、未確定、読取失敗、詳細省略/欠損/保存失敗、exit11、最新失敗と過去成功を区別する。

### DASH-004 読取専用入口と共通Viewerと配布

明示rootとrefreshで読取のみ、Gua Viewer導線、秘密値とpath境界を守り実Runner/browser/配布物で検証する。

**主担当:** #26 **協力:** #2/#9/#15/#17/#18

**AT-DASH-004（未実行）:** 明示rootとrefreshで読取のみ、Gua Viewer導線、秘密値とpath境界を守り実Runner/browser/配布物で検証する。
