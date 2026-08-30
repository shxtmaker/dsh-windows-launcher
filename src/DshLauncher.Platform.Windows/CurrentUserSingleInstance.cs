using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace DshLauncher.Platform.Windows;

public static class SingleInstanceContract
{
    public const string MaintenanceExitArgument =
        "--request-maintenance-exit";
    public const string TimeoutSecondsArgument = "--timeout-seconds";

    private static readonly TimeSpan DefaultMaintenanceExitTimeout =
        TimeSpan.FromSeconds(30);

    public static bool IsMaintenanceExitRequest(
        IReadOnlyList<string> arguments) =>
        TryGetMaintenanceExitTimeout(arguments, out _);

    public static bool TryGetMaintenanceExitTimeout(
        IReadOnlyList<string> arguments,
        out TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        timeout = DefaultMaintenanceExitTimeout;
        if (arguments.Count == 1)
        {
            return string.Equals(
                arguments[0],
                MaintenanceExitArgument,
                StringComparison.Ordinal);
        }

        if (arguments.Count != 3 ||
            !string.Equals(arguments[0], MaintenanceExitArgument, StringComparison.Ordinal) ||
            !string.Equals(arguments[1], TimeoutSecondsArgument, StringComparison.Ordinal) ||
            !int.TryParse(
                arguments[2],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var seconds) ||
            seconds is < 1 or > 120)
        {
            return false;
        }

        timeout = TimeSpan.FromSeconds(seconds);
        return true;
    }
}

public sealed record CurrentUserInstanceIdentity
{
    private CurrentUserInstanceIdentity(string pipeName)
    {
        PipeName = pipeName;
    }

    public string PipeName { get; }

    public static CurrentUserInstanceIdentity ForCurrentUser(string baseName)
    {
        using var identity = WindowsIdentity.GetCurrent(
            TokenAccessLevels.Query);
        var sid = identity.User?.Value ?? throw new InvalidOperationException(
            "The current Windows user has no security identifier.");
        return FromSid(sid, baseName);
    }

    public static CurrentUserInstanceIdentity FromSid(string sid, string baseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        ValidateBaseName(baseName);
        var canonicalSid = new SecurityIdentifier(sid).Value;
        var digest = SHA256.HashData(
            Encoding.UTF8.GetBytes(canonicalSid));
        var userScope = Convert.ToHexString(digest.AsSpan(0, 16))
            .ToLowerInvariant();
        return new CurrentUserInstanceIdentity(
            $"{baseName}.{userScope}");
    }

    private static void ValidateBaseName(string baseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        if (baseName.Length > 128 ||
            baseName.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '-' or '_')))
        {
            throw new ArgumentException(
                "Single-instance base name is not canonical.",
                nameof(baseName));
        }
    }
}

public enum SingleInstanceRequestKind
{
    Activate,
    OpenTarget,
    MaintenanceExit
}

public sealed record SingleInstanceRequest
{
    private SingleInstanceRequest(
        SingleInstanceRequestKind kind,
        Guid? targetId)
    {
        Kind = kind;
        TargetId = targetId;
    }

    public SingleInstanceRequestKind Kind { get; }

    public Guid? TargetId { get; }

    public static SingleInstanceRequest Create(
        SingleInstanceRequestKind kind,
        Guid? targetId = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (kind == SingleInstanceRequestKind.OpenTarget)
        {
            if (targetId is null || targetId == Guid.Empty)
            {
                throw new ArgumentException(
                    "An open-target request requires a non-empty target ID.",
                    nameof(targetId));
            }
        }
        else if (targetId is not null)
        {
            throw new ArgumentException(
                "Only an open-target request may contain a target ID.",
                nameof(targetId));
        }

        return new SingleInstanceRequest(kind, targetId);
    }
}

public static class SingleInstanceRequestCodec
{
    public const int MaximumEncodedCharacters = 64;

