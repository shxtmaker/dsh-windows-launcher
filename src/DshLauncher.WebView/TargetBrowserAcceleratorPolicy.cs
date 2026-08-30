namespace DshLauncher.WebView;

[Flags]
public enum TargetAcceleratorModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
}

public enum TargetBrowserAcceleratorDisposition
{
    PassThrough,
    AllowBrowserFeature,
    Block,
}

public static class TargetBrowserAcceleratorPolicy
{
    private const int VirtualKeyF3 = 0x72;
    private const int VirtualKeyF5 = 0x74;
    private const int VirtualKeyF12 = 0x7B;
    private const int VirtualKeyBrowserBack = 0xA6;
    private const int VirtualKeyBrowserForward = 0xA7;
    private const int VirtualKeyBrowserHome = 0xAC;
    private const int VirtualKeyAdd = 0x6B;
    private const int VirtualKeySubtract = 0x6D;
    private const int VirtualKeyOemPlus = 0xBB;
    private const int VirtualKeyOemMinus = 0xBD;

    public static TargetBrowserAcceleratorDisposition Classify(
        int virtualKey,
        TargetAcceleratorModifiers modifiers)
    {
        var control = modifiers.HasFlag(TargetAcceleratorModifiers.Control);
        var alt = modifiers.HasFlag(TargetAcceleratorModifiers.Alt);
        var shift = modifiers.HasFlag(TargetAcceleratorModifiers.Shift);

        if (virtualKey is VirtualKeyF12 or
            VirtualKeyBrowserBack or
            VirtualKeyBrowserForward or
            VirtualKeyBrowserHome ||
            (alt && virtualKey is 'D' or 0x24 or 0x25 or 0x27) ||
            (control && IsForbiddenControlKey(virtualKey, shift)))
        {
            return TargetBrowserAcceleratorDisposition.Block;
        }

        if (virtualKey is VirtualKeyF3 or VirtualKeyF5 ||
            (control && !alt && IsAllowedControlKey(virtualKey)))
        {
            return TargetBrowserAcceleratorDisposition.AllowBrowserFeature;
        }

        return TargetBrowserAcceleratorDisposition.PassThrough;
    }

    private static bool IsForbiddenControlKey(int virtualKey, bool shift)
    {
        if (shift && virtualKey is 'C' or 'I' or 'J')
        {
            return true;
        }

        return virtualKey is 'D' or 'E' or 'H' or 'J' or 'K' or 'L' or
            'N' or 'O' or 'P' or 'S' or 'T' or 'U' or 0x09 or 0x2E;
    }

    private static bool IsAllowedControlKey(int virtualKey)
    {
        return virtualKey is 'F' or 'R' or 'G' or '0' or 0x60 or
            VirtualKeyAdd or VirtualKeySubtract or
            VirtualKeyOemPlus or VirtualKeyOemMinus;
    }
}
