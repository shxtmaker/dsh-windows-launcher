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
}
