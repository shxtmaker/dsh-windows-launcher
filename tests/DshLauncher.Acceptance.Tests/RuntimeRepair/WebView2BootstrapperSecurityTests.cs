using System.IO;
using System.Security.Cryptography;
using DshLauncher.Desktop.RuntimeRepair;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace DshLauncher.Acceptance.Tests.RuntimeRepair;

public sealed class WebView2BootstrapperSecurityTests
{
    private static readonly WebView2AuthenticodeEvidence TrustedMicrosoftEvidence = new(
        IsSignatureValid: true,
        IsMicrosoftSigner: true,
        HasTrustedTimestamp: true);

    [Fact]
    [Trait("triggerTags", "VFY-07,RS-14")]
    public void EmbeddedPolicyUsesTheFrozenBootstrapperHash()
    {
        var policy = WebView2BootstrapperTrustPolicy.LoadEmbedded();

        Assert.Equal(
            "94314D8B20C8A370DF81C5CC3D8D7A3E23FE5DE14EF5E988229FF3208E449146",
            policy.Sha256);
    }

    [Fact]
    [Trait("triggerTags", "VFY-07,RS-14")]
    public async Task HashMismatchIsRejectedBeforeAuthenticodeInspection()
    {
        using var source = new TemporaryDirectory();
        var sourcePath = source.WriteBootstrapper([1, 2, 3, 4]);
        var authenticode = new ScriptedAuthenticodeVerifier(TrustedMicrosoftEvidence);
        var preparer = CreatePreparer(
            new WebView2BootstrapperTrustPolicy(new string('0', 64)),
            authenticode,
            source.Path);

        await using var result = await preparer.PrepareAsync(
            sourcePath,
            source.Path,
            TestContext.Current.CancellationToken);

        Assert.Equal(WebView2BootstrapperPreparationStatus.HashMismatch, result.Status);
        Assert.Null(result.Artifact);
        Assert.Equal(0, authenticode.InvocationCount);
    }

    [Theory]
    [Trait("triggerTags", "VFY-07,RS-14")]
    [InlineData(false, true, true, "InvalidSignature")]
    [InlineData(true, false, true, "UnexpectedSigner")]
    [InlineData(true, true, false, "MissingTrustedTimestamp")]
    public async Task AuthenticodeFailuresAreRejected(
        bool signatureValid,
        bool microsoftSigner,
        bool trustedTimestamp,
        string expectedStatus)
    {
        using var source = new TemporaryDirectory();
        var contents = new byte[] { 9, 8, 7, 6, 5 };
        var sourcePath = source.WriteBootstrapper(contents);
        var authenticode = new ScriptedAuthenticodeVerifier(
            new WebView2AuthenticodeEvidence(
                signatureValid,
                microsoftSigner,
                trustedTimestamp));
        var preparer = CreatePreparer(
            PolicyFor(contents),
            authenticode,
            source.Path);

        await using var result = await preparer.PrepareAsync(
            sourcePath,
            source.Path,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, result.Status.ToString());
        Assert.Null(result.Artifact);
        Assert.Equal(1, authenticode.InvocationCount);
        Assert.Empty(Directory.EnumerateDirectories(
            source.Path,
            "DshLauncher.WebView2Repair.*"));
    }

    [Fact]
    [Trait("triggerTags", "VFY-07,RS-14")]
    public async Task ReparseCandidateIsRejectedBeforeItCanBeReadOrCopied()
    {
        using var source = new TemporaryDirectory();
        var sourcePath = System.IO.Path.Combine(
            source.Path,
            WebView2RuntimeDependency.BootstrapperFileName);
        var authenticode = new ScriptedAuthenticodeVerifier(TrustedMicrosoftEvidence);
        var pathGuard = new RejectingPathGuard();
        var preparer = new SecureWebView2BootstrapperPreparer(
            new WebView2BootstrapperTrustPolicy(new string('0', 64)),
            pathGuard,
            authenticode,
            () => source.Path);

        await using var result = await preparer.PrepareAsync(
            sourcePath,
            source.Path,
            TestContext.Current.CancellationToken);

        Assert.Equal(WebView2BootstrapperPreparationStatus.UnsafePath, result.Status);
        Assert.Equal(1, pathGuard.InvocationCount);
        Assert.Equal(0, authenticode.InvocationCount);
    }

