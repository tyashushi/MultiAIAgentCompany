namespace MultiAIAgentCompany.Core.Agents;

/// <summary>
/// その CLI が実行できる場所にあるか（設計 §28-1）。
/// </summary>
/// <remarks>
/// <b>押してから失敗するまで分からない、を無くす。</b> 起動に失敗すると
/// <c>Win32Exception</c> の1行が作業ログに出るだけで、
/// 「どの CLI が無いのか」も「どうすればよいか」も分からなかった（§25-2 の 22番）。
/// <para>
/// <b>見つからないことと、起動できないことは違う。</b> ここで見るのは
/// <b>PATH 上に実行できるものがあるか</b>だけ —— 実際に動くかは起動してみるまで分からない。
/// 「見つかった」を「動く」と言い換えない（§7）。
/// </para>
/// </remarks>
public static class AgentExecutable
{
    /// <summary>その CLI の実行ファイル名。<b>アダプタが起動に使う名前と同じ。</b></summary>
    public static string NameOf(AgentKind kind) => kind switch
    {
        AgentKind.ClaudeCode => "claude",
        AgentKind.CodexCli => "codex",
        AgentKind.AntigravityCli => "agy",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// PATH をたどって実行ファイルを探す。無ければ null。
    /// </summary>
    /// <param name="pathVariable">
    /// 探す場所。null なら環境変数の <c>PATH</c>。テストのために外から渡せるようにしてある。
    /// </param>
    /// <param name="includeWellKnown">
    /// よくある置き場所（Homebrew、<c>~/.local/bin</c> など）も見るか。
    /// <b>既定は見る</b> —— GUI 起動では PATH が最小限になるため。
    /// テストで「PATH にだけ在る／無い」を確かめたいときに false にする。
    /// </param>
    public static string? Find(AgentKind kind, string? pathVariable = null, bool includeWellKnown = true)
    {
        return FindNamed(NameOf(kind), pathVariable, includeWellKnown);
    }

    /// <summary>
    /// 起動に使う実体を決める（設計 §28-1）。
    /// </summary>
    /// <remarks>
    /// <b>見つけた場所を、起動にも使う</b>（レビューで発覚）。
    /// 探すだけ探して名前で起動すると、**GUI 起動では OS が PATH から見つけられず失敗する** ——
    /// 「見つかっているのに起動できない」うえ、失敗の案内まで出なくなっていた。
    /// </remarks>
    /// <returns>見つかったフルパス。見つからなければ受け取った名前をそのまま。</returns>
    public static string ResolveCommand(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return FindNamed(fileName, pathVariable: null, includeWellKnown: true) ?? fileName;
    }

    private static string? FindNamed(string name, string? pathVariable, bool includeWellKnown)
    {
        // **PATH が空でも諦めない**（レビューで発覚）。GUI 起動では最小限どころか
        // 空のこともあり、そこで打ち切ると既知の場所を一度も見ない。
        var path = pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var directory in Directories(path, includeWellKnown))
        {
            foreach (var candidate in CandidatesIn(directory, name))
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 見に行けない場所は飛ばす。**探索そのものを失敗にしない。**
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 探す場所。<b>PATH だけでは足りない</b>（設計 §28-1、レビューで発覚）。
    /// </summary>
    /// <remarks>
    /// <b>GUI から起動したアプリの `PATH` は最小限になる</b> —— Finder や Dock 経由だと
    /// シェルの設定を通らないので、`/opt/homebrew/bin` も `~/.local/bin` も入っていない。
    /// そこだけを見て「無い」と言うと、**実在する CLI を一切起動できないアプリ**になる。
    /// </remarks>
    private static IEnumerable<string> Directories(string path, bool includeWellKnown)
    {
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return directory;
        }

        if (!includeWellKnown || OperatingSystem.IsWindows())
        {
            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return "/opt/homebrew/bin";
        yield return "/usr/local/bin";
        yield return Path.Combine(home, ".local", "bin");
        yield return Path.Combine(home, "bin");
    }

    private static IEnumerable<string> CandidatesIn(string directory, string name)
    {
        string combined;
        try
        {
            combined = Path.Combine(directory, name);
        }
        catch (ArgumentException)
        {
            // PATH に不正な文字が入っていることがある。そこだけ飛ばす。
            yield break;
        }

        yield return combined;
        if (OperatingSystem.IsWindows())
        {
            yield return combined + ".exe";
            yield return combined + ".cmd";
        }
    }
}
