using System.Collections.ObjectModel;
using MultiAIAgentCompany.Core.Status;
using MultiAIAgentCompany.Core.Workspace;
using MultiAIAgentCompany.Core.Workspace.Trust;
using MultiAIAgentCompany.Core.Coordination;
using MultiAIAgentCompany.Core.Agents;
using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Desktop;

/// <summary>
/// 部門定義から画面を組み立てる。<b>状態は検出器（Core）が決め、ここは繋ぐだけ</b>（設計 §4）。
/// </summary>
public sealed class ShellComposer
{
    /// <summary>根拠がこれより古くなったら活動状態は Unknown へ落ちる（設計 §7）。</summary>
    private static readonly TimeSpan EvidenceMaxAge = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, DepartmentStatusTracker> _trackers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DepartmentDefinition> _definitions = new(StringComparer.Ordinal);

    private readonly TimeProvider _clock;

    public ShellComposer(IReadOnlyList<DepartmentDefinition> departments, TimeProvider clock)
    {
        _clock = clock;
        Approvals = new ApprovalQueue();
        Shell = new ShellViewModel
        {
            Approvals = Approvals,
            WorkLog = ["まだ何も動かしていない"],
            SecretaryTranscript = ["秘書はまだ起動していない"],
            Departments = [],
        };

        Rebuild(departments);
    }

    /// <summary>
    /// 部門の一式を作り直す（設計 §15-8 / §30-4）。
    /// </summary>
    /// <remarks>
    /// <b>部門はワークスペースごとに違う。</b> <c>.company/departments.json</c> は
    /// 人間が編集できる（§15-8）ので、フォルダを開くたびに読み直す。
    /// <para>
    /// <b>前のフォルダの部門は、まだ動いている。</b> 切り替えは「選ぶ」が先で
    /// 「前の部門を止める」が後（§26-2b）—— 開けなかったときに前のフォルダの秘書と部門だけ
    /// 死んでいる状態を作らないため。だから作り直したタイルへ、前のフォルダのイベントが
    /// 届き得る。塞いでいるのは <c>DepartmentRunner.StillOurs</c> の側。
    /// <b>ここで <see cref="Workspace"/> を先に差し替えてあることが、その判定の前提になる。</b>
    /// </para>
    /// </remarks>
    private void Rebuild(IReadOnlyList<DepartmentDefinition> departments)
    {
        _trackers.Clear();
        _definitions.Clear();
        Shell.Departments.Clear();

        foreach (var department in departments)
        {
            var tracker = new DepartmentStatusTracker(
                new AgentRef(department.Id, department.Agent), _clock, EvidenceMaxAge);
            _trackers[department.Id] = tracker;
            _definitions[department.Id] = department;
            Shell.Departments.Add(new DepartmentTile(
                department.Id, department.DisplayName, department.Agent, department.Mode, tracker));
        }
    }

    public ShellViewModel Shell { get; }

    /// <summary>(a) ランタイム承認の待ち行列。セッションの ApprovalRequested をここへ流す。</summary>
    public ApprovalQueue Approvals { get; }

    /// <summary>部門の検出器。セッションのイベントをここへ流し込む。</summary>
    public DepartmentStatusTracker TrackerOf(string departmentId) => _trackers[departmentId];

    public IReadOnlyCollection<string> DepartmentIds => _trackers.Keys;

    public DepartmentDefinition DefinitionOf(string departmentId) => _definitions[departmentId];

    /// <summary>その部門をこのワークスペースが持っているか（設計 §17-6）。</summary>
    /// <remarks><b>知らない部門の提案を捨てない</b>ので、呼び出し元が見分けられるようにする。</remarks>
    public bool KnowsDepartment(string departmentId) => _definitions.ContainsKey(departmentId);

    /// <summary>選ばれたワークスペース。まだ選ばれていなければ null。</summary>
    public WorkspaceRef? Workspace { get; private set; }

    public TaskStore? Tasks { get; private set; }

    public LeaseStore? Leases { get; private set; }

    public TaskDispatcher? Dispatcher { get; private set; }

