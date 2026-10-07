# ADR-draft: 配布物は self-contained の publish を Inno Setup と zip で包み、タグ push の Actions で作る

- **ステータス**: 提案(未採番)
- **カテゴリ**: architecture
- **日付**: 2026-10-07
- **対象プロジェクト**: Voice2Txt
- **関連**: BLK-human-20261007-2050-3、BLK-human-20261007-2050-2(CUDA の実行時ライブラリの後入れ)、`cuda-runtime-fetched-at-first-run.md`

## コンテキスト

配布物には自作コードと MIT の依存だけを入れ、NVIDIA の DLL とモデルの重みは初回起動で利用者の PC が取得する(人間の決定)。
利用者は .NET を入れていないことがある。Whisper.net.Runtime(.Cuda) は linux / arm64 / x86 の実行時ライブラリも持ってくる(publish で約 440 MB)。

## 検討した選択肢

1. framework-dependent(.NET を利用者が入れる)— 配布物は小さいが、入れていない PC で起動しない
2. **self-contained(win-x64)を publish し、読まない実行時ライブラリ(linux / arm64 / x86)を落としてから Inno Setup の setup.exe と zip に包む** — .NET を入れずに動く。.NET ランタイムも MIT
3. MSIX / WiX — 署名や追加の道具が要り、上書き更新と自分だけのインストールの扱いが Inno Setup より重い

## 決定

2 を採る。作り方と中身の検査(NVIDIA の DLL・`.bin` があれば止める、exe と setup.exe の版がタグと同じ)は `packaging/build.ps1` に置き、
GitHub Actions(`.github/workflows/windows-app.yml`、タグ `v*` の push と手動実行)も手元も同じスクリプトを呼ぶ。setup.exe は `AppId=Voice2Txt` 固定で上書き更新、既定は自分だけ(管理者権限なし)。
zip の中のファイル名は ASCII、区切りは `/`(Windows PowerShell 5.1 の Compress-Archive の `\` を避ける)。

## 結果

setup.exe 約 170 MB、zip 約 190 MB(大半は whisper.cpp の CUDA 版 `ggml-cuda-whisper.dll`)。unit(`PackagingTests`)が、build.ps1 の検査が `CudaRuntimeCatalog` の DLL とモデルの重みを全部弾くことを守る。
