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
        var action = Of(RuntimeState.Running, ActivityState.Working, CoreTaskStatus.InProgress, running: true);

        Assert.False(action.NeedsHuman);
    }

    [Fact]
    public void 休んでいると分からないと倒れたは別物()
    {
        // 実測（§7）で Gemini CLI が1時間無言で生存した。この3つの取り違えが最も高くつく。
        var resting = Of(RuntimeState.Running, ActivityState.Resting, null, running: true);
        var unknown = Of(RuntimeState.Running, ActivityState.Unknown, null, running: true);
        var down = Of(RuntimeState.Failed, ActivityState.Unknown, null, running: true);

        Assert.Equal(DepartmentPose.Resting, resting.Pose);
        Assert.Equal(DepartmentPose.Unknown, unknown.Pose);
        Assert.Equal(DepartmentRuntimeMark.None, unknown.RuntimeMark);
        Assert.Equal(DepartmentRuntimeMark.Down, down.RuntimeMark);
        Assert.False(resting.NeedsHuman);
    }

    private static DepartmentCallToAction Of(
        RuntimeState runtime, ActivityState activity, CoreTaskStatus? work,
        bool dispatchedAcrossRestart = false, bool running = false, bool reportNotObservedByDeadline = false,
        bool externalTerminal = false)
    {
        var evidence = new Evidence(EvidenceSource.StructuredEvent, DateTimeOffset.UnixEpoch, null, null,
            new AgentRef("実装", AgentKind.CodexCli), null, null, "test");
        var status = new DepartmentStatus(
            new Observed<RuntimeState>(runtime, evidence),
            new Observed<ActivityState>(activity, evidence),
            work is null ? null : new Observed<CoreTaskStatus>(work.Value, evidence));

        return DepartmentCallToAction.From(
            status, dispatchedAcrossRestart, running, reportNotObservedByDeadline, externalTerminal);
    }
    [Fact]
    public void 相談は活動からでも仕事からでも同じ用件になる()
    {
        // (b) の相談は経路が2つある —— エージェントが出す Consulting（活動）と、
        // .company/ に現れる AwaitingAnswer（仕事）。片方だけを条件にすると、
        // 「要対応と出ているのに押すものが無い」状態ができる（§15-6）。
        Assert.Equal(DepartmentAction.AnswerQuestion,
            Of(RuntimeState.Running, ActivityState.Consulting, CoreTaskStatus.InProgress, running: true).Action);
        Assert.Equal(DepartmentAction.AnswerQuestion,
            Of(RuntimeState.Running, ActivityState.Working, CoreTaskStatus.AwaitingAnswer, running: true).Action);
    }

    [Fact]
    public void 落ちたときは原因を見るであって再起動ではない()
    {
        // §15-4 は「原因を見て、再起動するか決める」。UI が復旧の判断を先取りしない。
        Assert.Equal(DepartmentAction.Investigate,
            Of(RuntimeState.Failed, ActivityState.Unknown, null, running: false).Action);
    }

    [Fact]
    public void 意図した終了は原因を見るに入れない()
    {
        // 終わった部門に報告が残っているなら、急ぐのは報告を読むこと。
        Assert.Equal(DepartmentAction.ReadReport,
            Of(RuntimeState.Exited, ActivityState.Unknown, CoreTaskStatus.Reported, running: false).Action);
    }

    [Fact]
    public void 何もすることが無ければボタンを出さない()
    {
        Assert.Equal(DepartmentAction.None,
            Of(RuntimeState.Running, ActivityState.Working, CoreTaskStatus.InProgress, running: true).Action);
    }

    [Fact]
    public void 動いていなければ別枠で起動を促す()
    {
        var action = Of(RuntimeState.Running, ActivityState.Unknown, null, running: false);

        Assert.Equal(DepartmentAction.None, action.Action);
        Assert.Equal(DepartmentLifecycle.Start, action.Lifecycle);
    }

    [Fact]
    public void 仕事の用件があっても起動できる()
    {
        // ここが実機で詰まった場所。回答の配達にはセッションが要るのに、
        // 用件ボタンが1つしかないと「起動する」が永久に隠れる（§15-6）。
        var action = Of(RuntimeState.Running, ActivityState.Unknown, CoreTaskStatus.AwaitingAnswer, running: false);

        Assert.Equal(DepartmentAction.AnswerQuestion, action.Action);
        Assert.Equal(DepartmentLifecycle.Start, action.Lifecycle);
    }

    [Fact]
    public void 落ちているときは起動ボタンを出さない()
    {
        // §15-4 は「原因を見て、再起動するか決める」。すぐ横に起動を置くとその判断を飛ばさせる。
        var action = Of(RuntimeState.Failed, ActivityState.Unknown, null, running: false);

        Assert.Equal(DepartmentAction.Investigate, action.Action);
        Assert.Equal(DepartmentLifecycle.None, action.Lifecycle);
    }

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void 要対応とボタンは必ず一致する(
        RuntimeState runtime, ActivityState activity, CoreTaskStatus? work, bool acrossRestart, bool running)
    {
        // **片方だけを直すと「要対応と出ているのに押すものが無い」が生まれる。**
        // 全組み合わせで一致することを機械で確かめる（§15-6）。
        var action = Of(runtime, activity, work, acrossRestart, running);

        Assert.Equal(action.Action is not DepartmentAction.None, action.NeedsHuman);
    }

    [Theory]
    [InlineData(CoreTaskStatus.Dispatched)]
    [InlineData(CoreTaskStatus.InProgress)]
    public void 期限までに報告を観測していなければ確認を促す(CoreTaskStatus work)
    {
        var action = Of(RuntimeState.Running, ActivityState.Working, work,
            running: true, reportNotObservedByDeadline: true);

        Assert.Equal(DepartmentBadge.ReportNotObservedByDeadline, action.Badge);
        Assert.Equal(DepartmentAction.CheckMissingReport, action.Action);
        Assert.True(action.NeedsHuman);
    }

    [Fact]
    public void 沈黙より再起動を跨いだ送信確認が勝つ()
    {
        var action = Of(RuntimeState.Running, ActivityState.Working, CoreTaskStatus.Dispatched,
            dispatchedAcrossRestart: true, running: true, reportNotObservedByDeadline: true);

        Assert.Equal(DepartmentBadge.NeedsDeliveryCheck, action.Badge);
        Assert.Equal(DepartmentAction.CheckDelivery, action.Action);
    }

    [Fact]
    public void 報告済みの仕事に沈黙は効かない()
    {
        var action = Of(RuntimeState.Running, ActivityState.Working, CoreTaskStatus.Reported,
            running: true, reportNotObservedByDeadline: true);

        Assert.Equal(DepartmentBadge.NeedsAcceptance, action.Badge);
        Assert.Equal(DepartmentAction.ReadReport, action.Action);
    }

    [Fact]
    public void 沈黙より稼働が落ちた原因を見る用件が勝つ()
    {
        var action = Of(RuntimeState.Failed, ActivityState.Unknown, CoreTaskStatus.Dispatched,
            reportNotObservedByDeadline: true);

        Assert.Equal(DepartmentBadge.ReportNotObservedByDeadline, action.Badge);
        Assert.Equal(DepartmentAction.Investigate, action.Action);
    }

    public static TheoryData<RuntimeState, ActivityState, CoreTaskStatus?, bool, bool> AllCombinations()
    {
        var data = new TheoryData<RuntimeState, ActivityState, CoreTaskStatus?, bool, bool>();
        foreach (var runtime in Enum.GetValues<RuntimeState>())
        foreach (var activity in Enum.GetValues<ActivityState>())
        foreach (var work in Enum.GetValues<CoreTaskStatus>().Cast<CoreTaskStatus?>().Append(null))
        foreach (var acrossRestart in new[] { false, true })
        foreach (var running in new[] { false, true })
        {
            data.Add(runtime, activity, work, acrossRestart, running);
        }

        return data;
    }
    [Fact]
    public void 渡していない仕事には渡すボタンを出す()
    {
        // 実機で詰まった。部門が起動していないと dispatch が拒否され、Drafted のまま残り、
        // 提案カードも消えているので先へ進める手段が無かった（§15-6）。
        var action = Of(RuntimeState.Running, ActivityState.Unknown, CoreTaskStatus.Drafted, running: true);

        Assert.Equal(DepartmentAction.DispatchTask, action.Action);
    }

    [Fact]
    public void 送信確認は渡すより優先される()
    {
        // Dispatched は §14-1 の復旧契約なので、Drafted で隠さない。
        var action = Of(RuntimeState.Running, ActivityState.Unknown, CoreTaskStatus.Dispatched,
            dispatchedAcrossRestart: true, running: true);

        Assert.Equal(DepartmentAction.CheckDelivery, action.Action);
    }

    [Fact]
    public void 差し戻しには渡すボタンではなく送り直すボタンを出す()
    {
        // **Rejected → Dispatched は現在の instruction.md を attempts/ へ封じる。**
        // dispatch は先に読んでから遷移するので、そのまま当てると古い指示を送ったうえ、
        // 新しい試行に指示書が残らない。順序を変えた別経路にする（§19-1）。
        var action = Of(RuntimeState.Running, ActivityState.Unknown, CoreTaskStatus.Rejected, running: true);

        Assert.Equal(DepartmentAction.RedispatchTask, action.Action);
    }

    [Fact]
    public void 差し戻しは送信確認と質問に隠される()
    {
        // 送り直しは §14-1 の復旧契約より下。急ぐのは「届いたか」の方（§15-6 の順序）。
        var action = Of(RuntimeState.Running, ActivityState.Consulting, CoreTaskStatus.Rejected, running: true);

        Assert.Equal(DepartmentAction.AnswerQuestion, action.Action);
    }



    [Fact]
    public void 外部ターミナルの部門には起動を出さない()
    {
        // **窓は仕事を渡したときに開く**（設計 §32）。起動しても渡す手段が無いので、
        // 押しても何も起きないボタンを置かない（§15-6）。
        var call = Of(RuntimeState.Running, ActivityState.Unknown, work: null, externalTerminal: true);

        Assert.Equal(DepartmentLifecycle.None, call.Lifecycle);
    }

    [Fact]
    public void 外部ターミナルの部門は開いている間だけ前面に出せる()
    {
        var call = Of(RuntimeState.Running, ActivityState.Unknown, work: null,
            running: true, externalTerminal: true);

        Assert.Equal(DepartmentLifecycle.Focus, call.Lifecycle);
    }

    [Fact]
    public void 構造化の部門はこれまでどおり起動を出す()
    {
        var call = Of(RuntimeState.Running, ActivityState.Unknown, work: null);

        Assert.Equal(DepartmentLifecycle.Start, call.Lifecycle);
    }
}
