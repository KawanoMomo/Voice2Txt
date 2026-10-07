# Voice2Txt

ローカルの Whisper(whisper.cpp)で音声をテキスト化する Windows 常駐ツール。トークキーを押している間だけ録音し、離した時点で文字起こしした結果を、操作中のアプリのカーソル位置へ届ける(プッシュトゥトーク音声入力)。音声を外部へ送らない。

- 仕様: `docs/specs/2026-10-06-foundation-design.md`
- 用語集: `CONTEXT.md`(文書と画面の語はここに揃える)
- オーバーレイの見本: `docs/moc/2026-10-06-ptt-overlay.html`
- ADR ドラフト: `docs/adr/drafts/`
- ライセンス: MIT(`LICENSE`)。依存・実行時に取得するもの(モデルの重み・NVIDIA CUDA の実行時ライブラリ)のライセンスは `THIRD_PARTY_NOTICES.md`

## 構成
- `src/Voice2Txt/` — 常駐アプリ(トレイ・オーバーレイ・トークキー・録音・貼り付け・検証モード)
- `src/Voice2Txt.Core/` — 発話の順序・取り消し・貼り付け先の判定・退避のロジック(キー・マイク・クリップボード・前面ウィンドウ・エンジンは差し替え可能)
- `tests/Voice2Txt.Core.Tests/` — unit(`dotnet test`)
- `tests/scenarios/` — 検証モードの台本(`{persona}-{手順}.json`)。`tools/Verify` が回す
- `tools/MakeFixtures/` — 台本が使う合成音声の素材を `test-results/fixtures/` に作る(Windows の日本語の音声合成。素材はリポジトリに入れない。`tools/Verify` も実行前に足りないものを作る)。`ja-one-sentence.wav`: 1 文、`silence.wav`: 無音 3 秒、`utt-01`〜`utt-18.wav`: 連投・退避・言い直し・言い淀み・途中経過用の文。文は `tools/MakeFixtures/FixtureMaker.cs` 内。`dotnet run --project tools/MakeFixtures -- --force` で作り直すと合成の読みを表示するので、正解文と違う読みになっていないか確かめる

## 使い方
- 起動するとタスクトレイに常駐する。初回はモデル(`large-v3-turbo`)を取得元からダウンロードし、ハッシュで検証してから読み込む(トレイのツールチップに進み具合)
- 版: トレイアイコンのツールチップ(例「Voice2Txt v0.4 — 待機中(モデル large-v3-turbo)」)と右クリックメニューの先頭に、動いている版(タグ `v{major}.{minor}`)が出る。exe の版はビルド時にリポジトリ直下の `VERSION` から入る(`Directory.Build.props`。exe のプロパティの製品バージョンも同じ)。ログの `start version=` にも書く
- トークキー(初期値 右 Ctrl)を押している間だけ録音し、離すと確定版を、押した時点の前面ウィンドウへ Ctrl+V で貼る。ウィンドウが変わっていたら貼らずにクリップボードに残す(退避。Ctrl+V で貼れる)。他のアプリがクリップボードを開いたままなら最大 3 秒粘り、それでも入らなければ「入力失敗」と出す(Ctrl+V では貼れない。ログに `reason=ClipboardBusy`)
- 途中経過: 押している間、オーバーレイの「録音中」の下に、それまでの音声を約 0.7 秒ごとに文字起こしし直した暫定の文字が出る(後から変わることがある。最大 3 行、収まらなければ末尾を残す)。操作中のアプリには書き込まない。届くのは離した後の確定版だけ。前の発話を処理している間は出ない。設定の `showInterim` を `false` にすると出さない
- 設定: `%APPDATA%\Voice2Txt\settings.json`(`talkKey`: トークキーの名前 例 `RControlKey` / `RMenu`、`model`、`minPressSeconds`、`silenceThreshold`、`showInterim`: 押下中にオーバーレイへ途中経過を出す(初期値 `true`)、`removeFillers`: 確定版から言い淀みを取り除く(初期値 `true`)、`fillers`: 取り除く語の一覧(初期値「ええと」「えっと」「ええ」「ああ」「あの」「その」「まあ」など。2 文字の語は前後が句読点・文頭・文末のときだけ取り除く))。トレイのメニュー「設定…」で開く設定画面で、各項目を名前・説明・選択肢付きで変えて保存できる(保存先は同じ settings.json。保存後に「今すぐ再起動」を選べる)。JSON を手で直すときはトレイの「設定ファイルを開く」。変更は再起動で効く(自動起動は保存した時点で効く)。`talkKey` が読めない名前(例 `RAlt`)なら右 Ctrl を使い、トレイの通知とログ(`talkkey-invalid`)で知らせる
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
- `result.json` — 動いた exe の版(`version`)と常駐時のトレイのツールチップの文言(`trayTooltip`)・届いた文字列と順序・届け先(`textbox` / 退避は `clipboard`)・取り除いた言い淀みの数(`fillersRemoved`)・各発話の時間(マイクが開くまで・押していた時間・離してから届くまで)・取り消しの理由・状態の列・テキスト欄の中身
- `shots/NN-{状態}.png` — 状態が変わるごと(途中経過が変わるごとも)のオーバーレイのスクリーンショット。状態の列の `interimChars` はそのとき出していた途中経過の文字数(本文は書かない)
- 本物の前面ウィンドウの検査 — 実行中 25 ms ごとと状態が変わるたびに `GetForegroundWindow` を調べ、`foregroundSamples` / `overlayForegroundCount`・状態ごとの `foreground`(`overlay` / `textbox` / `other`)に残す。オーバーレイが 1 度でも前面になれば、期待に書かなくても tools/Verify は failed にする(フォーカスを奪わない、の受け入れ基準)
- `verify.log`