    public CompanyScanner? Scanner { get; private set; }

    /// <summary>相談スレッドの保存口（設計 §32-6）。</summary>
    public ThreadStore? Threads { get; private set; }

    public SecretaryOutbox? Outbox { get; private set; }

    /// <summary>計画の保存口（設計 §37-2）。</summary>
    public PlanStore? Plans { get; private set; }

    /// <summary>計画を1回に1つ進める（設計 §37）。</summary>
    public PlanRunner? PlanRunner { get; private set; }

    /// <summary>
    /// 起動時の走査で「送ったかもしれない」と分かった仕事（設計 §14-1）。
    /// <b>通常の <c>Dispatched</c> と区別する</b> —— ボタンが出るのはこちらだけ。
    /// </summary>
    private readonly HashSet<string> _acrossRestart = new(StringComparer.Ordinal);

    /// <summary>もう作業ログに書いた沈黙（設計 §31）。<b>毎回の走査で書き直さない。</b></summary>
    private readonly HashSet<string> _noticedSilence = new(StringComparer.Ordinal);

    /// <summary>まだ人間に見せていない沈黙の1行。<see cref="DrainSilenceNotices"/> で取り出す。</summary>
    private readonly List<string> _pendingSilenceNotices = [];

    /// <summary>
    /// フォルダが選ばれたときに、各 CLI の trust を読み直す（設計 §13-9）。
    /// <b>ディスクへ書かない</b>（§21-1）—— 起動時に前回のフォルダを開くので、
    /// 「選ぶ」が書き込みを含むと、人間が今回まだ何も選んでいないのに書くことになる。
    /// protocol の正本（§17-6）は <see cref="WriteSecretaryProtocolAsync"/> で、
    /// 秘書を起動する直前に置く。
    /// </summary>
    public async Task<bool> SelectWorkspaceAsync(string root, CancellationToken ct)
    {
        var workspace = new WorkspaceRef(root);
        var rows = await WorkspaceTrustReport.BuildAsync(workspace,
            [new ClaudeCodeTrustProbe(), new CodexCliTrustProbe(), new AntigravityTrustProbe()],
            ct);

        // **部門はワークスペースごとに違う**（設計 §15-8）。ここまで読んでいなかったので、
        // `departments.json` を編集しても効かなかった（レビューで発覚、2026-09-08）。
        // **読めなかったら開かない** —— 既定に落とすと、人間が書いた設定を
        // 黙って無視したまま動く（§7）。
        var departments = await ReadDepartmentsAsync(workspace, ct);

        Workspace = workspace;
        var paths = workspace.Company;

        // **同じ顔ぶれなら作り直さない**（レビューで発覚、2026-09-08）。
        // 同じフォルダを開き直したときも通るので、無条件に作り直すと
        // **動いているセッションが古い検出器に繋がったまま、画面のタイルだけが新品になる** ——
        // 稼働も承認も届かないのに「起動できる」ように見える。
        var rebuilt = !_definitions.Values.SequenceEqual(departments);
        if (rebuilt)
        {
            Rebuild(departments);
        }
        // **ワークスペースが変わったら、沈黙の記憶も捨てる**（設計 §31-6）。
        // **Rebuild に置かない** —— あれは顔ぶれが変わったときしか呼ばれないので、
        // 同じ部門構成の別フォルダへ切り替えると、前のフォルダの slug を
        // 「もう知らせた」と覚えたままになる（1回目の実装で指摘された）。
        _noticedSilence.Clear();
        _pendingSilenceNotices.Clear();
        Tasks = new TaskStore(paths, _clock);
        Leases = new LeaseStore(paths, _clock);
        Threads = new ThreadStore(paths, _clock);
        Dispatcher = new TaskDispatcher(paths, Tasks, Leases, _clock);
        Scanner = new CompanyScanner(paths, Tasks, Leases, _clock);
        Outbox = new SecretaryOutbox(paths);
        Plans = new PlanStore(paths, _clock);
        PlanRunner = new PlanRunner(paths, Plans, Tasks, Dispatcher, _clock);

        Shell.WorkspaceLabel = root;

        // **開いたことは、開いた側が入れる**（設計 §28-2、レビューで発覚）。
        Shell.HasWorkspace = true;
        Shell.Trust.Clear();
        foreach (var row in rows)
        {
            // **CLI があるかどうかも、ここで一緒に見る**（設計 §28-1）。
            // 無いものに trust を与えろと言っても始まらない。
            Shell.Trust.Add(new TrustRow(row.Agent, row.State, AgentExecutable.Find(row.Agent)));
        }

        // **部門の顔ぶれが変わったことを、呼び出し元に伝える。**
        // 変わったなら動いているセッションを止める必要がある（古い検出器に繋がっているので）。
        return rebuilt;
    }

