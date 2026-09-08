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

    /// <summary>
    /// OS のターミナルで対話起動する（設計 §32）。<b>承認は人間がその窓で押す。</b>
    /// </summary>
    /// <remarks>
    /// 初期ブリーフの「確定した設計判断」#3
    /// （**人間がターミナル上で直接承認する。アプリは承認を代行しない**）に戻す形。
    /// §22 で落としたのは「アプリ内にターミナルを埋め込む」という<b>手段</b>であって、
    /// この<b>目的</b>ではなかった（§32-1）。
    /// <para>
    /// アプリは構造化イベントを受け取らないので、活動状態は <c>Unknown</c> に落ちる。
    /// <b>それは許容する</b> —— ブリーフ #6 の
    /// 「誤って『休けい中』と表示するより『不明』と出す方が安全」（§32-4）。
    /// 仕事状態は <c>.company/</c> の文書から来るので無傷である。
    /// </para>
    /// </remarks>
    ExternalTerminal,

    // **`Tui` は消した**（設計 §22-4、2026-09-06）。v1 はターミナルを持たないと決めたので、
    // 残すと「使えるかのように見える値」になる。3つの CLI はすべて構造化で動く（§13-3 追記2）。
    // PTY が要るときの実測は §13-4 / §13-6 / §13-7 / §13-8 に残してある ——
    // **知識は設計に、動かない選択肢はコードに残さない。**
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
/// <param name="DefaultDriveMode">
/// <b>部門として動かすときの既定</b>の駆動モード。
/// <b>3つとも <see cref="DriveMode.ExternalTerminal"/> で揃える</b>（設計 §32-3）——
/// AI ごとに分けない。秘書だけが例外で、そちらは <c>Structured</c> を明示して起動する。
/// </param>
public sealed record AgentCapabilities(
    AgentKind Kind,
    bool SupportsStructuredConversation,
    bool SupportsRuntimeApprovalRoundTrip,
    bool ReportsDeniedActions,
    DriveMode DefaultDriveMode)
{
    /// <summary>実測（設計 §13-1 / §13-2 / §13-3、2026-09-05）に基づく既定。</summary>
    /// <remarks>
    /// <b>構造化の能力（前3つ）は消していない。</b> 秘書が使うし、部門で使えないと
    /// 決める理由も無い —— 変えたのは<b>既定</b>だけである（§32-3）。
    /// </remarks>
    public static AgentCapabilities For(AgentKind kind) => kind switch
    {
        AgentKind.ClaudeCode => new(kind, true, true, true, DriveMode.ExternalTerminal),
        AgentKind.CodexCli => new(kind, true, true, true, DriveMode.ExternalTerminal),

        // 承認の往復は無いが、**握りつぶしは denied_actions で検出できる**（§13-3）。
        // そして --input-format stream-json で1プロセス多ターンが回る（§13-3 追記2、実測）。
        //
        // **既定は 2026-09-09 に ExternalTerminal へ変えた**（§32-3）。
        // ここが「人間に聞く手段が無い CLI」だったので §30-4 で危険モードを作ったが、
        // ターミナルという聞く手段ができたので、**その抜け道は廃止した**。
        AgentKind.AntigravityCli => new(kind, true, false, true, DriveMode.ExternalTerminal),

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
