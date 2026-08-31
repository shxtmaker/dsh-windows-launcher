using System.IO;
using System.Text;
using System.Text.Json;
using DshLauncher.Compatibility;
using Microsoft.Web.WebView2.Core;

namespace DshLauncher.WebView;

internal sealed record DevToolsFetchCommand(
    string Method,
    string ParametersJson,
    string RequestId);

internal static class TargetResponseCspPolicy
{
    private const string ContentSecurityPolicyHeader =
        "Content-Security-Policy";
    private static readonly string[] SelfSource = ["'self'"];

    public static string CreateTransientPolicy(TargetContentBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return "default-src 'none'; connect-src 'self'; script-src 'none'; " +
               "style-src 'none'; img-src 'none'; media-src 'none'; " +
               "font-src 'none'; frame-src 'none'; worker-src 'none'; " +
               "object-src 'none'; base-uri 'none'; form-action 'none'";
    }

    public static string CreatePolicy(
        TargetContentBinding binding,
        PageCapabilitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.TargetId != binding.TargetId)
        {
            throw new ArgumentException(
                "The page capability snapshot must belong to the target binding.",
                nameof(snapshot));
        }

        var grants = snapshot.BaseCapabilities
            .Concat(snapshot.ExtensionCapabilities)
            .ToArray();
        var imageSources = SourcesFor(grants, WebResourceKind.Image);
        if (grants.Any(static grant => grant.Kind == CapabilityKind.DataImage))
        {
            imageSources.Add("data:");
        }
        if (grants.Any(static grant => grant.Kind == CapabilityKind.BlobImage))
        {
            imageSources.Add("blob:");
        }

        var mediaSources = SourcesFor(grants, WebResourceKind.Media);
        if (grants.Any(static grant => grant.Kind == CapabilityKind.BlobMedia))
        {
            mediaSources.Add("blob:");
        }

        var connectSources = SourcesFor(
            grants,
            WebResourceKind.XmlHttpRequest,
            WebResourceKind.Fetch,
            WebResourceKind.EventSource,
            WebResourceKind.WebSocket);
        foreach (var grant in grants.Where(static grant =>
                     grant.Kind == CapabilityKind.TargetWebSocket &&
                     grant.Path is not null))
        {
            connectSources.Add($"ws://{binding.Authority}{grant.Path}");
        }

        var scriptSources = SelfSource
            .Concat(grants
                .Where(static grant =>
                    grant.Kind == CapabilityKind.ReviewedInlineScriptSha256 &&
                    grant.ScriptSha256 is not null)
                .Select(static grant => $"'sha256-{grant.ScriptSha256}'"));
        var frameSources = grants
            .Where(static grant => grant.Kind == CapabilityKind.Frame)
            .Select(static grant => grant.Origin)
            .Where(static origin => origin is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        var builder = new StringBuilder();
        AppendDirective(builder, "default-src", ["'none'"]);
        AppendDirective(builder, "connect-src", WithSelf(connectSources));
        AppendDirective(builder, "script-src", scriptSources);
        AppendDirective(
            builder,
            "style-src",
            WithSelf(SourcesFor(grants, WebResourceKind.Stylesheet)));
        AppendDirective(builder, "img-src", WithSelf(imageSources));
        AppendDirective(builder, "media-src", WithSelf(mediaSources));
        AppendDirective(
            builder,
            "font-src",
            WithSelf(SourcesFor(grants, WebResourceKind.Font)));
        AppendDirective(builder, "frame-src", WithSelf(frameSources));
        AppendDirective(builder, "worker-src", ["'none'"]);
        AppendDirective(builder, "object-src", ["'none'"]);
        AppendDirective(builder, "base-uri", ["'self'"]);
        AppendDirective(builder, "form-action", ["'self'"]);
        return builder.ToString();
    }

    public static string CreateEnableParameters(TargetContentBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return JsonSerializer.Serialize(new
        {
            patterns = new[]
            {
                new
                {
                    urlPattern = binding.Origin.AbsoluteUri + "*",
                    resourceType = "Document",
                    requestStage = "Response",
                },
            },
            handleAuthRequests = false,
        });
    }

