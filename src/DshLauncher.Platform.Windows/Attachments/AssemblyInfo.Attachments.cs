using System.Runtime.CompilerServices;

// D14：Windows 暂存适配器的原生捕获铸造口（NativePasteGesture.MintFromNativePaste）与
// 桥不可见的 CaptureNative 需要被 Windows 专项测试直接驱动；测试程序集与生产程序集同一仓库、
// 同一构建图，因此只对这一个测试程序集开放 internal 可见性。
// 桥/页面侧拿不到 C# 类型系统，公开表面仍然只接受不透明 id（见 WindowsStagingBoundaryTests）。
[assembly: InternalsVisibleTo("DshLauncher.Platform.Windows.Tests")]
