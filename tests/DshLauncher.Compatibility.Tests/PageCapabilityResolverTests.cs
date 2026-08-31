using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using DshLauncher.Compatibility;
using Xunit;

namespace DshLauncher.Compatibility.Tests;

[Trait("triggerTags", "VFY-06,VFY-08")]
public sealed class PageCapabilityResolverTests
{
    private static readonly Guid TargetId = Guid.Parse(
        "9766444b-5cc0-4c68-bc93-d39d17bc01f1");

    [Fact]
    public void ExactRuleMatchProducesExtendedCandidateAndSeparateBaseFallback()
    {
        var resolver = CompatibilityFixture.CreateResolver();

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(CompatibilityFixture.Descriptor(
                CompatibilityFixture.Component())));

        Assert.Equal(CompatibilityLevel.Extended, resolution.Level);
        Assert.Equal(CompatibilityReasonCode.None, resolution.PrimaryReason);
        Assert.NotSame(resolution.BaseSnapshot, resolution.Snapshot);
        Assert.Empty(resolution.BaseSnapshot.ExtensionCapabilities);
        var grant = Assert.Single(resolution.Snapshot.ExtensionCapabilities);
        Assert.Equal(CapabilityKind.ExternalPassive, grant.Kind);
        Assert.Equal("https://market.example:443", grant.Origin);
        Assert.Equal("/manifest/", grant.Path);
        Assert.Equal(CapabilityPathMatch.SegmentPrefix, grant.PathMatch);
        Assert.Equal([HttpMethodKind.Get, HttpMethodKind.Head], grant.Methods);
        Assert.Equal([WebResourceKind.Image, WebResourceKind.Font], grant.ResourceKinds);
        Assert.Equal("Load reviewed market data", grant.Purpose);
        var rule = Assert.Single(resolution.Snapshot.MatchedRules);
        Assert.Equal("market.manifest", rule.RuleId);
        Assert.Equal("1.0.0", rule.RuleVersion);
        Assert.Equal("market.manifest", rule.AdapterKey);
        Assert.NotNull(resolution.DescriptorIdentity);
        Assert.Matches("^[0-9a-f]{64}$", resolution.Snapshot.Sha256);
        Assert.NotEqual(resolution.BaseSnapshot.Sha256, resolution.Snapshot.Sha256);
    }

    [Fact]
    public void ExactRemoteUiRuleProducesReviewedHashesAndExactTargetWebSockets()
    {
        var capabilities = string.Join(",", [
            CompatibilityFixture.ReviewedInlineScriptCapability(),
            CompatibilityFixture.ReviewedInlineScriptCapability(
                "60H3O19ZLKq8nr2bYG0M2Erc+j7nEJP55v17BRb6+90="),
            CompatibilityFixture.TargetWebSocketCapability(),
            CompatibilityFixture.TargetWebSocketCapability(
                "/remote/sidebar/ws/terminal"),
            CompatibilityFixture.TargetWebSocketCapability(
                "/remote/sidebar/ws/agent-terminals"),
            CompatibilityFixture.TargetWebSocketCapability(
                "/remote/api/dsh-ssh/terminal"),
        ]);
        var resolver = CompatibilityFixture.CreateResolver(
            rules: CompatibilityFixture.Rule(capabilities: capabilities));

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(CompatibilityFixture.Descriptor(
                CompatibilityFixture.Component())));

        Assert.Equal(CompatibilityLevel.Extended, resolution.Level);
        Assert.Equal(
            [
                "60H3O19ZLKq8nr2bYG0M2Erc+j7nEJP55v17BRb6+90=",
                "mXHaYLlSQVyeSOfzqV5XdTPGPqz5QzoV1XVjJm6ZROw=",
            ],
            resolution.Snapshot.ExtensionCapabilities
                .Where(static grant =>
                    grant.Kind == CapabilityKind.ReviewedInlineScriptSha256)
                .Select(static grant => grant.ScriptSha256)
                .Order(StringComparer.Ordinal));
        var sockets = resolution.Snapshot.ExtensionCapabilities
            .Where(static grant => grant.Kind == CapabilityKind.TargetWebSocket)
            .ToArray();
        Assert.Equal(4, sockets.Length);
        Assert.All(sockets, grant =>
        {
            Assert.Null(grant.Origin);
            Assert.Equal(CapabilityPathMatch.Exact, grant.PathMatch);
            Assert.Equal([HttpMethodKind.Get], grant.Methods);
            Assert.Equal([WebResourceKind.WebSocket], grant.ResourceKinds);
            Assert.Equal(["device"], grant.QueryKeys);
        });
    }

    [Fact]
    public void SourceRevisionMismatchReturnsBaseWithoutTryingNearbyRule()
    {
        var resolver = CompatibilityFixture.CreateResolver();
        var component = CompatibilityFixture.Component(
            sourceRevisionProperty: "\"sourceRev\":\"different\",");

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(CompatibilityFixture.Descriptor(component)));

        Assert.Equal(CompatibilityLevel.Base, resolution.Level);
        Assert.Equal(CompatibilityReasonCode.NoMatchingRule, resolution.PrimaryReason);
        Assert.NotNull(resolution.DescriptorIdentity);
    }

    [Fact]
    public void RuleWithoutSourceRevisionMatchesAnyImmutableDescriptorRevision()
    {
        var rule = CompatibilityFixture.Rule(sourceRevisionProperty: string.Empty);
        var resolver = CompatibilityFixture.CreateResolver(rules: rule);
        var component = CompatibilityFixture.Component(
            sourceRevisionProperty: "\"sourceRev\":\"other-revision\",");

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(CompatibilityFixture.Descriptor(component)));

        Assert.Equal(CompatibilityLevel.Extended, resolution.Level);
    }

    [Fact]
    public void ExactPrereleaseRuleMatchesOnlyExactSemverText()
    {
        var rule = CompatibilityFixture.Rule(
            minimumUiVersion: "1.2.3-beta.1",
            maximumUiVersion: "1.2.3-beta.1");
        var resolver = CompatibilityFixture.CreateResolver(rules: rule);
        var component = CompatibilityFixture.Component(uiVersion: "1.2.3-beta.1");

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(CompatibilityFixture.Descriptor(component)));

        Assert.Equal(CompatibilityLevel.Extended, resolution.Level);
    }

    [Fact]
    public void ExactRuleDoesNotIgnoreSemverBuildIdentity()
    {
        var rule = CompatibilityFixture.Rule(
            minimumUiVersion: "1.2.3+reviewed",
            maximumUiVersion: "1.2.3+reviewed");
        var resolver = CompatibilityFixture.CreateResolver(rules: rule);
        var component = CompatibilityFixture.Component(uiVersion: "1.2.3+different");

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(CompatibilityFixture.Descriptor(component)));

        Assert.Equal(CompatibilityLevel.Base, resolution.Level);
        Assert.Equal(CompatibilityReasonCode.NoMatchingRule, resolution.PrimaryReason);
    }

    [Fact]
    public void UnknownComponentAlongsideMatchProducesPartialExtendedCandidate()
    {
        var resolver = CompatibilityFixture.CreateResolver();
        var known = CompatibilityFixture.Component();
        var unknown = CompatibilityFixture.Component(
            uiId: "org.unknown.ui",
            adapterKeys: "\"unknown.adapter\"");

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(
                CompatibilityFixture.Descriptor($"{known},{unknown}")));

        Assert.Equal(CompatibilityLevel.Extended, resolution.Level);
        Assert.Equal(CompatibilityReasonCode.PartialMatch, resolution.PrimaryReason);
        Assert.Single(resolution.Snapshot.ExtensionCapabilities);
    }

    [Fact]
    public void MultipleRulesWithSameScopeAndPurposeMergeDeterministically()
    {
        var first = CompatibilityFixture.Rule(
            ruleId: "api.read",
            adapterKey: "api.read",
            purpose: "Use reviewed API",
            capabilities: CompatibilityFixture.ReviewedApiCapability());
        var second = CompatibilityFixture.Rule(
            ruleId: "api.write",
            adapterKey: "api.write",
            purpose: "Use reviewed API",
            capabilities: CompatibilityFixture.ReviewedApiCapability(
                methods: "\"POST\",\"OPTIONS\"",
                resourceKinds: "\"xhr\""));
        var resolver = CompatibilityFixture.CreateResolver(rules: $"{first},{second}");
        var component = CompatibilityFixture.Component(
            adapterKeys: "\"api.write\",\"api.read\"");

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(CompatibilityFixture.Descriptor(component)));

        Assert.Equal(CompatibilityLevel.Extended, resolution.Level);
        var grant = Assert.Single(resolution.Snapshot.ExtensionCapabilities);
        Assert.Equal(
            [HttpMethodKind.Get, HttpMethodKind.Post, HttpMethodKind.Options],
            grant.Methods);
        Assert.Equal(
            [WebResourceKind.XmlHttpRequest, WebResourceKind.Fetch],
            grant.ResourceKinds);
        Assert.Equal(2, resolution.Snapshot.MatchedRules.Count);
    }

    [Fact]
    public void SameScopeWithDifferentPurposeFailsClosedToBase()
    {
        var first = CompatibilityFixture.Rule(
            ruleId: "api.read",
            adapterKey: "api.read",
            purpose: "Read data",
            capabilities: CompatibilityFixture.ReviewedApiCapability());
        var second = CompatibilityFixture.Rule(
            ruleId: "api.write",
            adapterKey: "api.write",
            purpose: "Write data",
            capabilities: CompatibilityFixture.ReviewedApiCapability(
                methods: "\"POST\"",
                resourceKinds: "\"xhr\""));
        var resolver = CompatibilityFixture.CreateResolver(rules: $"{first},{second}");
        var component = CompatibilityFixture.Component(
            adapterKeys: "\"api.read\",\"api.write\"");

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(CompatibilityFixture.Descriptor(component)));

        Assert.Equal(CompatibilityLevel.Base, resolution.Level);
        Assert.Equal(CompatibilityReasonCode.RuleConflict, resolution.PrimaryReason);
        Assert.Same(resolution.BaseSnapshot, resolution.Snapshot);
    }

    [Fact]
    public void SnapshotAndDescriptorCanonicalBytesAreIndependentOfInputOrdering()
    {
        var resolver = CompatibilityFixture.CreateResolver();
        var firstComponent = CompatibilityFixture.Component(
            adapterKeys: "\"market.manifest\"");
        var first = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(CompatibilityFixture.Descriptor(firstComponent)));
        var reorderedDescriptor = CompatibilityFixture.Utf8(
            """
            {"components":[{"adapterKeys":["market.manifest"],"sourceRev":"abc1234","uiVersion":"1.2.3","uiId":"org.example.ui"}],"contractVersion":"1.1.0","schemaVersion":1}
            """);
        var second = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(reorderedDescriptor));

        Assert.Equal(first.DescriptorIdentity!.Sha256, second.DescriptorIdentity!.Sha256);
        Assert.Equal(first.Snapshot.Sha256, second.Snapshot.Sha256);
        Assert.Equal(first.Snapshot.CanonicalJson.ToArray(), second.Snapshot.CanonicalJson.ToArray());
    }

    [Fact]
    public void TargetIdentityIsBoundIntoSnapshotDigest()
    {
        var resolver = CompatibilityFixture.CreateResolver();
        var response = CompatibilityFixture.Response(
            CompatibilityFixture.Descriptor(CompatibilityFixture.Component()));

        var first = resolver.Resolve(TargetId, response);
        var second = resolver.Resolve(Guid.NewGuid(), response);

        Assert.NotEqual(first.Snapshot.Sha256, second.Snapshot.Sha256);
    }

    [Fact]
    public void CompleteDescriptorIdentityIsBoundIntoCandidateDigest()
    {
        var resolver = CompatibilityFixture.CreateResolver(
            rules: CompatibilityFixture.Rule(
                minimumUiVersion: "1.2.3",
                maximumUiVersion: "1.2.4"));
        var first = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(CompatibilityFixture.Descriptor(
                CompatibilityFixture.Component(uiVersion: "1.2.3"))));
        var second = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(CompatibilityFixture.Descriptor(
                CompatibilityFixture.Component(uiVersion: "1.2.4"))));

        Assert.Equal(CompatibilityLevel.Extended, first.Level);
        Assert.Equal(CompatibilityLevel.Extended, second.Level);
        Assert.NotEqual(first.DescriptorIdentity!.Sha256, second.DescriptorIdentity!.Sha256);
        Assert.NotEqual(first.Snapshot.Sha256, second.Snapshot.Sha256);
    }

    [Fact]
    public void QueryKeysAreCanonicalAndAbsentMeansNoQueryAuthorization()
    {
        var withQueryKeys = CompatibilityFixture.Rule(
            capabilities: CompatibilityFixture.ReviewedApiCapability(
                queryKeysProperty: ",\"queryKeys\":[\"page\",\"filter\"]"));
        var configured = CompatibilityFixture.CreateResolver(rules: withQueryKeys)
            .Resolve(
                TargetId,
                CompatibilityFixture.Response(CompatibilityFixture.Descriptor(
                    CompatibilityFixture.Component())));
        var withoutQueryKeys = CompatibilityFixture.CreateResolver(
                rules: CompatibilityFixture.Rule(
                    capabilities: CompatibilityFixture.ReviewedApiCapability()))
            .Resolve(
                TargetId,
                CompatibilityFixture.Response(CompatibilityFixture.Descriptor(
                    CompatibilityFixture.Component())));

        Assert.Equal(
            ["filter", "page"],
            Assert.Single(configured.Snapshot.ExtensionCapabilities).QueryKeys);
        Assert.Empty(Assert.Single(withoutQueryKeys.Snapshot.ExtensionCapabilities).QueryKeys);
    }

    [Fact]
    public void FrameScopedScriptCarriesExactParentOrigin()
    {
        var resolver = CompatibilityFixture.CreateResolver(
            rules: CompatibilityFixture.Rule(
                capabilities: CompatibilityFixture.PassiveCapability(
                    methods: "\"GET\"",
                    resourceKinds: "\"script\"",
                    documentScope: "CrossOriginFrameOnly")));

        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(CompatibilityFixture.Descriptor(
                CompatibilityFixture.Component())));

        var grant = Assert.Single(resolution.Snapshot.ExtensionCapabilities);
        Assert.Equal(CapabilityDocumentScope.CrossOriginFrameOnly, grant.DocumentScope);
        Assert.Equal("https://frame.example:443", grant.ParentOrigin);
        Assert.Equal([WebResourceKind.Script], grant.ResourceKinds);
    }

    [Fact]
    public void PublishedCollectionsAreReadOnlyAndCannotMutateSnapshot()
    {
        var resolver = CompatibilityFixture.CreateResolver();
        var resolution = resolver.Resolve(
            TargetId,
            CompatibilityFixture.Response(CompatibilityFixture.Descriptor(
                CompatibilityFixture.Component())));
        var originalDigest = resolution.Snapshot.Sha256;

        Assert.IsType<ReadOnlyCollection<CapabilityGrant>>(
            resolution.Snapshot.ExtensionCapabilities);
        Assert.IsType<ReadOnlyCollection<RuleIdentity>>(
            resolution.Snapshot.MatchedRules);
        Assert.IsType<ReadOnlyCollection<string>>(
            resolution.Snapshot.ExtensionCapabilities[0].QueryKeys);
        Assert.IsType<ReadOnlyCollection<string>>(
            resolution.DescriptorIdentity!.Components[0].AdapterKeys);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<CapabilityGrant>)resolution.Snapshot.ExtensionCapabilities).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)resolution.Snapshot.ExtensionCapabilities[0].QueryKeys)
            .Add("unexpected"));
        var bytes = resolution.Snapshot.CanonicalJson.ToArray();
        Array.Fill(bytes, (byte)'x');
        var exposedCanonicalJson = resolution.Snapshot.CanonicalJson;
        Assert.True(MemoryMarshal.TryGetArray(exposedCanonicalJson, out var segment));
        Array.Fill(segment.Array!, (byte)'x');

        Assert.Equal(originalDigest, resolution.Snapshot.Sha256);
        Assert.NotEqual(bytes, resolution.Snapshot.CanonicalJson.ToArray());
        Assert.NotEqual(segment.Array, resolution.Snapshot.CanonicalJson.ToArray());
    }

    [Fact]
    public void EmptyTargetIdentityIsRejectedAtThePublicInterface()
    {
        var resolver = CompatibilityFixture.CreateResolver();

        Assert.Throws<ArgumentException>(() => resolver.Resolve(
            Guid.Empty,
            BoundedDescriptorResponse.Missing()));
    }
}
