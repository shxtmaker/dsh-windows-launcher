using System.Net;
using System.IO;

namespace DshLauncher.WebView;

public sealed class TargetContentBinding
{
    public TargetContentBinding(
        Guid targetId,
        Uri origin,
        string userDataFolder)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataFolder);

        if (targetId == Guid.Empty)
        {
            throw new ArgumentException(
                "Target identity must not be empty.",
                nameof(targetId));
        }

        if (!origin.IsAbsoluteUri ||
            !string.Equals(origin.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            !IPAddress.TryParse(origin.Host, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !IsRfc1918(address) ||
            origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment) ||
            !string.IsNullOrEmpty(origin.UserInfo))
        {
            throw new ArgumentException(
                "Target origin must be a clean absolute HTTP IPv4 origin.",
                nameof(origin));
        }

        if (!Path.IsPathFullyQualified(userDataFolder))
        {
            throw new ArgumentException(
                "The user data folder must be an absolute path.",
                nameof(userDataFolder));
        }

        TargetId = targetId;
        Origin = new Uri(
            $"http://{origin.Host}:{origin.Port}/",
            UriKind.Absolute);
        UserDataFolder = Path.GetFullPath(userDataFolder);
    }

    public Guid TargetId { get; }

    public Uri Origin { get; }

    public string UserDataFolder { get; }

    public string Authority => $"{Origin.Host}:{Origin.Port}";

    private static bool IsRfc1918(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }
}
