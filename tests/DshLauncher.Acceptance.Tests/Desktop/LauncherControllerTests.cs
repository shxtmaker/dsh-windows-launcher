using DshLauncher.Core;
using DshLauncher.Desktop;
using DshLauncher.Desktop.Dialogs;
using DshLauncher.Desktop.ViewModels;
using Xunit;

namespace DshLauncher.Acceptance.Tests.Desktop;

public sealed class LauncherControllerTests : IAsyncLifetime
{
    private static readonly string[] EmptyCatalogStartupEvents =
        ["manager.snapshot", "dialogs.add"];

    private static readonly string[] AddWorkflowEvents =
    [
        "dialogs.add",
        "manager.InspectCandidate",
        "dialogs.trust",
        "manager.ConfirmCandidate",
        "manager.snapshot",
        "dialogs.pair",
        "manager.Pair",
        "windows.open-or-activate",
    ];

    private static readonly string[] DelegationEvents =
        ["manager.snapshot", "diagnostics.export", "updates.open"];

    private static readonly TargetId FirstId = new(
        Guid.Parse("a00e06b3-5ed2-4ec8-b6e5-8bb93fa63210"));

    private static readonly TargetId SecondId = new(
        Guid.Parse("4c85b879-3a1e-455f-848c-42ccfffa1e02"));

    private TargetEndpoint? _firstEndpoint;
    private TargetEndpoint? _secondEndpoint;

    private TargetEndpoint FirstEndpoint => _firstEndpoint ??
        throw new InvalidOperationException("The endpoint fixture has not been initialized.");

    private TargetEndpoint SecondEndpoint => _secondEndpoint ??
        throw new InvalidOperationException("The endpoint fixture has not been initialized.");