    /// <summary>
    /// そのワークスペースの部門定義を読む。無ければ既定を書き下ろす（設計 §15-8）。
    /// </summary>
    /// <remarks>
    /// <b>読めなかったら例外にする。</b> 既定へ落とすと、人間が書いた設定を無視したまま
    /// 動き続ける —— 特に危険モード（§30-4）が「設定したのに効かない」形になる。
    /// 呼び出し元は開くのをやめて、理由を人間に出すこと（§21-1 の「開かずに聞く」）。
    /// </remarks>
    private async Task<IReadOnlyList<DepartmentDefinition>> ReadDepartmentsAsync(
        WorkspaceRef workspace, CancellationToken ct)
    {
        var store = new DepartmentStore(workspace.Company);
        switch (await store.ReadAsync(ct))
        {
            case DefinitionReadResult.Found found:
                return found.Definition.Departments;

            case DefinitionReadResult.Unreadable broken:
                throw new InvalidOperationException($"departments.json を読めません: {broken.Reason}");

            default:
                // **初回は書き下ろす。** 人間が編集する場所なので、
                // 「どこを編集すればよいか」がファイルとして在ることに意味がある（§15-8）。
                var defaults = DepartmentStore.CreateDefaultDepartments();
                if (await store.SaveAsync(new CompanyDefinition(0, []), defaults, ct)
                    is DefinitionWriteResult.Rejected rejected)
                {
                    throw new InvalidOperationException($"departments.json を作れません: {rejected.Reason}");
                }

                return defaults;
        }
    }

    /// <summary>
    /// `.company/` を1周見て、仕事状態をタイルへ反映する（設計 §16-1）。
    /// </summary>
    /// <remarks>
    /// <b>走査が正本。</b> セッションのイベントから仕事状態を作らない（§7）。
    /// </remarks>
    public async Task<CompanyScanResult?> ScanAsync(CompanyScanKind kind, CancellationToken ct)
    {
        if (Scanner is null || Tasks is null)
        {
            return null;
        }

        var result = await Scanner.SyncAsync(kind, ct);

        // **アプリが知っていることを、それを使う側より先に埋める**（レビューで発覚、2026-09-08）。
        // ここが `PushWorkStatesAsync` の後ろにあったので、起動時の1周だけ
        // `_acrossRestart` が空のまま読まれていた。実害は2つ:
        //   - 「まだ届いたか分からない」仕事に「報告を観測していない」と作業ログが出る（§31-3）。
        //     **タイルのバッジは NeedsDeliveryCheck が勝つので、画面と食い違う**
        //   - `UrgencyOf` の「再起動を跨いだ Dispatched」が起動時の1周だけ効かない
        if (kind is CompanyScanKind.Startup)
        {
            // §16-3: 部門が分かるものはタイルにも出す。左ペインだけだと右を見ている人が拾えない。
            _acrossRestart.Clear();
            foreach (var task in result.Dispatched)
            {
                _acrossRestart.Add(task.Slug);
            }

            var acrossRestart = result.Dispatched.Select(task => task.DepartmentId).ToHashSet(StringComparer.Ordinal);
            foreach (var tile in Shell.Departments)
            {
                tile.DispatchedAcrossRestart = acrossRestart.Contains(tile.Id);
            }
        }

        await PushWorkStatesAsync(ct);
        RefreshProposals();

        // ここから下は**人間に見せる側**。上で埋めた事実を使う。
        if (kind is CompanyScanKind.Startup)
        {
            Shell.Recovery.Clear();

            foreach (var task in result.Dispatched)
            {
                Shell.Recovery.Add(new RecoveryItem(RecoveryKind.MaybeSent, task.Slug,
                    $"{task.DepartmentId} へ送ったかもしれない。届いたか確かめる（自動で再送しない）")
                {
                    DepartmentId = task.DepartmentId,
                });
            }

            if (result.UnreadableLease is { } leaseReason)
            {
                // **仕事に紐づかない**（設計 §23-1）。ワークスペース全体の書き込み権の話。
                Shell.Recovery.Add(new RecoveryItem(
                    RecoveryKind.UnreadableLease, ".company/lease.json",
                    $"{leaseReason}。**この間はどの部門にも仕事を渡せない**"));
            }

            if (result.ExpiredWriteLease is { } expired)
            {
                NoteExpiredWriteLease(expired);
            }

            foreach (var broken in result.Unreadable)
            {
                // 部門に紐づけない —— state.json が読めないなら departmentId も信用できない。
                Shell.Recovery.Add(new RecoveryItem(RecoveryKind.Unreadable, broken.Slug, broken.Reason));
            }

            await AddConfigurationWarningsAsync(ct);
        }

        return result;
    }

