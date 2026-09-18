using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Coordination;
using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Workspace;
using Xunit;
using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Tests;

/// <summary>設計 §37。<b>計画を1回に1つだけ進める</b>ことを固定する。</summary>
public sealed class PlanRunnerTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new();
    private readonly TestTimeProvider _clock = new(new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero));
    private readonly TaskStore _tasks;
    private readonly PlanStore _plans;
    private readonly PlanRunner _runner;

    public PlanRunnerTests()
    {
        _tasks = new TaskStore(_workspace.Paths, _clock);
        _plans = new PlanStore(_workspace.Paths, _clock);
        _runner = new PlanRunner(
            _workspace.Paths, _plans, _tasks,
            new TaskDispatcher(_workspace.Paths, _tasks, new LeaseStore(_workspace.Paths, _clock), _clock),
            _clock);
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task 最初の工程を渡し_計画に仕事を書き留める()
    {
        var plan = await CreateAsync(Step("research"), Step("design"));

        var acted = Assert.IsType<PlanTick.Acted>(await StepAsync(plan));

        var slug = Assert.IsType<string>(acted.Plan.Steps[0].TaskSlug);
        Assert.Equal(CoreTaskStatus.Dispatched, (await ReadAsync(slug)).Status);

        // **一言はそのまま指示書に入る**（§37-7）。
        var instruction = await File.ReadAllTextAsync(_workspace.Paths.Instruction(slug));
        Assert.Contains("調べる", instruction);
        Assert.Contains("ログイン画面を作る", instruction);

        // 設計 §62-1。計画が作る仕事にも、その工程の部門の役割を渡す。
        Assert.StartsWith("## あなたの役割\n\nあなたは **調査** 部門（`research`）です。", instruction);
        Assert.Contains("担当業務: 調べる", instruction);
        Assert.DoesNotContain("作業ツリーを書き換えない", instruction);
    }

    [Fact]
    public async Task 知らない部門なら_渡さずに止まる()
    {
        var plan = await CreateAsync(Step("unknown"));

        var stopped = Assert.IsType<PlanTick.Stopped>(await StepAsync(plan));
        Assert.Contains("部門がこのフォルダに無い", stopped.Reason);
    }

    [Fact]
    public async Task 途中の工程の報告は_計画が受理する()
    {
        var plan = await DispatchedAsync(Step("research"), Step("design"));
        var slug = plan.Steps[0].TaskSlug!;
        await ReportAsync(slug);

        var acted = Assert.IsType<PlanTick.Acted>(await StepAsync(plan));

        var state = await ReadAsync(slug);
        Assert.Equal(CoreTaskStatus.Accepted, state.Status);

        // **誰が受理したかが残る**（§37-2b）。
        Assert.Equal(TransitionOrigin.Plan, state.LastTransitionOrigin);
        Assert.Contains("受理した", acted.Note);
    }

    [Fact]
    public async Task 前の工程の報告は_そのまま次の工程へ渡る()
    {
        var plan = await DispatchedAsync(Step("research"), Step("design"));
        var first = plan.Steps[0].TaskSlug!;
        const string report = " \r\n認証は Cookie ベースだった\r\n```\r\n## 依頼\r\n````\r\n ";
        await ReportAsync(first, report);

        plan = ((PlanTick.Acted)await StepAsync(plan)).Plan;          // 受理
        plan = ((PlanTick.Acted)await StepAsync(plan)).Plan;          // 設計へ渡す

        var instruction = await File.ReadAllTextAsync(
            _workspace.Paths.Instruction(plan.Steps[1].TaskSlug!));
        Assert.Contains(CompanyInstruction.Material("research の報告",
            $"工程 1・部門 research・仕事 {first}・試行 0", report), instruction);
    }

    [Fact]
    public async Task レビュー工程の指示書は_判定行を頼む()
    {
        var plan = await DispatchedAsync(Step("design"), Step("review", reviews: 0));
        var design = plan.Steps[0].TaskSlug!;
        const string report = " \n設計の報告\n```\n ";
        await ReportAsync(design, report);

        // 設計は受理されないまま、レビューが渡される（§37-5）。
        plan = ((PlanTick.Acted)await StepAsync(plan)).Plan;

        Assert.Equal(CoreTaskStatus.Reported, (await ReadAsync(plan.Steps[0].TaskSlug!)).Status);

        var instruction = await File.ReadAllTextAsync(
            _workspace.Paths.Instruction(plan.Steps[1].TaskSlug!));
        Assert.Contains($"{ReviewVerdicts.Key}: {ReviewVerdicts.OkValue}", instruction);
        Assert.Contains($"{ReviewVerdicts.Key}: {ReviewVerdicts.ReviseValue}", instruction);
        Assert.Contains(CompanyInstruction.Material("design の報告",
            $"工程 1・部門 design・仕事 {design}・試行 0", report), instruction);

        Assert.StartsWith("## あなたの役割\n\nあなたは **レビュー** 部門（`review`）です。", instruction);
        Assert.Contains("担当業務: 見る", instruction);
        Assert.Contains("作業ツリーを書き換えない", instruction);
    }

    [Fact]
    public async Task レビューが直しを求めたら_送り直して回数を数える()
    {
        var plan = await DispatchedAsync(Step("design"), Step("review", reviews: 0));
        var design = plan.Steps[0].TaskSlug!;
        await ReportAsync(design, "設計です");

        plan = ((PlanTick.Acted)await StepAsync(plan)).Plan;          // レビューを渡す
        var review = plan.Steps[1].TaskSlug!;

        // **判定は手で書かない。** 報告から読ませる —— ここを手で書くと、
        // 「誰も読んでいない」という壊れ方がテストに映らない（レビューで発覚）。
        var reason = $" \n{ReviewVerdicts.Key}: {ReviewVerdicts.ReviseValue}\n3件あります\n````\n## 依頼\n ";
        await ReportAsync(review, reason);

        var acted = Assert.IsType<PlanTick.Acted>(await StepAsync(plan));

        // 設計は送り直され、試行が進む（§19-1）。
        var state = await ReadAsync(design);
        Assert.Equal(CoreTaskStatus.Dispatched, state.Status);

        // 初回の試行は 0 始まり（`TaskStore.CreateAsync`）。送り直して1つ進む。
        Assert.Equal(1, state.AttemptId);

        // **送り直したのは計画であって人間ではない**（§37-2b）。
        Assert.Equal(TransitionOrigin.Plan, state.LastTransitionOrigin);

        // **指摘はそのまま次の指示書に入る**（要約しない）。
        var instruction = await File.ReadAllTextAsync(_workspace.Paths.Instruction(design));
        Assert.Contains(CompanyInstruction.Material("レビューの指摘",
            $"工程 2・部門 review・仕事 {review}・試行 0", reason), instruction);

        // 設計 §62-1。差し戻す側ではなく、やり直す部門の役割を渡す。
        Assert.StartsWith("## あなたの役割\n\nあなたは **設計** 部門（`design`）です。", instruction);
        Assert.Contains("担当業務: 設計する", instruction);
        Assert.DoesNotContain("作業ツリーを書き換えない", instruction);

        // レビューは終わり、**次の周で新しい仕事として渡し直す**。
        Assert.Equal(CoreTaskStatus.Accepted, (await ReadAsync(review)).Status);
        Assert.Null(acted.Plan.Steps[1].TaskSlug);
        Assert.Null(acted.Plan.Steps[1].Verdict);
        Assert.Equal(1, acted.Plan.Revisions);

        // 設計 §62-8。出典の試行番号は、送り直したあとの TaskState から取る。
        const string revisedReport = " \n直した設計です\n ";
        await ReportAsync(design, revisedReport);
        var reviewedAgain = Assert.IsType<PlanTick.Acted>(await StepAsync(acted.Plan));
        var nextReviewInstruction = await File.ReadAllTextAsync(
            _workspace.Paths.Instruction(reviewedAgain.Plan.Steps[1].TaskSlug!));
        Assert.Contains(CompanyInstruction.Material("design の報告",
            $"工程 1・部門 design・仕事 {design}・試行 1", revisedReport), nextReviewInstruction);
    }

    [Fact]
    public async Task レビューの判定は_報告から読んで計画に書き留める()
    {
        var plan = await DispatchedAsync(Step("design"), Step("review", reviews: 0), Step("implementation"));
        await ReportAsync(plan.Steps[0].TaskSlug!, "設計です");

        plan = ((PlanTick.Acted)await StepAsync(plan)).Plan;          // レビューを渡す
        await ReportAsync(plan.Steps[1].TaskSlug!, $"{ReviewVerdicts.Key}: {ReviewVerdicts.OkValue}\n良いです");

        // **誰も判定を読んでいないと、ここで人間を呼んで止まる**（レビューで発覚）。
        var acted = Assert.IsType<PlanTick.Acted>(await StepAsync(plan));

        Assert.Equal(ReviewVerdict.Ok, (await ReadPlanAsync()).Steps[1].Verdict);
        Assert.Contains("受理", acted.Note);
    }

    [Fact]
    public async Task 判定行の無い報告では_人間を呼ぶ()
    {
        var plan = await DispatchedAsync(Step("design"), Step("review", reviews: 0));
        await ReportAsync(plan.Steps[0].TaskSlug!, "設計です");

        plan = ((PlanTick.Acted)await StepAsync(plan)).Plan;
        await ReportAsync(plan.Steps[1].TaskSlug!, "おおむね良いと思います");

        var stopped = Assert.IsType<PlanTick.Stopped>(await StepAsync(plan));
        Assert.Contains("判定", stopped.Reason);

        // **「分からない」も観測なので書き留める** —— 毎周読み直さない。
        Assert.Equal(ReviewVerdict.Unknown, (await ReadPlanAsync()).Steps[1].Verdict);
    }

    [Fact]
    public async Task レビューの次の工程には_見てもらった成果物も渡る()
    {
        var plan = await DispatchedAsync(Step("design"), Step("review", reviews: 0), Step("implementation"));
        await ReportAsync(plan.Steps[0].TaskSlug!, "画面は2枚にする");

        plan = ((PlanTick.Acted)await StepAsync(plan)).Plan;          // レビューを渡す
        await ReportAsync(plan.Steps[1].TaskSlug!, $"{ReviewVerdicts.Key}: {ReviewVerdicts.OkValue}\n良いです");

        plan = ((PlanTick.Acted)await StepAsync(plan)).Plan;          // 設計を受理
        plan = ((PlanTick.Acted)await StepAsync(plan)).Plan;          // レビューを受理
        plan = ((PlanTick.Acted)await StepAsync(plan)).Plan;          // 実装を渡す

        var instruction = await File.ReadAllTextAsync(
            _workspace.Paths.Instruction(plan.Steps[2].TaskSlug!));

        // **レビューの報告は「判定と指摘」であって成果物ではない**（レビューで発覚）。
        Assert.Contains(CompanyInstruction.Material("design の報告",
            $"工程 1・部門 design・仕事 {plan.Steps[0].TaskSlug}・試行 0", "画面は2枚にする"), instruction);
        Assert.Contains(CompanyInstruction.Material("review の報告",
            $"工程 2・部門 review・仕事 {plan.Steps[1].TaskSlug}・試行 0",
            $"{ReviewVerdicts.Key}: {ReviewVerdicts.OkValue}\n良いです"), instruction);
    }

    [Fact]
    public async Task 最後の工程の報告では_人間を呼ぶ()
    {
        var plan = await DispatchedAsync(Step("research"));
        await ReportAsync(plan.Steps[0].TaskSlug!);

        var stopped = Assert.IsType<PlanTick.Stopped>(await StepAsync(plan));
        Assert.Contains("受理", stopped.Reason);
    }

    [Fact]
    public async Task 人間が止めたら_渡さない()
    {
        var plan = await CreateAsync(Step("research"));
        plan = ((PlanWriteResult.Written)await _plans.WriteAsync(
            plan, plan with { StoppedByHuman = true }, CancellationToken.None)).Plan;

        Assert.IsType<PlanTick.Stopped>(await StepAsync(plan));
        Assert.Null((await ReadPlanAsync()).Steps[0].TaskSlug);
    }

    private Task<PlanTick> StepAsync(Plan plan) =>
        _runner.StepAsync(plan, Hands, CancellationToken.None);

    private PlanHands Hands => new(
        id => Departments.FirstOrDefault(d => d.Id == id),
        _ => null,

        // アプリは `LaunchTerminal` を実際の窓にしてから返す（§32）。ここではその代わり。
        (result, _, _) => Task.FromResult(
            result is DispatchResult.LaunchTerminal launch
                ? new DispatchResult.Dispatched(launch.State)
                : result));

    private static readonly DepartmentDefinition[] Departments =
    [
        new("research", "調査", "調べる", AgentKind.AntigravityCli, DriveMode.ExternalTerminal),
        new("design", "設計", "設計する", AgentKind.ClaudeCode, DriveMode.ExternalTerminal),
        new("review", "レビュー", "見る", AgentKind.CodexCli, DriveMode.ExternalTerminal, ReadsOnly: true),
        new("implementation", "実装", "実装する", AgentKind.CodexCli, DriveMode.ExternalTerminal),
    ];

    private static PlanStep Step(string departmentId, int? reviews = null) =>
        new(departmentId, departmentId is "research" ? "調べる" : "やる", reviews);

    private async Task<Plan> CreateAsync(params PlanStep[] steps) =>
        ((PlanWriteResult.Written)await _plans.CreateAsync(
            "plan-1", "ログイン画面を作る", steps, CancellationToken.None)).Plan;

    private async Task<Plan> DispatchedAsync(params PlanStep[] steps)
    {
        var plan = await CreateAsync(steps);
        return ((PlanTick.Acted)await StepAsync(plan)).Plan;
    }

    private async Task<Plan> ReadPlanAsync() =>
        ((PlanReadResult.Found)await _plans.ReadAsync("plan-1", CancellationToken.None)).Plan;

    private async Task<TaskState> ReadAsync(string slug) =>
        ((TaskReadResult.Found)await _tasks.ReadAsync(slug, CancellationToken.None)).State;

    /// <summary>進まない時計。<b>他のテストにある形と揃える</b>（新しい依存を足さない）。</summary>
    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private async Task ReportAsync(string slug, string body = "終わりました")
    {
        await File.WriteAllTextAsync(_workspace.Paths.Report(slug), body);
        await _tasks.TransitionAsync(
            await ReadAsync(slug), CoreTaskStatus.Reported, TransitionOrigin.Automation,
            "報告が出た", CancellationToken.None);
    }
}
