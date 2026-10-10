> 履歴注記 2026-10-06: 以下は起票前改訂案。Gua #109および#123〜#128は1.1.1で実装済み。Playtestは現行GuaTraceSession/Reader/Reportとschemaを再利用し、この案を第二Trace実装の依頼にしない。

## Trace の責務を理解するための履歴

この文書は Gua の共通Traceを設計した起票前改訂案です。現行APIの使い方は公開Guaと [artifacts.md](artifacts.md)を参照し、この案のAPI新設をもう一度実装しないでください。

購入テストでは、操作と応答、観測、判定を同じ時系列で関連付けると「操作が届かなかった」のか「取引後の応答が失われた」のかを調べられます。Trace自体が購入のGoalを採点するわけではありません。下記のsession／step／eventは調査の構成、PlaytestのScenario／Run／Resultは目的と判定の構成です。読む順番は [開発者ガイド](developer-guide.ja.md)から辿れます。

# Gua #109 改訂本文案

## 概要

既存failure diagnosticsを、UIテスト・Semantic Game Input・Raw Input・外部Runnerの実行過程を記録する、共通のGua TraceとTrace Viewerへ拡張する。

操作・ホスト結果・Assertion・公開観測・診断情報を一つの時系列で関連付ける。既定は直近100 stepを保持して非成功時に成果物化する方針とし、成功した探索にも使えるalways保存とstreamingをv1から実装する。

Gua TraceはGoalを解釈しない。Planner、探索戦略、Scenarioの採点・期限・終了優先順位は呼び出し側が実行し、Traceは事実と評価結果を保存する。改訂範囲をPlaytest側へ代替実装しない。

## 前提と責務

#106のlint report、#107のaction phase/source location、#108のcomparison resultとの連携を維持する。Observe/Propertyと共通Value、InputActionの追加metadata、汎用Replay時間制御は別の機能として実装し、Traceはそれらを取得・関連付ける側に置く。

Trace基盤の着手に全連携の完成は必須ではない。一方、この親Issueの完了には関連付けと統合試験が必要である。GuaのCIやpublic APIからGua Playtestへの依存を生やさない。

## 1. Session・Step・Event・参照

GuaTraceSession/GuaTraceOptionsとversioned schemaを追加する。実名・配置は既存構成と整合させて確定する。

Sessionは一実行、Stepは閲覧する操作・検証などのまとまり、Eventは開始・終了・処理段階・観測更新の追記事実を表す。Step kindはaction/assertion/mark/lifecycle。

traceId、stepId、eventId/収集sequence、sourceId、sessionEpoch、requestId、親・関連step、観測参照を区別する。Runtime IDやrequestIdを実行全体で無条件に一意とみなさない。client/native記録を二重のプレイ操作として数えない。raw context由来でsource locationのない記録も許す。

収集時計とホスト時刻・frame・revisionを分ける。同じ数値のtimestampでも同一時計・同時観測と断定しない。元操作の結果と受信情報を失わない。

## 2. 操作・入力保持・未確認

UI、Semantic Game Input、Raw Input、所有入力のcleanupを同じ基盤へ記録する。

要求送信、enqueue受付、ホスト処理完了、期待したゲーム状態の成立は別の事実。操作結果には確認できた処理段階、元の結果・error、Selectorと解決対象を残す。

保持開始要求とその完了、保持状態、解除要求、期限切れ等の契機、解除確認を区別する。lease満了予定だけで解除済みを記録しない。入力開始/解除の予定時刻、送信時刻、取得できた適用時刻、結果受信時刻を区別する。

Timeout/中断は未実行や副作用の取消しを意味しない。遅い結果は追加Eventとして残し、呼び出し側の確定済み結果を遡って変更しない。Trace自体は再送、解除、clock進行を実行しない。

## 3. Snapshot・観測区間・Change

Snapshot BlobとObservation Recordを分ける。秘密値処理後の保存内容をhash化して重複排除し、同じ内容を別時点に観測した事実は残す。World Snapshotなどの収集は公開能力と明示した取得方針に従い、未提供・任意省略・必須取得失敗を区別する。UI/World別取得は各source/epoch/frame/revision/時刻を保持する。

#108のbaseline用の既定正規化をTrace保存へ流用しない。公開・許可されたposition/bounds/frameなど、調査に必要な情報は保持し、baseline比較は別attachmentとする。

before/afterの取得理由を明示する。操作前、入力完了時、wait終了時、主結果決定時、cleanup後を混同しない。操作に関連する区間の変化であって、操作が原因と自動認定しない。

Observe/Propertyの通知を受信できた範囲で中間Changeも保存する。追加はafter、削除はbefore、変更は両方。共通Valueを使い、nullを不在のダミー値にしない。

不在、取得失敗、部分取得、通知欠損、古い観測、保持範囲外を区別する。購読・Snapshotの連続性が未保証なら明記し、再取得で失われた中間履歴を復元したとしない。

## 4. 外部Runnerの記録口

特定テストframeworkなしで、Step開始/終了、mark、Assertion評価経過、既存request相関、観測参照、attachment、flush/終了を利用できるAPIを提供する。

Assertionの対象、operator、期待値、観測値、True/False/Unknown、評価時刻、用いた観測、呼び出し側の役割・結果を分ける。failure条件の評価trueとScenario失敗を矛盾なく表す。within/forや同時成立の採点は呼び出し側で実行する。

Playtest固有情報はgua-playtest.scenarioId等の名前空間付き注釈で記録できる。軽量注釈は共通Value、大きな構造化情報はschema識別付きattachment。Traceが意味を解釈しなくても未知namespaceを汎用表示できる。

