# ADR-draft: 2 つ目のエンジン Haas-ja-v1.0 を speech.cpp の worker(別プロセス、JSON Lines)で動かす

- **ステータス**: 提案(未採番)
- **カテゴリ**: technology
- **日付**: 2026-11-02
- **対象プロジェクト**: Voice2Txt
- **関連**: whisper-cpp-single-engine.md(エンジンを whisper.cpp に一本化)から外れる。BLK-human-20261102-1015-1

## コンテキスト

人間の要望で、日本語 ASR の Haas-ja-v1.0(Parakeet-TDT 系 616M、Apache-2.0)を設定 `engine: haas` で選べるようにする。
公式の実装は Linux + NVIDIA + NeMo(Python)で Windows では動かない。speech.cpp(MIT、ggml、Vulkan / CPU)が GGUF に変換した重みで動かせ、
Windows x64 Vulkan の release(exe + dll)と、プログラムから使う `speech worker`(JSON Lines、protocol 4)と C API(`speech.h`)を持つ。

## 検討した選択肢

### A) `speech worker` を常駐の子プロセスにして JSON Lines で往復する

- メリット: 公開された安定した境界(protocol は呼び出し側が変える必要があるときだけ上がる)。ネイティブの例外・GPU ドライバの不具合が
  アプリのプロセスを落とさない。exe を PATH から差し替えられる。ready に device が出るので、どこで動いているかを示せる
- デメリット: PCM を base64 で送る分の手間(16 kHz × 30 秒で約 1.3 MB、数 ms)。子プロセスの後始末が要る(stdin を閉じれば worker は自分で終わる)

### B) `speech.dll` の C API を P/Invoke する

- メリット: プロセス間の往復が無い
- デメリット: C API の構造体・関数の宣言を自前で保ち、版が上がるたびに合わせる。ネイティブの不具合がアプリごと落とす。whisper.cpp(Whisper.net)と
  ggml の版違いの 2 つ目を同じプロセスに読み込むことになり、シンボルの衝突の再発経路を作る

## 決定

A を採る。エンジンの境界は既存の `ITranscriber`(+ 動いている先 `Runtime` を持つ `IEngine`)のまま、Haas は `SpeechWorkerTranscriber` が満たす。
途中経過も同じ worker で押下中の写しを文字起こしし直す。要求は 1 つずつ(SemaphoreSlim)。

- 取得: speech.cpp は PATH → `%APPDATA%\Voice2Txt\tools\speech\`(release v0.10.1 の書庫を SHA-256 で照合して展開)。重みは Whisper のモデルと同じ
  `ModelProvisioner`(取得元は Hugging Face のコミットで固定、SHA-256 照合)
- 用意できないときは Whisper に黙って落とさない(利用者が選んだエンジンと違う文字起こしが届くのは、何が起きたか分からない失敗になる)。
  理由をトレイの通知・ツールチップ・ログ `engine-error` に出す
- `backend` は speech.cpp の `--device` に読み替える(speech.cpp に CUDA は無いので cuda / vulkan は GPU)。CUDA の実行時ライブラリは取得しない

## 結果

- whisper.cpp 専用の処理(語の時刻による拾い直し、初期プロンプト)は Haas には掛からない。言い淀みの除去・無音の取り消し・届け方は共通
- 30 秒を超える発話は区切らずに渡すと文が抜ける(配布元の注意)。VAD で区切るのは後続(BLK-human-20261102-1015-3)
- 子プロセスの起動には外から取得した exe の実行が要る。実機での速度・誤り率は検証モードの `--engine haas` で同じ台本を回して比べる
