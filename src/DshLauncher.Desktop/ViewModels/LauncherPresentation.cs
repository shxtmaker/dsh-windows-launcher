namespace DshLauncher.Desktop.ViewModels;

public sealed record LauncherTarget(
    Guid TargetId,
    string? DisplayName,
    string EffectiveDisplayName,
    string Endpoint,
    bool IsDefault,
    bool IsPaired);

public sealed record LauncherSnapshot(long Revision, IReadOnlyList<LauncherTarget> Targets);

public interface ILauncherController
{
    ValueTask<LauncherSnapshot> ReadAsync(CancellationToken cancellationToken);

    ValueTask AddAsync(CancellationToken cancellationToken);

    ValueTask OpenAsync(Guid targetId, CancellationToken cancellationToken);

    ValueTask PairAsync(Guid targetId, CancellationToken cancellationToken);

    ValueTask SetDefaultAsync(Guid targetId, CancellationToken cancellationToken);

    ValueTask RenameAsync(Guid targetId, CancellationToken cancellationToken);

    ValueTask ForgetAsync(Guid targetId, CancellationToken cancellationToken);

    ValueTask ExportDiagnosticsAsync(CancellationToken cancellationToken);

    ValueTask ViewUpdatesAsync(CancellationToken cancellationToken);
}
