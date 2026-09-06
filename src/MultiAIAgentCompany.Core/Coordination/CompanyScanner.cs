namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// <c>.company/</c> を走査して、publish 済みの調整文書に対応する仕事状態を進める。
/// </summary>
/// <remarks>
/// <b>state.json を書くのは <see cref="TaskStore"/> だけ</b>（設計 §14-1）。
/// この型は文書を読んで遷移を選び、書き込みは必ず <see cref="TaskStore.TransitionAsync"/>
/// に委ねる。
/// </remarks>
/// <summary>
/// 走査の種類。<b>復旧は再起動時の契約</b>であって、定期走査の話ではない（§14-1）。
/// </summary>
public enum CompanyScanKind
{
    /// <summary>
    /// 起動時・ワークスペース選択時。<b>ここでだけ復旧の一覧を作る。</b>
    /// </summary>
    Startup,

    /// <summary>
    /// 動いている間の定期走査。<b>復旧の一覧を作らない</b> ——
    /// いま飛んでいる `Dispatched` を毎回「送ったかもしれない」に混ぜると、
    /// 人間が「送信を確認する」を読まなくなる。
    /// </summary>
    Periodic,
}

public sealed class CompanyScanner
{
    private readonly CompanyPaths _paths;
    private readonly TaskStore _tasks;

    public CompanyScanner(CompanyPaths paths, TaskStore tasks)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
    }

    /// <summary>
    /// <c>.company/</c> を1周見て、文書の出現に応じて仕事状態を進める。
    /// <b>書くのは TaskStore だけ</b>（設計 §14-1）。
    /// </summary>
    public async Task<CompanyScanResult> SyncAsync(CompanyScanKind kind, CancellationToken ct)
    {
        var recovery = await _tasks.ScanForRecoveryAsync(ct);
        var applied = new List<AppliedTransition>();
        var blocked = new List<BlockedTransition>();
        var unreadable = recovery.Unreadable.ToList();
        var unreadableSlugs = unreadable.Select(task => task.Slug).ToHashSet(StringComparer.Ordinal);

        foreach (var slug in await _tasks.ListSlugsAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (unreadableSlugs.Contains(slug))
            {
                continue;
            }

            try
            {
                var read = await _tasks.ReadAsync(slug, ct);
                if (read is TaskReadResult.Unreadable broken)
                {
                    unreadable.Add(new UnreadableTask(slug, broken.Reason));
                    unreadableSlugs.Add(slug);
                    continue;
                }

                if (read is not TaskReadResult.Found found)
                {
                    continue;
                }

                var transition = FindTransition(found.State);
                if (transition is null)
                {
                    continue;
                }

                var check = TaskTransitions.Check(found.State.Status, transition.Value.To, TransitionOrigin.Automation);
                if (!check.Allowed)
                {
                    blocked.Add(new BlockedTransition(slug, found.State.Status, transition.Value.To, check.Reason));
                    continue;
                }

                var write = await _tasks.TransitionAsync(
                    found.State, transition.Value.To, TransitionOrigin.Automation, null, ct);
                switch (write)
                {
                    case TaskWriteResult.Written:
                        applied.Add(new AppliedTransition(slug, found.State.Status, transition.Value.To, transition.Value.Because));
                        break;
                    case TaskWriteResult.Conflicted conflicted:
                        blocked.Add(new BlockedTransition(slug, found.State.Status, transition.Value.To, conflicted.Reason));
                        break;
                    case TaskWriteResult.Rejected rejected:
                        blocked.Add(new BlockedTransition(slug, found.State.Status, transition.Value.To, rejected.Reason));
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // 1つの仕事が読めなくても、残りの仕事の走査は続ける。読めない state.json
                // は部門に紐づけず、人間が直せる未解決項目として返す（設計 §16-3）。
                unreadable.Add(new UnreadableTask(slug, $"state.json を読めません: {exception.Message}"));
                unreadableSlugs.Add(slug);
            }
        }

        // Dispatched は「送ったかもしれない」。ここから自動再送はしない（設計 §14-1）。
        // この走査で Reported / AwaitingAnswer へ進んだものは、もはや Dispatched のままではない。
        // §14-1 の「送ったかもしれない」は**再起動を跨いだとき**の話。
        // 定期走査で毎回返すと、2秒前に投げたばかりの仕事まで「送信を確認する」になり、
        // 人間がその表示を読まなくなる。
        var transitionedSlugs = applied.Select(transition => transition.Slug).ToHashSet(StringComparer.Ordinal);
        var dispatched = kind is CompanyScanKind.Startup
            ? recovery.Dispatched.Where(task => !transitionedSlugs.Contains(task.Slug)).ToArray()
            : [];

        return new CompanyScanResult(applied, blocked, unreadable, dispatched);
    }

    private (TaskStatus To, string Because)? FindTransition(TaskState state)
    {
        // publish 契約（設計 §16-1）により最終名だけを見る。*.tmp.* はここに該当せず、
        // 書きかけを完成済みと推定する経路はない。
        var reportPublished = File.Exists(_paths.Report(state.Slug));
        var questionPublished = File.Exists(_paths.Question(state.Slug));
        var answerPublished = File.Exists(_paths.Answer(state.Slug));

        // report.md と question.md が共存したら報告を優先する。報告は仕事の終わりである。
        if (state.Status is TaskStatus.Dispatched or TaskStatus.InProgress)
        {
            if (reportPublished)
            {
                return (TaskStatus.Reported, "report.md が publish された");
            }

            if (questionPublished && !answerPublished)
            {
                return (TaskStatus.AwaitingAnswer, "question.md が publish された");
            }
        }

        if (state.Status is TaskStatus.AwaitingAnswer && answerPublished)
        {
            return (TaskStatus.InProgress, "answer.md が置かれた");
        }

        // **終端の仕事は候補にしない。**
        // Reported → Accepted のあとも report.md は残るので、ここで候補にすると
        // 以後の走査すべてで「Accepted → Reported が Blocked」が積み上がる。
        // 本当に遅れて出た報告と、既に読まれた報告を、走査からは区別できない ——
        // 区別できないものを人間の要対応にすると、その一覧が読まれなくなる。

        // Dispatched → InProgress はここに意図的に存在しない。部門が着手した事実は
        // 文書から観測できず、活動状態（Working）とも混同しない（設計 §7）。
        return null;
    }
}

/// <param name="Applied">実際に書かれた遷移。</param>
/// <param name="Blocked">遷移すべきだが書けなかったもの（理由つき）。</param>
/// <param name="Unreadable">state.json を読めなかった仕事。部門に紐づけられない（設計 §16-3）。</param>
/// <param name="Dispatched">送ったかもしれないまま残っている仕事（設計 §14-1）。自動再送しない。</param>
public sealed record CompanyScanResult(
    IReadOnlyList<AppliedTransition> Applied,
    IReadOnlyList<BlockedTransition> Blocked,
    IReadOnlyList<UnreadableTask> Unreadable,
    IReadOnlyList<TaskState> Dispatched);

public sealed record AppliedTransition(string Slug, TaskStatus From, TaskStatus To, string Because);
public sealed record BlockedTransition(string Slug, TaskStatus From, TaskStatus To, string Reason);
