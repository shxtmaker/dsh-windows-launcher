using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DshLauncher.Core.Hub;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DshLauncher.WebUi;

/// <summary>Options for the local management web server.</summary>
public sealed record HubWebServerOptions
{
    /// <summary>Loopback port for the dashboard; 0 picks a free port.</summary>
    public int Port { get; init; } = DefaultPort;

    public const int DefaultPort = 4780;
}

/// <summary>
/// The independent web page host: a loopback Kestrel serving the management
/// dashboard plus a small JSON API over the hub. Management stays on
/// 127.0.0.1 by design — pairing credentials must not ride a LAN-facing UI.
/// </summary>
public sealed class HubWebServer : IAsyncDisposable
{
    public HubWebServer(PairingHub hub, HubWebServerOptions? options = null)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _options = options ?? new HubWebServerOptions();
    }

    /// <summary>The dashboard URL actually bound (resolves a dynamic port).</summary>
    public string DashboardUrl => DashboardUrlValue is { } url ? url : $"http://127.0.0.1:{_options.Port}/";

    public bool IsStarted => _app is not null;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_app is not null)
        {
            return;
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, _options.Port));
        builder.WebHost.UseContentRoot(AppContext.BaseDirectory);
        builder.Logging.ClearProviders();
        builder.Services.Configure<JsonOptions>(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        var app = builder.Build();
        MapRoutes(app);
        _app = app;
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        DashboardUrlValue = ResolveBoundUrl(app);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_app is not null)
        {
            try
            {
                await _app.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                await _app.DisposeAsync().ConfigureAwait(false);
            }

            _app = null;
        }
    }

    private readonly PairingHub _hub;
    private readonly HubWebServerOptions _options;
    private WebApplication? _app;
    private string? DashboardUrlValue;
    private bool _disposed;

    private static string ResolveBoundUrl(WebApplication app)
    {
        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()?
            .Addresses
            .FirstOrDefault(address => address.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        return addresses ?? $"http://127.0.0.1:{new HubWebServerOptions().Port}/";
    }

    private void MapRoutes(WebApplication app)
    {
        var dashboard = LoadDashboard();

        app.MapGet("/", () => Results.Text(dashboard, "text/html; charset=utf-8"));

        app.MapGet("/api/hub", async (CancellationToken cancellationToken) =>
            Results.Json(await _hub.GetSnapshotAsync(cancellationToken).ConfigureAwait(false)));

        app.MapPost("/api/targets", async (AddTargetRequest request, CancellationToken cancellationToken) =>
        {
            var result = string.IsNullOrWhiteSpace(request.PairingLink)
                ? await _hub.AddEndpointAsync(request.BaseUrl ?? string.Empty, request.DisplayName, cancellationToken)
                    .ConfigureAwait(false)
                : await _hub.AddFromPairingLinkAsync(request.PairingLink, request.DisplayName, cancellationToken)
                    .ConfigureAwait(false);
            return ToHttpResult(result, successStatus: StatusCodes.Status201Created);
        });

        app.MapPost("/api/targets/{id:guid}/pairing",
            async (Guid id, PairingRequest request, CancellationToken cancellationToken) =>
                ToHttpResult(await _hub.AttachPairingAsync(id, request.PairingLink, cancellationToken)
                    .ConfigureAwait(false)));

        app.MapPost("/api/targets/{id:guid}/rename",
            async (Guid id, RenameRequest request, CancellationToken cancellationToken) =>
                ToHttpResult(await _hub.RenameAsync(id, request.DisplayName, cancellationToken)
                    .ConfigureAwait(false)));

        app.MapPost("/api/targets/{id:guid}/keep-alive",
            async (Guid id, KeepAliveRequest request, CancellationToken cancellationToken) =>
                ToHttpResult(await _hub.SetKeepAliveAsync(id, request.Enabled, cancellationToken)
                    .ConfigureAwait(false)));

        app.MapPost("/api/targets/{id:guid}/heartbeat",
            async (Guid id, CancellationToken cancellationToken) =>
            {
                var result = await _hub.HeartbeatNowAsync(id, cancellationToken).ConfigureAwait(false);
                return ToHttpResult(result);
            });

        app.MapGet("/api/targets/{id:guid}/remote-url",
            async (Guid id, CancellationToken cancellationToken) =>
                ToHttpResult(await _hub.GetRemoteUiUrlAsync(id, cancellationToken).ConfigureAwait(false)));

        app.MapDelete("/api/targets/{id:guid}",
            async (Guid id, CancellationToken cancellationToken) =>
                ToHttpResult(await _hub.RemoveAsync(id, cancellationToken).ConfigureAwait(false)));

        app.MapGet("/api/events", async (HttpContext context) => await StreamEventsAsync(context).ConfigureAwait(false));
    }

    private static readonly JsonSerializerOptions SseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private async Task StreamEventsAsync(HttpContext context)
    {
        context.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        var channel = System.Threading.Channels.Channel.CreateBounded<HubSnapshot>(
            new System.Threading.Channels.BoundedChannelOptions(8)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });
        void OnChanged(HubSnapshot snapshot) => channel.Writer.TryWrite(snapshot);
        _hub.Changed += OnChanged;
        try
        {
            channel.Writer.TryWrite(await _hub.GetSnapshotAsync(context.RequestAborted).ConfigureAwait(false));
            await foreach (var snapshot in channel.Reader.ReadAllAsync(context.RequestAborted).ConfigureAwait(false))
            {
                var frame = JsonSerializer.Serialize(snapshot, SseJson);
                await context.Response.WriteAsync($"data: {frame}\n\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnect: the normal stream end.
        }
        finally
        {
            _hub.Changed -= OnChanged;
        }
    }

    private static readonly Lazy<string> DashboardHtml = new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"{assembly.GetName().Name}.Dashboard.index.html";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The dashboard resource {resourceName} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private static string LoadDashboard() => DashboardHtml.Value;

    private static IResult ToHttpResult(HubOperationResult result, int successStatus = StatusCodes.Status200OK)
    {
        if (result.Error is { } error)
        {
            return Results.Json(
                new { error = new { code = error.Code.ToString(), retryable = error.Retryable } },
                statusCode: MapErrorStatus(error.Code));
        }

        if (result.Url is { } url)
        {
            return Results.Json(new { url });
        }

        return Results.Json(result.Target, statusCode: successStatus);
    }

    private static int MapErrorStatus(HubErrorCode code) => code switch
    {
        HubErrorCode.InvalidPairingLink or HubErrorCode.InvalidBaseUrl => StatusCodes.Status400BadRequest,
        HubErrorCode.TargetNotFound => StatusCodes.Status404NotFound,
        HubErrorCode.TargetLimitReached or HubErrorCode.TargetNotPaired => StatusCodes.Status409Conflict,
        HubErrorCode.PairingForbidden => StatusCodes.Status403Forbidden,
        HubErrorCode.PairingInvalidToken => StatusCodes.Status400BadRequest,
        HubErrorCode.PairingTokenUsed => StatusCodes.Status409Conflict,
        HubErrorCode.PairingRateLimited => StatusCodes.Status429TooManyRequests,
        HubErrorCode.PairingUnreachable => StatusCodes.Status504GatewayTimeout,
        _ => StatusCodes.Status502BadGateway,
    };

    private sealed record AddTargetRequest(string? PairingLink, string? BaseUrl, string? DisplayName);

    private sealed record PairingRequest(string PairingLink);

    private sealed record RenameRequest(string? DisplayName);

    private sealed record KeepAliveRequest(bool Enabled);
}