台本の設定(`settings`): `talkKey` などを台本に書く(検証モードは `%APPDATA%` の settings.json を読まない)。例 `"settings": {"talkKey": "RMenu"}`。モデルを差し替えるときは `"settings": {"model": "base"}`(名前は「使い方」のモデルの表。読み込んだ名前は result.json の `model`、期待は `expect.model`)。
台本の手(`actions[].do`): `waitModel` / `press`(`audio`、`key`)/ `holdUntilAudioEnd` / `release`(`key`)/ `key`(`key` を押して離す。省略時 `C`)/ `focus`(`window`: `textbox` か `other`)/ `wait`(`ms`)/ `waitIdle` / `lockClipboard`(`ms`: その間クリップボードを使えなくする。粘っても入らなければ状態「入力失敗」・取り消しの理由 `ClipboardBusy`)/
`openSettings`(トレイの「設定…」と同じ設定画面を開いて撮る。`name` は shot の名前)/ `setSetting`(`name`: 項目のキー 例 `talkKey`、`value`: 設定ファイルの値か画面の名前 例 `右Alt`)/ `saveSettings`(撮ってから保存を押す。`<dir>/settings.json` に書く)/ `restart`(その settings.json を読み直して受け付け直す = 再起動。モデルが変われば読み込み直す)。
`key` は Keys の名前(`RControlKey` / `RMenu` / `C` など。省略時は設定のトークキー)。キーは本番のキーボードフックと同じ判定に通す: トークキーは握りつぶして録音、それ以外は素通し(録音しない)、トークキーの押下中に別キーを押すと取り消して「トークキー+そのキー」を合成して送る。
結果の `passedKeys`(素通ししたキー。`"RControlKey down"` の形)・`sentKeys`(合成して送ったキー)・`warnings`(例: talkKey を読めない)に残る。
期待(`expect`): `deliveries` / `textbox`(`text` と `minSimilarity`、`notContains`: 残っていてはならない語)/ `states` / `stateTexts`(オーバーレイの文言に含まれる文字列)/ `forbiddenStates` / `cancellations` / `passedKeys` / `sentKeys` / `warnings` / `maxReleaseToDeliverMs` / `version`(版。`versionFile: "VERSION"` ならそのファイルから作る。result.json の `version` と `trayTooltip` に出ていること)/ `screenshots` / `shots`(台本の `shot` で撮ったものを名前で引く。`state`・`minMeter` / `maxMeter`(音量バー)・`minInterimChars` / `maxInterimChars`(途中経過の文字数))/ `savedSettings`(設定画面で保存した値。例 `{"talkKey": "RMenu"}`)/ `settingsErrors`(保存できなかった理由。空配列なら失敗なし)。

全部の台本を回す: `dotnet run --project tools/Verify -- --exe <Voice2Txt.exe> --scenarios tests/scenarios --out <dir>`(`passed: 名前` / `failed: 名前` と理由、最後に `passed=N failed=M`、`<dir>/summary.json`)。台本の `audio` は `test-results/fixtures/` の合成音声を指し、足りなければ実行前に作る(`fixtures: N 本を作った`。日本語の音声合成の声が要る)

## 開発
このリポジトリは自律開発ループが育てる。