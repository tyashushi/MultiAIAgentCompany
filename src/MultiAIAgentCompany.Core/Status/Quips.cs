using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Core.Status;

/// <summary>
/// ロボットの一言（設計 §52）。<b>キャラクターは「やる気が空回りしている新人ロボット社員」</b>。
/// </summary>
/// <remarks>
/// <b>遊ぶのは、状態を運ばない場所だけ</b>（§52-1）。ポーズ・バッジ・印・根拠の文言には入れない ——
/// あれは「人間が何をすべきか」を出すもので（§15-0）、冗談を混ぜると読み違える。
/// <para>
/// <b>観測していないことを、面白さのために言わない</b>（§7）。
/// 「分からない」部門に「サボり中」とは書かない。
/// </para>
/// <para>
/// <b>人間が急いで判断する場面では遊ばない。</b> 承認待ち・相談中・不調は、何をすればよいかだけを書く。
/// </para>
/// </remarks>
public static class Quips
{
    /// <summary>フォルダを選ぶ前（§52-2）。</summary>
    public const string NoWorkspace = "出社しました。…オフィスはどこですか？";

    /// <summary>秘書との会話がまだ無い（§52-2）。</summary>
    public const string EmptyTranscript = "秘書、待機中。議事録の用紙だけ先に用意しました";

    /// <summary>相談が1件も無い（§52-2）。</summary>
    public const string NoThreads = "相談ゼロ件。平和です";

    /// <summary>どの部門も仕事を抱えていない（§52-2）。</summary>
    public const string AllIdle = "全部門、手が空いています。仕事をください。切実に";

    /// <summary>報告を受理したとき（§52-4）。</summary>
    public const string Cheer = "お疲れさまでした！（ぺこり）";

    /// <summary>
    /// ポーズに添える一言の候補。<b>遊んでよい状態だけ</b>を返し、それ以外は空（§52-3）。
    /// </summary>
    public static IReadOnlyList<string> For(DepartmentPose pose) => pose switch
    {
        DepartmentPose.Working => ["カタカタカタ…", "いま話しかけないでください（集中）", "キーボードが熱い"],
        DepartmentPose.Resting => ["あぐらで待機中", "仕事、ありますか？", "休憩ではありません。待機です"],

        // **「分からない」を「サボり」にしない**（§7）—— 分からない、と言っているだけの文にする。
        DepartmentPose.Unknown => ["本人に聞いてみないと、なんとも", "静かです。寝てはいない…はず"],
        _ => [],
    };

    /// <summary>
    /// ポーズにマウスを載せたときの一言。<b>遊ばない状態では、何をすればよいかだけ</b>を返す（§52-3）。
    /// </summary>
    /// <param name="pose">いまのポーズ。</param>
    /// <param name="needsHuman">
    /// 人間の出番があるか（<see cref="DepartmentCallToAction.NeedsHuman"/>）。<b>あればポーズにかかわらず遊ばない。</b>
    /// </param>
    /// <param name="pick">候補の数を受け取り、使う位置を返す。<b>テストで固定するため</b>に外から渡す。</param>
    /// <remarks>
    /// <b>ポーズだけで決めない</b>（Codex の指摘）。外部ターミナルの部門はポーズが原理的に <c>Unknown</c> のまま
    /// なので（§32-4）、**質問への回答や報告の受理を待っていても「分からない」の冗談が出ていた。**
    /// 人間の出番は仕事状態からも来る。
    /// </remarks>
    public static string Pick(DepartmentPose pose, bool needsHuman, Func<int, int> pick)
    {
        ArgumentNullException.ThrowIfNull(pick);

        var candidates = For(pose);
        if (candidates.Count > 0 && !needsHuman)
        {
            return candidates[Math.Clamp(pick(candidates.Count), 0, candidates.Count - 1)];
        }

        return pose switch
        {
            DepartmentPose.AwaitingApproval => "ツールの実行を許可するか、決めてください",
            DepartmentPose.Consulting => "質問が届いています。読んで答えてください",
            DepartmentPose.Degraded => "様子がおかしいです。原因を見てください",
            _ => "あなたの出番です。タイルのボタンから進めてください",
        };
    }

    /// <summary>
    /// どの部門も仕事を抱えていないか（§52-2）。<b>部門が無いときは言わない。</b>
    /// </summary>
    /// <remarks>
    /// <b>活動状態では判定しない。</b> 外部ターミナルの部門は活動が原理的に <c>Unknown</c> のまま
    /// なので（§32-4）、「全員が手すき」はほぼ観測できない。**仕事状態は `.company/` の文書から来る**ので、
    /// 「抱えている仕事が無い」は言い切れる。
    /// </remarks>
    public static bool AreAllIdle(IReadOnlyCollection<CoreTaskStatus?> works)
    {
        ArgumentNullException.ThrowIfNull(works);
        return works.Count > 0
            && works.All(work => work is null or CoreTaskStatus.Accepted or CoreTaskStatus.Cancelled);
    }
}
