using System.Reflection;
using DshLauncher.Core.Attachments;
using DshLauncher.Platform.Windows;
using DshLauncher.Platform.Windows.Attachments;
using Xunit;

namespace DshLauncher.Platform.Windows.Tests.Attachments;

/// <summary>
/// D14 边界用例（WP-13）：用反射钉住"没有任意路径读取入口"这一结构性事实。
/// 这些断言不依赖 Win32 行为，但按仓库门禁仍归 Windows 专项程序集，
/// 本轮在 Linux 上<b>只枚举不执行</b>。
/// </summary>
[Trait("triggerTags", "VFY-06")]
public sealed class WindowsStagingBoundaryTests
{
    [Fact]
    public void TheAdapterPublicSurfaceOnlyAcceptsOpaqueIds()
    {
        var stringParameters = typeof(WindowsAttachmentStagingAdapter)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetParameters())
            .Where(parameter => parameter.ParameterType == typeof(string))
            .Select(parameter => parameter.Name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["batchId", "captureId", "snapshotId"], stringParameters);
        Assert.DoesNotContain(
            typeof(WindowsAttachmentStagingAdapter).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static),
            member => member.Name.Contains("Path", StringComparison.Ordinal)
                && member is MethodInfo or PropertyInfo or ConstructorInfo);
    }

    [Fact]
    public void TheOnlyWayToRegisterACandidateRequiresANativeGestureToken()
    {
        var register = typeof(WindowsAttachmentStagingAdapter).GetMethod(
            "RegisterNativeCapture",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(register);
        Assert.True(register.IsAssembly, "登记候选必须是 internal：桥/外部程序集不能直接调用");
        var parameters = register.GetParameters();
        Assert.Equal(typeof(NativePasteGesture), parameters[0].ParameterType);
        Assert.Equal(typeof(string), parameters[1].ParameterType);

        // 手势令牌只能在本程序集内铸造。
        var gestureFactory = typeof(NativePasteGesture).GetMethod(
            "MintFromNativePaste",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(gestureFactory);
        Assert.True(gestureFactory.IsAssembly);
        Assert.Empty(typeof(NativePasteGesture).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void TheNativeCaptureTypeExposesNoPathAccessor()
    {
        Assert.DoesNotContain(
            typeof(WindowsNativeDropCapture).GetMembers(BindingFlags.Public | BindingFlags.Instance),
            member => member.Name.Contains("Path", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(INativeStagingCapture).GetMembers(),
            member => member.Name.Contains("Path", StringComparison.Ordinal));
    }

    [Fact]
    public void TheBridgeReceiptAndCleanupOutcomeCarryNoLocalPath()
    {
        StagingBoundaryAssertions.NoPropertyNameContainsPath(typeof(StagingCaptureReceipt));
        StagingBoundaryAssertions.NoPropertyNameContainsPath(typeof(StagingCleanupOutcome));
    }

    [Fact]
    public void TheCaptureResultKeepsTheStagedPathOffTheBridgeSurface()
    {
        // 完整结果（含暂存路径）只经 internal 的 CaptureNative 交给原生传输半区。
        var native = typeof(WindowsAttachmentStagingAdapter).GetMethod(
            "CaptureNative",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(native);
        Assert.True(native.IsAssembly);
        Assert.Equal(typeof(StagingCaptureResult), native.ReturnType);

        var bridge = typeof(WindowsAttachmentStagingAdapter).GetMethod(
            nameof(WindowsAttachmentStagingAdapter.CaptureForBridge));
        Assert.NotNull(bridge);
        Assert.True(bridge.IsPublic);
        Assert.Equal(typeof(StagingCaptureReceipt), bridge.ReturnType);
    }
}

/// <summary>反射断言助手。</summary>
internal static class StagingBoundaryAssertions
{
    /// <summary>类型的公开属性名里不得出现 Path（回执不得携带本机路径）。</summary>
    public static void NoPropertyNameContainsPath(Type type) =>
        Assert.DoesNotContain(
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => property.Name.Contains("Path", StringComparison.Ordinal));
}
