namespace MultiAIAgentCompany.Core.Sessions;

/// <summary>
/// モデルを人間向けに言い換える（設計 §27-2）。<b>表示補助であって正規化ではない。</b>
/// </summary>
/// <remarks>
/// <b>知らない ID は、そのまま出す。</b> 対応表は新しいモデルが出るたび古くなるので、
/// 「読みやすさ」より「古くならないこと」を優先する。元の ID は診断で見られる（§22）。
/// </remarks>
public static class AgentModelText
{
    public static string Of(AgentModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var effort = EffortOf(model.ReasoningEffort);
        return effort is null ? NameOf(model.Id) : $"{NameOf(model.Id)} {effort}";
    }

    private static string NameOf(string id) => id switch
    {
        "claude-opus-5" => "Opus 5",
        "claude-sonnet-5" => "Sonnet 5",
        "claude-haiku-4-5-20251001" => "Haiku 4.5",
        _ => id,
    };

    private static string? EffortOf(string? effort) => effort switch
    {
        null => null,
        "high" => "高",
        "medium" => "中",
        "low" => "低",

        // 知らない強さも隠さない。**空欄にすると「無い」と読まれる。**
        _ => effort,
    };
}
