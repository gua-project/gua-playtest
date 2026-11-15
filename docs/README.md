# Gua Playtest 開発者向け文書

初めて読むなら [開発者ガイド](developer-guide.ja.md) から始めてください。購入テストを例に、目的を決めてから観測・入力・判定・保存するまでを追えます。コードとテストの入口、手元で試せるコマンドも載せています。

## 使い方と現行仕様

| 知りたいこと | 参照先 |
| --- | --- |
| どのコマンドでゲームを起動できるか | [CLI のコマンド一覧](cli.md) |
| Windows でゲームを起動して観測したい | [observe-run](observation-runtime.md) |
| 指定した UI を最大１回クリックしたい | [click-run](single-ui-runtime.md) |
| Player の公開 UI を台本型 Planner で操作したい | [scripted-explore-run](scripted-explore-runtime.md) |
| 保存した固定１クリック Plan を Windows で試したい | [replay-run](replay-runtime.md) |
| Player の Recording を候補・試行・明示採用・別 Run での再生に使いたい | [Player Recording からの再生手順](scripted-explore-replay.md) |
| Scenario、Environment、Plan、結果の形式を確かめたい | [文書契約](contracts.md)、[スキーマ](schemas/) |
| 値の比較、複数対象、時間条件を調べたい | [Assertion](assertions.md)、[Condition](conditions.md) |
| 起動、接続、開始状態、プロセスの所有権を調べたい | [実行の準備](preparation.md) |
| 合否の確定、期限、予算、後処理の規則を調べたい | [Run の実行契約](execution.md) |
| Gua の依存、観測、入力完了、接続先の能力を確認したい | [SDK とスキーマの版](contracts.md#gua-sdk同梱スキーマホストの互換性)、[Gua bridge](gua-bridge.md) |
| Planner が使える観測と提案の採用条件を調べたい | [Planner の契約](planning.md) |
| 保存済みの結果、Trace、HTML を利用したい | [成果物の保存と読み取り](artifacts.md)、[Dashboard](dashboard.md)、[Cloud からの利用](cloud-consumption.md) |

仕様を決めた箇所を探すには [OPEN-ID の一覧](03-open-decisions.md) を使います。実際に使える CLI、ライブラリの契約、実ゲームで確認済みの範囲は、各文書にあるコード・テスト・証拠の参照先で確認できます。

## 要件と検証記録

- [要件台帳](01-spec-ledger.md) で要件IDと受け入れ条件を確認し、[traceability.json](traceability.json) で仮の作業IDと実 Issue の対応を調べられます。
- [検証条件](fixture-evidence.md) には、実行前に固定するテストケース、故障の起こし方、結果を独立に確かめる方法、実ゲームで必要な証拠をまとめています。
- [基盤](../foundation-evidence.md)、[文書契約](../issue-2-evidence.md)、[時間条件](../issue-5-evidence.md)、[bridge](../issue-7-evidence.md)、[Codex プロトコル](../issue-13-review-evidence.md) の記録には、各時点で調べた範囲と残る確認が載っています。
- 保存層の CI 失敗と調査の経緯は [PR #34 の検証記録](https://github.com/gua-project/gua-playtest/pull/34#issuecomment-6487400072)、現行の保存規則は [成果物の保存と読み取り](artifacts.md) にあります。

検証を読むときは、対象のコミット、環境、実行コマンド、結果、ログを確かめてください。スキーマ検証や Fake 試験が成功していても、実ゲームでの合格は実際の実行記録で確認します。

## 設計の履歴

- [起票準備パックの原文](https://github.com/gua-project/gua-playtest/blob/ee78e841279ae4fdcdaf5bf86af5e1a197aef8d3/docs/README.md#L52) は、2026年9月16日時点の設計合意と調査記録です。
- [Issue の起票案](02-issue-plan.md) と [Gua #109 の改訂案](04-gua-109-revision.md) には、当時の責務分担と提案が載っています。
- [checks.json](checks.json) は起票準備資料の ID・参照・依存関係を検査した記録です。
