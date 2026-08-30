using System.Diagnostics;

namespace DshLauncher.WebView;

public interface ITargetExternalNavigationConsent
{
    ValueTask<bool> ConfirmAsync(
        TargetExternalNavigationPrompt prompt,
        CancellationToken cancellationToken);
}

public interface ITargetExternalUriLauncher
{
    ValueTask OpenAsync(
        Uri destination,
        CancellationToken cancellationToken);
}

public sealed class SystemTargetExternalUriLauncher : ITargetExternalUriLauncher
{
    public ValueTask OpenAsync(
        Uri destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        if (!destination.IsAbsoluteUri ||
            (!string.Equals(
                 destination.Scheme,
                 Uri.UriSchemeHttp,
                 StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(
                 destination.Scheme,
                 Uri.UriSchemeHttps,
                 StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(
                 destination.Scheme,
                 Uri.UriSchemeMailto,
                 StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "Only HTTP, HTTPS, and mailto destinations can be opened externally.",
                nameof(destination));
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = destination.AbsoluteUri,
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException(
            "The system URI handler did not start.");
        return ValueTask.CompletedTask;
    }
}

public sealed record TargetExternalNavigationPrompt(
    TargetExternalNavigationKind Kind,
    string DisplayTarget);

public enum TargetExternalNavigationOutcome
{
    NavigateInCurrentTarget,
    OpenedExternally,
    Declined,
    Blocked
}

public sealed class TargetExternalNavigationCoordinator
{
    private readonly TargetContentSecurityPolicy _policy;
    private readonly ITargetExternalNavigationConsent _consent;
    private readonly ITargetExternalUriLauncher _launcher;

    public TargetExternalNavigationCoordinator(
        TargetContentSecurityPolicy policy,
        ITargetExternalNavigationConsent consent,
        ITargetExternalUriLauncher launcher)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _consent = consent ?? throw new ArgumentNullException(nameof(consent));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    }

    public async ValueTask<TargetExternalNavigationOutcome> HandleAsync(
        Uri destination,
        bool isUserInitiated,
        CancellationToken cancellationToken = default)
    {
        var decision = _policy.EvaluateNavigation(
            destination,
            isUserInitiated);
        if (decision.Disposition == TargetNavigationDisposition.AllowInTarget)
        {
            return TargetExternalNavigationOutcome.NavigateInCurrentTarget;
        }

        if (decision.Disposition !=
            TargetNavigationDisposition.RequireExternalConfirmation)
        {
            return TargetExternalNavigationOutcome.Blocked;
        }

        var confirmed = await _consent.ConfirmAsync(
            new TargetExternalNavigationPrompt(
                decision.ExternalKind,
                decision.DisplayTarget),
            cancellationToken).ConfigureAwait(false);
        if (!confirmed)
        {
            return TargetExternalNavigationOutcome.Declined;
        }

        await _launcher.OpenAsync(destination, cancellationToken)
            .ConfigureAwait(false);
        return TargetExternalNavigationOutcome.OpenedExternally;
    }
}
