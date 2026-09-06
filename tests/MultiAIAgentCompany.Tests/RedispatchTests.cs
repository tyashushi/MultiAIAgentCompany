using MultiAIAgentCompany.Core.Coordination;
using Xunit;
using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Tests;

/// <summary>設計 §19 —— 差し戻しから次の試行へ。<b>順序が本体</b>。</summary>
public sealed class RedispatchTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new();
    private readonly TaskStore _tasks;

    public RedispatchTests() => _tasks = new TaskStore(_workspace.Paths, TimeProvider.System);

    public void Dispose() => _workspace.Dispose();

    private async Task<TaskState> RejectedTaskAsync()
    {
        var state = Assert.IsType<TaskWriteResult.Written>(
            await _tasks.CreateAsync("feature", "implementation", CancellationToken.None)).State;
        foreach (var next in new[] { CoreTaskStatus.Dispatched, CoreTaskStatus.Reported })
        {
            state = Assert.IsType<TaskWriteResult.Written>(await _tasks.TransitionAsync(
                state, next, TransitionOrigin.Human, null, CancellationToken.None)).State;
        }

        await File.WriteAllTextAsync(_workspace.Paths.Instruction("feature"), "最初の指示");
        await File.WriteAllTextAsync(_workspace.Paths.Report("feature"), "最初の報告");
        await File.WriteAllTextAsync(_workspace.Paths.Rejection("feature"), "ここが違う");

        return Assert.IsType<TaskWriteResult.Written>(await _tasks.TransitionAsync(
            state, CoreTaskStatus.Rejected, TransitionOrigin.Human, null, CancellationToken.None)).State;
    }

    [Fact]
    public async Task 次の指示は封じられずに昇格する()
    {
        // **これが §19-1 の本体。** 素直に繋ぐと、新しい指示が過去試行へ封じられる。
        var rejected = await RejectedTaskAsync();
        await File.WriteAllTextAsync(_workspace.Paths.NextInstruction("feature"), "やり直しの指示");

        var result = Assert.IsType<TaskWriteResult.Written>(
            await _tasks.RedispatchAsync(rejected, CancellationToken.None));

        Assert.Equal(CoreTaskStatus.Dispatched, result.State.Status);
        Assert.Equal(1, result.State.AttemptId);
        Assert.Equal("やり直しの指示", await File.ReadAllTextAsync(_workspace.Paths.Instruction("feature")));
        Assert.False(File.Exists(_workspace.Paths.NextInstruction("feature")));
    }

    [Fact]
    public async Task 旧い試行は差し戻し理由ごと封じられる()
    {
        // rejection.md はその試行の報告に対する人間の判断（§19-2）。
        var rejected = await RejectedTaskAsync();
        await File.WriteAllTextAsync(_workspace.Paths.NextInstruction("feature"), "やり直しの指示");

        await _tasks.RedispatchAsync(rejected, CancellationToken.None);

        var attempt = _workspace.Paths.AttemptDirectory("feature", 0);
        Assert.Equal("最初の指示", await File.ReadAllTextAsync(Path.Combine(attempt, "instruction.md")));
        Assert.Equal("最初の報告", await File.ReadAllTextAsync(Path.Combine(attempt, "report.md")));
        Assert.Equal("ここが違う", await File.ReadAllTextAsync(Path.Combine(attempt, "rejection.md")));
    }

    [Fact]
    public async Task 次の指示が無ければ進めない()
    {
        // 指示書の無いまま Dispatched を書かない（§19-1 / §14-1）。
        var rejected = await RejectedTaskAsync();

        var result = Assert.IsType<TaskWriteResult.Rejected>(
            await _tasks.RedispatchAsync(rejected, CancellationToken.None));

        Assert.Contains("next-instruction.md", result.Reason, StringComparison.Ordinal);
        Assert.Equal(CoreTaskStatus.Rejected, ((TaskReadResult.Found)
            await _tasks.ReadAsync("feature", CancellationToken.None)).State.Status);
    }

    [Fact]
    public async Task 差し戻された仕事だけを進められる()
    {
        var created = Assert.IsType<TaskWriteResult.Written>(
            await _tasks.CreateAsync("other", "implementation", CancellationToken.None)).State;
        await File.WriteAllTextAsync(_workspace.Paths.NextInstruction("other"), "やり直しの指示");

        Assert.IsType<TaskWriteResult.Rejected>(await _tasks.RedispatchAsync(created, CancellationToken.None));
    }
}
