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
