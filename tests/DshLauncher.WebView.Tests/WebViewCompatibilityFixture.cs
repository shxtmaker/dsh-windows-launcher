using System.IO;
using System.Text;
using DshLauncher.Compatibility;

namespace DshLauncher.WebView.Tests;

internal static class WebViewCompatibilityFixture
{
    public static readonly Guid TargetId =
        Guid.Parse("54f02e80-2c92-44d8-8f7f-b0fc118c93fa");

    public static TargetContentBinding CreateBinding() => new(
        TargetId,
        new Uri("http://192.168.10.20:3080/"),
        @"C:\Users\test\AppData\Local\DshWindowsLauncher\targets\54f02e802c9244d88f7fb0fc118c93fa\udf");

    public static PageCapabilityResolver CreateResolver(string rules = "") =>
        PageCapabilityResolver.Create(Contract(), Registry(rules));

    public static PageCapabilitySnapshot CreateBaseSnapshot(Guid? targetId = null) =>
        CreateResolver().Resolve(
            targetId ?? TargetId,
            BoundedDescriptorResponse.Missing())
            .Snapshot;

    public static PageCapabilityResolution CreateExtendedResolution()
    {
        var resolver = CreateResolver(Rule());
        return resolver.Resolve(
            TargetId,
            BoundedDescriptorResponse.Received(
                200,
                "application/json; charset=utf-8",
                wasRedirected: false,
                Descriptor()));
    }

    public static byte[] Descriptor() => Utf8(
        """
        {
          "schemaVersion": 1,
          "contractVersion": "1.0.0",
          "components": [
            {
              "uiId": "org.example.ui",
              "uiVersion": "1.2.3",
              "sourceRev": "abc1234",
              "adapterKeys": ["example.bundle"]
            }
          ]
        }
        """);

    private static byte[] Contract() => File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "WebUiContractCapabilities.json"));

    private static byte[] Registry(string rules) => Utf8(
        $$"""
        {
          "schemaVersion": 1,
          "registryVersion": 1,
          "contractVersion": "1.0.0",
          "activeRules": [{{rules}}],
          "tombstones": []
        }
        """);

    public static string Rule() =>
        """
        {
          "ruleId": "org.example.bundle",
          "ruleVersion": "1.0.0",
          "contractVersion": "1.0.0",
          "evidenceBaseline": {
            "harnessCommit": "0123456789abcdef0123456789abcdef01234567",
            "lanPluginVersion": "1.2.1",
            "routeVersion": "fixture-v1"
          },
          "match": {
            "uiId": "org.example.ui",
            "minimumUiVersion": "1.2.3",
            "maximumUiVersion": "1.2.3",
            "sourceRev": "abc1234",
            "adapterKey": "example.bundle"
          },
          "purpose": {
            "displayName": "Load reviewed example dependencies",
            "diagnosticName": "example-dependencies"
          },
          "grants": [
            {
              "capabilityId": "external-reviewed-passive-resource",
              "origin": "https://assets.example:443",
              "path": {"kind": "segment-prefix", "value": "/theme/"},
              "methods": ["GET", "HEAD"],
              "resourceTypes": ["image", "stylesheet", "font"],
              "redirectPolicy": "none"
            },
            {
              "capabilityId": "external-reviewed-api",
              "origin": "https://api.example:443",
              "path": {"kind": "segment-prefix", "value": "/v1/"},
              "methods": ["GET", "POST", "OPTIONS"],
              "resourceTypes": ["fetch", "xhr"],
              "redirectPolicy": "none",
              "queryKeys": ["cursor", "mode"]
            },
            {
              "capabilityId": "external-reviewed-frame",
              "origin": "https://frame.example:443",
              "path": {"kind": "exact", "value": "/challenge"},
              "methods": ["GET", "HEAD"],
              "resourceTypes": ["document", "frame"],
              "redirectPolicy": "none"
            }
          ]
        }
        """;

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
