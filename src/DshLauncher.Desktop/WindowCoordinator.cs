using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DshLauncher.Core;
using DshLauncher.Desktop.Resources;
using DshLauncher.Platform.Windows;
using DshLauncher.WebView;

namespace DshLauncher.Desktop;

public sealed class WindowCoordinator : ITargetWindowCoordinator
{
    private readonly ApplicationDataLayout _layout;
    private readonly ITargetExternalNavigationConsent _externalConsent;
    private readonly ITargetExternalUriLauncher _externalLauncher;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<TargetId, TargetWindowEntry> _entries = [];

    public event EventHandler? AllTargetWindowsClosed;

    public event EventHandler<TargetAuthenticationInvalidatedEventArgs>?
        AuthenticationInvalidated;

    public bool HasOpenWindows
    {
        get
        {
            _dispatcher.VerifyAccess();
            return _entries.Count > 0;
        }
    }

    public WindowCoordinator(
        ApplicationDataLayout layout,
        ITargetExternalNavigationConsent externalConsent,
        ITargetExternalUriLauncher externalLauncher,
        Dispatcher dispatcher)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _externalConsent = externalConsent ?? throw new ArgumentNullException(nameof(externalConsent));
        _externalLauncher = externalLauncher ?? throw new ArgumentNullException(nameof(externalLauncher));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public async ValueTask<bool> TryActivateAsync(
        TargetId targetId,
        CancellationToken cancellationToken)
    {
        if (_dispatcher.CheckAccess())
        {
            return await TryActivateCoreAsync(targetId, cancellationToken)
                .ConfigureAwait(true);
        }

        return await _dispatcher.InvokeAsync(
            () => TryActivateCoreAsync(targetId, cancellationToken),
            DispatcherPriority.Normal,
            cancellationToken).Task.Unwrap().ConfigureAwait(false);
    }

    public ValueTask OpenOrActivateAsync(
        TargetView target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        return DispatchAsync(() => OpenOrActivateCoreAsync(target, cancellationToken), cancellationToken);
    }

    public ValueTask CloseAsync(TargetId targetId, CancellationToken cancellationToken) =>
        DispatchAsync(() => CloseCoreAsync(targetId, cancellationToken), cancellationToken);

    public ValueTask CloseAllAsync(CancellationToken cancellationToken) =>
        DispatchAsync(async () =>
        {
            foreach (var targetId in _entries.Keys.ToArray())
            {
                await CloseCoreAsync(targetId, cancellationToken).ConfigureAwait(true);
            }
        }, cancellationToken);

