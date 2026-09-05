namespace MultiAIAgentCompany.Core.Workspace.Trust;

/// <summary>trust 設定に記録されたパスとワークスペースを同じ基準で比較する。</summary>
internal static class WorkspacePathNormalizer
{
    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(ResolveFully(Path.GetFullPath(path), depth: 0));

    /// <summary>
    /// パスの<b>全ての要素</b>についてシンボリックリンクを解決する。
    /// </summary>
    /// <remarks>
    /// <c>ResolveLinkTarget</c> は最後の要素しか見ないので、<c>/tmp/repo</c> のように
    /// 親がリンク（macOS の <c>/tmp</c> → <c>/private/tmp</c>）だと効かない。
    /// さらに<b>解決した先にも未解決の祖先が残る</b> ——
    /// <c>/var/...</c> を指すリンクを辿ると <c>/var</c> がそのまま返る ——
    /// ので、解決先に対して**もう一度**解決し直す。
    /// <para>
    /// trust の記録が別の綴りで入っていると、trust 済みなのに <c>false</c> と答えてしまう
    /// （§13-9 規則3）。ここが甘いと「trust しろ」と人間に無用な操作をさせる。
    /// </para>
    /// </remarks>
    private static string ResolveFully(string fullPath, int depth)
    {
        // 循環したリンクで無限に潜らない。深すぎるときは解決を諦めてそのまま比較する。
        if (depth > 32)
        {
            return fullPath;
        }

        var current = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(current))
        {
            return fullPath;
        }

        foreach (var segment in fullPath[current.Length..]
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(current, segment);
            var target = ResolveOneLevel(candidate);
            current = ReferenceEquals(target, candidate) || string.Equals(target, candidate, StringComparison.Ordinal)
                ? candidate
                : ResolveFully(Path.GetFullPath(target), depth + 1);
        }

        return current;
    }

    /// <summary>1階層ぶんのリンクを解決する。リンクでなければ受け取ったパスをそのまま返す。</summary>
    private static string ResolveOneLevel(string path)
    {
        try
        {
            var asDirectory = new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            if (asDirectory is not null)
            {
                return asDirectory.FullName;
            }

            var asFile = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            if (asFile is not null)
            {
                return asFile.FullName;
            }
        }
        catch (IOException)
        {
            // 循環したリンク・壊れたリンク。trust の確認で例外を外へ出さない。
        }
        catch (UnauthorizedAccessException)
        {
        }

        return path;
    }

    public static bool Equals(string left, string right) => string.Equals(
        Normalize(left), Normalize(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
