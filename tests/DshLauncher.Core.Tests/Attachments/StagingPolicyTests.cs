using System.Reflection;
using DshLauncher.Core.Attachments;
using Xunit;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>
/// D14 平台中立判定测试：候选接受、路径包含关系、限额、变化检测与清理归属全部由纯函数表达，
/// 因此在 Linux 上逐条真实执行。这里<b>不</b>断言任何 Windows 事实（属性、锁、ACL、重解析点、
/// 云占位），那些属于 WindowsPending，见 Platform.Windows.Tests 的待验清单。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
public sealed class StagingPolicyTests
{
    private const string Root = @"C:\owned\staging";

    [Theory]
    [InlineData(@"\\server\share\a.txt", StagingPathKind.Unc)]
    [InlineData(@"//server/share/a.txt", StagingPathKind.Unc)]
    [InlineData(@"\\?\C:\a.txt", StagingPathKind.Device)]
    [InlineData(@"\\.\PhysicalDrive0", StagingPathKind.Device)]
    [InlineData(@"\??\C:\a.txt", StagingPathKind.Device)]
    public void ClassifyRejectsUncAndDeviceForms(string path, StagingPathKind expected)
    {
        var classification = StagingPathPolicy.Classify(path);

        Assert.Equal(expected, classification.Kind);
    }

    [Theory]
    [InlineData(@"..\a.txt", StagingPathKind.Traversal)]
    [InlineData(@"C:\a\..\b.txt", StagingPathKind.Traversal)]
    [InlineData(@"dir\a.txt", StagingPathKind.RelativeNested)]
    [InlineData("a.txt", StagingPathKind.RelativeLeaf)]
    [InlineData(@"\a.txt", StagingPathKind.Rooted)]
    [InlineData("C:a.txt", StagingPathKind.DriveRelative)]
    [InlineData("C:", StagingPathKind.DriveRelative)]
    [InlineData("", StagingPathKind.Invalid)]
    [InlineData("a.txt ", StagingPathKind.Invalid)]
    [InlineData("1:a.txt", StagingPathKind.Invalid)]
    public void ClassifyRejectsTraversalRelativeAndInvalidForms(string path, StagingPathKind expected)
    {
        var classification = StagingPathPolicy.Classify(path);

        Assert.Equal(expected, classification.Kind);
    }

    [Theory]
    [InlineData(@"C:\a.txt")]
    [InlineData(@"c:/dir/a.txt")]
    [InlineData(@"C:\dir\a b c.txt")]
    public void ClassifyAcceptsLocalAbsolutePaths(string path)
    {
        var classification = StagingPathPolicy.Classify(path);

        Assert.Equal(StagingPathKind.DriveAbsolute, classification.Kind);
    }

    [Theory]
    [InlineData(@"..\evil.txt")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData(@"dir\evil.txt")]
    [InlineData(@"C:\evil.txt")]
    [InlineData(@"\\server\share\evil.txt")]
    [InlineData(@"\evil.txt")]
    [InlineData(@"C:evil.txt")]
    [InlineData("evil.txt:ads")]
    [InlineData("CON")]
    [InlineData("evil.")]
    [InlineData("evil ")]
    [InlineData("")]
    public void ResolveOwnedLeafRefusesHostileLeafNames(string leaf)
    {
        var decision = StagingPathPolicy.ResolveOwnedLeaf(Root, leaf);

        Assert.False(decision.Allowed);
        Assert.Equal(AttachmentStagingCodes.CleanupOutsideOwnedRoot, decision.Code);
    }

    [Fact]
    public void ResolveOwnedLeafAcceptsAPlainLeafName()
    {
        var decision = StagingPathPolicy.ResolveOwnedLeaf(Root, "0123456789abcdef.bin");

        Assert.True(decision.Allowed);
        Assert.Equal(@"C:\owned\staging\0123456789abcdef.bin", decision.FullPath);
    }

