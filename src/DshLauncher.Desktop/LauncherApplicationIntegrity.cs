using System.IO;
using System.Reflection;
using System.Text.Json;
using DshLauncher.Compatibility;
using DshLauncher.Core;
using DshLauncher.Desktop.RuntimeRepair;

namespace DshLauncher.Desktop;

internal static class LauncherApplicationIntegrity
{
    private const int MaximumEmbeddedResourceBytes = 1024 * 1024;

    public static PageCapabilityResolver LoadCompatibilityResolver(
        LauncherBuildIdentity buildIdentity)
    {
        ArgumentNullException.ThrowIfNull(buildIdentity);
        var assembly = typeof(App).Assembly;
        var contract = ReadEmbeddedResource(
            assembly,
            "DshLauncher.Desktop.WebUiContractCapabilities.json");
        var registry = ReadEmbeddedResource(
            assembly,
            "DshLauncher.Desktop.WebUiAdapterRegistry.json");
        var releaseConstants = ReadEmbeddedResource(
            assembly,
            "DshLauncher.Desktop.ReleaseConstants.json");
        var resolver = PageCapabilityResolver.Create(contract, registry);

        using var document = JsonDocument.Parse(releaseConstants, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        var root = document.RootElement;
        var releaseStatus = RequiredString(root, "releaseStatus");
        var compatibility = RequiredObject(root, "webUiCompatibility");
        if (!string.Equals(
                RequiredString(compatibility, "contractVersion"),
                resolver.ContractVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                RequiredString(
                    compatibility,
                    "contractCapabilitiesCanonicalSha256"),
                resolver.ContractSha256,
                StringComparison.Ordinal) ||
            RequiredInt32(compatibility, "registryVersion") !=
                resolver.RegistryVersion ||
            !string.Equals(
                RequiredString(compatibility, "registryCanonicalSha256"),
                resolver.RegistrySha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Embedded WebUI compatibility identity does not match release constants.");
        }

        if (buildIdentity.IsInternalTest)
        {
            if (!string.Equals(
                    releaseStatus,
                    "development",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "An internal test build requires development release constants.");
            }
        }
        else
        {
            VerifyOfficialManagedAssembly(root, releaseStatus, assembly);
        }

        return resolver;
    }

    private static void VerifyOfficialManagedAssembly(
        JsonElement releaseConstants,
        string releaseStatus,
        Assembly assembly)
    {
        if (!string.Equals(releaseStatus, "candidate", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "An official build requires candidate release constants.");
        }

        var distribution = RequiredObject(releaseConstants, "distribution");
        var signing = RequiredObject(distribution, "signing");
        var expectedSubject = RequiredString(signing, "certificateSubject");
        var path = assembly.Location;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException(
                "The managed launcher assembly path is unavailable.");
        }

        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.RandomAccess);
        var evidence = WindowsWebView2AuthenticodeVerifier
            .InspectSigner(path, handle);
        if (!evidence.IsSignatureValid ||
            !evidence.HasTrustedTimestamp ||
            !string.Equals(
                evidence.Subject,
                expectedSubject,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The managed launcher assembly signature is not the expected release identity.");
        }
    }

    private static byte[] ReadEmbeddedResource(
        Assembly assembly,
        string name)
    {
        using var stream = assembly.GetManifestResourceStream(name) ??
            throw new InvalidDataException(
                "An embedded launcher integrity resource is missing.");
        if (stream.Length is <= 0 or > MaximumEmbeddedResourceBytes)
        {
            throw new InvalidDataException(
                "An embedded launcher integrity resource has an invalid size.");
        }

        using var buffer = new MemoryStream(checked((int)stream.Length));
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static JsonElement RequiredObject(
        JsonElement parent,
        string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Release constants property is invalid: {name}");
        }

        return value;
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"Release constants property is invalid: {name}");
        }

        return value.GetString()!;
    }

    private static int RequiredInt32(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            !value.TryGetInt32(out var result) ||
            result <= 0)
        {
            throw new InvalidDataException(
                $"Release constants property is invalid: {name}");
        }

        return result;
    }
}
