# 工程与发布入口

`eng/` 的正式发布流程只公开三个顺序入口。CI 如存在，也只能调用这些脚本，不复制或跳过
脚本内门禁。另有一个与正式身份完全隔离的轻量内网测试打包入口；它不能进入正式发布
流程。本项目不签名任何自有产物（见 docs/adr/0008），但正式包保留全部安装器安全加固。

## `make-app-icon.ps1`

从 `assets/brand/mascot-source.png`（原创 mascot 位图）生成应用图标：整幅方形源图套上自有圆角遮罩
把白色四角切成真透明，输出多尺寸 `assets/brand/app/DshWindowsLauncher.ico` 与逐尺寸 PNG 预览。
按尺寸分两档：≥40px 用整幅构图，≤32px 改用脸部放大裁切（否则 16px 上只剩头发）。
`-Verify` 重画到临时目录并逐字节比对已提交产物，用于阻止图标与生成器漂移（Acceptance 测试会调用它）。

```powershell
pwsh -File .\eng\make-app-icon.ps1          # 重新生成
pwsh -File .\eng\make-app-icon.ps1 -Verify  # 只校验不写盘
```

## `verify.ps1`

执行精确 SDK 检查、locked restore、格式检查、Release `win-x64` 构建、全部自动测试、固定项目依赖图、NuGet 来源与锁文件、漏洞/弃用依赖、第三方许可证、SPDX 2.3 SBOM 及仓库秘密扫描。任一步失败均返回非零。

```powershell
pwsh -File .\eng\verify.ps1 `
  -DotNetPath "$env:LOCALAPPDATA\DshWindowsLauncherDev\dotnet-10.0.400\dotnet.exe"
```

三个测试项目（Core、Platform.Windows、Acceptance）作为 Microsoft.Testing.Platform 可执行测试模块逐一运行，避免混用 VSTest。脚本先枚举预期测试方法和固定数据集，再用结构化结果核对实际执行、动态 Theory 展开、零跳过和逐数据集 `triggerTags`。结果目录包含逐模块 `.mtp.log`、脱敏 `.test-evidence.json`、夹具清单和 `verify-summary.json`。`releaseStatus=candidate` 时，任何空发布常量、不符合 `^https?://` 的发布地址或 `distribution.signing.policy` 不等于 `none` 都会在构建前阻断（内网场景允许使用 HTTP）。

## `package.ps1`

正式打包必须在没有安装本产品的干净隔离 Windows runner 上进行。脚本要求：

- 参数 SemVer 与候选发布常量完全相同；
- WebView2 Evergreen Standalone x64、Evergreen Bootstrapper 和 Inno Setup 官方安装包匹配冻结版本、SHA-256、有效 Authenticode 与时间戳；
- `ISCC.exe` 精确为 Inno Setup `7.0.2` 且由 Pyrsys B.V. 有效签名；
- 自有产物全部保持 `NotSigned`：不需要 `signtool.exe`、证书或时间戳服务；
- 用户显式允许在专用固定卷目录短暂安装最终包，以验证真实 `unins*.exe` 的身份与未签名状态。

```powershell
pwsh -File .\eng\package.ps1 `
  -Version 1.0.0-rc.1 `
  -WebView2OfflineInstallerPath C:\release-inputs\MicrosoftEdgeWebView2RuntimeInstallerX64.exe `
  -WebView2BootstrapperPath C:\release-inputs\MicrosoftEdgeWebview2Setup.exe `
  -InnoSetupInstallerPath C:\release-inputs\innosetup-7.0.2-x64.exe `
  -InnoCompilerPath "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe" `
  -UninstallerVerificationDirectory C:\release-validation\DshWindowsLauncher `
  -AllowInstallerExecutionForUninstallerVerification
```

脚本先完整运行 `verify.ps1`，再发布自包含、多文件、非裁剪应用，把已冻结 Bootstrapper 固定暂存并安装为 `MicrosoftEdgeWebview2Setup.exe`，断言 apphost 与托管主程序集保持未签名，编译不带签名的 Inno 安装器和卸载器。安装器仍保留全部 `[Code]` 安全加固（路径锁定、重解析点拒绝、IPC 退出、就地升级身份校验）。最终安装器、`.sha256`、`package-manifest.json`（`schemaVersion` 3，`ownArtifactsSigned=false`）、验证摘要、SPDX 2.3 SBOM、第三方许可证清单和发行说明输入写入 `artifacts/package/<version>/release/`。安装器 SHA-256 只在全部真实卸载器验证完成后冻结。

不再存在签名参数。证书私钥、签名主体和时间戳服务地址不再是发布契约的一部分；
上游第三方输入的 Microsoft / Pyrsys B.V. 签名校验保留，因为它们验证的是别人交给我们的字节。

## `package-internal.ps1`

内部测试打包只接受形如 `x.y.z-internal-test.n` 的版本，其中 `x.y.z` 必须与发布常量中的
产品版本一致，`n` 必须为正整数。它还要求：

- `releaseStatus` 精确为 `development`；
- Git 提交可用且工作树为 clean；
- 完整运行 `verify.ps1`，且 PASS 摘要绑定相同提交；
- WebView2 Evergreen Bootstrapper 的版本、SHA-256、Microsoft Authenticode 和可信时间戳
  全部匹配冻结基线；
- Inno Setup 编译器具有有效 Pyrsys B.V. Authenticode 和可信时间戳；
- 发布显式使用 `LauncherBuildFlavor=InternalTest`、自包含、多文件、非裁剪 `win-x64`；
- 主程序和安装器的 Authenticode 状态均精确为 `NotSigned`。

```powershell
pwsh -File .\eng\package-internal.ps1 `
  -Version 1.0.0-internal-test.1 `
  -WebView2BootstrapperPath C:\internal-inputs\MicrosoftEdgeWebview2Setup.exe `
  -InnoCompilerPath "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe" `
  -DotNetPath "$env:LOCALAPPDATA\DshWindowsLauncherDev\dotnet-10.0.400\dotnet.exe"
