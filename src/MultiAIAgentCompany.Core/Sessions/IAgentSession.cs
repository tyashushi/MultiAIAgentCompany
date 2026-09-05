using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Status;

namespace MultiAIAgentCompany.Core.Sessions;

/// <summary>
/// 1部門ぶんの子プロセス。設計 §9 —— <b>アプリが全部門の親になる。</b>
/// </summary>
/// <remarks>
/// <b>駆動モードで別のインターフェイスに分ける。</b>
/// 構造化（<see cref="IStructuredSession"/>）はパイプで、承認の往復が閉じる。
/// TUI（<see cref="ITuiSession"/>）は PTY で、§9 の契約が全部かかる。
/// <para>
/// 1つの型に押し込むと、パイプのセッションに <c>ResizeAsync</c> が生えて、
/// <b>黙って何もしない実装</b>になる。§2 の「3つの CLI は対称ではない」と同じ話で、
/// 2つの駆動モードも対称ではない。
/// </para>
/// <para>
/// どちらにも共通するのは<b>生死と観測</b>だけなので、この型はそれだけを持つ。
/// </para>
/// </remarks>
public interface IAgentSession : IAsyncDisposable
{
    string DepartmentId { get; }

    ProcessIdentity Identity { get; }

    DriveMode Mode { get; }

    /// <summary>
    /// 段階的な終了。入力停止 → 正常終了要求 → SIGTERM → 最後に SIGKILL（設計 §9）。
    /// <b>対象は子1つではなくプロセスツリー</b> —— CLI は自分で子を作る。
    /// </summary>
    Task StopAsync(CancellationToken ct);

    event EventHandler<Evidence>? Observed;

    event EventHandler<int>? Exited;
}

/// <summary>
/// 構造化モードのセッション。stdin / stdout のパイプで、**承認の往復が閉じる**（設計 §13-1 / §13-2）。
/// </summary>
/// <remarks>
/// <b>stdout を読み続けること。</b> 読むのを止めるとパイプのバッファが詰まって
/// 子プロセスが止まる（設計 §5 の「非表示のターミナルも読み続ける」はパイプにも当てはまる）。
/// </remarks>
public interface IStructuredSession : IAgentSession
{
    /// <summary>人間・秘書からの1メッセージを送る。</summary>
    Task SendUserMessageAsync(string text, CancellationToken ct);

    /// <summary>
    /// 承認に答える。<paramref name="decision"/> は
    /// <paramref name="request"/> が提示したものでなければならない（設計 §5）。
    /// </summary>
    /// <param name="reason">
    /// 人間が書いた理由。<b>エージェントに届く</b>（Claude は拒否の <c>message</c> として読む）。
    /// 無いと、拒否された agent は理由の分からないまま盲目的に再試行する。
    /// <para>
    /// 「提示された選択肢」ではなく「その回の応答」に属するので、
    /// <see cref="ApprovalDecision"/> ではなくここに置く。
    /// </para>
    /// <para>
    /// <b>秘密値を書かない。</b> 人間が打った文字列がそのままモデルへ渡る（設計 §10）。
    /// </para>
    /// </param>
    Task RespondAsync(ApprovalRequest request, ApprovalDecision decision, string? reason, CancellationToken ct);

    /// <summary>(a) ランタイム承認の要求（設計 §3）。</summary>
    event EventHandler<ApprovalRequest>? ApprovalRequested;

    /// <summary>
    /// turn が終わった。<b>「終わった」は「成功した」ではない。</b>
    /// 判定は3層すべてを見た <see cref="OutcomeVerdict"/> で渡す（設計 §13 末尾 / §14-4）。
    /// </summary>
    event EventHandler<OutcomeVerdict>? TurnFinished;
}

/// <summary>
/// TUI モードのセッション。PTY 上の対話画面。設計 §9 の契約が全部かかる。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>部門ごとに session / process group を隔離する。全体で SIGHUP を無視しない
/// （PTY では SIGHUP が端末切断の正規の意味を持つ）</item>
/// <item>リサイズごとに TIOCSWINSZ。SIGWINCH が届くことを実機で確認する</item>
/// <item>Ctrl-C / Ctrl-Z を GUI ショートカットにせず、foreground process group へ伝える</item>
/// <item><b>非表示でも読み続ける。</b> 止めると PTY バッファが詰まって子が停止する</item>
/// </list>
/// </remarks>
public interface ITuiSession : IAgentSession
{
    /// <summary>
    /// 文章を送る。<b>ターミナルへの直打ちではなく入力欄経由</b>（設計 §13-7）。
    /// </summary>
    /// <param name="submitKey">
    /// 解決済みの送信キー。<see cref="SubmitKey.IsResolved"/> が false のものを渡すと
    /// <see cref="ArgumentException"/>。<c>0D</c> にフォールバックさせない（設計 §14-4）。
    /// </param>
    Task SendTextAsync(string text, SubmitKey submitKey, CancellationToken ct);

    /// <summary>
    /// 単キー（<c>y</c>/<c>n</c>、矢印、Ctrl-C）や制御バイト。
    /// <b>文章を送るのに使わない。</b>
    /// </summary>
    Task SendBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct);

    /// <summary>ペインの大きさが変わったことを子へ伝える（TIOCSWINSZ → SIGWINCH）。</summary>
    Task ResizeAsync(int columns, int rows, CancellationToken ct);
}

/// <summary>
/// プロセスの同一性。<b>PID だけでは足りない</b>（再利用される）。設計 §9。
/// </summary>
/// <param name="Pid">子プロセスの PID。</param>
/// <param name="SessionId">setsid で作った session id。PTY でなければ null 相当の 0。</param>
/// <param name="ProcessGroupId">process group id。シグナルの宛先はこちら。</param>
/// <param name="StartedAt">開始時刻。PID 再利用を見破るための第2の鍵。</param>
public readonly record struct ProcessIdentity(int Pid, int SessionId, int ProcessGroupId, DateTimeOffset StartedAt);
