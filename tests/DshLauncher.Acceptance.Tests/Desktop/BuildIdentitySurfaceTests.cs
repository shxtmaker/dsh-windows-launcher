using System.IO;
using DshLauncher.Core;
using DshLauncher.Desktop;
using DshLauncher.Desktop.RuntimeRepair;
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

        Assert.Equal("1.0.0", resolver.ContractVersion);
        Assert.Equal(
            "a543d6f2d6bc736b89dbd430a8bae35efb536df2e87bc519e18bca39f491ecbb",
            resolver.ContractSha256);
        Assert.Equal(1, resolver.RegistryVersion);
        Assert.Equal(
            "7d71067cc626b90f6e7e0ef8df4c3de8417da55e42b4d1f100a5082c498287ad",
            resolver.RegistrySha256);
    }
}
