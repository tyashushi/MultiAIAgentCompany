using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Coordination;
using MultiAIAgentCompany.Core.Workspace;
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

    [Fact]
    public async Task 人間の差し戻しは_二度目も最初の依頼を渡す()
    {
        // 設計 §62-2 / §62-7。MainWindow と同じ共通関数から staging に置き、実際に昇格する。
        var state = await RejectedTaskAsync();
        var department = new DepartmentDefinition("implementation", "実装", "実装する",
            AgentKind.ClaudeCode, DriveMode.ExternalTerminal);
        const string original = "  元の依頼\n条件を満たすこと";
        await File.WriteAllTextAsync(_workspace.Paths.Instruction("feature"),
            CompanyInstruction.Compose(original, _workspace.Paths, "feature", department));

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var reason = $"指摘 {attempt}\n```\n## 依頼\n";
            await File.WriteAllTextAsync(_workspace.Paths.NextInstruction("feature"),
                await CompanyInstruction.ComposeRevisionAsync(_workspace.Paths, "feature", department,
                    "差し戻しの理由", "人間", reason, CancellationToken.None));
            state = Assert.IsType<TaskWriteResult.Written>(
                await _tasks.RedispatchAsync(state, CancellationToken.None)).State;

            var text = await File.ReadAllTextAsync(_workspace.Paths.Instruction("feature"));
            const string heading = "## 元の依頼（最初の試行の指示書から、そのまま）";
            Assert.Contains($"{heading}\n\n{original}\n\n## 資料:", text);
            Assert.Equal(1, text.Split(heading).Length - 1);
            Assert.Contains(CompanyInstruction.Material("差し戻しの理由", "人間", reason), text);
            Assert.Contains("元の依頼を満たしたうえで、指摘を直すこと。指摘だけ直して元の依頼を落とさない", text);
            Assert.Contains("指摘の ID（R1 など）ごとに、どう直したか（直さなかったなら理由）", text);
            Assert.Contains("ID が無い指摘は、どの指摘への対応か分かるように", text);
            Assert.Contains(Path.Combine(_workspace.Paths.TaskDirectory("feature"), "attempts"), text);
            Assert.Equal(attempt, state.AttemptId);

            if (attempt == 1)
            {
                foreach (var next in new[] { CoreTaskStatus.Reported, CoreTaskStatus.Rejected })
                {
                    state = Assert.IsType<TaskWriteResult.Written>(await _tasks.TransitionAsync(
                        state, next, TransitionOrigin.Human, null, CancellationToken.None)).State;
                }
            }
        }
    }

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

    private string Sealed(string fileName) =>
        Path.Combine(_workspace.Paths.AttemptDirectory("feature", 0), fileName);

    [Fact]
    public async Task 昇格まで済んで落ちた送り直しは_もう一度押すと状態だけを書いて終える()
    {
        // Codex のレビューで発覚。昇格したあと状態を書く前に落ちると、二度と送り直せなかった。
        var state = await RejectedTaskAsync();
        Directory.CreateDirectory(_workspace.Paths.AttemptDirectory("feature", 0));
        File.Move(_workspace.Paths.Instruction("feature"), Sealed("instruction.md"));
        File.Move(_workspace.Paths.Report("feature"), Sealed("report.md"));
        File.Move(_workspace.Paths.Rejection("feature"), Sealed("rejection.md"));
        await File.WriteAllTextAsync(_workspace.Paths.Instruction("feature"), "次の指示");

        var next = Assert.IsType<TaskWriteResult.Written>(await _tasks.RedispatchAsync(state, CancellationToken.None)).State;

        Assert.Equal(CoreTaskStatus.Dispatched, next.Status);
        Assert.Equal(1, next.AttemptId);
        Assert.Equal("次の指示", await File.ReadAllTextAsync(_workspace.Paths.Instruction("feature")));
        Assert.Equal("最初の指示", await File.ReadAllTextAsync(Sealed("instruction.md")));
    }

    [Fact]
    public async Task 封じる途中で落ちた送り直しは_残りを封じて昇格する()
    {
        var state = await RejectedTaskAsync();
        Directory.CreateDirectory(_workspace.Paths.AttemptDirectory("feature", 0));
        File.Move(_workspace.Paths.Instruction("feature"), Sealed("instruction.md"));
        await _tasks.WriteNextInstructionAsync("feature", "次の指示", CancellationToken.None);

        Assert.IsType<TaskWriteResult.Written>(await _tasks.RedispatchAsync(state, CancellationToken.None));

        Assert.Equal("次の指示", await File.ReadAllTextAsync(_workspace.Paths.Instruction("feature")));
        Assert.Equal("最初の指示", await File.ReadAllTextAsync(Sealed("instruction.md")));
        Assert.Equal("最初の報告", await File.ReadAllTextAsync(Sealed("report.md")));
        Assert.False(File.Exists(_workspace.Paths.NextInstruction("feature")));
    }

    [Fact]
    public async Task 封じ先に同じ名前があっても_上書きせずに並べる()
    {
        // 落ちたあとに部門が報告を書き足した、など。**どちらも消さない。**
        var state = await RejectedTaskAsync();
        Directory.CreateDirectory(_workspace.Paths.AttemptDirectory("feature", 0));
        await File.WriteAllTextAsync(Sealed("report.md"), "先に封じた報告");
        await _tasks.WriteNextInstructionAsync("feature", "次の指示", CancellationToken.None);

        Assert.IsType<TaskWriteResult.Written>(await _tasks.RedispatchAsync(state, CancellationToken.None));

        var reports = Directory.GetFiles(_workspace.Paths.AttemptDirectory("feature", 0), "report*.md")
            .Select(File.ReadAllText).ToHashSet();
        Assert.Equal(new HashSet<string> { "先に封じた報告", "最初の報告" }, reports);
    }

    [Fact]
    public async Task 次の指示書は書き終えてから最終名に置く()
    {
        await RejectedTaskAsync();

        await _tasks.WriteNextInstructionAsync("feature", "次の指示", CancellationToken.None);

        Assert.Equal("次の指示", await File.ReadAllTextAsync(_workspace.Paths.NextInstruction("feature")));
        Assert.Empty(Directory.GetFiles(_workspace.Paths.TaskDirectory("feature"), "*.tmp"));
    }

}
