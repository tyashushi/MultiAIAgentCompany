namespace MultiAIAgentCompany.Core.Agents;

/// <summary>
/// 部門を担当する CLI。<b>3つは対称ではない</b>（設計 §2）。
/// 「複数 AI を同じ枠で扱う」という発想が最初に壊れる場所なので、
/// 能力の違いは <see cref="AgentCapabilities"/> に明示して持つ。
/// </summary>
public enum AgentKind
{
    ClaudeCode,
    CodexCli,
    AntigravityCli,
}

/// <summary>
/// 部門の動かし方（設計 §5）。部門ごとに設定する。既定は CLI の能力で決まる。
/// </summary>
public enum DriveMode
{
    /// <summary>stream-json / app-server の JSON-RPC。承認をアプリ内のボタンで扱える。</summary>
    Structured,

    /// <summary>PTY 上の対話画面。承認はターミナルで人間が直接行う。アプリは代行しない。</summary>
    Tui,
}

/// <summary>
/// その CLI にできること。<b>実測（設計 §13）で確かめた事実だけを書く。</b>
/// </summary>
/// <param name="Kind">対象の CLI。</param>
/// <param name="SupportsStructuredConversation">構造化での多ターン会話ができるか。</param>
/// <param name="SupportsRuntimeApprovalRoundTrip">
/// (a) ランタイム承認の往復が閉じるか（設計 §3）。
/// Claude Code と Codex CLI は閉じた。<b>Antigravity は往復そのものが無い。</b>
/// </param>
/// <param name="ReportsDeniedActions">
/// 承認できずに握りつぶした操作を構造化して返すか。
/// Antigravity の <c>result.denied_actions</c> がこれで、<c>result.status:"SUCCESS"</c> が
/// 嘘をつくときの唯一の手がかりになる。
/// </param>
/// <param name="DefaultDriveMode">既定の駆動モード。</param>
public sealed record AgentCapabilities(
    AgentKind Kind,
    bool SupportsStructuredConversation,
    bool SupportsRuntimeApprovalRoundTrip,
    bool ReportsDeniedActions,
    DriveMode DefaultDriveMode)
{
    /// <summary>実測（設計 §13-1 / §13-2 / §13-3、2026-09-05）に基づく既定。</summary>
    public static AgentCapabilities For(AgentKind kind) => kind switch
    {
        AgentKind.ClaudeCode => new(kind, true, true, true, DriveMode.Structured),
        AgentKind.CodexCli => new(kind, true, true, true, DriveMode.Structured),

        // 承認の往復は無いが、**握りつぶしは denied_actions で検出できる**（§13-3）。
        // そして --input-format stream-json で1プロセス多ターンが回る（§13-3 追記2、実測）。
        // したがって既定は構造化。2026-09-06 に TUI から変更した（§5）。
        AgentKind.AntigravityCli => new(kind, true, false, true, DriveMode.Structured),

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
