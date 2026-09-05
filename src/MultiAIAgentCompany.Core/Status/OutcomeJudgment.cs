namespace MultiAIAgentCompany.Core.Status;

/// <summary>
/// 1つの層が何を言っているか。<b>「見ていない」と「成功と確認した」を同じ値にしない</b>（設計 §14-4）。
/// </summary>
public enum LayerObservation
{
    /// <summary>まだ読んでいない。成功の根拠にならない。</summary>
    NotObserved,

    /// <summary>この経路にはこの層が存在しない（例: 構造化 CLI に MCP の isError は無い）。</summary>
    NotApplicable,

    /// <summary>読んで、失敗していないことを確認した。</summary>
    Ok,

    /// <summary>読んで、失敗を確認した。</summary>
    Failed,
}

/// <summary>
/// その経路で<b>どの層が Ok まで確認されていなければならないか</b>。
/// </summary>
/// <remarks>
/// 設計 §13 末尾の「成功判定は必ず3層すべてを見る」を、経路ごとの必須集合として表す。
/// 空の要求は作れない —— 何も要求しなければ、何も見ずに成功が返ってしまう。
/// </remarks>
public sealed record OutcomeRequirement
{
    public OutcomeRequirement(bool protocol, bool tool, bool payload)
    {
        if (!protocol && !tool && !payload)
        {
            throw new ArgumentException("必須の層が1つも無い要求は作れない（何も見ずに成功が返る）");
        }

        Protocol = protocol;
        Tool = tool;
        Payload = payload;
    }

    public bool Protocol { get; }

    public bool Tool { get; }

    public bool Payload { get; }

    /// <summary>
    /// Unity MCP。実測（§13-5b）で3段階のどこでも失敗が隠れたので、3層すべてを要求する。
    /// </summary>
    public static readonly OutcomeRequirement UnityMcp = new(protocol: true, tool: true, payload: true);

    /// <summary>
    /// CLI の構造化経路。MCP の <c>isError</c> にあたる層は無いので要求しない。
    /// Antigravity の <c>status:"SUCCESS"</c> は Payload 層だが、<c>denied_actions</c> は
    /// 層とは別に必ず検査される（<see cref="OutcomeSignals.Judge"/>）。
    /// </summary>
    public static OutcomeRequirement For(Agents.AgentKind kind) => kind switch
    {
        Agents.AgentKind.ClaudeCode => new(protocol: true, tool: false, payload: true),
        Agents.AgentKind.CodexCli => new(protocol: true, tool: false, payload: true),
        Agents.AgentKind.AntigravityCli => new(protocol: true, tool: false, payload: true),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

/// <summary>
/// 応答が「成功」と言っているとき、それが嘘でないかを判定するための材料。設計 §13 末尾 / §14-4。
/// </summary>
/// <remarks>
/// 実測で、失敗は3段階のうちどこでも隠れられることが分かっている。
/// <list type="number">
/// <item>JSON-RPC の <c>error</c> …… 通信・プロトコルの失敗</item>
/// <item><c>result.isError</c> …… ツールの失敗（引数バリデーション等）</item>
/// <item>中身の JSON の <c>success</c> …… 対象側（Unity 等）の失敗</item>
/// </list>
/// Unity MCP の <c>find_gameobjects</c> は、正しい形の呼び出しでも中身が
/// <c>{"success": false, ...}</c> を返し、そのとき <c>isError</c> は <c>false</c> だった。
/// 最初に書いた並列テストはこれを見落とし、全 4142 回が「成功」に見えていた。
/// <para>
/// 初版はこれを「観測した層に失敗が無ければ成功」として実装していて、層を1つ渡すだけで
/// 成功が返った（再レビューで発覚、§14-4）。いまは<b>必須の層が Ok になるまで成功にしない</b>。
/// </para>
/// </remarks>
/// <param name="Protocol">層1。トランスポート／JSON-RPC。</param>
/// <param name="Tool">層2。<c>result.isError</c> 相当。</param>
/// <param name="Payload">層3。応答本体が自称する成否。</param>
/// <param name="DeniedActions">
/// 承認が無いために握りつぶされた操作。Antigravity の <c>result.denied_actions</c> 相当。
/// 空でないなら、どの層が何を言おうと成功ではない。
/// </param>
public sealed record OutcomeSignals(
    LayerObservation Protocol = LayerObservation.NotObserved,
    LayerObservation Tool = LayerObservation.NotObserved,
    LayerObservation Payload = LayerObservation.NotObserved,
    IReadOnlyList<string>? DeniedActions = null)
{
    /// <summary>
    /// 3層と denied_actions を見たうえでの判定。
    /// <b>失敗を1つでも見たら失敗。必須の層が Ok でなければ成功にしない。</b>
    /// </summary>
    public OutcomeVerdict Judge(OutcomeRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        if (Protocol is LayerObservation.Failed)
        {
            return new OutcomeVerdict(false, "層1（プロトコル）が失敗を報告した");
        }

        if (Tool is LayerObservation.Failed)
        {
            return new OutcomeVerdict(false, "層2（ツール）が失敗を報告した");
        }

        if (Payload is LayerObservation.Failed)
        {
            return new OutcomeVerdict(false, "層3（応答本体）が失敗を報告した");
        }

        if (DeniedActions is { Count: > 0 } denied)
        {
            return new OutcomeVerdict(false, $"承認されずに握りつぶされた操作がある: {string.Join(", ", denied)}");
        }

        var missing = new List<string>();
        AddIfMissing(requirement.Protocol, Protocol, "層1（プロトコル）", missing);
        AddIfMissing(requirement.Tool, Tool, "層2（ツール）", missing);
        AddIfMissing(requirement.Payload, Payload, "層3（応答本体）", missing);

        return missing.Count > 0
            ? new OutcomeVerdict(false, $"必須の層が成功を確認していない: {string.Join(" / ", missing)}")
            : new OutcomeVerdict(true, "必須の層がすべて成功を確認した");
    }

    private static void AddIfMissing(bool required, LayerObservation actual, string label, List<string> into)
    {
        if (!required || actual is LayerObservation.Ok)
        {
            return;
        }

        // 必須の層に「適用外」は使えない。必須だと宣言した以上、読めなければ成功ではない。
        into.Add($"{label}={actual}");
    }
}

/// <param name="Succeeded">必須の層をすべて見たうえでの成否。</param>
/// <param name="Reason">人間に見せる理由。秘密値を入れない。</param>
public sealed record OutcomeVerdict(bool Succeeded, string Reason);
