using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Coordination;
using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Status;
using MultiAIAgentCompany.Core.Workspace;
using CoreTaskStatus = MultiAIAgentCompany.Core.Coordination.TaskStatus;

namespace MultiAIAgentCompany.Desktop;

public partial class MainWindow : Window
{
    private readonly ShellComposer? _composer;
    private readonly DepartmentRunner? _runner;
    private DepartmentTile? _selected;
    private DispatcherTimer? _scanTimer;
    private SecretaryRunner? _secretary;

    /// <summary>
    /// いま仕事にしている最中の提案。<b>同じ提案が2つの仕事にならないようにする</b> ——
    /// 二度押しはクラッシュを待たずに起きる。
    /// </summary>
    private readonly HashSet<string> _acceptingProposals = new(StringComparer.Ordinal);

    /// <summary>
    /// 回答の送信で例外が出た仕事。<b>自動で送り直さない</b>（設計 §16-5 / §14-1）——
    /// 届いたかもしれないので、5秒ごとに送り直すと同じ回答が何度も届く。
    /// </summary>
    private readonly HashSet<string> _answerDeliveryUncertain = new(StringComparer.Ordinal);

    /// <summary>
    /// 走査が走っている最中か。<b>重ねて走らせない</b> ——
    /// 送信が5秒より長くかかると次のタイマーが入り、同じ <c>answer.md</c> を
    /// 2度届けてしまう（§16-5 の「自動で再送しない」を自分で破る）。
    /// </summary>
    private bool _scanInFlight;

    public MainWindow() => AvaloniaXamlLoader.Load(this);

    public MainWindow(ShellComposer composer, DepartmentRunner runner, SecretaryRunner secretary) : this()
    {
        _composer = composer;
        _runner = runner;
        _secretary = secretary;
        DataContext = composer.Shell;

        _secretary.StateChanged += (_, _) => Dispatcher.UIThread.Post(UpdateSecretaryStatus);
        _secretary.Said += (_, line) => Dispatcher.UIThread.Post(() => Say($"秘書: {line}"));
        UpdateSecretaryStatus();
    }

    /// <summary>
    /// 秘書が居ないときも中央ペインを空にしない（設計 §17-4）——
    /// 状態と、次に人間が取る行動を出す。
    /// </summary>
    private void UpdateSecretaryStatus()
    {
        if (DataContext is not ShellViewModel shell || _secretary is null)
        {
            return;
        }

        if (_composer?.Workspace is null)
        {
            shell.SecretaryStatus = "フォルダを選ぶ（秘書は選んだフォルダで動く）";
            return;
        }

        // trust を状態に含める（設計 §17-4）。**未 trust と判定不能を分ける**（§13-9）——
        // 読めていないだけなのに「未 trust」と言い切らない。
        var trust = shell.Trust.FirstOrDefault(row => row.Agent == AgentKind.ClaudeCode)?.State;
        shell.SecretaryStatus = (_secretary.State, trust) switch
        {
            (SecretaryState.Running, _) => "秘書と会話できる",
            (SecretaryState.Starting, _) => "秘書を起動中",
            (SecretaryState.Failed, _) => $"秘書を起動できなかった / 落ちた: {_secretary.FailureReason}",
            (_, WorkspaceTrustState.NotTrusted) =>
                "Claude Code がこのフォルダを trust していない。アプリは trust を書かない（その CLI で一度起動して信頼を与える）",
            (_, WorkspaceTrustState.Unknown) =>
                "Claude Code の trust を判定できない。未 trust とは限らない",
            _ => "秘書はまだ起動していない。最初の送信で起動する",
        };
    }

