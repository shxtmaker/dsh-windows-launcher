using DshLauncher.Core.Attachments;
using DshLauncher.Core.Remote;
using Xunit;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>
/// D16 手势授权的平台中立测试（Linux 真实执行）：铸造条件、绑定维度、短命窗口、
/// 一次性消费与确定拒绝码。真实键盘/焦点/窗口事件属 WindowsPending，这里只验证规则本身。
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
public sealed class NativePasteGestureAuthorizationTests
{
    private const int Life = NativeGestureAuthorizer.DefaultLifetimeMs;

    [Theory]
    [InlineData(false, true, true, true, NativePasteCodes.GestureWindowHidden)]
    [InlineData(true, false, true, true, NativePasteCodes.GestureWindowInactive)]
    [InlineData(true, true, false, true, NativePasteCodes.GestureFocusOutsidePage)]
    [InlineData(true, true, true, false, NativePasteCodes.GestureTabNotActive)]
    public void MintRequiresEveryWindowTabAndFocusFact(
        bool visible,
        bool active,
        bool focusInside,
        bool tabMatches,
        string expectedCode)
    {
        var authorizer = new NativeGestureAuthorizer();
        var evidence = NativePasteFixture.Evidence(
            windowVisible: visible,
            windowActive: active,
            focusInsidePage: focusInside,
            activeTabMatches: tabMatches);

        var decision = authorizer.Mint(evidence, nowMs: 0);

        Assert.False(decision.Authorized);
        Assert.Equal(expectedCode, decision.Code);
        Assert.Equal(0, authorizer.LiveCount);
    }

    [Fact]
    public void MintBindsTargetEpochFocusAndExpiry()
    {
        var authorizer = new NativeGestureAuthorizer();

        var decision = authorizer.Mint(NativePasteFixture.Evidence(), nowMs: 1_000);

        Assert.True(decision.Authorized);
        Assert.Equal(NativePasteCodes.AuthorizationGranted, decision.Code);
        var authorization = decision.Authorization;
        Assert.Equal(NativePasteFixture.TargetA, authorization.TargetId);
        Assert.Equal(1, authorization.PageEpoch.Value);
        Assert.Equal(NativePasteFixture.FocusA, authorization.FocusToken);
        Assert.Equal(1_000, authorization.IssuedAtMs);
        Assert.Equal(1_000 + Life, authorization.ExpiresAtMs);
    }

    [Fact]
    public void MintRefusesASecondAuthorizationForTheSameGesture()
    {
        var authorizer = new NativeGestureAuthorizer();
        var evidence = NativePasteFixture.Evidence();
        Assert.True(authorizer.Mint(evidence, nowMs: 0).Authorized);

        var second = authorizer.Mint(evidence, nowMs: 10);

        Assert.False(second.Authorized);
        Assert.Equal(NativePasteCodes.GestureAlreadyMinted, second.Code);
    }

    [Theory]
    [InlineData("", NativePasteCodes.GestureIdInvalid)]
    [InlineData("   ", NativePasteCodes.GestureIdInvalid)]
    public void MintRefusesAnEmptyGestureId(string gestureId, string expectedCode)
    {
        var authorizer = new NativeGestureAuthorizer();

        var decision = authorizer.Mint(NativePasteFixture.Evidence(gestureId: gestureId), nowMs: 0);

        Assert.False(decision.Authorized);
        Assert.Equal(expectedCode, decision.Code);
    }

