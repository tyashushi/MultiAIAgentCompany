using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Status;
using MultiAIAgentCompany.Core.Workspace;
using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Desktop;

/// <summary>
/// 3ペインの表示用モデル。<b>ここに業務を書かない</b>（設計 §4）。
/// Core の型をそのまま並べるだけの層に留める。
/// </summary>
public sealed class ShellViewModel : INotifyPropertyChanged
{
    private string _workspaceLabel = "（ワークスペース未選択）";

    public string WorkspaceLabel
    {
        get => _workspaceLabel;
        set
        {
            _workspaceLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WorkspaceLabel)));
        }
    }

    /// <summary>
    /// 選んだフォルダに対する各 CLI の trust 判定（設計 §13-9）。
    /// <b>「未 trust」と「判定できない」を分けて出す。</b>
    /// </summary>
    public ObservableCollection<TrustRow> Trust { get; } = [];

    /// <summary>
    /// 起動時の復旧走査で見つかったもの（設計 §14-1 / §16-3）。
    /// <b>一過性のログ行にしない</b> —— 流れて消えると、自動再送しない契約を人間が守れない。
    /// </summary>
    public ObservableCollection<RecoveryItem> Recovery { get; } = [];

    /// <summary>
    /// 秘書が居ないときも中央ペインを空にしない（設計 §17-4）——
    /// 状態と、次に人間が取る行動を出す。
    /// </summary>
    public string SecretaryStatus
    {
        get;
        set
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SecretaryStatus)));
        }
    } = "（ワークスペース未選択）";

    public bool HasRecovery => Recovery.Count > 0;

    public ShellViewModel() =>
        // 計算プロパティなので、集合が変わったことを自分で知らせないと画面に出ない。
        Recovery.CollectionChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasRecovery)));

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>左ペイン: 作業ログ一覧。</summary>
    public required ObservableCollection<string> WorkLog { get; init; }

    /// <summary>中央ペイン: 秘書との会話。</summary>
    public required ObservableCollection<string> SecretaryTranscript { get; init; }

    /// <summary>右ペイン: 部門ステータス。</summary>
    public required IReadOnlyList<DepartmentTile> Departments { get; init; }

    /// <summary>
    /// (a) ランタイム承認の待ち行列（設計 §3 / §5）。
    /// <b>(b) 判断の相談はここに来ない</b> —— あれは <c>.company/</c> のファイルで扱う。
    /// </summary>
    public required ApprovalQueue Approvals { get; init; }
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
    private readonly List<string> _observations = [];

    public DepartmentTile(
        string id, string name, AgentKind agent, DriveMode mode, DepartmentStatusTracker tracker)
    {
        Id = id;
        Name = name;
        Agent = agent;
        Mode = mode;
        _tracker = tracker;
        _tracker.Changed += OnTrackerChanged;
    }

    /// <summary>部門の識別子。<c>.company/</c> と検出器はこちらで引く。</summary>
    public string Id { get; }

    public string Name { get; }

    public AgentKind Agent { get; }

    public DriveMode Mode { get; }

    public DepartmentStatus Status => _tracker.Current;

    /// <summary>
    /// 人型アイコンの3層（設計 §15）。<b>この対応は Core が決める</b> ——
    /// 「どの状態で人間が何をすべきか」は業務ロジックであって、表示の都合ではない（§4）。
    /// </summary>
    public DepartmentCallToAction Call =>
        DepartmentCallToAction.From(Status, DispatchedAcrossRestart, SessionRunning);

    /// <summary>
    /// セッションが動いているか。<b>沈黙から導かない</b>（§7）——
    /// <see cref="DepartmentRunner"/> が知っている事実を入れる。
    /// </summary>
    public bool SessionRunning
    {
        get;
        set
        {
            field = value;
            RaiseAll();
        }
    }

    /// <summary>
    /// ボタンの文言（設計 §15-6）。<b>押す前に何が起きるか分かるようにする。</b>
    /// </summary>
    public string ActionLabel => Call.Action switch
    {
        DepartmentAction.Investigate => "原因を見る",
        DepartmentAction.ShowApproval => "承認を見る",
        DepartmentAction.AnswerQuestion => "質問に答える",
        DepartmentAction.ReadReport => "報告を読む",
        DepartmentAction.CheckDelivery => "送信を確認する",
        DepartmentAction.ShowObservations => "観測を見る",
        _ => string.Empty,
    };

    public bool HasAction => Call.Action is not DepartmentAction.None;

    /// <summary>
    /// 起動は仕事の用件と別枠（設計 §15-6）。<b>用件を隠さない。</b>
    /// </summary>
    public bool CanStart => Call.Lifecycle is DepartmentLifecycle.Start;

    /// <summary>直近の観測。「原因を見る」「観測を見る」で人間に出す。</summary>
    public IReadOnlyList<string> RecentObservations => _observations;

    /// <summary>
    /// 起動時の走査で「送ったかもしれない」と分かったか（設計 §14-1 / §16-3）。
    /// <b>推定しない</b> —— 起動時の走査結果からしか入らない。
    /// </summary>
    public bool DispatchedAcrossRestart
    {
        get;
        set
        {
            field = value;
            RaiseAll();
        }
    }

    /// <summary>
    /// いま担当している仕事。<c>question.md</c> / <c>report.md</c> はこの下にある（§6）。
    /// <b>仕事状態と一緒に入れる</b> —— 状態だけあって置き場所が分からない、を作らない。
    /// </summary>
    public string? CurrentTaskSlug
    {
        get;
        set
        {
            field = value;
            RaiseAll();
        }
    }

    /// <summary>
    /// この部門への指示の下書き。<b>中央の入力欄とは別</b>（設計 §17-4）——
    /// 中央は秘書のもので、同じ欄が複数の文脈を背負うと §0 / §1 と衝突する。
    /// </summary>
    public string TaskDraft
    {
        get;
        set
        {
            field = value;
            Raise();
        }
    } = string.Empty;

    /// <summary>この部門を選んでいるか。</summary>
    public bool IsSelected
    {
        get;
        set
        {
            field = value;
            Raise();
        }
    }

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

    /// <summary>
    /// 観測を控えておく。<b>秘密値は入らない</b> —— 元が RedactedSummary（§10）。
    /// </summary>
    /// <remarks>
    /// <b>UI スレッドへ渡し直す。</b> 観測は読み取りループ（別スレッド）から来るので、
    /// そのまま足すと、人間が「観測を見る」を押して列挙している最中に書き換わる。
    /// </remarks>
    public void Record(Evidence evidence)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Append(evidence);
            return;
        }

        Dispatcher.UIThread.Post(() => Append(evidence));
    }

    private void Append(Evidence evidence)
    {
        _observations.Insert(0, $"{evidence.ObservedAt:HH:mm:ss}  {evidence.Source}  {evidence.RedactedSummary}");
        if (_observations.Count > 30)
        {
            _observations.RemoveAt(_observations.Count - 1);
        }
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
                 {
                     nameof(Status), nameof(Call), nameof(RuntimeText), nameof(ActivityText),
                     nameof(WorkText), nameof(Glyph), nameof(BadgeGlyph), nameof(RuntimeGlyph),
                     nameof(NeedsHuman), nameof(EvidenceText), nameof(ActionLabel), nameof(HasAction),
                     nameof(SessionRunning), nameof(CanStart),
                 })
        {
            Raise(name);
        }
    }

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>trust 1行ぶんの表示。<b>言い回しをここで決める</b>（判定は Core）。</summary>
public sealed record TrustRow(AgentKind Agent, WorkspaceTrustState State)
{
    public string AgentText => Agent.ToString();

