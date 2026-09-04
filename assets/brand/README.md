# 品牌图标资产

本目录集中应用图标相关的素材与产物。决策与约束见
[ADR 0009](../../docs/adr/0009-app-icon-from-original-mascot.md)。

## 素材来源

| 文件 | 说明 |
|---|---|
| `mascot-source.png` | **唯一设计输入**：1254×1254 满幅方形 mascot 插画（蓝发、眨眼、女仆头饰、深蓝服饰与宝石领结），自带圆角容器与白色四角，由产品方提供。约 2.3 MB，是仓库最大的单个资产。**不要手工编辑**；换图请直接替换本文件后重新生成。 |
| `app/DshWindowsLauncher.ico` | 生成产物：16/20/24/32/40/48/64 帧为 32bpp DIB（含 AND 掩码），128/256 帧为 PNG。 |
| `app/png/app-<size>.png` | 逐尺寸 RGBA 预览，用于目视检查小尺寸可读性。 |

该素材为 AI 生成的位图，其可版权性与来源链条在多数法域仍不确定；本仓库只记录「由产品方提供」，
不声称原创著作权。对外发布前需重新评估。

**禁止**：上游 DeepSeek Harness 官方标志（`favicon.svg` 及其任何可识别几何变体）与微软徽标图形
（四格旗帜等）不得作为本产品的图标、徽标或品牌元素。理由见 ADR 0009。

## 设计令牌

全部以显式变量写在 `eng/make-app-icon.ps1` 顶部：

| 令牌 | 值 | 含义 |
|---|---|---|
| `cornerRadiusRatio` | 0.23 | 自有圆角遮罩半径；必须 ≥ 源图自带圆角，否则白角残留成白边 |
| `smallSizeThreshold` | 32 | ≤ 该尺寸走小尺寸档 |
| `smallFaceZoom` | 1.85 | 小尺寸档的放大裁切倍数 |
| `smallFaceAnchorX/Y` | 0.55 / 0.56 | 脸部锚点（相对源图像素尺寸的比例） |
| `supersampleFactor` | 4 | 先按 4 倍尺寸绘制再 HighQuality 降采样 |

两档是**两套构图**：≥40px 用整幅，≤32px 用脸部特写。这是为了让 16px 还能认出人脸的刻意取舍。

## 重新生成

```powershell
pwsh -File .\eng\make-app-icon.ps1            # 重写 app/ 下全部产物
pwsh -File .\eng\make-app-icon.ps1 -Verify    # 只校验已提交产物能否逐字节复现
```

渲染走 WPF（`DrawingVisual` + `RenderTargetBitmap`），无第三方依赖、无联网。`-Verify` 失败即说明
产物与生成器漂移——Acceptance 测试 `ApplicationIconIsReproducibleAndWiredIntoEverySurface` 会调用它。

改设计后请**同时**检查 `app/png/app-16.png`、`app-32.png`、`app-256.png` 再下结论：只看大图会
做出在小尺寸上完全不可用的图标。若换了新 mascot 素材，脸部锚点与放大倍数需要重测。

## 接入面

- `src/DshLauncher.Desktop/DshLauncher.Desktop.csproj` 的 `<ApplicationIcon>`：apphost 内嵌，
  覆盖资源管理器、任务栏、Alt+Tab、开始菜单快捷方式、卸载显示图标与 WPF 窗口默认图标。
- 三个安装器脚本（`installer/*.iss`）的 `SetupIconFile`，缺文件时 ISPP `#error` 拒绝编译。
- `src/DshLauncher.Desktop/TrayHost.cs` 的 `Icon.ExtractAssociatedIcon(Environment.ProcessPath)`
  因此自动拿到品牌图标，无需额外资源。

图标是二进制资产：`.gitattributes` 中 `*.ico`、`*.png` 标记为 `binary`。
