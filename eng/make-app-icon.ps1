<#
.SYNOPSIS
    由 mascot 位图源生成 DSH Windows Launcher 的应用图标（.ico / .png）。

.DESCRIPTION
    源素材：assets/brand/mascot-source.png —— 满幅方形插画，自带圆角容器，四角为白。
    本脚本不做图块检测（画面溢出边界，投影法拿不到干净边界），而是：
      1. 整幅方形作为源裁切区；
      2. 套上自己的圆角方形遮罩（半径比例 >= 画面自带圆角），把白色角切成真透明；
      3. 大尺寸档直接用整幅构图；小尺寸档（<=32px）改用围绕脸部的放大裁切，
         因为细节插画降采样到 16/24px 必然糊成一团。
    渲染走 WPF（DrawingVisual + RenderTargetBitmap），先 4 倍超采样再 HighQuality 降采样。
    .ico 中 <=64px 帧写成 32bpp DIB（含 AND 掩码），>=128px 帧写成 PNG。

    确定性：同机同参数输出字节稳定。-Verify 重画到临时目录并逐字节比对已提交产物。

.EXAMPLE
    pwsh -File .\eng\make-app-icon.ps1
    pwsh -File .\eng\make-app-icon.ps1 -Verify
#>
[CmdletBinding()]
param(
    [int[]] $Sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256),

    [string] $OutputDirectory,

    [switch] $Verify
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'common.ps1')

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$sourcePath = Join-Path $repositoryRoot 'assets/brand/mascot-source.png'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'assets/brand/app'
}

# 圆角遮罩半径（相对画布边长）。必须 >= 源图自带圆角，否则白角会残留成白边。
$cornerRadiusRatio = 0.23
# 小尺寸档：围绕脸部放大裁切。锚点是相对源图像素尺寸的比例；
# 源图里眼睛大约在 x≈0.55 / y≈0.56，放大倍数不够则 16px 上只剩头发。
$smallSizeThreshold = 32
$smallFaceZoom = 1.85
$smallFaceAnchorX = 0.55
$smallFaceAnchorY = 0.56
$supersampleFactor = 4

function Write-DshBytes {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][byte[]] $Bytes
    )

    # Set-Content 会把 byte[] 按文本序列化，裸字节必须走 WriteAllBytes。
    [IO.File]::WriteAllBytes($Path, $Bytes)
}

function Assert-DshIconFile {
    param([Parameter(Mandatory)][string] $Path)

    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 6) {
        throw '图标文件短于 ICONDIR。'
    }
    if ([BitConverter]::ToUInt16($bytes, 0) -ne 0 -or [BitConverter]::ToUInt16($bytes, 2) -ne 1) {
        throw '图标头部不是 ICONDIR（reserved=0 / type=1）。'
    }

    $frameCount = [BitConverter]::ToUInt16($bytes, 4)
    $expectedOffset = 6 + (16 * $frameCount)
    for ($index = 0; $index -lt $frameCount; $index++) {
        $entry = 6 + ($index * 16)
        $size = [int] $bytes[$entry]
        if ($size -eq 0) { $size = 256 }
        $imageSize = [BitConverter]::ToUInt32($bytes, $entry + 8)
        $imageOffset = [BitConverter]::ToUInt32($bytes, $entry + 12)
        if ($imageOffset -ne $expectedOffset) {
            throw "图标第 $index 帧偏移不连续：实际 $imageOffset，预期 $expectedOffset。"
        }
        if (($expectedOffset + $imageSize) -gt $bytes.Length) {
            throw "图标第 $index 帧越界。"
        }
        $isPng = (@($bytes[$imageOffset], $bytes[$imageOffset + 1], $bytes[$imageOffset + 2], $bytes[$imageOffset + 3]) -join ',') -eq '137,80,78,71'
        $isBmp = ([BitConverter]::ToUInt32($bytes, $imageOffset) -eq 40) `
            -and ([BitConverter]::ToUInt16($bytes, $imageOffset + 12) -eq 1) `
            -and ([BitConverter]::ToUInt16($bytes, $imageOffset + 14) -eq 32)
        if (-not $isPng -and -not $isBmp) {
            throw "图标第 $index 帧既不是 32bpp BMP 也不是 PNG。"
        }
        $expectedOffset += $imageSize
        Write-Host ("  frame {0}x{0}  {1} bytes  @{2}" -f $size, $imageSize, $imageOffset)
    }
    if ($expectedOffset -ne $bytes.Length) {
        throw "图标尾部多余 $($bytes.Length - $expectedOffset) 字节。"
    }

    return $frameCount
}