    public static string Encode(SingleInstanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Kind switch
        {
            SingleInstanceRequestKind.Activate => "activate",
            SingleInstanceRequestKind.OpenTarget =>
                $"open-target:{request.TargetId!.Value:N}",
            SingleInstanceRequestKind.MaintenanceExit => "maintenance-exit",
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
    }

    public static SingleInstanceRequest Decode(string encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (encoded.Length is 0 or > MaximumEncodedCharacters)
        {
            throw new FormatException("The single-instance request is invalid.");
        }

        if (string.Equals(encoded, "activate", StringComparison.Ordinal))
        {
            return SingleInstanceRequest.Create(
                SingleInstanceRequestKind.Activate);
        }

        if (string.Equals(
            encoded,
            "maintenance-exit",
            StringComparison.Ordinal))
        {
            return SingleInstanceRequest.Create(
                SingleInstanceRequestKind.MaintenanceExit);
        }

        const string prefix = "open-target:";
        if (encoded.StartsWith(prefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(encoded.AsSpan(prefix.Length), "N", out var id) &&
            id != Guid.Empty)
        {
            return SingleInstanceRequest.Create(
                SingleInstanceRequestKind.OpenTarget,
                id);
        }

        throw new FormatException("The single-instance request is invalid.");
    }
}

public enum SingleInstanceResponse : byte
{
    Accepted = 1,
    Rejected = 2
}

public sealed class CurrentUserSingleInstance : IAsyncDisposable
{
    private const int PipeBufferBytes = 256;

    private readonly NamedPipeServerStream _server;
    private readonly Func<
        SingleInstanceRequest,
        CancellationToken,
        ValueTask<SingleInstanceResponse>> _handler;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _listener;
    private int _disposed;

    private CurrentUserSingleInstance(
        NamedPipeServerStream server,
        Func<
            SingleInstanceRequest,
            CancellationToken,
            ValueTask<SingleInstanceResponse>> handler)
    {
        _server = server;
        _handler = handler;
        _listener = ListenAsync();
    }

    public static CurrentUserSingleInstance? TryStart(
        CurrentUserInstanceIdentity identity,
        Func<
            SingleInstanceRequest,
            CancellationToken,
            ValueTask<SingleInstanceResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(handler);

        try
        {
            var server = new NamedPipeServerStream(
                identity.PipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                PipeBufferBytes,
                PipeBufferBytes);
            return new CurrentUserSingleInstance(server, handler);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static async ValueTask<SingleInstanceResponse> ForwardAsync(
        CurrentUserInstanceIdentity identity,
        SingleInstanceRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            timeout,
            TimeSpan.Zero);

        var encoded = Encoding.ASCII.GetBytes(
            SingleInstanceRequestCodec.Encode(request));
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var effectiveToken = timeoutSource.Token;

        await using var client = new NamedPipeClientStream(
            ".",
            identity.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(effectiveToken).ConfigureAwait(false);

        var header = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(
            header,
            checked((ushort)encoded.Length));
        await client.WriteAsync(header, effectiveToken).ConfigureAwait(false);
        await client.WriteAsync(encoded, effectiveToken).ConfigureAwait(false);
        await client.FlushAsync(effectiveToken).ConfigureAwait(false);

        var response = new byte[1];
        await client.ReadExactlyAsync(response, effectiveToken)
            .ConfigureAwait(false);
        return response[0] switch
        {
            (byte)SingleInstanceResponse.Accepted =>
                SingleInstanceResponse.Accepted,
            (byte)SingleInstanceResponse.Rejected =>
                SingleInstanceResponse.Rejected,
            _ => throw new IOException(
                "The primary instance returned an invalid response."),
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _server.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _listener.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _shutdown.Dispose();
    }

    private async Task ListenAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _server.WaitForConnectionAsync(_shutdown.Token)
                    .ConfigureAwait(false);
                var response = await ReadAndDispatchAsync(_shutdown.Token)
                    .ConfigureAwait(false);
                await _server.WriteAsync(
                    new byte[] { (byte)response },
                    _shutdown.Token).ConfigureAwait(false);
                await _server.FlushAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
                when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception) when (_server.IsConnected)
            {
                await TryWriteRejectedAsync().ConfigureAwait(false);
            }
            finally
            {
                if (_server.IsConnected)
                {
                    _server.Disconnect();
                }
            }
        }
    }

    private async ValueTask<SingleInstanceResponse> ReadAndDispatchAsync(
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(ushort)];
        await _server.ReadExactlyAsync(header, cancellationToken)
            .ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header);
        if (length is 0 or > SingleInstanceRequestCodec.MaximumEncodedCharacters)
        {
            return SingleInstanceResponse.Rejected;
        }

        var payload = new byte[length];
        await _server.ReadExactlyAsync(payload, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var request = SingleInstanceRequestCodec.Decode(
                Encoding.ASCII.GetString(payload));
            return await _handler(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FormatException)
        {
            return SingleInstanceResponse.Rejected;
        }
    }

    private async ValueTask TryWriteRejectedAsync()
    {
        try
        {
            await _server.WriteAsync(
                new byte[] { (byte)SingleInstanceResponse.Rejected },
                _shutdown.Token).ConfigureAwait(false);
            await _server.FlushAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
    }
}
