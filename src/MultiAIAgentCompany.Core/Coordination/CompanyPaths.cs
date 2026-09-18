namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// 調整基盤の置き場所。設計 §6 —— レベル4（共有ドキュメント方式）の本体。
/// </summary>
/// <remarks>
/// 部門間の受け渡しは、アプリのメモリではなく<b>ワークスペース内のファイル</b>で行う。
/// アプリが落ちても状態が残るので、GUI は状態の所有者ではなく閲覧者になる。
/// <code>
/// &lt;ワークスペース&gt;/.company/
///   tasks/&lt;task-slug&gt;/
///     instruction.md   指示書
///     report.md        報告書
///     question.md      (b) 判断の相談
///     answer.md        人間の回答
///     state.json       状態、revision、lease、観測記録
///   plans/&lt;plan-id&gt;/plan.json   計画
///   archive/
///   lease.json       書き込み権と Unity 権（ワークスペースに1つ。§14-2）
/// </code>
/// </remarks>
public sealed class CompanyPaths
{
    public CompanyPaths(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        WorkspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    public string WorkspaceRoot { get; }

    public string Root => Path.Combine(WorkspaceRoot, ".company");

    public string TasksRoot => Path.Combine(Root, "tasks");

    public string PlansRoot => Path.Combine(Root, "plans");

    public string ArchiveRoot => Path.Combine(Root, "archive");

    /// <summary>人間が秘書に添付したファイルの複製（設計 §58-2）。<b>片付けない。</b></summary>
    public string AttachmentsRoot => Path.Combine(Root, "attachments");

    /// <summary>
    /// 読めなかった仕事を移す場所（設計 §16-4）。
    /// <b>消さない</b> —— 中身は残し、仕事一覧から外すだけ。
    /// </summary>
    public string UnreadableRoot => Path.Combine(Root, "unreadable");

    /// <summary>秘書の領域（設計 §17-6）。</summary>
    public string SecretaryRoot => Path.Combine(Root, "secretary");

    /// <summary>protocol の正本。<b>会話やコード中の文字列に寄せない</b>（§17-6）。</summary>
    public string SecretaryReadme => Path.Combine(SecretaryRoot, "README.md");

    /// <summary><b>未処理の提案の正本</b>。走査はここだけを見る（§17-6）。</summary>
    public string SecretaryOutbox => Path.Combine(SecretaryRoot, "outbox");

    /// <summary>秘書との会話スレッドの保存場所。</summary>
    public string SecretaryThreads => Path.Combine(SecretaryRoot, "threads");

    public string ThreadDirectory(string slug) => Path.Combine(SecretaryThreads, RequireSlug(slug));

    /// <summary>
    /// 片付けた相談の置き場所（設計 §45）。
    /// </summary>
    /// <remarks>
    /// <b>消さずに移す</b>（§16-4）—— 走査が読めない仕事を <c>unreadable/</c> へ移すのと同じ形。
    /// 会話は正本ではない（§17-3）が、<b>何を相談したかは人間の記録</b>である。
    /// </remarks>
    public string ArchivedThreads => Path.Combine(ArchiveRoot, "threads");

    public string ThreadMeta(string slug) => Path.Combine(ThreadDirectory(slug), "meta.json");

    public string ThreadTranscript(string slug) => Path.Combine(ThreadDirectory(slug), "transcript.jsonl");

    /// <summary>受理した提案の保管場所。<b>消さずに移す</b>（§16-4 / §17-6）。</summary>
    public string SecretaryAccepted => Path.Combine(SecretaryRoot, "processed", "accepted");

    /// <summary>
    /// 却下した提案の保管場所。<b>消すと「提案があったが仕事にしなかった」が失われる</b>。
    /// </summary>
    public string SecretaryRejected => Path.Combine(SecretaryRoot, "processed", "rejected");

    /// <summary>
    /// 権利の置き場所。<b>ワークスペース全体で1ファイル</b>（設計 §14-2）。
    /// タスクごとに持たせると、別タスクに別部門の有効な lease を同時に置けてしまう。
    /// </summary>
    public string Lease => Path.Combine(Root, "lease.json");

    /// <summary>ワークスペースごとの部門定義。</summary>
    public string Departments => Path.Combine(Root, "departments.json");

    public string TaskDirectory(string slug) => Path.Combine(TasksRoot, RequireSlug(slug));

    public string PlanDirectory(string id) => Path.Combine(PlansRoot, RequireSlug(id));

    public string PlanFile(string id) => Path.Combine(PlanDirectory(id), "plan.json");

    /// <summary>計画の共有文書（設計 §62-4）。<b>アプリだけが書く</b>。</summary>
    public string Brief(string id) => Path.Combine(PlanDirectory(id), "brief.md");

    public string Instruction(string slug) => Path.Combine(TaskDirectory(slug), "instruction.md");

    public string Report(string slug) => Path.Combine(TaskDirectory(slug), "report.md");

    public string Question(string slug) => Path.Combine(TaskDirectory(slug), "question.md");

    public string Answer(string slug) => Path.Combine(TaskDirectory(slug), "answer.md");

    /// <summary>
    /// 差し戻しの理由（設計 §19-2）。<b><c>Note</c> に入れない</b> ——
    /// あれは遷移のたびに上書きされ、部門へ届く経路でもない。
    /// </summary>
    public string Rejection(string slug) => Path.Combine(TaskDirectory(slug), "rejection.md");

    /// <summary>
    /// 次の試行の指示の staging（設計 §19-1）。
    /// <b>封じ込めの対象に入れない</b> —— 入れると、新しい指示が過去試行へ移ってしまう。
    /// </summary>
    public string NextInstruction(string slug) => Path.Combine(TaskDirectory(slug), "next-instruction.md");

    public string State(string slug) => Path.Combine(TaskDirectory(slug), "state.json");

    /// <summary>過去の試行を封じる場所（設計 §14-1）。</summary>
    public string AttemptDirectory(string slug, int attemptId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attemptId);

        return Path.Combine(TaskDirectory(slug), "attempts", attemptId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// slug としてパス片に使える文字列か。<b>検査の住所はここ1つ</b>。
    /// 緩めると、調整基盤がワークスペースの外へ書く経路になる。
    /// </summary>
    public static bool IsValidSlug(string? slug) =>
        !string.IsNullOrWhiteSpace(slug)
        && slug is not ("." or "..")
        && slug.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string RequireSlug(string slug)
    {
        if (!IsValidSlug(slug))
        {
            throw new ArgumentException($"task slug に使えない文字が含まれている: '{slug}'", nameof(slug));
        }

        return slug;
    }
}
