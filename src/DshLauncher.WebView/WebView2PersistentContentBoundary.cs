using Microsoft.Web.WebView2.Core;

namespace DshLauncher.WebView;

internal static class WebView2PersistentContentBoundary
{
    private const CoreWebView2BrowsingDataKinds ProhibitedData =
        CoreWebView2BrowsingDataKinds.ServiceWorkers |
        CoreWebView2BrowsingDataKinds.CacheStorage;

    public static Task ClearProhibitedDataAsync(CoreWebView2 core)
    {
        ArgumentNullException.ThrowIfNull(core);
        return core.Profile.ClearBrowsingDataAsync(ProhibitedData);
    }
}