function Get-DshSourceRect {
    param(
        [Parameter(Mandatory)][pscustomobject] $Source,
        [Parameter(Mandatory)][bool] $FaceCrop
    )

    # 统一用像素尺寸：BitmapSource.Width 是 96dpi 下的 DIP，若源图 DPI 元数据不是 96 就会与绘制时的 PixelWidth 错位。
    if (-not $FaceCrop) {
        return [System.Windows.Rect]::new(0, 0, $Source.PixelWidth, $Source.PixelHeight)
    }

    # 放大裁切：取一个以脸部锚点为中心的方形区域，边长 = 原边长 / 放大倍数。
    $side = [Math]::Min($Source.PixelWidth, $Source.PixelHeight) / $smallFaceZoom
    $anchorX = $Source.PixelWidth * $smallFaceAnchorX
    $anchorY = $Source.PixelHeight * $smallFaceAnchorY
    $x = [Math]::Min([Math]::Max(0, $anchorX - $side / 2), $Source.PixelWidth - $side)
    $y = [Math]::Min([Math]::Max(0, $anchorY - $side / 2), $Source.PixelHeight - $side)
    return [System.Windows.Rect]::new($x, $y, $side, $side)
}

function New-DshIconBitmap {
    param(
        [Parameter(Mandatory)][int] $Size,
        [Parameter(Mandatory)][System.Windows.Media.Imaging.BitmapSource] $Source
    )

    $small = $Size -le $smallSizeThreshold
    $sourceRect = Get-DshSourceRect -Source $Source -FaceCrop $small
    $renderSize = $Size * $supersampleFactor

    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()

    # 遮罩按最终画布比例生成，圆角随超采样一起放大，保证边缘同样是高分辨率渲染。
    $radius = $renderSize * $cornerRadiusRatio
    $clip = [System.Windows.Media.RectangleGeometry]::new(
        [System.Windows.Rect]::new(0, 0, $renderSize, $renderSize), $radius, $radius)
    $clip.Freeze()
    $context.PushClip($clip)

    # DrawingContext.DrawImage 只有 (image, rect) 两参重载，没有源矩形裁切版本；
    # 所以裁切靠变换完成：先按裁切区左上角平移，再整体缩放到目标画布。
    $scale = $renderSize / $sourceRect.Width
    $transform = [System.Windows.Media.TransformGroup]::new()
    $transform.Children.Add([System.Windows.Media.TranslateTransform]::new(
        -$sourceRect.X, -$sourceRect.Y))
    $transform.Children.Add([System.Windows.Media.ScaleTransform]::new($scale, $scale))
    $context.PushTransform($transform)
    $context.DrawImage($Source, [System.Windows.Rect]::new(
        0, 0, $Source.PixelWidth, $Source.PixelHeight))
    $context.Pop()
    $context.Pop()
    $context.Close()

    $sharp = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $renderSize, $renderSize, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $sharp.Render($visual)

    # 降采样挂在 DrawingVisual 上（RenderOptions 需要 DependencyObject），而非 DrawingContext。
    $finalVisual = [System.Windows.Media.DrawingVisual]::new()
    [System.Windows.Media.RenderOptions]::SetBitmapScalingMode(
        $finalVisual, [System.Windows.Media.BitmapScalingMode]::HighQuality)
    $finalContext = $finalVisual.RenderOpen()
    $finalContext.DrawImage($sharp, [System.Windows.Rect]::new(0, 0, $Size, $Size))
    $finalContext.Close()

    $final = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $final.Render($finalVisual)
    return $final
}

