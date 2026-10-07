; Inno Setup 6 — Voice2Txt のインストーラ(BLK-human-20261007-2050-3)
;
;   packaging\build.ps1 -Version 1.0 が publish の後に呼ぶ:
;   ISCC /DAppVersion=1.0 /DSourceDir=<publish のフォルダ> /DOutDir=<dist> packaging\installer.iss
;
; 包むのは build.ps1 が検査した publish のフォルダだけ(自作コードと MIT の依存)。NVIDIA の DLL とモデルの重みは入れない
; (初回起動で利用者の PC が取得する)。設定・モデル・CUDA の実行時ライブラリはアプリの外(%APPDATA% / %LOCALAPPDATA% の Voice2Txt)に
; あるので、更新しても消えず、アンインストールでも残す。
#define AppName "Voice2Txt"
#ifndef AppVersion
  #define AppVersion "0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\Voice2Txt"
#endif
#ifndef OutDir
  #define OutDir "..\dist"
#endif

[Setup]
; 上書き更新にするため AppId は固定(以後変えない)
AppId=Voice2Txt
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
UninstallDisplayName={#AppName}
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousPrivileges=yes
DisableDirPage=auto
DisableProgramGroupPage=auto
; 常駐中の exe を閉じてから置き換える
CloseApplications=yes
RestartApplications=no
AppPublisher=Voice2Txt
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppName}.exe
LicenseFile={#SourceDir}\LICENSE.txt
OutputDir={#OutDir}
OutputBaseFilename={#AppName}-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 既定は自分だけ(管理者権限を求めない)。全ユーザー向けはコマンドライン /ALLUSERS でだけ選べる
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline

[Languages]
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[InstallDelete]
; 旧版で消えたファイルが残って混ざらないよう、アプリのフォルダの実行時ライブラリを消してから入れる
Type: filesandordirs; Name: "{app}\runtimes"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppName}.exe"
Name: "{group}\はじめにお読みください"; Filename: "{app}\README.txt"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppName}.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Run]
; インストールの最後に CUDA の実行時ライブラリを先に取得できる(NVIDIA の公式 redist から。SHA-256 を照合。NVIDIA の GPU がある PC だけ)
Filename: "{app}\{#AppName}.exe"; Parameters: "--prepare-cuda"; StatusMsg: "CUDA の実行時ライブラリを取得しています…"; Description: "CUDA の実行時ライブラリ(NVIDIA)を今取得する"; Flags: postinstall skipifsilent unchecked runhidden waituntilterminated
Filename: "{app}\{#AppName}.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// アンインストールしたら、ログオン時の自動起動の登録(HKCU の Run の値 Voice2Txt)も外す。設定・モデルのフォルダは残す
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Voice2Txt');
end;
