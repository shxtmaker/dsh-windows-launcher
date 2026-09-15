using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;

namespace DshLauncher.Core.Attachments;

/// <summary>PNG 适配结果。失败时 <see cref="Code"/> 是确定码；成功时携带确定性编码出的 PNG 与 SHA-256。</summary>
public sealed record ScreenshotPngResult
{
    public required bool Ok { get; init; }

    public string? Code { get; init; }

    public string? Detail { get; init; }

    /// <summary>编码后的 PNG 字节（失败时为 <c>null</c>）。</summary>
    public byte[]? Png { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>像素数（用于回执与限额记录）。</summary>
    public long PixelCount { get; init; }

    /// <summary>PNG 的 SHA-256（小写十六进制），与暂存/桥回执使用同一形式。</summary>
    public string? Sha256 { get; init; }
}

/// <summary>
/// D16 截图 PNG 适配器（平台中立，纯托管，无 P/Invoke、无第三方依赖）。
///
/// 输入是剪贴板位图的 DIB 字节（Windows 侧在 STA 之外复制出来），输出是可直接进入
/// D14 暂存与草稿通路的 PNG：
/// <list type="bullet">
/// <item><b>先判定后分配</b>：尺寸/像素/字节护栏在 <see cref="ScreenshotPngPolicy"/> 里先算，
/// 荒谬尺寸不会走到任何缓冲区分配；</item>
/// <item><b>确定性编码</b>：固定 PNG 结构（IHDR + 单块 IDAT + IEND，无辅助块）、
/// 每行固定滤波类型 0、固定压缩级别、CRC-32 自带实现；同一输入必得同一字节串与同一 SHA-256；</item>
/// <item><b>确定的拒绝码</b>：畸形 DIB、不支持的像素格式、超限分别有独立码，不抛异常；</item>
/// <item>32bpp 无 alpha 掩码时按"全 0 alpha 视为不透明"的固定规则处理，
/// 因为 GDI 截图与浏览器复制图片都用 0 填 alpha 通道。</item>
/// </list>
/// </summary>
public static class ScreenshotPngEncoder
{
    /// <summary>剪贴板 DIB 数据畸形（截断、头部自相矛盾）。</summary>
    public const string MalformedCode = "clipboard-malformed";

    /// <summary>PNG 结构常量：1 字节滤波类型 + RGBA。</summary>
    private const int FilterBytesPerRow = 1;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>把 CF_DIB/CF_DIBV5 字节解码为 RGBA 并确定性编码成 PNG。</summary>
    public static ScreenshotPngResult EncodeDib(ReadOnlySpan<byte> dib, AttachmentLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        var header = DibHeader.Parse(dib);
        if (!header.Ok)
        {
            return Fail(header.Code!, header.Detail!);
        }

        var dimension = ScreenshotPngPolicy.EvaluateDimensions(header.Width, header.Height, limits);
        if (!dimension.Allowed)
        {
            return Fail(dimension.Code!, dimension.Detail!);
        }

        var required = (long)header.DataOffset + ((long)header.Stride * header.Height);
        if (required > dib.Length)
        {
            return Fail(
                MalformedCode,
                $"DIB 数据被截断：需要 {required} 字节，实际 {dib.Length} 字节");
        }

        byte[] rgba;
        try
        {
            rgba = new byte[dimension.RawBytes];
        }
        catch (OutOfMemoryException error)
        {
            return Fail(ScreenshotPngPolicy.EncodeFailedCode, "分配截图缓冲区失败：" + error.Message);
        }

        header.ConvertToRgba(dib, rgba);
        return EncodeRgba(rgba, header.Width, header.Height, limits);
    }

