using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DshLauncher.Compatibility;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DshLauncher.WebView;

public sealed class WebView2TargetContentRuntime : ITargetContentRuntime
{
    private static readonly TimeSpan BrowserProcessReleaseTimeout =
        TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DescriptorReadTimeout =
        TimeSpan.FromSeconds(15);
    private const int MaximumDescriptorBytes = 64 * 1024;
    private const string DescriptorPath =
        "/.well-known/dsh-webui-compatibility.json";

    private readonly Panel _container;
    private readonly ITargetExternalNavigationConsent _consent;
    private readonly ITargetExternalUriLauncher _launcher;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<ulong> _cancelledNavigations = [];
    private readonly Dictionary<CoreWebView2Frame, TrackedFrame> _trackedFrames = [];
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
    private bool _bootstrapActive;
    private Uri? _expectedDocumentNavigation;
    private Uri? _pendingDocumentNavigation;
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
        Func<TargetRuntimeSignal, CancellationToken, ValueTask> signalSink,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(binding);
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

            _binding = binding;
            _signalSink = signalSink;
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

    public ValueTask<BoundedDescriptorResponse> BeginPageCapabilityCycleAsync(
        CancellationToken cancellationToken) =>
        InvokeLifecycleAsync(
            BeginPageCapabilityCycleCoreAsync,
            cancellationToken);

    public ValueTask SuspendPageCapabilitiesAsync(
        CancellationToken cancellationToken) =>
        InvokeLifecycleAsync(
            SuspendPageCapabilitiesCoreAsync,
            cancellationToken);

    public ValueTask ActivatePageCapabilityAsync(
        PageCapabilitySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return InvokeLifecycleAsync(
            () => ActivatePageCapabilityCoreAsync(snapshot),
            cancellationToken);
    }

    public ValueTask RecreateWebViewAsync(CancellationToken cancellationToken) =>
        InvokeLifecycleAsync(
            async () =>
            {
                PreserveCurrentDocumentNavigationCore();
                await ReleaseBrowserProcessCoreAsync(
                    releaseEnvironment: false,
                    cancellationToken).ConfigureAwait(true);
                await CreateWebViewCoreAsync().ConfigureAwait(true);
            },
            cancellationToken);

