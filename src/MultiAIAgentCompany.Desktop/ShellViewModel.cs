using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
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
    public required ObservableCollection<string> WorkLog { get; init; }

    /// <summary>中央ペイン: 秘書との会話。</summary>
    public required ObservableCollection<string> SecretaryTranscript { get; init; }

    /// <summary>右ペイン: 部門ステータス。</summary>
    public required IReadOnlyList<DepartmentTile> Departments { get; init; }
}

/// <summary>
/// 右ペインの1部門ぶん。<b>状態は自分で決めず、<see cref="DepartmentStatusTracker"/> から受け取る。</b>
/// </summary>
/// <remarks>
/// 検出器の規則（§7）は Core にあり、ここには無い。この型がするのは、
/// 3軸を §15 の3層へ写して、画面へ通知することだけ。
/// <para>
/// <b>通知は UI スレッドへ渡し直す。</b> 観測はセッションの読み取りループ
/// （＝別スレッド）から来る。
/// </para>
/// </remarks>
public sealed class DepartmentTile : INotifyPropertyChanged
{
    private readonly DepartmentStatusTracker _tracker;

    public DepartmentTile(
        string name, AgentKind agent, DriveMode mode, DepartmentStatusTracker tracker)
    {
        Name = name;
        Agent = agent;
        Mode = mode;
        _tracker = tracker;
        _tracker.Changed += OnTrackerChanged;
    }

    public string Name { get; }

    public AgentKind Agent { get; }

    public DriveMode Mode { get; }

    public DepartmentStatus Status => _tracker.Current;

    /// <summary>
    /// 人型アイコンの3層（設計 §15）。<b>この対応は Core が決める</b> ——
    /// 「どの状態で人間が何をすべきか」は業務ロジックであって、表示の都合ではない（§4）。
    /// </summary>
    public DepartmentCallToAction Call => DepartmentCallToAction.From(Status, DispatchedAcrossRestart);

    /// <summary>起動時の走査で「送ったかもしれない」と分かったか（設計 §14-1）。</summary>
    public bool DispatchedAcrossRestart { get; init; }

    public string RuntimeText => Status.Runtime.Value.ToString();

    public string ActivityText => Status.Activity.Value.ToString();

    public string WorkText => Status.Work is null ? "—" : Status.Work.Value.ToString();

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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnTrackerChanged(object? sender, DepartmentStatus status)
    {
        // 観測は読み取りループ（別スレッド）から来る。UI スレッドへ渡し直す。
        if (Dispatcher.UIThread.CheckAccess())
        {
            RaiseAll();
            return;
        }

        Dispatcher.UIThread.Post(RaiseAll);
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
                 {
                     nameof(Status), nameof(Call), nameof(RuntimeText), nameof(ActivityText),
                     nameof(WorkText), nameof(Glyph), nameof(BadgeGlyph), nameof(RuntimeGlyph),
                     nameof(NeedsHuman), nameof(EvidenceText),
                 })
        {
            Raise(name);
        }
    }

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
