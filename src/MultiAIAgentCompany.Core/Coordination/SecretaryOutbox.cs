namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// 秘書が publish した提案1件（設計 §17-6）。<b>まだ仕事ではない。</b>
/// </summary>
/// <param name="Id">ファイル名から取った識別子。</param>
/// <param name="DepartmentId">
/// 宛先の部門。<b>知らない部門でも捨てない</b> —— 人間に見せて判断させる。
/// </param>
/// <param name="Body">指示の本文。</param>
public sealed record SecretaryProposal(string Id, string? DepartmentId, string Body);

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
                proposals.Add(Parse(name, File.ReadAllText(file)));
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
