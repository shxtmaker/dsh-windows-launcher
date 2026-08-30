using DshLauncher.Compatibility;
using Xunit;

namespace DshLauncher.Compatibility.Tests;

[Trait("triggerTags", "VFY-06,VFY-08")]
public sealed class ConfigurationValidationTests
{
    [Fact]
    public void CreateExposesDeterministicTrustedConfigurationIdentity()
    {
        var first = CompatibilityFixture.CreateResolver();
        var secondRegistry = CompatibilityFixture.Utf8(
            $$"""
            {"tombstones":[],"activeRules":[{{CompatibilityFixture.Rule()}}],"contractVersion":"1.0.0","registryVersion":1,"schemaVersion":1}
            """);
        var second = PageCapabilityResolver.Create(
            CompatibilityFixture.Contract(),
            secondRegistry);

        Assert.Equal("1.0.0", first.ContractVersion);
        Assert.Equal(1, first.RegistryVersion);
        Assert.Equal(first.ContractSha256, second.ContractSha256);
        Assert.Equal(first.RegistrySha256, second.RegistrySha256);
        Assert.Matches("^[0-9a-f]{64}$", first.ContractSha256);
        Assert.Matches("^[0-9a-f]{64}$", first.RegistrySha256);
    }

    [Fact]
    public void ProductionGovernanceFilesLoadWithReleaseCanonicalIdentity()
    {
        var resolver = PageCapabilityResolver.Create(
            File.ReadAllBytes(FindRepositoryFile(
                "compatibility/contracts/webui-contract-capabilities.json")),
            File.ReadAllBytes(FindRepositoryFile(
                "compatibility/registry/webui-adapter-registry.json")));

        Assert.Equal(
            "a543d6f2d6bc736b89dbd430a8bae35efb536df2e87bc519e18bca39f491ecbb",
            resolver.ContractSha256);
        Assert.Equal(
            "7d71067cc626b90f6e7e0ef8df4c3de8417da55e42b4d1f100a5082c498287ad",
            resolver.RegistrySha256);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}", CompatibilityReasonCode.ContractMalformed)]
    [InlineData("{\"schemaVersion\":2}", CompatibilityReasonCode.ContractUnsupportedSchema)]
    [InlineData("[]", CompatibilityReasonCode.ContractMalformed)]
    [InlineData("{\"schemaVersion\":1.0}", CompatibilityReasonCode.ContractMalformed)]
    public void CreateRejectsMalformedContract(
        string contract,
        CompatibilityReasonCode expectedReason)
    {
        var exception = Assert.Throws<CompatibilityConfigurationException>(() =>
            PageCapabilityResolver.Create(
                CompatibilityFixture.Utf8(contract),
                CompatibilityFixture.Registry(CompatibilityFixture.Rule())));

        Assert.Equal(expectedReason, exception.ReasonCode);
        Assert.DoesNotContain(contract, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateRejectsUnsupportedContractSchema()
    {
        var contract = CompatibilityFixture.Utf8(
            System.Text.Encoding.UTF8.GetString(CompatibilityFixture.Contract())
                .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal));

        var exception = Assert.Throws<CompatibilityConfigurationException>(() =>
            PageCapabilityResolver.Create(
                contract,
                CompatibilityFixture.Registry(CompatibilityFixture.Rule())));

        Assert.Equal(CompatibilityReasonCode.ContractUnsupportedSchema, exception.ReasonCode);
    }

    [Fact]
    public void CreateRejectsRuleOutsideGlobalCeiling()
    {
        var exception = Assert.Throws<CompatibilityConfigurationException>(() =>
            CompatibilityFixture.CreateResolver(
                rules: CompatibilityFixture.Rule(
                    capabilities: CompatibilityFixture.WebSocketCapability())));

        Assert.Equal(CompatibilityReasonCode.RegistryInvalidCapability, exception.ReasonCode);
    }

    [Theory]
    [InlineData("https://market.example/", "/manifest/", "segment-prefix")]
    [InlineData("https://market.example:443", "/manifest", "segment-prefix")]
    [InlineData("https://market.example:443", "/a%2Fb/", "segment-prefix")]
    [InlineData("https://market.example:443", "/a%252Fb/", "segment-prefix")]
    [InlineData("https://market.example:443", "/../secret", "exact")]
    public void CreateRejectsNonCanonicalCapabilityScope(
        string origin,
        string path,
        string pathMatch)
    {
        var capability = CompatibilityFixture.PassiveCapability(
            origin: origin,
            path: path,
            pathMatch: pathMatch);

        var exception = Assert.Throws<CompatibilityConfigurationException>(() =>
            CompatibilityFixture.CreateResolver(
                rules: CompatibilityFixture.Rule(capabilities: capability)));

        Assert.Equal(CompatibilityReasonCode.RegistryInvalidCapability, exception.ReasonCode);
    }

    [Fact]
    public void CreateRejectsPassivePostOrScriptCapability()
    {
        var passivePost = CompatibilityFixture.PassiveCapability(
            methods: "\"POST\"",
            resourceKinds: "\"script\"");

        var exception = Assert.Throws<CompatibilityConfigurationException>(() =>
            CompatibilityFixture.CreateResolver(
                rules: CompatibilityFixture.Rule(capabilities: passivePost)));

        Assert.Equal(CompatibilityReasonCode.RegistryInvalidCapability, exception.ReasonCode);
    }

    [Fact]
    public void CreateRejectsOverlappingMatchDomains()
    {
        var first = CompatibilityFixture.Rule(
            ruleId: "market.first",
            minimumUiVersion: "1.0.0",
            maximumUiVersion: "2.0.0");
        var second = CompatibilityFixture.Rule(
            ruleId: "market.second",
            minimumUiVersion: "1.5.0",
            maximumUiVersion: "3.0.0");

        var exception = Assert.Throws<CompatibilityConfigurationException>(() =>
            CompatibilityFixture.CreateResolver(rules: $"{first},{second}"));

        Assert.Equal(CompatibilityReasonCode.RegistryRuleOverlap, exception.ReasonCode);
    }

    [Fact]
    public void CreateAllowsDisjointVersionRanges()
    {
        var first = CompatibilityFixture.Rule(
            ruleId: "market.first",
            minimumUiVersion: "1.0.0",
            maximumUiVersion: "1.9.9");
        var second = CompatibilityFixture.Rule(
            ruleId: "market.second",
            minimumUiVersion: "2.0.0",
            maximumUiVersion: "3.0.0");

        var resolver = CompatibilityFixture.CreateResolver(rules: $"{first},{second}");

        Assert.Equal(1, resolver.RegistryVersion);
    }

    [Fact]
    public void CreateRejectsPrereleaseRangeInsteadOfGuessingNearbyVersion()
    {
        var rule = CompatibilityFixture.Rule(
            minimumUiVersion: "1.2.3-alpha.1",
            maximumUiVersion: "1.2.3-beta.2");

        var exception = Assert.Throws<CompatibilityConfigurationException>(() =>
            CompatibilityFixture.CreateResolver(rules: rule));

        Assert.Equal(CompatibilityReasonCode.RegistryMalformed, exception.ReasonCode);
    }

    [Fact]
    public void CreateRejectsActiveRuleWithSameIdAsTombstone()
    {
        var tombstone =
            """
            {
              "ruleId":"market.manifest",
              "revokedFromRegistryVersion":1,
              "severity":"High",
              "reasonCode":"RULE_TOO_BROAD"
            }
            """;

        var exception = Assert.Throws<CompatibilityConfigurationException>(() =>
            CompatibilityFixture.CreateResolver(tombstones: tombstone));

        Assert.Equal(CompatibilityReasonCode.RegistryTombstoneConflict, exception.ReasonCode);
    }

    [Fact]
    public void CreateRejectsActiveConditionalCapability()
    {
        var exception = Assert.Throws<CompatibilityConfigurationException>(() =>
            CompatibilityFixture.CreateResolver(
                rules: CompatibilityFixture.Rule(
                    capabilities: CompatibilityFixture.WebSocketCapability())));

        Assert.Equal(CompatibilityReasonCode.RegistryInvalidCapability, exception.ReasonCode);
    }

    [Fact]
    public void CreateAllowsPassiveScriptOnlyInsideApprovedCrossOriginFrame()
    {
        var capability = CompatibilityFixture.PassiveCapability(
            methods: "\"GET\"",
            resourceKinds: "\"script\"",
            documentScope: "CrossOriginFrameOnly");

        var resolver = CompatibilityFixture.CreateResolver(
            rules: CompatibilityFixture.Rule(capabilities: capability));

        Assert.Equal(1, resolver.RegistryVersion);
    }

    [Fact]
    public void CreateRejectsFrameScriptWithoutExactParentOrigin()
    {
        var capability = CompatibilityFixture.PassiveCapability(
                methods: "\"GET\"",
                resourceKinds: "\"script\"",
                documentScope: "CrossOriginFrameOnly")
            .Replace(
                ",\"parentOrigin\":\"https://frame.example:443\"",
                string.Empty,
                StringComparison.Ordinal);

        var exception = Assert.Throws<CompatibilityConfigurationException>(() =>
            CompatibilityFixture.CreateResolver(
                rules: CompatibilityFixture.Rule(capabilities: capability)));

        Assert.Equal(CompatibilityReasonCode.RegistryInvalidCapability, exception.ReasonCode);
    }

    [Fact]
    public void CreateRejectsDuplicateQueryKeys()
    {
        var capability = CompatibilityFixture.ReviewedApiCapability(
            queryKeysProperty: ",\"queryKeys\":[\"page\",\"page\"]");

        var exception = Assert.Throws<CompatibilityConfigurationException>(() =>
            CompatibilityFixture.CreateResolver(
                rules: CompatibilityFixture.Rule(capabilities: capability)));

        Assert.Equal(CompatibilityReasonCode.RegistryInvalidCapability, exception.ReasonCode);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(relativePath);
    }
}
