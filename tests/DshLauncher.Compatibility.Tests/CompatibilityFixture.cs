using System.Text;
using DshLauncher.Compatibility;

namespace DshLauncher.Compatibility.Tests;

internal static class CompatibilityFixture
{
    public static byte[] Contract() => Utf8(
        """
        {
          "schemaVersion": 1,
          "contractVersion": "1.1.0",
          "descriptorSchemaId": "https://schemas.dshwindowslauncher.invalid/webui-compatibility-descriptor.schema.json",
          "limits": {
            "descriptorBytes": 65536,
            "descriptorComponents": 64,
            "jsonDepth": 32
          },
          "methodCeilings": {
            "passiveResource": ["GET", "HEAD"],
            "reviewedApi": ["GET", "OPTIONS", "POST"]
          },
          "baseCapabilities": [
            {"capabilityId":"target-http-resource","description":"Target resources."},
            {"capabilityId":"same-origin-frame","description":"Target frames."},
            {"capabilityId":"about-blank-frame","description":"Bound blank frames."},
            {"capabilityId":"data-image","description":"Data images."},
            {"capabilityId":"blob-image","description":"Blob images."},
            {"capabilityId":"blob-media","description":"Blob media."},
            {"capabilityId":"blob-data-read","description":"Blob data."},
            {"capabilityId":"webgl-rendering","description":"WebGL."},
            {"capabilityId":"page-editing","description":"Page editing."},
            {"capabilityId":"find-in-page","description":"Find."},
            {"capabilityId":"page-zoom","description":"Zoom."},
            {"capabilityId":"user-image-drop","description":"Image input."}
          ],
          "extensionCapabilities": [
            {
              "capabilityId":"external-reviewed-api",
              "description":"Reviewed API.",
              "methodCeiling":"reviewedApi",
              "resourceTypes":["fetch","xhr"],
              "mainDocumentScriptAllowed":false
            },
            {
              "capabilityId":"external-reviewed-frame",
              "description":"Reviewed frame.",
              "methodCeiling":"passiveResource",
              "resourceTypes":["document","frame"],
              "mainDocumentScriptAllowed":false
            },
            {
              "capabilityId":"external-reviewed-frame-script",
              "description":"Reviewed frame script.",
              "methodCeiling":"passiveResource",
              "resourceTypes":["script"],
              "mainDocumentScriptAllowed":false
            },
            {
              "capabilityId":"external-reviewed-passive-resource",
              "description":"Reviewed passive resources.",
              "methodCeiling":"passiveResource",
              "resourceTypes":["font","image","media","stylesheet"],
              "mainDocumentScriptAllowed":false
            },
            {
              "capabilityId":"reviewed-inline-script-sha256",
              "description":"Reviewed inline script.",
              "methodCeiling":"passiveResource",
              "resourceTypes":["script"],
              "mainDocumentScriptAllowed":true
            },
            {
              "capabilityId":"target-websocket",
              "description":"Reviewed target WebSocket.",
              "methodCeiling":"passiveResource",
              "resourceTypes":["websocket"],
              "mainDocumentScriptAllowed":false
            }
          ],
          "conditionalCapabilities": [
            {
              "capabilityId":"external-websocket",
              "status":"not-authorized",
              "requiredEvidence":[
                "minimum-runtime","evergreen-runtime","same-endpoint-edge","negative-arrival-count"
              ]
            }
          ],
          "permanentlyForbiddenCapabilities":["external-main-script"],
          "globalCspDebt":[]
        }
        """);

    public static byte[] Registry(
        string rules,
        string tombstones = "",
        int registryVersion = 1) => Utf8(
        $$"""
        {
          "schemaVersion": 1,
          "registryVersion": {{registryVersion}},
          "contractVersion": "1.1.0",
          "activeRules": [{{rules}}],
          "tombstones": [{{tombstones}}]
        }
        """);

    public static string Rule(
        string ruleId = "market.manifest",
        string ruleVersion = "1.0.0",
        string uiId = "org.example.ui",
        string minimumUiVersion = "1.2.3",
        string maximumUiVersion = "1.2.3",
        string sourceRevisionProperty = "\"sourceRev\": \"abc1234\",",
        string adapterKey = "market.manifest",
        string purpose = "Load reviewed market data",
        string? capabilities = null) =>
        $$"""
        {
          "ruleId": "{{ruleId}}",
          "ruleVersion": "{{ruleVersion}}",
          "contractVersion": "1.1.0",
          "evidenceBaseline": {
            "harnessCommit": "0123456789abcdef0123456789abcdef01234567",
            "lanPluginVersion": "1.2.1",
            "routeVersion": "fixture-v1"
          },
          "match": {
            "uiId": "{{uiId}}",
            "minimumUiVersion": "{{minimumUiVersion}}",
            "maximumUiVersion": "{{maximumUiVersion}}",
            {{sourceRevisionProperty}}
            "adapterKey": "{{adapterKey}}"
          },
          "purpose": {
            "displayName": "{{purpose}}",
            "diagnosticName": "reviewed-dependencies"
          },
          "grants": [{{capabilities ?? PassiveCapability()}}]
        }
        """;