    [Theory]
    [InlineData(@"C:\owned\staging\a.txt", true)]
    [InlineData(@"c:\OWNED\STAGING\a.txt", true)]
    [InlineData(@"C:\owned\staging-evil\a.txt", false)]
    [InlineData(@"C:\owned\staging", false)]
    [InlineData(@"C:\owned", false)]
    [InlineData(@"C:\owned2\staging\a.txt", false)]
    [InlineData(@"\\server\share\staging\a.txt", false)]
    [InlineData("", false)]
    public void IsWithinOwnedRootUsesSeparatorBoundaries(string candidate, bool expected)
    {
        Assert.Equal(expected, StagingPathPolicy.IsWithinOwnedRoot(Root, candidate));
    }

    [Theory]
    [InlineData(@"C:\owned\staging\..\..\Windows\evil.dll", AttachmentStagingCodes.PathTraversal)]
    [InlineData(@"\\server\share\evil.txt", AttachmentStagingCodes.UncPath)]
    [InlineData(@"\\?\C:\evil.txt", AttachmentStagingCodes.DevicePath)]
    [InlineData(@"\evil.txt", AttachmentStagingCodes.PathNotAbsolute)]
    [InlineData("C:evil.txt", AttachmentStagingCodes.PathNotAbsolute)]
    [InlineData(@"dir\evil.txt", AttachmentStagingCodes.PathNotAbsolute)]
    [InlineData(@"C:\Windows\System32\evil.dll", AttachmentStagingCodes.CleanupOutsideOwnedRoot)]
    [InlineData(@"C:\owned\staging-evil\evil.txt", AttachmentStagingCodes.CleanupOutsideOwnedRoot)]
    public void ConfineToOwnedRootGivesADeterminateCodeForEachEscapeForm(string candidate, string expectedCode)
    {
        var decision = StagingPathPolicy.ConfineToOwnedRoot(Root, candidate);

        Assert.False(decision.Allowed);
        Assert.Equal(expectedCode, decision.Code);
    }

    [Theory]
    [InlineData(@"\\server\share\staging")]
    [InlineData(@"\\?\C:\staging")]
    [InlineData(@"C:\owned\..\staging")]
    [InlineData("staging")]
    [InlineData(@"\staging")]
    public void NormalizeOwnedRootRejectsRootsThatAreNotLocalAbsolute(string root)
    {
        Assert.Throws<ArgumentException>(() => StagingPathPolicy.NormalizeOwnedRoot(root));
    }

    [Fact]
    public void NormalizeOwnedRootAcceptsALocalAbsoluteRoot()
    {
        Assert.Equal(@"C:\owned\staging", StagingPathPolicy.NormalizeOwnedRoot(@"c:/owned/staging/"));
    }

    [Theory]
    [InlineData(@"..\..\evil.txt", "evil.txt")]
    [InlineData(@"C:\Users\me\报告 v2.txt", "报告 v2.txt")]
    [InlineData("a<b>c|d?e*f.txt", "a_b_c_d_e_f.txt")]
    [InlineData("trailing.  ", "trailing")]
    [InlineData("", StagingPathPolicy.DefaultDisplayName)]
    public void NormalizeDisplayNameNeverKeepsPathSyntax(string raw, string expected)
    {
        Assert.Equal(expected, StagingPathPolicy.NormalizeDisplayName(raw));
    }

    [Fact]
    public void CreateStagingLeafNameUsesOnlyTheSnapshotIdAndASafeExtension()
    {
        var leaf = StagingPathPolicy.CreateStagingLeafName(@"..\..\evil.tar.gz", "abc123");

        Assert.Equal("abc123.gz", leaf);
        Assert.True(StagingPathPolicy.IsValidLeafName(leaf));
    }

    [Fact]
    public void CandidatePolicyRefusesDirectoriesBeforeLookingAtThePathForm()
    {
        var descriptor = StagingFakesHelper.Descriptor(
            length: 10,
            kind: StagingSourceKind.Directory,
            pathKind: StagingPathKind.Unc);

        var decision = StagingCandidatePolicy.Evaluate(descriptor);

        Assert.False(decision.Allowed);
        Assert.Equal(AttachmentStagingCodes.Directory, decision.Code);
    }

