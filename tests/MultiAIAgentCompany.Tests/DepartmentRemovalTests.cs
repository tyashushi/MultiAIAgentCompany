using MultiAIAgentCompany.Core.Coordination;
using Xunit;
using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// 部門を消してよいかの判定（設計 §47）。
/// </summary>
/// <remarks>
/// <b>消すのは定義であって、記録ではない。</b> ここで見ているのは
/// **「消したあと、人間が操作できなくなるものが残らないか」**である。
/// </remarks>
public sealed class DepartmentRemovalTests
{
    [Fact]
    public void 何も抱えていなければ消せる()
    {
        Assert.IsType<DepartmentRemovalDecision.Allowed>(Decide());
    }

    [Fact]
    public void 終わった仕事だけなら消せる()
    {
        Assert.IsType<DepartmentRemovalDecision.Allowed>(Decide(tasks:
        [
            Task("task-1", CoreTaskStatus.Accepted),
            Task("task-2", CoreTaskStatus.Cancelled),
            Task("task-3", CoreTaskStatus.Failed),
        ]));
    }

    [Theory]
    [InlineData(CoreTaskStatus.Drafted)]
    [InlineData(CoreTaskStatus.Dispatched)]
    [InlineData(CoreTaskStatus.InProgress)]
    [InlineData(CoreTaskStatus.AwaitingAnswer)]
    [InlineData(CoreTaskStatus.Reported)]
    [InlineData(CoreTaskStatus.Rejected)]
    public void 終わっていない仕事があれば消せない(CoreTaskStatus status)
    {
        // **`Drafted` も数える。** まだ渡していないだけで、既に departmentId を持つ仕事である。
        var blocked = Assert.IsType<DepartmentRemovalDecision.Blocked>(Decide(tasks: [Task("task-1", status)]));
        Assert.Contains(blocked.Reasons, reason => reason.Contains("task-1", StringComparison.Ordinal));
    }

    [Fact]
    public void 他の部門の仕事は数えない()
    {
        var other = new TaskState(
            "task-other", CoreTaskStatus.Dispatched, 0, 1, "他の部門",
            TransitionOrigin.Automation, DateTimeOffset.UnixEpoch);

        Assert.IsType<DepartmentRemovalDecision.Allowed>(Decide(tasks: [other]));
    }

    [Fact]
    public void 計画や提案が参照していれば消せない()
    {
        var blocked = Assert.IsType<DepartmentRemovalDecision.Blocked>(
            Decide(plans: ["plan-1"], proposals: ["提案-2"]));

        Assert.Equal(2, blocked.Reasons.Count);
    }

    [Fact]
    public void 書き込み権を持っていれば消せない()
    {
        var blocked = Assert.IsType<DepartmentRemovalDecision.Blocked>(Decide(holdsWriteLease: true));
        Assert.Contains(blocked.Reasons, reason => reason.Contains("書き込み権", StringComparison.Ordinal));
    }

    [Fact]
    public void 読めない仕事があれば消せない()
    {
        // **判定そのものが信用できない**（§7）—— どの部門のものか分からない。
        var blocked = Assert.IsType<DepartmentRemovalDecision.Blocked>(Decide(unreadable: 1));
        Assert.Contains(blocked.Reasons, reason => reason.Contains("読めない", StringComparison.Ordinal));
    }

    [Fact]
    public void 理由は全部返す()
    {
        // **1つ直すたびに次が出るのでは、人間が何度も往復する。**
        var blocked = Assert.IsType<DepartmentRemovalDecision.Blocked>(Decide(
            tasks: [Task("task-1", CoreTaskStatus.Dispatched)],
            plans: ["plan-1"],
            holdsWriteLease: true,
            unreadable: 2));

        Assert.Equal(4, blocked.Reasons.Count);
    }

    [Fact]
    public void 窓が動いているだけなら_終了を選ばせる()
    {
        // **他の理由と区別する。** これは人間が「終了して消す」を選べる。
        Assert.IsType<DepartmentRemovalDecision.NeedsSessionStop>(Decide(sessionRunning: true));
    }

    [Fact]
    public void 他に理由があるときは_窓の話より先に出す()
    {
        // 窓を閉じても消せないのに「終了して消す」を出すと、押しても消えない。
        Assert.IsType<DepartmentRemovalDecision.Blocked>(
            Decide(tasks: [Task("task-1", CoreTaskStatus.Reported)], sessionRunning: true));
    }

    private static TaskState Task(string slug, CoreTaskStatus status) =>
        new(slug, status, 0, 1, "設計", TransitionOrigin.Automation, DateTimeOffset.UnixEpoch);

    private static DepartmentRemovalDecision Decide(
        IReadOnlyList<TaskState>? tasks = null,
        IReadOnlyList<string>? plans = null,
        IReadOnlyList<string>? proposals = null,
        bool holdsWriteLease = false,
        int unreadable = 0,
        bool sessionRunning = false) =>
        DepartmentRemoval.Decide(
            "設計", tasks ?? [], plans ?? [], proposals ?? [], holdsWriteLease, unreadable, sessionRunning);
}
