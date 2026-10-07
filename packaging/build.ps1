# 配布物を作る: dotnet publish(self-contained、win-x64)→ 中身の検査 → portable.zip →(Inno Setup があれば)setup.exe。
#   powershell -File packaging\build.ps1 -Version 1.0 [-OutDir dist] [-RequireInstaller]
# GitHub Actions(.github/workflows/windows-app.yml)と手元の両方で同じものを作る。
# 中身は自作コードと MIT の依存(.NET ランタイム・Whisper.net・whisper.cpp/ggml・NAudio)だけ。NVIDIA の DLL とモデルの重みは入れない
# (初回起動で利用者の PC が取得する。THIRD_PARTY_NOTICES.md)。入っていれば止める。
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [string]$OutDir = 'dist',
  [switch]$RequireInstaller
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$Version = $Version -replace '^v', ''
if ($Version -notmatch '^\d+(\.\d+){1,3}$') { throw "版が読めない ($Version)。例 1.0 / 1.0.1" }
if (-not [System.IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path $root $OutDir }
$app = Join-Path $OutDir 'Voice2Txt'
Remove-Item $app -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $OutDir -Force | Out-Null

# 1. publish。版はタグ(VERSION より優先)
& dotnet publish (Join-Path $root 'src\Voice2Txt') -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o $app
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# 2. 使わない実行時ライブラリを落とす(Whisper.net.Runtime(.Cuda) は linux / arm64 / x86 も持ってくる。読むのは win-x64 と cuda\win-x64 だけ)
$rt = Join-Path $app 'runtimes'
Get-ChildItem $rt -Directory | Where-Object { $_.Name -notin @('win-x64', 'cuda') } | Remove-Item -Recurse -Force
Get-ChildItem (Join-Path $rt 'cuda') -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne 'win-x64' } | Remove-Item -Recurse -Force
Get-ChildItem $app -Filter '*.pdb' -Recurse | Remove-Item -Force

# 3. 文書(ライセンス・他者の著作物の一覧・初回起動で取得するもの)
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $app 'LICENSE.txt')
Copy-Item (Join-Path $root 'THIRD_PARTY_NOTICES.md') (Join-Path $app 'THIRD_PARTY_NOTICES.md')
# zip の中のファイル名は ASCII にする(日本語名は展開ツールによって文字化けする)
Copy-Item (Join-Path $PSScriptRoot 'README-dist.txt') (Join-Path $app 'README.txt')

# 4. 中身の検査: NVIDIA の DLL・Vulkan のローダー(GPU ドライバ側の vulkan-1.dll)・モデルの重み・音声が無いこと、exe の版がタグと同じこと
$bad = Get-ChildItem $app -Recurse -File | Where-Object {
  $_.Name -match '^(cudart|cublas|cublasLt|cudnn|nvrtc|nvJitLink|cufft|curand|cusparse|cusolver|vulkan-)\d*_?\d*\.dll$' -or $_.Name -match '\.(bin|gguf|wav|mp3|m4a|flac)$'
}
if ($bad) { throw ("配布物に入れてはならないファイル: " + (($bad | ForEach-Object { $_.FullName.Substring($app.Length + 1) }) -join ', ')) }
$exe = Join-Path $app 'Voice2Txt.exe'
if (-not (Test-Path $exe)) { throw "Voice2Txt.exe が無い" }
# VersionInfo の文字列は末尾に空白・NUL の詰め物が付くことがあるので両端を落として比べる。exe は 1.0 → 1.0.0 のように 3 桁以上になる
$pv = ((Get-Item $exe).VersionInfo.ProductVersion -replace '[\s\0]+$', '').Trim()
if (($pv -replace '(\.0)+$', '') -ne ($Version -replace '(\.0)+$', '')) { throw "exe の版 '$pv' がタグ '$Version' と食い違う" }
$files = (Get-ChildItem $app -Recurse -File).Count
$mb = [math]::Round(((Get-ChildItem $app -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Output "publish ok: $files files, $mb MB, exe $pv"

# 5. portable.zip(フォルダごと詰める。展開してから起動する)
$zip = Join-Path $OutDir "Voice2Txt-$Version-portable.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
# Windows PowerShell 5.1 の Compress-Archive / ZipFile は区切りを \ で書き、展開ツールによってはフォルダにならないので、/ で 1 件ずつ詰める
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$za = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
  foreach ($f in Get-ChildItem $app -Recurse -File) {
    $name = 'Voice2Txt/' + $f.FullName.Substring($app.Length + 1).Replace('\', '/')
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $f.FullName, $name, [System.IO.Compression.CompressionLevel]::Optimal)
  }
} finally { $za.Dispose() }
Write-Output "zip=$zip"

# 6. setup.exe(Inno Setup 6)
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
  Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
  if ($RequireInstaller) { throw "Inno Setup 6(ISCC.exe)が無い" }
  Write-Output "setup=skipped (Inno Setup 6 が無い)"
  exit 0
}
& $iscc "/DAppVersion=$Version" "/DSourceDir=$app" "/DOutDir=$OutDir" (Join-Path $PSScriptRoot 'installer.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
$setup = Join-Path $OutDir "Voice2Txt-$Version-setup.exe"
$spv = ((Get-Item $setup).VersionInfo.ProductVersion -replace '[\s\0]+$', '').Trim()
if ($spv -ne $Version) { throw "setup.exe の版 '$spv' がタグ '$Version' と食い違う" }
Write-Output "setup=$setup"
