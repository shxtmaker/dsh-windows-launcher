using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

using Color = System.Windows.Media.Color;

namespace DshLauncher.Desktop;

/// <summary>
/// The shared minimal-tech palette. Every surface — Theme.xaml styles,
/// data templates and code-behind state colors — draws from these frozen
/// brushes so the two windows never drift apart.
/// </summary>
public static class UiPalette
{
    private static readonly Color WindowColor = Color.FromRgb(0x0B, 0x12, 0x20);
    private static readonly Color SurfaceColor = Color.FromRgb(0x0F, 0x1A, 0x2E);
    private static readonly Color SurfaceAltColor = Color.FromRgb(0x16, 0x23, 0x3C);
    private static readonly Color HoverColor = Color.FromRgb(0x1D, 0x2C, 0x49);
    private static readonly Color BorderColor = Color.FromRgb(0x1E, 0x2C, 0x4A);
    private static readonly Color BorderStrongColor = Color.FromRgb(0x2E, 0x43, 0x70);

    private static readonly Color TextPrimaryColor = Color.FromRgb(0xE9, 0xF0, 0xFB);
    private static readonly Color TextSecondaryColor = Color.FromRgb(0x9D, 0xAE, 0xC9);
    private static readonly Color TextMutedColor = Color.FromRgb(0x64, 0x76, 0x9A);

    private static readonly Color AccentColor = Color.FromRgb(0x22, 0xD3, 0xEE);
    private static readonly Color AccentHoverColor = Color.FromRgb(0x67, 0xE8, 0xF9);
    private static readonly Color AccentContrastColor = Color.FromRgb(0x05, 0x2A, 0x33);
    private static readonly Color AccentSoftColor = Color.FromRgb(0x11, 0x32, 0x3E);
    private static readonly Color AccentBorderColor = Color.FromRgb(0x1F, 0x5D, 0x6E);

    private static readonly Color SuccessColor = Color.FromRgb(0x34, 0xD3, 0x99);
    private static readonly Color SuccessSoftColor = Color.FromRgb(0x14, 0x2B, 0x22);
    private static readonly Color SuccessBorderColor = Color.FromRgb(0x24, 0x5A, 0x44);

    private static readonly Color WarningColor = Color.FromRgb(0xFB, 0xBF, 0x24);
    private static readonly Color WarningSoftColor = Color.FromRgb(0x33, 0x29, 0x12);
    private static readonly Color WarningBorderColor = Color.FromRgb(0x6B, 0x55, 0x18);

    private static readonly Color DangerColor = Color.FromRgb(0xF8, 0x71, 0x71);
    private static readonly Color DangerSoftColor = Color.FromRgb(0x2F, 0x1B, 0x1B);
    private static readonly Color DangerBorderColor = Color.FromRgb(0x6B, 0x35, 0x35);

    private static readonly Color NeutralColor = Color.FromRgb(0x7C, 0x8C, 0xA8);
    private static readonly Color NeutralSoftColor = Color.FromRgb(0x1B, 0x26, 0x37);
    private static readonly Color NeutralBorderColor = Color.FromRgb(0x33, 0x42, 0x5E);

    public static readonly SolidColorBrush WindowBrush = Frozen(WindowColor);
    public static readonly SolidColorBrush SurfaceBrush = Frozen(SurfaceColor);
    public static readonly SolidColorBrush SurfaceAltBrush = Frozen(SurfaceAltColor);
    public static readonly SolidColorBrush HoverBrush = Frozen(HoverColor);
    public static readonly SolidColorBrush BorderBrush = Frozen(BorderColor);
    public static readonly SolidColorBrush BorderStrongBrush = Frozen(BorderStrongColor);

    public static readonly SolidColorBrush TextPrimaryBrush = Frozen(TextPrimaryColor);
    public static readonly SolidColorBrush TextSecondaryBrush = Frozen(TextSecondaryColor);
    public static readonly SolidColorBrush TextMutedBrush = Frozen(TextMutedColor);

    public static readonly SolidColorBrush AccentBrush = Frozen(AccentColor);
    public static readonly SolidColorBrush AccentHoverBrush = Frozen(AccentHoverColor);
    public static readonly SolidColorBrush AccentContrastBrush = Frozen(AccentContrastColor);
    public static readonly SolidColorBrush AccentSoftBrush = Frozen(AccentSoftColor);
    public static readonly SolidColorBrush AccentBorderBrush = Frozen(AccentBorderColor);

    public static readonly SolidColorBrush SuccessBrush = Frozen(SuccessColor);
    public static readonly SolidColorBrush SuccessSoftBrush = Frozen(SuccessSoftColor);
    public static readonly SolidColorBrush SuccessBorderBrush = Frozen(SuccessBorderColor);

    public static readonly SolidColorBrush WarningBrush = Frozen(WarningColor);
    public static readonly SolidColorBrush WarningSoftBrush = Frozen(WarningSoftColor);
    public static readonly SolidColorBrush WarningBorderBrush = Frozen(WarningBorderColor);

    public static readonly SolidColorBrush DangerBrush = Frozen(DangerColor);
    public static readonly SolidColorBrush DangerSoftBrush = Frozen(DangerSoftColor);
    public static readonly SolidColorBrush DangerBorderBrush = Frozen(DangerBorderColor);

    public static readonly SolidColorBrush NeutralBrush = Frozen(NeutralColor);
    public static readonly SolidColorBrush NeutralSoftBrush = Frozen(NeutralSoftColor);
    public static readonly SolidColorBrush NeutralBorderBrush = Frozen(NeutralBorderColor);

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Shared window chrome effects. The attached <c>DarkTitleBar</c> flag opts
/// a window into the immersive dark title bar when the host's DWM supports
/// it; older systems silently keep the classic chrome.
/// </summary>
public static class UiChrome
{
    public static readonly DependencyProperty DarkTitleBarProperty = DependencyProperty.RegisterAttached(
        "DarkTitleBar", typeof(bool), typeof(UiChrome), new PropertyMetadata(false, OnDarkTitleBarChanged));

    public static void SetDarkTitleBar(Window window, bool value) => window.SetValue(DarkTitleBarProperty, value);

    public static bool GetDarkTitleBar(Window window) => (bool)window.GetValue(DarkTitleBarProperty);

    private static void OnDarkTitleBarChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Window window && (bool)e.NewValue)
        {
            window.SourceInitialized += static (windowSender, _) => ApplyDarkTitleBar((Window)windowSender!);
        }
    }

    private static void ApplyDarkTitleBar(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            _ = DwmSetWindowAttribute(handle, DarkModeAttribute, ref EnabledValue, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // No DWM: keep the classic title bar instead of failing startup.
        }
    }

    // DWMWA_USE_IMMERSIVE_DARK_MODE
    private const int DarkModeAttribute = 20;
    private static int EnabledValue = 1;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
