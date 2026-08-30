namespace DshLauncher.WebView;

internal static class TargetAuthenticationResponsePolicy
{
    public static bool IsInvalidRootNavigation(
        TargetContentBinding binding,
        string source,
        int httpStatusCode)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return httpStatusCode == 401 &&
               Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
               IsExactBoundUri(binding, uri, "/");
    }

    public static bool IsInvalidApiResponse(
        TargetContentBinding binding,
        string requestUri,
        int httpStatusCode)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return httpStatusCode == 401 &&
               Uri.TryCreate(requestUri, UriKind.Absolute, out var uri) &&
               IsExactBoundApiUri(binding, uri);
    }

    private static bool IsExactBoundUri(
        TargetContentBinding binding,
        Uri uri,
        string path)
    {
        return IsBoundOrigin(binding, uri) &&
               string.Equals(uri.AbsolutePath, path, StringComparison.Ordinal) &&
               string.IsNullOrEmpty(uri.Query) &&
               string.IsNullOrEmpty(uri.Fragment) &&
               string.IsNullOrEmpty(uri.UserInfo);
    }

    private static bool IsExactBoundApiUri(
        TargetContentBinding binding,
        Uri uri)
    {
        return IsBoundOrigin(binding, uri) &&
               (string.Equals(uri.AbsolutePath, "/api", StringComparison.Ordinal) ||
                uri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal)) &&
               string.IsNullOrEmpty(uri.Query) &&
               string.IsNullOrEmpty(uri.Fragment) &&
               string.IsNullOrEmpty(uri.UserInfo);
    }

    private static bool IsBoundOrigin(
        TargetContentBinding binding,
        Uri uri)
    {
        return uri.IsAbsoluteUri &&
               string.Equals(
                   uri.Scheme,
                   binding.Origin.Scheme,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   uri.Host,
                   binding.Origin.Host,
                   StringComparison.OrdinalIgnoreCase) &&
               uri.Port == binding.Origin.Port;
    }
}