    public async ValueTask InitializeAsync()
    {
        _firstEndpoint = await AcceptanceEndpointFactory.CreateAsync(
            "192.168.80.20",
            "3080",
            TestContext.Current.CancellationToken);
        _secondEndpoint = await AcceptanceEndpointFactory.CreateAsync(
            "192.168.80.21",
            "3180",
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    [Trait("triggerTags", "VFY-07,RS-04")]
    public async Task EmptyCatalogStartupEntersAddFlowWithoutOpeningAWindow()
    {
        var events = new List<string>();
        var manager = new ScriptedTargetManager(ReadySnapshot(revision: 1), events);
        var dialogs = new FakeLauncherDialogs(events) { AddResult = null };
        var windows = new FakeWindowCoordinator(events);
        var controller = CreateController(manager, dialogs, windows, events);

        await controller.ApplyStartupPolicyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EmptyCatalogStartupEvents, events);
        Assert.Empty(manager.Commands);
        Assert.Empty(windows.CreatedTargets);
    }

    [Fact]
    [Trait("triggerTags", "VFY-07,RS-04")]
    public async Task StartupWithMultipleTargetsOpensOnlyTheDefaultTarget()
    {
        var events = new List<string>();
        var first = Target(FirstId, FirstEndpoint, isDefault: false, paired: true);
        var second = Target(SecondId, SecondEndpoint, isDefault: true, paired: true);
        var snapshot = ReadySnapshot(12, first, second);
        var manager = new ScriptedTargetManager(snapshot, events)
        {
            ExecuteHandler = command => command is PrepareOpen prepare &&
                prepare.TargetId == SecondId
                    ? Success(snapshot, new OpenPrepared(SecondId, OpenDisposition.Ready))
                    : throw new InvalidOperationException("Startup selected an unexpected target."),
        };
        var dialogs = new FakeLauncherDialogs(events);
        var windows = new FakeWindowCoordinator(events);
        var controller = CreateController(manager, dialogs, windows, events);

        await controller.ApplyStartupPolicyAsync(TestContext.Current.CancellationToken);

        var prepare = Assert.IsType<PrepareOpen>(Assert.Single(manager.Commands));
        Assert.Equal(SecondId, prepare.TargetId);
        Assert.Equal(new[] { SecondId }, windows.CreatedTargets);
        Assert.Empty(windows.ActivatedTargets);
        Assert.Equal(0, dialogs.AddInvocationCount);
    }

    [Fact]
    [Trait("triggerTags", "VFY-07,RS-04")]
    public async Task RepeatedOpenActivatesTheExistingTargetWindowInsteadOfCreatingAnother()
    {
        var events = new List<string>();
        var target = Target(FirstId, FirstEndpoint, isDefault: true, paired: true);
        var snapshot = ReadySnapshot(7, target);
        var manager = new ScriptedTargetManager(snapshot, events)
        {
            ExecuteHandler = command => command is PrepareOpen
                ? Success(snapshot, new OpenPrepared(FirstId, OpenDisposition.Ready))
                : throw new InvalidOperationException("Only PrepareOpen is expected."),
        };
        var windows = new FakeWindowCoordinator(events);
        var controller = CreateController(
            manager,
            new FakeLauncherDialogs(events),
            windows,
            events);

        await controller.OpenAsync(FirstId.Value, TestContext.Current.CancellationToken);
        await controller.OpenAsync(FirstId.Value, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { FirstId }, windows.CreatedTargets);
        Assert.Equal(new[] { FirstId }, windows.ActivatedTargets);
        Assert.Equal(new[] { FirstId, FirstId }, windows.ActivationChecks);
        Assert.Single(manager.Commands.OfType<PrepareOpen>());
    }

    [Fact]
    [Trait("triggerTags", "VFY-03,VFY-07,RS-06")]
    public async Task ForgetWaitsForAnInFlightOpenAndClosesTheResultBeforeDeletingTheTarget()
    {
        var events = new List<string>();
        var target = Target(FirstId, FirstEndpoint, isDefault: true, paired: true);
        var snapshot = ReadySnapshot(41, target);
        var afterForget = ReadySnapshot(42);
        var openEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOpen = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new ScriptedTargetManager(snapshot, events);
        manager.ExecuteAsyncHandler = async (command, cancellationToken) =>
        {
            switch (command)
            {
                case PrepareOpen:
                    openEntered.TrySetResult();
                    await releaseOpen.Task.WaitAsync(cancellationToken);
                    return Success(snapshot, new OpenPrepared(FirstId, OpenDisposition.Ready));
                case Forget forget when forget.TargetId == FirstId &&
                    forget.ExpectedRevision == 41:
                    return manager.Publish(
                        Success(afterForget, new TargetForgotten(FirstId)));
                default:
                    throw new InvalidOperationException(
                        "The concurrent workflow issued an unexpected command.");
            }
        };
        var dialogs = new FakeLauncherDialogs(events)
        {
            ForgetResult = new ForgetDialogResult(true, null),
        };
        var windows = new FakeWindowCoordinator(events);
        var controller = CreateController(manager, dialogs, windows, events);

        var opening = controller.OpenAsync(
            FirstId.Value,
            TestContext.Current.CancellationToken).AsTask();
        await openEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var forgetting = controller.ForgetAsync(
            FirstId.Value,
            TestContext.Current.CancellationToken).AsTask();

        await Task.Yield();
        Assert.DoesNotContain("dialogs.forget", events);
        releaseOpen.TrySetResult();
        await Task.WhenAll(opening, forgetting);

        Assert.True(
            events.IndexOf("windows.open-or-activate") < events.IndexOf("dialogs.forget"),
            "Forget must not inspect a target while its open operation is still in flight.");
        Assert.True(
            events.IndexOf("windows.open-or-activate") < events.IndexOf("windows.close"),
            "An in-flight open must finish before forget closes the target window.");
        Assert.True(
            events.IndexOf("windows.close") < events.IndexOf("manager.Forget"),
            "The target window must close before target and session data are deleted.");
        Assert.Empty(manager.Snapshot.Targets);
    }

    [Theory]
    [Trait("triggerTags", "VFY-04,VFY-07,RS-07")]
    [InlineData(TargetErrorCode.NetworkNotPrivate, "可能为公用网络或状态未知")]
    [InlineData(TargetErrorCode.HarnessNotRunning, "Harness 似乎未启动")]
    [InlineData(TargetErrorCode.DefaultPortOccupied, "默认端口 3080")]
    [InlineData(TargetErrorCode.WrongService, "核对明确配置的 Harness 端口")]
    [InlineData(TargetErrorCode.LegacyUnauthenticatedHarness, "无认证 Harness")]
    [InlineData(TargetErrorCode.UnsupportedHarness, "Harness 风格协议")]
    [InlineData(TargetErrorCode.ProbeForbidden, "拒绝了探测")]
    [InlineData(TargetErrorCode.ProbeUnreachable, "无法到达该目标")]
    [InlineData(TargetErrorCode.ProbeTimedOut, "目标探测超时")]
    [InlineData(TargetErrorCode.SessionForbidden, "拒绝了当前会话")]
    public async Task NetworkAndProbeFailuresHaveDistinctActionableMessages(
        TargetErrorCode errorCode,
        string expectedFragment)
    {
        var events = new List<string>();
        var target = Target(FirstId, FirstEndpoint, isDefault: true, paired: true);
        var snapshot = ReadySnapshot(8, target);
        var manager = new ScriptedTargetManager(snapshot, events)
        {
            ExecuteHandler = _ => new TargetCommandResult(
                false,
                snapshot,
                null,
                new TargetError(errorCode, TargetErrorScope.Command, true)),
        };
        var controller = CreateController(
            manager,
            new FakeLauncherDialogs(events),
            new FakeWindowCoordinator(events),
            events);

        var exception = await Assert.ThrowsAsync<LauncherPresentationException>(
            () => controller.OpenAsync(
                FirstId.Value,
                TestContext.Current.CancellationToken).AsTask());

        Assert.Contains(expectedFragment, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("triggerTags", "VFY-05,VFY-07,RS-05")]
    public async Task AddRunsInspectTrustConfirmPairAndOpenInOrder()
    {
        var events = new List<string>();
        var candidateId = new CandidateId(
            Guid.Parse("40c7f40e-6ddf-4da4-a1bd-287c90debd19"));
        var empty = ReadySnapshot(1);
        var pendingTarget = Target(FirstId, FirstEndpoint, isDefault: true, paired: false);
        var pending = ReadySnapshot(2, pendingTarget);
        var pairedTarget = pendingTarget with { SessionState = TargetSessionState.Paired };
        var paired = ReadySnapshot(2, pairedTarget);
        var manager = new ScriptedTargetManager(empty, events);
        manager.ExecuteHandler = command => command switch
        {
            InspectCandidate inspect when inspect.Ipv4 == "192.168.80.20" && inspect.Port is null =>
                Success(empty, new CandidateInspected(new CandidateView(candidateId, FirstEndpoint))),
            ConfirmCandidate confirm when confirm.CandidateId == candidateId &&
                confirm.DisplayName == "Harness A" &&
                confirm.Trust == new TrustConfirmation(1, true, true, true) =>
                manager.Publish(Success(pending, new TargetConfirmed(FirstId, false))),
            Pair pair when pair.TargetId == FirstId && !pair.Link.IsCleared =>
                manager.Publish(Success(paired, new TargetPaired(FirstId))),
            _ => throw new InvalidOperationException("The add workflow issued an unexpected command."),
        };
        var pairingCharacters =
            "http://192.168.80.20:3080/?token=ephemeral".ToCharArray();
        var dialogs = new FakeLauncherDialogs(events)
        {
            AddResult = new AddTargetDialogResult("192.168.80.20", null, "Harness A"),
            TrustAccepted = true,
            PairingCharacters = pairingCharacters,
        };
        var windows = new FakeWindowCoordinator(events);
        var controller = CreateController(manager, dialogs, windows, events);

        await controller.AddAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AddWorkflowEvents, events);
        Assert.Collection(
            manager.Commands,
            command => Assert.IsType<InspectCandidate>(command),
            command => Assert.IsType<ConfirmCandidate>(command),
            command => Assert.IsType<Pair>(command));
        Assert.All(pairingCharacters, character => Assert.Equal('\0', character));
        Assert.Equal(new[] { FirstId }, windows.CreatedTargets);
    }

    [Fact]
    [Trait("triggerTags", "VFY-05,VFY-07,RS-05")]
    public async Task CancellingTrustStopsBeforeConfirmAndLeavesTheCatalogUnchanged()
    {
        var events = new List<string>();
        var empty = ReadySnapshot(20);
        var manager = new ScriptedTargetManager(empty, events)
        {
            ExecuteHandler = command => command is InspectCandidate
                ? Success(
                    empty,
                    new CandidateInspected(new CandidateView(
                        new CandidateId(Guid.Parse("2b2df902-35e2-4bb0-8dd5-204ea426d519")),
                        FirstEndpoint)))
                : throw new InvalidOperationException("Cancellation must prevent persistence."),
        };
        var dialogs = new FakeLauncherDialogs(events)
        {
            AddResult = new AddTargetDialogResult("192.168.80.20", null, null),
            TrustAccepted = false,
        };
        var windows = new FakeWindowCoordinator(events);
        var controller = CreateController(manager, dialogs, windows, events);

        await controller.AddAsync(TestContext.Current.CancellationToken);

        Assert.IsType<InspectCandidate>(Assert.Single(manager.Commands));
        Assert.Empty(manager.Snapshot.Targets);
        Assert.Equal(20, manager.Snapshot.Revision);
        Assert.Equal(0, dialogs.PairInvocationCount);
        Assert.Empty(windows.CreatedTargets);
    }

    [Fact]
    [Trait("triggerTags", "VFY-05,VFY-07,RS-08")]
    public async Task CancellingPairingKeepsTheConfirmedTargetPendingWithoutSendingAPairCommand()
    {
        var events = new List<string>();
        var candidateId = new CandidateId(
            Guid.Parse("8d0b448c-53a7-4427-8167-ee95d726b809"));
        var empty = ReadySnapshot(30);
        var pending = ReadySnapshot(
            31,
            Target(FirstId, FirstEndpoint, isDefault: true, paired: false));
        var manager = new ScriptedTargetManager(empty, events);
        manager.ExecuteHandler = command => command switch
        {
            InspectCandidate => Success(
                empty,
                new CandidateInspected(new CandidateView(candidateId, FirstEndpoint))),
            ConfirmCandidate => manager.Publish(
                Success(pending, new TargetConfirmed(FirstId, false))),
            _ => throw new InvalidOperationException("A cancelled pairing must not send Pair."),
        };
        var dialogs = new FakeLauncherDialogs(events)
        {
            AddResult = new AddTargetDialogResult("192.168.80.20", null, null),
            TrustAccepted = true,
            PairingCharacters = null,
        };
        var windows = new FakeWindowCoordinator(events);
        var controller = CreateController(manager, dialogs, windows, events);

        await controller.AddAsync(TestContext.Current.CancellationToken);

        Assert.Collection(
            manager.Commands,
            command => Assert.IsType<InspectCandidate>(command),
            command => Assert.IsType<ConfirmCandidate>(command));
        Assert.Equal(
            TargetSessionState.PendingPairing,
            Assert.Single(manager.Snapshot.Targets).SessionState);
        Assert.Equal(1, dialogs.PairInvocationCount);
        Assert.Empty(windows.CreatedTargets);
    }

    [Fact]
    [Trait("triggerTags", "VFY-05,RS-08")]
    public async Task PairingDialogCleanupComparesThePastedClipboardAndZerosItsCapturedCopy()
    {
        var captured = "http://192.168.80.20:3080/?token=ephemeral".ToCharArray();
        var clipboard = new RecordingClipboardPort();

        await WpfLauncherDialogs.ClearCapturedClipboardAsync(
            clipboard,
            captured,
            TestContext.Current.CancellationToken);

        Assert.Equal("http://192.168.80.20:3080/?token=ephemeral", clipboard.ComparedText);
        Assert.All(captured, character => Assert.Equal('\0', character));
    }

    [Fact]
    [Trait("triggerTags", "VFY-05,RS-08")]
    public async Task PairingClipboardCleanupCannotBeCancelledBeforeTheCapturedCopyIsZeroed()
    {
        var captured = "http://192.168.80.20:3080/?token=ephemeral".ToCharArray();
        var clipboard = new RecordingClipboardPort
        {
            ThrowCancellation = true,
        };
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await WpfLauncherDialogs.ClearCapturedClipboardAsync(
            clipboard,
            captured,
            cancelled.Token);

        Assert.Equal(CancellationToken.None, clipboard.ObservedCancellationToken);
        Assert.All(captured, character => Assert.Equal('\0', character));
    }

    [Fact]
    [Trait("triggerTags", "VFY-05,RS-08")]
    public void PairingDialogAcceptsOnlyTheUneditedCurrentExplicitPaste()
    {
        var pasted = "http://192.168.80.20:3080/?token=ephemeral".ToCharArray();
        var identical = pasted.ToArray();
        var edited = pasted.ToArray();
        edited[^1] = 'x';

        Assert.True(PairTargetDialog.IsCurrentExplicitPaste(
            identical,
            pasted,
            currentRevision: 4,
            acceptedPasteRevision: 4));
        Assert.False(PairTargetDialog.IsCurrentExplicitPaste(
            edited,
            pasted,
            currentRevision: 4,
            acceptedPasteRevision: 4));
        Assert.False(PairTargetDialog.IsCurrentExplicitPaste(
            identical,
            pasted,
            currentRevision: 5,
            acceptedPasteRevision: 4));
        Assert.False(PairTargetDialog.IsCurrentExplicitPaste(
            identical,
            captured: null,
            currentRevision: 4,
            acceptedPasteRevision: 4));
    }

    [Fact]
    [Trait("triggerTags", "VFY-02,VFY-03,VFY-07,RS-06")]
    public async Task RenameDefaultAndForgetUseTheSelectedIdentityAndConfirmedRevision()
    {
        var events = new List<string>();
        var first = Target(FirstId, FirstEndpoint, isDefault: true, paired: true);
        var second = Target(SecondId, SecondEndpoint, isDefault: false, paired: true);
        var snapshot = ReadySnapshot(73, first, second);
        var afterForget = ReadySnapshot(74, second with { IsDefault = true });
        var manager = new ScriptedTargetManager(snapshot, events);
        manager.ExecuteHandler = command => command switch
        {
            Rename rename when rename.TargetId == FirstId && rename.DisplayName == "Renamed" =>
                Success(snapshot, new TargetRenamed(FirstId)),
            SetDefault setDefault when setDefault.TargetId == SecondId =>
                Success(snapshot, new DefaultTargetChanged(SecondId)),
            Forget forget when forget.TargetId == FirstId &&
                forget.ExpectedRevision == 73 &&
                forget.SuccessorTargetId == SecondId =>
                manager.Publish(Success(afterForget, new TargetForgotten(FirstId))),
            _ => throw new InvalidOperationException("A mutation used stale or incorrect identity data."),
        };
        var dialogs = new FakeLauncherDialogs(events)
        {
            RenameResult = new RenameDialogResult(true, "Renamed"),
            ForgetResult = new ForgetDialogResult(true, SecondId),
        };
        var windows = new FakeWindowCoordinator(events);
        var controller = CreateController(manager, dialogs, windows, events);

        await controller.RenameAsync(FirstId.Value, TestContext.Current.CancellationToken);
        await controller.SetDefaultAsync(SecondId.Value, TestContext.Current.CancellationToken);
        await controller.ForgetAsync(FirstId.Value, TestContext.Current.CancellationToken);

        Assert.Collection(
            manager.Commands,
            command => Assert.IsType<Rename>(command),
            command => Assert.IsType<SetDefault>(command),
            command => Assert.IsType<Forget>(command));
        var forgetCommand = Assert.IsType<Forget>(manager.Commands[2]);
        Assert.Equal(73, forgetCommand.ExpectedRevision);
        Assert.Equal(SecondId, forgetCommand.SuccessorTargetId);
        Assert.Equal(new[] { FirstId }, windows.ClosedTargets);
        Assert.True(
            events.IndexOf("windows.close") < events.IndexOf("manager.Forget"),
            "The target window must close before the destructive forget command.");
    }

    [Fact]
    [Trait("triggerTags", "VFY-07,RS-10")]
    public async Task DiagnosticsAndUpdateActionsDelegateWithoutOpeningTargetWindows()
    {
        var events = new List<string>();
        var snapshot = ReadySnapshot(
            8,
            Target(FirstId, FirstEndpoint, isDefault: true, paired: true));
        var manager = new ScriptedTargetManager(snapshot, events);
        var dialogs = new FakeLauncherDialogs(events);
        var windows = new FakeWindowCoordinator(events);
        var diagnostics = new FakeDiagnosticsService(events);
        var updates = new FakeUpdatePageService(events);
        var controller = new LauncherController(manager, dialogs, windows, diagnostics, updates);

        await controller.ExportDiagnosticsAsync(TestContext.Current.CancellationToken);
        await controller.ViewUpdatesAsync(TestContext.Current.CancellationToken);

        Assert.Same(snapshot, diagnostics.ExportedSnapshot);
        Assert.Equal(1, diagnostics.InvocationCount);
        Assert.Equal(1, updates.InvocationCount);
        Assert.Equal(DelegationEvents, events);
        Assert.Empty(windows.CreatedTargets);
        Assert.Empty(manager.Commands);
    }

    [Fact]
    [Trait("triggerTags", "VFY-02,VFY-07,RS-06")]
    public async Task AddingAnExistingEndpointSkipsPairingAndActivatesThatTarget()
    {
        var events = new List<string>();
        var candidateId = new CandidateId(
            Guid.Parse("4f1d9112-d8ae-4c1e-a28c-827234b24280"));
        var target = Target(FirstId, FirstEndpoint, isDefault: true, paired: true);
        var snapshot = ReadySnapshot(90, target);
        var manager = new ScriptedTargetManager(snapshot, events)
        {
            ExecuteHandler = command => command switch
            {
                InspectCandidate => Success(
                    snapshot,
                    new CandidateInspected(new CandidateView(candidateId, FirstEndpoint))),
                ConfirmCandidate => Success(snapshot, new TargetConfirmed(FirstId, true)),
                _ => throw new InvalidOperationException("Existing targets must not be paired again."),
            },
        };
        var dialogs = new FakeLauncherDialogs(events)
        {
            AddResult = new AddTargetDialogResult("192.168.80.20", null, "ignored"),
            TrustAccepted = true,
            PairingCharacters = "must-not-be-read".ToCharArray(),
        };
        var windows = new FakeWindowCoordinator(events);
        windows.RegisterOpenTarget(FirstId);
        var controller = CreateController(manager, dialogs, windows, events);

        await controller.AddAsync(TestContext.Current.CancellationToken);

        Assert.Collection(
            manager.Commands,
            command => Assert.IsType<InspectCandidate>(command),
            command => Assert.IsType<ConfirmCandidate>(command));
        Assert.Equal(0, dialogs.PairInvocationCount);
        Assert.Empty(windows.CreatedTargets);
        Assert.Equal(new[] { FirstId }, windows.ActivatedTargets);
    }

    [Fact]
    [Trait("triggerTags", "VFY-05,VFY-07,RS-05")]
    public async Task VisibleAuthenticationInvalidationClosesBeforeVerifiedSessionCleanup()
    {
        var events = new List<string>();
        var pairedTarget = Target(
            FirstId,
            FirstEndpoint,
            isDefault: true,
            paired: true);
        var paired = ReadySnapshot(101, pairedTarget);
        var pending = ReadySnapshot(
            101,
            pairedTarget with { SessionState = TargetSessionState.PendingPairing });
        var manager = new ScriptedTargetManager(paired, events);
        manager.ExecuteHandler = command => command is InvalidateSession invalidate &&
                invalidate.TargetId == FirstId
                    ? manager.Publish(new TargetCommandResult(
                        true,
                        pending,
                        new SessionInvalidated(FirstId),
                        null))
                    : throw new InvalidOperationException(
                        "Only the affected session may be invalidated.");
        var windows = new FakeWindowCoordinator(events);
        windows.RegisterOpenTarget(FirstId);
        var controller = CreateController(
            manager,
            new FakeLauncherDialogs(events),
            windows,
            events);
        var refreshed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        controller.SnapshotChanged += (_, _) => refreshed.TrySetResult();

        windows.RaiseAuthenticationInvalidated(FirstId);
        await refreshed.Task.WaitAsync(TestContext.Current.CancellationToken);

        var command = Assert.IsType<InvalidateSession>(
            Assert.Single(manager.Commands));
        Assert.Equal(FirstId, command.TargetId);
        Assert.Equal(new[] { FirstId }, windows.ClosedTargets);
        Assert.Equal(
            TargetSessionState.PendingPairing,
            Assert.Single(manager.Snapshot.Targets).SessionState);
        Assert.True(
            events.IndexOf("windows.close") <
            events.IndexOf("manager.InvalidateSession"),
            "The WebView and browser process must release before session data is deleted.");
    }

    private static LauncherController CreateController(
        ScriptedTargetManager manager,
        FakeLauncherDialogs dialogs,
        FakeWindowCoordinator windows,
        List<string> events) => new(
            manager,
            dialogs,
            windows,
            new FakeDiagnosticsService(events),
            new FakeUpdatePageService(events));

    private static TargetView Target(
        TargetId id,
        TargetEndpoint endpoint,
        bool isDefault,
        bool paired) => new(
            id,
            endpoint,
            null,
            endpoint.Authority,
            isDefault,
            TargetManager.CurrentTrustPolicyVersion,
            new DateTimeOffset(2026, 8, 30, 1, 2, 3, TimeSpan.Zero),
            paired ? TargetSessionState.Paired : TargetSessionState.PendingPairing);

    private static TargetManagerSnapshot ReadySnapshot(
        long revision,
        params TargetView[] targets) => new(
            CatalogAvailability.Ready,
            TargetManager.CurrentCatalogSchemaVersion,
            revision,
            Array.AsReadOnly(targets),
            targets.SingleOrDefault(target => target.IsDefault)?.TargetId,
            null);

    private static TargetCommandResult Success(
        TargetManagerSnapshot snapshot,
        TargetCommandOutcome outcome) => new(true, snapshot, outcome, null);
}

internal sealed class ScriptedTargetManager : ITargetManager
{
    private readonly List<string> _events;

