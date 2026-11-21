# 小さな冒険者ギルドで薬草の依頼を完了する

Windows x64 と PowerShell 7 で、配布済みの guild-windows（ゲーム commit e0eeaec6b21ae52bfb6e947151c0907d6beebdd2）の guild.exe と同梱 DLL を同じフォルダーに展開します。リポジトリルートから次を実行します。OutputDirectory は毎回新しい場所を指定してください。

```powershell
dotnet restore --locked-mode
dotnet build -c Release --no-restore
./fixtures/guild-herb/Start.ps1 -GameDirectory C:/games/guild-windows -OutputDirectory C:/tests/guild-first
```

Start.ps1 が Playtest CLI を1回起動し、正式な Scenario と Environment を読み込みます。ゲームは専用の保存先とポート18357でヘッドレス起動します。通常版・日本語・1日目・60Gから、開始、道具屋、数量を2回増加、購入確認と確定、受付に戻ってミナと会話、薬草依頼の受注、3回納品、報酬受領の順に進みます。各クリックは Player の guarded dispatch で確認します。RunMonitor は同一の公開UI観測で、薬草依頼の3/3・doneと、残高68G・空の鞄を要求します。購入額は12G、報酬は20Gです。

出力先の command.json に Run ID と結果、runs 配下に RunResult と Trace、report.html に公式 Trace reader から生成したレポートが保存されます。-Case CancelPurchase は購入を取り消し、-Case PartialDelivery は2個だけ納品して報酬受領を押します。どちらも Run が Failed となり、対応する未達状態と残高が保存されたときに対照試験が成功します。

fixed-click-run は click-run と同じ Windows 起動引数に、順序付きの --target-id を1〜64個指定するローカル CLI です。Environment に許可された標準UIの点観測と click を使い、各操作を個別に予約します。送信結果が未確認なら再送せず、残りの手順を停止します。最後の操作後も Goal・期限・取消の判定は Runner が行います。この経路の成果物は RunResult と Trace です。
