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

    /// <summary>前回のワークスペース。<b>覚えるのはパスだけ</b>（設計 §21-3）。</summary>
    private readonly WorkspaceMemory _memory = WorkspaceMemory.CreateDefault();

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

        Opened += async (_, _) => await ResumeWorkspaceAsync();
    }

    /// <summary>
    /// 前回のワークスペースを開き直す（設計 §21-1）。
    /// </summary>
    /// <remarks>
    /// <b>疑わしいときは開かない。</b> 開くと Startup 走査が <c>state.json</c> を進めるので、
    /// 人間が今回まだ何も選んでいない場所でそれを起こさない。
    /// <b>理由は必ず出す</b> —— 黙って「選んでください」に戻ると、前回の場所が
    /// 消えたことに気付けない。
    /// </remarks>
    private async Task ResumeWorkspaceAsync()
    {
        switch (await _memory.DecideAsync(CancellationToken.None))
        {
            case WorkspaceResume.Open open:
                try
                {
                    await OpenWorkspaceAsync(open.Remembered.RawPath);
                    Note($"前回のフォルダを開いた: {open.Remembered.RawPath}");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // **ここで投げると毎回の起動が落ちる。** 自動で開いた副作用で、
                    // 人間が別のフォルダを選ぶ画面にすら辿り着けなくなる。
                    Note($"前回のフォルダを開けなかった（{exception.GetType().Name}: {exception.Message}）。"
                        + $"選び直す: {open.Remembered.RawPath}");
                }

                break;

            // **理由は記録が読めないときも出す**（§21-1）。黙って捨てると
            // 「まだ選んでいない」と区別が付かない。
            case WorkspaceResume.Ask ask:
                Note(ask.Remembered is { } remembered
                    ? $"前回のフォルダを開かなかった（{ask.Reason}）。選び直す: {remembered.RawPath}"
                    : $"前回のフォルダを開かなかった（{ask.Reason}）");
                break;
        }
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

            case DepartmentAction.DispatchTask:
                await DispatchDraftedAsync(tile);
                break;

            // 差し戻したまま止まっている仕事を送り直す（設計 §19-1）。
            // 理由は既に rejection.md にある。次の指示書があるならそれを昇格させる。
            case DepartmentAction.RedispatchTask:
                await RedispatchAsync(tile);
                break;

            case DepartmentAction.ShowApproval:
                Note($"{tile.Name} の承認は中央ペインに出ている");
                break;

            case DepartmentAction.AnswerQuestion:
                OpenCoordinationFile(tile, "question.md", "質問");
                break;

            case DepartmentAction.ReadReport:
                await ShowCoordinationFileAsync(tile, "report.md", "報告");
                break;

            // §14-1: Dispatched は「送ったかもしれない」。**自動再送しない。**
            case DepartmentAction.CheckDelivery:
                Note($"{tile.Name}: 送られたか確かめる。指示は届いているかもしれない（自動で再送しない）");
                break;
        }
    }

    /// <summary>
    /// 診断を出す（設計 §22）。<b>読むだけ。ターミナルではない。</b>
    /// </summary>
    /// <remarks>
    /// v1 はターミナルを持たない（§22-1）。代わりに、<b>止まって見えるときに
    /// 人間が見るべきもの</b>をここへ出す —— プロセスの素性、最後に観測したこと、
    /// stderr の生の行。
    /// <para>
    /// <b>用件のボタン（§15-6 の段）にはしない。</b> 診断は「人間の出番」ではなく、
    /// 人間が自分の判断で覗くもの。段に足すと「人間の出番」の意味が濁る。
    /// </para>
    /// </remarks>
    private void OnShowDiagnostics(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not DepartmentTile tile)
        {
            return;
        }

        Select(tile);
        Note($"—— {tile.Name} の診断 ——");
        Note($"稼働 {tile.RuntimeText} / 活動 {tile.ActivityText} / 仕事 {tile.WorkText}");
        Note(_runner?.IsRunning(tile.Id) is true
            ? $"プロセス: 動いている（{tile.Agent} / {tile.Mode}）"
            : $"プロセス: 動いていない（{tile.Agent} / {tile.Mode}）");

        // **観測が無いことを「正常」と読ませない**（§7）。
        Note(tile.RecentObservations.Count is 0
            ? "観測: まだ何も観測していない"
            : $"観測（最新）: {tile.RecentObservations[0]}");

        Note(tile.Diagnostics.Summary);
        foreach (var diagnostic in tile.Diagnostics.Recent(10))
        {
            Note($"[{diagnostic.Stream}] {diagnostic.Text}");
        }
    }

    /// <summary>
    /// 書き込み権で弾かれたときの言い方（設計 §24-1）。
    /// </summary>
    /// <remarks>
    /// <b>「待つ」と言ってよいのは、有効な保持者がいるときだけ。</b>
    /// 失効した保持者は<b>待っても消えない</b> —— §14-2 が時間切れだけでの奪取を禁じているので、
    /// 人間が引き取ると決めない限り永久に空かない。実機で「失敗ではない。待つ」と出て、
    /// 待っても直らなかった（2026-09-06）。
    /// </remarks>
    private static string BlockedText(DispatchResult.Blocked blocked) =>
        $"{blocked.Reason}（失敗ではない。待つ）";

    /// <summary>
    /// 失効した書き込み権で弾かれたとき（設計 §24-1）。
    /// </summary>
    /// <remarks>
    /// <b>ここに2つ目の復旧 UI を作らない。</b> 左の「確かめてほしいこと」が正本で、
    /// ここはそこへ誘導するだけ（§16-4 と二重にしない）。
    /// </remarks>
    private string ExpiredLeaseText(DispatchResult.BlockedByExpiredLease expired)
    {
        // **誘導する前に、誘導先を作る**（レビューで発覚）。ワークスペースを開いたあとに
        // 失効した場合、起動時の走査は二度と回らないので、一覧は空のままだった。
        _composer?.NoteExpiredWriteLease(expired.Holder);

        return $"{expired.Reason}。**待っても空かない** —— "
            + $"左の「確かめてほしいこと」から外す（{expired.Holder.Holder.Id} が "
            + $"{expired.Holder.ExpiresAt.ToLocalTime():MM/dd HH:mm} に失効）";
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

    /// <summary>
    /// 指示書はあるが渡していない仕事を渡す（設計 §15-6）。
    /// 「仕事にする」は<b>提案を仕事に昇格させる操作</b>であって、
    /// 「部門へ送信成功する」操作ではない —— 部門が動いていないときはここに来る。
    /// </summary>
    private async Task DispatchDraftedAsync(DepartmentTile tile)
    {
        if (_composer?.Tasks is not { } tasks || _composer.Dispatcher is not { } dispatcher
            || tile.CurrentTaskSlug is not { } slug)
        {
            return;
        }

        if (await tasks.ReadAsync(slug, CancellationToken.None) is not TaskReadResult.Found found)
        {
            Note($"{slug}: 読めなくなっている");
            return;
        }

        // **押す前と状態が変わっていることがある**（`.company/` は人間が手で直せる。§14-2）。
        // 特に Rejected へ変わっていた場合、そのまま渡すと現在の instruction.md を
        // attempts/ へ封じたうえで古い指示を送ることになる（§15-10）。
        if (found.State.Status is not CoreTaskStatus.Drafted)
        {
            Note($"{slug}: 状態が {found.State.Status} に変わっている（渡さない）");
            await ScanAsync(CompanyScanKind.Periodic);
            return;
        }

        var result = await dispatcher.DispatchAsync(
            found.State, _composer.DefinitionOf(tile.Id), SessionOf(tile.Id),
            TimeSpan.FromMinutes(30), CancellationToken.None);

        Note(result switch
        {
            DispatchResult.Dispatched => $"{tile.Name} に {slug} を渡した",
            DispatchResult.Blocked blocked => $"{slug}: {BlockedText(blocked)}",
            DispatchResult.BlockedByExpiredLease expired => $"{slug}: {ExpiredLeaseText(expired)}",
            DispatchResult.SentUncertain uncertain => $"{slug}: {uncertain.Reason}。**届いたか確かめる**",
            DispatchResult.Rejected rejected => $"{slug}: {rejected.Reason}（先に「起動する」）",

            // **理由を捨てない**（実機で踏んだ、2026-09-06）。ここに落ちていたせいで
            // 「渡せなかった」としか出ず、原因（読めない lease.json）に辿り着けなかった。
            DispatchResult.Conflicted conflicted => $"{slug}: {conflicted.Reason}",
            _ => $"{slug}: 渡せなかった（{result.GetType().Name}）",
        });

        await ScanAsync(CompanyScanKind.Periodic);
    }

    /// <summary>
    /// 報告を受理して仕事を終える（設計 §6 / §19-3）。<b>終端なので自動化は戻せない。</b>
    /// </summary>
    private async void OnAcceptReport(object? sender, RoutedEventArgs e)
    {
        if (_composer?.Tasks is not { } tasks || (sender as Control)?.DataContext is not DepartmentTile tile)
        {
            return;
        }

        Select(tile);
        if (await ReadReportedAsync(tile) is not { } state)
        {
            return;
        }

        var write = await tasks.TransitionAsync(
            state, CoreTaskStatus.Accepted, TransitionOrigin.Human, "報告を受理した", CancellationToken.None);

        Note(write switch
        {
            TaskWriteResult.Written => $"{tile.Name}: {state.Slug} を受理した",
            TaskWriteResult.Conflicted conflicted => $"{state.Slug}: {conflicted.Reason}",
            TaskWriteResult.Rejected rejected => $"{state.Slug}: {rejected.Reason}",
            _ => $"{state.Slug}: 受理できなかった（{write.GetType().Name}）",
        });

        await ScanAsync(CompanyScanKind.Periodic);
    }

    /// <summary>
    /// 報告を差し戻して、次の試行として送り直す（設計 §19-1 ～ §19-3）。
    /// </summary>
    /// <remarks>
    /// <b>順序を守る。</b> 理由を残す → <c>Rejected</c> にする → 次の指示を staging に置く →
    /// 封じて昇格して <c>Dispatched</c>（<see cref="TaskStore.RedispatchAsync"/>）→ 送る。
    /// <para>
    /// <b>理由は次の指示書に本文として入れる</b>（§19-2）—— <c>rejection.md</c> を
    /// 置いただけでは部門が読む保証がない。
    /// </para>
    /// </remarks>
    private async void OnRejectReport(object? sender, RoutedEventArgs e)
    {
        if (_composer is not { Tasks: { } tasks, Dispatcher: { } dispatcher, Workspace: { } workspace }
            || (sender as Control)?.DataContext is not DepartmentTile tile)
        {
            return;
        }

        Select(tile);

        var reason = tile.RejectionDraft.Trim();
        if (reason.Length is 0)
        {
            // 理由の無い差し戻しは、同じ報告をもう一度受け取るだけになる（§19-2）。
            Note($"{tile.Name}: 差し戻す理由を書く（次の指示書に入る）");
            return;
        }

        if (await ReadReportedAsync(tile) is not { } state)
        {
            return;
        }

        var write = await tasks.TransitionAsync(
            state, CoreTaskStatus.Rejected, TransitionOrigin.Human, "報告を差し戻した", CancellationToken.None);
        if (write is not TaskWriteResult.Written rejected)
        {
            Note($"{state.Slug}: {(write as TaskWriteResult.Conflicted)?.Reason ?? (write as TaskWriteResult.Rejected)?.Reason ?? "差し戻せなかった"}");
            await ScanAsync(CompanyScanKind.Periodic);
            return;
        }

        // ここから先で落ちても、仕事は Rejected として残る。
        // 「差し戻した仕事を送り直す」で拾える（§15-6 の不変条件）。
        await File.WriteAllTextAsync(workspace.Company.Rejection(state.Slug), reason, CancellationToken.None);
        await File.WriteAllTextAsync(
            workspace.Company.NextInstruction(state.Slug),
            CompanyInstruction.Compose(NextInstructionText(reason), workspace.Company, state.Slug),
            CancellationToken.None);

        tile.RejectionDraft = string.Empty;
        await RedispatchCoreAsync(tile, rejected.State, dispatcher);
    }

    /// <summary>
    /// 差し戻したまま止まっている仕事を送り直す（設計 §19-1）。
    /// </summary>
    private async Task RedispatchAsync(DepartmentTile tile)
    {
        if (_composer?.Tasks is not { } tasks || _composer.Dispatcher is not { } dispatcher
            || tile.CurrentTaskSlug is not { } slug)
        {
            return;
        }

        if (await tasks.ReadAsync(slug, CancellationToken.None) is not TaskReadResult.Found found)
        {
            Note($"{slug}: 読めなくなっている");
            return;
        }

        // **押す前と状態が変わっていることがある**（`.company/` は人間が手で直せる。§14-2）。
        if (found.State.Status is not CoreTaskStatus.Rejected)
        {
            Note($"{slug}: 状態が {found.State.Status} に変わっている（送り直さない）");
            await ScanAsync(CompanyScanKind.Periodic);
            return;
        }

        await RedispatchCoreAsync(tile, found.State, dispatcher);
    }

    private async Task RedispatchCoreAsync(DepartmentTile tile, TaskState state, TaskDispatcher dispatcher)
    {
        var result = await dispatcher.RedispatchAsync(
            state, _composer!.DefinitionOf(tile.Id), SessionOf(tile.Id),
            TimeSpan.FromMinutes(30), CancellationToken.None);

        Note(result switch
        {
            DispatchResult.Dispatched => $"{tile.Name} に {state.Slug} を差し戻して送り直した",
            DispatchResult.Blocked blocked => $"{state.Slug}: {BlockedText(blocked)}",
            DispatchResult.BlockedByExpiredLease expired => $"{state.Slug}: {ExpiredLeaseText(expired)}",
            DispatchResult.SentUncertain uncertain => $"{state.Slug}: {uncertain.Reason}。**届いたか確かめる**",
            DispatchResult.Rejected rejected => $"{state.Slug}: {rejected.Reason}",
            DispatchResult.Conflicted conflicted => $"{state.Slug}: {conflicted.Reason}",
            _ => $"{state.Slug}: 送り直せなかった（{result.GetType().Name}）",
        });

        await ScanAsync(CompanyScanKind.Periodic);
    }

    /// <summary>
    /// 次の試行の指示書の本文。<b>理由をここに書き写す</b>（設計 §19-2）——
    /// 部門が <c>rejection.md</c> を開くとは限らない。
    /// </summary>
    private static string NextInstructionText(string reason) =>
        $"""
        前の試行の報告は受理されなかった。同じ仕事をやり直すこと。

        ## 差し戻しの理由

        {reason}

        前の試行の指示書と報告は attempts/ に残っている。必要なら読むこと。
        """;

    /// <summary>
    /// 押した時点の状態を読み直す。<b>画面の値で判断しない</b>（§14-2）——
    /// <c>.company/</c> は人間が手で直せるし、走査が先に進めていることもある。
    /// </summary>
    private async Task<TaskState?> ReadReportedAsync(DepartmentTile tile)
    {
        if (_composer?.Tasks is not { } tasks || tile.CurrentTaskSlug is not { } slug)
        {
            Note($"{tile.Name}: どの仕事の報告か分からない（.company/ の経路が未接続）");
            return null;
        }

        if (await tasks.ReadAsync(slug, CancellationToken.None) is not TaskReadResult.Found found)
        {
            Note($"{slug}: 読めなくなっている");
            return null;
        }

        if (found.State.Status is not CoreTaskStatus.Reported)
        {
            Note($"{slug}: 状態が {found.State.Status} に変わっている（判断しない）");
            await ScanAsync(CompanyScanKind.Periodic);
            return null;
        }

        return found.State;
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
    /// <summary>
    /// 調整文書の中身を中央の会話に出す（設計 §17-5）。
    /// </summary>
    /// <remarks>
    /// <b>外部アプリで開かず、その場で読ませる。</b> 報告を読むのに窓を移ると、
    /// そのまま受理か差し戻しかを決める流れ（§19-3）が切れる。
    /// <para>
    /// <b>ライブ表示専用。</b> 会話は正本ではない（§17-3）。
    /// 長い報告は途中で切るが、<b>切ったことを黙らない</b>（§22-2 と同じ理由）。
    /// </para>
    /// </remarks>
    private async Task ShowCoordinationFileAsync(DepartmentTile tile, string fileName, string label)
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

        string content;
        try
        {
            content = await File.ReadAllTextAsync(path, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 読めなかったことを、読んだことにしない。
            Note($"{tile.Name}: {label}を読めなかった（{exception.GetType().Name}）。場所は {path}");
            return;
        }

        const int limit = 4000;
        Say(content.Length > limit
            ? $"{tile.Name}: {content[..limit]}\n\n（長いので残り {content.Length - limit} 文字は省いた。全文は {path}）"
            : $"{tile.Name}: {content}");

        // 出どころを左に残す。会話は落ちたら失われてよいが、場所は追える（§17-3）。
        Note($"{tile.Name} の{label}を出した: {path}");
    }

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

        // 起動に失敗したとき、入力欄の内容を消さない（§17-4）。
        if (!await EnsureSecretaryAsync(workspace))
        {
            return;
        }

        ClearMessage();
        Say($"あなた: {text}");
        await _secretary.SendAsync(text, CancellationToken.None);
    }

    /// <summary>
    /// 秘書が動いていなければ起動して、protocol を読ませる（設計 §17-4 / §17-6）。
    /// </summary>
    /// <remarks>
    /// <b>順序が契約。</b> README を置く → 起動する → 1通目に「これを読んで」を送る。
    /// 逆にすると読ませる先が無い（§21-1）。
    /// <para>
    /// <b>起動の経路を1つにする。</b> 送信の副作用とワークスペース選択の両方から呼ぶので、
    /// 別々に書くと片方だけ順序を間違える。
    /// </para>
    /// </remarks>
    /// <returns>秘書が使える状態か。</returns>
    private async Task<bool> EnsureSecretaryAsync(MultiAIAgentCompany.Core.Workspace.WorkspaceRef workspace)
    {
        if (_secretary is null || _composer is null)
        {
            return false;
        }

        if (_secretary.IsRunning)
        {
            return true;
        }

        await _composer.WriteSecretaryProtocolAsync(CancellationToken.None);

        if (await _secretary.StartAsync(workspace, CancellationToken.None) is { } failure)
        {
            Note(failure);
            return false;
        }

        // **起動していないのに「使える」と答えない。** 既に起動中だった場合、
        // StartAsync は「失敗ではない」を返すが、この呼び出しは何も起こしていない ——
        // ここで送ると SendAsync が黙って捨てる（§7 の「沈黙を正常にしない」）。
        if (!_secretary.IsRunning)
        {
            Note("秘書は起動中。立ち上がったらもう一度送る");
            return false;
        }

        // protocol の正本は README。**中身を会話に埋めない**（設計 §17-6）。
        await _secretary.SendAsync(
            SecretaryReadme.StartupMessage(workspace.Company), CancellationToken.None);
        return true;
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
            DispatchResult.Blocked blocked => $"{slug}: {BlockedText(blocked)}",
            DispatchResult.BlockedByExpiredLease expired => $"{slug}: {ExpiredLeaseText(expired)}",
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

        // lease は仕事ではないので、仕事のフォルダを引かない（設計 §23-1）。
        var directory = item.IsUnreadableLease || item.IsExpiredLease
            ? workspace.Company.Root
            : workspace.Company.TaskDirectory(item.Slug);
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

    /// <summary>
    /// 読めない <c>lease.json</c> を隔離して、空の書き込み権を作る（設計 §23-1）。
    /// </summary>
    /// <remarks>
    /// <b>人間が押したときだけ。</b> 自動で作り直すのは §14-2 に反する ——
    /// 読めない lease は「誰も持っていない」ではなく「持ち主を検証できない」。
    /// <para>
    /// <b>押した時点でもう一度読む</b>（<see cref="LeaseRecovery.IsolateAsync"/> の中）。
    /// 人間が手で直した直後かもしれない。
    /// </para>
    /// </remarks>
    private async void OnRecoveryIsolateLease(object? sender, RoutedEventArgs e)
    {
        if (Item(sender) is not { } item || _composer is not { Workspace: { } workspace, Leases: { } leases })
        {
            return;
        }

        var moved = await LeaseRecovery.IsolateAsync(
            workspace.Company, leases, DateTimeOffset.Now, CancellationToken.None);

        Note(moved is null
            ? "書き込み権: 隔離しなかった（いまは読める、またはファイルを動かせなかった）"
            : $"書き込み権を隔離して作り直した（元は消えていない）→ {moved}");

        if (moved is not null)
        {
            _composer.Shell.Recovery.Remove(item);
        }

        await ScanAsync(CompanyScanKind.Startup);
    }

    /// <summary>
    /// 失効した書き込み権を外す（設計 §24-2）。
    /// </summary>
    /// <remarks>
    /// <b>§14-2 を破っていない。</b> 根拠は時間ではなく、人間が
    /// 「その保持者はもう動いていない」と判断したこと。
    /// <b>押した時点でもう一度読む</b> —— 有効な保持者に変わっていたら外さない。
    /// </remarks>
    private async void OnRecoveryReleaseExpiredLease(object? sender, RoutedEventArgs e)
    {
        if (Item(sender) is not { Lease: { } judged } item || _composer?.Leases is not { } leases)
        {
            return;
        }

        if (await leases.ReadAsync(CancellationToken.None) is not LeaseReadResult.Found found)
        {
            Note("書き込み権を読めない（先に隔離する）");
            return;
        }

        var write = await leases.ReleaseExpiredAsync(
            found.Leases, LeaseKind.Write, judged, CancellationToken.None);
        Note(write switch
        {
            LeaseWriteResult.Written => "失効した書き込み権を外した。これで仕事を渡せる",
            LeaseWriteResult.Denied denied => $"外さなかった: {denied.Reason}",
            LeaseWriteResult.NotHeld notHeld => $"外すものが無かった: {notHeld.Reason}",
            LeaseWriteResult.Conflicted conflicted => $"外さなかった: {conflicted.Reason}（もう一度読む）",
            _ => "外せなかった",
        });

        if (write is LeaseWriteResult.Written)
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
            DispatchResult.Blocked blocked => $"{slug}: {BlockedText(blocked)}",
            DispatchResult.BlockedByExpiredLease expired => $"{slug}: {ExpiredLeaseText(expired)}",
            DispatchResult.Rejected rejected => $"{slug}: {rejected.Reason}（先に部門を起動する）",
            DispatchResult.Conflicted conflicted => $"{slug}: {conflicted.Reason}",
            _ => $"{slug}: 渡せなかった（{result.GetType().Name}）",
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
        // **起動中のものも見る**（§17-7）。WorkspaceRoot は起動が終わるまで null なので、
        // それだけを見ると「切り替わっていない」と誤判定する。
        if (_secretary is { TargetWorkspaceRoot: { } previous } && !string.Equals(previous, path, StringComparison.Ordinal))
        {
            Note("ワークスペースが変わったので秘書を終了した（次の送信で新しいフォルダで起動する）");
            await _secretary.DisposeAsync();
        }

        await OpenWorkspaceAsync(path);

        // **人間が選んだときだけ覚える**（設計 §21-2）。起動時の自動復帰では上書きしない ——
        // 開けなかった記録を開いたことにしない。
        await _memory.RememberAsync(path, DateTimeOffset.Now, CancellationToken.None);
    }

    /// <summary>ワークスペースを開く。人間の選択と起動時の復帰で同じ経路を通る（設計 §21-1）。</summary>
    private async Task OpenWorkspaceAsync(string path)
    {
        if (_composer is null)
        {
            return;
        }

        await _composer.SelectWorkspaceAsync(path, CancellationToken.None);
        UpdateSecretaryStatus();

        // 起動時（ワークスペース選択時）の走査だけが復旧の一覧を作る（設計 §16-1）。
        await ScanAsync(CompanyScanKind.Startup);
        StartPeriodicScan();

        // **フォルダが決まったら秘書を起こす**（2026-09-06、人間が指定。§17-4 を改めた）。
        // 選んだ時点で相手が居る方が自然、という判断。**代償は書いてある**（§17-7）——
        // アプリを開くだけで claude が1つ起動し、README も書かれる。
        // **開き始めたときの path と、いま開いているものが同じか確かめる**（§17-7）。
        // 走査を待っている間に人間が別のフォルダを選ぶと、古い側のこの行が
        // **選ばれていないフォルダで claude を起こす**。しかも後から来た側は
        // `IsRunning` を見て、その間違った秘書を使い回す。
        if (_composer.Workspace is { } opened
            && string.Equals(opened.Root, path, StringComparison.Ordinal))
        {
            try
            {
                await EnsureSecretaryAsync(opened);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // **フォルダを選んだだけでアプリが落ちない。** 書けないフォルダ
                // （読み取り専用、`.company/secretary` を作れない）を選ぶと、
                // ここは async void の先なので投げるとそのまま落ちる。
                Note($"秘書を起動できなかった（{exception.GetType().Name}: {exception.Message}）。"
                    + "ワークスペースは開いている");
            }

            UpdateSecretaryStatus();
        }
    }
}
