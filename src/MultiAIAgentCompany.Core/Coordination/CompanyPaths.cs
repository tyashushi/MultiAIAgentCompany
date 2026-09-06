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

    public string ArchiveRoot => Path.Combine(Root, "archive");

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

    public string Instruction(string slug) => Path.Combine(TaskDirectory(slug), "instruction.md");

    public string Report(string slug) => Path.Combine(TaskDirectory(slug), "report.md");

    public string Question(string slug) => Path.Combine(TaskDirectory(slug), "question.md");

    public string Answer(string slug) => Path.Combine(TaskDirectory(slug), "answer.md");

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
