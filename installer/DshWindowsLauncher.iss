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
#ifndef WebView2InstallerPath
  #error WebView2InstallerPath is required
#endif
#ifndef WebView2MinimumVersion
  #error WebView2MinimumVersion is required
#endif
#ifndef RequiredSpaceBytes
  #error RequiredSpaceBytes is required
#endif
#ifndef OutputDirectory
  #error OutputDirectory is required
#endif
#ifndef OutputBaseFilename
  #error OutputBaseFilename is required
#endif
#ifndef MaintenanceHelperSha256
  #error MaintenanceHelperSha256 is required
#endif
#ifndef IdentityHelperSha256
  #error IdentityHelperSha256 is required
#endif
#ifndef InstallOwnershipMarkerSha256
  #error InstallOwnershipMarkerSha256 is required
#endif
#ifndef WebView2HealthHelperSha256
  #error WebView2HealthHelperSha256 is required
#endif
#ifndef WebView2CoreAssemblySha256
  #error WebView2CoreAssemblySha256 is required
#endif
#ifndef WebView2LoaderSha256
  #error WebView2LoaderSha256 is required
#endif

#define ProductName "DSH Windows Launcher"
#define ExecutableName "DshWindowsLauncher.exe"
#define RuntimeInstallerName "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
#define RuntimeBootstrapperName "MicrosoftEdgeWebview2Setup.exe"
#define MaintenanceHelperName "maintenance-ipc.ps1"
#define IdentityHelperName "validate-installation.ps1"
#define WebView2HealthHelperName "webview2-health-probe.ps1"
#define WebView2CoreAssemblyName "Microsoft.Web.WebView2.Core.dll"
#define WebView2LoaderName "WebView2Loader.dll"
#define InstallOwnershipMarkerName ".dsh-windows-launcher-install-owner"
; 应用图标由 eng/make-app-icon.ps1 从 mascot 源图生成；安装向导与卸载条目共用这一份。
#define AppIconFile AddBackslash(SourcePath) + "..\assets\brand\app\DshWindowsLauncher.ico"
#if !FileExists(AppIconFile)
  #error Application icon is missing; run eng/make-app-icon.ps1
#endif

