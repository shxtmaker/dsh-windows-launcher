using DshLauncher.Compatibility;

namespace DshLauncher.WebView;

public interface ITargetContentHost : IAsyncDisposable
{
    TargetContentBinding Binding { get; }

    TargetContentState State { get; }

    TargetContentFailure? Failure { get; }

    CompatibilityStatusDto? CompatibilityStatus { get; }

    event EventHandler<TargetContentStateChangedEventArgs>? StateChanged;

    ValueTask OpenAsync(CancellationToken cancellationToken = default);

    ValueTask ReloadAsync(CancellationToken cancellationToken = default);

    ValueTask SuspendPageCapabilitiesAsync(
        CancellationToken cancellationToken = default);

    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}

public interface ITargetContentRuntime : IAsyncDisposable
{
    ValueTask InitializeAsync(
        TargetContentBinding binding,
        Func<TargetRuntimeSignal, CancellationToken, ValueTask> signalSink,
        CancellationToken cancellationToken);

    ValueTask<BoundedDescriptorResponse> BeginPageCapabilityCycleAsync(
        CancellationToken cancellationToken);

    ValueTask SuspendPageCapabilitiesAsync(CancellationToken cancellationToken);

    ValueTask ActivatePageCapabilityAsync(
        PageCapabilitySnapshot snapshot,
        CancellationToken cancellationToken);

    ValueTask NavigateToRootAsync(CancellationToken cancellationToken);

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
    private readonly IPageCapabilityResolver _resolver;
    private readonly IExternalCapabilityConfirmationPort _confirmation;
    private readonly TargetContentRecoveryPolicy _recoveryPolicy;
    private TargetContentState _state = TargetContentState.Created;
    private TargetContentFailure? _failure;
    private CompatibilityStatusDto? _compatibilityStatus;
    private int _rendererFailureCount;
    private int _browserFailureCount;
    private int _disposed;

    public TargetContentHost(
        TargetContentBinding binding,
        ITargetContentRuntime runtime,
        IPageCapabilityResolver resolver,
        IExternalCapabilityConfirmationPort confirmation,
        TargetContentRecoveryPolicy? recoveryPolicy = null)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
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