    public static string PassiveCapability(
        string origin = "https://market.example:443",
        string path = "/manifest/",
        string pathMatch = "segment-prefix",
        string methods = "\"GET\",\"HEAD\"",
        string resourceKinds = "\"image\",\"font\"",
        string documentScope = "TargetDocument",
        string queryKeysProperty = "")
    {
        var frameScript = documentScope == "CrossOriginFrameOnly";
        var capabilityId = frameScript
            ? "external-reviewed-frame-script"
            : "external-reviewed-passive-resource";
        var parentOrigin = frameScript
            ? ",\"parentOrigin\":\"https://frame.example:443\""
            : string.Empty;
        return $$"""
        {
          "capabilityId": "{{capabilityId}}",
          "origin": "{{origin}}",
          "path": {"kind":"{{pathMatch}}","value":"{{path}}"},
          "methods": [{{methods}}],
          "resourceTypes": [{{resourceKinds}}],
          "redirectPolicy": "none"{{parentOrigin}}{{queryKeysProperty}}
        }
        """;
    }

    public static string ReviewedApiCapability(
        string methods = "\"GET\"",
        string resourceKinds = "\"fetch\"",
        string queryKeysProperty = "") =>
        $$"""
        {
          "capabilityId": "external-reviewed-api",
          "origin": "https://api.example:443",
          "path": {"kind":"segment-prefix","value":"/v1/"},
          "methods": [{{methods}}],
          "resourceTypes": [{{resourceKinds}}],
          "redirectPolicy": "none"{{queryKeysProperty}}
        }
        """;

    public static string FrameCapability() =>
        """
        {
          "capabilityId": "external-reviewed-frame",
          "origin": "https://frame.example:443",
          "path": {"kind":"exact","value":"/challenge"},
          "methods": ["GET", "HEAD"],
          "resourceTypes": ["document", "frame"],
          "redirectPolicy": "none"
        }
        """;

    public static string WebSocketCapability() =>
        """
        {
          "capabilityId": "external-websocket",
          "origin": "https://socket.example:443",
          "path": {"kind":"exact","value":"/stream"},
          "methods": ["GET"],
          "resourceTypes": ["websocket"],
          "redirectPolicy": "none"
        }
        """;

    public static string ReviewedInlineScriptCapability(
        string digest = "mXHaYLlSQVyeSOfzqV5XdTPGPqz5QzoV1XVjJm6ZROw=") =>
        $$"""
        {
          "capabilityId": "reviewed-inline-script-sha256",
          "scriptSha256": "{{digest}}"
        }
        """;

    public static string TargetWebSocketCapability(
        string path = "/remote/api/remote.mux",
        string pathMatch = "exact",
        string methods = "\"GET\"",
        string resourceKinds = "\"websocket\"",
        string queryKeys = "\"device\"") =>
        $$"""
        {
          "capabilityId": "target-websocket",
          "path": {"kind":"{{pathMatch}}","value":"{{path}}"},
          "methods": [{{methods}}],
          "resourceTypes": [{{resourceKinds}}],
          "redirectPolicy": "none",
          "queryKeys": [{{queryKeys}}]
        }
        """;

    public static byte[] Descriptor(
        string components,
        string contractVersion = "1.1.0",
        int schemaVersion = 1,
        string extraProperty = "") => Utf8(
        $$"""
        {
          "schemaVersion": {{schemaVersion}},
          "contractVersion": "{{contractVersion}}",
          "components": [{{components}}]{{extraProperty}}
        }
        """);

    public static string Component(
        string uiId = "org.example.ui",
        string uiVersion = "1.2.3",
        string sourceRevisionProperty = "\"sourceRev\": \"abc1234\",",
        string adapterKeys = "\"market.manifest\"",
        string extraProperty = "") =>
        $$"""
        {
          "uiId": "{{uiId}}",
          "uiVersion": "{{uiVersion}}",
          {{sourceRevisionProperty}}
          "adapterKeys": [{{adapterKeys}}]{{extraProperty}}
        }
        """;

    public static PageCapabilityResolver CreateResolver(
        string? rules = null,
        byte[]? contract = null,
        string tombstones = "") =>
        PageCapabilityResolver.Create(
            contract ?? Contract(),
            Registry(rules ?? Rule(), tombstones));

    public static BoundedDescriptorResponse Response(byte[] body) =>
        BoundedDescriptorResponse.Received(
            200,
            "application/json; charset=utf-8",
            wasRedirected: false,
            body);

    public static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