function ConvertTo-DshPngBytes {
    param([Parameter(Mandatory)][System.Windows.Media.Imaging.BitmapSource] $Image)

    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($Image))
    $stream = [System.IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        return $stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function ConvertTo-DshIcoDibBytes {
    param([Parameter(Mandatory)][System.Windows.Media.Imaging.BitmapSource] $Image)

    $width = $Image.PixelWidth
    $height = $Image.PixelHeight
    $sourceStride = $width * 4
    $premultiplied = New-Object byte[] ($sourceStride * $height)
    $Image.CopyPixels($premultiplied, $sourceStride, 0)

    $pixelStride = $width * 4
    $pixels = New-Object byte[] ($pixelStride * $height)
    for ($row = 0; $row -lt $height; $row++) {
        # ICO 的 DIB 是自底向上行序。
        $targetRow = $height - 1 - $row
        for ($column = 0; $column -lt $width; $column++) {
            $sourceIndex = $row * $sourceStride + $column * 4
            $blue = $premultiplied[$sourceIndex]
            $green = $premultiplied[$sourceIndex + 1]
            $red = $premultiplied[$sourceIndex + 2]
            $alpha = $premultiplied[$sourceIndex + 3]

            if ($alpha -gt 0 -and $alpha -lt 255) {
                $red = [Math]::Min(255, [Math]::Round($red * 255 / $alpha))
                $green = [Math]::Min(255, [Math]::Round($green * 255 / $alpha))
                $blue = [Math]::Min(255, [Math]::Round($blue * 255 / $alpha))
            }
            elseif ($alpha -eq 0) {
                $red = 0
                $green = 0
                $blue = 0
            }

            $targetIndex = $targetRow * $pixelStride + $column * 4
            $pixels[$targetIndex] = $blue
            $pixels[$targetIndex + 1] = $green
            $pixels[$targetIndex + 2] = $red
            $pixels[$targetIndex + 3] = $alpha
        }
    }

    $maskRowBytes = ([Math]::Floor(($width + 31) / 32)) * 4
    $mask = New-Object byte[] ($maskRowBytes * $height)
    for ($row = 0; $row -lt $height; $row++) {
        $targetRow = $height - 1 - $row
        for ($column = 0; $column -lt $width; $column++) {
            $alpha = $premultiplied[$row * $sourceStride + $column * 4 + 3]
            if ($alpha -eq 0) {
                $maskByte = $targetRow * $maskRowBytes + [Math]::Floor($column / 8)
                $mask[$maskByte] = $mask[$maskByte] -bor (0x80 -shr ($column % 8))
            }
        }
    }

    $header = New-Object byte[] 40
    [BitConverter]::GetBytes([uint32] 40).CopyTo($header, 0)
    [BitConverter]::GetBytes([int32] $width).CopyTo($header, 4)
    [BitConverter]::GetBytes([int32] ($height * 2)).CopyTo($header, 8)
    [BitConverter]::GetBytes([uint16] 1).CopyTo($header, 12)
    [BitConverter]::GetBytes([uint16] 32).CopyTo($header, 14)
    [BitConverter]::GetBytes([uint32] 0).CopyTo($header, 16)

    # 必须拼成单一 byte[]；@($header) + ... 会得到 object[]，写入时只剩零星字节。
    $dib = New-Object byte[] ($header.Length + $pixels.Length + $mask.Length)
    [Buffer]::BlockCopy($header, 0, $dib, 0, $header.Length)
    [Buffer]::BlockCopy($pixels, 0, $dib, $header.Length, $pixels.Length)
    [Buffer]::BlockCopy($mask, 0, $dib, ($header.Length + $pixels.Length), $mask.Length)
    return $dib
}

function New-DshIconFile {
    param([Parameter(Mandatory)][pscustomobject[]] $Frames)

    $ordered = @($Frames | Sort-Object Size)
    $entryCount = $ordered.Count
    $offset = 6 + (16 * $entryCount)
    foreach ($frame in $ordered) {
        $frame.Bytes = [byte[]] $frame.Bytes
        $frame | Add-Member -NotePropertyName ImageOffset -NotePropertyValue $offset -Force
        $offset += $frame.Bytes.Length
    }

    $stream = [System.IO.MemoryStream]::new()
    try {
        $writer = [System.IO.BinaryWriter]::new($stream)
        try {
            $writer.Write([uint16] 0)
            $writer.Write([uint16] 1)
            $writer.Write([uint16] $entryCount)
            foreach ($frame in $ordered) {
                # 256px 在目录项里记 0；必须强转 byte，否则 PowerShell 会选中 Write(int) 写足 4 字节。
                $dimension = if ($frame.Size -ge 256) { [byte] 0 } else { [byte] $frame.Size }
                $writer.Write($dimension)
                $writer.Write($dimension)
                $writer.Write([byte] 0)
                $writer.Write([byte] 0)
                $writer.Write([uint16] 1)
                $writer.Write([uint16] 32)
                $writer.Write([uint32] $frame.Bytes.Length)
                $writer.Write([uint32] $frame.ImageOffset)
            }
            foreach ($frame in $ordered) {
                $writer.Write($frame.Bytes)
            }
            $writer.Flush()
        }
        finally {
            $writer.Dispose()
        }

        return $stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

try {
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "mascot 源图不存在：$sourcePath"
    }

    $requestedSizes = @($Sizes | Sort-Object -Unique)
    if ($requestedSizes.Count -eq 0 -or @($requestedSizes | Where-Object { $_ -lt 16 -or $_ -gt 256 }).Count -gt 0) {
        throw '-Sizes 必须落在 16..256 之间且非空。'
    }

    # BitmapCacheOption.OnLoad：先把像素整体读入并冻结，避免文件句柄悬挂影响后续 -Verify。
    $decoded = [System.Windows.Media.Imaging.BitmapFrame]::Create(
        [Uri]::new($sourcePath, [UriKind]::Absolute))
    $source = [System.Windows.Media.Imaging.FormatConvertedBitmap]::new(
        $decoded, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $source.Freeze()
    if ($source.PixelWidth -ne $source.PixelHeight) {
        throw "mascot 源图必须是正方形，实际 $($source.PixelWidth) x $($source.PixelHeight)。"
    }

    $targetRoot = if ($Verify) {
        Join-Path ([IO.Path]::GetTempPath()) "dsh-app-icon-verify-$([Guid]::NewGuid().ToString('N'))"
    }
    else {
        $OutputDirectory
    }
    $pngDirectory = Join-Path $targetRoot 'png'
    $null = New-Item -ItemType Directory -Path $pngDirectory -Force
    $iconPath = Join-Path $targetRoot 'DshWindowsLauncher.ico'

    $frames = [System.Collections.Generic.List[object]]::new()
    foreach ($size in $requestedSizes) {
        $bitmap = New-DshIconBitmap -Size $size -Source $source
        $pngBytes = ConvertTo-DshPngBytes -Image $bitmap
        $payload = if ($size -le 64) {
            ConvertTo-DshIcoDibBytes -Image $bitmap
        }
        else {
            $pngBytes
        }
        $frames.Add([pscustomobject]@{
                Size = $size
                Bytes = $payload
            })
        Write-DshBytes -Path (Join-Path $pngDirectory ("app-{0}.png" -f $size)) -Bytes $pngBytes
    }

    Write-DshBytes -Path $iconPath -Bytes (New-DshIconFile -Frames $frames)
    $frameCount = Assert-DshIconFile -Path $iconPath
    if ($frameCount -ne $requestedSizes.Count) {
        throw "图标帧数与请求尺寸数不一致：$frameCount / $($requestedSizes.Count)。"
    }

    # 输出清单用相对 $targetRoot 的名字，这样 -Verify 写到临时目录时也能逐个对回仓库里的已提交产物。
    $outputs = [System.Collections.Generic.List[object]]::new()
    foreach ($file in @(Get-ChildItem -LiteralPath $targetRoot -File -Recurse)) {
        $relative = [IO.Path]::GetRelativePath($targetRoot, $file.FullName)
        $committedPath = if ($Verify) {
            Join-Path $OutputDirectory $relative
        }
        else {
            $file.FullName
        }
        $outputs.Add([pscustomobject]@{
                Name = $relative.Replace('\', '/')
                CommittedPath = $committedPath
                Sha256 = Get-DshSha256 -Path $file.FullName
                Bytes = $file.Length
            })
    }

    if ($Verify) {
        try {
            $drift = [System.Collections.Generic.List[string]]::new()
            foreach ($output in $outputs) {
                if (-not (Test-Path -LiteralPath $output.CommittedPath -PathType Leaf)) {
                    $drift.Add("缺少已提交产物：$($output.Name)")
                    continue
                }
                if ((Get-DshSha256 -Path $output.CommittedPath) -cne $output.Sha256) {
                    $drift.Add("已提交产物与生成结果不一致：$($output.Name)")
                }
            }
            $presentNames = @(
                $outputs |
                    Where-Object { Test-Path -LiteralPath $_.CommittedPath -PathType Leaf } |
                    ForEach-Object { $_.Name } |
                    Sort-Object
            )
            if (($presentNames -join '|') -cne (@($outputs.Name | Sort-Object) -join '|')) {
                $drift.Add('已提交产物集合与生成器输出集合不一致。')
            }
            if ($drift.Count -gt 0) {
                throw "应用图标已漂移：`n$($drift -join [Environment]::NewLine)"
            }
            Write-Host "make-app-icon.ps1: VERIFY PASS（$($outputs.Count) 个产物逐字节一致）"
        }
        finally {
            Remove-Item -LiteralPath $targetRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    else {
        Write-Host "make-app-icon.ps1: PASS -> $iconPath"
        Write-Host ("  source {0}x{0}  小尺寸档 <= {1}px 放大 {2}x 裁切" -f
            $source.PixelWidth, $smallSizeThreshold, $smallFaceZoom)
        foreach ($output in $outputs) {
            Write-Host ("  {0}  {1}  {2} bytes" -f $output.Name, $output.Sha256, $output.Bytes)
        }
    }
}
catch {
    Write-Error "make-app-icon.ps1: FAIL`n$($_.Exception.Message)"
    exit 1
}
