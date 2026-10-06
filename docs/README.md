> 現在の契約: [contracts.md](contracts.md)、[schema](schemas/)。実Issue対応と追加DASH要件はtraceability.json。以下のreview-ledger-r1の起票前情報は履歴であり、2026-10-06の明示実装許可と区別する。現行Gua参照はgua-v1.1.1 / 88f5dca4aa97c5d5187ab66ea4416377f3affc96。

# Gua Playtest 合意仕様・起票準備パック

作成日: 2026-09-16  
資料版: review-ledger-r1（製品やprotocolのリリース番号ではない）

## この資料の位置付け

この会話で直近まで承認された仕様を、撤回・訂正を反映して整理した。合意内容の主な根拠はこの会話であり、公開文書から新機能を採用した資料ではない。

**設計合意と、実装・検証の完了は別である。** 本資料では実装の全件監査・製品テスト・実ゲーム実行を行っていない。テストIDは受け入れ確認の計画で、合格実績ではない。Guaに存在する機能を未実装と一律にみなさず、起票時に既存実装との差分・必要な回帰試験へ縮める。

GitHubのIssue作成・変更、PR、リポジトリ移管は行っていない。移管は10月1日にまとめるという利用者の方針を維持する。現在の審査規則や実装完了日は推定していない。

## ファイル

| ファイル | 内容 |
|---|---|
| 01-spec-ledger.md | 合意要件、主担当、協力先、受け入れ確認 |
| 02-issue-plan.md | Gua側・Playtest側の起票案、依存、対象外、完了条件 |
| 03-open-decisions.md | 大枠を変更せず実装前に具体化する残件と担当 |
| 04-gua-109-revision.md | 承認済み#109改訂案の差替え用整理 |
| traceability.json | 上記対応の機械可読データ。GitHub操作を含まない |
| checks.json | この資料自体のID・参照・依存整合性チェック |

## 読み方

要件IDは合意仕様の識別子。G-/T-/P-で始まる作業IDは今回の起票案で、実GitHub番号ではない。GH-106/GH-107/GH-108のみ既存Issueへの依存参照である。

合意済み要件: **126件**。新設作業候補: **29件**（Gua側12件、Playtest側17件）。加えてPlaytestの管理用親Issueを1件設け、Gua側は既存#109を改訂して親として利用する案。大量のIssueを今すぐ作る指示ではない。

OPEN-*は、未確定の数値・境界・API形式を黙って実装者任せにしないための台帳。v0.1の採用機能を削る項目ではない。影響する契約を実装する前に解消し、独立した作業は進められる。

## 優先する修正

直近の整合性レビューを優先する。通常allの同時評価と時間成立履歴、条件ツリーごとの期限、True/False/Unknown、空all=false、結果の一度だけの確定、必須後処理未達のexit 11、未送信拒否と送信後不明の分離が現在の仕様である。

## 確認した接続先資料

固定コードの基準commitは`10a8ef71713fe491633cf2b5649c76007cfd4767`。Issue本文は取得時点の内容であり、コード全体の現状を保証するものではない。

- [S-109] Gua #109 現行本文
  https://github.com/link1345/gua/issues/109
  確認内容: open。旧本文で、100step・失敗時保存と#106/#107/#108連携が記載されている。改訂案の反映は未実施。
- [S-106] Gua #106 Semantic Lint
  https://github.com/link1345/gua/issues/106
  確認内容: 明示的Lint、runtimeの通常公開を自動拒否しない、profile境界。
- [S-107] Gua #107 Locator auto-wait
  https://github.com/link1345/gua/issues/107
  確認内容: 直前再解決、strict複数一致、enqueueとcompletion、総時間予算。
- [S-108] Gua #108 Semantic baseline比較
  https://github.com/link1345/gua/issues/108
  確認内容: baselineの既定除外と明示更新。Traceとは役割が違う。
- [S-INPUT] InputAction応答schema（固定commit）
  https://github.com/link1345/gua/blob/10a8ef71713fe491633cf2b5649c76007cfd4767/protocol/schema/game-input-actions.schema.json
  確認内容: descriptionは既に必須。valueTypeはbutton/axis1d/vector2/text。valueSchema/examplesの新設範囲とは区別する。
- [S-REC] Recording schema（固定commit）
  https://github.com/link1345/gua/blob/10a8ef71713fe491633cf2b5649c76007cfd4767/protocol/schema/recording.schema.json
  確認内容: schemaVersion 1/2、game_input/operation/arguments、requestId、pre/postRevision、secretKey等。定義だけで全Replay経路実装済みとは判断しない。

検索ではObserve、valueSchema、Timed Segmentも確認したが、同義語や別のIssueでの実装までは網羅していない。今回の新設候補は、実起票前に対象リポジトリ・open PR・既存Issueを再確認する。
