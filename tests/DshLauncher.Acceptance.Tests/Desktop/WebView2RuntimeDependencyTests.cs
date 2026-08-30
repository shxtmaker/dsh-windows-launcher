using DshLauncher.Desktop.RuntimeRepair;
using Xunit;

namespace DshLauncher.Acceptance.Tests.Desktop;

public sealed class WebView2RuntimeDependencyTests
{
    [Theory]
    [Trait("triggerTags", "VFY-07,RS-14")]
    [InlineData("151.0.4129.49", WebView2RuntimeStatus.BelowMinimum)]
    [InlineData("151.0.4129.50", WebView2RuntimeStatus.Ready)]
    [InlineData("151.0.4130.0", WebView2RuntimeStatus.Ready)]
    [InlineData("152.0.0.0", WebView2RuntimeStatus.Ready)]
    public void CheckEnforcesTheMinimumRuntimeVersion(
        string installedVersion,
        WebView2RuntimeStatus expectedStatus)
    {
        var dependency = CreateDependency(new ScriptedRuntimeProbe(installedVersion));

        var result = dependency.Check();

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(installedVersion, result.InstalledVersion);
    }

    [Fact]
    [Trait("triggerTags", "VFY-07,RS-14")]
    public void MissingRuntimeRequiresRepair()
    {
        var dependency = CreateDependency(new ScriptedRuntimeProbe((string?)null));

        var result = dependency.Check();

        Assert.Equal(WebView2RuntimeStatus.Missing, result.Status);
        Assert.False(result.IsReady);
    }

    [Fact]
    [Trait("triggerTags", "VFY-07,RS-14")]
    public void InstalledButUnusableRuntimeRequiresRepair()
    {
        var dependency = CreateDependency(
            new ScriptedRuntimeProbe(
                new WebView2RuntimeObservation("151.0.4129.50", IsHealthy: false)));

        var result = dependency.Check();

        Assert.Equal(WebView2RuntimeStatus.Corrupted, result.Status);
        Assert.Equal("151.0.4129.50", result.InstalledVersion);
        Assert.False(result.IsReady);
    }

    [Fact]
    [Trait("triggerTags", "VFY-07,RS-14")]
    public void InstalledProbePreservesTheVersionWhenEnvironmentCreationFails()
    {
        var probe = new InstalledWebView2RuntimeProbe(
            () => "151.0.4129.50",
            () => throw new InvalidOperationException("corrupted runtime"));

        var result = probe.Inspect();

        Assert.Equal("151.0.4129.50", result.AvailableVersion);
        Assert.False(result.IsHealthy);
    }

    [Theory]
    [Trait("triggerTags", "VFY-07,RS-14")]
    [InlineData("")]
    [InlineData("151.0.4129")]
    [InlineData("151.0.4129.50 dev")]
    [InlineData("151.0.invalid.50")]
    [InlineData("151.0.2147483648.50")]
    public void InvalidRuntimeVersionsFailClosed(string installedVersion)
    {
        var dependency = CreateDependency(new ScriptedRuntimeProbe(installedVersion));

        var result = dependency.Check();

        Assert.Equal(WebView2RuntimeStatus.InvalidVersion, result.Status);
        Assert.False(result.IsReady);
    }

    [Fact]
    [Trait("triggerTags", "VFY-07,RS-14")]
    public async Task MissingBootstrapperLeavesRepairRequired()
    {
        var runner = new ScriptedBootstrapperRunner(
            new WebView2BootstrapperRunResult(WebView2BootstrapperRunStatus.Missing));
        var dependency = CreateDependency(new ScriptedRuntimeProbe((string?)null), runner);

        var result = await dependency.RepairAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WebView2RuntimeRepairStatus.BootstrapperMissing, result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal(1, runner.InvocationCount);
    }

