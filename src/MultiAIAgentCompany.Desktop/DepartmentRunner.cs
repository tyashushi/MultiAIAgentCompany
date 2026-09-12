using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Agents.Antigravity;
using MultiAIAgentCompany.Core.Agents.ClaudeCode;
using MultiAIAgentCompany.Core.Agents.CodexCli;
using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Terminal;
using MultiAIAgentCompany.Core.Status;
using MultiAIAgentCompany.Core.Workspace;

namespace MultiAIAgentCompany.Desktop;

/// <summary>
/// 部門の実セッションを起動し、観測を検出器と承認キューへ流す。
/// </summary>
/// <remarks>
/// <b>ここに判定を書かない。</b> 状態を決めるのは <see cref="DepartmentStatusTracker"/>（§7）、
/// 承認の語彙を決めるのは CLI（§5）。この型がするのは配線だけ。
/// <para>
/// 終了は §9 の契約に従う —— ウィンドウを閉じたら全部門を終了する。
/// </para>
/// </remarks>
public sealed class DepartmentRunner(ShellComposer composer) : IAsyncDisposable
{
    /// <summary>
    /// 動いているセッション。<b>必ず <c>_startGate</c> の下で触る</b>（レビューで発覚）——
    /// 起動の完了はスレッドプール、読み出しは UI スレッドなので、素の Dictionary では壊れる。
    /// </summary>
    private readonly Dictionary<string, IAgentSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>外部ターミナルを開く道具（設計 §32）。<b>macOS 版だけがある</b>（§32-7）。</summary>
    private readonly ITerminalLauncher _terminals = TerminalLaunchers.ForCurrentOs();

    /// <summary>
    /// 「いまの世代」（設計 §26-1、§17-7 と同じ形）。
    /// </summary>
    /// <remarks>
    /// <b>起動中の停止を成立させるためにある。</b> 止めるときに <c>_sessions</c> しか見ないと、
    /// **走っている起動を取りこぼす** —— そのあと起動が完了して、前のフォルダで動く部門が残る
    /// （レビューで発覚）。
    /// </remarks>
    private int _generation;

    /// <summary>走っている起動。<b>停止はこれを待つ。</b></summary>
    private readonly List<Task> _starting = [];

    /// <summary>起動処理中の部門。<b>起動が終わるまで `_sessions` には入らない。</b></summary>
    private readonly HashSet<string> _startingIds = new(StringComparer.Ordinal);

    /// <summary>
    /// 上の2つを守る錠（設計 §26-1）。
    /// </summary>
    /// <remarks>
    /// 起動は UI スレッド以外からも終わるので、素の <c>List</c> / <c>HashSet</c> を
    /// そのまま触ると壊れる（レビューで発覚）。
    /// </remarks>
    private readonly object _startGate = new();

    /// <summary>
    /// 部門を起動する。既に動いていれば何もしない。
    /// </summary>
    /// <returns>起動の結末（設計 §26-1）。</returns>
    public async Task<DepartmentStart> StartAsync(
        DepartmentDefinition department, WorkspaceRef workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(department);
        ArgumentNullException.ThrowIfNull(workspace);

        // **起動処理中も見る**（レビューで発覚）。`_sessions` は起動が終わってから入るので、
        // 二度押しすると**同じ部門の CLI が2つ立ち**、片方は参照を失って孤児になる。
        lock (_startGate)
        {
            if (_sessions.ContainsKey(department.Id))
            {
                return new DepartmentStart.AlreadyRunning();
            }

            if (!_startingIds.Add(department.Id))
            {
                return new DepartmentStart.AlreadyStarting();
            }
        }

        var tracker = composer.TrackerOf(department.Id);
        tracker.OnStarting();

        // 前の観測は、この起動の話ではない（§27-3）。
        ForgetModel(department.Id);

        // **「走っている」印を、起動を始める前に立てる**（レビューで発覚）。
        // タスクを作ってから登録するまでの隙間で停止が走ると、
        // **待つべき起動が待たれない**まま前のフォルダのロックが返る。
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int generation;
        lock (_startGate)
        {
            generation = _generation;
            _starting.Add(pending.Task);
        }

        try
        {
            return await StartCoreAsync(department, workspace, tracker, generation, ct);
        }
        finally
        {
            lock (_startGate)
            {
                _starting.Remove(pending.Task);
                _startingIds.Remove(department.Id);
            }

            pending.TrySetResult();
        }
    }

