namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// レビュー工程の報告から判定を読む（設計 §37-5）。
/// </summary>
/// <remarks>
/// <b>本文を読んで推し量らない。</b> 読むのは約束した1行だけで、
/// 無ければ <see cref="ReviewVerdict.Unknown"/> —— 人間を呼ぶ（§7）。
/// <para>
/// <b>「たぶん OK」を作らない。</b> 完全自動では、ここで甘く判定した瞬間に
/// **駄目出しを飛ばして実装が走る。**
/// </para>
/// </remarks>
public static class ReviewVerdicts
{
    /// <summary>部門に書いてもらう行。<b>protocol の正本はここ</b>（§17-6 と同じ姿勢）。</summary>
    public const string Key = "verdict";

    /// <summary>そのまま次へ進んでよいときの値。</summary>
    public const string OkValue = "ok";

    /// <summary>直しが要るときの値。</summary>
    public const string ReviseValue = "revise";

    /// <summary>
    /// レビュー工程へ頼む文面（設計 §37-5）。
    /// </summary>
    /// <remarks>
    /// <b>読む側と頼む側を同じ場所に置く。</b> 文面を <c>PlanRunner</c> に、
    /// 読み方をここに置くと、**片方だけ直したときに気付けない** ——
    /// 実機での確かめ（<c>ReviewVerdictLiveTests</c>）も、この文面をそのまま使う。
    /// </remarks>
    public static string RequestText =>
        $"""
        ## 判定を1行で書くこと

        `report.md` に **`{Key}: {OkValue}`**
        （このまま次へ進んでよい）か
        **`{Key}: {ReviseValue}`**（直しが要る）の行を
        **必ず1行**入れてください。**この行が無いと、人間が呼ばれて計画が止まります。**
        """;

    public static ReviewVerdict Parse(string? report)
    {
        if (string.IsNullOrWhiteSpace(report))
        {
            return ReviewVerdict.Unknown;
        }

        ReviewVerdict? found = null;

        foreach (var raw in report.Split('\n'))
        {
            if (ValueOf(raw) is not { } value)
            {
                continue;
            }

            var verdict = value switch
            {
                OkValue => ReviewVerdict.Ok,
                ReviseValue => ReviewVerdict.NeedsRevision,

                // **知らない値は「分からない」。** 通しも戻しもしない。
                _ => ReviewVerdict.Unknown,
            };

            if (verdict is ReviewVerdict.Unknown)
            {
                return ReviewVerdict.Unknown;
            }

            // **食い違う判定が2つあったら、どちらも採らない。**
            // 片方を選ぶのは推測であって、観測ではない。
            if (found is { } already && already != verdict)
            {
                return ReviewVerdict.Unknown;
            }

            found = verdict;
        }

        return found ?? ReviewVerdict.Unknown;
    }

    /// <summary>
    /// その行が判定行なら、値を小文字で返す。
    /// </summary>
    /// <remarks>
    /// <b>飾りは剥がす</b>（Markdown の見出し・太字・引用・箇条書き）——
    /// 部門は人間が読む文書を書くので、素の <c>verdict: ok</c> で来るとは限らない。
    /// </remarks>
    private static string? ValueOf(string line)
    {
        var text = line.Trim().TrimStart('#', '>', '-', '*', ' ', '\t');
        text = text.Replace("**", string.Empty).Replace("`", string.Empty).Trim();

        if (!text.StartsWith(Key, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = text[Key.Length..].TrimStart();
        if (rest.StartsWith(':') || rest.StartsWith('：'))
        {
            return rest[1..].Trim().TrimEnd('.', '。').ToLowerInvariant();
        }

        return null;
    }
}
