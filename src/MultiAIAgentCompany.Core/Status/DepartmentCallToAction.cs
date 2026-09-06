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
    /// <summary>人間の出番があるか。無ければ眺めているだけでよい。</summary>
    public bool NeedsHuman =>
        Pose is DepartmentPose.AwaitingApproval or DepartmentPose.Consulting or DepartmentPose.Degraded
        || Badge is not DepartmentBadge.None
        || RuntimeMark is DepartmentRuntimeMark.Down;

    /// <param name="status">3軸の現在値。</param>
    /// <param name="dispatchedAcrossRestart">
    /// <c>Dispatched</c> のまま再起動を跨いだか（設計 §14-1 の復旧契約）。
    /// 呼び出し元が起動時の走査結果から渡す。<b>この型は自分で推定しない。</b>
    /// </param>
    public static DepartmentCallToAction From(DepartmentStatus status, bool dispatchedAcrossRestart = false)
    {
        ArgumentNullException.ThrowIfNull(status);

        return new DepartmentCallToAction(
            PoseOf(status.Activity.Value),
            BadgeOf(status.Work?.Value, dispatchedAcrossRestart),
            MarkOf(status.Runtime.Value));
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
