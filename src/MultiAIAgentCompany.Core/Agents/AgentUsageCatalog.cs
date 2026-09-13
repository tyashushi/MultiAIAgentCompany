using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MultiAIAgentCompany.Core.Agents;

/// <summary>1つの利用枠。<b>使用率ではなく残り</b>に揃える（設計 §50）。</summary>
public sealed record AgentUsageWindow(
    string? Group, string Name, double RemainingPercent, DateTimeOffset? ResetsAt, string? ResetText);

/// <summary>残量の取得結果。<b>読めないことを、残量ゼロと扱わない</b>（設計 §7 / §50）。</summary>
public abstract record AgentUsageResult(AgentKind Kind)
{
    public sealed record Available(AgentKind Kind, IReadOnlyList<AgentUsageWindow> Windows, DateTimeOffset ObservedAt) : AgentUsageResult(Kind);
    public sealed record NotInstalled(AgentKind Kind) : AgentUsageResult(Kind);
    public sealed record Unreadable(AgentKind Kind, string Reason, string RawOutput) : AgentUsageResult(Kind);
}

/// <summary>会話を使わずに、3つの CLI の残量を読む（設計 §50）。</summary>
public static class AgentUsageCatalog
{
    /// <summary><b>30秒で打ち切る。</b> 失敗時も標準出力と標準エラーを先頭2000文字まで返す（§7 / §50）。</summary>
    public static async Task<AgentUsageResult> ReadAsync(AgentKind kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        Process? process = null;
        Task outputTask = Task.CompletedTask;
        Task errorTask = Task.CompletedTask;
        IReadOnlyList<AgentUsageWindow> windows = [];
        string? reason = null;
        try
        {
            if (AgentExecutable.Find(kind) is not { } executable)
            {
                return new AgentUsageResult.NotInstalled(kind);
            }

            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = kind is AgentKind.CodexCli,
                CreateNoWindow = true,
            };
            string[] arguments = kind switch
            {
                // **履歴にセッションを残さない旗を外さない**（§50-1、実機で確認済み）。
                AgentKind.ClaudeCode => ["-p", "/usage", "--output-format", "json", "--no-session-persistence"],
                AgentKind.AntigravityCli => ["-p", "/quota", "--output-format", "json"],
                AgentKind.CodexCli => ["app-server", "--stdio"],
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            process = Process.Start(info) ?? throw new InvalidOperationException("プロセスを作れない");
            errorTask = CaptureAsync(process.StandardError, stderr, token);
            if (kind is AgentKind.CodexCli)
            {
                var response = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                outputTask = CaptureCodexAsync(process.StandardOutput, stdout, response, token);
                await process.StandardInput.WriteLineAsync(
                    """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"MultiAIAgentCompany","version":"0.1"}}}""".AsMemory(), token).ConfigureAwait(false);
                await process.StandardInput.WriteLineAsync(
                    """{"jsonrpc":"2.0","method":"initialized"}""".AsMemory(), token).ConfigureAwait(false);
                await process.StandardInput.WriteLineAsync(
                    """{"jsonrpc":"2.0","id":2,"method":"account/rateLimits/read"}""".AsMemory(), token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
                var line = await response.Task.WaitAsync(token).ConfigureAwait(false);
                if (line is not null)
                {
                    using var document = JsonDocument.Parse(line);
                    var error = Property(document.RootElement, "error");
                    if (error.ValueKind is not JsonValueKind.Undefined)
                        reason = "読み取りエラー: " + (String(error, "message") ?? error.ToString());
                    else
                        windows = ParseCodex(line);
                }
            }
            else
            {
                outputTask = CaptureAsync(process.StandardOutput, stdout, token);
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
                windows = kind is AgentKind.ClaudeCode ? ParseClaude(stdout.ToString()) : ParseAntigravity(stdout.ToString());
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            reason = "時間切れ（30秒）";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            reason = $"起動できない: {exception.Message}";
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    // **app-server は応答後も終わらない。** キャンセル時も子孫ごと終わらせる（§50）。
                    try
                    {
                        if (kind is AgentKind.CodexCli) process.StandardInput.Close();
                    }
                    finally
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception exception)
                {
                    reason = $"終了処理に失敗: {exception.Message}";
                }

                // **途中までの出力も残す。** 終了後の読み取りも30秒の期限内に収める（§7）。
                try { await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception exception) { reason ??= $"出力を読めない: {exception.Message}"; }
                process.Dispose();
            }
        }

        ct.ThrowIfCancellationRequested();
        if (reason is null && windows.Count > 0)
            return new AgentUsageResult.Available(kind, windows, DateTimeOffset.Now);
        var raw = stdout + "\n" + stderr;
        return new AgentUsageResult.Unreadable(kind, reason ?? "出力の形が想定と違う", raw[..Math.Min(2000, raw.Length)]);
    }

    private static async Task CaptureAsync(StreamReader reader, StringBuilder output, CancellationToken ct)
    {
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            output.Append(buffer, 0, count);
    }

