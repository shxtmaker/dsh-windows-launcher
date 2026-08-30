using System.Runtime.InteropServices;
using DshLauncher.Compatibility;
using Xunit;

namespace DshLauncher.Compatibility.Tests;

[Trait("triggerTags", "VFY-06,VFY-08")]
public sealed class DescriptorValidationTests
{
    private static readonly Guid TargetId = Guid.Parse(
        "7cb426f9-5234-45cc-bff1-7e4995a91f19");

    [Fact]
    public void MissingDescriptorReturnsBaseSnapshot()
    {
        var resolver = CompatibilityFixture.CreateResolver();

        var resolution = resolver.Resolve(
            TargetId,
            BoundedDescriptorResponse.Missing());

        Assert.Equal(CompatibilityLevel.Base, resolution.Level);
        Assert.Equal(CompatibilityReasonCode.DescriptorMissing, resolution.PrimaryReason);
        Assert.Same(resolution.BaseSnapshot, resolution.Snapshot);
        Assert.Empty(resolution.Snapshot.ExtensionCapabilities);
        Assert.Null(resolution.DescriptorIdentity);
    }

    [Theory]
    [InlineData(404, "application/json; charset=utf-8", false, CompatibilityReasonCode.DescriptorStatusRejected)]
    [InlineData(200, "application/json; charset=utf-8", true, CompatibilityReasonCode.DescriptorRedirected)]
    [InlineData(200, "application/json", false, CompatibilityReasonCode.DescriptorContentTypeRejected)]
    [InlineData(200, "text/json; charset=utf-8", false, CompatibilityReasonCode.DescriptorContentTypeRejected)]
    [InlineData(200, "application/json; charset=utf-8; profile=x", false, CompatibilityReasonCode.DescriptorContentTypeRejected)]
    public void RejectedResponseMetadataReturnsBaseSnapshot(
        int statusCode,
        string contentType,
        bool redirected,
        CompatibilityReasonCode expectedReason)
    {
        var resolver = CompatibilityFixture.CreateResolver();
        var response = BoundedDescriptorResponse.Received(
            statusCode,
            contentType,
            redirected,
            CompatibilityFixture.Descriptor(CompatibilityFixture.Component()));

        var resolution = resolver.Resolve(TargetId, response);

        Assert.Equal(CompatibilityLevel.Base, resolution.Level);
        Assert.Equal(expectedReason, resolution.PrimaryReason);
        Assert.Same(resolution.BaseSnapshot, resolution.Snapshot);
    }

    [Fact]
    public void OversizedDescriptorReturnsBaseWithoutParsing()
    {
        var resolver = CompatibilityFixture.CreateResolver();
        var bytes = new byte[(64 * 1024) + 1];
        Array.Fill(bytes, (byte)'x');

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(bytes));

