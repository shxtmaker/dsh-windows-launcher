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
                    var remote = new RemoteWindow(hub, Guid.NewGuid(), "https://example.invalid/",
                        "Startup test", "https://example.invalid", Path.Combine(root, "webview"),
                        static _ => Task.CompletedTask);
                    Assert.NotNull(remote.Icon);
                    remote.AllowClose();
                    remote.Close();
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
