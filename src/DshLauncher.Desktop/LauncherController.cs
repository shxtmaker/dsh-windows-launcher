using System.Collections.Concurrent;
using DshLauncher.Core;
using DshLauncher.Desktop.Dialogs;
using DshLauncher.Desktop.ViewModels;

namespace DshLauncher.Desktop;

public interface ILauncherDialogs
{
    AddTargetDialogResult? ShowAddTarget();

    bool ConfirmTrust(TargetEndpoint endpoint);

    ValueTask<char[]?> ShowPairingLinkAsync(
        TargetView target,
        CancellationToken cancellationToken);

    RenameDialogResult ShowRename(TargetView target);

    ForgetDialogResult ShowForget(TargetView target, IReadOnlyList<TargetView> successors);
}

public sealed record RenameDialogResult(bool Accepted, string? DisplayName);

public sealed record ForgetDialogResult(bool Accepted, TargetId? SuccessorTargetId);

public interface ITargetWindowCoordinator
{
    event EventHandler<TargetAuthenticationInvalidatedEventArgs>?
        AuthenticationInvalidated;

    ValueTask<bool> TryActivateAsync(TargetId targetId, CancellationToken cancellationToken);

    ValueTask OpenOrActivateAsync(TargetView target, CancellationToken cancellationToken);

    ValueTask CloseAsync(TargetId targetId, CancellationToken cancellationToken);

    ValueTask CloseAllAsync(CancellationToken cancellationToken);
}

public sealed class TargetAuthenticationInvalidatedEventArgs : EventArgs
{
    public TargetAuthenticationInvalidatedEventArgs(TargetId targetId)
    {
        TargetId = targetId;
    }

    public TargetId TargetId { get; }
}

public interface IDiagnosticsExportService
{
    ValueTask ExportAsync(TargetManagerSnapshot snapshot, CancellationToken cancellationToken);
}

public interface IUpdatePageService
{
    ValueTask OpenAsync(CancellationToken cancellationToken);
}

public sealed class LauncherController : ILauncherController
{
    private readonly ITargetManager _targetManager;
    private readonly ILauncherDialogs _dialogs;
    private readonly ITargetWindowCoordinator _windows;
    private readonly IDiagnosticsExportService _diagnostics;
    private readonly IUpdatePageService _updates;
    private readonly ConcurrentDictionary<TargetId, SemaphoreSlim> _targetOperations =
        new();

    public event EventHandler? SnapshotChanged;

    public LauncherController(
        ITargetManager targetManager,
        ILauncherDialogs dialogs,
        ITargetWindowCoordinator windows,
        IDiagnosticsExportService diagnostics,
        IUpdatePageService updates)
    {
        ArgumentNullException.ThrowIfNull(targetManager);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(updates);

        _targetManager = targetManager;
        _dialogs = dialogs;
        _windows = windows;
        _diagnostics = diagnostics;
        _updates = updates;
        _windows.AuthenticationInvalidated += OnAuthenticationInvalidated;
    }

