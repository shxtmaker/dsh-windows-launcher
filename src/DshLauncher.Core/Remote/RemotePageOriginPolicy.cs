namespace DshLauncher.Core.Remote;

/// <summary>
/// 一次来源判定所针对的 WebView2 事件种类。生产侧只把这些事实喂进来，
/// 判定本身不依赖任何 WebView2 类型，因此可在 Linux 完整执行。
/// </summary>
public enum RemoteNavigationKind
{
    /// <summary>顶层文档导航（<c>CoreWebView2.NavigationStarting</c> / <c>NavigationCompleted</c>）。
    /// 服务器重定向仍属于主文档导航：判据是<b>最终</b>文档 URL。</summary>
    MainDocument,

    /// <summary>子框架导航（<c>CoreWebView2.FrameNavigationStarting</c>）：不授予也不撤销能力。</summary>
    ChildFrame,

    /// <summary>子资源请求（<c>CoreWebView2.WebResourceRequested</c>）：不授予也不撤销能力。</summary>
    Subresource,

    /// <summary>外部链接导航（<c>CoreWebView2.NewWindowRequested</c> / <c>target=_blank</c>）：
    /// 这次导航绝不继承此前页面的文件能力。</summary>
    ExternalLink,
}

/// <summary>来源判定结论：<see cref="Trusted"/> 只在"主文档 + 来源逐字相同"时为真。</summary>
public readonly record struct RemoteNavigationVerdict(
    bool Trusted,
    bool MainDocument,
    string Code,
    RemoteOrigin? Origin);

/// <summary>
/// 远程页面来源规则（D15，纯函数）：
/// <list type="bullet">
/// <item><b>只有主文档</b>能决定可信来源；子框架与子资源既不能授予、也不能撤销能力
/// （因此生产侧不得拿 <c>WebResourceRequested</c> 之类的任意请求当凭据）；</item>
/// <item>主文档来源必须与目标可信来源逐字相同（规范化后比较）；</item>
/// <item>任何外部链接导航都不继承文件能力，即便它最终落在同一来源。</item>
/// </list>
/// </summary>
public static class RemotePageOriginPolicy
{
    /// <summary>判定一次导航/请求是否可作为可信主文档来源。</summary>
    public static RemoteNavigationVerdict Evaluate(
        RemoteOrigin? trustedOrigin,
        string? uri,
        RemoteNavigationKind kind)
    {
        if (kind is RemoteNavigationKind.ChildFrame or RemoteNavigationKind.Subresource)
        {
            return new RemoteNavigationVerdict(false, false, RemoteOriginCodes.NotMainDocument, null);
        }

        var parsed = RemoteOrigin.Parse(uri);
        if (!parsed.Ok || parsed.Origin is null)
        {
            return new RemoteNavigationVerdict(false, true, parsed.Code, null);
        }

        if (kind == RemoteNavigationKind.ExternalLink)
        {
            return new RemoteNavigationVerdict(false, true, RemoteOriginCodes.ExternalNavigation, parsed.Origin);
        }

        if (trustedOrigin is null)
        {
            // 目标地址本身不可信（例如配对了非 http/https 地址）：一律失败关闭。
            return new RemoteNavigationVerdict(false, true, RemoteOriginCodes.Invalid, parsed.Origin);
        }

        var trusted = trustedOrigin.Matches(parsed.Origin);
        return new RemoteNavigationVerdict(
            trusted,
            true,
            trusted ? RemoteOriginCodes.Trusted : RemoteOriginCodes.External,
            parsed.Origin);
    }

    /// <summary>便捷入口：只判定顶层文档 URL 是否等于可信来源。</summary>
    public static bool IsTrustedMainDocument(RemoteOrigin? trustedOrigin, string? documentUri) =>
        Evaluate(trustedOrigin, documentUri, RemoteNavigationKind.MainDocument).Trusted;
}
