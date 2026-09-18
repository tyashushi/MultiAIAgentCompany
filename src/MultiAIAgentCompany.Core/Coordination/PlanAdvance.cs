namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// 計画について、<b>いま1つだけ取るべき行動</b>（設計 §37）。
/// </summary>
/// <remarks>
/// <b>1回に1つしか返さない。</b> まとめて返すと、途中で失敗したときに
/// 「どこまでやったか」が呼び出し側に散る。呼び出し側は1つ実行して、また訊く。
/// </remarks>
public abstract record PlanNext
{
    /// <summary>その工程を部門へ渡す。</summary>
    public sealed record Dispatch(int Index, PlanStep Step) : PlanNext;

    /// <summary>その工程はまだ動いている。<b>何もしない。</b></summary>
    public sealed record Wait(int Index, PlanStep Step) : PlanNext;

    /// <summary>
    /// 仕事はあるが、まだ渡っていない（<c>Drafted</c>）。<b>渡す</b>（設計 §37-6b）。
    /// </summary>
    /// <remarks>
    /// <b>作り直さない。</b> 既にある仕事を渡す —— 作り直すと、
    /// **渡らなかった仕事が毎周1つずつ増える。**
    /// <para>
    /// <b>これは §14-1 が禁じた自動再送ではない。</b> あちらは
    /// <c>Dispatched</c>（送ったかもしれない）の話で、<c>Drafted</c> は
    /// **状態を書く前に止まった＝送っていない**ことが確定している。
    /// </para>
    /// </remarks>
    public sealed record Deliver(int Index, PlanStep Step, string Slug) : PlanNext;

    /// <summary>
    /// その工程の報告を受理する（<see cref="TransitionOrigin.Plan"/> で）。
    /// </summary>
    public sealed record AcceptStep(int Index, PlanStep Step) : PlanNext;

    /// <summary>
    /// レビューが直しを求めた。<b>見てもらった工程へ戻す</b>（設計 §37-5）。
    /// </summary>
    public sealed record SendBack(int ReviewIndex, int TargetIndex, PlanStep Target) : PlanNext;

    /// <summary>
    /// 止めて人間を呼ぶ（設計 §37-6）。<b>飛ばして次へ進めない。</b>
    /// </summary>
    public sealed record NeedsHuman(string Reason, int? Index = null) : PlanNext;

    /// <summary>全部の工程が終わった。</summary>
    public sealed record Done : PlanNext;
}

/// <summary>
/// 計画をどこまで進めてよいかを計算する（設計 §37）。
/// </summary>
/// <remarks>
/// <b>純関数。</b> ファイルも時計も触らない —— §31 の <see cref="ReportWatch"/> と同じ形で、
/// **判定をここに閉じて、テストで固定する。**
/// <para>
/// <b>止まる条件を全部ここに書く</b>（§37-6）。呼び出し側に散らすと、
/// 「どれか1つを忘れたときだけ勝手に進む」という壊れ方をする。
/// </para>
/// </remarks>
public static class PlanAdvance
{
    /// <param name="plan">いまの計画。</param>
    /// <param name="states">
    /// 工程が生んだ仕事の状態（slug → 状態）。<b>正本は <c>state.json</c></b>（§7）。
    /// </param>
    /// <param name="revisionLimit">差し戻しの上限（設計 §37-5）。</param>
    public static PlanNext Decide(
        Plan plan,
        IReadOnlyDictionary<string, TaskState> states,
        int revisionLimit = Plan.DefaultRevisionLimit)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(states);

        // **人間が止めたなら、それが最優先。** 他の条件を先に見ると、
        // 止めた直後の1周で次を渡してしまう。
        if (plan.StoppedByHuman)
        {
            return new PlanNext.NeedsHuman("人間が止めた");
        }

        if (plan.Steps.Count is 0)
        {
            return new PlanNext.NeedsHuman("工程が1つも無い");
        }

