; DshWindowsLauncher.UnsignedRelease.iss
;
; 轻量内网分发安装器：不包含 Code 段的安全加固（路径验证、重解析点拒绝、
; 目录句柄锁定、就地升级身份校验等），也不跑真实安装/卸载验证。
; 本项目已不签名任何自有产物，因此“不签名”不再是这一份与正式份的区别；
; 区别在于加固。正式入口 eng/package.ps1 使用 DshWindowsLauncher.iss（同样不签名，
; 但保留全部 Code 段加固与真实安装验证）。两者 AppId 不同，不得在同一用户环境共存。

#ifndef SourceRoot
  #error SourceRoot is required
#endif
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef FileVersion
  #error FileVersion is required
#endif
#ifndef Publisher
  #error Publisher is required
#endif
#ifndef ReleaseUri
  #error ReleaseUri is required
#endif
#ifndef OutputDirectory
  #error OutputDirectory is required
#endif
#ifndef OutputBaseFilename
  #error OutputBaseFilename is required
#endif

#define ProductName "DSH Windows Launcher"
#define ExecutableName "DshWindowsLauncher.exe"
#define RuntimeBootstrapperName "MicrosoftEdgeWebview2Setup.exe"

[Setup]
AppId={{A7E3F2B1-9C4D-4E8A-B5F6-1D2E3F4A5B6C}
AppName={#ProductName}
AppVersion={#AppVersion}
AppVerName={#ProductName} {#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL={#ReleaseUri}
AppSupportURL={#ReleaseUri}
AppUpdatesURL={#ReleaseUri}
AppCopyright=Copyright (C) {#Publisher}
AppComments=Official release; Authenticode status: NotSigned
DefaultDirName={localappdata}\Programs\DshWindowsLauncher
DefaultGroupName={#ProductName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
AllowUNCPath=no
AllowNetworkDrive=no
AllowRootDirectory=no
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
OutputDir={#OutputDirectory}
OutputBaseFilename={#OutputBaseFilename}
VersionInfoVersion={#FileVersion}
VersionInfoProductVersion={#FileVersion}
VersionInfoProductName={#ProductName}
VersionInfoCompany={#Publisher}
VersionInfoDescription={#ProductName} 按用户安装程序（未签名）
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
RestartIfNeededByRun=no
SetupLogging=yes
SignedUninstaller=no
UninstallDisplayName={#ProductName} (Unsigned)
UninstallDisplayIcon={app}\{#ExecutableName}
WizardStyle=modern
ShowLanguageDialog=no
ChangesAssociations=no
ChangesEnvironment=no
CreateAppDir=yes
CreateUninstallRegKey=yes
DisableReadyMemo=no
DisableReadyPage=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "{#SourceRoot}\{#RuntimeBootstrapperName}"; DestDir: "{app}"; DestName: "{#RuntimeBootstrapperName}"; Flags: ignoreversion
Source: "{#SourceRoot}\*"; DestDir: "{app}"; Excludes: "{#RuntimeBootstrapperName}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#ProductName}"; Filename: "{app}\{#ExecutableName}"; WorkingDir: "{app}"

[Run]
Filename: "{app}\{#ExecutableName}"; Description: "立即启动 {#ProductName}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
