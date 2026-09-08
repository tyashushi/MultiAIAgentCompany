using MultiAIAgentCompany.Core.Coordination;
using Xunit;
using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Tests;

public sealed class ReportWatchTests
{
    private static readonly DateTimeOffset UpdatedAt = new(2026, 9, 8, 12, 0, 0, TimeSpan.FromHours(9));
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(30);

    [Fact]
    public void 期限が無い部門は見ない()
    {
        Assert.Null(ReportWatch.Of(State(CoreTaskStatus.Dispatched), null, UpdatedAt.AddDays(3)));
    }

    [Theory]
    [InlineData(29)]
    [InlineData(30)]
    public void 期限内と期限ちょうどでは出さない(int minutes)
    {
        Assert.Null(ReportWatch.Of(State(CoreTaskStatus.Dispatched), Deadline, UpdatedAt.AddMinutes(minutes)));
    }

    [Theory]
    [InlineData(CoreTaskStatus.Dispatched)]
    [InlineData(CoreTaskStatus.InProgress)]
    public void 期限を超えたら最後の更新時刻と経過を返す(CoreTaskStatus status)
    {
        var now = UpdatedAt.Add(Deadline).AddTicks(1);
        var silence = Assert.IsType<ReportSilence>(ReportWatch.Of(State(status), Deadline, now));

        Assert.Equal("task-1", silence.Slug);
        Assert.Equal("design", silence.DepartmentId);
        Assert.Equal(UpdatedAt, silence.Since);
        Assert.Equal(Deadline.Add(TimeSpan.FromTicks(1)), silence.Elapsed);
    }

    [Theory]
    [InlineData(CoreTaskStatus.AwaitingAnswer)]
    [InlineData(CoreTaskStatus.Drafted)]
    [InlineData(CoreTaskStatus.Reported)]
    [InlineData(CoreTaskStatus.Accepted)]
    [InlineData(CoreTaskStatus.Failed)]
    [InlineData(CoreTaskStatus.Rejected)]
    [InlineData(CoreTaskStatus.Cancelled)]
    public void 部門の報告を待つ状態以外は期限を超えても出さない(CoreTaskStatus status)
    {
        Assert.Null(ReportWatch.Of(State(status), Deadline, UpdatedAt.AddDays(3)));
    }

    [Fact]
    public void 時計が巻き戻ったときは出さない()
    {
        Assert.Null(ReportWatch.Of(State(CoreTaskStatus.Dispatched), Deadline, UpdatedAt.AddTicks(-1)));
    }

    private static TaskState State(CoreTaskStatus status) =>
        new("task-1", status, 1, 1, "design", TransitionOrigin.Automation, UpdatedAt);
}
