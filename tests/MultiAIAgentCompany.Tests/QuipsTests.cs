using MultiAIAgentCompany.Core.Status;
using Xunit;
using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Tests;

/// <summary>ロボットの一言（設計 §52）。<b>遊んでよい場所と、遊ばない場所を分ける。</b></summary>
public sealed class QuipsTests
{
    [Theory]
    [InlineData(DepartmentPose.Working)]
    [InlineData(DepartmentPose.Resting)]
    [InlineData(DepartmentPose.Unknown)]
    public void 遊んでよい状態では候補から選ぶ(DepartmentPose pose)
    {
        var candidates = Quips.For(pose);

        Assert.NotEmpty(candidates);
        Assert.Equal(candidates[0], Quips.Pick(pose, needsHuman: false, _ => 0));
        Assert.Equal(candidates[^1], Quips.Pick(pose, needsHuman: false, count => count - 1));
    }

    [Theory]
    [InlineData(DepartmentPose.AwaitingApproval, "許可")]
    [InlineData(DepartmentPose.Consulting, "答えて")]
    [InlineData(DepartmentPose.Degraded, "原因")]
    public void 人間が急いで判断する状態では遊ばない(DepartmentPose pose, string action)
    {
        // **何をすればよいかだけを書く**（§52-3）。候補を持たないので、選び方に左右されない。
        Assert.Empty(Quips.For(pose));
        Assert.Contains(action, Quips.Pick(pose, needsHuman: false, _ => 0));
        Assert.Equal(Quips.Pick(pose, needsHuman: false, _ => 0), Quips.Pick(pose, needsHuman: false, _ => 5));
    }

    [Theory]
    [InlineData(DepartmentPose.Unknown)]
    [InlineData(DepartmentPose.Working)]
    [InlineData(DepartmentPose.Resting)]
    public void 人間の出番があればポーズにかかわらず遊ばない(DepartmentPose pose)
    {
        // **外部ターミナルの部門はポーズが Unknown のまま**（§32-4）なので、
        // 質問や報告を待っていても「分からない」の冗談が出ていた（Codex の指摘）。
        var line = Quips.Pick(pose, needsHuman: true, _ => 0);

        Assert.DoesNotContain(line, Quips.For(pose));
        Assert.Contains("出番", line);
    }

    [Fact]
    public void 分からない部門をサボりとは言わない()
    {
        // **観測していないことを、面白さのために言わない**（§7）。
        Assert.All(Quips.For(DepartmentPose.Unknown), line =>
        {
            Assert.DoesNotContain("サボ", line);
            Assert.DoesNotContain("休憩中", line);
        });
    }

    [Fact]
    public void 選ぶ位置が外れても落ちない()
    {
        Assert.Equal(Quips.For(DepartmentPose.Working)[0], Quips.Pick(DepartmentPose.Working, needsHuman: false, _ => -3));
        Assert.Equal(Quips.For(DepartmentPose.Working)[^1], Quips.Pick(DepartmentPose.Working, needsHuman: false, _ => 99));
    }

    [Fact]
    public void 仕事を抱えていなければ手すきと言う()
    {
        Assert.True(Quips.AreAllIdle([null, CoreTaskStatus.Accepted, CoreTaskStatus.Cancelled]));
    }

    [Theory]
    [InlineData(CoreTaskStatus.Drafted)]
    [InlineData(CoreTaskStatus.Dispatched)]
    [InlineData(CoreTaskStatus.InProgress)]
    [InlineData(CoreTaskStatus.AwaitingAnswer)]
    [InlineData(CoreTaskStatus.Reported)]
    [InlineData(CoreTaskStatus.Rejected)]
    [InlineData(CoreTaskStatus.Failed)]
    public void 一部門でも仕事が残っていれば手すきと言わない(CoreTaskStatus work)
    {
        Assert.False(Quips.AreAllIdle([null, work]));
    }

    [Fact]
    public void 部門が無いときは手すきと言わない()
    {
        Assert.False(Quips.AreAllIdle([]));
    }
}
