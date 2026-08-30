using System.Collections.ObjectModel;
using System.Windows;
using DshLauncher.Desktop.Resources;

namespace DshLauncher.Desktop.ViewModels;

public sealed class TargetCenterViewModel : ObservableObject, IDisposable
{
    private readonly ILauncherController _controller;
    private readonly CancellationTokenSource _lifetime = new();
    private TargetListItemViewModel? _selectedTarget;
    private string _statusText = Strings.ReadyStatus;

    public TargetCenterViewModel(ILauncherController controller)
    {
        _controller = controller;
        AddCommand = new AsyncCommand(AddAsync);
        OpenCommand = new AsyncCommand(OpenAsync, HasSelection);
        PairCommand = new AsyncCommand(PairAsync, HasUnpairedSelection);
        SetDefaultCommand = new AsyncCommand(SetDefaultAsync, HasNonDefaultSelection);
        RenameCommand = new AsyncCommand(RenameAsync, HasSelection);
        ForgetCommand = new AsyncCommand(ForgetAsync, HasSelection);
        RefreshCommand = new AsyncCommand(RefreshAsync);
        ExportDiagnosticsCommand = new AsyncCommand(ExportDiagnosticsAsync);
        ViewUpdatesCommand = new AsyncCommand(ViewUpdatesAsync);
    }

    public ObservableCollection<TargetListItemViewModel> Targets { get; } = [];

    public TargetListItemViewModel? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (SetProperty(ref _selectedTarget, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public Visibility EmptyStateVisibility => Targets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public AsyncCommand AddCommand { get; }
    public AsyncCommand OpenCommand { get; }
    public AsyncCommand PairCommand { get; }
    public AsyncCommand SetDefaultCommand { get; }
    public AsyncCommand RenameCommand { get; }
    public AsyncCommand ForgetCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand ExportDiagnosticsCommand { get; }
    public AsyncCommand ViewUpdatesCommand { get; }

    public Task InitializeAsync() => RefreshAsync();

    public void Dispose() => _lifetime.Cancel();

    private bool HasSelection() => SelectedTarget is not null;
    private bool HasUnpairedSelection() => SelectedTarget is { IsPaired: false };
    private bool HasNonDefaultSelection() => SelectedTarget is { IsDefault: false };

    private async Task AddAsync()
    {
        await RunAndRefreshAsync(ct => _controller.AddAsync(ct)).ConfigureAwait(true);
    }

    private async Task OpenAsync()
    {
        if (SelectedTarget is { } target)
        {
            await RunAsync(ct => _controller.OpenAsync(target.TargetId, ct)).ConfigureAwait(true);
        }
    }

    private async Task PairAsync()
    {
        if (SelectedTarget is { } target)
        {
            await RunAndRefreshAsync(ct => _controller.PairAsync(target.TargetId, ct)).ConfigureAwait(true);
        }
    }

    private async Task SetDefaultAsync()
    {
        if (SelectedTarget is { } target)
        {
            await RunAndRefreshAsync(ct => _controller.SetDefaultAsync(target.TargetId, ct)).ConfigureAwait(true);
        }
    }

    private async Task RenameAsync()
    {
        if (SelectedTarget is { } target)
        {
            await RunAndRefreshAsync(ct => _controller.RenameAsync(target.TargetId, ct)).ConfigureAwait(true);
        }
    }

    private async Task ForgetAsync()
    {
        if (SelectedTarget is { } target)
        {
            await RunAndRefreshAsync(ct => _controller.ForgetAsync(target.TargetId, ct)).ConfigureAwait(true);
        }
    }

    private async Task ExportDiagnosticsAsync() =>
        await RunAsync(ct => _controller.ExportDiagnosticsAsync(ct)).ConfigureAwait(true);

    private async Task ViewUpdatesAsync() =>
        await RunAsync(ct => _controller.ViewUpdatesAsync(ct)).ConfigureAwait(true);

    public async Task RefreshAsync()
    {
        await RunAsync(async ct =>
        {
            var selectedId = SelectedTarget?.TargetId;
            var snapshot = await _controller.ReadAsync(ct).ConfigureAwait(true);
            Targets.Clear();
            foreach (var target in snapshot.Targets)
            {
                Targets.Add(new TargetListItemViewModel(target));
            }

            SelectedTarget = Targets.FirstOrDefault(item => item.TargetId == selectedId)
                ?? Targets.FirstOrDefault();
            OnPropertyChanged(nameof(EmptyStateVisibility));
        }).ConfigureAwait(true);
    }

    private async Task RunAndRefreshAsync(Func<CancellationToken, ValueTask> operation)
    {
        await RunAsync(operation).ConfigureAwait(true);
        var operationStatus = StatusText;
        await RefreshAsync().ConfigureAwait(true);
        if (operationStatus != Strings.ReadyStatus)
        {
            StatusText = operationStatus;
        }
    }

    private async Task RunAsync(Func<CancellationToken, ValueTask> operation)
    {
        StatusText = Strings.WorkingStatus;
        try
        {
            await operation(_lifetime.Token).ConfigureAwait(true);
            StatusText = Strings.ReadyStatus;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            StatusText = Strings.ReadyStatus;
        }
        catch (LauncherPresentationException exception)
        {
            StatusText = exception.UserMessage;
        }
        catch (Exception)
        {
            StatusText = Strings.UnexpectedError;
        }
    }

    private void RaiseCommandStates()
    {
        OpenCommand.RaiseCanExecuteChanged();
        PairCommand.RaiseCanExecuteChanged();
        SetDefaultCommand.RaiseCanExecuteChanged();
        RenameCommand.RaiseCanExecuteChanged();
        ForgetCommand.RaiseCanExecuteChanged();
    }
}

public sealed class LauncherPresentationException : Exception
{
    public LauncherPresentationException(string userMessage)
        : base(userMessage)
    {
        UserMessage = userMessage;
    }

    public string UserMessage { get; }
}
