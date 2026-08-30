namespace DshLauncher.WebView;

public interface ITargetContentHost : IAsyncDisposable
{
    TargetContentBinding Binding { get; }

    TargetContentState State { get; }

    TargetContentFailure? Failure { get; }

    event EventHandler<TargetContentStateChangedEventArgs>? StateChanged;

    ValueTask OpenAsync(CancellationToken cancellationToken = default);

    ValueTask ReloadAsync(CancellationToken cancellationToken = default);

    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}

public interface ITargetContentRuntime : IAsyncDisposable
{
    ValueTask InitializeAsync(
        TargetContentBinding binding,
        TargetContentSecurityPolicy policy,
        Func<TargetRuntimeSignal, CancellationToken, ValueTask> signalSink,
        CancellationToken cancellationToken);

    ValueTask NavigateToRootAsync(CancellationToken cancellationToken);

    ValueTask ReloadAsync(CancellationToken cancellationToken);

    ValueTask RecreateWebViewAsync(CancellationToken cancellationToken);

    ValueTask RecreateEnvironmentAsync(CancellationToken cancellationToken);

    ValueTask CloseAsync(CancellationToken cancellationToken);
}

public sealed class TargetContentHost : ITargetContentHost
{
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _signalGate = new(1, 1);
    private readonly ITargetContentRuntime _runtime;
    private readonly TargetContentSecurityPolicy _securityPolicy;
    private readonly TargetContentRecoveryPolicy _recoveryPolicy;
    private TargetContentState _state = TargetContentState.Created;
    private TargetContentFailure? _failure;
    private int _rendererFailureCount;
    private int _browserFailureCount;
    private int _disposed;

