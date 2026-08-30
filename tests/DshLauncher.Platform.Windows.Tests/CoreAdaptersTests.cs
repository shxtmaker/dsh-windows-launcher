using DshLauncher.Core;
using DshLauncher.Platform.Windows;
using Xunit;

namespace DshLauncher.Platform.Windows.Tests;

[Trait("triggerTags", "VFY-04,VFY-05,VFY-07")]
public sealed class CoreAdaptersTests
{
    [Theory]
    [MemberData(nameof(NetworkCategoryCases))]
    public void NetworkCategoriesFailClosed(
        NetworkCategory[] input,
        NetworkCategory expected)
    {
        Assert.Equal(expected, WindowsNetworkPort.AggregateCategories(input));
    }

    public static TheoryData<NetworkCategory[], NetworkCategory> NetworkCategoryCases => new()
    {
        { [], NetworkCategory.Unknown },
        { [NetworkCategory.Unknown], NetworkCategory.Unknown },
        { [NetworkCategory.Private], NetworkCategory.Private },
        { [NetworkCategory.Private, NetworkCategory.Unknown], NetworkCategory.Unknown },
        { [NetworkCategory.Private, NetworkCategory.Public], NetworkCategory.Public },
        { [NetworkCategory.Public], NetworkCategory.Public },
        { [NetworkCategory.Public, NetworkCategory.Unknown], NetworkCategory.Public },
    };

    [Fact]
    public void GuidGeneratorNeverReturnsTheEmptyIdentity()
    {
        var generator = new GuidIdGenerator();

        Assert.NotEqual(Guid.Empty, generator.NewId());
        Assert.NotEqual(generator.NewId(), generator.NewId());
    }

    [Fact]
    public void SystemClockReturnsUtc()
    {
        Assert.Equal(TimeSpan.Zero, new SystemClock().UtcNow.Offset);
    }
}
