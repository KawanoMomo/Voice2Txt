# ADR-draft: 常駐アプリ本体は C# / .NET で作り、whisper.cpp は Whisper.net でプロセス内に取り込む

- **ステータス**: 提案(未採番)
- **カテゴリ**: technology
- **日付**: 2026-10-06
- **対象プロジェクト**: Voice2Txt
- **関連ADR**: `whisper-cpp-single-engine.md`(ドラフト)

## コンテキスト

作者の他プロジェクトは Python / JS が中心で、C# の知見は少ない。
一方、本ツールで最も壊れやすいのは Whisper ではなく Windows 固有の部分(グローバルキーフックとトークキーの握りつぶし、修飾キー、クリップボードの退避・復元、管理者権限ウィンドウの判定)である。

## 検討した選択肢

- **A) C# / .NET + Whisper.net**: Windows 標準APIを直接書ける。単一 exe 配布。CUDA / Vulkan / OpenVINO / CPU のランタイムパッケージがありエンジン決定と噛み合う。C# の知見が少ない
- **B) Python + whisper-server 子プロセス(または pywhispercpp)**: 知見は最多。ただし低レベルフック内で Python が遅れると Windows にフックを外される既知の罠があり、子プロセスの死活管理と重い配布物を抱える
- **C) Rust + whisper-rs**: 軽量だが学習コスト最大で、ループ開発の初速が出ない

## 決定

A を採る。言語の慣れより、壊れやすい Windows 固有部分を標準APIで直接制御できることを優先した。

## 結果

- 他プロジェクトの Python 実装(NotebookOllama の transcriber 等)はコードとして流用しない
- Whisper.net の各ランタイム(特に OpenVINO で Intel NPU にどこまで載るか)は実機で実測して確定させる。本ドラフトの記述は実測前の知識ベース
