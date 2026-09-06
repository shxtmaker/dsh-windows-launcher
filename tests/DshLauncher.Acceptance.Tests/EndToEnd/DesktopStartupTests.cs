using System.IO;
using DshLauncher.Core;
using DshLauncher.Core.Hub;
using DshLauncher.Core.Pairing;
using DshLauncher.Desktop;
using DshLauncher.Platform.Windows;
using Xunit;

namespace DshLauncher.Acceptance.Tests.EndToEnd;

public sealed class DesktopStartupTests
{
    [Fact]
    [Trait("triggerTags", "VFY-01,VFY-02")]
    public void DesktopWindowsInitializeWithTheirPackagedIcons()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var root = Path.Combine(Path.GetTempPath(), "DshLauncher.StartupTests", Guid.NewGuid().ToString("N"));
                var data = new ApplicationDataStore(new ApplicationDataLayout(root));
                data.InitializeAsync().AsTask().GetAwaiter().GetResult();
                using var transport = new HttpPairingTransport(new PairingTransportOptions());
                var hub = new PairingHub(new PairingHubOptions(), new JsonTargetStore(data), transport,
                    new SystemClock(), new GuidIdGenerator());
                hub.StartAsync().AsTask().GetAwaiter().GetResult();
                try
                {
                    using var tray = new TrayHost("Startup test", static () => { }, static () => { });
                    var management = new ManagementWindow(hub, data, tray);
                    Assert.NotNull(management.Icon);
                    management.AllowClose();
                    management.Close();
                    var firstTarget = Guid.NewGuid();
                    var remote = new RemoteWindow(hub, firstTarget, "https://example.invalid/",
                        "Startup test", "https://example.invalid", Path.Combine(root, "webview"),
                        static _ => Task.CompletedTask);
                    Assert.NotNull(remote.Icon);
                    var host = Assert.IsType<System.Windows.Controls.Grid>(remote.FindName("PageHost"));
                    var tabs = Assert.IsType<System.Windows.Controls.ListBox>(remote.FindName("PageTabs"));
                    var firstView = Assert.Single(host.Children.Cast<System.Windows.UIElement>());
                    var secondTarget = Guid.NewGuid();
                    var windowCount = app.Windows.Count;
                    remote.OpenPage(secondTarget, "https://second.invalid/", "Second target",
                        "https://second.invalid", Path.Combine(root, "second-webview"));
                    Assert.Equal(windowCount, app.Windows.Count);
                    Assert.Equal(2, tabs.Items.Count);
                    Assert.Equal(System.Windows.Visibility.Hidden, firstView.Visibility);
                    var secondView = host.Children[1];
                    Assert.Equal(System.Windows.Visibility.Visible, secondView.Visibility);
                    remote.OpenPage(firstTarget, "https://example.invalid/", "Startup test",
                        "https://example.invalid", Path.Combine(root, "webview"));
                    Assert.Equal(2, host.Children.Count);
                    Assert.Same(firstView, host.Children[0]);
                    Assert.Equal(System.Windows.Visibility.Visible, firstView.Visibility);
                    Assert.Equal(System.Windows.Visibility.Hidden, secondView.Visibility);
                    tabs.SelectedIndex = 1;
                    Assert.Equal(System.Windows.Visibility.Visible, secondView.Visibility);
                    remote.RemovePage(secondTarget);
                    Assert.Single(tabs.Items.Cast<object>());
                    Assert.Equal(System.Windows.Visibility.Visible, firstView.Visibility);
                    remote.AllowClose();
                    remote.Close();
                    Assert.Empty(host.Children.Cast<System.Windows.UIElement>());
                }
                finally
                {
                    hub.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    app.Shutdown();
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Desktop initialization timed out.");
        Assert.Null(failure);
    }
}
