using System.Windows;
using DshLauncher.Core;
using DshLauncher.Desktop.Dialogs;
using DshLauncher.Desktop.ViewModels;

namespace DshLauncher.Desktop;

public sealed class WpfLauncherDialogs(
    Func<Window?> ownerProvider,
    IClipboardPort clipboard) : ILauncherDialogs
{
    private readonly Func<Window?> _ownerProvider = ownerProvider ??
        throw new ArgumentNullException(nameof(ownerProvider));
    private readonly IClipboardPort _clipboard = clipboard ??
        throw new ArgumentNullException(nameof(clipboard));

    public AddTargetDialogResult? ShowAddTarget()
    {
        var dialog = Own(new AddTargetDialog());
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public bool ConfirmTrust(TargetEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return Own(new TrustDialog(endpoint.Authority)).ShowDialog() == true;
    }

    public async ValueTask<char[]?> ShowPairingLinkAsync(
        TargetView target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var dialog = Own(new PairTargetDialog());
        try
        {
            var accepted = dialog.ShowDialog() == true;
            await ClearCapturedClipboardAsync(
                _clipboard,
                dialog.TakePastedClipboardText(),
                cancellationToken);
            return accepted ? dialog.TakePairingLink() : null;
        }
        finally
        {
            if (dialog.TakePairingLink() is { } unclaimedPairingLink)
            {
                Array.Clear(unclaimedPairingLink);
            }

            if (dialog.TakePastedClipboardText() is { } unclaimedClipboardText)
            {
                Array.Clear(unclaimedClipboardText);
            }
        }
    }

    public RenameDialogResult ShowRename(TargetView target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var dialog = Own(new RenameTargetDialog(target.DisplayName));
        return dialog.ShowDialog() == true
            ? new RenameDialogResult(true, dialog.DisplayName)
            : new RenameDialogResult(false, null);
    }

    public ForgetDialogResult ShowForget(
        TargetView target,
        IReadOnlyList<TargetView> successors)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(successors);

        var model = new ForgetTargetDialogModel
        {
            TargetName = target.EffectiveDisplayName,
            Endpoint = target.Endpoint.Authority,
        };
        IReadOnlyList<TargetView> selectableSuccessors = target.IsDefault
            ? successors
            : Array.Empty<TargetView>();
        foreach (var successor in selectableSuccessors)
        {
            model.Successors.Add(new LauncherTarget(
                successor.TargetId.Value,
                successor.DisplayName,
                successor.EffectiveDisplayName,
                successor.Endpoint.Authority,
                successor.IsDefault,
                successor.SessionState == TargetSessionState.Paired));
        }

        var dialog = Own(new ForgetTargetDialog(model));
        return dialog.ShowDialog() == true
            ? new ForgetDialogResult(
                true,
                dialog.SuccessorTargetId is { } id ? new TargetId(id) : null)
            : new ForgetDialogResult(false, null);
    }

    private T Own<T>(T dialog)
        where T : Window
    {
        var owner = _ownerProvider();
        if (owner is not null && owner.IsVisible)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return dialog;
    }

    internal static async ValueTask ClearCapturedClipboardAsync(
        IClipboardPort clipboard,
        char[]? capturedText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        if (capturedText is null)
        {
            return;
        }

        try
        {
            await clipboard.ClearIfUnchangedAsync(capturedText, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch
        {
            // Clipboard cleanup is best effort; the in-process copy is always cleared.
        }
        finally
        {
            Array.Clear(capturedText);
        }
    }
}