    private void Say(string line)
    {
        if (DataContext is ShellViewModel shell)
        {
            // 会話は正本ではない（§17-3）。落ちたら失われてよい。
            // ただし**上限を置く** —— 永続しなくても長時間起動で膨らむ（§17-5）。
            shell.SecretaryTranscript.Add(line);
            while (shell.SecretaryTranscript.Count > 500)
            {
                shell.SecretaryTranscript.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// 部門パネルを押す。<b>選ぶだけ。副作用なし</b>（設計 §15-6 で §1 を改めた）——
    /// 同じ押下がプロセスを起動したりファイルを開いたりすると、
    /// 人間が押す前に結果を予測できない。
    /// </summary>
    private void OnDepartmentClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DepartmentTile tile)
        {
            Select(tile);
        }
    }

    /// <summary>
    /// パネル上のボタン。<b>文言どおりのことをする</b>（設計 §15-6 の8段）。
    /// </summary>
    private async void OnDepartmentAction(object? sender, RoutedEventArgs e)
    {
        if (_composer is null || _runner is null
            || (sender as Control)?.DataContext is not DepartmentTile tile)
        {
            return;
        }

        Select(tile);

        switch (tile.Call.Action)
        {
            // 落ちた理由は観測に出ている。**再起動はここから改めて選ぶ**（§15-4 / §15-6）——
            // 原因を見ないまま再起動すると、同じ落ち方を繰り返して枠を使うだけになり得る。
            case DepartmentAction.Investigate:
            case DepartmentAction.ShowObservations:
                ShowObservations(tile);
                break;

            case DepartmentAction.ShowApproval:
                Note($"{tile.Name} の承認は中央ペインに出ている");
                break;

            case DepartmentAction.AnswerQuestion:
                OpenCoordinationFile(tile, "question.md", "質問");
                break;

            case DepartmentAction.ReadReport:
                OpenCoordinationFile(tile, "report.md", "報告");
                break;

            // §14-1: Dispatched は「送ったかもしれない」。**自動再送しない。**
            case DepartmentAction.CheckDelivery:
                Note($"{tile.Name}: 送られたか確かめる。指示は届いているかもしれない（自動で再送しない）");
                break;
        }
    }

    /// <summary>起動。<b>仕事の用件とは別枠</b>（設計 §15-6）。</summary>
    private async void OnDepartmentStart(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not DepartmentTile tile)
        {
            return;
        }

        Select(tile);
        await StartAsync(tile);
        await ScanAsync(CompanyScanKind.Periodic);
    }

    private async Task StartAsync(DepartmentTile tile)
    {
        if (_composer?.Workspace is not { } workspace || _runner is null)
        {
            Note("先にワークスペースを選ぶ（部門は選んだフォルダで動く）");
            return;
        }

        var failure = await _runner.StartAsync(_composer.DefinitionOf(tile.Id), workspace, CancellationToken.None);
        tile.SessionRunning = _runner.IsRunning(tile.Id);
        Note(failure ?? $"{tile.Name} を起動した");
    }

    private void ShowObservations(DepartmentTile tile)
    {
        Note($"—— {tile.Name} の観測 ——");
        foreach (var line in tile.RecentObservations.Take(10))
        {
            Note(line);
        }

        if (tile.RecentObservations.Count == 0)
        {
            Note("（観測がまだ無い）");
        }
    }

    /// <summary>
    /// 調整文書を OS の既定アプリで開く。§14-2 が「人間が手で直せる」ことに寄りかかっているので、
    /// アプリの中に編集画面を持たない。
    /// </summary>
    /// <remarks>
    /// <b>ボタンは文言どおりのことをする</b>（§15-6）。開けないなら、
    /// 何が無いのかを言う —— 「開く」と書いてあるのに何も起きない、を作らない。
    /// </remarks>
    private void OpenCoordinationFile(DepartmentTile tile, string fileName, string label)
    {
        if (_composer?.Workspace is not { } workspace)
        {
            Note("先にワークスペースを選ぶ");
            return;
        }

        if (tile.CurrentTaskSlug is not { } slug)
        {
            Note($"{tile.Name}: どの仕事の{label}か分からない（.company/ の経路が未接続）");
            return;
        }

        var path = Path.Combine(workspace.Company.TaskDirectory(slug), fileName);
        if (!File.Exists(path))
        {
            Note($"{tile.Name}: {label}のファイルがまだ無い（{path}）");
            return;
        }

        try
        {
            using var _ = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            Note($"{tile.Name}の{label}を開いた: {path}");
        }
        catch (Exception exception)
        {
            // 開けなかったことを、開いたことにしない。
            Note($"{tile.Name}: {label}を開けなかった（{exception.GetType().Name}）。場所は {path}");
        }
    }

