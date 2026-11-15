# 仕様の決定状況

OPEN-ID は、公開する形式、数値、実行時の境界などを決めた経緯を追うための識別子です。実装や試験の状態は各契約と検証記録で確認してください。判断が残っている項目は、影響を受ける契約を公開・実装する前に詰めます。

起票時の検討事項、仮の担当、当時の判断は [改稿前の記録](https://github.com/gua-project/gua-playtest/blob/ee78e841279ae4fdcdaf5bf86af5e1a197aef8d3/docs/03-open-decisions.md) にあります。作業 ID と実 Issue 番号の対応は [traceability.json](traceability.json) を参照してください。

## OPEN-01 wire/ABIの互換性とschema改版

Playtest の文書形式 v1 では、同梱スキーマをローカルで解決し、文書形式の版、SDK の版、接続先の能力を個別に確認します。追加観測値には Gua Observe v1 を使います。InputAction の説明は既存の必須フィールド `description` を使います。Gua スキーマの取得元と現在の SDK は [文書契約](contracts.md) を参照してください。

## OPEN-02 Value・演算子の細部とenum境界

**比較規則は確定済みです。** P-03 の契約は [assertions.md](assertions.md#open-02-decisions) にあります。Gua の Value v1 を使い、元の JSON が持つ型を保って比較します。例えば、`[x]` に対する `containsAll [x,x]` は True です。必要な数まで重複して含むかではなく、各値が含まれるかで判定します。空の期待値、連続する要素列、絶対許容差、.NET 正規表現とその実行上限も同じ契約で決まっています。

**接続時に確認する事項**は、Gua から受け取る enum カタログの有効性と、観測対象の同一性です。これは P-07 の範囲です。実際の観測を比較処理へ渡すときの扱いは [Gua bridge](gua-bridge.md#observation-boundary) に従います。

## OPEN-03 Observeの変更検知・寿命・購読境界

Gua Observe v1 のフレーム単位の取得と明示通知、Owner と登録の寿命、Snapshot と購読カーソルを使います。内部変数への代入をすべて自動で観測できるとは限りません。欠落や古い観測を扱う規則は [Gua bridge](gua-bridge.md#observation-boundary) を参照してください。

## OPEN-04 既存標準stateとカスタムObserveの参照

Target と read の `source`、`region`、`selector`、キャッシュの同一性は [文書契約](contracts.md#targets-and-observation-identity) で決まっています。比較は [assertions.md](assertions.md)、時間条件は [conditions.md](conditions.md)、実行時の解決は [Gua bridge](gua-bridge.md) を参照してください。

## OPEN-05 条件評価の完全な状態表

[比較契約](assertions.md) と [条件契約](conditions.md) で、三値論理、全比較の事前検証、対象数、入れ子、`within` と `for`、観測の連続性、保持状態と期限切れの伝播が決まっています。観測エラーは真偽と時間状態を分けて扱います。成立前の保持はリセットし、期限とタイマーは進めます。Running や待機の開始時には、同じ観測時刻を複数の条件で共有します。

[条件の状態表](../tests/fixtures/playtest/condition-state-table.json) と [ConditionTests](../tests/Gua.Playtest.Foundation.Tests/ConditionTests.cs) で期待値を確認できます。Run の終端や予算は [OPEN-06](#open-06-終端述語理由コード予算最終操作)、実観測の連続性・profile・操作抑止や実ゲームでの受け入れは、各接続経路と [検証条件](fixture-evidence.md) を参照してください。

## OPEN-06 終端述語・理由コード・予算最終操作

終端候補を選ぶ条件、理由の一覧、同順位の決め方、最後に許可された操作で結果を確認できる時間は [実行契約](execution.md) にあります。最後の操作で成功し、完了条件も満たした場合は、予算を使い切ったことだけで失敗にはしません。ホスト、Planner、Replay、CLI を組み合わせた検証は、各経路と FIX-007 の証拠で確認します。

## OPEN-07 元Recordingの時刻・host順序保証・simulation能力

[Replay 契約](replay.md#published-gua-adapters-and-timing) で、UI の記録時刻、古い記録の時刻が不明な場合の変換、同一オフセットでの順序、必要な時計とホスト能力、実時間のリースと後処理を扱います。厳密な時間規則を満たせない場合は実行前に拒否します。実エンジンが操作を適用した時刻や、シミュレーション時計とリースの挙動には実測の証拠が必要です。

## OPEN-08 Replay Planの参照範囲・hash・採用証拠

[文書契約](contracts.md) は、元バイト列の SHA-256、Plan の外に置く採用証拠、Recording 全体を扱う境界を定めます。[Replay 契約](replay.md) は保持したバイト列と順序付きチェックポイントを使い、既存の待機を残します。試行が最後まで実行されたことと、Plan が明示採用されたことは [採用契約](plan-adoption.md) の証拠で確認します。

## OPEN-09 Planner実行隔離と接続契約

実 Planner を実ゲームへ接続する前に、対応する Codex／App Server の版、出力スキーマ、サンドボックス、承認、ネットワーク、直接の Gua 接続を防ぐ仕組みを OS ごとに確認します。隔離はプロンプトやアセンブリの分離だけでは確認できません。[Planner 契約](planning.md) と [Codex 接続の調査](codex-protocol.md) を参照してください。

## OPEN-10 資源制限・既定値・設定場所

Environment には有限の上限を明示します。Run の時間・件数上限と後処理の時間配分は [実行契約](execution.md) にあります。Trace の 100 step は合意済みです。8行動・3反復・3回復や 20ms は起票時の提案・説明に使った値です。実行時に適用する上限は、[保存](artifacts.md)、[回復](exploration.md)、[CLI](cli.md) の各契約で確認します。

## OPEN-11 配布と互換性の実測値

現行 SDK の依存とパッケージ検査は [文書契約](contracts.md) と [Gua bridge](gua-bridge.md#distribution-purpose) にあります。配布時の互換性を示すには、対象のネイティブ配布物、エンジン、OS、Viewer、Codex の組み合わせで実行した証拠が必要です。

## OPEN-12 正式名称とIssue対応の確定

型とスキーマの名称は [文書契約](contracts.md)、作業 ID と実 Issue の対応は [traceability.json](traceability.json) にあります。結果の理由と同順位の規則は [実行契約](execution.md)、現在使えるコマンドは [CLI](cli.md) を参照してください。