    [Theory]
    [InlineData(StagingSourceKind.Device, StagingPathKind.DriveAbsolute, false, false, false, false, AttachmentStagingCodes.NotRegularFile)]
    [InlineData(StagingSourceKind.Other, StagingPathKind.DriveAbsolute, false, false, false, false, AttachmentStagingCodes.NotRegularFile)]
    [InlineData(StagingSourceKind.RegularFile, StagingPathKind.Unc, false, false, false, false, AttachmentStagingCodes.UncPath)]
    [InlineData(StagingSourceKind.RegularFile, StagingPathKind.Device, false, false, false, false, AttachmentStagingCodes.DevicePath)]
    [InlineData(StagingSourceKind.RegularFile, StagingPathKind.RelativeLeaf, false, false, false, false, AttachmentStagingCodes.PathNotAbsolute)]
    [InlineData(StagingSourceKind.RegularFile, StagingPathKind.Traversal, false, false, false, false, AttachmentStagingCodes.PathTraversal)]
    [InlineData(StagingSourceKind.RegularFile, StagingPathKind.DriveAbsolute, true, false, false, false, AttachmentStagingCodes.ReparsePoint)]
    [InlineData(StagingSourceKind.RegularFile, StagingPathKind.DriveAbsolute, false, true, false, false, AttachmentStagingCodes.CloudPlaceholder)]
    [InlineData(StagingSourceKind.RegularFile, StagingPathKind.DriveAbsolute, false, false, true, false, AttachmentStagingCodes.OfflineFile)]
    [InlineData(StagingSourceKind.RegularFile, StagingPathKind.DriveAbsolute, false, false, false, true, AttachmentStagingCodes.NetworkShare)]
    public void CandidatePolicyRefusesEveryUnsupportedSourceFact(
        StagingSourceKind kind,
        StagingPathKind pathKind,
        bool reparse,
        bool placeholder,
        bool offline,
        bool network,
        string expectedCode)
    {
        var descriptor = StagingFakesHelper.Descriptor(
            length: 10,
            kind: kind,
            pathKind: pathKind,
            isReparsePoint: reparse,
            isCloudPlaceholder: placeholder,
            isOffline: offline,
            isNetworkShare: network);

        var decision = StagingCandidatePolicy.Evaluate(descriptor);

        Assert.False(decision.Allowed);
        Assert.Equal(expectedCode, decision.Code);
    }