    /// <summary>
    /// 秘書に送る。<b>未起動ならその副作用として起動する</b>（設計 §17-4）——
    /// ワークスペース選択に起動を隠さない。
    /// </summary>
    private async void OnSendToSecretary(object? sender, RoutedEventArgs e)
    {
        if (_secretary is null || _composer is null || PeekMessage() is not { } text)
        {
            return;
        }

        if (_composer.Workspace is not { } workspace)
        {
            Note("先にワークスペースを選ぶ");
            return;
        }

        if (!_secretary.IsRunning)
        {
            // 起動に失敗したとき、入力欄の内容を消さない（§17-4）。
            if (await _secretary.StartAsync(workspace, CancellationToken.None) is { } failure)
            {
                Note(failure);
                return;
            }

            // protocol の正本は README。**中身を会話に埋めない**（設計 §17-6）。
            await _secretary.SendAsync(
                SecretaryReadme.StartupMessage(workspace.Company), CancellationToken.None);
        }

        ClearMessage();
        Say($"あなた: {text}");
        await _secretary.SendAsync(text, CancellationToken.None);
    }

    /// <summary>
    /// 選んだ部門に仕事を作る（設計 §17-4 で右ペインへ移した）。
    /// <c>instruction.md</c> を書き、<see cref="TaskDispatcher"/> に渡す（§6 / §16-2）。
    /// </summary>
    private async void OnMakeTask(object? sender, RoutedEventArgs e)
    {
        if (_composer is null || _runner is null
            || (sender as Control)?.DataContext is not DepartmentTile tile)
        {
            return;
        }

        Select(tile);

        if (_composer.Tasks is not { } tasks || _composer.Dispatcher is not { } dispatcher
            || _composer.Workspace is not { } workspace)
        {
            Note("先にワークスペースを選ぶ");
            return;
        }

        // **中央の入力欄は秘書のもの**（§17-4）。部門の仕事はタイルの入力欄から取る。
        // 秘書が outbox に publish する経路が入るまでの暫定（§17-1）。
        if (string.IsNullOrWhiteSpace(tile.TaskDraft))
        {
            Note($"{tile.Name} のタイルの入力欄に指示を書いてから押す");
            return;
        }

        var text = tile.TaskDraft;

        // 同じ秒に2つ作ると衝突して、2つ目が黙って作られない。
        var slug = $"task-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        if (await tasks.CreateAsync(slug, tile.Id, CancellationToken.None) is not TaskWriteResult.Written created)
        {
            Note($"仕事を作れなかった: {slug}");
            return;
        }

        tile.TaskDraft = string.Empty;

        // 人間の文章だけでは足りない —— 部門は報告をどこにどう書くかを知らない（§16-1）。
        await File.WriteAllTextAsync(
            workspace.Company.Instruction(slug),
            CompanyInstruction.Compose(text, workspace.Company, slug),
            CancellationToken.None);

        var result = await dispatcher.DispatchAsync(
            created.State, _composer.DefinitionOf(tile.Id), SessionOf(tile.Id),
            TimeSpan.FromMinutes(30), CancellationToken.None);

        Note(result switch
        {
            DispatchResult.Dispatched => $"{tile.Name} に {slug} を渡した",
            DispatchResult.NeedsHuman needsHuman => $"{slug}: {needsHuman.Reason}（人間が送る）",
            DispatchResult.Blocked blocked => $"{slug}: {blocked.Reason}（失敗ではない。待つ）",
            DispatchResult.SentUncertain uncertain => $"{slug}: {uncertain.Reason}。**届いたか確かめる**",
            DispatchResult.Rejected rejected => $"{slug}: {rejected.Reason}",
            DispatchResult.Conflicted conflicted => $"{slug}: {conflicted.Reason}",
            _ => $"{slug}: 不明な結果",
        });

        await ScanAsync(CompanyScanKind.Periodic);
    }

    /// <summary>入力欄を読むだけ。<b>消さない</b> —— 送れなかったときに人間の文章を失わない。</summary>
    private string? PeekMessage() =>
        this.FindControl<TextBox>("MessageBox") is { } box && !string.IsNullOrWhiteSpace(box.Text)
            ? box.Text
            : null;

    /// <summary>送れた／保存できたあとにだけ消す。</summary>
    private void ClearMessage()
    {
        if (this.FindControl<TextBox>("MessageBox") is { } box)
        {
            box.Text = string.Empty;
        }
    }

    private IStructuredSession? SessionOf(string departmentId) => _runner?.StructuredSessionOf(departmentId);

    /// <summary>`.company/` を1周見る。<b>走査が正本</b>（設計 §16-1）。</summary>
    private async Task ScanAsync(CompanyScanKind kind)
    {
        if (_composer is null || _scanInFlight)
        {
            return;
        }

        _scanInFlight = true;
        try
        {
            await ScanCoreAsync(kind);
        }
        finally
        {
            _scanInFlight = false;
        }
    }

