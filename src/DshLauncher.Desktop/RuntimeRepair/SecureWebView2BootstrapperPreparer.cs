using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace DshLauncher.Desktop.RuntimeRepair;

internal sealed class SecureWebView2BootstrapperPreparer
    : IWebView2BootstrapperPreparer
{
    private readonly WebView2BootstrapperTrustPolicy _policy;
    private readonly IWebView2BootstrapperPathGuard _pathGuard;
    private readonly IWebView2AuthenticodeVerifier _authenticode;
    private readonly Func<string> _temporaryRootProvider;

    public SecureWebView2BootstrapperPreparer(
        WebView2BootstrapperTrustPolicy policy,
        IWebView2BootstrapperPathGuard pathGuard,
        IWebView2AuthenticodeVerifier authenticode,
        Func<string> temporaryRootProvider)
    {
        ArgumentNullException.ThrowIfNull(pathGuard);
        ArgumentNullException.ThrowIfNull(authenticode);
        ArgumentNullException.ThrowIfNull(temporaryRootProvider);

        _policy = policy;
        _pathGuard = pathGuard;
        _authenticode = authenticode;
        _temporaryRootProvider = temporaryRootProvider;
    }

    public async ValueTask<WebView2BootstrapperPreparation> PrepareAsync(
        string sourcePath,
        string expectedDirectory,
        CancellationToken cancellationToken)
    {
        FileStream source;
        try
        {
            source = _pathGuard.OpenExpectedRegularFile(
                sourcePath,
                expectedDirectory);
        }
        catch (FileNotFoundException)
        {
            return WebView2BootstrapperPreparation.Rejected(
                WebView2BootstrapperPreparationStatus.Missing);
        }
        catch (DirectoryNotFoundException)
        {
            return WebView2BootstrapperPreparation.Rejected(
                WebView2BootstrapperPreparationStatus.Missing);
        }
        catch (UnsafeWebView2BootstrapperPathException)
        {
            return WebView2BootstrapperPreparation.Rejected(
                WebView2BootstrapperPreparationStatus.UnsafePath);
        }
        catch (Exception)
        {
            return WebView2BootstrapperPreparation.Rejected(
                WebView2BootstrapperPreparationStatus.Failed);
        }

        await using (source.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceHash = await SHA256.HashDataAsync(
                source,
                cancellationToken).ConfigureAwait(false);
            if (!MatchesExpectedHash(sourceHash))
            {
                return WebView2BootstrapperPreparation.Rejected(
                    WebView2BootstrapperPreparationStatus.HashMismatch);
            }

            source.Position = 0;
            return await StageAndInspectAsync(
                source,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<WebView2BootstrapperPreparation> StageAndInspectAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        string? stagingDirectory = null;
        string? stagedPath = null;
        SafeFileHandle? directoryLock = null;
        FileStream? fileLock = null;
        var directoryWasLocked = false;
        try
        {
            var temporaryRoot = Path.GetFullPath(_temporaryRootProvider());
            Directory.CreateDirectory(temporaryRoot);
            stagingDirectory = Path.Combine(
                temporaryRoot,
                $"DshLauncher.WebView2Repair.{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDirectory);

            stagedPath = Path.Combine(
                stagingDirectory,
                WebView2RuntimeDependency.BootstrapperFileName);
            await using (var writer = new FileStream(
                             stagedPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous |
                             FileOptions.SequentialScan |
                             FileOptions.WriteThrough))
            {
                await source.CopyToAsync(
                    writer,
                    bufferSize: 64 * 1024,
                    cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                writer.Flush(flushToDisk: true);
            }

            fileLock = new FileStream(
                stagedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            var stagedHash = await SHA256.HashDataAsync(
                fileLock,
                cancellationToken).ConfigureAwait(false);
            var stagedHashMatches = MatchesExpectedHash(stagedHash);
            var evidence = default(WebView2AuthenticodeEvidence);
            if (stagedHashMatches)
            {
                fileLock.Position = 0;
                evidence = _authenticode.Inspect(
                    stagedPath,
                    fileLock.SafeFileHandle);
            }

            directoryLock = _pathGuard.LockCreatedDirectory(stagingDirectory);
            directoryWasLocked = true;
            var initialFileLock = fileLock;
            try
            {
                fileLock = _pathGuard.OpenExpectedRegularFile(
                    stagedPath,
                    stagingDirectory);
            }
            finally
            {
                await initialFileLock.DisposeAsync().ConfigureAwait(false);
                if (ReferenceEquals(fileLock, initialFileLock))
                {
                    fileLock = null;
                }
            }

            var finalHash = await SHA256.HashDataAsync(
                fileLock ?? throw new InvalidOperationException(
                    "The staged WebView2 bootstrapper lock was not acquired."),
                cancellationToken).ConfigureAwait(false);
            if (!stagedHashMatches || !MatchesExpectedHash(finalHash))
            {
                return WebView2BootstrapperPreparation.Rejected(
                    WebView2BootstrapperPreparationStatus.HashMismatch);
            }

            var rejectedStatus = ClassifyEvidence(evidence);
            if (rejectedStatus is { } status)
            {
                return WebView2BootstrapperPreparation.Rejected(status);
            }

            var artifact = new LockedWebView2BootstrapperArtifact(
                stagedPath,
                fileLock,
                directoryLock);
            fileLock = null;
            directoryLock = null;
            return WebView2BootstrapperPreparation.Prepared(artifact);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnsafeWebView2BootstrapperPathException)
        {
            return WebView2BootstrapperPreparation.Rejected(
                WebView2BootstrapperPreparationStatus.UnsafePath);
        }
        catch (Exception)
        {
            return WebView2BootstrapperPreparation.Rejected(
                WebView2BootstrapperPreparationStatus.Failed);
        }
        finally
        {
            if (fileLock is not null)
            {
                await fileLock.DisposeAsync().ConfigureAwait(false);
            }

            if (stagingDirectory is not null)
            {
                if (directoryWasLocked)
                {
                    TryDeleteStagedFile(stagedPath);
                    directoryLock?.Dispose();
                    directoryLock = null;
                    TryDeleteStagingDirectory(stagingDirectory);
                }
                else
                {
                    TryDeleteEmptyStagingDirectory(stagingDirectory);
                }
            }

            directoryLock?.Dispose();
        }
    }

    private bool MatchesExpectedHash(ReadOnlySpan<byte> hash) =>
        string.Equals(
            Convert.ToHexString(hash),
            _policy.Sha256,
            StringComparison.Ordinal);

    private static WebView2BootstrapperPreparationStatus? ClassifyEvidence(
        WebView2AuthenticodeEvidence evidence)
    {
        if (!evidence.IsSignatureValid)
        {
            return WebView2BootstrapperPreparationStatus.InvalidSignature;
        }

        if (!evidence.IsMicrosoftSigner)
        {
            return WebView2BootstrapperPreparationStatus.UnexpectedSigner;
        }

        return evidence.HasTrustedTimestamp
            ? null
            : WebView2BootstrapperPreparationStatus.MissingTrustedTimestamp;
    }

    private static void TryDeleteStagedFile(string? stagedPath)
    {
        try
        {
            if (stagedPath is not null)
            {
                File.Delete(stagedPath);
            }
        }
        catch (Exception)
        {
        }
    }

    private static void TryDeleteStagingDirectory(string stagingDirectory)
    {
        try
        {
            Directory.Delete(stagingDirectory);
        }
        catch (Exception)
        {
        }
    }

    private static void TryDeleteEmptyStagingDirectory(string stagingDirectory)
    {
        try
        {
            if ((File.GetAttributes(stagingDirectory) & FileAttributes.ReparsePoint) == 0)
            {
                Directory.Delete(stagingDirectory);
            }
        }
        catch (Exception)
        {
        }
    }
}
