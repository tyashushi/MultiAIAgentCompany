using System.Diagnostics;

namespace MultiAIAgentCompany.Core.Agents;

/// <summary>選べるモデル（設計 §47-2）。</summary>
/// <param name="Id">CLI に渡す値。</param>
/// <param name="Label">人間に見せる名前。<b>Id と違うことがある。</b></param>
public sealed record AgentModelChoice(string Id, string Label);

/// <summary>
/// CLI からモデルの一覧を取る（設計 §47-2）。
/// </summary>
/// <remarks>
/// <b>取れる CLI からは取る。</b> 手で持つ一覧は古くなる ——
/// Antigravity は <c>agy models</c> が id と表示名を返すので、そこから読む。
/// <para>
/// <b>Claude と Codex からは取れない</b>（一覧を出す口が無い。<c>codex models</c> は端末を要求する）。
/// そちらは自由入力のままにする —— <b>取れないものを、それらしく埋めない</b>（§7）。
/// </para>
/// <para>
/// <b>失敗しても空で返す。</b> 一覧が出ないことで設定画面を止めない（打ち込めばよい）。
/// </para>
/// </remarks>
public static class AgentModelCatalog
{
    /// <summary>その CLI が一覧を出せるか。</summary>
    public static bool CanList(AgentKind kind) => kind is AgentKind.AntigravityCli;

    public static async Task<IReadOnlyList<AgentModelChoice>> ListAsync(AgentKind kind, CancellationToken ct)
    {
        if (!CanList(kind) || AgentExecutable.Find(kind) is not { } executable)
        {
            return [];
        }

        try
        {
            var info = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("models");

            using var process = Process.Start(info);
            if (process is null)
            {
                return [];
            }

            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return Parse(output);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// <c>agy models</c> の出力を読む。<b>タブ区切りの行だけ</b>を採る。
    /// </summary>
    /// <remarks>
    /// 先頭に「Fetching available models...」のような行が混じるので、
    /// <b>形に合う行だけ拾う</b> —— 知らない行を id として扱わない（§7）。
    /// </remarks>
    public static IReadOnlyList<AgentModelChoice> Parse(string output)
    {
        var choices = new List<AgentModelChoice>();
        foreach (var line in (output ?? string.Empty).Split('\n'))
        {
            var parts = line.Trim('\r', ' ').Split('\t', 2);
            if (parts.Length is 2 && parts[0].Length > 0 && parts[1].Trim().Length > 0)
            {
                choices.Add(new AgentModelChoice(parts[0].Trim(), parts[1].Trim()));
            }
        }

        return choices;
    }
}