    /// <summary>
    /// 各部門の「いま人間の出番に近い仕事」をタイルへ入れる。
    /// </summary>
    /// <remarks>
    /// 1部門が複数の仕事を持ち得るので、<b>いちばん急ぐものを選ぶ</b>
    /// （§15-6 のボタンが1つであるのと同じ理由）。
    /// </remarks>
    private async Task PushWorkStatesAsync(CancellationToken ct)
    {
        if (Tasks is null)
        {
            return;
        }

        var chosen = new Dictionary<string, TaskState>(StringComparer.Ordinal);
        foreach (var slug in await Tasks.ListSlugsAsync(ct))
        {
            if (await Tasks.ReadAsync(slug, ct) is not TaskReadResult.Found found)
            {
                continue;
            }

            var state = found.State;
            if (!chosen.TryGetValue(state.DepartmentId, out var current)
                || UrgencyOf(state) > UrgencyOf(current))
            {
                chosen[state.DepartmentId] = state;
            }
        }

        // 走査に出てこなかった部門の仕事状態を残さない。
        // 残すと、別のワークスペースに切り替えたあとも古い報告まちを指し続ける。
        foreach (var (departmentId, tracker) in _trackers)
        {
            if (chosen.ContainsKey(departmentId))
            {
                continue;
            }

            tracker.OnWorkStateCleared();
            foreach (var tile in Shell.Departments.Where(t => t.Id == departmentId))
            {
                tile.CurrentTaskSlug = null;
                tile.ReportNotObservedSince = null;
            }
        }

        foreach (var (departmentId, state) in chosen)
        {
            if (!_trackers.TryGetValue(departmentId, out var tracker))
            {
                continue;
            }

            // 仕事状態は Document 根拠でしか動かない（§7 の信頼順）。
            tracker.OnWorkStateChanged(state.Status, new Evidence(
                EvidenceSource.Document, _clock.GetUtcNow(), null, null,
                new AgentRef(departmentId, DefinitionOf(departmentId).Agent), null, null,
                $"{state.Slug}: {state.Status}（試行 {state.AttemptId}）"));

            var deadline = DefinitionOf(departmentId).ReportDeadline;
            var silence = ReportWatch.Of(state, deadline, _clock.GetUtcNow());

            // **まず「送られたか」を確かめる**（設計 §31-3、レビューで発覚）。
            // 再起動を跨いだ `Dispatched` はバッジが `NeedsDeliveryCheck` になるので、
            // 作業ログだけ沈黙の話を出すと**画面と食い違う**。
            // **判定はバッジと同じ述語**にする —— 片方だけ直すと、また食い違う。
            var maybeSent = state.Status is CoreTaskStatus.Dispatched && _acrossRestart.Contains(state.Slug);

            if (silence is null)
            {
                // 差し戻して送り直したあと、また気付けるようにする（§31-6）。
                _noticedSilence.Remove(state.Slug);
            }
            else if (!maybeSent && _noticedSilence.Add(state.Slug))
            {
                // **覚えもしない**（上の `!maybeSent`）—— 人間が配達を確かめて
                // `InProgress` へ進めたあと、そこから黙ったままなら、そのとき初めて知らせる。
                _pendingSilenceNotices.Add(
                    $"{silence.Slug}: 期限（{deadline.GetValueOrDefault().TotalMinutes}分）までに報告を観測していない（{Math.Round(silence.Elapsed.TotalMinutes)}分）。**部門は生きているかもしれない** —— 失敗とは書かない");
            }

            foreach (var tile in Shell.Departments.Where(t => t.Id == departmentId))
            {
                tile.CurrentTaskSlug = state.Slug;
                tile.ReportNotObservedSince = silence?.Since;
            }
        }
    }

