using System.Diagnostics;
using System.Text.Json;

namespace MultiAIAgentCompany.Core.Agents;

/// <summary>選べるモデル（設計 §48）。</summary>
/// <param name="Id">CLI に渡す値。</param>
/// <param name="Label">人間に見せる名前。<b>Id と違うことがある。</b></param>
/// <param name="Efforts">
/// そのモデルで使える思考の強さ。<b>空なら「分からない」</b>——
/// **「無い」ではない**ので、呼び出し側は CLI ごとの候補に落とす（§7）。
/// </param>
public sealed record AgentModelChoice(string Id, string Label, IReadOnlyList<string> Efforts);

/// <summary>
/// CLI からモデルと思考の強さの候補を取る（設計 §48）。
/// </summary>
/// <remarks>
/// <b>取れる CLI からは取る。</b> 手で持つ一覧は古くなる —— そして
/// **モデルごとに使える強さが違う**ので、一律の候補を出すと
/// 「設定できるが起動しない組み合わせ」を作れてしまう（§47-2 で agy で踏んだ）。
/// <para>
/// <b>取り方は CLI ごとに違う</b>（2026-09-13 に実機で確かめた）——
/// Codex は <c>codex debug models</c> が JSON を返し、Antigravity は <c>agy models</c> が
/// タブ区切りを返す。**Claude はモデルの一覧を出せない**（カタログが実行ファイルの中にある）。
/// </para>
/// <para>
/// <b>失敗しても空で返す。</b> 一覧が出ないことで設定画面を止めない（打ち込めばよい）。
/// </para>
/// </remarks>
public static class AgentModelCatalog
{
    /// <summary>その CLI がモデルの一覧を出せるか。</summary>
    public static bool CanListModels(AgentKind kind) =>
        kind is AgentKind.AntigravityCli or AgentKind.CodexCli;

    public static async Task<IReadOnlyList<AgentModelChoice>> ListModelsAsync(AgentKind kind, CancellationToken ct)
    {
        if (!CanListModels(kind) || AgentExecutable.Find(kind) is not { } executable)
        {
            return [];
        }

        var arguments = kind is AgentKind.CodexCli ? new[] { "debug", "models" } : ["models"];
        var output = await RunAsync(executable, arguments, ct).ConfigureAwait(false);

        return kind is AgentKind.CodexCli ? ParseCodex(output) : ParseAntigravity(output);
    }

    /// <summary>
    /// その CLI で使える思考の強さ（設計 §48）。
    /// </summary>
    /// <remarks>
    /// <b>Claude は聞けば答える</b> —— 無効な値を渡すと
    /// <c>Valid values: low, medium, high, xhigh, max.</c> と警告に書いてくる。
    /// <c>--help</c> と併せれば**会話を1つも使わない**（実機で確かめた）。
    /// <para>
    /// <b>Codex はモデルごとに違う</b>ので、ここでは返さない（<see cref="AgentModelChoice.Efforts"/> を使う）。
    /// <b>Antigravity は持てない</b>（強さがモデル名に畳まれている。§47-2）。
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<string>> ListEffortsAsync(AgentKind kind, CancellationToken ct)
    {
        if (kind is not AgentKind.ClaudeCode || AgentExecutable.Find(kind) is not { } executable)
        {
            return [];
        }

        var output = await RunAsync(
            executable, ["--effort", "mac-probe-invalid", "--help"], ct).ConfigureAwait(false);
        return ParseValidValues(output);
    }

    /// <summary><c>codex debug models</c> の JSON を読む。<b>隠されているものは出さない。</b></summary>
    public static IReadOnlyList<AgentModelChoice> ParseCodex(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind is not JsonValueKind.Array)
            {
                return [];
            }

            var choices = new List<AgentModelChoice>();
            foreach (var model in models.EnumerateArray())
            {
                // **`visibility: "hide"` は出さない。** CLI 自身が人間に見せないと決めたものを、
                // こちらの画面で選ばせない（`codex-auto-review` などが該当する）。
                if (model.TryGetProperty("visibility", out var visibility)
                    && !string.Equals(visibility.GetString(), "list", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!model.TryGetProperty("slug", out var slug) || slug.GetString() is not { Length: > 0 } id)
                {
                    continue;
                }

                var label = model.TryGetProperty("display_name", out var display)
                    ? display.GetString() ?? id
                    : id;

                var efforts = new List<string>();
                if (model.TryGetProperty("supported_reasoning_levels", out var levels)
                    && levels.ValueKind is JsonValueKind.Array)
                {
                    foreach (var level in levels.EnumerateArray())
                    {
                        if (level.TryGetProperty("effort", out var effort)
                            && effort.GetString() is { Length: > 0 } value)
                        {
                            efforts.Add(value);
                        }
                    }
                }

                choices.Add(new AgentModelChoice(id, label, efforts));
            }

            return choices;
        }
        catch (JsonException)
        {
            // **読めないものは無いものとして扱う。** 版が変われば形も変わる（§7）。
            return [];
        }
    }

    /// <summary>
    /// <c>agy models</c> の出力を読む。<b>タブ区切りの行だけ</b>を採る。
    /// </summary>
    /// <remarks>
    /// 先頭に「Fetching available models...」のような行が混じるので、
    /// <b>形に合う行だけ拾う</b> —— 知らない行を id として扱わない（§7）。
    /// <b>強さは返さない</b> —— あちらはモデル名に畳まれている（§47-2）。
    /// </remarks>
    public static IReadOnlyList<AgentModelChoice> ParseAntigravity(string output)
    {
        var choices = new List<AgentModelChoice>();
        foreach (var line in (output ?? string.Empty).Split('\n'))
        {
            var parts = line.Trim('\r', ' ').Split('\t', 2);
            if (parts.Length is 2 && parts[0].Length > 0 && parts[1].Trim().Length > 0)
            {
                choices.Add(new AgentModelChoice(parts[0].Trim(), parts[1].Trim(), []));
            }
        }

        return choices;
    }

    /// <summary><c>Valid values: a, b, c.</c> を読む。</summary>
    public static IReadOnlyList<string> ParseValidValues(string output)
    {
        const string Marker = "Valid values:";
        var index = (output ?? string.Empty).IndexOf(Marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return [];
        }

        var rest = output![(index + Marker.Length)..];
        var end = rest.IndexOfAny(['\n', '\r']);
        if (end >= 0)
        {
            rest = rest[..end];
        }

        return [.. rest.TrimEnd('.', ' ').Split(',')
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)];
    }

    private static async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return string.Empty;
            }

            // **標準エラーも読む。** Claude は候補を警告として出す（標準出力ではない）。
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return await stdout + "\n" + await stderr;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return string.Empty;
        }
    }
}
