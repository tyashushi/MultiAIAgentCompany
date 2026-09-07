using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Coordination;
using MultiAIAgentCompany.Core.Sessions;
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
    /// フォルダを選んだか（設計 §28-2）。
    /// </summary>
    /// <remarks>
    /// <b>選ぶまでは、空の3ペインを見せない。</b> 初見の人間に「何のアプリで、まず何をするか」
    /// を出す —— いまは小さな文字が散っているだけだった（§25-2 の 26番）。
    /// <para>
    /// <b>表示用の文字列から推定しない</b>（§7、レビューで発覚）。最初はラベルの先頭が
    /// 「（」かどうかで見ていたので、**`（株）案件` のようなフォルダを開くと案内が出っぱなし**になり、
    /// 会話が隠れて操作できなくなる。<b>開いたかどうかは、開いた側が入れる。</b>
    /// </para>
    /// </remarks>
    public bool HasWorkspace
    {
        get;
        set
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasWorkspace)));
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
    /// 秘書が publish した未処理の提案（設計 §17-6）。
    /// <b>「仕事にする」は自由入力欄ではなく、この具体的な提案に出す</b>（§17-4）。
    /// </summary>
    public ObservableCollection<ProposalCard> Proposals { get; } = [];

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

    /// <summary>
    /// アプリ全体の診断（設計 §28-9）。<b>部門に紐づかない失敗はここへ。</b>
    /// </summary>
    /// <remarks>
    /// 最初は「選択中の部門があればそこへ」だったが、**詳細が一番要る初期化と復旧の失敗では
    /// 部門が選ばれていない** ので、そのまま捨てていた（レビューで発覚）。
    /// <b>ライブ専用</b>で、保存しない（§10）。
    /// </remarks>
    public DiagnosticsLog Diagnostics { get; } = new();

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

    /// <summary>
    /// CLI が申告したモデル（設計 §27）。<b>観測できたときだけ入る。</b>
    /// </summary>
    /// <remarks>
    /// <b>こちらが渡した設定値で埋めない。</b> 見出しは狭いので、observed と intended が
    /// 同じ見た目で並ぶと読み分けられない —— 設定値は診断ビューに出典つきで出す（§27-3）。
    /// </remarks>
    public AgentModel? ObservedModel
    {
        get;
        set
        {
            field = value;
            Raise();
            Raise(nameof(ModelText));
            Raise(nameof(HasModel));
        }
    }

    /// <summary>見出しに出す言い換え。未観測なら空。</summary>
    public string ModelText => ObservedModel is { } model ? AgentModelText.Of(model) : string.Empty;

    public bool HasModel => ObservedModel is not null;

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
        DepartmentAction.DispatchTask => "この仕事を渡す",
        DepartmentAction.RedispatchTask => "差し戻した仕事を送り直す",
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
    /// 診断（設計 §22）。<b>観測と別に持つ</b> —— こちらは redact していない中身で、
    /// 保存しない。ターミナルの代わりに<b>読むだけ</b>で出す。
    /// </summary>
    public DiagnosticsLog Diagnostics { get; } = new();

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

    /// <summary>
    /// 差し戻しの理由。<b>次の指示書に入る</b>（設計 §19-2）——
    /// <c>rejection.md</c> を置くだけでは部門が読む保証がない。
    /// </summary>
    /// <remarks>
    /// <b>§15-7 の「拒否理由の入力欄を出さない」はここに掛からない。</b>
    /// あれは (a) 実行時の承認の話で、承認は待たせる操作だから理由入力で止めない。
    /// 報告の差し戻しは待たせる相手がいないうえ、理由が無いと次の試行が同じ報告を返す。
    /// </remarks>
    public string RejectionDraft
    {
        get;
        set
        {
            field = value;
            Raise();
        }
    } = string.Empty;

    /// <summary>
    /// 受理／差し戻しを出すか。<b>ボタンの文言（<see cref="ActionLabel"/>）とは別軸</b> ——
    /// 用件の1段目は「報告を読む」で、読んだあとに決めるのが受理か差し戻しだから、
    /// 同じ1つのボタンに畳めない（設計 §19-3）。
    /// </summary>
    public bool CanJudgeReport => Status.Work?.Value is CoreTaskStatus.Reported;

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
                     nameof(SessionRunning), nameof(CanStart), nameof(CanJudgeReport),
                 })
        {
            Raise(name);
        }
    }

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>trust 1行ぶんの表示。<b>言い回しをここで決める</b>（判定は Core）。</summary>
/// <param name="ExecutablePath">
/// PATH 上で見つかった実行ファイル。<b>見つからなければ null</b>（設計 §28-1）。
/// </param>
public sealed record TrustRow(AgentKind Agent, WorkspaceTrustState State, string? ExecutablePath = null)
{
    public string AgentText => Agent.ToString();

