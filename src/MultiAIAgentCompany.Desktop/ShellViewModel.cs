using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Status;
using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Desktop;

/// <summary>
/// 3ペインの表示用モデル。<b>ここに業務を書かない</b>（設計 §4）。
/// Core の型をそのまま並べるだけの層に留める。
/// </summary>
public sealed class ShellViewModel
{
    public required string WorkspaceLabel { get; init; }

    /// <summary>左ペイン: 作業ログ一覧。</summary>
    public required IReadOnlyList<string> WorkLog { get; init; }

    /// <summary>中央ペイン: 秘書との会話。</summary>
    public required IReadOnlyList<string> SecretaryTranscript { get; init; }

    /// <summary>右ペイン: 部門ステータス。</summary>
    public required IReadOnlyList<DepartmentTile> Departments { get; init; }

    /// <summary>
    /// 骨組みの段階で画面を起動して確かめるための固定値。
    /// <b>実際の状態はここではなく <c>.company/</c> と構造化イベントから来る</b>（設計 §6 / §7）。
    /// </summary>
    public static ShellViewModel CreateDesignPlaceholder()
    {
        var now = DateTimeOffset.Now;

        return new ShellViewModel
        {
            WorkspaceLabel = "（ワークスペース未選択）",
            WorkLog = ["まだ何も動かしていない"],
            SecretaryTranscript = ["秘書はまだ起動していない"],
            Departments =
            [
                // 絵ができるまでの仮置き。§15 の3層が同時に見える組み合わせにしてある ——
                // 「働いているが別の仕事の報告が人間待ち」「倒れているが仕事は残っている」が
                // 1枚で読めることを、起動して目で確かめるため。
                DepartmentTile.Placeholder("設計", AgentKind.ClaudeCode, DriveMode.Structured,
                    RuntimeState.Running, ActivityState.Working, null, now),
                DepartmentTile.Placeholder("実装", AgentKind.CodexCli, DriveMode.Structured,
                    RuntimeState.Running, ActivityState.AwaitingApproval, CoreTaskStatus.InProgress, now),
                DepartmentTile.Placeholder("調査", AgentKind.AntigravityCli, DriveMode.Structured,
                    RuntimeState.Running, ActivityState.Consulting, CoreTaskStatus.AwaitingAnswer, now),
                DepartmentTile.Placeholder("レビュー", AgentKind.ClaudeCode, DriveMode.Structured,
                    RuntimeState.Running, ActivityState.Working, CoreTaskStatus.Reported, now),
                DepartmentTile.Placeholder("テスト", AgentKind.CodexCli, DriveMode.Structured,
                    RuntimeState.Failed, ActivityState.Unknown, CoreTaskStatus.Dispatched, now),
            ],
        };
    }
}

/// <summary>右ペインの1部門ぶん。</summary>
public sealed class DepartmentTile
{
    public required string Name { get; init; }

    public required AgentKind Agent { get; init; }

    public required DriveMode Mode { get; init; }

    public required DepartmentStatus Status { get; init; }

    public string RuntimeText => Status.Runtime.Value.ToString();

    public string ActivityText => Status.Activity.Value.ToString();

    public string WorkText => Status.Work is null ? "—" : Status.Work.Value.ToString();

    /// <summary>
    /// 人型アイコンの3層（設計 §15）。<b>この対応は Core が決める</b> ——
    /// 「どの状態で人間が何をすべきか」は業務ロジックであって、表示の都合ではない（§4）。
    /// </summary>
    public DepartmentCallToAction Call => DepartmentCallToAction.From(Status);

    /// <summary>
    /// ポーズ。<b>(a) 承認まちと (b) 相談中を同じ絵にしない</b>（設計 §3）——
    /// 人間の行き先が違う。ここは絵ができるまでの仮置き。
    /// </summary>
    public string Glyph => Call.Pose switch
    {
        DepartmentPose.Working => "🏃",
        DepartmentPose.Resting => "🧍",
        DepartmentPose.AwaitingApproval => "🙋",   // (a) → 承認ボタン / ターミナルへ
        DepartmentPose.Consulting => "💬",         // (b) → .company/ のドキュメントへ
        DepartmentPose.Degraded => "🤕",
        _ => "❔",
    };

    /// <summary>右上のバッジ。人間の返事を待つ仕事があるときだけ（設計 §15-3）。</summary>
    public string BadgeGlyph => Call.Badge switch
    {
        DepartmentBadge.NeedsAnswer => "❓",
        DepartmentBadge.NeedsAcceptance => "📝",
        DepartmentBadge.NeedsDeliveryCheck => "📮",
        _ => string.Empty,
    };

    /// <summary>左下の印。稼働状態の担当（設計 §15-4）。</summary>
    public string RuntimeGlyph => Call.RuntimeMark switch
    {
        DepartmentRuntimeMark.Down => "⚠️",
        DepartmentRuntimeMark.Ended => "⏹",
        _ => string.Empty,
    };

    /// <summary>人間の出番があるか。無ければ眺めているだけでよい。</summary>
    public bool NeedsHuman => Call.NeedsHuman;

    /// <summary>状態の根拠。<b>状態だけを見せない</b>（設計 §7）。</summary>
    public string EvidenceText =>
        $"{Status.Activity.Evidence.Source} / {Status.Activity.Evidence.RedactedSummary}";

    internal static DepartmentTile Placeholder(
        string name,
        AgentKind agent,
        DriveMode mode,
        RuntimeState runtime,
        ActivityState activity,
        CoreTaskStatus? work,
        DateTimeOffset now)
    {
        var evidence = new Evidence(
            Source: EvidenceSource.Dispatch,
            ObservedAt: now,
            SessionId: null,
            TurnId: null,
            Agent: new AgentRef(name, agent),
            AgentVersion: null,
            DetectorVersion: null,
            RedactedSummary: "まだ起動していない");

        return new DepartmentTile
        {
            Name = name,
            Agent = agent,
            Mode = mode,
            Status = new DepartmentStatus(
                new Observed<RuntimeState>(runtime, evidence),
                new Observed<ActivityState>(activity, evidence),
                work is null ? null : new Observed<CoreTaskStatus>(work.Value, evidence)),
        };
    }
}
