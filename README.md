# Gua Playtest

変更・push前の検証と独立サブエージェント監査は [AGENTS.md](AGENTS.md) と
[$playtest-bug-hunt](.agents/skills/playtest-bug-hunt/SKILL.md) に従う。
通常最大2回、意図的なPR全体監査は総計最大4回。上限は追加独立監査を止める。
既知の具体的CI／review不具合は、根拠確認・同型分岐のまとめ修正・回帰検証・
追加独立監査未実施の差分開示を経てまとめpushし、実CI／実reviewへ進める。
変更なしの同一コマンド再試行と、根拠ある修正後の検証は区別する。
ローカル監査は最終HEADの実CI・実Codex GitHub AI reviewの代替ではない。

C#/.NET 10 のゲーム外 Playtest 基盤。現在は五つの project、抽象窓口、静的検証の共通 Runner、CLI 骨格、CI と配布 smoke を実装する。

```powershell
dotnet restore --locked-mode
dotnet build --no-restore -c Release
dotnet test --no-build --no-restore -c Release
./scripts/check-boundaries.ps1
./scripts/package-smoke.ps1 -Rid win-x64
```

`global.json` は SDK 10.0.301 以降の同一 major/minor feature band を許可する。Gua は公開 NuGet **1.1.1** を固定し、依存 lock の content hash を保存する。Gua checkout、`../gua`、Node、engine、Codex 認証は build と offline validate に不要。

```powershell
dotnet run --project src/Gua.Playtest.Cli -- validate --gua-schema selector.schema.json selector.json
dotnet run --project src/Gua.Playtest.Cli -- doctor --native
```

`validate --gua-schema` は Gua の embedded schema に対する構造検証だけを行う。成功は exit 0、不正/未実装/取得不能は exit 2、中断は exit 130。stdout は status/code の単一 JSON。ファイルは UTF-8、1 MiB 上限。文書値・例外・ローカルパスは診断へ出さない。Scenario validate は #2、正式 CLI/全終了コードは #15 の担当であり、この段階では非ゼロで未実装を示す。

`doctor --native` は package の core/runtime の ABI/protocol/buildId と embedded Viewer の commit を確認する。ゲームを起動・接続・操作しない。native のない配置は非ゼロ。native override を拒否する。起動成功は engine/bridge の機能証拠ではない。

依存: `Core ← Runner / GuaIntegration / Planners.Codex ← Cli`。Core に engine/Codex package はない。Runner は `IStaticValidator`、clock/observation/planner/host の抽象窓口を使う。具体 Gua schema/native 連携は GuaIntegration。Codex module は #13 未実装を例外で明示し、認証や Codex 本体を同梱しない。Fake は tests にのみ存在する。

Trace writer/reader/詳細 Viewer は Gua の `GuaTraceSession` / `GuaTraceReader` / `GuaTraceReport` をそのまま使い、Playtest 独自形式へ複製しない。実製品の Run への接続は #9。境界テストは public package だけで round-trip、Player profile、秘密値 redaction、HTML の非上書きを確認する。

PR/push CI は build、tests、実際に zip 展開した self-contained archive と独立 tool-path への .NET Tool install を実行する。公開/release job はない。現 smoke は早期実行試験であり、最終配布、ライセンス閉包、engine、実 Codex、全 E2E の完了は #18 で確認する。[要件対応と残件](foundation-evidence.md)。