    public static DevToolsFetchCommand? CreatePausedResponseCommand(
        string parameterObjectJson,
        TargetContentBinding binding,
        string contentSecurityPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterObjectJson);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSecurityPolicy);
        if (contentSecurityPolicy.Contains('\r') ||
            contentSecurityPolicy.Contains('\n'))
        {
            throw new ArgumentException(
                "The content security policy must be a single header value.",
                nameof(contentSecurityPolicy));
        }

        string? requestId = null;
        try
        {
            using var document = JsonDocument.Parse(parameterObjectJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!TryGetRequiredString(root, "requestId", out requestId))
            {
                return null;
            }

            if (!root.TryGetProperty("request", out var request) ||
                request.ValueKind != JsonValueKind.Object ||
                !TryGetRequiredString(request, "url", out var requestUrl) ||
                !Uri.TryCreate(requestUrl, UriKind.Absolute, out var resource) ||
                !IsBoundResource(resource, binding) ||
                !TryGetRequiredString(root, "resourceType", out var resourceType) ||
                !string.Equals(
                    resourceType,
                    "Document",
                    StringComparison.Ordinal) ||
                !root.TryGetProperty("responseStatusCode", out var statusElement) ||
                !statusElement.TryGetInt32(out var statusCode) ||
                statusCode is < 100 or > 599 ||
                !root.TryGetProperty("responseHeaders", out var headersElement) ||
                headersElement.ValueKind != JsonValueKind.Array)
            {
                return CreateFailRequest(requestId);
            }

            var headers = new List<(string Name, string Value)>();
            foreach (var header in headersElement.EnumerateArray())
            {
                if (header.ValueKind != JsonValueKind.Object ||
                    !TryGetRequiredString(header, "name", out var name) ||
                    !header.TryGetProperty("value", out var valueElement) ||
                    valueElement.ValueKind != JsonValueKind.String)
                {
                    return CreateFailRequest(requestId);
                }

                headers.Add((name, valueElement.GetString() ?? string.Empty));
            }

            headers.Add((ContentSecurityPolicyHeader, contentSecurityPolicy));
            var responseStatusText = root.TryGetProperty(
                    "responseStatusText",
                    out var statusTextElement) &&
                statusTextElement.ValueKind == JsonValueKind.String
                    ? statusTextElement.GetString()
                    : null;
            return new DevToolsFetchCommand(
                "Fetch.fulfillRequest",
                WriteFulfillParameters(
                    requestId,
                    statusCode,
                    responseStatusText,
                    headers),
                requestId);
        }
        catch (JsonException)
        {
            return requestId is null ? null : CreateFailRequest(requestId);
        }
        catch (InvalidOperationException)
        {
            return requestId is null ? null : CreateFailRequest(requestId);
        }
    }

    public static DevToolsFetchCommand CreateFailRequest(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("requestId", requestId);
            writer.WriteString("errorReason", "BlockedByClient");
            writer.WriteEndObject();
        }

        return new DevToolsFetchCommand(
            "Fetch.failRequest",
            Encoding.UTF8.GetString(stream.ToArray()),
            requestId);
    }

    private static string WriteFulfillParameters(
        string requestId,
        int statusCode,
        string? statusText,
        IReadOnlyList<(string Name, string Value)> headers)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("requestId", requestId);
            writer.WriteNumber("responseCode", statusCode);
            if (!string.IsNullOrEmpty(statusText))
            {
                writer.WriteString("responsePhrase", statusText);
            }

            writer.WriteStartArray("responseHeaders");
            foreach (var (name, value) in headers)
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteString("value", value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool TryGetRequiredString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool IsBoundResource(
        Uri resource,
        TargetContentBinding binding)
    {
        return resource.IsAbsoluteUri &&
               string.Equals(
                   resource.Scheme,
                   binding.Origin.Scheme,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   resource.Host,
                   binding.Origin.Host,
                   StringComparison.OrdinalIgnoreCase) &&
               resource.Port == binding.Origin.Port;
    }

    private static HashSet<string> SourcesFor(
        IEnumerable<CapabilityGrant> grants,
        params WebResourceKind[] resourceKinds)
    {
        var requested = resourceKinds.ToHashSet();
        return grants
            .Where(grant =>
                grant.Origin is not null &&
                grant.DocumentScope != CapabilityDocumentScope.CrossOriginFrameOnly &&
                grant.ResourceKinds.Any(requested.Contains))
            .Select(static grant => grant.Origin!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<string> WithSelf(IEnumerable<string> sources) =>
        SelfSource
            .Concat(sources.Order(StringComparer.Ordinal));

    private static void AppendDirective(
        StringBuilder builder,
        string name,
        IEnumerable<string> sources)
    {
        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        builder.Append(name);
        foreach (var source in sources.Distinct(StringComparer.Ordinal))
        {
            builder.Append(' ').Append(source);
        }

        builder.Append(';');
    }
}

internal static class TargetContentCspViolationPolicy
{
    private const string LegacyInlineScriptViolationPrefix =
        "Refused to execute inline script because it violates the following " +
        "Content Security Policy directive";
    private const string CurrentInlineScriptViolationPrefix =
        "Executing inline script violates the following " +
        "Content Security Policy directive";

    public static bool IsBootstrapInlineScriptViolation(
        string parameterObjectJson,
        TargetContentBinding binding,
        Uri expectedDocument)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(expectedDocument);
        if (string.IsNullOrWhiteSpace(parameterObjectJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(parameterObjectJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("entry", out var entry) ||
                entry.ValueKind != JsonValueKind.Object ||
                !TryGetRequiredString(entry, "source", out var source) ||
                !string.Equals(source, "security", StringComparison.Ordinal) ||
                !TryGetRequiredString(entry, "level", out var level) ||
                !string.Equals(level, "error", StringComparison.Ordinal) ||
                !TryGetRequiredString(entry, "text", out var text) ||
                !IsInlineScriptViolation(text) ||
                !TryGetRequiredString(entry, "url", out var url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var resource))
            {
                return false;
            }

            return IsExpectedDocument(resource, binding, expectedDocument);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetRequiredString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool IsInlineScriptViolation(string text)
    {
        return text.StartsWith(
                   LegacyInlineScriptViolationPrefix,
                   StringComparison.Ordinal) ||
               text.StartsWith(
                   CurrentInlineScriptViolationPrefix,
                   StringComparison.Ordinal);
    }

    private static bool IsExpectedDocument(
        Uri resource,
        TargetContentBinding binding,
        Uri expectedDocument)
    {
        return resource.IsAbsoluteUri &&
               expectedDocument.IsAbsoluteUri &&
               string.Equals(
                   resource.Scheme,
                   binding.Origin.Scheme,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   resource.Host,
                   binding.Origin.Host,
                   StringComparison.OrdinalIgnoreCase) &&
               resource.Port == binding.Origin.Port &&
               string.Equals(
                   resource.AbsolutePath,
                   expectedDocument.AbsolutePath,
                   StringComparison.Ordinal);
    }
}

internal sealed class WebView2ResponseCspBoundary : IDisposable
{
    private readonly CoreWebView2 _core;
    private readonly TargetContentBinding _binding;
    private readonly string _contentSecurityPolicy;
    private readonly Action _failureSink;
    private readonly CoreWebView2DevToolsProtocolEventReceiver _receiver;
    private readonly Action<WebView2ResponseCspBoundary>? _inlineScriptViolationSink;
    private readonly CoreWebView2DevToolsProtocolEventReceiver? _logReceiver;
    private Uri? _inlineScriptObservationDocument;
    private int _logDomainEnabled;
    private int _inlineScriptObservationActive;
    private int _disposed;

    private WebView2ResponseCspBoundary(
        CoreWebView2 core,
        TargetContentBinding binding,
        string contentSecurityPolicy,
        Action failureSink,
        Action<WebView2ResponseCspBoundary>? inlineScriptViolationSink)
    {
        _core = core;
        _binding = binding;
        _contentSecurityPolicy = contentSecurityPolicy;
        _failureSink = failureSink;
        _receiver = core.GetDevToolsProtocolEventReceiver("Fetch.requestPaused");
        _inlineScriptViolationSink = inlineScriptViolationSink;
        _logReceiver = inlineScriptViolationSink is null
            ? null
            : core.GetDevToolsProtocolEventReceiver("Log.entryAdded");
    }

    public static async Task<WebView2ResponseCspBoundary> EnableAsync(
        CoreWebView2 core,
        TargetContentBinding binding,
        string contentSecurityPolicy,
        Action? failureSink = null,
        Action<WebView2ResponseCspBoundary>? inlineScriptViolationSink = null)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSecurityPolicy);
        var boundary = new WebView2ResponseCspBoundary(
            core,
            binding,
            contentSecurityPolicy,
            failureSink ?? (() => { }),
            inlineScriptViolationSink);
        boundary._receiver.DevToolsProtocolEventReceived +=
            boundary.OnRequestPaused;
        try
        {
            await core.CallDevToolsProtocolMethodAsync(
                "Fetch.enable",
                TargetResponseCspPolicy.CreateEnableParameters(binding))
                .ConfigureAwait(true);
            return boundary;
        }
        catch
        {
            boundary.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _receiver.DevToolsProtocolEventReceived -= OnRequestPaused;
        Volatile.Write(ref _inlineScriptObservationActive, 0);
        Volatile.Write(ref _inlineScriptObservationDocument, null);
        if (_logReceiver is not null)
        {
            _logReceiver.DevToolsProtocolEventReceived -= OnLogEntryAdded;
        }
    }

    public async Task BeginInlineScriptObservationAsync(Uri expectedDocument)
    {
        ArgumentNullException.ThrowIfNull(expectedDocument);
        if (_logReceiver is null ||
            Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Volatile.Write(ref _inlineScriptObservationActive, 0);
        Volatile.Write(ref _inlineScriptObservationDocument, null);
        _logReceiver.DevToolsProtocolEventReceived -= OnLogEntryAdded;
        try
        {
            if (Volatile.Read(ref _logDomainEnabled) == 0)
            {
                await _core.CallDevToolsProtocolMethodAsync("Log.enable", "{}")
                    .ConfigureAwait(true);
                Volatile.Write(ref _logDomainEnabled, 1);
            }

            await _core.CallDevToolsProtocolMethodAsync("Log.clear", "{}")
                .ConfigureAwait(true);
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _logReceiver.DevToolsProtocolEventReceived += OnLogEntryAdded;
            Volatile.Write(ref _inlineScriptObservationDocument, expectedDocument);
            Volatile.Write(ref _inlineScriptObservationActive, 1);
        }
        catch
        {
            // This receiver only improves diagnostics. CSP enforcement remains
            // active when a runtime does not expose the Log domain.
            _logReceiver.DevToolsProtocolEventReceived -= OnLogEntryAdded;
        }
    }

    public void EndInlineScriptObservation()
    {
        Volatile.Write(ref _inlineScriptObservationActive, 0);
        Volatile.Write(ref _inlineScriptObservationDocument, null);
        if (_logReceiver is not null)
        {
            _logReceiver.DevToolsProtocolEventReceived -= OnLogEntryAdded;
        }
    }

    private void OnRequestPaused(
        object? sender,
        CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        DevToolsFetchCommand? command;
        try
        {
            command = TargetResponseCspPolicy.CreatePausedResponseCommand(
                args.ParameterObjectAsJson,
                _binding,
                _contentSecurityPolicy);
        }
        catch
        {
            NotifyFailure();
            return;
        }
        if (command is null)
        {
            NotifyFailure();
            return;
        }

        _ = SendCommandSafeAsync(command);
    }

    private void OnLogEntryAdded(
        object? sender,
        CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Volatile.Read(ref _inlineScriptObservationActive) == 0 ||
            _inlineScriptViolationSink is null ||
            !string.IsNullOrEmpty(args.SessionId))
        {
            return;
        }

        var expectedDocument = Volatile.Read(
            ref _inlineScriptObservationDocument);
        if (expectedDocument is null)
        {
            return;
        }

        bool isViolation;
        try
        {
            isViolation = TargetContentCspViolationPolicy
                .IsBootstrapInlineScriptViolation(
                    args.ParameterObjectAsJson,
                    _binding,
                    expectedDocument);
        }
        catch
        {
            return;
        }

        if (!isViolation ||
            Interlocked.Exchange(ref _inlineScriptObservationActive, 0) == 0)
        {
            return;
        }

        try
        {
            // Do not persist or surface the browser entry: it may contain a
            // sensitive target query. Only the bounded classification escapes.
            _inlineScriptViolationSink(this);
        }
        catch
        {
            // Boundary reporting cannot escape the WebView2 event pump.
        }
    }

    private async Task SendCommandSafeAsync(DevToolsFetchCommand command)
    {
        try
        {
            await _core.CallDevToolsProtocolMethodAsync(
                command.Method,
                command.ParametersJson).ConfigureAwait(true);
        }
        catch
        {
            NotifyFailure();
            if (!string.Equals(
                    command.Method,
                    "Fetch.failRequest",
                    StringComparison.Ordinal))
            {
                try
                {
                    var failure = TargetResponseCspPolicy.CreateFailRequest(
                        command.RequestId);
                    await _core.CallDevToolsProtocolMethodAsync(
                        failure.Method,
                        failure.ParametersJson).ConfigureAwait(true);
                }
                catch
                {
                    // A paused request remains fail-closed if the browser is failing.
                }
            }
        }
    }

    private void NotifyFailure()
    {
        try
        {
            _failureSink();
        }
        catch
        {
            // Boundary reporting cannot escape the WebView2 event pump.
        }
    }
}
