namespace DshLauncher.WebView;

internal sealed class BrowserProcessExitWaiter
{
    private readonly TaskCompletionSource _exited = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public void Signal() => _exited.TrySetResult();

    public async Task WaitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        try
        {
            await _exited.Task.WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                "The WebView2 browser process did not release its user data folder in time.",
                exception);
        }
    }
}

internal sealed class BrowserProcessGeneration
{
    private readonly object _gate = new();
    private readonly BrowserProcessExitWaiter _exit = new();
    private uint? _browserProcessId;
    private uint? _observedExitProcessId;

    public void CaptureBrowserProcessId(uint browserProcessId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(browserProcessId);

        lock (_gate)
        {
            if (_browserProcessId is { } existing &&
                existing != browserProcessId)
            {
                throw new InvalidOperationException(
                    "A browser generation cannot change process identity.");
            }

            if (_observedExitProcessId is { } observed &&
                observed != browserProcessId)
            {
                throw new InvalidOperationException(
                    "The observed browser exit did not belong to this generation.");
            }

            _browserProcessId = browserProcessId;
        }
    }

    public void ObserveExit(uint browserProcessId)
    {
        lock (_gate)
        {
            if (_browserProcessId is null ||
                _browserProcessId == browserProcessId)
            {
                _observedExitProcessId ??= browserProcessId;
                _exit.Signal();
            }
        }
    }

    public async Task WaitForQuiescenceAsync(
        Func<bool> hasRunningProcesses,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hasRunningProcesses);
        if (!hasRunningProcesses())
        {
            return;
        }

        await _exit.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (hasRunningProcesses())
        {
            throw new InvalidOperationException(
                "The WebView2 browser process exit was observed before its environment became quiescent.");
        }
    }
}