    private async Task ScanCoreAsync(CompanyScanKind kind)
    {

        var result = await _composer.ScanAsync(kind, CancellationToken.None);
        foreach (var applied in result?.Applied ?? [])
        {
            Note($"{applied.Slug}: {applied.From} → {applied.To}（{applied.Because}）");
        }

        foreach (var blocked in result?.Blocked ?? [])
        {
            Note($"{blocked.Slug}: {blocked.From} → {blocked.To} を書けなかった（{blocked.Reason}）");
        }

        await DeliverAnswersAsync();
    }

    /// <summary>
    /// 置かれた <c>answer.md</c> を部門へ届ける（設計 §16-5）。
    /// <b>届いたときだけ状態が進む。</b>
    /// </summary>
    private async Task DeliverAnswersAsync()
    {
        if (_composer?.Tasks is not { } tasks || _composer.Dispatcher is not { } dispatcher
            || _composer.Workspace is not { } workspace)
        {
            return;
        }

        foreach (var slug in await tasks.ListSlugsAsync(CancellationToken.None))
        {
            if (_answerDeliveryUncertain.Contains(slug)
                || await tasks.ReadAsync(slug, CancellationToken.None) is not TaskReadResult.Found found
                || found.State.Status is not CoreTaskStatus.AwaitingAnswer
                || !File.Exists(workspace.Company.Answer(slug)))
            {
                continue;
            }

            var state = found.State;
            var result = await dispatcher.DeliverAnswerAsync(
                state, _composer.DefinitionOf(state.DepartmentId),
                SessionOf(state.DepartmentId), CancellationToken.None);

            switch (result)
            {
                case DispatchResult.Dispatched:
                    Note($"{slug}: 回答を届けた（作業中に戻した）");
                    break;

                case DispatchResult.SentUncertain uncertain:
                    // 届いたかもしれない。同じ回答を何度も送らない。
                    _answerDeliveryUncertain.Add(slug);
                    Note($"{slug}: {uncertain.Reason}。**届いたか確かめる**（自動では送り直さない）");
                    break;

                case DispatchResult.Rejected rejected:
                    Note($"{slug}: 回答を届けられない（{rejected.Reason}）");
                    break;

                case DispatchResult.NeedsHuman needsHuman:
                    Note($"{slug}: {needsHuman.Reason}（人間が渡す）");
                    break;
            }
        }
    }

    // --- 復旧の後始末（設計 §16-4）。「まだ決めない」はボタンを出さない —— 何もしなければ残る。

    /// <summary>人間が「受け取って作業中だった」と観測した。<b>アプリの推定ではない</b>（§7）。</summary>
    private async void OnRecoveryConfirmDelivered(object? sender, RoutedEventArgs e) =>
        await ResolveAsync(sender, async (tasks, state) =>
        {
            var result = await tasks.TransitionAsync(state, CoreTaskStatus.InProgress,
                TransitionOrigin.Human, "人間が部門側で受領と作業継続を確認した", CancellationToken.None);
            return result is TaskWriteResult.Written
                ? $"{state.Slug}: 受領を確認した（作業中）"
                : $"{state.Slug}: 記録できなかった";
        });

    /// <summary>
    /// もう一度送る。<b>状態は動かさない</b>（§16-4）—— 再送しても
    /// 「送ったかもしれない」であることは変わらない。
    /// </summary>
    private async void OnRecoveryResend(object? sender, RoutedEventArgs e) =>
        await ResolveAsync(sender, async (tasks, state) =>
        {
            if (_composer?.Dispatcher is not { } dispatcher)
            {
                return "ワークスペースが選ばれていない";
            }

            var result = await dispatcher.RetryDeliveryAsync(
                state, _composer.DefinitionOf(state.DepartmentId),
                SessionOf(state.DepartmentId), CancellationToken.None);

            return result switch
            {
                DispatchResult.Dispatched => $"{state.Slug}: もう一度送った（二重に実行されたかもしれない）",
                DispatchResult.NeedsHuman needsHuman => $"{state.Slug}: {needsHuman.Reason}",
                DispatchResult.SentUncertain uncertain => $"{state.Slug}: {uncertain.Reason}",
                DispatchResult.Rejected rejected => $"{state.Slug}: {rejected.Reason}",
                _ => $"{state.Slug}: 送れなかった",
            };
        }, keepItem: true);

