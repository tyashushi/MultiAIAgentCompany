using MultiAIAgentCompany.Core.Coordination;
using Xunit;
using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// 設計 §37。<b>止まる条件を全部ここで固定する</b>（§37-6）——
/// 呼び出し側に散らすと「1つ忘れたときだけ勝手に進む」壊れ方をする。
/// </summary>
public sealed class PlanAdvanceTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 12, 10, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void 渡していない工程があれば_それを渡す()
    {
        var next = Assert.IsType<PlanNext.Dispatch>(Decide(Plan(Step("research"))));

        Assert.Equal(0, next.Index);
        Assert.Equal("research", next.Step.DepartmentId);
    }

    [Fact]
    public void 人間が止めたら_他より先に止まる()
    {
        // **順番が大事。** 他の条件を先に見ると、止めた直後の1周で次を渡してしまう。
        var plan = Plan(Step("research")) with { StoppedByHuman = true };
        var next = Assert.IsType<PlanNext.NeedsHuman>(Decide(plan));

        Assert.Equal("人間が止めた", next.Reason);
    }

    [Fact]
    public void 止めていても_全工程を受理し終えていれば終わり()
    {
        // 実機で踏んだ形（§62-29）: 止めたあと残りを人間が受理して回すと、
        // 8工程すべて `✓ 受理した` なのに帯は「止めている」のままで、
        // 出口が「計画を続ける」しか無かった。
        var plan = Plan(Step("research", slug: "t1"), Step("design", slug: "t2"))
            with { StoppedByHuman = true };

        Assert.IsType<PlanNext.Done>(
            Decide(plan, ("t1", CoreTaskStatus.Accepted), ("t2", CoreTaskStatus.Accepted)));
    }

    [Fact]
    public void 止めた計画で_状態を読めない工程があれば_終わりにしない()
    {
        // **読めないものを「たぶん終わった」にしない**（§7）。止まったままで良い。
        var plan = Plan(Step("research", slug: "t1"), Step("design", slug: "t2"))
            with { StoppedByHuman = true };

        var next = Assert.IsType<PlanNext.NeedsHuman>(Decide(plan, ("t1", CoreTaskStatus.Accepted)));
        Assert.Equal("人間が止めた", next.Reason);
    }

    [Fact]
    public void 工程が1つも無ければ止まる()
    {
        Assert.IsType<PlanNext.NeedsHuman>(Decide(Plan()));
    }

    [Theory]
    [InlineData(CoreTaskStatus.Dispatched)]
    [InlineData(CoreTaskStatus.InProgress)]
    public void 動いている工程は待つ(CoreTaskStatus status)
    {
        var plan = Plan(Step("research", slug: "t1"), Step("design"));
        var next = Assert.IsType<PlanNext.Wait>(Decide(plan, ("t1", status)));

        Assert.Equal(0, next.Index);
    }

    [Fact]
    public void 渡っていない仕事は_こちらの番として渡す()
    {
        // **`Drafted` を「動いている」に混ぜない**（§37-6b）——
        // 混ぜると、渡せなかった計画が永久に待ち続ける。
        var plan = Plan(Step("research", slug: "t1"), Step("design"));
        var next = Assert.IsType<PlanNext.Deliver>(Decide(plan, ("t1", CoreTaskStatus.Drafted)));

        Assert.Equal(0, next.Index);
        Assert.Equal("t1", next.Slug);
    }

    [Fact]
    public void レビュワーが2人なら_全員が通るまで受理しない()
    {
        // §29 の設計レビュー2部門で、**必ず起きる形**。
        var plan = Plan(
            Step("design", slug: "t1"),
            Step("review-a", reviews: 0, slug: "t2", verdict: ReviewVerdict.Ok),
            Step("review-b", reviews: 0));

        // 1人目の報告は受理する（その工程は終わっている）。
        var accept = Assert.IsType<PlanNext.AcceptStep>(
            Decide(plan, ("t1", CoreTaskStatus.Reported), ("t2", CoreTaskStatus.Reported)));
        Assert.Equal(1, accept.Index);

        // **設計はまだ受理しない。** 2人目を渡しに行く。
        var next = Assert.IsType<PlanNext.Dispatch>(
            Decide(plan, ("t1", CoreTaskStatus.Reported), ("t2", CoreTaskStatus.Accepted)));

        Assert.Equal(2, next.Index);
    }

    [Fact]
    public void レビュワーが2人とも通ってはじめて受理する()
    {
        var plan = Plan(
            Step("design", slug: "t1"),
            Step("review-a", reviews: 0, slug: "t2", verdict: ReviewVerdict.Ok),
            Step("review-b", reviews: 0, slug: "t3", verdict: ReviewVerdict.Ok));

        var next = Assert.IsType<PlanNext.AcceptStep>(Decide(plan,
            ("t1", CoreTaskStatus.Reported), ("t2", CoreTaskStatus.Reported), ("t3", CoreTaskStatus.Reported)));

        Assert.Equal(0, next.Index);
    }

    [Fact]
    public void 通らないまま終わったレビューは_素通りさせない()
    {
        // 人間が判定の無い報告を受理した場合など。**ここを抜けると実装まで走る。**
        var plan = Plan(
            Step("design", slug: "t1"),
            Step("review", reviews: 0, slug: "t2", verdict: ReviewVerdict.Unknown),
            Step("implementation"));

        var next = Assert.IsType<PlanNext.NeedsHuman>(
            Decide(plan, ("t1", CoreTaskStatus.Reported), ("t2", CoreTaskStatus.Accepted)));

        Assert.Contains("通らないまま終わっている", next.Reason);
    }

    [Fact]
    public void 受理していない工程を残したまま_終わりにしない()
    {
        var plan = Plan(
            Step("design", slug: "t1"),
            Step("review", reviews: 0, slug: "t2"));

        // レビューが受理済みでも、判定が無ければ設計は受理されていない。
        var next = Assert.IsType<PlanNext.NeedsHuman>(
            Decide(plan, ("t1", CoreTaskStatus.Reported), ("t2", CoreTaskStatus.Accepted)));

        Assert.Contains("通らないまま終わっている", next.Reason);
    }

    [Theory]
    [InlineData(CoreTaskStatus.Failed, "失敗")]
    [InlineData(CoreTaskStatus.Cancelled, "取り消")]
    [InlineData(CoreTaskStatus.AwaitingAnswer, "質問")]
    [InlineData(CoreTaskStatus.Rejected, "差し戻")]
    public void 止まる条件では_飛ばさず人間を呼ぶ(CoreTaskStatus status, string reason)
    {
        var plan = Plan(Step("research", slug: "t1"), Step("design"));
        var next = Assert.IsType<PlanNext.NeedsHuman>(Decide(plan, ("t1", status)));

        Assert.Contains(reason, next.Reason);
        Assert.Equal(0, next.Index);
    }

    [Fact]
    public void 状態を読めない工程では_たぶん終わったにしない()
    {
        var plan = Plan(Step("research", slug: "t1"), Step("design"));
        Assert.IsType<PlanNext.NeedsHuman>(Decide(plan));
    }

    [Fact]
    public void 途中の工程の報告は_計画が受理する()
    {
        var plan = Plan(Step("research", slug: "t1"), Step("design"));
        var next = Assert.IsType<PlanNext.AcceptStep>(Decide(plan, ("t1", CoreTaskStatus.Reported)));

        Assert.Equal(0, next.Index);
    }

    [Fact]
    public void 最後の工程の報告は_人間が受理する()
    {
        // **ここを自動にすると、誰も成果物を読まないまま計画が終わる**（§37-3）。
        var plan = Plan(Step("research", slug: "t1"));
        var next = Assert.IsType<PlanNext.NeedsHuman>(Decide(plan, ("t1", CoreTaskStatus.Reported)));

        Assert.Contains("受理", next.Reason);
    }

    [Fact]
    public void 受理済みの工程は次へ進む()
    {
        var plan = Plan(Step("research", slug: "t1"), Step("design"));
        var next = Assert.IsType<PlanNext.Dispatch>(Decide(plan, ("t1", CoreTaskStatus.Accepted)));

        Assert.Equal(1, next.Index);
    }

    [Fact]
    public void 全部受理されたら終わり()
    {
        var plan = Plan(Step("research", slug: "t1"), Step("design", slug: "t2"));
        Assert.IsType<PlanNext.Done>(
            Decide(plan, ("t1", CoreTaskStatus.Accepted), ("t2", CoreTaskStatus.Accepted)));
    }

    [Fact]
    public void レビュー待ちの工程は_受理せずに次へ進む()
    {
        // **受理は終端。** ここで受理すると、駄目出しが来ても §19 の差し戻しに入れない。
        var plan = Plan(
            Step("design", slug: "t1"),
            Step("review", reviews: 0),
            Step("implementation"));

        var next = Assert.IsType<PlanNext.Dispatch>(Decide(plan, ("t1", CoreTaskStatus.Reported)));

        Assert.Equal(1, next.Index);
        Assert.Equal("review", next.Step.DepartmentId);
    }

    [Fact]
    public void レビューが通ってはじめて_見てもらった工程を受理する()
    {
        var plan = Plan(
            Step("design", slug: "t1"),
            Step("review", reviews: 0, slug: "t2", verdict: ReviewVerdict.Ok),
            Step("implementation"));

        var next = Assert.IsType<PlanNext.AcceptStep>(
            Decide(plan, ("t1", CoreTaskStatus.Reported), ("t2", CoreTaskStatus.Reported)));

        Assert.Equal(0, next.Index);
    }

    [Fact]
    public void レビューが動いている間は_見てもらった工程を待たせたまま待つ()
    {
        var plan = Plan(
            Step("design", slug: "t1"),
            Step("review", reviews: 0, slug: "t2"),
            Step("implementation"));

        var next = Assert.IsType<PlanNext.Wait>(
            Decide(plan, ("t1", CoreTaskStatus.Reported), ("t2", CoreTaskStatus.Dispatched)));

        Assert.Equal(1, next.Index);
    }

    [Fact]
    public void レビューが通せば_次へ進む()
    {
        var plan = Plan(
            Step("design", slug: "t1"),
            Step("review", reviews: 0, slug: "t2", verdict: ReviewVerdict.Ok),
            Step("implementation"));

        var next = Assert.IsType<PlanNext.AcceptStep>(
            Decide(plan, ("t1", CoreTaskStatus.Accepted), ("t2", CoreTaskStatus.Reported)));

        Assert.Equal(1, next.Index);
    }

    [Fact]
    public void レビューが直しを求めたら_見てもらった工程へ戻す()
    {
        var plan = Plan(
            Step("design", slug: "t1"),
            Step("review", reviews: 0, slug: "t2", verdict: ReviewVerdict.NeedsRevision),
            Step("implementation"));

        var next = Assert.IsType<PlanNext.SendBack>(
            Decide(plan, ("t1", CoreTaskStatus.Accepted), ("t2", CoreTaskStatus.Reported)));

        Assert.Equal(1, next.ReviewIndex);
        Assert.Equal(0, next.TargetIndex);
        Assert.Equal("design", next.Target.DepartmentId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ReviewVerdict.Unknown)]
    public void レビューの判定が読めなければ_人間を呼ぶ(ReviewVerdict? verdict)
    {
        var plan = Plan(
            Step("design", slug: "t1"),
            Step("review", reviews: 0, slug: "t2", verdict: verdict),
            Step("implementation"));

        var next = Assert.IsType<PlanNext.NeedsHuman>(
            Decide(plan, ("t1", CoreTaskStatus.Accepted), ("t2", CoreTaskStatus.Reported)));

        Assert.Contains("判定", next.Reason);
    }

    [Fact]
    public void 差し戻しが上限に達したら_止まる()
    {
        var plan = Plan(
            Step("design", slug: "t1"),
            Step("review", reviews: 0, slug: "t2", verdict: ReviewVerdict.NeedsRevision)) with
        {
            Revisions = Core.Coordination.Plan.DefaultRevisionLimit,
        };

        var next = Assert.IsType<PlanNext.NeedsHuman>(
            Decide(plan, ("t1", CoreTaskStatus.Accepted), ("t2", CoreTaskStatus.Reported)));

        Assert.Contains("上限", next.Reason);
    }

    [Fact]
    public void 上限の1つ手前までは戻す()
    {
        var plan = Plan(
            Step("design", slug: "t1"),
            Step("review", reviews: 0, slug: "t2", verdict: ReviewVerdict.NeedsRevision)) with
        {
            Revisions = Core.Coordination.Plan.DefaultRevisionLimit - 1,
        };

        Assert.IsType<PlanNext.SendBack>(
            Decide(plan, ("t1", CoreTaskStatus.Accepted), ("t2", CoreTaskStatus.Reported)));
    }

    [Fact]
    public void レビューが見る相手のすぐ後に無い計画は_進めずに止まる()
    {
        // 設計 §62-13。この規則より前に作られた計画も、進める前に止める。
        var plan = Plan(
            Step("design", slug: "t1"),
            Step("implementation"),
            Step("review", reviews: 0));

        var next = Assert.IsType<PlanNext.NeedsHuman>(Decide(plan, ("t1", CoreTaskStatus.Reported)));
        Assert.Contains("レビューは見る相手のすぐ後に置く", next.Reason);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(-1)]
    [InlineData(1)]
    public void 戻り先が計画に無ければ_止まる(int reviews)
    {
        // 1 は自分自身。**自分へ戻すと永久に回る。**
        var plan = Plan(
            Step("design", slug: "t1"),
            Step("review", reviews: reviews, slug: "t2", verdict: ReviewVerdict.NeedsRevision));

        var next = Assert.IsType<PlanNext.NeedsHuman>(
            Decide(plan, ("t1", CoreTaskStatus.Accepted), ("t2", CoreTaskStatus.Reported)));

        Assert.Contains("戻り先", next.Reason);
    }

    [Theory]
    [InlineData(CoreTaskStatus.Dispatched, null, true)]
    [InlineData(CoreTaskStatus.InProgress, null, true)]
    [InlineData(CoreTaskStatus.AwaitingAnswer, null, true)]
    [InlineData(CoreTaskStatus.Reported, null, true)]
    [InlineData(CoreTaskStatus.Reported, ReviewVerdict.Ok, false)]
    [InlineData(CoreTaskStatus.Accepted, ReviewVerdict.Ok, false)]
    public void レビューが見ている間の工程は_人間に判断させない(CoreTaskStatus review, ReviewVerdict? verdict, bool under)
    {
        // 設計 §62-17（人間の決定）。実機では、監査が見ている最中のテストを人間が先に受理していた。
        var plan = Plan(
            Step("testing", slug: "t1"),
            Step("audit", reviews: 0, slug: "t2", verdict: verdict));

        var result = PlanAdvance.UnderReview(plan, States(("t1", CoreTaskStatus.Reported), ("t2", review)));

        Assert.Equal(under, result.TryGetValue("t1", out var reviewer));
        if (under) Assert.Equal("audit", reviewer);
    }

    [Fact]
    public void レビューがまだ渡っていない工程と_人間が止めた計画は_判断させる()
    {
        // partial で止まったときなど、人間が決めるほか無い。
        var waiting = Plan(Step("implementation", slug: "t1"), Step("review", reviews: 0));
        Assert.Empty(PlanAdvance.UnderReview(waiting, States(("t1", CoreTaskStatus.Reported))));

        var stopped = Plan(Step("implementation", slug: "t1"), Step("review", reviews: 0, slug: "t2")) with { StoppedByHuman = true };
        Assert.Empty(PlanAdvance.UnderReview(stopped, States(("t1", CoreTaskStatus.Reported), ("t2", CoreTaskStatus.Dispatched))));
    }

    private static Dictionary<string, TaskState> States(params (string Slug, CoreTaskStatus Status)[] states) =>
        states.ToDictionary(
            row => row.Slug,
            row => new TaskState(row.Slug, row.Status, 1, 1, "dept", TransitionOrigin.Plan, At),
            StringComparer.Ordinal);

    private static PlanNext Decide(Plan plan, params (string Slug, CoreTaskStatus Status)[] states) =>
        PlanAdvance.Decide(
            plan,
            states.ToDictionary(
                row => row.Slug,
                row => new TaskState(row.Slug, row.Status, 1, 1, "dept", TransitionOrigin.Plan, At),
                StringComparer.Ordinal));

    private static PlanStep Step(
        string departmentId, int? reviews = null, string? slug = null, ReviewVerdict? verdict = null) =>
        new(departmentId, "次へ", reviews, slug, verdict);

    private static Plan Plan(params PlanStep[] steps) =>
        new("plan-1", "ログイン画面を作りたい", steps, 0, false, 1, At, At);
}