        // **この規則より前に作られた計画も、進める前に確かめる**（設計 §62-13）。
        if (OrderProblem(plan.Steps) is { } problem)
        {
            return new PlanNext.NeedsHuman(problem);
        }

        int? unaccepted = null;

        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];

            // まだ渡していない工程。**ここまでの工程は全部終わっている**（下で continue した）。
            if (step.TaskSlug is not { Length: > 0 } slug)
            {
                return new PlanNext.Dispatch(index, step);
            }

            if (!states.TryGetValue(slug, out var state))
            {
                // **無いものを「たぶん終わった」にしない**（§7）。
                return new PlanNext.NeedsHuman($"{slug} の状態を読めない", index);
            }

            switch (state.Status)
            {
                case TaskStatus.Accepted:
                    continue;


                // **渡っていない。こちらの番である**（設計 §37-6b、レビュー2周目で発覚）。
                // ここを「動いている」に混ぜると、渡せなかった計画が**永久に待ち続け**、
                // 画面は「進めている」と出しながら何も起きない。
                case TaskStatus.Drafted:
                    return new PlanNext.Deliver(index, step, slug);

                case TaskStatus.Dispatched:
                case TaskStatus.InProgress:
                    return new PlanNext.Wait(index, step);

                // **人間の番**（§31-2 と同じ扱い）。部門は止まっていない。
                case TaskStatus.AwaitingAnswer:
                    return new PlanNext.NeedsHuman($"{slug} が質問している", index);

                case TaskStatus.Failed:
                    return new PlanNext.NeedsHuman($"{slug} が失敗した", index);

                case TaskStatus.Cancelled:
                    return new PlanNext.NeedsHuman($"{slug} は取り消された", index);

                // 差し戻したまま止まっている。**計画は送り直しまでを1つの操作でやる**ので、
                // ここに居るのは送り直しが通らなかったときだけ。
                case TaskStatus.Rejected:
                    return new PlanNext.NeedsHuman($"{slug} が差し戻されたまま止まっている", index);

                case TaskStatus.Reported:
                    // **null は「この工程はもう動かないが、まだ受理しない」** ——
                    // レビュー待ちの工程がこれに当たる。次の工程へ進む。
                    if (AtReport(plan, index, step, slug, states, revisionLimit) is { } action)
                    {
                        return action;
                    }

                    // **飛ばしたことを覚えておく**（レビュー2周目で発覚）——
                    // 覚えないと、**受理していない工程を残したまま「終わった」**になる。
                    unaccepted ??= index;
                    continue;

                default:
                    return new PlanNext.NeedsHuman($"{slug} の状態が分からない", index);
            }
        }

        return unaccepted is { } pending
            ? new PlanNext.NeedsHuman("レビューが通っていない工程が残っている", pending)
            : new PlanNext.Done();
    }

    /// <returns>
    /// 取るべき行動。<b>null は「まだ受理しないまま、次の工程へ進む」</b> ——
    /// レビューを頼んである工程は、**レビューが通るまで受理しない**（設計 §37-5）。
    /// <para>
    /// <b>ここを受理してしまうと終端になり、駄目出しが来ても戻せない。</b>
    /// §19 の差し戻しは <c>Reported</c> からしか始まらない。
    /// </para>
    /// </returns>
    private static PlanNext? AtReport(
        Plan plan, int index, PlanStep step, string slug,
        IReadOnlyDictionary<string, TaskState> states, int revisionLimit)
    {
        if (step.IsReview)
        {
            // **判定を推し量らない**（§37-5）。書いていなければ人間を呼ぶ。
            switch (step.Verdict)
            {
                case null:
                case ReviewVerdict.Unknown:
                    return new PlanNext.NeedsHuman($"{slug} の判定を読めない", index);

                case ReviewVerdict.NeedsRevision:
                    if (plan.Revisions >= revisionLimit)
                    {
                        return new PlanNext.NeedsHuman(
                            $"差し戻しが上限（{revisionLimit}回）に達した", index);
                    }

                    var target = step.ReviewsStep!.Value;
                    if (target < 0 || target >= plan.Steps.Count || target == index)
                    {
                        return new PlanNext.NeedsHuman($"戻り先の工程が計画に無い", index);
                    }

                    return new PlanNext.SendBack(index, target, plan.Steps[target]);

                case ReviewVerdict.Ok:
                    break;
            }
        }

        // **レビューを頼んである工程は、その判定が出るまで受理しない。**
        var reviewers = ReviewersOf(plan, index);
        if (!step.IsReview && reviewers.Count > 0)
        {
            var passed = 0;
            foreach (var reviewer in reviewers)
            {
                var review = plan.Steps[reviewer];
                var state = review.TaskSlug is { Length: > 0 } reviewSlug
                    && states.TryGetValue(reviewSlug, out var found) ? found : null;

                if (review.Verdict is ReviewVerdict.Ok
                    && state?.Status is TaskStatus.Reported or TaskStatus.Accepted)
                {
                    passed++;
                    continue;
                }

                // **終わっているのに通っていないレビューを、素通りさせない**
                // （レビュー2周目で発覚）。人間が判定の無い報告を受理した場合など ——
                // ここを抜けると、**レビューを通らないまま実装まで走る。**
                if (state is not null && TaskTransitions.IsTerminal(state.Status))
                {
                    return new PlanNext.NeedsHuman(
                        $"{review.DepartmentId} のレビューが通らないまま終わっている", reviewer);
                }
            }

            // **全員が通ってはじめて受理する**（レビュー2周目で発覚）。
            // 1人でも残っていると、**あとから来た駄目出しを反映できない**
            // —— 受理は終端なので、§19 の差し戻しに入れない。
            return passed == reviewers.Count ? new PlanNext.AcceptStep(index, step) : null;
        }

        // **最後の工程だけは人間が受理する**（設計 §37-3）。
        // ここを自動にすると、**誰も成果物を読まないまま計画が終わる。**
        return index == plan.Steps.Count - 1
            ? new PlanNext.NeedsHuman("最後の報告を受理する", index)
            : new PlanNext.AcceptStep(index, step);
    }

    /// <summary>
    /// その工程を見るレビュー工程。<b>複数ありうる</b>（§29 の設計レビュー2部門）。
    /// </summary>
    /// <summary>
    /// 工程の順が規則に合わなければ、その理由（設計 §62-13、人間の決定）。
    /// </summary>
    /// <remarks>
    /// <b>レビューは、見る相手の工程のすぐ後に置く。</b> 間に置いてよいのは、同じ工程を見る別のレビューだけ。
    /// 間に別の工程があると、差し戻しで見る相手を直しても、間の工程は直す前の成果物を使って
    /// 受理済みのまま残り、先へ進む（Codex のレビューで発覚）。
    /// </remarks>
    public static string? OrderProblem(IReadOnlyList<PlanStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        for (var index = 0; index < steps.Count; index++)
        {
            // 範囲の外（自分自身・後ろ・負）は「戻り先が無い」として別に止める。ここでは見ない。
            if (steps[index].ReviewsStep is not { } reviewed || reviewed < 0 || reviewed >= index)
            {
                continue;
            }

            for (var between = reviewed + 1; between < index; between++)
            {
                if (steps[between].ReviewsStep != reviewed)
                {
                    return $"工程 {index + 1}（{steps[index].DepartmentId}）は工程 {reviewed + 1}（{steps[reviewed].DepartmentId}）を見るが、"
                        + $"間に工程 {between + 1}（{steps[between].DepartmentId}）がある。レビューは見る相手のすぐ後に置く";
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<int> ReviewersOf(Plan plan, int index)
    {
        var reviewers = new List<int>();
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            if (i != index && plan.Steps[i].ReviewsStep == index)
            {
                reviewers.Add(i);
            }
        }

        return reviewers;
    }
}
