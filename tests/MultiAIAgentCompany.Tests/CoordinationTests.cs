using MultiAIAgentCompany.Core.Coordination;
using Xunit;
using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Tests;

/// <summary>設計 §6 —— 自動化と人間で遷移の権利を分ける。</summary>
public sealed class TaskTransitionTests
{
    [Fact]
    public void 自動化は終端状態から復帰できない()
    {
        var check = TaskTransitions.Check(CoreTaskStatus.Accepted, CoreTaskStatus.InProgress, TransitionOrigin.Automation);

        Assert.False(check.Allowed);
    }

    [Fact]
    public void 人間は終端状態から戻せる()
    {
        var check = TaskTransitions.Check(CoreTaskStatus.Accepted, CoreTaskStatus.InProgress, TransitionOrigin.Human);

        Assert.True(check.Allowed);
    }

    [Fact]
    public void 報告の受理は人間の判断で自動化にはできない()
    {
        Assert.False(TaskTransitions.Check(CoreTaskStatus.Reported, CoreTaskStatus.Accepted, TransitionOrigin.Automation).Allowed);
        Assert.True(TaskTransitions.Check(CoreTaskStatus.Reported, CoreTaskStatus.Accepted, TransitionOrigin.Human).Allowed);
    }

    [Fact]
    public void 定義されていない遷移は通さない()
    {
        Assert.False(TaskTransitions.Check(CoreTaskStatus.Drafted, CoreTaskStatus.Reported, TransitionOrigin.Human).Allowed);
    }
}

/// <summary>調整基盤の置き場所（設計 §6）と、そこから外へ出られないこと。</summary>
public sealed class CompanyPathsTests
{
    private static readonly CompanyPaths Paths = new("/tmp/ws");

    [Fact]
    public void 設計どおりの配置になっている()
    {
        Assert.Equal(Path.Combine("/tmp/ws", ".company"), Paths.Root);
        Assert.Equal(Path.Combine("/tmp/ws", ".company", "tasks", "add-login", "instruction.md"), Paths.Instruction("add-login"));
        Assert.Equal(Path.Combine("/tmp/ws", ".company", "tasks", "add-login", "state.json"), Paths.State("add-login"));
        Assert.Equal(Path.Combine("/tmp/ws", ".company", "archive"), Paths.ArchiveRoot);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("")]
    public void ワークスペースの外へ出る_slug_は拒否する(string slug)
    {
        Assert.ThrowsAny<ArgumentException>(() => Paths.TaskDirectory(slug));
    }
}

/// <summary>設計 §8 —— 書き込み権は同時に1部門だけ。時間で解ける。</summary>
public sealed class WriteLeaseTests
{
    [Fact]
    public void 失効した_lease_は有効ではない()
    {
        var start = DateTimeOffset.UnixEpoch;
        var lease = new WriteLease("実装", start, start.AddMinutes(10));

        Assert.True(lease.IsValidAt(start.AddMinutes(9)));
        Assert.False(lease.IsValidAt(start.AddMinutes(11)));
    }
}