    private async Task<DepartmentStart> StartCoreAsync(
        DepartmentDefinition department, WorkspaceRef workspace, DepartmentStatusTracker tracker,
        int generation, CancellationToken ct)
    {
        try
        {
            var adapter = AdapterFor(department);
            var session = await adapter.StartAsync(
                workspace, department.Id, department.Mode, ct);

            // **起動している間に切り替えられていたら、これは前のフォルダの部門**（§26-1）。
            // ここで登録すると、画面は新しいフォルダなのに CLI は前を触ったまま残る。
            if (generation != _generation)
            {
                await session.DisposeAsync();
                tracker.OnExited(0);

                // **成功として返さない**（レビューで発覚）。null を返すと呼び出し元が
                // 「起動した」と記録し、そのあと走査まで走る —— 切り替えの最中に
                // 前のフォルダを触りに行く。
                return new DepartmentStart.Superseded();
            }

            lock (_startGate)
            {
                _sessions[department.Id] = session;
            }

            Wire(department, workspace, session, tracker);
            PublishModel(department.Id, session);
            return new DepartmentStart.Started();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 起動できなかったことを、起動したことにしない。
            // trust が無い・CLI が入っていない・モードが未対応、いずれもここに来る。
            tracker.OnExited(-1);

            // **「無いから起動を拒む」ことはしない**（設計 §28-1、レビューで発覚）——
            // GUI 起動では PATH が最小限になり、実在する CLI を「無い」と誤判定する。
            // 失敗したときに、見つからなかった事実を**案内として添える**に留める。
            var name = AgentExecutable.NameOf(department.Agent);
            var found = AgentExecutable.Find(department.Agent);
            var missing = found is null
                ? $"（`{name}` を探しましたが見つかりませんでした。入っていないか、PATH に無いのかもしれません）"

                // 見つかっているのに失敗したなら、**場所は分かっている**。そう言う。
                : $"（`{found}` は在ります。起動そのものが失敗しました）";
            return new DepartmentStart.Failed(
                $"{department.DisplayName} を起動できなかった: {exception.Message}{missing}");
        }
    }

    /// <summary>
    /// 外部ターミナルで部門を開く（設計 §32）。
    /// </summary>
    /// <remarks>
    /// <b>仕事を渡したときに呼ばれる</b> —— あちらには「起動しておいて後から渡す」が無い
    /// （アプリは窓へ打ち込めない）。`TaskDispatcher` が状態を書いてから
    /// <c>DispatchResult.LaunchTerminal</c> を返すので、その要求をここで開く。
    /// <b>プロセスの親はアプリのまま</b>である（§9）。
    /// </remarks>
    /// <param name="replaceExisting">
    /// 既に窓があるとき、<b>同じ窓の新しいタブで開き直してよいか</b>（設計 §32-8）。
    /// <b>ここでは判断しない</b> —— 前の仕事がまだ動いているかは、
    /// 仕事の状態を見られる呼び出し元しか知らない。
    /// </param>
    public async Task<DepartmentStart> StartTerminalAsync(
        DepartmentDefinition department, WorkspaceRef workspace,
        TerminalLaunchRequest request, CancellationToken ct, bool replaceExisting = false)
    {
        ArgumentNullException.ThrowIfNull(department);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);

        var tracker = composer.TrackerOf(department.Id);

