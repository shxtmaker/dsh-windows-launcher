namespace DshLauncher.Core.Attachments;

/// <summary>位图尺寸/像素/分配判定结果（在分配任何缓冲区之前给出）。</summary>
public sealed record ScreenshotDimensionDecision
{
    public required bool Allowed { get; init; }

    public string? Code { get; init; }

    public string? Detail { get; init; }

    /// <summary>像素数（宽 × 高）。</summary>
    public long PixelCount { get; init; }

    /// <summary>一行 RGBA 的字节数。</summary>
    public long StrideBytes { get; init; }

    /// <summary>转换后 RGBA 载荷字节数（含每行 1 字节 PNG 滤波类型）。</summary>
    public long RawBytes { get; init; }
}

/// <summary>
/// D16 截图限额与尺寸护栏（平台中立纯函数）。冻结限额：
/// 编码前像素数上限 <see cref="AttachmentProtocol.MaxScreenshotPixels"/>（4000 万像素）、
/// 编码后字节上限 <see cref="AttachmentProtocol.MaxFileBytes"/>（20 MiB）。
///
/// 判定顺序固定（宽/高合法性 → PNG 硬上限 → 像素上限），因此既不会为"荒谬尺寸"分配内存，
/// 也不会把超大截图送进编码器之后才发现超限。所有上界都是<b>先算后分配</b>。
/// </summary>
public static class ScreenshotPngPolicy
{
    /// <summary>宽或高非法（≤0，或超出 PNG/实现上界）。</summary>
    public const string DimensionsInvalidCode = "screenshot-dimensions-invalid";

    /// <summary>像素格式不受支持（调色板、16bpp、怪掩码等）。</summary>
    public const string PixelFormatUnsupportedCode = "screenshot-pixel-format-unsupported";

    /// <summary>PNG 编码失败（不改变任何既有状态）。</summary>
    public const string EncodeFailedCode = "screenshot-encode-failed";

    /// <summary>单个维度硬上界（PNG IHDR 为 uint32，实现取更保守的 65535）。</summary>
    public const int MaxDimension = 65_535;

    /// <summary>RGBA 每像素字节数。</summary>
    public const int BytesPerPixel = 4;

    /// <summary>PNG 文件头 + IHDR/IDAT/IEND 与 zlib 包装的固定开销上界。</summary>
    public const int PngFixedOverheadBytes = 512;

    /// <summary>
    /// 护栏：在分配 RGBA 缓冲之前判定。返回的 <see cref="ScreenshotDimensionDecision.RawBytes"/>
    /// 就是随后会被分配的字节数；超出冻结上限时直接拒绝。
    /// </summary>
    public static ScreenshotDimensionDecision EvaluateDimensions(int width, int height, AttachmentLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        if (width <= 0 || height <= 0)
        {
            return Deny(DimensionsInvalidCode, $"位图尺寸非法：{width}x{height}");
        }

        if (width > MaxDimension || height > MaxDimension)
        {
            return Deny(DimensionsInvalidCode, $"位图尺寸 {width}x{height} 超出单维上界 {MaxDimension}");
        }

        var pixels = (long)width * height;
        if (pixels > limits.MaxScreenshotPixels)
        {
            // 复用冻结结果码（D10），不另造同义码。
            return Deny(
                "limit-screenshot-pixels",
                $"位图 {width}x{height} = {pixels} 像素，超过编码前上限 {limits.MaxScreenshotPixels}");
        }

        var stride = (long)width * BytesPerPixel;
        var raw = (pixels * BytesPerPixel) + height;
        return new ScreenshotDimensionDecision
        {
            Allowed = true,
            PixelCount = pixels,
            StrideBytes = stride,
            RawBytes = raw,
        };
    }

    /// <summary>
    /// 编码输出的<b>上界</b>（含每行滤波字节与 deflate 最坏情况开销），
    /// 用于在编码前判断输出是否不可能装进冻结的文件字节上限。
    /// </summary>
    public static long PngUpperBound(long rawBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rawBytes);

        // zlib 存储块最坏情况：每 65535 字节加 5 字节块头，另加 2 字节 zlib 头与 4 字节 Adler-32；
        // 再加 PNG 固定开销。这个上界只会高估，不会低估。
        var storedBlocks = ((rawBytes + 65_534) / 65_535) * 5;
        return rawBytes + storedBlocks + PngFixedOverheadBytes;
    }

    private static ScreenshotDimensionDecision Deny(string code, string detail) =>
        new() { Allowed = false, Code = code, Detail = detail };
}
