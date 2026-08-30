using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace DshLauncher.Desktop.Dialogs;

public partial class PairTargetDialog : Window
{
    private char[]? _pastedClipboardText;
    private char[]? _pairingLink;
    private long _editRevision;
    private long _acceptedPasteRevision = -1;

    public PairTargetDialog()
    {
        InitializeComponent();
        Closed += (_, _) => PairingLinkBox.Clear();
        ContentRendered += (_, _) => PairingLinkBox.Focus();
        DataObject.AddPastingHandler(PairingLinkBox, OnPasting);
        PairingLinkBox.PasswordChanged += (_, _) => _editRevision++;
    }

    public char[]? TakePastedClipboardText() =>
        Interlocked.Exchange(ref _pastedClipboardText, null);

    public char[]? TakePairingLink() =>
        Interlocked.Exchange(ref _pairingLink, null);

    private void ConfirmClick(object sender, RoutedEventArgs e)
    {
        using var secure = PairingLinkBox.SecurePassword.Copy();
        if (secure.Length == 0)
        {
            return;
        }

        var buffer = new char[secure.Length];
        var pointer = Marshal.SecureStringToGlobalAllocUnicode(secure);
        try
        {
            Marshal.Copy(pointer, buffer, 0, buffer.Length);
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(pointer);
        }

        var isCurrentPaste = IsCurrentExplicitPaste(
            buffer,
            _pastedClipboardText,
            _editRevision,
            _acceptedPasteRevision);
        PairingLinkBox.Clear();
        if (!isCurrentPaste)
        {
            Array.Clear(buffer);
            PasteOnlyError.Visibility = Visibility.Visible;
            return;
        }

        _pairingLink = buffer;
        DialogResult = true;
    }

    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText, autoConvert: true) ||
            e.DataObject.GetData(DataFormats.UnicodeText, autoConvert: true) is not string text ||
            text.Length == 0)
        {
            return;
        }

        var replacement = text.ToCharArray();
        var previous = Interlocked.Exchange(ref _pastedClipboardText, replacement);
        if (previous is not null)
        {
            Array.Clear(previous);
        }

        var revisionBeforePaste = _editRevision;
        _acceptedPasteRevision = -1;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () => CaptureCompletedPaste(replacement, revisionBeforePaste));
    }

    private void CaptureCompletedPaste(char[] captured, long revisionBeforePaste)
    {
        if (!ReferenceEquals(_pastedClipboardText, captured) ||
            _editRevision <= revisionBeforePaste)
        {
            return;
        }

        using var secure = PairingLinkBox.SecurePassword.Copy();
        var entered = new char[secure.Length];
        var pointer = Marshal.SecureStringToGlobalAllocUnicode(secure);
        try
        {
            Marshal.Copy(pointer, entered, 0, entered.Length);
            if (IsCurrentExplicitPaste(
                    entered,
                    captured,
                    _editRevision,
                    _editRevision))
            {
                _acceptedPasteRevision = _editRevision;
                PasteOnlyError.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(pointer);
            Array.Clear(entered);
        }
    }

    internal static bool IsCurrentExplicitPaste(
        ReadOnlySpan<char> entered,
        char[]? captured,
        long currentRevision,
        long acceptedPasteRevision)
    {
        if (captured is null ||
            acceptedPasteRevision < 0 ||
            currentRevision != acceptedPasteRevision ||
            entered.Length != captured.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            MemoryMarshal.AsBytes(entered),
            MemoryMarshal.AsBytes(captured.AsSpan()));
    }
}
