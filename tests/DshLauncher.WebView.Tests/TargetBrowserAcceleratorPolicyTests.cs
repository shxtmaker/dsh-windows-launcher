using DshLauncher.WebView;
using Xunit;

namespace DshLauncher.WebView.Tests;

[Trait("triggerTags", "VFY-06")]
public sealed class TargetBrowserAcceleratorPolicyTests
{
    [Theory]
    [InlineData('F', TargetAcceleratorModifiers.Control)]
    [InlineData('G', TargetAcceleratorModifiers.Control)]
    [InlineData('R', TargetAcceleratorModifiers.Control)]
    [InlineData('0', TargetAcceleratorModifiers.Control)]
    [InlineData(0xBB, TargetAcceleratorModifiers.Control | TargetAcceleratorModifiers.Shift)]
    [InlineData(0xBD, TargetAcceleratorModifiers.Control)]
    [InlineData(0x72, TargetAcceleratorModifiers.None)]
    [InlineData(0x74, TargetAcceleratorModifiers.None)]
    public void FindZoomAndRefreshBrowserFeaturesRemainAvailable(
        int virtualKey,
        TargetAcceleratorModifiers modifiers)
    {
        Assert.Equal(
            TargetBrowserAcceleratorDisposition.AllowBrowserFeature,
            TargetBrowserAcceleratorPolicy.Classify(virtualKey, modifiers));
    }

    [Theory]
    [InlineData('P', TargetAcceleratorModifiers.Control)]
    [InlineData('S', TargetAcceleratorModifiers.Control)]
    [InlineData('L', TargetAcceleratorModifiers.Control)]
    [InlineData('T', TargetAcceleratorModifiers.Control)]
    [InlineData('U', TargetAcceleratorModifiers.Control)]
    [InlineData('I', TargetAcceleratorModifiers.Control | TargetAcceleratorModifiers.Shift)]
    [InlineData(0x7B, TargetAcceleratorModifiers.None)]
    [InlineData(0xA6, TargetAcceleratorModifiers.None)]
    [InlineData(0x25, TargetAcceleratorModifiers.Alt)]
    [InlineData(0x27, TargetAcceleratorModifiers.Alt)]
    public void BrowserChromeNavigationAndFileActionsAreBlocked(
        int virtualKey,
        TargetAcceleratorModifiers modifiers)
    {
        Assert.Equal(
            TargetBrowserAcceleratorDisposition.Block,
            TargetBrowserAcceleratorPolicy.Classify(virtualKey, modifiers));
    }

    [Theory]
    [InlineData('A', TargetAcceleratorModifiers.Control)]
    [InlineData('C', TargetAcceleratorModifiers.Control)]
    [InlineData('V', TargetAcceleratorModifiers.Control)]
    [InlineData('X', TargetAcceleratorModifiers.Control)]
    [InlineData('Z', TargetAcceleratorModifiers.Control)]
    [InlineData('W', TargetAcceleratorModifiers.Control)]
    public void EditingAndNativeWindowShortcutsPassThrough(
        int virtualKey,
        TargetAcceleratorModifiers modifiers)
    {
        Assert.Equal(
            TargetBrowserAcceleratorDisposition.PassThrough,
            TargetBrowserAcceleratorPolicy.Classify(virtualKey, modifiers));
    }
}
