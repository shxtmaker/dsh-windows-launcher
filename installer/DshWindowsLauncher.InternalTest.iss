#ifndef SourceRoot
  #error SourceRoot is required
#endif
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef FileVersion
  #error FileVersion is required
#endif
#ifndef OutputDirectory
  #error OutputDirectory is required
#endif
#ifndef OutputBaseFilename
  #error OutputBaseFilename is required
#endif

#define ProductName "DSH Windows Launcher (INTERNAL TEST)"
#define ExecutableName "DshWindowsLauncher.InternalTest.exe"
#define RuntimeBootstrapperName "MicrosoftEdgeWebview2Setup.exe"

[Setup]
AppId={{F3418DD7-58B7-4E0D-B0F7-D77C52FDF91C}
AppName={#ProductName}
AppVersion={#AppVersion}
AppVerName={#ProductName} {#AppVersion}
AppPublisher=INTERNAL TEST - UNSIGNED
AppCopyright=UNSIGNED INTERNAL TEST - NOT FOR PRODUCTION USE
AppComments=UNSIGNED INTERNAL TEST - NOT FOR PRODUCTION USE
DefaultDirName={localappdata}\Programs\DshWindowsLauncher.InternalTest
DefaultGroupName={#ProductName}
DisableProgramGroupPage=yes
DisableDirPage=yes
UsePreviousAppDir=no
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
VersionInfoCompany=INTERNAL TEST - UNSIGNED
VersionInfoDescription=UNSIGNED INTERNAL TEST - NOT FOR PRODUCTION USE
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
RestartIfNeededByRun=no
SetupLogging=yes
SignedUninstaller=no
UninstallDisplayName={#ProductName} (UNSIGNED)
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