    public ScriptedTargetManager(TargetManagerSnapshot snapshot, List<string> events)
    {
        Snapshot = snapshot;
        _events = events;
    }

    public TargetManagerSnapshot Snapshot { get; private set; }

    public List<TargetCommand> Commands { get; } = [];

    public Func<TargetCommand, TargetCommandResult>? ExecuteHandler { get; set; }

    public Func<
        TargetCommand,
        CancellationToken,
        ValueTask<TargetCommandResult>>? ExecuteAsyncHandler
    { get; set; }

    public ValueTask<TargetManagerSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Add("manager.snapshot");
        return ValueTask.FromResult(Snapshot);
    }

    public ValueTask<TargetCommandResult> ExecuteAsync(
        TargetCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Add($"manager.{command.GetType().Name}");
        Commands.Add(command);
        if (ExecuteAsyncHandler is not null)
        {
            return ExecuteAsyncHandler(command, cancellationToken);
        }

        return ValueTask.FromResult(
            ExecuteHandler?.Invoke(command)
            ?? throw new InvalidOperationException("No command result was scripted."));
    }

    public TargetCommandResult Publish(TargetCommandResult result)
    {
        Snapshot = result.Snapshot;
        return result;
    }
}

internal sealed class FakeLauncherDialogs : ILauncherDialogs
{
    private readonly List<string> _events;

