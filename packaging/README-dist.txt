Voice2Txt — プッシュトゥトークの音声入力(Windows)

使い方
  トークキー(初期値 右 Ctrl)を押している間だけ録音し、離すと文字起こしした文を、押した時点で前面にあったアプリへ貼り付けます。
  音声は PC の外へ送りません。トレイのアイコンを右クリックすると、版・設定・ログオン時の起動を選べます。

portable.zip の場合
  zip をフォルダごと展開してから、Voice2Txt\Voice2Txt.exe を起動してください(zip の中から直接起動しないでください)。

初回起動で取得するもの(配布物には入っていません)
  - Whisper のモデルの重み(初期値 large-v3-turbo、約 1.6 GB)
      取得元 https://huggingface.co/ggerganov/whisper.cpp 。SHA-256 で検証して %APPDATA%\Voice2Txt\models\ に置きます。
  - NVIDIA CUDA の実行時ライブラリ(cudart / cuBLAS。NVIDIA の GPU とドライバがある PC だけ)
      取得元 NVIDIA の公式 redist(https://developer.download.nvidia.com/compute/cuda/redist/)。SHA-256 を照合して
      %LOCALAPPDATA%\Voice2Txt\runtime\ に置きます。NVIDIA の CUDA Toolkit EULA に従う NVIDIA の著作物です。
      取得しない場合は設定の fetchCudaRuntime を false にしてください(CPU で動きます)。
  準備中はトレイとオーバーレイに「モデル準備中」と出ます。ネットに出るのはこの 2 つの取得だけです。

ライセンス
  Voice2Txt は MIT ライセンスです(LICENSE.txt)。同梱する他者の著作物と、そのライセンスは THIRD_PARTY_NOTICES.md にあります。
