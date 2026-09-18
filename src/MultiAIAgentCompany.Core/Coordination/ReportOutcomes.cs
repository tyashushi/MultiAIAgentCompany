namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>報告の <c>outcome:</c> 行が言っていること（設計 §62-5）。</summary>
public enum ReportOutcome
{
    /// <summary>行が無い・読めない・食い違う。<b>これまでどおり進める</b>。</summary>
    Unknown,

    Done,

    Partial,

    Blocked,
}

/// <summary>
/// 「報告がある」と「できた」を分ける（設計 §62-5）。
/// </summary>
/// <remarks>
/// <b>読む側と頼む側を同じ場所に置く</b>（<see cref="ReviewVerdicts"/> と同じ置き方）。
/// <para>
/// <b>行が無いことを「できなかった」にしない。</b> 古い部門や書き忘れで計画が止まる ——
/// 止めるのは、部門が自分で <c>partial</c> / <c>blocked</c> と書いたときだけ。
/// </para>
/// </remarks>
public static class ReportOutcomes
{
    public const string Key = "outcome";

    public const string DoneValue = "done";

    public const string PartialValue = "partial";

    public const string BlockedValue = "blocked";

    /// <summary>指示書の約束に入れる文面。</summary>
    public static string RequestText =>
        $"""
        報告には、結果を表す次の行を**必ず1行**入れること:
        `{Key}: {DoneValue}`（依頼をやり終えた）/ `{Key}: {PartialValue}`（一部だけ）/ `{Key}: {BlockedValue}`（進められなかった）。
        計画の途中の工程で `{PartialValue}` か `{BlockedValue}` を書くと、計画は止まり人間が呼ばれる。
        """;

    public static ReportOutcome Parse(string? report)
    {
        if (string.IsNullOrWhiteSpace(report))
        {
            return ReportOutcome.Unknown;
        }

        ReportOutcome? found = null;

        foreach (var raw in report.Split('\n'))
        {
            if (ReviewVerdicts.ValueOf(raw, Key) is not { } value)
            {
                continue;
            }

            var outcome = value switch
            {
                DoneValue => ReportOutcome.Done,
                PartialValue => ReportOutcome.Partial,
                BlockedValue => ReportOutcome.Blocked,
                _ => ReportOutcome.Unknown,
            };

            // **知らない値・食い違う2行は「分からない」**（§37-5 と同じ。片方を選ぶのは推測）。
            if (outcome is ReportOutcome.Unknown || (found is { } already && already != outcome))
            {
                return ReportOutcome.Unknown;
            }

            found = outcome;
        }

        return found ?? ReportOutcome.Unknown;
    }

    /// <summary>計画を止める値か。</summary>
    public static bool StopsPlan(ReportOutcome outcome) =>
        outcome is ReportOutcome.Partial or ReportOutcome.Blocked;

    /// <summary>画面と作業ログに出す値。</summary>
    public static string Describe(ReportOutcome outcome) => outcome switch
    {
        ReportOutcome.Done => DoneValue,
        ReportOutcome.Partial => PartialValue,
        ReportOutcome.Blocked => BlockedValue,
        _ => "不明",
    };
}