    private static async Task CaptureCodexAsync(StreamReader reader, StringBuilder output,
        TaskCompletionSource<string?> response, CancellationToken ct)
    {
        // **行になっていない出力も残す。** 時間切れでも診断を捨てない（§7）。
        var pending = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                output.Append(buffer, 0, count);
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] != '\n') { pending.Append(buffer[i]); continue; }
                    var line = pending.ToString();
                    pending.Clear();
                    if (IsCodexResponse(line)) response.TrySetResult(line);
                }
            }
            if (IsCodexResponse(pending.ToString())) response.TrySetResult(pending.ToString());
            response.TrySetResult(null);
        }
        catch (Exception exception)
        {
            response.TrySetException(exception);
            throw;
        }
    }

    private static bool IsCodexResponse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var id = Property(root, "id");
            return Property(root, "method").ValueKind is JsonValueKind.Undefined
                && id.ValueKind is JsonValueKind.Number && id.TryGetInt32(out var value) && value == 2;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Claude の <c>result</c> から利用枠だけを読む。<b>利用傾向の行は採らない</b>（§50）。</summary>
    public static IReadOnlyList<AgentUsageWindow> ParseClaude(string output) => Parse(output, root =>
    {
        if (Property(root, "is_error").ValueKind is JsonValueKind.True || String(root, "result") is not { } result) return [];
        var windows = new List<AgentUsageWindow>();
        foreach (var line in result.Split('\n'))
        {
            var match = Regex.Match(line.TrimEnd('\r'), @"^(?<name>[^:\n]+): (?<used>\d+(?:\.\d+)?)% used(?: · resets (?<reset>.+))?$", RegexOptions.CultureInvariant);
            if (!match.Success || !double.TryParse(match.Groups["used"].Value, CultureInfo.InvariantCulture, out var used)
                || !double.IsFinite(used)) continue;
            var name = match.Groups["name"].Value;
            name = name switch
            {
                "Current session" => "5時間枠",
                "Current week (all models)" => "週枠（全モデル）",
                _ when name.StartsWith("Current week (", StringComparison.Ordinal) && name.EndsWith(')') => $"週枠（{name[14..^1]}）",
                _ => name,
            };
            windows.Add(new(null, name, Math.Clamp(100 - used, 0, 100), null,
                match.Groups["reset"].Success ? match.Groups["reset"].Value : null));
        }
        return windows;
    });

    /// <summary>Codex の2枠を読む。<b>クレジットの情報は混ぜない</b>（§50）。</summary>
    public static IReadOnlyList<AgentUsageWindow> ParseCodex(string responseLine) => Parse(responseLine, root =>
    {
        if (Property(root, "error").ValueKind is not JsonValueKind.Undefined) return [];
        var limits = Property(Property(root, "result"), "rateLimits");
        var windows = new List<AgentUsageWindow>();
        foreach (var key in new[] { "primary", "secondary" })
        {
            var window = Property(limits, key);
            var duration = Property(window, "windowDurationMins");
            if (!Number(window, "usedPercent", out var used) || duration.ValueKind is not JsonValueKind.Number
                || !duration.TryGetInt32(out var minutes)) continue;
            DateTimeOffset? reset = null;
            var timestamp = Property(window, "resetsAt");
            if (timestamp.ValueKind is JsonValueKind.Number && timestamp.TryGetInt64(out var seconds)
                && seconds >= -62135596800 && seconds <= 253402300799)
                reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
            var name = minutes switch { 300 => "5時間枠", 10080 => "週枠", _ => $"{minutes}分枠" };
            windows.Add(new(null, name, Math.Clamp(100 - used, 0, 100), reset, null));
        }
        return windows;
    });

    /// <summary>Antigravity のモデル群ごとの枠を読む（設計 §50）。</summary>
    public static IReadOnlyList<AgentUsageWindow> ParseAntigravity(string output) => Parse(output, root =>
    {
        if (String(root, "status") != "SUCCESS") return [];
        var groups = Property(Property(Property(root, "command"), "data"), "groups");
        if (groups.ValueKind is not JsonValueKind.Array) return [];
        var windows = new List<AgentUsageWindow>();
        foreach (var group in groups.EnumerateArray())
        {
            var buckets = Property(group, "buckets");
            if (String(group, "name") is not { } groupName || buckets.ValueKind is not JsonValueKind.Array) continue;
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (!Number(bucket, "remaining_fraction", out var fraction)) continue;
                var name = String(bucket, "window") switch
                {
                    "5h" => "5時間枠", "weekly" => "週枠", _ => String(bucket, "name"),
                };
                if (name is null) continue;
                DateTimeOffset? reset = DateTimeOffset.TryParse(String(bucket, "reset_time"), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date) ? date : null;
                windows.Add(new(groupName, name, Math.Clamp(fraction, 0, 1) * 100, reset, null));
            }
        }
        return windows;
    });

    // **形が違う JSON も例外にしない。** 版が変わったら「読めない」と返す（§7 / §50）。
    private static IReadOnlyList<AgentUsageWindow> Parse(string output, Func<JsonElement, IReadOnlyList<AgentUsageWindow>> parse)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];
        try { using var document = JsonDocument.Parse(output); return parse(document.RootElement); }
        catch (JsonException) { return []; }
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind is JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string? String(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static bool Number(JsonElement element, string name, out double number)
    {
        number = 0;
        var value = Property(element, name);
        return value.ValueKind is JsonValueKind.Number && value.TryGetDouble(out number) && double.IsFinite(number);
    }
}
