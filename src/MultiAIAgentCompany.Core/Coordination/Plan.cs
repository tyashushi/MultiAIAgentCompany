namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// レビュー工程が出した判定（設計 §37-5）。
/// </summary>
/// <remarks>
/// <b>報告の中身を読んで推し量らない。</b> レビュー部門は判定を1行で書く約束で、
/// **書いていなければ <see cref="Unknown"/> にして人間を呼ぶ**（§7）——
/// 「たぶん OK」で次へ進めると、駄目出しを飛ばして実装が走る。
/// </remarks>
public enum ReviewVerdict
{
    /// <summary>判定行が無い、または読めない。<b>人間を呼ぶ。</b></summary>
    Unknown,

    /// <summary>このまま次へ進んでよい。</summary>
    Ok,

    /// <summary>直しが要る。<b>見てもらった工程へ戻す。</b></summary>
    NeedsRevision,
}

/// <summary>
/// 計画の1工程（設計 §37-2）。
/// </summary>
/// <param name="DepartmentId">担当部門。</param>
/// <param name="Handover">
/// 次の工程への一言。<b>秘書が足してよいのはここだけ</b>（§37-7）——
/// 前の工程の報告は<b>そのまま</b>渡す。要約させると秘書が仕様の作者になる。
/// </param>
/// <param name="ReviewsStep">
/// レビュー工程なら、<b>どの工程を見るか</b>（0 起点の位置）。レビューでなければ null。
/// <para>
/// <b>「直前の書き手へ戻す」と推測しない。</b> 明示的に持たないと、
/// 工程が増えたときに戻り先が黙って変わる。
/// </para>
/// </param>
/// <param name="TaskSlug">この工程が生んだ仕事。まだ渡していなければ null。</param>
/// <param name="Verdict">
/// レビュー工程の判定。<b>観測して書き込む</b>（報告を読んだ結果）——
/// 計算では出せないので、<see cref="TaskSlug"/> と同じく記録する。
/// </param>
public sealed record PlanStep(
    string DepartmentId,
    string Handover,
    int? ReviewsStep = null,
    string? TaskSlug = null,
    ReviewVerdict? Verdict = null)
{
    public bool IsReview => ReviewsStep is not null;
}

/// <summary>
/// 秘書が立てて、アプリが進める計画（設計 §37）。
/// </summary>
/// <remarks>
/// <b>進行はアプリが持つ。</b> 秘書（LLM）に持たせると、進行中のパイプラインが
/// **会話の中にしか無い** —— §17-3 が「会話は正本にしない」と決めているので、
/// 秘書を立て直した瞬間にどこまで進んだかが消える（§37-1）。
/// <para>
/// <b>計算で出せるものは持たない</b>（§31-1）。各仕事の状態は <c>state.json</c> が正本で、
/// ここが持つのは<b>どの仕事がどの工程だったか</b>だけである。
/// 「いまどこ」「止まっているか」は <see cref="PlanAdvance.Decide"/> が導く。
/// </para>
/// </remarks>
/// <param name="Id">計画の識別子。ディレクトリ名と一致する。</param>
/// <param name="Goal">人間が最初に言った一言。<b>言い換えない。</b></param>
/// <param name="Steps">工程の列。<b>順番が契約</b>。</param>
/// <param name="Revisions">
/// 差し戻した回数（設計 §37-5）。<b>状態機械には履歴が残らない</b>ので、ここに持つ。
/// </param>
/// <param name="StoppedByHuman">
/// 人間が止めたか（設計 §37-3）。<b>これは観測ではなく決定</b>なので永続する。
/// </param>
/// <param name="Revision">楽観ロック。<c>state.json</c> と同じ姿勢（§14-1）。</param>
public sealed record Plan(
    string Id,
    string Goal,
    IReadOnlyList<PlanStep> Steps,
    int Revisions,
    bool StoppedByHuman,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// 差し戻しの既定の上限（設計 §37-5）。
    /// </summary>
    /// <remarks>
    /// <b>人間が居ないので、誰も止めない。</b> レビューと設計が延々と往復し得る。
    /// </remarks>
    public const int DefaultRevisionLimit = 3;
}
