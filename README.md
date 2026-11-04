# Voice2Txt

ローカルで音声をテキスト化する Windows 常駐ツール(文字起こしのエンジンは Whisper(whisper.cpp)か、日本語専用の Haas(speech.cpp)を選ぶ)。トークキーを押している間だけ録音し、離した時点で文字起こしした結果を、操作中のアプリのカーソル位置へ届ける(プッシュトゥトーク音声入力)。音声を外部へ送らない。

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
- 終了: タスクトレイ(通知領域)の Voice2Txt のアイコンを右クリックし、メニューの最後の「終了」を選ぶ(右クリックのメニューは上から 版 / 設定… / 設定ファイルを開く / 設定フォルダを開く / ログオン時に起動する / 終了)。アイコンが見当たらないときは、タスクバー右端の「^」(隠れているインジケーターを表示)を開くと中にある。設定画面からは終了できない。次のサインインで起動させたくなければ、同じメニューの「ログオン時に起動する」のチェックも外す
- 版: トレイアイコンのツールチップ(例「Voice2Txt v0.4 — 待機中(モデル large-v3-turbo)」)と右クリックメニューの先頭に、動いている版(タグ `v{major}.{minor}`)が出る。exe の版はビルド時にリポジトリ直下の `VERSION` から入る(`Directory.Build.props`。exe のプロパティの製品バージョンも同じ)。ログの `start version=` にも書く
- トークキー(初期値 右 Ctrl)を押している間だけ録音し、離すと確定版を、押した時点の前面ウィンドウへ Ctrl+V で貼る。ウィンドウが変わっていたら貼らずにクリップボードに残す(退避。Ctrl+V で貼れる)。他のアプリがクリップボードを開いたままなら最大 3 秒粘り、それでも入らなければ「入力失敗」と出す(Ctrl+V では貼れない。ログに `reason=ClipboardBusy`)
- 押さない録音: 設定「録音のしかた」(`recordMode`: `hold` 初期値 / `toggle`)を「押さない録音」にすると、開始キー(`startKey`、初期値 `Ctrl+Alt+Space`)で録音を始め、終了キー(`stopKey`、初期値 同じ。同じなら押すたびに始める・止めるが切り替わる)で止めて確定版を届ける。録音中に他のキー・ショートカット・マウス・窓の切り替えをしても録音は途切れず、キーはそのままアプリに渡る(開始・終了キーの最後のキーだけは渡さない)。貼り付け先は開始キーを押した時点の前面。この間トークキーは録音に使わない。組み合わせは `Ctrl` / `Alt` / `Shift` / `Win` と 1 つのキー(A〜Z・0〜9・F1〜F24・Space・Pause など)を `+` でつなぐ(文字・数字・Space は Ctrl・Alt・Win のどれかと組み合わせる)。読めない値は既定に戻し、トレイの通知とログ(`recordmode-invalid`)で知らせる。オーバーレイの録音中の案内は「Ctrl+Alt+Space で入力」
- その場の押さない録音: 録音のしかたが「押している間だけ録音」のままでも、トークキーを押していないときに開始キー(`startKey`)を押すと、その発話だけ押さない録音になる(録音中は他のキー・トークキーもアプリに渡り、取り消さない)。終了キー(`stopKey`)で止めて確定版を届けると、トークキーの押している間だけ録音に戻る。設定の切り替えも再起動も要らない。トークキーを押している間に開始キーを押したときは、他のキーと同じく取り消し。ログは `record-mode hold talkKey=… start=… stop=…`
- 途中経過: 押している間、オーバーレイの「録音中」の下に、それまでの音声を約 0.7 秒ごとに文字起こしし直した暫定の文字が出る(後から変わることがある。最大 3 行、収まらなければ末尾を残す。25 秒より長く話すと直近 25 秒ほどだけを文字起こしし直して頭に「…」を付けるので、長く話しても出し直しの間隔は延びない。確定版は全体から作る)。操作中のアプリには書き込まない。届くのは離した後の確定版だけ。前の発話を処理している間は出ない。設定の `showInterim` を `false` にすると出さない。CPU で動いているときは自動で出さない(確定版を遅らせないため)
- オーバーレイの状態表示: オーバーレイの一番下の案内の行が、状態の印と本文で今の状態を知らせる(フォーカスを奪わず、クリックは素通りする)。状態の名前は検証モードの結果の `state` とスクショ名 `NN-{状態}.png`(`+` は `_`)と同じ。本文の `右Ctrl` はトークキーの名前(押さない録音では録音中が「Ctrl+Alt+Space で入力」)。「消えるまで」の無い状態は、その状態が続く間出ている

  | 状態 | 印 | 本文 | 意味 |
  |---|---|---|---|
  | 準備中 | 灰色の点 | `準備中…  赤い点が出てから話してください` | トークキーを押してからマイクが開くまで。この間の声は録られないので、赤い点 ● の「録音中」に変わってから話す。前の発話を文字起こししている間に次を押したときも、マイクが開くまで一瞬出る |
  | 録音中 | 赤い点 ●(脈打つ) | `録音中  右Ctrl を離すと入力` | 録音している。離すと確定版を作って貼り付け先ウィンドウへ届ける。押している間は上に途中経過が出る(上の「途中経過」) |
  | 処理中+次を録音中 | 赤い点 ● | `録音中  処理待ち 1件` | 前の発話を文字起こししている間に次を録音している。数は確定版を作っている・待っている前の発話の件数で、録音した順に 1 件ずつ届く(発話は失われない)。録音のしかたは「録音中」と同じ |
  | 処理中 | 回る輪 | `文字起こし中…` | 離した後、確定版を作っている |
  | 貼り付け完了 | ✓ | `入力しました` | 貼り付け先ウィンドウへ届けた(約 0.8 秒で消える) |
  | 退避 | ! | `ウィンドウが変わったため貼り付けませんでした — Ctrl+V で貼れます` | 上の「トークキー」の段落(約 4 秒で消える) |
  | 入力失敗 | ! | `他のアプリがクリップボードを使用中のため入力できませんでした — もう一度話してください` | 上の「トークキー」の段落(約 6 秒で消える) |
  | (持ち越した知らせ) | 黄色の文字 | 録音中・処理中の行の末尾に `! 入力できなかった 1件` | 次の発話を録音・処理している間に退避・入力失敗が起きた。その間は行の末尾に件数だけを出し、録音と処理が済んだら(貼り付け完了の代わりに)退避・入力失敗としてまとめて知らせる。後の貼り付けでクリップボードを上書きした後は `ウィンドウが変わったため 2件 貼り付けませんでした — 後の入力で上書きしたため Win+V の履歴から貼れます`(Ctrl+V では出ない。最後の 1 件がまだクリップボードにあれば「最後の 1件は Ctrl+V、前の分は Win+V の履歴から貼れます」。約 6 秒で消える) |
  | 取り消し(無音) | — | `音声が検出されませんでした` | 声が無い・言い淀みだけで、何も届けなかった(約 1.5 秒で消える) |
  | 取り消し | — | `取り消しました(押す時間が短すぎます)` | 押す時間が設定 `minPressSeconds` より短く、何も届けなかった。括弧の中は録音・文字起こしに失敗したときはその旨(約 1.2 秒で消える)。トークキーを押している間に他のキーを押したときは何も出さずに消える |
  | モデル準備中 | 回る輪 | `モデル準備中 — 完了まで音声入力は使えません` | モデル・エンジンを用意している途中(「モデル準備中」の後の括弧に進み具合が入る)。待てば使える。下の「モデル」「エンジン」「バックエンド」の段落(離した後も約 4 秒出す) |
  | 使えない | ! | 理由と直し方 | 用意に失敗し、設定を直して再起動するまで使えない。下の「エンジン」の段落(離した後も約 6 秒出す) |
