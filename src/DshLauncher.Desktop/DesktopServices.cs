using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using DshLauncher.Core;
using DshLauncher.Desktop.Resources;
using DshLauncher.Desktop.ViewModels;
using DshLauncher.Platform.Windows;
using DshLauncher.WebView;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace DshLauncher.Desktop;

public sealed class DiagnosticExportService : IDiagnosticsExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly RollingDiagnosticLog _diagnosticLog;
    private readonly Func<Window?> _ownerProvider;
    private readonly Dispatcher _dispatcher;

    public DiagnosticExportService(
        RollingDiagnosticLog diagnosticLog,
        Func<Window?> ownerProvider,
        Dispatcher dispatcher)
    {
        _diagnosticLog = diagnosticLog ?? throw new ArgumentNullException(nameof(diagnosticLog));
        _ownerProvider = ownerProvider ?? throw new ArgumentNullException(nameof(ownerProvider));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public async ValueTask ExportAsync(
        TargetManagerSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var destination = await _dispatcher.InvokeAsync(
            SelectDestination,
            DispatcherPriority.Normal,
            cancellationToken);
        if (destination is null)
        {
            return;
        }

        var temporary = Path.Combine(
            Path.GetDirectoryName(destination) ?? throw new InvalidOperationException(),
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                await WriteJsonAsync(archive, "product.json", new
                {
                    name = LauncherBuildIdentity.Current.ProductName,
                    version = typeof(App).Assembly.GetName().Version?.ToString(),
                }, cancellationToken).ConfigureAwait(false);
                await WriteJsonAsync(archive, "os.json", new
                {
                    description = RuntimeInformation.OSDescription,
                    architecture = RuntimeInformation.OSArchitecture.ToString(),
                }, cancellationToken).ConfigureAwait(false);
                await WriteJsonAsync(archive, "runtime.json", new
                {
                    dotnet = Environment.Version.ToString(),
                    webView2 = TryGetWebView2Version(),
                }, cancellationToken).ConfigureAwait(false);
                await WriteReleaseConstantsAsync(archive, cancellationToken).ConfigureAwait(false);
                await WriteJsonAsync(archive, "target-status.json", new
                {
                    schemaVersion = 1,
                    catalogAvailability = snapshot.Availability.ToString(),
                    catalogSchemaVersion = snapshot.SchemaVersion,
                    targetCount = snapshot.Targets.Count,
                    targets = snapshot.Targets.Select(target => new
                    {
                        targetRef = CreateTargetReference(target.TargetId),
                        endpoint = target.Endpoint.Authority,
                        isDefault = target.IsDefault,
                        sessionState = target.SessionState.ToString(),
                        trustPolicyVersion = target.TrustPolicyVersion,
                    }).ToArray(),
                }, cancellationToken).ConfigureAwait(false);
                await CopyLogsAsync(archive, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, destination, overwrite: true);
            temporary = string.Empty;
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporary) && File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private string? SelectDestination()
    {
        if (MessageBox.Show(
                _ownerProvider(),
                Strings.DiagnosticsConsent,
                Strings.DiagnosticsTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Information,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return null;
        }

        var dialog = new SaveFileDialog
        {
            Title = Strings.DiagnosticsTitle,
            Filter = "ZIP (*.zip)|*.zip",
            AddExtension = true,
            DefaultExt = ".zip",
            FileName = $"{LauncherBuildIdentity.Current.ExecutableBaseName}-Diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip",
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(_ownerProvider()) == true ? dialog.FileName : null;
    }

    private async ValueTask CopyLogsAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < RollingDiagnosticLogPolicy.MaximumFiles; index++)
        {
            var fileName = index == 0 ? "launcher.log" : $"launcher.{index}.log";
            var entryName = $"logs/{fileName}";
            if (!DiagnosticExportWhitelist.IsAllowed(entryName))
            {
                continue;
            }

            var content = await _diagnosticLog.ReadForExportAsync(
                index,
                cancellationToken).ConfigureAwait(false);
            if (content is null)
            {
                continue;
            }

            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            await using var output = entry.Open();
            await output.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask WriteReleaseConstantsAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        await using var source = typeof(App).Assembly.GetManifestResourceStream(
            "DshLauncher.Desktop.ReleaseConstants.json") ??
            throw new InvalidOperationException("Embedded release constants are missing.");
        var entry = archive.CreateEntry("dependency-baseline.json", CompressionLevel.Optimal);
        await using var destination = entry.Open();
        await source.CopyToAsync(destination, 16 * 1024, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WriteJsonAsync<T>(
        ZipArchive archive,
        string entryName,
        T value,
        CancellationToken cancellationToken)
    {
        if (!DiagnosticExportWhitelist.IsAllowed(entryName))
        {
            throw new InvalidOperationException("Diagnostic entry is not allowed.");
        }

        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var output = entry.Open();
        await JsonSerializer.SerializeAsync(output, value, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string CreateTargetReference(TargetId targetId)
    {
        var hash = SHA256.HashData(targetId.Value.ToByteArray());
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    private static string? TryGetWebView2Version()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public sealed class UpdatePageService : IUpdatePageService
{
    private readonly Uri? _releaseUri;
    private readonly Func<Window?> _ownerProvider;
    private readonly Dispatcher _dispatcher;
    private readonly ITargetExternalUriLauncher _launcher;

    public UpdatePageService(
        Uri? releaseUri,
        Func<Window?> ownerProvider,
        Dispatcher dispatcher,
        ITargetExternalUriLauncher launcher)
    {
        if (releaseUri is not null &&
            (!releaseUri.IsAbsoluteUri || releaseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Release URI must be an absolute HTTPS URI.", nameof(releaseUri));
        }

        _releaseUri = releaseUri;
        _ownerProvider = ownerProvider ?? throw new ArgumentNullException(nameof(ownerProvider));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    }

    public async ValueTask OpenAsync(CancellationToken cancellationToken)
    {
        if (_releaseUri is null)
        {
            throw new LauncherPresentationException(Strings.UpdateUnavailable);
        }

        var confirmed = await _dispatcher.InvokeAsync(
            () => MessageBox.Show(
                _ownerProvider(),
                Strings.UpdateConfirmation,
                LauncherBuildIdentity.Current.ProductName,
                MessageBoxButton.YesNo,
                MessageBoxImage.Information,
                MessageBoxResult.No) == MessageBoxResult.Yes,
            DispatcherPriority.Normal,
            cancellationToken);
        if (confirmed)
        {
            await _launcher.OpenAsync(_releaseUri, cancellationToken).ConfigureAwait(false);
        }
    }
}
