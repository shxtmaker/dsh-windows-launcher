using System.IO;
using DshLauncher.Core;
using DshLauncher.WebView;
using Xunit;

namespace DshLauncher.WebView.Tests;

[Trait("triggerTags", "VFY-05,VFY-06")]
public sealed class WebView2TargetRuntimePortTests
{
    private static readonly TargetId TargetId = new(
        new Guid("be85ff53-5860-4445-98b0-8ae046b628ef"));
    private static readonly string[] ResetCalls =
        ["exists", "verify", "delete", "exists", "prepare", "verify"];
    private static readonly string[] ExistsOnlyCalls = ["exists"];

    [Fact]
    public async Task BrowserProcessExitWaiterCompletesOnlyAfterTheReleaseSignal()
    {
        var waiter = new BrowserProcessExitWaiter();
        var waiting = waiter.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.False(waiting.IsCompleted);
        waiter.Signal();
        await waiting;
    }

    [Fact]
    public async Task BrowserProcessExitWaiterFailsClosedWhenReleaseIsNotObserved()
    {
        var waiter = new BrowserProcessExitWaiter();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => waiter.WaitAsync(
                TimeSpan.FromMilliseconds(20),
                TestContext.Current.CancellationToken));

        Assert.Contains("did not release", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrowserGenerationAcceptsAnExitObservedBeforeEnsureReturns()
    {
        var generation = new BrowserProcessGeneration();
        var running = true;

        generation.ObserveExit(41);
        running = false;
        await generation.WaitForQuiescenceAsync(
            () => running,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BrowserGenerationIgnoresAnotherKnownBrowserProcess()
    {
        var generation = new BrowserProcessGeneration();
        generation.CaptureBrowserProcessId(41);
        generation.ObserveExit(42);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            generation.WaitForQuiescenceAsync(
                () => true,
                TimeSpan.FromMilliseconds(20),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BrowserGenerationProvesNoProcessWasStartedWithoutWaitingForAnEvent()
    {
        var generation = new BrowserProcessGeneration();

        await generation.WaitForQuiescenceAsync(
            () => false,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public void SensitiveManagedStringClearsTheExactStringPassedToNavigation()
    {
        using var value = SensitiveManagedString.Create(
            "http://192.168.10.20:3080/?token=".AsSpan(),
            "ephemeral-secret".AsSpan());
        string? observed = null;

        value.UseAndClear(text => observed = text);

        Assert.NotNull(observed);
        Assert.All(observed, character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task ActiveBrowserLeaseBlocksUdfResetUntilQuiescenceIsProven()
    {
        var targetId = new TargetId(
            Guid.Parse("32a38936-d80a-4234-b935-5193d500ed19"));
        var running = true;
        var lease = BrowserProcessEnvironmentLease.RegisterForTesting(
            targetId,
            () => running,
            TimeSpan.FromMilliseconds(20));
        lease.CaptureBrowserProcessId(71);
        var data = new FakeBrowserDataStore { Exists = true };
        var runtime = new WebView2TargetRuntimePort(
            data,
            new FakeBrowserSessionFactory());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runtime.ResetUncommittedAsync(
                targetId,
                TestContext.Current.CancellationToken));

        Assert.Empty(data.Calls);
        running = false;
        lease.ObserveExitForTesting(71);
        await BrowserProcessEnvironmentLease.EnsureReleasedAsync(
            targetId,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PartialBrowserStartCanReleaseThroughAnEarlyGenerationEvent()
    {
        var targetId = new TargetId(
            Guid.Parse("4343d94f-2cf6-47a7-8ae2-193f845f9dd2"));
        var running = true;
        var lease = BrowserProcessEnvironmentLease.RegisterForTesting(
            targetId,
            () => running,
            TimeSpan.FromSeconds(1));

        lease.ObserveExitForTesting(81);
        running = false;
        await BrowserProcessEnvironmentLease.EnsureReleasedAsync(
            targetId,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ResetDeletesAnyUncommittedUdfThenPreparesAndVerifiesAnEmptyOne()
    {
        var data = new FakeBrowserDataStore { Exists = true };
        var runtime = new WebView2TargetRuntimePort(
            data,
            new FakeBrowserSessionFactory());

        await runtime.ResetUncommittedAsync(
            TargetId,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ResetCalls,
            data.Calls);
        Assert.True(data.Exists);
    }

    [Fact]
    public async Task PairReturnsOnlyTheThreeRequiredHandshakeFactsAndDisposesTheSession()
    {
        var endpoint = await EndpointFactory.CreateAsync(
            TestContext.Current.CancellationToken);
        var data = new FakeBrowserDataStore { Exists = true };
        var session = new FakeBrowserSession
        {
            Pairing = new TargetBrowserPairingObservation(
                TokenRequestStatusCode: 303,
                CleanRoot: Successful(200),
                AuthenticatedApi: Successful(404)),
        };
        var factory = new FakeBrowserSessionFactory(session);
        var runtime = new WebView2TargetRuntimePort(data, factory);
        const string encodedToken = "AbC_123-safe";

        var result = await runtime.PairAsync(
            TargetId,
            endpoint,
            encodedToken.AsMemory(),
            TestContext.Current.CancellationToken);

        Assert.Equal(303, result.TokenRequestStatusCode);
        Assert.True(result.RootPageLoaded);
        Assert.True(result.AuthenticatedApiAvailable);
        Assert.Equal(encodedToken, session.ObservedToken);
        Assert.True(session.Disposed);
        var context = Assert.Single(factory.Contexts);
        Assert.Equal(TargetId, context.TargetId);
        Assert.Equal(endpoint.Origin + "/", context.Origin.AbsoluteUri);
        Assert.Equal(data.UserDataFolder, context.UserDataFolder);
    }

    [Fact]
    public async Task PairTreatsPinnedGetApi404AsAnAuthenticatedBoundary()
    {
        var endpoint = await EndpointFactory.CreateAsync(
            TestContext.Current.CancellationToken);
        var session = new FakeBrowserSession
        {
            Pairing = new TargetBrowserPairingObservation(
                TokenRequestStatusCode: 303,
                CleanRoot: Successful(200),
                AuthenticatedApi: Successful(404)),
        };
        var runtime = new WebView2TargetRuntimePort(
            new FakeBrowserDataStore { Exists = true },
            new FakeBrowserSessionFactory(session));

        var result = await runtime.PairAsync(
            TargetId,
            endpoint,
            "valid-token".AsMemory(),
            TestContext.Current.CancellationToken);

        Assert.True(result.RootPageLoaded);
        Assert.True(result.AuthenticatedApiAvailable);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task PairTreatsWebView2Http404ErrorPageAsAnAuthenticatedBoundary()
    {
        var endpoint = await EndpointFactory.CreateAsync(
            TestContext.Current.CancellationToken);
        var session = new FakeBrowserSession
        {
            Pairing = new TargetBrowserPairingObservation(
                TokenRequestStatusCode: 303,
                CleanRoot: Successful(200),
                AuthenticatedApi: new TargetBrowserNavigationObservation(
                    OriginMatched: true,
                    NavigationSucceeded: false,
                    HttpStatusCode: 404)),
        };
        var runtime = new WebView2TargetRuntimePort(
            new FakeBrowserDataStore { Exists = true },
            new FakeBrowserSessionFactory(session));

        var result = await runtime.PairAsync(
            TargetId,
            endpoint,
            "valid-token".AsMemory(),
            TestContext.Current.CancellationToken);

        Assert.True(result.RootPageLoaded);
        Assert.True(result.AuthenticatedApiAvailable);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task PairNeverTreatsCrossOriginCompletionAsAValidHandshake()
    {
        var endpoint = await EndpointFactory.CreateAsync(
            TestContext.Current.CancellationToken);
        var session = new FakeBrowserSession
        {
            Pairing = new TargetBrowserPairingObservation(
                TokenRequestStatusCode: 303,
                CleanRoot: new TargetBrowserNavigationObservation(
                    OriginMatched: false,
                    NavigationSucceeded: true,
                    HttpStatusCode: 200),
                AuthenticatedApi: Successful(404)),
        };
        var runtime = new WebView2TargetRuntimePort(
            new FakeBrowserDataStore { Exists = true },
            new FakeBrowserSessionFactory(session));

        var result = await runtime.PairAsync(
            TargetId,
            endpoint,
            "valid-token".AsMemory(),
            TestContext.Current.CancellationToken);

        Assert.Equal(303, result.TokenRequestStatusCode);
        Assert.False(result.RootPageLoaded);
        Assert.True(result.AuthenticatedApiAvailable);
        Assert.True(session.Disposed);
    }

    [Theory]
    [MemberData(nameof(OpenClassifications))]
    public async Task PrepareOpenMapsSanitizedBrowserObservations(
        TargetBrowserOpenObservation observation,
        RuntimeOpenStatus expected)
    {
        var endpoint = await EndpointFactory.CreateAsync(
            TestContext.Current.CancellationToken);
        var session = new FakeBrowserSession { Open = observation };
        var runtime = new WebView2TargetRuntimePort(
            new FakeBrowserDataStore { Exists = true },
            new FakeBrowserSessionFactory(session));

        var result = await runtime.PrepareOpenAsync(
            TargetId,
            endpoint,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, result);
        Assert.True(session.Disposed);
    }

    public static TheoryData<TargetBrowserOpenObservation, RuntimeOpenStatus>
        OpenClassifications => new()
        {
            {
                new TargetBrowserOpenObservation(
                    Successful(200),
                    Successful(404)),
                RuntimeOpenStatus.Ready
            },
            {
                new TargetBrowserOpenObservation(
                    Successful(200),
                    new TargetBrowserNavigationObservation(true, false, 404)),
                RuntimeOpenStatus.Ready
            },
            {
                new TargetBrowserOpenObservation(
                    Successful(200),
                    Successful(200)),
                RuntimeOpenStatus.TemporaryFailure
            },
            {
                new TargetBrowserOpenObservation(
                    Successful(401),
                    NotAttempted()),
                RuntimeOpenStatus.Unauthorized
            },
            {
                new TargetBrowserOpenObservation(
                    Successful(200),
                    Successful(401)),
                RuntimeOpenStatus.Unauthorized
            },
            {
                new TargetBrowserOpenObservation(
                    Successful(403),
                    NotAttempted()),
                RuntimeOpenStatus.Forbidden
            },
            {
                new TargetBrowserOpenObservation(
                    Successful(200),
                    Successful(403)),
                RuntimeOpenStatus.Forbidden
            },
            {
                new TargetBrowserOpenObservation(
                    new TargetBrowserNavigationObservation(false, true, 200),
                    NotAttempted()),
                RuntimeOpenStatus.OriginMismatch
            },
            {
                new TargetBrowserOpenObservation(
                    new TargetBrowserNavigationObservation(true, false, null),
                    NotAttempted()),
                RuntimeOpenStatus.TemporaryFailure
            },
            {
                new TargetBrowserOpenObservation(
                    Successful(200),
                    Successful(500)),
                RuntimeOpenStatus.TemporaryFailure
            },
        };

    [Fact]
    public async Task MissingUdfRequiresPairingWithoutOpeningAWebView()
    {
        var endpoint = await EndpointFactory.CreateAsync(
            TestContext.Current.CancellationToken);
        var data = new FakeBrowserDataStore { Exists = false };
        var factory = new FakeBrowserSessionFactory();
        var runtime = new WebView2TargetRuntimePort(data, factory);

        var result = await runtime.PrepareOpenAsync(
            TargetId,
            endpoint,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeOpenStatus.Unauthorized, result);
        Assert.Empty(factory.Contexts);
        Assert.Equal(ExistsOnlyCalls, data.Calls);
    }

    [Fact]
    public async Task TemporarySessionIsDisposedWhenBrowserObservationThrows()
    {
        var endpoint = await EndpointFactory.CreateAsync(
            TestContext.Current.CancellationToken);
        var session = new FakeBrowserSession { ThrowOnOpen = true };
        var runtime = new WebView2TargetRuntimePort(
            new FakeBrowserDataStore { Exists = true },
            new FakeBrowserSessionFactory(session));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runtime.PrepareOpenAsync(
                TargetId,
                endpoint,
                TestContext.Current.CancellationToken));

        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task PairingSessionInitializationFailureHasAStableDiagnosticStage()
    {
        var endpoint = await EndpointFactory.CreateAsync(
            TestContext.Current.CancellationToken);
        var diagnostics = new RecordingPairingDiagnosticSink();
        var runtime = new WebView2TargetRuntimePort(
            new FakeBrowserDataStore { Exists = true },
            new FakeBrowserSessionFactory(),
            diagnostics);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runtime.PairAsync(
                TargetId,
                endpoint,
                "safe-credential".AsMemory(),
                TestContext.Current.CancellationToken));

        Assert.Contains(
            diagnostics.Events,
            item => item.Stage == PairingDiagnosticStage.BrowserSessionOpen &&
                    item.Outcome == PairingDiagnosticOutcome.Failed &&
                    item.FailureCategory == PairingFailureCategory.BrowserInitialization);
    }

    [Fact]
    public async Task PairingAndReleaseFailuresAreSanitizedAndReleaseIsStillAttempted()
    {
        var endpoint = await EndpointFactory.CreateAsync(
            TestContext.Current.CancellationToken);
        var diagnostics = new RecordingPairingDiagnosticSink();
        var session = new FakeBrowserSession
        {
            ThrowOnPair = true,
            ThrowOnDispose = true,
        };
        var runtime = new WebView2TargetRuntimePort(
            new FakeBrowserDataStore { Exists = true },
            new FakeBrowserSessionFactory(session),
            diagnostics);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runtime.PairAsync(
                TargetId,
                endpoint,
                "safe-credential".AsMemory(),
                TestContext.Current.CancellationToken));

        Assert.Equal("The temporary browser pairing workflow failed.", exception.Message);
        Assert.True(session.Disposed);
        Assert.Contains(
            diagnostics.Events,
            item => item.Stage == PairingDiagnosticStage.BrowserSessionRelease &&
                    item.Outcome == PairingDiagnosticOutcome.Failed &&
                    item.FailureCategory == PairingFailureCategory.BrowserProcessRelease);
    }

    [Fact]
    public async Task DeleteSessionDataMustVerifyThatTheUdfIsActuallyGone()
    {
        var data = new FakeBrowserDataStore
        {
            Exists = true,
            LeaveDataAfterDelete = true,
        };
        var runtime = new WebView2TargetRuntimePort(
            data,
            new FakeBrowserSessionFactory());

        await Assert.ThrowsAsync<IOException>(async () =>
            await runtime.DeleteSessionDataAsync(
                TargetId,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteSessionDataRetriesATransientBrowserDataLock()
    {
        var data = new FakeBrowserDataStore
        {
            Exists = true,
            TransientDeleteFailuresRemaining = 1,
        };
        var runtime = new WebView2TargetRuntimePort(
            data,
            new FakeBrowserSessionFactory());

        await runtime.DeleteSessionDataAsync(
            TargetId,
            TestContext.Current.CancellationToken);

        Assert.False(data.Exists);
        Assert.Equal(2, data.Calls.Count(call => call == "delete"));
    }

    [Fact]
    public async Task DeleteSessionDataWaitsForASeventhTransientBrowserDataLock()
    {
        var data = new FakeBrowserDataStore
        {
            Exists = true,
            TransientDeleteFailuresRemaining = 7,
        };
        var runtime = new WebView2TargetRuntimePort(
            data,
            new FakeBrowserSessionFactory());

        await runtime.DeleteSessionDataAsync(
            TargetId,
            TestContext.Current.CancellationToken);

        Assert.False(data.Exists);
        Assert.Equal(8, data.Calls.Count(call => call == "delete"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad%ZZ")]
    [InlineData("contains space")]
    [InlineData("extra&parameter")]
    public async Task PairRejectsInvalidEncodedTokensBeforeTouchingTheUdf(
        string encodedToken)
    {
        var endpoint = await EndpointFactory.CreateAsync(
            TestContext.Current.CancellationToken);
        var data = new FakeBrowserDataStore { Exists = true };
        var runtime = new WebView2TargetRuntimePort(
            data,
            new FakeBrowserSessionFactory());

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await runtime.PairAsync(
                TargetId,
                endpoint,
                encodedToken.AsMemory(),
                TestContext.Current.CancellationToken));

        Assert.Empty(data.Calls);
    }

    private static TargetBrowserNavigationObservation Successful(
        int statusCode) => new(
            OriginMatched: true,
            NavigationSucceeded: true,
            statusCode);

    private static TargetBrowserNavigationObservation NotAttempted() => new(
        OriginMatched: true,
        NavigationSucceeded: false,
        HttpStatusCode: null);

    private sealed class FakeBrowserDataStore : ITargetBrowserDataStore
    {
        public string UserDataFolder { get; } =
            @"C:\Users\test\AppData\Local\DshWindowsLauncher\targets\be85ff535860444598b08ae046b628ef\udf";

        public bool Exists { get; set; }

        public bool LeaveDataAfterDelete { get; init; }

        public int TransientDeleteFailuresRemaining { get; set; }

        public List<string> Calls { get; } = [];

        public string GetUserDataFolder(TargetId targetId)
        {
            Calls.Add("get");
            return UserDataFolder;
        }

        public ValueTask PrepareAsync(
            TargetId targetId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("prepare");
            Exists = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask VerifyAsync(
            TargetId targetId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("verify");
            if (!Exists)
            {
                throw new IOException("The test UDF does not exist.");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(
            TargetId targetId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("delete");
            if (TransientDeleteFailuresRemaining > 0)
            {
                TransientDeleteFailuresRemaining--;
                throw new IOException("The test UDF is temporarily locked.");
            }

            if (!LeaveDataAfterDelete)
            {
                Exists = false;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> ExistsAsync(
            TargetId targetId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("exists");
            return ValueTask.FromResult(Exists);
        }
    }

    private sealed class FakeBrowserSessionFactory : ITargetBrowserSessionFactory
    {
        private readonly Queue<FakeBrowserSession> _sessions;

        public FakeBrowserSessionFactory(params FakeBrowserSession[] sessions)
        {
            _sessions = new Queue<FakeBrowserSession>(sessions);
        }

        public List<TargetBrowserSessionContext> Contexts { get; } = [];

        public ValueTask<ITargetBrowserSession> OpenAsync(
            TargetBrowserSessionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Contexts.Add(context);
            if (_sessions.Count == 0)
            {
                throw new InvalidOperationException("No browser session was configured.");
            }

            return ValueTask.FromResult<ITargetBrowserSession>(
                _sessions.Dequeue());
        }
    }

    private sealed class FakeBrowserSession : ITargetBrowserSession
    {
        public TargetBrowserPairingObservation Pairing { get; init; } = new(
            303,
            Successful(200),
            Successful(404));

        public TargetBrowserOpenObservation Open { get; init; } = new(
            Successful(200),
            Successful(404));

        public bool ThrowOnOpen { get; init; }

        public bool ThrowOnPair { get; init; }

        public bool ThrowOnDispose { get; init; }

        public bool Disposed { get; private set; }

        public string? ObservedToken { get; private set; }

        public ValueTask<TargetBrowserPairingObservation> PairAsync(
            ReadOnlyMemory<char> encodedToken,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservedToken = encodedToken.ToString();
            return ThrowOnPair
                ? throw new InvalidOperationException(
                    "http://192.168.10.20:3080/?token=must-not-cross")
                : ValueTask.FromResult(Pairing);
        }

        public ValueTask<TargetBrowserOpenObservation> PrepareOpenAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ThrowOnOpen
                ? throw new InvalidOperationException("Scripted browser failure.")
                : ValueTask.FromResult(Open);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ThrowOnDispose
                ? ValueTask.FromException(
                    new InvalidOperationException("scripted release failure"))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingPairingDiagnosticSink : IPairingDiagnosticSink
    {
        public List<PairingDiagnosticEvent> Events { get; } = [];

        public ValueTask ReportAsync(
            PairingDiagnosticEvent diagnosticEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(diagnosticEvent);
            return ValueTask.CompletedTask;
        }
    }

    private static class EndpointFactory
    {
        public static async ValueTask<TargetEndpoint> CreateAsync(
            CancellationToken cancellationToken)
        {
            var support = new EndpointSupport();
            using var manager = new TargetManager(new TargetManagerPorts(
                new EmptyTargetStorage(),
                support,
                support,
                support,
                support,
                support,
                support,
                support));
            var result = await manager.ExecuteAsync(
                new InspectCandidate("192.168.10.20", "3080"),
                cancellationToken);
            return result.Outcome is CandidateInspected inspected
                ? inspected.Candidate.Endpoint
                : throw new InvalidOperationException(
                    "The endpoint fixture could not be created.");
        }

        private sealed class EmptyTargetStorage : ITargetStorage
        {
            public ValueTask<TargetCatalogReadResult> ReadCatalogAsync(
                CancellationToken cancellationToken) =>
                ValueTask.FromResult(new TargetCatalogReadResult(
                    CatalogReadStatus.Missing));

            public ValueTask WriteCatalogAsync(
                TargetCatalogDocument document,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException();

            public ValueTask<IReadOnlyCollection<ForgetTombstone>>
                ReadForgetTombstonesAsync(CancellationToken cancellationToken) =>
                ValueTask.FromResult<IReadOnlyCollection<ForgetTombstone>>([]);

            public ValueTask WriteForgetTombstoneAsync(
                ForgetTombstone tombstone,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException();

            public ValueTask ClearForgetTombstoneAsync(
                TargetId targetId,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException();
        }

        private sealed class EndpointSupport :
            IClock,
            IIdGenerator,
            INetworkPort,
            ITargetProbePort,
            ISessionPort,
            ITargetRuntimePort,
            IClipboardPort
        {
            public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

            public Guid NewId() => Guid.NewGuid();

            public ValueTask<NetworkCategory> GetCurrentCategoryAsync(
                CancellationToken cancellationToken) =>
                ValueTask.FromResult(NetworkCategory.Private);

            public ValueTask<TargetProbeResult> ProbeAsync(
                TargetEndpoint endpoint,
                CancellationToken cancellationToken) =>
                ValueTask.FromResult(new TargetProbeResult(
                    TargetProbeClassification.SupportedAuthenticatedHarness));

            public ValueTask<SessionMetadataReadResult> ReadAsync(
                TargetId targetId,
                TargetEndpoint endpoint,
                CancellationToken cancellationToken) =>
                ValueTask.FromResult(new SessionMetadataReadResult(
                    SessionMetadataStatus.Missing));

            public ValueTask MarkPairingInProgressAsync(
                TargetId targetId,
                TargetEndpoint endpoint,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException();

            public ValueTask CommitAsync(
                TargetId targetId,
                TargetEndpoint endpoint,
                DateTimeOffset committedAtUtc,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException();

            public ValueTask DeleteAsync(
                TargetId targetId,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException();

            public ValueTask DeletePairingStateAsync(
                TargetId targetId,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException();

            public ValueTask ResetUncommittedAsync(
                TargetId targetId,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException();

            public ValueTask<PairingHandshake> PairAsync(
                TargetId targetId,
                TargetEndpoint endpoint,
                ReadOnlyMemory<char> encodedToken,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException();

            public ValueTask<RuntimeOpenStatus> PrepareOpenAsync(
                TargetId targetId,
                TargetEndpoint endpoint,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException();

            public ValueTask CloseAsync(
                TargetId targetId,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException();

            public ValueTask DeleteSessionDataAsync(
                TargetId targetId,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException();

            public ValueTask<bool> SessionDataExistsAsync(
                TargetId targetId,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException();

            public ValueTask ClearIfUnchangedAsync(
                ReadOnlyMemory<char> pastedText,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException();
        }
    }
}