        // **終了コードを観測できないので、専用の経路で伝える**（§32-2e）。
        // **付け直した窓にも同じ見張りを付ける**（§41-2b、レビューで発覚）——
        // 片方だけにすると、そちらでは**死んだセッションが残り、次の dispatch が
        // 「もう開いている」と判断して窓を開き直せなくなる。**
        void WatchDisappearance(TerminalDepartmentSession watched) =>
            watched.Disappeared += (_, _) =>
            {
                tracker.OnDisappeared();

                lock (_startGate)
                {
                    if (_sessions.TryGetValue(department.Id, out var current) && ReferenceEquals(current, watched))
                    {
                        _sessions.Remove(department.Id);
                    }
                }

                SessionsChanged?.Invoke(this, department.Id);
            };


        // **「走っている」印を、起動を始める前に立てる**（§26-1、構造化の側と同じ理由）。
        // ここを飛ばすと、Terminal.app を起こしている最中に切り替え／終了が走ったとき、
        // **待つべき起動が待たれない**まま前のフォルダのロックが返る。
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int generation;
        TerminalDepartmentSession? replacing = null;
        lock (_startGate)
        {
            if (_startingIds.Contains(department.Id))
            {
                return new DepartmentStart.AlreadyStarting();
            }

            if (_sessions.TryGetValue(department.Id, out var existing))
            {
                // **窓を2つ並べない。** どちらで答えればよいか分からなくなる。
                // 開き直してよいかは**呼び出し元が決める**（§32-8）——
                // あちらは仕事の状態を見られるが、ここからは見えない。
                if (!replaceExisting || existing is not TerminalDepartmentSession terminal)
                {
                    return new DepartmentStart.AlreadyRunning();
                }

                replacing = terminal;
            }

            _startingIds.Add(department.Id);
            generation = _generation;
            _starting.Add(pending.Task);
        }

        if (replacing is not null)
        {
            // **同じ窓の新しいタブで開く**（設計 §32-8 / §33-5）。
            // 前の CLI は終わった仕事の話をして待っているだけなので、閉じてよい ——
            // **記録は `.company/` に残っている**（§6）。
            request = request with { ReuseWindowId = replacing.Handle.WindowId };
            await replacing.DisposeAsync();

            lock (_startGate)
            {
                if (_sessions.TryGetValue(department.Id, out var current) && ReferenceEquals(current, replacing))
                {
                    _sessions.Remove(department.Id);
                }
            }
        }