AIに渡した観測参照、判断要求、提案、採用/却下、短い理由、取得できた利用量も同じ仕組みへ載せる。内部の思考全文は必須記録にしない。

## 5. 保存方針・制限・中断

captureModeはrecent/streaming、savePolicyはonFailure/alwaysを独立した軸として実装する。既定recent・直近100 step・onFailure。中断や結果未確定を成功として破棄しない。

100 stepはユーザーが見るまとまりの上限で、native phaseの件数へ混ぜない。メモリ、キュー、全artifact、個別attachmentにも有限上限を設ける。実数値は公開前に確定する。

保持中のStepが参照するSnapshotを黙って削除しない。保持できなければ欠損/保持範囲外を明記する。上限到達では詳細収集停止・通知と最終サマリー用領域を確保し、静かに落とし続けない。

manifest、追記Event、Snapshot、attachment、Recording参照を含むversioned形式を定義する。物理配置案はmanifest.json/events.jsonl/snapshots/attachments/recordings。正常終了でfinalizeし、不完全末尾を検出して保存済み完全レコードまで読めるようにする。

呼び出し側の主結果、Trace終了状態、記録範囲・品質は別々に保持する。メモリのみのrecentで強制終了した場合や未flush部分の完全保存は保証しない。主結果Passed・必須記録失敗も矛盾なく表し、採点をTraceが上書きしない。

## 6. Recording・既存診断の再利用

既存Gua Recording形式を置換せず、artifactと対応stepを参照する。拒否・Timeout・観測・markなど、再生しない出来事もTraceに残す。Trace完了を決定論的再生保証とみなさない。

#106 lint、#108 comparison、既存diagnostics/log/pending request/screenshot/environment/versionを関連stepへ結び付ける。lint自動実行、baseline自動更新は行わない。

## 7. 秘密値とViewer安全性

公開profileを維持し、PlayerのTrace取得のためDebugへ昇格しない。明示sensitive/maskを、保存用buffer・queue・hashより前へ適用する。

引数、観測値、注釈、ログ、例外、添付にも同じ責任境界を適用する。Screenshotは独立の取得/mask方針を持ち、安全に保存できなければ省略可能。文字のsensitiveだけで画像も自動保護されるとは保証しない。

source locationは相対化等で扱い、ローカルの不要な絶対パスを公開しない。HTMLをエスケープし、Trace内コード/外部URLを自動実行・取得しない。添付パスの範囲外アクセスを防ぐ。秘密であることのheuristicによる完全保護を約束しない。

## 8. Viewerと配布

Inspectorと静的HTML reportで同schema/React componentを利用する。Timeline、時間、状態、失敗強調、UI/World/追加観測値、前後/中間差分、screenshotとnode bounds、native phase、source location、logs、pending、lint/comparison、注釈、欠損と未確認を表示する。

成功Runも同じ画面で閲覧できる。未知namespaceは汎用表示する。生成済みViewer/schemaを版固定で配布し、利用者にGuaのソースcheckoutやWeb開発環境を必須にしない。

Viewerはゲーム操作やReplayを実行しない。

## 9. 実行への影響・失敗の分離

Traceのために既存completion queueを先取りしない。ゲームスレッドでファイルI/Oを待たず、有界の収集経路から保存へ委譲する。負荷超過は記録品質として報告する。

capture/flush/viewerの失敗は元のテスト例外type/stackや主結果を上書きしない。必須artifactを満たせずCIを非ゼロにするかは呼び出し側が決定できる。確定済み主結果と後処理結果を分けて保存する。

## 10. 受け入れ条件

| 検証対象 | 必須の確認 |
|---|---|
| 既定と成功保存 | 正常成功ではTraceを残さず、alwaysでは成功でも残す。全capture/save組合せを検証する |
| 容量と中断 | 100step、byte/queue上限、不完全末尾、未finalize、保存失敗が可視化される |
| 相関 | request/source/epoch、client/native、自動/明示記録が混同・二重化しない |
| 操作事実 | enqueueとcompletion、保持開始と解除、Timeoutと遅い結果を区別する |
| 観測 | Blobと観測時点、位置変化、中間phase、欠損・不在・古い値を区別する |
| 外部Runner | テストframeworkとPlaytestへの依存なしに、注釈・評価経過・attachmentを保存できる |
| 既存連携 | lint/comparison/diagnostics/Recordingが関連Stepから参照できる |
| 公開・安全 | Player公開境界、秘密marker、悪意あるHTML/添付パス/URLを検証する |
| 不干渉 | completion受信を妨げず、clock/入力を変えず、元失敗を保存失敗で消さない |
| 表示・配布 | 同schema/componentでInspectorと静的reportが読め、生成済み資産だけで利用できる |

正常・失敗・中断fixtureを機械テストとブラウザで確認する。期待した故障が実際に発火したことも外側のテストで確認する。未実行やskipは合格としない。

## 対象外

Goal/Scenario/Planner/Explorerの実行、AI接続、ゲーム固有採点、Observe本体、InputAction metadata本体、動画収録、network trace、process dump、全frame無制限保存、CI provider専用upload、自動テスト生成・retry・Replay実行。

## 作業分割と完了

本親Issueの下にT-01〜T-06の作業案を置く。これらは起票前の仮IDであり、実際のGitHub番号へ対応付ける。秘密値・互換性・失敗時処理は各作業に含め、最後だけにまとめない。

各作業は対応要件、着手/統合依存、変更範囲、対象外、受け入れテスト、文書・互換性を記載する。Trace基盤が先に完成しても、全連携と配布・統合確認まで親Issueを完了にしない。

現在の状態ラベル・open PRを実作業前に再確認し、競合を避ける。既存の作業状態管理方針を維持する。本文差替え・子Issue作成は、利用者の明示指示後に行う。