    /// <summary>
    /// まだ人間に見せていない沈黙の通知を取り出す（設計 §31）。
    /// <b>取り出したら消える</b> —— 走査のたびに同じ行を積み直さない。
    /// </summary>
    public IReadOnlyList<string> DrainSilenceNotices()
    {
        var notices = _pendingSilenceNotices.ToArray();
        _pendingSilenceNotices.Clear();
        return notices;
    }

    /// <summary>
    /// 未処理の提案を読み直す（設計 §17-6）。<b>outbox が正本</b>なので、
    /// アプリ側に処理済みの印を持たない。
    /// </summary>
    private void RefreshProposals()
    {
        if (Outbox is null)
        {
            return;
        }

        Shell.Proposals.Clear();
        foreach (var proposal in Outbox.Read())
        {
            // 知らない部門でも捨てない。人間に見せて判断させる（§17-6）。
            var known = proposal.DepartmentId is { } id && _definitions.TryGetValue(id, out var definition);
            var label = proposal.DepartmentId is null
                ? "宛先が書かれていない"
                : known
                    ? _definitions[proposal.DepartmentId].DisplayName
                    : $"知らない部門: {proposal.DepartmentId}";

            // 本文が空の提案を「仕事にできる」にしない —— 仕事を作ってから
            // 指示書を作れずに落ちる（レビューで発覚）。
            Shell.Proposals.Add(new ProposalCard(
                proposal, label, known && !string.IsNullOrWhiteSpace(proposal.Body)));
        }
    }

    /// <summary>
    /// 1部門が複数の仕事を持つとき、どれをタイルに出すか。
    /// <b>ボタンの優先順位（§15-6）と揃える</b> —— ずれると、用件のある仕事が選ばれない。
    /// </summary>
    /// <remarks>
    /// <b>§15-6 の段と同じ順にする。</b> 用件が出る仕事が、用件の出ない仕事に隠れてはいけない
    /// （<c>Rejected</c> で一度踏んでいる。下のコメント参照）。
    /// </remarks>
    private int UrgencyOf(TaskState state) => state.Status switch
    {
        CoreTaskStatus.AwaitingAnswer => 7,
        CoreTaskStatus.Reported => 6,

        // **再起動を跨いだ Dispatched だけが用件になる**（§14-1）。
        // 通常の Dispatched はボタンを出さないので、Drafted より下に置く ——
        // 上に置くと、渡していない仕事がまた選ばれなくなる。
        CoreTaskStatus.Dispatched when _acrossRestart.Contains(state.Slug) => 5,

        // **黙って返ってこない仕事も用件になる**（設計 §31）。
        // ここを 1 のままにすると、**同じ部門に下書きが1つあるだけで永久に隠れる** ——
        // ⏳ のバッジも作業ログの1行も出ない。§31 が拾おうとしている仕事そのものが
        // 拾えなくなるので、`Drafted` より上に置く（§15-6 の 5b と同じ位置）。
        CoreTaskStatus.Dispatched or CoreTaskStatus.InProgress when IsSilent(state) => 4,

        CoreTaskStatus.Drafted => 3,

        // 差し戻したまま止まっている仕事（§19-1）。**0 のままにしない** ——
        // 用件（RedispatchTask）が出るのに、同じ部門の InProgress に選ばれて
        // 永久に隠れる。§15-6 の 6b と同じ位置。
        CoreTaskStatus.Rejected => 2,
        CoreTaskStatus.Dispatched => 1,
        CoreTaskStatus.InProgress => 1,
        _ => 0,
    };