        try
        {
            tracker.OnStarting();
            var started = await TerminalDepartmentSession.StartAsync(
                department.Id, department.Agent, request, _terminals, TimeProvider.System, ct);

            // **窓が塞がっていたときだけ、窓を指す手を付け直す**（設計 §41-2b、レビューで発覚）。
            // ここへ来る前に前のセッションを捨てているので、そのまま返すと
            // **まだ生きている窓に、二度と手が届かない** ——
            // 前面化も終了もできず、次の dispatch が2つ目の窓を開く（§32-8）。
            //
            // **それ以外の失敗では付け直さない**（レビュー3周目で発覚）——
            // 窓が閉じられていた場合まで付け直すと、**死んだ窓を指したまま**になり、
            // 次の dispatch が新しい窓を開けなくなる。**生きていると観測できたものだけ。**
            if (started is TerminalStartResult.WindowBusy stillBusy)
            {
                if (replacing is not null && generation == _generation)
                {
                    var reattached = await TerminalDepartmentSession.ReattachAsync(
                        department.Id, department.Agent, replacing.Handle, _terminals, TimeProvider.System, ct);

                    lock (_startGate)
                    {
                        _sessions[department.Id] = reattached;
                    }

                    Wire(department, workspace, reattached, tracker);
                    reattached.ReplayObservations();
                    WatchDisappearance(reattached);

                    // **「終わった」と書かない**（§7）。付け直した窓は生きているので、
                    // 本当に居なくなったなら、そのセッションの見張りが `Disappeared` を出す。
                    return new DepartmentStart.Failed(
                        $"{department.DisplayName} のターミナルを開き直せなかった: {stillBusy.Reason}");
                }

                tracker.OnExited(-1);
                return new DepartmentStart.Failed(
                    $"{department.DisplayName} のターミナルを開き直せなかった: {stillBusy.Reason}");
            }

            if (started is TerminalStartResult.Failed failed)
            {
                tracker.OnExited(-1);
                return new DepartmentStart.Failed($"{department.DisplayName} のターミナルを開けなかった: {failed.Reason}");
            }

            var (session, unverified) = started switch
            {
                TerminalStartResult.Started ok => (ok.Session, (string?)null),
                TerminalStartResult.StartedUnverified uncertain => (uncertain.Session, uncertain.Reason),
                _ => throw new InvalidOperationException("未知の TerminalStartResult です"),
            };

            // 起動中に切り替えられていたら、これは前のフォルダの部門（§26-1）。
            if (generation != _generation)
            {
                await session.DisposeAsync();
                tracker.OnDisappeared();
                return new DepartmentStart.Superseded();
            }

            lock (_startGate)
            {
                _sessions[department.Id] = session;
            }

            Wire(department, workspace, session, tracker);

            // **購読してから流し込む**（§22-2 と同じ理由）。起動の観測は
            // `StartAsync` の中で出るので、そのまま raise すると**誰も聞いていない**。
            // 聞き逃すと、検出器は `Starting` のまま止まる。
            session.ReplayObservations();

            WatchDisappearance(session);

            return unverified is { } reason
                ? new DepartmentStart.StartedUnverified(reason)
                : new DepartmentStart.Started();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            tracker.OnExited(-1);
            return new DepartmentStart.Failed(
                $"{department.DisplayName} のターミナルを開けなかった: {exception.Message}");
        }
        finally
        {
            lock (_startGate)
            {
                _starting.Remove(pending.Task);
                _startingIds.Remove(department.Id);
            }

            pending.TrySetResult();
        }
    }

    /// <summary>
    /// セッションの有無が変わった（設計 §32）。<b>画面の「動いているか」を直すために出す。</b>
    /// </summary>
    public event EventHandler<string>? SessionsChanged;

    /// <summary>
    /// その部門のターミナルを前面に出す（設計 §32-2c）。
    /// </summary>
    /// <returns><b>出せたか。</b> 窓が閉じられていれば false —— 出せたことにしない（§7）。</returns>
    public async Task<bool> FocusAsync(string departmentId, CancellationToken ct)
    {
        IAgentSession? session;
        lock (_startGate)
        {
            _sessions.TryGetValue(departmentId, out session);
        }

        return session is TerminalDepartmentSession terminal && await terminal.FocusAsync(ct);
    }

    private void Wire(
        DepartmentDefinition department, WorkspaceRef workspace,
        IAgentSession session, DepartmentStatusTracker tracker)
    {
        session.Observed += (_, evidence) =>
        {
            tracker.OnObserved(evidence);
            if (!StillOurs(workspace)) return;
            Observed?.Invoke(this, (department.Id, evidence));

            // モデルは init / handshake で分かる。**いつ来るかは CLI 次第**なので、
            // 観測のたびに拾い直す（設計 §27）。
            PublishModel(department.Id, session);
        };
        session.Exited += (_, exitCode) => tracker.OnExited(exitCode);

        // 診断は**ライブ専用**（設計 §22）。観測（永続してよい要約）と別の経路で運ぶ。
        session.Diagnosed += (_, diagnostic) =>
        {
            if (!StillOurs(workspace)) return;
            Diagnosed?.Invoke(this, (department.Id, diagnostic));
        };

        // **購読より前に出た分を流し込む**（§22-2）。trust・login・ハンドシェイクの失敗は
        // ここに出るのに、アダプタはハンドシェイクを終えてからセッションを返す。
        // 購読を先にしてから取り置きを流すので、その間の1行が二重に出ることはある ——
        // **重複は害が無いが、取りこぼしは害がある。**
        foreach (var diagnostic in session.RecentDiagnostics(50).Reverse())
        {
            if (!StillOurs(workspace)) break;
            Diagnosed?.Invoke(this, (department.Id, diagnostic));
        }

        if (session is not IStructuredSession structured)
        {
            return;
        }

        structured.TurnFinished += (_, verdict) =>
        {
            tracker.OnTurnFinished(verdict);

            // **失敗を状態に書ける唯一の場所**（設計 §30-3）。走査はファイルしか見ないので、
            // 権限拒否のように文書に現れない失敗は、ここで拾わないと
            // 仕事が `Dispatched` のまま永久に止まる。
            if (!verdict.Succeeded && StillOurs(workspace))
            {
                // **どのフォルダの部門だったかを一緒に渡す**（レビューで発覚、§26-1）。
                // 切り替えは「選ぶ」が先で「前の部門を止める」が後（§26-2b）なので、
                // 前のフォルダの turn 失敗が切り替え後に届く。部門 id だけで照合すると、
                // **A の失敗で B の同名部門の仕事を Failed にする。**
                TurnFailed?.Invoke(this, (department.Id, workspace.Root, verdict.Reason));
            }
        };
        structured.ApprovalRequested += (_, request) =>
        {
            tracker.OnApprovalRequested(request);

            // **前のフォルダの承認要求を、いまのフォルダの待ち行列に入れない**
            // （レビューで発覚、§26-1）。検出器には残す —— 要求が来た事実は観測なので。
            if (!StillOurs(workspace)) return;

            // 承認の返事はこのセッションへ返す。決定は CLI が提示したものだけ（§5）。
            composer.Approvals.Add(new PendingApproval(
                department.DisplayName, request, tracker,
                (decision, reason, token) => structured.RespondAsync(request, decision, reason, token)));
        };
    }

    /// <summary>
    /// このセッションが、いま画面に出ているワークスペースのものか（設計 §26-1）。
    /// </summary>
    /// <remarks>
    /// <b>切り替えは「選ぶ」が先で「前の部門を止める」が後</b>（§26-2b）—— 開けなかったときに
    /// 前のフォルダの秘書と部門だけ死んでいる状態を作らないため。その順序の代償として、
    /// <b>止めるまでの間、前のフォルダのセッションが生きたままイベントを出す。</b>
    /// 部門 id はフォルダをまたいで同じなので、素通しすると
    /// <b>A の観測・承認・失敗が B のタイルと待ち行列に入る。</b>
    /// <para>
    /// <b>検出器（tracker）には流す。</b> あれはセッションに紐づいているので、
    /// 混ざらないし、観測を捨てる理由も無い（§7）。止めるのは画面と調整基盤へ出る側だけ。
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <b>綴りではなく鍵で比べる</b>（レビューで発覚、2026-09-08）。`/tmp` と `/private/tmp`、
    /// symlink、Windows の大小 —— 切り替えの判定は <c>KeyOf</c> を使っている（§26-1）ので、
    /// ここだけ生の文字列で比べると、<b>同じフォルダを開き直しただけで、
    /// 生き残ったセッションの観測・承認・失敗が全部捨てられる。</b>
    /// </remarks>
    private bool StillOurs(WorkspaceRef workspace) =>
        composer.Workspace is { } current
        && string.Equals(
            WorkspaceInstanceLock.KeyOf(current.Root),
            WorkspaceInstanceLock.KeyOf(workspace.Root),
            StringComparison.Ordinal);

    /// <summary>
    /// CLI が申告したモデルをタイルへ渡す（設計 §27）。
    /// </summary>
    /// <remarks><b>未観測なら何も入れない。</b> 設定値で埋めない。</remarks>
    private void PublishModel(string departmentId, IAgentSession session)
    {
        // **読み取りループから UI を触らない**（レビューで発覚）。他の通知と同じく
        // UI スレッドへ載せる —— 直に触ると Avalonia の検査に引っかかるか、黙って落ちる。
        var model = session.ObservedModel;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            foreach (var tile in composer.Shell.Departments.Where(t => t.Id == departmentId))
            {
                tile.ObservedModel = model;
            }
        });
    }

    /// <summary>
    /// 観測したモデルを消す（設計 §27-3）。
    /// </summary>
    /// <remarks>
    /// <b>古い観測を出しっぱなしにしない</b>（レビューで発覚）。見出しは「いま動いている
    /// CLI が申告したモデル」なので、止めたあとや別のフォルダに切り替えたあとも
    /// 前の値が残っていると嘘になる。
    /// </remarks>
    private void ForgetModel(string departmentId) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            foreach (var tile in composer.Shell.Departments.Where(t => t.Id == departmentId))
            {
                tile.ObservedModel = null;
            }
        });

    /// <summary>この部門へ直接1メッセージ送る。</summary>
    /// <remarks>
    /// <b>これは調整基盤の経路ではない。</b> 本来は秘書が <c>instruction.md</c> を書き、
    /// アプリが dispatch する（§6 / §14-1）。ここは配線を実物で確かめるための直通路。
    /// </remarks>
    public Task SendAsync(string departmentId, string text, CancellationToken ct) =>
        SessionOf(departmentId) is IStructuredSession structured
            ? structured.SendUserMessageAsync(text, ct)
            : Task.CompletedTask;

    /// <summary>
    /// いま動いている部門の名前（設計 §28-3）。<b>閉じてよいか人間が判断する材料。</b>
    /// </summary>
    public IReadOnlyList<string> RunningDepartments()
    {
        lock (_startGate)
        {
            return [.. _sessions.Keys];
        }
    }

    public bool IsRunning(string departmentId)
    {
        lock (_startGate)
        {
            return _sessions.ContainsKey(departmentId);
        }
    }

    /// <summary>dispatch の宛先。動いていない、または構造化でなければ null。</summary>
    public IStructuredSession? StructuredSessionOf(string departmentId) => SessionOf(departmentId) as IStructuredSession;

    private IAgentSession? SessionOf(string departmentId)
    {
        lock (_startGate)
        {
            return _sessions.TryGetValue(departmentId, out var session) ? session : null;
        }
    }

    /// <summary>観測が来たことを画面へ知らせる（部門ごとの一覧に控えるため）。</summary>
    public event EventHandler<(string DepartmentId, Evidence Evidence)>? Observed;

    /// <summary>
    /// 診断の生の行（設計 §22）。<b>保存しない。</b>
    /// <see cref="Observed"/> と混ぜない —— あちらは redact 済みの要約で永続しうる。
    /// </summary>
    public event EventHandler<(string DepartmentId, LiveDiagnostic Diagnostic)>? Diagnosed;

    /// <summary>
    /// 部門の turn が<b>失敗して</b>終わった（設計 §30-3）。理由つき。
    /// </summary>
    /// <remarks>
    /// <b>これを仕事の失敗と決めつけない。</b> 抱えている仕事があるか、
    /// 報告や質問が出ていないかは、受け取った側が調整基盤に照らして決める
    /// （<see cref="TaskDispatcher.FailInFlightAsync"/>）。
    /// </remarks>
    public event EventHandler<(string DepartmentId, string WorkspaceRoot, string Because)>? TurnFailed;

    /// <summary>
    /// その部門のアダプタ（設計 §46）。
    /// </summary>
    /// <remarks>
    /// <b>モデルと思考の強さは、定義から渡す。</b> 直す前は Codex のモデルしか見ておらず、
    /// **Claude と Antigravity は設定を無視していた** —— 設定できるのに効かない鍵になっていた。
    /// </remarks>
    private static IAgentAdapter AdapterFor(DepartmentDefinition department) => department.Agent switch
    {
        AgentKind.ClaudeCode => new ClaudeCodeAdapter(department.Model, department.ReasoningEffort),
        AgentKind.CodexCli => new CodexCliAdapter(department.Model ?? "gpt-5.6-terra", department.ReasoningEffort),
        AgentKind.AntigravityCli => new AntigravityAdapter(department.Model, department.ReasoningEffort),
        _ => throw new NotSupportedException($"担当できるアダプタが無い: {department.Agent}"),
    };

    /// <summary>
    /// 全部門を終了する。<b>ウィンドウを閉じたときにここへ来る</b>（設計 §9）——
    /// v1 はバックグラウンド継続を持たない。無人運転に近づくため。
    /// </summary>
    /// <summary>
    /// いま動いている部門を全部止める（設計 §26-1）。<b>この後も使える。</b>
    /// </summary>
    /// <remarks>
    /// ワークスペースを切り替えるときに要る。**止めずに切り替えると、部門は前のフォルダで
    /// 動き続ける** —— 画面は B なのに CLI は A を触っており、A のロックを返した瞬間に
    /// 別のアプリが A を開ける（レビューで発覚）。
    /// </remarks>
    /// <summary>
    /// その部門だけ終わらせる（設計 §47）。
    /// </summary>
    /// <remarks>
    /// <b>部門を消すときに要る。</b> 定義から消すのに窓が残ると、
    /// **人間が制御できない CLI がワークスペースを書ける**（レビューの指摘）。
    /// <para>
    /// <b>居なければ何もしない。</b> 「終わらせた」と言えるのは、居たものを終わらせたときだけ。
    /// </para>
    /// </remarks>
    /// <returns>終わらせたか（居なければ false）。</returns>
    public async Task<bool> StopAsync(string departmentId)
    {
        IAgentSession? session;
        lock (_startGate)
        {
            if (!_sessions.TryGetValue(departmentId, out session))
            {
                return false;
            }

            _sessions.Remove(departmentId);
        }

        try
        {
            await session.DisposeAsync();
        }
        catch (Exception)
        {
            // 終わらせられなくても、こちらの手からは離す（§9 の片付けと同じ姿勢）。
        }

        composer.TrackerOf(departmentId).OnDisappeared();
        SessionsChanged?.Invoke(this, departmentId);
        return true;
    }

    public async Task StopAllAsync()
    {
        // 世代を進めてから待つ。走っている起動は、自分が古いと分かって自分で閉じる。
        _generation++;
        Task[] pending;
        lock (_startGate)
        {
            pending = [.. _starting];
        }

        foreach (var starting in pending)
        {
            try
            {
                await starting;
            }
            catch (Exception)
            {
                // 起動の失敗はもう関係ない。停止はここで止まらない。
            }
        }

        // **UI スレッドで触る**（レビューで発覚）。終了処理は `Task.Run` の中から来るので、
        // ここで直に書くとスレッド違反で落ち、**その先の秘書の後始末が飛ぶ**。
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            foreach (var tile in composer.Shell.Departments)
            {
                tile.SessionRunning = false;
                tile.ObservedModel = null;
            }
        });

        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        IAgentSession[] sessions;
        lock (_startGate)
        {
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception)
            {
                // 1つ落とせなくても残りの後始末を続ける。
            }
        }
    }
}

/// <summary>部門を起動した結果（設計 §26-1）。</summary>
public abstract record DepartmentStart
{
    public sealed record Started : DepartmentStart;

    public sealed record Failed(string Reason) : DepartmentStart;

    /// <summary>
    /// 窓は開いたが、<b>起動を確かめられていない</b>（設計 §41）。
    /// <b>「起動した」と言わない</b> —— 呼び出し側は人間に確かめさせる。
    /// </summary>
    public sealed record StartedUnverified(string Reason) : DepartmentStart;

    /// <summary>もう動いている。<b>「起動した」と言わない</b>（設計 §27-4）。</summary>
    public sealed record AlreadyRunning : DepartmentStart;

    /// <summary>いま起動処理中。二度押しはここに来る。</summary>
    public sealed record AlreadyStarting : DepartmentStart;

    /// <summary>
    /// 起動している間にワークスペースが切り替わったので捨てた。
    /// <b>失敗ではないが、成功でもない。</b>
    /// </summary>
    public sealed record Superseded : DepartmentStart;
}
