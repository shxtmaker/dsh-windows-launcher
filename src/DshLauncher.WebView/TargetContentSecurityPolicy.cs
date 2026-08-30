namespace DshLauncher.WebView;

public sealed class TargetContentSecurityPolicy
{
    private readonly TargetRuntimeSecurityPolicy _runtimePolicy =
        TargetRuntimeSecurityPolicy.Restricted;

    public TargetContentSecurityPolicy(TargetContentBinding binding)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
    }

    public TargetContentBinding Binding { get; }

    public TargetRuntimeSecurityPolicy RuntimePolicy => _runtimePolicy;

    public TargetNavigationDecision EvaluateNavigation(
        Uri destination,
        bool isUserInitiated)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (IsSameOrigin(destination))
        {
            return new TargetNavigationDecision(
                TargetNavigationDisposition.AllowInTarget,
                TargetExternalNavigationKind.None,
                Binding.Authority);
        }

        if (!isUserInitiated || !destination.IsAbsoluteUri)
        {
            return TargetNavigationDecision.Blocked;
        }

        if (string.Equals(
                destination.Scheme,
                Uri.UriSchemeHttp,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                destination.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
        {
            return new TargetNavigationDecision(
                TargetNavigationDisposition.RequireExternalConfirmation,
                TargetExternalNavigationKind.HttpOrHttps,
                destination.Authority);
        }

        if (string.Equals(
            destination.Scheme,
            Uri.UriSchemeMailto,
            StringComparison.OrdinalIgnoreCase))
        {
            return new TargetNavigationDecision(
                TargetNavigationDisposition.RequireExternalConfirmation,
                TargetExternalNavigationKind.Mailto,
                Uri.UriSchemeMailto);
        }

        return TargetNavigationDecision.Blocked;
    }

    public bool AllowsResource(
        Uri resource,
        TargetContentResourceKind resourceKind)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return IsSameOrigin(resource) ||
               resourceKind == TargetContentResourceKind.Websocket &&
               DshWebUiCompatibilityPolicy.IsBoundWebSocket(
                   resource,
                   Binding) ||
               DshWebUiCompatibilityPolicy.AllowsResource(
                   resource,
                   Binding,
                   resourceKind);
    }

    public bool AllowsFrameNavigation(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return DshWebUiCompatibilityPolicy.AllowsFrameNavigation(
            destination,
            Binding);
    }

    private bool IsSameOrigin(Uri destination)
    {
        return destination.IsAbsoluteUri &&
               string.Equals(
                   destination.Scheme,
                   Binding.Origin.Scheme,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   destination.Host,
                   Binding.Origin.Host,
                   StringComparison.OrdinalIgnoreCase) &&
               destination.Port == Binding.Origin.Port;
    }
}

public enum TargetContentResourceKind
{
    Document,
    Stylesheet,
    Image,
    Media,
    Font,
    Script,
    XmlHttpRequest,
    Fetch,
    TextTrack,
    EventSource,
    Websocket,
    Manifest,
    SignedExchange,
    Ping,
    CspViolationReport,
    Other,
}

public sealed record TargetNavigationDecision(
    TargetNavigationDisposition Disposition,
    TargetExternalNavigationKind ExternalKind,
    string DisplayTarget)
{
    public static TargetNavigationDecision Blocked { get; } = new(
        TargetNavigationDisposition.Block,
        TargetExternalNavigationKind.None,
        string.Empty);
}

public enum TargetNavigationDisposition
{
    AllowInTarget,
    RequireExternalConfirmation,
    Block
}

public enum TargetExternalNavigationKind
{
    None,
    HttpOrHttps,
    Mailto
}

public sealed record TargetRuntimeSecurityPolicy(
    bool AllowDownloads,
    bool AllowPermissions,
    bool AllowCertificateErrors,
    bool AllowDevTools,
    bool AllowDefaultContextMenus,
    bool AllowHostObjects,
    bool AllowWebMessages,
    bool AllowBrowserNavigationShortcuts,
    bool AllowPageEditing,
    bool AllowFindInPage,
    bool AllowZoom)
{
    public static TargetRuntimeSecurityPolicy Restricted { get; } = new(
        AllowDownloads: false,
        AllowPermissions: false,
        AllowCertificateErrors: false,
        AllowDevTools: false,
        AllowDefaultContextMenus: false,
        AllowHostObjects: false,
        AllowWebMessages: false,
        AllowBrowserNavigationShortcuts: false,
        AllowPageEditing: true,
        AllowFindInPage: true,
        AllowZoom: true);
}
