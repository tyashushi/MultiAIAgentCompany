using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Status;

namespace MultiAIAgentCompany.Core.Terminal;

/// <summary>
/// 外部ターミナルで動いている部門（設計 §32）。
/// </summary>
/// <remarks>
/// <b><see cref="IStructuredSession"/> ではない。</b> パイプが無いので、
/// 送ることも、承認に答えることも、発言を受け取ることもできない ——
/// <b>それが目的である</b>（承認は人間がその窓で押す。ブリーフ #3）。
/// <para>
/// <b>turn の終わりを観測しない。</b> 実測（§32-2e）で、3つの CLI はどれも
/// turn が終わってもセッションを終了しない。**完了の信号は <c>report.md</c> だけ**（§16-1）。
/// </para>
/// <para>
/// <b>活動状態は <c>Unknown</c> のままになる。</b> 出せる根拠が
/// <see cref="EvidenceSource.Dispatch"/>（起動した）しか無く、
/// 検出器は Dispatch を <c>Working</c> の根拠にしない（§7）。
/// ブリーフ #6 の「誤って『休けい中』と表示するより『不明』と出す方が安全」どおり。
/// </para>
/// </remarks>
public sealed class TerminalDepartmentSession : IAgentSession
{
    private readonly ITerminalLauncher _launcher;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _watching = new();
    private readonly List<LiveDiagnostic> _diagnostics = [];

    /// <summary>購読より前に出た観測（設計 §22-2 と同じ扱い）。</summary>
    private readonly List<Evidence> _pending = [];
    private readonly object _gate = new();
    private int _disposed;

    private TerminalDepartmentSession(
        string departmentId, AgentKind agent, TerminalHandle handle,
        ITerminalLauncher launcher, ProcessIdentity identity, TimeProvider clock)
    {
        DepartmentId = departmentId;
        Agent = agent;
        Handle = handle;
        _launcher = launcher;
        Identity = identity;
        _clock = clock;
    }

