using Microsoft.Win32.SafeHandles;
using System.IO;

namespace DshLauncher.Desktop.RuntimeRepair;

internal enum WebView2BootstrapperPreparationStatus
{
    Prepared,
    Missing,
    UnsafePath,
    HashMismatch,
    InvalidSignature,
    UnexpectedSigner,
    MissingTrustedTimestamp,
    Failed,
}

internal readonly record struct WebView2AuthenticodeEvidence(
    bool IsSignatureValid,
    bool IsMicrosoftSigner,
    bool HasTrustedTimestamp);

internal readonly record struct AuthenticodeSignerEvidence(
    bool IsSignatureValid,
    string? Subject,
    string? SimpleName,
    string? Thumbprint,
    bool HasTrustedTimestamp);

internal interface IWebView2AuthenticodeVerifier
{
    WebView2AuthenticodeEvidence Inspect(
        string path,
        SafeFileHandle fileHandle);
}

internal interface IWebView2BootstrapperPathGuard
{
    FileStream OpenExpectedRegularFile(string path, string expectedDirectory);

    SafeFileHandle LockCreatedDirectory(string path);
}

internal interface IWebView2BootstrapperPreparer
{
    ValueTask<WebView2BootstrapperPreparation> PrepareAsync(
        string sourcePath,
        string expectedDirectory,
        CancellationToken cancellationToken);
}

internal interface IWebView2BootstrapperProcessRunner
{
    ValueTask<WebView2BootstrapperRunResult> RunAsync(
        string executablePath,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class WebView2BootstrapperPreparation : IAsyncDisposable
{
    private WebView2BootstrapperPreparation(
        WebView2BootstrapperPreparationStatus status,
        LockedWebView2BootstrapperArtifact? artifact)
    {
        Status = status;
        Artifact = artifact;
    }

    public WebView2BootstrapperPreparationStatus Status { get; }

    public LockedWebView2BootstrapperArtifact? Artifact { get; }

    public static WebView2BootstrapperPreparation Prepared(
        LockedWebView2BootstrapperArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return new WebView2BootstrapperPreparation(
            WebView2BootstrapperPreparationStatus.Prepared,
            artifact);
    }

    public static WebView2BootstrapperPreparation Rejected(
        WebView2BootstrapperPreparationStatus status)
    {
        if (status == WebView2BootstrapperPreparationStatus.Prepared)
        {
            throw new ArgumentException(
                "A prepared result requires a locked artifact.",
                nameof(status));
        }

        return new WebView2BootstrapperPreparation(status, artifact: null);
    }

    public ValueTask DisposeAsync() =>
        Artifact is null ? ValueTask.CompletedTask : Artifact.DisposeAsync();
}

internal sealed class LockedWebView2BootstrapperArtifact : IAsyncDisposable
{
    private readonly FileStream _fileLock;
    private readonly SafeFileHandle _directoryLock;
    private readonly string _directoryPath;
    private bool _disposed;

    public LockedWebView2BootstrapperArtifact(
        string executablePath,
        FileStream fileLock,
        SafeFileHandle directoryLock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(fileLock);
        ArgumentNullException.ThrowIfNull(directoryLock);

        ExecutablePath = executablePath;
        _directoryPath = Path.GetDirectoryName(executablePath) ??
            throw new ArgumentException(
                "The staged executable path has no directory.",
                nameof(executablePath));
        _fileLock = fileLock;
        _directoryLock = directoryLock;
    }

    public string ExecutablePath { get; }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _fileLock.Dispose();
        TryDeleteFile(ExecutablePath);
        _directoryLock.Dispose();
        TryDeleteDirectory(_directoryPath);
        return ValueTask.CompletedTask;
    }

    private static void TryDeleteFile(string executablePath)
    {
        try
        {
            File.Delete(executablePath);
        }
        catch (Exception)
        {
        }
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        try
        {
            Directory.Delete(directoryPath);
        }
        catch (Exception)
        {
        }
    }
}

internal sealed class UnsafeWebView2BootstrapperPathException : IOException
{
    public UnsafeWebView2BootstrapperPathException(string message)
        : base(message)
    {
    }

    public UnsafeWebView2BootstrapperPathException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}
