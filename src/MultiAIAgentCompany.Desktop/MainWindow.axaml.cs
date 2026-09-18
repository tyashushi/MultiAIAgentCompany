using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Coordination;
using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Status;
using MultiAIAgentCompany.Core.Terminal;
using MultiAIAgentCompany.Core.Workspace;
using MultiAIAgentCompany.Core.Workspace.Trust;
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
    /// 沈黙を知らせた turn の起点（設計 §36-4 / §31-6）。<b>一度だけ言う</b> ——
    /// 走査は5秒ごとに回るので、毎回積むと作業ログが沈黙で埋まる。
    /// </summary>
    /// <remarks>
    /// <b>覚えているのは時刻そのもの</b>なので、turn が変わればひとりでに忘れる ——
    /// 立て直しでもフォルダの切り替えでもセッションが入れ替わり、走っている turn は消える。
    /// 「切り替えで消えるべきものが、消える場所に置かれていない」（§31-6）を、
    /// <b>消す場所を要らなくすることで</b>避けている。
    /// </remarks>
    private DateTimeOffset? _secretaryStalledNoticed;

    /// <summary>
    /// 権利を持ち続けていることを知らせた部門（設計 §24-4）。<b>1度だけ言う</b>。
    /// </summary>
    private readonly HashSet<string> _renewedLease = new(StringComparer.Ordinal);

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
    private Task? _scanTask;

    /// <summary>
    /// ワークスペースの切り替えを1つずつにする（設計 §26-1）。
    /// </summary>
    /// <remarks>
    /// <b>重ねると壊れる。</b> 切り替えの途中でもう一度切り替えられると、
    /// 前のロックを持ったまま次のロックで上書きし、**返されないロックが残る**
    /// （レビューで発覚）。ここが直列なら、その形は起きない。
    /// </remarks>
    private readonly SemaphoreSlim _switchGate = new(1, 1);

    /// <summary>
    /// いま切り替えている最中か（設計 §26-2b）。
    /// </summary>
    /// <remarks>
    /// <b>切り替えの途中で、前のフォルダに新しい仕事を作らせない。</b>
    /// 後始末の await の合間に UI へ制御が戻るので、そこで「起動する」や「送る」を押されると、
    /// **これから手放すフォルダで CLI が立つ**（レビューで発覚）。
    /// </remarks>
    private bool _switching;

    /// <summary>終了処理に入ったか（設計 §26-4）。</summary>
    private bool _closing;

    /// <summary>閉じてよいと人間が答えた（設計 §28-3）。<b>二度は聞かない。</b></summary>
    private bool _confirmedClose;

    /// <summary>確認を出している最中（設計 §28-3）。<b>二枚目を出さない。</b></summary>
    private bool _askingClose;

    /// <summary>アイコンの一覧の窓（設計 §15-5）。<b>1つだけ開く。</b></summary>
    private IconGalleryWindow? _iconGallery;

    /// <summary>部門の設定の窓（設計 §47）。<b>1つだけ開く。</b></summary>
    private DepartmentSettingsWindow? _settings;

    /// <summary>プレビューの窓は1つを使い回す（設計 §56-5 / §58-5）。</summary>
    private ImagePreviewWindow? _imagePreview;

    /// <summary>前回のワークスペース。<b>覚えるのはパスだけ</b>（設計 §21-3）。</summary>
    private readonly WorkspaceMemory _memory = WorkspaceMemory.CreateDefault();

    /// <summary>ウィンドウの見た目（設計 §28-5）。<b>状態は覚えない。</b></summary>
    private readonly WindowLayoutMemory _layout = WindowLayoutMemory.CreateDefault();

    /// <summary>
    /// いま開いているワークスペースの排他ロック（設計 §26）。
    /// <b>握っている間だけ、そのフォルダを開いていられる。</b>
    /// </summary>
    private WorkspaceInstanceLock? _instanceLock;

    /// <summary>
    /// いま握っているフォルダ（**解決後のパス**）。
    /// </summary>
    /// <remarks>
    /// <b>綴りで比べない</b>（レビューで発覚）。`/tmp/repo` と `/private/tmp/repo` のように
    /// 同じ実体を別の綴りで選ぶと、**自分が握っているロックを「別のアプリ」と報告する**。
    /// </remarks>
    private string? _lockedWorkspace;

    /// <summary>
    /// 切り替え前に握っていたロック。<b>切り替えが済むまで手放さない</b>（設計 §26-1）。
    /// </summary>
    /// <remarks>
    /// 新しい方を取った時点で返すと、走査や秘書の停止がまだ前のフォルダを触っている間に、
    /// **別のアプリがそこを開ける**（レビューで発覚）。
    /// </remarks>
    private WorkspaceInstanceLock? _supersededLock;

    /// <summary>残量取得の二重起動を防ぎ、閉じるときに止める（設計 §50）。</summary>
    private CancellationTokenSource? _usageCancellation;
    private bool _usageReading;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<TranscriptLinkTextBlock>("TranscriptLinks")!.LinkClicked += OnTranscriptImageLink;
        // **窓のどこに落としても添付にする**（設計 §58-6）。
        AddHandler(DragDrop.DragOverEvent, OnDragOverAttachment);
        AddHandler(DragDrop.DropEvent, OnDropAttachment);
        Closed += (_, _) => _usageCancellation?.Cancel();

        // **ターミナルで信頼を与えて戻ってきたら、表示を読み直す**（設計 §54）。
        Activated += async (_, _) => await RefreshTrustAsync();

        if (!OperatingSystem.IsMacOS())
        {
            BindMenuGestures();
        }
    }

    /// <summary>
    /// メニューのショートカットを、窓のキー操作として効かせる（macOS 以外）。
    /// </summary>
    /// <remarks>
    /// <b>窓の中に描いたメニューでは、ショートカットが効かない</b>（2026-09-14、Windows の実機で確かめた）。
    /// サブメニューの項目は開くまで作られないので、そこに付いたキーは誰も聞いていない。
    /// macOS は OS のメニューが聞くので要らない（足すと2回走る）。
    /// <para>
    /// <b>メニューの定義から作る。</b> キーと処理をここに別に書くと、片方だけ直し忘れる。
    /// </para>
    /// </remarks>
    private void BindMenuGestures()
    {
        if (NativeMenu.GetMenu(this) is not { } menu)
        {
            return;
        }

        foreach (var item in Flatten(menu))
        {
            if (item.Gesture is { } gesture)
            {
                KeyBindings.Add(new KeyBinding { Gesture = gesture, Command = new MenuItemClick(item) });
            }
        }

        static IEnumerable<NativeMenuItem> Flatten(NativeMenu menu) => menu.Items
            .OfType<NativeMenuItem>()
            .SelectMany(item => item.Menu is { } sub ? Flatten(sub).Prepend(item) : [item]);
    }

    /// <summary>メニュー項目を押したことにする。<b>処理はメニューの Click のまま</b>（入口を増やさない）。</summary>
    private sealed class MenuItemClick(NativeMenuItem item) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => item.IsEnabled;

        public void Execute(object? parameter) => ((INativeMenuItemExporterEventsImplBridge)item).RaiseClicked();
    }

    /// <summary>trust を読み直す窓口。<b>切り替えの最中と終了処理の最中は読まない。</b></summary>
    private async Task RefreshTrustAsync()
    {
        if (_closing || _switching || _composer is null)
        {
            return;
        }

        try
        {
            await _composer.RefreshTrustAsync(CancellationToken.None);

            // **秘書の状態の文言も trust から作っている**（Codex の指摘）。読み直さないと、
            // 行は「信頼済み」なのに秘書の欄だけ「trust していない」と言い続ける。
            UpdateSecretaryStatus();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            NoteException("trust を読み直せなかった", exception);
        }
    }

    /// <summary>信頼を与えるためのターミナル。<b>部門の窓とは別</b>に開く。</summary>
    private readonly ITerminalLauncher _trustTerminals = TerminalLaunchers.ForCurrentOs();

    /// <summary>
    /// 未 trust の CLI を、このフォルダでターミナルに開く（設計 §54、人間の要望）。
    /// </summary>
    /// <remarks>
    /// <b>引数を付けずに対話起動するだけ。</b> 信頼の確認は CLI 自身が出すので、
    /// **押すのは人間**（アプリは trust を書かない。§13-9）。部門の仕事も渡さない。
    /// </remarks>
    private async void OnOpenTrustTerminal(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not TrustRow row || _composer?.Workspace is not { } workspace)
        {
            return;
        }

        try
        {
            var command = AgentExecutable.ResolveCommand(AgentExecutable.NameOf(row.Agent));
            var result = await _trustTerminals.LaunchAsync(
                new TerminalLaunchRequest($"MultiAI-trust-{row.Agent}", workspace.Root, command, []),
                CancellationToken.None);

            Note(result switch
            {
                TerminalLaunchResult.Launched =>
                    $"{row.Agent} をターミナルで開いた。信頼を与えたら、このアプリに戻ると表示が更新される",
                TerminalLaunchResult.Failed failed => $"{row.Agent} をターミナルで開けなかった: {failed.Reason}",
                TerminalLaunchResult.WindowBusy busy => $"{row.Agent} をターミナルで開けなかった: {busy.Reason}",
                _ => $"{row.Agent} をターミナルで開けなかった（{result.GetType().Name}）",
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            NoteException($"{row.Agent} をターミナルで開けなかった", exception);
        }
    }

    private async void OnAgentUsageExpanded(object? sender, RoutedEventArgs e) => await RefreshAgentUsageAsync();

    private async void OnRefreshAgentUsage(object? sender, RoutedEventArgs e) => await RefreshAgentUsageAsync();

    /// <summary><b>3つを並行に取り、届いた順に出す。</b> タイマーからは呼ばない（設計 §50-2）。</summary>
    private async Task RefreshAgentUsageAsync()
    {
        if (_usageReading || _closing || DataContext is not ShellViewModel shell) return;
        _usageReading = true;
        using var cancellation = new CancellationTokenSource();
        _usageCancellation = cancellation;
        try
        {
            shell.AgentUsageObservedText = string.Empty;
            foreach (var row in shell.AgentUsage) row.BeginRead();
            await Task.WhenAll(shell.AgentUsage.Select(async row =>
            {
                AgentUsageResult result;
                try
                {
                    result = await Task.Run(() => AgentUsageCatalog.ReadAsync(row.Kind, cancellation.Token), cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    result = new AgentUsageResult.Unreadable(row.Kind, $"取得できない: {exception.Message}", string.Empty);
                }

                if (cancellation.IsCancellationRequested) return;
                row.Show(result);
                var observed = shell.AgentUsage.Max(item => item.ObservedAt);
                shell.AgentUsageObservedText = observed is { } time ? $"取得 {time.ToLocalTime():HH:mm}" : string.Empty;
            }));
        }
        catch (Exception exception)
        {
            // **画面の失敗で落とさず、理由をパネルに出す**（§49 / §50）。
            shell.AgentUsageObservedText = $"読み取れなかった: {exception.Message}";
        }
        finally
        {
            _usageCancellation = null;
            _usageReading = false;
        }
    }

    public MainWindow(ShellComposer composer, DepartmentRunner runner, SecretaryRunner secretary) : this()
    {
        _composer = composer;
        _runner = runner;
        _secretary = secretary;

        // **UI の失敗を、人間に見せる**（設計 §49）。落とさない代わりに、黙らない ——
        // 記録（`crash.log`）だけだと、**画面を見ている人間には何も起きていないように見える。**
        Program.UiThreadFailed += (_, exception) => Dispatcher.UIThread.Post(() =>
            NoteException("画面の処理で例外が出た（アプリは動き続けている）", exception));
        DataContext = composer.Shell;

        // **失敗を仕事の状態に書ける唯一の経路**（設計 §30-3）。
        // 走査はファイルしか見ないので、権限拒否のように文書へ現れない失敗は
        // ここで拾わないと `Dispatched` のまま永久に残る。
        _runner.TurnFailed += (_, item) => Dispatcher.UIThread.Post(
            async () => await FailInFlightAsync(item.DepartmentId, item.WorkspaceRoot, item.Because));

        _secretary.StateChanged += (_, _) => Dispatcher.UIThread.Post(UpdateSecretaryStatus);
        _secretary.Said += (_, line) => Dispatcher.UIThread.Post(() => SayAndRecord("secretary", line));

        // **聞かずに通したことを黙らない**（設計 §35 / §7）。
        _secretary.AutoApproved += (_, reason) =>
            Dispatcher.UIThread.Post(() => Note($"秘書: {reason} を聞かずに通した"));

        // **秘書の stderr を捨てない**（設計 §22、2026-09-09 に実機で踏んだ）。
        // 「API Error: 400 status code (no body)」の**後ろにある理由**は、ここにしか出ない。
        _secretary.Diagnosed += (_, diagnostic) => Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is ShellViewModel shell)
            {
                shell.Diagnostics.Add(diagnostic);
            }
        });

        // **窓が閉じられたらタイルも直す**（設計 §32）。放っておくと
        // 「前面に出す」が残り、押しても何も起きない。
        _runner.SessionsChanged += (_, departmentId) =>
            Dispatcher.UIThread.Post(() => RefreshRunning(departmentId));
        UpdateSecretaryStatus();

        Opened += async (_, _) => await ResumeWorkspaceAsync();

        // **動いている最中に閉じたら、一度だけ確かめる**（設計 §28-3）。
        // 閉じると §9 により全部の CLI が終わる —— 進行中の仕事が切れる。
        Closing += OnWindowClosing;

        // **見た目を覚える**（設計 §28-5）。§21 はパスしか覚えていなかったので、
        // 毎回この大きさとペイン幅に戻っていた。
        RestoreLayout();
        Closing += (_, _) => SaveLayout();

        // **最初に触る場所へフォーカスを置く**（設計 §28-7）。
        // 起動直後に打ち始められないと、まずマウスを持つことになる。
        Opened += (_, _) => this.FindControl<TextBox>("MessageBox")?.Focus();
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
                    // 開けなかった理由（別インスタンスが開いている等）は
                    // OpenWorkspaceAsync が出す（§26-2）。ここで重ねて言わない。
                    if (await OpenWorkspaceAsync(open.Remembered.RawPath))
                    {
                        Note($"前回のフォルダを開いた: {open.Remembered.RawPath}");
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // **ここで投げると毎回の起動が落ちる。** 自動で開いた副作用で、
                    // 人間が別のフォルダを選ぶ画面にすら辿り着けなくなる。
                    NoteException($"前回のフォルダを開けなかった（選び直す: {open.Remembered.RawPath}）", exception);
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
        // **秘書の CLI の trust を見る**（§54-2、人間の要望）。Claude 決め打ちだったので、
        // 秘書を Codex にしても Claude の trust で文言を作っていた。
        var agent = _secretary.Definition.Agent;
        var trust = shell.Trust.FirstOrDefault(row => row.Agent == agent)?.State;
        shell.SecretaryStatus = (_secretary.State, trust) switch
        {
            (SecretaryState.Running, _) => "秘書と会話できる",
            (SecretaryState.Starting, _) => "秘書を起動中",
            (SecretaryState.Failed, _) => $"秘書を起動できなかった / 落ちた: {_secretary.FailureReason}",
            (_, WorkspaceTrustState.NotTrusted) =>
                $"{agent} がこのフォルダを trust していない。アプリは trust を書かない（上の「ターミナルで開く」から信頼を与える）",
            (_, WorkspaceTrustState.NoRecord) =>
                $"{agent} の trust の記録がまだ無い。上の「ターミナルで開く」から一度起動すれば分かる",
            (_, WorkspaceTrustState.Unknown) =>
                $"{agent} の trust を判定できない。未 trust とは限らない",
            _ => "秘書はまだ起動していない。最初の送信で起動する",
        };
    }

    /// <summary>
    /// 会話を1行足し、<b>いまのスレッドにも書き残す</b>（設計 §32-6）。
    /// </summary>
    /// <remarks>
    /// <b>「会話は正本ではない」（§17-3）は変わらない。</b> 残すのは
    /// <b>人間が読み返すため</b>であって、protocol も仕事も正本はファイルのままである ——
    /// 秘書は <c>.company/secretary/README.md</c> を読み、仕事は
    /// <c>.company/tasks/</c> に居る。**ここが消えても、仕事は消えない。**
    /// </remarks>
    private void SayAndRecord(string role, string text)
    {
        Say(role is "human" ? $"あなた: {text}" : $"秘書: {text}");
        _ = RecordAsync(role, text);
    }

    private async Task RecordAsync(string role, string text)
    {
        if (_composer?.Threads is not { } threads)
        {
            return;
        }

        try
        {
            // **選んでいなければ、その場で作る。** 相談を始めるのに
            // 「まず新規作成を押す」を挟まない（Claude / ChatGPT と同じ）。
            if (_composer.Shell.CurrentThreadId is not { } id)
            {
                var created = await threads.CreateAsync(TitleFrom(text), CancellationToken.None);
                if (created is not ThreadCreateResult.Created ok)
                {
                    Note($"相談スレッドを作れなかった（{((ThreadCreateResult.Failed)created).Reason}）");
                    return;
                }

                id = ok.Meta.Id;
                _composer.Shell.CurrentThreadId = id;
            }

            var written = await threads.AppendAsync(
                id, new ThreadEntry(role, text, DateTimeOffset.UtcNow), CancellationToken.None);

            // **書けなかったことを黙って飲まない**（§25-2）。
            if (written is ThreadWriteResult.Failed failed)
            {
                Note($"相談を書き残せなかった（{failed.Reason}）");
            }
            else if (written is ThreadWriteResult.Missing missing)
            {
                Note($"相談スレッドが見つからない（{missing.Reason}）");
                _composer.Shell.CurrentThreadId = null;
            }

            await _composer.RefreshThreadsAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            Note($"相談を書き残せなかった（{exception.GetType().Name}）");
        }
    }

    /// <summary>最初の発言からスレッドの名前を作る。<b>長い本文をそのまま名前にしない。</b></summary>
    private static string TitleFrom(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length switch
        {
            0 => "新しい相談",
            <= 30 => line,
            _ => line[..30] + "…",
        };
    }

    /// <summary>「＋ 新しい相談」（設計 §32-6）。</summary>
    private async void OnNewThread(object? sender, RoutedEventArgs e)
    {
        if (_composer?.Threads is null)
        {
            Note("フォルダを選ぶまで相談を始められません");
            return;
        }

        // **ここでは作らない。** 空のスレッドが並ぶと、一覧が「何を話したか」の
        // 目次ではなくなる。**次の発言で作られる**（RecordAsync）。
        _composer.Shell.CurrentThreadId = null;
        ShowTranscript([]);
        Say("新しい相談を始めます。下の入力欄から話しかけてください");
    }

    /// <summary>
    /// その相談を片付ける（設計 §45）。
    /// </summary>
    /// <remarks>
    /// <b>消さない。</b> <c>archive/threads/</c> へ移すだけである（§16-4）——
    /// 消えるのは<b>一覧からだけ</b>で、何を相談したかはフォルダに残る。
    /// <para>
    /// <b>開いている相談を片付けたら、新しい相談に戻す</b> ——
    /// 中身の無い id を掴んだままにすると、次の発言がどこにも書けない。
    /// </para>
    /// </remarks>
    private async void OnArchiveThread(object? sender, RoutedEventArgs e)
    {
        // **行のクリックに伝えない。** `Click` は上へ流れるので、そのままだと
        // 片付けた直後に**同じ相談を開こうとして「もう無い」**と言うことになる。
        e.Handled = true;

        if ((sender as Control)?.DataContext is not ThreadItem item
            || _composer?.Threads is not { } threads)
        {
            return;
        }

        var result = await threads.ArchiveAsync(item.Id, CancellationToken.None);
        Note(result switch
        {
            ThreadWriteResult.Written => $"相談を片付けた: {item.Title}（.company/archive/threads/ に残っている）",
            ThreadWriteResult.Missing missing => $"{item.Title}: {missing.Reason}",
            ThreadWriteResult.Failed failed => $"{item.Title}: {failed.Reason}",
            _ => $"{item.Title}: 片付けられなかった（{result.GetType().Name}）",
        });

        if (result is ThreadWriteResult.Written
            && string.Equals(_composer.Shell.CurrentThreadId, item.Id, StringComparison.Ordinal))
        {
            _composer.Shell.CurrentThreadId = null;
            ShowTranscript([]);
        }

        await _composer.RefreshThreadsAsync(CancellationToken.None);
    }

    /// <summary>左ペインで相談を選んだ（設計 §32-6）。</summary>
    private async void OnThreadSelected(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ThreadItem item
            || _composer?.Threads is not { } threads)
        {
            return;
        }

        var read = await threads.ReadAsync(item.Id, CancellationToken.None);
        switch (read)
        {
            case ThreadReadResult.Found found:
                _composer.Shell.CurrentThreadId = found.Meta.Id;
                ShowTranscript(found.Entries);

                // **飛ばした行を黙らせない**（§25-2）。
                if (found.SkippedLines > 0)
                {
                    Note($"{found.Meta.Title}: 読めなかった行が {found.SkippedLines} 行あった");
                }

                break;

            case ThreadReadResult.Missing:
                Note($"{item.Title}: もう無い");
                await _composer.RefreshThreadsAsync(CancellationToken.None);
                break;

            case ThreadReadResult.Unreadable unreadable:
                // **既定に落とさない**（§30-6）。読めないものを空として開くと、
                // そのまま書き足して元の記録を潰す。
                Note($"{item.Title}: 読めない（{unreadable.Reason}）");
                break;
        }
    }

    private void ShowTranscript(IReadOnlyList<ThreadEntry> entries)
    {
        if (DataContext is not ShellViewModel shell)
        {
            return;
        }

        shell.SecretaryTranscript.Clear();
        foreach (var entry in entries)
        {
            shell.SecretaryTranscript.Add(
                entry.Role is "human" ? $"あなた: {entry.Text}" : $"秘書: {entry.Text}");
        }

        // **空のときに仮の1行を積まない**（設計 §44-3、レビューで発覚）。
        // 積むと `HasTranscript` が真になり、**中央の空の見せ方が永久に出ない** ——
        // 「無い」を「1行ある」で表すと、画面は空かどうかを判断できなくなる。
    }

    /// <summary>
    /// タイルの「動いているか」を、いまの事実に合わせる（設計 §7）。
    /// </summary>
    /// <remarks><b>沈黙から導かない</b> —— <see cref="DepartmentRunner"/> が知っていることを写す。</remarks>
    private void RefreshRunning(string departmentId)
    {
        if (_composer is null || _runner is null)
        {
            return;
        }

        foreach (var tile in _composer.Shell.Departments.Where(t => t.Id == departmentId))
        {
            tile.SessionRunning = _runner.IsRunning(departmentId);
        }
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

            // 新しい発言まで追う（設計 §25-1）。**描画のあとに動かす** ——
            // 追加した直後は、まだ中身の高さが確定していない。
            Dispatcher.UIThread.Post(
                () => this.FindControl<ScrollViewer>("TranscriptScroll")?.ScrollToEnd(),
                DispatcherPriority.Background);
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
            || (sender as Control)?.DataContext is not DepartmentTile tile
            || Busy("部門の操作"))
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
                // 外部ターミナルの承認は、その窓で答える（設計 §61-6b）。
                if (tile.Mode is DriveMode.ExternalTerminal) await FocusDepartmentAsync(tile);
                else Note($"{tile.Name} の承認は中央ペインに出ている");
                break;

            // **報告はプレビューで読む**（設計 §62-9）。中央に全文を流さない。
            case DepartmentAction.ReadReport:
                if (tile.CurrentTaskSlug is { } reportSlug) OnTranscriptImageLink(ReportLink(reportSlug));
                else Note($"{tile.Name}: どの仕事の報告か分からない（.company/ の経路が未接続）");
                break;

            // §14-1: Dispatched は「送ったかもしれない」。**自動再送しない。**
            case DepartmentAction.CheckDelivery:
                Note($"{tile.Name}: 送られたか確かめる。指示は届いているかもしれない（自動で再送しない）");
                break;

            // §31-2: 沈黙は部門についての証拠ではない。**「失敗した」と言わない。**
            // 見せるのは観測だけで、待つ／取り消す／起動し直すの判断は人間がする（§15-4 と同じ姿勢）。
            case DepartmentAction.CheckMissingReport:
                Note($"{tile.Name}: 期限までに報告を観測していない。**失敗とは限らない** —— 観測を見て、待つか決める");
                ShowObservations(tile);
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
    /// <summary>
    /// 部門の設定を開く（設計 §47）。
    /// </summary>
    /// <remarks>
    /// <b>消してよいかは Core が決める</b>（<see cref="DepartmentRemoval"/>）——
    /// 画面はその結果を出すだけにする。判定に要る材料（仕事・計画・提案・権利・
    /// 読めない数・窓が動いているか）は、ここで集めて渡す。
    /// </remarks>
    private async void OnShowDepartmentSettings(object? sender, EventArgs e)
    {
        if (_composer?.Workspace is not { } workspace || _composer.Tasks is not { } tasks)
        {
            Note("フォルダを選ぶまで、部門の設定は開けません");
            return;
        }

        if (_settings is { } open)
        {
            if (open.WindowState is WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            return;
        }

        var window = new DepartmentSettingsWindow(
            new DepartmentStore(workspace.Company),
            DecideRemovalAsync,
            async departmentId =>
            {
                if (_runner is not null)
                {
                    await _runner.StopAsync(departmentId);
                }
            },
            agent => (DataContext as ShellViewModel)?.Trust.FirstOrDefault(row => row.Agent == agent));

        _settings = window;
        window.Closed += async (_, _) =>
        {
            if (ReferenceEquals(_settings, window))
            {
                _settings = null;
            }

            // **閉じたら読み直す。** 保存された設定は、開き直しで効く（§47）。
            if (_composer?.Workspace is { } current)
            {
                await OpenWorkspaceAsync(current.Root);
            }
        };

        window.Show(this);
        await window.LoadAsync();
    }

    /// <summary>
    /// その部門を消してよいか（設計 §47）。<b>材料を集めるだけ</b>で、判定は Core。
    /// </summary>
    /// <remarks>
    /// <b>そのとき読む。</b> 覚えている値で判定すると、**画面を開いている間に増えた仕事**を
    /// 見落とす —— 消してよいかは、押した瞬間の事実で決める（§7）。
    /// </remarks>
    private async Task<DepartmentRemovalDecision> DecideRemovalAsync(string departmentId)
    {
        if (_composer?.Tasks is not { } tasks)
        {
            return new DepartmentRemovalDecision.Blocked(["ワークスペースが選ばれていない"]);
        }

        var ct = CancellationToken.None;
        var recovery = await tasks.ScanForRecoveryAsync(ct);

        var states = new List<TaskState>();
        foreach (var slug in await tasks.ListSlugsAsync(ct))
        {
            if (await tasks.ReadAsync(slug, ct) is TaskReadResult.Found found)
            {
                states.Add(found.State);
            }
        }

        var plans = new List<string>();
        if (_composer.Plans is { } planStore)
        {
            foreach (var id in await planStore.ListIdsAsync(ct))
            {
                if (await planStore.ReadAsync(id, ct) is PlanReadResult.Found plan
                    && plan.Plan.Steps.Any(step => string.Equals(step.DepartmentId, departmentId, StringComparison.Ordinal)))
                {
                    plans.Add(id);
                }
            }
        }

        var proposals = _composer.Outbox is { } outbox
            ? outbox.Read()
                .Where(proposal => string.Equals(proposal.DepartmentId, departmentId, StringComparison.Ordinal))
                .Select(proposal => proposal.Id)
                .ToArray()
            : [];

        var holdsLease = _composer.Leases is { } leases
            && await leases.ReadAsync(ct) is LeaseReadResult.Found found2
            && found2.Leases.Holders.TryGetValue(LeaseKind.Write, out var holder)
            && holder.Holder.Kind is ActorKind.Department
            && string.Equals(holder.Holder.Id, departmentId, StringComparison.Ordinal);

        return DepartmentRemoval.Decide(
            departmentId, states, plans, proposals, holdsLease,
            recovery.Unreadable.Count, _runner?.IsRunning(departmentId) is true);
    }

    /// <summary>
    /// アイコンを実寸で並べて見る（設計 §15-5）。
    /// </summary>
    /// <remarks>
    /// <b>ふだんの画面には6つとも出ない</b> —— ポーズは活動状態と1対1なので、
    /// その状態にならないと見えない。**絵を直したかどうかは、ここで見る。**
    /// </remarks>
    private void OnShowIconGallery(object? sender, EventArgs e)
    {
        // **押すたびに窓を増やさない。** 既に開いているなら、それを前に出す。
        if (_iconGallery is { } open)
        {
            // **最小化されていると `Activate()` だけでは戻らない**（レビューで指摘）——
            // 人間には「押しても何も起きない」に見える。
            if (open.WindowState is WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            return;
        }

        var gallery = new IconGalleryWindow();
        _iconGallery = gallery;
        gallery.Closed += (_, _) =>
        {
            if (ReferenceEquals(_iconGallery, gallery))
            {
                _iconGallery = null;
            }
        };

        gallery.Show(this);
    }

    private void OnShowDiagnostics(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not DepartmentTile tile)
        {
            return;
        }

        Select(tile);

        List<string> lines =
        [
            $"稼働 {tile.RuntimeText} / 活動 {tile.ActivityText} / 仕事 {tile.WorkText}",
            _runner?.IsRunning(tile.Id) is true
                ? $"プロセス: 動いている（{tile.Agent} / {tile.Mode}）"
                : $"プロセス: 動いていない（{tile.Agent} / {tile.Mode}）",

            // **観測と設定を分けて、出典つきで出す**（設計 §27-3）。
            // 見出しは狭いので観測値だけ。ここでは両方見せて、食い違いに気付けるようにする。
            tile.ObservedModel is { } observed
                ? $"モデル（CLI の申告）: {observed.Id}"
                    + (observed.ReasoningEffort is { } effort ? $" / 思考の強さ: {effort}" : " / 思考の強さ: 申告なし")
                : "モデル（CLI の申告）: まだ申告されていない",
            _composer?.DefinitionOf(tile.Id).Model is { } configured
                ? $"モデル（こちらの設定）: {configured}"
                : "モデル（こちらの設定）: 指定なし（CLI の既定に任せる）",

            // **観測が無いことを「正常」と読ませない**（§7）。
            tile.RecentObservations.Count is 0
                ? "観測: まだ何も観測していない"
                : $"観測（最新）: {tile.RecentObservations[0]}",
            tile.Diagnostics.Summary,
        ];

        // 新しい順のまま並べる。1件にまとめるので、作業ログの時系列は崩れない（§25-2）。
        lines.AddRange(tile.Diagnostics.Recent(10).Select(d => $"[{d.Stream}] {d.Text}"));
        NoteBlock($"—— {tile.Name} の診断 ——", lines);
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
    /// <summary>
    /// 有効な保持者がいて渡せないとき（設計 §14-2）。
    /// </summary>
    /// <remarks>
    /// <b>「待つ」と言い切らない</b>（2026-09-09 に実機で踏んだ）。
    /// 保持者の仕事が既に終端（<c>Failed</c> など）なら、**待っても、その仕事はもう動かない** ——
    /// そのとき人間がすべきことは「待つ」ではなく「その部門の権利を外す」である。
    /// <b>アプリは勝手に外さない</b>（§14-2 の「失効は停止の証拠ではない」と同じ理由で、
    /// 終端も停止の証拠ではない）。**言い方だけを正す。**
    /// </remarks>
    private static string BlockedText(DispatchResult.Blocked blocked) =>
        blocked.HolderWorkIsOver

            // **「待つ」と言い切らない。** 保持者の仕事が終端なら、待っても空かない。
            ? blocked.Reason
            : $"{blocked.Reason}（失敗ではない。待つ）";

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
    private async Task FocusDepartmentAsync(DepartmentTile tile)
    {
        // 通常の前面化と承認の用件で同じ処理を使う（設計 §61-6b）。
        if (!await _runner!.FocusAsync(tile.Id, CancellationToken.None))
            Note($"{tile.Name}: ターミナルを前面に出せなかった（窓が閉じられている可能性があります）");
    }

    private async void OnDepartmentStart(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not DepartmentTile tile || Busy("部門の起動"))
        {
            return;
        }

        Select(tile);

        // **外部ターミナルの部門は「前面に出す」**（設計 §32）。
        // あちらは窓を**仕事を渡したときに**開くので、ここで起こすものが無い。
        if (tile.Call.Lifecycle is DepartmentLifecycle.Focus)
        {
            await FocusDepartmentAsync(tile);

            return;
        }

        // **起動していないなら走査しない**（設計 §26-1）。切り替えで捨てられた場合、
        // ここで走査すると前のフォルダを触りに行くか、新しいフォルダの起動時走査を潰す。
        if (await StartAsync(tile))
        {
            await ScanAsync(CompanyScanKind.Periodic);
        }
    }

    /// <summary>
    /// 指示書はあるが渡していない仕事を渡す（設計 §15-6）。
    /// 「仕事にする」は<b>提案を仕事に昇格させる操作</b>であって、
    /// <summary>
    /// 部門の turn が失敗して終わったので、抱えている仕事を <c>Failed</c> にする（設計 §30-3）。
    /// </summary>
    /// <remarks>
    /// <b>1件も無くても黙らない。</b> 「仕事は無かった」も観測なので作業ログに出す ——
    /// 出さないと、失敗が消えたのか、そもそも仕事が無かったのかが区別できない（§7）。
    /// </remarks>
    private async Task FailInFlightAsync(string departmentId, string workspaceRoot, string because)
    {
        if (_composer?.Dispatcher is not { } dispatcher || _composer.Workspace is not { } workspace) return;

        // **前のフォルダの失敗で、いまのフォルダの仕事を落とさない**（レビューで発覚、§26-1）。
        // 切り替えは「選ぶ」が先で「前の部門を止める」が後（§26-2b）なので、
        // ここには前のフォルダの turn 失敗が遅れて届く。
        // **綴りではなく鍵で比べる**（レビューで発覚）。`/tmp` と `/private/tmp`、symlink、
        // Windows の大小 —— 切り替えの判定は `KeyOf` を使っている（§26-1）ので、
        // ここだけ生の文字列で比べると、**同じフォルダを開き直しただけで失敗が捨てられる。**
        if (!string.Equals(
                WorkspaceInstanceLock.KeyOf(workspace.Root),
                WorkspaceInstanceLock.KeyOf(workspaceRoot),
                StringComparison.Ordinal))
        {
            Note($"前のフォルダの失敗が届いた（{because}）。**いまのフォルダの仕事は動かさない**");
            return;
        }

        var name = _composer.DefinitionOf(departmentId).DisplayName;
        try
        {
            var failed = await dispatcher.FailInFlightAsync(departmentId, because, CancellationToken.None);
            if (failed.Count is 0)
            {
                Note($"{name}: turn が失敗した（{because}）。**抱えている仕事は無い**");
                return;
            }

            foreach (var task in failed)
            {
                Note($"{task.Slug}: 失敗した —— {task.Reason}");
            }
        }
        catch (Exception exception)
        {
            NoteException($"{name} の失敗を書けなかった", exception);
        }
    }

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

        var result = await LaunchIfTerminalAsync(
            await dispatcher.DispatchAsync(
                found.State, _composer.DefinitionOf(tile.Id), SessionOf(tile.Id),
                TimeSpan.FromMinutes(30), CancellationToken.None),
            tile.Id);

        Note(result switch
        {
            DispatchResult.Dispatched => $"{tile.Name} に {slug} を渡した",

            // **積んだだけなら「渡した」と言わない**（設計 §32-12）。いずれ書かれるので失敗ではない。
            DispatchResult.QueuedForNextTurn queued =>
                $"{tile.Name} は前の turn を処理中。{slug} を**順番待ちに入れた**（待ち {queued.Ahead} 件）",
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
    /// <summary>
    /// その仕事を取り消す（設計 §44-5）。
    /// </summary>
    /// <remarks>
    /// <b>「消す」ではない。</b> <c>state.json</c> も文書も残り、状態が
    /// <c>Cancelled</c> になるだけ（§16-4 の「消さずに移す」と同じ姿勢）——
    /// 消えるのは<b>タイルの上から</b>である。
    /// <para>
    /// <b>実行の停止は保証しない。</b> 部門が動いていれば、それは動き続ける ——
    /// 窓を閉じるのは人間の仕事（§14-2 の「失効は停止の証拠ではない」と同じ）。
    /// </para>
    /// </remarks>
    private async void OnCancelTask(object? sender, RoutedEventArgs e)
    {
        if (Busy("仕事の取り消し"))
        {
            return;
        }

        if (_composer?.Tasks is not { } tasks || (sender as Control)?.DataContext is not DepartmentTile tile
            || tile.CurrentTaskSlug is not { } slug)
        {
            return;
        }

        Select(tile);
        if (await tasks.ReadAsync(slug, CancellationToken.None) is not TaskReadResult.Found found)
        {
            Note($"{slug}: 状態を読めないので取り消せない");
            return;
        }

        var write = await tasks.TransitionAsync(
            found.State, CoreTaskStatus.Cancelled, TransitionOrigin.Human,
            "人間が取り消した（実行の停止は保証しない）", CancellationToken.None);

        // **取り消したら書き込み権を返す。** 受理と同じ理由（人間がそう決めたから）——
        // 返さないと、要らなくなった仕事のためにワークスペースが失効まで塞がる。
        if (write is TaskWriteResult.Written && _composer?.Dispatcher is { } releasing
            && await releasing.ReleaseWriteLeaseIfIdleAsync(
                _composer.DefinitionOf(tile.Id), CancellationToken.None))
        {
            Note($"{tile.Name}: 書き込み権を返した（抱えている仕事が無くなった）");
        }

        Note(write switch
        {
            TaskWriteResult.Written =>
                $"{tile.Name}: {slug} を取り消した（部門が動いていれば止まっていない）",
            TaskWriteResult.Conflicted conflicted => $"{slug}: {conflicted.Reason}",
            TaskWriteResult.Rejected rejected => $"{slug}: {rejected.Reason}",
            _ => $"{slug}: 取り消せなかった（{write.GetType().Name}）",
        });

        await ScanAsync(CompanyScanKind.Periodic);
    }

    private async void OnAcceptReport(object? sender, RoutedEventArgs e)
    {
        if (Busy("報告の受理"))
        {
            return;
        }

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

        // **受理したら書き込み権を返す**（2026-09-09 に実機で踏んだ）。
        // ここが無いと、**仕事を1つ終えるたびにワークスペースが失効まで塞がる。**
        // 返してよい理由は「アプリがそう推測した」ではなく、
        // **人間が報告を読んで受理した**から —— §14-2 が禁じているのは
        // アプリの推測で外すことであって、人間の確認に従うことではない。
        if (write is TaskWriteResult.Written && _composer?.Dispatcher is { } releasing
            && await releasing.ReleaseWriteLeaseIfIdleAsync(
                _composer.DefinitionOf(tile.Id), CancellationToken.None))
        {
            Note($"{tile.Name}: 書き込み権を返した（抱えている仕事が無くなった）");
        }

        // **受理できたときだけねぎらう**（設計 §52-4）。起きたことへの反応なので、嘘にならない。
        if (write is TaskWriteResult.Written)
        {
            tile.Cheer();
        }

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
        if (Busy("差し戻し"))
        {
            return;
        }

        if (_composer is not { Tasks: { } tasks, Dispatcher: { } dispatcher, Workspace: { } workspace }
            || (sender as Control)?.DataContext is not DepartmentTile tile)
        {
            return;
        }

        Select(tile);

        var reason = tile.RejectionDraft;
        if (string.IsNullOrWhiteSpace(reason))
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
            await CompanyInstruction.ComposeRevisionAsync(
                workspace.Company, state.Slug, _composer.DefinitionOf(tile.Id),
                "差し戻しの理由", "人間", reason, CancellationToken.None),
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
        var result = await LaunchIfTerminalAsync(
            await dispatcher.RedispatchAsync(
                state, _composer!.DefinitionOf(tile.Id), SessionOf(tile.Id),
                TimeSpan.FromMinutes(30), CancellationToken.None),
            tile.Id);

        Note(result switch
        {
            DispatchResult.Dispatched => $"{tile.Name} に {state.Slug} を差し戻して送り直した",
            DispatchResult.QueuedForNextTurn queued =>
                $"{tile.Name} は前の turn を処理中。{state.Slug} を**順番待ちに入れた**（待ち {queued.Ahead} 件）",
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

    private async Task<bool> StartAsync(DepartmentTile tile)
    {
        if (_composer?.Workspace is not { } workspace || _runner is null)
        {
            Note("先にワークスペースを選ぶ（部門は選んだフォルダで動く）");
            return false;
        }

        var definition = _composer.DefinitionOf(tile.Id);

        var started = await _runner.StartAsync(definition, workspace, CancellationToken.None);
        tile.SessionRunning = _runner.IsRunning(tile.Id);
        Note(started switch
        {
            DepartmentStart.Started => $"{tile.Name} を起動した",
            DepartmentStart.Failed failed => failed.Reason,

            // **もう動いているものを「起動した」と言わない**（レビューで発覚、§27-4）。
            DepartmentStart.AlreadyRunning => $"{tile.Name} は既に動いている（何もしなかった）",
            DepartmentStart.AlreadyStarting => $"{tile.Name} は起動処理中（二重には起動しない）",

            // 切り替えで捨てた。**起動したことにしない**（§26-1）。
            DepartmentStart.Superseded => $"{tile.Name} の起動は、ワークスペースが変わったので取り消した",
            _ => $"{tile.Name}: 知らない起動結果（{started.GetType().Name}）",
        });

        return started is DepartmentStart.Started;
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
    /// 秘書に送る。<b>未起動ならその副作用として起動する</b>（設計 §17-4）——
    /// ワークスペース選択に起動を隠さない。
    /// </summary>
    private async void OnSendToSecretary(object? sender, RoutedEventArgs e)
    {
        var body = PeekMessage();
        var drafts = (DataContext as ShellViewModel)?.PendingAttachments.ToArray() ?? [];
        // **本文が空でも添付があれば送れる**（設計 §58-4）。
        if (_secretary is null || _composer is null || (body is null && drafts.Length == 0)
            || Busy("秘書への送信"))
        {
            return;
        }

        // 複製の間に Enter をもう一度押されても、同じ添付を2回送らない。
        if (_copyingAttachments)
        {
            Note("添付を複製している最中（終わってから送る）");
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

        var text = body ?? "";
        if (drafts.Length > 0)
        {
            // **複製は送るとき**（§58-2）。失敗したら送らず、入力と添付を残す（§17-4）。
            AttachmentCopyResult copied;
            _copyingAttachments = true;
            try { copied = await Attachments.CopyAsync(workspace.Company, drafts, DateTimeOffset.Now, CancellationToken.None); }
            finally { _copyingAttachments = false; }

            if (copied is not AttachmentCopyResult.Copied { Links: var links })
            {
                Note($"送れなかった: {(copied as AttachmentCopyResult.Failed)?.Reason}（入力と添付はそのまま）");
                return;
            }
            text = Attachments.Compose(body, links);
            if (DataContext is ShellViewModel shell)
            {
                foreach (var draft in drafts) shell.PendingAttachments.Remove(draft);
            }
        }

        ClearMessage();
        SayAndRecord("human", text);

        // **1通にまとめて送る**（設計 §32-12）。protocol の案内と人間の本文を
        // 別々の turn にすると、1通目のツール実行中に2通目が割り込む。
        var toSend = _secretaryNeedsProtocol
            ? SecretaryReadme.StartupMessage(workspace.Company, text)
            : text;

        var outcome = await _secretary.SendAsync(toSend, CancellationToken.None);

        // **積まれたことを黙らない**（設計 §32-12）。走っている turn があると
        // その場では書かれない —— 言わないと「送ったのに何も起きない」になる（§7）。
        if (!outcome.Written)
        {
            Note($"秘書はまだ前の依頼を処理中。**順番待ちに入れた**（待ち {outcome.Queued} 件）");
            Say($"（前の依頼を処理中なので、順番待ちに入れました。待ち {outcome.Queued} 件）");
        }

        // **送れてから降ろす。** 先に降ろすと、送信で落ちたとき
        // protocol を読ませないまま次へ進む（§7 の「していないことをしたことにしない」）。
        _secretaryNeedsProtocol = false;
    }

    /// <summary>
    /// 秘書が publish した計画を取り込み、走っている計画を1つ進める（設計 §37）。
    /// </summary>
    /// <remarks>
    /// <b>人間の受理を挟まない</b>（§37-3）。人間の出番は<b>割り込みだけ</b>で、
    /// 報告は出た瞬間に中央へ出る（§34-2）ので、流れてくるのを読んでいて止められる。
    /// </remarks>
    private async Task AdvancePlansAsync()
    {
        if (_composer is not { Plans: { } store, PlanRunner: { } runner, Outbox: { } outbox }
            || DataContext is not ShellViewModel shell)
        {
            return;
        }

        // **走査より前に取り込む**（§34-1 と同じ理由）—— あとから拾うと
        // 「計画を始めた」が一瞬見えてから消える。
        foreach (var published in outbox.ReadPlans())
        {
            // **outbox のファイル名を計画の ID にしない**（レビューで発覚）。
            // 秘書が付ける名前は日本語や空白を含み得る —— そのまま渡すと
            // `CompanyPaths.RequireSlug` が投げて、**走査そのものが落ちる。**
            // 仕事と同じく、こちらで名前を決める。取り込みの重複は
            // **outbox から移すこと**で防ぐ（§17-6 の提案と同じ形）。
            var id = $"plan-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
            var created = await store.CreateAsync(
                id, published.Goal, published.Steps, CancellationToken.None, published.Brief);
            Note(created is PlanWriteResult.Written
                ? $"計画を受け取った: {published.Goal}（{published.Steps.Count} 工程）"
                : $"計画を作れなかった: {published.Id}");

            if (created is PlanWriteResult.Written)
            {
                // ここまで済んでから移す（§17-6 と同じ順序）。
                outbox.Accept(published.Id, id, DateTimeOffset.Now);
            }
        }

        var hands = new PlanHands(
            id => _composer.KnowsDepartment(id) ? _composer.DefinitionOf(id) : null,
            SessionOf,
            async (result, departmentId, ct) => await LaunchIfTerminalAsync(result, departmentId));

        foreach (var id in await store.ListIdsAsync(CancellationToken.None))
        {
            if (await store.ReadAsync(id, CancellationToken.None) is not PlanReadResult.Found found)
            {
                continue;
            }

            var tick = await runner.StepAsync(found.Plan, hands, CancellationToken.None);
            ShowPlan(shell, found.Plan, tick);

            if (tick is PlanTick.Acted acted)
            {
                Note($"計画「{found.Plan.Goal}」: {acted.Note}");
            }

            // **1周に1つの計画だけ動かす。** 複数を同時に進めると、
            // 書き込み権を取り合って**どちらも進まない**（§14-2）。
            if (tick is not PlanTick.Done)
            {
                return;
            }
        }
    }

    /// <summary>計画の帯に出す1行（設計 §37-3）。<b>状態は動かさない。</b></summary>
    private void ShowPlan(ShellViewModel shell, Plan plan, PlanTick tick)
    {
        shell.PlanStopped = plan.StoppedByHuman;
        shell.PlanStatus = tick switch
        {
            PlanTick.Done => null,
            PlanTick.Stopped stopped when plan.StoppedByHuman =>
                $"計画「{plan.Goal}」を止めている（{stopped.Reason}）",
            PlanTick.Stopped stopped => $"計画「{plan.Goal}」が止まった: {stopped.Reason}",
            PlanTick.Idle idle => $"計画「{plan.Goal}」を進めている（{idle.Reason}）",
            PlanTick.Acted acted => $"計画「{plan.Goal}」: {acted.Note}",
            _ => null,
        };
    }

    /// <summary>計画を止める・続ける（設計 §37-3）。</summary>
    /// <remarks>
    /// <b>止めるのは「次を渡さない」こと。</b> 走っている窓は閉じない ——
    /// 動いている部門を殺さない（§32-8 と同じ姿勢）。その窓は人間が直接止められる。
    /// <para>
    /// <b>「続ける」が無いと、一度止めた計画を進める手段が画面から消える</b>
    /// （§19-1 / §30-2 で3度踏んだ形）。
    /// </para>
    /// </remarks>
    private async Task SetPlanStoppedAsync(bool stopped)
    {
        if (_composer?.Plans is not { } store || Busy("計画の操作"))
        {
            return;
        }

        foreach (var id in await store.ListIdsAsync(CancellationToken.None))
        {
            if (await store.ReadAsync(id, CancellationToken.None) is not PlanReadResult.Found found
                || found.Plan.StoppedByHuman == stopped)
            {
                continue;
            }

            var write = await store.WriteAsync(
                found.Plan, found.Plan with { StoppedByHuman = stopped }, CancellationToken.None);
            Note(write is PlanWriteResult.Written
                ? $"計画「{found.Plan.Goal}」を{(stopped ? "止めた（走っている窓は閉じない）" : "続ける")}"
                : $"計画を書き換えられなかった: {id}");
        }

        await ScanAsync(CompanyScanKind.Periodic);
    }

    private async void OnStopPlan(object? sender, RoutedEventArgs e) => await SetPlanStoppedAsync(true);

    private async void OnResumePlan(object? sender, RoutedEventArgs e) => await SetPlanStoppedAsync(false);

    /// <summary>
    /// 秘書へ送った turn の終わりを、期限までに観測しているか（設計 §36）。
    /// </summary>
    /// <remarks>
    /// <b>秘書についての主張はしない。</b> 出すのは「アプリが turn の終わりを観測していない」
    /// という、アプリ自身についての事実だけで、状態は何も動かさない（§31-5）。
    /// </remarks>
    private void UpdateSecretaryTurnWatch()
    {
        if (DataContext is not ShellViewModel shell || _secretary is null)
        {
            return;
        }

        // **承認を出したまま押されていないなら、待たせているのはこちら**（§31-2 と同じ）。
        // ここを見ないと、人間が承認を放置しているだけで「立て直せ」と言うことになる。
        // **写しではなく正本を見る**（レビュー2周目で発覚）—— 画面の一覧は post 越しに
        // 追いかけているので、並ぶ前・消える前の隙間がある。
        var awaitingHuman = shell.Approvals.HasPending(ApprovalSource.Secretary);

        var now = TimeProvider.System.GetUtcNow();
        var activity = _secretary.Activity;

        var silence = TurnWatch.Of(
            activity, awaitingHuman, TurnWatch.DefaultDeadline, now);

        if (silence is null)
        {
            shell.SecretaryStalledText = null;
            _secretaryStalledNoticed = null;
            return;
        }

        var waiting = silence.Queued > 0
            ? $"。次の {silence.Queued} 件は送られないまま待っている"
            : string.Empty;
        var line = $"秘書に送ってから {Math.Round(silence.Elapsed.TotalMinutes)} 分、turn の終わりを観測していない{waiting}";

        shell.SecretaryStalledText = $"{line}。**秘書が生きているかは分からない** —— 立て直すと、いまの turn は捨てられる";

        // **一度だけ言う**（§31-6）。turn が変わったら、そのときまた言う。
        if (_secretaryStalledNoticed != silence.Since)
        {
            _secretaryStalledNoticed = silence.Since;
            Note($"{line}。**失敗とは書かない** —— 観測していないだけである");
        }
    }

    /// <summary>
    /// 秘書を立て直す（設計 §36-3）。<b>終わらせてから、いつもの起動経路で起こす。</b>
    /// </summary>
    /// <remarks>
    /// <b>待ち行列を人間のボタンで開けるのではない。</b> 開けても観測は増えておらず、
    /// 前の turn が生きていれば割り込みが起きる（§32-12）。**観測できる終端**を作る ——
    /// プロセスを終わらせる。
    /// <para>
    /// <b>積んであったものは送り直さない</b>（§14-1）。仕事の指示書なら
    /// <c>.company/tasks/</c> に残っていて、状態は <c>Dispatched</c>（送ったかもしれない）のまま
    /// §16-3 の復旧走査が拾う。人間の相談なら、もう一度打てばよい。
    /// </para>
    /// </remarks>
    private async void OnRestartSecretary(object? sender, RoutedEventArgs e)
    {
        if (_secretary is null || _composer?.Workspace is not { } workspace || Busy("秘書を立て直す操作"))
        {
            return;
        }

        Note("秘書を立て直す（いまの turn は捨てる。積んだ依頼は送り直さない）");
        // **会話に残さない。** `SayAndRecord` は役を「人間か秘書か」でしか書けないので、
        // ここで使うと**アプリの断りを秘書の発言として記録する**ことになる（§17-3）。
        Say("（秘書を立て直しました。前の依頼は送り直していません）");

        await _secretary.StopAsync();

        // **印はここで降ろす。** 新しいセッションには走っている turn が無いので
        // 次の走査でも消えるが、押した手応えを5秒待たせない。
        if (DataContext is ShellViewModel shell)
        {
            shell.SecretaryStalledText = null;
        }

        _secretaryStalledNoticed = null;

        // **起動の経路は1つ**（§17-4）—— README を置く → 起動する → 1通目に読ませる。
        if (!await EnsureSecretaryAsync(workspace))
        {
            Note("秘書を起こし直せなかった。観測を見る");
        }

        UpdateSecretaryStatus();
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

        // **ここでは送らない**（設計 §32-12）。ここで protocol を送り、呼び出し元が
        // 続けて本文を送ると、**1通目がツールを実行している最中に2通目が割り込む** ——
        // `SendUserMessageAsync` は行を書くだけで turn の完了を待たない。
        // 送るのは呼び出し元で、**1通にまとめる**（SecretaryReadme.StartupMessage）。
        _secretaryNeedsProtocol = true;
        return true;
    }

    /// <summary>
    /// 次の送信に protocol の案内を混ぜるか（設計 §17-6）。
    /// </summary>
    /// <remarks>
    /// <b>「送った」ではなく「まだ送っていない」を持つ。</b> 起動しただけでは
    /// 秘書は protocol を読んでいないので、**最初の1通に必ず混ぜる。**
    /// </remarks>
    private bool _secretaryNeedsProtocol;

    /// <summary>
    /// 選んだ部門に仕事を作る（設計 §17-4 で右ペインへ移した）。
    /// <c>instruction.md</c> を書き、<see cref="TaskDispatcher"/> に渡す（§6 / §16-2）。
    /// </summary>
    private async void OnMakeTask(object? sender, RoutedEventArgs e)
    {
        if (Busy("仕事を作る操作"))
        {
            return;
        }

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
            CompanyInstruction.Compose(text, workspace.Company, slug, _composer.DefinitionOf(tile.Id)),
            CancellationToken.None);

        var result = await LaunchIfTerminalAsync(
            await dispatcher.DispatchAsync(
                created.State, _composer.DefinitionOf(tile.Id), SessionOf(tile.Id),
                TimeSpan.FromMinutes(30), CancellationToken.None),
            tile.Id);

        Note(result switch
        {
            DispatchResult.Dispatched => $"{tile.Name} に {slug} を渡した",
            DispatchResult.Blocked blocked => $"{slug}: {BlockedText(blocked)}",
            DispatchResult.BlockedByExpiredLease expired => $"{slug}: {ExpiredLeaseText(expired)}",
            DispatchResult.SentUncertain uncertain => $"{slug}: {uncertain.Reason}。**届いたか確かめる**",
            DispatchResult.Rejected rejected => $"{slug}: {rejected.Reason}",
            DispatchResult.Conflicted conflicted => $"{slug}: {conflicted.Reason}",

            // **積んだだけなら「渡した」と言わない**（設計 §32-12、レビュー2周目で発覚）。
            // ここを足し忘れていたので「不明な結果」に落ちていた。
            DispatchResult.QueuedForNextTurn pending =>
                $"{tile.Name} は前の turn を処理中。{slug} を**順番待ちに入れた**（待ち {pending.Ahead} 件）",
            _ => $"{slug}: 不明な結果",
        });

        await ScanAsync(CompanyScanKind.Periodic);
    }

    /// <summary>複製の最中（設計 §58-2）。</summary>
    private bool _copyingAttachments;

    /// <summary>ファイルを含むドラッグだけを受け付ける（設計 §58-6）。</summary>
    private void OnDragOverAttachment(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// 落とされたファイルを、送る前の添付に足す（設計 §58-3）。<b>ここでは複製しない。</b>
    /// 越えたものだけ理由を付けて弾く。
    /// </summary>
    private void OnDropAttachment(object? sender, DragEventArgs e)
    {
        if (DataContext is not ShellViewModel shell || e.DataTransfer.TryGetFiles() is not { } items)
        {
            return;
        }
        e.Handled = true;

        try
        {
            var paths = new List<string>();
            foreach (var item in items)
            {
                if (item.TryGetLocalPath() is { } path) paths.Add(path);
                else Note($"{item.Name}: この場所のファイルは添付できない（手元のパスが無い）");
            }

            var admission = Attachments.Admit(shell.PendingAttachments.ToArray(), paths);
            foreach (var draft in admission.Accepted) shell.PendingAttachments.Add(draft);
            foreach (var reason in admission.Rejections) Note($"添付できない: {reason}");
            if (admission.Accepted.Count > 0) this.FindControl<TextBox>("MessageBox")?.Focus();
        }
        catch (Exception exception)
        {
            // 落としたことでアプリを落とさない（§49）。
            Note($"添付を受け取れなかった（{exception.GetType().Name}）");
        }
    }

    private void OnRemoveAttachment(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AttachmentDraft draft } && DataContext is ShellViewModel shell)
        {
            shell.PendingAttachments.Remove(draft);
        }
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
        if (_composer is null || _scanTask is not null)
        {
            return;
        }

        var task = ScanCoreAsync(kind);
        _scanTask = task;
        try
        {
            await task;
        }
        finally
        {
            _scanTask = null;
        }
    }

    /// <summary>
    /// 走っている走査が終わるのを待つ（設計 §26-1）。
    /// </summary>
    /// <remarks>
    /// <b>切り替えの前に要る。</b> 5秒タイマーの走査が前のフォルダを読んでいる最中に
    /// ロックを返すと、**まだ触っているフォルダを別のアプリが開ける**（レビューで発覚）。
    /// </remarks>
    private async Task WaitForScanAsync()
    {
        if (_scanTask is { } running)
        {
            try
            {
                await running;
            }
            catch (Exception)
            {
                // 走査の失敗はここでは扱わない。待つことだけが目的。
            }
        }
    }

    /// <summary>
    /// 外部ターミナルの部門なら、ここで窓を開く（設計 §32）。
    /// </summary>
    /// <remarks>
    /// <b>状態は `TaskDispatcher` が既に書いている。</b> ここでやるのは
    /// プロセスを起こすことだけ —— §9 の「アプリが全部門の親になる」を守るために、
    /// Core ではなくここで起こす。
    /// <para>
    /// 呼び出し元の結果表示を増やさずに済むよう、<b>既にある結果に畳んで返す。</b>
    /// 開けたら <c>Dispatched</c>、開けなければ <c>SentUncertain</c> ——
    /// どちらも状態は <c>Dispatched</c> のままで、**自動で送り直さない**（§14-1）。
    /// </para>
    /// </remarks>
    private async Task<DispatchResult> LaunchIfTerminalAsync(DispatchResult result, string departmentId)
    {
        if (result is not DispatchResult.LaunchTerminal launch
            || _composer?.Workspace is not { } workspace || _runner is null)
        {
            return result;
        }

        // **protocol は窓を開く前に置く。** 起動時に渡すのは「これを読んで」だけ。
        await _composer.WriteDepartmentProtocolAsync(CancellationToken.None);

        // **開き直してよいかは、仕事の状態で決める**（設計 §32-8）。
        // 3つの CLI は turn が終わってもセッションを終了しない（§32-2e）ので、
        // **2つ目の仕事はいつも「窓が既にある」状態で来る。**
        // 前の仕事が終わっているなら、その窓の CLI は待っているだけなので開き直してよい。
        var busy = _composer.Dispatcher is { } dispatcher
            && await dispatcher.HasWorkInFlightAsync(
                departmentId, launch.State.Slug, CancellationToken.None);

        var started = await _runner.StartTerminalAsync(
            _composer.DefinitionOf(departmentId), workspace, launch.Request, CancellationToken.None,
            replaceExisting: !busy);

        // **開いたなら、タイルにもそう出す。** ここを忘れると
        // 「ターミナルを前面に出す」が出ないまま窓だけが在る（レビューで発覚）。
        RefreshRunning(departmentId);

        return started switch
        {
            DepartmentStart.Started => new DispatchResult.Dispatched(launch.State),

            // **確かめられていないものを「渡した」と言わない**（設計 §41）。
            // 状態は `Dispatched` のままで、人間が窓を見て確かめる（§14-1）。
            DepartmentStart.StartedUnverified uncertain =>
                new DispatchResult.SentUncertain(
                    launch.State, $"{uncertain.Reason}。**その窓で受け取れたか確かめる**"),

            // **ここへ来るのは「前の仕事がまだ動いている」ときだけ**（設計 §32-8）。
            // 終わっていれば上で開き直している。動いている部門を殺さない。
            // **「渡した」と言わない**（§7）—— 状態は `Dispatched`（§14-1）のまま、
            // 人間に確かめさせる。自動で送り直さない。
            DepartmentStart.AlreadyRunning or DepartmentStart.AlreadyStarting =>
                new DispatchResult.SentUncertain(
                    launch.State,
                    "その部門は**まだ前の仕事を抱えている**。新しい指示は読まれていない —— "
                    + "前の仕事が終わってから渡し直す（その窓で直接伝えてもよい）"),

            DepartmentStart.Failed failed =>
                new DispatchResult.SentUncertain(launch.State, $"{failed.Reason}。窓が開いていないか確かめる"),

            // 切り替えの最中だった。**前のフォルダの話なので、ここでは何も言わない**（§26-1）。
            _ => new DispatchResult.SentUncertain(launch.State, "切り替えの最中だったので窓を開かなかった"),
        };
    }

    private async Task ScanCoreAsync(CompanyScanKind kind)
    {
        // 呼び出し元（ScanAsync）が既に弾いているが、**不変条件をここにも書く** ——
        // 書かないとコンパイラの null 警告を抑えるだけになり、
        // 前提が変わったときに気付けない。
        if (_composer is not { } composer)
        {
            return;
        }

        // **走査より前に拾う**（2026-09-11、実機で人間が見つけた）。
        // 走査の中でカードを作り直すので、あとから受理すると
        // **「仕事にする」が一瞬出てから消える。**
        await AutoAcceptProposalsAsync();

        var result = await composer.ScanAsync(kind, CancellationToken.None);
        var arrived = new List<AppliedTransition>();
        foreach (var applied in result?.Applied ?? [])
        {
            Note($"{applied.Slug}: {applied.From} → {applied.To}（{applied.Because}）");

            // **報告は、出た瞬間に中央へ出す**（設計 §19-4、2026-09-11 に人間が決めた）。
            // ボタンを1つ挟むと、**報告が来たこと自体に気付いてから読む**ことになる。
            // **質問も同じ**（設計 §57、2026-09-16 に人間が決めた）。「質問に答える」ボタンは廃した。
            if (applied.To is CoreTaskStatus.Reported or CoreTaskStatus.AwaitingAnswer)
            {
                arrived.Add(applied);
            }
        }

        foreach (var blocked in result?.Blocked ?? [])
        {
            Note($"{blocked.Slug}: {blocked.From} → {blocked.To} を書けなかった（{blocked.Reason}）");
        }

        foreach (var line in composer.DrainSilenceNotices())
        {
            Note(line);
        }

        // **動いている仕事の書き込み権を、切れる前に更新する**（設計 §24-4）。
        // **渡すより先にやる** —— 先に渡そうとすると、切れかけの権利で弾かれる。
        if (composer.Dispatcher is { } leases
            && await leases.RenewWriteLeaseForInFlightAsync(TimeSpan.FromMinutes(30), CancellationToken.None)
                is { } renewed && _renewedLease.Add(renewed))
        {
            // **1度だけ言う**（§31-6 と同じ）。5秒ごとに積むと作業ログが埋まる。
            Note($"{renewed}: 仕事が動いている間は書き込み権を持ち続ける");
        }

        // **計画を1つだけ進める**（設計 §37）。走査のたびに1回 ——
        // まとめて進めると、途中で失敗したときに計画とディスクがずれる。
        await AdvancePlansAsync();

        // **秘書にも同じ問いを立てる**（設計 §36）。部門は §31 が仕事の期限で拾うが、
        // 秘書には仕事もタイルも無い（§17-2）ので、**どこからも拾われない**。
        UpdateSecretaryTurnWatch();

        // **相談スレッドはフォルダごと**（設計 §32-6）。フォルダを開く経路が複数あるので、
        // ここ1箇所で読み直す —— 起動時走査は、どの経路からも必ず通る。
        if (kind is CompanyScanKind.Startup)
        {
            composer.Shell.CurrentThreadId = null;
            ShowTranscript([]);
            var unreadable = await composer.RefreshThreadsAsync(CancellationToken.None);
            if (unreadable > 0)
            {
                // **黙って捨てない**（§25-2）。一覧に出ている範囲が全部だと思わせない。
                Note($"読めない相談スレッドが {unreadable} 件あった（一覧には出していない）");
            }
        }

        // **走査で状態を書いたあとに出す。** 先に出すと、まだ Reported でない仕事の
        // 報告を読ませることになる（§7 の「観測してから言う」）。
        foreach (var applied in arrived)
        {
            if (applied.To is CoreTaskStatus.Reported)
            {
                await ShowArrivedReportAsync(applied.Slug);
                await CheckWorktreeAfterReportAsync(applied.Slug);
            }
            else
            {
                await ShowArrivedQuestionAsync(applied.Slug);
            }
        }

        await DeliverAnswersAsync();
    }

    /// <summary>
    /// 出たばかりの報告を、中央ペインへ知らせる（設計 §19-4 / §62-9）。
    /// </summary>
    /// <remarks>
    /// <b>ボタンを待たない。</b> 人間が「報告を読む」を押すまで何も出ないと、
    /// **報告が来たこと自体に気付く工程**が1つ増える。
    /// <para>
    /// <b>全文は出さず、押せる場所だけ出す</b>（§62-9）。報告は節立ての長い文書になった（§62-5）ので、
    /// 会話に流すと秘書とのやり取りが埋もれる。押すとプレビューで整形して読める。
    /// </para>
    /// <para>
    /// <b>用件のボタンは残す</b>（§15-6）—— あれは読み直しと、
    /// 受理・差し戻しへの入口を兼ねている。<b>出すのと、決めるのは別。</b>
    /// </para>
    /// </remarks>
    private async Task ShowArrivedReportAsync(string slug)
    {
        if (_composer is not { Workspace: { } workspace } composer)
        {
            return;
        }

        var who = composer.Tasks is { } tasks
            && await tasks.ReadAsync(slug, CancellationToken.None) is TaskReadResult.Found found
            && composer.KnowsDepartment(found.State.DepartmentId)
                ? composer.DefinitionOf(found.State.DepartmentId).DisplayName
                : "部門";

        // **読めなかったことを、読んだことにしない**（§7）。場所は出すので、押せば理由が窓に出る。
        var note = File.Exists(workspace.Company.Report(slug)) ? "" : "（いまは読めない）";
        Say($"【{slug} の報告】{who} から報告が届いた: {ReportLink(slug)}{note}");
    }

    /// <summary>報告の場所を、会話の中で押せる形（ワークスペースからの相対パス）で返す（§62-9）。</summary>
    private static string ReportLink(string slug) => $".company/tasks/{slug}/report.md";

    /// <summary>
    /// 読むだけの部門の報告が出たら、そのあいだに作業ツリーが変わっていないかを確かめる（設計 §62-6）。
    /// </summary>
    /// <remarks>
    /// <b>計画でない仕事にも出す。</b> 計画の工程なら計画も止まる（<c>PlanRunner</c>）が、
    /// 止める計画が無い仕事では、ここで言わないと誰も気付かない。
    /// 比べられなかったときも黙らない —— 「変わっていない」と「確かめていない」は違う。
    /// </remarks>
    private async Task CheckWorktreeAfterReportAsync(string slug)
    {
        if (_composer is not { Tasks: { } tasks, Workspace: { } workspace } composer
            || await tasks.ReadAsync(slug, CancellationToken.None) is not TaskReadResult.Found found
            || !composer.KnowsDepartment(found.State.DepartmentId)
            || !composer.DefinitionOf(found.State.DepartmentId).ReadsOnly)
        {
            return;
        }

        var name = composer.DefinitionOf(found.State.DepartmentId).DisplayName;
        switch (await WorktreeSnapshot.CheckAsync(workspace.Company, slug, found.State.AttemptId, CancellationToken.None))
        {
            case WorktreeCheck.Changed changed:
                var line = $"{slug}: 読むだけの{name}のあいだに作業ツリーが変わった: {WorktreeSnapshot.Describe(changed.Paths)}。"
                    + "読むだけの部門が書いたか、同時に動いた別の部門が書いた。変わったものを確かめてから受理すること";
                Say($"【注意】{line}");
                Note(line);
                break;

            case WorktreeCheck.Unavailable unavailable:
                Note($"{slug}: 読むだけの{name}が作業ツリーを書き換えていないかは確かめられなかった（{unavailable.Reason}）");
                break;

            // 送る前の控えが無い（取るのに失敗した・この機能より前に送った）。**「変わっていない」とは言わない。**
            case WorktreeCheck.NoBaseline:
                Note($"{slug}: 読むだけの{name}が作業ツリーを書き換えていないかは確かめられなかった（送る前の控えが無い）");
                break;
        }
    }

    /// <summary>
    /// 出たばかりの質問を、中央ペインへ出す（設計 §57）。
    /// </summary>
    /// <remarks>
    /// <b>ボタンを待たない</b>のは報告（§19-4）と同じ。<b>答え方も一緒に言う</b> ——
    /// ボタンが無くなったので、どこに答えるかを示すのはこの1行だけになった。
    /// </remarks>
    private async Task ShowArrivedQuestionAsync(string slug)
    {
        if (_composer?.Workspace is not { } workspace || _composer.Tasks is not { } tasks)
        {
            return;
        }

        var path = Path.Combine(workspace.Company.TaskDirectory(slug), "question.md");
        string content;
        try
        {
            content = await File.ReadAllTextAsync(path, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // **読めなかったことを、読んだことにしない**（§7）。
            Note($"{slug}: 質問が出たが読めなかった（{exception.GetType().Name}）。場所は {path}");
            return;
        }

        var department = await tasks.ReadAsync(slug, CancellationToken.None) is TaskReadResult.Found found
            ? _composer.Shell.Departments.FirstOrDefault(tile => tile.Id == found.State.DepartmentId)
            : null;
        var who = department?.Name ?? "部門";

        // 答え方は駆動モードで違う。**外部ターミナルは窓の入力欄**（§32）、構造化は answer.md（§7）。
        var external = department is not null
            && _composer.DefinitionOf(department.Id).Mode is DriveMode.ExternalTerminal;
        var how = external
            ? $"答えは {who} のターミナルの入力欄に打ってください（タイルの「前面に出す」で窓へ行けます）"
            : $"答えは {workspace.Company.Answer(slug)} に書いてください";

        const int limit = 4000;
        var body = content.Length > limit
            ? $"{content[..limit]}\n\n（長いので残り {content.Length - limit} 文字は省いた。全文は {path}）"
            : content.TrimEnd();
        Say($"【{slug} の質問（{who}）】\n{body}\n\n（{how}）");
    }

    /// <summary>
    /// 秘書の提案を、そのまま仕事にする（設計 §34-1）。
    /// </summary>
    /// <remarks>
    /// <b>2026-09-11 に人間が決めた。</b> §17-6 は
    /// 「<b>人間が『仕事にする』を押したときだけ</b>タスク化する」だったが、
    /// **押す手間のほうが邪魔だ**という判断で自動にした。
    /// <para>
    /// <b>何を手放したかは書いておく。</b> 秘書が提案した時点で部門が動き出すので、
    /// **人間が見る前に作業ツリーへの書き込みが始まり得る。**
    /// §6 の「人間は時々の意思決定者」は、ここでは
    /// <b>報告を受理するかどうか</b>に寄る（§19）。
    /// </para>
    /// <para>
    /// <b>宛先が分からない提案は自動にしない。</b> 捨てもしない ——
    /// カードとして残り、理由が出る（§17-6）。**分からないものを勝手に決めない。**
    /// </para>
    /// </remarks>
    private async Task AutoAcceptProposalsAsync()
    {
        if (_closing || _switching || _composer is not { } composer
            || composer.Tasks is not { } tasks || composer.Dispatcher is not { } dispatcher
            || composer.Workspace is not { } workspace || composer.Outbox is not { } outbox)
        {
            return;
        }

        // **画面のカードではなく outbox を直接読む**（2026-09-11）。
        // カードは走査が作るので、それを待つと**一瞬出てから消える**ことになる。
        foreach (var proposal in outbox.Read())
        {
            // 宛先が分からない・本文が空のものは自動にしない。**捨てもしない**（§17-6）——
            // カードとして残り、理由が出る。
            if (proposal.DepartmentId is not { } departmentId
                || !composer.KnowsDepartment(departmentId)
                || string.IsNullOrWhiteSpace(proposal.Body))
            {
                continue;
            }

            var card = new ProposalCard(proposal, composer.DefinitionOf(departmentId).DisplayName, true);

            // **二重に作らない**（§17-6）。outbox が未処理の正本なので、
            // まだそこに在ることを確かめてから作る。
            if (!_acceptingProposals.Add(card.Id))
            {
                continue;
            }

            try
            {
                if (!File.Exists(Path.Combine(workspace.Company.SecretaryOutbox, $"{card.Id}.md")))
                {
                    continue;
                }

                Note($"提案 {card.Id} を**自動で仕事にする**（§34-1）");
                await AcceptCoreAsync(card, departmentId, tasks, dispatcher, workspace, outbox);
            }
            finally
            {
                _acceptingProposals.Remove(card.Id);
            }
        }
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

                // **「届けた」と言わない**（設計 §32-12、レビューで発覚）。
                // 記録は残っている（二重に積まないため）ので、いずれ書かれる ——
                // **言うべきなのは「まだ」だけ。**
                case DispatchResult.QueuedForNextTurn queued:
                    Note($"{slug}: 部門が前の turn を処理中。回答を**順番待ちに入れた**（待ち {queued.Ahead} 件）");
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

            // **外部ターミナルなら窓を開き直す**（設計 §32、2026-09-12 に実機で踏んだ）。
            var result = await LaunchIfTerminalAsync(
                await dispatcher.RetryDeliveryAsync(
                    state, _composer.DefinitionOf(state.DepartmentId),
                    SessionOf(state.DepartmentId), TimeSpan.FromMinutes(30), CancellationToken.None),
                state.DepartmentId);

            return result switch
            {
                DispatchResult.Dispatched => $"{state.Slug}: もう一度送った（二重に実行されたかもしれない）",
                DispatchResult.QueuedForNextTurn queued =>
                    $"{state.Slug}: 前の turn を処理中なので**順番待ちに入れた**（待ち {queued.Ahead} 件）",
                DispatchResult.SentUncertain uncertain => $"{state.Slug}: {uncertain.Reason}",
                DispatchResult.Rejected rejected => $"{state.Slug}: {rejected.Reason}",

                // **理由を握りつぶさない**（設計 §23-3、2026-09-12 に実機で踏んだ）。
                // ここが「送れなかった」の1行だけだったので、**書き込み権が失効している**
                // という、人間が直せるはずの理由が画面から消えていた。
                DispatchResult.BlockedByExpiredLease expired => $"{state.Slug}: {expired.Reason}",
                DispatchResult.Blocked blocked => $"{state.Slug}: {blocked.Reason}",
                DispatchResult.Conflicted conflicted => $"{state.Slug}: {conflicted.Reason}",
                _ => $"{state.Slug}: 送れなかった（{result.GetType().Name}）",
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

    private void OnTranscriptImageLink(string link)
    {
        if (_composer?.Workspace is not { } workspace) return;
        try
        {
            // **会話の文字列をそのまま開かない。** .company の内側へ解決できたものだけ（§56）。
            var resolution = TranscriptImageLinks.Resolve(workspace.Company, link);
            if (_imagePreview is null)
            {
                _imagePreview = new ImagePreviewWindow();
                _imagePreview.Closed += (_, _) => _imagePreview = null;
                _imagePreview.SetImage(link, resolution);
                _imagePreview.Show(this);
            }
            else
            {
                _imagePreview.SetImage(link, resolution);
            }
            _imagePreview.Activate();
        }
        catch (Exception exception)
        {
            Note($"プレビューを開けなかった（{exception.GetType().Name}）");
        }
    }

    /// <summary>
    /// 読めない仕事を隔離する。<b><c>state.json</c> を勝手に補完しない</b>（§14-1 / §16-4）。
    /// </summary>
    private async void OnRecoveryQuarantine(object? sender, RoutedEventArgs e)
    {
        if (Busy("隔離"))
        {
            return;
        }

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
        if (Busy("書き込み権の隔離"))
        {
            return;
        }

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
        if (Busy("書き込み権の解除"))
        {
            return;
        }

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

    private async void OnRecoveryRescan(object? sender, RoutedEventArgs e)
    {
        // **ここも塞ぐ**（レビューで発覚）。ResolveAsync を通らない唯一の復旧操作なので、
        // 切り替え中に押されると前のフォルダを読みに行き、しかも新しいフォルダの
        // 起動時走査が「走査中」で飛ばされる。
        if (Busy("再走査"))
        {
            return;
        }

        await ScanAsync(CompanyScanKind.Startup);
    }

    private RecoveryItem? Item(object? sender) => (sender as Control)?.DataContext as RecoveryItem;

    private async Task ResolveAsync(
        object? sender, Func<TaskStore, TaskState, Task<string>> resolve, bool keepItem = false)
    {
        // 切り替え中は前のフォルダを触らない（設計 §26-2b）。
        // **復旧の操作は全部ここを通る**ので、1箇所で塞ぐ。
        if (Busy("復旧の操作"))
        {
            return;
        }

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
        if (Busy("提案の受け入れ"))
        {
            return;
        }

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
        if (_composer is not { } composer)
        {
            return;
        }

        var slug = $"task-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        if (await tasks.CreateAsync(slug, departmentId, CancellationToken.None) is not TaskWriteResult.Written created)
        {
            Note($"仕事を作れなかった: {slug}");
            return;
        }

        await File.WriteAllTextAsync(
            workspace.Company.Instruction(slug),
            CompanyInstruction.Compose(card.Body, workspace.Company, slug, composer.DefinitionOf(departmentId)),
            CancellationToken.None);

        // ここまで済んでから移す（§17-6）。
        outbox.Accept(card.Id, slug, DateTimeOffset.Now);

        var result = await LaunchIfTerminalAsync(
            await dispatcher.DispatchAsync(
                created.State, composer.DefinitionOf(departmentId), SessionOf(departmentId),
                TimeSpan.FromMinutes(30), CancellationToken.None),
            departmentId);

        Note(result switch
        {
            DispatchResult.Dispatched => $"{card.TargetText} に {slug} を渡した",
            DispatchResult.QueuedForNextTurn queued =>
                $"{card.TargetText} は前の turn を処理中。{slug} を**順番待ちに入れた**（待ち {queued.Ahead} 件）",
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
        if (Busy("提案の却下"))
        {
            return;
        }

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
    /// <summary>
    /// 定期走査を止める（設計 §26-1）。<b>切り替えの間は前のフォルダを触らない。</b>
    /// </summary>
    private void StopPeriodicScan()
    {
        _scanTimer?.Stop();
        _scanTimer = null;
    }

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

    /// <summary>
    /// 切り替え中は、ワークスペースに触る操作を断る（設計 §26-2b）。
    /// </summary>
    /// <remarks><b>黙って無視しない。</b> 押したのに何も起きない、を作らない（§15-6）。</remarks>
    private bool Busy(string what)
    {
        // **終了処理の最中も塞ぐ**（レビューで発覚）。閉じる要求をいったん取り消して
        // 後始末をしている間、ウィンドウは操作できるままなので、**片付けたあとに
        // 新しい CLI が立つ** —— それは誰にも終了されない。
        if (_closing)
        {
            Note($"終了処理の最中です（{what}はできません）");
            return true;
        }

        if (!_switching)
        {
            return false;
        }

        Note($"ワークスペースを切り替えている最中です（{what}は切り替えが終わってから）");
        return true;
    }

    /// <summary>
    /// 例外を、人間に見せる1行と、追える詳細に分ける（設計 §28-9）。
    /// </summary>
    /// <remarks>
    /// <b>1行だけだと追えない。</b> これまで <c>exception.Message</c> しか出しておらず、
    /// どこで起きたかが分からなかった（§25-3 の 30番）。
    /// <para>
    /// <b>詳細は保存しない。</b> スタックトレースにはパスが載るので、
    /// 診断（§22、ライブ専用）と同じ扱いにする —— <see cref="Status.Evidence"/> へ入れない（§10）。
    /// </para>
    /// </remarks>
    private void NoteException(string what, Exception exception)
    {
        Note($"{what}（{exception.GetType().Name}: {exception.Message}）");

        // **`_composer` を経由しない**（レビューで発覚）。初期化の失敗では
        // まだ composer が無く、**一番詳細が要る場面でそのまま捨てていた**。
        if (DataContext is ShellViewModel shell)
        {
            shell.Diagnostics.Add(new LiveDiagnostic(DiagnosticStream.Protocol, $"{what}: {exception}"));
        }
    }

    /// <summary>
    /// メニューからフォルダを選ぶ（設計 §28-7）。
    /// </summary>
    /// <remarks>
    /// <b>ボタンと同じ経路を通す。</b> `NativeMenuItem.Click` は
    /// <see cref="EventArgs"/> を渡すので、ここで受けてボタンの経路へ渡すだけにする ——
    /// 入口ごとに処理を書くと、片方だけ直し忘れる。
    /// </remarks>
    private void OnPickWorkspaceFromMenu(object? sender, EventArgs e) =>
        OnPickWorkspace(sender, new RoutedEventArgs());

    /// <summary>
    /// いま開いているフォルダを OS のファイラで開く（設計 §28-7）。
    /// </summary>
    private void OnRevealWorkspace(object? sender, EventArgs e)
    {
        if (_composer?.Workspace is not { } workspace)
        {
            Note("先にワークスペースを選ぶ");
            return;
        }

        try
        {
            using var _ = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(workspace.Root) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            // 開けなかったことを、開いたことにしない。場所は出す。
            Note($"フォルダを開けなかった（{exception.GetType().Name}）。場所は {workspace.Root}");
        }
    }

    private async void OnRescanFromMenu(object? sender, EventArgs e)
    {
        if (Busy("再走査"))
        {
            return;
        }

        await ScanAsync(CompanyScanKind.Startup);
    }

    /// <summary>
    /// 作業ログを消す（設計 §28-7）。<b>`.company/` は触らない</b> —— 画面の表示だけ。
    /// </summary>
    private void OnClearWorkLog(object? sender, EventArgs e)
    {
        if (DataContext is ShellViewModel shell)
        {
            shell.WorkLog.Clear();
            Note("作業ログを消した（.company/ の中身は消えていない）");
        }
    }

    /// <summary>
    /// アプリ全体の診断を出す（設計 §28-9）。<b>1行では追えないときの出口。</b>
    /// </summary>
    private void OnShowLastErrors(object? sender, EventArgs e)
    {
        if (DataContext is not ShellViewModel shell)
        {
            return;
        }

        // **5行では足りない**（2026-09-09 に実機で踏んだ）。proxy や login の失敗は
        // 数行にわたるので、途中で切れると原因に辿り着けない。
        // 出し先は作業ログ（上限 500 行、永続しない）なので、広げても害が無い。
        var recent = shell.Diagnostics.Recent(30);
        NoteBlock(
            $"—— 直近のエラーの詳細（{shell.Diagnostics.Summary}）——",
            recent.Count is 0 ? ["まだ記録がない"] : recent.Select(d => d.Text));
    }

    /// <summary>
    /// 会話を全部クリップボードへ（2026-09-11、人間の要望）。
    /// </summary>
    /// <remarks>
    /// <b>まとめて1つの部品にしたので、ドラッグでも選べる</b>（§34-4）——
    /// これは「全部」を1手で取るための口。
    /// </remarks>
    private async void OnCopyTranscript(object? sender, EventArgs e)
    {
        if (DataContext is not ShellViewModel shell)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            // **できなかったことを、できたことにしない**（§7）。
            Note("クリップボードを使えない");
            return;
        }

        await clipboard.SetTextAsync(shell.TranscriptText);
        Note($"会話を全部コピーした（{shell.SecretaryTranscript.Count} 行）");
    }

    /// <summary>
    /// 作業ログを全部コピーする（人間の要望、2026-09-12）。
    /// </summary>
    /// <remarks>
    /// <b>1行ずつ選べるだけでは足りない</b>（§37-10）——
    /// 人に見せたり調べたりするのは**起きた順に並んだ全体**で、
    /// そこを手で拾わせると、**拾い落としたところが「起きなかったこと」になる。**
    /// </remarks>
    private async void OnCopyWorkLog(object? sender, EventArgs e)
    {
        if (DataContext is not ShellViewModel shell)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            // **できなかったことを、できたことにしない**（§7）。
            Note("クリップボードを使えない");
            return;
        }

        // **画面に出ている順のまま渡す。** 並べ替えると、読み返したときに
        // 作業ログと食い違う。
        await clipboard.SetTextAsync(string.Join(Environment.NewLine, shell.WorkLog));
        Note($"作業ログを全部コピーした（{shell.WorkLog.Count} 行）");
    }

    private void OnFocusMessage(object? sender, EventArgs e) =>
        this.FindControl<TextBox>("MessageBox")?.Focus();

    /// <summary>
    /// 入力欄で Enter を押したら送る（設計 §13-7 / §28-6）。
    /// </summary>
    /// <remarks>
    /// <b>実測の上に立っている。</b> IME の変換確定 Enter は <c>KeyDown</c> として
    /// 届かない（TextBox 経路、実測17件中16件）ので、**確定と送信は衝突しない**。
    /// §13-7 でそう結論しておきながら、実装していなかった（§25-4）。
    /// <para>
    /// <b>修飾キーつきは通す。</b> Shift+Enter などを送信にすると、
    /// 将来 複数行入力を足したときに衝突する。
    /// </para>
    /// </remarks>
    private static bool IsPlainEnter(KeyEventArgs e) =>
        e.Key is Key.Enter or Key.Return && e.KeyModifiers is KeyModifiers.None;

    private void OnMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsPlainEnter(e))
        {
            return;
        }

        e.Handled = true;
        OnSendToSecretary(sender, new RoutedEventArgs());
    }

    private void OnTaskDraftKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsPlainEnter(e))
        {
            return;
        }

        e.Handled = true;
        OnMakeTask(sender, new RoutedEventArgs());
    }

    private void OnRejectionKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsPlainEnter(e))
        {
            return;
        }

        e.Handled = true;
        OnRejectReport(sender, new RoutedEventArgs());
    }

    /// <summary>
    /// 覚えていた見た目に戻す（設計 §28-5）。
    /// </summary>
    /// <remarks>
    /// <b>おかしな値は使わない。</b> 画面に収まらない大きさを復元すると
    /// 「起動したのに何も見えない」になる —— 判定は <see cref="WindowLayoutMemory"/> 側。
    /// </remarks>
    private void RestoreLayout()
    {
        if (_layout.Load() is not { } saved)
        {
            return;
        }

        Width = saved.Width;
        Height = saved.Height;
        if (saved.Maximized)
        {
            WindowState = WindowState.Maximized;
        }

        if (this.FindControl<Grid>("PaneGrid") is { } grid && grid.ColumnDefinitions.Count is 5)
        {
            grid.ColumnDefinitions[0].Width = new GridLength(saved.LeftPane);
            grid.ColumnDefinitions[4].Width = new GridLength(saved.RightPane);
        }
    }

    private void SaveLayout()
    {
        var grid = this.FindControl<Grid>("PaneGrid");
        var left = grid?.ColumnDefinitions.Count is 5 ? grid.ColumnDefinitions[0].ActualWidth : 260;
        var right = grid?.ColumnDefinitions.Count is 5 ? grid.ColumnDefinitions[4].ActualWidth : 320;

        // 最大化中は、戻したときの大きさを覚える（最大化の値を覚えても意味がない）。
        var maximized = WindowState is WindowState.Maximized;
        _layout.Save(new WindowLayout(
            maximized ? RestoredWidth() : Width,
            maximized ? RestoredHeight() : Height,
            maximized,
            left <= 0 ? 260 : left,
            right <= 0 ? 320 : right));
    }

    private double RestoredWidth() => _layout.Load()?.Width ?? 1280;

    private double RestoredHeight() => _layout.Load()?.Height ?? 800;

    /// <summary>
    /// 動いているものがあるなら、閉じる前に一度だけ確かめる（設計 §28-3）。
    /// </summary>
    /// <remarks>
    /// <b>毎回は聞かない。</b> 何も動いていないのに確認を出すのは、ただの邪魔。
    /// <b>そして二度は聞かない</b> —— 一度「閉じる」と答えたら、そのまま閉じる。
    /// </remarks>
    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // **ダイアログを出している最中にもう一度閉じられても、二枚目を出さない**
        // （レビューで発覚）。二重のモーダルは、閉じられなくなるか落ちる。
        // **人間が「閉じる」と答えたら、もう止めない**（レビューで発覚）。
        // ここで `_askingClose` を見て取り消すと、`Close()` が自分自身に弾かれ、
        // **ウィンドウが永久に閉じられなくなる**。
        if (_confirmedClose || _closing)
        {
            return;
        }

        // 確認を出している最中に、もう一度閉じられた。二枚目は出さない。
        if (_askingClose)
        {
            e.Cancel = true;
            return;
        }

        var running = _runner?.RunningDepartments() ?? [];
        var secretaryRunning = _secretary?.IsRunning is true;
        if (running.Count is 0 && !secretaryRunning)
        {
            return;
        }

        e.Cancel = true;
        _askingClose = true;

        var what = running.Count is 0
            ? "秘書"
            : string.Join("、", running.Select(id => _composer?.DefinitionOf(id).DisplayName ?? id))
                + (secretaryRunning ? "、秘書" : string.Empty);

        var box = new Window
        {
            Title = "閉じますか",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        var close = new Button { Content = "閉じる（動いているものは終了する）", IsDefault = true };
        var stay = new Button { Content = "やめる", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => box.Close(true);
        stay.Click += (_, _) => box.Close(false);

        box.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock
                {
                    Text = $"いま動いている: {what}",
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                },
                new TextBlock
                {
                    // **何が起きるかを言う**（§15-6）。「本当に？」だけ聞かない。
                    Text = "閉じると、このアプリが起動した CLI はすべて終了します。"
                        + "進行中の作業は途中で切れます（.company/ に書かれたものは残ります）。",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 12),
                    Opacity = 0.85,
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { close, stay },
                },
            },
        };

        try
        {
            if (await box.ShowDialog<bool>(this))
            {
                _confirmedClose = true;

                // **閉じる前に印を降ろす。** 上のガードに自分で引っかからないように。
                _askingClose = false;
                Close();
            }
        }
        finally
        {
            _askingClose = false;
        }
    }

    /// <summary>
    /// 終了処理に入ったことを画面へ伝える（設計 §26-4）。
    /// </summary>
    /// <remarks>ここから先は、新しいセッションを作らせない。</remarks>
    internal void NotifyClosing()
    {
        _closing = true;
        _usageCancellation?.Cancel();
    }

    private void Note(string line)
    {
        if (DataContext is not ShellViewModel shell)
        {
            return;
        }

        shell.WorkLog.Insert(0, $"{DateTimeOffset.Now:HH:mm:ss}  {line}");

        // **上限を置く**（設計 §25-2）。会話は 500、部門の観測は 30 で切っているのに、
        // 作業ログだけ無制限だった —— 数日つけっぱなしにすると伸び続ける。
        while (shell.WorkLog.Count > 500)
        {
            shell.WorkLog.RemoveAt(shell.WorkLog.Count - 1);
        }
    }

    /// <summary>
    /// 何行かをまとめて1件として出す（設計 §25-2）。
    /// </summary>
    /// <remarks>
    /// <b><see cref="Note"/> を繰り返さない。</b> あれは先頭に挿入するので、
    /// 繰り返すと**逆順になり**、作業ログ全体の時系列も崩れる（2026-09-06 に自分で入れた）。
    /// </remarks>
    private void NoteBlock(string title, IEnumerable<string> lines)
    {
        var body = string.Join(Environment.NewLine, lines);
        Note(string.IsNullOrEmpty(body) ? title : $"{title}{Environment.NewLine}{body}");
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
        // **開けたときだけ覚える**（設計 §21-2 / §26-1）。
        // 別のアプリが開いていて開けなかったものを、次の起動で開こうとしない。
        if (await OpenWorkspaceAsync(path))
        {
            await _memory.RememberAsync(path, DateTimeOffset.Now, CancellationToken.None);
        }
    }

    /// <summary>ワークスペースを開く。人間の選択と起動時の復帰で同じ経路を通る（設計 §21-1）。</summary>
    private async Task<bool> OpenWorkspaceAsync(string path)
    {
        if (_composer is null)
        {
            return false;
        }

        // **切り替えは1つずつ**（設計 §26-1）。重ねると、返されないロックが残る。
        await _switchGate.WaitAsync();
        try
        {
            return await OpenWorkspaceCoreAsync(path);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    private async Task<bool> OpenWorkspaceCoreAsync(string path)
    {
        _switching = true;
        try
        {
            return await SwitchWorkspaceAsync(path);
        }
        finally
        {
            _switching = false;
        }
    }

    private async Task<bool> SwitchWorkspaceAsync(string path)
    {
        // **2つのアプリが同じフォルダを開かない**（設計 §26）。
        // §14-1 は「`state.json` を書くのはアプリだけ」に寄りかかっている。
        // 取れなければ**そのフォルダは開かない**。アプリは動いたままで、別のフォルダは選べる。
        if (!TryHoldWorkspace(path))
        {
            return false;
        }

        // **綴りではなく鍵で比べる**（レビューで発覚）。同じフォルダを別の綴りで開き直したときに
        // 秘書と部門を止めてしまうし、Windows では自分のロックを他人のものと言う。
        var leaving = _composer!.Workspace;
        var changed = leaving is not null
            && !string.Equals(WorkspaceInstanceLock.KeyOf(leaving.Root), WorkspaceInstanceLock.KeyOf(path),
                StringComparison.Ordinal);

        // 走査だけは先に止める。**これは戻せる**（開けなければ再開すればよい）。
        StopPeriodicScan();
        await WaitForScanAsync();

        try
        {
            // **差し替えが済むまで、前のフォルダのものを壊さない**（レビューで発覚、§26-2）。
            // 先に秘書と部門を止めると、選んだ先が開けなかったときに
            // **前のフォルダに居るのに、その秘書と部門だけ死んでいる**状態になる。
            // **部門の顔ぶれが変わったなら、同じフォルダでも止める**（レビューで発覚）。
            // 作り直したタイルは新しい検出器を持つので、動いているセッションは繋がらない。
            changed |= await _composer.SelectWorkspaceAsync(path, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // **フォルダを選んだだけでアプリを落とさない。** ここは `async void` の先。
            ReleaseHeldWorkspace();
            StartPeriodicScan();
            NoteException($"{path} を開けませんでした（前のワークスペースはそのまま）", exception);
            return false;
        }

        // **選択中のタイルは、もう画面に無い**（レビューの派生）。部門はワークスペースごとに
        // 作り直される（§15-8）ので、掴んだままにすると **消えたタイルへ直接送信する**。
        _selected = null;

        // **秘書の CLI は、そのフォルダの設定に従う**（設計 §46）。
        // **動いている秘書は取り替えない** —— 効くのは次の起動から
        // （会話の途中で中身が入れ替わると、人間から見て同じ相手が別人になる）。
        if (_secretary is not null)
        {
            _secretary.Definition = _composer.Secretary;
        }

        if (changed)
        {
            // ここまで来て初めて「開けた」と言える。**前のフォルダのものを止めるのはここ。**
            if (_secretary is not null)
            {
                Note("ワークスペースが変わったので秘書を終了した（次の送信で新しいフォルダで起動する）");
                await _secretary.DisposeAsync();
            }

            if (_runner is not null)
            {
                Note("ワークスペースが変わったので部門を終了した");
                await _runner.StopAllAsync();
            }
        }

        UpdateSecretaryStatus();

        try
        {
            // 起動時（ワークスペース選択時）の走査だけが復旧の一覧を作る（設計 §16-1）。
            await ScanAsync(CompanyScanKind.Startup);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 走査が失敗しても、フォルダは開いている。**握ったままにする**（§26-1）。
            NoteException("起動時の走査に失敗しました", exception);
        }

        StartPeriodicScan();

        // **フォルダが決まったら秘書を起こす**（2026-09-06、人間が指定。§17-4 を改めた）。
        // 選んだ時点で相手が居る方が自然、という判断。**代償は書いてある**（§17-7）——
        // アプリを開くだけで claude が1つ起動し、README も書かれる。
        // **開き始めたときの path と、いま開いているものが同じか確かめる**（§17-7）。
        // 走査を待っている間に人間が別のフォルダを選ぶと、古い側のこの行が
        // **選ばれていないフォルダで claude を起こす**。しかも後から来た側は
        // `IsRunning` を見て、その間違った秘書を使い回す。
        // **終了処理に入っていたら起こさない**（レビューで発覚）。走査を待っている間に
        // 閉じられると、片付けたあとに `claude` が立ち、誰にも終了されない。
        // 同一性の判定は**どこでも同じ規則**にする（§26-1）。いまは `WorkspaceRef` が
        // 綴りをそのまま持つので生比較でも通るが、揃えておかないと、
        // 正規化を足した瞬間にここだけ静かに落ちる。
        if (!_closing
            && _composer.Workspace is { } opened
            && string.Equals(WorkspaceInstanceLock.KeyOf(opened.Root), WorkspaceInstanceLock.KeyOf(path),
                StringComparison.Ordinal))
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
                NoteException("秘書を起動できなかった（ワークスペースは開いている）", exception);
            }

            UpdateSecretaryStatus();
        }

        // ここまで来れば、前のフォルダはもう誰も触っていない。
        _supersededLock?.Dispose();
        _supersededLock = null;

        // **会話の記録を git に入れないか聞く**（設計 §53）。切り替えの門の外で待つ ——
        // 人間が返事をするまで、別のフォルダへの切り替えを塞がない。
        if (_composer.Workspace is { } workspace)
        {
            Dispatcher.UIThread.Post(() => _ = AskCompanyGitIgnoreAsync(workspace));
        }

        return true;
    }

    /// <summary>このアプリを起動してから、もう聞いたフォルダ（鍵）。</summary>
    /// <remarks>
    /// <b>「あとで」を選んだフォルダを、同じ起動の中で聞き直さない。</b>
    /// 部門の設定を閉じるたびにフォルダを開き直す（§47）ので、覚えないと閉じるたびに出る。
    /// </remarks>
    private readonly HashSet<string> _gitIgnoreAsked = new(StringComparer.Ordinal);

    /// <summary>
    /// <c>.company/</c> を <c>.gitignore</c> に足すか、人間に聞く（設計 §53）。
    /// </summary>
    /// <remarks>
    /// <b>黙って足さない。</b> <c>.gitignore</c> は利用者の持ち物 —— §30-4 で却下した
    /// 「アプリが人間の持ち物を勝手に触る」と同じになる。
    /// </remarks>
    private async Task AskCompanyGitIgnoreAsync(WorkspaceRef workspace)
    {
        try
        {
            if (_closing || !_gitIgnoreAsked.Add(WorkspaceInstanceLock.KeyOf(workspace.Root)))
            {
                return;
            }

            if (await CompanyGitIgnore.CheckAsync(workspace, CancellationToken.None)
                is not CompanyGitIgnoreCheck.ShouldAsk ask)
            {
                return;
            }

            var box = new Window
            {
                Title = ".company/ を git に入れないようにしますか",
                Width = 520,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };

            var add = new Button { Content = ".gitignore に足す", IsDefault = true };
            var later = new Button { Content = "あとで", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
            var never = new Button { Content = "このフォルダでは聞かない", Margin = new Thickness(8, 0, 0, 0) };
            add.Click += (_, _) => box.Close("add");
            later.Click += (_, _) => box.Close("later");
            never.Click += (_, _) => box.Close("never");

            var body = "このフォルダの .company/ には、秘書との会話・指示書・報告書が保存されます。"
                + "このままだと git add . で一緒に commit され、公開リポジトリならそのまま公開されます。"
                + $"\n\n{workspace.Root}/.gitignore に「.company/」の行を足します（注釈の行も1行付きます）。";
            if (ask.AlreadyTracked)
            {
                // **足しても外れないものがある**ことを、足す前に言う。
                body += "\n\n※ .company/ の中身は既に git に入っています。.gitignore に足しても、"
                    + "入っているものは外れません（外すには git rm -r --cached .company が要ります）。";
            }

            box.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { add, later, never },
                    },
                },
            };

            switch (await box.ShowDialog<string?>(this))
            {
                case "add":
                    // **「足した」と「効いた」を分けて言う**（Codex の指摘）。
                    Note(await CompanyGitIgnore.AddAsync(workspace, CancellationToken.None)
                        ? $".gitignore に .company/ を足した（{workspace.Root}）"
                        : $".gitignore に .company/ を足したが、git はまだ .company/ を無視していない。"
                            + $"ほかの .gitignore が打ち消していないか確かめてください（{workspace.Root}）");
                    break;
                case "never":
                    await CompanyGitIgnore.DeclineAsync(workspace, CancellationToken.None);
                    Note(".company/ を .gitignore に足すか、このフォルダでは聞かない（.company/gitignore-declined を消すとまた聞く）");
                    break;
                default:
                    Note(".company/ を .gitignore に足していない（次にアプリを起動したとき、また聞く）");
                    break;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // **聞けなかったことで、フォルダを開いたことを取り消さない。** ただし黙らない。
            NoteException(".gitignore を確かめられなかった", exception);
        }
    }

    /// <summary>
    /// 開けなかったときに、掴んだロックを戻す（設計 §26-1）。
    /// </summary>
    private void ReleaseHeldWorkspace()
    {
        // 既に新しいフォルダを見ているなら、握ったままにする（§26-1）。
        if (_composer?.Workspace is { } current
            && string.Equals(_lockedWorkspace, WorkspaceInstanceLock.KeyOf(current.Root), StringComparison.Ordinal))
        {
            _supersededLock?.Dispose();
            _supersededLock = null;
            return;
        }

        _instanceLock?.Dispose();
        _instanceLock = _supersededLock;
        _lockedWorkspace = _composer?.Workspace?.Root is { } root ? WorkspaceInstanceLock.KeyOf(root) : null;
        _supersededLock = null;
    }

    /// <summary>
    /// そのフォルダの排他ロックを握る（設計 §26-1）。
    /// </summary>
    /// <remarks>
    /// <b>正本は OS のロック。</b> pid や起動時刻は人間への説明で、判定には使わない。
    /// <b>「ロックを消す」操作は出さない</b>（§26-2）—— Unix では、握られているファイルを
    /// 消して作り直すと別の実体になり、2つのプロセスが別々のロックを持ててしまう。
    /// </remarks>
    private bool TryHoldWorkspace(string path)
    {
        // 同じフォルダを開き直すときは、握ったまま進む。**ロックの鍵と同じ規則で比べる。**
        var resolved = WorkspaceInstanceLock.KeyOf(path);
        if (_instanceLock is not null && string.Equals(_lockedWorkspace, resolved, StringComparison.Ordinal))
        {
            return true;
        }

        var previous = _instanceLock;
        switch (WorkspaceInstanceLock.Acquire(path, WorkspaceMemory.RuntimeRoot, DateTimeOffset.Now))
        {
            case WorkspaceInstanceLockResult.Acquired acquired:
                _instanceLock = acquired.Lock;
                _lockedWorkspace = resolved;

                // 前のフォルダは、**切り替えが済んでから**返す（§26-1）。
                // ここで返すと、まだ走査や秘書がそこを触っている間に別のアプリが開ける。
                _supersededLock = previous;
                return true;

            case WorkspaceInstanceLockResult.Held held:
                Note(held.Holder is { } holder
                    ? $"{path} は別のアプリが開いています（pid {holder.Pid} / {holder.Host} / "
                        + $"{holder.OpenedAt.ToLocalTime():MM/dd HH:mm} から）。**このアプリでは開きません** —— "
                        + "そちらを閉じてからもう一度選ぶか、別のフォルダを選ぶ"
                    : $"{path} は別のアプリが開いています（保持者情報は読めません）。"
                        + "**このアプリでは開きません** —— そちらを閉じるか、別のフォルダを選ぶ");
                return false;

            case WorkspaceInstanceLockResult.Unavailable unavailable:
                // **「開いている」と言い切らない**（§13-9 と同じ姿勢）。置けないだけかもしれない。
                Note($"{path} の排他ロックを置けませんでした（{unavailable.Reason}）。開きません");
                return false;

            default:
                return false;
        }
    }
}