        Assert.Equal(CompatibilityReasonCode.DescriptorTooLarge, resolution.PrimaryReason);
    }

    [Fact]
    public void DescriptorResponseRetainsOnlyTheOversizeSentinel()
    {
        var bytes = new byte[1024 * 1024];

        var response = CompatibilityFixture.Response(bytes);

        Assert.Equal((64 * 1024) + 1, response.Body.Length);
    }

    [Fact]
    public void DescriptorAtSixtyFourKiBBoundaryIsAccepted()
    {
        var descriptor = CompatibilityFixture.Descriptor(CompatibilityFixture.Component());
        var originalLength = descriptor.Length;
        Array.Resize(ref descriptor, 64 * 1024);
        descriptor.AsSpan(originalLength).Fill((byte)' ');
        var resolver = CompatibilityFixture.CreateResolver();

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(descriptor));

        Assert.Equal(CompatibilityLevel.Extended, resolution.Level);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}", CompatibilityReasonCode.DescriptorDuplicateProperty)]
    [InlineData("{} trailing", CompatibilityReasonCode.DescriptorMalformed)]
    [InlineData("[]", CompatibilityReasonCode.DescriptorMalformed)]
    [InlineData("{\"schemaVersion\":1.0}", CompatibilityReasonCode.DescriptorMalformed)]
    public void StrictJsonFailuresReturnStableBaseReason(
        string descriptor,
        CompatibilityReasonCode expectedReason)
    {
        var resolver = CompatibilityFixture.CreateResolver();

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(CompatibilityFixture.Utf8(descriptor)));

        Assert.Equal(CompatibilityLevel.Base, resolution.Level);
        Assert.Equal(expectedReason, resolution.PrimaryReason);
        Assert.DoesNotContain(descriptor, resolution.PrimaryReason.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExcessiveJsonDepthReturnsSpecificReason()
    {
        var nested = new string('[', 34) + "0" + new string(']', 34);
        var descriptor = CompatibilityFixture.Descriptor(
            CompatibilityFixture.Component(),
            extraProperty: $",\"extensions\":{{\"org.example.nested\":{nested}}}");
        var resolver = CompatibilityFixture.CreateResolver();

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(descriptor));

        Assert.Equal(CompatibilityReasonCode.DescriptorTooDeep, resolution.PrimaryReason);
    }

    [Fact]
    public void MoreThanSixtyFourComponentsReturnsSpecificReason()
    {
        var components = string.Join(
            ',',
            Enumerable.Range(0, 65).Select(index => CompatibilityFixture.Component(
                uiId: $"org.example.ui{index}")));
        var resolver = CompatibilityFixture.CreateResolver();

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(CompatibilityFixture.Descriptor(components)));

        Assert.Equal(CompatibilityReasonCode.DescriptorTooManyComponents, resolution.PrimaryReason);
    }

    [Theory]
    [InlineData(",\"origin\":\"https://evil.example:443\"")]
    [InlineData(",\"methods\":[\"GET\"]")]
    [InlineData(",\"permissions\":[\"camera\"]")]
    public void UnknownAuthorizationFieldReturnsBase(string extraProperty)
    {
        var resolver = CompatibilityFixture.CreateResolver();
        var component = CompatibilityFixture.Component(extraProperty: extraProperty);

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(CompatibilityFixture.Descriptor(component)));

        Assert.Equal(CompatibilityReasonCode.DescriptorUnknownField, resolution.PrimaryReason);
    }

    [Theory]
    [InlineData("01.2.3")]
    [InlineData("1.2")]
    [InlineData("1.2.3-01")]
    [InlineData("latest")]
    [InlineData("^1.2.3")]
    public void NonExactSemverReturnsBase(string version)
    {
        var resolver = CompatibilityFixture.CreateResolver();
        var component = CompatibilityFixture.Component(uiVersion: version);

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(CompatibilityFixture.Descriptor(component)));

        Assert.Equal(CompatibilityReasonCode.DescriptorInvalidIdentity, resolution.PrimaryReason);
    }

    [Fact]
    public void ArbitrarilyLargeSemverIdentifiersUseExactNumericPrecedence()
    {
        const string version = "1234567890123456789012345678901234567890.2.3-999999999999999999999999999999";
        var rule = CompatibilityFixture.Rule(
            minimumUiVersion: version,
            maximumUiVersion: version);
        var resolver = CompatibilityFixture.CreateResolver(rules: rule);
        var component = CompatibilityFixture.Component(uiVersion: version);

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(CompatibilityFixture.Descriptor(component)));

        Assert.Equal(CompatibilityLevel.Extended, resolution.Level);
    }

    [Fact]
    public void UnknownContractReturnsBase()
    {
        var resolver = CompatibilityFixture.CreateResolver();

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(CompatibilityFixture.Descriptor(
                CompatibilityFixture.Component(),
                contractVersion: "2.0.0")));

        Assert.Equal(CompatibilityReasonCode.DescriptorUnsupportedContract, resolution.PrimaryReason);
    }

    [Fact]
    public void DuplicateComponentIdentityReturnsBase()
    {
        var component = CompatibilityFixture.Component();
        var resolver = CompatibilityFixture.CreateResolver();

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(
                CompatibilityFixture.Descriptor($"{component},{component}")));

        Assert.Equal(CompatibilityReasonCode.DescriptorIdentityConflict, resolution.PrimaryReason);
    }

    [Fact]
    public void DescriptorResponseCopiesCallerBuffer()
    {
        var bytes = CompatibilityFixture.Descriptor(CompatibilityFixture.Component());
        var response = CompatibilityFixture.Response(bytes);
        Array.Fill(bytes, (byte)'x');
        var resolver = CompatibilityFixture.CreateResolver();

        var resolution = resolver.Resolve(TargetId, response);

        Assert.Equal(CompatibilityLevel.Extended, resolution.Level);
    }

    [Fact]
    public void DescriptorResponseDoesNotExposeItsRetainedBuffer()
    {
        var response = CompatibilityFixture.Response(
            CompatibilityFixture.Descriptor(CompatibilityFixture.Component()));
        var exposedBody = response.Body;
        Assert.True(MemoryMarshal.TryGetArray(exposedBody, out var segment));
        Array.Fill(segment.Array!, (byte)'x');
        var resolver = CompatibilityFixture.CreateResolver();

        var resolution = resolver.Resolve(TargetId, response);

        Assert.Equal(CompatibilityLevel.Extended, resolution.Level);
    }

    [Fact]
    public void ExtensionsNamespaceIsIgnoredAndCannotChangeCapabilities()
    {
        var resolver = CompatibilityFixture.CreateResolver();
        var plain = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(CompatibilityFixture.Descriptor(
                CompatibilityFixture.Component())));
        var extendedMetadata = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(CompatibilityFixture.Descriptor(
                CompatibilityFixture.Component(
                    extraProperty: ",\"extensions\":{\"org.example.vendor\":{\"note\":\"ignored\"}}"),
                extraProperty: ",\"extensions\":{\"org.example.vendor\":true}")));

        Assert.Equal(plain.Snapshot.Sha256, extendedMetadata.Snapshot.Sha256);
        Assert.Equal(plain.DescriptorIdentity!.Sha256, extendedMetadata.DescriptorIdentity!.Sha256);
    }

    [Fact]
    public void AuthorizationFieldInsideExtensionsNamespaceIsRejected()
    {
        var resolver = CompatibilityFixture.CreateResolver();
        var descriptor = CompatibilityFixture.Descriptor(
            CompatibilityFixture.Component(),
            extraProperty:
                ",\"extensions\":{\"org.example.vendor\":{\"origin\":\"https://evil.example:443\"}}");

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(descriptor));

        Assert.Equal(CompatibilityReasonCode.DescriptorUnknownField, resolution.PrimaryReason);
    }

    [Fact]
    public void InvalidUtf8ReturnsBaseWithoutLeakingBytes()
    {
        var resolver = CompatibilityFixture.CreateResolver();
        var invalidUtf8 = new byte[] { 0x7B, 0x22, 0xC3, 0x28, 0x22, 0x7D };

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(invalidUtf8));

        Assert.Equal(CompatibilityReasonCode.DescriptorMalformed, resolution.PrimaryReason);
    }

    [Fact]
    public void ExplicitNullSourceRevisionIsRejectedByTheDescriptorSchema()
    {
        var resolver = CompatibilityFixture.CreateResolver();
        var component = CompatibilityFixture.Component(
            sourceRevisionProperty: "\"sourceRev\":null,");

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(CompatibilityFixture.Descriptor(component)));

        Assert.Equal(CompatibilityReasonCode.DescriptorInvalidIdentity, resolution.PrimaryReason);
    }
}
