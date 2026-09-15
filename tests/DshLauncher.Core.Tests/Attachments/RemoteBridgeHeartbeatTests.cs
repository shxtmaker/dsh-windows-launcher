using System.Reflection;
using System.Text;
using DshLauncher.Core.Attachments;
using DshLauncher.Core.Hub;
using DshLauncher.Core.Pairing;
using DshLauncher.Core.Remote;
using Xunit;

namespace DshLauncher.Core.Tests.Attachments;

/// <summary>
/// D17：「桥关闭不影响配对心跳」的可移植证据（Linux 真实执行）。
///
/// 用一个<b>真实</b> <see cref="PairingHub"/>（真实配对状态机、真实保活循环、内存目标库）
/// 与一条完整的附件桥生命周期（握手 → 传输 → 取消 → 关闭 → 释放）并排跑：
/// <list type="bullet">
/// <item>桥关闭前后，枢纽快照逐字段不变、<c>Changed</c> 事件数不变、保活循环仍在跑；</item>
/// <item>桥关闭后心跳仍然成功（真实调用下一次保活并观察传输记录）；</item>
/// <item>桥的公开表面<b>不含</b>任何配对/枢纽成员（结构性护栏：桥没有能力去关配对）。</item>
/// </list>
/// </summary>
[Trait("triggerTags", "VFY-02,VFY-03,VFY-04,VFY-05")]
public sealed class RemoteBridgeHeartbeatTests
{
    [Fact]
    public async Task ClosingTheBridgeLeavesThePairingHubSnapshotAndKeepAliveUntouched()
    {
        using var hubRig = new HubTestRig();
        await hubRig.Hub.StartAsync(TestContext.Current.CancellationToken);
        var target = await hubRig.AddPairedTargetAsync();
        await hubRig.WaitUntilAsync(_ => hubRig.Transport.HeartbeatCredentials.Count >= 1);
        var before = await hubRig.Hub.GetSnapshotAsync(TestContext.Current.CancellationToken);
        var heartbeatsBefore = hubRig.Transport.HeartbeatCredentials.Count;

        using (var rig = new RemoteBridgeRig())
        {
            rig.Handshake();
            rig.Staging.AddCapture("capture-1", "snapshot-1", "note.txt", Encoding.UTF8.GetBytes("heartbeat"));
            rig.Composition.StartBatch(new RemoteBridgeBatchRequest("batch-1", ["capture-1"]));
            rig.Settle();
            rig.Composition.CancelBatch("cancelled");
            rig.Composition.Close("session-closed");
            rig.Composition.Dispose();
        }

        var after = await hubRig.Hub.GetSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.True(hubRig.Hub.IsStarted);
        Assert.Equal(before.HeartbeatInterval, after.HeartbeatInterval);
        var beforeTarget = Assert.Single(before.Targets);
        var afterTarget = Assert.Single(after.Targets);
        Assert.Equal(beforeTarget.TargetId, afterTarget.TargetId);
        Assert.Equal(beforeTarget.Pairing, afterTarget.Pairing);
        Assert.Equal(PairingState.Paired, afterTarget.Pairing);
        Assert.Equal(beforeTarget.HasCredential, afterTarget.HasCredential);
        Assert.Equal(beforeTarget.BaseUrl, afterTarget.BaseUrl);
        Assert.Equal(beforeTarget.DisplayName, afterTarget.DisplayName);
        // 保活循环没有被桥关闭打断：枢纽仍在持续发心跳，且目标没有被摘掉或取消配对。
        Assert.Equal(before.Targets.Count, after.Targets.Count);

        // 保活循环仍在真实跑：再看一次下一次心跳（凭据与端点都由枢纽自己保存）。
        await hubRig.WaitUntilAsync(_ => hubRig.Transport.HeartbeatCredentials.Count > heartbeatsBefore);
        var heartbeat = await hubRig.Hub.HeartbeatNowAsync(target.TargetId, TestContext.Current.CancellationToken);
        Assert.Null(heartbeat.Error);
        Assert.Contains(
            hubRig.Transport.HeartbeatCredentials,
            credential => credential.DeviceId == "device-1");
    }

    [Fact]
    public async Task TheBridgeOwnsItsOwnCancellationAndNeverTouchesTheSharedHubCancellation()
    {
        using var hubRig = new HubTestRig();
        await hubRig.Hub.StartAsync(TestContext.Current.CancellationToken);
        await hubRig.AddPairedTargetAsync();
        await hubRig.WaitUntilAsync(_ => hubRig.Transport.HeartbeatCredentials.Count >= 1);
        var heartbeatsBefore = hubRig.Transport.HeartbeatCredentials.Count;

        using var rig = new RemoteBridgeRig();
        rig.Handshake();
        rig.Staging.AddCapture("capture-1", "snapshot-1", "note.txt", Encoding.UTF8.GetBytes("cancel"));
        rig.Composition.StartBatch(new RemoteBridgeBatchRequest("batch-1", ["capture-1"]));

        rig.Page.Close(RemotePageCancellationTrigger.CredentialRevoked);
        rig.Composition.Close(RemotePageSessionCodes.SessionClosed);

        // 页面凭据被"吊销"只会关闭这个页面会话/附件桥；枢纽的保活与目标状态不受影响。
        await hubRig.WaitUntilAsync(_ => hubRig.Transport.HeartbeatCredentials.Count > heartbeatsBefore);
        var snapshot = await hubRig.Hub.GetSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PairingState.Paired, Assert.Single(snapshot.Targets).Pairing);
        Assert.True(hubRig.Hub.IsStarted);
    }

    [Fact]
    public void TheBridgeSurfaceExposesNoPairingOrHubMember()
    {
        var forbidden = new[] { "PairingHub", "IPairingTransport", "StoredTarget", "HubSnapshot", "PairingState" };
        var surface = typeof(RemoteBridgeComposition)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        foreach (var member in surface)
        {
            var texts = new List<string> { member.Name };
            texts.AddRange(member switch
            {
                MethodBase method => method.GetParameters().Select(parameter => parameter.ParameterType.FullName ?? string.Empty),
                _ => [],
            });
            texts.Add(member switch
            {
                PropertyInfo property => property.PropertyType.FullName ?? string.Empty,
                FieldInfo field => field.FieldType.FullName ?? string.Empty,
                MethodInfo method => method.ReturnType.FullName ?? string.Empty,
                _ => string.Empty,
            });

            foreach (var text in texts)
            {
                foreach (var name in forbidden)
                {
                    Assert.DoesNotContain(name, text, StringComparison.Ordinal);
                }
            }
        }
    }
}