    public ValueTask RecreateEnvironmentAsync(
        CancellationToken cancellationToken) =>
        InvokeLifecycleAsync(
            async () =>
            {
                PreserveCurrentDocumentNavigationCore();
                await ReleaseBrowserProcessCoreAsync(
                    releaseEnvironment: true,
                    cancellationToken).ConfigureAwait(true);
                await CreateWebViewCoreAsync().ConfigureAwait(true);
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

    private async ValueTask<T> InvokeLifecycleAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            if (_container.Dispatcher.CheckAccess())
            {
                return await action().ConfigureAwait(true);
            }

            return await _container.Dispatcher.InvokeAsync(
                action,
                DispatcherPriority.Normal,
                cancellationToken).Task.Unwrap().ConfigureAwait(false);
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
            AllowExternalDrop = false,
        };
        _container.Children.Add(webView);
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            await webView.EnsureCoreWebView2Async(_environment)
                .ConfigureAwait(true);
            await WebView2PersistentContentBoundary.ClearProhibitedDataAsync(
                webView.CoreWebView2).ConfigureAwait(true);
            browserLease.CaptureBrowserProcessId(
                webView.CoreWebView2.BrowserProcessId);
            ConfigureSecurity(
                webView.CoreWebView2,
                TargetRuntimeSecurityPolicy.Restricted,
                allowPageScript: false);
            Subscribe(webView);
            _webView = webView;
            _policy = null;
            _externalNavigation = null;
            _bootstrapActive = false;
            _processFailurePublished = false;
            _authenticationInvalidPublished = false;
        }
        catch
        {
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

    private async Task<BoundedDescriptorResponse>
        BeginPageCapabilityCycleCoreAsync()
    {
        _container.Dispatcher.VerifyAccess();
        var binding = _binding ?? throw new InvalidOperationException(
            "The target content runtime has not been initialized.");
        var webView = _webView ?? throw new InvalidOperationException(
            "The WebView2 target runtime is closed.");
        var core = webView.CoreWebView2;
        PreserveCurrentDocumentNavigationCore();
        await SuspendPageCapabilitiesCoreAsync().ConfigureAwait(true);

        var descriptorUri = new Uri(binding.Origin, DescriptorPath);
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<BoundedDescriptorResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var redirected = false;

        void OnStarting(
            object? _,
            CoreWebView2NavigationStartingEventArgs args)
        {
            if (args.IsRedirected)
            {
                redirected = true;
                args.Cancel = true;
            }
        }

        void OnCompleted(
            object? _,
            CoreWebView2NavigationCompletedEventArgs args)
        {
            completion.TrySetResult(args.IsSuccess);
        }

        void OnResponse(
            object? _,
            CoreWebView2WebResourceResponseReceivedEventArgs args)
        {
            if (!string.Equals(
                    args.Request.Uri,
                    descriptorUri.AbsoluteUri,
                    StringComparison.Ordinal))
            {
                return;
            }

            _ = CaptureDescriptorResponseAsync(
                args,
                redirected,
                response);
        }

        core.NavigationStarting += OnStarting;
        core.NavigationCompleted += OnCompleted;
        core.WebResourceResponseReceived += OnResponse;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token);
        timeout.CancelAfter(DescriptorReadTimeout);
        try
        {
            var descriptorRequest = (_environment ??
                throw new InvalidOperationException()).CreateWebResourceRequest(
                    descriptorUri.AbsoluteUri,
                    "GET",
                    Stream.Null,
                    "Cache-Control: no-cache, no-store\r\nPragma: no-cache");
            core.NavigateWithWebResourceRequest(descriptorRequest);
            _ = await completion.Task.WaitAsync(timeout.Token)
                .ConfigureAwait(true);
            if (redirected)
            {
                return BoundedDescriptorResponse.Received(
                    statusCode: 0,
                    contentType: null,
                    wasRedirected: true,
                    ReadOnlyMemory<byte>.Empty);
            }

            return await response.Task.WaitAsync(timeout.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            core.Stop();
            return BoundedDescriptorResponse.Missing();
        }
        finally
        {
            core.NavigationStarting -= OnStarting;
            core.NavigationCompleted -= OnCompleted;
            core.WebResourceResponseReceived -= OnResponse;
        }
    }

    private async Task SuspendPageCapabilitiesCoreAsync()
    {
        _container.Dispatcher.VerifyAccess();
        var binding = _binding ?? throw new InvalidOperationException(
            "The target content runtime has not been initialized.");
        var webView = _webView ?? throw new InvalidOperationException(
            "The WebView2 target runtime is closed.");
        var core = webView.CoreWebView2;
        core.Stop();
        ClearTrackedFrames();
        _policy = null;
        _externalNavigation = null;
        _bootstrapActive = true;
        _expectedDocumentNavigation = null;
        _authenticationInvalidPublished = false;
        _processFailurePublished = false;
        webView.AllowExternalDrop = false;
        ConfigureSecurity(
            core,
            TargetRuntimeSecurityPolicy.Restricted,
            allowPageScript: false);

        _responseCspBoundary?.Dispose();
        _responseCspBoundary = null;
        _responseCspBoundary = await WebView2ResponseCspBoundary.EnableAsync(
            core,
            binding,
            TargetResponseCspPolicy.CreateTransientPolicy(binding),
            () => FireSignal(TargetRuntimeSignal.Blocked()))
            .ConfigureAwait(true);
    }

    private async Task ActivatePageCapabilityCoreAsync(
        PageCapabilitySnapshot snapshot)
    {
        _container.Dispatcher.VerifyAccess();
        var binding = _binding ?? throw new InvalidOperationException(
            "The target content runtime has not been initialized.");
        var webView = _webView ?? throw new InvalidOperationException(
            "The WebView2 target runtime is closed.");
        if (snapshot.TargetId != binding.TargetId)
        {
            throw new ArgumentException(
                "The page capability snapshot must belong to the target binding.",
                nameof(snapshot));
        }

        var policy = new TargetContentSecurityPolicy(binding, snapshot);
        _responseCspBoundary?.Dispose();
        _responseCspBoundary = null;
        var responseCspBoundary = await WebView2ResponseCspBoundary.EnableAsync(
            webView.CoreWebView2,
            binding,
            TargetResponseCspPolicy.CreatePolicy(binding, snapshot),
            () => FireSignal(TargetRuntimeSignal.Blocked()))
            .ConfigureAwait(true);
        _responseCspBoundary = responseCspBoundary;
        _policy = policy;
        _externalNavigation = new TargetExternalNavigationCoordinator(
            policy,
            _consent,
            _launcher);
        ConfigureSecurity(
            webView.CoreWebView2,
            policy.RuntimePolicy,
            allowPageScript: true);
        webView.AllowExternalDrop = snapshot.BaseCapabilities
            .Concat(snapshot.ExtensionCapabilities)
            .Any(static grant => grant.Kind == CapabilityKind.UserImageInput);
        _bootstrapActive = false;
    }

    private static async Task CaptureDescriptorResponseAsync(
        CoreWebView2WebResourceResponseReceivedEventArgs args,
        bool redirected,
        TaskCompletionSource<BoundedDescriptorResponse> completion)
    {
        try
        {
            var contentType = TryGetHeader(
                args.Response.Headers,
                "Content-Type");
            await using var content = await args.Response.GetContentAsync()
                .ConfigureAwait(true);
            using var buffer = new MemoryStream();
            var block = new byte[8192];
            while (buffer.Length <= MaximumDescriptorBytes)
            {
                var read = await content.ReadAsync(block).ConfigureAwait(true);
                if (read == 0)
                {
                    break;
                }

                var remaining = MaximumDescriptorBytes + 1 - (int)buffer.Length;
                buffer.Write(block, 0, Math.Min(read, remaining));
                if (buffer.Length > MaximumDescriptorBytes)
                {
                    break;
                }
            }

            completion.TrySetResult(BoundedDescriptorResponse.Received(
                args.Response.StatusCode,
                contentType,
                redirected || args.Response.StatusCode is >= 300 and <= 399,
                buffer.ToArray()));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private static string? TryGetHeader(
        CoreWebView2HttpResponseHeaders headers,
        string name)
    {
        try
        {
            return headers.GetHeader(name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private Task NavigateToRootCoreAsync()
    {
        _container.Dispatcher.VerifyAccess();
        var webView = _webView ?? throw new InvalidOperationException(
            "The WebView2 target runtime is closed.");
        if (_policy is null || _bootstrapActive)
        {
            throw new InvalidOperationException(
                "A page capability snapshot must be activated before root navigation.");
        }
        var destination = _pendingDocumentNavigation ??
            (_binding ?? throw new InvalidOperationException()).Origin;
        _pendingDocumentNavigation = null;
        _expectedDocumentNavigation = destination;
        try
        {
            webView.CoreWebView2.Navigate(destination.AbsoluteUri);
        }
        catch
        {
            _expectedDocumentNavigation = null;
            throw;
        }
        return Task.CompletedTask;
    }

    private static void ConfigureSecurity(
        CoreWebView2 core,
        TargetRuntimeSecurityPolicy policy,
        bool allowPageScript)
    {
        var settings = core.Settings;
        settings.IsScriptEnabled = allowPageScript;
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
        core.FrameCreated += OnRootFrameCreated;
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
        core.FrameCreated -= OnRootFrameCreated;
        ClearTrackedFrames();
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
        _policy = null;
        _externalNavigation = null;
        _bootstrapActive = false;
        _expectedDocumentNavigation = null;
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
        if (_bootstrapActive)
        {
            if (!IsDescriptorUri(args.Uri) || args.IsRedirected)
            {
                Cancel(args);
            }

            return;
        }

        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var destination))
        {
            CancelAndBlock(args);
            return;
        }

        var decision = (_policy ?? throw new InvalidOperationException())
            .EvaluateNavigation(destination, args.IsUserInitiated);
        switch (decision.Disposition)
        {
            case TargetNavigationDisposition.AllowInTarget
                when _expectedDocumentNavigation is { } expected &&
                     !args.IsRedirected &&
                     string.Equals(
                         destination.AbsoluteUri,
                         expected.AbsoluteUri,
                         StringComparison.Ordinal):
                _expectedDocumentNavigation = null;
                FireSignal(TargetRuntimeSignal.NavigationStarted());
                break;
            case TargetNavigationDisposition.AllowInTarget
                when !args.IsRedirected &&
                     IsReplayableGetNavigation(args):
                _pendingDocumentNavigation = destination;
                Cancel(args);
                FireSignal(TargetRuntimeSignal.PageCapabilityCycleRequested());
                break;
            case TargetNavigationDisposition.AllowInTarget:
                CancelAndBlock(args);
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

    private static bool IsReplayableGetNavigation(
        CoreWebView2NavigationStartingEventArgs args)
    {
        try
        {
            return !args.RequestHeaders.Contains("Content-Type");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private void OnWebResourceRequested(
        object? sender,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (_bootstrapActive)
        {
            if (args.ResourceContext == CoreWebView2WebResourceContext.Document &&
                string.Equals(
                    args.Request.Method,
                    "GET",
                    StringComparison.Ordinal) &&
                IsDescriptorUri(args.Request.Uri))
            {
                return;
            }

            BlockResource(args);
            return;
        }

        if (Uri.TryCreate(
                args.Request.Uri,
                UriKind.Absolute,
                out var resource) &&
            (_policy ?? throw new InvalidOperationException())
                .AllowsResource(
                    resource,
                    ClassifyResourceContext(args.ResourceContext),
                    args.Request.Method))
        {
            return;
        }

        BlockResource(args);
    }

    private void BlockResource(
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        args.Response = (_environment ?? throw new InvalidOperationException())
            .CreateWebResourceResponse(
                Stream.Null,
                403,
                "Forbidden",
                "Cache-Control: no-store");
    }

    private static TargetContentResourceKind ClassifyResourceContext(
        CoreWebView2WebResourceContext resourceContext)
    {
        return resourceContext switch
        {
            CoreWebView2WebResourceContext.Document =>
                TargetContentResourceKind.Document,
            CoreWebView2WebResourceContext.Stylesheet =>
                TargetContentResourceKind.Stylesheet,
            CoreWebView2WebResourceContext.Image =>
                TargetContentResourceKind.Image,
            CoreWebView2WebResourceContext.Media =>
                TargetContentResourceKind.Media,
            CoreWebView2WebResourceContext.Font =>
                TargetContentResourceKind.Font,
            CoreWebView2WebResourceContext.Script =>
                TargetContentResourceKind.Script,
            CoreWebView2WebResourceContext.XmlHttpRequest =>
                TargetContentResourceKind.XmlHttpRequest,
            CoreWebView2WebResourceContext.Fetch =>
                TargetContentResourceKind.Fetch,
            CoreWebView2WebResourceContext.TextTrack =>
                TargetContentResourceKind.TextTrack,
            CoreWebView2WebResourceContext.EventSource =>
                TargetContentResourceKind.EventSource,
            CoreWebView2WebResourceContext.Websocket =>
                TargetContentResourceKind.Websocket,
            CoreWebView2WebResourceContext.Manifest =>
                TargetContentResourceKind.Manifest,
            CoreWebView2WebResourceContext.SignedExchange =>
                TargetContentResourceKind.SignedExchange,
            CoreWebView2WebResourceContext.Ping =>
                TargetContentResourceKind.Ping,
            CoreWebView2WebResourceContext.CspViolationReport =>
                TargetContentResourceKind.CspViolationReport,
            _ => TargetContentResourceKind.Other,
        };
    }

    private void OnWebResourceResponseReceived(
        object? sender,
        CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        if (_bootstrapActive)
        {
            return;
        }

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
        if (_bootstrapActive)
        {
            return;
        }

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

    private void OnRootFrameCreated(
        object? sender,
        CoreWebView2FrameCreatedEventArgs args)
    {
        TrackFrame(args.Frame, parent: null);
    }

    private void OnChildFrameCreated(
        object? sender,
        CoreWebView2FrameCreatedEventArgs args)
    {
        var parent = sender as CoreWebView2Frame;
        TrackFrame(
            args.Frame,
            parent is not null && _trackedFrames.TryGetValue(parent, out var state)
                ? state
                : TrackedFrame.UnknownParent);
    }

    private void OnTrackedFrameNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (sender is not CoreWebView2Frame frame ||
            !_trackedFrames.TryGetValue(frame, out var state) ||
            _bootstrapActive ||
            args.IsRedirected)
        {
            args.Cancel = true;
            FireSignal(TargetRuntimeSignal.Blocked());
            return;
        }

        var parentOrigin = state.Parent is null
            ? CurrentRootDocumentOrigin()
            : state.Parent.CommittedOrigin;
        if (parentOrigin is null ||
            !Uri.TryCreate(args.Uri, UriKind.Absolute, out var destination) ||
            (_policy ?? throw new InvalidOperationException())
                .AllowsFrameNavigation(destination, parentOrigin) is false)
        {
            args.Cancel = true;
            FireSignal(TargetRuntimeSignal.Blocked());
            return;
        }

        state.PendingNavigationId = args.NavigationId;
        state.PendingOrigin = string.Equals(
            destination.OriginalString,
            "about:blank",
            StringComparison.OrdinalIgnoreCase)
                ? parentOrigin
                : ToOrigin(destination);
        state.CommittedOrigin = null;
    }

    private void OnTrackedFrameContentLoading(
        object? sender,
        CoreWebView2ContentLoadingEventArgs args)
    {
        if (sender is CoreWebView2Frame frame &&
            _trackedFrames.TryGetValue(frame, out var state) &&
            state.PendingNavigationId == args.NavigationId)
        {
            state.CommittedOrigin = state.PendingOrigin;
        }
    }

    private void OnTrackedFrameNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        if (sender is not CoreWebView2Frame frame ||
            !_trackedFrames.TryGetValue(frame, out var state) ||
            state.PendingNavigationId != args.NavigationId)
        {
            return;
        }

        if (args.IsSuccess)
        {
            state.CommittedOrigin = state.PendingOrigin;
        }
        else
        {
            state.CommittedOrigin = null;
        }

        state.PendingNavigationId = null;
        state.PendingOrigin = null;
    }

    private void OnTrackedFrameDestroyed(object? sender, object args)
    {
        if (sender is CoreWebView2Frame frame)
        {
            UntrackFrame(frame);
        }
    }

    private void TrackFrame(CoreWebView2Frame frame, TrackedFrame? parent)
    {
        if (_trackedFrames.ContainsKey(frame))
        {
            return;
        }

        var state = new TrackedFrame(frame, parent);
        _trackedFrames.Add(frame, state);
        frame.NavigationStarting += OnTrackedFrameNavigationStarting;
        frame.ContentLoading += OnTrackedFrameContentLoading;
        frame.NavigationCompleted += OnTrackedFrameNavigationCompleted;
        frame.FrameCreated += OnChildFrameCreated;
        frame.Destroyed += OnTrackedFrameDestroyed;
    }

    private void UntrackFrame(CoreWebView2Frame frame)
    {
        if (!_trackedFrames.Remove(frame, out var state))
        {
            return;
        }

        foreach (var child in _trackedFrames.Values
                     .Where(candidate => ReferenceEquals(candidate.Parent, state))
                     .Select(candidate => candidate.Frame)
                     .ToArray())
        {
            UntrackFrame(child);
        }

        frame.NavigationStarting -= OnTrackedFrameNavigationStarting;
        frame.ContentLoading -= OnTrackedFrameContentLoading;
        frame.NavigationCompleted -= OnTrackedFrameNavigationCompleted;
        frame.FrameCreated -= OnChildFrameCreated;
        frame.Destroyed -= OnTrackedFrameDestroyed;
    }

    private void ClearTrackedFrames()
    {
        foreach (var frame in _trackedFrames.Keys.ToArray())
        {
            UntrackFrame(frame);
        }
    }

    private Uri? CurrentRootDocumentOrigin()
    {
        if (_webView?.CoreWebView2 is not { } core ||
            !Uri.TryCreate(core.Source, UriKind.Absolute, out var source) ||
            !IsBoundOrigin(source.AbsoluteUri))
        {
            return null;
        }

        return ToOrigin(source);
    }

    private static Uri ToOrigin(Uri uri) =>
        new(uri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);

    private sealed class TrackedFrame
    {
        private TrackedFrame()
        {
            Frame = null!;
        }

        public TrackedFrame(CoreWebView2Frame frame, TrackedFrame? parent)
        {
            Frame = frame;
            Parent = parent;
        }

        public static TrackedFrame UnknownParent { get; } = new();

        public CoreWebView2Frame Frame { get; }

        public TrackedFrame? Parent { get; }

        public Uri? CommittedOrigin { get; set; }

        public ulong? PendingNavigationId { get; set; }

        public Uri? PendingOrigin { get; set; }
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

    private void PreserveCurrentDocumentNavigationCore()
    {
        if (_pendingDocumentNavigation is not null ||
            _policy is null ||
            _webView?.CoreWebView2 is not { } core ||
            !Uri.TryCreate(core.Source, UriKind.Absolute, out var source) ||
            !IsBoundOrigin(source.AbsoluteUri))
        {
            return;
        }

        _pendingDocumentNavigation = source;
    }

    private bool IsBoundOrigin(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (_policy ?? throw new InvalidOperationException())
                   .EvaluateNavigation(uri, isUserInitiated: false)
                   .Disposition == TargetNavigationDisposition.AllowInTarget;
    }

    private bool IsDescriptorUri(string value)
    {
        var binding = _binding;
        return binding is not null &&
               Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               string.Equals(
                   uri.Scheme,
                   binding.Origin.Scheme,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   uri.Host,
                   binding.Origin.Host,
                   StringComparison.OrdinalIgnoreCase) &&
               uri.Port == binding.Origin.Port &&
               string.Equals(uri.AbsolutePath, DescriptorPath, StringComparison.Ordinal) &&
               string.IsNullOrEmpty(uri.Query) &&
               string.IsNullOrEmpty(uri.Fragment) &&
               string.IsNullOrEmpty(uri.UserInfo);
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