    [Fact]
    [Trait("triggerTags", "VFY-07,RS-14")]
    public async Task RunnerExecutesOnlyTheLockedStagedCopy()
    {
        using var source = new TemporaryDirectory();
        using var stageRoot = new TemporaryDirectory();
        var contents = new byte[] { 10, 20, 30, 40, 50, 60 };
        var sourcePath = source.WriteBootstrapper(contents);
        var preparer = CreatePreparer(
            PolicyFor(contents),
            new ScriptedAuthenticodeVerifier(TrustedMicrosoftEvidence),
            stageRoot.Path);
        var process = new LockObservingProcessRunner(sourcePath);
        var runner = new WebView2BootstrapperRunner(
            source.Path,
            TimeSpan.FromSeconds(10),
            preparer,
            process);

        var result = await runner.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WebView2BootstrapperRunStatus.Completed, result.Status);
        Assert.Equal(1, process.InvocationCount);
        Assert.NotNull(process.ExecutedPath);
        Assert.False(string.Equals(
            sourcePath,
            process.ExecutedPath,
            StringComparison.OrdinalIgnoreCase));
        Assert.True(process.WriteReplacementWasBlocked);
        Assert.True(process.DeleteReplacementWasBlocked);
        Assert.True(process.DirectoryReplacementWasBlocked);
        Assert.False(File.Exists(process.ExecutedPath));
        Assert.Equal(contents, File.ReadAllBytes(sourcePath));
        Assert.Empty(Directory.EnumerateDirectories(
            stageRoot.Path,
            "DshLauncher.WebView2Repair.*"));
    }

    [Fact]
    [Trait("triggerTags", "VFY-07,RS-14")]
    public async Task WindowsProcessCanStartWhileTheStagedPathIsLocked()
    {
        using var staging = new TemporaryDirectory();
        var executablePath = System.IO.Path.Combine(
            staging.Path,
            WebView2RuntimeDependency.BootstrapperFileName);
        File.Copy(
            System.IO.Path.Combine(Environment.SystemDirectory, "whoami.exe"),
            executablePath);
        var guard = new WindowsWebView2BootstrapperPathGuard();
        using var directoryLock = guard.LockCreatedDirectory(staging.Path);
        await using var fileLock = guard.OpenExpectedRegularFile(
            executablePath,
            staging.Path);
        var process = new WindowsWebView2BootstrapperProcessRunner();

        var result = await process.RunAsync(
            executablePath,
            staging.Path,
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.NotEqual(WebView2BootstrapperRunStatus.StartFailed, result.Status);
        Assert.NotEqual(WebView2BootstrapperRunStatus.TimedOut, result.Status);
    }

    [Fact]
    [Trait("triggerTags", "VFY-07,RS-14")]
    public async Task RunnerNeverStartsAnUntrustedCandidate()
    {
        using var source = new TemporaryDirectory();
        _ = source.WriteBootstrapper([1, 2, 3]);
        var process = new LockObservingProcessRunner(expectedSourcePath: null);
        var runner = new WebView2BootstrapperRunner(
            source.Path,
            TimeSpan.FromSeconds(10),
            new ScriptedPreparer(WebView2BootstrapperPreparationStatus.InvalidSignature),
            process);

        var result = await runner.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WebView2BootstrapperRunStatus.Untrusted, result.Status);
        Assert.Equal(0, process.InvocationCount);
    }

    private static SecureWebView2BootstrapperPreparer CreatePreparer(
        WebView2BootstrapperTrustPolicy policy,
        IWebView2AuthenticodeVerifier authenticode,
        string stageRoot) => new(
            policy,
            new WindowsWebView2BootstrapperPathGuard(),
            authenticode,
            () => stageRoot);

    private static WebView2BootstrapperTrustPolicy PolicyFor(byte[] contents) => new(
        Convert.ToHexString(SHA256.HashData(contents)));

    private sealed class ScriptedAuthenticodeVerifier : IWebView2AuthenticodeVerifier
    {
        private readonly WebView2AuthenticodeEvidence _evidence;

        public ScriptedAuthenticodeVerifier(WebView2AuthenticodeEvidence evidence)
        {
            _evidence = evidence;
        }

        public int InvocationCount { get; private set; }

        public WebView2AuthenticodeEvidence Inspect(
            string path,
            SafeFileHandle fileHandle)
        {
            InvocationCount++;
            return _evidence;
        }
    }

    private sealed class RejectingPathGuard : IWebView2BootstrapperPathGuard
    {
        public int InvocationCount { get; private set; }

        public FileStream OpenExpectedRegularFile(string path, string expectedDirectory)
        {
            InvocationCount++;
            throw new UnsafeWebView2BootstrapperPathException("reparse point");
        }

        public SafeFileHandle LockCreatedDirectory(string path) =>
            throw new InvalidOperationException("Staging must not start.");
    }

    private sealed class LockObservingProcessRunner : IWebView2BootstrapperProcessRunner
    {
        private readonly string? _expectedSourcePath;

        public LockObservingProcessRunner(string? expectedSourcePath)
        {
            _expectedSourcePath = expectedSourcePath;
        }

        public int InvocationCount { get; private set; }

        public string? ExecutedPath { get; private set; }

        public bool WriteReplacementWasBlocked { get; private set; }

        public bool DeleteReplacementWasBlocked { get; private set; }

        public bool DirectoryReplacementWasBlocked { get; private set; }

        public ValueTask<WebView2BootstrapperRunResult> RunAsync(
            string executablePath,
            string workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            ExecutedPath = executablePath;

            if (_expectedSourcePath is not null &&
                string.Equals(executablePath, _expectedSourcePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new Xunit.Sdk.XunitException("The source candidate was executed directly.");
            }

            try
            {
                using var replacement = new FileStream(
                    executablePath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException)
            {
                WriteReplacementWasBlocked = true;
            }

            try
            {
                File.Move(executablePath, executablePath + ".replaced");
            }
            catch (IOException)
            {
                DeleteReplacementWasBlocked = true;
            }

            var executableDirectory = System.IO.Path.GetDirectoryName(executablePath) ??
                throw new Xunit.Sdk.XunitException(
                    "The staged executable path has no directory.");
            try
            {
                Directory.Move(
                    executableDirectory,
                    executableDirectory + ".replaced");
            }
            catch (IOException)
            {
                DirectoryReplacementWasBlocked = true;
            }

            return ValueTask.FromResult(
                new WebView2BootstrapperRunResult(
                    WebView2BootstrapperRunStatus.Completed,
                    ExitCode: 0));
        }
    }

    private sealed class ScriptedPreparer : IWebView2BootstrapperPreparer
    {
        private readonly WebView2BootstrapperPreparationStatus _status;

        public ScriptedPreparer(WebView2BootstrapperPreparationStatus status)
        {
            _status = status;
        }

        public ValueTask<WebView2BootstrapperPreparation> PrepareAsync(
            string sourcePath,
            string expectedDirectory,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(WebView2BootstrapperPreparation.Rejected(_status));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"DshLauncher.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WriteBootstrapper(byte[] contents)
        {
            var path = System.IO.Path.Combine(
                Path,
                WebView2RuntimeDependency.BootstrapperFileName);
            File.WriteAllBytes(path, contents);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
