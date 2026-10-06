# ADR-draft: 基盤の実装で決めた 2 点 — CUDA が読めないときの CPU 退避と、検証モードの差し替え範囲

- **ステータス**: 提案(未採番)
- **カテゴリ**: architecture
- **日付**: 2026-10-06
- **対象プロジェクト**: Voice2Txt
- **関連ADR**: `whisper-cpp-single-engine.md`、`dotnet-host-whisper-net.md`(ドラフト)

## コンテキスト

仕様 5 章は「バックエンドは CUDA 固定」。Whisper.net 1.9.1 の CUDA ランタイム(`ggml-cuda-whisper.dll`)は `cublas64_13.dll` を要し、
このPCには CUDA 13 の cuBLAS が入っていない(ドライバの `nvcuda.dll` だけがある)。このままでは CUDA を読めず、エンジンが起動しない。

検証モード(仕様 6 章)は、GUI へのクリック・キー入力が届かない環境で、発話の流れを最初から最後まで自動で確かめる手段。

## 検討した選択肢

1. CUDA だけを読む(読めなければ起動しない)— 仕様どおりだが、cuBLAS が無い機械では何もできない
2. CUDA → CPU の順に読む(Whisper.net の `RuntimeLibraryOrder`)。読めたものを結果とログに残す
3. cuBLAS の再頒布物を同梱する — 数百 MB 増える。取得元とハッシュの管理が増える

検証モードの差し替え範囲:
A. マイク・トークキーだけ差し替え、クリップボードと Ctrl+V は本物
B. マイク・トークキー・前面ウィンドウ・クリップボード・貼り付けを差し替え、オーバーレイとエンジンは本物

## 決定

- 2 を採る。順序は CUDA → CPU。どちらが読めたかを `result.json` の `runtime` とログ `engine-ready runtime=` に残す。
  CUDA で動かすこと(cuBLAS 13 の用意)は別の BLK で扱う。Vulkan と自動選択は後続ループ(仕様 7 章)
- B を採る。検証モードは本物のクリップボードとキー入力を一切触らない(人間の作業を壊さない・並列に走らせられる)。
  前面ウィンドウは台本の `focus` で切り替える仮想のもの、貼り付けは検証用のテキスト欄のカーソル位置への挿入。
  オーバーレイは本物を出し、画面から CAPTUREBLT 付きで写す(写せなければ同じ描画で代え、`capture: render` と記録する)

### 追記(CUDA の DLL の用意)

CUDA で動かすために 3 を採る。ただし実行時に取得すると「ネットに出るのはモデル取得だけ」に反するので、**ビルド時**に NVIDIA の公式 redist
(`developer.download.nvidia.com/compute/cuda/redist`、`redistrib_13.0.2.json` の版と SHA-256)から `cudart64_13.dll`(Whisper.net が GPU の有無を確かめるのに使う)・
`cublas64_13.dll`・`cublasLt64_13.dll` を取得し、ハッシュを確かめて exe の横に置く(`src/Voice2Txt/CudaRedist.targets`)。配布物は約 530 MB 増える。
人間に CUDA Toolkit を入れてもらう案は、実機受け入れ以外の機械(Intel ノート等)でも同じ手間が要るので採らない。
最初の文字起こしは GPU の初期化で数秒〜十秒かかるので、モデル準備中のうちに無音 1 秒で暖機する。

## 結果

- CPU では 3 秒の 1 文に約 16 秒かかる(junior-3 の実測)。体験の要件(離してから 1〜2 秒)は CUDA でなければ満たせない
- CUDA(RTX 2080 Ti)では暖機後 約 0.3 秒(junior-3 の実測)。junior-3 は `runtime: Cuda` と上限 3000 ms を期待する
- トークキーのフック・本物のクリップボード・Ctrl+V 送出は検証モードで確かめられない。実機受け入れで確かめる
