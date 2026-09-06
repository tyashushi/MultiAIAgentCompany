using System.Text.Json;
using MultiAIAgentCompany.Core.Coordination;
using Xunit;
using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Tests;

public sealed class TaskStoreTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new();
    private readonly TaskStore _store;

    public TaskStoreTests()
    {
        _store = new TaskStore(new CompanyPaths(_workspace.Path), TimeProvider.System);
    }

    [Fact]
    public async Task 作って読むと往復する()
    {
        var created = Assert.IsType<TaskWriteResult.Written>(await _store.CreateAsync("feature", "implementation", CancellationToken.None));
        var read = Assert.IsType<TaskReadResult.Found>(await _store.ReadAsync("feature", CancellationToken.None));

        Assert.Equal(created.State, read.State);
        Assert.Equal(1, read.State.Revision);
        Assert.Equal(0, read.State.AttemptId);
        Assert.Equal(CoreTaskStatus.Drafted, read.State.Status);
    }

    [Fact]
    public async Task DraftedからDispatchedへ遷移するとRevisionが2になる()
    {
        var created = Assert.IsType<TaskWriteResult.Written>(await _store.CreateAsync("feature", "implementation", CancellationToken.None));
        var result = Assert.IsType<TaskWriteResult.Written>(await _store.TransitionAsync(created.State, CoreTaskStatus.Dispatched, TransitionOrigin.Automation, null, CancellationToken.None));

        Assert.Equal(2, result.State.Revision);
        Assert.Equal(CoreTaskStatus.Dispatched, result.State.Status);
    }

    [Fact]
    public async Task 古いexpectedはConflictedとなりstate_jsonを変更しない()
    {
        var created = Assert.IsType<TaskWriteResult.Written>(await _store.CreateAsync("feature", "implementation", CancellationToken.None));
        var current = Assert.IsType<TaskWriteResult.Written>(await _store.TransitionAsync(created.State, CoreTaskStatus.Dispatched, TransitionOrigin.Automation, null, CancellationToken.None));
        var before = await File.ReadAllTextAsync(_workspace.Paths.State("feature"));

        Assert.IsType<TaskWriteResult.Conflicted>(await _store.TransitionAsync(created.State, CoreTaskStatus.InProgress, TransitionOrigin.Automation, null, CancellationToken.None));

        Assert.Equal(before, await File.ReadAllTextAsync(_workspace.Paths.State("feature")));
        Assert.Equal(CoreTaskStatus.Dispatched, current.State.Status);
    }

    [Fact]
    public async Task 拒否される遷移はRejectedとなりstate_jsonを変更しない()
    {
        var reported = await CreateReportedAsync("feature");
        var before = await File.ReadAllTextAsync(_workspace.Paths.State("feature"));

        Assert.IsType<TaskWriteResult.Rejected>(await _store.TransitionAsync(reported, CoreTaskStatus.Accepted, TransitionOrigin.Automation, null, CancellationToken.None));

        Assert.Equal(before, await File.ReadAllTextAsync(_workspace.Paths.State("feature")));
    }

    [Fact]
    public async Task RejectedからDispatchedで現在の試行を封じる()
    {
        var reported = await CreateReportedAsync("feature");
        var rejected = Assert.IsType<TaskWriteResult.Written>(await _store.TransitionAsync(reported, CoreTaskStatus.Rejected, TransitionOrigin.Human, "redo", CancellationToken.None));
        await File.WriteAllTextAsync(_workspace.Paths.Instruction("feature"), "instruction");
        await File.WriteAllTextAsync(_workspace.Paths.Report("feature"), "report");

        await File.WriteAllTextAsync(_workspace.Paths.NextInstruction("feature"), "next");

        // 差し戻しからの再送は RedispatchAsync だけを通る（設計 §19-1）。
        Assert.IsType<TaskWriteResult.Rejected>(await _store.TransitionAsync(rejected.State, CoreTaskStatus.Dispatched, TransitionOrigin.Human, null, CancellationToken.None));
        var result = Assert.IsType<TaskWriteResult.Written>(await _store.RedispatchAsync(rejected.State, CancellationToken.None));

        Assert.Equal(1, result.State.AttemptId);
        var attempt = _workspace.Paths.AttemptDirectory("feature", 0);
        Assert.Equal("instruction", await File.ReadAllTextAsync(Path.Combine(attempt, "instruction.md")));
        Assert.Equal("report", await File.ReadAllTextAsync(Path.Combine(attempt, "report.md")));
        Assert.Equal("next", await File.ReadAllTextAsync(_workspace.Paths.Instruction("feature")));
        Assert.False(File.Exists(_workspace.Paths.Report("feature")));
    }

    [Fact]
    public async Task 壊れたJSONは例外ではなくUnreadableになる()
    {
        Directory.CreateDirectory(_workspace.Paths.TaskDirectory("broken"));
        await File.WriteAllTextAsync(_workspace.Paths.State("broken"), "{");

        Assert.IsType<TaskReadResult.Unreadable>(await _store.ReadAsync("broken", CancellationToken.None));
    }

    [Fact]
    public async Task Slugがディレクトリ名と違うstate_jsonはUnreadableになる()
    {
        var created = Assert.IsType<TaskWriteResult.Written>(await _store.CreateAsync("feature", "implementation", CancellationToken.None));
        var other = created.State with { Slug = "other" };
        await File.WriteAllTextAsync(_workspace.Paths.State("feature"), JsonSerializer.Serialize(other, TaskStateJson.Options));

        Assert.IsType<TaskReadResult.Unreadable>(await _store.ReadAsync("feature", CancellationToken.None));
    }

    [Fact]
    public async Task ScanForRecoveryはDispatchedだけを返す()
    {
        var dispatched = Assert.IsType<TaskWriteResult.Written>(await _store.CreateAsync("dispatched", "implementation", CancellationToken.None));
        await _store.TransitionAsync(dispatched.State, CoreTaskStatus.Dispatched, TransitionOrigin.Automation, null, CancellationToken.None);
        await _store.CreateAsync("draft", "implementation", CancellationToken.None);

        var found = await _store.ScanForRecoveryAsync(CancellationToken.None);

        var state = Assert.Single(found.Dispatched);
        Assert.Equal("dispatched", state.Slug);
        Assert.Empty(found.Unreadable);
    }

    [Fact]
    public async Task 復旧走査は読めなかった仕事も返す()
    {
        // 壊れた state.json を黙って捨てると「仕事が無い」と区別できなくなる。
        await _store.CreateAsync("broken", "implementation", CancellationToken.None);
        await File.WriteAllTextAsync(_workspace.Paths.State("broken"), "{ これは JSON ではない");

        var found = await _store.ScanForRecoveryAsync(CancellationToken.None);

        Assert.Empty(found.Dispatched);
        Assert.Equal("broken", Assert.Single(found.Unreadable).Slug);
    }

    [Fact]
    public async Task 迷子のディレクトリがあっても復旧走査は落ちない()
    {
        // slug として使えない名前のディレクトリが1つあるだけで、起動時に例外になっていた。
        await _store.CreateAsync("ok-task", "implementation", CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(_workspace.Paths.TasksRoot, "foo bar"));

        var found = await _store.ScanForRecoveryAsync(CancellationToken.None);

        Assert.Empty(found.Dispatched);
        Assert.Empty(found.Unreadable);
    }

    [Fact]
    public async Task フィールドが欠けたstate_jsonは既定値で埋めない()
    {
        // status を落とした state.json が Drafted として読めると、信頼順1位の根拠が嘘をつく。
        await _store.CreateAsync("partial", "implementation", CancellationToken.None);
        var missingStatus =
            "{\"slug\":\"partial\",\"attemptId\":0,\"revision\":1,\"departmentId\":\"implementation\","
            + "\"lastTransitionOrigin\":\"Automation\",\"updatedAt\":\"2026-09-05T00:00:00+00:00\"}";
        await File.WriteAllTextAsync(_workspace.Paths.State("partial"), missingStatus);

        Assert.IsType<TaskReadResult.Unreadable>(await _store.ReadAsync("partial", CancellationToken.None));
    }

    [Fact]
    public async Task 書いたあと一時ファイルは残らない()
    {
        await _store.CreateAsync("feature", "implementation", CancellationToken.None);

        Assert.Empty(Directory.EnumerateFiles(_workspace.Paths.TaskDirectory("feature"), ".state.json.*.tmp"));
    }

    [Fact]
    public async Task 日本語のnoteがエスケープされない()
    {
        // note は「人間に見せる1行」。既定の System.Text.Json は日本語を \uXXXX に潰す。
        var created = Assert.IsType<TaskWriteResult.Written>(await _store.CreateAsync("feature", "実装", CancellationToken.None));
        await _store.TransitionAsync(created.State, CoreTaskStatus.Dispatched, TransitionOrigin.Automation, "秘書が投げた", CancellationToken.None);

        var json = await File.ReadAllTextAsync(_workspace.Paths.State("feature"));

        Assert.Contains("秘書が投げた", json);
        Assert.Contains("実装", json);
        Assert.DoesNotContain("\\u", json);
    }

    [Fact]
    public async Task state_jsonの列挙は数値でなく名前で書かれる()
    {
        var created = Assert.IsType<TaskWriteResult.Written>(await _store.CreateAsync("feature", "implementation", CancellationToken.None));
        await _store.TransitionAsync(created.State, CoreTaskStatus.Dispatched, TransitionOrigin.Automation, null, CancellationToken.None);

        Assert.Contains("\"Dispatched\"", await File.ReadAllTextAsync(_workspace.Paths.State("feature")));
    }

    private async Task<TaskState> CreateReportedAsync(string slug)
    {
        var drafted = Assert.IsType<TaskWriteResult.Written>(await _store.CreateAsync(slug, "implementation", CancellationToken.None));
        var dispatched = Assert.IsType<TaskWriteResult.Written>(await _store.TransitionAsync(drafted.State, CoreTaskStatus.Dispatched, TransitionOrigin.Automation, null, CancellationToken.None));
        var inProgress = Assert.IsType<TaskWriteResult.Written>(await _store.TransitionAsync(dispatched.State, CoreTaskStatus.InProgress, TransitionOrigin.Automation, null, CancellationToken.None));
        return Assert.IsType<TaskWriteResult.Written>(await _store.TransitionAsync(inProgress.State, CoreTaskStatus.Reported, TransitionOrigin.Automation, null, CancellationToken.None)).State;
    }

    public void Dispose() => _workspace.Dispose();

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"multi-ai-agent-company-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            Paths = new CompanyPaths(Path);
        }

        public string Path { get; }
        public CompanyPaths Paths { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
