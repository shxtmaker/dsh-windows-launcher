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
        Assert.Equal(
            new[]
            {
                TargetContentState.Initializing,
                TargetContentState.Loading,
                TargetContentState.Ready,
            },
            states);
        Assert.All(states, state => Assert.True(Enum.IsDefined(state)));
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

    private static TargetContentHost CreateHost(FakeTargetContentRuntime runtime)
    {
        var binding = new TargetContentBinding(
            Guid.Parse("54f02e80-2c92-44d8-8f7f-b0fc118c93fa"),
            new Uri("http://192.168.10.20:3080/"),
            @"C:\Users\test\AppData\Local\DshWindowsLauncher\targets\54f02e802c9244d88f7fb0fc118c93fa\udf");

        return new TargetContentHost(binding, runtime);
    }

    private sealed class FakeTargetContentRuntime : ITargetContentRuntime
    {
        private Func<TargetRuntimeSignal, CancellationToken, ValueTask>? _signalSink;

        public bool CompleteInitialNavigation { get; init; }

        public bool CompleteRecovery { get; set; }

        public ValueTask InitializeAsync(
            TargetContentBinding binding,
            TargetContentSecurityPolicy policy,
            Func<TargetRuntimeSignal, CancellationToken, ValueTask> signalSink,
            CancellationToken cancellationToken)
        {
            _signalSink = signalSink;
            return ValueTask.CompletedTask;
        }

        public async ValueTask NavigateToRootAsync(
            CancellationToken cancellationToken)
        {
            if (CompleteInitialNavigation)
            {
                await EmitAsync(TargetRuntimeSignal.Ready(), cancellationToken);
            }
        }

        public async ValueTask ReloadAsync(CancellationToken cancellationToken)
        {
            if (CompleteRecovery)
            {
                await EmitAsync(TargetRuntimeSignal.Ready(), cancellationToken);
            }
        }

        public async ValueTask RecreateWebViewAsync(
            CancellationToken cancellationToken)
        {
            if (CompleteRecovery)
            {
                await EmitAsync(TargetRuntimeSignal.Ready(), cancellationToken);
            }
        }

        public async ValueTask RecreateEnvironmentAsync(
            CancellationToken cancellationToken)
        {
            if (CompleteRecovery)
            {
                await EmitAsync(TargetRuntimeSignal.Ready(), cancellationToken);
            }
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
}
