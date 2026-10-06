# ADR-draft: 文字起こしエンジンは whisper.cpp に一本化し、NPU は Intel(OpenVINO)のみ扱う

- **ステータス**: 提案(未採番)
- **カテゴリ**: technology
- **日付**: 2026-10-06
- **対象プロジェクト**: Voice2Txt
- **関連ADR**: 10_NotebookOllama ADR-019(LLM バックエンドの Vulkan 一本化)

## コンテキスト

Voice2Txt の中核はプッシュトゥトーク音声入力で、トークキーを離してから貼り付けまでの遅延が体験を決める。
加えて、CUDA 非対応の GPU や NPU でも動かしたいという要求がある。

手元の知見は faster-whisper(NotebookOllama・meeting-transcriber で採用)にあるが、faster-whisper(CTranslate2)は実質 CUDA と CPU しか使えない。

## 検討した選択肢

### A) faster-whisper

- メリット: 既存の知見と実装をそのまま流用でき、立ち上がりが速い
- デメリット: AMD・Intel の GPU や NPU では動かない。要求を満たせない

### B) whisper.cpp に一本化(CUDA / Vulkan / CPU、Intel NPU は OpenVINO)

- メリット: 1つのエンジンでバックエンドを切り替えられる。Python と cuDNN に依存しない(過去の cuDNN バージョン衝突の再発経路が無い)
- デメリット: 手元に知見が無く、遅延チューニングを一から行う。OpenVINO で NPU に載るのはエンコーダ部分のみ(未実測)

### C) バックエンド別に複数エンジンを併用(CUDA は faster-whisper、それ以外は OpenVINO / ONNX Runtime 等)

- デメリット: ベンダー別の経路ごとに依存・検証が増える。NotebookOllama が ipex-llm / DirectML を削除して Vulkan に一本化した(ADR-019)のと同じ理由で保守が破綻する

## 決定

B を採る。エンジンは whisper.cpp に一本化し、バックエンドは自動選択(CUDA → Vulkan → CPU)+ユーザー設定で上書き可能とする。
NPU は Intel Core Ultra(OpenVINO)のみを後続のループで追加し、AMD Ryzen AI・Qualcomm の NPU は対象外とする。

正式な対象機種は「RTX 2080 Ti 搭載のこのPC」と「Intel Core Ultra ノート」の2機種。それ以外は Vulkan / CPU で動けば良しとするベストエフォート扱い(手元に実機が無いバックエンドは検証できないため)。

## 結果

- エンジンの境界(音声を渡すとテキストが返る)は基盤で切り、将来のエンジン差し替えに備える
- faster-whisper 系の既存実装(NotebookOllama transcriber 等)はコードとしては流用しない。日本語の精度・モデル選定の知見のみ参照する
- 各バックエンドの対応状況(特に OpenVINO での NPU 利用範囲)は、実機での実測で確定させる。本ドラフトの記述は実測前の知識ベース