    public FakeLauncherDialogs(List<string> events)
    {
        _events = events;
    }

    public AddTargetDialogResult? AddResult { get; init; }

    public bool TrustAccepted { get; init; }

    public char[]? PairingCharacters { get; init; }

    public RenameDialogResult RenameResult { get; init; } = new(false, null);

    public ForgetDialogResult ForgetResult { get; init; } = new(false, null);

    public int AddInvocationCount { get; private set; }

    public int PairInvocationCount { get; private set; }

    public AddTargetDialogResult? ShowAddTarget()
    {
        _events.Add("dialogs.add");
        AddInvocationCount++;
        return AddResult;
    }

    public bool ConfirmTrust(TargetEndpoint endpoint)
    {
        _events.Add("dialogs.trust");
        return TrustAccepted;
    }

    public ValueTask<char[]?> ShowPairingLinkAsync(
        TargetView target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Add("dialogs.pair");
        PairInvocationCount++;
        return ValueTask.FromResult(PairingCharacters);
    }

    public RenameDialogResult ShowRename(TargetView target)
    {
        _events.Add("dialogs.rename");
        return RenameResult;
    }

    public ForgetDialogResult ShowForget(
        TargetView target,
        IReadOnlyList<TargetView> successors)
    {
        _events.Add("dialogs.forget");
        return ForgetResult;
    }
}