    [Fact]
    [Trait("triggerTags", "VFY-07,RS-14")]
    public async Task NonZeroInstallerExitLeavesRepairRequired()
    {
        var runner = new ScriptedBootstrapperRunner(
            new WebView2BootstrapperRunResult(
                WebView2BootstrapperRunStatus.NonZeroExit,
                ExitCode: 1603));
        var dependency = CreateDependency(new ScriptedRuntimeProbe((string?)null), runner);

        var result = await dependency.RepairAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WebView2RuntimeRepairStatus.InstallerFailed, result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal(1603, result.ExitCode);
    }

    [Fact]
    [Trait("triggerTags", "VFY-07,RS-14")]
    public async Task UntrustedBootstrapperLeavesRepairRequired()
    {
        var runner = new ScriptedBootstrapperRunner(
            new WebView2BootstrapperRunResult(
                WebView2BootstrapperRunStatus.Untrusted));
        var dependency = CreateDependency(new ScriptedRuntimeProbe((string?)null), runner);

        var result = await dependency.RepairAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            WebView2RuntimeRepairStatus.BootstrapperUntrusted,
            result.Status);
        Assert.False(result.Succeeded);
    }

    [Fact]
    [Trait("triggerTags", "VFY-07,RS-14")]
    public async Task SuccessfulInstallerIsReprobedBeforeStartupCanContinue()
    {
        var probe = new ScriptedRuntimeProbe(null, "151.0.4129.50");
        var runner = new ScriptedBootstrapperRunner(
            new WebView2BootstrapperRunResult(WebView2BootstrapperRunStatus.Completed));
        var dependency = CreateDependency(probe, runner);

        Assert.Equal(WebView2RuntimeStatus.Missing, dependency.Check().Status);
        var result = await dependency.RepairAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WebView2RuntimeRepairStatus.Succeeded, result.Status);
        Assert.True(result.Succeeded);
        Assert.Equal(WebView2RuntimeStatus.Ready, result.Runtime.Status);
        Assert.Equal(2, probe.InvocationCount);
    }

    [Fact]
    [Trait("triggerTags", "VFY-07,RS-14")]
    public async Task InstallerSuccessWithoutAValidRuntimeStillBlocksStartup()
    {
        var probe = new ScriptedRuntimeProbe(null, "151.0.4129.49");
        var runner = new ScriptedBootstrapperRunner(
            new WebView2BootstrapperRunResult(WebView2BootstrapperRunStatus.Completed));
        var dependency = CreateDependency(probe, runner);

        Assert.Equal(WebView2RuntimeStatus.Missing, dependency.Check().Status);
        var result = await dependency.RepairAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WebView2RuntimeRepairStatus.RuntimeStillUnavailable, result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal(WebView2RuntimeStatus.BelowMinimum, result.Runtime.Status);
    }

    private static WebView2RuntimeDependency CreateDependency(
        IWebView2RuntimeProbe probe,
        IWebView2BootstrapperRunner? runner = null) => new(
            probe,
            runner ?? new ScriptedBootstrapperRunner(
                new WebView2BootstrapperRunResult(WebView2BootstrapperRunStatus.Missing)));

    private sealed class ScriptedRuntimeProbe : IWebView2RuntimeProbe
    {
        private readonly Queue<WebView2RuntimeObservation> _observations;

        public ScriptedRuntimeProbe(params string?[] versions)
            : this(versions.Select(static version =>
                new WebView2RuntimeObservation(version, IsHealthy: version is not null)).ToArray())
        {
        }

        public ScriptedRuntimeProbe(params WebView2RuntimeObservation[] observations)
        {
            _observations = new Queue<WebView2RuntimeObservation>(observations);
        }

        public int InvocationCount { get; private set; }

        public WebView2RuntimeObservation Inspect()
        {
            InvocationCount++;
            return _observations.Count > 1
                ? _observations.Dequeue()
                : _observations.Peek();
        }
    }

    private sealed class ScriptedBootstrapperRunner : IWebView2BootstrapperRunner
    {
        private readonly WebView2BootstrapperRunResult _result;

        public ScriptedBootstrapperRunner(WebView2BootstrapperRunResult result)
        {
            _result = result;
        }

        public int InvocationCount { get; private set; }

        public ValueTask<WebView2BootstrapperRunResult> RunAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            return ValueTask.FromResult(_result);
        }
    }
}