    public CompatibilityStatusDto? CompatibilityStatus
    {
        get
        {
            lock (_stateLock)
            {
                return _compatibilityStatus;
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
                    HandleRuntimeSignalAsync,
                    cancellationToken).ConfigureAwait(false);

                PublishState(TargetContentState.Loading);
                await RunPageCapabilityCycleAsync(cancellationToken)
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
                await RunPageCapabilityCycleAsync(cancellationToken)
                    .ConfigureAwait(false);
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

    public async ValueTask SuspendPageCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is TargetContentState.Created or TargetContentState.Closed)
            {
                throw new InvalidOperationException(
                    "Only an opened target content host can be suspended.");
            }

            PublishState(TargetContentState.Loading);
            await _runtime.SuspendPageCapabilitiesAsync(cancellationToken)
                .ConfigureAwait(false);
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
        var pageCapabilityCycleRequested = false;

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
                case TargetRuntimeSignalKind.PageCapabilityCycleRequested:
                    PublishState(TargetContentState.Loading);
                    pageCapabilityCycleRequested = true;
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
            await ExecuteRecoverySerializedAsync(
                recoveryAction.Value,
                signal.ProcessFailureKind,
                cancellationToken).ConfigureAwait(false);
        }
        else if (pageCapabilityCycleRequested)
        {
            await ExecutePageCapabilityCycleSerializedAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask ExecutePageCapabilityCycleSerializedAsync(
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == TargetContentState.Closed)
            {
                return;
            }

            await RunPageCapabilityCycleAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            PublishFailure(TargetContentFailureKind.Navigation);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async ValueTask ExecuteRecoverySerializedAsync(
        TargetRecoveryAction action,
        TargetProcessFailureKind failureKind,
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == TargetContentState.Closed)
            {
                return;
            }

            await ExecuteRecoveryAsync(action, failureKind, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
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
                    await RunPageCapabilityCycleAsync(cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case TargetRecoveryAction.RecreateWebView:
                    await _runtime.RecreateWebViewAsync(cancellationToken)
                        .ConfigureAwait(false);
                    await RunPageCapabilityCycleAsync(cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case TargetRecoveryAction.RecreateEnvironment:
                    await _runtime.RecreateEnvironmentAsync(cancellationToken)
                        .ConfigureAwait(false);
                    await RunPageCapabilityCycleAsync(cancellationToken)
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

    private async ValueTask RunPageCapabilityCycleAsync(
        CancellationToken cancellationToken)
    {
        var descriptor = await _runtime.BeginPageCapabilityCycleAsync(
            cancellationToken).ConfigureAwait(false);
        var resolution = _resolver.Resolve(Binding.TargetId, descriptor);
        var effectiveSnapshot = resolution.Snapshot;
        var effectiveLevel = resolution.Level;
        var reasons = resolution.Reasons.ToList();
        var candidateUses = resolution.Snapshot.ExtensionCapabilities
            .Where(static grant => grant.Origin is not null)
            .Select(static grant => new ExternalCapabilityUse(
                new Uri(grant.Origin!, UriKind.Absolute)
                    .GetLeftPart(UriPartial.Authority),
                grant.Purpose,
                grant.Kind))
            .Distinct()
            .ToArray();

        if (resolution.Level == CompatibilityLevel.Extended &&
            candidateUses.Length > 0)
        {
            ExternalCapabilityDecision decision;
            try
            {
                decision = await _confirmation.ConfirmAsync(
                    new ExternalCapabilityConfirmationRequest(
                        Binding.TargetId,
                        resolution.Snapshot.Sha256,
                        candidateUses),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                decision = ExternalCapabilityDecision.Rejected;
                reasons.Add(
                    CompatibilityReasonCode.ExternalCapabilityConfirmationFailed);
            }

            if (decision != ExternalCapabilityDecision.Accepted)
            {
                effectiveSnapshot = resolution.BaseSnapshot;
                effectiveLevel = CompatibilityLevel.Base;
                reasons.Add(decision == ExternalCapabilityDecision.RegistryRollbackBlocked
                    ? CompatibilityReasonCode.RegistryRollbackBlocked
                    : CompatibilityReasonCode.ExternalCapabilityRejected);
            }
        }

        await _runtime.ActivatePageCapabilityAsync(
            effectiveSnapshot,
            cancellationToken).ConfigureAwait(false);
        PublishCompatibility(new CompatibilityStatusDto(
            effectiveLevel,
            reasons.LastOrDefault(resolution.PrimaryReason),
            reasons,
            effectiveSnapshot.ContractVersion,
            resolution.DescriptorIdentity?.SchemaVersion ?? 1,
            effectiveSnapshot.RegistryVersion,
            effectiveSnapshot.Sha256,
            candidateUses.Select(static use => use.Purpose),
            effectiveSnapshot.MatchedRules.Select(static rule =>
                $"{rule.RuleId}@{rule.RuleVersion}")));
        await _runtime.NavigateToRootAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private void PublishCompatibility(CompatibilityStatusDto status)
    {
        lock (_stateLock)
        {
            _compatibilityStatus = status;
        }

        PublishState(State, Failure, force: true);
    }

    private void PublishState(
        TargetContentState next,
        TargetContentFailure? failure = null,
        bool force = false)
    {
        EventHandler<TargetContentStateChangedEventArgs>? handler;
        lock (_stateLock)
        {
            if (!force && _state == next && Equals(_failure, failure))
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

        var args = new TargetContentStateChangedEventArgs(
            next,
            failure,
            CompatibilityStatus);
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
        TargetContentFailure? failure,
        CompatibilityStatusDto? compatibilityStatus)
    {
        State = state;
        Failure = failure;
        CompatibilityStatus = compatibilityStatus;
    }

    public TargetContentState State { get; }

    public TargetContentFailure? Failure { get; }

    public CompatibilityStatusDto? CompatibilityStatus { get; }
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

    public static TargetRuntimeSignal PageCapabilityCycleRequested() =>
        new(
            TargetRuntimeSignalKind.PageCapabilityCycleRequested,
            TargetProcessFailureKind.Other);

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
    PageCapabilityCycleRequested,
    ProcessFailed
}