[Setup]
SetupIconFile={#AppIconFile}
AppId={{4440FC88-98CA-403E-8E20-3DFEBEF0E609}
AppName={#ProductName}
AppVersion={#AppVersion}
AppVerName={#ProductName} {#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL={#ReleaseUri}
AppSupportURL={#ReleaseUri}
AppUpdatesURL={#ReleaseUri}
AppCopyright=Copyright (C) {#Publisher}
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
VersionInfoDescription={#ProductName} 按用户安装程序
Compression=lzma2/max
SolidCompression=yes
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
SetupLogging=yes
; 本项目不签名自有产物：卸载器也不走 Inno 的签名回环（SignedUninstaller=no）。
; Code 段里的路径锁定、重解析点拒绝与安装身份校验全部保留。
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
Source: "{#WebView2InstallerPath}"; DestName: "{#RuntimeInstallerName}"; Flags: dontcopy noencryption
Source: "{#SourcePath}\{#MaintenanceHelperName}"; DestName: "{#MaintenanceHelperName}"; Flags: dontcopy noencryption
Source: "{#SourcePath}\{#IdentityHelperName}"; DestName: "{#IdentityHelperName}"; Flags: dontcopy noencryption
Source: "{#SourcePath}\{#WebView2HealthHelperName}"; DestName: "{#WebView2HealthHelperName}"; Flags: dontcopy noencryption
Source: "{#SourceRoot}\{#WebView2CoreAssemblyName}"; DestName: "{#WebView2CoreAssemblyName}"; Flags: dontcopy noencryption
Source: "{#SourceRoot}\{#WebView2LoaderName}"; DestName: "{#WebView2LoaderName}"; Flags: dontcopy noencryption
Source: "{#SourcePath}\{#MaintenanceHelperName}"; DestDir: "{app}\.installer-support"; DestName: "{#MaintenanceHelperName}"; Flags: ignoreversion
Source: "{#SourcePath}\install-owner.txt"; DestDir: "{app}"; DestName: "{#InstallOwnershipMarkerName}"; Flags: ignoreversion
Source: "{#SourceRoot}\{#RuntimeBootstrapperName}"; DestDir: "{app}"; DestName: "{#RuntimeBootstrapperName}"; Flags: ignoreversion
Source: "{#SourceRoot}\*"; DestDir: "{app}"; Excludes: "{#RuntimeBootstrapperName}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#ProductName}"; Filename: "{app}\{#ExecutableName}"; WorkingDir: "{app}"

[Run]
Filename: "{app}\{#ExecutableName}"; Description: "立即启动 {#ProductName}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent; Check: ShouldOfferLaunch

[Code]
const
  UninstallRegistryKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{4440FC88-98CA-403E-8E20-3DFEBEF0E609}_is1';
  WebView2RegistryKey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  OwnershipMarkerName = '.dsh-windows-launcher-owner';
  OwnershipMarkerContent = 'DshWindowsLauncher:v1';
  InstallOwnershipMarkerContent = 'DshWindowsLauncher:install:v1';
  PowerShellRelativePath = 'WindowsPowerShell\v1.0\powershell.exe';
  FileAttributeReparsePoint = $00000400;
  InvalidFileAttributes = $FFFFFFFF;
  DriveFixed = 3;
  FileShareRead = $00000001;
  FileShareWrite = $00000002;
  GenericRead = $80000000;
  FileReadAttributes = $00000080;
  FileAccessDelete = $00010000;
  OpenExisting = 3;
  FileFlagOpenReparsePoint = $00200000;
  FileFlagBackupSemantics = $02000000;
  FileDispositionInfoEx = 21;
  FileDispositionFlagDelete = $00000001;
  FileDispositionFlagIgnoreReadOnlyAttribute = $00000010;
  MaxFinalPathCharacters = 32768;

type
  TDshFileTime = record
    LowDateTime: Cardinal;
    HighDateTime: Cardinal;
  end;

  TDshByHandleFileInformation = record
    FileAttributes: Cardinal;
    CreationTime: TDshFileTime;
    LastAccessTime: TDshFileTime;
    LastWriteTime: TDshFileTime;
    VolumeSerialNumber: Cardinal;
    FileSizeHigh: Cardinal;
    FileSizeLow: Cardinal;
    NumberOfLinks: Cardinal;
    FileIndexHigh: Cardinal;
    FileIndexLow: Cardinal;
  end;

  TDshFileDispositionInfoEx = record
    Flags: Cardinal;
  end;

var
  ExistingInstall: Boolean;
  ExistingInstallPath: String;
  ExistingVersion: String;
  DeleteApplicationData: Boolean;
  RuntimeRestartRequired: Boolean;
  InstallPathHandles: array of THandle;
  MaintenanceHelperHandle: THandle;
  MaintenanceReadyPath: String;
  MaintenanceFailurePath: String;
  MaintenanceStopPath: String;
  MaintenanceReleasedPath: String;
  MaintenanceReservationActive: Boolean;
  MaintenanceHelperPending: Boolean;

function GetDriveTypeW(RootPathName: String): Cardinal;
  external 'GetDriveTypeW@kernel32.dll stdcall';

function GetCurrentProcessId: Cardinal;
  external 'GetCurrentProcessId@kernel32.dll stdcall';

function GetFileAttributesW(FileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function CreateFileW(FileName: String; DesiredAccess: Cardinal;
  ShareMode: Cardinal; SecurityAttributes: NativeInt;
  CreationDisposition: Cardinal; FlagsAndAttributes: Cardinal;
  TemplateFile: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';

function GetFinalPathNameByHandleW(FileHandle: THandle; FilePath: String;
  FilePathCharacters: Cardinal; Flags: Cardinal): Cardinal;
  external 'GetFinalPathNameByHandleW@kernel32.dll stdcall';

function GetFileInformationByHandle(FileHandle: THandle;
  var FileInformation: TDshByHandleFileInformation): Boolean;
  external 'GetFileInformationByHandle@kernel32.dll stdcall';

function SetFileInformationByHandle(FileHandle: THandle;
  FileInformationClass: Integer;
  var FileInformation: TDshFileDispositionInfoEx;
  BufferSize: Cardinal): Boolean;
  external 'SetFileInformationByHandle@kernel32.dll stdcall';

function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function NormalizePath(const Path: String): String;
begin
  Result := RemoveBackslashUnlessRoot(ExpandFileName(Path));
end;

function NormalizeFinalPath(const Path: String): String;
var
  Value: String;
begin
  Value := Path;
  if Pos('\\?\UNC\', Value) = 1 then
    Value := '\\' + Copy(Value, 9, Length(Value))
  else if Pos('\\?\', Value) = 1 then
    Value := Copy(Value, 5, Length(Value));
  Result := NormalizePath(Value);
end;

function IsPathWithinRoot(const Path: String; const Root: String): Boolean;
var
  NormalizedPath: String;
  NormalizedRoot: String;
  RootPrefix: String;
begin
  NormalizedPath := NormalizePath(Path);
  NormalizedRoot := NormalizePath(Root);
  if CompareText(NormalizedPath, NormalizedRoot) = 0 then
  begin
    Result := True;
    Exit;
  end;
  RootPrefix := AddBackslash(NormalizedRoot);
  Result := CompareText(Copy(NormalizedPath, 1, Length(RootPrefix)), RootPrefix) = 0;
end;

procedure ReleaseInstallPathHandles;
var
  Index: Integer;
begin
  for Index := GetArrayLength(InstallPathHandles) - 1 downto 0 do
    CloseHandle(InstallPathHandles[Index]);
  SetArrayLength(InstallPathHandles, 0);
end;

procedure KeepInstallPathHandle(const Handle: THandle);
var
  Count: Integer;
begin
  Count := GetArrayLength(InstallPathHandles);
  SetArrayLength(InstallPathHandles, Count + 1);
  InstallPathHandles[Count] := Handle;
end;

function TryLockDirectory(const Path: String; const ExpectedDriveRoot: String;
  var Reason: String): Boolean;
var
  Attributes: Cardinal;
  DirectoryHandle: THandle;
  FinalPathBuffer: String;
  FinalPathLength: Cardinal;
  FinalPath: String;
  FinalDriveRoot: String;
begin
  Result := False;
  Attributes := GetFileAttributesW(Path);
  if (Attributes = InvalidFileAttributes) or
    ((Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0) then
  begin
    Reason := '安装路径组件不存在或不是目录。';
    Exit;
  end;

  DirectoryHandle := CreateFileW(
    Path,
    0,
    FileShareRead or FileShareWrite,
    0,
    OpenExisting,
    FileFlagBackupSemantics or FileFlagOpenReparsePoint,
    0);
  if DirectoryHandle = THandle(-1) then
  begin
    Reason := '无法锁定安装目录，目录可能正在被替换。';
    Exit;
  end;

  { Re-read attributes only after the no-delete-share handle makes the path
    component stable. The pre-open attributes are not trusted across the open. }
  Attributes := GetFileAttributesW(Path);
  if (Attributes = InvalidFileAttributes) or
    ((Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0) or
    ((Attributes and FileAttributeReparsePoint) <> 0) then
  begin
    CloseHandle(DirectoryHandle);
    Reason := '安装目录或其父目录是重解析点。';
    Exit;
  end;

  FinalPathBuffer := StringOfChar(#0, MaxFinalPathCharacters);
  FinalPathLength := GetFinalPathNameByHandleW(
    DirectoryHandle,
    FinalPathBuffer,
    MaxFinalPathCharacters,
    0);
  if (FinalPathLength = 0) or (FinalPathLength >= MaxFinalPathCharacters) then
  begin
    CloseHandle(DirectoryHandle);
    Reason := '无法通过目录句柄解析最终安装路径。';
    Exit;
  end;

  FinalPath := NormalizeFinalPath(Copy(FinalPathBuffer, 1, FinalPathLength));
  FinalDriveRoot := AddBackslash(ExtractFileDrive(FinalPath));
  if (CompareText(FinalPath, NormalizePath(Path)) <> 0) or
    (CompareText(FinalDriveRoot, ExpectedDriveRoot) <> 0) or
    (GetDriveTypeW(FinalDriveRoot) <> DriveFixed) or
    not IsPathWithinRoot(FinalPath, ExpectedDriveRoot) then
  begin
    CloseHandle(DirectoryHandle);
    Reason := '目录句柄解析后的最终路径越出所选本机固定卷或安装根边界。';
    Exit;
  end;

  KeepInstallPathHandle(DirectoryHandle);
  Result := True;
end;

function LockExistingPathComponents(const Path: String;
  const ExpectedDriveRoot: String; var Reason: String): Boolean;
var
  CurrentPath: String;
  Remainder: String;
  Component: String;
  Separator: Integer;
begin
  Result := False;
  CurrentPath := ExpectedDriveRoot;
  if not TryLockDirectory(CurrentPath, ExpectedDriveRoot, Reason) then
    Exit;

  Remainder := Copy(NormalizePath(Path), Length(ExpectedDriveRoot) + 1, Length(Path));
  while Remainder <> '' do
  begin
    Separator := Pos('\', Remainder);
    if Separator = 0 then
    begin
      Component := Remainder;
      Remainder := '';
    end
    else
    begin
      Component := Copy(Remainder, 1, Separator - 1);
      Delete(Remainder, 1, Separator);
    end;

    if Component <> '' then
    begin
      CurrentPath := AddBackslash(CurrentPath) + Component;
      if not DirExists(CurrentPath) then
        Exit;
      if not TryLockDirectory(CurrentPath, ExpectedDriveRoot, Reason) then
        Exit;
    end;
  end;
  Result := True;
end;

function IsReparsePoint(const Path: String): Boolean;
var
  Attributes: Cardinal;
begin
  Attributes := GetFileAttributesW(Path);
  Result := (Attributes <> InvalidFileAttributes) and
    ((Attributes and FileAttributeReparsePoint) <> 0);
end;

function ExistingPathContainsReparsePoint(const Path: String): Boolean;
var
  CurrentPath: String;
  ParentPath: String;
begin
  Result := False;
  CurrentPath := NormalizePath(Path);
  while CurrentPath <> '' do
  begin
    if FileOrDirExists(CurrentPath) and IsReparsePoint(CurrentPath) then
    begin
      Result := True;
      Exit;
    end;

    ParentPath := RemoveBackslashUnlessRoot(ExtractFileDir(CurrentPath));
    if (ParentPath = '') or (CompareText(ParentPath, CurrentPath) = 0) then
      Exit;
    CurrentPath := ParentPath;
  end;
end;

function IsDirectoryEmpty(const Path: String): Boolean;
var
  FindResult: TFindRec;
begin
  Result := True;
  if not DirExists(Path) then
    Exit;

  if FindFirst(AddBackslash(Path) + '*', FindResult) then
  begin
    try
      repeat
        if (FindResult.Name <> '.') and (FindResult.Name <> '..') then
        begin
          Result := False;
          Exit;
        end;
      until not FindNext(FindResult);
    finally
      FindClose(FindResult);
    end;
  end;
end;

function FindExistingParent(const Path: String): String;
var
  CurrentPath: String;
  ParentPath: String;
begin
  CurrentPath := NormalizePath(Path);
  while not DirExists(CurrentPath) do
  begin
    ParentPath := RemoveBackslashUnlessRoot(ExtractFileDir(CurrentPath));
    if (ParentPath = '') or (CompareText(ParentPath, CurrentPath) = 0) then
    begin
      Result := '';
      Exit;
    end;
    CurrentPath := ParentPath;
  end;
  Result := CurrentPath;
end;

function IsDirectoryWritable(const Path: String): Boolean;
var
  ExistingParent: String;
  ProbeFile: String;
begin
  Result := False;
  ExistingParent := FindExistingParent(Path);
  if ExistingParent = '' then
    Exit;

  ProbeFile := AddBackslash(ExistingParent) +
    '.dshwl-write-test-' + IntToStr(Random(2147483647)) + '.tmp';
  if not SaveStringToFile(ProbeFile, '', False) then
    Exit;

  Result := DeleteFile(ProbeFile);
end;

function ValidateInstallDirectory(const CandidatePath: String; var Reason: String): Boolean;
var
  Path: String;
  DriveRoot: String;
  FreeBytes: Int64;
  TotalBytes: Int64;
begin
  Result := False;
  Reason := '';
  Path := NormalizePath(CandidatePath);

  if (Path = '') or (Pos('\\', Path) = 1) or
    (Pos('\\?\', Path) = 1) or (Pos('\\.\', Path) = 1) then
  begin
    Reason := '安装目录不能是 UNC、设备或扩展设备路径。';
    Exit;
  end;

  DriveRoot := AddBackslash(ExtractFileDrive(Path));
  if (Length(DriveRoot) < 3) or (GetDriveTypeW(DriveRoot) <> DriveFixed) then
  begin
    Reason := '安装目录必须位于本机固定卷，不能使用网络映射或可移动卷。';
    Exit;
  end;

  if ExistingPathContainsReparsePoint(Path) then
  begin
    Reason := '安装目录或其现有父目录包含重解析点。';
    Exit;
  end;

  if ExistingInstall then
  begin
    if CompareText(Path, NormalizePath(ExistingInstallPath)) <> 0 then
    begin
      Reason := '升级或修复必须沿用已登记安装目录。改变目录前请先普通卸载。';
      Exit;
    end;
  end
  else if not IsDirectoryEmpty(Path) then
  begin
    Reason := '首次安装只允许不存在或为空的目录。';
    Exit;
  end;

  if not GetSpaceOnDisk64(DriveRoot, FreeBytes, TotalBytes) then
  begin
    Reason := '无法确认安装卷可用空间。';
    Exit;
  end;

  if FreeBytes < {#RequiredSpaceBytes} then
  begin
    Reason := '安装卷可用空间不足。';
    Exit;
  end;

  Result := True;
end;

function SecureInstallDirectory(const CandidatePath: String; var Reason: String): Boolean;
var
  Path: String;
  DriveRoot: String;
  ExistingParent: String;
begin
  Result := False;
  ReleaseInstallPathHandles;

  if not ValidateInstallDirectory(CandidatePath, Reason) then
    Exit;

  Path := NormalizePath(CandidatePath);
  DriveRoot := AddBackslash(ExtractFileDrive(Path));
  ExistingParent := FindExistingParent(Path);
  if ExistingParent = '' then
  begin
    Reason := '找不到可锁定的现有安装目录父级。';
    Exit;
  end;

  if not LockExistingPathComponents(ExistingParent, DriveRoot, Reason) then
  begin
    ReleaseInstallPathHandles;
    Exit;
  end;

  if not ForceDirectories(Path) then
  begin
    ReleaseInstallPathHandles;
    Reason := '无法创建安装目录。';
    Exit;
  end;

  if not LockExistingPathComponents(Path, DriveRoot, Reason) then
  begin
    ReleaseInstallPathHandles;
    Exit;
  end;

  if ExistingPathContainsReparsePoint(Path) then
  begin
    ReleaseInstallPathHandles;
    Reason := '安装目录或其父目录在最终锁定时包含重解析点。';
    Exit;
  end;

  if (not ExistingInstall) and (not IsDirectoryEmpty(Path)) then
  begin
    ReleaseInstallPathHandles;
    Reason := '首次安装目录在最终锁定后不是空目录。';
    Exit;
  end;

  if not IsDirectoryWritable(Path) then
  begin
    ReleaseInstallPathHandles;
    Reason := '最终锁定的安装目录不可写。';
    Exit;
  end;

  Result := True;
end;

function TakeVersionComponent(var Version: String): String;
var
  Separator: Integer;
begin
  Separator := Pos('.', Version);
  if Separator = 0 then
  begin
    Result := Version;
    Version := '';
  end
  else
  begin
    Result := Copy(Version, 1, Separator - 1);
    Delete(Version, 1, Separator);
  end;
end;

function CompareNumericVersions(const LeftVersion: String; const RightVersion: String): Integer;
var
  LeftRemainder: String;
  RightRemainder: String;
  LeftNumber: Int64;
  RightNumber: Int64;
  Index: Integer;
begin
  LeftRemainder := LeftVersion;
  RightRemainder := RightVersion;
  for Index := 1 to 4 do
  begin
    LeftNumber := StrToInt64Def(TakeVersionComponent(LeftRemainder), 0);
    RightNumber := StrToInt64Def(TakeVersionComponent(RightRemainder), 0);
    if LeftNumber < RightNumber then
    begin
      Result := -1;
      Exit;
    end;
    if LeftNumber > RightNumber then
    begin
      Result := 1;
      Exit;
    end;
  end;
  Result := 0;
end;

function IsNumericIdentifier(const Value: String): Boolean;
var
  Index: Integer;
begin
  Result := Value <> '';
  for Index := 1 to Length(Value) do
  begin
    if (Value[Index] < '0') or (Value[Index] > '9') then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function CompareNumericIdentifiers(const LeftValue: String; const RightValue: String): Integer;
begin
  if Length(LeftValue) < Length(RightValue) then
  begin
    Result := -1;
    Exit;
  end;
  if Length(LeftValue) > Length(RightValue) then
  begin
    Result := 1;
    Exit;
  end;
  if LeftValue < RightValue then
  begin
    Result := -1;
    Exit;
  end;
  if LeftValue > RightValue then
  begin
    Result := 1;
    Exit;
  end;
  Result := 0;
end;

function SplitSemanticVersion(const Version: String; var Core: String; var Prerelease: String): Boolean;
var
  Value: String;
  Separator: Integer;
begin
  Value := Version;
  Separator := Pos('+', Value);
  if Separator > 0 then
    Delete(Value, Separator, Length(Value));

  Separator := Pos('-', Value);
  if Separator > 0 then
  begin
    Core := Copy(Value, 1, Separator - 1);
    Prerelease := Copy(Value, Separator + 1, Length(Value));
  end
  else
  begin
    Core := Value;
    Prerelease := '';
  end;
  Result := Core <> '';
end;

function ComparePrereleaseIdentifiers(const LeftValue: String; const RightValue: String): Integer;
var
  LeftRemainder: String;
  RightRemainder: String;
  LeftIdentifier: String;
  RightIdentifier: String;
  LeftNumeric: Boolean;
  RightNumeric: Boolean;
  NumericComparison: Integer;
begin
  LeftRemainder := LeftValue;
  RightRemainder := RightValue;
  while (LeftRemainder <> '') or (RightRemainder <> '') do
  begin
    if LeftRemainder = '' then
    begin
      Result := -1;
      Exit;
    end;
    if RightRemainder = '' then
    begin
      Result := 1;
      Exit;
    end;

    LeftIdentifier := TakeVersionComponent(LeftRemainder);
    RightIdentifier := TakeVersionComponent(RightRemainder);
    LeftNumeric := IsNumericIdentifier(LeftIdentifier);
    RightNumeric := IsNumericIdentifier(RightIdentifier);

    if LeftNumeric and RightNumeric then
    begin
      NumericComparison := CompareNumericIdentifiers(LeftIdentifier, RightIdentifier);
      if NumericComparison <> 0 then
      begin
        Result := NumericComparison;
        Exit;
      end;
    end
    else if LeftNumeric then
    begin
      Result := -1;
      Exit;
    end
    else if RightNumeric then
    begin
      Result := 1;
      Exit;
    end
    else
    begin
      if LeftIdentifier < RightIdentifier then
      begin
        Result := -1;
        Exit;
      end;
      if LeftIdentifier > RightIdentifier then
      begin
        Result := 1;
        Exit;
      end;
    end;
  end;
  Result := 0;
end;

function CompareSemanticVersions(const LeftVersion: String; const RightVersion: String): Integer;
var
  LeftCore: String;
  RightCore: String;
  LeftPrerelease: String;
  RightPrerelease: String;
begin
  SplitSemanticVersion(LeftVersion, LeftCore, LeftPrerelease);
  SplitSemanticVersion(RightVersion, RightCore, RightPrerelease);
  Result := CompareNumericVersions(LeftCore, RightCore);
  if Result <> 0 then
    Exit;

  if (LeftPrerelease = '') and (RightPrerelease = '') then
  begin
    Result := 0;
    Exit;
  end;
  if LeftPrerelease = '' then
  begin
    Result := 1;
    Exit;
  end;
  if RightPrerelease = '' then
  begin
    Result := -1;
    Exit;
  end;
  Result := ComparePrereleaseIdentifiers(LeftPrerelease, RightPrerelease);
end;

function ReadExistingInstallation: Boolean;
begin
  ExistingInstallPath := '';
  ExistingVersion := '';
  Result := RegQueryStringValue(HKCU, UninstallRegistryKey, 'InstallLocation', ExistingInstallPath);
  if Result then
    RegQueryStringValue(HKCU, UninstallRegistryKey, 'DisplayVersion', ExistingVersion);
end;

function IsSafeRegisteredValue(const Value: String; const MaximumLength: Integer): Boolean;
begin
  Result := (Length(Value) <= MaximumLength) and
    (Pos('"', Value) = 0) and
    (Pos(#10, Value) = 0) and
    (Pos(#13, Value) = 0) and
    (Pos(#0, Value) = 0);
end;

function GetInstalledWebView2Version: String;
var
  MachineVersion: String;
  UserVersion: String;
begin
  MachineVersion := '';
  UserVersion := '';
  RegQueryStringValue(HKLM32, WebView2RegistryKey, 'pv', MachineVersion);
  RegQueryStringValue(HKCU32, WebView2RegistryKey, 'pv', UserVersion);

  if CompareNumericVersions(MachineVersion, UserVersion) >= 0 then
    Result := MachineVersion
  else
    Result := UserVersion;
end;

function TryOpenOwnedEntry(const Path: String; const OwnershipRoot: String;
  const ExpectDirectory: Boolean; const DesiredAccess: Cardinal;
  var EntryHandle: THandle; var Information: TDshByHandleFileInformation;
  var Reason: String): Boolean; forward;

function TryLockTrustedRegularFile(const Path: String; const ExpectedRoot: String;
  const ExpectedSha256: String; var FileHandle: THandle;
  var Reason: String): Boolean;
var
  Information: TDshByHandleFileInformation;
begin
  Result := False;
  if not TryOpenOwnedEntry(
    Path,
    ExpectedRoot,
    False,
    GenericRead or FileReadAttributes,
    FileHandle,
    Information,
    Reason) then
    Exit;

  if Information.NumberOfLinks <> 1 then
  begin
    CloseHandle(FileHandle);
    FileHandle := THandle(-1);
    Reason := '受信任辅助文件不能是硬链接。';
    Exit;
  end;

  if CompareText(GetSHA256OfFile(Path), ExpectedSha256) <> 0 then
  begin
    CloseHandle(FileHandle);
    FileHandle := THandle(-1);
    Reason := '受信任辅助文件哈希不匹配。';
    Exit;
  end;

  Result := True;
end;

procedure RemoveMaintenanceStateFiles;
begin
  if MaintenanceReadyPath <> '' then
    DeleteFile(MaintenanceReadyPath);
  if MaintenanceFailurePath <> '' then
    DeleteFile(MaintenanceFailurePath);
  if MaintenanceStopPath <> '' then
    DeleteFile(MaintenanceStopPath);
  if MaintenanceReleasedPath <> '' then
    DeleteFile(MaintenanceReleasedPath);
end;

function ReleaseMaintenanceIpcReservation: Boolean;
var
  Index: Integer;
begin
  Result := True;
  if MaintenanceReservationActive or MaintenanceHelperPending then
  begin
    SaveStringToFile(MaintenanceStopPath, 'stop', False);
    for Index := 1 to 200 do
    begin
      if FileExists(MaintenanceReleasedPath) or
        FileExists(MaintenanceFailurePath) then
        Break;
      Sleep(50);
    end;
    if not FileExists(MaintenanceReleasedPath) and
      not FileExists(MaintenanceFailurePath) then
    begin
      Result := False;
      Exit;
    end;
  end;

  if MaintenanceHelperHandle <> THandle(-1) then
  begin
    CloseHandle(MaintenanceHelperHandle);
    MaintenanceHelperHandle := THandle(-1);
  end;
  MaintenanceReservationActive := False;
  MaintenanceHelperPending := False;
  RemoveMaintenanceStateFiles;
end;

procedure BestEffortReleaseMaintenanceIpcReservation;
begin
  if not ReleaseMaintenanceIpcReservation then
    Log('当前用户 IPC 维护占用未能在进程退出前确认释放。');
end;

function RunMaintenanceIpcHelper(const ForUninstall: Boolean;
  var Reason: String): Boolean;
var
  HelperPath: String;
  HelperRoot: String;
  StatePrefix: String;
  Parameters: String;
  ResultCode: Integer;
  Index: Integer;
begin
  Result := False;
  if not ReleaseMaintenanceIpcReservation then
  begin
    Reason := '上一维护辅助程序未能确认释放当前用户 IPC。';
    Exit;
  end;
  if ForUninstall then
  begin
    HelperRoot := NormalizePath(ExpandConstant('{app}'));
    HelperPath := AddBackslash(HelperRoot) +
      '.installer-support\{#MaintenanceHelperName}';
  end
  else
  begin
    ExtractTemporaryFile('{#MaintenanceHelperName}');
    HelperRoot := NormalizePath(ExpandConstant('{tmp}'));
    HelperPath := AddBackslash(HelperRoot) + '{#MaintenanceHelperName}';
  end;

  if not TryLockTrustedRegularFile(
    HelperPath,
    HelperRoot,
    '{#MaintenanceHelperSha256}',
    MaintenanceHelperHandle,
    Reason) then
    Exit;

  StatePrefix := AddBackslash(ExpandConstant('{tmp}')) +
    'DshWindowsLauncher-maintenance-' +
    IntToStr(GetCurrentProcessId) + '-' +
    IntToStr(Random(2147483647));
  MaintenanceReadyPath := StatePrefix + '.ready';
  MaintenanceFailurePath := StatePrefix + '.failed';
  MaintenanceStopPath := StatePrefix + '.stop';
  MaintenanceReleasedPath := StatePrefix + '.released';
  RemoveMaintenanceStateFiles;

  Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' +
    AddQuotes(HelperPath) +
    ' -TimeoutSeconds 30' +
    ' -ParentProcessId ' + IntToStr(GetCurrentProcessId) +
    ' -ReadyPath ' + AddQuotes(MaintenanceReadyPath) +
    ' -FailurePath ' + AddQuotes(MaintenanceFailurePath) +
    ' -StopPath ' + AddQuotes(MaintenanceStopPath) +
    ' -ReleasedPath ' + AddQuotes(MaintenanceReleasedPath);
  if not Exec(
    ExpandConstant('{sys}\' + PowerShellRelativePath),
    Parameters,
    HelperRoot,
    SW_HIDE,
    ewNoWait,
    ResultCode) then
  begin
    Reason := '无法启动当前用户 IPC 维护辅助程序。';
    BestEffortReleaseMaintenanceIpcReservation;
    Exit;
  end;
  MaintenanceHelperPending := True;

  for Index := 1 to 700 do
  begin
    if FileExists(MaintenanceFailurePath) then
    begin
      MaintenanceHelperPending := False;
      Reason := '当前用户应用实例未能在 30 秒内正常退出。';
      BestEffortReleaseMaintenanceIpcReservation;
      Exit;
    end;
    if FileExists(MaintenanceReadyPath) then
    begin
      CloseHandle(MaintenanceHelperHandle);
      MaintenanceHelperHandle := THandle(-1);
      MaintenanceHelperPending := False;
      MaintenanceReservationActive := True;
      Result := True;
      Exit;
    end;
    Sleep(50);
  end;

  Reason := '当前用户 IPC 维护辅助程序未在限定时间内就绪。';
  BestEffortReleaseMaintenanceIpcReservation;
end;

function ValidateRegisteredInstallation(var Reason: String): Boolean;
var
  Path: String;
  DriveRoot: String;
  MarkerPath: String;
  ApplicationPath: String;
  UninstallerPath: String;
  IdentityHelperPath: String;
  IdentityHelperRoot: String;
  MarkerValue: AnsiString;
  MarkerHandle: THandle;
  ApplicationHandle: THandle;
  UninstallerHandle: THandle;
  IdentityHelperHandle: THandle;
  MarkerInformation: TDshByHandleFileInformation;
  ApplicationInformation: TDshByHandleFileInformation;
  UninstallerInformation: TDshByHandleFileInformation;
  Parameters: String;
  ResultCode: Integer;
begin
  Result := False;
  MarkerHandle := THandle(-1);
  ApplicationHandle := THandle(-1);
  UninstallerHandle := THandle(-1);
  IdentityHelperHandle := THandle(-1);
  ReleaseInstallPathHandles;

  Path := NormalizePath(ExistingInstallPath);
  DriveRoot := AddBackslash(ExtractFileDrive(Path));
  if (Path = '') or (GetDriveTypeW(DriveRoot) <> DriveFixed) or
    not LockExistingPathComponents(Path, DriveRoot, Reason) then
  begin
    Reason := '已登记安装目录不是可验证的本机固定卷目录。';
    Exit;
  end;

  try
    MarkerPath := AddBackslash(Path) + '{#InstallOwnershipMarkerName}';
    if not TryOpenOwnedEntry(
      MarkerPath,
      Path,
      False,
      GenericRead or FileReadAttributes,
      MarkerHandle,
      MarkerInformation,
      Reason) then
      Exit;
    if (MarkerInformation.NumberOfLinks <> 1) or
      (CompareText(GetSHA256OfFile(MarkerPath),
        '{#InstallOwnershipMarkerSha256}') <> 0) or
      (not LoadStringFromFile(MarkerPath, MarkerValue)) or
      (Trim(String(MarkerValue)) <> InstallOwnershipMarkerContent) then
    begin
      Reason := '已登记安装目录缺少有效且唯一的产品所有权标记。';
      Exit;
    end;

    ApplicationPath := AddBackslash(Path) + '{#ExecutableName}';
    if not TryOpenOwnedEntry(
      ApplicationPath,
      Path,
      False,
      GenericRead or FileReadAttributes,
      ApplicationHandle,
      ApplicationInformation,
      Reason) then
      Exit;
    if ApplicationInformation.NumberOfLinks <> 1 then
    begin
      Reason := '已登记主程序不能是硬链接。';
      Exit;
    end;

    UninstallerPath := AddBackslash(Path) + 'unins000.exe';
    if not TryOpenOwnedEntry(
      UninstallerPath,
      Path,
      False,
      GenericRead or FileReadAttributes,
      UninstallerHandle,
      UninstallerInformation,
      Reason) then
      Exit;
    if UninstallerInformation.NumberOfLinks <> 1 then
    begin
      Reason := '已登记卸载器不能是硬链接。';
      Exit;
    end;

    ExtractTemporaryFile('{#IdentityHelperName}');
    IdentityHelperRoot := NormalizePath(ExpandConstant('{tmp}'));
    IdentityHelperPath := AddBackslash(IdentityHelperRoot) + '{#IdentityHelperName}';
    if not TryLockTrustedRegularFile(
      IdentityHelperPath,
      IdentityHelperRoot,
      '{#IdentityHelperSha256}',
      IdentityHelperHandle,
      Reason) then
      Exit;

    Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' +
      AddQuotes(IdentityHelperPath) +
      ' -ApplicationPath ' + AddQuotes(ApplicationPath) +
      ' -UninstallerPath ' + AddQuotes(UninstallerPath) +
      ' -ExpectedProductVersion ' + AddQuotes(ExistingVersion);
    if not Exec(
      ExpandConstant('{sys}\' + PowerShellRelativePath),
      Parameters,
      Path,
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) or (ResultCode <> 0) then
    begin
      Reason := '已登记安装的 Authenticode 状态或版本身份无效。';
      Exit;
    end;

    Result := True;
  finally
    if IdentityHelperHandle <> THandle(-1) then
      CloseHandle(IdentityHelperHandle);
    if UninstallerHandle <> THandle(-1) then
      CloseHandle(UninstallerHandle);
    if ApplicationHandle <> THandle(-1) then
      CloseHandle(ApplicationHandle);
    if MarkerHandle <> THandle(-1) then
      CloseHandle(MarkerHandle);
    ReleaseInstallPathHandles;
  end;
end;

function VerifyWebView2EnvironmentHealth(var Reason: String): Boolean;
var
  TemporaryRoot: String;
  HelperPath: String;
  CoreAssemblyPath: String;
  LoaderPath: String;
  UserDataFolder: String;
  HelperHandle: THandle;
  CoreAssemblyHandle: THandle;
  LoaderHandle: THandle;
  Parameters: String;
  ResultCode: Integer;
begin
  Result := False;
  HelperHandle := THandle(-1);
  CoreAssemblyHandle := THandle(-1);
  LoaderHandle := THandle(-1);
  ExtractTemporaryFile('{#WebView2HealthHelperName}');
  ExtractTemporaryFile('{#WebView2CoreAssemblyName}');
  ExtractTemporaryFile('{#WebView2LoaderName}');
  TemporaryRoot := NormalizePath(ExpandConstant('{tmp}'));
  HelperPath := AddBackslash(TemporaryRoot) + '{#WebView2HealthHelperName}';
  CoreAssemblyPath := AddBackslash(TemporaryRoot) + '{#WebView2CoreAssemblyName}';
  LoaderPath := AddBackslash(TemporaryRoot) + '{#WebView2LoaderName}';
  UserDataFolder := AddBackslash(TemporaryRoot) +
    'DshWindowsLauncher-WebView2Health-' + IntToStr(Random(2147483647));

  try
    if not TryLockTrustedRegularFile(
      HelperPath,
      TemporaryRoot,
      '{#WebView2HealthHelperSha256}',
      HelperHandle,
      Reason) then
      Exit;
    if not TryLockTrustedRegularFile(
      CoreAssemblyPath,
      TemporaryRoot,
      '{#WebView2CoreAssemblySha256}',
      CoreAssemblyHandle,
      Reason) then
      Exit;
    if not TryLockTrustedRegularFile(
      LoaderPath,
      TemporaryRoot,
      '{#WebView2LoaderSha256}',
      LoaderHandle,
      Reason) then
      Exit;

    Parameters := '-Sta -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' +
      AddQuotes(HelperPath) +
      ' -CoreAssemblyPath ' + AddQuotes(CoreAssemblyPath) +
      ' -UserDataFolder ' + AddQuotes(UserDataFolder) +
      ' -MinimumVersion ' + AddQuotes('{#WebView2MinimumVersion}') +
      ' -TimeoutSeconds 30';
    if not Exec(
      ExpandConstant('{sys}\' + PowerShellRelativePath),
      Parameters,
      TemporaryRoot,
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) or (ResultCode <> 0) then
    begin
      Reason := 'WebView2 Runtime 无法创建隔离的 Stable 环境。';
      Exit;
    end;

    if DirExists(UserDataFolder) then
    begin
      Reason := 'WebView2 Runtime 健康探测未释放隔离数据目录。';
      Exit;
    end;
    Result := True;
  finally
    if LoaderHandle <> THandle(-1) then
      CloseHandle(LoaderHandle);
    if CoreAssemblyHandle <> THandle(-1) then
      CloseHandle(CoreAssemblyHandle);
    if HelperHandle <> THandle(-1) then
      CloseHandle(HelperHandle);
  end;
end;

function RepairAndVerifyWebView2(var NeedsRestart: Boolean; var Reason: String): Boolean;
var
  InstallerPath: String;
  InstalledVersion: String;
  ResultCode: Integer;
begin
  Result := False;
  InstalledVersion := GetInstalledWebView2Version;
  if CompareNumericVersions(InstalledVersion, '{#WebView2MinimumVersion}') >= 0 then
  begin
    if VerifyWebView2EnvironmentHealth(Reason) then
    begin
      Result := True;
      Exit;
    end;
    Reason := '';
  end;

  ExtractTemporaryFile('{#RuntimeInstallerName}');
  InstallerPath := ExpandConstant('{tmp}\{#RuntimeInstallerName}');

  if not Exec(InstallerPath, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Reason := '无法启动 WebView2 Runtime 离线安装程序。';
    Exit;
  end;

  if (ResultCode <> 0) and (ResultCode <> 3010) then
  begin
    Reason := 'WebView2 Runtime 修复失败，退出码 ' + IntToStr(ResultCode) + '。';
    Exit;
  end;

  if ResultCode = 3010 then
  begin
    NeedsRestart := True;
    RuntimeRestartRequired := True;
  end;

  InstalledVersion := GetInstalledWebView2Version;
  if CompareNumericVersions(InstalledVersion, '{#WebView2MinimumVersion}') < 0 then
  begin
    Reason := 'WebView2 Runtime 版本低于要求的 {#WebView2MinimumVersion}。';
    Exit;
  end;

  if not VerifyWebView2EnvironmentHealth(Reason) then
    Exit;

  Result := True;
end;

function InitializeSetup: Boolean;
begin
  ExistingInstall := ReadExistingInstallation;
  DeleteApplicationData := False;
  RuntimeRestartRequired := False;
  SetArrayLength(InstallPathHandles, 0);
  MaintenanceHelperHandle := THandle(-1);
  MaintenanceReservationActive := False;
  MaintenanceHelperPending := False;
  MaintenanceReadyPath := '';
  MaintenanceFailurePath := '';
  MaintenanceStopPath := '';
  MaintenanceReleasedPath := '';
  Result := True;

  if ExistingInstall and
    ((not IsSafeRegisteredValue(ExistingInstallPath, MaxFinalPathCharacters - 1)) or
    (not IsSafeRegisteredValue(ExistingVersion, 128))) then
  begin
    SuppressibleMsgBox(
      '已安装产品登记包含不安全或过长的路径或版本值。为避免覆盖错误位置，安装已中止。',
      mbCriticalError,
      MB_OK,
      IDOK);
    Result := False;
  end
  else if ExistingInstall and (ExistingVersion = '') then
  begin
    SuppressibleMsgBox(
      '已安装产品缺少可验证的 DisplayVersion。为避免错误降级或覆盖，安装已中止。请先普通卸载并保留数据。',
      mbCriticalError,
      MB_OK,
      IDOK);
    Result := False;
  end
  else if ExistingInstall and
    (CompareSemanticVersions('{#AppVersion}', ExistingVersion) < 0) then
  begin
    SuppressibleMsgBox(
      '已安装版本 ' + ExistingVersion + ' 高于当前安装包 {#AppVersion}，不允许降级。',
      mbCriticalError,
      MB_OK,
      IDOK);
    Result := False;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Reason: String;
begin
  Result := True;
  if CurPageID <> wpSelectDir then
    Exit;

  WizardForm.DirEdit.Text := NormalizePath(WizardDirValue);
  if not ValidateInstallDirectory(WizardDirValue, Reason) then
  begin
    SuppressibleMsgBox(Reason, mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Reason: String;
begin
  Result := '';
  if not ValidateInstallDirectory(WizardDirValue, Reason) then
  begin
    Result := Reason;
    Exit;
  end;

  if ExistingInstall and not ValidateRegisteredInstallation(Reason) then
  begin
    Result := Reason;
    Exit;
  end;

  if not RunMaintenanceIpcHelper(False, Reason) then
  begin
    Result := Reason + ' 安装已中止，且不会强制结束进程。';
    Exit;
  end;

  if not RepairAndVerifyWebView2(NeedsRestart, Reason) then
  begin
    Result := Reason;
    Exit;
  end;

  if not SecureInstallDirectory(WizardDirValue, Reason) then
  begin
    Result := Reason;
    Exit;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if not ReleaseMaintenanceIpcReservation then
      RaiseException('当前用户 IPC 维护占用未能确认释放。');
  end;
  if CurStep = ssDone then
    ReleaseInstallPathHandles;
end;

procedure DeinitializeSetup;
begin
  BestEffortReleaseMaintenanceIpcReservation;
  ReleaseInstallPathHandles;
end;

function ShouldOfferLaunch: Boolean;
begin
  Result := ReleaseMaintenanceIpcReservation;
  if Result then
    Result := (not ExistingInstall) and (not RuntimeRestartRequired);
end;

function TryOpenOwnedEntry(const Path: String; const OwnershipRoot: String;
  const ExpectDirectory: Boolean; const DesiredAccess: Cardinal;
  var EntryHandle: THandle; var Information: TDshByHandleFileInformation;
  var Reason: String): Boolean;
var
  Attributes: Cardinal;
  OpenFlags: Cardinal;
  FinalPathBuffer: String;
  FinalPathLength: Cardinal;
  FinalPath: String;
begin
  Result := False;
  EntryHandle := THandle(-1);
  Attributes := GetFileAttributesW(Path);
  if Attributes = InvalidFileAttributes then
  begin
    Reason := '数据项在安全打开前已不存在。';
    Exit;
  end;

  if ExpectDirectory <> ((Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) then
  begin
    Reason := '数据项类型在安全打开前发生变化。';
    Exit;
  end;

  OpenFlags := FileFlagOpenReparsePoint;
  if ExpectDirectory then
    OpenFlags := OpenFlags or FileFlagBackupSemantics;
  EntryHandle := CreateFileW(
    Path,
    DesiredAccess,
    FileShareRead,
    0,
    OpenExisting,
    OpenFlags,
    0);
  if EntryHandle = THandle(-1) then
  begin
    Reason := '无法以禁止写入和替换的方式锁定数据项。';
    Exit;
  end;

  if not GetFileInformationByHandle(EntryHandle, Information) then
  begin
    CloseHandle(EntryHandle);
    EntryHandle := THandle(-1);
    Reason := '无法通过句柄读取数据项身份。';
    Exit;
  end;

  if ((Information.FileAttributes and FileAttributeReparsePoint) <> 0) or
    (ExpectDirectory <> ((Information.FileAttributes and FILE_ATTRIBUTE_DIRECTORY) <> 0)) then
  begin
    CloseHandle(EntryHandle);
    EntryHandle := THandle(-1);
    Reason := '数据项是重解析点或类型已发生变化。';
    Exit;
  end;

  FinalPathBuffer := StringOfChar(#0, MaxFinalPathCharacters);
  FinalPathLength := GetFinalPathNameByHandleW(
    EntryHandle,
    FinalPathBuffer,
    MaxFinalPathCharacters,
    0);
  if (FinalPathLength = 0) or (FinalPathLength >= MaxFinalPathCharacters) then
  begin
    CloseHandle(EntryHandle);
    EntryHandle := THandle(-1);
    Reason := '无法通过句柄解析数据项最终路径。';
    Exit;
  end;

  FinalPath := NormalizeFinalPath(Copy(FinalPathBuffer, 1, FinalPathLength));
  if (CompareText(FinalPath, NormalizePath(Path)) <> 0) or
    not IsPathWithinRoot(FinalPath, OwnershipRoot) then
  begin
    CloseHandle(EntryHandle);
    EntryHandle := THandle(-1);
    Reason := '数据项句柄解析到应用数据根之外。';
    Exit;
  end;

  Result := True;
end;

function MarkOwnedHandleForDeletion(const EntryHandle: THandle): Boolean;
var
  Disposition: TDshFileDispositionInfoEx;
begin
  Disposition.Flags := FileDispositionFlagDelete or
    FileDispositionFlagIgnoreReadOnlyAttribute;
  Result := SetFileInformationByHandle(
    EntryHandle,
    FileDispositionInfoEx,
    Disposition,
    SizeOf(Disposition));
end;

function DeleteOwnedDirectoryByHandle(const Path: String;
  const OwnershipRoot: String; var Reason: String): Boolean; forward;

function DeleteOwnedEntryByHandle(const Path: String;
  const OwnershipRoot: String; const ExpectDirectory: Boolean;
  var Reason: String): Boolean;
var
  EntryHandle: THandle;
  Information: TDshByHandleFileInformation;
begin
  if ExpectDirectory then
  begin
    Result := DeleteOwnedDirectoryByHandle(Path, OwnershipRoot, Reason);
    Exit;
  end;

  Result := False;
  if not TryOpenOwnedEntry(
    Path,
    OwnershipRoot,
    False,
    FileAccessDelete or FileReadAttributes,
    EntryHandle,
    Information,
    Reason) then
    Exit;
  try
    if not MarkOwnedHandleForDeletion(EntryHandle) then
    begin
      Reason := '无法通过已验证文件句柄删除数据项。';
      Exit;
    end;
    Result := True;
  finally
    CloseHandle(EntryHandle);
  end;
end;

function DeleteOwnedDirectoryContentsByHandle(const Path: String;
  const OwnershipRoot: String; const SkipPath: String;
  var Reason: String): Boolean;
var
  FindResult: TFindRec;
  ChildPath: String;
  ExpectDirectory: Boolean;
begin
  Result := True;
  if not FindFirst(AddBackslash(Path) + '*', FindResult) then
    Exit;
  try
    repeat
      if (FindResult.Name <> '.') and (FindResult.Name <> '..') then
      begin
        ChildPath := AddBackslash(Path) + FindResult.Name;
        if (SkipPath <> '') and
          (CompareText(NormalizePath(ChildPath), NormalizePath(SkipPath)) = 0) then
          Continue;

        ExpectDirectory :=
          (FindResult.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0;
        if not DeleteOwnedEntryByHandle(
          ChildPath,
          OwnershipRoot,
          ExpectDirectory,
          Reason) then
        begin
          Result := False;
          Exit;
        end;
      end;
    until not FindNext(FindResult);
  finally
    FindClose(FindResult);
  end;
end;

function DeleteOwnedDirectoryByHandle(const Path: String;
  const OwnershipRoot: String; var Reason: String): Boolean;
var
  DirectoryHandle: THandle;
  Information: TDshByHandleFileInformation;
begin
  Result := False;
  if not TryOpenOwnedEntry(
    Path,
    OwnershipRoot,
    True,
    FileAccessDelete or FileReadAttributes,
    DirectoryHandle,
    Information,
    Reason) then
    Exit;
  try
    if not DeleteOwnedDirectoryContentsByHandle(
      Path,
      OwnershipRoot,
      '',
      Reason) then
      Exit;
    if not IsDirectoryEmpty(Path) then
    begin
      Reason := '数据目录在删除期间新增或残留条目。';
      Exit;
    end;
    if not MarkOwnedHandleForDeletion(DirectoryHandle) then
    begin
      Reason := '无法通过已验证目录句柄删除数据目录。';
      Exit;
    end;
    Result := True;
  finally
    CloseHandle(DirectoryHandle);
  end;
end;

function ClearOwnedApplicationData(var ResidualPath: String): Boolean;
var
  ApplicationDataPath: String;
  ExpectedPath: String;
  DriveRoot: String;
  ParentPath: String;
  MarkerPath: String;
  MarkerValue: AnsiString;
  RootHandle: THandle;
  MarkerHandle: THandle;
  RootInformation: TDshByHandleFileInformation;
  MarkerInformation: TDshByHandleFileInformation;
  Reason: String;
  RootAttributes: Cardinal;
begin
  Result := False;
  RootHandle := THandle(-1);
  MarkerHandle := THandle(-1);
  ApplicationDataPath := NormalizePath(ExpandConstant('{localappdata}\DshWindowsLauncher'));
  ExpectedPath := NormalizePath(AddBackslash(ExpandConstant('{localappdata}')) + 'DshWindowsLauncher');
  ResidualPath := ApplicationDataPath;

  if CompareText(ApplicationDataPath, ExpectedPath) <> 0 then
    Exit;
  RootAttributes := GetFileAttributesW(ApplicationDataPath);
  if RootAttributes = InvalidFileAttributes then
  begin
    Result := True;
    ResidualPath := '';
    Exit;
  end;
  if (RootAttributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
    Exit;
  DriveRoot := AddBackslash(ExtractFileDrive(ApplicationDataPath));
  ParentPath := NormalizePath(ExtractFileDir(ApplicationDataPath));
  if (GetDriveTypeW(DriveRoot) <> DriveFixed) or
    not LockExistingPathComponents(ParentPath, DriveRoot, Reason) then
    Exit;

  try
    if not TryOpenOwnedEntry(
      ApplicationDataPath,
      ApplicationDataPath,
      True,
      FileAccessDelete or FileReadAttributes,
      RootHandle,
      RootInformation,
      Reason) then
      Exit;

    MarkerPath := AddBackslash(ApplicationDataPath) + OwnershipMarkerName;
    if not TryOpenOwnedEntry(
      MarkerPath,
      ApplicationDataPath,
      False,
      GenericRead or FileAccessDelete or FileReadAttributes,
      MarkerHandle,
      MarkerInformation,
      Reason) then
      Exit;
    if (MarkerInformation.NumberOfLinks <> 1) or
      (MarkerInformation.FileSizeHigh <> 0) or
      (MarkerInformation.FileSizeLow > 128) then
      Exit;
    if not LoadStringFromFile(MarkerPath, MarkerValue) then
      Exit;
    if Trim(String(MarkerValue)) <> OwnershipMarkerContent then
      Exit;

    if not DeleteOwnedDirectoryContentsByHandle(
      ApplicationDataPath,
      ApplicationDataPath,
      MarkerPath,
      Reason) then
      Exit;
    if not MarkOwnedHandleForDeletion(MarkerHandle) then
      Exit;
    CloseHandle(MarkerHandle);
    MarkerHandle := THandle(-1);

    if not IsDirectoryEmpty(ApplicationDataPath) then
      Exit;
    if not MarkOwnedHandleForDeletion(RootHandle) then
      Exit;
    CloseHandle(RootHandle);
    RootHandle := THandle(-1);

    Result := not DirExists(ApplicationDataPath);
    if Result then
      ResidualPath := '';
  finally
    if MarkerHandle <> THandle(-1) then
      CloseHandle(MarkerHandle);
    if RootHandle <> THandle(-1) then
      CloseHandle(RootHandle);
    ReleaseInstallPathHandles;
  end;
end;

function InitializeUninstall: Boolean;
var
  FirstConfirmation: Integer;
  SecondConfirmation: Integer;
  Reason: String;
begin
  SetArrayLength(InstallPathHandles, 0);
  MaintenanceHelperHandle := THandle(-1);
  MaintenanceReservationActive := False;
  MaintenanceHelperPending := False;
  MaintenanceReadyPath := '';
  MaintenanceFailurePath := '';
  MaintenanceStopPath := '';
  MaintenanceReleasedPath := '';
  Result := RunMaintenanceIpcHelper(True, Reason);
  if not Result then
  begin
    SuppressibleMsgBox(
      Reason + ' 卸载已中止，且不会强制结束进程。',
      mbCriticalError,
      MB_OK,
      IDOK);
    Exit;
  end;

  DeleteApplicationData := False;
  FirstConfirmation := SuppressibleMsgBox(
    '普通卸载会保留全部本地配置和配对会话。是否额外删除这些数据？此操作不可恢复。',
    mbConfirmation,
    MB_YESNO or MB_DEFBUTTON2,
    IDNO);
  if FirstConfirmation <> IDYES then
    Exit;

  SecondConfirmation := SuppressibleMsgBox(
    '再次确认：删除 %LOCALAPPDATA%\DshWindowsLauncher 中带有效所有权标记的全部本地数据？',
    mbCriticalError,
    MB_YESNO or MB_DEFBUTTON2,
    IDNO);
  DeleteApplicationData := SecondConfirmation = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResidualPath: String;
begin
  if (CurUninstallStep = usPostUninstall) and DeleteApplicationData then
  begin
    if not ClearOwnedApplicationData(ResidualPath) then
      SuppressibleMsgBox(
        '程序卸载已完成，但本地数据未能安全删除。残留路径：' + ResidualPath,
        mbError,
        MB_OK,
        IDOK);
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    if not ReleaseMaintenanceIpcReservation then
      SuppressibleMsgBox(
        '程序卸载已完成，但当前用户 IPC 维护占用未能确认释放；本卸载进程退出后辅助程序将自行释放。',
        mbError,
        MB_OK,
        IDOK);
  end;
end;

procedure DeinitializeUninstall;
begin
  BestEffortReleaseMaintenanceIpcReservation;
  ReleaseInstallPathHandles;
end;
