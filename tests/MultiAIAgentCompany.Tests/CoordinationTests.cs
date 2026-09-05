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
        var check = TaskTransitions.Check(CoreTaskStatus.Accepted, CoreTaskStatus.Drafted, TransitionOrigin.Human);

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

    [Fact]
    public void 未定義のenum値は素通りさせない()
    {
        // state.json は人間が手で直せるファイル。初版は `origin is Automation` で弾いていたため
        // (TransitionOrigin)123 で報告の自動受理が、(TaskStatus)123 で終端からの復帰が通った（§14-4）。
        Assert.False(TaskTransitions.Check(CoreTaskStatus.Reported, CoreTaskStatus.Accepted, (TransitionOrigin)123).Allowed);
        Assert.False(TaskTransitions.Check(CoreTaskStatus.Accepted, (CoreTaskStatus)123, TransitionOrigin.Human).Allowed);
    }

    [Fact]
    public void 人間の終端からの復帰先は仕切り直しの入口だけ()
    {
        Assert.True(TaskTransitions.Check(CoreTaskStatus.Accepted, CoreTaskStatus.Dispatched, TransitionOrigin.Human).Allowed);
        Assert.False(TaskTransitions.Check(CoreTaskStatus.Accepted, CoreTaskStatus.Reported, TransitionOrigin.Human).Allowed);
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
        Assert.Equal(Path.Combine("/tmp/ws", ".company", "lease.json"), Paths.Lease);
        Assert.Equal(Path.Combine("/tmp/ws", ".company", "tasks", "add-login", "attempts", "2"), Paths.AttemptDirectory("add-login", 2));
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

/// <summary>設計 §14-2 —— 権利はワークスペースに1つ。取得前は有効でない。</summary>
public sealed class WorkspaceLeaseTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static WorkspaceLeases WithWriteHeldBy(string department) => new(1,
        new Dictionary<LeaseKind, LeaseHolder>
        {
            [LeaseKind.Write] = new(LeaseKind.Write, department, "task", T0, T0.AddMinutes(10)),
        });

    [Fact]
    public void 書き込み権を持てるのは同時に1部門だけ()
    {
        // 初版は lease をタスクごとの TaskState に置いていたので、別タスクに別部門の
        // 有効な lease を同時に置けた（再レビューで発覚、§14-2）。
        var leases = WithWriteHeldBy("実装");

        Assert.True(leases.IsHeldBy(LeaseKind.Write, "実装", T0.AddMinutes(1)));
        Assert.False(leases.IsHeldBy(LeaseKind.Write, "テスト", T0.AddMinutes(1)));
        Assert.False(leases.CanAcquire(LeaseKind.Write, T0.AddMinutes(1)));
    }

    [Fact]
    public void Unity権は書き込み権とは別の権利()
    {
        // Unity を読むだけの仕事は成果物を書かないが、MCP のキューは食う（§13-5b）。
        var leases = WithWriteHeldBy("実装");

        Assert.True(leases.CanAcquire(LeaseKind.Unity, T0.AddMinutes(1)));
    }

    [Fact]
    public void 失効した権利は取り直せる()
    {
        var leases = WithWriteHeldBy("実装");

        Assert.False(leases.IsHeldBy(LeaseKind.Write, "実装", T0.AddMinutes(11)));
        Assert.True(leases.CanAcquire(LeaseKind.Write, T0.AddMinutes(11)));
    }

    [Fact]
    public void 取得前の権利は有効ではない()
    {
        // 初版の IsValidAt は ExpiresAt しか見ておらず、未来の lease も有効になっていた。
        var future = new LeaseHolder(LeaseKind.Unity, "調査", "task", T0.AddHours(1), T0.AddHours(2));

        Assert.False(future.IsValidAt(T0));
        Assert.True(future.IsValidAt(T0.AddMinutes(90)));
    }
}
