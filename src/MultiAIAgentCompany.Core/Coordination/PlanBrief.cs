namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// 計画の共有文書 <c>brief.md</c>（設計 §62-4）。
/// </summary>
/// <remarks>
/// <b>工程を跨ぐ前提と決定を1か所に置く。</b> 報告が渡るのは「すぐ前」と「見る相手」だけなので、
/// それより前に決まったことは後の工程に届かない。
/// <para>
/// <b>書くのはアプリだけ。</b> 部門は報告の <see cref="AdditionHeading"/> 節に書き、
/// アプリが受理のときに<b>要約せずそのまま</b>転記する（§37-7 と同じ理由 ——
/// 要約した側が仕様の作者になる）。部門が直接書くと、受理されていない試行の案が決定に紛れる。
/// </para>
/// </remarks>
public static class PlanBrief
{
    /// <summary>部門が報告に書く節の見出し。</summary>
    public const string AdditionHeading = "## 共有文書への追記";

    /// <summary>転記を受ける見出し。</summary>
    public const string DecisionsHeading = "## 決定事項と追記（工程から）";

    /// <summary>
    /// 計画を作るときの最初の版を書く。<b>既にあれば書かない</b>（上書きで追記を消さない）。
    /// </summary>
    public static async Task WriteInitialAsync(
        CompanyPaths paths, string planId, string goal, IReadOnlyList<PlanStep> steps, string? secretaryBrief,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(steps);

        var path = paths.Brief(planId);
        if (File.Exists(path))
        {
            return;
        }

        var stepLines = steps.Select((step, index) =>
            $"{index + 1}. `{step.DepartmentId}`"
            + (step.ReviewsStep is { } reviewed ? $"（工程 {reviewed + 1} を見る）" : "")
            + $" —— {step.Handover}");

        var premise = string.IsNullOrWhiteSpace(secretaryBrief)
            ? "（秘書は前提を書いていない）"
            : secretaryBrief.Trim();

        var content = $"""
            # 計画の共有文書

            **この文書は直接書き換えないこと。** 足すべきことは、報告の `{AdditionHeading}` 節に書く。
            工程が受理されると、アプリがその節をそのままここへ足す。

            ## 目的

            {goal}

            ## 工程

            {string.Join("\n", stepLines)}

            ## 前提（秘書が計画のときに書いたもの）

            {premise}

            {DecisionsHeading}

            """;

        await WriteAtomicallyAsync(path, content, ct);
    }

    /// <summary>工程の指示書に入れる1節。<b>文書が無い古い計画では null</b>。</summary>
    public static string? ReadingInstruction(CompanyPaths paths, string planId)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var path = paths.Brief(planId);

        // **本文は埋めない。** 長くなるのと、読む時点の最新を読ませるため。
        return File.Exists(path)
            ? $"""
              ## 計画の共有文書

              最初に `{path}` を読むこと。計画全体の目的・前提・これまでの工程で決まったことが書いてある。
              **このファイルは直接書き換えない。** 後の工程が知るべき決定事項（とその理由）・用語・制約があれば、
              報告に `{AdditionHeading}` の節を作って書くこと。受理されると、アプリがそのまま足す。
              """
            : null;
    }

    /// <summary>報告の <see cref="AdditionHeading"/> 節の本文。無い・空なら null。</summary>
    /// <remarks>節の終わりは、次の <c>## </c> 見出しか末尾。</remarks>
    public static string? ExtractAddition(string? report)
    {
        if (string.IsNullOrWhiteSpace(report))
        {
            return null;
        }

        var lines = report.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Array.FindIndex(lines, line => line.TrimEnd() == AdditionHeading);
        if (start < 0)
        {
            return null;
        }

        var end = Array.FindIndex(lines, start + 1, line => line.StartsWith("## ", StringComparison.Ordinal));
        var body = string.Join("\n", lines[(start + 1)..(end < 0 ? lines.Length : end)]).Trim('\n');
        return string.IsNullOrWhiteSpace(body) ? null : body;
    }

    /// <summary>転記の出典見出し。<b>同じ受理を2度足さないための鍵でもある</b>。</summary>
    public static string SourceHeading(int stepIndex, string departmentId, string slug, int attemptId) =>
        $"### 工程 {stepIndex + 1}・{departmentId}・{slug}・試行 {attemptId}";

    /// <summary>
    /// 受理した工程の追記を足す。足したら true。
    /// </summary>
    /// <remarks>
    /// 文書が無い（古い計画）・追記が無い・同じ出典が既にあるときは何もしない ——
    /// アプリが落ちて同じ受理をやり直しても重複しない。
    /// </remarks>
    public static async Task<bool> AppendAsync(
        CompanyPaths paths, string planId, string sourceHeading, string? addition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var path = paths.Brief(planId);
        if (string.IsNullOrWhiteSpace(addition) || !File.Exists(path))
        {
            return false;
        }

        var current = await File.ReadAllTextAsync(path, ct);
        if (current.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Any(line => line.TrimEnd() == sourceHeading))
        {
            return false;
        }

        var separator = current.EndsWith("\n\n", StringComparison.Ordinal) ? ""
            : current.EndsWith('\n') ? "\n"
            : "\n\n";
        await WriteAtomicallyAsync(path, $"{current}{separator}{sourceHeading}\n\n{addition.Trim('\n')}\n", ct);
        return true;
    }

    private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, ct);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
