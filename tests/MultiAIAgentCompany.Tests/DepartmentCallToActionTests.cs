using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Status;
using Xunit;
using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Tests;

/// <summary>設計 §15。3軸を混ぜず、層にする。</summary>
public sealed class DepartmentCallToActionTests
{
    [Fact]
    public void 働いていても別の仕事の報告まちは隠れない()
    {
        // 混ぜると片方が消える。層にしてあるので両方読める（§15-1）。
        var action = Of(RuntimeState.Running, ActivityState.Working, CoreTaskStatus.Reported);

        Assert.Equal(DepartmentPose.Working, action.Pose);
        Assert.Equal(DepartmentBadge.NeedsAcceptance, action.Badge);
        Assert.True(action.NeedsHuman);
    }

    [Fact]
    public void 倒れていても仕事の状態は残る()
    {
        var action = Of(RuntimeState.Failed, ActivityState.Unknown, CoreTaskStatus.Reported);

        Assert.Equal(DepartmentRuntimeMark.Down, action.RuntimeMark);
        Assert.Equal(DepartmentBadge.NeedsAcceptance, action.Badge);
        // 落ちたことをポーズで表さない。ポーズは活動状態の担当（§15-4）。
        Assert.Equal(DepartmentPose.Unknown, action.Pose);
    }

    [Fact]
    public void 承認まちと相談中は別のポーズになる()
    {
        // §3。人間の行き先が違う —— ターミナル／承認ボタン か、.company/ のドキュメントか。
        Assert.Equal(DepartmentPose.AwaitingApproval,
            Of(RuntimeState.Running, ActivityState.AwaitingApproval, null).Pose);
        Assert.Equal(DepartmentPose.Consulting,
            Of(RuntimeState.Running, ActivityState.Consulting, null).Pose);
    }

    [Fact]
    public void 承認まちにバッジを重ねない()
    {
        // ポーズが既に「許可を待っている」と言っているので、二重に出さない（§15-3）。
        var action = Of(RuntimeState.Running, ActivityState.AwaitingApproval, CoreTaskStatus.InProgress);

        Assert.Equal(DepartmentBadge.None, action.Badge);
    }

    [Fact]
    public void 送ったかもしれない仕事は再起動を跨いだときだけ確認を促す()
    {
        // Dispatched は「送ったかもしれない」を意味し、自動再送しない（§14-1）。
        // ただし通常運転中の Dispatched は人間の出番ではない。
        Assert.Equal(DepartmentBadge.None,
            Of(RuntimeState.Running, ActivityState.Working, CoreTaskStatus.Dispatched).Badge);
        Assert.Equal(DepartmentBadge.NeedsDeliveryCheck,
            Of(RuntimeState.Running, ActivityState.Unknown, CoreTaskStatus.Dispatched, dispatchedAcrossRestart: true).Badge);
    }

    [Fact]
    public void 何も要らないときは人間を呼ばない()
    {
        var action = Of(RuntimeState.Running, ActivityState.Working, CoreTaskStatus.InProgress);

        Assert.False(action.NeedsHuman);
    }

    [Fact]
    public void 休んでいると分からないと倒れたは別物()
    {
        // 実測（§7）で Gemini CLI が1時間無言で生存した。この3つの取り違えが最も高くつく。
        var resting = Of(RuntimeState.Running, ActivityState.Resting, null);
        var unknown = Of(RuntimeState.Running, ActivityState.Unknown, null);
        var down = Of(RuntimeState.Failed, ActivityState.Unknown, null);

        Assert.Equal(DepartmentPose.Resting, resting.Pose);
        Assert.Equal(DepartmentPose.Unknown, unknown.Pose);
        Assert.Equal(DepartmentRuntimeMark.None, unknown.RuntimeMark);
        Assert.Equal(DepartmentRuntimeMark.Down, down.RuntimeMark);
        Assert.False(resting.NeedsHuman);
    }

    private static DepartmentCallToAction Of(
        RuntimeState runtime, ActivityState activity, CoreTaskStatus? work, bool dispatchedAcrossRestart = false)
    {
        var evidence = new Evidence(EvidenceSource.StructuredEvent, DateTimeOffset.UnixEpoch, null, null,
            new AgentRef("実装", AgentKind.CodexCli), null, null, "test");
        var status = new DepartmentStatus(
            new Observed<RuntimeState>(runtime, evidence),
            new Observed<ActivityState>(activity, evidence),
            work is null ? null : new Observed<CoreTaskStatus>(work.Value, evidence));

        return DepartmentCallToAction.From(status, dispatchedAcrossRestart);
    }
}
