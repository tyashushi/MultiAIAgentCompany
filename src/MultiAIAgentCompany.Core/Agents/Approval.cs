namespace MultiAIAgentCompany.Core.Agents;

/// <summary>
/// 承認の種類。設計 §3。<b>混ぜない。</b>
/// 同じ絵で表示すると、人間がターミナルを開くべきか
/// <c>.company/</c> のドキュメントを読むべきかが分からなくなる。
/// </summary>
public enum ApprovalKind
{
    /// <summary>
    /// (a) ランタイム承認。CLI 自身がツール実行前に出す許可要求。
    /// CLI のランタイムが強制するので、アプリが肩代わりすることはできない。
    /// </summary>
    Runtime,

    /// <summary>
    /// (b) 判断の相談。エージェントが自分で選んで出す。ファイルで扱えるのでベンダー非依存。
    /// </summary>
    Consultation,
}

/// <summary>
/// 承認要求。<b>決定の語彙をハードコードしない</b>（設計 §5）。
/// </summary>
/// <remarks>
/// Codex の <c>availableDecisions</c> は版で変わる。実測（§13-2）では 0.144.6 が
/// <c>['accept', {'acceptWithExecpolicyAmendment': {...}}, 'cancel']</c> を提示し、
/// 仕様書が挙げていた <c>decline</c> / <c>acceptForSession</c> は<b>提示されなかった</b>。
/// UI のボタンは <see cref="AvailableDecisions"/> から動的に生成し、
/// 提示されていない決定を返さない。
/// </remarks>
public sealed record ApprovalRequest(
    string RequestId,
    ApprovalKind Kind,
    AgentKind Agent,
    string? SessionId,
    string? TurnId,
    string Title,
    string Details,
    IReadOnlyList<ApprovalDecision> AvailableDecisions,
    IReadOnlyList<string> SuggestedRules,
    string? ToolName = null,
    string? TargetPath = null)
{
    /// <summary>
    /// 何のツールか（設計 §35）。<b>自動承認の判定に使う。</b>
    /// </summary>
    /// <remarks>
    /// <b>表示用の <c>Title</c> と分ける。</b> あちらは CLI が人間向けに作った文字列で、
    /// **版で変わる**。判定に使うと、文言が変わった日から黙って通らなくなる（§7）。
    /// <b>返してこない CLI では null</b> —— そのときは自動承認しない。
    /// </remarks>
    /// <summary>提示された決定かどうか。提示されていない決定は送らない。</summary>
    public bool Offers(string decisionId) =>
        AvailableDecisions.Any(d => string.Equals(d.Id, decisionId, StringComparison.Ordinal));
}

/// <summary>
/// 1つの決定肢。CLI が文字列でも構造体でも提示してくるので、生の形を <see cref="RawJson"/> に保つ。
/// </summary>
/// <param name="Id">CLI へ返すときの識別子（<c>accept</c> / <c>cancel</c> など）。</param>
/// <param name="Label">ボタンに出す文言。</param>
/// <param name="RawJson">
/// CLI が提示した生の JSON。オブジェクト形式の決定
/// （<c>{"acceptWithExecpolicyAmendment": {...}}</c>）をそのまま返せるようにするため。
/// 文字列形式なら null。
/// </param>
public sealed record ApprovalDecision(string Id, string Label, string? RawJson = null);
