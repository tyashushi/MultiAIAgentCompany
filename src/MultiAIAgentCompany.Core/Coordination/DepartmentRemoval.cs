namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// その部門を定義から消してよいか（設計 §47）。
/// </summary>
/// <remarks>
/// <b>純関数。</b> ファイルも時計も触らない —— 判定をここに閉じて、テストで固定する
/// （§31 の <see cref="ReportWatch"/>、§37 の <see cref="PlanAdvance"/> と同じ形）。
/// <para>
/// <b>消すのは定義であって、記録ではない。</b> `.company/` の仕事も文書も残る（§16-4）。
/// だが<b>タイルは消える</b>ので、**操作する口が無くなる**ものを先に片付けさせる。
/// </para>
/// </remarks>
public static class DepartmentRemoval
{
    /// <param name="departmentId">消そうとしている部門。</param>
    /// <param name="tasks">ワークスペースの全部の仕事（読めたもの）。</param>
    /// <param name="planReferences">その部門を指している計画の識別子。</param>
    /// <param name="proposalReferences">その部門宛の、まだ仕事になっていない提案。</param>
    /// <param name="holdsWriteLease">その部門が書き込み権を持っているか。</param>
    /// <param name="unreadableTasks">
    /// 読めなかった仕事の数。<b>0 でなければ、どの部門のものか分からない</b>ので消させない（§7）。
    /// </param>
    /// <param name="sessionRunning">
    /// その部門の CLI が動いているか。<b>仕事が無くても動いていることがある</b> ——
    /// CLI は turn が終わっても終了しない（§32-2e）。
    /// </param>
    public static DepartmentRemovalDecision Decide(
        string departmentId,
        IReadOnlyList<TaskState> tasks,
        IReadOnlyList<string> planReferences,
        IReadOnlyList<string> proposalReferences,
        bool holdsWriteLease,
        int unreadableTasks,
        bool sessionRunning)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(departmentId);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(planReferences);
        ArgumentNullException.ThrowIfNull(proposalReferences);

        var blockers = new List<string>();

        // **終わっていない仕事は全部**（`Drafted` も含む）。まだ渡していないだけで、
        // **既に departmentId を持つ具体的な仕事**なので、部門を消すと迷子になる。
        var unfinished = tasks
            .Where(task => string.Equals(task.DepartmentId, departmentId, StringComparison.Ordinal))
            .Where(task => !TaskTransitions.IsTerminal(task.Status))
            .Select(task => task.Slug)
            .ToArray();
        if (unfinished.Length > 0)
        {
            blockers.Add($"終わっていない仕事が {unfinished.Length} 件ある（{string.Join("、", unfinished)}）");
        }

        if (planReferences.Count > 0)
        {
            blockers.Add($"計画が参照している（{string.Join("、", planReferences)}）");
        }

        if (proposalReferences.Count > 0)
        {
            blockers.Add($"仕事になっていない提案が宛てている（{string.Join("、", proposalReferences)}）");
        }

        // **持ったまま消すと、後続の dispatch を塞ぐ。** 権利を外す口が無くなるので、先に返させる。
        if (holdsWriteLease)
        {
            blockers.Add("書き込み権を持っている（先に仕事を終えるか取り消す）");
        }

        // **読めない仕事があるときは、判定そのものが信用できない**（§7）。
        if (unreadableTasks > 0)
        {
            blockers.Add($"読めない仕事が {unreadableTasks} 件ある（どの部門のものか分からない）");
        }

        // **窓が残ると、人間が制御できない CLI がワークスペースを書ける**（レビューの指摘）。
        // これだけは「終了して消す」を選べるので、他の理由と区別する。
        return blockers.Count > 0
            ? new DepartmentRemovalDecision.Blocked(blockers)
            : sessionRunning
                ? new DepartmentRemovalDecision.NeedsSessionStop()
                : new DepartmentRemovalDecision.Allowed();
    }
}

/// <summary>消してよいかの答え（設計 §47）。</summary>
public abstract record DepartmentRemovalDecision
{
    /// <summary>消してよい。</summary>
    public sealed record Allowed : DepartmentRemovalDecision;

    /// <summary>
    /// 動いているターミナルがある。<b>終わらせれば消せる</b> ——
    /// 人間に「終了して消す / やめる」を選ばせる。
    /// </summary>
    public sealed record NeedsSessionStop : DepartmentRemovalDecision;

    /// <summary>
    /// 消せない。<b>理由を全部返す</b> —— 1つ直すたびに次が出るのでは、人間が何度も往復する。
    /// </summary>
    public sealed record Blocked(IReadOnlyList<string> Reasons) : DepartmentRemovalDecision;
}
