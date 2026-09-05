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
                DepartmentTile.Placeholder("設計", AgentKind.ClaudeCode, DriveMode.Structured,
                    RuntimeState.Exited, ActivityState.Unknown, null, now),
                DepartmentTile.Placeholder("実装", AgentKind.CodexCli, DriveMode.Structured,
                    RuntimeState.Exited, ActivityState.Unknown, null, now),
                DepartmentTile.Placeholder("調査", AgentKind.AntigravityCli, DriveMode.Tui,
                    RuntimeState.Exited, ActivityState.Unknown, null, now),
                DepartmentTile.Placeholder("レビュー", AgentKind.CodexCli, DriveMode.Structured,
                    RuntimeState.Exited, ActivityState.Unknown, null, now),
                DepartmentTile.Placeholder("テスト", AgentKind.CodexCli, DriveMode.Structured,
                    RuntimeState.Exited, ActivityState.Unknown, null, now),
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
    /// 人型アイコン。設計 §3 —— <b>(a) 承認まちと (b) 相談中を同じ絵にしない。</b>
    /// 同じ絵にすると、人間がターミナルを開くべきか
    /// <c>.company/</c> のドキュメントを読むべきかが分からなくなる。
    /// </summary>
    public string Glyph => Status.Activity.Value switch
    {
        ActivityState.Working => "🏃",
        ActivityState.Resting => "🧍",
        ActivityState.AwaitingApproval => "🙋",   // (a) → ターミナル / 承認ボタンへ
        ActivityState.Consulting => "💬",         // (b) → .company/ のドキュメントへ
        ActivityState.Degraded => "🤕",
        _ => "❔",
    };

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
