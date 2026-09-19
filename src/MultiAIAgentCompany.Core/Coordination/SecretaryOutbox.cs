namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// 秘書が publish した提案1件（設計 §17-6）。<b>まだ仕事ではない。</b>
/// </summary>
/// <param name="Id">ファイル名から取った識別子。</param>
/// <param name="DepartmentId">
/// 宛先の部門。<b>知らない部門でも捨てない</b> —— 人間に見せて判断させる。
/// </param>
/// <param name="Body">指示の本文。</param>
/// <param name="PlanProblem">
/// 計画として書かれていたが、工程の順が規則に合わず受け取らなかった理由（設計 §62-13）。
/// </param>
public sealed record SecretaryProposal(string Id, string? DepartmentId, string Body, string? PlanProblem = null);

/// <summary>秘書が publish した計画。仕事1件の提案と混ぜず、工程の列として渡す（§37）。</summary>
/// <param name="Brief">
/// 秘書が <c>brief:</c> の下に書いた前提（設計 §62-4）。書いていなければ null。
/// </param>
public sealed record SecretaryPlanProposal(
    string Id, string Goal, IReadOnlyList<PlanStep> Steps, string? Brief = null);

/// <summary>
/// 秘書の outbox を読む。<b>ここが未処理の提案の正本</b>（設計 §17-6）。
/// </summary>
public sealed class SecretaryOutbox(CompanyPaths paths)
{
    /// <summary>
    /// 未処理の提案を読む。<b>publish 途中のものは見ない</b>（§16-1 の publish 契約）。
    /// </summary>
    public IReadOnlyList<SecretaryProposal> Read()
    {
        if (!Directory.Exists(paths.SecretaryOutbox))
        {
            return [];
        }

        var proposals = new List<SecretaryProposal>();
        foreach (var file in Directory.EnumerateFiles(paths.SecretaryOutbox, "*.md").OrderBy(f => f, StringComparer.Ordinal))
        {
            // publish 途中（*.tmp.*）を完成品として読まない。
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Contains(".tmp.", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var content = File.ReadAllText(file);
                if (ParsePlan(name, content) is null)
                {
                    // 解決できない計画は、宛先不明の提案として本文を人間に残す（§34-1）。
                    // **工程の順が規則に合わないだけなら、その理由を添える**（設計 §62-13）。
                    // 「宛先が無い」と出すと、秘書に何を直してもらえばよいか分からない。
                    var proposal = Parse(name, content);
                    proposals.Add(ParsePlanCore(name, content) is { } misordered
                        && ShapeProblem(misordered.Steps) is { } problem
                            ? proposal with { PlanProblem = problem }
                            : proposal);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 読めなかったことを、無かったことにしない。
                proposals.Add(new SecretaryProposal(name, null, "（この提案を読めなかった）"));
            }
        }

        return proposals;
    }

    /// <summary>
    /// 工程を解決できた計画を読む。解決できなかったものは <see cref="Read"/> で人間へ返す。
    /// </summary>
    public IReadOnlyList<SecretaryPlanProposal> ReadPlans()
    {
        if (!Directory.Exists(paths.SecretaryOutbox))
        {
            return [];
        }

        var plans = new List<SecretaryPlanProposal>();
        foreach (var file in Directory.EnumerateFiles(paths.SecretaryOutbox, "*.md").OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Contains(".tmp.", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (ParsePlan(name, File.ReadAllText(file)) is { } plan)
                {
                    plans.Add(plan);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 読めなかったものは Read() が人間へ返す。計画として自動には渡さない。
            }
        }

        return plans;
    }

    /// <remarks>
    /// <b>レビューは、見る相手の工程のすぐ後に置く</b>（設計 §62-13、人間の決定）。
    /// 外れた計画は受け取らない —— 間の工程が、あとで直される前の成果物を使って先へ進むから。
    /// </remarks>
    internal static SecretaryPlanProposal? ParsePlan(string id, string content) =>
        ParsePlanCore(id, content) is { } plan && ShapeProblem(plan.Steps) is null ? plan : null;

    /// <summary>工程の並びが規則に合わない理由（設計 §62-13 / §62-18）。合っていれば null。</summary>
    private static string? ShapeProblem(IReadOnlyList<PlanStep> steps) =>
        PlanAdvance.OrderProblem(steps) ?? PlanAdvance.DesignProblem(steps);

    private static SecretaryPlanProposal? ParsePlanCore(string id, string content)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        if (!lines[0].StartsWith("plan:", StringComparison.Ordinal))
        {
            return null;
        }

        var goal = lines[0]["plan:".Length..].Trim();
        if (goal.Length is 0)
        {
            return null;
        }

        var steps = new List<PlanStep>();
        string? brief = null;
        for (var number = 1; number < lines.Length; number++)
        {
            var raw = lines[number];

            // **空行で計画を落とさない。** 秘書は人間が読む文書を書くので、
            // 見出しや箇条書きの間に空行が入る。ここで厳しくすると、
            // **正しい計画が「宛先不明の提案」に化けて自動にならない。**
            var line = raw.Trim();
            if (line.Length is 0)
            {
                continue;
            }

            // **`brief:` から後ろは前提の文章**（設計 §62-4）。秘書が人間に向けて書く文書なので、
            // 行の形を問わない —— `step:` と書いてあっても工程として読まない。
            if (line.StartsWith("brief:", StringComparison.Ordinal))
            {
                var first = line["brief:".Length..].Trim();
                var rest = string.Join("\n", lines.Skip(number + 1)).Trim('\n');
                brief = string.Join("\n\n", new[] { first, rest }.Where(part => part.Trim().Length > 0));
                break;
            }

            if (!line.StartsWith("step:", StringComparison.Ordinal))
            {
                // **知らない行は半端に読まない。** 計画として受け取らず、人間へ返す。
                return null;
            }

            var step = line["step:".Length..];
            var separator = step.IndexOf('/');
            if (separator < 0)
            {
                return null;
            }

            var destination = step[..separator].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var handover = step[(separator + 1)..].Trim();
            if (destination.Length is < 1 or > 2 || handover.Length is 0)
            {
                return null;
            }

            int? reviewsStep = null;
            if (destination.Length is 2)
            {
                if (!destination[1].StartsWith("reviews=", StringComparison.Ordinal))
                {
                    return null;
                }

                var reviewedDepartment = destination[1]["reviews=".Length..];
                var index = steps.FindLastIndex(s => string.Equals(s.DepartmentId, reviewedDepartment, StringComparison.Ordinal));
                if (index < 0)
                {
                    // 戻り先を推測しない。自分自身や後続工程も、まだ列に無いので解決されない。
                    return null;
                }

                reviewsStep = index;
            }

            steps.Add(new PlanStep(destination[0], handover, reviewsStep));
        }

        // **工程が1つも無い計画は計画ではない。** 空のまま受け取ると、
        // `PlanAdvance` が「工程が1つも無い」で止めることになり、**理由が1段遠くなる。**
        return steps.Count is 0
            ? null
            : new SecretaryPlanProposal(id, goal, steps, string.IsNullOrWhiteSpace(brief) ? null : brief);
    }

    /// <summary>
    /// 先頭行の <c>department: &lt;id&gt;</c> と本文に分ける（設計 §17-6）。
    /// </summary>
    /// <remarks>
    /// 宛先が無い・読めない場合も<b>本文は残す</b> —— 人間が読んで判断できる。
    /// </remarks>
    internal static SecretaryProposal Parse(string id, string content)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length > 0 && lines[0].StartsWith("department:", StringComparison.Ordinal))
        {
            var department = lines[0]["department:".Length..].Trim();
            var body = string.Join("\n", lines.Skip(1)).Trim();
            return new SecretaryProposal(id, department.Length > 0 ? department : null, body);
        }

        return new SecretaryProposal(id, null, content.Trim());
    }

    /// <summary>受理した提案を移す。<b>消さない</b>（§16-4 / §17-6）。</summary>
    public void Accept(string id, string taskSlug, DateTimeOffset now) =>
        Move(id, paths.SecretaryAccepted, $"{id}-{now.ToUniversalTime():yyyyMMdd-HHmmss}-{taskSlug}.md");

    /// <summary>
    /// 却下した提案を移す。<b>消さない</b> —— 消すと
    /// 「提案があったが人間が仕事にしなかった」という事実が失われる（§17-6）。
    /// </summary>
    public void Reject(string id, DateTimeOffset now) =>
        Move(id, paths.SecretaryRejected, $"{id}-{now.ToUniversalTime():yyyyMMdd-HHmmss}.md");

    private void Move(string id, string destinationDirectory, string fileName)
    {
        var source = Path.Combine(paths.SecretaryOutbox, $"{id}.md");
        if (!File.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(destinationDirectory);
        File.Move(source, Path.Combine(destinationDirectory, fileName), overwrite: true);
    }
}
