using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Workspace;

namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// 計画を1つ進めた結果（設計 §37）。
/// </summary>
public abstract record PlanTick
{
    /// <summary>いまは何もしない（工程が動いている）。</summary>
    public sealed record Idle(string Reason) : PlanTick;

    /// <summary>1つ進めた。</summary>
    public sealed record Acted(Plan Plan, string Note) : PlanTick;

    /// <summary>止まった。<b>飛ばして次へ進めない</b>（§37-6）。</summary>
    public sealed record Stopped(string Reason) : PlanTick;

    /// <summary>全部の工程が終わった。</summary>
    public sealed record Done : PlanTick;
}

/// <summary>
/// 計画を進めるのに要る、アプリ側の手（設計 §37-1）。
/// </summary>
/// <remarks>
/// <b>部門の定義とセッションと窓は Core からは見えない。</b> 渡してもらう ——
/// ここで自前に持つと、<see cref="TaskDispatcher"/> と同じものを2つ持つことになる。
/// </remarks>
/// <param name="DepartmentOf">部門 ID から定義を引く。無ければ null。</param>
/// <param name="SessionOf">構造化部門のセッション。外部ターミナルなら null。</param>
/// <param name="DeliverAsync">
/// <see cref="DispatchResult.LaunchTerminal"/> を実際の窓にする（§32）。
/// <b>これを解決して返すこと</b> —— <c>LaunchTerminal</c> のまま返ってきたら、
/// 窓が開いていないので「渡した」とは扱わない。構造化部門ではそのまま返してよい。
/// </param>
public sealed record PlanHands(
    Func<string, DepartmentDefinition?> DepartmentOf,
    Func<string, IStructuredSession?> SessionOf,
    Func<DispatchResult, string, CancellationToken, Task<DispatchResult>> DeliverAsync);