    public async ValueTask<LauncherSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _targetManager.GetSnapshotAsync(cancellationToken);
        EnsureCatalogReady(snapshot);
        return ToLauncherSnapshot(snapshot);
    }

    public async ValueTask AddAsync(CancellationToken cancellationToken)
    {
        var input = _dialogs.ShowAddTarget();
        if (input is null)
        {
            return;
        }

        var inspected = await ExecuteRequiredAsync(
            new InspectCandidate(input.Ipv4, input.Port),
            cancellationToken);
        var candidate = inspected.Outcome as CandidateInspected
            ?? throw new InvalidOperationException("Candidate inspection returned an unexpected outcome.");

        if (!_dialogs.ConfirmTrust(candidate.Candidate.Endpoint))
        {
            return;
        }

        var confirmed = await ExecuteRequiredAsync(
            new ConfirmCandidate(
                candidate.Candidate.CandidateId,
                input.DisplayName,
                new TrustConfirmation(
                    TargetManager.CurrentTrustPolicyVersion,
                    TrustedLanConfirmed: true,
                    LinuxFirewallConfirmed: true,
                    PrivilegedHarnessConfirmed: true)),
            cancellationToken);
        var outcome = confirmed.Outcome as TargetConfirmed
            ?? throw new InvalidOperationException("Candidate confirmation returned an unexpected outcome.");

        if (outcome.WasExisting)
        {
            await OpenAsync(outcome.TargetId.Value, cancellationToken);
            return;
        }

        await PairAsync(outcome.TargetId.Value, cancellationToken);
    }

    public ValueTask OpenAsync(Guid targetId, CancellationToken cancellationToken)
    {
        var id = RequireTargetId(targetId);
        return WithTargetOperationAsync(
            id,
            token => OpenUnderGateAsync(id, token),
            cancellationToken);
    }

    private async ValueTask OpenUnderGateAsync(
        TargetId id,
        CancellationToken cancellationToken)
    {
        if (await _windows.TryActivateAsync(id, cancellationToken))
        {
            return;
        }

        var prepared = await ExecuteRequiredAsync(new PrepareOpen(id), cancellationToken);
        var outcome = prepared.Outcome as OpenPrepared
            ?? throw new InvalidOperationException("Open preparation returned an unexpected outcome.");

        if (outcome.Disposition == OpenDisposition.PairingRequired)
        {
            await PairUnderGateAsync(id, cancellationToken);
            return;
        }

        var target = FindRequiredTarget(prepared.Snapshot, id);
        await _windows.OpenOrActivateAsync(target, cancellationToken);
    }

    public ValueTask PairAsync(Guid targetId, CancellationToken cancellationToken)
    {
        var id = RequireTargetId(targetId);
        return WithTargetOperationAsync(
            id,
            token => PairUnderGateAsync(id, token),
            cancellationToken);
    }

    private async ValueTask PairUnderGateAsync(
        TargetId id,
        CancellationToken cancellationToken)
    {
        var snapshot = await _targetManager.GetSnapshotAsync(cancellationToken);
        EnsureCatalogReady(snapshot);
        var target = FindRequiredTarget(snapshot, id);
        var characters = await _dialogs.ShowPairingLinkAsync(target, cancellationToken);
        if (characters is null)
        {
            return;
        }

        using var secret = PairingLinkSecret.TakeOwnership(characters);
        var paired = await ExecuteRequiredAsync(new Pair(id, secret), cancellationToken);
        var updated = FindRequiredTarget(paired.Snapshot, id);
        await _windows.OpenOrActivateAsync(updated, cancellationToken);
    }

    public async ValueTask SetDefaultAsync(Guid targetId, CancellationToken cancellationToken)
    {
        await ExecuteRequiredAsync(new SetDefault(RequireTargetId(targetId)), cancellationToken);
    }

    public async ValueTask RenameAsync(Guid targetId, CancellationToken cancellationToken)
    {
        var id = RequireTargetId(targetId);
        var snapshot = await _targetManager.GetSnapshotAsync(cancellationToken);
        EnsureCatalogReady(snapshot);
        var target = FindRequiredTarget(snapshot, id);
        var result = _dialogs.ShowRename(target);
        if (!result.Accepted)
        {
            return;
        }

        await ExecuteRequiredAsync(new Rename(id, result.DisplayName), cancellationToken);
    }

    public ValueTask ForgetAsync(Guid targetId, CancellationToken cancellationToken)
    {
        var id = RequireTargetId(targetId);
        return WithTargetOperationAsync(
            id,
            token => ForgetUnderGateAsync(id, token),
            cancellationToken);
    }

    private async ValueTask ForgetUnderGateAsync(
        TargetId id,
        CancellationToken cancellationToken)
    {
        var snapshot = await _targetManager.GetSnapshotAsync(cancellationToken);
        EnsureCatalogReady(snapshot);
        var target = FindRequiredTarget(snapshot, id);
        var successors = snapshot.Targets.Where(item => item.TargetId != id).ToArray();
        var result = _dialogs.ShowForget(target, successors);
        if (!result.Accepted)
        {
            return;
        }

        await _windows.CloseAsync(id, cancellationToken);
        await ExecuteRequiredAsync(
            new Forget(id, snapshot.Revision, result.SuccessorTargetId),
            cancellationToken);
    }

    public async ValueTask ExportDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _targetManager.GetSnapshotAsync(cancellationToken);
        await _diagnostics.ExportAsync(snapshot, cancellationToken);
    }

    public ValueTask ViewUpdatesAsync(CancellationToken cancellationToken) =>
        _updates.OpenAsync(cancellationToken);

    public async ValueTask ApplyStartupPolicyAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _targetManager.GetSnapshotAsync(cancellationToken);
        EnsureCatalogReady(snapshot);
        if (snapshot.Targets.Count == 0)
        {
            await AddAsync(cancellationToken);
            return;
        }

        var target = snapshot.Targets.Count == 1
            ? snapshot.Targets[0]
            : snapshot.Targets.Single(item => item.IsDefault);
        await OpenAsync(target.TargetId.Value, cancellationToken);
    }

    private async ValueTask<TargetCommandResult> ExecuteRequiredAsync(
        TargetCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _targetManager.ExecuteAsync(command, cancellationToken);
        if (!result.Succeeded)
        {
            throw TargetErrorPresentation.CreateException(result.Error);
        }

        return result;
    }

    private async ValueTask WithTargetOperationAsync(
        TargetId targetId,
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken)
    {
        var gate = _targetOperations.GetOrAdd(
            targetId,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await operation(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private void OnAuthenticationInvalidated(
        object? sender,
        TargetAuthenticationInvalidatedEventArgs args)
    {
        _ = InvalidateAuthenticationSafeAsync(args.TargetId);
    }

    private async Task InvalidateAuthenticationSafeAsync(TargetId targetId)
    {
        try
        {
            await WithTargetOperationAsync(
                targetId,
                async token =>
                {
                    await _windows.CloseAsync(targetId, token);
                    var result = await ExecuteRequiredAsync(
                        new InvalidateSession(targetId),
                        token);
                    if (result.Outcome is not SessionInvalidated invalidated ||
                        invalidated.TargetId != targetId)
                    {
                        throw new InvalidOperationException(
                            "Session invalidation returned an unexpected outcome.");
                    }
                },
                CancellationToken.None);
        }
        catch
        {
            // The target window remains closed if verified cleanup cannot finish.
        }
        finally
        {
            PublishSnapshotChanged();
        }
    }

    private void PublishSnapshotChanged()
    {
        var handler = SnapshotChanged;
        if (handler is null)
        {
            return;
        }

        foreach (EventHandler subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, EventArgs.Empty);
            }
            catch
            {
                // Presentation refresh failures cannot corrupt session cleanup.
            }
        }
    }

    private static TargetId RequireTargetId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new LauncherPresentationException(Resources.Strings.UnexpectedError);
        }

        return new TargetId(value);
    }

    private static TargetView FindRequiredTarget(TargetManagerSnapshot snapshot, TargetId targetId) =>
        snapshot.Targets.FirstOrDefault(item => item.TargetId == targetId)
        ?? throw TargetErrorPresentation.CreateException(
            new TargetError(TargetErrorCode.TargetNotFound, TargetErrorScope.Target, false));

    private static void EnsureCatalogReady(TargetManagerSnapshot snapshot)
    {
        if (snapshot.Availability != CatalogAvailability.Ready)
        {
            throw TargetErrorPresentation.CreateException(snapshot.Problem);
        }
    }

    private static LauncherSnapshot ToLauncherSnapshot(TargetManagerSnapshot snapshot) =>
        new(
            snapshot.Revision,
            snapshot.Targets.Select(target => new LauncherTarget(
                target.TargetId.Value,
                target.DisplayName,
                target.EffectiveDisplayName,
                target.Endpoint.Authority,
                target.IsDefault,
                target.SessionState == TargetSessionState.Paired)).ToArray());
}
