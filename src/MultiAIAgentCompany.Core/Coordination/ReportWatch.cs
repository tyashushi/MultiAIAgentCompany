namespace MultiAIAgentCompany.Core.Coordination;

/// <param name="Slug">その仕事。</param>
/// <param name="DepartmentId">担当部門。</param>
/// <param name="Since">最後に状態が動いた時刻（= state.UpdatedAt）。</param>
/// <param name="Elapsed">そこからの経過。</param>
public sealed record ReportSilence(string Slug, string DepartmentId, DateTimeOffset Since, TimeSpan Elapsed);

/// <summary>
/// 期限までに報告を観測していないかを計算する（設計 §31）。
/// 部門の進捗についての主張ではないので、TaskStatus や state.json へ書かない。
/// </summary>
public static class ReportWatch
{
    public static ReportSilence? Of(TaskState state, TimeSpan? deadline, DateTimeOffset now)
    {
        if (deadline is null || state.Status is not (TaskStatus.Dispatched or TaskStatus.InProgress))
        {
            return null;
        }

        var elapsed = now - state.UpdatedAt;
        if (elapsed < TimeSpan.Zero || elapsed <= deadline.Value)
        {
            return null;
        }

        return new ReportSilence(state.Slug, state.DepartmentId, state.UpdatedAt, elapsed);
    }
}
