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

        // **止めていても、終わったものは終わっている**（設計 §62-29、実機で踏んだ）。
        // 「止める」は<b>次を渡さない</b>ことなので（§32-8）、渡すものが残っていなければ
        // 止めているものも無い。ここを飛ばすと、全工程を受理し終えた計画が
        // **「止めている」のまま帯に残り**、出口が「計画を続ける」しか無くなる。
        // <b>読むだけで、何も渡さない</b>ので、止めた直後に走っても次は渡らない。
        if (AllAccepted(plan, states))
        {
            return new PlanNext.Done();
        }

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

        // **波（同時に走る組）ごとに見る**（設計 §62-33）。
        // 波の中はどの工程から渡してもよく、**波が全部片付くまで次の波へ行かない**。
        // 波が1工程だけなら、これまでと同じ一直線になる。
        for (var start = 0; start < plan.Steps.Count; start = WaveEnd(plan.Steps, start))
        {
            var end = WaveEnd(plan.Steps, start);
            PlanNext? stop = null, act = null, hold = null;

            for (var index = start; index < end; index++)
            {
                // **波を最後まで見てから決める。** 途中で返すと、
                // 止まる条件を抱えた工程を飛ばして兄弟を渡してしまう。
                switch (AtStep(plan, index, states, revisionLimit, ref unaccepted))
                {
                    case null:
                        continue;
                    case PlanNext.NeedsHuman human:
                        stop ??= human;
                        break;
                    case PlanNext.Wait wait:
                        hold ??= wait;
                        break;
                    case { } action:
                        act ??= action;
                        break;
                }
            }

            // **止まる条件が勝つ**（§37-6）。次に「こちらの番」、最後に「待つ」。
            // 待っている兄弟が居ても、渡せる工程があるなら渡す —— それが並行。
            if (stop is not null) return stop;
            if (act is not null) return act;
            if (hold is not null) return hold;
        }

        return unaccepted is { } pending
            ? new PlanNext.NeedsHuman("レビューが通っていない工程が残っている", pending)
            : new PlanNext.Done();
    }

    /// <summary>
    /// 全工程が受理済みか（設計 §62-29）。
    /// </summary>
    /// <remarks>
    /// <b>「状態を読めない」は受理ではない。</b> 読めない工程が1つでもあれば false にして、
    /// 通常の判断（人間を呼ぶ）に落とす —— ここで甘く見ると、**読めない工程を
    /// 終わったことにして計画を閉じる**（§7）。
    /// </remarks>
    private static bool AllAccepted(Plan plan, IReadOnlyDictionary<string, TaskState> states) =>
        plan.Steps.Count > 0
        && plan.Steps.All(step =>
            step.TaskSlug is { Length: > 0 } slug
            && states.TryGetValue(slug, out var state)
            && state.Status is TaskStatus.Accepted);


    /// <summary>
    /// <paramref name="start"/> から始まる波の終わり（その次の工程の位置。設計 §62-33）。
    /// </summary>
    /// <remarks>
    /// 波は<b>連なった <see cref="PlanStep.RunsWithPrevious"/></b> で決まる。
    /// 先頭の工程の印は見ない —— 前が無いので、波の始まりでしかない。
    /// </remarks>
    public static int WaveEnd(IReadOnlyList<PlanStep> steps, int start)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var end = start + 1;
        while (end < steps.Count && steps[end].RunsWithPrevious)
        {
            end++;
        }

        return end;
    }

    /// <summary>
    /// 1工程について、いま取るべき行動（設計 §62-33）。
    /// </summary>
    /// <returns>
    /// <b>null は「この工程は先へ進んでよい」</b> —— 受理済みか、レビュー待ちのまま次へ行くもの。
    /// </returns>
    private static PlanNext? AtStep(
        Plan plan, int index,
        IReadOnlyDictionary<string, TaskState> states, int revisionLimit, ref int? unaccepted)
    {
        var step = plan.Steps[index];

        // まだ渡していない工程。
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
                return null;

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
                return null;

            default:
                return new PlanNext.NeedsHuman($"{slug} の状態が分からない", index);
        }
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

            // **見る相手と同時に走らせない**（設計 §62-33）。同じ波に入れると、
            // レビューは**まだ出来ていないもの**を見に行く。
            if (WaveEnd(steps, reviewed) > index)
            {
                return $"工程 {index + 1}（{steps[index].DepartmentId}）は工程 {reviewed + 1}"
                    + $"（{steps[reviewed].DepartmentId}）を見るのに、同時に走る組に入っている。"
                    + "レビューは見る相手が終わってから";
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

    /// <summary>
    /// いまレビュー（監査を含む）に見てもらっている工程の仕事 → 見ている部門（設計 §62-17、人間の決定）。
    /// </summary>
    /// <remarks>
    /// <b>見ている間は、人間に受理・差し戻しをさせない。</b> 先に受理すると、レビューが「直しが要る」と
    /// 言っても受理済みの工程は差し戻せず、計画が止まる（実機で発覚）。判定が出れば計画が進める。
    /// <para>
    /// <b>レビューがまだ渡っていないときは入れない。</b> 途中の工程が <c>partial</c> を報告して止まったときなど、
    /// 人間が決めるほか無い場面がある。人間が止めた計画も入れない。
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, string> UnderReview(
        Plan plan, IReadOnlyDictionary<string, TaskState> states)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(states);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (plan.StoppedByHuman)
        {
            return result;
        }

        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var review = plan.Steps[index];
            if (review.ReviewsStep is not { } target || target < 0 || target >= plan.Steps.Count
                || plan.Steps[target].TaskSlug is not { Length: > 0 } targetSlug
                || review.TaskSlug is not { Length: > 0 } reviewSlug
                || !states.TryGetValue(reviewSlug, out var state))
            {
                continue;
            }

            var looking = state.Status is TaskStatus.Drafted or TaskStatus.Dispatched
                    or TaskStatus.InProgress or TaskStatus.AwaitingAnswer
                || (state.Status is TaskStatus.Reported && review.Verdict is null);
            if (looking)
            {
                result.TryAdd(targetSlug, review.DepartmentId);
            }
        }

        return result;
    }

    /// <summary>
    /// 実装の工程より前に設計の工程が無ければ、その理由（設計 §62-18、人間の決定）。
    /// </summary>
    /// <remarks>
    /// 実装部門の担当業務は「承認された設計を実装する」—— 設計の無い計画では前提が崩れる。
    /// <b>計画を受け取るときだけ見る</b>（すでに動いている計画は止めない）。1件の仕事の提案には効かない。
    /// </remarks>
    public static string? DesignProblem(IReadOnlyList<PlanStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var implementation = steps.ToList().FindIndex(step =>
            string.Equals(step.DepartmentId, Workspace.DepartmentStore.ImplementationDepartmentId, StringComparison.Ordinal));
        if (implementation < 0
            || steps.Take(implementation).Any(step =>
                string.Equals(step.DepartmentId, Workspace.DepartmentStore.DesignDepartmentId, StringComparison.Ordinal)))
        {
            return null;
        }

        return $"工程 {implementation + 1}（{steps[implementation].DepartmentId}）より前に設計（{Workspace.DepartmentStore.DesignDepartmentId}）の工程が無い。"
            + "実装の前には必ず設計を置く";
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
