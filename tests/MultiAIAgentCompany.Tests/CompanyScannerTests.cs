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
    public async Task AwaitingAnswerでもreportが出たらReportedにする()
    {
        // **報告は仕事の終わりで、その規則は状態によって変わらない**（§16-1）。
        // 以前は `Dispatched or InProgress` の中にだけ書いてあったので、
        // 質問を出したあと自分で進めて報告した部門が、**永久に「質問に答える」のまま**残った。
        await CreateAtAsync("selfresolved", CoreTaskStatus.AwaitingAnswer);
        await File.WriteAllTextAsync(_workspace.Paths.Question("selfresolved"), "判断してください");
        await File.WriteAllTextAsync(_workspace.Paths.Report("selfresolved"), "自分で決めて進めました");

        var result = await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        Assert.Equal(CoreTaskStatus.Reported, await StatusAsync("selfresolved"));
        Assert.Equal("report.md が publish された", Assert.Single(result.Applied).Because);
    }

    [Fact]
    public async Task AwaitingAnswerでreportが無ければ動かさない()
    {
        // 上の修正で AwaitingAnswer を素通しにしていないこと。
        await CreateAtAsync("waiting", CoreTaskStatus.AwaitingAnswer);
        await File.WriteAllTextAsync(_workspace.Paths.Question("waiting"), "判断してください");

        var result = await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        Assert.Empty(result.Applied);
        Assert.Equal(CoreTaskStatus.AwaitingAnswer, await StatusAsync("waiting"));
    }

    [Fact]
    public async Task 走査はanswerでは状態を進めない()
    {
        // 進めるのは部門へ届いたあと（§16-5）。ここで InProgress を書くと、
        // 送る前に落ちたときに「部門が作業中」が嘘になる。
        await CreateAtAsync("feature", CoreTaskStatus.AwaitingAnswer);
        await File.WriteAllTextAsync(_workspace.Paths.Answer("feature"), "回答");

        var result = await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        Assert.Empty(result.Applied);
        Assert.Equal(CoreTaskStatus.AwaitingAnswer, await StatusAsync("feature"));
    }

    [Fact]
    public async Task 回答済みの質問へ戻り続けない()
    {
        // 回答を届けて InProgress にしたあとも question.md は残る。
        // 配達記録が無いと、走査のたびに AwaitingAnswer へ戻る（設計 §20-1）。
        await DeliveredAsync("feature", "質問");

        var result = await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        Assert.Empty(result.Applied);
        Assert.Equal(CoreTaskStatus.InProgress, await StatusAsync("feature"));
    }

    [Fact]
    public async Task 同じ試行で2度目の質問に気付く()
    {
        // **§18-1 の2番。** publish は同じ最終名への rename なので、2度目は1度目を上書きする。
        // 「answer.md があるか」だけを見ていると、新しい質問が永久に人間へ出ない（§20-1）。
        await DeliveredAsync("feature", "1度目の質問");
        await File.WriteAllTextAsync(_workspace.Paths.Question("feature"), "2度目の質問");

        var result = await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        Assert.Equal(CoreTaskStatus.AwaitingAnswer, Assert.Single(result.Applied).To);
        Assert.Equal(CoreTaskStatus.AwaitingAnswer, await StatusAsync("feature"));
    }

    [Fact]
    public async Task 質問が同じなら回答が書き換わっても戻らない()
    {
        // 届けたあとの answer.md 編集は、部門が回答を待っている合図ではない（設計 §20-5）。
        await DeliveredAsync("feature", "質問");
        await File.WriteAllTextAsync(_workspace.Paths.Answer("feature"), "書き直した回答");

        var result = await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        Assert.Empty(result.Applied);
        Assert.Equal(CoreTaskStatus.InProgress, await StatusAsync("feature"));
    }

    [Fact]
    public async Task 差し戻したあとは同じ内容の質問でも新しい質問として見る()
    {
        // 配達記録は試行と組（設計 §20-3）。持ち越すと、次の試行の質問が回答済みに見える。
        var delivered = await DeliveredAsync("feature", "質問");
        foreach (var next in new[] { CoreTaskStatus.Reported, CoreTaskStatus.Rejected })
        {
            delivered = Assert.IsType<TaskWriteResult.Written>(await _tasks.TransitionAsync(
                delivered, next, TransitionOrigin.Human, null, CancellationToken.None)).State;
        }

        await File.WriteAllTextAsync(_workspace.Paths.NextInstruction("feature"), "やり直し");
        Assert.IsType<TaskWriteResult.Written>(await _tasks.RedispatchAsync(delivered, CancellationToken.None));

        // 部門が**同じ内容の**質問をもう一度出す。
        await File.WriteAllTextAsync(_workspace.Paths.Question("feature"), "質問");
        var result = await _scanner.SyncAsync(CompanyScanKind.Startup, CancellationToken.None);

        Assert.Equal(CoreTaskStatus.AwaitingAnswer, Assert.Single(result.Applied).To);
    }

    /// <summary>質問に回答を届けたところまで進んだ仕事。</summary>
    private async Task<TaskState> DeliveredAsync(string slug, string question)
    {
        await CreateAtAsync(slug, CoreTaskStatus.AwaitingAnswer);
        await File.WriteAllTextAsync(_workspace.Paths.Question(slug), question);
        await File.WriteAllTextAsync(_workspace.Paths.Answer(slug), "回答");

        var state = (await _tasks.ReadAsync(slug, CancellationToken.None) as TaskReadResult.Found)!.State;
        var digest = await CompanyDigest.OfFileAsync(_workspace.Paths.Question(slug), CancellationToken.None);
        var answerDigest = await CompanyDigest.OfFileAsync(_workspace.Paths.Answer(slug), CancellationToken.None);

        return Assert.IsType<TaskWriteResult.Written>(await _tasks.RecordAnswerDeliveryAsync(
            state,
            new AnswerDelivery(state.AttemptId, digest!, answerDigest!, DateTimeOffset.UtcNow),
            CancellationToken.None)).State;
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

}
