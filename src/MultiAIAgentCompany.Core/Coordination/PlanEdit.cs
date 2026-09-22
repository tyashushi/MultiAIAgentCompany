namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// 走っている計画の工程を、人間が並べ替える（設計 §62-35、人間の要望）。
/// </summary>
/// <remarks>
/// <b>純関数。</b> ファイルに触らない —— 書くのは <see cref="PlanStore.WriteAsync"/> で、
/// ここは<b>新しい工程の列を作るか、断る理由を返すか</b>だけ（<see cref="PlanAdvance"/> と同じ形）。
/// <para>
/// <b>渡した工程は動かさない。</b> 仕事になった工程（<see cref="PlanStep.TaskSlug"/> がある）を
/// 動かすと、**もう終わった工程を「これから」の位置に置く**ことになる。
/// 動かせるのは、まだ渡していない工程どうしの入れ替えだけ。
/// </para>
/// <para>
/// <b>レビュー先は位置で指している</b>（<see cref="PlanStep.ReviewsStep"/>）ので、
/// 並べ替えたら<b>指し直す</b>。ここを忘れると、レビューが黙って別の工程を見る。
/// </para>
/// </remarks>
public static class PlanEdit
{
    /// <summary>工程を1つ動かす。</summary>
    /// <param name="from">動かす工程（0 起点）。</param>
    /// <param name="to">動かす先（0 起点）。</param>
    public static PlanEditResult Move(Plan plan, int from, int to)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (from < 0 || from >= plan.Steps.Count || to < 0 || to >= plan.Steps.Count)
        {
            return new PlanEditResult.Rejected("その工程はこの計画にありません");
        }

        if (from == to)
        {
            return new PlanEditResult.Rejected("同じ位置です");
        }

        // **渡した工程は動かさない。** 通り道の工程も含めて確かめる ——
        // 間に渡し済みの工程があると、飛び越えた先で順番の意味が変わる。
        var low = Math.Min(from, to);
        var high = Math.Max(from, to);
        for (var index = low; index <= high; index++)
        {
            if (plan.Steps[index].TaskSlug is { Length: > 0 })
            {
                return new PlanEditResult.Rejected(
                    $"工程 {index + 1}（{plan.Steps[index].DepartmentId}）はもう部門に渡してあるので、並べ替えられません");
            }
        }

        var steps = plan.Steps.ToList();
        var moved = steps[from];
        steps.RemoveAt(from);
        steps.Insert(to, moved);

        return Validated(plan, Repointed(steps, plan.Steps, from, to));
    }

    /// <summary>「前の工程と同時に走る」を付け外しする（設計 §62-33）。</summary>
    public static PlanEditResult SetRunsWithPrevious(Plan plan, int index, bool value)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (index < 0 || index >= plan.Steps.Count)
        {
            return new PlanEditResult.Rejected("その工程はこの計画にありません");
        }

        if (index is 0)
        {
            return new PlanEditResult.Rejected("最初の工程には「同時に走る」を付けられません（前の工程がありません）");
        }

        if (plan.Steps[index].TaskSlug is { Length: > 0 })
        {
            return new PlanEditResult.Rejected(
                $"工程 {index + 1}（{plan.Steps[index].DepartmentId}）はもう部門に渡してあるので、変えられません");
        }

        var steps = plan.Steps.ToList();
        steps[index] = steps[index] with { RunsWithPrevious = value };
        return Validated(plan, steps);
    }

    /// <summary>
    /// 動かしたあとのレビュー先を指し直す（設計 §62-35）。
    /// </summary>
    /// <remarks>
    /// <b>「どの工程を見るか」は動かない。</b> 動いたのは位置だけなので、
    /// 元の位置 → 新しい位置の対応で引き直す。
    /// </remarks>
    private static List<PlanStep> Repointed(
        List<PlanStep> steps, IReadOnlyList<PlanStep> before, int from, int to)
    {
        // 元の位置 → 新しい位置。
        var moved = new int[before.Count];
        var order = Enumerable.Range(0, before.Count).ToList();
        var carried = order[from];
        order.RemoveAt(from);
        order.Insert(to, carried);
        for (var position = 0; position < order.Count; position++)
        {
            moved[order[position]] = position;
        }

        for (var index = 0; index < steps.Count; index++)
        {
            if (steps[index].ReviewsStep is { } reviewed && reviewed >= 0 && reviewed < moved.Length)
            {
                steps[index] = steps[index] with { ReviewsStep = moved[reviewed] };
            }
        }

        return steps;
    }

    /// <summary>
    /// 並べ替えた結果を、進められる形かどうかで受ける（設計 §62-35）。
    /// </summary>
    /// <remarks>
    /// <b>壊れた並びを書かない。</b> 書いてしまうと、次の周で計画が止まり、
    /// **人間は自分が何をして止めたのか分からない**（§28-1）。断るのはここ。
    /// </remarks>
    private static PlanEditResult Validated(Plan plan, List<PlanStep> steps) =>
        PlanAdvance.OrderProblem(steps) is { } problem
            ? new PlanEditResult.Rejected(problem)
            : new PlanEditResult.Edited(plan with { Steps = steps });
}

/// <summary>工程を並べ替えた結果（設計 §62-35）。</summary>
public abstract record PlanEditResult
{
    /// <param name="Plan">新しい工程の列を持つ計画。<b>まだ書いていない。</b></param>
    public sealed record Edited(Plan Plan) : PlanEditResult;

    public sealed record Rejected(string Reason) : PlanEditResult;
}
