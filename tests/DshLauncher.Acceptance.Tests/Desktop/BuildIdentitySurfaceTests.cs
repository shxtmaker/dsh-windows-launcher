using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using DshLauncher.Core;
using DshLauncher.Desktop;
using DshLauncher.Desktop.RuntimeRepair;
using DshLauncher.WebView;
using Xunit;

namespace DshLauncher.Acceptance.Tests.Desktop;

[Trait("triggerTags", "VFY-01,VFY-07")]
public sealed class BuildIdentitySurfaceTests
{
    [Fact]
    public void TargetWindowTitleKeepsTheSelectedBuildIdentityVisible()
    {
        const string targetName = "Ubuntu test target";

        var title = TargetWindow.ComposeTitle(targetName);

        Assert.Equal(
            $"{LauncherBuildIdentity.Current.ProductName} · {targetName}",
            title);
    }

    [Fact]
    public void WebView2ProbeUsesTheSelectedBuildIdentityTemporaryRoot()
    {
        var probeFolder = InstalledWebView2RuntimeProbe.CreateProbeUserDataFolder();
        var expectedRoot = Path.Combine(
            Path.GetTempPath(),
            LauncherBuildIdentity.Current.ApplicationDataId);

        Assert.Equal(expectedRoot, Directory.GetParent(probeFolder)!.FullName);
        Assert.StartsWith(
            "WebView2RuntimeProbe.",
            Path.GetFileName(probeFolder),
            StringComparison.Ordinal);
    }

    [Fact]
    public void InternalBuildLoadsTheEmbeddedCompatibilityIdentity()
    {
        var resolver = LauncherApplicationIntegrity.LoadCompatibilityResolver(
            LauncherBuildIdentity.Current);

        Assert.Equal("1.1.0", resolver.ContractVersion);
        Assert.Equal(
            "4fe5ce356b3745c0bbca4bbe76e303d326355d8cf51b27546c91801b1669fe64",
            resolver.ContractSha256);
        Assert.Equal(3, resolver.RegistryVersion);
        Assert.Equal(
            "0c5949d0cb6566e2f39bb37a617f4e686906e001046b0f49ca440e5c3aa621d8",
            resolver.RegistrySha256);
    }

    [Fact]
    public void EmbeddedOfficialReleaseUriCanInitializeTheUpdateService()
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream(
            "DshLauncher.Desktop.ReleaseConstants.json");
        Assert.NotNull(stream);
        using var document = JsonDocument.Parse(stream);
        var value = document.RootElement
            .GetProperty("distribution")
            .GetProperty("officialReleaseUri")
            .GetString();
        Assert.NotNull(value);

        var service = new UpdatePageService(
            new Uri(value),
            () => null,
            Dispatcher.CurrentDispatcher,
            new UnusedExternalUriLauncher());

        Assert.NotNull(service);
    }

    private sealed class UnusedExternalUriLauncher : ITargetExternalUriLauncher
    {
        public ValueTask OpenAsync(Uri destination, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
