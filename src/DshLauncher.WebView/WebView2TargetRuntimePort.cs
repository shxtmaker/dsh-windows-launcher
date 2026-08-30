using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DshLauncher.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DshLauncher.WebView;

public interface ITargetBrowserDataStore
{
    string GetUserDataFolder(TargetId targetId);

    ValueTask PrepareAsync(
        TargetId targetId,
        CancellationToken cancellationToken);

    ValueTask VerifyAsync(
        TargetId targetId,
        CancellationToken cancellationToken);

    ValueTask DeleteAsync(
        TargetId targetId,
        CancellationToken cancellationToken);

    ValueTask<bool> ExistsAsync(
        TargetId targetId,
        CancellationToken cancellationToken);
}

public interface ITargetBrowserSessionFactory
{
    ValueTask<ITargetBrowserSession> OpenAsync(
        TargetBrowserSessionContext context,
        CancellationToken cancellationToken);
}

public interface ITargetBrowserSession : IAsyncDisposable
{
    ValueTask<TargetBrowserPairingObservation> PairAsync(
        ReadOnlyMemory<char> encodedToken,
        CancellationToken cancellationToken);

    ValueTask<TargetBrowserOpenObservation> PrepareOpenAsync(
        CancellationToken cancellationToken);
}

public sealed record TargetBrowserSessionContext(
    TargetId TargetId,
    Uri Origin,
    string UserDataFolder);

public sealed record TargetBrowserNavigationObservation(
    bool OriginMatched,
    bool NavigationSucceeded,
    int? HttpStatusCode);

public sealed record TargetBrowserPairingObservation(
    int? TokenRequestStatusCode,
    TargetBrowserNavigationObservation CleanRoot,
    TargetBrowserNavigationObservation AuthenticatedApi);

public sealed record TargetBrowserOpenObservation(
    TargetBrowserNavigationObservation CleanRoot,
    TargetBrowserNavigationObservation AuthenticatedApi);

internal static class PinnedHarnessBrowserContract
{
    public const int AuthenticatedApiBoundaryStatusCode = 404;

    public static bool IsCleanRootAvailable(
        TargetBrowserNavigationObservation observation) =>
        observation.OriginMatched &&
        observation.NavigationSucceeded &&
        observation.HttpStatusCode is >= 200 and <= 299;

    public static bool IsAuthenticatedApiBoundaryPassed(
        TargetBrowserNavigationObservation observation) =>
        observation.OriginMatched &&
        observation.HttpStatusCode == AuthenticatedApiBoundaryStatusCode;
}

