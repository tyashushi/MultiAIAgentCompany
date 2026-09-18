using System.Diagnostics;
using MultiAIAgentCompany.Core.Coordination;

namespace MultiAIAgentCompany.Core.Workspace;

/// <summary>
/// <c>.company/</c> が git に入らないようにするか（設計 §53）。
/// </summary>
/// <remarks>
/// <b>会話の記録が、利用者のリポジトリにうっかり commit される</b>のを防ぐ。
/// <c>.company/</c> には秘書との会話・指示書・報告書が入る —— `git add .` で入り、
/// 公開リポジトリならそのまま世に出る。
/// <para>
/// <b>黙って書き換えない。</b> <c>.gitignore</c> は利用者の持ち物なので、足すかどうかは人間に聞く
/// （§30-4 で「アプリが人間の持ち物を勝手に触る」を却下したのと同じ）。
/// </para>
/// </remarks>
public static class CompanyGitIgnore
{
    /// <summary>足す行。<b>何のための行か</b>を1行目に書く —— 後から見た人が消してよいか判断できるように。</summary>
    public const string Entry = ".company/";

    private const string Comment = "# MultiAIAgentCompany の作業記録（秘書との会話・指示書・報告書）";

    /// <summary>「このフォルダでは聞かない」と人間が決めた印（<c>.company/</c> の中）。</summary>
    public static string DeclinedMarker(CompanyPaths paths) => Path.Combine(paths.Root, "gitignore-declined");

    /// <summary>
    /// 聞くべきかを調べる。<b>git が分からないときは聞かない</b>（分からないことで人間を煩わせない）。
    /// </summary>
    public static async Task<CompanyGitIgnoreCheck> CheckAsync(WorkspaceRef workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (File.Exists(DeclinedMarker(workspace.Company)))
        {
            return new CompanyGitIgnoreCheck.Declined();
        }

        // **終了コードで判定する**（2026-09-13 に実機で確かめた）: 0 = 無視されている、
        // 1 = 無視されていない、128 = git の管理下ではない。**`.company/` がまだ無くても判定できる。**
        var (exitCode, _) = await RunGitAsync(workspace.Root, ["check-ignore", "-q", Entry], ct).ConfigureAwait(false);
        switch (exitCode)
        {
            case 0:
                return new CompanyGitIgnoreCheck.AlreadyIgnored();
            case 1:
                break;
            default:
                return new CompanyGitIgnoreCheck.NotApplicable();
        }

        // **既に commit されているものは、`.gitignore` に足しても外れない。** そのことも人間に言う。
        var (listCode, tracked) = await RunGitAsync(workspace.Root, ["ls-files", "--", Entry], ct).ConfigureAwait(false);
        return new CompanyGitIgnoreCheck.ShouldAsk(AlreadyTracked: listCode == 0 && tracked.Trim().Length > 0);
    }

    /// <summary>
    /// 既存の <c>.gitignore</c> の末尾に、<c>.company/</c> の行を足した結果を返す。
    /// </summary>
    /// <remarks>
    /// <b>「もう書いてあるか」を文字で判定しない</b>（Codex の指摘）。行頭に空白のある `  .company/` は git に効かず、
    /// 後ろの `!.company/` は前の行を打ち消す —— **書いてあるのに効いていない**ことがある。
    /// 足すかどうかは git に聞いた結果（<see cref="CheckAsync"/>）で決めてあるので、ここでは**必ず末尾に足す**。
    /// git は後ろの行を優先するので、前にある打ち消しにも勝つ。
    /// </remarks>
    public static string AppendEntry(string? existing)
    {
        var text = existing ?? string.Empty;

        // **前の行にくっつけない。** 改行で終わっていないファイルに足すと、最後の行と1行になる。
        var separator = text.Length == 0 ? string.Empty : text.EndsWith('\n') ? "\n" : "\n\n";
        return $"{text}{separator}{Comment}\n{Entry}\n";
    }

    /// <summary>
    /// ワークスペース直下の <c>.gitignore</c> に足し、<b>効いたかを git に聞き直す</b>。
    /// </summary>
    /// <returns>足したあと、git が <c>.company/</c> を無視するようになったか。</returns>
    /// <remarks>
    /// <b>「足した」と「効いた」を分ける</b>（Codex の指摘）。`.company/` の中に別の `.gitignore` があるなど、
    /// 足しても効かない形はまだあり得る —— そのとき「足した」とだけ言うと、人間は守られていると思い込む。
    /// </remarks>
    public static async Task<bool> AddAsync(WorkspaceRef workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var path = Path.Combine(workspace.Root, ".gitignore");
        var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : null;
        await File.WriteAllTextAsync(path, AppendEntry(existing), ct).ConfigureAwait(false);

        var (exitCode, _) = await RunGitAsync(workspace.Root, ["check-ignore", "-q", Entry], ct).ConfigureAwait(false);
        return exitCode == 0;
    }

    /// <summary>「このフォルダでは聞かない」を覚える。</summary>
    public static async Task DeclineAsync(WorkspaceRef workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        Directory.CreateDirectory(workspace.Company.Root);
        await File.WriteAllTextAsync(
            DeclinedMarker(workspace.Company),
            "このフォルダでは .gitignore に .company/ を足すか聞かない（人間が決めた）。消すと、次に開いたときにまた聞く。\n",
            ct).ConfigureAwait(false);
    }

    private static async Task<(int ExitCode, string Output)> RunGitAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Sessions.ProcessEncoding.Utf8,
                StandardErrorEncoding = Sessions.ProcessEncoding.Utf8,
                UseShellExecute = false,
            };

            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return (-1, string.Empty);
            }

            var output = process.StandardOutput.ReadToEndAsync(ct);
            _ = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // **git が無い環境でも止めない。** 聞かないだけ。
            return (-1, string.Empty);
        }
    }
}

/// <summary><see cref="CompanyGitIgnore.CheckAsync"/> の結果。</summary>
public abstract record CompanyGitIgnoreCheck
{
    /// <summary>無視されていない。<b>人間に聞く。</b></summary>
    /// <param name="AlreadyTracked"><c>.company/</c> の中身が既に commit されているか。</param>
    public sealed record ShouldAsk(bool AlreadyTracked) : CompanyGitIgnoreCheck;

    /// <summary>もう無視されている（<c>.gitignore</c>・グローバル設定・<c>info/exclude</c> のどれでも）。</summary>
    public sealed record AlreadyIgnored : CompanyGitIgnoreCheck;

    /// <summary>「このフォルダでは聞かない」と人間が決めてある。</summary>
    public sealed record Declined : CompanyGitIgnoreCheck;

    /// <summary>git の管理下ではない、または git が使えない。</summary>
    public sealed record NotApplicable : CompanyGitIgnoreCheck;
}
