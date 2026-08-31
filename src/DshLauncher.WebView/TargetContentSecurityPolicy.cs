using DshLauncher.Compatibility;

namespace DshLauncher.WebView;

public sealed class TargetContentSecurityPolicy
{
    private readonly TargetRuntimeSecurityPolicy _runtimePolicy;

    public TargetContentSecurityPolicy(
        TargetContentBinding binding,
        PageCapabilitySnapshot snapshot)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        if (snapshot.TargetId != binding.TargetId)
        {
            throw new ArgumentException(
                "The page capability snapshot must belong to the target binding.",
                nameof(snapshot));
        }

        var grantedKinds = snapshot.BaseCapabilities
            .Concat(snapshot.ExtensionCapabilities)
            .Select(static grant => grant.Kind)
            .ToHashSet();
        _runtimePolicy = TargetRuntimeSecurityPolicy.Restricted with
        {
            AllowPageEditing = grantedKinds.Contains(CapabilityKind.PageEditing),
            AllowFindInPage = grantedKinds.Contains(CapabilityKind.FindInPage),
            AllowZoom = grantedKinds.Contains(CapabilityKind.Zoom),
        };
    }

    public TargetContentBinding Binding { get; }

    public PageCapabilitySnapshot Snapshot { get; }

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
        TargetContentResourceKind resourceKind,
        string method = "GET")
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (!TryMapMethod(method, out var mappedMethod) ||
            !TryMapResourceKind(resourceKind, out var mappedResourceKind))
        {
            return false;
        }

        return Snapshot.BaseCapabilities
                   .Concat(Snapshot.ExtensionCapabilities)
                   .Any(grant => AllowsResource(
                       grant,
                       resource,
                       mappedResourceKind,
                       mappedMethod));
    }

    public bool AllowsFrameNavigation(
        Uri destination,
        Uri? parentOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        parentOrigin ??= Binding.Origin;
        return Snapshot.BaseCapabilities
            .Concat(Snapshot.ExtensionCapabilities)
            .Any(grant => AllowsFrame(grant, destination, parentOrigin));
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

    private bool AllowsResource(
        CapabilityGrant grant,
        Uri resource,
        WebResourceKind resourceKind,
        HttpMethodKind method)
    {
        if (!grant.Methods.Contains(method) ||
            !grant.ResourceKinds.Contains(resourceKind))
        {
            return false;
        }

        return grant.Kind switch
        {
            CapabilityKind.SameOrigin => IsSameOrigin(resource),
            CapabilityKind.DataImage =>
                resourceKind == WebResourceKind.Image &&
                string.Equals(resource.Scheme, "data", StringComparison.OrdinalIgnoreCase),
            CapabilityKind.BlobImage =>
                resourceKind == WebResourceKind.Image && IsBoundBlob(resource),
            CapabilityKind.BlobMedia =>
                resourceKind == WebResourceKind.Media && IsBoundBlob(resource),
            CapabilityKind.BlobData =>
                resourceKind is WebResourceKind.Fetch or WebResourceKind.XmlHttpRequest &&
                IsBoundBlob(resource),
            CapabilityKind.ExternalPassive or CapabilityKind.ReviewedApi =>
                grant.DocumentScope != CapabilityDocumentScope.CrossOriginFrameOnly &&
                MatchesExternalGrant(grant, resource),
            CapabilityKind.Frame =>
                resourceKind is WebResourceKind.Document or WebResourceKind.Frame &&
                MatchesExternalGrant(grant, resource),
            CapabilityKind.WebSocket =>
                resourceKind == WebResourceKind.WebSocket &&
                MatchesExternalGrant(grant, resource),
            CapabilityKind.TargetWebSocket =>
                resourceKind == WebResourceKind.WebSocket &&
                MatchesTargetWebSocketGrant(grant, resource),
            _ => false,
        };
    }

    private bool AllowsFrame(
        CapabilityGrant grant,
        Uri destination,
        Uri parentOrigin)
    {
        return grant.Kind switch
        {
            CapabilityKind.SameOriginFrame =>
                IsSameOrigin(parentOrigin) && IsSameOrigin(destination),
            CapabilityKind.AboutBlankFrame =>
                IsSameOrigin(parentOrigin) &&
                string.Equals(
                    destination.OriginalString,
                    "about:blank",
                    StringComparison.OrdinalIgnoreCase),
            CapabilityKind.Frame =>
                ParentOriginMatches(grant, parentOrigin) &&
                MatchesExternalGrant(grant, destination),
            _ => false,
        };
    }

    private static bool TryMapMethod(
        string method,
        out HttpMethodKind mappedMethod)
    {
        if (string.Equals(method, "GET", StringComparison.Ordinal))
        {
            mappedMethod = HttpMethodKind.Get;
            return true;
        }

        if (string.Equals(method, "HEAD", StringComparison.Ordinal))
        {
            mappedMethod = HttpMethodKind.Head;
            return true;
        }

        if (string.Equals(method, "POST", StringComparison.Ordinal))
        {
            mappedMethod = HttpMethodKind.Post;
            return true;
        }

        if (string.Equals(method, "OPTIONS", StringComparison.Ordinal))
        {
            mappedMethod = HttpMethodKind.Options;
            return true;
        }

        mappedMethod = default;
        return false;
    }

    private static bool TryMapResourceKind(
        TargetContentResourceKind resourceKind,
        out WebResourceKind mappedResourceKind)
    {
        switch (resourceKind)
        {
            case TargetContentResourceKind.Document:
                mappedResourceKind = WebResourceKind.Document;
                return true;
            case TargetContentResourceKind.Stylesheet:
                mappedResourceKind = WebResourceKind.Stylesheet;
                return true;
            case TargetContentResourceKind.Image:
                mappedResourceKind = WebResourceKind.Image;
                return true;
            case TargetContentResourceKind.Media:
                mappedResourceKind = WebResourceKind.Media;
                return true;
            case TargetContentResourceKind.Font:
                mappedResourceKind = WebResourceKind.Font;
                return true;
            case TargetContentResourceKind.Script:
                mappedResourceKind = WebResourceKind.Script;
                return true;
            case TargetContentResourceKind.XmlHttpRequest:
                mappedResourceKind = WebResourceKind.XmlHttpRequest;
                return true;
            case TargetContentResourceKind.Fetch:
                mappedResourceKind = WebResourceKind.Fetch;
                return true;
            case TargetContentResourceKind.TextTrack:
                mappedResourceKind = WebResourceKind.TextTrack;
                return true;
            case TargetContentResourceKind.EventSource:
                mappedResourceKind = WebResourceKind.EventSource;
                return true;
            case TargetContentResourceKind.Websocket:
                mappedResourceKind = WebResourceKind.WebSocket;
                return true;
            case TargetContentResourceKind.Manifest:
                mappedResourceKind = WebResourceKind.Manifest;
                return true;
            case TargetContentResourceKind.Other:
                mappedResourceKind = WebResourceKind.Other;
                return true;
            default:
                mappedResourceKind = default;
                return false;
        }
    }

    private bool IsBoundBlob(Uri resource)
    {
        const string Prefix = "blob:";
        var value = resource.OriginalString;
        return value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) &&
               Uri.TryCreate(value[Prefix.Length..], UriKind.Absolute, out var owner) &&
               IsSameOrigin(owner);
    }

    private bool ParentOriginMatches(
        CapabilityGrant grant,
        Uri parentOrigin)
    {
        if (grant.ParentOrigin is null)
        {
            return IsSameOrigin(parentOrigin);
        }

        return Uri.TryCreate(
                   grant.ParentOrigin,
                   UriKind.Absolute,
                   out var allowedParent) &&
               HaveSameOrigin(parentOrigin, allowedParent);
    }

    private static bool HaveSameOrigin(Uri left, Uri right) =>
        left.IsAbsoluteUri &&
        right.IsAbsoluteUri &&
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static bool MatchesExternalGrant(
        CapabilityGrant grant,
        Uri resource)
    {
        if (grant.Origin is null || !resource.IsAbsoluteUri ||
            !Uri.TryCreate(grant.Origin, UriKind.Absolute, out var allowedOrigin) ||
            !string.Equals(
                resource.Scheme,
                allowedOrigin.Scheme,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                resource.IdnHost,
                allowedOrigin.IdnHost,
                StringComparison.OrdinalIgnoreCase) ||
            resource.Port != allowedOrigin.Port ||
            !string.IsNullOrEmpty(resource.UserInfo) ||
            !string.IsNullOrEmpty(resource.Fragment) ||
            !HasAllowedQuery(GetRawQuery(resource), grant.QueryKeys) ||
            HasAmbiguousPath(resource.AbsolutePath))
        {
            return false;
        }

        var path = grant.Path ?? "/";
        return grant.PathMatch switch
        {
            CapabilityPathMatch.Exact =>
                string.Equals(resource.AbsolutePath, path, StringComparison.Ordinal),
            CapabilityPathMatch.SegmentPrefix =>
                IsSegmentPrefix(path, resource.AbsolutePath),
            CapabilityPathMatch.None => resource.AbsolutePath == "/",
            _ => false,
        };
    }

    private bool MatchesTargetWebSocketGrant(
        CapabilityGrant grant,
        Uri resource)
    {
        return resource.IsAbsoluteUri &&
               string.Equals(resource.Scheme, "ws", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   resource.IdnHost,
                   Binding.Origin.IdnHost,
                   StringComparison.OrdinalIgnoreCase) &&
               resource.Port == Binding.Origin.Port &&
               string.IsNullOrEmpty(resource.UserInfo) &&
               string.IsNullOrEmpty(resource.Fragment) &&
               !HasAmbiguousPath(resource.AbsolutePath) &&
               grant.PathMatch == CapabilityPathMatch.Exact &&
               string.Equals(
                   resource.AbsolutePath,
                   grant.Path,
                   StringComparison.Ordinal) &&
               HasAllowedQuery(GetRawQuery(resource), grant.QueryKeys);
    }

    private static string GetRawQuery(Uri resource)
    {
        var original = resource.OriginalString;
        var queryIndex = original.IndexOf('?');
        if (queryIndex < 0)
        {
            return string.Empty;
        }

        var fragmentIndex = original.IndexOf('#', queryIndex + 1);
        return fragmentIndex < 0
            ? original[queryIndex..]
            : original[queryIndex..fragmentIndex];
    }

    private static bool HasAllowedQuery(
        string query,
        IReadOnlyList<string> allowedKeys)
    {
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        if (allowedKeys.Count == 0 || query[0] != '?')
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in query[1..].Split('&', StringSplitOptions.None))
        {
            var separator = item.IndexOf('=');
            if (separator <= 0 || separator != item.LastIndexOf('='))
            {
                return false;
            }

            var key = item[..separator];
            if (key.Contains('%', StringComparison.Ordinal) ||
                key.Contains('+', StringComparison.Ordinal) ||
                !allowedKeys.Contains(key, StringComparer.Ordinal) ||
                !seen.Add(key))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSegmentPrefix(string prefix, string path)
    {
        if (string.Equals(prefix, path, StringComparison.Ordinal))
        {
            return true;
        }

        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return (prefix.Length > 0 && prefix[^1] == '/') ||
               path.Length > prefix.Length && path[prefix.Length] == '/';
    }

    private static bool HasAmbiguousPath(string path) =>
        path.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("%5c", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("%2e", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("%25", StringComparison.OrdinalIgnoreCase);
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
