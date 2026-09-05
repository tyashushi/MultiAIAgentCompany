using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Coordination;
using MultiAIAgentCompany.Core.Status;
using Xunit;
using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Tests;

public sealed class DepartmentStatusTrackerTests
{
    private readonly TestTimeProvider _clock = new(new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero));
    private readonly AgentRef _agent = new("engineering", AgentKind.CodexCli);

    [Fact]
    public void 沈黙はRestingにならず根拠が古くなればUnknownになる()
    {
        var tracker = Create();
        tracker.OnObserved(StructuredEvidence());

        _clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(ActivityState.Unknown, tracker.Current.Activity.Value);
        Assert.NotEqual(ActivityState.Resting, tracker.Current.Activity.Value);
    }

    [Fact]
    public void AwaitingApprovalは承認要求からしか出ない()
    {
        var tracker = Create();
        tracker.OnObserved(StructuredEvidence());
        tracker.OnObserved(StructuredEvidence());

        Assert.NotEqual(ActivityState.AwaitingApproval, tracker.Current.Activity.Value);
    }

    [Fact]
    public void ランタイム承認と相談は別の活動状態になる()
    {
        var tracker = Create();
        tracker.OnApprovalRequested(Request("runtime", ApprovalKind.Runtime));
        Assert.Equal(ActivityState.AwaitingApproval, tracker.Current.Activity.Value);

        tracker.OnApprovalRequested(Request("consultation", ApprovalKind.Consultation));
        Assert.Equal(ActivityState.Consulting, tracker.Current.Activity.Value);
    }

    [Fact]
    public void 承認を返してもWorkingへ戻さずUnknownになる()
    {
        var tracker = Create();
        tracker.OnApprovalRequested(Request("runtime", ApprovalKind.Runtime));
        tracker.OnApprovalResolved("runtime");

        Assert.Equal(ActivityState.Unknown, tracker.Current.Activity.Value);
    }

    [Theory]
    [InlineData(true, ActivityState.Resting)]
    [InlineData(false, ActivityState.Degraded)]
    public void TurnFinishedは成否に応じた活動状態になる(bool succeeded, ActivityState expected)
    {
        var tracker = Create();
        tracker.OnTurnFinished(new OutcomeVerdict(succeeded, "test"));

        Assert.Equal(expected, tracker.Current.Activity.Value);
    }

    [Fact]
    public void Dispatch根拠の観測はWorkingにしない()
    {
        var tracker = Create();
        tracker.OnObserved(Evidence(EvidenceSource.Dispatch));

        Assert.Equal(ActivityState.Unknown, tracker.Current.Activity.Value);
        Assert.Equal(RuntimeState.Running, tracker.Current.Runtime.Value);
    }

    [Fact]
    public void 異常終了は活動をUnknownにして稼働をFailedにする()
    {
        var tracker = Create();
        tracker.OnTurnFinished(new OutcomeVerdict(true, "test"));
        tracker.OnExited(1);

        Assert.Equal(RuntimeState.Failed, tracker.Current.Runtime.Value);
        Assert.Equal(ActivityState.Unknown, tracker.Current.Activity.Value);
        Assert.NotEqual(ActivityState.Resting, tracker.Current.Activity.Value);
    }

    [Fact]
    public void 仕事状態はDocument根拠でしか変わらない()
    {
        var tracker = Create();
        Assert.Throws<ArgumentException>(() => tracker.OnWorkStateChanged(CoreTaskStatus.InProgress, StructuredEvidence()));

        tracker.OnWorkStateChanged(CoreTaskStatus.InProgress, Evidence(EvidenceSource.Document));
        Assert.Equal(CoreTaskStatus.InProgress, tracker.Current.Work!.Value);
        Assert.Equal(EvidenceSource.Document, tracker.Current.Work.Evidence.Source);
    }

    [Fact]
    public void 各設定済み軸には根拠がある()
    {
        var tracker = Create();
        tracker.OnObserved(StructuredEvidence());
        tracker.OnWorkStateChanged(CoreTaskStatus.Dispatched, Evidence(EvidenceSource.Document));

        Assert.NotNull(tracker.Current.Runtime.Evidence);
        Assert.NotNull(tracker.Current.Activity.Evidence);
        Assert.NotNull(tracker.Current.Work!.Evidence);
    }

    [Fact]
    public void 同じ状態の再通知ではChangedを追加発火しない()
    {
        var tracker = Create();
        var changes = 0;
        tracker.Changed += (_, _) => changes++;

        tracker.OnObserved(StructuredEvidence());
        tracker.OnObserved(StructuredEvidence());

        Assert.Equal(1, changes);
    }

    [Theory]
    [InlineData(0, RuntimeState.Exited)]
    [InlineData(1, RuntimeState.Failed)]
    public void ExitCodeで稼働状態が決まる(int exitCode, RuntimeState expected)
    {
        var tracker = Create();
        tracker.OnExited(exitCode);

        Assert.Equal(expected, tracker.Current.Runtime.Value);
    }

    private DepartmentStatusTracker Create() => new(_agent, _clock, TimeSpan.FromMinutes(5));

    private Evidence StructuredEvidence() => Evidence(EvidenceSource.StructuredEvent);

    private Evidence Evidence(EvidenceSource source) => new(source, _clock.GetUtcNow(), "session", "turn", _agent, "0.153.4", "test", "test observation");

    private ApprovalRequest Request(string id, ApprovalKind kind) =>
        new(id, kind, AgentKind.CodexCli, "session", "turn", "title", "details", [], []);

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    [Fact]
    public void 承認まちは後続の観測で消えない()
    {
        // 承認待ちの最中にも構造化イベントは流れてくる。それで Working に戻すと、
        // 「人間が動かないと進まない」という事実が画面から消える（§3）。
        var tracker = Create();
        tracker.OnApprovalRequested(Request("r1", ApprovalKind.Runtime));

        tracker.OnObserved(StructuredEvidence());

        Assert.Equal(ActivityState.AwaitingApproval, tracker.Current.Activity.Value);
    }

    [Fact]
    public void 承認まちは時間が経っても消えない()
    {
        // §7 の「古い根拠は現在の証拠ではない」は推定した状態に効く規則であって、
        // 「人間の返事を待っている」という既知の事実には効かない。1時間悩むのは正常。
        var tracker = Create();
        tracker.OnApprovalRequested(Request("r1", ApprovalKind.Runtime));

        _clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(ActivityState.AwaitingApproval, tracker.Current.Activity.Value);
    }

    [Fact]
    public void 承認に答えれば時間で消えるようになる()
    {
        var tracker = Create();
        tracker.OnApprovalRequested(Request("r1", ApprovalKind.Runtime));
        tracker.OnApprovalResolved("r1");

        _clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(ActivityState.Unknown, tracker.Current.Activity.Value);
    }

}