    public TargetContentHost(
        TargetContentBinding binding,
        ITargetContentRuntime runtime,
        TargetContentRecoveryPolicy? recoveryPolicy = null)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _securityPolicy = new TargetContentSecurityPolicy(binding);
        _recoveryPolicy = recoveryPolicy ?? TargetContentRecoveryPolicy.Default;
    }

    public TargetContentBinding Binding { get; }

    public TargetContentState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public TargetContentFailure? Failure
    {
        get
        {
            lock (_stateLock)
            {
                return _failure;
            }
        }
    }

    public event EventHandler<TargetContentStateChangedEventArgs>? StateChanged;

    public async ValueTask OpenAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == TargetContentState.Closed)
            {
                throw new InvalidOperationException(
                    "A closed target content host cannot be reopened.");
            }

            if (State != TargetContentState.Created)
            {
                return;
            }

            PublishState(TargetContentState.Initializing);
            try
            {
                await _runtime.InitializeAsync(
                    Binding,
                    _securityPolicy,
                    HandleRuntimeSignalAsync,
                    cancellationToken).ConfigureAwait(false);

                PublishState(TargetContentState.Loading);
                await _runtime.NavigateToRootAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                PublishFailure(TargetContentFailureKind.Cancelled);
                throw;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                PublishFailure(TargetContentFailureKind.Initialization);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask ReloadAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is TargetContentState.Created or TargetContentState.Closed)
            {
                throw new InvalidOperationException(
                    "Only an opened target content host can be reloaded.");
            }

            PublishState(TargetContentState.Loading);
            try
            {
                await _runtime.ReloadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                PublishFailure(TargetContentFailureKind.Navigation);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask CloseAsync(
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == TargetContentState.Closed)
            {
                return;
            }

            PublishState(TargetContentState.Closed);
            await _runtime.CloseAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != TargetContentState.Closed)
            {
                PublishState(TargetContentState.Closed);
                await _runtime.CloseAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await _runtime.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
            _signalGate.Dispose();
        }
    }

    private async ValueTask HandleRuntimeSignalAsync(
        TargetRuntimeSignal signal,
        CancellationToken cancellationToken)
    {
        TargetRecoveryAction? recoveryAction = null;

        await _signalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == TargetContentState.Closed)
            {
                return;
            }

            switch (signal.Kind)
            {
                case TargetRuntimeSignalKind.NavigationStarted:
                    PublishState(TargetContentState.Loading);
                    break;
                case TargetRuntimeSignalKind.Ready:
                    _rendererFailureCount = 0;
                    _browserFailureCount = 0;
                    PublishState(TargetContentState.Ready);
                    break;
                case TargetRuntimeSignalKind.Blocked:
                    PublishState(
                        TargetContentState.Blocked,
                        new TargetContentFailure(
                            TargetContentFailureKind.SecurityPolicy));
                    break;
                case TargetRuntimeSignalKind.DownloadBlocked:
                    PublishState(
                        TargetContentState.Blocked,
                        new TargetContentFailure(
                            TargetContentFailureKind.Download));
                    break;
                case TargetRuntimeSignalKind.NavigationFailed:
                    PublishFailure(TargetContentFailureKind.Navigation);
                    break;
                case TargetRuntimeSignalKind.AuthenticationInvalid:
                    PublishFailure(TargetContentFailureKind.Authentication);
                    break;
                case TargetRuntimeSignalKind.ProcessFailed:
                    recoveryAction = PlanRecovery(signal.ProcessFailureKind);
                    break;
                default:
                    PublishFailure(TargetContentFailureKind.Runtime);
                    break;
            }
        }
        finally
        {
            _signalGate.Release();
        }

        if (recoveryAction is not null)
        {
            await ExecuteRecoveryAsync(
                recoveryAction.Value,
                signal.ProcessFailureKind,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private TargetRecoveryAction PlanRecovery(
        TargetProcessFailureKind failureKind)
    {
        var count = failureKind == TargetProcessFailureKind.Browser
            ? ++_browserFailureCount
            : ++_rendererFailureCount;
        var action = _recoveryPolicy.Decide(failureKind, count);

        if (action == TargetRecoveryAction.Fail)
        {
            PublishFailure(MapFailure(failureKind));
        }
        else
        {
            PublishState(TargetContentState.Recovering);
        }

        return action;
    }

    private async ValueTask ExecuteRecoveryAsync(
        TargetRecoveryAction action,
        TargetProcessFailureKind failureKind,
        CancellationToken cancellationToken)
    {
        if (action == TargetRecoveryAction.Fail)
        {
            return;
        }

        try
        {
            switch (action)
            {
                case TargetRecoveryAction.Reload:
                    await _runtime.ReloadAsync(cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case TargetRecoveryAction.RecreateWebView:
                    await _runtime.RecreateWebViewAsync(cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case TargetRecoveryAction.RecreateEnvironment:
                    await _runtime.RecreateEnvironmentAsync(cancellationToken)
                        .ConfigureAwait(false);
                    break;
                default:
                    PublishFailure(MapFailure(failureKind));
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            PublishFailure(MapFailure(failureKind));
        }
    }

    private void PublishFailure(TargetContentFailureKind kind)
    {
        PublishState(
            TargetContentState.Failed,
            new TargetContentFailure(kind));
    }

    private void PublishState(
        TargetContentState next,
        TargetContentFailure? failure = null)
    {
        EventHandler<TargetContentStateChangedEventArgs>? handler;
        lock (_stateLock)
        {
            if (_state == next && Equals(_failure, failure))
            {
                return;
            }

            _state = next;
            _failure = failure;
            handler = StateChanged;
        }

        if (handler is null)
        {
            return;
        }

        var args = new TargetContentStateChangedEventArgs(next, failure);
        foreach (EventHandler<TargetContentStateChangedEventArgs> subscriber in
                 handler.GetInvocationList())
        {
            try
            {
                subscriber(this, args);
            }
            catch (Exception)
            {
                // A presentation subscriber cannot corrupt host state.
            }
        }
    }

    private static TargetContentFailureKind MapFailure(
        TargetProcessFailureKind failureKind)
    {
        return failureKind switch
        {
            TargetProcessFailureKind.Renderer =>
                TargetContentFailureKind.RendererProcess,
            TargetProcessFailureKind.Browser =>
                TargetContentFailureKind.BrowserProcess,
            TargetProcessFailureKind.Unresponsive =>
                TargetContentFailureKind.Unresponsive,
            _ => TargetContentFailureKind.Runtime,
        };
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }
}

public enum TargetContentState
{
    Created,
    Initializing,
    Loading,
    Ready,
    Recovering,
    Blocked,
    Failed,
    Closed
}

public sealed record TargetContentFailure(TargetContentFailureKind Kind);

public enum TargetContentFailureKind
{
    Initialization,
    Navigation,
    Authentication,
    SecurityPolicy,
    RendererProcess,
    BrowserProcess,
    Unresponsive,
    Runtime,
    Download,
    Cancelled
}

public sealed class TargetContentStateChangedEventArgs : EventArgs
{
    public TargetContentStateChangedEventArgs(
        TargetContentState state,
        TargetContentFailure? failure)
    {
        State = state;
        Failure = failure;
    }

    public TargetContentState State { get; }

    public TargetContentFailure? Failure { get; }
}

public sealed record TargetRuntimeSignal(
    TargetRuntimeSignalKind Kind,
    TargetProcessFailureKind ProcessFailureKind)
{
    public static TargetRuntimeSignal NavigationStarted() =>
        new(TargetRuntimeSignalKind.NavigationStarted, TargetProcessFailureKind.Other);

    public static TargetRuntimeSignal Ready() =>
        new(TargetRuntimeSignalKind.Ready, TargetProcessFailureKind.Other);

    public static TargetRuntimeSignal Blocked() =>
        new(TargetRuntimeSignalKind.Blocked, TargetProcessFailureKind.Other);

    public static TargetRuntimeSignal DownloadBlocked() =>
        new(TargetRuntimeSignalKind.DownloadBlocked, TargetProcessFailureKind.Other);

    public static TargetRuntimeSignal NavigationFailed() =>
        new(TargetRuntimeSignalKind.NavigationFailed, TargetProcessFailureKind.Other);

    public static TargetRuntimeSignal AuthenticationInvalid() =>
        new(TargetRuntimeSignalKind.AuthenticationInvalid, TargetProcessFailureKind.Other);

    public static TargetRuntimeSignal RendererFailed() =>
        new(TargetRuntimeSignalKind.ProcessFailed, TargetProcessFailureKind.Renderer);

    public static TargetRuntimeSignal BrowserFailed() =>
        new(TargetRuntimeSignalKind.ProcessFailed, TargetProcessFailureKind.Browser);

    public static TargetRuntimeSignal Unresponsive() =>
        new(TargetRuntimeSignalKind.ProcessFailed, TargetProcessFailureKind.Unresponsive);
}

public enum TargetRuntimeSignalKind
{
    NavigationStarted,
    Ready,
    Blocked,
    DownloadBlocked,
    NavigationFailed,
    AuthenticationInvalid,
    ProcessFailed
}
