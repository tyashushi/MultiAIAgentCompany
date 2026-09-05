namespace MultiAIAgentCompany.Core.Sessions;

/// <summary>
/// 1部門ぶんの子プロセス。設計 §9 —— <b>アプリが PTY を握る＝アプリが全部門の親になる。</b>
/// </summary>
/// <remarks>
/// <c>ToolPanel</c> が「GUI を親にしない」と明示して避けていた問題を、意図的に引き受ける。
/// したがって契約を決めておく（設計 §9）:
/// <list type="bullet">
/// <item>部門ごとに session / process group を隔離する。全体で SIGHUP を無視する解決はしない
/// （PTY では SIGHUP が端末切断の正規の意味を持つ）</item>
/// <item>PID だけでなく SID / PGID / foreground PGID / 開始時刻を持つ（PID 再利用対策）</item>
/// <item>終了は段階的に。入力停止 → 正常終了要求 → SIGTERM → 最後に SIGKILL。対象は process group</item>
/// <item>master の EOF / EIO と子の waitpid() を別に扱い、zombie を回収する</item>
/// <item>リサイズごとに TIOCSWINSZ。SIGWINCH が届くことを実機で確認する</item>
/// <item>Ctrl-C / Ctrl-Z を GUI ショートカットにせず、foreground process group へ正しく伝える</item>
/// <item><b>非表示のセッションも読み続ける。</b> 読み取りを止めると PTY バッファが詰まって
/// 子プロセスが停止する（設計 §5）</item>
/// </list>
/// </remarks>
public interface IAgentSession : IAsyncDisposable
{
    string DepartmentId { get; }

    ProcessIdentity Identity { get; }

    Agents.DriveMode Mode { get; }

    /// <summary>
    /// 文章を送る。TUI モードでは<b>ターミナルへの直打ちではなく、入力欄経由</b>（設計 §13-7）。
    /// </summary>
    /// <param name="submitKey">
    /// 解決済みの送信キー。<see cref="Agents.SubmitKey.IsResolved"/> が false のものを渡すと
    /// <see cref="ArgumentException"/> になる —— <c>0D</c> にフォールバックさせないため（設計 §14-4）。
    /// 送信キーが不明なときは送らず、部門の状態を「送信キー不明」にする。
    /// </param>
    Task SendTextAsync(string text, Agents.SubmitKey submitKey, CancellationToken ct);

    /// <summary>
    /// 単キー（<c>y</c>/<c>n</c>、矢印、Ctrl-C）や制御バイトをそのまま送る。
    /// <b>文章を送るのに使わない。</b> 文章は <see cref="SendTextAsync"/> だけを通す。
    /// </summary>
    Task SendBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct);

    /// <summary>ペインの大きさが変わったことを子へ伝える（TIOCSWINSZ → SIGWINCH）。</summary>
    Task ResizeAsync(int columns, int rows, CancellationToken ct);

    /// <summary>段階的な終了。SIGKILL は最後の手段。</summary>
    Task StopAsync(CancellationToken ct);

    event EventHandler<Status.Evidence>? Observed;

    event EventHandler<int>? Exited;
}

/// <summary>
/// プロセスの同一性。<b>PID だけでは足りない</b>（再利用される）。設計 §9。
/// </summary>
/// <param name="Pid">子プロセスの PID。</param>
/// <param name="SessionId">setsid で作った session id。</param>
/// <param name="ProcessGroupId">process group id。シグナルの宛先はこちら。</param>
/// <param name="StartedAt">開始時刻。PID 再利用を見破るための第2の鍵。</param>
public readonly record struct ProcessIdentity(int Pid, int SessionId, int ProcessGroupId, DateTimeOffset StartedAt);
