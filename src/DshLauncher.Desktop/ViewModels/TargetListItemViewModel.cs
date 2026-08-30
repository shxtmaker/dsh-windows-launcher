using DshLauncher.Desktop.Resources;

namespace DshLauncher.Desktop.ViewModels;

public sealed class TargetListItemViewModel(LauncherTarget target)
{
    public Guid TargetId { get; } = target.TargetId;
    public string EffectiveDisplayName { get; } = target.EffectiveDisplayName;
    public string Endpoint { get; } = target.Endpoint;
    public bool IsDefault { get; } = target.IsDefault;
    public bool IsPaired { get; } = target.IsPaired;
    public string DefaultText => IsDefault ? Strings.DefaultBadge : string.Empty;
    public string PairingStateText => IsPaired ? Strings.PairingCommitted : Strings.PairingPending;
}