    /// <summary>期限までに報告を観測していないか（設計 §31-2）。</summary>
    /// <remarks>
    /// <b>計算は <see cref="ReportWatch"/> ひとつに閉じる。</b> ここと
    /// <see cref="PushWorkStatesAsync"/> で別々に書くと、
    /// 「選ばれたのにバッジが出ない」「バッジは出たのに選ばれない」がすれ違って起きる。
    /// <para>
    /// <b>知らない部門は false。</b> ここは<b>走査に出てきた全部の仕事</b>に当たるので、
    /// <c>departments.json</c> から消された部門を指す <c>state.json</c> が1つあるだけで
    /// 走査ごと落ちる —— 下の選別ループは <c>_trackers</c> で弾いてから
    /// <see cref="DefinitionOf"/> を引いているが、こちらはその前に来る。
    /// </para>
    /// </remarks>
    private bool IsSilent(TaskState state) =>
        _definitions.TryGetValue(state.DepartmentId, out var department)
        && ReportWatch.Of(state, department.ReportDeadline, _clock.GetUtcNow()) is not null;

    /// <summary>
    /// 失効した書き込み権を未解決項目に出す（設計 §24-2）。
    /// </summary>
    /// <remarks>
    /// <b>起動時の走査と、dispatch で弾かれたときの両方から呼ぶ。</b>
    /// ワークスペースを開いたあとに失効した場合、起動時の走査は二度と回らない ——
    /// **「左の一覧から外して」と言いながら、その一覧に項目が無い**ことになる（レビューで発覚）。
    /// <para>
    /// <b>作る場所を1つにする。</b> 二重の復旧 UI も、文言のずれも作らない（§16-4）。
    /// </para>
    /// </remarks>
    public void NoteExpiredWriteLease(LeaseHolder expired)
    {
        ArgumentNullException.ThrowIfNull(expired);

        if (Shell.Recovery.Any(item => item.Kind is RecoveryKind.ExpiredLease))
        {
            return;
        }

        // **アプリが言えることだけを言う**（設計 §24-2 / §9）。
        // **lease が切れてもセッションは死なない**（レビューで発覚）——
        // 30分の lease が切れただけで動き続けている部門はふつうにある。
        // 「セッションを持っていない」と無条件に言うと、**動いている相手の権利を
        // 外させる嘘の安心**になる。分からないときは言わない。
        // **部門の保持者のときだけ部門を引く**（§17-2）。秘書と部門は `Kind` で区別されるので、
        // ID だけで引くと、`secretary` という ID の部門があったとき別のプロセスの話をする。
        var session = expired.Holder.Kind is ActorKind.Department
            ? Shell.Departments.FirstOrDefault(tile => tile.Id == expired.Holder.Id)?.SessionRunning
            : null;
        var about = session switch
        {
            true => "**このアプリはその部門のセッションをまだ持っている**（外すと動いている相手の権利を消す）",
            false => "このアプリはその部門のセッションを持っていないが、別のアプリや手で起動した CLI までは分からない",
            null => "その保持者がいま動いているかは、このアプリからは分からない",
        };

        Shell.Recovery.Add(new RecoveryItem(
            RecoveryKind.ExpiredLease, ".company/lease.json",
            $"{expired.Holder.Id} が {expired.AcquiredAt.ToLocalTime():MM/dd HH:mm} に取り、"
            + $"{expired.ExpiresAt.ToLocalTime():MM/dd HH:mm} に失効（仕事: {expired.TaskSlug}）。"
            + $"**待っても空かない。** {about}")
        {
            Lease = expired,
        });
    }