    /// <summary>
    /// 把已经排好序的 BGRA 像素（<paramref name="stride"/> 字节一行）编码成 PNG。
    /// 供平台半区在把 DIB 复制成紧凑缓冲后直接使用；同样的护栏与确定性保证。
    /// </summary>
    public static ScreenshotPngResult EncodeBgra32(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        int stride,
        AttachmentLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        var dimension = ScreenshotPngPolicy.EvaluateDimensions(width, height, limits);
        if (!dimension.Allowed)
        {
            return Fail(dimension.Code!, dimension.Detail!);
        }

        if (stride < width * ScreenshotPngPolicy.BytesPerPixel
            || (long)stride * height > bgra.Length)
        {
            return Fail(
                MalformedCode,
                $"BGRA 缓冲长度不足：stride={stride} height={height} length={bgra.Length}");
        }

        byte[] rgba;
        try
        {
            rgba = new byte[dimension.RawBytes];
        }
        catch (OutOfMemoryException error)
        {
            return Fail(ScreenshotPngPolicy.EncodeFailedCode, "分配截图缓冲区失败：" + error.Message);
        }

        var opaque = IsAlphaAllZero(bgra, stride, width, height);
        for (var y = 0; y < height; y++)
        {
            var source = bgra.Slice(y * stride, width * ScreenshotPngPolicy.BytesPerPixel);
            var target = rgba.AsSpan((y * ((width * ScreenshotPngPolicy.BytesPerPixel) + FilterBytesPerRow)) + FilterBytesPerRow);
            for (var x = 0; x < width; x++)
            {
                var pixel = source.Slice(x * 4, 4);
                target[(x * 4) + 0] = pixel[2];
                target[(x * 4) + 1] = pixel[1];
                target[(x * 4) + 2] = pixel[0];
                target[(x * 4) + 3] = opaque ? (byte)255 : pixel[3];
            }
        }

        return EncodeRgba(rgba, width, height, limits);
    }

    private static ScreenshotPngResult EncodeRgba(byte[] rgba, int width, int height, AttachmentLimits limits)
    {
        byte[] png;
        try
        {
            png = WritePng(rgba, width, height);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or NotSupportedException)
        {
            return Fail(ScreenshotPngPolicy.EncodeFailedCode, "PNG 编码失败：" + error.Message);
        }

        if (png.Length > limits.MaxFileBytes)
        {
            return Fail(
                "limit-file-bytes",
                $"编码后 PNG {png.Length} 字节超过单文件上限 {limits.MaxFileBytes}");
        }

        return new ScreenshotPngResult
        {
            Ok = true,
            Png = png,
            Width = width,
            Height = height,
            PixelCount = (long)width * height,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(png)),
        };
    }

