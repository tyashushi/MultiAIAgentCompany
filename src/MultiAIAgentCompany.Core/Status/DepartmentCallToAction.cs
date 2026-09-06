using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Core.Status;

/// <summary>
/// 人型アイコンのポーズ。<b>活動状態と1対1</b>（設計 §15-2）。
/// </summary>
public enum DepartmentPose
{
    /// <summary>分からない。放置しないが、異常ではない。</summary>
    Unknown,

    /// <summary>動いている。人間は何もしなくてよい。</summary>
    Working,

    /// <summary>turn が終わって手が空いている。仕事を割り当てられる。</summary>
    Resting,

    /// <summary>(a) ツール実行の許可を待っている。<b>行き先はアプリの承認ボタン</b>（設計 §3）。</summary>
    AwaitingApproval,

    /// <summary>(b) 判断を人間に聞いている。<b>行き先は <c>.company/</c> のドキュメント</b>（設計 §3）。</summary>
    Consulting,

    /// <summary>動いてはいるが何かおかしい。</summary>
    Degraded,
}

/// <summary>
/// 右上のバッジ。<b>人間の返事を待っている仕事があるときだけ</b>出す（設計 §15-3）。
/// </summary>
public enum DepartmentBadge
{
    None,

    /// <summary><c>question.md</c> を読んで <c>answer.md</c> を書く。</summary>
    NeedsAnswer,

    /// <summary><c>report.md</c> を読んで受理か差し戻しを決める。</summary>
    NeedsAcceptance,

    /// <summary>
    /// 送られたか確かめる。<c>Dispatched</c> は「送ったかもしれない」を意味し、
    /// 自動再送しない（設計 §14-1）。
    /// </summary>
    NeedsDeliveryCheck,
}

/// <summary>
/// パネル上のボタンが求める用件。設計 §15-6 の8段。<b>上から順に、最初に当たったもの</b>。
/// </summary>
/// <remarks>
/// <b><see cref="DepartmentCallToAction.NeedsHuman"/> と必ず一致していなければならない。</b>
/// 片方だけを直すと「要対応と出ているのに押すものが無い」が生まれる
/// （実装前の相談で見つかった。2026-09-06）。
/// </remarks>
public enum DepartmentAction
{
    /// <summary>人間の出番が無い。ボタンを出さない。</summary>
    None,

    /// <summary>
    /// 落ちた理由を見る。<b>「再起動する」にしない</b> —— §15-4 は
    /// 「原因を見て、再起動するか決める」であって、UI が復旧の判断を先取りしない。
    /// </summary>
    Investigate,

    /// <summary>中央ペインの該当承認へ寄せる。</summary>
    ShowApproval,

    /// <summary>
    /// <c>question.md</c> を開く。<b>活動 <c>Consulting</c> と仕事 <c>AwaitingAnswer</c> の
    /// どちらからでも来る</b>（§3 / §7）—— (b) の相談は経路が2つある。
    /// </summary>
    AnswerQuestion,

    /// <summary><c>report.md</c> を開く。受理か差し戻しを決めるのは人間（§6）。</summary>
    ReadReport,

    /// <summary>送られたか確かめる。<b>自動再送しない</b>（§14-1）。</summary>
    CheckDelivery,

    /// <summary>
    /// 指示書はあるが、まだ部門へ投げていない仕事を渡す（設計 §15-6）。
    /// </summary>
    /// <remarks>
    /// <b><c>Rejected</c>（差し戻し）に同じものを当てない。</b>
    /// <c>Rejected → Dispatched</c> は現在の <c>instruction.md</c> を
    /// <c>attempts/&lt;n&gt;/</c> へ封じるが、dispatch は**先に読んでから**遷移するので、
    /// **古い指示を送ったうえ、新しい試行に指示書が残らない**（§15-10 の未決）。
    /// </remarks>
    DispatchTask,

    /// <summary>観測を並べる。何かおかしいが、落ちてはいない。</summary>
    ShowObservations,

}

/// <summary>
/// 部門のライフサイクル操作。<b>仕事の用件とは別枠</b>（設計 §15-6、2026-09-06 に分けた）。
/// </summary>
/// <remarks>
/// 起動は仕事の用件ではなく、§9 のプロセス所有権に属する副作用。同じ列に並べると、
/// <c>Reported</c> や <c>AwaitingAnswer</c> の部門を**永久に起動できなくなる**
/// （実機で詰まった。回答の配達にはセッションが要るのに、起動ボタンが隠れていた）。
/// </remarks>
public enum DepartmentLifecycle
{
    None,

    /// <summary>セッションを開く。</summary>
    Start,
}

/// <summary>左下の印。稼働状態の担当（設計 §15-4）。</summary>
public enum DepartmentRuntimeMark
{
    None,

    /// <summary>プロセスが落ちた。<b>人間が最も早く気付くべき事実</b>なので強く出す。</summary>
    Down,

    /// <summary>終了した。意図した終了なら何もしなくてよい。</summary>
    Ended,
}

