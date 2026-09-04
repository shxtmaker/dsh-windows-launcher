using System.Reflection;

namespace DshLauncher.Core;

public sealed class LauncherBuildIdentity
{
    private const string MetadataPrefix = "DshLauncher.";

    private LauncherBuildIdentity(IReadOnlyDictionary<string, string> metadata)
    {
        BuildFlavor = Require(metadata, "BuildFlavor");
        if (BuildFlavor is not ("InternalTest" or "Official"))
        {
            throw new InvalidOperationException(
                $"Unknown launcher build flavor: {BuildFlavor}");
        }

        ProductName = Require(metadata, "ProductName");
        ExecutableBaseName = Require(metadata, "ExecutableBaseName");
        ApplicationDataId = Require(metadata, "ApplicationDataId");
        SingleInstanceBaseName = Require(metadata, "SingleInstanceBaseName");
        InstallerAppId = Require(metadata, "InstallerAppId");
        InstallDirectoryName = Require(metadata, "InstallDirectoryName");
        DataOwnerId = Require(metadata, "DataOwnerId");
        InstallOwnerId = Require(metadata, "InstallOwnerId");

        if (!Guid.TryParseExact(InstallerAppId, "D", out _))
        {
            throw new InvalidOperationException(
                "Launcher installer AppId metadata is not a canonical GUID.");
        }
    }

    public static LauncherBuildIdentity Current { get; } =
        FromAssembly(typeof(LauncherBuildIdentity).Assembly);

    public string BuildFlavor { get; }

    public bool IsInternalTest => BuildFlavor == "InternalTest";

    public string ProductName { get; }

    public string ExecutableBaseName { get; }

    public string ApplicationDataId { get; }

    public string SingleInstanceBaseName { get; }

    public string InstallerAppId { get; }

    public string InstallDirectoryName { get; }

    /// <summary>
    /// Identifies the application-data ownership token embedded in the assembly
    /// metadata. Installers and external maintenance tools read this value via
    /// <see cref="System.Reflection.AssemblyMetadataAttribute"/> to verify they
    /// are operating on the correct data root before performing destructive
    /// operations (e.g. uninstall data cleanup).
    /// </summary>
    public string DataOwnerId { get; }

    /// <summary>
    /// Identifies the installation ownership token embedded in the assembly
    /// metadata. The installer writes this value into
    /// <c>installer/install-owner.txt</c> at packaging time; external tools
    /// compare the file content against this property to confirm the
    /// installation was produced by the same build flavor.
    /// </summary>
    public string InstallOwnerId { get; }

    private static LauncherBuildIdentity FromAssembly(Assembly assembly)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (attribute.Key.StartsWith(MetadataPrefix, StringComparison.Ordinal) &&
                !metadata.TryAdd(
                    attribute.Key[MetadataPrefix.Length..],
                    attribute.Value ?? string.Empty))
            {
                throw new InvalidOperationException(
                    $"Duplicate launcher build metadata: {attribute.Key}");
            }
        }

        return new LauncherBuildIdentity(metadata);
    }

    private static string Require(
        IReadOnlyDictionary<string, string> metadata,
        string key)
    {
        if (!metadata.TryGetValue(key, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Launcher build metadata is missing: {MetadataPrefix}{key}");
        }

        return value;
    }
}