    /// <summary>
    /// 秘書の protocol の正本を置く（設計 §17-6）。
    /// <b>秘書へ1通目を送るより前に呼ぶこと</b> —— 1通目は「これを読んで」だけなので、
    /// 順序が逆になると読ませる先が無い。
    /// </summary>
    public Task WriteSecretaryProtocolAsync(CancellationToken ct) =>
        Workspace is { } workspace
            ? SecretaryReadme.WriteAsync(workspace.Company,
                [.. _definitions.Values.Select(d =>
                    $"- `{d.Id}` … {d.DisplayName}（{d.Responsibility}）")],
                ct)
            : Task.CompletedTask;

    /// <summary>
    /// 部門の protocol の正本を置く（設計 §32-5）。
    /// </summary>
    /// <remarks>
    /// <b>窓を開くより前に呼ぶこと。</b> 起動時に渡すのは「これを読んで」だけなので、
    /// 順序が逆になると読ませる先が無い（秘書の README と同じ理由）。
    /// </remarks>
    public Task WriteDepartmentProtocolAsync(CancellationToken ct) =>
        Workspace is { } workspace
            ? DepartmentReadme.WriteAsync(workspace.Company, ct)
            : Task.CompletedTask;

    /// <summary>
    /// 部門定義について、人間に伝えるべきことを出す（設計 §32-10）。
    /// </summary>
    /// <remarks>
    /// <b>2つとも、版番号では見つけられない。</b> 中身を見ないと分からないし、
    /// 版が同じでも起きる（人間は手で書き換える）——
    /// §23-4 の「<c>schemaVersion</c> を持つか」は、これで閉じられる。
    /// <para>
    /// <b>アプリは直さない。</b> `departments.json` は人間の持ち物である（§30-4 / §30-6）。
    /// </para>
    /// </remarks>
    private async Task AddConfigurationWarningsAsync(CancellationToken ct)
    {
        // ① 動かない組み合わせ（§30-1 の実測）。
        foreach (var warning in DepartmentWarnings.For([.. _definitions.Values]))
        {
            Shell.Recovery.Add(
                new RecoveryItem(RecoveryKind.Configuration, "departments.json", warning.Message)
                {
                    DepartmentId = warning.DepartmentId,
                });
        }

        // ② アプリが読まなかったキー。**黙って捨てない**（§30-6 の裏返し）。
        if (Workspace is not { } workspace)
        {
            return;
        }

        var unread = await new DepartmentStore(workspace.Company).FindUnreadKeysAsync(ct);
        if (unread.Count > 0)
        {
            Shell.Recovery.Add(new RecoveryItem(
                RecoveryKind.Configuration, "departments.json",
                $"`departments.json` に、**このアプリが読まなかったキー**があります: "
                + $"{string.Join(", ", unread)}。"
                + "書いても効きません（廃止されたか、綴りが違います）"));
        }
    }

    /// <summary>
    /// 相談スレッドの一覧を読み直す（設計 §32-6）。
    /// </summary>
    /// <returns>読めなかったスレッドの数。<b>黙って捨てない</b>（§25-2）。</returns>
    public async Task<int> RefreshThreadsAsync(CancellationToken ct)
    {
        if (Threads is null)
        {
            Shell.Threads.Clear();
            return 0;
        }

        var listed = await Threads.ListAsync(ct);
        Shell.Threads.Clear();
        foreach (var meta in listed.Threads)
        {
            Shell.Threads.Add(new ThreadItem(meta.Id, meta.Title, meta.UpdatedAt)
            {
                IsSelected = string.Equals(meta.Id, Shell.CurrentThreadId, StringComparison.Ordinal),
            });
        }

        // 選んでいたスレッドが消えていたら、選択も外す。
        if (Shell.CurrentThreadId is { } current && Shell.Threads.All(t => t.Id != current))
        {
            Shell.CurrentThreadId = null;
        }

        return listed.Unreadable;
    }

    public static ShellComposer CreateDefault(TimeProvider clock) =>
        new(Core.Workspace.DepartmentStore.CreateDefaultDepartments(), clock);
}
