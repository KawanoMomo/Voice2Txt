# THIRD_PARTY_NOTICES

Voice2Txt(MIT、`LICENSE`)が同梱・参照する他者の著作物と、そのライセンス。
リポジトリには他者のバイナリ(.dll / .exe)・モデルの重み(.bin)・音声(.wav)を入れない。NuGet の依存はビルド時に nuget.org から取得する。

## 配布物に入るもの(アプリが実行時に使う)

| 名前 | 版 | ライセンス | 著作権者 | 取得元 |
|---|---|---|---|---|
| Whisper.net | 1.9.1 | MIT | Copyright (c) 2024 sandrohanea | https://github.com/sandrohanea/whisper.net |
| Whisper.net.Runtime | 1.9.1 | MIT | Copyright (c) 2024 sandrohanea | 同上(whisper.cpp のネイティブ実行時ライブラリを含む) |
| Whisper.net.Runtime.Cuda | 1.9.1 | MIT | Copyright (c) 2024 sandrohanea | 同上(whisper.cpp の CUDA 版のネイティブ実行時ライブラリを含む) |
| Whisper.net.Runtime.Vulkan | 1.9.1 | MIT | Copyright (c) 2024 sandrohanea | 同上(whisper.cpp の Vulkan 版のネイティブ実行時ライブラリ `ggml-vulkan-whisper.dll` ほかを含む) |
| whisper.cpp / ggml(Whisper.net.Runtime(.Cuda / .Vulkan) に含まれる) | Whisper.net 1.9.1 が同梱する版 | MIT | Copyright (c) 2023-2024 The ggml authors | https://github.com/ggml-org/whisper.cpp |
| NAudio.WinMM / NAudio.Core | 3.1.0 | MIT | Copyright (c) Mark Heath | https://github.com/naudio/NAudio |
| .NET ランタイム / Windows Forms(配布物は self-contained で同梱) | 10.0 | MIT | Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/runtime / https://github.com/dotnet/winforms |

各ライセンスの全文は下の「MIT License」(著作権者の行をそれぞれの名前に読み替える)。

## 実行時に利用者の PC が取得するもの(配布物にもリポジトリにも含めない)

- **Whisper のモデルの重み**(`ggml-*.bin`、初期値 `large-v3-turbo`)— OpenAI Whisper の重み(MIT、Copyright (c) 2022 OpenAI)を
  whisper.cpp の形式に変換したもの。初回起動時にアプリが固定の取得元(https://huggingface.co/ggerganov/whisper.cpp)からダウンロードし、
  SHA-256 で検証してから `%APPDATA%\Voice2Txt\models\` に置く。
- **NVIDIA CUDA の実行時ライブラリ**(`cudart64_13.dll` / `cublas64_13.dll` / `cublasLt64_13.dll`)— NVIDIA の CUDA Toolkit EULA
  (https://docs.nvidia.com/cuda/eula/)に従う NVIDIA の著作物。配布物には含めない。
  利用者の PC が初回起動時(または `Voice2Txt.exe --prepare-cuda`)に NVIDIA の公式 redist から取得し、SHA-256 を照合してから
  `%LOCALAPPDATA%\Voice2Txt\runtime\` に置く(設定 `fetchCudaRuntime` で止められる。止めると CPU で動く)。取得元・版・ハッシュ(`src/Voice2Txt.Core/CudaRuntime.cs` に固定):

  | 取り出す DLL | 取得元の zip | SHA-256(zip) |
  |---|---|---|
  | `cudart64_13.dll` | https://developer.download.nvidia.com/compute/cuda/redist/cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-13.0.96-archive.zip(CUDA Runtime 13.0.96) | `a2ed875f9997aa24904fb70cc9db3acd9308433cde99bc8e63ec1271c9da31b4` |
  | `cublasLt64_13.dll` / `cublas64_13.dll` | https://developer.download.nvidia.com/compute/cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-13.1.0.3-archive.zip(cuBLAS 13.1.0.3) | `4ac4847bbe4f7709b244956fcfc32197a2954ee70b155cb67eebd9ee26f7e339` |

## 利用者の PC に既にあるものを使う(配布物にもリポジトリにも含めず、取得もしない)

- **Vulkan のローダー**(`vulkan-1.dll`)と GPU のドライバ — Vulkan のバックエンドは、利用者の PC の GPU ドライバが入れたものを読む。
  無ければ Vulkan を使わず CPU で動く(設定 `backend`)。

## 開発・テストだけで使うもの(配布物に入らない)

| 名前 | 版 | ライセンス | 著作権者 | 取得元 |
|---|---|---|---|---|
| System.Speech | 10.0.0 | MIT | © Microsoft Corporation | https://www.nuget.org/packages/System.Speech(`tools/MakeFixtures` がテスト用の合成音声を作る) |
| xunit | 2.9.3 | Apache-2.0 | Copyright (C) .NET Foundation | https://github.com/xunit/xunit(全文 https://www.apache.org/licenses/LICENSE-2.0) |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 | Copyright (C) .NET Foundation | 同上 |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT | © Microsoft Corporation | https://github.com/microsoft/vstest |
| coverlet.collector | 6.0.4 | MIT | Copyright (c) 2018 Toni Solarin-Sodara | https://github.com/coverlet-coverage/coverlet |

テストが使う音声は、テストの実行時に Windows の音声合成(日本語の声)で `test-results/fixtures/` に作る。本物の人の声・他者の録音は使わない。

## MIT License(全文)

```
MIT License

Copyright (c) <著作権者>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