/// <summary>
/// 人型アイコンが出す3つの層。設計 §15-1。
/// </summary>
/// <remarks>
/// <b>3軸を1枚に混ぜず、層にする。</b> ポーズは活動、バッジは仕事、印は稼働を担当し、
/// どれも他を隠さない ——「働いているが別の仕事の報告が人間待ち」も、
/// 「倒れているが仕事は Reported のまま」も、1枚で読める（§7 の「3軸を潰さない」）。
/// </remarks>
public sealed record DepartmentCallToAction(
    DepartmentPose Pose,
    DepartmentBadge Badge,
    DepartmentRuntimeMark RuntimeMark)
{
    /// <summary>
    /// パネル上のボタンが求める用件（設計 §15-6）。<b>上から順に、最初に当たったもの。</b>
    /// </summary>
    public DepartmentAction Action { get; init; } = DepartmentAction.None;

    /// <summary>
    /// 別枠のライフサイクル操作（設計 §15-6）。<b>仕事の用件を隠さない。</b>
    /// </summary>
    public DepartmentLifecycle Lifecycle { get; init; } = DepartmentLifecycle.None;

    /// <summary>
    /// 人間の出番があるか。<b><see cref="Action"/> と必ず一致する</b> ——
    /// 「要対応と出ているのに押すものが無い」を作らないため（§15-6）。
    /// </summary>
    public bool NeedsHuman => Action is not DepartmentAction.None;

    /// <param name="status">3軸の現在値。</param>
    /// <param name="dispatchedAcrossRestart">
    /// <c>Dispatched</c> のまま再起動を跨いだか（設計 §14-1 の復旧契約）。
    /// 呼び出し元が起動時の走査結果から渡す。<b>この型は自分で推定しない。</b>
    /// </param>
    /// <param name="sessionRunning">
    /// セッションが動いているか。<b>沈黙から導かない</b>（§7）——
    /// 呼び出し元が知っている事実を渡す。
    /// </param>
    public static DepartmentCallToAction From(
        DepartmentStatus status, bool dispatchedAcrossRestart = false, bool sessionRunning = false)
    {
        ArgumentNullException.ThrowIfNull(status);

        var pose = PoseOf(status.Activity.Value);
        var badge = BadgeOf(status.Work?.Value, dispatchedAcrossRestart);
        var mark = MarkOf(status.Runtime.Value);

        return new DepartmentCallToAction(pose, badge, mark)
        {
            Action = ActionOf(pose, badge, mark, status.Work?.Value),

            // 落ちているときは出さない。§15-4 は「原因を見て、再起動するか決める」であり、
            // すぐ横に「起動」を置くとその判断を飛ばさせる。
            Lifecycle = !sessionRunning && mark is not DepartmentRuntimeMark.Down
                ? DepartmentLifecycle.Start
                : DepartmentLifecycle.None,
        };
    }

    /// <summary>§15-6 の8段。<b>順序が意味を持つ</b>。</summary>
    private static DepartmentAction ActionOf(
        DepartmentPose pose, DepartmentBadge badge, DepartmentRuntimeMark mark, CoreTaskStatus? work)
    {
        // 1. 落ちている。Exited（意図した終了）はここに入れない ——
        //    終わった部門に報告が残っているなら、急ぐのは報告を読むこと。
        if (mark is DepartmentRuntimeMark.Down)
        {
            return DepartmentAction.Investigate;
        }

        // 2. (a) 承認まち。
        if (pose is DepartmentPose.AwaitingApproval)
        {
            return DepartmentAction.ShowApproval;
        }

        // 3. (b) 相談。活動と仕事のどちらからでも来る（§3 / §7）。
        if (pose is DepartmentPose.Consulting || badge is DepartmentBadge.NeedsAnswer)
        {
            return DepartmentAction.AnswerQuestion;
        }

        // 4. 報告まち。
        if (badge is DepartmentBadge.NeedsAcceptance)
        {
            return DepartmentAction.ReadReport;
        }

        // 5. 送ったかもしれない仕事（§14-1）。
        if (badge is DepartmentBadge.NeedsDeliveryCheck)
        {
            return DepartmentAction.CheckDelivery;
        }

        // 6. 指示書はあるが、まだ渡していない。
        //    5（送信確認）より下 —— あちらは §14-1 の復旧契約なので、これで隠さない。
        //    7（観測を見る）より上 —— こちらの方が具体的な用件。
        if (work is CoreTaskStatus.Drafted)
        {
            return DepartmentAction.DispatchTask;
        }

        // 7. 何かおかしいが落ちてはいない。
        if (pose is DepartmentPose.Degraded)
        {
            return DepartmentAction.ShowObservations;
        }

        // 起動はここに来ない。仕事の用件とは別枠（Lifecycle）——
        // 同じ列に並べると、用件のある部門を永久に起動できなくなる。
        return DepartmentAction.None;
    }

    private static DepartmentPose PoseOf(ActivityState activity) => activity switch
    {
        ActivityState.Working => DepartmentPose.Working,
        ActivityState.Resting => DepartmentPose.Resting,
        ActivityState.AwaitingApproval => DepartmentPose.AwaitingApproval,
        ActivityState.Consulting => DepartmentPose.Consulting,
        ActivityState.Degraded => DepartmentPose.Degraded,
        ActivityState.Unknown => DepartmentPose.Unknown,
        _ => DepartmentPose.Unknown,
    };

    private static DepartmentBadge BadgeOf(CoreTaskStatus? work, bool dispatchedAcrossRestart) => work switch
    {
        CoreTaskStatus.AwaitingAnswer => DepartmentBadge.NeedsAnswer,
        CoreTaskStatus.Reported => DepartmentBadge.NeedsAcceptance,
        CoreTaskStatus.Dispatched when dispatchedAcrossRestart => DepartmentBadge.NeedsDeliveryCheck,
        _ => DepartmentBadge.None,
    };

    private static DepartmentRuntimeMark MarkOf(RuntimeState runtime) => runtime switch
    {
        RuntimeState.Failed => DepartmentRuntimeMark.Down,
        RuntimeState.Exited => DepartmentRuntimeMark.Ended,
        _ => DepartmentRuntimeMark.None,
    };
}