/// <summary>
/// 秘書が立てた計画を、<b>1回に1つだけ</b>進める（設計 §37）。
/// </summary>
/// <remarks>
/// <b>何をするかは決めない。</b> それは <see cref="PlanAdvance.Decide"/> ——
/// ここは<b>決まったことを実行する</b>だけで、止まる条件もあちらが持っている（§37-6）。
/// <para>
/// <b>1回に1つ。</b> まとめてやると、途中で失敗したときに
/// 「どこまでやったか」が計画とディスクでずれる。呼び出し側は走査のたびに1回呼ぶ。
/// </para>
/// </remarks>
public sealed class PlanRunner(
    CompanyPaths paths, PlanStore plans, TaskStore tasks, TaskDispatcher dispatcher, TimeProvider clock)
{
    /// <summary>書き込み権を借りる長さ。<c>MainWindow</c> の dispatch と揃える。</summary>
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(30);

    public async Task<PlanTick> StepAsync(Plan plan, PlanHands hands, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(hands);

        var states = await ReadStatesAsync(plan, ct);

        // **判定を観測してから決める**（設計 §37-5b、レビューで発覚）。
        // ここが無いと、`ReviewVerdicts` も `PlanStep.Verdict` も**誰も書かない** ——
        // レビュー工程のある計画は、報告が出るたびに必ず人間を呼んで止まる。
        // **機械はあるのに、そこへ入る道が無い**（§30 と同じ形）。
        if (await ObserveVerdictsAsync(plan, states, ct) is { } observed)
        {
            plan = observed;
        }

        return PlanAdvance.Decide(plan, states) switch
        {
            PlanNext.Done => new PlanTick.Done(),
            PlanNext.Wait wait => new PlanTick.Idle($"{wait.Step.DepartmentId} が動いている"),
            PlanNext.NeedsHuman human => new PlanTick.Stopped(human.Reason),
            PlanNext.Dispatch dispatch => await DispatchAsync(plan, dispatch, states, hands, ct),
            PlanNext.Deliver deliver => await DeliverAsync(plan, deliver, states, hands, ct),
            PlanNext.AcceptStep accept => await AcceptAsync(plan, accept, states, hands, ct),
            PlanNext.SendBack back => await SendBackAsync(plan, back, states, hands, ct),
            _ => new PlanTick.Stopped("計画の次の手が分からない"),
        };
    }

    /// <summary>
    /// レビュー工程の報告から判定を読み、計画に書き留める（設計 §37-5b）。
    /// </summary>
    /// <remarks>
    /// <b>観測であって判断ではない。</b> 読めなければ <see cref="ReviewVerdict.Unknown"/> を書き、
    /// **何をするかは <see cref="PlanAdvance.Decide"/> が決める**（そこで人間を呼ぶ）。
    /// <para>
    /// <b>一度書いたら読み直さない。</b> 同じ報告を毎周読み直すと、
    /// 差し戻しのあと（判定を消してある）と区別がつかない。
    /// </para>
    /// </remarks>
    /// <returns>書き留めたら新しい計画。何も変わらなければ null。</returns>
    private async Task<Plan?> ObserveVerdictsAsync(
        Plan plan, IReadOnlyDictionary<string, TaskState> states, CancellationToken ct)
    {
        var steps = plan.Steps.ToArray();
        var changed = false;

        for (var index = 0; index < steps.Length; index++)
        {
            var step = steps[index];
            if (!step.IsReview || step.Verdict is not null
                || step.TaskSlug is not { Length: > 0 } slug
                || !states.TryGetValue(slug, out var state)
                || state.Status is not TaskStatus.Reported)
            {
                continue;
            }

            steps[index] = step with { Verdict = ReviewVerdicts.Parse(await ReadTextAsync(paths.Report(slug), ct)) };
            changed = true;
        }

        if (!changed)
        {
            return null;
        }

        // **書けなかったら、判定を無かったことにする**（次の周でまた読む）——
        // 書けていないのに進むと、計画とディスクがずれる。
        return await plans.WriteAsync(plan, plan with { Steps = steps }, ct) is PlanWriteResult.Written written
            ? written.Plan
            : null;
    }

    private async Task<IReadOnlyDictionary<string, TaskState>> ReadStatesAsync(Plan plan, CancellationToken ct)
    {
        var states = new Dictionary<string, TaskState>(StringComparer.Ordinal);
        foreach (var step in plan.Steps)
        {
            if (step.TaskSlug is not { Length: > 0 } slug || states.ContainsKey(slug))
            {
                continue;
            }

            // **読めなかったものは入れない。** `PlanAdvance` が「状態を読めない」で止める ——
            // ここで既定値を入れると、**読めていないのに進む。**
            if (await tasks.ReadAsync(slug, ct) is TaskReadResult.Found found)
            {
                states[slug] = found.State;
            }
        }

        return states;
    }

    private async Task<PlanTick> DispatchAsync(
        Plan plan, PlanNext.Dispatch next, IReadOnlyDictionary<string, TaskState> states,
        PlanHands hands, CancellationToken ct)
    {
        if (hands.DepartmentOf(next.Step.DepartmentId) is not { } department)
        {
            return new PlanTick.Stopped($"{next.Step.DepartmentId} という部門がこのフォルダに無い");
        }

        // **レビューを渡す前に、見てもらう工程の書き込み権を返す**（設計 §37-4b）。
        // 読むだけの部門は書き手が居る間は待つ（§29-1）。ところがレビュー対象の工程は
        // **レビューが通るまで受理しない**（§37-5）ので、受理で返していると
        // **互いに待って永久に進まない。** 返す根拠は受理ではなく「報告が出た」——
        // §37-4 で弱めたとおりである。
        if (next.Step.ReviewsStep is { } reviewed && reviewed < plan.Steps.Count
            && hands.DepartmentOf(plan.Steps[reviewed].DepartmentId) is { } reviewedDepartment)
        {
            await dispatcher.ReleaseWriteLeaseIfIdleAsync(reviewedDepartment, ct);
        }

        var slug = NewSlug();
        if (await tasks.CreateAsync(slug, department.Id, ct) is not TaskWriteResult.Written created)
        {
            return new PlanTick.Stopped($"{slug} を作れなかった");
        }

        await File.WriteAllTextAsync(
            paths.Instruction(slug),
            CompanyInstruction.Compose(await ComposeStepTextAsync(plan, next.Index, states, ct), paths, slug, department),
            ct);

        // **渡す前に、計画へ書く**（§14-1 と同じ順序）。あとにすると、
        // ここで落ちたときに**渡した仕事が計画から迷子になる。**
        var recorded = await plans.WriteAsync(plan, WithStep(plan, next.Index, step => step with { TaskSlug = slug }), ct);
        if (recorded is not PlanWriteResult.Written written)
        {
            return new PlanTick.Stopped($"計画を書けなかった（{Reason(recorded)}）");
        }

        var result = await hands.DeliverAsync(
            await dispatcher.DispatchAsync(
                created.State, department, hands.SessionOf(department.Id), LeaseDuration, ct),
            department.Id,
            ct);

        // **渡らなかったことを「渡した」にしない**（§7）。計画に slug は残っているので、
        // 人間が直したあと、次の周で `Dispatched` として拾える。
        return result is DispatchResult.Dispatched or DispatchResult.QueuedForNextTurn or DispatchResult.SentUncertain
            ? new PlanTick.Acted(written.Plan, $"{department.DisplayName} に {slug} を渡した（工程 {next.Index + 1}）")
            : new PlanTick.Stopped($"{department.DisplayName} に渡せなかった: {Describe(result)}");
    }

    /// <summary>
    /// 作ってあるが渡っていない仕事を渡す（設計 §37-6b）。
    /// </summary>
    /// <remarks>
    /// <b>作り直さない。</b> `Drafted` は「状態を書く前に止まった」＝**送っていない**ので、
    /// 同じ仕事をそのまま渡してよい（§14-1 が禁じた自動再送は `Dispatched` の話）。
    /// </remarks>
    private async Task<PlanTick> DeliverAsync(
        Plan plan, PlanNext.Deliver next, IReadOnlyDictionary<string, TaskState> states,
        PlanHands hands, CancellationToken ct)
    {
        if (hands.DepartmentOf(next.Step.DepartmentId) is not { } department)
        {
            return new PlanTick.Stopped($"{next.Step.DepartmentId} という部門がこのフォルダに無い");
        }

        if (!states.TryGetValue(next.Slug, out var state))
        {
            return new PlanTick.Stopped($"{next.Slug} の状態を読めない");
        }

        var result = await hands.DeliverAsync(
            await dispatcher.DispatchAsync(state, department, hands.SessionOf(department.Id), LeaseDuration, ct),
            department.Id,
            ct);

        return result is DispatchResult.Dispatched or DispatchResult.QueuedForNextTurn or DispatchResult.SentUncertain
            ? new PlanTick.Acted(plan, $"{department.DisplayName} に {next.Slug} を渡した（工程 {next.Index + 1}）")
            : new PlanTick.Stopped($"{department.DisplayName} に渡せなかった: {Describe(result)}");
    }

    private async Task<PlanTick> AcceptAsync(
        Plan plan, PlanNext.AcceptStep next, IReadOnlyDictionary<string, TaskState> states,
        PlanHands hands, CancellationToken ct)
    {
        if (next.Step.TaskSlug is not { } slug || !states.TryGetValue(slug, out var state))
        {
            return new PlanTick.Stopped($"工程 {next.Index + 1} の状態を読めない");
        }

        var write = await tasks.TransitionAsync(
            state, TaskStatus.Accepted, TransitionOrigin.Plan, "計画が受理した", ct);
        if (write is not TaskWriteResult.Written)
        {
            return new PlanTick.Stopped($"{slug} を受理できなかった（{Reason(write)}）");
        }

        // **書き込み権を返す**（設計 §37-4）。**ここは人間の確認より1段弱い** ——
        // `report.md` が出ていても、その窓の CLI はまだ生きていて書ける。
        // §14-2 の「lease は保証ではなく約束」の範囲だが、弱いことは分かっている。
        if (hands.DepartmentOf(next.Step.DepartmentId) is { } department)
        {
            await dispatcher.ReleaseWriteLeaseIfIdleAsync(department, ct);
        }

        return new PlanTick.Acted(plan, $"{slug} を受理した（工程 {next.Index + 1}）");
    }

    private async Task<PlanTick> SendBackAsync(
        Plan plan, PlanNext.SendBack next, IReadOnlyDictionary<string, TaskState> states,
        PlanHands hands, CancellationToken ct)
    {
        var review = plan.Steps[next.ReviewIndex];
        if (review.TaskSlug is not { } reviewSlug || !states.TryGetValue(reviewSlug, out var reviewState))
        {
            return new PlanTick.Stopped($"レビュー工程 {next.ReviewIndex + 1} の状態を読めない");
        }

        if (next.Target.TaskSlug is not { } targetSlug || !states.TryGetValue(targetSlug, out var targetState))
        {
            return new PlanTick.Stopped($"戻す先の工程 {next.TargetIndex + 1} の状態を読めない");
        }

        if (hands.DepartmentOf(next.Target.DepartmentId) is not { } department)
        {
            return new PlanTick.Stopped($"{next.Target.DepartmentId} という部門がこのフォルダに無い");
        }

        // **差し戻しの理由は、レビューの報告そのもの**（§37-7）——
        // 計画が要約すると、**直す側が読むのは要約だけ**になる。
        var reason = await ReadTextAsync(paths.Report(reviewSlug), ct)
            ?? "レビューが直しを求めたが、報告を読めなかった";

        var rejected = await tasks.TransitionAsync(
            targetState, TaskStatus.Rejected, TransitionOrigin.Plan, "レビューが直しを求めた", ct);
        if (rejected is not TaskWriteResult.Written written)
        {
            return new PlanTick.Stopped($"{targetSlug} を差し戻せなかった（{Reason(rejected)}）");
        }

        // ここから落ちても、仕事は `Rejected` として残る（§19-1 の不変条件）。
        await File.WriteAllTextAsync(paths.Rejection(targetSlug), reason, ct);
        await File.WriteAllTextAsync(
            paths.NextInstruction(targetSlug),
            CompanyInstruction.Compose(
                $"""
                前の試行はレビューを通らなかった。同じ仕事をやり直すこと。

                {CompanyInstruction.Material("レビューの指摘", $"工程 {next.ReviewIndex + 1}・部門 {review.DepartmentId}・仕事 {reviewSlug}・試行 {reviewState.AttemptId}", reason)}
                """,
                paths, targetSlug, department),
            ct);

        // **レビューの仕事はここで終わり。** 見つけるべきものを見つけたので受理する。
        // **やり直したら、レビューも新しい仕事として渡し直す**（同じ報告を2度読ませない）。
        await tasks.TransitionAsync(reviewState, TaskStatus.Accepted, TransitionOrigin.Plan, "指摘を受け取った", ct);

        // **同じ工程を見る全員を消す**（レビュー2周目で発覚）。1人ぶんだけ消すと、
        // **前の試行に対する「ok」が次の試行の判定として残る** ——
        // やり直した成果物を、誰も見ていないのに通ったことになる。
        var next2 = plan with
        {
            Revisions = plan.Revisions + 1,
            Steps = plan.Steps
                .Select(step => step.ReviewsStep == next.TargetIndex
                    ? step with { TaskSlug = null, Verdict = null }
                    : step)
                .ToArray(),
        };

        var recorded = await plans.WriteAsync(plan, next2, ct);
        if (recorded is not PlanWriteResult.Written planWritten)
        {
            return new PlanTick.Stopped($"計画を書けなかった（{Reason(recorded)}）");
        }

        var result = await hands.DeliverAsync(
            await dispatcher.RedispatchAsync(
                written.State, department, hands.SessionOf(department.Id), LeaseDuration, ct,
                TransitionOrigin.Plan),
            department.Id,
            ct);

        return result is DispatchResult.Dispatched or DispatchResult.QueuedForNextTurn or DispatchResult.SentUncertain
            ? new PlanTick.Acted(planWritten.Plan,
                $"レビューが直しを求めたので {targetSlug} を送り直した（{planWritten.Plan.Revisions} 回目）")
            : new PlanTick.Stopped($"{department.DisplayName} に送り直せなかった: {Describe(result)}");
    }

    /// <summary>
    /// その工程へ渡す本文（設計 §37-7）。
    /// </summary>
    /// <remarks>
    /// <b>前の工程の報告は、そのまま渡す。</b> 要約すると、
    /// **要約した側（アプリ）が仕様の作者になる。** 足すのは秘書が書いた一言だけ。
    /// </remarks>
    private async Task<string> ComposeStepTextAsync(
        Plan plan, int index, IReadOnlyDictionary<string, TaskState> states, CancellationToken ct)
    {
        var step = plan.Steps[index];
        var parts = new List<string>
        {
            $"""
            この仕事は、計画「{plan.Goal}」の {index + 1} 番目の工程です。

            {step.Handover}
            """,
        };

        // レビュー工程は**見る相手の報告**を、ふつうの工程は**すぐ前の工程の報告**を読む。
        //
        // **直前がレビューなら、見てもらった工程の報告も渡す**（レビューで発覚）。
        // レビューの報告は「判定と指摘」であって**成果物ではない** ——
        // これが無いと、実装は「ok」とコメントだけを受け取って、**設計そのものを受け取らない。**
        var sources = new List<int>();
        if (step.ReviewsStep is { } reviewed)
        {
            sources.Add(reviewed);
        }
        else if (index - 1 >= 0)
        {
            if (plan.Steps[index - 1].ReviewsStep is { } artifact)
            {
                sources.Add(artifact);
            }

            sources.Add(index - 1);
        }

        foreach (var source in sources)
        {
            if (source >= 0 && source < plan.Steps.Count
                && plan.Steps[source].TaskSlug is { } previousSlug
                && await ReadTextAsync(paths.Report(previousSlug), ct) is { } report)
            {
                // 設計 §62-8。出典は分かるものだけを書く。本文を指示として混ぜない。
                var origin = $"工程 {source + 1}・部門 {plan.Steps[source].DepartmentId}・仕事 {previousSlug}";
                if (states.TryGetValue(previousSlug, out var state))
                {
                    origin += $"・試行 {state.AttemptId}";
                }

                parts.Add(CompanyInstruction.Material($"{plan.Steps[source].DepartmentId} の報告", origin, report));
            }
        }

        if (step.IsReview)
        {
            // **判定行を頼むのはここ**（§37-5）。部門共通の protocol には置かない ——
            // これが要るのは計画のレビュー工程だけで、全部門に配ると
            // 「自分に関係のある約束か」を読み手が判断することになる（§32-5）。
            // 文面の正本は `ReviewVerdicts` —— **読む側と同じ場所に置く**（§37-5）。
            parts.Add(ReviewVerdicts.RequestText);
        }

        return string.Join("\n\n", parts);
    }

    private static Plan WithStep(Plan plan, int index, Func<PlanStep, PlanStep> change) =>
        plan with
        {
            Steps = plan.Steps.Select((step, i) => i == index ? change(step) : step).ToArray(),
        };

    private string NewSlug() =>
        $"task-{clock.GetUtcNow().ToLocalTime():yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";

    private static async Task<string?> ReadTextAsync(string path, CancellationToken ct)
    {
        try
        {
            return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Reason(TaskWriteResult result) => result switch
    {
        TaskWriteResult.Conflicted conflicted => conflicted.Reason,
        TaskWriteResult.Rejected rejected => rejected.Reason,
        _ => result.GetType().Name,
    };

    private static string Reason(PlanWriteResult result) => result switch
    {
        PlanWriteResult.Conflicted conflicted => conflicted.Reason,
        PlanWriteResult.Rejected rejected => rejected.Reason,
        _ => result.GetType().Name,
    };

    private static string Describe(DispatchResult result) => result switch
    {
        DispatchResult.Blocked blocked => blocked.Reason,
        DispatchResult.BlockedByExpiredLease blocked => blocked.Reason,
        DispatchResult.Rejected rejected => rejected.Reason,
        DispatchResult.Conflicted conflicted => conflicted.Reason,
        _ => result.GetType().Name,
    };
}
