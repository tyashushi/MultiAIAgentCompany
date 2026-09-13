namespace MultiAIAgentCompany.Core.Agents;

/// <summary>
/// 部門を起動するときの権限モード（設計 §51）。<b>バイパスは持たない</b>（§51-2）。
/// </summary>
public enum AgentPermissionMode
{
    Auto,
    Manual,
    AcceptEdits,
    Plan,
}

public static class AgentPermissionModes
{
    /// <summary>その CLI が<b>持っているモードだけ</b>を表示順に返す（設計 §51-2）。</summary>
    public static IReadOnlyList<AgentPermissionMode> For(AgentKind kind) => kind switch
    {
        AgentKind.ClaudeCode => [AgentPermissionMode.Auto, AgentPermissionMode.Manual,
            AgentPermissionMode.AcceptEdits, AgentPermissionMode.Plan],
        AgentKind.CodexCli => [AgentPermissionMode.Auto],
        AgentKind.AntigravityCli => [AgentPermissionMode.AcceptEdits, AgentPermissionMode.Plan],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>画面に出す名前。<b>CLI の引数とは分ける</b>（設計 §51）。</summary>
    public static string Label(AgentPermissionMode mode) => mode switch
    {
        AgentPermissionMode.Auto => "自動",
        AgentPermissionMode.Manual => "手動",
        AgentPermissionMode.AcceptEdits => "編集を受け入れる",
        AgentPermissionMode.Plan => "プラン",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}

/// <summary>
/// 権限モードが<b>実際に効くか</b>を CLI に聞く（設計 §51-4）。
/// </summary>
/// <remarks>
/// <b>Claude は、モデルによって黙って通常モードに戻る</b>（2026-09-13 に実機で確かめた）——
/// `--permission-mode auto` を付けても haiku では `default` で起動し、エラーも警告も出さない。
/// 手で一覧を持つと古くなるので、<b>聞く</b>（§48 と同じ姿勢）。
/// <para>
/// <b>会話を使わない。</b> `-p "/usage"` は agent turn を始めない（§50-1）が、
/// stream-json の最初の `init` に<b>実際のモード</b>が載る。
/// </para>
/// </remarks>
public static class AgentPermissionProbe
{
    /// <summary>そのモードで起動したとき、Claude の <c>init</c> が申告するはずの値。</summary>
    /// <remarks><b>`manual` は `default` と申告される</b>（実機で確かめた）。</remarks>
    public static string ExpectedClaudeReport(AgentPermissionMode mode) => mode switch
    {
        AgentPermissionMode.Auto => "auto",
        AgentPermissionMode.Manual => "default",
        AgentPermissionMode.AcceptEdits => "acceptEdits",
        AgentPermissionMode.Plan => "plan",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    /// <summary>
    /// Claude をそのモデルとモードで<b>起動だけ</b>して、申告されたモードを返す。分からなければ null。
    /// </summary>
    public static async Task<string?> ProbeClaudeAsync(string? model, AgentPermissionMode mode, CancellationToken ct)
    {
        if (AgentExecutable.Find(AgentKind.ClaudeCode) is not { } executable)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        var info = new System.Diagnostics.ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // **起動引数は部門と同じものを使う** —— 違う形で聞くと、確かめたものと起動するものがずれる。
        foreach (var argument in AgentExecutable.InteractiveArguments(AgentKind.ClaudeCode, "/usage", model, null, mode))
        {
            if (argument == "/usage")
            {
                continue;
            }

            info.ArgumentList.Add(argument);
        }

        foreach (var argument in new[] { "-p", "/usage", "--output-format", "stream-json", "--verbose", "--no-session-persistence" })
        {
            info.ArgumentList.Add(argument);
        }

        System.Diagnostics.Process? process = null;
        try
        {
            process = System.Diagnostics.Process.Start(info);
            if (process is null)
            {
                return null;
            }

            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            while (await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
            {
                if (ParseInitPermissionMode(line) is { } reported)
                {
                    return reported;
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
            // **`init` を読んだら待たない。** `/usage` の取得まで付き合う理由が無い。
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }

                process.Dispose();
            }
        }
    }

    /// <summary>stream-json の1行が <c>init</c> なら、その <c>permissionMode</c> を返す。</summary>
    public static string? ParseInitPermissionMode(string line)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(line);
            var root = document.RootElement;
            return root.ValueKind is System.Text.Json.JsonValueKind.Object
                && root.TryGetProperty("type", out var type) && type.GetString() == "system"
                && root.TryGetProperty("subtype", out var subtype) && subtype.GetString() == "init"
                && root.TryGetProperty("permissionMode", out var mode)
                && mode.ValueKind is System.Text.Json.JsonValueKind.String
                ? mode.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
