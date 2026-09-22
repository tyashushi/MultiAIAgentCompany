using MultiAIAgentCompany.Core.Coordination;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// 設計 §62-35。<b>人間が走っている計画の工程を並べ替える。</b>
/// </summary>
public sealed class PlanEditTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 22, 10, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void 渡していない工程どうしは入れ替えられる()
    {
        var plan = Plan(Step("design", slug: "t1"), Step("testing"), Step("implementation"));

        var edited = Assert.IsType<PlanEditResult.Edited>(PlanEdit.Move(plan, 2, 1)).Plan;

        Assert.Equal(["design", "implementation", "testing"], edited.Steps.Select(s => s.DepartmentId));
    }

    [Fact]
    public void 渡した工程は動かせない()
    {
        // **もう終わった工程を「これから」の位置に置かない。**
        var plan = Plan(Step("design", slug: "t1"), Step("implementation"));

        var rejected = Assert.IsType<PlanEditResult.Rejected>(PlanEdit.Move(plan, 1, 0));
        Assert.Contains("渡してある", rejected.Reason);
    }

    [Fact]
    public void 並べ替えたらレビュー先を指し直す()
    {
        // **ここを忘れると、レビューが黙って別の工程を見る。**
        var plan = Plan(
            Step("research"),
            Step("design"),
            Step("review", reviews: 1),
            Step("testing"));

        // テストを先頭へ。設計は 1 → 2 に動くので、レビュー先も 2 になる。
        var edited = Assert.IsType<PlanEditResult.Edited>(PlanEdit.Move(plan, 3, 0)).Plan;

        Assert.Equal(["testing", "research", "design", "review"], edited.Steps.Select(s => s.DepartmentId));
        Assert.Equal(2, edited.Steps[3].ReviewsStep);
    }

    [Fact]
    public void 進められない並びには_しない()
    {
        // レビューは見る相手のすぐ後（§62-13）。間に別の工程を割り込ませる並べ替えは断る。
        var plan = Plan(
            Step("design"),
            Step("review", reviews: 0),
            Step("implementation"));

        var rejected = Assert.IsType<PlanEditResult.Rejected>(PlanEdit.Move(plan, 2, 1));
        Assert.Contains("レビュー", rejected.Reason);
    }

    [Fact]
    public void 同時の印を付け外しできる()
    {
        var plan = Plan(Step("design", slug: "t1"), Step("review-a", reviews: 0), Step("review-b", reviews: 0));

        var edited = Assert.IsType<PlanEditResult.Edited>(PlanEdit.SetRunsWithPrevious(plan, 2, true)).Plan;
        Assert.True(edited.Steps[2].RunsWithPrevious);

        var back = Assert.IsType<PlanEditResult.Edited>(PlanEdit.SetRunsWithPrevious(edited, 2, false)).Plan;
        Assert.False(back.Steps[2].RunsWithPrevious);
    }

    [Fact]
    public void 見る相手と同時にはできない()
    {
        var plan = Plan(Step("design"), Step("review", reviews: 0));

        var rejected = Assert.IsType<PlanEditResult.Rejected>(PlanEdit.SetRunsWithPrevious(plan, 1, true));
        Assert.Contains("同時に走る組", rejected.Reason);
    }

    [Fact]
    public void 最初の工程と_渡した工程には_同時を付けない()
    {
        var plan = Plan(Step("design"), Step("implementation", slug: "t2"));

        Assert.Contains("最初", Assert.IsType<PlanEditResult.Rejected>(
            PlanEdit.SetRunsWithPrevious(plan, 0, true)).Reason);
        Assert.Contains("渡してある", Assert.IsType<PlanEditResult.Rejected>(
            PlanEdit.SetRunsWithPrevious(plan, 1, true)).Reason);
    }

    private static PlanStep Step(string departmentId, int? reviews = null, string? slug = null) =>
        new(departmentId, "次へ", reviews, slug);

    private static Plan Plan(params PlanStep[] steps) =>
        new("plan-1", "作りたい", steps, 0, false, 1, At, At);
}
