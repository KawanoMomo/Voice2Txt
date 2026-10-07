# ADR-draft: NVIDIA の CUDA 実行時ライブラリは同梱せず、初回起動時に利用者の PC が取得する

- **ステータス**: 提案(未採番)。`foundation-backend-fallback-and-verify-boundaries.md` の「ビルド時に取得して exe の横に置く」を置き換える
- **カテゴリ**: architecture
- **日付**: 2026-10-07
- **対象プロジェクト**: Voice2Txt
- **関連**: BLK-human-20261007-2050-2(人間の決定: 他社の著作物を配布物に同梱しない)、THIRD_PARTY_NOTICES.md

## コンテキスト

`CudaRedist.targets` がビルド時に NVIDIA の redist(cudart / cuBLAS)を取得して exe の横に置いていたため、ビルド成果物と将来の配布物に NVIDIA の DLL が入る。
人間は配布物に他社の著作物を同梱しないと決めた。Whisper.net.Runtime.Cuda の `ggml-cuda-whisper.dll`(MIT)はこれらの DLL を名前で要する。

## 検討した選択肢

1. ビルド時取得を続け、配布物を作るときだけ除く — 開発機と配布物で動きが違い、配布物では常に CPU になる
2. 利用者に CUDA Toolkit を入れてもらう — 手順が増え、版が合わないと黙って CPU に落ちる
3. **初回起動時にアプリが固定の取得元から取得し、SHA-256 を照合して `%LOCALAPPDATA%\Voice2Txt\runtime\` に置き、Whisper.net が CUDA 版を読む前に `NativeLibrary.Load` で読み込んでおく**(同じ名前の DLL は読み込み済みのものが使われる)

## 決定

3 を採る。取得元・版・ハッシュは `CudaRuntimeCatalog` に固定する(モデルと同じ形)。NVIDIA のドライバ(nvcuda.dll)が無い PC では取得しない。
取得中はモデル準備中と同じ表示(「モデル準備中(CUDA の準備中 …)」)、取得・読み込みに失敗したら通知とトレイに「CUDA 無し(CPU)」を出して CPU で動く。
設定 `fetchCudaRuntime` で止められる。`--prepare-cuda` で先に取得できる(インストーラの最後)。検証モードは取得せず、取得済みの runtime を読む。

## 結果

- `dotnet publish` の出力と `git ls-files` に NVIDIA の DLL が無い
- 取得済みの PC では検証モードの `runtime` が `Cuda`(junior-3)。ネットに出るのはモデルと CUDA の実行時ライブラリの取得だけになる(憲章の「モデル取得だけ」との差は人間の決定による)
- `runtime\download\` に取得元と同じ zip を置けば、ネットに出ずに照合して使う(開発機の用意・オフラインの導入)
