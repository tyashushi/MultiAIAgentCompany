namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// 仕事状態の遷移規則。設計 §6。
/// <c>CodexAutomation</c> の <c>TransitionTable</c> を<b>設計として参照</b>している
/// （コードは別リポジトリなので流用しない）。
/// </summary>
/// <remarks>
/// 取り入れたのは <see cref="TransitionOrigin"/> で権利を分けるという発想。
/// <b>自動化は終端状態から復帰できない。</b> 人間だけが再開できる。
/// </remarks>
public static class TaskTransitions
{
    /// <summary>これ以上、自動化が動かしてはいけない状態。</summary>
    public static bool IsTerminal(TaskStatus status) => status
        is TaskStatus.Accepted
        or TaskStatus.Failed
        or TaskStatus.Cancelled;

    private static readonly Dictionary<TaskStatus, TaskStatus[]> Allowed = new()
    {
        [TaskStatus.Drafted] = [TaskStatus.Dispatched, TaskStatus.Cancelled],
        [TaskStatus.Dispatched] = [TaskStatus.InProgress, TaskStatus.Failed, TaskStatus.Cancelled],
        [TaskStatus.InProgress] = [TaskStatus.AwaitingAnswer, TaskStatus.Reported, TaskStatus.Failed, TaskStatus.Cancelled],
        [TaskStatus.AwaitingAnswer] = [TaskStatus.InProgress, TaskStatus.Cancelled],
        [TaskStatus.Reported] = [TaskStatus.Accepted, TaskStatus.Rejected],
        [TaskStatus.Rejected] = [TaskStatus.Dispatched, TaskStatus.Cancelled],
        [TaskStatus.Accepted] = [],
        [TaskStatus.Failed] = [],
        [TaskStatus.Cancelled] = [],
    };

    /// <summary>
    /// その遷移を、その主体が起こしてよいか。
    /// </summary>
    public static TransitionCheck Check(TaskStatus from, TaskStatus to, TransitionOrigin origin)
    {
        // state.json は人間が手で直せるファイルなので、列挙の外の値が入ってくる。
        // 初版は `origin is Automation` で弾いていたため (TransitionOrigin)123 が素通りし、
        // 報告の自動受理と終端からの復帰が両方通っていた（再レビューで発覚、§14-4）。
        if (!Enum.IsDefined(from) || !Enum.IsDefined(to))
        {
            return new TransitionCheck(false, $"未定義の仕事状態は遷移させない: {(int)from} → {(int)to}");
        }

        if (!Enum.IsDefined(origin))
        {
            return new TransitionCheck(false, $"未定義の遷移主体は遷移させない: {(int)origin}");
        }

        if (from == to)
        {
            return new TransitionCheck(false, "同じ状態への遷移は書き込みにしない");
        }

        if (IsTerminal(from))
        {
            // 終端から動かせるのは人間だけ。「復帰」であることを明示的に記録する。
            if (origin is not TransitionOrigin.Human)
            {
                return new TransitionCheck(false, $"自動化は終端状態 {from} から遷移できない");
            }

            // 復帰先も定義された遷移でなければならない（終端は Allowed が空なので、
            // 人間には仕切り直しの入口だけを許す）。
            return to is TaskStatus.Drafted or TaskStatus.Dispatched
                ? new TransitionCheck(true, $"人間が終端 {from} から {to} へ戻した")
                : new TransitionCheck(false, $"終端 {from} からの復帰先は Drafted か Dispatched だけ");
        }

        if (!Allowed.TryGetValue(from, out var targets) || !targets.Contains(to))
        {
            return new TransitionCheck(false, $"{from} → {to} は許可された遷移ではない");
        }

        // Reported → Accepted / Rejected は人間の判断。自動化に受理させない。
        if (from is TaskStatus.Reported && origin is TransitionOrigin.Automation)
        {
            return new TransitionCheck(false, "報告の受理・差し戻しは人間の判断");
        }

        return new TransitionCheck(true, $"{from} → {to}");
    }
}

/// <param name="Allowed">遷移してよいか。</param>
/// <param name="Reason">人間に見せる理由。</param>
public sealed record TransitionCheck(bool Allowed, string Reason);
