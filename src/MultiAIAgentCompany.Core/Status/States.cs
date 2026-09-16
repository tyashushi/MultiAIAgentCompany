namespace MultiAIAgentCompany.Core.Status;

/// <summary>
/// 稼働状態 —— プロセスの生死。設計 §7 の第1軸。
/// </summary>
public enum RuntimeState
{
    Starting,
    Running,
    Exited,
    Failed,
}

/// <summary>
/// 活動状態 —— そのエージェントが今なにをしているか。設計 §7 の第2軸。
/// </summary>
/// <remarks>
/// 規則（設計 §7）:
/// <list type="bullet">
/// <item>「一定時間出力が無い」を <see cref="Resting"/> にしない。実測 2026-09-05、
/// Gemini CLI が quota 超過の 429 を無言でリトライし続け、1時間出力ゼロのまま生存した。</item>
/// <item><see cref="AwaitingApproval"/> は推定しない。構造化イベント（Codex の
/// <c>activeFlags:["waitingOnApproval"]</c> など）か、版を固定した画面 fixture で
/// 確認した強いパターンだけを根拠にする。</item>
/// <item>検出器が知らない CLI 版なら <see cref="Unknown"/>。人間の入力直後に
/// <see cref="Working"/> と断定しない。</item>
/// </list>
/// </remarks>
public enum ActivityState
{
    Unknown,
    Working,
    Resting,

    /// <summary>(a) ランタイム承認まち。設計 §3。人間はターミナル／承認ボタンへ行く。</summary>
    AwaitingApproval,

    /// <summary>(b) 判断の相談中。設計 §3。人間は <c>.company/</c> のドキュメントへ行く。</summary>
    Consulting,

    Degraded,

    /// <summary>agy のログの版を未検証なので、承認待ちを区別できない（設計 §61-3）。</summary>
    WorkingOrAwaitingApproval,
}
