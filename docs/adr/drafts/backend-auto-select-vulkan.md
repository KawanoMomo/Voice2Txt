# ADR-draft: バックエンドに Vulkan を足し、設定 backend の順(自動は CUDA → Vulkan → CPU)で選ぶ

- **ステータス**: 提案(未採番)。`foundation-backend-fallback-and-verify-boundaries.md` の「CUDA → CPU」を置き換える
- **カテゴリ**: architecture
- **日付**: 2026-10-08
- **対象プロジェクト**: Voice2Txt
- **関連**: BLK-human-20261008-0530-1(人間の決定: 仕様 7 章「バックエンドの自動選択」を前倒し)、`cuda-runtime-fetched-at-first-run.md`

## コンテキスト

NVIDIA の GPU が無い PC は CPU に落ち、1 文に約 16 秒かかる。Whisper.net 1.9.1 には whisper.cpp の Vulkan 版(Whisper.net.Runtime.Vulkan、MIT)があり、
`RuntimeOptions.RuntimeLibraryOrder` の順に読めるものを読む(プロセスで最初に読めたものを使い続ける)。Vulkan のローダー `vulkan-1.dll` は GPU ドライバが入れる。

## 検討した選択肢

1. 自動だけ(CUDA → Vulkan → CPU)。上書きの設定は持たない — Vulkan の不具合の切り分けや、CUDA の取得を避けたい人が選べない
2. **設定 `backend`(auto / cuda / vulkan / cpu)。auto は CUDA → Vulkan → CPU。cuda / vulkan はそれを先に試し、読めなければ CPU**
3. 2 で、選んだものが読めなければ起動しない — 何も入力できなくなる。CPU で動いて通知する方が業務を止めない

## 決定

- 2 を採る。試す順は `Voice2Txt.Core/Backends.cs`(Whisper.net に依存しない Core に置き、unit で守る)。
- CUDA の実行時ライブラリ(後入れ)は、CUDA を試す設定(auto / cuda)のときだけ取得・読み込みする。vulkan / cpu では NVIDIA の DLL に触れない
- CPU で動くときは途中経過を止める(`PushToTalkController.InterimEnabled`。設定 `showInterim` は変えない)。CPU では作り直しが確定版を遅らせるため(仕様 7 章)
- 使っているものをトレイのツールチップ「待機中(モデル …、CUDA|Vulkan|CPU)」に出す(常駐時と検証モードで同じ文。`Backends.IdleTrayStatus`)。
  選んだものと違うもので動いたとき・auto で CPU に落ちたときは通知する
- `vulkan-1.dll` は同梱しない(`packaging/build.ps1` が弾く)。whisper.cpp の Vulkan 版の DLL(MIT)は同梱する

## 結果

- RTX 2080 Ti(このPC)の実測、junior-1 / junior-3 の 1 文: 離してから届くまで CUDA 約 230 ms、Vulkan 約 370 ms、CPU 約 15.7 秒。
  Vulkan は起動時の用意(読み込み + 暖機)に約 13 秒(CUDA は約 1.7 秒)
- 検証モードは台本の `settings.backend` で選べ、結果に `runtime` / `backend` / `interim` を残す。junior-1 は `backend: vulkan` で `runtime: Vulkan` を期待する
- 起動中にバックエンドを切り替えることはできない(ネイティブのライブラリはプロセスに 1 つ)。設定の変更は再起動で効く
- Intel NPU(OpenVINO)は扱わない(仕様 7 章の後続)
