using DshLauncher.Platform.Windows;
using Xunit;

namespace DshLauncher.Platform.Windows.Tests;

[Trait("triggerTags", "VFY-07")]
public sealed class InstallPathPolicyTests
{
    [Fact]
    public void MissingDirectoryOnWritableFixedVolumeWithEnoughSpaceIsEligible()
    {
        var facts = EligibleFacts() with
        {
            DirectoryExists = false,
        };

        var result = InstallPathPolicy.Evaluate(facts);

        Assert.Equal(InstallPathQualification.Eligible, result.Qualification);
    }

    [Fact]
    public void ExistingEmptyDirectoryIsEligible()
    {
        var facts = EligibleFacts() with
        {
            DirectoryExists = true,
            DirectoryIsEmpty = true,
        };

        var result = InstallPathPolicy.Evaluate(facts);

        Assert.Equal(InstallPathQualification.Eligible, result.Qualification);
    }

    [Theory]
    [InlineData(InstallVolumeKind.Network)]
    [InlineData(InstallVolumeKind.Removable)]
    [InlineData(InstallVolumeKind.CdRom)]
    [InlineData(InstallVolumeKind.Ram)]
    [InlineData(InstallVolumeKind.Unknown)]
    public void OnlyLocalFixedVolumesAreEligible(InstallVolumeKind volumeKind)
    {
        var result = InstallPathPolicy.Evaluate(EligibleFacts() with
        {
            VolumeKind = volumeKind,
        });

        Assert.Equal(
            InstallPathQualification.NotLocalFixedVolume,
            result.Qualification);
    }

    [Fact]
    public void UncDeviceAndUnresolvedPathsAreRejectedBeforeVolumeChecks()
    {
        Assert.Equal(
            InstallPathQualification.UncPath,
            InstallPathPolicy.Evaluate(EligibleFacts() with
            {
                IsUnc = true,
            }).Qualification);
        Assert.Equal(
            InstallPathQualification.DevicePath,
            InstallPathPolicy.Evaluate(EligibleFacts() with
            {
                IsDevicePath = true,
            }).Qualification);
        Assert.Equal(
            InstallPathQualification.FinalPathUnresolved,
            InstallPathPolicy.Evaluate(EligibleFacts() with
            {
                FinalPathResolved = false,
            }).Qualification);
    }

    [Fact]
    public void ReparsePointsAndNonEmptyDirectoriesAreNeverTakenOver()
    {
        Assert.Equal(
            InstallPathQualification.ReparsePoint,
            InstallPathPolicy.Evaluate(EligibleFacts() with
            {
                ContainsReparsePoint = true,
            }).Qualification);
        Assert.Equal(
            InstallPathQualification.DirectoryNotEmpty,
            InstallPathPolicy.Evaluate(EligibleFacts() with
            {
                DirectoryExists = true,
                DirectoryIsEmpty = false,
            }).Qualification);
    }

    [Fact]
    public void WriteAndSpaceFailuresAreExplicit()
    {
        Assert.Equal(
            InstallPathQualification.NotWritable,
            InstallPathPolicy.Evaluate(EligibleFacts() with
            {
                IsWritable = false,
            }).Qualification);
        Assert.Equal(
            InstallPathQualification.InsufficientSpace,
            InstallPathPolicy.Evaluate(EligibleFacts() with
            {
                AvailableBytes = 99,
                RequiredBytes = 100,
            }).Qualification);
    }

    private static InstallPathFacts EligibleFacts() => new(
        IsFullyQualified: true,
        IsUnc: false,
        IsDevicePath: false,
        FinalPathResolved: true,
        VolumeKind: InstallVolumeKind.Fixed,
        ContainsReparsePoint: false,
        DirectoryExists: false,
        DirectoryIsEmpty: true,
        IsWritable: true,
        AvailableBytes: 10_000,
        RequiredBytes: 1_000);
}