public sealed class WebView2TargetRuntimePort : ITargetRuntimePort
{
    private static readonly TimeSpan[] BrowserDataDeleteRetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800),
        TimeSpan.FromMilliseconds(1_600),
    ];

    private readonly ITargetBrowserDataStore _dataStore;
    private readonly ITargetBrowserSessionFactory _sessionFactory;
    private readonly IPairingDiagnosticSink _pairingDiagnostics;
    private readonly ConcurrentDictionary<TargetId, SemaphoreSlim> _targetGates =
        new();

    public WebView2TargetRuntimePort(ITargetBrowserDataStore dataStore)
        : this(dataStore, NullPairingDiagnosticSink.Instance)
    {
    }

    public WebView2TargetRuntimePort(
        ITargetBrowserDataStore dataStore,
        IPairingDiagnosticSink pairingDiagnostics)
        : this(
            dataStore,
            new WebView2TargetBrowserSessionFactory(
                Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher,
                pairingDiagnostics: pairingDiagnostics),
            pairingDiagnostics)
    {
    }

    public WebView2TargetRuntimePort(
        ITargetBrowserDataStore dataStore,
        ITargetBrowserSessionFactory sessionFactory)
        : this(dataStore, sessionFactory, NullPairingDiagnosticSink.Instance)
    {
    }

    public WebView2TargetRuntimePort(
        ITargetBrowserDataStore dataStore,
        ITargetBrowserSessionFactory sessionFactory,
        IPairingDiagnosticSink pairingDiagnostics)
    {
        _dataStore = dataStore ?? throw new ArgumentNullException(nameof(dataStore));
        _sessionFactory = sessionFactory ??
            throw new ArgumentNullException(nameof(sessionFactory));
        _pairingDiagnostics = pairingDiagnostics ??
            throw new ArgumentNullException(nameof(pairingDiagnostics));
    }

    public ValueTask ResetUncommittedAsync(
        TargetId targetId,
        CancellationToken cancellationToken)
    {
        ValidateTargetId(targetId);
        return WithTargetGateAsync(
            targetId,
            async token =>
            {
                await BrowserProcessEnvironmentLease.EnsureReleasedAsync(
                    targetId,
                    token).ConfigureAwait(false);
                if (await _dataStore.ExistsAsync(targetId, token)
                    .ConfigureAwait(false))
                {
                    await _dataStore.VerifyAsync(targetId, token)
                        .ConfigureAwait(false);
                    await DeleteBrowserDataWithRetryAsync(
                        targetId,
                        "The uncommitted browser data could not be removed.",
                        token).ConfigureAwait(false);
                }

                await _dataStore.PrepareAsync(targetId, token)
                    .ConfigureAwait(false);
                await _dataStore.VerifyAsync(targetId, token)
                    .ConfigureAwait(false);
            },
            cancellationToken);
    }

    public ValueTask<PairingHandshake> PairAsync(
        TargetId targetId,
        TargetEndpoint endpoint,
        ReadOnlyMemory<char> encodedToken,
        CancellationToken cancellationToken)
    {
        ValidateTargetId(targetId);
        ArgumentNullException.ThrowIfNull(endpoint);
        ValidateEncodedToken(encodedToken.Span);

        return WithTargetGateAsync(
            targetId,
            async token =>
            {
                await ReportPairingAsync(
                    PairingDiagnosticStage.BrowserDataPreparation,
                    PairingDiagnosticOutcome.Started).ConfigureAwait(false);
                try
                {
                    await BrowserProcessEnvironmentLease.EnsureReleasedAsync(
                        targetId,
                        token).ConfigureAwait(false);
                    await _dataStore.PrepareAsync(targetId, token)
                        .ConfigureAwait(false);
                    await _dataStore.VerifyAsync(targetId, token)
                        .ConfigureAwait(false);
                    await ReportPairingAsync(
                        PairingDiagnosticStage.BrowserDataPreparation,
                        PairingDiagnosticOutcome.Succeeded).ConfigureAwait(false);
                }
                catch
                {
                    await ReportPairingAsync(
                        PairingDiagnosticStage.BrowserDataPreparation,
                        PairingDiagnosticOutcome.Failed,
                        PairingFailureCategory.BrowserData).ConfigureAwait(false);
                    throw;
                }

                var context = CreateContext(targetId, endpoint);
                await ReportPairingAsync(
                    PairingDiagnosticStage.BrowserSessionOpen,
                    PairingDiagnosticOutcome.Started).ConfigureAwait(false);
                ITargetBrowserSession session;
                try
                {
                    session = await _sessionFactory.OpenAsync(
                        context,
                        token).ConfigureAwait(false);
                    await ReportPairingAsync(
                        PairingDiagnosticStage.BrowserSessionOpen,
                        PairingDiagnosticOutcome.Succeeded).ConfigureAwait(false);
                }
                catch
                {
                    await ReportPairingAsync(
                        PairingDiagnosticStage.BrowserSessionOpen,
                        PairingDiagnosticOutcome.Failed,
                        PairingFailureCategory.BrowserInitialization).ConfigureAwait(false);
                    throw;
                }

                TargetBrowserPairingObservation? observation = null;
                var pairingFailed = false;
                var pairingCancelled = false;
                try
                {
                    observation = await session.PairAsync(encodedToken, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    pairingCancelled = true;
                }
                catch
                {
                    pairingFailed = true;
                }

                var releaseFailed = false;
                await ReportPairingAsync(
                    PairingDiagnosticStage.BrowserSessionRelease,
                    PairingDiagnosticOutcome.Started).ConfigureAwait(false);
                try
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                    await ReportPairingAsync(
                        PairingDiagnosticStage.BrowserSessionRelease,
                        PairingDiagnosticOutcome.Succeeded).ConfigureAwait(false);
                }
                catch
                {
                    releaseFailed = true;
                    await ReportPairingAsync(
                        PairingDiagnosticStage.BrowserSessionRelease,
                        PairingDiagnosticOutcome.Failed,
                        PairingFailureCategory.BrowserProcessRelease).ConfigureAwait(false);
                }

                if (pairingCancelled)
                {
                    throw new OperationCanceledException(token);
                }

                if (pairingFailed)
                {
                    throw new InvalidOperationException(
                        "The temporary browser pairing workflow failed.");
                }

                if (releaseFailed)
                {
                    throw new InvalidOperationException(
                        "The temporary browser pairing session did not release its user data folder.");
                }

                return new PairingHandshake(
                    observation!.TokenRequestStatusCode ?? 0,
                    PinnedHarnessBrowserContract.IsCleanRootAvailable(
                        observation.CleanRoot),
                    PinnedHarnessBrowserContract.IsAuthenticatedApiBoundaryPassed(
                        observation.AuthenticatedApi));
            },
            cancellationToken);
    }

    private ValueTask ReportPairingAsync(
        PairingDiagnosticStage stage,
        PairingDiagnosticOutcome outcome,
        PairingFailureCategory? failureCategory = null,
        int? httpStatusCode = null) =>
        _pairingDiagnostics.ReportSafelyAsync(
            new PairingDiagnosticEvent(
                stage,
                outcome,
                failureCategory,
                httpStatusCode));

    public ValueTask<RuntimeOpenStatus> PrepareOpenAsync(
        TargetId targetId,
        TargetEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        ValidateTargetId(targetId);
        ArgumentNullException.ThrowIfNull(endpoint);

        return WithTargetGateAsync(
            targetId,
            async token =>
            {
                await BrowserProcessEnvironmentLease.EnsureReleasedAsync(
                    targetId,
                    token).ConfigureAwait(false);
                if (!await _dataStore.ExistsAsync(targetId, token)
                    .ConfigureAwait(false))
                {
                    return RuntimeOpenStatus.Unauthorized;
                }

                await _dataStore.VerifyAsync(targetId, token)
                    .ConfigureAwait(false);
                var context = CreateContext(targetId, endpoint);
                await using var session = await _sessionFactory.OpenAsync(
                    context,
                    token).ConfigureAwait(false);
                var observation = await session.PrepareOpenAsync(token)
                    .ConfigureAwait(false);
                return ClassifyOpen(observation);
            },
            cancellationToken);
    }

    public ValueTask CloseAsync(
        TargetId targetId,
        CancellationToken cancellationToken)
    {
        ValidateTargetId(targetId);
        return WithTargetGateAsync(
            targetId,
            token => BrowserProcessEnvironmentLease.EnsureReleasedAsync(
                targetId,
                token),
            cancellationToken);
    }

    public ValueTask DeleteSessionDataAsync(
        TargetId targetId,
        CancellationToken cancellationToken)
    {
        ValidateTargetId(targetId);
        return WithTargetGateAsync(
            targetId,
            async token =>
            {
                await BrowserProcessEnvironmentLease.EnsureReleasedAsync(
                    targetId,
                    token).ConfigureAwait(false);
                if (!await _dataStore.ExistsAsync(targetId, token)
                    .ConfigureAwait(false))
                {
                    return;
                }

                await _dataStore.VerifyAsync(targetId, token)
                    .ConfigureAwait(false);
                await DeleteBrowserDataWithRetryAsync(
                    targetId,
                    "The target browser data could not be removed.",
                    token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public ValueTask<bool> SessionDataExistsAsync(
        TargetId targetId,
        CancellationToken cancellationToken)
    {
        ValidateTargetId(targetId);
        return WithTargetGateAsync(
            targetId,
            token => _dataStore.ExistsAsync(targetId, token),
            cancellationToken);
    }

    public static RuntimeOpenStatus ClassifyOpen(
        TargetBrowserOpenObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!observation.CleanRoot.OriginMatched ||
            !observation.AuthenticatedApi.OriginMatched)
        {
            return RuntimeOpenStatus.OriginMismatch;
        }

        if (HasStatus(observation, 401))
        {
            return RuntimeOpenStatus.Unauthorized;
        }

        if (HasStatus(observation, 403))
        {
            return RuntimeOpenStatus.Forbidden;
        }

        return PinnedHarnessBrowserContract.IsCleanRootAvailable(
                   observation.CleanRoot) &&
               PinnedHarnessBrowserContract.IsAuthenticatedApiBoundaryPassed(
                   observation.AuthenticatedApi)
            ? RuntimeOpenStatus.Ready
            : RuntimeOpenStatus.TemporaryFailure;
    }

    private TargetBrowserSessionContext CreateContext(
        TargetId targetId,
        TargetEndpoint endpoint)
    {
        var folder = _dataStore.GetUserDataFolder(targetId);
        if (string.IsNullOrWhiteSpace(folder) ||
            !Path.IsPathFullyQualified(folder))
        {
            throw new InvalidOperationException(
                "The browser data store returned an invalid UDF path.");
        }

        return new TargetBrowserSessionContext(
            targetId,
            new Uri($"{endpoint.Origin}/", UriKind.Absolute),
            Path.GetFullPath(folder));
    }

    private static bool HasStatus(
        TargetBrowserOpenObservation observation,
        int statusCode)
    {
        return observation.CleanRoot.HttpStatusCode == statusCode ||
               observation.AuthenticatedApi.HttpStatusCode == statusCode;
    }

    private async ValueTask DeleteBrowserDataWithRetryAsync(
        TargetId targetId,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        IOException? lastFailure = null;
        for (var attempt = 0;
             attempt <= BrowserDataDeleteRetryDelays.Length;
             attempt++)
        {
            try
            {
                await _dataStore.DeleteAsync(targetId, cancellationToken)
                    .ConfigureAwait(false);
                if (!await _dataStore.ExistsAsync(targetId, cancellationToken)
                    .ConfigureAwait(false))
                {
                    return;
                }

                lastFailure = new IOException(failureMessage);
            }
            catch (IOException exception)
            {
                lastFailure = exception;
            }

            if (attempt == BrowserDataDeleteRetryDelays.Length)
            {
                throw new IOException(failureMessage, lastFailure);
            }

            await Task.Delay(
                    BrowserDataDeleteRetryDelays[attempt],
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask WithTargetGateAsync(
        TargetId targetId,
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken)
    {
        var gate = _targetGates.GetOrAdd(
            targetId,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<TResult> WithTargetGateAsync<TResult>(
        TargetId targetId,
        Func<CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken)
    {
        var gate = _targetGates.GetOrAdd(
            targetId,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static void ValidateTargetId(TargetId targetId)
    {
        if (targetId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "Target identity must not be empty.",
                nameof(targetId));
        }
    }

    private static void ValidateEncodedToken(ReadOnlySpan<char> encodedToken)
    {
        var invalid = encodedToken.IsEmpty || encodedToken.Length > 4096;
        for (var index = 0; index < encodedToken.Length; index++)
        {
            var character = encodedToken[index];
            if (char.IsWhiteSpace(character) ||
                char.IsControl(character) ||
                character is '&' or ';' or '#' or '?')
            {
                invalid = true;
                break;
            }

            if (character == '%')
            {
                if (index + 2 >= encodedToken.Length ||
                    !Uri.IsHexDigit(encodedToken[index + 1]) ||
                    !Uri.IsHexDigit(encodedToken[index + 2]))
                {
                    invalid = true;
                    break;
                }

                index += 2;
            }
        }

        if (invalid)
        {
            throw new ArgumentException(
                "The encoded pairing token is not valid base64url data.",
                nameof(encodedToken));
        }
    }
}

public sealed class WebView2TargetBrowserSessionFactory :
    ITargetBrowserSessionFactory
{
    private static readonly TimeSpan DefaultOperationTimeout =
        TimeSpan.FromSeconds(15);

    private readonly Dispatcher _dispatcher;
    private readonly TimeSpan _operationTimeout;
    private readonly IPairingDiagnosticSink _pairingDiagnostics;

    public WebView2TargetBrowserSessionFactory(
        Dispatcher dispatcher,
        TimeSpan? operationTimeout = null,
        IPairingDiagnosticSink? pairingDiagnostics = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _operationTimeout = operationTimeout ?? DefaultOperationTimeout;
        _pairingDiagnostics = pairingDiagnostics ?? NullPairingDiagnosticSink.Instance;
        if (_operationTimeout <= TimeSpan.Zero ||
            _operationTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }
    }

    public async ValueTask<ITargetBrowserSession> OpenAsync(
        TargetBrowserSessionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ = new TargetContentBinding(
            context.TargetId.Value,
            context.Origin,
            context.UserDataFolder);
        if (_dispatcher.Thread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException(
                "WebView2 browser sessions require an STA WPF dispatcher.");
        }

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            throw new InvalidOperationException(
                "The WPF dispatcher is no longer available.");
        }

        return await _dispatcher.InvokeAsync(
                () => WebView2TargetBrowserSession.CreateAsync(
                    context,
                    _operationTimeout,
                    _pairingDiagnostics,
                    cancellationToken),
                DispatcherPriority.Normal,
                cancellationToken)
            .Task.Unwrap().ConfigureAwait(false);
    }
}

internal sealed class WebView2TargetBrowserSession : ITargetBrowserSession
{
    private readonly TargetBrowserSessionContext _context;
    private readonly TimeSpan _operationTimeout;
    private readonly Dispatcher _dispatcher;
    private readonly Window _window;
    private readonly WebView2 _webView;
    private readonly CoreWebView2Environment _environment;
    private readonly BrowserProcessEnvironmentLease _browserLease;
    private readonly IPairingDiagnosticSink _pairingDiagnostics;
    private WebView2ResponseCspBoundary? _responseCspBoundary;
    private bool _originMismatch;
    private int _disposed;

    private WebView2TargetBrowserSession(
        TargetBrowserSessionContext context,
        TimeSpan operationTimeout,
        Window window,
        WebView2 webView,
        CoreWebView2Environment environment,
        BrowserProcessEnvironmentLease browserLease,
        IPairingDiagnosticSink pairingDiagnostics)
    {
        _context = context;
        _operationTimeout = operationTimeout;
        _dispatcher = window.Dispatcher;
        _window = window;
        _webView = webView;
        _environment = environment;
        _browserLease = browserLease;
        _pairingDiagnostics = pairingDiagnostics;
        SubscribeSecurity(webView.CoreWebView2);
    }

    public static async Task<WebView2TargetBrowserSession> CreateAsync(
        TargetBrowserSessionContext context,
        TimeSpan operationTimeout,
        IPairingDiagnosticSink pairingDiagnostics,
        CancellationToken cancellationToken)
    {
        Dispatcher.CurrentDispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(pairingDiagnostics);
        cancellationToken.ThrowIfCancellationRequested();
        var host = new Grid();
        var webView = new WebView2
        {
            AllowExternalDrop = false,
        };
        host.Children.Add(webView);
        var window = new Window
        {
            Width = 1,
            Height = 1,
            Left = -32_000,
            Top = -32_000,
            Opacity = 0,
            ShowActivated = false,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            Content = host,
        };

        CoreWebView2Environment? environment = null;
        BrowserProcessEnvironmentLease? browserLease = null;
        var activeStage = PairingDiagnosticStage.BrowserEnvironmentCreate;
        try
        {
            window.Show();
            await ReportPairingAsync(
                pairingDiagnostics,
                PairingDiagnosticStage.BrowserEnvironmentCreate,
                PairingDiagnosticOutcome.Started).ConfigureAwait(true);
            environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                context.UserDataFolder).ConfigureAwait(true);
            await ReportPairingAsync(
                pairingDiagnostics,
                PairingDiagnosticStage.BrowserEnvironmentCreate,
                PairingDiagnosticOutcome.Succeeded).ConfigureAwait(true);
            browserLease = BrowserProcessEnvironmentLease.Register(
                context.TargetId,
                environment,
                operationTimeout);
            cancellationToken.ThrowIfCancellationRequested();
            activeStage = PairingDiagnosticStage.BrowserControllerInitialize;
            await ReportPairingAsync(
                pairingDiagnostics,
                PairingDiagnosticStage.BrowserControllerInitialize,
                PairingDiagnosticOutcome.Started).ConfigureAwait(true);
            await webView.EnsureCoreWebView2Async(environment)
                .ConfigureAwait(true);
            await ReportPairingAsync(
                pairingDiagnostics,
                PairingDiagnosticStage.BrowserControllerInitialize,
                PairingDiagnosticOutcome.Succeeded).ConfigureAwait(true);
            browserLease.CaptureBrowserProcessId(
                webView.CoreWebView2.BrowserProcessId);
            cancellationToken.ThrowIfCancellationRequested();
            ConfigureSecurity(webView.CoreWebView2);
            return new WebView2TargetBrowserSession(
                context,
                operationTimeout,
                window,
                webView,
                environment,
                browserLease,
                pairingDiagnostics);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            await ReportPairingAsync(
                pairingDiagnostics,
                activeStage,
                PairingDiagnosticOutcome.Cancelled,
                PairingFailureCategory.Cancelled).ConfigureAwait(true);
            webView.Dispose();
            window.Close();
            if (browserLease is not null)
            {
                await ReportPairingAsync(
                    pairingDiagnostics,
                    PairingDiagnosticStage.BrowserSessionRelease,
                    PairingDiagnosticOutcome.Started).ConfigureAwait(true);
                try
                {
                    await browserLease.WaitForReleaseAsync(
                        CancellationToken.None).ConfigureAwait(true);
                    await ReportPairingAsync(
                        pairingDiagnostics,
                        PairingDiagnosticStage.BrowserSessionRelease,
                        PairingDiagnosticOutcome.Succeeded).ConfigureAwait(true);
                }
                catch
                {
                    await ReportPairingAsync(
                        pairingDiagnostics,
                        PairingDiagnosticStage.BrowserSessionRelease,
                        PairingDiagnosticOutcome.Failed,
                        PairingFailureCategory.BrowserProcessRelease).ConfigureAwait(true);
                    throw new InvalidOperationException(
                        "The cancelled temporary WebView2 browser session did not release its user data folder.");
                }
            }

            throw;
        }
        catch
        {
            await ReportPairingAsync(
                pairingDiagnostics,
                activeStage,
                PairingDiagnosticOutcome.Failed,
                PairingFailureCategory.BrowserInitialization).ConfigureAwait(true);
            webView.Dispose();
            window.Close();
            if (browserLease is not null)
            {
                await ReportPairingAsync(
                    pairingDiagnostics,
                    PairingDiagnosticStage.BrowserSessionRelease,
                    PairingDiagnosticOutcome.Started).ConfigureAwait(true);
                try
                {
                    await browserLease.WaitForReleaseAsync(
                        CancellationToken.None).ConfigureAwait(true);
                    await ReportPairingAsync(
                        pairingDiagnostics,
                        PairingDiagnosticStage.BrowserSessionRelease,
                        PairingDiagnosticOutcome.Succeeded).ConfigureAwait(true);
                }
                catch
                {
                    await ReportPairingAsync(
                        pairingDiagnostics,
                        PairingDiagnosticStage.BrowserSessionRelease,
                        PairingDiagnosticOutcome.Failed,
                        PairingFailureCategory.BrowserProcessRelease).ConfigureAwait(true);
                    throw new InvalidOperationException(
                        "The failed temporary WebView2 browser session did not release its user data folder.");
                }
            }

            throw new InvalidOperationException(
                "The temporary WebView2 browser session could not be initialized.");
        }
    }

    private static ValueTask ReportPairingAsync(
        IPairingDiagnosticSink diagnosticSink,
        PairingDiagnosticStage stage,
        PairingDiagnosticOutcome outcome,
        PairingFailureCategory? failureCategory = null,
        int? httpStatusCode = null) =>
        diagnosticSink.ReportSafelyAsync(
            new PairingDiagnosticEvent(
                stage,
                outcome,
                failureCategory,
                httpStatusCode));

    public ValueTask<TargetBrowserPairingObservation> PairAsync(
        ReadOnlyMemory<char> encodedToken,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return InvokeAsync(
            () => PairCoreAsync(encodedToken, cancellationToken),
            cancellationToken);
    }

    public ValueTask<TargetBrowserOpenObservation> PrepareOpenAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return InvokeAsync(
            () => PrepareOpenCoreAsync(cancellationToken),
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            throw new InvalidOperationException(
                "The temporary WebView2 browser session cannot prove process release after dispatcher shutdown.");
        }

        if (_dispatcher.CheckAccess())
        {
            await DisposeCoreAsync().ConfigureAwait(true);
            return;
        }

        await _dispatcher.InvokeAsync(
            DisposeCoreAsync,
            DispatcherPriority.Send).Task.Unwrap().ConfigureAwait(false);
    }

    private async Task<TargetBrowserPairingObservation> PairCoreAsync(
        ReadOnlyMemory<char> encodedToken,
        CancellationToken cancellationToken)
    {
        _dispatcher.VerifyAccess();
        var tokenUrl = CreateTokenUrl(encodedToken.Span);
        int? tokenStatus = null;
        await ReportPairingAsync(
            PairingDiagnosticStage.TokenNavigation,
            PairingDiagnosticOutcome.Started).ConfigureAwait(true);
        var tokenNavigationFailed = false;
        var tokenNavigationCancelled = false;
        try
        {
            var tokenObservation = await NavigateSensitiveTokenAsync(
                tokenUrl,
                cancellationToken).ConfigureAwait(true);
            tokenStatus = tokenObservation.ObservedRequestStatusCode ??
                tokenObservation.Navigation.HttpStatusCode;
            await ReportPairingAsync(
                PairingDiagnosticStage.TokenNavigation,
                tokenStatus == 303
                    ? PairingDiagnosticOutcome.Succeeded
                    : PairingDiagnosticOutcome.Rejected,
                tokenStatus == 303 ? null : PairingFailureCategory.Navigation,
                NormalizeHttpStatusCode(tokenStatus)).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            tokenNavigationCancelled = true;
            await ReportPairingAsync(
                PairingDiagnosticStage.TokenNavigation,
                PairingDiagnosticOutcome.Cancelled,
                PairingFailureCategory.Cancelled).ConfigureAwait(true);
        }
        catch
        {
            tokenNavigationFailed = true;
            await ReportPairingAsync(
                PairingDiagnosticStage.TokenNavigation,
                PairingDiagnosticOutcome.Failed,
                PairingFailureCategory.Navigation).ConfigureAwait(true);
        }

        PairingFailureCategory? historyCleanupFailure = null;
        await ReportPairingAsync(
            PairingDiagnosticStage.BrowsingHistoryCleanup,
            PairingDiagnosticOutcome.Started).ConfigureAwait(true);
        try
        {
            await ClearBrowsingHistoryAsync().ConfigureAwait(true);
            await ReportPairingAsync(
                PairingDiagnosticStage.BrowsingHistoryCleanup,
                PairingDiagnosticOutcome.Succeeded).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            historyCleanupFailure = PairingFailureCategory.Timeout;
            await ReportPairingAsync(
                PairingDiagnosticStage.BrowsingHistoryCleanup,
                PairingDiagnosticOutcome.Failed,
                historyCleanupFailure).ConfigureAwait(true);
        }
        catch
        {
            historyCleanupFailure = PairingFailureCategory.HistoryCleanup;
            await ReportPairingAsync(
                PairingDiagnosticStage.BrowsingHistoryCleanup,
                PairingDiagnosticOutcome.Failed,
                historyCleanupFailure).ConfigureAwait(true);
        }

        if (tokenNavigationCancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (tokenNavigationFailed)
        {
            throw new InvalidOperationException(
                "The temporary pairing token navigation failed.");
        }

        if (historyCleanupFailure is not null)
        {
            throw new InvalidOperationException(
                historyCleanupFailure == PairingFailureCategory.Timeout
                    ? "The temporary pairing navigation history cleanup timed out."
                    : "The temporary pairing navigation history could not be cleared.");
        }

        if (tokenStatus != 303)
        {
            return new TargetBrowserPairingObservation(
                tokenStatus,
                NotAttempted(),
                NotAttempted());
        }

        await ReportPairingAsync(
            PairingDiagnosticStage.ResponseCspBoundary,
            PairingDiagnosticOutcome.Started).ConfigureAwait(true);
        try
        {
            await EnsureResponseCspBoundaryAsync().ConfigureAwait(true);
            await ReportPairingAsync(
                PairingDiagnosticStage.ResponseCspBoundary,
                PairingDiagnosticOutcome.Succeeded).ConfigureAwait(true);
        }
        catch
        {
            await ReportPairingAsync(
                PairingDiagnosticStage.ResponseCspBoundary,
                PairingDiagnosticOutcome.Failed,
                PairingFailureCategory.BrowserInitialization).ConfigureAwait(true);
            throw;
        }

        var root = await ObservePairingNavigationStageAsync(
            PairingDiagnosticStage.CleanRootNavigation,
            _context.Origin.AbsoluteUri,
            PinnedHarnessBrowserContract.IsCleanRootAvailable,
            cancellationToken).ConfigureAwait(true);
        if (!PinnedHarnessBrowserContract.IsCleanRootAvailable(root.Navigation))
        {
            return new TargetBrowserPairingObservation(
                tokenStatus,
                root.Navigation,
                NotAttempted());
        }

        var apiUri = new Uri(_context.Origin, "/api").AbsoluteUri;
        var api = await ObservePairingNavigationStageAsync(
            PairingDiagnosticStage.AuthenticatedApiNavigation,
            apiUri,
            PinnedHarnessBrowserContract.IsAuthenticatedApiBoundaryPassed,
            cancellationToken).ConfigureAwait(true);
        return new TargetBrowserPairingObservation(
            tokenStatus,
            root.Navigation,
            api.Navigation);
    }

    private async Task<InternalNavigationObservation> ObservePairingNavigationStageAsync(
        PairingDiagnosticStage stage,
        string navigationUri,
        Func<TargetBrowserNavigationObservation, bool> isSuccessful,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isSuccessful);
        await ReportPairingAsync(
            stage,
            PairingDiagnosticOutcome.Started).ConfigureAwait(true);
        try
        {
            var observation = await NavigateAsync(
                navigationUri,
                navigationUri,
                cancellationToken).ConfigureAwait(true);
            var available = isSuccessful(observation.Navigation);
            await ReportPairingAsync(
                stage,
                available
                    ? PairingDiagnosticOutcome.Succeeded
                    : PairingDiagnosticOutcome.Incomplete,
                available
                    ? null
                    : observation.Navigation.OriginMatched
                        ? PairingFailureCategory.Navigation
                        : PairingFailureCategory.OriginMismatch,
                NormalizeHttpStatusCode(observation.Navigation.HttpStatusCode))
                .ConfigureAwait(true);
            return observation;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReportPairingAsync(
                stage,
                PairingDiagnosticOutcome.Cancelled,
                PairingFailureCategory.Cancelled).ConfigureAwait(true);
            throw;
        }
        catch
        {
            await ReportPairingAsync(
                stage,
                PairingDiagnosticOutcome.Failed,
                PairingFailureCategory.Navigation).ConfigureAwait(true);
            throw;
        }
    }

    private ValueTask ReportPairingAsync(
        PairingDiagnosticStage stage,
        PairingDiagnosticOutcome outcome,
        PairingFailureCategory? failureCategory = null,
        int? httpStatusCode = null) =>
        _pairingDiagnostics.ReportSafelyAsync(
            new PairingDiagnosticEvent(
                stage,
                outcome,
                failureCategory,
                httpStatusCode));

    private static int? NormalizeHttpStatusCode(int? statusCode) =>
        statusCode is >= 100 and <= 599 ? statusCode : null;

    private async Task<TargetBrowserOpenObservation> PrepareOpenCoreAsync(
        CancellationToken cancellationToken)
    {
        _dispatcher.VerifyAccess();
        await EnsureResponseCspBoundaryAsync().ConfigureAwait(true);
        var root = await NavigateAsync(
            _context.Origin.AbsoluteUri,
            _context.Origin.AbsoluteUri,
            cancellationToken).ConfigureAwait(true);
        if (!PinnedHarnessBrowserContract.IsCleanRootAvailable(root.Navigation))
        {
            return new TargetBrowserOpenObservation(
                root.Navigation,
                NotAttempted());
        }

        var apiUri = new Uri(_context.Origin, "/api").AbsoluteUri;
        var api = await NavigateAsync(
            apiUri,
            apiUri,
            cancellationToken).ConfigureAwait(true);
        return new TargetBrowserOpenObservation(
            root.Navigation,
            api.Navigation);
    }

    private async Task<InternalNavigationObservation> NavigateAsync(
        string navigationUri,
        string observedRequestUri,
        CancellationToken cancellationToken)
    {
        return await ObserveNavigationAsync(
            () => _webView.CoreWebView2.Navigate(navigationUri),
            args => string.Equals(
                args.Request.Uri,
                observedRequestUri,
                StringComparison.Ordinal),
            cancellationToken).ConfigureAwait(true);
    }

    private async Task<InternalNavigationObservation> NavigateSensitiveTokenAsync(
        SensitiveManagedString navigationUri,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(navigationUri);
        var firstResponse = true;
        try
        {
            return await ObserveNavigationAsync(
                () => navigationUri.UseAndClear(
                    _webView.CoreWebView2.Navigate),
                _ =>
                {
                    var capture = firstResponse;
                    firstResponse = false;
                    return capture;
                },
                cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            navigationUri.Dispose();
        }
    }

    private async Task<InternalNavigationObservation> ObserveNavigationAsync(
        Action startNavigation,
        Func<CoreWebView2WebResourceResponseReceivedEventArgs, bool>
            shouldObserveResponse,
        CancellationToken cancellationToken)
    {
        _dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(startNavigation);
        ArgumentNullException.ThrowIfNull(shouldObserveResponse);
        var core = _webView.CoreWebView2;
        _originMismatch = false;
        ulong? navigationId = null;
        int? observedStatus = null;
        var completion = new TaskCompletionSource<
            TargetBrowserNavigationObservation>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStarting(
            object? _,
            CoreWebView2NavigationStartingEventArgs args)
        {
            navigationId ??= args.NavigationId;
        }

        void OnResponse(
            object? _,
            CoreWebView2WebResourceResponseReceivedEventArgs args)
        {
            if (navigationId is not null && shouldObserveResponse(args))
            {
                observedStatus = args.Response.StatusCode;
            }
        }

        void OnCompleted(
            object? _,
            CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.NavigationId != navigationId)
            {
                return;
            }

            completion.TrySetResult(new TargetBrowserNavigationObservation(
                !_originMismatch && IsBoundOrigin(core.Source),
                args.IsSuccess,
                args.HttpStatusCode == 0 ? null : args.HttpStatusCode));
        }

        core.WebResourceResponseReceived += OnResponse;
        core.NavigationStarting += OnStarting;
        core.NavigationCompleted += OnCompleted;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_operationTimeout);
        try
        {
            startNavigation();
            startNavigation = null!;
            var observation = await completion.Task.WaitAsync(timeout.Token)
                .ConfigureAwait(true);
            return new InternalNavigationObservation(
                observation,
                observedStatus);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            core.Stop();
            return new InternalNavigationObservation(
                new TargetBrowserNavigationObservation(
                    OriginMatched: !_originMismatch,
                    NavigationSucceeded: false,
                    HttpStatusCode: null),
                observedStatus);
        }
        finally
        {
            core.WebResourceResponseReceived -= OnResponse;
            core.NavigationStarting -= OnStarting;
            core.NavigationCompleted -= OnCompleted;
        }
    }

    private async Task ClearBrowsingHistoryAsync()
    {
        await _webView.CoreWebView2.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.BrowsingHistory)
            .WaitAsync(_operationTimeout)
            .ConfigureAwait(true);
    }

    private SensitiveManagedString CreateTokenUrl(ReadOnlySpan<char> encodedToken)
    {
        var prefix = $"{_context.Origin.AbsoluteUri}?token=";
        return SensitiveManagedString.Create(prefix.AsSpan(), encodedToken);
    }

    private async Task EnsureResponseCspBoundaryAsync()
    {
        if (_responseCspBoundary is not null)
        {
            return;
        }

        var binding = new TargetContentBinding(
            _context.TargetId.Value,
            _context.Origin,
            _context.UserDataFolder);
        _responseCspBoundary = await WebView2ResponseCspBoundary.EnableAsync(
            _webView.CoreWebView2,
            binding).ConfigureAwait(true);
    }

    private void SubscribeSecurity(CoreWebView2 core)
    {
        core.AddWebResourceRequestedFilter(
            "*",
            CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.NavigationStarting += OnNavigationStarting;
        core.FrameNavigationStarting += OnFrameNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
        core.DownloadStarting += OnDownloadStarting;
        core.PermissionRequested += OnPermissionRequested;
        core.ServerCertificateErrorDetected += OnServerCertificateErrorDetected;
        core.BasicAuthenticationRequested += OnBasicAuthenticationRequested;
        core.ClientCertificateRequested += OnClientCertificateRequested;
    }

    private void UnsubscribeSecurity(CoreWebView2 core)
    {
        core.WebResourceRequested -= OnWebResourceRequested;
        core.RemoveWebResourceRequestedFilter(
            "*",
            CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
        core.NavigationStarting -= OnNavigationStarting;
        core.FrameNavigationStarting -= OnFrameNavigationStarting;
        core.NewWindowRequested -= OnNewWindowRequested;
        core.LaunchingExternalUriScheme -= OnLaunchingExternalUriScheme;
        core.DownloadStarting -= OnDownloadStarting;
        core.PermissionRequested -= OnPermissionRequested;
        core.ServerCertificateErrorDetected -= OnServerCertificateErrorDetected;
        core.BasicAuthenticationRequested -= OnBasicAuthenticationRequested;
        core.ClientCertificateRequested -= OnClientCertificateRequested;
    }

    private static void ConfigureSecurity(CoreWebView2 core)
    {
        var settings = core.Settings;
        settings.IsScriptEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
    }

    private void OnWebResourceRequested(
        object? sender,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (IsBoundOrigin(args.Request.Uri))
        {
            return;
        }

        args.Response = _environment.CreateWebResourceResponse(
            Stream.Null,
            403,
            "Forbidden",
            "Cache-Control: no-store");
    }

    private void OnNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsBoundOrigin(args.Uri))
        {
            args.Cancel = true;
            _originMismatch = true;
        }
    }

    private void OnFrameNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsBoundOrigin(args.Uri))
        {
            args.Cancel = true;
            _originMismatch = true;
        }
    }

    private static void OnNewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
    }

    private static void OnLaunchingExternalUriScheme(
        object? sender,
        CoreWebView2LaunchingExternalUriSchemeEventArgs args)
    {
        args.Cancel = true;
    }

    private static void OnDownloadStarting(
        object? sender,
        CoreWebView2DownloadStartingEventArgs args)
    {
        args.Cancel = true;
        args.Handled = true;
    }

    private static void OnPermissionRequested(
        object? sender,
        CoreWebView2PermissionRequestedEventArgs args)
    {
        args.State = CoreWebView2PermissionState.Deny;
        args.SavesInProfile = false;
        args.Handled = true;
    }

    private static void OnServerCertificateErrorDetected(
        object? sender,
        CoreWebView2ServerCertificateErrorDetectedEventArgs args)
    {
        args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
    }

    private static void OnBasicAuthenticationRequested(
        object? sender,
        CoreWebView2BasicAuthenticationRequestedEventArgs args)
    {
        args.Cancel = true;
    }

    private static void OnClientCertificateRequested(
        object? sender,
        CoreWebView2ClientCertificateRequestedEventArgs args)
    {
        args.SelectedCertificate = null;
        args.Cancel = true;
        args.Handled = true;
    }

    private bool IsBoundOrigin(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               string.Equals(
                   uri.Scheme,
                   _context.Origin.Scheme,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   uri.Host,
                   _context.Origin.Host,
                   StringComparison.OrdinalIgnoreCase) &&
               uri.Port == _context.Origin.Port;
    }

    private async Task DisposeCoreAsync()
    {
        _dispatcher.VerifyAccess();
        if (_webView.CoreWebView2 is { } core)
        {
            _responseCspBoundary?.Dispose();
            _responseCspBoundary = null;
            UnsubscribeSecurity(core);
            core.Stop();
        }

        _webView.Dispose();
        _window.Close();
        await _browserLease.WaitForReleaseAsync(CancellationToken.None)
            .ConfigureAwait(true);
    }

    private ValueTask<TResult> InvokeAsync<TResult>(
        Func<Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        if (_dispatcher.CheckAccess())
        {
            return new ValueTask<TResult>(operation());
        }

        return new ValueTask<TResult>(
            _dispatcher.InvokeAsync(
                    operation,
                    DispatcherPriority.Normal,
                    cancellationToken)
                .Task.Unwrap());
    }

    private static TargetBrowserNavigationObservation NotAttempted() => new(
        OriginMatched: true,
        NavigationSucceeded: false,
        HttpStatusCode: null);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    private sealed record InternalNavigationObservation(
        TargetBrowserNavigationObservation Navigation,
        int? ObservedRequestStatusCode);
}
