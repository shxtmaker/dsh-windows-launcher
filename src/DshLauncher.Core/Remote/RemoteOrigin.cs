using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace DshLauncher.Core.Remote;

/// <summary>
/// 远程页面来源判定的稳定码。它们只在本机使用，绝不写进线协议报文。
/// </summary>
public static class RemoteOriginCodes
{
    /// <summary>URL 是合法的 http/https 来源。</summary>
    public const string Valid = "origin-valid";

    /// <summary>不是绝对 URL，或缺少可用的主机名。</summary>
    public const string Invalid = "origin-invalid";

    /// <summary>不是 http/https（例如 file:、about:、javascript:）。</summary>
    public const string UnsupportedScheme = "origin-unsupported-scheme";

    /// <summary>authority 里带 userinfo（<c>http://user@host/</c>），一律不当作可信页面。</summary>
    public const string UserInfoRejected = "origin-userinfo-rejected";

    /// <summary>主文档来源与目标可信来源一致。</summary>
    public const string Trusted = "origin-trusted";

    /// <summary>主文档来源与目标可信来源不同（含后缀伪装、端口/协议不同）。</summary>
    public const string External = "origin-external";

    /// <summary>这是一次外部链接导航：无论最终落在哪里，都不在这次导航里继承文件能力。</summary>
    public const string ExternalNavigation = "origin-external-navigation";

    /// <summary>子框架导航或子资源请求：既不授予能力，也不撤销既有能力。</summary>
    public const string NotMainDocument = "origin-not-main-document";
}

/// <summary>
/// 一次 URL → 来源的解析结果。<see cref="Code"/> 取 <see cref="RemoteOriginCodes"/> 的稳定码。
/// </summary>
public readonly record struct RemoteOriginParse(bool Ok, string Code, RemoteOrigin? Origin);

/// <summary>
/// 远程页面所属的一个规范化来源（scheme + host + port）。
///
/// 规范化规则（D15，全部由 Linux 可跑用例钉住）：
/// <list type="bullet">
/// <item>scheme 只接受 http/https，且小写化；</item>
/// <item>authority 带 userinfo 一律拒绝（<c>http://evil.example@our.host/</c> 不是我们的页面）；</item>
/// <item>主机名小写化并去掉结尾的根点（<c>our.host.</c> 与 <c>our.host</c> 相同）；</item>
/// <item>IPv6 字面量按 <see cref="IPAddress"/> 规范化后加方括号（<c>[0:0:0:0:0:0:0:1]</c> 与 <c>[::1]</c> 相同）；</item>
/// <item>默认端口（http 80 / https 443）不写进规范化形式，非默认端口必须比较。</item>
/// </list>
/// 比较永远基于 <see cref="Normalized"/> 的逐字比较，绝不做前缀/后缀匹配，
/// 因此 <c>our.host.evil.example</c>、<c>evil-our.host</c> 都不会被当作同一来源。
/// </summary>
public sealed record RemoteOrigin
{
    private RemoteOrigin(string scheme, string host, int port)
    {
        Scheme = scheme;
        Host = host;
        Port = port;
        Normalized = port == DefaultPort(scheme)
            ? scheme + "://" + host
            : scheme + "://" + host + ":" + port.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>小写的 <c>http</c> 或 <c>https</c>。</summary>
    public string Scheme { get; }

    /// <summary>规范化主机：小写 DNS 名（无结尾点），或带方括号的 IPv6 字面量。</summary>
    public string Host { get; }

    /// <summary>端口（未写端口时为协议默认端口）。</summary>
    public int Port { get; }

    /// <summary><c>scheme://host[:port]</c>，默认端口省略。</summary>
    public string Normalized { get; }

    /// <summary>是否为协议默认端口。</summary>
    public bool IsDefaultPort => Port == DefaultPort(Scheme);

    /// <summary>逐字比较两个来源是否相同（大小写、默认端口、结尾点、IPv6 写法已在解析时归一）。</summary>
    public bool Matches(RemoteOrigin? other) =>
        other is not null && string.Equals(Normalized, other.Normalized, StringComparison.Ordinal);

    /// <inheritdoc />
    public override string ToString() => Normalized;

    /// <summary>解析一个绝对 URL 并规范化其来源；失败返回确定码而不是抛异常。</summary>
    public static RemoteOriginParse Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return new RemoteOriginParse(false, RemoteOriginCodes.Invalid, null);
        }

