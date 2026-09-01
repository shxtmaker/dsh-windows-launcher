namespace DshLauncher.Core.Pairing;

/// <summary>
/// One parsed "DSH 远程访问" pairing link: <c>{origin}/pair-accept?pair={token}</c>.
/// The origin names the harness web endpoint; the token is the one-time
/// pairing secret minted by the host-side remote access panel.
/// </summary>
public sealed record PairingLink(Uri BaseUri, string Token)
{
    /// <summary>
    /// Parses one pairing link exactly as the remote access panel advertises
    /// it. The link must be a single absolute HTTP(S) URL whose path is
    /// exactly <c>/pair-accept</c> and whose only query parameter is a
    /// non-empty <c>pair</c> value; userinfo, fragments, extra paths and
    /// extra query parameters are refused so a pasted link can never smuggle
    /// unexpected targets.
    /// </summary>
    public static PairingLink Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new PairingLinkFormatException("The pairing link must not be empty.");
        }

        var candidate = input.Trim();
        if (candidate.Length > MaximumLinkCharacters)
        {
            throw new PairingLinkFormatException("The pairing link is too long.");
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new PairingLinkFormatException("The pairing link must be an absolute http or https URL.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new PairingLinkFormatException("The pairing link must not carry user information.");
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            throw new PairingLinkFormatException("The pairing link must name a host.");
        }

        if (uri.Fragment.Length > 0)
        {
            throw new PairingLinkFormatException("The pairing link must not carry a fragment.");
        }

        if (!string.Equals(uri.AbsolutePath, AcceptPagePath, StringComparison.Ordinal))
        {
            throw new PairingLinkFormatException("The pairing link must point at /pair-accept.");
        }

        var query = uri.Query;
        if (query.Length == 0 || query[0] != '?')
        {
            throw new PairingLinkFormatException("The pairing link is missing the pair token.");
        }

        var parameters = query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries);
        if (parameters.Length != 1)
        {
            throw new PairingLinkFormatException("The pairing link must carry exactly one query parameter.");
        }

        var separator = parameters[0].IndexOf('=');
        if (separator <= 0 || parameters[0][..separator] != "pair")
        {
            throw new PairingLinkFormatException("The pairing link must carry a pair query parameter.");
        }

        var token = Uri.UnescapeDataString(parameters[0][(separator + 1)..]);
        if (token.Length == 0 || token.Length > MaximumTokenCharacters ||
            token.Any(character => character is <= ' ' or >= '\u007f' or '?'))
        {
            throw new PairingLinkFormatException("The pairing token is invalid.");
        }

        return new PairingLink(BaseUriOf(uri), token);
    }

    /// <summary>
    /// Normalizes a bare harness origin (<c>scheme://host[:port]</c>) for
    /// endpoint-only targets. Any path, query or fragment is refused.
    /// </summary>
    public static Uri ParseBaseUri(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new PairingLinkFormatException("The harness address must not be empty.");
        }

        var candidate = input.Trim();
        if (candidate.Length > MaximumLinkCharacters)
        {
            throw new PairingLinkFormatException("The harness address is too long.");
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new PairingLinkFormatException("The harness address must be an absolute http or https URL.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(uri.Host))
        {
            throw new PairingLinkFormatException("The harness address must name a host without user information.");
        }

        if (uri.PathAndQuery != "/" || uri.Fragment.Length > 0)
        {
            throw new PairingLinkFormatException("The harness address must be a bare origin.");
        }

        return BaseUriOf(uri);
    }

    /// <summary>Builds the cookieless remote UI URL for a paired device id.</summary>
    public static string RemoteUiUrl(Uri baseUri, string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return $"{baseUri.ToString().TrimEnd('/')}/pair-app?device={Uri.EscapeDataString(deviceId)}";
    }

    private static Uri BaseUriOf(Uri uri) => new(
        $"{uri.Scheme}://{uri.IdnHost}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}",
        UriKind.Absolute);

    private const string AcceptPagePath = "/pair-accept";
    private const int MaximumLinkCharacters = 2048;
    private const int MaximumTokenCharacters = 512;
}

/// <summary>Thrown when a pairing link or bare origin fails strict parsing.</summary>
public sealed class PairingLinkFormatException : FormatException
{
    public PairingLinkFormatException(string message) : base(message)
    {
    }
}
