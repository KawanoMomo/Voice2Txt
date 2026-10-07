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
- `tests/fixtures/audio/` — 合成音声の素材(`ja-one-sentence.wav`: 1 文、`silence.wav`: 無音 3 秒、`utt-01`〜`utt-11.wav`: 連投・退避・言い直し用の短い文。`make-fixtures.ps1` で作り直せる。文は同スクリプト内。合成の読みを表示するので、正解文と違う読みになっていないか確かめる)

## 使い方
- 起動するとタスクトレイに常駐する。初回はモデル(`large-v3-turbo`)を取得元からダウンロードし、ハッシュで検証してから読み込む(トレイのツールチップに進み具合)
- トークキー(初期値 右 Ctrl)を押している間だけ録音し、離すと確定版を、押した時点の前面ウィンドウへ Ctrl+V で貼る。ウィンドウが変わっていたら貼らずにクリップボードに残す(退避。Ctrl+V で貼れる)。他のアプリがクリップボードを開いたままなら最大 3 秒粘り、それでも入らなければ「入力失敗」と出す(Ctrl+V では貼れない。ログに `reason=ClipboardBusy`)
- 設定: `%APPDATA%\Voice2Txt\settings.json`(`talkKey`: トークキーの名前 例 `RControlKey` / `RMenu`、`model`、`minPressSeconds`、`silenceThreshold`)。トレイのメニュー「設定ファイルを開く」。変更は再起動で効く。`talkKey` が読めない名前(例 `RAlt`)なら右 Ctrl を使い、トレイの通知とログ(`talkkey-invalid`)で知らせる
- 自動起動: トレイのメニュー「ログオン時に起動する」で切り替える(初期値オフ。設定の `autoStart` と HKCU の Run の値 `Voice2Txt` を一致させる。起動時にも合わせ直す)
- モデル: 設定の `model` で選ぶ(初期値 `large-v3-turbo`。変更は再起動で効く)。置き場は `%APPDATA%\Voice2Txt\models\{ファイル名}`。無ければ初回に取得元(Hugging Face ggerganov/whisper.cpp)からダウンロードし、SHA-256 で検証してから使う(手動で置いてもよい。ハッシュが合えば使う)。読み込んだモデルはトレイのツールチップ「待機中(モデル …)」とログ `engine-ready model=` で分かる。
  知らない名前ならエラーにして、トレイの通知とログ(`engine-error 未知のモデル: …`)に選べる名前の一覧を出す。名前の代わりにファイル名(例 `ggml-small.bin`)でもよい。大文字小文字は問わない

  | `model` | ファイル名 | おおよそのサイズ |
  |---|---|---|
  | `tiny` | `ggml-tiny.bin` | 78 MB |
  | `base` | `ggml-base.bin` | 148 MB |
  | `small` | `ggml-small.bin` | 488 MB |
  | `medium` | `ggml-medium.bin` | 1.5 GB |
  | `large-v3-turbo`(初期値) | `ggml-large-v3-turbo.bin` | 1.6 GB |
  | `large-v3` | `ggml-large-v3.bin` | 3.1 GB |
- ログ: `%APPDATA%\Voice2Txt\logs\app.log`(時間・取り消しの理由などのメタ情報だけ。文字起こしの本文は書かない)
- バックエンド: CUDA を先に試し、読めなければ CPU(ログの `engine-ready runtime=`)。CUDA に要る `cudart64_13.dll` / `cublas64_13.dll` / `cublasLt64_13.dll` は、ビルド時に NVIDIA の公式 redist から取得し SHA-256 を確かめて exe の横に置く(`src/Voice2Txt/CudaRedist.targets`、キャッシュ `%LOCALAPPDATA%\Voice2Txt\redist`、`-p:FFV_SkipCudaRedist=true` で取得しない)。実行時はネットに出ない。起動時に無音 1 秒で暖機してから受け付ける

## 検証モード
`Voice2Txt.exe --verify --scenario <json> --out <dir>` — マイクの代わりに台本の音声ファイルを流し込み、トークキーの押下・離しを台本どおりに指示し、
確定版をアプリ自身が開く「検証用のテキスト欄」へ届ける。本物のマイク・クリップボード・キーボードは触らない。`<dir>` に書くもの:
- `result.json` — 届いた文字列と順序・届け先(`textbox` / 退避は `clipboard`)・各発話の時間(マイクが開くまで・押していた時間・離してから届くまで)・取り消しの理由・状態の列・テキスト欄の中身
- `shots/NN-{状態}.png` — 状態が変わるごとのオーバーレイのスクリーンショット
- 本物の前面ウィンドウの検査 — 実行中 25 ms ごとと状態が変わるたびに `GetForegroundWindow` を調べ、`foregroundSamples` / `overlayForegroundCount`・状態ごとの `foreground`(`overlay` / `textbox` / `other`)に残す。オーバーレイが 1 度でも前面になれば、期待に書かなくても tools/Verify は failed にする(フォーカスを奪わない、の受け入れ基準)
- `verify.log`

台本の設定(`settings`): `talkKey` などを台本に書く(検証モードは `%APPDATA%` の settings.json を読まない)。例 `"settings": {"talkKey": "RMenu"}`。モデルを差し替えるときは `"settings": {"model": "base"}`(名前は「使い方」のモデルの表。読み込んだ名前は result.json の `model`、期待は `expect.model`)。
台本の手(`actions[].do`): `waitModel` / `press`(`audio`、`key`)/ `holdUntilAudioEnd` / `release`(`key`)/ `key`(`key` を押して離す。省略時 `C`)/ `focus`(`window`: `textbox` か `other`)/ `wait`(`ms`)/ `waitIdle` / `lockClipboard`(`ms`: その間クリップボードを使えなくする。粘っても入らなければ状態「入力失敗」・取り消しの理由 `ClipboardBusy`)。
`key` は Keys の名前(`RControlKey` / `RMenu` / `C` など。省略時は設定のトークキー)。キーは本番のキーボードフックと同じ判定に通す: トークキーは握りつぶして録音、それ以外は素通し(録音しない)、トークキーの押下中に別キーを押すと取り消して「トークキー+そのキー」を合成して送る。
結果の `passedKeys`(素通ししたキー。`"RControlKey down"` の形)・`sentKeys`(合成して送ったキー)・`warnings`(例: talkKey を読めない)に残る。
期待(`expect`): `deliveries` / `textbox`(`text` と `minSimilarity`)/ `states` / `stateTexts`(オーバーレイの文言に含まれる文字列)/ `forbiddenStates` / `cancellations` / `passedKeys` / `sentKeys` / `warnings` / `maxReleaseToDeliverMs` / `screenshots`。

全部の台本を回す: `dotnet run --project tools/Verify -- --exe <Voice2Txt.exe> --scenarios tests/scenarios --out <dir>`(`passed: 名前` / `failed: 名前` と理由、最後に `passed=N failed=M`、`<dir>/summary.json`)

## 開発
このリポジトリは `E:\04_Loop` の自律開発ループが育てる。main に直接コミットしない(builder が worktree で作り、マージする)。