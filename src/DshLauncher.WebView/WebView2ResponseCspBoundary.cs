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
        var frameSources = grants
            .Where(static grant => grant.Kind == CapabilityKind.Frame)
            .Select(static grant => grant.Origin)
            .Where(static origin => origin is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        var builder = new StringBuilder();
        AppendDirective(builder, "default-src", ["'none'"]);
        AppendDirective(builder, "connect-src", WithSelf(connectSources));
        AppendDirective(builder, "script-src", ["'self'"]);
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

internal sealed class WebView2ResponseCspBoundary : IDisposable
{
    private readonly CoreWebView2 _core;
    private readonly TargetContentBinding _binding;
    private readonly string _contentSecurityPolicy;
    private readonly Action _failureSink;
    private readonly CoreWebView2DevToolsProtocolEventReceiver _receiver;
    private int _disposed;

    private WebView2ResponseCspBoundary(
        CoreWebView2 core,
        TargetContentBinding binding,
        string contentSecurityPolicy,
        Action failureSink)
    {
        _core = core;
        _binding = binding;
        _contentSecurityPolicy = contentSecurityPolicy;
        _failureSink = failureSink;
        _receiver = core.GetDevToolsProtocolEventReceiver("Fetch.requestPaused");
    }

    public static async Task<WebView2ResponseCspBoundary> EnableAsync(
        CoreWebView2 core,
        TargetContentBinding binding,
        string contentSecurityPolicy,
        Action? failureSink = null)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSecurityPolicy);
        var boundary = new WebView2ResponseCspBoundary(
            core,
            binding,
            contentSecurityPolicy,
            failureSink ?? (() => { }));
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
