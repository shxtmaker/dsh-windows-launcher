using DshLauncher.Platform.Windows;
using Xunit;

namespace DshLauncher.Platform.Windows.Tests;

[Trait("triggerTags", "VFY-07")]
public sealed class CurrentUserSingleInstanceTests
{
    private const string TestPipeBaseName = "DshWindowsLauncher.Tests.SingleInstance";

    [Fact]
    public void ContractKeepsInstallerFacingNamesStable()
    {
        Assert.Equal(
            "--request-maintenance-exit",
            SingleInstanceContract.MaintenanceExitArgument);
    }

    [Fact]
    public void IdentityIsStableUserScopedAndDoesNotExposeTheSid()
    {
        const string firstSid = "S-1-5-21-111-222-333-1001";
        const string secondSid = "S-1-5-21-111-222-333-1002";

        var first = CurrentUserInstanceIdentity.FromSid(firstSid, TestPipeBaseName);
        var repeated = CurrentUserInstanceIdentity.FromSid(firstSid, TestPipeBaseName);
        var second = CurrentUserInstanceIdentity.FromSid(secondSid, TestPipeBaseName);

        Assert.Equal(first, repeated);
        Assert.NotEqual(first.PipeName, second.PipeName);
        Assert.StartsWith(
            $"{TestPipeBaseName}.",
            first.PipeName,
            StringComparison.Ordinal);
        Assert.DoesNotContain(firstSid, first.PipeName, StringComparison.Ordinal);
        Assert.DoesNotContain("Local\\", first.PipeName, StringComparison.Ordinal);
    }

    [Fact]
    public void SameUserHasSeparateInternalTestAndOfficialPipeNames()
    {
        const string sid = "S-1-5-21-111-222-333-1001";

        var internalTest = CurrentUserInstanceIdentity.FromSid(
            sid,
            "DshWindowsLauncher.InternalTest.SingleInstance");
        var official = CurrentUserInstanceIdentity.FromSid(
            sid,
            "DshWindowsLauncher.SingleInstance");

        Assert.NotEqual(internalTest.PipeName, official.PipeName);
        Assert.StartsWith(
            "DshWindowsLauncher.InternalTest.SingleInstance.",
            internalTest.PipeName,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "DshWindowsLauncher.SingleInstance.",
            official.PipeName,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SingleInstanceRequestKind.Activate, null, "activate")]
    [InlineData(SingleInstanceRequestKind.OpenTarget, "45f19a8f-f014-4fbd-9da9-dd2c768ca7f2", "open-target:45f19a8ff0144fbd9da9dd2c768ca7f2")]
    [InlineData(SingleInstanceRequestKind.MaintenanceExit, null, "maintenance-exit")]
    public void RequestCodecIsBoundedAndCanonical(
        SingleInstanceRequestKind kind,
        string? targetIdText,
        string expected)
    {
        Guid? targetId = targetIdText is null
            ? null
            : Guid.Parse(targetIdText);
        var request = SingleInstanceRequest.Create(kind, targetId);

        var encoded = SingleInstanceRequestCodec.Encode(request);
        var decoded = SingleInstanceRequestCodec.Decode(encoded);

        Assert.Equal(expected, encoded);
        Assert.Equal(request, decoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("open-target")]
    [InlineData("open-target:not-a-guid")]
    [InlineData("activate:extra")]
    [InlineData("unknown")]
    public void RequestCodecRejectsMalformedMessages(string encoded)
    {
        Assert.Throws<FormatException>(() =>
            SingleInstanceRequestCodec.Decode(encoded));
    }

    [Fact]
    public void MaintenanceArgumentsMustMatchExactly()
    {
        Assert.True(SingleInstanceContract.IsMaintenanceExitRequest(
            [SingleInstanceContract.MaintenanceExitArgument]));
        Assert.True(SingleInstanceContract.TryGetMaintenanceExitTimeout(
            [
                SingleInstanceContract.MaintenanceExitArgument,
                SingleInstanceContract.TimeoutSecondsArgument,
                "30",
            ],
            out var timeout));
        Assert.Equal(TimeSpan.FromSeconds(30), timeout);
        Assert.False(SingleInstanceContract.IsMaintenanceExitRequest([]));
        Assert.False(SingleInstanceContract.IsMaintenanceExitRequest(
            [SingleInstanceContract.MaintenanceExitArgument, "unexpected"]));
        Assert.False(SingleInstanceContract.IsMaintenanceExitRequest(
            ["--REQUEST-MAINTENANCE-EXIT"]));
        Assert.False(SingleInstanceContract.TryGetMaintenanceExitTimeout(
            [
                SingleInstanceContract.MaintenanceExitArgument,
                SingleInstanceContract.TimeoutSecondsArgument,
                "0",
            ],
            out _));
    }

    [Fact]
    public async Task CurrentUserPipeAcceptsOnePrimaryAndForwardsARequest()
    {
        var identity = CurrentUserInstanceIdentity.FromSid(
            $"S-1-5-21-111-222-333-{Random.Shared.Next(10000, 99999)}",
            TestPipeBaseName);
        var received = new TaskCompletionSource<SingleInstanceRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var primary = CurrentUserSingleInstance.TryStart(
            identity,
            (request, _) =>
            {
                received.TrySetResult(request);
                return ValueTask.FromResult(SingleInstanceResponse.Accepted);
            });
        await using var duplicate = CurrentUserSingleInstance.TryStart(
            identity,
            static (_, _) =>
                ValueTask.FromResult(SingleInstanceResponse.Accepted));

        Assert.NotNull(primary);
        Assert.Null(duplicate);

        var request = SingleInstanceRequest.Create(
            SingleInstanceRequestKind.OpenTarget,
            new Guid("fe57d2f0-d2a7-44aa-8cb3-75b061e7e44e"));
        var response = await CurrentUserSingleInstance.ForwardAsync(
            identity,
            request,
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.Equal(SingleInstanceResponse.Accepted, response);
        Assert.Equal(request, await received.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken));
    }
}