        var candidate = url.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return new RemoteOriginParse(false, RemoteOriginCodes.Invalid, null);
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        if (!string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) &&
            !string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return new RemoteOriginParse(false, RemoteOriginCodes.UnsupportedScheme, null);
        }

        // userinfo 不是来源的一部分，但带凭据的 URL 一律不当可信页面：
        // 这既是来源规则，也是防 "http://evil.example@our.host/" 这类视觉伪装。
        if (uri.UserInfo.Length > 0)
        {
            return new RemoteOriginParse(false, RemoteOriginCodes.UserInfoRejected, null);
        }

        // Uri 会悄悄丢掉 authority 里的越界内容（实测 http://[::1].evil.example/ 的 Host 就是 "[::1]"），
        // 因此主机必须对照<b>原始 authority 文本</b>复核，不能只信 Uri.Host。
        if (!TryExtractAuthority(candidate, out var authority) ||
            authority.Contains('@') ||
            !TryCanonicalizeHost(uri, authority, out var host))
        {
            return new RemoteOriginParse(false, RemoteOriginCodes.Invalid, null);
        }

        return new RemoteOriginParse(true, RemoteOriginCodes.Valid, new RemoteOrigin(scheme, host, uri.Port));
    }

    /// <summary>取出 URL 里 scheme 之后的原始 authority 文本（到第一个 <c>/ ? #</c> 为止）。</summary>
    private static bool TryExtractAuthority(string url, out string authority)
    {
        authority = string.Empty;
        var separator = url.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0)
        {
            return false;
        }

        var start = separator + 3;
        var end = url.IndexOfAny(['/', '?', '#'], start);
        authority = end < 0 ? url[start..] : url[start..end];
        return authority.Length > 0;
    }

    private static bool TryCanonicalizeHost(Uri uri, string authority, out string host)
    {
        host = string.Empty;
        if (authority.StartsWith('['))
        {
            // IPv6 字面量必须是 "[<literal>]" 或 "[<literal>]:<port>"，后面不允许再有任何字符。
            var closing = authority.IndexOf(']', StringComparison.Ordinal);
            if (closing < 0)
            {
                return false;
            }

            var rest = authority[(closing + 1)..];
            var portText = rest.Length > 1 ? rest[1..] : string.Empty;
            if (rest.Length > 0 &&
                (rest[0] != ':' || portText.Length == 0 || !portText.All(char.IsAsciiDigit)))
            {
                return false;
            }

            if (uri.HostNameType != UriHostNameType.IPv6)
            {
                return false;
            }

            var literal = authority[1..closing];
            if (!IPAddress.TryParse(literal, out var address) ||
                address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }

            host = "[" + address.ToString() + "]";
            return true;
        }

        if (authority.Contains('[') || authority.Contains(']'))
        {
            return false;
        }

        if (uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.Basic))
        {
            return false;
        }

        var candidate = uri.Host.ToLowerInvariant().TrimEnd('.');
        if (candidate.Length == 0)
        {
            return false;
        }

        // 不变量：规范化结果必须是 authority 文本去掉大小写、端口与结尾根点后的忠实形态；
        // 任何"被 Uri 丢掉了字符"的情况都判为不可信。
        var colon = authority.LastIndexOf(':');
        var rawHost = (colon < 0 ? authority : authority[..colon]).ToLowerInvariant().TrimEnd('.');
        if (!string.Equals(rawHost, candidate, StringComparison.Ordinal))
        {
            return false;
        }

        host = candidate;
        return true;
    }

    private static int DefaultPort(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ? 443 : 80;
}
