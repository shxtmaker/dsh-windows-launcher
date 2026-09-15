using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DshLauncher.Core.Attachments;
using Xunit;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>
/// D16 截图 PNG 适配器的平台中立测试（Linux 真实执行）：尺寸/像素/字节护栏先于分配、
/// DIB 解析与 alpha 规则、确定性编码（同一输入必得同一字节串与同一 SHA-256）。
/// 真实剪贴板位图（CF_DIB/CF_DIBV5 的取得与所有权）属 WindowsPending。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
public sealed class ScreenshotPngAdapterTests
{
    private static readonly AttachmentLimits Limits = new();

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Theory]
    [InlineData(0, 10, ScreenshotPngPolicy.DimensionsInvalidCode)]
    [InlineData(10, 0, ScreenshotPngPolicy.DimensionsInvalidCode)]
    [InlineData(-3, 10, ScreenshotPngPolicy.DimensionsInvalidCode)]
    [InlineData(ScreenshotPngPolicy.MaxDimension + 1, 10, ScreenshotPngPolicy.DimensionsInvalidCode)]
    [InlineData(10, ScreenshotPngPolicy.MaxDimension + 1, ScreenshotPngPolicy.DimensionsInvalidCode)]
    [InlineData(8_001, 5_000, "limit-screenshot-pixels")]
    public void DimensionsAndThePixelCapAreGuardedBeforeAnyAllocation(int width, int height, string expectedCode)
    {
        var decision = ScreenshotPngPolicy.EvaluateDimensions(width, height, Limits);

        Assert.False(decision.Allowed);
        Assert.Equal(expectedCode, decision.Code);
        Assert.Equal(0, decision.RawBytes);
    }

    [Fact]
    public void TheFrozenPixelCapBoundaryIsAcceptedAndItsRawSizeIsExact()
    {
        var decision = ScreenshotPngPolicy.EvaluateDimensions(8_000, 5_000, Limits);

        Assert.True(decision.Allowed);
        Assert.Equal(AttachmentProtocol.MaxScreenshotPixels, decision.PixelCount);
        Assert.Equal(32_000, decision.StrideBytes);
        Assert.Equal((AttachmentProtocol.MaxScreenshotPixels * 4) + 5_000, decision.RawBytes);
    }

    [Fact]
    public void TheEncodedUpperBoundGrowsWithThePayload()
    {
        var small = ScreenshotPngPolicy.PngUpperBound(1_000);
        var large = ScreenshotPngPolicy.PngUpperBound(1_000_000);

        Assert.True(large > small);
        Assert.True(small > 1_000);
    }

    [Fact]
    public void EncodingTheSameDibTwiceYieldsIdenticalBytesAndHash()
    {
        var dib = BuildDib32(4, 3);

        var first = ScreenshotPngEncoder.EncodeDib(dib, Limits);
        var second = ScreenshotPngEncoder.EncodeDib(dib, Limits);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal(first.Png, second.Png);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(first.Png!)), first.Sha256);
        Assert.Equal(64, first.Sha256!.Length);
    }

    [Fact]
    public void TheEncodedPngHasTheFrozenStructure()
    {
        var encoded = ScreenshotPngEncoder.EncodeDib(BuildDib32(3, 2), Limits);

        Assert.True(encoded.Ok);
        var png = encoded.Png!;
        Assert.Equal(3, encoded.Width);
        Assert.Equal(2, encoded.Height);
        Assert.Equal(6, encoded.PixelCount);
        Assert.Equal(PngSignature, png[..8]);
        Assert.Equal("IHDR", Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)));
        Assert.Equal(8, png[24]);   // 位深
        Assert.Equal(6, png[25]);   // 真彩 + alpha
        Assert.Equal(0, png[26]);   // deflate
        Assert.Equal(0, png[27]);   // 自适应滤波（每行显式 0）
        Assert.Equal(0, png[28]);   // 非隔行

        // 只有一个 IDAT，块顺序固定，每个块的 CRC 都自洽。
        Assert.Equal(["IHDR", "IDAT", "IEND"], ChunkTypes(png));
        Assert.True(ChunkCrcIsValid(png, 8, out var afterIhdr));
        Assert.True(ChunkCrcIsValid(png, afterIhdr, out var afterIdat));
        Assert.True(ChunkCrcIsValid(png, afterIdat, out var end));
        Assert.Equal(png.Length, end);
    }

    [Fact]
    public void TheEncodedRowsCarryFilterTypeZeroAndTheExpectedRgbaPixels()
    {
        var encoded = ScreenshotPngEncoder.EncodeDib(BuildDib32(2, 2), Limits);

        Assert.True(encoded.Ok);
        var raw = DecodeFirstIdat(encoded.Png!);

        // 每行 1 字节滤波类型 + 2 像素 × 4 字节；bottom-up 的下一行先出现在 PNG 里。
        Assert.Equal((2 * 4 + 1) * 2, raw.Length);
        Assert.Equal(0, raw[0]);
        Assert.Equal(0, raw[9]);
        Assert.Equal([0x31, 0x21, 0x11, 0xFF], raw[1..5]);
        Assert.Equal([0x32, 0x22, 0x12, 0xFF], raw[5..9]);
        Assert.Equal([0x30, 0x20, 0x10, 0xFF], raw[10..14]);
    }

    [Fact]
    public void TheSameVisibleImageEncodesIdenticallyFromBottomUpAndTopDownDibs()
    {
        var bottomUp = BuildDib32(3, 2);
        var topDown = BuildDib32(3, 2);
        BinaryPrimitives.WriteInt32LittleEndian(topDown.AsSpan(8, 4), -2);

        // top-down：内存第一行就是显示的第一行，因此把两份行序反过来。
        const int stride = 3 * 4;
        var row0 = topDown.AsSpan(40, stride).ToArray();
        var row1 = topDown.AsSpan(40 + stride, stride).ToArray();
        row1.CopyTo(topDown.AsSpan(40, stride));
        row0.CopyTo(topDown.AsSpan(40 + stride, stride));

        var bottomUpPng = ScreenshotPngEncoder.EncodeDib(bottomUp, Limits);
        var topDownPng = ScreenshotPngEncoder.EncodeDib(topDown, Limits);

        Assert.True(bottomUpPng.Ok);
        Assert.True(topDownPng.Ok);
        Assert.Equal(bottomUpPng.Sha256, topDownPng.Sha256);
        Assert.Equal(bottomUpPng.Png, topDownPng.Png);
    }

    [Fact]
    public void A32BitDibWithAllZeroAlphaIsTreatedAsOpaque()
    {
        var encoded = ScreenshotPngEncoder.EncodeDib(BuildDib32(1, 1, alpha: 0), Limits);

        Assert.True(encoded.Ok);
        Assert.Equal([0x30, 0x20, 0x10, 0xFF], DecodeFirstIdat(encoded.Png!)[1..5]);
    }

    [Fact]
    public void A32BitDibWithRealAlphaKeepsIt()
    {
        var encoded = ScreenshotPngEncoder.EncodeDib(BuildDib32(1, 1, alpha: 0x7F), Limits);

        Assert.True(encoded.Ok);
        Assert.Equal([0x30, 0x20, 0x10, 0x7F], DecodeFirstIdat(encoded.Png!)[1..5]);
    }

    [Fact]
    public void A24BitDibWithRowPaddingIsConvertedRowByRow()
    {
        // 3 像素 × 3 字节 = 9 字节，按 4 字节对齐每行补 3 字节填充。
        var encoded = ScreenshotPngEncoder.EncodeDib(BuildDib24(3, 1), Limits);

        Assert.True(encoded.Ok);
        var raw = DecodeFirstIdat(encoded.Png!);
        Assert.Equal(1 + (3 * 4), raw.Length);
        Assert.Equal([0x30, 0x20, 0x10, 0xFF], raw[1..5]);
        Assert.Equal([0x31, 0x21, 0x11, 0xFF], raw[5..9]);
        Assert.Equal([0x32, 0x22, 0x12, 0xFF], raw[9..13]);
    }

    [Fact]
    public void BitfieldMasksAreHonoured()
    {
        // 掩码 0x000000FF / 0x0000FF00 / 0x00FF0000 / 0xFF000000：内存里本来就是 RGBA。
        var encoded = ScreenshotPngEncoder.EncodeDib(BuildDib32(1, 1, alpha: 0x40, compression: 3), Limits);

        Assert.True(encoded.Ok);
        Assert.Equal([0x10, 0x20, 0x30, 0x40], DecodeFirstIdat(encoded.Png!)[1..5]);
    }

    [Theory]
    [InlineData(0, 0, ScreenshotPngEncoder.MalformedCode)]
    [InlineData(39, 40, ScreenshotPngEncoder.MalformedCode)]
    [InlineData(40, 12, ScreenshotPngPolicy.PixelFormatUnsupportedCode)]
    [InlineData(40, 124, ScreenshotPngEncoder.MalformedCode)]
    public void TruncatedOrForeignHeadersAreRefused(int spanLength, uint declaredHeaderSize, string expectedCode)
    {
        var dib = new byte[Math.Max(spanLength, 40)];
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(0, 4), declaredHeaderSize);

        var encoded = ScreenshotPngEncoder.EncodeDib(dib.AsSpan(0, spanLength), Limits);

        Assert.False(encoded.Ok);
        Assert.Equal(expectedCode, encoded.Code);
        Assert.Null(encoded.Png);
    }

    [Fact]
    public void AnUnsupportedBitDepthIsRefused()
    {
        var dib = BuildDib32(2, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14, 2), 16);

        var encoded = ScreenshotPngEncoder.EncodeDib(dib, Limits);

        Assert.False(encoded.Ok);
        Assert.Equal(ScreenshotPngPolicy.PixelFormatUnsupportedCode, encoded.Code);
    }

    [Fact]
    public void AnUnsupportedCompressionMethodIsRefused()
    {
        var dib = BuildDib32(2, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16, 4), 1);

        var encoded = ScreenshotPngEncoder.EncodeDib(dib, Limits);

        Assert.False(encoded.Ok);
        Assert.Equal(ScreenshotPngPolicy.PixelFormatUnsupportedCode, encoded.Code);
    }

    [Fact]
    public void AnIllegalPlaneCountIsRefusedAsMalformed()
    {
        var dib = BuildDib32(2, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12, 2), 2);

        var encoded = ScreenshotPngEncoder.EncodeDib(dib, Limits);

        Assert.False(encoded.Ok);
        Assert.Equal(ScreenshotPngEncoder.MalformedCode, encoded.Code);
    }

    [Fact]
    public void AZeroWidthDibIsRefusedAsMalformed()
    {
        var dib = BuildDib32(2, 2);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4, 4), 0);

        var encoded = ScreenshotPngEncoder.EncodeDib(dib, Limits);

        Assert.False(encoded.Ok);
        Assert.Equal(ScreenshotPngEncoder.MalformedCode, encoded.Code);
    }

    [Fact]
    public void ATruncatedPixelPayloadIsRefused()
    {
        var dib = BuildDib32(8, 8);
        var truncated = dib.AsSpan(0, dib.Length - 16).ToArray();

        var encoded = ScreenshotPngEncoder.EncodeDib(truncated, Limits);

        Assert.False(encoded.Ok);
        Assert.Equal(ScreenshotPngEncoder.MalformedCode, encoded.Code);
    }

    [Fact]
    public void ADibOverThePixelCapIsRefusedBeforeAllocating()
    {
        var dib = BuildDib32(0, 0, overrideWidth: 9_000, overrideHeight: 5_000, payloadOnly: true);

        var encoded = ScreenshotPngEncoder.EncodeDib(dib, Limits);

        Assert.False(encoded.Ok);
        Assert.Equal("limit-screenshot-pixels", encoded.Code);
        Assert.Null(encoded.Png);
    }

    [Fact]
    public void AnEncodedPngBeyondTheFileByteCapIsRefused()
    {
        var tight = new AttachmentLimits { MaxFileBytes = 32 };

        var encoded = ScreenshotPngEncoder.EncodeDib(BuildDib32(4, 4), tight);

        Assert.False(encoded.Ok);
        Assert.Equal("limit-file-bytes", encoded.Code);
    }

    [Fact]
    public void EncodeBgra32ValidatesTheBufferAndKeepsOpaquePixels()
    {
        var bgra = new byte[2 * 2 * 4];
        bgra[0] = 0x11;
        bgra[1] = 0x22;
        bgra[2] = 0x33;

        var ok = ScreenshotPngEncoder.EncodeBgra32(bgra, 2, 2, 2 * 4, Limits);
        var tooShort = ScreenshotPngEncoder.EncodeBgra32(bgra.AsSpan(0, 8), 2, 2, 2 * 4, Limits);

        Assert.True(ok.Ok);
        Assert.Equal([0x33, 0x22, 0x11, 0xFF], DecodeFirstIdat(ok.Png!)[1..5]);
        Assert.False(tooShort.Ok);
        Assert.Equal(ScreenshotPngEncoder.MalformedCode, tooShort.Code);
    }

    [Fact]
    public void AScreenshotCaptureHasNoPathAndStillGoesThroughTheStagingGuards()
    {
        using var fixture = new StagingFixture();
        var png = ScreenshotPngEncoder.EncodeDib(BuildDib32(2, 2), Limits).Png!;

        var capture = new InMemoryNativeCapture("capture-shot-1", "screenshot.png", png);
        var result = fixture.Service.Capture(capture, TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(png.LongLength, result.ByteLength);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(png)), result.Sha256);
        Assert.Equal("screenshot.png", result.DisplayName);
        Assert.StartsWith(FakeStagingFileSystem.Root + @"\", result.StagedPath, StringComparison.Ordinal);
        Assert.Equal(png, Assert.Single(fixture.FileSystem.Destinations).Written.ToArray());
    }

    [Fact]
    public void TheSyntheticScreenshotOriginSkipsPathRulesButKeepsTheOtherGuards()
    {
        var descriptor = new StagingSourceDescriptor
        {
            Origin = StagingSourceOrigin.NativeScreenshot,
            PathKind = StagingPathKind.Synthetic,
            LeafName = "screenshot.png",
            Kind = StagingSourceKind.RegularFile,
            Length = 10,
            IsReparsePoint = true,
            IsCloudPlaceholder = true,
            IsNetworkShare = true,
        };

        Assert.True(StagingCandidatePolicy.Evaluate(descriptor).Allowed);
        Assert.False(StagingCandidatePolicy.Evaluate(descriptor with { Kind = StagingSourceKind.Device }).Allowed);
        Assert.False(StagingCandidatePolicy.Evaluate(descriptor with { Length = -1 }).Allowed);
    }

    [Fact]
    public void ALocalFileCandidateStillCannotClaimTheSyntheticOrigin()
    {
        var descriptor = StagingFixture.Descriptor(length: 4, pathKind: StagingPathKind.RelativeLeaf);

        Assert.False(StagingCandidatePolicy.Evaluate(descriptor).Allowed);
        Assert.False(StagingCandidatePolicy.Evaluate(descriptor with { PathKind = StagingPathKind.Unc }).Allowed);
    }

    private static byte[] BuildDib32(
        int width,
        int height,
        byte alpha = 0,
        uint compression = 0,
        int? overrideWidth = null,
        int? overrideHeight = null,
        bool payloadOnly = false)
    {
        var headerSize = compression == 3 ? 108 : 40;
        const int maskBytes = 0;
        var stride = width * 4;
        var payloadBytes = payloadOnly ? 0 : stride * height;
        var dib = new byte[headerSize + maskBytes + payloadBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(0, 4), (uint)headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4, 4), overrideWidth ?? width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8, 4), overrideHeight ?? height);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14, 2), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16, 4), compression);
        if (compression == 3)
        {
            // 32bpp BI_BITFIELDS：内存里按 RGBA 排列，便于断言掩码被真正使用。
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(40, 4), 0x000000FF);
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(44, 4), 0x0000FF00);
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(48, 4), 0x00FF0000);
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(52, 4), 0xFF000000);
        }

        if (payloadOnly)
        {
            return dib;
        }

        var pixels = dib.AsSpan(headerSize + maskBytes);
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                var pixel = pixels.Slice((row * stride) + (column * 4), 4);
                pixel[0] = (byte)(0x10 + column + row);   // B
                pixel[1] = (byte)(0x20 + column + row);   // G
                pixel[2] = (byte)(0x30 + column + row);   // R
                pixel[3] = alpha;
            }
        }

        return dib;
    }

    private static byte[] BuildDib24(int width, int height)
    {
        var stride = ((width * 3) + 3) / 4 * 4;
        var dib = new byte[40 + (stride * height)];
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(0, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8, 4), height);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14, 2), 24);
        var pixels = dib.AsSpan(40);
        for (var column = 0; column < width; column++)
        {
            pixels[column * 3] = (byte)(0x10 + column);
            pixels[(column * 3) + 1] = (byte)(0x20 + column);
            pixels[(column * 3) + 2] = (byte)(0x30 + column);
        }

        return dib;
    }

    private static List<string> ChunkTypes(byte[] png)
    {
        var types = new List<string>();
        var offset = 8;
        while (offset + 12 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            types.Add(Encoding.ASCII.GetString(png, offset + 4, 4));
            offset += 12 + length;
        }

        return types;
    }

    private static bool ChunkCrcIsValid(byte[] png, int offset, out int next)
    {
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
        var expected = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length, 4));
        var actual = Crc32(png.AsSpan(offset + 4, 4 + length));
        next = offset + 12 + length;
        return expected == actual;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc;
    }

    private static byte[] DecodeFirstIdat(byte[] png)
    {
        var offset = 8;
        while (offset + 12 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == "IDAT")
            {
                using var source = new MemoryStream(png, offset + 8, length);
                using var zlib = new ZLibStream(source, CompressionMode.Decompress);
                using var output = new MemoryStream();
                zlib.CopyTo(output);
                return output.ToArray();
            }

            offset += 12 + length;
        }

        throw new InvalidOperationException("PNG 里没有 IDAT。");
    }
}