    /// <summary>
    /// <b>そもそも CLI があるか</b>を、trust より先に言う（設計 §28-1）。
    /// </summary>
    /// <remarks>
    /// 無いものに「信頼を与えてください」と言っても始まらない。
    /// ただし<b>「見つかった」を「動く」と言わない</b> —— 実際に動くかは起動するまで分からない（§7）。
    /// </remarks>
    public string StateText => ExecutablePath is null
        ? "見つからない"
        : State switch
        {
            WorkspaceTrustState.Trusted => "信頼済み",
            WorkspaceTrustState.NotTrusted => "未 trust",
            _ => "判定できない",
        };

    /// <summary>
    /// 人間が取る行動。<b>アプリは trust を書かない</b>（設計 §13-9 規則2）——
    /// 与えるのは人間の操作なので、どこで与えるかを伝えるに留める。
    /// </summary>
    public string ActionText => ExecutablePath is null
        ? $"`{AgentExecutable.NameOf(Agent)}` が PATH に無い。入れるか、PATH を通してからアプリを開き直す"
        : State switch
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
        RecoveryKind.UnreadableLease => "書き込み権を読めない",
        RecoveryKind.ExpiredLease => "書き込み権が失効したまま",
        _ => "読めない",
    };

    public bool IsMaybeSent => Kind is RecoveryKind.MaybeSent;

    public bool IsUnreadable => Kind is RecoveryKind.Unreadable;

    /// <summary>
    /// <c>lease.json</c> が読めない（設計 §23）。<b>この1つで全部の dispatch が止まる。</b>
    /// </summary>
    public bool IsUnreadableLease => Kind is RecoveryKind.UnreadableLease;

    /// <summary>
    /// 失効した書き込み権が残っている（設計 §24）。<b>待っても空かない。</b>
    /// </summary>
    public bool IsExpiredLease => Kind is RecoveryKind.ExpiredLease;

    /// <summary>担当部門。<b>読めない仕事では null</b> —— 中の departmentId も信用できない（§16-3）。</summary>
    public string? DepartmentId { get; init; }

    /// <summary>
    /// 人間に見せている保持者（設計 §24-2）。<b>押したときにこれと違っていたら実行しない。</b>
    /// </summary>
    public LeaseHolder? Lease { get; init; }
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

    /// <summary>
    /// <c>lease.json</c> を読めなかった（設計 §23）。
    /// <b>仕事1件の問題ではなく、ワークスペース全体が止まる。</b>
    /// </summary>
    UnreadableLease,

    /// <summary>
    /// 書き込み権が失効した保持者を持ったまま（設計 §24）。
    /// <b>時間では空かない</b> —— §14-2 が時間切れだけでの奪取を禁じているので、
    /// 人間が外すまでこのワークスペースでは誰にも仕事を渡せない。
    /// </summary>
    ExpiredLease,
}

/// <summary>秘書の提案1件（設計 §17-6）。<b>まだ仕事ではない。</b></summary>
public sealed record ProposalCard(SecretaryProposal Proposal, string DepartmentLabel, bool CanMakeTask)
{
    public string Id => Proposal.Id;

    public string Body => Proposal.Body;

    /// <summary>宛先。<b>知らない部門でも捨てず、そう出す</b>（§17-6）。</summary>
    public string TargetText => DepartmentLabel;

    public bool HasProblem => !CanMakeTask;

    /// <summary>なぜ仕事にできないか。<b>捨てないので、理由を出す</b>（§17-6）。</summary>
    public string ProblemText => string.IsNullOrWhiteSpace(Body)
        ? "本文が空なので仕事にできない。秘書に書き直してもらうか、やめる"
        : "宛先が分からないので仕事にできない。秘書に部門を聞き直すか、やめる";
}
