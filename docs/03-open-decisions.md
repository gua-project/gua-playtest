# 実装前に具体化する詳細契約

ここにあるのは採用機能の撤回案ではなく、大枠の合意から実装へ進む際にまだ確定していない境界・実数値・API形式である。新しい機能を際限なく増やすための一覧ではない。

全項目が決まるまでプロジェクト全体を止めない。各項目は影響する契約の公開・実装前に閉じる。解決内容は要件IDと試験例へ反映し、旧案と併記して曖昧に残さない。

## OPEN-01 wire/ABIの互換性とschema改版

追加観測値とInputAction metadataを既存schemaへどう載せるか、旧clientのadditionalProperties制約、version negotiation、公開package/ABIの改版単位。既存descriptionは新設しない。

**担当案:** G-01 G-03 G-04 T-01 P-01

**固定する時点:** 破壊的なprotocol変更・型生成・全client実装に入る前。契約済みのFake/プロジェクト作業は継続可能。

**状態:** Gua側は1.1.1公開契約で確定。Playtest版1のlocal schema registry、版/能力の分離はcontracts.mdへ反映。

## OPEN-02 Value・演算子の細部とenum境界

enumTypeの名前衝突・定義更新、flags/alias/未定義数値の扱い、stringの比較/Unicode、setの等価・正規化と-0、collection containsAllの重複と空の意味、integer/number境界、regex方言と実行上限。型・許可集合は再議論せず具体例へ固定する。

**担当案:** G-01 P-03

**固定する時点:** 正規化・serializer・比較エンジンの互換契約を公開する前。

**状態:** 未確定。具体的な案を別途確認してから、決定した仕様と受け入れデータへ更新する。

## OPEN-03 Observeの変更検知・寿命・購読境界

getter型登録の変化をいつ検出するか、公開frame/tickか明示通知か、登録解除/同名再登録/collectionのin-place変更、getter例外、購読とSnapshotの連続性、欠損通知。任意の内部変数代入が自動で観測できるとは仮定しない。

**担当案:** G-02 G-03 T-03

**固定する時点:** G-02のnative/binding API実装前。最初に閉じる契約課題。

**状態:** Gua Observe v1で確定済み。frame sample/Notify、Owner/登録寿命、Snapshot/cursor、gap/staleを再利用。

## OPEN-04 既存標準stateとカスタムObserveの参照

visible/enabled/worldPosition等の既存fieldと同名Observeの名前衝突、UI/world selectorの正式対応、取得不能なproperty/複数一致/対象差替え、同一性とschemaキャッシュの境界。

**担当案:** G-02 P-01 P-03 P-07

**固定する時点:** 正式Target schemaとselector変換を固定する前。

**状態:** Target/readのsource/region/selectorと同一性cacheはcontracts.mdで確定。比較/時間意味は#4/#5、runtime解決は#7。

## OPEN-05 条件評価の完全な状態表

承認済みの三値・通常all・時間成立履歴・within/forを維持しつつ、任意のネスト、Unknown中の期限切れ、対象数変化とforの継続、観測errorとPending/Expired、同時観測境界を真理値表・状態機械へ落とす。

**担当案:** P-03 P-04 P-05

**固定する時点:** P-04の公開意味規則とgolden fixture確定前。

**状態:** 未確定。具体的な案を別途確認してから、決定した仕様と受け入れデータへ更新する。

## OPEN-06 終端述語・理由コード・予算最終操作

主結果優先順位は承認済み。ただし予算枯渇を終端候補にする時点、最後の承認済み操作の成功、完了未確認をいつ継続不能と判定するか、phase/origin/reasonの正式列挙とtie-breakを固定する。最後の操作の成功を直ちに予算切れで打消さない既存合意を守る。

**担当案:** P-05 P-13

**固定する時点:** 実行制御とCLI result schema公開前。

**状態:** 未確定。具体的な案を別途確認してから、決定した仕様と受け入れデータへ更新する。

## OPEN-07 元Recordingの時刻・host順序保証・simulation能力

記録時刻が送信/適用/結果受信のどれか、旧記録の変換可否、同offset順序、ホスト適用時刻の計測、leaseとsimulation時計の関係、原子的適用を要求する場合の能力検査。保証不能な厳密モードは拒否する。

**担当案:** G-05 P-09

**固定する時点:** Timed Segment executorとRecording互換変換の実装前。

**状態:** 未確定。具体的な案を別途確認してから、決定した仕様と受け入れデータへ更新する。

## OPEN-08 Replay Planの参照範囲・hash・採用証拠

固定bytes SHA-256は承認済み。Plan本体と採用証拠の格納場所/hash対象、自己参照hashの回避、Recording範囲の完全カバレッジ・飛ばす操作、既存waitとの競合、候補試験の完走条件を固定する。

**担当案:** P-01 P-09 P-14

**固定する時点:** plan create/acceptのファイル形式を公開する前。

**状態:** bytes SHA-256、外部採用証拠、全Recording境界はcontracts.mdで確定。実完走/採用の受け入れは#10/#14。

## OPEN-09 Planner実行隔離と接続契約

対応するCodex/App Server版、利用できる出力schema制約、sandbox/承認/ネットワーク/直接Gua接続の防止をOSごとに検証する。プロンプトやアセンブリ分離だけで技術的境界と称さない。

**担当案:** P-10 P-12

**固定する時点:** 実Plannerを実ゲームへ接続する前。

**状態:** 未確定。具体的な案を別途確認してから、決定した仕様と受け入れデータへ更新する。

## OPEN-10 資源制限・既定値・設定場所

Trace byte/event/queue/attachment上限、準備/cleanup/Planner/waitの既定期限、区間時間とmaxLateness、観測打切り、regex上限、停滞初期値の格納場所。100stepは確定、8行動/3反復/3回復や20msは提案値/説明用値として区別する。

**担当案:** T-01 P-01 P-05 P-11

**固定する時点:** 無制限動作を残さず、その機能の公開・本番fixture実行前に有限値を固定する。

**状態:** 公開形式は明示有限Environment limitsで確定。100step以外に提案既定値を導入しない。runtime制御/実効解決は#6/#12/#15。

## OPEN-11 配布と互換性の実測値

利用するGua正式版、native/engine/OS実行経路、Viewer配布API、Codex対応範囲を実測する。v0.1の配布方針は確定だが、実測していない版・OSを対応済みにしない。

**担当案:** G-06 P-12 P-16 P-17

**固定する時点:** 配布公開と対応表の確定前。

**状態:** 未確定。具体的な案を別途確認してから、決定した仕様と受け入れデータへ更新する。

## OPEN-12 正式名称とIssue対応の確定

仮のフィールド/インターフェース/Package ID/Reason名と、既存APIの命名の対応。今回のG-/T-/P-IDは起票用仮IDであり実GitHub番号ではない。移管後のURLは実際の移管結果で更新する。

**担当案:** P-01 P-13 P-17

**固定する時点:** 公開API・NuGet登録・GitHub起票前。

**状態:** 型/schema名と実Issue対応はcontracts.mdとtraceability.jsonで確定。結果reason/tie-breakは#6、CLI入口の正式組立は#15。

## 今回の整理から見た最初の一件

OPEN-03はGua 1.1.1で確定済み。現在はPlaytest側のOPEN-02/05/06/09/11を担当Issueで具体化し、確定した契約から並行実装する。

これはObserveを別方式へ変更する決定ではない。既に採用したAPIとread-only方針を維持し、どの瞬間に変化を確定して通知するかを具体化する。