    /// <summary>
    /// アプリ上で取り消す。<b>実行の停止は保証しない</b>（§16-4）——
    /// lease も自動で解放しない（§14-2 の「失効は停止の証拠ではない」と同じ）。
    /// </summary>
    private async void OnRecoveryCancel(object? sender, RoutedEventArgs e) =>
        await ResolveAsync(sender, async (tasks, state) =>
        {
            var result = await tasks.TransitionAsync(state, CoreTaskStatus.Cancelled,
                TransitionOrigin.Human, "人間がアプリ上で取り消した（実行の停止は保証しない）", CancellationToken.None);
            return result is TaskWriteResult.Written
                ? $"{state.Slug}: アプリ上で取り消した（部門が動いていれば止まっていない）"
                : $"{state.Slug}: 記録できなかった";
        });

    private void OnRecoveryOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (Item(sender) is not { } item || _composer?.Workspace is not { } workspace)
        {
            return;
        }

        var directory = workspace.Company.TaskDirectory(item.Slug);
        try
        {
            using var _ = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
            Note($"{item.Slug} のフォルダを開いた");
        }
        catch (Exception exception)
        {
            Note($"{item.Slug}: フォルダを開けなかった（{exception.GetType().Name}）。場所は {directory}");
        }
    }

    /// <summary>
    /// 読めない仕事を隔離する。<b><c>state.json</c> を勝手に補完しない</b>（§14-1 / §16-4）。
    /// </summary>
    private async void OnRecoveryQuarantine(object? sender, RoutedEventArgs e)
    {
        if (Item(sender) is not { } item || _composer?.Workspace is not { } workspace)
        {
            return;
        }

        var moved = UnreadableTaskQuarantine.Isolate(workspace.Company, item.Slug, DateTimeOffset.Now);
        Note(moved is null
            ? $"{item.Slug}: 隔離できなかった"
            : $"{item.Slug}: 隔離した（中身は消えていない）→ {moved}");

        if (moved is not null)
        {
            _composer.Shell.Recovery.Remove(item);
        }

        await ScanAsync(CompanyScanKind.Startup);
    }

    private async void OnRecoveryRescan(object? sender, RoutedEventArgs e) =>
        await ScanAsync(CompanyScanKind.Startup);

    private RecoveryItem? Item(object? sender) => (sender as Control)?.DataContext as RecoveryItem;

    private async Task ResolveAsync(
        object? sender, Func<TaskStore, TaskState, Task<string>> resolve, bool keepItem = false)
    {
        if (Item(sender) is not { } item || _composer?.Tasks is not { } tasks)
        {
            return;
        }

        if (await tasks.ReadAsync(item.Slug, CancellationToken.None) is not TaskReadResult.Found found)
        {
            Note($"{item.Slug}: 読めなくなっている");
            return;
        }

        Note(await resolve(tasks, found.State));

        if (!keepItem)
        {
            _composer.Shell.Recovery.Remove(item);
        }

        await ScanAsync(CompanyScanKind.Periodic);
    }

    /// <summary>
    /// 提案を仕事にする（設計 §17-6）。<b>タスクを作って instruction.md を書いてから</b>
    /// outbox の文書を移す —— 途中で落ちたら提案は残る（失うよりまし）。
    /// </summary>
    private async void OnProposalAccept(object? sender, RoutedEventArgs e)
    {
        if (_composer is null || (sender as Control)?.DataContext is not ProposalCard card
            || card.Proposal.DepartmentId is not { } departmentId
            || _composer.Tasks is not { } tasks || _composer.Dispatcher is not { } dispatcher
            || _composer.Workspace is not { } workspace || _composer.Outbox is not { } outbox)
        {
            return;
        }

        // **outbox が未処理の正本**（§17-6）。まだそこに在ることを確かめてから作る ——
        // 二度押しや、別経路で処理済みになっていた場合に、1つの提案が2つの仕事になる。
        if (!_acceptingProposals.Add(card.Id))
        {
            return;
        }

        try
        {
            if (!File.Exists(Path.Combine(workspace.Company.SecretaryOutbox, $"{card.Id}.md")))
            {
                Note($"提案 {card.Id} は既に処理されている");
                return;
            }

            if (string.IsNullOrWhiteSpace(card.Body))
            {
                Note($"提案 {card.Id} は本文が空なので仕事にできない");
                return;
            }

            await AcceptCoreAsync(card, departmentId, tasks, dispatcher, workspace, outbox);
        }
        finally
        {
            _acceptingProposals.Remove(card.Id);
        }

        await ScanAsync(CompanyScanKind.Periodic);
    }

    private async Task AcceptCoreAsync(
        ProposalCard card, string departmentId, TaskStore tasks, TaskDispatcher dispatcher,
        MultiAIAgentCompany.Core.Workspace.WorkspaceRef workspace, SecretaryOutbox outbox)
    {
        var slug = $"task-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        if (await tasks.CreateAsync(slug, departmentId, CancellationToken.None) is not TaskWriteResult.Written created)
        {
            Note($"仕事を作れなかった: {slug}");
            return;
        }

        await File.WriteAllTextAsync(
            workspace.Company.Instruction(slug),
            CompanyInstruction.Compose(card.Body, workspace.Company, slug),
            CancellationToken.None);

        // ここまで済んでから移す（§17-6）。
        outbox.Accept(card.Id, slug, DateTimeOffset.Now);

        var result = await dispatcher.DispatchAsync(
            created.State, _composer.DefinitionOf(departmentId), SessionOf(departmentId),
            TimeSpan.FromMinutes(30), CancellationToken.None);

        Note(result switch
        {
            DispatchResult.Dispatched => $"{card.TargetText} に {slug} を渡した",
            DispatchResult.NeedsHuman needsHuman => $"{slug}: {needsHuman.Reason}（人間が送る）",
            DispatchResult.Blocked blocked => $"{slug}: {blocked.Reason}（失敗ではない。待つ）",
            DispatchResult.Rejected rejected => $"{slug}: {rejected.Reason}（先に部門を起動する）",
            _ => $"{slug}: 渡せなかった",
        });
    }

    /// <summary>提案をやめる。<b>消さずに移す</b>（§16-4 / §17-6）。</summary>
    private async void OnProposalReject(object? sender, RoutedEventArgs e)
    {
        if (_composer?.Outbox is not { } outbox || (sender as Control)?.DataContext is not ProposalCard card)
        {
            return;
        }

        outbox.Reject(card.Id, DateTimeOffset.Now);
        Note($"提案 {card.Id} をやめた（記録は残っている）");
        await ScanAsync(CompanyScanKind.Periodic);
    }

    private void Select(DepartmentTile tile)
    {
        if (_selected is not null)
        {
            _selected.IsSelected = false;
        }

        _selected = tile;
        tile.IsSelected = true;
    }

    /// <summary>
    /// 定期走査。<b>走査が正本</b>（設計 §16-1）—— イベントは合図にしか使わない。
    /// </summary>
    private void StartPeriodicScan()
    {
        if (_scanTimer is not null)
        {
            return;
        }

        _scanTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background,
            async (_, _) => await ScanAsync(CompanyScanKind.Periodic));
        _scanTimer.Start();
    }

    private void Note(string line)
    {
        if (DataContext is ShellViewModel shell)
        {
            shell.WorkLog.Insert(0, $"{DateTimeOffset.Now:HH:mm:ss}  {line}");
        }
    }

    /// <summary>
    /// ワークスペースを選ぶ。選んだら各 CLI の trust を読み直す（設計 §13-9）。
    /// <b>アプリは trust を書かない</b> —— 読むだけ。
    /// </summary>
    private async void OnPickWorkspace(object? sender, RoutedEventArgs e)
    {
        if (_composer is null)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "AI たちが働くフォルダを選ぶ",
            AllowMultiple = false,
        });

        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        // **ワークスペースが変わったら秘書は別人。** 前のフォルダで動いている Claude に
        // 次のメッセージを送ると、意図と違う作業ツリーへ届く。
        if (_secretary is { WorkspaceRoot: { } previous } && !string.Equals(previous, path, StringComparison.Ordinal))
        {
            Note("ワークスペースが変わったので秘書を終了した（次の送信で新しいフォルダで起動する）");
            await _secretary.DisposeAsync();
        }

        await _composer.SelectWorkspaceAsync(path, CancellationToken.None);
        UpdateSecretaryStatus();

        // 起動時（ワークスペース選択時）の走査だけが復旧の一覧を作る（設計 §16-1）。
        await ScanAsync(CompanyScanKind.Startup);
        StartPeriodicScan();
    }
}
