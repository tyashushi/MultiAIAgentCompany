using MultiAIAgentCompany.Core.Coordination;

namespace MultiAIAgentCompany.Core.Agents;

/// <param name="AutoApprove">人間に聞かずに通してよいか。</param>
/// <param name="Reason">人間に見せる1行。<b>通したときも言う</b>（黙って通さない）。</param>
public sealed record ApprovalVerdict(bool AutoApprove, string Reason);

/// <summary>
/// 秘書の (a) ランタイム承認を、どこまで自動で通すか（設計 §35）。
/// </summary>
/// <remarks>
/// <b>「全部自動」にしない。</b> §30-4 の危険モードを廃止した（§32-3）のと同じ理由で、
/// **聞けるのに聞かない**を作らない。ここで通すのは
/// <b>秘書が自分の持ち場でやること</b>だけである。
/// <para>
/// <b>通したことは必ず言う</b>（§7）—— 黙って強い権限で動いているものを作らない。
/// </para>
/// <para>
/// <b>部門には掛けない。</b> 部門は外部ターミナルで動き、承認は人間がその窓で押す（§32）。
/// ここは<b>秘書だけ</b>の話である。
/// </para>
/// </remarks>
public static class SecretaryApprovalPolicy
{
    /// <summary>読むだけのツール。<b>作業ツリーの中なら通す。</b></summary>
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.Ordinal)
    {
        "Read", "Glob", "Grep", "LS", "NotebookRead",
    };

    /// <summary>書くツール。<b>秘書の持ち場の中だけ通す。</b></summary>
    private static readonly HashSet<string> WriteTools = new(StringComparer.Ordinal)
    {
        "Write", "Edit", "NotebookEdit",
    };

    public static ApprovalVerdict Decide(ApprovalRequest request, CompanyPaths paths)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(paths);

        // **分からないものは聞く。** ツール名を返してこない CLI や、
        // 名前が変わった版では、判定材料が無い（§7）。
        if (request.ToolName is not { Length: > 0 } tool)
        {
            return new ApprovalVerdict(false, "何のツールか分からないので聞く");
        }

        // **Bash は「命令の形」で縛って通す**（設計 §38、人間が広げた）。
        // 場所ではなく命令が対象なので、`TargetPath` では判定できない。
        if (tool is "Bash")
        {
            return SecretaryBashPolicy.Decide(request.CommandLine, paths.WorkspaceRoot);
        }

        if (!ReadOnlyTools.Contains(tool) && !WriteTools.Contains(tool))
        {
            return new ApprovalVerdict(false, $"{tool} は自動で通さない");
        }

        if (request.TargetPath is not { Length: > 0 } target)
        {
            return new ApprovalVerdict(false, $"{tool} の対象が分からないので聞く");
        }

        var full = Full(target);
        if (full is null)
        {
            return new ApprovalVerdict(false, $"{tool} の対象を解決できないので聞く");
        }

        if (ReadOnlyTools.Contains(tool))
        {
            return IsInside(full, Full(paths.WorkspaceRoot))
                ? new ApprovalVerdict(true, $"{tool}（作業フォルダの中を読む）")
                : new ApprovalVerdict(false, $"{tool} が作業フォルダの外を読もうとしている");
        }

        // **書き込みは `.company/` の中まで**（設計 §38、人間が広げた）。
        // 秘書が指示書や報告に手を入れられるようになる ——
        // §17-6 の「秘書は tasks/ を触らない」という**約束の方を、人間が改めた。**
        //
        // **ただし `state.json` と `lease.json` は除く。** あれは約束ではなく**不変条件**で、
        // §14-1（state.json を書くのはアプリだけ）と §14-2（権利の正本）が
        // **revision の楽観ロックごと**そこに乗っている。
        // 秘書が書けると、**アプリの知らない書き換え**として検出される側に回る（§23-1）。
        if (!IsInside(full, Full(paths.Root)))
        {
            return new ApprovalVerdict(false, $"{tool} が .company/ の外に書こうとしている");
        }

        // **大文字小文字を区別せずに比べる**（レビューで発覚、2026-09-12）。
        // macOS と Windows の既定は区別しないので、`STATE.JSON` は
        // **同じファイルを指しながら、綴りの照合だけを素通りする。**
        var fileName = Path.GetFileName(full);
        return string.Equals(fileName, "state.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "lease.json", StringComparison.OrdinalIgnoreCase)
            ? new ApprovalVerdict(false, $"{fileName} を書くのはアプリだけ（§14-1 / §14-2）")
            : new ApprovalVerdict(true, $"{tool}（.company/ の中に書く）");
    }

    /// <summary>
    /// <b>綴りではなく実体で比べる</b>（§26-1 と同じ姿勢）。
    /// <c>..</c> や symlink で外へ出られないようにする。
    /// </summary>
    private static string? Full(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsInside(string full, string? root)
    {
        if (root is null)
        {
            return false;
        }

        // **区切りまで含めて比べる。** 含めないと `/ws` が `/ws-other` に当たる。
        return full.Equals(root, PathComparison)
            || full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>
    /// <b>Windows では大文字小文字を区別しない</b>（2026-09-14）。CLI は <c>c:\</c> と <c>C:\</c> を
    /// 混ぜて渡してくるので、区別すると作業フォルダの中を読むたびに人間に聞くことになる。
    /// </summary>
    internal static StringComparison PathComparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
