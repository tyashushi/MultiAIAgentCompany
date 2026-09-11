using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Coordination;
using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Status;
using MultiAIAgentCompany.Core.Workspace;
using Xunit;
using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Tests;

public sealed class TaskDispatcherTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new();
    private readonly TestTimeProvider _clock = new(new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero));
    private readonly TaskStore _tasks;
    private readonly LeaseStore _leases;
    private readonly TaskDispatcher _dispatcher;

    public TaskDispatcherTests()
    {
        _tasks = new TaskStore(_workspace.Paths, _clock);
        _leases = new LeaseStore(_workspace.Paths, _clock);
        _dispatcher = new TaskDispatcher(_workspace.Paths, _tasks, _leases, _clock);
    }

    [Fact]
    public async Task 構造化部門には指示書を渡し送信前にDispatchedを書き込む()
    {
        var expected = await CreateDraftAsync();
        var session = new FakeSession("implementation", () => _tasks.ReadAsync("feature", CancellationToken.None));

        var result = Assert.IsType<DispatchResult.Dispatched>(await DispatchAsync(expected, StructuredDepartment, session));

        Assert.Equal("実装してください", Assert.Single(session.Messages));
        Assert.Equal(CoreTaskStatus.Dispatched, result.State.Status);
        Assert.Equal(CoreTaskStatus.Dispatched, session.StatusWhenSent);
        Assert.Equal(CoreTaskStatus.Dispatched, (await ReadStateAsync()).Status);
    }

    [Fact]
    public async Task 送信例外でもDispatchedを巻き戻さない()
    {
        var expected = await CreateDraftAsync();
        var session = new FakeSession("implementation") { SendException = new IOException("pipe failed") };

        Assert.IsType<DispatchResult.SentUncertain>(await DispatchAsync(expected, StructuredDepartment, session));

        Assert.Equal(CoreTaskStatus.Dispatched, (await ReadStateAsync()).Status);
    }

    [Fact]
    public async Task 他部門の有効なWriteleaseがあればBlockedで状態を変えない()
    {
        var expected = await CreateDraftAsync();
        var leases = Assert.IsType<LeaseReadResult.Found>(await _leases.ReadAsync(CancellationToken.None)).Leases;
        await _leases.AcquireAsync(leases, LeaseKind.Write, Actor.OfDepartment("review"), "other", TimeSpan.FromMinutes(10), LeaseTakeover.Deny, CancellationToken.None);

        var result = Assert.IsType<DispatchResult.Blocked>(await DispatchAsync(expected, StructuredDepartment, new FakeSession("implementation")));

        Assert.Equal(Actor.OfDepartment("review"), result.Holder.Holder);
        Assert.Equal(CoreTaskStatus.Drafted, (await ReadStateAsync()).Status);
    }

    [Fact]
    public async Task 自部門の有効なleaseはrenewして進む()
    {
        var expected = await CreateDraftAsync();
        var leases = Assert.IsType<LeaseReadResult.Found>(await _leases.ReadAsync(CancellationToken.None)).Leases;
        var held = Assert.IsType<LeaseWriteResult.Written>(await _leases.AcquireAsync(leases, LeaseKind.Write, Actor.OfDepartment("implementation"), "old-task", TimeSpan.FromMinutes(1), LeaseTakeover.Deny, CancellationToken.None));

        Assert.IsType<DispatchResult.Dispatched>(await DispatchAsync(expected, StructuredDepartment, new FakeSession("implementation")));
        var renewed = Assert.IsType<LeaseReadResult.Found>(await _leases.ReadAsync(CancellationToken.None)).Leases.Holders[LeaseKind.Write];

        Assert.Equal(held.Leases.Holders[LeaseKind.Write].AcquiredAt, renewed.AcquiredAt);
        Assert.Equal("old-task", renewed.TaskSlug);
    }

    [Fact]
    public async Task 失効したleaseを勝手に奪わず待っても空かないと言う()
    {
        var expected = await CreateDraftAsync();
        var leases = Assert.IsType<LeaseReadResult.Found>(await _leases.ReadAsync(CancellationToken.None)).Leases;
        await _leases.AcquireAsync(leases, LeaseKind.Write, Actor.OfDepartment("review"), "other", TimeSpan.FromMinutes(1), LeaseTakeover.Deny, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(2));

        // **Blocked と型で分ける**（設計 §24-1）。同じ型に潰すと UI が両方に「待つ」と言う。
        var result = Assert.IsType<DispatchResult.BlockedByExpiredLease>(
            await DispatchAsync(expected, StructuredDepartment, new FakeSession("implementation")));

        Assert.Contains("失効", result.Reason);
        Assert.Equal(CoreTaskStatus.Drafted, (await ReadStateAsync()).Status);
    }

    [Fact]
    public async Task 指示書が無ければleaseを取らずRejectedにする()
    {
        var expected = Assert.IsType<TaskWriteResult.Written>(await _tasks.CreateAsync("feature", "implementation", CancellationToken.None)).State;

        Assert.IsType<DispatchResult.Rejected>(await DispatchAsync(expected, StructuredDepartment, new FakeSession("implementation")));

        Assert.False(File.Exists(_workspace.Paths.Lease));
    }

    [Fact]
    public async Task 許されない遷移ならRejectedにする()
    {
        var expected = await CreateDraftAsync();
        var dispatched = Assert.IsType<TaskWriteResult.Written>(await _tasks.TransitionAsync(expected, CoreTaskStatus.Dispatched, TransitionOrigin.Automation, null, CancellationToken.None)).State;
        var inProgress = Assert.IsType<TaskWriteResult.Written>(await _tasks.TransitionAsync(dispatched, CoreTaskStatus.InProgress, TransitionOrigin.Automation, null, CancellationToken.None)).State;
        var reported = Assert.IsType<TaskWriteResult.Written>(await _tasks.TransitionAsync(inProgress, CoreTaskStatus.Reported, TransitionOrigin.Automation, null, CancellationToken.None)).State;
        var accepted = Assert.IsType<TaskWriteResult.Written>(await _tasks.TransitionAsync(reported, CoreTaskStatus.Accepted, TransitionOrigin.Human, null, CancellationToken.None)).State;

        Assert.IsType<DispatchResult.Rejected>(await DispatchAsync(accepted, StructuredDepartment, new FakeSession("implementation")));
    }

    [Fact]
    public async Task 古いexpectedならConflictedにする()
    {
        var expected = await CreateDraftAsync();
        await _tasks.TransitionAsync(expected, CoreTaskStatus.Dispatched, TransitionOrigin.Automation, null, CancellationToken.None);

        Assert.IsType<DispatchResult.Conflicted>(await DispatchAsync(expected, StructuredDepartment, new FakeSession("implementation")));
    }

    [Fact]
    public async Task 差し戻しの送り直しは古い指示ではなく昇格した指示を送る()
    {
        // **この案件で一番踏みやすい罠**（設計 §19-1）。dispatch は instruction.md を
        // 先に読んでから遷移するので、そのまま当てると古い指示が飛ぶ。
        var rejected = await CreateRejectedAsync();
        await File.WriteAllTextAsync(_workspace.Paths.NextInstruction("feature"), "やり直してください");
        var session = new FakeSession("implementation");

        var result = Assert.IsType<DispatchResult.Dispatched>(await _dispatcher.RedispatchAsync(
            rejected, StructuredDepartment, session, TimeSpan.FromMinutes(10), CancellationToken.None));

        Assert.Equal("やり直してください", Assert.Single(session.Messages));
        Assert.Equal(1, result.State.AttemptId);
        Assert.Equal(CoreTaskStatus.Dispatched, (await ReadStateAsync()).Status);
    }

    [Fact]
    public async Task 次の指示が無ければ送り直さず状態も動かさない()
    {
        var rejected = await CreateRejectedAsync();
        var session = new FakeSession("implementation");

        Assert.IsType<DispatchResult.Rejected>(await _dispatcher.RedispatchAsync(
            rejected, StructuredDepartment, session, TimeSpan.FromMinutes(10), CancellationToken.None));

        Assert.Empty(session.Messages);
        Assert.Equal(CoreTaskStatus.Rejected, (await ReadStateAsync()).Status);
        // 進めなかったのに lease を握ったままにしない（§14-2）。
        var leases = Assert.IsType<LeaseReadResult.Found>(await _leases.ReadAsync(CancellationToken.None));
        Assert.True(leases.Leases.CanAcquire(LeaseKind.Write, _clock.GetUtcNow()));
    }

    [Fact]
    public async Task 送り直しの送信例外でもDispatchedを巻き戻さない()
    {
        var rejected = await CreateRejectedAsync();
        await File.WriteAllTextAsync(_workspace.Paths.NextInstruction("feature"), "やり直してください");
        var session = new FakeSession("implementation") { SendException = new IOException("pipe failed") };

        Assert.IsType<DispatchResult.SentUncertain>(await _dispatcher.RedispatchAsync(
            rejected, StructuredDepartment, session, TimeSpan.FromMinutes(10), CancellationToken.None));

        Assert.Equal(CoreTaskStatus.Dispatched, (await ReadStateAsync()).Status);
    }

    [Fact]
    public async Task もう報告が出ている仕事へは回答を届けない()
    {
        // 走査が AwaitingAnswer → Reported を書くまでには隙間がある。
        // その間にここを通ると、**終わっている部門へ回答を送る**ことになる。
        var awaiting = await CreateAwaitingAnswerAsync("最初の質問");
        await File.WriteAllTextAsync(_workspace.Paths.Report("feature"), "自分で決めて進めました");

        var rejected = Assert.IsType<DispatchResult.Rejected>(await _dispatcher.DeliverAnswerAsync(
            awaiting, StructuredDepartment, new FakeSession("implementation"), CancellationToken.None));

        Assert.Contains("report.md", rejected.Reason);
    }

    [Fact]
    public async Task 回答を届けたら何に答えたかを記録する()
    {
        var awaiting = await CreateAwaitingAnswerAsync("最初の質問");

        var result = Assert.IsType<DispatchResult.Dispatched>(await _dispatcher.DeliverAnswerAsync(
            awaiting, StructuredDepartment, new FakeSession("implementation"), CancellationToken.None));

        var delivery = Assert.IsType<AnswerDelivery>(result.State.AnswerDelivery);
        Assert.Equal(awaiting.AttemptId, delivery.AttemptId);
        Assert.Equal(
            await CompanyDigest.OfFileAsync(_workspace.Paths.Question("feature"), CancellationToken.None),
            delivery.QuestionSha256);
    }

    [Fact]
    public async Task 送信中に来た2度目の質問を回答済みにしない()
    {
        // **静かに壊れる形**（設計 §20-2）。送ったあとに question.md を読み直して記録すると、
        // その間に publish された質問へ回答済みの印が付き、人間へ永久に出なくなる。
        var awaiting = await CreateAwaitingAnswerAsync("1度目の質問");
        var questionPath = _workspace.Paths.Question("feature");
        var session = new FakeSession("implementation")
        {
            DuringSend = () => File.WriteAllText(questionPath, "2度目の質問"),
        };

        var result = Assert.IsType<DispatchResult.Dispatched>(await _dispatcher.DeliverAnswerAsync(
            awaiting, StructuredDepartment, session, CancellationToken.None));

        var delivery = Assert.IsType<AnswerDelivery>(result.State.AnswerDelivery);
        Assert.NotEqual(
            await CompanyDigest.OfFileAsync(questionPath, CancellationToken.None),
            delivery.QuestionSha256);

        // 走査は2度目の質問に気付く。
        var scanner = new CompanyScanner(_workspace.Paths, _tasks);
        var scan = await scanner.SyncAsync(CompanyScanKind.Periodic, CancellationToken.None);
        Assert.Equal(CoreTaskStatus.AwaitingAnswer, Assert.Single(scan.Applied).To);
    }

    [Fact]
    public async Task 前回の回答のままで新しい質問には送らない()
    {
        // 送れてしまうと、部門には噛み合わない回答が届くだけで失敗にも見えない（設計 §20-4）。
        var awaiting = await CreateAwaitingAnswerAsync("1度目の質問");
        var delivered = Assert.IsType<DispatchResult.Dispatched>(await _dispatcher.DeliverAnswerAsync(
            awaiting, StructuredDepartment, new FakeSession("implementation"), CancellationToken.None)).State;

        await File.WriteAllTextAsync(_workspace.Paths.Question("feature"), "2度目の質問");
        var back = Assert.IsType<TaskWriteResult.Written>(await _tasks.TransitionAsync(
            delivered, CoreTaskStatus.AwaitingAnswer, TransitionOrigin.Automation, null, CancellationToken.None)).State;
        var session = new FakeSession("implementation");

        var result = Assert.IsType<DispatchResult.Rejected>(
            await _dispatcher.DeliverAnswerAsync(back, StructuredDepartment, session, CancellationToken.None));

        Assert.Contains("前回届けた回答のまま", result.Reason);
        Assert.Empty(session.Messages);
    }

    [Fact]
    public async Task 送信中に書き換えられた回答を送ったことにしない()
    {
        // **§20-2 は回答側にも掛かる**（Codex のレビューで出た）。読み直して hash を取ると、
        // 送っていない bytes を送った記録になり、次の質問への回答が
        // §20-4 の番人に「前回と同じ」と誤判定される。
        var awaiting = await CreateAwaitingAnswerAsync("質問");
        var answerPath = _workspace.Paths.Answer("feature");
        var sentBytes = await File.ReadAllBytesAsync(answerPath);
        var session = new FakeSession("implementation")
        {
            DuringSend = () => File.WriteAllText(answerPath, "あとから書き換えた回答"),
        };

        var result = Assert.IsType<DispatchResult.Dispatched>(await _dispatcher.DeliverAnswerAsync(
            awaiting, StructuredDepartment, session, CancellationToken.None));

        var delivery = Assert.IsType<AnswerDelivery>(result.State.AnswerDelivery);
        Assert.Equal(CompanyDigest.OfBytes(sentBytes), delivery.AnswerSha256);
    }

    /// <summary>質問が publish され、人間が回答を置いたところ。</summary>
    private async Task<TaskState> CreateAwaitingAnswerAsync(string question)
    {
        var state = await CreateDraftAsync();
        foreach (var next in new[] { CoreTaskStatus.Dispatched, CoreTaskStatus.AwaitingAnswer })
        {
            state = Assert.IsType<TaskWriteResult.Written>(await _tasks.TransitionAsync(
                state, next, TransitionOrigin.Automation, null, CancellationToken.None)).State;
        }

        await File.WriteAllTextAsync(_workspace.Paths.Question("feature"), question);
        await File.WriteAllTextAsync(_workspace.Paths.Answer("feature"), "こうしてください");
        return state;
    }

    /// <summary>報告まで進んで差し戻された仕事。<c>instruction.md</c> と <c>report.md</c> がある。</summary>
    private async Task<TaskState> CreateRejectedAsync()
    {
        var state = await CreateDraftAsync();
        foreach (var next in new[] { CoreTaskStatus.Dispatched, CoreTaskStatus.Reported, CoreTaskStatus.Rejected })
        {
            state = Assert.IsType<TaskWriteResult.Written>(await _tasks.TransitionAsync(
                state, next, TransitionOrigin.Human, null, CancellationToken.None)).State;
        }

        await File.WriteAllTextAsync(_workspace.Paths.Report("feature"), "できました");
        return state;
    }

    [Fact]
    public async Task 読むだけの部門は書き込み権を取らない()
    {
        // **2人のレビュアーが同じ文書を読むだけで直列化されない**（設計 §29-1 / §14-2）。
        var expected = await CreateDraftAsync();

        Assert.IsType<DispatchResult.Dispatched>(await _dispatcher.DispatchAsync(
            expected, ReadOnlyDepartment, new FakeSession("implementation"),
            TimeSpan.FromMinutes(10), CancellationToken.None));

        var leases = Assert.IsType<LeaseReadResult.Found>(await _leases.ReadAsync(CancellationToken.None));
        Assert.True(leases.Leases.CanAcquire(LeaseKind.Write, _clock.GetUtcNow()));
    }

    [Fact]
    public async Task 読むだけの部門も書いている最中は待つ()
    {
        // **取らないことと、無視することは違う**（設計 §29-1、レビューで発覚）。
        // 書き換え中の作業ツリーを読むと、途中の状態を読んで誤った指摘を出す。
        var expected = await CreateDraftAsync();
        var leases = Assert.IsType<LeaseReadResult.Found>(await _leases.ReadAsync(CancellationToken.None)).Leases;
        await _leases.AcquireAsync(leases, LeaseKind.Write, Actor.OfDepartment("review"), "other",
            TimeSpan.FromMinutes(10), LeaseTakeover.Deny, CancellationToken.None);

        var result = Assert.IsType<DispatchResult.Blocked>(await _dispatcher.DispatchAsync(
            expected, ReadOnlyDepartment, new FakeSession("implementation"),
            TimeSpan.FromMinutes(10), CancellationToken.None));

        Assert.Contains("書き込み中", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 失効した書き手は読むだけの部門を止めない()
    {
        // 失効した保持者は書いていない（§24 と同じ判定）。
        var expected = await CreateDraftAsync();
        var leases = Assert.IsType<LeaseReadResult.Found>(await _leases.ReadAsync(CancellationToken.None)).Leases;
        await _leases.AcquireAsync(leases, LeaseKind.Write, Actor.OfDepartment("review"), "other",
            TimeSpan.FromMinutes(1), LeaseTakeover.Deny, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(2));

        Assert.IsType<DispatchResult.Dispatched>(await _dispatcher.DispatchAsync(
            expected, ReadOnlyDepartment, new FakeSession("implementation"),
            TimeSpan.FromMinutes(10), CancellationToken.None));
    }

    private static readonly DepartmentDefinition ReadOnlyDepartment =
        new("implementation", "設計レビュー", "設計を読む", AgentKind.CodexCli, DriveMode.Structured,
            Model: null, ReadsOnly: true);

    [Fact]
    public async Task 外部ターミナルの部門には送らず窓を開く要求を返す()
    {
        // **Core はプロセスを起こさない**（§9 でアプリが全部門の親）。
        // 状態は書いたうえで、「何を開くべきか」だけを返す（設計 §32）。
        var draft = await CreateDraftAsync();

        var result = Assert.IsType<DispatchResult.LaunchTerminal>(
            await DispatchAsync(draft, TerminalDepartment, session: null));

        Assert.Equal(CoreTaskStatus.Dispatched, result.State.Status);
        Assert.Equal(CoreTaskStatus.Dispatched, (await ReadStateAsync()).Status);
    }

    [Fact]
    public async Task 窓を開く要求に指示書の本文を入れない()
    {
        // **§32-2f の実測。** argv は ps に出るので、指示に秘密が入り得る以上そこへ流さない。
        var draft = await CreateDraftAsync();
        await File.WriteAllTextAsync(
            _workspace.Paths.Instruction("feature"), "ここに秘密が書いてあるかもしれない");

        var result = Assert.IsType<DispatchResult.LaunchTerminal>(
            await DispatchAsync(draft, TerminalDepartment, session: null));

        Assert.All(
            result.Request.Arguments,
            argument => Assert.DoesNotContain("秘密", argument));
        Assert.Contains(result.Request.Arguments, a => a.Contains("instruction.md"));
    }

    [Fact]
    public async Task 外部ターミナルの部門はセッションが無くても弾かれない()
    {
        // Structured は session が要る（送る先が無い）が、こちらは窓を開くので要らない。
        var draft = await CreateDraftAsync();

        Assert.IsNotType<DispatchResult.Rejected>(
            await DispatchAsync(draft, TerminalDepartment, session: null));
    }

    [Fact]
    public async Task 保持者の仕事が終わっていたら待てとは言わない()
    {
        // **2026-09-09 に実機で踏んだ。** Failed の仕事が lease を握ったまま残り、
        // 画面は「失敗ではない。待つ」と出していた —— 待っても、その仕事はもう動かない。
        var dead = Assert.IsType<TaskWriteResult.Written>(
            await _tasks.CreateAsync("dead", "review", CancellationToken.None)).State;
        dead = Assert.IsType<TaskWriteResult.Written>(await _tasks.TransitionAsync(
            dead, CoreTaskStatus.Dispatched, TransitionOrigin.Human, null, CancellationToken.None)).State;
        Assert.IsType<TaskWriteResult.Written>(await _tasks.TransitionAsync(
            dead, CoreTaskStatus.Failed, TransitionOrigin.Automation, "権限を拒否された", CancellationToken.None));

        var leases = await ReadLeasesAsync();
        await _leases.AcquireAsync(leases, LeaseKind.Write, Actor.OfDepartment("review"), "dead",
            TimeSpan.FromMinutes(10), LeaseTakeover.Deny, CancellationToken.None);

        var draft = await CreateDraftAsync();
        var blocked = Assert.IsType<DispatchResult.Blocked>(
            await DispatchAsync(draft, StructuredDepartment, new FakeSession("implementation")));

        Assert.True(blocked.HolderWorkIsOver);
        Assert.Contains("待っても空きません", blocked.Reason);
    }

    [Fact]
    public async Task 抱えている仕事が無くなったら書き込み権を返す()
    {
        // **仕事を1つ終えるたびにワークスペースが塞がっていた**（2026-09-09）。
        var done = Assert.IsType<TaskWriteResult.Written>(
            await _tasks.CreateAsync("done", "implementation", CancellationToken.None)).State;
        done = Assert.IsType<TaskWriteResult.Written>(await _tasks.TransitionAsync(
            done, CoreTaskStatus.Dispatched, TransitionOrigin.Human, null, CancellationToken.None)).State;
        done = Assert.IsType<TaskWriteResult.Written>(await _tasks.TransitionAsync(
            done, CoreTaskStatus.Reported, TransitionOrigin.Automation, null, CancellationToken.None)).State;
        Assert.IsType<TaskWriteResult.Written>(await _tasks.TransitionAsync(
            done, CoreTaskStatus.Accepted, TransitionOrigin.Human, null, CancellationToken.None));

        var leases = await ReadLeasesAsync();
        await _leases.AcquireAsync(leases, LeaseKind.Write, Actor.OfDepartment("implementation"), "done",
            TimeSpan.FromMinutes(10), LeaseTakeover.Deny, CancellationToken.None);

        Assert.True(await _dispatcher.ReleaseWriteLeaseIfIdleAsync(
            StructuredDepartment, CancellationToken.None));

        var after = await ReadLeasesAsync();
        Assert.False(after.Holders.ContainsKey(LeaseKind.Write));
    }

    [Fact]
    public async Task まだ動いている仕事があれば書き込み権を返さない()
    {
        // **無条件に返さない。** 同じ部門が別の仕事を抱えていることがある。
        var flying = Assert.IsType<TaskWriteResult.Written>(
            await _tasks.CreateAsync("flying", "implementation", CancellationToken.None)).State;
        Assert.IsType<TaskWriteResult.Written>(await _tasks.TransitionAsync(
            flying, CoreTaskStatus.Dispatched, TransitionOrigin.Human, null, CancellationToken.None));

        var leases = await ReadLeasesAsync();
        await _leases.AcquireAsync(leases, LeaseKind.Write, Actor.OfDepartment("implementation"), "flying",
            TimeSpan.FromMinutes(10), LeaseTakeover.Deny, CancellationToken.None);

        Assert.False(await _dispatcher.ReleaseWriteLeaseIfIdleAsync(
            StructuredDepartment, CancellationToken.None));

        var after = await ReadLeasesAsync();
        Assert.True(after.Holders.ContainsKey(LeaseKind.Write));
    }

    [Fact]
    public async Task 読むだけの部門は返す権利を持っていない()
    {
        // 取っていないので返せない（§29-1）。**「返した」と言わない。**
        var readOnly = StructuredDepartment with { ReadsOnly = true };

        Assert.False(await _dispatcher.ReleaseWriteLeaseIfIdleAsync(readOnly, CancellationToken.None));
    }

    private async Task<WorkspaceLeases> ReadLeasesAsync() =>
        Assert.IsType<LeaseReadResult.Found>(await _leases.ReadAsync(CancellationToken.None)).Leases;

    private async Task<TaskState> CreateDraftAsync()
    {
        var state = Assert.IsType<TaskWriteResult.Written>(await _tasks.CreateAsync("feature", "implementation", CancellationToken.None)).State;
        await File.WriteAllTextAsync(_workspace.Paths.Instruction("feature"), "実装してください");
        return state;
    }

    private async Task<TaskState> ReadStateAsync() =>
        Assert.IsType<TaskReadResult.Found>(await _tasks.ReadAsync("feature", CancellationToken.None)).State;

    private Task<DispatchResult> DispatchAsync(TaskState expected, DepartmentDefinition department, IStructuredSession? session) =>
        _dispatcher.DispatchAsync(expected, department, session, TimeSpan.FromMinutes(10), CancellationToken.None);

    private static readonly DepartmentDefinition TerminalDepartment =
        new("implementation", "実装", "実装する", AgentKind.CodexCli, DriveMode.ExternalTerminal);

    private static readonly DepartmentDefinition StructuredDepartment =
        new("implementation", "実装", "実装する", AgentKind.CodexCli, DriveMode.Structured);

    public void Dispose() => _workspace.Dispose();

#pragma warning disable CS0067 // インターフェイス契約の観測イベントはこの偽物では発火しない。
    private sealed class FakeSession(string departmentId, Func<Task<TaskReadResult>>? whenSending = null) : IStructuredSession
    {
        public List<string> Messages { get; } = [];
        public Exception? SendException { get; init; }
        public CoreTaskStatus? StatusWhenSent { get; private set; }
        public string DepartmentId { get; } = departmentId;
        public ProcessIdentity Identity { get; } = new(1, 1, 1, DateTimeOffset.UnixEpoch);
        public DriveMode Mode => DriveMode.Structured;
        public event EventHandler<Evidence>? Observed;
        public event EventHandler<LiveDiagnostic>? Diagnosed;
        public IReadOnlyList<LiveDiagnostic> RecentDiagnostics(int count) => [];
        public AgentModel? ObservedModel => null;
        public event EventHandler<int>? Exited;
        public event EventHandler<ApprovalRequest>? ApprovalRequested;
        public event EventHandler<OutcomeVerdict>? TurnFinished;
        public event EventHandler<LiveAgentMessage>? Spoke;

        /// <summary>送信の最中に外の世界が動く場合（設計 §20-2 の検証で使う）。</summary>
        public Action? DuringSend { get; init; }

        public async Task<SendOutcome> SendUserMessageAsync(string text, CancellationToken ct)
        {
            if (whenSending is not null && await whenSending() is TaskReadResult.Found found)
            {
                StatusWhenSent = found.State.Status;
            }
            DuringSend?.Invoke();
            if (SendException is not null) throw SendException;
            Messages.Add(text);
            return SendOutcome.Sent;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RespondAsync(ApprovalRequest request, ApprovalDecision decision, string? reason, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
#pragma warning restore CS0067

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    [Fact]
    public async Task セッションが無い構造化部門はDispatchedを書く前に弾く()
    {
        // Dispatched は「送ったかもしれない」を意味し、復旧時に自動再送されない（§14-1）。
        // 送れないと分かっている呼び出しでそれを書くと、誰も進められない状態が残る。
        var created = Assert.IsType<TaskWriteResult.Written>(
            await _tasks.CreateAsync("feature", "engineering", CancellationToken.None));

        var result = await _dispatcher.DispatchAsync(
            created.State, StructuredDepartment, session: null, TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.IsType<DispatchResult.Rejected>(result);
        var read = Assert.IsType<TaskReadResult.Found>(await _tasks.ReadAsync("feature", CancellationToken.None));
        Assert.Equal(CoreTaskStatus.Drafted, read.State.Status);
        Assert.Equal(1, read.State.Revision);
    }

    [Fact]
    public async Task 遷移が通らなかったらWrite_leaseを返す()
    {
        // 握ったままにすると、dispatch していないのに他の部門が失効まで待たされる（§14-2）。
        var created = Assert.IsType<TaskWriteResult.Written>(
            await _tasks.CreateAsync("feature", "engineering", CancellationToken.None));
        await File.WriteAllTextAsync(_workspace.Paths.Instruction("feature"), "やること");
        var cancelled = Assert.IsType<TaskWriteResult.Written>(await _tasks.TransitionAsync(
            created.State, CoreTaskStatus.Cancelled, TransitionOrigin.Human, null, CancellationToken.None));

        // revision は合っているが、終端からは dispatch できない。
        var result = await _dispatcher.DispatchAsync(
            cancelled.State, StructuredDepartment, new FakeSession("engineering"),
            TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.IsType<DispatchResult.Rejected>(result);
        var leases = Assert.IsType<LeaseReadResult.Found>(await _leases.ReadAsync(CancellationToken.None));
        Assert.True(leases.Leases.CanAcquire(LeaseKind.Write, _clock.GetUtcNow()));
    }

    [Fact]
    public async Task 部門のturnが失敗したら抱えている仕事をFailedにする()
    {
        // **`Failed` はこれまで到達できなかった**（設計 §30-2）。走査はファイルしか見ないので、
        // 権限拒否のように文書へ現れない失敗は、この経路でしか状態に書けない。
        var expected = await CreateDraftAsync();
        await DispatchAsync(expected, StructuredDepartment, new FakeSession("implementation"));

        var failed = await _dispatcher.FailInFlightAsync(
            "implementation", "承認されずに握りつぶされた操作がある: read_file", CancellationToken.None);

        var one = Assert.Single(failed);
        Assert.Equal("feature", one.Slug);
        Assert.Equal(CoreTaskStatus.Failed, one.Status);
        Assert.Equal(CoreTaskStatus.Failed, (await ReadStateAsync()).Status);
        Assert.Contains("read_file", (await ReadStateAsync()).Note);
    }

    [Fact]
    public async Task 報告が出ているなら_turnが失敗してもFailedにしない()
    {
        // **証拠の順を変えない**（設計 §7 / §30-3）。`Document > StructuredEvent` なので、
        // report.md があるなら走査が Reported にする。
        var expected = await CreateDraftAsync();
        await DispatchAsync(expected, StructuredDepartment, new FakeSession("implementation"));
        await File.WriteAllTextAsync(_workspace.Paths.Report("feature"), "できました");

        Assert.Empty(await _dispatcher.FailInFlightAsync("implementation", "層3が失敗した", CancellationToken.None));
        Assert.Equal(CoreTaskStatus.Dispatched, (await ReadStateAsync()).Status);
    }

    [Fact]
    public async Task 未回答の質問があるなら_turnが失敗してもFailedにしない()
    {
        // **それは人間の番**であって、仕事の失敗ではない（設計 §30-3）。
        var expected = await CreateDraftAsync();
        await DispatchAsync(expected, StructuredDepartment, new FakeSession("implementation"));
        await File.WriteAllTextAsync(_workspace.Paths.Question("feature"), "どちらにしますか");

        Assert.Empty(await _dispatcher.FailInFlightAsync("implementation", "層3が失敗した", CancellationToken.None));
        Assert.Equal(CoreTaskStatus.Dispatched, (await ReadStateAsync()).Status);
    }

    [Fact]
    public async Task 別部門の仕事はFailedにしない()
    {
        var expected = await CreateDraftAsync();
        await DispatchAsync(expected, StructuredDepartment, new FakeSession("implementation"));

        Assert.Empty(await _dispatcher.FailInFlightAsync("research", "層3が失敗した", CancellationToken.None));
        Assert.Equal(CoreTaskStatus.Dispatched, (await ReadStateAsync()).Status);
    }

    [Fact]
    public async Task 終わった仕事はturnの失敗で蒸し返さない()
    {
        // Accepted や Cancelled まで行った仕事を、あとから来た turn の失敗で動かさない。
        var created = Assert.IsType<TaskWriteResult.Written>(
            await _tasks.CreateAsync("feature", "implementation", CancellationToken.None));
        await _tasks.TransitionAsync(created.State, CoreTaskStatus.Cancelled, TransitionOrigin.Human, null, CancellationToken.None);

        Assert.Empty(await _dispatcher.FailInFlightAsync("implementation", "層3が失敗した", CancellationToken.None));
        Assert.Equal(CoreTaskStatus.Cancelled, (await ReadStateAsync()).Status);
    }
}