    [Fact]
    public void CandidatePolicyAcceptsAPlainLocalRegularFile()
    {
        var decision = StagingCandidatePolicy.Evaluate(StagingFakesHelper.Descriptor(length: 0));

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void QuotaRefusesAFileOverTheFrozenFileLimit()
    {
        var limits = new AttachmentLimits { MaxFileBytes = 100, MaxFilesPerBatch = 10, MaxBatchBytes = 1000, MaxStagingBytesPerTarget = 1000 };

        var decision = StagingQuotaPolicy.Evaluate(limits, default, 101, long.MaxValue, 0);

        Assert.False(decision.Allowed);
        Assert.Equal("limit-file-bytes", decision.Code);
    }

    [Fact]
    public void QuotaRefusesTheFileThatWouldExceedTheBatchFileCount()
    {
        var limits = new AttachmentLimits { MaxFilesPerBatch = 2, MaxBatchBytes = 1000, MaxStagingBytesPerTarget = 1000 };
        var state = new StagingQuotaState(StagedBytesForTarget: 0, FilesInBatch: 2, BatchBytes: 0);

        var decision = StagingQuotaPolicy.Evaluate(limits, state, 1, long.MaxValue, 0);

        Assert.False(decision.Allowed);
        Assert.Equal("limit-batch-files", decision.Code);
    }

    [Fact]
    public void QuotaRefusesBytesThatWouldExceedTheBatchLimit()
    {
        var limits = new AttachmentLimits { MaxBatchBytes = 100, MaxFilesPerBatch = 10, MaxStagingBytesPerTarget = 1000 };
        var state = new StagingQuotaState(StagedBytesForTarget: 0, FilesInBatch: 1, BatchBytes: 60);

        var decision = StagingQuotaPolicy.Evaluate(limits, state, 41, long.MaxValue, 0);

        Assert.False(decision.Allowed);
        Assert.Equal("limit-batch-bytes", decision.Code);
    }

    [Fact]
    public void QuotaRefusesBytesThatWouldExceedThePerTargetStagingCap()
    {
        var limits = new AttachmentLimits { MaxFileBytes = 1000, MaxBatchBytes = 100_000, MaxFilesPerBatch = 10, MaxStagingBytesPerTarget = 100 };
        var state = new StagingQuotaState(StagedBytesForTarget: 80, FilesInBatch: 1, BatchBytes: 80);

        var decision = StagingQuotaPolicy.Evaluate(limits, state, 21, long.MaxValue, 0);

        Assert.False(decision.Allowed);
        Assert.Equal("limit-staging-bytes", decision.Code);
    }

    [Fact]
    public void QuotaRefusesWhenFreeSpaceCannotCoverTheFilePlusTheReserve()
    {
        var limits = new AttachmentLimits();

        var decision = StagingQuotaPolicy.Evaluate(limits, default, 500, availableFreeBytes: 1000, freeSpaceReserveBytes: 600);

        Assert.False(decision.Allowed);
        Assert.Equal(AttachmentStagingCodes.InsufficientFreeSpace, decision.Code);
    }

    [Fact]
    public void QuotaAllowsExactlyAtEveryLimit()
    {
        var limits = new AttachmentLimits
        {
            MaxFileBytes = 100,
            MaxFilesPerBatch = 2,
            MaxBatchBytes = 200,
            MaxStagingBytesPerTarget = 300,
        };
        var state = new StagingQuotaState(StagedBytesForTarget: 200, FilesInBatch: 1, BatchBytes: 100);

        var decision = StagingQuotaPolicy.Evaluate(limits, state, 100, availableFreeBytes: 1000, freeSpaceReserveBytes: 900);

        Assert.True(decision.Allowed);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("time")]
    [InlineData("fileid")]
    [InlineData("volume")]
    [InlineData("missing")]
    [InlineData("reparse")]
    public void ChangePolicyDetectsEveryMutation(string mutation)
    {
        var before = new StagingSourceIdentity(true, 100, 500, 7, 3, false);
        var after = mutation switch
        {
            "length" => before with { Length = 101 },
            "time" => before with { LastWriteTimeUtcTicks = 501 },
            "fileid" => before with { FileId = 8 },
            "volume" => before with { VolumeSerial = 4 },
            "missing" => StagingSourceIdentity.Missing,
            _ => before with { IsReparsePoint = true },
        };

        var decision = StagingSourceChangePolicy.Compare(before, after);

        Assert.False(decision.Unchanged);
        Assert.NotNull(decision.Reason);
    }

    [Fact]
    public void ChangePolicyAcceptsAnIdenticalIdentity()
    {
        var identity = new StagingSourceIdentity(true, 100, 500, 7, 3, false);

        Assert.True(StagingSourceChangePolicy.Compare(identity, identity).Unchanged);
    }

    [Theory]
    [InlineData(@"C:\owned\staging\..\..\Windows\evil.dll", AttachmentStagingCodes.CleanupOutsideOwnedRoot)]
    [InlineData(@"C:\Windows\System32\evil.dll", AttachmentStagingCodes.CleanupOutsideOwnedRoot)]
    [InlineData(@"\\server\share\evil.dll", AttachmentStagingCodes.CleanupOutsideOwnedRoot)]
    [InlineData(@"\evil.dll", AttachmentStagingCodes.CleanupOutsideOwnedRoot)]
    [InlineData(@"C:\owned\staging-evil\evil.dll", AttachmentStagingCodes.CleanupOutsideOwnedRoot)]
    [InlineData(@"C:\owned\staging\dir\evil.dll", AttachmentStagingCodes.CleanupOutsideOwnedRoot)]
    public void CleanupPolicyRefusesHostileTargets(string target, string expectedCode)
    {
        var info = new StagingOwnedEntryInfo(target, target, Exists: true, IsDirectory: false, IsReparsePoint: false);

        var decision = StagingCleanupPolicy.Evaluate(Root, target, info);

        Assert.False(decision.Allowed);
        Assert.Equal(expectedCode, decision.Code);
    }

    [Theory]
    [InlineData(true, false, AttachmentStagingCodes.CleanupReparsePoint)]
    [InlineData(false, true, AttachmentStagingCodes.CleanupNotRegularFile)]
    public void CleanupPolicyRefusesReparsePointsAndDirectories(bool reparse, bool directory, string expectedCode)
    {
        const string Target = @"C:\owned\staging\orphan.bin";
        var info = new StagingOwnedEntryInfo(Target, Target, Exists: true, IsDirectory: directory, IsReparsePoint: reparse);

        var decision = StagingCleanupPolicy.Evaluate(Root, Target, info);

        Assert.False(decision.Allowed);
        Assert.Equal(expectedCode, decision.Code);
    }

    [Fact]
    public void CleanupPolicyRefusesWhenTheResolvedFinalPathEscapesTheOwnedRoot()
    {
        const string Target = @"C:\owned\staging\link.bin";
        var info = new StagingOwnedEntryInfo(Target, @"C:\Users\me\.ssh\id_rsa", Exists: true, IsDirectory: false, IsReparsePoint: false);

        var decision = StagingCleanupPolicy.Evaluate(Root, Target, info);

        Assert.False(decision.Allowed);
        Assert.Equal(AttachmentStagingCodes.CleanupOutsideOwnedRoot, decision.Code);
    }

    [Fact]
    public void CleanupPolicyAllowsAnOwnedRegularFileAndAMissingFile()
    {
        const string Target = @"C:\owned\staging\orphan.bin";
        var existing = new StagingOwnedEntryInfo(Target, Target, Exists: true, IsDirectory: false, IsReparsePoint: false);
        var missing = new StagingOwnedEntryInfo(Target, Target, Exists: false, IsDirectory: false, IsReparsePoint: false);

        Assert.True(StagingCleanupPolicy.Evaluate(Root, Target, existing).Allowed);
        Assert.True(StagingCleanupPolicy.Evaluate(Root, Target, missing).Allowed);
    }

    [Fact]
    public void StagingServiceAcceptsNoPathFromItsCallers()
    {
        // 结构性边界：暂存服务的唯一快照入口只接受原生捕获票据；
        // 其余公开成员里的字符串参数只有不透明 id，没有任何 path/filePath 参数。
        var capture = typeof(AttachmentStagingService).GetMethod(nameof(AttachmentStagingService.Capture));
        Assert.NotNull(capture);
        var parameters = capture.GetParameters();
        Assert.Equal(typeof(INativeStagingCapture), parameters[0].ParameterType);
        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(string));

        var stringParameters = typeof(AttachmentStagingService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetParameters())
            .Where(parameter => parameter.ParameterType == typeof(string))
            .Select(parameter => parameter.Name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["batchId", "snapshotId"], stringParameters);
        Assert.DoesNotContain(
            typeof(INativeStagingCapture).GetMembers(),
            member => member.Name.Contains("Path", StringComparison.Ordinal));
    }
}

/// <summary>策略测试的构造助手（避免每个 InlineData 都写一长串命名参数）。</summary>
internal static class StagingFakesHelper
{
    public static StagingSourceDescriptor Descriptor(
        long length,
        StagingSourceKind kind = StagingSourceKind.RegularFile,
        StagingPathKind pathKind = StagingPathKind.DriveAbsolute,
        bool isReparsePoint = false,
        bool isCloudPlaceholder = false,
        bool isOffline = false,
        bool isNetworkShare = false,
        string leafName = "report.txt") =>
        new()
        {
            PathKind = pathKind,
            LeafName = leafName,
            Kind = kind,
            Length = length,
            IsReparsePoint = isReparsePoint,
            IsCloudPlaceholder = isCloudPlaceholder,
            IsOffline = isOffline,
            IsNetworkShare = isNetworkShare,
        };
}
