using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
    /// <summary>AI の残量。<b>保存せず、要求されたときだけ取り直す</b>（設計 §50）。</summary>
    public IReadOnlyList<AgentUsageRow> AgentUsage { get; } =
    [
        new(AgentKind.ClaudeCode, "Claude Code"),
        new(AgentKind.CodexCli, "Codex"),
        new(AgentKind.AntigravityCli, "Antigravity"),
    ];

    public string AgentUsageObservedText
    {
        get;
        set
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AgentUsageObservedText)));
        }
    } = string.Empty;

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

    /// <summary>
    /// 秘書へ送った turn の終わりを、期限までに観測していない（設計 §36）。
    /// </summary>
    /// <remarks>
    /// <b>これは秘書についての主張ではない。</b> 言っているのは
    /// 「アプリが turn の終わりを観測していない」だけで、状態は何も動かしていない（§31-5）。
    /// null なら出さない。
    /// </remarks>
    public string? SecretaryStalledText
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SecretaryStalledText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SecretaryStalled)));
        }
    }

    public bool SecretaryStalled => SecretaryStalledText is not null;


    /// <summary>
    /// 走っている計画の1行（設計 §37-3）。<b>無ければ null</b>。
    /// </summary>
    /// <remarks>
    /// <b>人間の出番は割り込みだけ。</b> 報告は出た瞬間に中央へ出る（§34-2）ので、
    /// 流れてくるのを読んでいて、まずいと思ったら止める。
    /// </remarks>
    public string? PlanStatus
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlanStatus)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPlan)));
        }
    }

    public bool HasPlan => PlanStatus is not null;

    /// <summary>人間が止めているか（設計 §37-3）。<b>止めたら「続ける」を出す。</b></summary>
    public bool PlanStopped
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlanStopped)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlanRunning)));
        }
    }

    /// <summary>止めていない＝走っている。<b>「続ける」と「止める」は同時に出さない。</b></summary>
    public bool PlanRunning => !PlanStopped;

    public bool HasRecovery => Recovery.Count > 0;

    /// <summary>
    /// 送る前の添付（設計 §58-6）。<b>まだ複製していない</b> —— 複製は送るとき（§58-2）。
    /// </summary>
    public ObservableCollection<AttachmentDraft> PendingAttachments { get; } = [];

    public bool HasPendingAttachments => PendingAttachments.Count > 0;

    public ShellViewModel()
    {
        PendingAttachments.CollectionChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPendingAttachments)));
        // 計算プロパティなので、集合が変わったことを自分で知らせないと画面に出ない。
        Recovery.CollectionChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasRecovery)));
        Threads.CollectionChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasThreads)));
    }

    /// <summary>フォルダを選ぶ前の一言（設計 §52-2）。</summary>
    public string NoWorkspaceQuip => Quips.NoWorkspace;

    /// <summary>会話が空のときの一言（設計 §52-2）。</summary>
    public string EmptyTranscriptQuip => Quips.EmptyTranscript;

    /// <summary>相談が無いときの一言（設計 §52-2）。</summary>
    public string NoThreadsQuip => Quips.NoThreads;

    /// <summary>どの部門も仕事を抱えていないときの一言（設計 §52-2）。</summary>
    public string AllIdleQuip => Quips.AllIdle;

    /// <summary>空の画面に置く絵。<b>状態を運ばない場所なので、ポーズの意味とは結び付けない</b>（§52-1）。</summary>
    public Bitmap? RestingPoseImage => PoseImages.Of(DepartmentPose.Resting);

    public Bitmap? ConsultingPoseImage => PoseImages.Of(DepartmentPose.Consulting);

    public bool HasThreads => Threads.Count > 0;

    /// <summary>
    /// どの部門も仕事を抱えていないか（設計 §52-2）。<b>判定は Core</b>（<see cref="Quips.AreAllIdle"/>）。
    /// </summary>
    public bool AllDepartmentsIdle => Quips.AreAllIdle([.. Departments.Select(tile => tile.Status.Work?.Value)]);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>左ペイン: 作業ログ一覧。</summary>
    public required ObservableCollection<string> WorkLog
    {
        get;
        init
        {
            field = value;

            // 計算プロパティなので、集合が変わったことを自分で知らせないと画面に出ない。
            value.CollectionChanged += (_, _) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WorkLogText)));
        }
    }

    /// <summary>
    /// 作業ログを1つの文字列にしたもの（2026-09-12、人間の要望）。
    /// </summary>
    /// <remarks>
    /// <b>1行ずつ別の部品にすると、またいで選べない。</b>
    /// <b>会話では §34-4 で既に解いていた問題を、こちらで解いていなかった</b> ——
    /// **同じ形が2箇所にあるなら、片方を直した日に両方見る。**
    /// <para>
    /// 読み返すのは<b>起きた順に並んだ全体</b>なので、手で拾わせると
    /// **拾い落としたところが「起きなかったこと」になる。**
    /// </para>
    /// </remarks>
    public string WorkLogText => string.Join(Environment.NewLine, WorkLog);

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
    public required ObservableCollection<string> SecretaryTranscript
    {
        get;
        init
        {
            field = value;

            // 計算プロパティなので、集合が変わったことを自分で知らせないと画面に出ない。
            value.CollectionChanged += (_, _) =>
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TranscriptText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasTranscript)));
            };
        }
    }

    /// <summary>
    /// 会話を1つの文字列にしたもの（2026-09-11、人間の要望）。
    /// </summary>
    /// <remarks>
    /// <b>発言ごとに別の部品にすると、またいで選べない。</b>
    /// 1つにまとめると、**ドラッグで複数の発言を選べる**し、
    /// <c>Cmd+A</c> で全部選べる。
    /// <para>
    /// 会話は正本ではない（§17-3）ので、**まとめて持っても失うものが無い** ——
    /// 上限は <c>SecretaryTranscript</c> 側（500 行）で効いている。
    /// </para>
    /// </remarks>
    public string TranscriptText => string.Join("\n\n", SecretaryTranscript);

    /// <summary>
    /// 会話が始まっているか（設計 §44）。
    /// </summary>
    /// <remarks>
    /// <b>空のときの見せ方を変えるためだけにある。</b> 何も無い広い面に文が1行あると、
    /// 「壊れている」のか「まだ何もしていない」のかが読めない。
    /// </remarks>
    public bool HasTranscript => SecretaryTranscript.Count > 0;

    /// <summary>
    /// 過去の相談スレッド（設計 §32-6）。
    /// </summary>
    /// <remarks>
    /// <b>左ペインの主役はこれ。</b> 初期ブリーフは「左＝チャットログ一覧」＋
    /// 「左と中央の UI は Claude Code アプリに似せる」と書いていたのに、
    /// 実装は<b>システムの作業ログ</b>になっていた —— 「ログ」に引きずられた取り違え。
    /// </remarks>
    public ObservableCollection<ThreadItem> Threads { get; } = [];

    /// <summary>いま中央に出しているスレッド。無ければ null。</summary>
    public string? CurrentThreadId
    {
        get;
        set
        {
            field = value;
            foreach (var thread in Threads)
            {
                thread.IsSelected = string.Equals(thread.Id, value, StringComparison.Ordinal);
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentThreadId)));
        }
    }

    /// <summary>右ペイン: 部門ステータス。</summary>
    /// <summary>
    /// 部門タイル。<b>ワークスペースごとに入れ替わる</b>（設計 §15-8 / §30-4）——
    /// <c>departments.json</c> は人間が編集できるので、フォルダを開くたびに作り直す。
    /// </summary>
    public required ObservableCollection<DepartmentTile> Departments
    {
        get;
        init
        {
            field = value;

            // **タイルの仕事状態が変わったら、「全員手すき」も読み直す**（§52-2）。
            value.CollectionChanged += (_, e) =>
            {
                foreach (var tile in e.NewItems?.OfType<DepartmentTile>() ?? [])
                {
                    tile.PropertyChanged += OnDepartmentChanged;
                }

                foreach (var tile in e.OldItems?.OfType<DepartmentTile>() ?? [])
                {
                    tile.PropertyChanged -= OnDepartmentChanged;
                }

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AllDepartmentsIdle)));
            };
        }
    }

    private void OnDepartmentChanged(object? sender, PropertyChangedEventArgs e) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AllDepartmentsIdle)));

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
        string id, string name, AgentKind agent, DriveMode mode, DepartmentStatusTracker tracker,
        AgentPermissionMode? permissionMode = null)
    {
        Id = id;
        Name = name;
        Agent = agent;
        Mode = mode;
        PermissionMode = permissionMode;
        _tracker = tracker;
        _tracker.Changed += OnTrackerChanged;
    }

    /// <summary>部門の識別子。<c>.company/</c> と検出器はこちらで引く。</summary>
    public string Id { get; }

    public string Name { get; }

    public AgentKind Agent { get; }

    public DriveMode Mode { get; }

    /// <summary><b>起動時の設定値</b>をタイルに出す（設計 §51-3）。</summary>
    public AgentPermissionMode? PermissionMode { get; }

    public string PermissionModeText => HasPermissionMode
        ? $"権限: {AgentPermissionModes.Label(PermissionMode!.Value)}"
        : string.Empty;

    /// <remarks>
    /// <b>実際に起動引数で渡るときだけ出す。</b> その CLI が持たない値や、構造化の部門に書かれた値は
    /// 渡らない（警告は <see cref="DepartmentWarnings"/> が出す）—— 渡らないものを「権限: 自動」と
    /// 出すと、**タイルが嘘をつく**（§7）。
    /// </remarks>
    public bool HasPermissionMode => PermissionMode is { } mode
        && Mode is DriveMode.ExternalTerminal
        && AgentPermissionModes.For(Agent).Contains(mode);

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
        DepartmentCallToAction.From(
            Status, DispatchedAcrossRestart, SessionRunning, ReportNotObservedSince is not null,
            Mode is DriveMode.ExternalTerminal);

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
        DepartmentAction.ShowApproval => DepartmentCallToAction.ApprovalLabel(Mode is DriveMode.ExternalTerminal),
        DepartmentAction.ReadReport => "報告をもう一度読む",
        DepartmentAction.CheckDelivery => "送信を確認する",
        DepartmentAction.CheckMissingReport => "報告を確かめる",
        DepartmentAction.DispatchTask => "この仕事を渡す",
        DepartmentAction.RedispatchTask => "差し戻した仕事を送り直す",
        DepartmentAction.ShowObservations => "観測を見る",
        _ => string.Empty,
    };

    /// <remarks>質問はボタンにしない（設計 §57）—— 中央に出ていて、答えは部門の窓で打つ。</remarks>
    public bool HasAction => Call.Action is not (DepartmentAction.None or DepartmentAction.AnswerQuestion);

    /// <summary>
    /// 起動は仕事の用件と別枠（設計 §15-6）。<b>用件を隠さない。</b>
    /// </summary>
    public bool CanStart => Call.Lifecycle is not DepartmentLifecycle.None;

    /// <summary>
    /// 別枠のボタンの文言。<b>押す前に何が起きるか分かるようにする</b>（設計 §15-6）。
    /// </summary>
    public string LifecycleLabel => Call.Lifecycle switch
    {
        DepartmentLifecycle.Start => "起動",
        DepartmentLifecycle.Focus => "ターミナルを前面に出す",
        _ => string.Empty,
    };

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
    /// 期限までに報告を観測していない仕事の、最後に状態が動いた時刻（設計 §31）。
    /// <b>推定しない</b> —— 走査が計算して入れる。
    /// </summary>
    public DateTimeOffset? ReportNotObservedSince
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
    /// いま抱えている仕事の件名（設計 §44-4）。
    /// </summary>
    /// <remarks>
    /// <b>「この仕事を渡す」が、どの仕事なのか分からなかった</b>（人間が実機で見つけた）。
    /// slug（<c>task-20260912-193527-c55c</c>）は人間の言葉ではないので、
    /// **指示書の1行目**を出す。slug はツールチップに置く ——
    /// あちらは `.company/` のフォルダや作業ログと突き合わせるときに要る。
    /// </remarks>
    public string? TaskSubject
    {
        get;
        set
        {
            field = value;
            Raise();
            Raise(nameof(HasTaskSubject));
        }
    }

    public bool HasTaskSubject => !string.IsNullOrWhiteSpace(TaskSubject);

    /// <summary>
    /// その仕事を取り消せるか（設計 §44-5）。
    /// </summary>
    /// <remarks>
    /// <b>終端は取り消せない</b>（<see cref="TaskTransitions.IsTerminal"/>）——
    /// 終わったものを「取り消す」と言うと、**何が起きたのかが後から読めなくなる。**
    /// <para>
    /// <b>「消す」ではない。</b> 仕事も記録も <c>.company/</c> に残る（§16-4）——
    /// 消えるのは<b>人間の目の前から</b>である。
    /// </para>
    /// </remarks>
    public bool CanCancelTask =>
        Status.Work?.Value is { } work && !TaskTransitions.IsTerminal(work);

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
    public bool CanJudgeReport => Status.Work?.Value is CoreTaskStatus.Reported && UnderReviewBy is null;

    /// <summary>
    /// いまこの仕事を見ているレビュー（監査を含む）の部門名（設計 §62-17）。見ている間は受理・差し戻しを出さない。
    /// </summary>
    public string? UnderReviewBy
    {
        get;
        set
        {
            field = value;
            Raise();
            Raise(nameof(CanJudgeReport));
            Raise(nameof(UnderReviewText));
            Raise(nameof(IsUnderReview));
        }
    }

    public bool IsUnderReview => UnderReviewBy is not null;

    public string UnderReviewText => $"{UnderReviewBy} が見ている。判定が出たら計画が進める（受理・差し戻しはそのあと）";

    /// <summary>この部門を選んでいるか。</summary>
    public bool IsSelected
    {
        get;
        set
        {
            field = value;
            Raise();
            Raise(nameof(PoseSize));
        }
    }

    /// <summary>
    /// 絵の大きさ。<b>選んでいる部門だけ大きく出す</b>（設計 §52-5）。
    /// </summary>
    /// <remarks>
    /// 40px では見分けが優先で細部は潰れる（§15-9）。**一覧の並びは 40px のまま**にして、
    /// 選んだ1つだけ表情や小物が見える大きさにする。
    /// </remarks>
    public double PoseSize => IsSelected ? 96 : 40;

    private (DepartmentPose Pose, bool NeedsHuman)? _quipKey;
    private string _quip = string.Empty;

    /// <summary>
    /// 絵にマウスを載せたときの一言（設計 §52-3）。
    /// </summary>
    /// <remarks>
    /// <b>ポーズか人間の出番が変わったときだけ選び直す。</b> 通知は観測のたびに全プロパティへ飛ぶ（<c>RaiseAll</c>）ので、
    /// 読むたびに選ぶと、見ている間に言葉が入れ替わってちらつく。
    /// </remarks>
    public string Quip
    {
        get
        {
            // **人間の出番も鍵に入れる**（Codex の指摘）—— ポーズが同じまま質問が届くことがある。
            var key = (Call.Pose, Call.NeedsHuman);
            if (_quipKey != key)
            {
                _quipKey = key;
                _quip = Quips.Pick(key.Pose, key.NeedsHuman, Random.Shared.Next);
            }

            return _quip;
        }
    }

    /// <summary>ねぎらいの一言（設計 §52-4）。<b>出している間だけ</b>入る。</summary>
    public string CheerText { get; private set; } = string.Empty;

    public bool HasCheer => CheerText.Length > 0;

    /// <summary>ねぎらいに添える絵（設計 §52-4）。<b>状態のポーズは差し替えない</b>（§52-1）。</summary>
    public Bitmap? CheerImage => PoseImages.Bowing;

    private DispatcherTimer? _cheerTimer;

    /// <summary>
    /// 報告を受理したときに、少しだけねぎらう（設計 §52-4）。
    /// </summary>
    /// <remarks>
    /// <b>人間が受理した、という起きたことへの反応</b>なので、観測していないことを言っていない（§7）。
    /// <para><b>10秒出す</b>（人間の判断）—— 2.5 秒では、受理を押して目を戻す前に消えていた。</para>
    /// </remarks>
    public void Cheer()
    {
        _cheerTimer?.Stop();
        CheerText = Quips.Cheer;
        Raise(nameof(CheerText));
        Raise(nameof(HasCheer));

        _cheerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _cheerTimer.Tick += (_, _) =>
        {
            _cheerTimer?.Stop();
            _cheerTimer = null;
            CheerText = string.Empty;
            Raise(nameof(CheerText));
            Raise(nameof(HasCheer));
        };
        _cheerTimer.Start();
    }

    public string RuntimeText => Status.Runtime.Value.ToString();

    public string ActivityText => Status.Activity.Value is ActivityState.WorkingOrAwaitingApproval
        ? "作業中か承認待ち" : Status.Activity.Value.ToString();

    public string WorkText => Status.Work is null ? "—" : Status.Work.Value.ToString();

    /// <summary>
    /// ポーズの絵（設計 §15-2）。<b>(a) 承認まちと (b) 相談中を同じ絵にしない</b>（§3）——
    /// 人間の行き先が違う。
    /// </summary>
    /// <remarks>
    /// <b>6枚は「1枚のキャラクターシート」から切り出したもの</b>（§15-5）。
    /// 24px でも見分けが付くことを確かめてある —— 見分けているのは
    /// **色でも表情でもなく、体の外形**である（低い塊／斜め／横長 など）。
    /// </remarks>
    public Bitmap? PoseImage => PoseImages.Of(Call.Pose);

    /// <summary>右上のバッジ。人間の返事を待つ仕事があるときだけ（設計 §15-3）。</summary>
    public string BadgeGlyph => Call.Badge switch
    {
        DepartmentBadge.NeedsAnswer => "❓",
        DepartmentBadge.NeedsAcceptance => "📝",
        DepartmentBadge.NeedsDeliveryCheck => "📮",
        DepartmentBadge.ReportNotObservedByDeadline => "⏳",
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
    /// <summary>
    /// 活動状態の根拠。<b>分からないなら、なぜ分からないかを出す。</b>
    /// </summary>
    /// <remarks>
    /// 外部ターミナルの部門はフックを観測するまでは <c>Unknown</c>（設計 §61-1）。
    /// 観測後はイベントの固定文と時刻を表示する（設計 §61-4）。
    /// そこに初期化時の根拠（「状態検出器を初期化した」）を出すと、
    /// **観測が止まっているように見える** —— 実機で人間が引っかかった（2026-09-09）。
    /// <para>
    /// その説明の1行も**出さない**（2026-09-16、人間の要望）。今の部門は全部外部ターミナルなので、
    /// どのタイルにも同じ文が並ぶだけだった。<b>根拠を作り替えてはいない</b> —— 行を消すだけで、
    /// 構造化の部門や観測できた活動には、これまでどおり根拠を出す。
    /// </para>
    /// </remarks>
    public string EvidenceText =>
        // 案内は macOS の新規起動だけ。最初のフックで消す（設計 §61-5 / §61-8）。
        Mode is DriveMode.ExternalTerminal && Agent is AgentKind.CodexCli && SessionRunning
            && _tracker.AwaitingFirstLifecycleHook
            ? "Codex の窓に「Hooks need review」が出ていたら、中身（printf … MAAC_ACTIVITY_EVENTS）を確かめて信頼してください"
            // 起動前の「手が空いている」は根拠の行を出さない（どのタイルにも同じ文が並ぶだけ）。
            : (Mode is DriveMode.ExternalTerminal && Status.Activity.Value is ActivityState.Unknown) || _tracker.NotStartedYet
                ? string.Empty
                : Status.Activity.Evidence.Source is EvidenceSource.LifecycleHook
                    ? $"{Status.Activity.Evidence.RedactedSummary}（最後の観測 {Status.Activity.Evidence.ObservedAt.ToLocalTime():HH:mm}）"
                    : $"{Status.Activity.Evidence.Source} / {Status.Activity.Evidence.RedactedSummary}";

    public bool HasEvidenceText => EvidenceText.Length > 0;

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
        _observations.Insert(0, $"{evidence.ObservedAt.ToLocalTime():HH:mm:ss}  {evidence.Source}  {evidence.RedactedSummary}");
        if (_observations.Count > 30)
        {
            _observations.RemoveAt(_observations.Count - 1);
        }
    }

    /// <summary>
    /// 導出プロパティを全部読み直させる。
    /// </summary>
    /// <remarks>
    /// <b>名前の一覧を持たない。</b> <c>PropertyChanged</c> は
    /// **プロパティ名が空なら「全部変わった」**を意味する（<see cref="INotifyPropertyChanged"/> の約束）。
    /// <para>
    /// 以前はここに名前を並べていて、**新しい導出プロパティを足すたびに、
    /// ここへ足すのを忘れると黙って古い値が残った** ——
    /// 実際 2026-09-09 に `LifecycleLabel` を足したとき、
    /// **ボタンは出るのに文字だけ空**になった（実機で人間が見つけた）。
    /// </para>
    /// <b>一覧は「覚えていないと壊れる」形</b>なので、持たないことにした。
    /// タイルは数個で、読み直しも文字列を組み立てるだけである。
    /// </remarks>
    private void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

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
    /// 「ターミナルで開く」を出すか（設計 §54）。<b>未 trust と分かっているか、記録がまだ無いとき。</b>
    /// </summary>
    /// <remarks>
    /// 「判定できない」（壊れていて読めない）には出さない —— 未 trust とは限らないのに、操作を促すことになる（§13-9）。
    /// 記録がまだ無いなら、一度起動すれば分かる（§55-5）。
    /// </remarks>
    public bool CanOpenTerminal => ExecutablePath is not null && State is WorkspaceTrustState.NotTrusted or WorkspaceTrustState.NoRecord;

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
            WorkspaceTrustState.NoRecord => "まだ記録が無い",
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
            WorkspaceTrustState.NotTrusted => "その CLI をこのフォルダで一度起動して信頼を与える（戻ってくると表示が更新される）",
            WorkspaceTrustState.NoRecord => "その CLI をこのフォルダで一度起動すれば分かる。信頼を聞かれたら与える（戻ってくると表示が更新される）",
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
        RecoveryKind.Configuration => "部門の設定",
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

    /// <summary>
    /// 部門の設定の話（設計 §32-10）。<b>直すのは人間</b>なので、出すのは「開く」だけ。
    /// </summary>
    public bool IsConfiguration => Kind is RecoveryKind.Configuration;

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
    /// 部門の設定が、そのままでは動かない組み合わせ（設計 §32-10）。
    /// <b>壊れてはいないので開ける。</b> 直すのは人間で、アプリは書き換えない。
    /// </summary>
    Configuration,

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
    public string ProblemText => Proposal.PlanProblem is { } problem
        ? $"工程の順が規則に合わない: {problem}。秘書に並べ直してもらうか、やめる"
        : string.IsNullOrWhiteSpace(Body)
        ? "本文が空なので仕事にできない。秘書に書き直してもらうか、やめる"
        : "宛先が分からないので仕事にできない。秘書に部門を聞き直すか、やめる";
}

