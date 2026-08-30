namespace DshLauncher.WebView;

internal static class DshWebUiCompatibilityPolicy
{
    internal const string MarketOrigin = "https://dsh-market.com";
    internal const string TurnstileOrigin =
        "https://challenges.cloudflare.com";

    private const string MarketChallengePath =
        "/api/turnstile/challenge";

    private static readonly string[] SameOriginFramePathPrefixes =
    [
        "/api/skin-center/we/web/",
        "/api/skin-center/we/scene-runtime/",
        "/sidebar/html/",
    ];

    public static bool AllowsResource(
        Uri resource,
        TargetContentBinding binding,
        TargetContentResourceKind resourceKind)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(binding);

        if (!resource.IsAbsoluteUri)
        {
            return false;
        }

        if (string.Equals(
                resource.Scheme,
                "data",
                StringComparison.OrdinalIgnoreCase))
        {
            return resourceKind == TargetContentResourceKind.Image;
        }

        if (string.Equals(
                resource.Scheme,
                "blob",
                StringComparison.OrdinalIgnoreCase))
        {
            return IsBoundBlob(resource, binding);
        }

        if (IsHttpsOrigin(resource, MarketOrigin))
        {
            return AllowsMarketResource(resource, resourceKind);
        }

        return IsHttpsOrigin(resource, TurnstileOrigin) &&
               AllowsTurnstileResource(resource, resourceKind);
    }

    public static bool AllowsFrameNavigation(
        Uri destination,
        TargetContentBinding binding)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(binding);

        if (!destination.IsAbsoluteUri)
        {
            return false;
        }

        if (string.Equals(
                destination.Scheme,
                "about",
                StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(
                destination.OriginalString,
                "about:blank",
                StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(
                destination.Scheme,
                "blob",
                StringComparison.OrdinalIgnoreCase))
        {
            return IsBoundBlob(destination, binding);
        }

        if (IsBoundHttpOrigin(destination, binding))
        {
            return SameOriginFramePathPrefixes.Any(prefix =>
                destination.AbsolutePath.StartsWith(
                    prefix,
                    StringComparison.Ordinal));
        }

        if (IsHttpsOrigin(destination, MarketOrigin))
        {
            return string.Equals(
                destination.AbsolutePath,
                MarketChallengePath,
                StringComparison.Ordinal);
        }

        return IsHttpsOrigin(destination, TurnstileOrigin) &&
               IsTurnstilePath(destination);
    }

    public static bool IsBoundWebSocket(
        Uri resource,
        TargetContentBinding binding)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(binding);

        return resource.IsAbsoluteUri &&
               string.Equals(
                   resource.Scheme,
                   "ws",
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   resource.Host,
                   binding.Origin.Host,
                   StringComparison.OrdinalIgnoreCase) &&
               resource.Port == binding.Origin.Port;
    }

    private static bool IsBoundBlob(
        Uri resource,
        TargetContentBinding binding)
    {
        const string BlobPrefix = "blob:";
        var value = resource.OriginalString;
        return value.StartsWith(BlobPrefix, StringComparison.OrdinalIgnoreCase) &&
               Uri.TryCreate(
                   value[BlobPrefix.Length..],
                   UriKind.Absolute,
                   out var creatorOrigin) &&
               IsBoundHttpOrigin(creatorOrigin, binding);
    }

    private static bool AllowsMarketResource(
        Uri resource,
        TargetContentResourceKind resourceKind)
    {
        return resourceKind switch
        {
            TargetContentResourceKind.Document => string.Equals(
                resource.AbsolutePath,
                MarketChallengePath,
                StringComparison.Ordinal),
            TargetContentResourceKind.Image => true,
            TargetContentResourceKind.XmlHttpRequest or
                TargetContentResourceKind.Fetch =>
                resource.AbsolutePath.StartsWith(
                    "/api/",
                    StringComparison.Ordinal) ||
                resource.AbsolutePath.StartsWith(
                    "/manifest/",
                    StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool AllowsTurnstileResource(
        Uri resource,
        TargetContentResourceKind resourceKind)
    {
        if (!IsTurnstilePath(resource))
        {
            return false;
        }

        return resourceKind switch
        {
            TargetContentResourceKind.Document or
                TargetContentResourceKind.Stylesheet or
                TargetContentResourceKind.Image or
                TargetContentResourceKind.Font or
                TargetContentResourceKind.XmlHttpRequest or
                TargetContentResourceKind.Fetch => true,
            TargetContentResourceKind.Script => true,
            _ => false,
        };
    }

    private static bool IsTurnstilePath(Uri resource)
    {
        return resource.AbsolutePath.StartsWith(
                   "/turnstile/",
                   StringComparison.Ordinal) ||
               resource.AbsolutePath.StartsWith(
                   "/cdn-cgi/",
                   StringComparison.Ordinal);
    }

    private static bool IsBoundHttpOrigin(
        Uri resource,
        TargetContentBinding binding)
    {
        return resource.IsAbsoluteUri &&
               string.Equals(
                   resource.Scheme,
                   binding.Origin.Scheme,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   resource.Host,
                   binding.Origin.Host,
                   StringComparison.OrdinalIgnoreCase) &&
               resource.Port == binding.Origin.Port;
    }

    private static bool IsHttpsOrigin(Uri resource, string origin)
    {
        var allowedOrigin = new Uri(origin, UriKind.Absolute);
        return resource.IsAbsoluteUri &&
               string.Equals(
                   resource.Scheme,
                   allowedOrigin.Scheme,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   resource.Host,
                   allowedOrigin.Host,
                   StringComparison.OrdinalIgnoreCase) &&
               resource.Port == allowedOrigin.Port;
    }
}
