using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace DshLauncher.Desktop.RuntimeRepair;

internal sealed partial class WindowsWebView2AuthenticodeVerifier
    : IWebView2AuthenticodeVerifier
{
    private const uint WinTrustNoUi = 2;
    private const uint WinTrustFileChoice = 1;
    private const uint WinTrustStateActionVerify = 1;
    private const uint WinTrustStateActionClose = 2;
    private const uint WinTrustRevocationCheckNone = 0x00000010;
    private const uint WinTrustCacheOnlyUrlRetrieval = 0x00001000;
    private static readonly Guid GenericVerifyV2 = new(
        "00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public WebView2AuthenticodeEvidence Inspect(
        string path,
        SafeFileHandle fileHandle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(fileHandle);
        if (fileHandle.IsInvalid || fileHandle.IsClosed)
        {
            return default;
        }

        nint fileInformationBuffer = 0;
        var handleReferenceAdded = false;
        var trustData = default(WinTrustData);
        try
        {
            fileHandle.DangerousAddRef(ref handleReferenceAdded);
            var fileInformation = new WinTrustFileInfo
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = 0,
                FileHandle = fileHandle.DangerousGetHandle(),
                KnownSubject = 0,
            };
            fileInformationBuffer = Marshal.AllocHGlobal(
                Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(
                fileInformation,
                fileInformationBuffer,
                false);

            trustData = new WinTrustData
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WinTrustNoUi,
                UnionChoice = WinTrustFileChoice,
                FileInformation = fileInformationBuffer,
                StateAction = WinTrustStateActionVerify,
                ProviderFlags = WinTrustRevocationCheckNone |
                                WinTrustCacheOnlyUrlRetrieval,
            };

            var actionId = GenericVerifyV2;
            if (WinVerifyTrust(windowHandle: 0, ref actionId, ref trustData) != 0)
            {
                return default;
            }

            var providerData = WTHelperProvDataFromStateData(
                trustData.StateData);
            if (providerData == 0)
            {
                return new WebView2AuthenticodeEvidence(
                    IsSignatureValid: true,
                    IsMicrosoftSigner: false,
                    HasTrustedTimestamp: false);
            }

            var signerPointer = WTHelperGetProvSignerFromChain(
                providerData,
                signerIndex: 0,
                counterSigner: 0,
                counterSignerIndex: 0);
            if (signerPointer == 0)
            {
                return new WebView2AuthenticodeEvidence(
                    IsSignatureValid: true,
                    IsMicrosoftSigner: false,
                    HasTrustedTimestamp: false);
            }

            var signer = Marshal.PtrToStructure<CryptProviderSigner>(
                signerPointer);
            var isMicrosoftSigner = signer.Error == 0 &&
                                    IsMicrosoftCertificate(signer);
            var hasTrustedTimestamp = HasTrustedTimestamp(
                providerData,
                signer);
            return new WebView2AuthenticodeEvidence(
                IsSignatureValid: true,
                isMicrosoftSigner,
                hasTrustedTimestamp);
        }
        catch (Exception)
        {
            return default;
        }
        finally
        {
            if (trustData.StateData != 0)
            {
                trustData.StateAction = WinTrustStateActionClose;
                var actionId = GenericVerifyV2;
                _ = WinVerifyTrust(
                    windowHandle: 0,
                    ref actionId,
                    ref trustData);
            }

            if (fileInformationBuffer != 0)
            {
                Marshal.FreeHGlobal(fileInformationBuffer);
            }

            if (handleReferenceAdded)
            {
                fileHandle.DangerousRelease();
            }
        }
    }

    private static bool HasTrustedTimestamp(
        nint providerData,
        CryptProviderSigner signer)
    {
        if (signer.CounterSignerCount == 0)
        {
            return false;
        }

        var timestampSignerPointer = WTHelperGetProvSignerFromChain(
            providerData,
            signerIndex: 0,
            counterSigner: 1,
            counterSignerIndex: 0);
        if (timestampSignerPointer == 0)
        {
            return false;
        }

        var timestampSigner = Marshal.PtrToStructure<CryptProviderSigner>(
            timestampSignerPointer);
        return timestampSigner.Error == 0 &&
               timestampSigner.CertificateChainCount > 0 &&
               timestampSigner.CertificateChain != 0;
    }

    private static bool IsMicrosoftCertificate(CryptProviderSigner signer)
    {
        if (signer.CertificateChainCount == 0 || signer.CertificateChain == 0)
        {
            return false;
        }

        var providerCertificate =
            Marshal.PtrToStructure<CryptProviderCertificateHeader>(
                signer.CertificateChain);
        if (providerCertificate.CertificateContext == 0)
        {
            return false;
        }

        var certificateContext = Marshal.PtrToStructure<CertificateContext>(
            providerCertificate.CertificateContext);
        if (certificateContext.EncodedCertificate == 0 ||
            certificateContext.EncodedCertificateSize == 0 ||
            certificateContext.EncodedCertificateSize > 1024 * 1024)
        {
            return false;
        }

        var encoded = new byte[certificateContext.EncodedCertificateSize];
        Marshal.Copy(
            certificateContext.EncodedCertificate,
            encoded,
            startIndex: 0,
            encoded.Length);
        using var certificate = X509CertificateLoader.LoadCertificate(encoded);
        return string.Equals(
            certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
            "Microsoft Corporation",
            StringComparison.OrdinalIgnoreCase);
    }

    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
    private static partial int WinVerifyTrust(
        nint windowHandle,
        ref Guid actionId,
        ref WinTrustData trustData);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperProvDataFromStateData(nint stateData);

    [LibraryImport("wintrust.dll")]
    private static partial nint WTHelperGetProvSignerFromChain(
        nint providerData,
        uint signerIndex,
        int counterSigner,
        uint counterSignerIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint StructureSize;
        public nint FilePath;
        public nint FileHandle;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructureSize;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint FileInformation;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CryptProviderSigner
    {
        public readonly uint StructureSize;
        public readonly FileTime VerifyAsOf;
        public readonly uint CertificateChainCount;
        public readonly nint CertificateChain;
        public readonly uint SignerType;
        public readonly nint SignerInformation;
        public readonly uint Error;
        public readonly uint CounterSignerCount;
        public readonly nint CounterSigners;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CryptProviderCertificateHeader
    {
        public readonly uint StructureSize;
        public readonly nint CertificateContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CertificateContext
    {
        public readonly uint EncodingType;
        public readonly nint EncodedCertificate;
        public readonly uint EncodedCertificateSize;
        public readonly nint CertificateInformation;
        public readonly nint CertificateStore;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FileTime(uint LowDateTime, uint HighDateTime);
}