internal sealed class FakeWindowCoordinator : ITargetWindowCoordinator
{
    private readonly List<string> _events;
    private readonly HashSet<TargetId> _openTargets = [];

    public FakeWindowCoordinator(List<string> events)
    {
        _events = events;
    }

    public event EventHandler<TargetAuthenticationInvalidatedEventArgs>?
        AuthenticationInvalidated;

    public List<TargetId> CreatedTargets { get; } = [];

    public List<TargetId> ActivatedTargets { get; } = [];

    public List<TargetId> ActivationChecks { get; } = [];

    public List<TargetId> ClosedTargets { get; } = [];

    public void RegisterOpenTarget(TargetId targetId) => _openTargets.Add(targetId);

    public void RaiseAuthenticationInvalidated(TargetId targetId) =>
        AuthenticationInvalidated?.Invoke(
            this,
            new TargetAuthenticationInvalidatedEventArgs(targetId));

    public ValueTask<bool> TryActivateAsync(
        TargetId targetId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Add("windows.try-activate");
        ActivationChecks.Add(targetId);
        if (!_openTargets.Contains(targetId))
        {
            return ValueTask.FromResult(false);
        }

        ActivatedTargets.Add(targetId);
        return ValueTask.FromResult(true);
    }

    public ValueTask OpenOrActivateAsync(
        TargetView target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Add("windows.open-or-activate");
        if (_openTargets.Add(target.TargetId))
        {
            CreatedTargets.Add(target.TargetId);
        }
        else
        {
            ActivatedTargets.Add(target.TargetId);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(TargetId targetId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Add("windows.close");
        _openTargets.Remove(targetId);
        ClosedTargets.Add(targetId);
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Add("windows.close-all");
        _openTargets.Clear();
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeDiagnosticsService : IDiagnosticsExportService
{
    private readonly List<string> _events;

    public FakeDiagnosticsService(List<string> events)
    {
        _events = events;
    }

    public int InvocationCount { get; private set; }

    public TargetManagerSnapshot? ExportedSnapshot { get; private set; }

    public ValueTask ExportAsync(
        TargetManagerSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Add("diagnostics.export");
        InvocationCount++;
        ExportedSnapshot = snapshot;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeUpdatePageService : IUpdatePageService
{
    private readonly List<string> _events;

    public FakeUpdatePageService(List<string> events)
    {
        _events = events;
    }

    public int InvocationCount { get; private set; }

    public ValueTask OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Add("updates.open");
        InvocationCount++;
        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingClipboardPort : IClipboardPort
{
    public string? ComparedText { get; private set; }

    public bool ThrowCancellation { get; init; }

    public CancellationToken ObservedCancellationToken { get; private set; }

    public ValueTask ClearIfUnchangedAsync(
        ReadOnlyMemory<char> pastedText,
        CancellationToken cancellationToken)
    {
        ObservedCancellationToken = cancellationToken;
        if (ThrowCancellation)
        {
            throw new OperationCanceledException();
        }

        cancellationToken.ThrowIfCancellationRequested();
        ComparedText = new string(pastedText.Span);
        return ValueTask.CompletedTask;
    }
}

internal static class AcceptanceEndpointFactory
{
    public static async ValueTask<TargetEndpoint> CreateAsync(
        string ipv4,
        string port,
        CancellationToken cancellationToken)
    {
        var support = new EndpointSupport();
        using var manager = new TargetManager(new TargetManagerPorts(
            new EmptyStorage(),
            support,
            support,
            support,
            support,
            support,
            support,
            support));
        var result = await manager.ExecuteAsync(
            new InspectCandidate(ipv4, port),
            cancellationToken);
        return result.Outcome is CandidateInspected inspected
            ? inspected.Candidate.Endpoint
            : throw new InvalidOperationException("The endpoint fixture could not be created.");
    }

    private sealed class EmptyStorage : ITargetStorage
    {
        public ValueTask<TargetCatalogReadResult> ReadCatalogAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new TargetCatalogReadResult(CatalogReadStatus.Missing));

        public ValueTask WriteCatalogAsync(
            TargetCatalogDocument document,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask<IReadOnlyCollection<ForgetTombstone>> ReadForgetTombstonesAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyCollection<ForgetTombstone>>(
                Array.Empty<ForgetTombstone>());

        public ValueTask WriteForgetTombstoneAsync(
            ForgetTombstone tombstone,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask ClearForgetTombstoneAsync(
            TargetId targetId,
            CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private sealed class EndpointSupport :
        IClock,
        IIdGenerator,
        INetworkPort,
        ITargetProbePort,
        ISessionPort,
        ITargetRuntimePort,
        IClipboardPort
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

        public Guid NewId() => Guid.NewGuid();

        public ValueTask<NetworkCategory> GetCurrentCategoryAsync(
            CancellationToken cancellationToken) => ValueTask.FromResult(NetworkCategory.Private);

        public ValueTask<TargetProbeResult> ProbeAsync(
            TargetEndpoint endpoint,
            CancellationToken cancellationToken) => ValueTask.FromResult(
                new TargetProbeResult(TargetProbeClassification.SupportedAuthenticatedHarness));

        public ValueTask<SessionMetadataReadResult> ReadAsync(
            TargetId targetId,
            TargetEndpoint endpoint,
            CancellationToken cancellationToken) => ValueTask.FromResult(
                new SessionMetadataReadResult(SessionMetadataStatus.Missing));

        public ValueTask MarkPairingInProgressAsync(
            TargetId targetId,
            TargetEndpoint endpoint,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask CommitAsync(
            TargetId targetId,
            TargetEndpoint endpoint,
            DateTimeOffset committedAtUtc,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask DeleteAsync(
            TargetId targetId,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask DeletePairingStateAsync(
            TargetId targetId,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask ResetUncommittedAsync(
            TargetId targetId,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask<PairingHandshake> PairAsync(
            TargetId targetId,
            TargetEndpoint endpoint,
            ReadOnlyMemory<char> encodedToken,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask<RuntimeOpenStatus> PrepareOpenAsync(
            TargetId targetId,
            TargetEndpoint endpoint,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask CloseAsync(
            TargetId targetId,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask DeleteSessionDataAsync(
            TargetId targetId,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask<bool> SessionDataExistsAsync(
            TargetId targetId,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        public ValueTask ClearIfUnchangedAsync(
            ReadOnlyMemory<char> pastedText,
            CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
