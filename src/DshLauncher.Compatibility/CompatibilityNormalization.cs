using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DshLauncher.Compatibility;

internal static partial class CompatibilityNormalization
{
    private static readonly HashSet<CapabilityKind> BaseCapabilityKinds =
    [
        CapabilityKind.SameOrigin,
        CapabilityKind.SameOriginFrame,
        CapabilityKind.AboutBlankFrame,
        CapabilityKind.DataImage,
        CapabilityKind.BlobImage,
        CapabilityKind.BlobMedia,
        CapabilityKind.WebGl,
        CapabilityKind.PageEditing,
        CapabilityKind.FindInPage,
        CapabilityKind.Zoom,
        CapabilityKind.UserImageInput,
        CapabilityKind.BlobData,
    ];

    private static readonly HashSet<CapabilityKind> ExtensionCapabilityKinds =
    [
        CapabilityKind.ExternalPassive,
        CapabilityKind.ReviewedApi,
        CapabilityKind.Frame,
        CapabilityKind.WebSocket,
        CapabilityKind.DedicatedWorker,
        CapabilityKind.SharedWorker,
        CapabilityKind.AudioWorklet,
        CapabilityKind.Oopif,
        CapabilityKind.Redirect,
        CapabilityKind.CdpFetch,
        CapabilityKind.ReviewedInlineScriptSha256,
        CapabilityKind.TargetWebSocket,
    ];

    private static readonly HashSet<WebResourceKind> PassiveResourceKinds =
    [
        WebResourceKind.Stylesheet,
        WebResourceKind.Image,
        WebResourceKind.Media,
        WebResourceKind.Font,
        WebResourceKind.TextTrack,
        WebResourceKind.Manifest,
    ];

    public static bool IsBaseCapability(CapabilityKind kind) =>
        BaseCapabilityKinds.Contains(kind);

    public static bool IsExtensionCapability(CapabilityKind kind) =>
        ExtensionCapabilityKinds.Contains(kind);

    public static bool TryNormalizeUiId(string value, out string normalized) =>
        TryNormalizeIdentifier(value, UiIdPattern(), 128, out normalized);

    public static bool TryNormalizeAdapterKey(string value, out string normalized) =>
        TryNormalizeIdentifier(value, AdapterKeyPattern(), 128, out normalized);

    public static bool TryNormalizeRuleId(string value, out string normalized) =>
        TryNormalizeIdentifier(value, RuleIdPattern(), 128, out normalized);

    public static bool TryNormalizeSourceRevision(string? value, out string? normalized)
    {
        normalized = value;
        return value is null ||
               value.Length is >= 7 and <= 128 &&
               value.All(character => char.IsAsciiLetterOrDigit(character) ||
                   character is '.' or '_' or '-');
    }

    public static bool TryNormalizePurpose(string value, out string normalized)
    {
        normalized = value.Normalize();
        return value.Length is >= 1 and <= 160 &&
               string.Equals(value, normalized, StringComparison.Ordinal) &&
               value.All(character => !char.IsControl(character));
    }

    public static bool TryNormalizeReasonToken(string value, out string normalized) =>
        TryNormalizeIdentifier(value, ReasonTokenPattern(), 64, out normalized);

    public static bool TryNormalizeCapabilityId(string value, out string normalized) =>
        TryNormalizeIdentifier(value, CapabilityIdPattern(), 96, out normalized);

    public static bool TryNormalizeDiagnosticName(string value, out string normalized) =>
        TryNormalizeIdentifier(value, DiagnosticNamePattern(), 96, out normalized);

    public static bool TryNormalizeQueryKey(string value, out string normalized) =>
        TryNormalizeIdentifier(value, QueryKeyPattern(), 128, out normalized);

    public static bool TryNormalizeSha256Base64(
        string value,
        out string normalized)
    {
        normalized = string.Empty;
        if (value.Length != 44)
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length != 32)
            {
                return false;
            }

