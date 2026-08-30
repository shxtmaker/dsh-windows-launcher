using DshLauncher.WebView;
using Xunit;

namespace DshLauncher.WebView.Tests;

[Trait("triggerTags", "VFY-06")]
public sealed class TargetExternalNavigationCoordinatorTests
{
    private static readonly TargetContentBinding Binding = new(
        new Guid("f42975bd-3ef9-462c-9403-f112879fe48f"),
        new Uri("http://192.168.10.20:3080/"),
        @"C:\Users\test\AppData\Local\DshWindowsLauncher\targets\f42975bd3ef9462c9403f112879fe48f\udf");

    [Fact]
    public async Task SameOriginPopupNavigatesInTheExistingTarget()
    {
        var consent = new RecordingConsent(confirmed: true);
        var launcher = new RecordingLauncher();
        var coordinator = CreateCoordinator(consent, launcher);

        var outcome = await coordinator.HandleAsync(
            new Uri("http://192.168.10.20:3080/session/42"),
            isUserInitiated: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            TargetExternalNavigationOutcome.NavigateInCurrentTarget,
            outcome);
        Assert.Empty(consent.Prompts);
        Assert.Empty(launcher.Destinations);
    }

    [Theory]
    [InlineData("https://example.com/private/path?secret=value", "example.com")]
    [InlineData("mailto:operator@example.com?body=private", "mailto")]
    public async Task UserExternalNavigationRequiresOneSanitizedConfirmation(
        string destination,
        string expectedDisplayTarget)
    {
        var consent = new RecordingConsent(confirmed: true);
        var launcher = new RecordingLauncher();
        var coordinator = CreateCoordinator(consent, launcher);
        var uri = new Uri(destination);

        var outcome = await coordinator.HandleAsync(
            uri,
            isUserInitiated: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(TargetExternalNavigationOutcome.OpenedExternally, outcome);
        var prompt = Assert.Single(consent.Prompts);
        Assert.Equal(expectedDisplayTarget, prompt.DisplayTarget);
        Assert.DoesNotContain("private", prompt.DisplayTarget, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", prompt.DisplayTarget, StringComparison.Ordinal);
        Assert.Equal(uri, Assert.Single(launcher.Destinations));
    }

    [Fact]
    public async Task DeclineOrMissingUserGestureNeverLaunchesAnything()
    {
        var consent = new RecordingConsent(confirmed: false);
        var launcher = new RecordingLauncher();
        var coordinator = CreateCoordinator(consent, launcher);
        var destination = new Uri("https://example.com/docs");

        var declined = await coordinator.HandleAsync(
            destination,
            isUserInitiated: true,
            TestContext.Current.CancellationToken);
        var scripted = await coordinator.HandleAsync(
            destination,
            isUserInitiated: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(TargetExternalNavigationOutcome.Declined, declined);
        Assert.Equal(TargetExternalNavigationOutcome.Blocked, scripted);
        Assert.Single(consent.Prompts);
        Assert.Empty(launcher.Destinations);
    }

    private static TargetExternalNavigationCoordinator CreateCoordinator(
        ITargetExternalNavigationConsent consent,
        ITargetExternalUriLauncher launcher)
    {
        return new TargetExternalNavigationCoordinator(
            new TargetContentSecurityPolicy(Binding),
            consent,
            launcher);
    }

    private sealed class RecordingConsent : ITargetExternalNavigationConsent
    {
        private readonly bool _confirmed;

        public RecordingConsent(bool confirmed)
        {
            _confirmed = confirmed;
        }

        public List<TargetExternalNavigationPrompt> Prompts { get; } = [];

        public ValueTask<bool> ConfirmAsync(
            TargetExternalNavigationPrompt prompt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Prompts.Add(prompt);
            return ValueTask.FromResult(_confirmed);
        }
    }

    private sealed class RecordingLauncher : ITargetExternalUriLauncher
    {
        public List<Uri> Destinations { get; } = [];

        public ValueTask OpenAsync(
            Uri destination,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Destinations.Add(destination);
            return ValueTask.CompletedTask;
        }
    }
}
