using MultiAIAgentCompany.Core.Coordination;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>設計 §62-5。<b>行が無いことを「できなかった」にしない</b>ことを固定する。</summary>
public sealed class ReportOutcomesTests
{
    [Theory]
    [InlineData("outcome: done", ReportOutcome.Done)]
    [InlineData("outcome: partial", ReportOutcome.Partial)]
    [InlineData("outcome: blocked", ReportOutcome.Blocked)]
    [InlineData("## 結果\n\n- **Outcome**: Partial。", ReportOutcome.Partial)]
    [InlineData("`outcome：blocked`", ReportOutcome.Blocked)]
    public void 行の値を読む(string report, ReportOutcome expected) =>
        Assert.Equal(expected, ReportOutcomes.Parse(report));

    [Theory]
    [InlineData("")]
    [InlineData("終わりました")]
    [InlineData("outcome: finished")]
    [InlineData("outcome: done\noutcome: blocked")]
    public void 無い_知らない_食い違うは分からない(string report) =>
        Assert.Equal(ReportOutcome.Unknown, ReportOutcomes.Parse(report));

    [Fact]
    public void 同じ値の2行は食い違いではない() =>
        Assert.Equal(ReportOutcome.Done, ReportOutcomes.Parse("outcome: done\n\noutcome: done"));

    [Fact]
    public void 止めるのは_partial_と_blocked_だけ()
    {
        Assert.True(ReportOutcomes.StopsPlan(ReportOutcome.Partial));
        Assert.True(ReportOutcomes.StopsPlan(ReportOutcome.Blocked));
        Assert.False(ReportOutcomes.StopsPlan(ReportOutcome.Done));
        Assert.False(ReportOutcomes.StopsPlan(ReportOutcome.Unknown));
    }
}