            normalized = Convert.ToBase64String(bytes);
            return string.Equals(value, normalized, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool TryNormalizeOrigin(string value, out string normalized)
    {
        normalized = string.Empty;
        if (value.Length is 0 or > 2048 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath != "/" ||
            uri.Port is < 1 or > 65535 ||
            !HasExplicitPort(value))
        {
            return false;
        }

        string host;
        if (uri.HostNameType == UriHostNameType.Dns)
        {
            try
            {
                host = new IdnMapping().GetAscii(uri.Host).ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (host.Contains('*', StringComparison.Ordinal) ||
                Uri.CheckHostName(host) != UriHostNameType.Dns)
            {
                return false;
            }
        }
        else if (uri.HostNameType == UriHostNameType.IPv6)
        {
            host = $"[{uri.Host.ToLowerInvariant()}]";
        }
        else if (uri.HostNameType == UriHostNameType.IPv4)
        {
            host = uri.Host;
        }
        else
        {
            return false;
        }

        normalized = $"{uri.Scheme.ToLowerInvariant()}://{host}:{uri.Port}";
        return true;
    }

    public static bool TryNormalizePath(
        string value,
        CapabilityPathMatch match,
        out string normalized)
    {
        normalized = string.Empty;
        if (value.Length is 0 or > 2048 || value[0] != '/' ||
            value.Contains('\\', StringComparison.Ordinal) ||
            value.Contains('?', StringComparison.Ordinal) ||
            value.Contains('#', StringComparison.Ordinal) ||
            value.Contains("//", StringComparison.Ordinal) ||
            EncodedSeparatorPattern().IsMatch(value) ||
            EncodedPercentPattern().IsMatch(value))
        {
            return false;
        }

        var segments = value.Split('/');
        foreach (var segment in segments)
        {
            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(segment);
            }
            catch (UriFormatException)
            {
                return false;
            }

            if (decoded is "." or ".." || decoded.Any(char.IsControl) ||
                !string.Equals(decoded, decoded.Normalize(), StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (match == CapabilityPathMatch.SegmentPrefix &&
            value != "/" && !value.EndsWith('/'))
        {
            return false;
        }

        if (match == CapabilityPathMatch.None)
        {
            return false;
        }

        normalized = NormalizePercentEncoding(value);
        return true;
    }

    public static bool IsGrantShapeValid(GrantDefinition grant)
    {
        if (!IsExtensionCapability(grant.Kind))
        {
            return false;
        }

        if (grant.Kind == CapabilityKind.ReviewedInlineScriptSha256)
        {
            return grant.Origin is null &&
                   grant.ParentOrigin is null &&
                   grant.Path is null &&
                   grant.PathMatch == CapabilityPathMatch.None &&
                   grant.Methods.Count == 0 &&
                   grant.ResourceKinds.Count == 0 &&
                   grant.QueryKeys.Count == 0 &&
                   grant.DocumentScope == CapabilityDocumentScope.TargetDocument &&
                   grant.ScriptSha256 is not null &&
                   TryNormalizeSha256Base64(grant.ScriptSha256, out _);
        }

        if (grant.Kind == CapabilityKind.TargetWebSocket)
        {
            return grant.Origin is null &&
                   grant.ParentOrigin is null &&
                   grant.Path is not null &&
                   grant.PathMatch == CapabilityPathMatch.Exact &&
                   grant.Methods.SequenceEqual([HttpMethodKind.Get]) &&
                   grant.ResourceKinds.SequenceEqual([WebResourceKind.WebSocket]) &&
                   grant.QueryKeys.All(static key => key == "device") &&
                   grant.DocumentScope == CapabilityDocumentScope.TargetDocument &&
                   grant.ScriptSha256 is null;
        }

        if (grant.ScriptSha256 is not null ||
            grant.Origin is null || grant.Path is null ||
            grant.PathMatch == CapabilityPathMatch.None ||
            grant.Methods.Count == 0 || grant.ResourceKinds.Count == 0)
        {
            return false;
        }

        if (grant.ParentOrigin is not null &&
            !HasScheme(grant.ParentOrigin, Uri.UriSchemeHttps))
        {
            return false;
        }

        return grant.Kind switch
        {
            CapabilityKind.ExternalPassive =>
                HasScheme(grant.Origin, Uri.UriSchemeHttps) &&
                grant.Methods.All(method =>
                    method is HttpMethodKind.Get or HttpMethodKind.Head) &&
                grant.ResourceKinds.All(resource =>
                    PassiveResourceKinds.Contains(resource) ||
                    resource == WebResourceKind.Script &&
                    grant.DocumentScope == CapabilityDocumentScope.CrossOriginFrameOnly) &&
                (grant.DocumentScope is
                    CapabilityDocumentScope.TargetDocument or
                    CapabilityDocumentScope.CrossOriginFrameOnly) &&
                (grant.DocumentScope != CapabilityDocumentScope.CrossOriginFrameOnly ||
                 grant.ParentOrigin is not null),
            CapabilityKind.ReviewedApi =>
                HasScheme(grant.Origin, Uri.UriSchemeHttps) &&
                grant.Methods.All(method => method is
                    HttpMethodKind.Get or HttpMethodKind.Post or HttpMethodKind.Options) &&
                grant.ResourceKinds.All(resource => resource is
                    WebResourceKind.XmlHttpRequest or WebResourceKind.Fetch) &&
                grant.DocumentScope != CapabilityDocumentScope.AnyApprovedDocument,
            CapabilityKind.Frame =>
                HasScheme(grant.Origin, Uri.UriSchemeHttps) &&
                grant.Methods.All(method =>
                    method is HttpMethodKind.Get or HttpMethodKind.Head) &&
                grant.ResourceKinds.All(resource => resource is
                    WebResourceKind.Document or WebResourceKind.Frame),
            CapabilityKind.WebSocket =>
                HasScheme(grant.Origin, "wss") &&
                grant.Methods.All(method => method == HttpMethodKind.Get) &&
                grant.ResourceKinds.All(resource => resource == WebResourceKind.WebSocket),
            CapabilityKind.DedicatedWorker or
                CapabilityKind.SharedWorker or
                CapabilityKind.AudioWorklet =>
                HasScheme(grant.Origin, Uri.UriSchemeHttps) &&
                grant.Methods.All(method => method == HttpMethodKind.Get) &&
                grant.ResourceKinds.All(resource => resource == WebResourceKind.Script),
            CapabilityKind.Oopif =>
                HasScheme(grant.Origin, Uri.UriSchemeHttps) &&
                grant.Methods.All(method =>
                    method is HttpMethodKind.Get or HttpMethodKind.Head) &&
                grant.ResourceKinds.All(resource => resource == WebResourceKind.Document),
            CapabilityKind.Redirect =>
                HasScheme(grant.Origin, Uri.UriSchemeHttps) &&
                grant.Methods.All(method =>
                    method is HttpMethodKind.Get or HttpMethodKind.Head) &&
                grant.ResourceKinds.All(resource => resource == WebResourceKind.Document),
            CapabilityKind.CdpFetch => false,
            _ => false,
        };
    }

    private static bool HasScheme(string origin, string scheme) =>
        origin.StartsWith($"{scheme}://", StringComparison.Ordinal);

    private static bool TryNormalizeIdentifier(
        string value,
        Regex pattern,
        int maximumLength,
        out string normalized)
    {
        normalized = value.Normalize();
        return value.Length is >= 1 && value.Length <= maximumLength &&
               string.Equals(value, normalized, StringComparison.Ordinal) &&
               pattern.IsMatch(value);
    }

    private static bool HasExplicitPort(string value)
    {
        var authorityStart = value.IndexOf("://", StringComparison.Ordinal) + 3;
        var authorityEnd = value.IndexOf('/', authorityStart);
        var authority = authorityEnd < 0
            ? value[authorityStart..]
            : value[authorityStart..authorityEnd];
        if (authority.StartsWith('['))
        {
            var bracket = authority.IndexOf(']');
            return bracket >= 0 && bracket + 1 < authority.Length &&
                   authority[bracket + 1] == ':';
        }

        return authority.LastIndexOf(':') > 0;
    }

    private static string NormalizePercentEncoding(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '%' && index + 2 < value.Length &&
                byte.TryParse(
                    value.AsSpan(index + 1, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var encoded))
            {
                var character = (char)encoded;
                if (char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~')
                {
                    builder.Append(character);
                }
                else
                {
                    builder.Append('%');
                    builder.Append(encoded.ToString("X2", CultureInfo.InvariantCulture));
                }

                index += 2;
            }
            else
            {
                builder.Append(value[index]);
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*(?:\\.[a-z0-9][a-z0-9-]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex UiIdPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9._/-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex AdapterKeyPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*(?:\\.[a-z0-9][a-z0-9-]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex RuleIdPattern();

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReasonTokenPattern();

    [GeneratedRegex("^[a-z][a-z0-9]*(?:-[a-z0-9]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityIdPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticNamePattern();

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex QueryKeyPattern();

    [GeneratedRegex("%(?:2[fF]|5[cC]|2[eE])", RegexOptions.CultureInvariant)]
    private static partial Regex EncodedSeparatorPattern();

    [GeneratedRegex("%25", RegexOptions.CultureInvariant)]
    private static partial Regex EncodedPercentPattern();
}
