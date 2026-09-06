using MultiAIAgentCompany.Core.Coordination;
using Xunit;
using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Tests;

public sealed class CompanyScannerTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new();
    private readonly TaskStore _tasks;
    private readonly CompanyScanner _scanner;

    public CompanyScannerTests()
    {
        _tasks = new TaskStore(_workspace.Paths, TimeProvider.System);
        _scanner = new CompanyScanner(_workspace.Paths, _tasks);
    }

    [Fact]
    public async Task DispatchedのreportはReportedにする()
    {
        await CreateAtAsync("report", CoreTaskStatus.Dispatched);
        await File.WriteAllTextAsync(_workspace.Paths.Report("report"), "完了しました");

        var result = await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        Assert.Equal(CoreTaskStatus.Reported, await StatusAsync("report"));
        var applied = Assert.Single(result.Applied);
        Assert.Equal("report.md が publish された", applied.Because);
    }

    [Fact]
    public async Task InProgressのquestionはAwaitingAnswerにする()
    {
        await CreateAtAsync("question", CoreTaskStatus.InProgress);
        await File.WriteAllTextAsync(_workspace.Paths.Question("question"), "判断してください");

        await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        Assert.Equal(CoreTaskStatus.AwaitingAnswer, await StatusAsync("question"));
    }

    [Fact]
    public async Task AwaitingAnswerのanswerはInProgressに戻す()
    {
        await CreateAtAsync("answer", CoreTaskStatus.AwaitingAnswer);
        await File.WriteAllTextAsync(_workspace.Paths.Answer("answer"), "進めてください");

        await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        Assert.Equal(CoreTaskStatus.InProgress, await StatusAsync("answer"));
    }

    [Fact]
    public async Task reportとquestionが両方あればReportedを優先する()
    {
        await CreateAtAsync("both", CoreTaskStatus.InProgress);
        await File.WriteAllTextAsync(_workspace.Paths.Report("both"), "報告");
        await File.WriteAllTextAsync(_workspace.Paths.Question("both"), "質問");

        var result = await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        Assert.Equal(CoreTaskStatus.Reported, await StatusAsync("both"));
        Assert.Equal(CoreTaskStatus.Reported, Assert.Single(result.Applied).To);
    }

    [Fact]
    public async Task tmpだけではpublish済みとして扱わない()
    {
        await CreateAtAsync("temporary", CoreTaskStatus.Dispatched);
        await File.WriteAllTextAsync(Path.Combine(_workspace.Paths.TaskDirectory("temporary"), "report.md.tmp.abc"), "書きかけ");

        var result = await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        Assert.Equal(CoreTaskStatus.Dispatched, await StatusAsync("temporary"));
        Assert.Empty(result.Applied);
    }

    [Fact]
    public async Task 同じ状態で二度走査しても二度目は書かない()
    {
        await CreateAtAsync("once", CoreTaskStatus.Dispatched);
        await File.WriteAllTextAsync(_workspace.Paths.Report("once"), "報告");
        await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        var second = await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        Assert.Empty(second.Applied);
    }

    [Fact]
    public async Task 終端のreportは候補にしない()
    {
        // Reported → Accepted のあとも report.md は残る。候補にすると以後の走査すべてで
        // Blocked が積み上がり、その一覧が読まれなくなる。
        await CreateAtAsync("feature", CoreTaskStatus.Accepted);
        await File.WriteAllTextAsync(_workspace.Paths.Report("feature"), "報告");

        var result = await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        Assert.Empty(result.Applied);
        Assert.Empty(result.Blocked);
        Assert.Equal(CoreTaskStatus.Accepted, await StatusAsync("feature"));
    }

    [Fact]
    public async Task 定期走査は飛んでいるDispatchedを復旧扱いにしない()
    {
        // §14-1 の「送ったかもしれない」は再起動を跨いだときの話。定期走査で毎回返すと、
        // 2秒前に投げたばかりの仕事まで「送信を確認する」になる。
        await CreateAtAsync("feature", CoreTaskStatus.Dispatched);

        var startup = await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);
        var periodic = await _scanner.SyncAsync(CompanyScanKind.Periodic, CancellationToken.None);

        Assert.Single(startup.Dispatched);
        Assert.Empty(periodic.Dispatched);
    }

    [Fact]
    public async Task 壊れたstate_jsonがあっても残りを走査する()
    {
        await CreateAtAsync("good", CoreTaskStatus.Dispatched);
        await File.WriteAllTextAsync(_workspace.Paths.Report("good"), "報告");
        Directory.CreateDirectory(_workspace.Paths.TaskDirectory("broken"));
        await File.WriteAllTextAsync(_workspace.Paths.State("broken"), "{");

        var result = await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        Assert.Equal(CoreTaskStatus.Reported, await StatusAsync("good"));
        Assert.Equal("broken", Assert.Single(result.Unreadable).Slug);
    }

    [Fact]
    public async Task Dispatchedのままの仕事を返す()
    {
        await CreateAtAsync("uncertain", CoreTaskStatus.Dispatched);

        var result = await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        Assert.Equal("uncertain", Assert.Single(result.Dispatched).Slug);
    }

    [Fact]
    public async Task Dispatchedに文書がなければInProgressと推定しない()
    {
        await CreateAtAsync("not-started", CoreTaskStatus.Dispatched);

        await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        Assert.Equal(CoreTaskStatus.Dispatched, await StatusAsync("not-started"));
    }

    /// <summary>
    /// 目的の状態まで、<b>許可された遷移だけ</b>を辿って作る（`TaskTransitions`）。
    /// 一本道ではない —— 例えば AwaitingAnswer の次は InProgress か Cancelled だけで、
    /// そこから直接 Reported へは行けない。
    /// </summary>
    private static readonly Dictionary<CoreTaskStatus, CoreTaskStatus[]> PathTo = new()
    {
        [CoreTaskStatus.Drafted] = [],
        [CoreTaskStatus.Dispatched] = [CoreTaskStatus.Dispatched],
        [CoreTaskStatus.InProgress] = [CoreTaskStatus.Dispatched, CoreTaskStatus.InProgress],
        [CoreTaskStatus.AwaitingAnswer] = [CoreTaskStatus.Dispatched, CoreTaskStatus.InProgress, CoreTaskStatus.AwaitingAnswer],
        [CoreTaskStatus.Reported] = [CoreTaskStatus.Dispatched, CoreTaskStatus.InProgress, CoreTaskStatus.Reported],
        [CoreTaskStatus.Accepted] = [CoreTaskStatus.Dispatched, CoreTaskStatus.InProgress, CoreTaskStatus.Reported, CoreTaskStatus.Accepted],
    };

    private async Task CreateAtAsync(string slug, CoreTaskStatus status)
    {
        var state = Assert.IsType<TaskWriteResult.Written>(
            await _tasks.CreateAsync(slug, "implementation", CancellationToken.None)).State;

        foreach (var next in PathTo[status])
        {
            state = Assert.IsType<TaskWriteResult.Written>(await _tasks.TransitionAsync(
                state, next, TransitionOrigin.Human, null, CancellationToken.None)).State;
        }

        Assert.Equal(status, state.Status);
    }

    private async Task<CoreTaskStatus> StatusAsync(string slug) =>
        (await _tasks.ReadAsync(slug, CancellationToken.None) as TaskReadResult.Found)!.State.Status;

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
