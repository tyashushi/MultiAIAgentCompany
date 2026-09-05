namespace MultiAIAgentCompany.Core.Status;

/// <summary>
/// 応答が「成功」と言っているとき、それが嘘でないかを判定するための材料。設計 §13 末尾。
/// </summary>
/// <remarks>
/// 実測（2026-09-05）で、失敗は3段階のうちどこでも隠れられることが分かっている。
/// <list type="number">
/// <item>JSON-RPC の <c>error</c> …… 通信・プロトコルの失敗</item>
/// <item><c>result.isError</c> …… ツールの失敗（引数バリデーション等）</item>
/// <item>中身の JSON の <c>success</c> …… 対象側（Unity 等）の失敗</item>
/// </list>
/// Unity MCP の <c>find_gameobjects</c> は、正しい形の呼び出しでも中身が
/// <c>{"success": false, ...}</c> を返し、そのとき <c>isError</c> は <c>false</c> だった。
/// 最初に書いた並列テストはこれを見落とし、全 4142 回が「成功」に見えていた
/// （実際には Editor に一度も届いていなかった）。
/// <para>
/// Antigravity の <c>result.status:"SUCCESS"</c> ＋ <c>denied_actions</c> も同じ構造の罠なので、
/// 判定はここに集約する。<b>このアプリのすべての検出器に共通する規則</b>。
/// </para>
/// </remarks>
/// <param name="ProtocolError">層1。トランスポート／JSON-RPC の error。無ければ null。</param>
/// <param name="ToolReportedError">層2。<c>result.isError</c> 相当。判らなければ null。</param>
/// <param name="PayloadSuccess">層3。応答本体が自称する成否。判らなければ null。</param>
/// <param name="DeniedActions">
/// 承認が無いために握りつぶされた操作。Antigravity の <c>result.denied_actions</c> 相当。
/// 空でないなら、他の3層が何を言おうと成功ではない。
/// </param>
public sealed record OutcomeSignals(
    string? ProtocolError = null,
    bool? ToolReportedError = null,
    bool? PayloadSuccess = null,
    IReadOnlyList<string>? DeniedActions = null)
{
    /// <summary>
    /// 3層すべてと denied_actions を見たうえでの判定。
    /// <b>どれか1つでも失敗を示していれば失敗</b>。判らない層は成功の根拠にしない。
    /// </summary>
    public OutcomeVerdict Judge()
    {
        if (ProtocolError is not null)
        {
            return new OutcomeVerdict(false, $"protocol error: {ProtocolError}");
        }

        if (ToolReportedError is true)
        {
            return new OutcomeVerdict(false, "tool reported isError");
        }

        if (DeniedActions is { Count: > 0 } denied)
        {
            return new OutcomeVerdict(false, $"denied actions: {string.Join(", ", denied)}");
        }

        if (PayloadSuccess is false)
        {
            return new OutcomeVerdict(false, "payload reported success=false");
        }

        // 層2・層3 が両方とも「判らない」なら、成功したとは言えない。
        // status:"SUCCESS" だけを見て成功にしないための番人。
        if (ToolReportedError is null && PayloadSuccess is null)
        {
            return new OutcomeVerdict(false, "no layer confirmed success");
        }

        return new OutcomeVerdict(true, "all observed layers agree");
    }
}

/// <param name="Succeeded">3層すべてを見たうえでの成否。</param>
/// <param name="Reason">人間に見せる理由。秘密値を入れない。</param>
public sealed record OutcomeVerdict(bool Succeeded, string Reason);
