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
AppId={{4440FC88-98CA-403E-8E20-3DFEBEF0E609}
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
UninstallDisplayName={#ProductName}
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
