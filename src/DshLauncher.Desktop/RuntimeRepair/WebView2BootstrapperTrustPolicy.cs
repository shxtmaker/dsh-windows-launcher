using System.Reflection;
using System.Text.Json;

namespace DshLauncher.Desktop.RuntimeRepair;

internal readonly record struct WebView2BootstrapperTrustPolicy
{
    private const string ResourceName = "DshLauncher.Desktop.ReleaseConstants.json";

    public WebView2BootstrapperTrustPolicy(string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (sha256.Length != 64 ||
            sha256.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "The WebView2 bootstrapper SHA-256 is invalid.",
                nameof(sha256));
        }

        Sha256 = sha256.ToUpperInvariant();
    }

    public string Sha256 { get; }

    public static WebView2BootstrapperTrustPolicy LoadEmbedded() =>
        LoadEmbedded(typeof(WebView2BootstrapperTrustPolicy).Assembly);

    internal static WebView2BootstrapperTrustPolicy LoadEmbedded(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        using var stream = assembly.GetManifestResourceStream(ResourceName) ??
            throw new InvalidOperationException("Embedded release constants are missing.");
        using var document = JsonDocument.Parse(stream);
        var sha256 = document.RootElement
            .GetProperty("dependencyBaseline")
            .GetProperty("webView2Runtime")
            .GetProperty("bootstrapper")
            .GetProperty("sha256")
            .GetString();
        return new WebView2BootstrapperTrustPolicy(
            sha256 ?? throw new InvalidOperationException(
                "The embedded WebView2 bootstrapper hash is missing."));
    }
}
