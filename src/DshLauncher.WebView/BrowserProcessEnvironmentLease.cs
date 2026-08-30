using System.Collections.Concurrent;
using DshLauncher.Core;
using Microsoft.Web.WebView2.Core;

namespace DshLauncher.WebView;

internal sealed class BrowserProcessEnvironmentLease
{
    private static readonly ConcurrentDictionary<
        TargetId,
        BrowserProcessEnvironmentLease> Active = new();

    private readonly TargetId _targetId;
    private readonly Func<bool> _hasRunningProcesses;
    private readonly Action _detach;
    private readonly BrowserProcessGeneration _generation = new();
    private readonly TimeSpan _releaseTimeout;
    private readonly object _releaseGate = new();
    private Task? _releaseTask;
    private int _released;

    private BrowserProcessEnvironmentLease(
        TargetId targetId,
        Func<bool> hasRunningProcesses,
        Action detach,
        TimeSpan releaseTimeout)
    {
        _targetId = targetId;
        _hasRunningProcesses = hasRunningProcesses;
        _detach = detach;
        _releaseTimeout = releaseTimeout;
    }

    public static BrowserProcessEnvironmentLease Register(
        TargetId targetId,
        CoreWebView2Environment environment,
        TimeSpan releaseTimeout)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (targetId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "Target identity must not be empty.",
                nameof(targetId));
        }

        if (releaseTimeout <= TimeSpan.Zero ||
            releaseTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(releaseTimeout));
        }

        BrowserProcessEnvironmentLease? lease = null;
        lease = new BrowserProcessEnvironmentLease(
            targetId,
            () => environment.GetProcessInfos().Count > 0,
            () => environment.BrowserProcessExited -=
                lease!.OnBrowserProcessExited,
            releaseTimeout);
        return RegisterCore(
            lease,
            () => environment.BrowserProcessExited +=
                lease!.OnBrowserProcessExited);
    }

    internal static BrowserProcessEnvironmentLease RegisterForTesting(
        TargetId targetId,
        Func<bool> hasRunningProcesses,
        TimeSpan releaseTimeout)
    {
        ArgumentNullException.ThrowIfNull(hasRunningProcesses);
        return RegisterCore(
            new BrowserProcessEnvironmentLease(
                targetId,
                hasRunningProcesses,
                () => { },
                releaseTimeout),
            () => { });
    }

    private static BrowserProcessEnvironmentLease RegisterCore(
        BrowserProcessEnvironmentLease lease,
        Action attach)
    {
        var targetId = lease._targetId;
        if (!Active.TryAdd(targetId, lease))
        {
            throw new InvalidOperationException(
                "The target already has an active WebView2 browser generation.");
        }

        try
        {
            attach();
            return lease;
        }
        catch
        {
            Active.TryRemove(targetId, out _);
            throw;
        }
    }

    public static async ValueTask EnsureReleasedAsync(
        TargetId targetId,
        CancellationToken cancellationToken)
    {
        if (Active.TryGetValue(targetId, out var lease))
        {
            await lease.WaitForReleaseAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public void CaptureBrowserProcessId(uint browserProcessId) =>
        _generation.CaptureBrowserProcessId(browserProcessId);

    internal void ObserveExitForTesting(uint browserProcessId) =>
        _generation.ObserveExit(browserProcessId);

    public async Task WaitForReleaseAsync(
        CancellationToken cancellationToken)
    {
        Task releaseTask;
        lock (_releaseGate)
        {
            if (Volatile.Read(ref _released) != 0)
            {
                return;
            }

            releaseTask = _releaseTask ??= ReleaseCoreAsync();
        }

        await releaseTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReleaseCoreAsync()
    {
        await Task.Yield();
        var released = false;
        try
        {
            await _generation.WaitForQuiescenceAsync(
                _hasRunningProcesses,
                _releaseTimeout,
                CancellationToken.None).ConfigureAwait(false);
            try
            {
                _detach();
            }
            catch
            {
                // Process quiescence, not event detachment, guards the UDF.
            }

            Volatile.Write(ref _released, 1);
            released = true;
            if (Active.TryGetValue(_targetId, out var current) &&
                ReferenceEquals(current, this))
            {
                Active.TryRemove(_targetId, out _);
            }
        }
        finally
        {
            if (!released)
            {
                lock (_releaseGate)
                {
                    _releaseTask = null;
                }
            }
        }
    }

    private void OnBrowserProcessExited(
        object? sender,
        CoreWebView2BrowserProcessExitedEventArgs args) =>
        _generation.ObserveExit(args.BrowserProcessId);
}