    public string StateText => State switch
    {
        WorkspaceTrustState.Trusted => "信頼済み",
        WorkspaceTrustState.NotTrusted => "未 trust",
        _ => "判定できない",
    };

    /// <summary>
    /// 人間が取る行動。<b>アプリは trust を書かない</b>（設計 §13-9 規則2）——
    /// 与えるのは人間の操作なので、どこで与えるかを伝えるに留める。
    /// </summary>
    public string ActionText => State switch
    {
        WorkspaceTrustState.Trusted => "そのまま使える",
        WorkspaceTrustState.NotTrusted => "その CLI をこのフォルダで一度起動して信頼を与える",
        _ => "設定ファイルを読めなかった。未 trust とは限らない",
    };
}

/// <summary>
/// 起動時の走査で人間に確かめてほしいもの（設計 §16-3）。
/// </summary>
/// <param name="Kind">何が起きているか。</param>
/// <param name="Slug">対象の仕事。</param>
/// <param name="Detail">人間に見せる1行。</param>
public sealed record RecoveryItem(RecoveryKind Kind, string Slug, string Detail)
{
    public string KindText => Kind switch
    {
        RecoveryKind.MaybeSent => "送ったかもしれない",
        _ => "読めない",
    };

    public bool IsMaybeSent => Kind is RecoveryKind.MaybeSent;

    public bool IsUnreadable => Kind is RecoveryKind.Unreadable;

    /// <summary>担当部門。<b>読めない仕事では null</b> —— 中の departmentId も信用できない（§16-3）。</summary>
    public string? DepartmentId { get; init; }
}

public enum RecoveryKind
{
    /// <summary><c>Dispatched</c> のまま再起動を跨いだ。<b>自動再送しない</b>（§14-1）。</summary>
    MaybeSent,

    /// <summary>
    /// <c>state.json</c> を読めなかった。<b>部門に紐づけられない</b> ——
    /// 読めないなら中の <c>departmentId</c> も信用できない（§16-3）。
    /// </summary>
    Unreadable,
}
