using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace DshLauncher.Desktop;

/// <summary>
/// Sends pairing HTTP requests through the same Chromium TLS stack as the remote UI.
/// Each origin has an isolated, private browser profile. No remote scripts, automatic
/// redirects, downloads, or browser-managed credentials are allowed to escape a request.
/// </summary>
public sealed class WebViewHttpMessageHandler(Dispatcher dispatcher) : HttpMessageHandler
{
    private readonly Dispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _dataFolder = Path.Combine(Path.GetTempPath(), "DshLauncher.Transport", Guid.NewGuid().ToString("N"));
    private Task<CoreWebView2Environment>? _environment;
    private bool _disposed;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var uri = request.RequestUri ?? throw new InvalidOperationException("An absolute request URI is required.");
        var session = _sessions.GetOrAdd(uri.GetLeftPart(UriPartial.Authority), static _ => new Session());
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await session.Gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            return await _dispatcher.InvokeAsync(
                () => SendOnDispatcherAsync(session, request, lifetime.Token),
                DispatcherPriority.Normal, lifetime.Token).Task.Unwrap().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or IOException or
                                          WebView2RuntimeNotFoundException or TimeoutException or ArgumentException)
        {
            throw new HttpRequestException("The browser HTTP request could not be completed.", exception);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendOnDispatcherAsync(Session session, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var environment = await (_environment ??= CoreWebView2Environment.CreateAsync(userDataFolder: _dataFolder)).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (session.Controller is null)
        {
            session.Host = new HwndSource(new HwndSourceParameters("DSH pairing transport")
            {
                Width = 1,
                Height = 1,
                PositionX = -10000,
                PositionY = -10000,
                WindowStyle = 0,
            });
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = Guid.NewGuid().ToString("N");
            options.IsInPrivateModeEnabled = true;
            session.Controller = await environment.CreateCoreWebView2ControllerAsync(session.Host.Handle, options).ConfigureAwait(true);
            session.Controller.IsVisible = false;
            var settings = session.Controller.CoreWebView2.Settings;
            settings.IsScriptEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            var initializedCore = session.Controller.CoreWebView2;
            initializedCore.NewWindowRequested += static (_, args) => args.Handled = true;
            initializedCore.DownloadStarting += static (_, args) => args.Cancel = true;
            initializedCore.PermissionRequested += static (_, args) => args.State = CoreWebView2PermissionState.Deny;
            initializedCore.BasicAuthenticationRequested += static (_, args) => args.Cancel = true;
            initializedCore.ClientCertificateRequested += static (_, args) => args.Handled = true;
            initializedCore.ServerCertificateErrorDetected += static (_, args) => args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
            initializedCore.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            await initializedCore.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", "{\"cacheDisabled\":true}").ConfigureAwait(true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var core = session.Controller.CoreWebView2;
        var uri = request.RequestUri!;
        core.CookieManager.DeleteAllCookies();
        using var body = request.Content is null ? null : new MemoryStream(await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(true));
        var headers = new System.Text.StringBuilder();
        foreach (var header in request.Headers)
        {
            if (!header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                headers.Append(header.Key).Append(": ").AppendJoin(", ", header.Value).Append("\r\n");
            }
        }
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) { continue; }
                headers.Append(header.Key).Append(": ").AppendJoin(", ", header.Value).Append("\r\n");
            }
        }
        headers.Append("Cache-Control: no-cache, no-store\r\n");
        var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = false;
        ulong? navigationId = null;
        string? activeRequestId = null;

        void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (!args.IsRedirected && args.Uri == uri.AbsoluteUri) { navigationId = args.NavigationId; }
            // HttpPairingTransport applies the redirect policy and recreates the POST.
            // Chromium must not perform a second request with a token on its own.
            if (args.IsRedirected || !string.Equals(args.Uri, uri.AbsoluteUri, StringComparison.Ordinal))
            {
                args.Cancel = true;
            }
        }
        void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            if (!string.Equals(args.Request.Uri, uri.AbsoluteUri, StringComparison.Ordinal) ||
                !string.Equals(args.Request.Method, request.Method.Method, StringComparison.Ordinal))
            {
                args.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            }
        }
        void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.NavigationId == navigationId && !args.IsSuccess && !received && args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            {
                completion.TrySetException(new HttpRequestException("Browser navigation failed: " + args.WebErrorStatus));
            }
        }
        var receiver = core.GetDevToolsProtocolEventReceiver("Fetch.requestPaused");
        async void OnResponsePaused(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
        {
            HttpResponseMessage? response = null;
            try
            {
                using var parameters = JsonDocument.Parse(args.ParameterObjectAsJson);
                var payload = parameters.RootElement;
                var id = payload.GetProperty("requestId").GetString();
                var intercepted = payload.GetProperty("request");
                var stop = JsonSerializer.Serialize(new { requestId = id, errorReason = "Aborted" });
                if (received || intercepted.GetProperty("url").GetString() != uri.AbsoluteUri ||
                    intercepted.GetProperty("method").GetString() != request.Method.Method)
                {
                    await core.CallDevToolsProtocolMethodAsync("Fetch.failRequest", stop).ConfigureAwait(true);
                    return;
                }
                if (!payload.TryGetProperty("responseStatusCode", out _) &&
                    !payload.TryGetProperty("responseErrorReason", out _))
                {
                    activeRequestId = id;
                    // This request originates in the native application. Replace
                    // about:blank's synthetic Origin only after Chromium has built
                    // its headers, and bind it to the validated target authority.
                    var outgoing = intercepted.GetProperty("headers").EnumerateObject()
                        .Where(header => !header.Name.Equals("Origin", StringComparison.OrdinalIgnoreCase) &&
                            !header.Name.Equals("Sec-Fetch-Site", StringComparison.OrdinalIgnoreCase) &&
                            !header.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                        .Select(header => new { name = header.Name, value = header.Value.GetString()! }).ToList();
                    outgoing.Add(new { name = "Origin", value = uri.GetLeftPart(UriPartial.Authority) });
                    outgoing.Add(new { name = "Sec-Fetch-Site", value = "same-origin" });
                    if (request.Headers.TryGetValues("Cookie", out var explicitCookies))
                    {
                        outgoing.Add(new { name = "Cookie", value = string.Join("; ", explicitCookies) });
                    }
                    await core.CallDevToolsProtocolMethodAsync("Fetch.continueRequest",
                        JsonSerializer.Serialize(new { requestId = id, headers = outgoing, interceptResponse = true })).ConfigureAwait(true);
                    return;
                }
                if (id != activeRequestId)
                {
                    await core.CallDevToolsProtocolMethodAsync("Fetch.failRequest", stop).ConfigureAwait(true);
                    return;
                }
                received = true;
                if (!payload.TryGetProperty("responseStatusCode", out var code))
                {
                    await core.CallDevToolsProtocolMethodAsync("Fetch.failRequest", stop).ConfigureAwait(true);
                    completion.TrySetException(new HttpRequestException("The browser HTTP connection failed."));
                    return;
                }
                var status = code.GetInt32();
                response = new HttpResponseMessage((HttpStatusCode)status);
                byte[] bytes = [];
                if (status is < 300 or >= 400)
                {
                    var json = await core.CallDevToolsProtocolMethodAsync("Fetch.getResponseBody",
                        JsonSerializer.Serialize(new { requestId = id })).ConfigureAwait(true);
                    using var bodyResult = JsonDocument.Parse(json);
                    var text = bodyResult.RootElement.GetProperty("body").GetString() ?? "";
                    bytes = bodyResult.RootElement.GetProperty("base64Encoded").GetBoolean()
                        ? Convert.FromBase64String(text) : System.Text.Encoding.UTF8.GetBytes(text);
                }
                response.Content = new ByteArrayContent(bytes);
                foreach (var header in payload.GetProperty("responseHeaders").EnumerateArray())
                {
                    var name = header.GetProperty("name").GetString()!;
                    var value = header.GetProperty("value").GetString()!;
                    // Chromium has already decompressed the body.
                    if (name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) { continue; }
                    if (!response.Headers.TryAddWithoutValidation(name, value))
                    {
                        response.Content.Headers.TryAddWithoutValidation(name, value);
                    }
                }
                // WebView2 removes Set-Cookie from Fetch response headers. The
                // private jar was empty before this request, so retrieve the server's
                // cookies here before clearing it; only names and values are needed.
                foreach (var cookie in await core.CookieManager.GetCookiesAsync(null).ConfigureAwait(true))
                {
                    response.Headers.TryAddWithoutValidation("Set-Cookie", cookie.Name + "=" + cookie.Value);
                }
                // Consume the response without navigating/rendering it. In particular,
                // a redirect cannot cause Chromium to forward a token independently.
                await core.CallDevToolsProtocolMethodAsync("Fetch.failRequest", stop).ConfigureAwait(true);
                if (completion.TrySetResult(response)) { response = null; }
            }
            catch (Exception exception) when (exception is COMException or IOException or InvalidOperationException or
                                              OperationCanceledException or JsonException or FormatException)
            {
                completion.TrySetException(new HttpRequestException("The browser response could not be read.", exception));
            }
            finally { response?.Dispose(); }
        }
        core.NavigationStarting += OnNavigationStarting;
        core.WebResourceRequested += OnResourceRequested;
        core.NavigationCompleted += OnNavigationCompleted;
        receiver.DevToolsProtocolEventReceived += OnResponsePaused;
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Fetch.enable", "{\"patterns\":[{\"urlPattern\":\"*\",\"requestStage\":\"Request\"}]}").ConfigureAwait(true);
            var resource = environment.CreateWebResourceRequest(uri.AbsoluteUri, request.Method.Method, body, headers.ToString());
            core.NavigateWithWebResourceRequest(resource);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            core.NavigationStarting -= OnNavigationStarting;
            core.WebResourceRequested -= OnResourceRequested;
            core.NavigationCompleted -= OnNavigationCompleted;
            receiver.DevToolsProtocolEventReceived -= OnResponsePaused;
            completion.TrySetCanceled(cancellationToken);
            core.Stop();
            core.CookieManager.DeleteAllCookies();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _lifetime.Cancel();
            void Close()
            {
                foreach (var session in _sessions.Values)
                {
                    session.Controller?.Close();
                    session.Host?.Dispose();
                }
            }
            if (_dispatcher.CheckAccess()) { Close(); }
            else if (!_dispatcher.HasShutdownStarted) { _dispatcher.Invoke(Close); }
            _lifetime.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class Session
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public HwndSource? Host { get; set; }
        public CoreWebView2Controller? Controller { get; set; }
    }
}