    private async Task OpenOrActivateCoreAsync(
        TargetView target,
        CancellationToken cancellationToken)
    {
        _dispatcher.VerifyAccess();
        if (_entries.TryGetValue(target.TargetId, out var existing))
        {
            if (existing.CloseTask is null)
            {
                Activate(existing.Window);
                return;
            }

            await existing.CloseTask.WaitAsync(cancellationToken)
                .ConfigureAwait(true);
            if (_entries.ContainsKey(target.TargetId))
            {
                throw new InvalidOperationException(
                    "The existing target window did not finish closing.");
            }
        }

        var window = new TargetWindow(target.EffectiveDisplayName);
        var surface = new Grid();
        window.SetHostContent(surface);
        var binding = new TargetContentBinding(
            target.TargetId.Value,
            new Uri(target.Endpoint.Origin + '/', UriKind.Absolute),
            _layout.GetUdfPath(target.TargetId.Value));
        var runtime = new WebView2TargetContentRuntime(
            surface,
            _externalConsent,
            _externalLauncher);
        var host = new TargetContentHost(binding, runtime);
        var entry = new TargetWindowEntry(window, host);
        _entries.Add(target.TargetId, entry);

        host.StateChanged += (_, args) =>
        {
            var authenticationInvalid =
                args.Failure?.Kind == TargetContentFailureKind.Authentication &&
                Interlocked.Exchange(
                    ref entry.AuthenticationInvalidationPublished,
                    1) == 0;
            _ = _dispatcher.InvokeAsync(
                () =>
                {
                    window.SetState(
                        MapState(args.State),
                        FailureDetail(args.Failure));
                    if (authenticationInvalid)
                    {
                        PublishAuthenticationInvalidated(target.TargetId);
                    }
                },
                DispatcherPriority.Background);
        };
        window.ReloadRequested += (_, _) => _ = ReloadSafeAsync(entry);
        window.Closing += (_, args) => OnWindowClosing(target.TargetId, entry, args);

        window.Show();
        Activate(window);
        try
        {
            await host.OpenAsync(cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            await CloseCoreAsync(target.TargetId, CancellationToken.None).ConfigureAwait(true);
            throw;
        }
    }

    private async Task<bool> TryActivateCoreAsync(
        TargetId targetId,
        CancellationToken cancellationToken)
    {
        _dispatcher.VerifyAccess();
        if (!_entries.TryGetValue(targetId, out var existing))
        {
            return false;
        }

        if (existing.CloseTask is { } closeTask)
        {
            await closeTask.WaitAsync(cancellationToken).ConfigureAwait(true);
            return false;
        }

        Activate(existing.Window);
        return true;
    }

    private async Task CloseCoreAsync(TargetId targetId, CancellationToken cancellationToken)
    {
        _dispatcher.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(targetId, out var entry))
        {
            return;
        }

        var closeTask = entry.CloseTask;
        if (closeTask is null)
        {
            entry.Closing = true;
            closeTask = CloseEntryCoreAsync(targetId, entry);
            entry.CloseTask = closeTask;
        }

        await closeTask.WaitAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task CloseEntryCoreAsync(TargetId targetId, TargetWindowEntry entry)
    {
        _dispatcher.VerifyAccess();
        await Task.Yield();
        _dispatcher.VerifyAccess();
        try
        {
            await entry.Host.CloseAsync(CancellationToken.None).ConfigureAwait(true);
            await entry.Host.DisposeAsync().ConfigureAwait(true);
            if (entry.Window.IsVisible)
            {
                entry.Window.Close();
            }

            if (_entries.TryGetValue(targetId, out var current) &&
                ReferenceEquals(current, entry))
            {
                _entries.Remove(targetId);
            }

            if (_entries.Count == 0)
            {
                AllTargetWindowsClosed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch
        {
            entry.Closing = false;
            entry.CloseTask = null;
            throw;
        }
    }

    private static async Task ReloadSafeAsync(TargetWindowEntry entry)
    {
        try
        {
            await entry.Host.ReloadAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            entry.Window.SetState(TargetWindowState.Failed, Strings.UnexpectedError);
        }
    }

    private void OnWindowClosing(
        TargetId targetId,
        TargetWindowEntry entry,
        System.ComponentModel.CancelEventArgs args)
    {
        if (entry.Closing)
        {
            return;
        }

        args.Cancel = true;
        _ = CloseFromUserSafeAsync(targetId, entry);
    }

    private async Task CloseFromUserSafeAsync(TargetId targetId, TargetWindowEntry entry)
    {
        try
        {
            await CloseCoreAsync(targetId, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception)
        {
            entry.Window.SetState(TargetWindowState.Failed, Strings.UnexpectedError);
        }
    }

    private async ValueTask DispatchAsync(
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        if (_dispatcher.CheckAccess())
        {
            await action().ConfigureAwait(true);
            return;
        }

        await _dispatcher.InvokeAsync(
            action,
            DispatcherPriority.Normal,
            cancellationToken).Task.Unwrap().ConfigureAwait(false);
    }

    private static void Activate(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
    }

    private static TargetWindowState MapState(TargetContentState state) => state switch
    {
        TargetContentState.Initializing => TargetWindowState.Initializing,
        TargetContentState.Loading => TargetWindowState.Loading,
        TargetContentState.Ready => TargetWindowState.Ready,
        TargetContentState.Recovering => TargetWindowState.Recovering,
        TargetContentState.Blocked => TargetWindowState.Blocked,
        TargetContentState.Failed => TargetWindowState.Failed,
        _ => TargetWindowState.Initializing,
    };

    private static string? FailureDetail(TargetContentFailure? failure) => failure?.Kind switch
    {
        TargetContentFailureKind.Authentication => Strings.TargetUnavailable,
        TargetContentFailureKind.Download => Strings.TargetWindowDownloadBlocked,
        TargetContentFailureKind.SecurityPolicy => Strings.BlockedContent,
        TargetContentFailureKind.Navigation => Strings.TargetUnavailable,
        TargetContentFailureKind.Cancelled => null,
        null => null,
        _ => Strings.UnexpectedError,
    };

    private void PublishAuthenticationInvalidated(TargetId targetId)
    {
        var handler = AuthenticationInvalidated;
        if (handler is null)
        {
            return;
        }

        var args = new TargetAuthenticationInvalidatedEventArgs(targetId);
        foreach (EventHandler<TargetAuthenticationInvalidatedEventArgs> subscriber in
                 handler.GetInvocationList())
        {
            try
            {
                subscriber(this, args);
            }
            catch
            {
                // A subscriber cannot corrupt the target window lifecycle.
            }
        }
    }

    private sealed class TargetWindowEntry(TargetWindow window, ITargetContentHost host)
    {
        public TargetWindow Window { get; } = window;
        public ITargetContentHost Host { get; } = host;
        public bool Closing { get; set; }
        public Task? CloseTask { get; set; }
        public int AuthenticationInvalidationPublished;
    }
}

public sealed class WpfTargetExternalNavigationConsent(
    Func<Window?> ownerProvider,
    Dispatcher dispatcher) : ITargetExternalNavigationConsent
{
    private readonly Func<Window?> _ownerProvider = ownerProvider ??
        throw new ArgumentNullException(nameof(ownerProvider));
    private readonly Dispatcher _dispatcher = dispatcher ??
        throw new ArgumentNullException(nameof(dispatcher));

    public async ValueTask<bool> ConfirmAsync(
        TargetExternalNavigationPrompt prompt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        cancellationToken.ThrowIfCancellationRequested();
        return await _dispatcher.InvokeAsync(
            () => MessageBox.Show(
                _ownerProvider(),
                $"{Strings.ExternalLinkPrompt}\n\n{prompt.DisplayTarget}",
                Strings.ExternalLinkTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes,
            DispatcherPriority.Normal,
            cancellationToken);
    }
}
