using DshLauncher.Core;
using Xunit;

namespace DshLauncher.Core.Tests;

[Trait("triggerTags", "VFY-01,VFY-07")]
public sealed class LauncherBuildIdentityTests
{
    [Fact]
    public void CurrentUsesTheSelectedFlavorAssemblyMetadata()
    {
        var identity = LauncherBuildIdentity.Current;
        var expected = identity.BuildFlavor switch
        {
            "InternalTest" => InternalTest,
            "Official" => Official,
            _ => throw new Xunit.Sdk.XunitException(
                $"Unexpected build flavor: {identity.BuildFlavor}"),
        };

        Assert.Equal(expected.BuildFlavor, identity.BuildFlavor);
        Assert.Equal(expected.IsInternalTest, identity.IsInternalTest);
        Assert.Equal(expected.ProductName, identity.ProductName);
        Assert.Equal(expected.ExecutableBaseName, identity.ExecutableBaseName);
        Assert.Equal(expected.ApplicationDataId, identity.ApplicationDataId);
        Assert.Equal(expected.SingleInstanceBaseName, identity.SingleInstanceBaseName);
        Assert.Equal(expected.InstallerAppId, identity.InstallerAppId);
        Assert.Equal(expected.InstallDirectoryName, identity.InstallDirectoryName);
        Assert.Equal(expected.DataOwnerId, identity.DataOwnerId);
        Assert.Equal(expected.InstallOwnerId, identity.InstallOwnerId);
    }

    [Fact]
    public void SelectedFlavorIsFullySeparatedFromTheOtherFlavor()
    {
        var identity = LauncherBuildIdentity.Current;
        var other = identity.IsInternalTest ? Official : InternalTest;

        Assert.NotEqual(other.ProductName, identity.ProductName);
        Assert.NotEqual(other.ExecutableBaseName, identity.ExecutableBaseName);
        Assert.NotEqual(other.ApplicationDataId, identity.ApplicationDataId);
        Assert.NotEqual(other.SingleInstanceBaseName, identity.SingleInstanceBaseName);
        Assert.NotEqual(other.InstallerAppId, identity.InstallerAppId);
        Assert.NotEqual(other.InstallDirectoryName, identity.InstallDirectoryName);
        Assert.NotEqual(other.DataOwnerId, identity.DataOwnerId);
        Assert.NotEqual(other.InstallOwnerId, identity.InstallOwnerId);
    }

    private static ExpectedIdentity InternalTest { get; } = new(
        "InternalTest",
        true,
        "DSH Windows Launcher (INTERNAL TEST)",
        "DshWindowsLauncher.InternalTest",
        "DshWindowsLauncher.InternalTest",
        "DshWindowsLauncher.InternalTest.SingleInstance",
        "F3418DD7-58B7-4E0D-B0F7-D77C52FDF91C",
        "DshWindowsLauncher.InternalTest",
        "DshWindowsLauncher.InternalTest:v1",
        "DshWindowsLauncher.InternalTest:install:v1");

    private static ExpectedIdentity Official { get; } = new(
        "Official",
        false,
        "DSH Windows Launcher",
        "DshWindowsLauncher",
        "DshWindowsLauncher",
        "DshWindowsLauncher.SingleInstance",
        "4440FC88-98CA-403E-8E20-3DFEBEF0E609",
        "DshWindowsLauncher",
        "DshWindowsLauncher:v1",
        "DshWindowsLauncher:install:v1");

    private sealed record ExpectedIdentity(
        string BuildFlavor,
        bool IsInternalTest,
        string ProductName,
        string ExecutableBaseName,
        string ApplicationDataId,
        string SingleInstanceBaseName,
        string InstallerAppId,
        string InstallDirectoryName,
        string DataOwnerId,
        string InstallOwnerId);
}
