using System.Text;
using System.Windows;
using System.Windows.Threading;
using DshLauncher.Desktop.Resources;
using DshLauncher.Platform.Windows;
using DshLauncher.WebView;
using StoredDecision = DshLauncher.Platform.Windows.ExternalCapabilityDecision;
using WebViewDecision = DshLauncher.WebView.ExternalCapabilityDecision;

namespace DshLauncher.Desktop;

public sealed class WpfExternalCapabilityConfirmationPort :
    IExternalCapabilityConfirmationPort
{
    private readonly JsonCompatibilityStateStore _stateStore;
    private readonly Func<Window?> _ownerProvider;
    private readonly Dispatcher _dispatcher;
    private readonly bool _extendedCompatibilityAllowed;

    public WpfExternalCapabilityConfirmationPort(
        JsonCompatibilityStateStore stateStore,
        Func<Window?> ownerProvider,
        Dispatcher dispatcher,
        bool extendedCompatibilityAllowed)
    {
        _stateStore = stateStore ??
            throw new ArgumentNullException(nameof(stateStore));
        _ownerProvider = ownerProvider ??
            throw new ArgumentNullException(nameof(ownerProvider));
        _dispatcher = dispatcher ??
            throw new ArgumentNullException(nameof(dispatcher));
        _extendedCompatibilityAllowed = extendedCompatibilityAllowed;
    }

    public async ValueTask<WebViewDecision> ConfirmAsync(
        ExternalCapabilityConfirmationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_extendedCompatibilityAllowed)
        {
            return WebViewDecision.RegistryRollbackBlocked;
        }

        var purposes = request.Uses
            .Select(static use => use.Purpose.Normalize(NormalizationForm.FormC))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var existing = await _stateStore
            .ReadExternalCapabilityConfirmationAsync(
                request.TargetId,
                cancellationToken)
            .ConfigureAwait(false);
        if ((existing.Status is CompatibilityStateReadStatus.Loaded or
            CompatibilityStateReadStatus.RecoveredFromBackup) &&
            existing.State is { } saved &&
            string.Equals(
                saved.SnapshotDigest,
                request.SnapshotSha256,
                StringComparison.OrdinalIgnoreCase) &&
            saved.NormalizedPurposes.SequenceEqual(
                purposes,
                StringComparer.Ordinal))
        {
            return saved.Decision == StoredDecision.Accepted
                ? WebViewDecision.Accepted
                : WebViewDecision.Rejected;
        }

        if (existing.Status is CompatibilityStateReadStatus.UnknownSchema or
            CompatibilityStateReadStatus.Corrupt)
        {
            return WebViewDecision.Rejected;
        }

        var accepted = await _dispatcher.InvokeAsync(
            () => MessageBox.Show(
                _ownerProvider(),
                CreatePrompt(request),
                Strings.CompatibilityConfirmationTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes,
            DispatcherPriority.Normal,
            cancellationToken);
        var decision = accepted
            ? StoredDecision.Accepted
            : StoredDecision.Rejected;
        await _stateStore.WriteExternalCapabilityConfirmationAsync(
            new ExternalCapabilityConfirmationState(
                request.TargetId,
                request.SnapshotSha256,
                purposes,
                decision,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        return accepted ? WebViewDecision.Accepted : WebViewDecision.Rejected;
    }

    public ValueTask RevokeAsync(
        Guid targetId,
        CancellationToken cancellationToken) =>
        _stateStore.DeleteExternalCapabilityConfirmationAsync(
            targetId,
            cancellationToken);

    private static string CreatePrompt(
        ExternalCapabilityConfirmationRequest request)
    {
        var details = request.Uses
            .Select(static use =>
                $"• {use.DisplayOrigin} | {use.Purpose} | {use.Kind}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return $"{Strings.CompatibilityConfirmationPrompt}\n\n" +
               string.Join(Environment.NewLine, details);
    }
}
