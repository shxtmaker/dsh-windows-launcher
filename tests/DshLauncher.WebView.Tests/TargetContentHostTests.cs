using DshLauncher.Compatibility;
using DshLauncher.WebView;
using Xunit;

namespace DshLauncher.WebView.Tests;

[Trait("triggerTags", "VFY-06")]
public sealed class TargetContentHostTests
{
    [Fact]
    public async Task DownloadBlockPublishesADistinctPresentationFailure()
    {
        var runtime = new FakeTargetContentRuntime
        {
            CompleteInitialNavigation = true,
        };
        await using var host = CreateHost(runtime);
        await host.OpenAsync(TestContext.Current.CancellationToken);

        await runtime.EmitAsync(
            TargetRuntimeSignal.DownloadBlocked(),
            TestContext.Current.CancellationToken);

        Assert.Equal(TargetContentState.Blocked, host.State);
        Assert.Equal(TargetContentFailureKind.Download, host.Failure?.Kind);
    }

    [Fact]
    public async Task OpenPublishesOnlySanitizedLifecycleStates()
    {
        var runtime = new FakeTargetContentRuntime
        {
            CompleteInitialNavigation = true,
        };
        await using var host = CreateHost(runtime);
        var states = new List<TargetContentState>();
        host.StateChanged += (_, change) => states.Add(change.State);

        await host.OpenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TargetContentState.Ready, host.State);
        Assert.Equal(TargetContentState.Initializing, states[0]);
        Assert.Equal(TargetContentState.Ready, states[^1]);
        Assert.Contains(TargetContentState.Loading, states);
        Assert.All(states, state => Assert.True(Enum.IsDefined(state)));
        Assert.Equal(
            ["Initialize", "BeginPageCapabilityCycle", "ActivatePageCapability", "NavigateToRoot"],
            runtime.Trace);
    }

    [Fact]
    public async Task RepeatedRendererFailuresStopAfterReloadAndOneRecreation()
    {
        var runtime = new FakeTargetContentRuntime
        {
            CompleteInitialNavigation = true,
            CompleteRecovery = false,
        };
        await using var host = CreateHost(runtime);
        await host.OpenAsync(TestContext.Current.CancellationToken);

        await runtime.EmitAsync(
            TargetRuntimeSignal.RendererFailed(),
            TestContext.Current.CancellationToken);
        Assert.Equal(TargetContentState.Recovering, host.State);

        await runtime.EmitAsync(
            TargetRuntimeSignal.RendererFailed(),
            TestContext.Current.CancellationToken);
        Assert.Equal(TargetContentState.Recovering, host.State);

        await runtime.EmitAsync(
            TargetRuntimeSignal.RendererFailed(),
            TestContext.Current.CancellationToken);
        Assert.Equal(TargetContentState.Failed, host.State);
        Assert.Equal(TargetContentFailureKind.RendererProcess, host.Failure?.Kind);
    }

    [Fact]
    public async Task BrowserEnvironmentIsRecreatedOnlyOncePerFailureEpisode()
    {
        var runtime = new FakeTargetContentRuntime
        {
            CompleteInitialNavigation = true,
            CompleteRecovery = false,
        };
        await using var host = CreateHost(runtime);
        await host.OpenAsync(TestContext.Current.CancellationToken);

        await runtime.EmitAsync(
            TargetRuntimeSignal.BrowserFailed(),
            TestContext.Current.CancellationToken);
        Assert.Equal(TargetContentState.Recovering, host.State);

        await runtime.EmitAsync(
            TargetRuntimeSignal.BrowserFailed(),
            TestContext.Current.CancellationToken);
        Assert.Equal(TargetContentState.Failed, host.State);
        Assert.Equal(TargetContentFailureKind.BrowserProcess, host.Failure?.Kind);
    }

    [Fact]
    public async Task ReadyAfterRecoveryStartsANewFiniteFailureEpisode()
    {
        var runtime = new FakeTargetContentRuntime
        {
            CompleteInitialNavigation = true,
            CompleteRecovery = true,
        };
        await using var host = CreateHost(runtime);
        await host.OpenAsync(TestContext.Current.CancellationToken);

        await runtime.EmitAsync(
            TargetRuntimeSignal.RendererFailed(),
            TestContext.Current.CancellationToken);
        Assert.Equal(TargetContentState.Ready, host.State);

        runtime.CompleteRecovery = false;
        await runtime.EmitAsync(
            TargetRuntimeSignal.RendererFailed(),
            TestContext.Current.CancellationToken);
        Assert.Equal(TargetContentState.Recovering, host.State);

        await runtime.EmitAsync(
            TargetRuntimeSignal.RendererFailed(),
            TestContext.Current.CancellationToken);
        Assert.Equal(TargetContentState.Recovering, host.State);
    }

    [Fact]
    public async Task CloseIsIdempotentAndTheBindingCanNeverBeReusedForAnotherTarget()
    {
        var runtime = new FakeTargetContentRuntime
        {
            CompleteInitialNavigation = true,
        };
        await using var host = CreateHost(runtime);
        var original = host.Binding;
        await host.OpenAsync(TestContext.Current.CancellationToken);

        await host.CloseAsync(TestContext.Current.CancellationToken);
        await host.CloseAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TargetContentState.Closed, host.State);
        Assert.Same(original, host.Binding);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await host.OpenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SecurityRejectionPublishesOneBlockedStateWithoutAFalseFailureState()
    {
        var runtime = new FakeTargetContentRuntime
        {
            CompleteInitialNavigation = true,
        };
        await using var host = CreateHost(runtime);
        await host.OpenAsync(TestContext.Current.CancellationToken);
        var changes = new List<TargetContentStateChangedEventArgs>();
        host.StateChanged += (_, change) => changes.Add(change);

        await runtime.EmitAsync(
            TargetRuntimeSignal.Blocked(),
            TestContext.Current.CancellationToken);

        var change = Assert.Single(changes);
        Assert.Equal(TargetContentState.Blocked, change.State);
        Assert.Equal(TargetContentFailureKind.SecurityPolicy, change.Failure?.Kind);
        Assert.Equal(TargetContentState.Blocked, host.State);
    }

    [Fact]
    public async Task SecurityRejectionIsNotOverwrittenByALateReadySignal()
    {
        var runtime = new FakeTargetContentRuntime();
        await using var host = CreateHost(runtime);
        await host.OpenAsync(TestContext.Current.CancellationToken);

        await runtime.EmitAsync(
            TargetRuntimeSignal.Blocked(),
            TestContext.Current.CancellationToken);
        await runtime.EmitAsync(
            TargetRuntimeSignal.Ready(),
            TestContext.Current.CancellationToken);

        Assert.Equal(TargetContentState.Blocked, host.State);
        Assert.Equal(TargetContentFailureKind.SecurityPolicy, host.Failure?.Kind);
    }

    [Fact]
    public async Task AuthenticationInvalidationPublishesADistinctSanitizedFailure()
    {
        var runtime = new FakeTargetContentRuntime
        {
            CompleteInitialNavigation = true,
        };
        await using var host = CreateHost(runtime);
        await host.OpenAsync(TestContext.Current.CancellationToken);

        await runtime.EmitAsync(
            TargetRuntimeSignal.AuthenticationInvalid(),
            TestContext.Current.CancellationToken);

        Assert.Equal(TargetContentState.Failed, host.State);
        Assert.Equal(TargetContentFailureKind.Authentication, host.Failure?.Kind);
    }

    [Fact]
    public async Task PageNavigationRequestRunsANewCompleteCapabilityCycle()
    {
        var runtime = new FakeTargetContentRuntime
        {
            CompleteInitialNavigation = true,
            CompleteRecovery = true,
        };
        await using var host = CreateHost(runtime);
        await host.OpenAsync(TestContext.Current.CancellationToken);

        await runtime.EmitAsync(
            TargetRuntimeSignal.PageCapabilityCycleRequested(),
            TestContext.Current.CancellationToken);

        Assert.Equal(TargetContentState.Ready, host.State);
        Assert.Equal(
            [
                "Initialize",
                "BeginPageCapabilityCycle",
                "ActivatePageCapability",
                "NavigateToRoot",
                "BeginPageCapabilityCycle",
                "ActivatePageCapability",
                "NavigateToRoot",
            ],
            runtime.Trace);
    }

    [Fact]
    public async Task SuspensionRemovesTheActivatedSnapshotBeforeRevocation()
    {
        var runtime = new FakeTargetContentRuntime
        {
            CompleteInitialNavigation = true,
        };
        await using var host = CreateHost(runtime);
        await host.OpenAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(runtime.ActivatedSnapshot);

        await host.SuspendPageCapabilitiesAsync(TestContext.Current.CancellationToken);

        Assert.Null(runtime.ActivatedSnapshot);
        Assert.Equal("SuspendPageCapabilities", runtime.Trace[^1]);
        Assert.Equal(TargetContentState.Loading, host.State);
    }

    [Theory]
    [InlineData(ExternalCapabilityDecision.Accepted, CompatibilityLevel.Extended)]
    [InlineData(ExternalCapabilityDecision.Rejected, CompatibilityLevel.Base)]
    public async Task ExternalCapabilitiesRequireConfirmationBeforeActivation(
        ExternalCapabilityDecision decision,
        CompatibilityLevel expectedLevel)
    {
        var runtime = new FakeTargetContentRuntime
        {
            CompleteInitialNavigation = true,
            DescriptorResponse = BoundedDescriptorResponse.Received(
                200,
                "application/json; charset=utf-8",
                wasRedirected: false,
                WebViewCompatibilityFixture.Descriptor()),
        };
        var confirmation = new FakeConfirmationPort(decision);
        await using var host = CreateHost(
            runtime,
            WebViewCompatibilityFixture.CreateResolver(
                WebViewCompatibilityFixture.Rule()),
            confirmation);

        await host.OpenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expectedLevel, host.CompatibilityStatus?.Level);
        Assert.Equal(expectedLevel, runtime.ActivatedSnapshot is { ExtensionCapabilities.Count: > 0 }
            ? CompatibilityLevel.Extended
            : CompatibilityLevel.Base);
        Assert.Single(confirmation.Requests);
    }

    private static TargetContentHost CreateHost(
        FakeTargetContentRuntime runtime,
        IPageCapabilityResolver? resolver = null,
        IExternalCapabilityConfirmationPort? confirmation = null)
    {
        var binding = new TargetContentBinding(
            Guid.Parse("54f02e80-2c92-44d8-8f7f-b0fc118c93fa"),
            new Uri("http://192.168.10.20:3080/"),
            @"C:\Users\test\AppData\Local\DshWindowsLauncher\targets\54f02e802c9244d88f7fb0fc118c93fa\udf");

        return new TargetContentHost(
            binding,
            runtime,
            resolver ?? WebViewCompatibilityFixture.CreateResolver(),
            confirmation ?? new FakeConfirmationPort());
    }

    private sealed class FakeTargetContentRuntime : ITargetContentRuntime
    {
        private Func<TargetRuntimeSignal, CancellationToken, ValueTask>? _signalSink;

        public bool CompleteInitialNavigation { get; init; }

        public bool CompleteRecovery { get; set; }

        public List<string> Trace { get; } = [];

        public PageCapabilitySnapshot? ActivatedSnapshot { get; private set; }

        public BoundedDescriptorResponse DescriptorResponse { get; init; } =
            BoundedDescriptorResponse.Missing();

        private int NavigationCount { get; set; }

        public ValueTask InitializeAsync(
            TargetContentBinding binding,
            Func<TargetRuntimeSignal, CancellationToken, ValueTask> signalSink,
            CancellationToken cancellationToken)
        {
            Trace.Add("Initialize");
            _signalSink = signalSink;
            return ValueTask.CompletedTask;
        }

        public ValueTask<BoundedDescriptorResponse> BeginPageCapabilityCycleAsync(
            CancellationToken cancellationToken)
        {
            Trace.Add("BeginPageCapabilityCycle");
            return ValueTask.FromResult(DescriptorResponse);
        }

        public ValueTask ActivatePageCapabilityAsync(
            PageCapabilitySnapshot snapshot,
            CancellationToken cancellationToken)
        {
            Trace.Add("ActivatePageCapability");
            ActivatedSnapshot = snapshot;
            return ValueTask.CompletedTask;
        }

        public ValueTask SuspendPageCapabilitiesAsync(
            CancellationToken cancellationToken)
        {
            Trace.Add("SuspendPageCapabilities");
            ActivatedSnapshot = null;
            return ValueTask.CompletedTask;
        }

        public async ValueTask NavigateToRootAsync(
            CancellationToken cancellationToken)
        {
            Trace.Add("NavigateToRoot");
            NavigationCount++;
            if (NavigationCount == 1
                    ? CompleteInitialNavigation
                    : CompleteRecovery)
            {
                await EmitAsync(TargetRuntimeSignal.Ready(), cancellationToken);
            }
        }

        public ValueTask RecreateWebViewAsync(
            CancellationToken cancellationToken)
        {
            Trace.Add("RecreateWebView");
            return ValueTask.CompletedTask;
        }

        public ValueTask RecreateEnvironmentAsync(
            CancellationToken cancellationToken)
        {
            Trace.Add("RecreateEnvironment");
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask EmitAsync(
            TargetRuntimeSignal signal,
            CancellationToken cancellationToken)
        {
            return _signalSink is null
                ? ValueTask.CompletedTask
                : _signalSink(signal, cancellationToken);
        }
    }

    private sealed class FakeConfirmationPort : IExternalCapabilityConfirmationPort
    {
        private readonly ExternalCapabilityDecision _decision;

        public FakeConfirmationPort(
            ExternalCapabilityDecision decision = ExternalCapabilityDecision.Rejected)
        {
            _decision = decision;
        }

        public List<ExternalCapabilityConfirmationRequest> Requests { get; } = [];

        public ValueTask<ExternalCapabilityDecision> ConfirmAsync(
            ExternalCapabilityConfirmationRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(_decision);
        }

        public ValueTask RevokeAsync(
            Guid targetId,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