- 設定: `%APPDATA%\Voice2Txt\settings.json`(`talkKey`: トークキーの名前 例 `RControlKey` / `RMenu`、`model`、`backend`: `auto` / `cuda` / `vulkan` / `cpu`(初期値 `auto`。下の「バックエンド」)、`minPressSeconds`、`silenceThreshold`、`showInterim`: 押下中にオーバーレイへ途中経過を出す(初期値 `true`)、`removeFillers`: 確定版から言い淀みを取り除く(初期値 `true`)、`fillers`: 取り除く語の一覧(初期値「ええと」「えっと」「ええ」「ああ」「あの」「その」「まあ」など。2 文字の語は前後が句読点・文頭・文末のときだけ取り除く))。トレイのメニュー「設定…」で開く設定画面で、各項目を名前・説明・選択肢付きで変えて保存できる(保存先は同じ settings.json。保存後に「今すぐ再起動」を選べる)。JSON を手で直すときはトレイの「設定ファイルを開く」。変更は再起動で効く(自動起動は保存した時点で効く)。`talkKey` が読めない名前(例 `RAlt`)なら右 Ctrl を使い、トレイの通知とログ(`talkkey-invalid`)で知らせる
- 自動起動: トレイのメニュー「ログオン時に起動する」で切り替える(初期値オフ。設定の `autoStart` と HKCU の Run の値 `Voice2Txt` を一致させる。起動時にも合わせ直す)
- 旧名からの引き継ぎ: 製品名は `Voice2Txt`(旧名は接頭辞付き)。通常起動の最初に、`%APPDATA%` と `%LOCALAPPDATA%` に旧名のフォルダがあれば中身(設定・モデル・ログ・CUDA の実行時ライブラリ)を `Voice2Txt` のフォルダへ移し(新しい側に同じ名前のファイルがあれば新しい方を残す。使用中で移せないものは旧フォルダに残す)、HKCU の Run の旧名の値を外す(`autoStart` がオンなら `Voice2Txt` で登録し直す)。ログ `legacy-migrated …`。旧名の版が動いている間は起動せず、終了を促す。検証モードは何も移さない
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
- エンジン: 設定 `engine`(初期値 `whisper`。無くても同じ)。`haas` にすると日本語 ASR の Haas-ja-v1.0(Third Intelligence、Apache-2.0)で文字起こしする。Windows では公式の実装(NeMo)が動かないので、speech.cpp(nyosegawa、MIT、Vulkan / CPU)の `speech worker` を常駐させて JSON Lines で往復する。speech.cpp は PATH の `speech.exe` を使い、無ければ release の書庫(v0.10.1、Windows x64 Vulkan)を SHA-256 で確かめて `%APPDATA%\Voice2Txt\tools\speech\` に取得する。重み(`Haas-616M-ja-v1.0-Q8_0.gguf`、0.66 GB)は `%APPDATA%\Voice2Txt\models\` に SHA-256 で確かめて取得する(取得元・版・ハッシュは `src/Voice2Txt.Core/SpeechEngine.cs`)。`backend` は speech.cpp の `--device` に読み替える(`auto` → 自動、`cuda` / `vulkan` → GPU(Vulkan)、`cpu` → CPU。CUDA の実行時ライブラリは取得しない)。トレイのツールチップは「待機中(エンジン haas、モデル haas-ja-v1.0、Vulkan)」。speech.cpp か重みを取得できない・worker が起動しないときは **Whisper に切り替えず**、トレイを「Haas エンジンを用意できません」にして通知とログ `engine-error` に理由を出す(`engine` を `whisper` に戻すか、取得できる状態にして再起動する)。用意に失敗した後にトークキーを押すと、オーバーレイは「モデル準備中」で待たせず「Haas エンジンを用意できないため音声入力は使えません — 設定のエンジンを Whisper に戻すか、取得できる状態にして再起動してください」と出す(Whisper のモデルを用意できないときも同じく、使えないことと直し方を出す)。この知らせは離した後も約 6 秒出しておく(モデル準備中の知らせは約 4 秒。その間に用意ができれば消える)。このときの状態の名前は「使えない」(ログ `state 使えない`、検証モードの結果の `state`、スクショ名 `NN-使えない.png`。本当に準備中のときは「モデル準備中」)。読めない値は Whisper にし、通知とログ `engine-invalid` で知らせる。Haas は 1 回の要求に文がいくつも入ると文を丸ごと落とす(配布元の注意)ので、30 秒を超える発話は声の区間(発話区間)ごとに文字起こししてつなぐ(区切り方は speech.cpp の `speech asr --vad` の既定に合わせる: 区間の前後に 0.3 秒の余白、0.5 秒の無音で区切る、区間は 10 秒まで(超えれば中の最も静かな所で切る)、0.25 秒未満の音は区間にしない。声の判定は無音の取り消しと同じく録音の大きさに合わせるので、小さい声の区間も落とさない。ログ `regions n= sec= max=`)。30 秒以下は今までどおり 1 回で渡す。英語の文は日本語として書かれる
- 発話に無い 1 文字の英字: Haas は言い淀み・息を「h」「a」のような小文字 1 文字で書くことがある(「ええと」→「aと」)。engine=haas のときは、届ける前に、どの語にも付いていない小文字 1 文字の英字(両隣が句読点か文頭・文末の「h、」「、a、」と、「ええと」を書いた「aと、」「a と」)だけを取り除く(whisper では取り除かない: whisper は断片を書かない。whisper も Haas も「x軸とy軸」「変数x」「n個」のように話した英字を日本語に付けて小文字 1 文字で書くので、日本語に付いた英字は残す。そのため Haas が日本語に付けて書いた断片「三部aと五部」は残る。言い淀みの除去より先。設定では止めない。途中経過も同じ)。大文字 1 文字(「A案」「ビタミンC」)・英単語・英文(「a few」)・型番・単位(「3x4」「5 m」「e-mail」)は残す
- カスタム語彙(どちらのエンジンでも): 設定 `keywords`(画面の「カスタム語彙」)に、固有名詞や同音の別の表記で書かれてしまう語(例 「速達」が「即達」になる)を、書いてほしい表記で並べる。1 語は `表記` か `表記=よみ,よみ`(例 `Haas=ハース`。読みが効くのはエンジンが Haas のときで、Whisper は表記だけを使う)。その語が話されたときにこの表記で書かれやすくなる。Whisper(既定)は表記を読点でつないで初期プロンプトの前に足し、さらに復号の 1 手ごとに表記の続きになる語へ寄せる(初期プロンプトだけでは動詞の活用形「寄って」が「酔って」のまま変わらない。寄せて語が変わったときは同じ音声を寄せずに復号し直して比べ、変わった所が語彙の表記の丸ごとの中にあり、文の確からしさが下がらないときだけ寄せた方で届ける。その発話は復号が 2 回になる。Windows の日本語 IME(MSIME。OS のもの)で文の読みが分かるときは、既定の Whisper が強く信じる同音の語(「週末に車で」が「来るまで」)も寄せる: 寄せずに復号し、語彙の頭が候補に挙がった手のうち、語彙の読みがその手から後ろの読みの頭と合うものでだけ語彙を選ばせて復号し直し、寄せた文と寄せない文の読みが同じ(同音の書き分け)ときだけ寄せた方で届ける(復号し直すのは 1 発話で高々 3 回。IME が無ければ前の寄せ方。ログ `keyword-bias ... reading=ime|none`)。ログ `keyword-bias`。起動時に読む。語彙を変えたら再起動)。Haas は speech.cpp の `keywords` に要求ごとに渡す(強さ・誤って書く場合の抑え(`keywords_alpha` / `keywords_guard`)は speech.cpp の既定)。空なら今までどおり。読めない語(空の表記・空の読み・`=` が 2 つ)は渡さず、通知とログ `keywords-invalid` で知らせる。**制約**: カスタム語彙を渡すと speech.cpp は文字起こしの時刻(timestamps)を返さない(本アプリは時刻を使わないので動きは変わらない)。語彙はどの発話にも効くので、言っていない所でも表記が変わることがある(必要な語だけ並べる)。Whisper では語彙が長すぎると先頭の語から効かなくなる(数・言い淀みの書き方の部分は残る)
- バックエンド: 設定 `backend`(初期値 `auto`)。`auto` は CUDA → Vulkan → CPU の順に読めるものを使う。`cuda` / `vulkan` / `cpu` はそれを先に試し、読めなければ CPU で動く(ログの `engine-ready runtime=Cuda|Vulkan|Cpu`、検証モードの結果の `runtime` / `backend`。読めない値は `auto` にし、通知とログ `backend-invalid` で知らせる)。使っているものはトレイのツールチップ「待機中(モデル …、CUDA|Vulkan|CPU)」に出る。選んだものと違うもので動いたとき・CPU に落ちたときは通知で知らせる。CPU では途中経過を出さない(`showInterim` がオンでも。結果の `interim`)。Vulkan は whisper.cpp の Vulkan 版(Whisper.net.Runtime.Vulkan、MIT)を同梱し、Vulkan のローダー(`vulkan-1.dll`)は利用者の GPU ドライバのものを使う(同梱しない)。RTX 2080 Ti の実測で 1 文の離してから届くまで CUDA 約 0.23 秒・Vulkan 約 0.37 秒・CPU 約 16 秒(Vulkan は起動時の用意に約 13 秒)。CUDA に要る NVIDIA の `cudart64_13.dll` / `cublas64_13.dll` / `cublasLt64_13.dll` はビルド成果物・配布物に含めない。初回起動時に(NVIDIA のドライバがある PC だけ)NVIDIA の公式 redist から取得し、SHA-256 を確かめて `%LOCALAPPDATA%\Voice2Txt\runtime\` に置き、そこから読む(`src/Voice2Txt.Core/CudaRuntime.cs`。取得中はオーバーレイ・トレイに「モデル準備中(CUDA の準備中 …)」、取得できなければ通知で「CUDA を使えません: …」と知らせて次の Vulkan / CPU で動く。ログ `cuda-runtime …`)。設定 `fetchCudaRuntime` を `false` にすると取得しない。`backend` が `vulkan` / `cpu` なら取得も読み込みもしない。先に取得しておくには `Voice2Txt.exe --prepare-cuda`(インストーラの最後など。`runtime\download\` に取得元と同じ zip があればそれを照合して使う)。検証モードは取得せず、取得済みの runtime があればそれを読む(結果の `cudaRuntime`)。ネットに出るのは下の「初回起動で取得するもの」だけ。起動時に無音 1 秒で暖機してから受け付ける

## 検証モード
`Voice2Txt.exe --verify --scenario <json> --out <dir>` — マイクの代わりに台本の音声ファイルを流し込み、トークキーの押下・離しを台本どおりに指示し、
確定版をアプリ自身が開く「検証用のテキスト欄」へ届ける。本物のマイク・クリップボード・キーボードは触らない。`<dir>` に書くもの:
- `result.json` — 動いた exe の版(`version`)と常駐時のトレイのツールチップの文言(`trayTooltip`)・届いた文字列と順序・届け先(`textbox` / 退避は `clipboard`)・取り除いた言い淀みの数(`fillersRemoved`)・各発話の時間(マイクが開くまで・押していた時間・離してから届くまで)・取り消しの理由・状態の列・テキスト欄の中身
- `shots/NN-{状態}.png` — 状態が変わるごと(途中経過が変わるごとも)のオーバーレイのスクリーンショット。状態の列の `interimChars` はそのとき出していた途中経過の文字数(本文は書かない)
- 本物の前面ウィンドウの検査 — 実行中 25 ms ごとと状態が変わるたびに `GetForegroundWindow` を調べ、`foregroundSamples` / `overlayForegroundCount`・状態ごとの `foreground`(`overlay` / `textbox` / `other`)に残す。オーバーレイが 1 度でも前面になれば、期待に書かなくても tools/Verify は failed にする(フォーカスを奪わない、の受け入れ基準)
- `verify.log`

台本の設定(`settings`): `talkKey` などを台本に書く(検証モードは `%APPDATA%` の settings.json を読まない)。例 `"settings": {"talkKey": "RMenu"}`。モデルを差し替えるときは `"settings": {"model": "base"}`(名前は「使い方」のモデルの表。読み込んだ名前は result.json の `model`、期待は `expect.model`)。
台本の手(`actions[].do`): `waitModel` / `press`(`audio`、`key`)/ `holdUntilAudioEnd`(流し終わるまで押し続ける。押下が録音になっていない — モデル準備中など — なら待たずに返り、理由を結果の `holdSkips` に残す。期待は `expect.holdSkips`)/ `release`(`key`)/ `key`(`key` を押して離す。省略時 `C`)/ `focus`(`window`: `textbox` か `other`)/ `wait`(`ms`)/ `waitIdle` / `lockClipboard`(`ms`: その間クリップボードを使えなくする。粘っても入らなければ状態「入力失敗」・取り消しの理由 `ClipboardBusy`)/
`openSettings`(トレイの「設定…」と同じ設定画面を開いて撮る。`name` は shot の名前。結果の shot の `values` に撮った時点で画面に出ている各項目の値を設定ファイルの形で残し、期待 `expect.shots[].values` で確かめる。選択式の値が画に写らなければ描き直し・画面からの写し・書き足しで補い、`capture` と `filled` に残す)/ `setSetting`(`name`: 項目のキー 例 `talkKey`、`value`: 設定ファイルの値か画面の名前 例 `右Alt`)/ `saveSettings`(撮ってから保存を押す。`<dir>/settings.json` に書く)/ `restart`(その settings.json を読み直して受け付け直す = 再起動。モデルが変われば読み込み直す。結果の発話の番号は再起動の前の続きから振る)。
`key` は Keys の名前(`RControlKey` / `RMenu` / `C` など。省略時は設定のトークキー)。キーは本番のキーボードフックと同じ判定に通す: トークキーは握りつぶして録音、それ以外は素通し(録音しない)、トークキーの押下中に別キーを押すと取り消して「トークキー+そのキー」を合成して送る。
手 `key` は `Ctrl+V` のように `+` でつないだ組み合わせも 1 手で送る(修飾キー `Ctrl` / `Shift` / `Alt` を押したまま最後のキーを押して離す。修飾キーはトークキーでない側の左右)。検証用のテキスト欄が前面のときの `Ctrl+V` は、利用者の貼り付けとしてクリップボードの中身を欄へ入れる(退避した文を元の欄へ貼り直す手順を確かめる)。
押さない録音(`settings.recordMode: "toggle"`)と、押している間だけ録音の中のその場の押さない録音では、`press` / `release` の `key` に開始キー・終了キーの組み合わせ(例 `Ctrl+Alt+Space`)を書くと 1 手で押して離す(`press` の `audio` はそのとき流し始める)。
結果の `passedKeys`(素通ししたキー。`"RControlKey down"` の形)・`sentKeys`(合成して送ったキー)・`warnings`(例: talkKey を読めない)に残る。
期待(`expect`): `deliveries`(`to`: 貼った先 `textbox` / `other`(貼った時点の仮の前面。`other` へ貼った文は検証用のテキスト欄に入らない)、退避なら `clipboard`)/ `textbox`(`text` と `minSimilarity`、`notContains`: 残っていてはならない語)/ `states` / `stateTexts`(オーバーレイの文言に含まれる文字列)/ `forbiddenStates` / `cancellations`(下の理由名の文字列の配列。発話の順に並べ、取り消しの無い台本は `[]`。例 `["ModelNotReady", "TooShort"]`)/ `passedKeys` / `sentKeys` / `warnings` / `maxReleaseToDeliverMs` / `version`(版。`versionFile: "VERSION"` ならそのファイルから作る。result.json の `version` と `trayTooltip` に出ていること)/ `screenshots` / `shots`(台本の `shot` で撮ったものを名前で引く。`state`・`minMeter` / `maxMeter`(音量バー)・`minInterimChars` / `maxInterimChars`(途中経過の文字数))/ `savedSettings`(設定画面で保存した値。例 `{"talkKey": "RMenu"}`)/ `settingsErrors`(保存できなかった理由。空配列なら失敗なし)。

取り消しの理由名(`cancellations` に書く値。結果の `cancellations[].reason` と同じ。ログでは `reason=`): `TooShort` = 短い押下の取り消し(押す時間が短すぎる)/ `OtherKey` = トークキーと他のキーを一緒に押した取り消し /
`Silence` = 無音の取り消し(音声が検出されない)/ `NoText` = 文字起こしの結果が空だった取り消し(言い淀みだけの発話も)/ `ModelNotReady` = モデル準備中(使えないときも)に押した押下 /
`Error` = 録音か文字起こしに失敗した取り消し / `ClipboardBusy` = 入力失敗(取り消しではなく、確定版はできたが他のアプリがクリップボードを使い続けて届けられなかった結末。発話の順に同じ配列へ入る)。
退避は届いた側(`deliveries` の `to: clipboard`)に入り、`cancellations` には入らない。

全部の台本を回す: `dotnet run --project tools/Verify -- --exe <Voice2Txt.exe> --scenarios tests/scenarios --out <dir>`(`passed: 名前` / `failed: 名前` と理由、最後に `passed=N failed=M`、`<dir>/summary.json`)。台本の `audio` は `test-results/fixtures/` の合成音声を指し、足りなければ実行前に作る(`fixtures: N 本を作った`。日本語の音声合成の声が要る)

## 配布物
タグ `v*` を push すると GitHub Actions(`.github/workflows/windows-app.yml`。手動実行は `tag` 入力で同じ版を作り直す)が、
`Voice2Txt-{版}-setup.exe`(Inno Setup。既定は自分だけに入れる。管理者権限は要らない)と `Voice2Txt-{版}-portable.zip`(フォルダごと展開してから `Voice2Txt\Voice2Txt.exe` を起動)を作り、Release に付ける。
手元で同じものを作るには `powershell -File packaging\build.ps1 -Version 1.0 -OutDir dist`(Inno Setup 6 が無ければ zip まで)。

- 中身: 自作コードと MIT の依存だけ(.NET ランタイム(self-contained、win-x64)・Whisper.net・whisper.cpp / ggml・NAudio)。`LICENSE.txt`・`THIRD_PARTY_NOTICES.md`・`README.txt`(初回起動で取得するもの)を同梱する。版はタグ(exe と setup.exe の版を照合する)
- 含めないもの: NVIDIA の CUDA 実行時ライブラリとモデルの重み。`build.ps1` は publish の中に `cudart*` / `cublas*` などの NVIDIA の DLL、Vulkan のローダー `vulkan-1.dll` か `.bin` があれば止める
- **初回起動で取得するもの**(ネットに出るのはこれだけ。どれも固定の取得元から取り、SHA-256 を照合する)。エンジンが Whisper(既定)のとき: Whisper のモデルの重み(初期値 `large-v3-turbo` 約 1.6 GB、Hugging Face ggerganov/whisper.cpp から。`%APPDATA%\Voice2Txt\models\`)と、NVIDIA の GPU がある PC では CUDA の実行時ライブラリ(cudart / cuBLAS 約 500 MB、NVIDIA の公式 redist から。`%LOCALAPPDATA%\Voice2Txt\runtime\`。設定 `fetchCudaRuntime: false` で取得せず Vulkan か CPU で動く)。どちらも SHA-256 を照合する。インストーラの最後に「CUDA の実行時ライブラリ(NVIDIA)を今取得する」を選ぶと先に取得する(`--prepare-cuda`)。エンジンを Haas(設定 `engine: haas`)にしたとき: speech.cpp(v0.10.1、Windows x64 Vulkan 版の書庫 約 30 MB、GitHub nyosegawa/speech.cpp の release から。`PATH` に `speech.exe` があればそれを使い取得しない。`%APPDATA%\Voice2Txt\tools\speech\`)と Haas-ja-v1.0 の重み(`Haas-616M-ja-v1.0-Q8_0.gguf` 約 0.66 GB、Hugging Face sakasegawa/Haas-ja-v1.0-GGUF の固定の版から。`%APPDATA%\Voice2Txt\models\`)。Haas では CUDA の実行時ライブラリを取得しない。取得元・版・ハッシュは `src/Voice2Txt.Core/SpeechEngine.cs` と `ModelCatalog.cs` / `CudaRuntime.cs`
- アンインストールはアプリのフォルダとログオン時の自動起動の登録を消す。設定・モデル・CUDA の実行時ライブラリ(`%APPDATA%` / `%LOCALAPPDATA%` の `Voice2Txt`)は残る

## 開発
このリポジトリは自律開発ループが育てる。