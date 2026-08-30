using DshLauncher.Desktop.ViewModels;
using Xunit;

namespace DshLauncher.Acceptance.Tests.Desktop;

public sealed class TargetCenterViewModelTests
{
    private static readonly Guid TargetId =
        Guid.Parse("8e59ec45-dafe-4a51-b270-63c4f07afee0");

    [Fact]
    [Trait("triggerTags", "VFY-05,RS-05")]
    public async Task PairingFailureRemainsVisibleAfterTheTargetListRefreshes()
    {
        const string pairingError = "配对握手未完整通过，已清理未提交会话。";
        var controller = new PairFailureController(pairingError);
        using var viewModel = new TargetCenterViewModel(controller);
        await viewModel.InitializeAsync();
        var commandCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCanExecuteChanged(object? sender, EventArgs args)
        {
            if (viewModel.PairCommand.CanExecute(null))
            {
                commandCompleted.TrySetResult();
            }
        }

        viewModel.PairCommand.CanExecuteChanged += OnCanExecuteChanged;
        try
        {
            viewModel.PairCommand.Execute(null);
            await commandCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            viewModel.PairCommand.CanExecuteChanged -= OnCanExecuteChanged;
        }

        Assert.Equal(pairingError, viewModel.StatusText);
    }

    private sealed class PairFailureController(string pairingError) : ILauncherController
    {
        private readonly LauncherSnapshot _snapshot = new(
            1,
            [
                new LauncherTarget(
                    TargetId,
                    "Test Harness",
                    "Test Harness",
                    "192.168.3.190:3080",
                    true,
                    false),
            ]);

        public ValueTask<LauncherSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_snapshot);
        }

        public ValueTask PairAsync(Guid targetId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(TargetId, targetId);
            return ValueTask.FromException(
                new LauncherPresentationException(pairingError));
        }

        public ValueTask AddAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask OpenAsync(Guid targetId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask SetDefaultAsync(
            Guid targetId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask RenameAsync(Guid targetId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask ForgetAsync(Guid targetId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask ExportDiagnosticsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask ViewUpdatesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