    /// <summary>
    /// 窓を開いてセッションにする。
    /// </summary>
    /// <remarks>
    /// <b>PID は起動直後には書かれていないことがある</b>ので、少しだけ待つ。
    /// 待っても現れなければ <c>0</c> のままにする —— **分からないものを埋めない**（§7）。
    /// </remarks>
    public static async Task<TerminalStartResult> StartAsync(
        string departmentId, AgentKind agent, TerminalLaunchRequest request,
        ITerminalLauncher launcher, TimeProvider clock, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(departmentId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(clock);

        var launched = await launcher.LaunchAsync(request, ct);
        if (launched is TerminalLaunchResult.Failed failed)
        {
            return new TerminalStartResult.Failed(failed.Reason);
        }

        // **窓が生きているときの失敗は、別の名前で返す**（設計 §41-2b）——
        // 呼び出し側は、その窓の handle を捨てずに付け直す。
        if (launched is TerminalLaunchResult.WindowBusy busy)
        {
            return new TerminalStartResult.WindowBusy(busy.Reason);
        }

        var handle = ((TerminalLaunchResult.Launched)launched).Handle;
        var pid = await WaitForPidAsync(handle.PidFilePath, ct);
        var identity = new ProcessIdentity(pid, 0, pid, clock.GetUtcNow());

        var session = new TerminalDepartmentSession(departmentId, agent, handle, launcher, identity, clock);

        // **ここで raise しない**（§22-2 と同じ）。購読するのは、このメソッドが
        // セッションを返したあとなので、いま出すと**誰も聞いていない。**
        // 取り置いて、購読側が `ReplayObservations` で引き取る。
        session._pending.Add(new Evidence(
            EvidenceSource.Dispatch, clock.GetUtcNow(), null, null,
            new AgentRef(departmentId, agent), null, null,
            $"外部ターミナルで起動した（窓 {handle.WindowId}）"));

        session.StartWatching();

        // **PID が現れないのは「起動を確かめられていない」ということ**（設計 §41）。
        // スクリプトは1行目で PID を書くので、**書かれていないなら走っていない。**
        //
        // **それでもセッションは返す** —— 窓は開いているかもしれないので、
        // 捨てると**アプリが知らない窓**が残り、次の dispatch が2つ目の窓を開く（§32-8）。
        // 渡ったことにしないのは、呼び出し側の仕事である。
        if (pid <= 0)
        {
            session.Note("PID が現れないので、起動を確かめられない（窓が閉じられたことにも気付けない）");
            return new TerminalStartResult.StartedUnverified(
                session, "起動用スクリプトが走った跡（PID）が現れなかった");
        }

        return new TerminalStartResult.Started(session);
    }

    /// <summary>
    /// 既にある窓に、セッションを付け直す（設計 §41-2b、レビューで発覚）。
    /// </summary>
    /// <remarks>
    /// <b>開き直しに失敗したときの受け皿。</b> 開き直しは「前のを終わらせてから打ち込む」
    /// 順なので、**打ち込めなかったときには、もう前のセッションを捨てている** ——
    /// そのまま失敗を返すと、**まだ生きているかもしれない窓の handle をアプリが失う**
    /// （前面化も終了もできず、次の dispatch が2つ目の窓を開く。§32-8）。
    /// <para>
    /// <b>「起動した」とは言わない。</b> ここは何も起こしていない ——
    /// 付け直しているのは<b>窓を指す手</b>だけである。
    /// </para>
    /// </remarks>
    public static async Task<TerminalDepartmentSession> ReattachAsync(
        string departmentId, AgentKind agent, TerminalHandle handle,
        ITerminalLauncher launcher, TimeProvider clock, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(departmentId);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(clock);

        var pid = await WaitForPidAsync(handle.PidFilePath, ct);
        var session = new TerminalDepartmentSession(
            departmentId, agent, handle, launcher, new ProcessIdentity(pid, 0, pid, clock.GetUtcNow()), clock);

        // **付け直したことも観測として出す**（§22-2 と同じ取り置き）。
        // 出さないと、呼び出し側が立てた「起動中」の印を**誰も降ろさない。**
        session._pending.Add(new Evidence(
            EvidenceSource.Dispatch, clock.GetUtcNow(), null, null,
            new AgentRef(departmentId, agent), null, null,
            $"窓 {handle.WindowId} にセッションを付け直した（開き直せなかったので、前の窓のまま）"));

        session.StartWatching();
        return session;
    }

    public string DepartmentId { get; }

    public AgentKind Agent { get; }

    /// <summary>開いた窓。<b>前面化に使う</b>（§32-2c）。</summary>
    public TerminalHandle Handle { get; }

    public ProcessIdentity Identity { get; }

    public DriveMode Mode => DriveMode.ExternalTerminal;

    /// <summary>
    /// <b>CLI が申告したモデルは分からない</b>（設計 §32-4）。
    /// 構造化イベントが無いので、観測できるものが無い —— **渡した値で埋めない**（§27）。
    /// </summary>
    public AgentModel? ObservedModel => null;

    public event EventHandler<Evidence>? Observed;

    /// <summary>
    /// <b>発火しない。</b> 終了コードを観測する経路が無い（§32-2e）。
    /// 窓が閉じられたことは <see cref="Disappeared"/> で伝える。
    /// </summary>
    public event EventHandler<int>? Exited { add { } remove { } }

    /// <summary>
    /// プロセスがもう居ないと分かった。<b>終了コードは観測していない</b>ので
    /// <see cref="Exited"/> とは別にする —— <c>0</c> を渡すと「成功した」に見える。
    /// </summary>
    public event EventHandler? Disappeared;

    public event EventHandler<LiveDiagnostic>? Diagnosed;

    public IReadOnlyList<LiveDiagnostic> RecentDiagnostics(int count)
    {
        lock (_gate)
        {
            return count >= _diagnostics.Count
                ? [.. _diagnostics]
                : [.. _diagnostics.Skip(_diagnostics.Count - count)];
        }
    }

    /// <summary>その窓を前面に出す（設計 §32-2c）。</summary>
    public Task<bool> FocusAsync(CancellationToken ct) => _launcher.FocusAsync(Handle, ct);

    public async Task StopAsync(CancellationToken ct)
    {
        var result = await _launcher.TerminateAsync(Handle, ct);
        Note(result switch
        {
            TerminalTerminateResult.Signalled signalled =>
                $"プロセスグループ {signalled.ProcessGroupId} に TERM を送った",
            TerminalTerminateResult.NotRunning notRunning => notRunning.Reason,
            TerminalTerminateResult.Failed failure => $"終了させられません: {failure.Reason}",
            _ => "終了の結果が分かりません",
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _watching.CancelAsync();
        try
        {
            await StopAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // 終了させられなくても、片付けは続ける（§9）。
        }

        _watching.Dispose();
    }

    /// <summary>
    /// 居なくなったことだけを見張る。
    /// </summary>
    /// <remarks>
    /// <b>「出力が無い」を見張っているのではない</b>（§7 の 429 無言リトライ）。
    /// 見ているのは<b>プロセスが在るかどうか</b>という、推定の要らない事実だけ。
    /// </remarks>
    private void StartWatching()
    {
        if (Identity.Pid <= 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var token = _watching.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _clock, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (IsAlive(Identity.Pid))
                {
                    continue;
                }

                Disappeared?.Invoke(this, EventArgs.Empty);
                return;
            }
        });
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<int> WaitForPidAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (File.Exists(path)
                    && int.TryParse((await File.ReadAllTextAsync(path, ct)).Trim(), out var pid))
                {
                    return pid;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 書かれている最中。次の周で読み直す。
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }

        return 0;
    }

    /// <summary>
    /// 購読より前に出た観測を流し込む（設計 §22-2）。
    /// </summary>
    /// <remarks>
    /// <b>購読してから呼ぶこと。</b> 起動の観測は <see cref="StartAsync"/> の中で出るので、
    /// そのまま raise すると誰も聞いていない —— 聞き逃すと、検出器は
    /// <c>Starting</c> のまま止まり、**開いている窓が「起動中」に見え続ける。**
    /// </remarks>
    public void ReplayObservations()
    {
        Evidence[] pending;
        lock (_gate)
        {
            pending = [.. _pending];
            _pending.Clear();
        }

        foreach (var evidence in pending)
        {
            Observed?.Invoke(this, evidence);
        }
    }

    internal void Note(string text)
    {
        var line = new LiveDiagnostic(DiagnosticStream.Protocol, text);
        lock (_gate)
        {
            _diagnostics.Add(line);
            while (_diagnostics.Count > 30)
            {
                _diagnostics.RemoveAt(0);
            }
        }

        Diagnosed?.Invoke(this, line);
    }
}

public abstract record TerminalStartResult
{
    public sealed record Started(TerminalDepartmentSession Session) : TerminalStartResult;

    /// <summary>
    /// 窓は開いたが、<b>起動を確かめられていない</b>（設計 §41）。
    /// </summary>
    /// <remarks>
    /// <b><see cref="Started"/> と混ぜない。</b> 混ぜると、届いていない指示が
    /// 「渡した」として記録される —— **実機で1度、そのまま止まった**（§41-1）。
    /// </remarks>
    public sealed record StartedUnverified(TerminalDepartmentSession Session, string Reason) : TerminalStartResult;

    /// <summary>
    /// 開き直そうとした窓が、まだ塞がっていた（設計 §41-2b）。
    /// <b>窓は生きている</b>ので、呼び出し側は handle を捨てない。
    /// </summary>
    public sealed record WindowBusy(string Reason) : TerminalStartResult;

    public sealed record Failed(string Reason) : TerminalStartResult;
}