    [Fact]
    public void MintRefusesAnEmptyTarget()
    {
        var authorizer = new NativeGestureAuthorizer();

        var decision = authorizer.Mint(NativePasteFixture.Evidence(targetId: Guid.Empty), nowMs: 0);

        Assert.False(decision.Authorized);
        Assert.Equal(NativePasteCodes.GestureTargetInvalid, decision.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(NativeGestureAuthorizer.MaxLifetimeMs + 1)]
    public void MintRefusesALifetimeBeyondTheBound(int lifetimeMs)
    {
        var authorizer = new NativeGestureAuthorizer();

        var decision = authorizer.Mint(NativePasteFixture.Evidence(), nowMs: 0, lifetimeMs);

        Assert.False(decision.Authorized);
        Assert.Equal(NativePasteCodes.GestureLifetimeTooLong, decision.Code);
    }

    [Fact]
    public void AuthorizeConsumesTheAuthorizationExactlyOnce()
    {
        var authorizer = new NativeGestureAuthorizer();
        var authorization = authorizer.Mint(NativePasteFixture.Evidence(), nowMs: 0).Authorization;
        var binding = new NativeGestureBinding(NativePasteFixture.TargetA, new RemotePageEpoch(1), NativePasteFixture.FocusA);

        var first = authorizer.Authorize(authorization, binding, nowMs: 10);
        var replay = authorizer.Authorize(authorization, binding, nowMs: 11);

        Assert.True(first.Authorized);
        Assert.Equal(NativePasteCodes.AuthorizationGranted, first.Code);
        Assert.False(replay.Authorized);
        Assert.Equal(NativePasteCodes.AuthorizationReplayed, replay.Code);
        Assert.Equal(0, authorizer.LiveCount);
        Assert.Equal(1, authorizer.ConsumedCount);
    }

    [Fact]
    public void AuthorizeRefusesAnExpiredAuthorization()
    {
        var authorizer = new NativeGestureAuthorizer();
        var authorization = authorizer.Mint(NativePasteFixture.Evidence(), nowMs: 0).Authorization;
        var binding = new NativeGestureBinding(NativePasteFixture.TargetA, new RemotePageEpoch(1), NativePasteFixture.FocusA);

        var decision = authorizer.Authorize(authorization, binding, nowMs: Life + 1);

        Assert.False(decision.Authorized);
        Assert.Equal(NativePasteCodes.AuthorizationExpired, decision.Code);
    }

    [Fact]
    public void AuthorizeRefusesATargetFromAnotherTabOrWindow()
    {
        var authorizer = new NativeGestureAuthorizer();
        var authorization = authorizer.Mint(NativePasteFixture.Evidence(), nowMs: 0).Authorization;

        var otherTarget = authorizer.Authorize(
            authorization,
            new NativeGestureBinding(NativePasteFixture.TargetB, new RemotePageEpoch(1), NativePasteFixture.FocusB),
            nowMs: 5);

        Assert.False(otherTarget.Authorized);
        Assert.Equal(NativePasteCodes.AuthorizationTargetMismatch, otherTarget.Code);
    }

    [Fact]
    public void AuthorizeRefusesAStalePageEpoch()
    {
        var authorizer = new NativeGestureAuthorizer();
        var authorization = authorizer.Mint(NativePasteFixture.Evidence(epoch: 1), nowMs: 0).Authorization;

        var decision = authorizer.Authorize(
            authorization,
            new NativeGestureBinding(NativePasteFixture.TargetA, new RemotePageEpoch(2), NativePasteFixture.FocusA),
            nowMs: 5);

        Assert.False(decision.Authorized);
        Assert.Equal(NativePasteCodes.AuthorizationEpochMismatch, decision.Code);
    }

    [Fact]
    public void AuthorizeRefusesAChangedFocusToken()
    {
        var authorizer = new NativeGestureAuthorizer();
        var authorization = authorizer.Mint(NativePasteFixture.Evidence(), nowMs: 0).Authorization;

        var decision = authorizer.Authorize(
            authorization,
            new NativeGestureBinding(NativePasteFixture.TargetA, new RemotePageEpoch(1), "window-9:page-z"),
            nowMs: 5);

        Assert.False(decision.Authorized);
        Assert.Equal(NativePasteCodes.AuthorizationFocusMismatch, decision.Code);
    }

    [Fact]
    public void AuthorizeRefusesAnAuthorizationThatWasNeverMinted()
    {
        var authorizer = new NativeGestureAuthorizer();
        var forged = new NativeGestureAuthorization("gesture-forged", NativePasteFixture.TargetA, new RemotePageEpoch(1), NativePasteFixture.FocusA, 0, 10_000);

        var decision = authorizer.Authorize(
            forged,
            new NativeGestureBinding(NativePasteFixture.TargetA, new RemotePageEpoch(1), NativePasteFixture.FocusA),
            nowMs: 5);

        Assert.False(decision.Authorized);
        Assert.Equal(NativePasteCodes.AuthorizationUnknown, decision.Code);
    }

    [Fact]
    public void AuthorizeRefusesATamperedCopyOfALiveAuthorization()
    {
        var authorizer = new NativeGestureAuthorizer();
        var authorization = authorizer.Mint(NativePasteFixture.Evidence(), nowMs: 0).Authorization;
        var tampered = authorization with { ExpiresAtMs = authorization.ExpiresAtMs + 60_000 };

        var decision = authorizer.Authorize(
            tampered,
            new NativeGestureBinding(NativePasteFixture.TargetA, new RemotePageEpoch(1), NativePasteFixture.FocusA),
            nowMs: 5);

        Assert.False(decision.Authorized);
        Assert.Equal(NativePasteCodes.AuthorizationUnknown, decision.Code);
        // 合法的那一份仍然可用：噪声请求不会把授权烧掉。
        Assert.True(authorizer.Authorize(
            authorization,
            new NativeGestureBinding(NativePasteFixture.TargetA, new RemotePageEpoch(1), NativePasteFixture.FocusA),
            nowMs: 6).Authorized);
    }

    [Fact]
    public void ANonMatchingRequestDoesNotConsumeTheValidAuthorization()
    {
        var authorizer = new NativeGestureAuthorizer();
        var authorization = authorizer.Mint(NativePasteFixture.Evidence(), nowMs: 0).Authorization;
        var stray = authorizer.Authorize(
            authorization,
            new NativeGestureBinding(NativePasteFixture.TargetA, new RemotePageEpoch(7), NativePasteFixture.FocusA),
            nowMs: 1);
        Assert.False(stray.Authorized);

        var valid = authorizer.Authorize(
            authorization,
            new NativeGestureBinding(NativePasteFixture.TargetA, new RemotePageEpoch(1), NativePasteFixture.FocusA),
            nowMs: 2);

        Assert.True(valid.Authorized);
    }

    [Fact]
    public void AuthorizeRefusesAClockRollback()
    {
        var authorizer = new NativeGestureAuthorizer();
        var authorization = authorizer.Mint(NativePasteFixture.Evidence(), nowMs: 5_000).Authorization;

        var decision = authorizer.Authorize(
            authorization,
            new NativeGestureBinding(NativePasteFixture.TargetA, new RemotePageEpoch(1), NativePasteFixture.FocusA),
            nowMs: 4_000);

        Assert.False(decision.Authorized);
        Assert.Equal(NativePasteCodes.AuthorizationNotYetValid, decision.Code);
    }

    [Fact]
    public void RevokeInvalidatesALiveAuthorizationWithoutConsumingIt()
    {
        var authorizer = new NativeGestureAuthorizer();
        var authorization = authorizer.Mint(NativePasteFixture.Evidence(), nowMs: 0).Authorization;

        Assert.True(authorizer.Revoke(authorization.GestureId));
        var decision = authorizer.Authorize(
            authorization,
            new NativeGestureBinding(NativePasteFixture.TargetA, new RemotePageEpoch(1), NativePasteFixture.FocusA),
            nowMs: 1);

        Assert.False(decision.Authorized);
        Assert.Equal(NativePasteCodes.AuthorizationUnknown, decision.Code);
        Assert.Null(authorizer.AuthorizationFor(authorization.GestureId));
    }

    [Fact]
    public void PruneExpiredDropsLiveAuthorizationsAndStillReportsExpiry()
    {
        var authorizer = new NativeGestureAuthorizer();
        var authorization = authorizer.Mint(NativePasteFixture.Evidence(), nowMs: 0).Authorization;

        Assert.Equal(1, authorizer.PruneExpired(Life + 1));
        Assert.Equal(0, authorizer.LiveCount);
        var decision = authorizer.Authorize(
            authorization,
            new NativeGestureBinding(NativePasteFixture.TargetA, new RemotePageEpoch(1), NativePasteFixture.FocusA),
            nowMs: Life + 2);

        Assert.False(decision.Authorized);
        Assert.Equal(NativePasteCodes.AuthorizationExpired, decision.Code);
    }

    [Fact]
    public void TheAuthorizationLedgerIsBounded()
    {
        var authorizer = new NativeGestureAuthorizer();
        for (var index = 0; index < NativeGestureAuthorizer.MaxTrackedGestures; index++)
        {
            Assert.True(authorizer.Mint(NativePasteFixture.Evidence(gestureId: $"gesture-{index}"), nowMs: 0).Authorized);
        }

        var overflow = authorizer.Mint(NativePasteFixture.Evidence(gestureId: "gesture-overflow"), nowMs: 0);

        Assert.False(overflow.Authorized);
        Assert.Equal(NativePasteCodes.GestureRegistryFull, overflow.Code);
    }

    [Fact]
    public void RevokeAllClearsEveryLiveAuthorization()
    {
        var authorizer = new NativeGestureAuthorizer();
        authorizer.Mint(NativePasteFixture.Evidence(gestureId: "g1"), nowMs: 0);
        authorizer.Mint(NativePasteFixture.Evidence(gestureId: "g2"), nowMs: 0);

        authorizer.RevokeAll();

        Assert.Equal(0, authorizer.LiveCount);
        Assert.Equal(0, authorizer.ConsumedCount);
    }

    [Fact]
    public void AuthorizationLifetimeIsShortByDesign()
    {
        Assert.InRange(NativeGestureAuthorizer.DefaultLifetimeMs, 1, 5_000);
        Assert.Equal(5_000, NativeGestureAuthorizer.MaxLifetimeMs);
    }
}
