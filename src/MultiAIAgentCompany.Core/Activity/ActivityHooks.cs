using System.Text.Encodings.Web;
using System.Text.Json;
using MultiAIAgentCompany.Core.Agents;

namespace MultiAIAgentCompany.Core.Activity;

/// <summary>信頼のハッシュを変えない、起動によらないフック定義（設計 §61-1 / §61-5）。</summary>
public static class ActivityHooks
{
    public static IReadOnlyList<string> Events { get; } = Array.AsReadOnly(new[]
        { "SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest", "PostToolUse", "Stop" });
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Command(string cli, string eventName) =>
        $"sh -c 'cat >/dev/null; printf \"%s {cli} {eventName}\\n\" \"$(date +%s)\" >> \"$MAAC_ACTIVITY_EVENTS\"'";

    private static object Hook(string cli, string eventName) => new { type = "command", command = Command(cli, eventName) };
    private static object MatchedHook(string cli, string eventName) => new { matcher = "", hooks = new[] { Hook(cli, eventName) } };

    public static string ClaudeSettings => JsonSerializer.Serialize(new
    {
        hooks = Events.ToDictionary(e => e, e => new[] { MatchedHook("claude", e) }),
    }, Json);

    public static string AgyHooksJson => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["maac-activity"] = new Dictionary<string, object>
        {
            ["PreInvocation"] = new[] { Hook("agy", "PreInvocation") },
            ["PostInvocation"] = new[] { Hook("agy", "PostInvocation") },
            ["PostToolUse"] = new[] { MatchedHook("agy", "PostToolUse") },
            ["Stop"] = new[] { Hook("agy", "Stop") },
        },
    }, Json);

    public static IReadOnlyList<string> Arguments(AgentKind kind, ActivityLaunch launch) => kind switch
    {
        AgentKind.ClaudeCode => ["--settings", ClaudeSettings],
        AgentKind.CodexCli => Events.SelectMany(e => new[]
        {
            "-c", $"hooks.{e}=[{{hooks=[{{type=\"command\",command={JsonSerializer.Serialize(Command("codex", e), Json)}}}]}}]",
        }).ToArray(),
        AgentKind.AntigravityCli => ["--add-dir", launch.AgyHooksDirectory, "--log-file", launch.AgyLogPath],
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