```

输出位于 `artifacts/package-internal/<version>/release/`，包含：

- `DSH-Windows-Launcher-INTERNAL-TEST-UNSIGNED-NOT-FOR-PRODUCTION-USE-<version>-win-x64.exe`；
- `internal-test-manifest.json`；
- `SHA256SUMS.txt`；
- `verify-summary.json`；
- `sbom.spdx.json`；
- `third-party-licenses.json`。

内部 manifest 不含生成时间或绝对输入路径，固定记录 `releaseEligible=false`、`signed=false`
及源提交。该脚本不执行安装或卸载。内部安装包不得传给 `release-smoke.ps1`，不得改名为
正式安装包，也不得作为正式候选或正式 Release。仅在当次获得明确上传授权后，才可作为
`prerelease=true` 的预发布上传；标题、正文和文件名必须完整保留 `INTERNAL TEST`、
`UNSIGNED`、`NOT FOR PRODUCTION USE`，且不得设为 latest 或正式发布。

## `package-unsigned.ps1`

`package-unsigned.ps1` 是 `package-internal.ps1 -OfficialUnsignedRelease` 的别名入口，用于内网
快速出正式身份包：使用正式产品名、`DshWindowsLauncher.exe`、正式 AppId 与数据根，但不跑
真实安装/卸载验证，并使用 `installer/DshWindowsLauncher.UnsignedRelease.iss`——这一份安装器
**不含** `[Code]` 安全加固。它要求 `releaseStatus=candidate`、`distribution.signing.policy=none`
且版本与发布常量完全相同，产物写入 `artifacts/package/<version>/release/`。

需要安装器安全加固、真实卸载器验证和完整发布证据时，只能使用 `package.ps1`（同样不签名）。

## `release-smoke.ps1`

该脚本只接受与 `package-manifest.json`（`schemaVersion` 3）、冻结 SHA-256、`NotSigned` Authenticode 状态和 PASS 验证摘要一致的安装包；候选与已安装 EXE 也必须实测为 `NotSigned`。`FirstRelease` 选择 `RS-01` 至 `RS-15`；`RegularPatch` 选择全部“每次”项目和 `-TriggerTags` 命中的条件项目。

```powershell
pwsh -File .\eng\release-smoke.ps1 `
  -InstallerPath 'C:\release\DSH Windows Launcher-Setup-1.0.0-rc.1-win-x64.exe' `
  -EvidenceDirectory C:\release-evidence\1.0.0-rc.1 `
  -ReleaseKind FirstRelease
```

每个 RS 结果必须由操作者输入。脚本不会根据安装包预检自动写入 RS `PASS`。`-NonInteractive` 只生成 `PENDING` 项并返回非零。生成的 `release-evidence.md` 始终保留 `Human release confirmation: NO`，人工发布确认必须在门禁全部通过后另行完成。

## 安装与数据契约

- 单实例基础名为 `DshWindowsLauncher.SingleInstance`；实际互斥和 IPC 名包含当前用户 SID，并使用 current-user ACL。
- 安装器通过 `--request-maintenance-exit --timeout-seconds 30` 请求正常退出。非零、超时或拒绝均在程序文件替换前中止，不强杀进程。
- 安装器在写入前以不共享删除权限的目录句柄锁定固定卷根至 `{app}` 的每个组件，拒绝重解析点，并用 `GetFinalPathNameByHandleW` 复核最终路径、卷和根边界；句柄保持到安装完成。
- 应用数据所有权标记固定为 `%LOCALAPPDATA%\DshWindowsLauncher\.dsh-windows-launcher-owner`，内容为 `DshWindowsLauncher:v2`。
- 普通卸载不删除应用数据。清数据需要两次默认否的确认，且精确根、所有权标记和整棵目录无重解析点全部验证后才逐项删除。
- 就地升级前的安装身份校验只接受 `NotSigned` 的已登记 EXE 与卸载器，用于拒绝被替换成其他来源（他人签名、`HashMismatch`、`Unknown`）的文件。

## Linux 夹具

无凭据错误服务夹具见 [fixtures/linux/README.md](fixtures/linux/README.md)。夹具不保存请求目标、请求体或 token，启动和停止均可重复执行。