/// <summary>
/// 左ペインに並ぶ相談スレッド1件（設計 §32-6）。
/// </summary>
public sealed class ThreadItem(string id, string title, DateTimeOffset updatedAt) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; } = id;

    public string Title { get; } = title;

    public DateTimeOffset UpdatedAt { get; } = updatedAt;

    /// <summary>一覧に出す時刻。<b>秒までは出さない</b> —— 一覧で読むものではない。</summary>
    public string UpdatedText => UpdatedAt.ToLocalTime().ToString("MM/dd HH:mm");

    public bool IsSelected
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}

/// <summary>CLI ごとの残量表示。<b>失敗の根拠もそのまま出す</b>（設計 §7 / §50）。</summary>
public sealed class AgentUsageRow(AgentKind kind, string name) : INotifyPropertyChanged
{
    public AgentKind Kind { get; } = kind;
    public string Name { get; } = name;
    public string Text { get; private set; } = string.Empty;
    public string RawOutput { get; private set; } = string.Empty;
    public bool HasRawOutput => RawOutput.Length > 0;
    public DateTimeOffset? ObservedAt { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void BeginRead()
    {
        Text = "取得中…";
        RawOutput = string.Empty;
        ObservedAt = null;
        Notify();
    }

    public void Show(AgentUsageResult result)
    {
        RawOutput = result is AgentUsageResult.Unreadable unreadable ? unreadable.RawOutput : string.Empty;
        ObservedAt = result is AgentUsageResult.Available available ? available.ObservedAt : null;
        Text = result switch
        {
            AgentUsageResult.Available value => string.Join(Environment.NewLine, value.Windows.Select(Format)),
            AgentUsageResult.NotInstalled => "見つからない（インストールされていない）",
            AgentUsageResult.Unreadable value => $"読み取れなかった: {value.Reason}",
            _ => string.Empty,
        };
        Notify();
    }

    private static string Format(AgentUsageWindow window)
    {
        var name = window.Group is null ? window.Name : $"{window.Group} / {window.Name}";
        var reset = window.ResetText ?? window.ResetsAt?.ToLocalTime().ToString("M/d HH:mm");
        return $"{name}　残り {Math.Round(window.RemainingPercent):0}%"
            + (reset is null ? string.Empty : $"　リセット {reset}");
    }

    private void Notify()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RawOutput)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasRawOutput)));
    }
}
