# Voice2Txt

ローカルの Whisper(whisper.cpp)で音声をテキスト化する Windows 常駐ツール。トークキーを押している間だけ録音し、離した時点で文字起こしした結果を、操作中のアプリのカーソル位置へ届ける(プッシュトゥトーク音声入力)。音声を外部へ送らない。

- 仕様: `docs/specs/2026-10-06-foundation-design.md`
- 用語集: `CONTEXT.md`(文書と画面の語はここに揃える)
- オーバーレイの見本: `docs/moc/2026-10-06-ptt-overlay.html`
- ADR ドラフト: `docs/adr/drafts/`

## 構成
- `src/Voice2Txt/` — 常駐アプリ(トレイ・オーバーレイ・トークキー・録音・貼り付け・検証モード)
- `src/Voice2Txt.Core/` — 発話の順序・取り消し・貼り付け先の判定・退避のロジック(キー・マイク・クリップボード・前面ウィンドウ・エンジンは差し替え可能)
- `tests/Voice2Txt.Core.Tests/` — unit(`dotnet test`)
- `tests/scenarios/` — 検証モードの台本(`{persona}-{手順}.json`)。`tools/Verify` が回す
- `tests/fixtures/audio/` — 合成音声の素材(`ja-one-sentence.wav`: 1 文、`silence.wav`: 無音 3 秒)

## 使い方
- 起動するとタスクトレイに常駐する。初回はモデル(`large-v3-turbo`)を取得元からダウンロードし、ハッシュで検証してから読み込む(トレイのツールチップに進み具合)
- トークキー(初期値 右 Ctrl)を押している間だけ録音し、離すと確定版を、押した時点の前面ウィンドウへ Ctrl+V で貼る。ウィンドウが変わっていたら貼らずにクリップボードに残す(退避。Ctrl+V で貼れる)。他のアプリがクリップボードを開いたままなら最大 3 秒粘り、それでも入らなければ「入力失敗」と出す(Ctrl+V では貼れない。ログに `reason=ClipboardBusy`)
- 設定: `%APPDATA%\Voice2Txt\settings.json`(`talkKey`: トークキーの名前 例 `RControlKey` / `RMenu`、`model`、`minPressSeconds`、`silenceThreshold`)。トレイのメニュー「設定ファイルを開く」。変更は再起動で効く
- 自動起動: トレイのメニュー「ログオン時に起動する」で切り替える(初期値オフ。設定の `autoStart` と HKCU の Run の値 `Voice2Txt` を一致させる。起動時にも合わせ直す)
- モデル: `%APPDATA%\Voice2Txt\models\ggml-large-v3-turbo.bin`(手動で置いてもよい。ハッシュが合えば使う)
- ログ: `%APPDATA%\Voice2Txt\logs\app.log`(時間・取り消しの理由などのメタ情報だけ。文字起こしの本文は書かない)
- バックエンド: CUDA を先に試し、読めなければ CPU(ログの `engine-ready runtime=`)。CUDA に要る `cudart64_13.dll` / `cublas64_13.dll` / `cublasLt64_13.dll` は、ビルド時に NVIDIA の公式 redist から取得し SHA-256 を確かめて exe の横に置く(`src/Voice2Txt/CudaRedist.targets`、キャッシュ `%LOCALAPPDATA%\Voice2Txt\redist`、`-p:FFV_SkipCudaRedist=true` で取得しない)。実行時はネットに出ない。起動時に無音 1 秒で暖機してから受け付ける

## 検証モード
`Voice2Txt.exe --verify --scenario <json> --out <dir>` — マイクの代わりに台本の音声ファイルを流し込み、トークキーの押下・離しを台本どおりに指示し、
確定版をアプリ自身が開く「検証用のテキスト欄」へ届ける。本物のマイク・クリップボード・キーボードは触らない。`<dir>` に書くもの:
- `result.json` — 届いた文字列と順序・届け先(`textbox` / 退避は `clipboard`)・各発話の時間(マイクが開くまで・押していた時間・離してから届くまで)・取り消しの理由・状態の列・テキスト欄の中身
- `shots/NN-{状態}.png` — 状態が変わるごとのオーバーレイのスクリーンショット
- `verify.log`

台本の手(`actions[].do`): `waitModel` / `press`(`audio`)/ `holdUntilAudioEnd` / `release` / `key`(押下中の別キー)/ `focus`(`window`: `textbox` か `other`)/ `wait`(`ms`)/ `waitIdle` / `lockClipboard`(`ms`: その間クリップボードを使えなくする。粘っても入らなければ状態「入力失敗」・取り消しの理由 `ClipboardBusy`)。
期待(`expect`): `deliveries` / `textbox`(`text` と `minSimilarity`)/ `states` / `forbiddenStates` / `cancellations` / `maxReleaseToDeliverMs` / `screenshots`。

全部の台本を回す: `dotnet run --project tools/Verify -- --exe <Voice2Txt.exe> --scenarios tests/scenarios --out <dir>`(`passed: 名前` / `failed: 名前` と理由、最後に `passed=N failed=M`、`<dir>/summary.json`)

## 開発
このリポジトリは `E:\04_Loop` の自律開発ループが育てる。main に直接コミットしない(builder が worktree で作り、マージする)。