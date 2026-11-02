# ADR-draft: Haas の長い発話は、アプリ内の声の区間の判定で分けて渡す(speech.cpp の silero-vad は使わない)

- **ステータス**: 提案(未採番)
- **カテゴリ**: technology
- **日付**: 2026-11-02
- **対象プロジェクト**: Voice2Txt
- **関連**: haas-engine-speech-cpp-worker.md、silence-cancel-relative-to-noise.md、BLK-human-20261102-1015-3

## コンテキスト

Haas-ja-v1.0(FastConformer)は 1 回の要求に文がいくつも入ると文を丸ごと落とす(speech.cpp の文書: Common Voice ja を間を置いて連ねた数分で
parakeet-ja が 600 文中 266 文を落とした)。要望は「30 秒を超える録音を speech.cpp の silero-vad で発話区間に分けて文字起こしし、つなぐ」。
ところが `speech worker` は検出モデル(silero-vad)を扱わず(`unsupported`)、VAD は `speech asr --vad` / `speech vad`(ファイルを読む CLI)と
`speech serve` と C API にしか無い。

## 検討した選択肢

### A) 発話ごとに `speech vad silero-vad` を別に起動し、区間を受け取ってから worker に区間ごとに渡す

- メリット: 要望どおりの Silero の判定
- デメリット: 録音を一時ファイル(WAVE)に書き出す(声をディスクに残す経路が増える)。長い発話のたびに 2 つ目のプロセスを起動する。
  silero-vad の重みの取得とハッシュ検証が増える。小さい声の区間を丸ごと落とす既知の弱点がある

### B) `speech serve`(HTTP、`chunking_strategy`)に切り替える

- デメリット: ポートを開く常駐サーバになる。worker の境界(ADR haas-engine-speech-cpp-worker)を作り直す

### C) アプリ内の声の区間の判定(無音の取り消し・拾い直しと同じ、録音の大きさに合わせた下限)で区間を作り、worker に区間ごとに渡す

- メリット: 追加のプロセス・取得・一時ファイルが無い。unit で確かめられる。小さい声も録音の大きさに合わせて声とみなす(Silero の弱点を持たない)
- デメリット: 雑音の多い録音では Silero より区切りが粗い(雑音を声とみなす区間が長くなる)。区切りの値は Silero の既定を写しただけで、Haas で実測していない

## 決定

C を採る。区切りの値は `speech asr --vad` の既定(余白 0.3 秒、0.5 秒の無音で区切る、区間は 10 秒まで、0.25 秒未満の音は区間にしない)に合わせ、
10 秒を超える区間は中の 100 ms の窓の RMS が最も小さい所で切る。30 秒以下の録音は今までどおり 1 回で渡す。区間の文字列は日本語ならそのまま、
英数字どうしは空白を挟んでつなぐ。

## 結果

- `RegionTranscriber` が Haas の worker を包む(途中経過も同じ区間で)。ログは `regions n= sec= max=`(本文は書かない)
- 30 秒以下でも文がいくつも入る発話は 1 回で渡すので、文を落とすことは残りうる(要望の範囲外。実測で落ちるなら区切る長さを下げる)
- worker が VAD を扱うようになれば A の代わりにそれを使う
