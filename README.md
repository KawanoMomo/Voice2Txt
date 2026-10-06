# Voice2Txt

ローカルの Whisper(whisper.cpp)で音声をテキスト化する Windows 常駐ツール。トークキーを押している間だけ録音し、離した時点で文字起こしした結果を、操作中のアプリのカーソル位置へ届ける(プッシュトゥトーク音声入力)。音声を外部へ送らない。

- 仕様: `docs/specs/2026-10-06-foundation-design.md`
- 用語集: `CONTEXT.md`(文書と画面の語はここに揃える)
- オーバーレイの見本: `docs/moc/2026-10-06-ptt-overlay.html`
- ADR ドラフト: `docs/adr/drafts/`

## 構成(基盤で作るもの)
- `src/Voice2Txt/` — 常駐アプリ(トレイ・オーバーレイ・トークキー・録音・貼り付け)
- `src/Voice2Txt.Core/` — 発話の順序・取り消し・貼り付け先の判定・退避のロジック(キー・マイク・クリップボード・前面ウィンドウは差し替え可能)
- `tests/Voice2Txt.Core.Tests/` — unit(`dotnet test`)
- `tests/scenarios/` — 検証モードの台本(`{persona}-{手順}.json`)。`tools/verify` が回す
- 検証モード: `Voice2Txt.exe --verify --scenario <json> --out <dir>` で、音声ファイルを流し込み、トークキーの押下・離しを台本どおりに指示し、結果の文字列・オーバーレイのスクリーンショット・時間を `<dir>` に書く

## 開発
このリポジトリは `E:\04_Loop` の自律開発ループが育てる。main に直接コミットしない(builder が worktree で作り、マージする)。