    private static byte[] WritePng(byte[] rgba, int width, int height)
    {
        using var output = new MemoryStream();
        output.Write(PngSignature);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8] = 8;  // 位深
        ihdr[9] = 6;  // 颜色类型：真彩 + alpha
        ihdr[10] = 0; // 压缩方法：deflate
        ihdr[11] = 0; // 滤波方法：自适应（每一行显式写 0 = None）
        ihdr[12] = 0; // 非隔行
        WriteChunk(output, "IHDR"u8, ihdr);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(rgba);
        }

        WriteChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);

        var crc = Crc32(type);
        crc = Crc32(data, crc);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint Crc32(ReadOnlySpan<byte> data, uint seed = 0xFFFFFFFFu)
    {
        var crc = seed;
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var index = 0; index < 256; index++)
        {
            var value = (uint)index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }

    private static bool IsAlphaAllZero(ReadOnlySpan<byte> bgra, int stride, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * stride, width * ScreenshotPngPolicy.BytesPerPixel);
            for (var x = 3; x < row.Length; x += 4)
            {
                if (row[x] != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static ScreenshotPngResult Fail(string code, string detail) =>
        new() { Ok = false, Code = code, Detail = detail };

    /// <summary>
    /// 解析后的 DIB 头部事实。只支持 BITMAPINFOHEADER 族的 24/32bpp BI_RGB 与 BI_BITFIELDS：
    /// 剪贴板截图（GDI/浏览器复制）只会产生这几种形态，其余形态给出确定的不支持码。
    /// </summary>
    private readonly struct DibHeader
    {
        private DibHeader(
            bool ok,
            string? code,
            string? detail,
            int width,
            int height,
            int stride,
            int dataOffset,
            int bitsPerPixel,
            bool topDown,
            uint redMask,
            uint greenMask,
            uint blueMask,
            uint alphaMask,
            bool alphaIsImplicitZero)
        {
            Ok = ok;
            Code = code;
            Detail = detail;
            Width = width;
            Height = height;
            Stride = stride;
            DataOffset = dataOffset;
            BitsPerPixel = bitsPerPixel;
            TopDown = topDown;
            RedMask = redMask;
            GreenMask = greenMask;
            BlueMask = blueMask;
            AlphaMask = alphaMask;
            AlphaIsImplicitZero = alphaIsImplicitZero;
        }

        public bool Ok { get; }

        public string? Code { get; }

        public string? Detail { get; }

        public int Width { get; }

        public int Height { get; }

        public int Stride { get; }

        public int DataOffset { get; }

        public int BitsPerPixel { get; }

        public bool TopDown { get; }

        public uint RedMask { get; }

        public uint GreenMask { get; }

        public uint BlueMask { get; }

        public uint AlphaMask { get; }

        /// <summary>32bpp BI_RGB：alpha 通道未定义，按"全 0 视为不透明"的固定规则处理。</summary>
        public bool AlphaIsImplicitZero { get; }

        public static DibHeader Parse(ReadOnlySpan<byte> dib)
        {
            if (dib.Length < 40)
            {
                return Unsupported(MalformedCode, $"DIB 头部不足 40 字节（实际 {dib.Length}）");
            }

            var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib[..4]);
            if (headerSize is not (40 or 52 or 56 or 108 or 124))
            {
                return Unsupported(
                    ScreenshotPngPolicy.PixelFormatUnsupportedCode,
                    $"不支持的 DIB 头长度 {headerSize}（只支持 BITMAPINFOHEADER/V4/V5）");
            }

            if (headerSize > dib.Length)
            {
                return Unsupported(MalformedCode, $"DIB 头声明 {headerSize} 字节，实际只有 {dib.Length}");
            }

            var width = BinaryPrimitives.ReadInt32LittleEndian(dib.Slice(4, 4));
            var heightField = BinaryPrimitives.ReadInt32LittleEndian(dib.Slice(8, 4));
            var planes = BinaryPrimitives.ReadUInt16LittleEndian(dib.Slice(12, 2));
            var bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(dib.Slice(14, 2));
            var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(16, 4));

            if (planes != 1)
            {
                return Unsupported(MalformedCode, $"DIB 平面数 {planes} 非法");
            }

            if (width <= 0 || heightField == 0)
            {
                return Unsupported(MalformedCode, $"DIB 尺寸非法：{width}x{heightField}");
            }

            if (bitsPerPixel is not (24 or 32))
            {
                return Unsupported(
                    ScreenshotPngPolicy.PixelFormatUnsupportedCode,
                    $"不支持的位深 {bitsPerPixel}（只支持 24/32bpp）");
            }

            if (compression is not (0 or 3))
            {
                return Unsupported(
                    ScreenshotPngPolicy.PixelFormatUnsupportedCode,
                    $"不支持的 DIB 压缩方式 {compression}（只支持 BI_RGB/BI_BITFIELDS）");
            }

            var topDown = heightField < 0;
            var height = Math.Abs(heightField);
            var stride = ((width * bitsPerPixel) + 31) / 32 * 4;
            var maskOffset = (int)headerSize;

            uint redMask;
            uint greenMask;
            uint blueMask;
            uint alphaMask = 0;
            var implicitAlpha = false;

            if (compression == 3)
            {
                if (headerSize >= 52)
                {
                    redMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(40, 4));
                    greenMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(44, 4));
                    blueMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(48, 4));
                    if (headerSize >= 56)
                    {
                        alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(52, 4));
                    }
                }
                else
                {
                    // BITMAPINFOHEADER + BI_BITFIELDS：3 个 DWORD 掩码紧跟头部。
                    if (dib.Length < 52)
                    {
                        return Unsupported(MalformedCode, "BI_BITFIELDS 掩码缺失");
                    }

                    redMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(40, 4));
                    greenMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(44, 4));
                    blueMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(48, 4));
                    maskOffset = 52;
                }

                if (!MasksAreValid(bitsPerPixel, redMask, greenMask, blueMask, alphaMask))
                {
                    return Unsupported(
                        ScreenshotPngPolicy.PixelFormatUnsupportedCode,
                        $"不支持的通道掩码 R={redMask:X8} G={greenMask:X8} B={blueMask:X8} A={alphaMask:X8}");
                }
            }
            else if (bitsPerPixel == 32)
            {
                redMask = 0x00FF0000;
                greenMask = 0x0000FF00;
                blueMask = 0x000000FF;
                implicitAlpha = true;
            }
            else
            {
                redMask = 0x00FF0000;
                greenMask = 0x0000FF00;
                blueMask = 0x000000FF;
            }

            return new DibHeader(
                true,
                null,
                null,
                width,
                height,
                stride,
                maskOffset,
                bitsPerPixel,
                topDown,
                redMask,
                greenMask,
                blueMask,
                alphaMask,
                implicitAlpha);
        }

        private static bool MasksAreValid(int bitsPerPixel, uint red, uint green, uint blue, uint alpha)
        {
            var all = red | green | blue | alpha;
            if (red == 0 || green == 0 || blue == 0)
            {
                return false;
            }

            if ((red & green) != 0 || (red & blue) != 0 || (green & blue) != 0)
            {
                return false;
            }

            if (alpha != 0 && ((alpha & red) != 0 || (alpha & green) != 0 || (alpha & blue) != 0))
            {
                return false;
            }

            var limit = bitsPerPixel == 32 ? uint.MaxValue : 0x00FFFFFFu;
            return (all & ~limit) == 0 && PopCount(red) <= 8 && PopCount(green) <= 8 && PopCount(blue) <= 8
                && PopCount(alpha) <= 8;
        }

        private static int PopCount(uint value) => BitOperations.PopCount(value);

        /// <summary>把 DIB 像素转换成紧凑 RGBA（含每行 1 字节 PNG 滤波类型 0）。</summary>
        public void ConvertToRgba(ReadOnlySpan<byte> dib, byte[] rgba)
        {
            var rowBytes = Width * ScreenshotPngPolicy.BytesPerPixel;
            // 只扫描像素区：DIB 头里必然有非零字节，从头扫会把"无 alpha"误判成"有 alpha"。
            var usesImplicitAlpha = AlphaIsImplicitZero
                && IsAlphaAllZero(dib.Slice(DataOffset), Stride, Width, Height);
            for (var y = 0; y < Height; y++)
            {
                var sourceRow = TopDown ? y : Height - 1 - y;
                var source = dib.Slice(DataOffset + (sourceRow * Stride), rowBytes);
                var target = rgba.AsSpan((y * (rowBytes + FilterBytesPerRow)) + FilterBytesPerRow, rowBytes);
                for (var x = 0; x < Width; x++)
                {
                    var offset = x * (BitsPerPixel / 8);
                    var value = BitsPerPixel == 32
                        ? BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, 4))
                        : (uint)(source[offset] | (source[offset + 1] << 8) | (source[offset + 2] << 16));
                    target[(x * 4) + 0] = Extract(value, RedMask);
                    target[(x * 4) + 1] = Extract(value, GreenMask);
                    target[(x * 4) + 2] = Extract(value, BlueMask);
                    target[(x * 4) + 3] = AlphaOf(value, usesImplicitAlpha);
                }
            }
        }

        /// <summary>
        /// alpha 取值规则（确定性）：有 alpha 掩码就用掩码；32bpp 无掩码（BI_RGB）时用第 4 字节，
        /// 但整幅全 0 则视为不透明（GDI 截图与浏览器复制图片的常见形态）；其余（24bpp）一律不透明。
        /// </summary>
        private byte AlphaOf(uint value, bool opaqueBecauseAlphaIsAllZero)
        {
            if (AlphaMask != 0)
            {
                return Extract(value, AlphaMask);
            }

            if (!AlphaIsImplicitZero)
            {
                return 255;
            }

            return opaqueBecauseAlphaIsAllZero ? (byte)255 : (byte)((value >> 24) & 0xFF);
        }

        private static byte Extract(uint value, uint mask)
        {
            if (mask == 0)
            {
                return 255;
            }

            var shift = BitOperations.TrailingZeroCount(mask);
            var raw = (value & mask) >> shift;
            var max = (1u << BitOperations.PopCount(mask)) - 1;
            return (byte)((raw * 255u) / max);
        }

        private static bool IsAlphaAllZero(ReadOnlySpan<byte> pixels, int stride, int width, int height)
        {
            for (var y = 0; y < height; y++)
            {
                var row = pixels.Slice(y * stride, width * 4);
                for (var x = 3; x < row.Length; x += 4)
                {
                    if (row[x] != 0)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static DibHeader Unsupported(string code, string detail) =>
            new(false, code, detail, 0, 0, 0, 0, 0, false, 0, 0, 0, 0, false);
    }
}
