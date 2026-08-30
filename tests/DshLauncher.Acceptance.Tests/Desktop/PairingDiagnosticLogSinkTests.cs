using System.IO;
using System.Text;
using DshLauncher.Core;
using DshLauncher.Desktop;
using DshLauncher.Platform.Windows;
using Xunit;

namespace DshLauncher.Acceptance.Tests.Desktop;

public sealed class PairingDiagnosticLogSinkTests
{
    [Fact]
    [Trait("triggerTags", "VFY-05,VFY-07,VFY-08")]
    public async Task PairingLogContainsOnlyBoundedFixedCodesAndNoEndpoint()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "DshLauncherPairingDiagnosticTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var layout = new ApplicationDataLayout(root);
            using var applicationData = new ApplicationDataStore(layout);
            await applicationData.InitializeAsync(TestContext.Current.CancellationToken);
            await using var log = new RollingDiagnosticLog(layout);
            var sink = new PairingDiagnosticLogSink(log);

            await sink.ReportAsync(
                new PairingDiagnosticEvent(
                    PairingDiagnosticStage.BrowserControllerInitialize,
                    PairingDiagnosticOutcome.Failed,
                    PairingFailureCategory.BrowserInitialization),
                TestContext.Current.CancellationToken);

            var bytes = await log.ReadForExportAsync(
                0,
                TestContext.Current.CancellationToken);
            var text = Encoding.UTF8.GetString(Assert.IsType<byte[]>(bytes));
            Assert.Contains(
                "pairing.browser-controller-initialize.failed.browser-initialization",
                text,
                StringComparison.Ordinal);
            Assert.DoesNotContain("192.168.", text, StringComparison.Ordinal);
            Assert.DoesNotContain("?token=", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cookie", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("triggerTags", "VFY-05,VFY-07,VFY-08")]
    public void EveryPairingDiagnosticCombinationMapsToASafeBoundedCode()
    {
        foreach (var stage in Enum.GetValues<PairingDiagnosticStage>())
            foreach (var outcome in Enum.GetValues<PairingDiagnosticOutcome>())
                foreach (var category in Enum.GetValues<PairingFailureCategory>())
                {
                    var classification = PairingDiagnosticLogSink.CreateClassification(
                        new PairingDiagnosticEvent(stage, outcome, category));
                    Assert.InRange(classification.Length, 1, 80);
                    Assert.All(classification, character => Assert.True(
                        char.IsAsciiLetterOrDigit(character) ||
                        character is '.' or '_' or '-'));
                }
    }
}
