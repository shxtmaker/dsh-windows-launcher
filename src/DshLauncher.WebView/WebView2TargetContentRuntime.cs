using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DshLauncher.WebView;

public sealed class WebView2TargetContentRuntime : ITargetContentRuntime
{
    private static readonly TimeSpan BrowserProcessReleaseTimeout =
        TimeSpan.FromSeconds(15);

    private readonly Panel _container;
    private readonly ITargetExternalNavigationConsent _consent;
    private readonly ITargetExternalUriLauncher _launcher;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<ulong> _cancelledNavigations = [];
    private TargetContentBinding? _binding;
    private TargetContentSecurityPolicy? _policy;
    private TargetExternalNavigationCoordinator? _externalNavigation;
    private Func<TargetRuntimeSignal, CancellationToken, ValueTask>? _signalSink;
    private CoreWebView2Environment? _environment;
    private BrowserProcessEnvironmentLease? _browserLease;
    private WebView2ResponseCspBoundary? _responseCspBoundary;
    private WebView2? _webView;
    private bool _processFailurePublished;
    private bool _authenticationInvalidPublished;
    private int _disposed;

    public WebView2TargetContentRuntime(
        Panel container,
        ITargetExternalNavigationConsent consent,
        ITargetExternalUriLauncher launcher)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _consent = consent ?? throw new ArgumentNullException(nameof(consent));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    }

    public async ValueTask InitializeAsync(
        TargetContentBinding binding,
        TargetContentSecurityPolicy policy,
        Func<TargetRuntimeSignal, CancellationToken, ValueTask> signalSink,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(signalSink);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_binding is not null)
            {
                if (_binding != binding)
                {
                    throw new InvalidOperationException(
                        "A WebView2 target runtime cannot change its target binding.");
                }

                return;
            }

            if (policy.Binding != binding)
            {
                throw new ArgumentException(
                    "The security policy must belong to the target binding.",
                    nameof(policy));
            }

            _binding = binding;
            _policy = policy;
            _signalSink = signalSink;
            _externalNavigation = new TargetExternalNavigationCoordinator(
                policy,
                _consent,
                _launcher);
            await InvokeOnDispatcherAsync(
                CreateWebViewCoreAsync,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public ValueTask NavigateToRootAsync(CancellationToken cancellationToken) =>
        InvokeLifecycleAsync(
            NavigateToRootCoreAsync,
            cancellationToken);

    public ValueTask ReloadAsync(CancellationToken cancellationToken) =>
        InvokeLifecycleAsync(
            () =>
            {
                _processFailurePublished = false;
                return NavigateToRootCoreAsync();
            },
            cancellationToken);

    public ValueTask RecreateWebViewAsync(CancellationToken cancellationToken) =>
        InvokeLifecycleAsync(
            async () =>
            {
                await ReleaseBrowserProcessCoreAsync(
                    releaseEnvironment: false,
                    cancellationToken).ConfigureAwait(true);
                await CreateWebViewCoreAsync().ConfigureAwait(true);
                await NavigateToRootCoreAsync().ConfigureAwait(true);
            },
            cancellationToken);

    public ValueTask RecreateEnvironmentAsync(
        CancellationToken cancellationToken) =>
        InvokeLifecycleAsync(
            async () =>
            {
                await ReleaseBrowserProcessCoreAsync(
                    releaseEnvironment: true,
                    cancellationToken).ConfigureAwait(true);
                await CreateWebViewCoreAsync().ConfigureAwait(true);
                await NavigateToRootCoreAsync().ConfigureAwait(true);
            },
            cancellationToken);

    public async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await InvokeLifecycleAsync(
            () => ReleaseBrowserProcessCoreAsync(
                releaseEnvironment: true,
                cancellationToken),
            cancellationToken,
            allowAfterClose: true).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await InvokeOnDispatcherAsync(
                () => ReleaseBrowserProcessCoreAsync(
                    releaseEnvironment: true,
                    CancellationToken.None),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
            _lifetime.Dispose();
        }
    }

    private async ValueTask InvokeLifecycleAsync(
        Func<Task> action,
        CancellationToken cancellationToken,
        bool allowAfterClose = false)
    {
        if (!allowAfterClose)
        {
            ThrowIfDisposed();
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            await InvokeOnDispatcherAsync(action, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task CreateWebViewCoreAsync()
    {
        _container.Dispatcher.VerifyAccess();
        var binding = _binding ?? throw new InvalidOperationException(
            "The target content runtime has not been initialized.");
        var policy = _policy ?? throw new InvalidOperationException(
            "The target content runtime has no security policy.");

        if (_environment is null)
        {
            _environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                binding.UserDataFolder).ConfigureAwait(true);
        }

        var browserLease = BrowserProcessEnvironmentLease.Register(
            new DshLauncher.Core.TargetId(binding.TargetId),
            _environment,
            BrowserProcessReleaseTimeout);
        _browserLease = browserLease;
        var webView = new WebView2
        {
            AllowExternalDrop = true,
        };
        _container.Children.Add(webView);
        WebView2ResponseCspBoundary? responseCspBoundary = null;
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            await webView.EnsureCoreWebView2Async(_environment)
                .ConfigureAwait(true);
            browserLease.CaptureBrowserProcessId(
                webView.CoreWebView2.BrowserProcessId);
            ConfigureSecurity(webView.CoreWebView2, policy.RuntimePolicy);
            responseCspBoundary = await WebView2ResponseCspBoundary.EnableAsync(
                webView.CoreWebView2,
                binding,
                () => FireSignal(TargetRuntimeSignal.Blocked()))
                .ConfigureAwait(true);
            Subscribe(webView);
            _responseCspBoundary = responseCspBoundary;
            _webView = webView;
            _processFailurePublished = false;
            _authenticationInvalidPublished = false;
        }
        catch
        {
            responseCspBoundary?.Dispose();
            if (webView.CoreWebView2 is { } core)
            {
                core.Stop();
            }

            _container.Children.Remove(webView);
            webView.Dispose();
            try
            {
                await browserLease.WaitForReleaseAsync(
                    CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "The failed WebView2 initialization did not release its user data folder.",
                    exception);
            }
            _browserLease = null;
            _environment = null;

            throw;
        }
    }

    private Task NavigateToRootCoreAsync()
    {
        _container.Dispatcher.VerifyAccess();
        var webView = _webView ?? throw new InvalidOperationException(
            "The WebView2 target runtime is closed.");
        webView.CoreWebView2.Navigate(
            (_binding ?? throw new InvalidOperationException()).Origin.AbsoluteUri);
        return Task.CompletedTask;
    }

    private static void ConfigureSecurity(
        CoreWebView2 core,
        TargetRuntimeSecurityPolicy policy)
    {
        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = policy.AllowDefaultContextMenus;
        settings.AreDevToolsEnabled = policy.AllowDevTools;
        settings.AreHostObjectsAllowed = policy.AllowHostObjects;
        settings.IsWebMessageEnabled = policy.AllowWebMessages;
        settings.AreBrowserAcceleratorKeysEnabled =
            policy.AllowFindInPage || policy.AllowZoom;
        settings.IsStatusBarEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsZoomControlEnabled = policy.AllowZoom;
        settings.IsPinchZoomEnabled = policy.AllowZoom;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
    }

    private void Subscribe(WebView2 webView)
    {
        var core = webView.CoreWebView2;
        webView.PreviewKeyDown += OnWebViewPreviewKeyDown;
        core.AddWebResourceRequestedFilter(
            "*",
            CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.WebResourceResponseReceived += OnWebResourceResponseReceived;
        core.NavigationStarting += OnNavigationStarting;
        core.FrameNavigationStarting += OnFrameNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
        core.DownloadStarting += OnDownloadStarting;
        core.PermissionRequested += OnPermissionRequested;
        core.ServerCertificateErrorDetected += OnServerCertificateErrorDetected;
        core.BasicAuthenticationRequested += OnBasicAuthenticationRequested;
        core.ClientCertificateRequested += OnClientCertificateRequested;
        core.ProcessFailed += OnProcessFailed;
    }

    private void Unsubscribe(WebView2 webView)
    {
        var core = webView.CoreWebView2;
        webView.PreviewKeyDown -= OnWebViewPreviewKeyDown;
        core.WebResourceRequested -= OnWebResourceRequested;
        core.WebResourceResponseReceived -= OnWebResourceResponseReceived;
        core.RemoveWebResourceRequestedFilter(
            "*",
            CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
        core.NavigationStarting -= OnNavigationStarting;
        core.FrameNavigationStarting -= OnFrameNavigationStarting;
        core.NavigationCompleted -= OnNavigationCompleted;
        core.NewWindowRequested -= OnNewWindowRequested;
        core.LaunchingExternalUriScheme -= OnLaunchingExternalUriScheme;
        core.DownloadStarting -= OnDownloadStarting;
        core.PermissionRequested -= OnPermissionRequested;
        core.ServerCertificateErrorDetected -= OnServerCertificateErrorDetected;
        core.BasicAuthenticationRequested -= OnBasicAuthenticationRequested;
        core.ClientCertificateRequested -= OnClientCertificateRequested;
        core.ProcessFailed -= OnProcessFailed;
    }

    private void DisposeWebViewCore()
    {
        _container.Dispatcher.VerifyAccess();
        var webView = _webView;
        _webView = null;
        _responseCspBoundary?.Dispose();
        _responseCspBoundary = null;
        _cancelledNavigations.Clear();
        if (webView is null)
        {
            return;
        }

        if (webView.CoreWebView2 is { } core)
        {
            Unsubscribe(webView);
            core.Stop();
        }

        _container.Children.Remove(webView);
        webView.Dispose();
    }

    private async Task ReleaseBrowserProcessCoreAsync(
        bool releaseEnvironment,
        CancellationToken cancellationToken)
    {
        _container.Dispatcher.VerifyAccess();
        DisposeWebViewCore();
        var browserLease = _browserLease;
        if (browserLease is not null)
        {
            await browserLease.WaitForReleaseAsync(cancellationToken)
                .ConfigureAwait(true);
            _browserLease = null;
        }

        if (releaseEnvironment && _environment is not null)
        {
            _environment = null;
        }
    }

    private void OnNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var destination))
        {
            CancelAndBlock(args);
            return;
        }

        var decision = (_policy ?? throw new InvalidOperationException())
            .EvaluateNavigation(destination, args.IsUserInitiated);
        switch (decision.Disposition)
        {
            case TargetNavigationDisposition.AllowInTarget:
                FireSignal(TargetRuntimeSignal.NavigationStarted());
                break;
            case TargetNavigationDisposition.RequireExternalConfirmation
                when destination.Scheme == Uri.UriSchemeMailto:
                Cancel(args);
                break;
            case TargetNavigationDisposition.RequireExternalConfirmation
                when IsCurrentDocumentBound() && args.IsUserInitiated:
                Cancel(args);
                FireAndForgetExternal(destination, isUserInitiated: true);
                break;
            default:
                CancelAndBlock(args);
                break;
        }
    }

    private static void OnWebViewPreviewKeyDown(
        object sender,
        KeyEventArgs args)
    {
        var keyboardModifiers = Keyboard.Modifiers;
        var modifiers = TargetAcceleratorModifiers.None;
        if (keyboardModifiers.HasFlag(ModifierKeys.Control))
        {
            modifiers |= TargetAcceleratorModifiers.Control;
        }

        if (keyboardModifiers.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= TargetAcceleratorModifiers.Alt;
        }

        if (keyboardModifiers.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= TargetAcceleratorModifiers.Shift;
        }

        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        var disposition = TargetBrowserAcceleratorPolicy.Classify(
            KeyInterop.VirtualKeyFromKey(key),
            modifiers);
        if (disposition == TargetBrowserAcceleratorDisposition.Block)
        {
            args.Handled = true;
        }
    }

    private void OnWebResourceRequested(
        object? sender,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (Uri.TryCreate(
                args.Request.Uri,
                UriKind.Absolute,
                out var resource) &&
            (_policy ?? throw new InvalidOperationException())
                .AllowsResource(resource))
        {
            return;
        }

        args.Response = (_environment ?? throw new InvalidOperationException())
            .CreateWebResourceResponse(
                Stream.Null,
                403,
                "Forbidden",
                "Cache-Control: no-store");
        FireSignal(TargetRuntimeSignal.Blocked());
    }

    private void OnWebResourceResponseReceived(
        object? sender,
        CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        var binding = _binding ?? throw new InvalidOperationException();
        if (TargetAuthenticationResponsePolicy.IsInvalidApiResponse(
                binding,
                args.Request.Uri,
                args.Response.StatusCode))
        {
            PublishAuthenticationInvalidOnce();
        }
    }

    private void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_cancelledNavigations.Remove(args.NavigationId))
        {
            return;
        }

        var binding = _binding ?? throw new InvalidOperationException();
        var source = _webView?.CoreWebView2.Source ?? string.Empty;
        if (TargetAuthenticationResponsePolicy.IsInvalidRootNavigation(
                binding,
                source,
                args.HttpStatusCode))
        {
            PublishAuthenticationInvalidOnce();
        }
        else if (args.IsSuccess && IsCurrentDocumentBound())
        {
            _processFailurePublished = false;
            FireSignal(TargetRuntimeSignal.Ready());
        }
        else
        {
            FireSignal(TargetRuntimeSignal.NavigationFailed());
        }
    }

    private void PublishAuthenticationInvalidOnce()
    {
        if (_authenticationInvalidPublished)
        {
            return;
        }

        _authenticationInvalidPublished = true;
        FireSignal(TargetRuntimeSignal.AuthenticationInvalid());
    }

    private void OnFrameNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var destination) ||
            (_policy ?? throw new InvalidOperationException())
                .EvaluateNavigation(destination, isUserInitiated: false)
                .Disposition != TargetNavigationDisposition.AllowInTarget)
        {
            args.Cancel = true;
            FireSignal(TargetRuntimeSignal.Blocked());
        }
    }

    private void OnNewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (!args.IsUserInitiated || !IsCurrentDocumentBound() ||
            !IsBoundOrigin(args.OriginalSourceFrameInfo.Source) ||
            !Uri.TryCreate(args.Uri, UriKind.Absolute, out var destination))
        {
            FireSignal(TargetRuntimeSignal.Blocked());
            return;
        }

        var decision = (_policy ?? throw new InvalidOperationException())
            .EvaluateNavigation(destination, isUserInitiated: true);
        if (decision.Disposition == TargetNavigationDisposition.AllowInTarget)
        {
            _webView?.CoreWebView2.Navigate(destination.AbsoluteUri);
            return;
        }

        if (decision.Disposition !=
            TargetNavigationDisposition.RequireExternalConfirmation)
        {
            FireSignal(TargetRuntimeSignal.Blocked());
            return;
        }

        var deferral = args.GetDeferral();
        FireAndForgetExternal(destination, isUserInitiated: true, deferral);
    }

    private void OnLaunchingExternalUriScheme(
        object? sender,
        CoreWebView2LaunchingExternalUriSchemeEventArgs args)
    {
        args.Cancel = true;
        if (!args.IsUserInitiated || !IsCurrentDocumentBound() ||
            !IsBoundOrigin(args.InitiatingOrigin) ||
            !Uri.TryCreate(args.Uri, UriKind.Absolute, out var destination) ||
            destination.Scheme != Uri.UriSchemeMailto)
        {
            FireSignal(TargetRuntimeSignal.Blocked());
            return;
        }

        var deferral = args.GetDeferral();
        FireAndForgetExternal(destination, isUserInitiated: true, deferral);
    }

    private void OnDownloadStarting(
        object? sender,
        CoreWebView2DownloadStartingEventArgs args)
    {
        args.Cancel = true;
        args.Handled = true;
        FireSignal(TargetRuntimeSignal.DownloadBlocked());
    }

    private static void OnPermissionRequested(
        object? sender,
        CoreWebView2PermissionRequestedEventArgs args)
    {
        args.State = CoreWebView2PermissionState.Deny;
        args.SavesInProfile = false;
        args.Handled = true;
    }

    private void OnServerCertificateErrorDetected(
        object? sender,
        CoreWebView2ServerCertificateErrorDetectedEventArgs args)
    {
        args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        FireSignal(TargetRuntimeSignal.Blocked());
    }

    private void OnBasicAuthenticationRequested(
        object? sender,
        CoreWebView2BasicAuthenticationRequestedEventArgs args)
    {
        args.Cancel = true;
        FireSignal(TargetRuntimeSignal.Blocked());
    }

    private void OnClientCertificateRequested(
        object? sender,
        CoreWebView2ClientCertificateRequestedEventArgs args)
    {
        args.SelectedCertificate = null;
        args.Cancel = true;
        args.Handled = true;
        FireSignal(TargetRuntimeSignal.Blocked());
    }

    private void OnProcessFailed(
        object? sender,
        CoreWebView2ProcessFailedEventArgs args)
    {
        if (_processFailurePublished)
        {
            return;
        }

        TargetRuntimeSignal? signal = args.ProcessFailedKind switch
        {
            CoreWebView2ProcessFailedKind.BrowserProcessExited =>
                TargetRuntimeSignal.BrowserFailed(),
            CoreWebView2ProcessFailedKind.RenderProcessExited =>
                TargetRuntimeSignal.RendererFailed(),
            CoreWebView2ProcessFailedKind.RenderProcessUnresponsive =>
                TargetRuntimeSignal.Unresponsive(),
            _ => null,
        };
        if (signal is not null)
        {
            _processFailurePublished = true;
            FireSignal(signal);
        }
    }

    private void CancelAndBlock(
        CoreWebView2NavigationStartingEventArgs args)
    {
        Cancel(args);
        FireSignal(TargetRuntimeSignal.Blocked());
    }

    private void Cancel(CoreWebView2NavigationStartingEventArgs args)
    {
        args.Cancel = true;
        _cancelledNavigations.Add(args.NavigationId);
    }

    private bool IsCurrentDocumentBound()
    {
        return _webView?.CoreWebView2 is { } core &&
               IsBoundOrigin(core.Source);
    }

    private bool IsBoundOrigin(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (_policy ?? throw new InvalidOperationException())
                   .EvaluateNavigation(uri, isUserInitiated: false)
                   .Disposition == TargetNavigationDisposition.AllowInTarget;
    }

    private void FireAndForgetExternal(
        Uri destination,
        bool isUserInitiated,
        CoreWebView2Deferral? deferral = null)
    {
        _ = HandleExternalAsync(
            destination,
            isUserInitiated,
            deferral);
    }

    private async Task HandleExternalAsync(
        Uri destination,
        bool isUserInitiated,
        CoreWebView2Deferral? deferral)
    {
        try
        {
            var outcome = await (_externalNavigation ??
                throw new InvalidOperationException()).HandleAsync(
                    destination,
                    isUserInitiated,
                    _lifetime.Token).ConfigureAwait(true);
            if (outcome == TargetExternalNavigationOutcome.Blocked)
            {
                await PublishSignalSafeAsync(TargetRuntimeSignal.Blocked())
                    .ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            await PublishSignalSafeAsync(TargetRuntimeSignal.Blocked())
                .ConfigureAwait(true);
        }
        finally
        {
            deferral?.Complete();
        }
    }

    private void FireSignal(TargetRuntimeSignal signal)
    {
        _ = PublishSignalSafeAsync(signal);
    }

    private async Task PublishSignalSafeAsync(TargetRuntimeSignal signal)
    {
        var sink = _signalSink;
        if (sink is null || _lifetime.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await sink(signal, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Runtime event delivery cannot escape the adapter event pump.
        }
    }

    private Task InvokeOnDispatcherAsync(
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        if (_container.Dispatcher.CheckAccess())
        {
            return action();
        }

        return _container.Dispatcher.InvokeAsync(
            action,
            DispatcherPriority.Normal,
            cancellationToken).Task.Unwrap();
    }

    private void EnsureInitialized()
    {
        if (_binding is null || _signalSink is null)
        {
            throw new InvalidOperationException(
                "The WebView2 target runtime has not been initialized.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }
}
